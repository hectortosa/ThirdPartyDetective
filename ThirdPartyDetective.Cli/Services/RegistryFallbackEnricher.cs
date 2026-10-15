using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using ThirdPartyDetective.Cli.Models;
using ThirdPartyDetective.Cli.Services.Registry;

namespace ThirdPartyDetective.Cli.Services;

public class RegistryFallbackEnricher : IRegistryFallbackEnricher
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromDays(30);
    private const int MaxConcurrency = 10;

    private static readonly JsonSerializerOptions CacheJsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly Dictionary<string, ILicenseLookup> _lookupsByEcosystem;
    private readonly ISpdxLicenseExpressionAdapter _expressionAdapter;
    private readonly ILicenseTextDetector _textDetector;
    private readonly ILogger<RegistryFallbackEnricher> _logger;

    public RegistryFallbackEnricher(
        IEnumerable<ILicenseLookup> lookups,
        ISpdxLicenseExpressionAdapter expressionAdapter,
        ILicenseTextDetector textDetector,
        ILogger<RegistryFallbackEnricher> logger)
    {
        _lookupsByEcosystem = lookups.ToDictionary(l => l.Ecosystem, StringComparer.OrdinalIgnoreCase);
        _expressionAdapter = expressionAdapter;
        _textDetector = textDetector;
        _logger = logger;
    }

    public async Task<IReadOnlyList<ThirdPartyInfo>> EnrichAsync(
        IReadOnlyList<ThirdPartyInfo> packages,
        ScanOptions options,
        CancellationToken cancellationToken = default)
    {
        // Enrich any package that's missing a license or a copyright notice. MIT, BSD
        // and ISC require the notice to accompany the software, and for npm and PyPI
        // only the package's own license files have it.
        var residual = packages.Where(p =>
            string.IsNullOrEmpty(p.LicenseExpression) || string.IsNullOrEmpty(p.Copyright)).ToList();
        if (residual.Count == 0)
        {
            _logger.LogDebug("No packages need registry fallback");
            return packages;
        }

        var cache = await LoadCacheAsync(options.RegistryFallbackCachePath, cancellationToken);
        _logger.LogInformation(
            "{Count} packages need registry fallback (out of {Total}); cache has {CacheCount} entries",
            residual.Count, packages.Count, cache.Entries.Count);

        if (options.Offline)
            _logger.LogInformation("Offline: registry fallback uses only the cache at {Path}", options.RegistryFallbackCachePath);

        using var semaphore = new SemaphoreSlim(MaxConcurrency, MaxConcurrency);
        var tasks = residual.Select(pkg => EnrichOneAsync(pkg, cache, options.Offline, semaphore, cancellationToken));
        await Task.WhenAll(tasks);

        // Offline runs only read the cache; rewriting it would just churn a committed file.
        if (!options.Offline)
            await SaveCacheAsync(options.RegistryFallbackCachePath, cache, cancellationToken);
        return packages;
    }

    private async Task EnrichOneAsync(
        ThirdPartyInfo pkg,
        LicenseCacheFile cache,
        bool offline,
        SemaphoreSlim semaphore,
        CancellationToken cancellationToken)
    {
        await semaphore.WaitAsync(cancellationToken);
        try
        {
            await EnrichOneCoreAsync(pkg, cache, offline, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            // One package's registry trouble must not abort the run (or lose the cache).
            _logger.LogWarning("Registry fallback failed for {Name}@{Version}: {Error}", pkg.Name, pkg.Version, ex.Message);
            _logger.LogDebug(ex, "Registry fallback failure details for {Name}@{Version}", pkg.Name, pkg.Version);
        }
        finally
        {
            semaphore.Release();
        }
    }

    private async Task EnrichOneCoreAsync(ThirdPartyInfo pkg, LicenseCacheFile cache, bool offline, CancellationToken cancellationToken)
    {
        var ecosystem = NormalizeEcosystem(pkg.SourceEcosystem);
        if (ecosystem == null || !_lookupsByEcosystem.TryGetValue(ecosystem, out var lookup))
        {
            _logger.LogDebug("Skipping {Name}@{Version}: no registry lookup for ecosystem '{Ecosystem}'",
                pkg.Name, pkg.Version, pkg.SourceEcosystem ?? "(none)");
            return;
        }

        var key = $"{ecosystem}:{pkg.Name.ToLowerInvariant()}:{pkg.Version}";
        LicenseCacheEntry? existing;
        lock (cache)
        {
            cache.Entries.TryGetValue(key, out existing);
        }
        if (existing != null && IsFromTheFuture(existing.Resolved))
            existing = null; // a hand-edited entry can't pin itself forever

        // Offline, whatever the cache holds is used, whatever its age: a committed
        // cache is what makes the run reproducible. Online, an entry is reused while
        // fresh, unless it has no license and the package still needs one (the
        // registry may have been down, or the lookup may have improved since).
        var needsLicense = string.IsNullOrEmpty(pkg.LicenseExpression);
        var entry = existing != null &&
                    (offline || (IsFresh(existing.Resolved) &&
                                 (existing.License != null || (!needsLicense && (existing.Authors != null || existing.Copyright != null)))))
            ? existing
            : null;
        var fromCache = entry != null;

        if (entry == null && offline)
        {
            _logger.LogDebug("Offline: {Name}@{Version} is not in the registry cache", pkg.Name, pkg.Version);
            return;
        }

        if (entry == null)
        {
            var result = await lookup.LookupLicenseAsync(pkg.Name, pkg.Version, cancellationToken);
            if (result == null)
            {
                // Not found, or the registry failed: keep what the cache already knows
                // instead of replacing it with an empty entry.
                if (existing == null) return;
                _logger.LogDebug("No registry answer for {Name}@{Version}; using the cached entry from {Resolved:O}",
                    pkg.Name, pkg.Version, existing.Resolved);
                entry = existing;
                fromCache = true;
            }
            else
            {
                entry = await ResolveAsync(pkg, result, cancellationToken);
                lock (cache)
                {
                    cache.Entries[key] = entry;
                }
            }
        }

        var rawLicense = entry.License;
        var rawUrl = entry.LicenseUrl;
        var rawProjectUrl = entry.ProjectUrl;
        var rawAuthors = entry.Authors;
        var rawCopyright = entry.Copyright;
        var rawLicenseText = entry.LicenseText;
        var detectedFrom = entry.DetectedFrom;

        if (!string.IsNullOrEmpty(entry.Note))
            pkg.WarningFlags.Add(entry.Note);

        // The registry only fills gaps: a license the SBOM already declares is never
        // replaced, and neither are SBOM-provided URLs, authors or copyright.
        if (!string.IsNullOrEmpty(rawLicense) && string.IsNullOrEmpty(pkg.LicenseExpression))
        {
            var info = _expressionAdapter.Normalize(rawLicense);
            pkg.LicenseExpression = info.Normalized;
            pkg.LicenseSubExpressions = info.SubExpressions.ToList();
            pkg.LicenseExceptions = info.Exceptions.ToList();
            pkg.LicenseSource = LicenseSource.Registry;
            if (detectedFrom != null)
                pkg.WarningFlags.Add($"License identified by matching the license text at {detectedFrom}; verify");
        }
        if (!string.IsNullOrEmpty(rawLicenseText) && string.IsNullOrEmpty(pkg.LicenseText))
            pkg.LicenseText = rawLicenseText;
        if (!string.IsNullOrEmpty(rawUrl) && string.IsNullOrEmpty(pkg.LicenseUrl))
            pkg.LicenseUrl = rawUrl;
        if (!string.IsNullOrEmpty(rawProjectUrl) && string.IsNullOrEmpty(pkg.ProjectUrl))
            pkg.ProjectUrl = rawProjectUrl;
        if (!string.IsNullOrEmpty(rawAuthors) && string.IsNullOrEmpty(pkg.Authors))
            pkg.Authors = rawAuthors;
        if (!string.IsNullOrEmpty(rawCopyright) && string.IsNullOrEmpty(pkg.Copyright))
            pkg.Copyright = rawCopyright;

        if (fromCache)
            _logger.LogDebug("Cache hit for {Name}@{Version}: license={License}, url={Url}",
                pkg.Name, pkg.Version, rawLicense ?? "(null)", rawUrl ?? "(null)");
        else
            _logger.LogInformation("Resolved {Name}@{Version} from {Ecosystem} registry: license={License}, url={Url}, authors={Authors}",
                pkg.Name, pkg.Version, ecosystem, rawLicense ?? "(null)", rawUrl ?? "(null)", rawAuthors ?? "(null)");
    }

    // Turns a registry answer into a cache entry: an exact license URL counts as an
    // expression; otherwise the license URL (or repository) text is matched.
    private async Task<LicenseCacheEntry> ResolveAsync(ThirdPartyInfo pkg, LicenseLookupResult result, CancellationToken cancellationToken)
    {
        var license = result.Expression;
        var detectedFrom = result.DetectedFrom;

        if (string.IsNullOrEmpty(license) && KnownLicenseUrls.TryGetExpression(result.Url, out var fromUrl))
            license = fromUrl;

        // The repository's default branch is the weakest evidence: it is probed only
        // when the registry declared nothing and the package has no license files of
        // its own. A commercial package can live in a repository whose root LICENSE
        // is MIT.
        var probeRepository = result.LicenseText == null && result.Note == null;
        var detectionUrl = result.Url ?? (probeRepository ? result.ProjectUrl : null);
        if (string.IsNullOrEmpty(license) && !string.IsNullOrEmpty(detectionUrl))
        {
            var detection = await _textDetector.DetectFromUrlAsync(detectionUrl, cancellationToken);
            if (detection != null)
            {
                license = detection.LicenseId;
                detectedFrom = detection.FromRepositoryDefaultBranch
                    ? $"{detection.SourceUrl} (repository default branch, not necessarily this version)"
                    : detection.SourceUrl;
                _logger.LogInformation("Detected {Spdx} for {Name}@{Version} from {Url}",
                    detection.LicenseId, pkg.Name, pkg.Version, detectedFrom);
            }
        }

        return new LicenseCacheEntry
        {
            License = license,
            LicenseUrl = result.Url,
            ProjectUrl = result.ProjectUrl,
            Authors = result.Authors,
            // Registries without a copyright field: the notice is in the license files.
            Copyright = result.Copyright ?? CopyrightExtractor.Extract(result.LicenseText),
            LicenseText = result.LicenseText,
            DetectedFrom = detectedFrom,
            Note = result.Note,
            Resolved = DateTime.UtcNow
        };
    }

    private static string? NormalizeEcosystem(string? sourceEcosystem)
    {
        if (string.IsNullOrEmpty(sourceEcosystem)) return null;
        return sourceEcosystem.ToLowerInvariant() switch
        {
            "nuget" => "nuget",
            "npm" => "npm",
            "python" or "pypi" => "pypi",
            _ => null
        };
    }

    private static bool IsFresh(DateTime resolved) =>
        DateTime.UtcNow - resolved < CacheTtl;

    // Beyond clock skew, a timestamp in the future marks a tampered entry.
    private static bool IsFromTheFuture(DateTime resolved) =>
        resolved - DateTime.UtcNow > TimeSpan.FromDays(1);

    private async Task<LicenseCacheFile> LoadCacheAsync(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
            return new LicenseCacheFile();

        try
        {
            await using var stream = File.OpenRead(path);
            var loaded = await JsonSerializer.DeserializeAsync<LicenseCacheFile>(stream, CacheJsonOptions, cancellationToken);
            if (loaded == null) return new LicenseCacheFile();
            // Older cache files lack fields or store them differently. Discard them so the
            // next run re-fetches and stores the current shape.
            if (loaded.Version < LicenseCacheFile.CurrentVersion)
            {
                _logger.LogInformation("Discarding cache at {Path}: version {Old} < {New} (re-fetching)",
                    path, loaded.Version, LicenseCacheFile.CurrentVersion);
                return new LicenseCacheFile();
            }
            return loaded;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not load license cache at {Path}; starting fresh", path);
            return new LicenseCacheFile();
        }
    }

    private async Task SaveCacheAsync(string path, LicenseCacheFile cache, CancellationToken cancellationToken)
    {
        try
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            // Lookups finish in any order. Ordinal key order makes a committed cache
            // diff only where entries changed.
            var ordered = new LicenseCacheFile
            {
                Version = cache.Version,
                Entries = cache.Entries.OrderBy(e => e.Key, StringComparer.Ordinal).ToDictionary(e => e.Key, e => e.Value)
            };

            // Write to a temporary file and rename, so an interrupted run never leaves a
            // truncated cache behind.
            var temp = path + ".tmp";
            await using (var stream = File.Create(temp))
            {
                await JsonSerializer.SerializeAsync(stream, ordered, CacheJsonOptions, cancellationToken);
            }
            File.Move(temp, path, overwrite: true);
            _logger.LogDebug("Saved license cache to {Path} ({Count} entries)", path, cache.Entries.Count);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not save license cache to {Path}", path);
        }
    }
}

public class LicenseCacheFile
{
    // v3: repository/homepage URLs moved from LicenseUrl to ProjectUrl.
    // v4: npm and PyPI entries carry the package's license files and copyright.
    public const int CurrentVersion = 4;

    public int Version { get; set; } = CurrentVersion;
    public Dictionary<string, LicenseCacheEntry> Entries { get; set; } = new();
}

public class LicenseCacheEntry
{
    public string? License { get; set; }
    public string? LicenseUrl { get; set; }
    public string? ProjectUrl { get; set; }
    public string? Authors { get; set; }
    public string? Copyright { get; set; }
    // The package's own license text (e.g. a .nupkg license file) and, when the
    // license was identified by matching text, where that text came from.
    public string? LicenseText { get; set; }
    public string? DetectedFrom { get; set; }
    // A registry declaration that could not be turned into a license, for review.
    public string? Note { get; set; }
    public DateTime Resolved { get; set; }
}
