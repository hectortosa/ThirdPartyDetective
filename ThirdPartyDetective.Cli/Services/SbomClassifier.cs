using Microsoft.Extensions.Logging;
using ThirdPartyDetective.Cli.Models;

namespace ThirdPartyDetective.Cli.Services;

public class SbomClassifier : ISbomClassifier
{
    private readonly ISbomMerger _merger;
    private readonly IRegistryFallbackEnricher _enricher;
    private readonly ILicenseAnalyzer _analyzer;
    private readonly IPolicyConfigLoader _policyLoader;
    private readonly IOverridesConfigLoader _overridesLoader;
    private readonly IOverrideApplier _overrideApplier;
    private readonly ILogger<SbomClassifier> _logger;

    public SbomClassifier(
        ISbomMerger merger,
        IRegistryFallbackEnricher enricher,
        ILicenseAnalyzer analyzer,
        IPolicyConfigLoader policyLoader,
        IOverridesConfigLoader overridesLoader,
        IOverrideApplier overrideApplier,
        ILogger<SbomClassifier> logger)
    {
        _merger = merger;
        _enricher = enricher;
        _analyzer = analyzer;
        _policyLoader = policyLoader;
        _overridesLoader = overridesLoader;
        _overrideApplier = overrideApplier;
        _logger = logger;
    }

    public async Task<PackageAnalysisResult> ClassifyAsync(ScanOptions options, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation(
            "Classifying {SbomCount} SBOM source(s) for mode {Mode}{Fallback}",
            options.SbomPaths.Count,
            options.Mode,
            options.RegistryFallback ? " (registry fallback enabled)" : "");

        var policy = _policyLoader.Load(options.PolicyConfigPath);
        var overrides = _overridesLoader.Load(options.OverridesConfigPath);

        var merged = await _merger.MergeAsync(options.SbomPaths, cancellationToken);

        // Excluded namespaces are removed before anything else: they never reach the
        // public registries (first-party names stay private), the overrides or any report.
        var included = merged.Where(p => !MatchesExcludedNamespace(p.Name, options.ExcludeNamespaces)).ToList();
        var excludedCount = merged.Count - included.Count;

        // The enricher fills gaps in place. A package an override fully decides is not
        // looked up: the override would replace the answer, and a lookup costs registry
        // requests and can download the package's archive.
        var enriched = included;
        if (options.RegistryFallback)
        {
            var decided = _overrideApplier.FullyDecided(included, overrides);
            if (decided.Count > 0)
                _logger.LogInformation("Registry fallback skips {Count} package(s) that overrides decide", decided.Count);
            await _enricher.EnrichAsync(included.Where(p => !decided.Contains(p)).ToList(), options, cancellationToken);
        }

        // Manual overrides apply post-enrich, pre-classify: SBOM and registry runs are still
        // visible in logs/debug, but the override replaces the final values before policy
        // evaluation. This is the audit-trail interpretation — a human's verdict wins.
        var overridesApplied = _overrideApplier.Apply(enriched, overrides);
        if (overridesApplied > 0)
            _logger.LogInformation("Applied {Count} manual override(s) from overrides config", overridesApplied);

        var packages = new List<ThirdPartyInfo>();
        var violations = new List<ThirdPartyInfo>();

        foreach (var pkg in enriched)
        {
            cancellationToken.ThrowIfCancellationRequested();

            pkg.LicenseType = _analyzer.ParseLicenseType(pkg.LicenseExpression, pkg.LicenseUrl);
            pkg.RequiresAttribution = _analyzer.RequiresAttribution(pkg.LicenseType, options.Mode);
            pkg.SaasCompliant = _analyzer.IsSaasCompliant(pkg.LicenseType);
            pkg.WarningFlags.AddRange(_analyzer.GetWarningFlags(pkg));

            pkg.IsViolation = ComputeViolation(pkg, options, policy);

            packages.Add(pkg);
            if (pkg.IsViolation)
                violations.Add(pkg);
        }

        if (excludedCount > 0)
            _logger.LogInformation("Excluded {Count} packages from output via --exclude-namespaces", excludedCount);

        var documentWarnings = ApplyAttributionWarnings(packages);

        // Ordinal, culture-independent order: the same SBOM gives the same report on any machine.
        var sortedPackages = SortPackages(packages);
        var sortedViolations = SortPackages(violations);

        _logger.LogInformation(
            "Classified {Total} packages, {Violations} violations under mode {Mode}",
            sortedPackages.Count, sortedViolations.Count, options.Mode);

        var registryResolved = sortedPackages.Count(p => p.LicenseSource == LicenseSource.Registry);
        if (registryResolved > 0)
            _logger.LogInformation("{Count} packages had license info resolved via registry fallback", registryResolved);

        return new PackageAnalysisResult
        {
            GeneratedAt = ResolveGeneratedAt(options),
            Packages = sortedPackages,
            Violations = sortedViolations,
            ScanPath = string.Join(", ", options.SbomPaths),
            Mode = options.Mode.ToString(),
            Summary = new AnalysisSummary
            {
                TotalPackages = sortedPackages.Count,
                PackagesRequiringAttribution = sortedPackages.Count(p => p.RequiresAttribution),
                WarningCount = sortedPackages.Sum(p => p.WarningFlags.Count) + documentWarnings.Count,
                LicenseTypeCounts = sortedPackages
                    .GroupBy(p => p.LicenseType.ToString())
                    .ToDictionary(g => g.Key, g => g.Count()),
                DocumentWarnings = documentWarnings,
                UnresolvedLicenses = sortedPackages
                    .Where(p => string.IsNullOrEmpty(p.LicenseExpression))
                    .Select(p => $"{p.Name}@{p.Version}")
                    .ToList()
            }
        };
    }

    // Decide whether the missing-attribution warning is per-package signal or universal noise.
    // Syft/Trivy SBOMs leave originator/copyrightText empty for ~all packages — emitting N
    // per-package warnings then is just noise. Above 75% missing, fold into one document-level
    // line; otherwise the warning is meaningful per-package and gets attached as before.
    private static List<string> ApplyAttributionWarnings(List<ThirdPartyInfo> packages)
    {
        var documentWarnings = new List<string>();
        var attribRequired = packages.Where(p => p.RequiresAttribution).ToList();
        if (attribRequired.Count == 0) return documentWarnings;

        var missing = attribRequired
            .Where(p => string.IsNullOrEmpty(p.Authors) && string.IsNullOrEmpty(p.Copyright))
            .ToList();
        if (missing.Count == 0) return documentWarnings;

        var missingRate = (double)missing.Count / attribRequired.Count;
        if (missingRate >= 0.75)
        {
            documentWarnings.Add(
                $"{missing.Count} of {attribRequired.Count} attribution-required packages have no copyright/author metadata. " +
                "This is typical for Syft/Trivy SBOMs. Run with --registry-fallback to fetch from package registries, " +
                "or populate .tpd-overrides.json for packages requiring legal review.");
        }
        else
        {
            foreach (var pkg in missing)
                pkg.WarningFlags.Add("Missing copyright/author information for attribution");
        }
        return documentWarnings;
    }

    private static List<ThirdPartyInfo> SortPackages(IEnumerable<ThirdPartyInfo> packages) =>
        packages.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(p => p.Name, StringComparer.Ordinal)
            .ThenBy(p => p.Version, StringComparer.Ordinal)
            .ToList();

    // SOURCE_DATE_EPOCH (reproducible-builds.org) wins; --deterministic otherwise drops
    // the timestamp; by default the reports record when they were generated.
    private static DateTime? ResolveGeneratedAt(ScanOptions options)
    {
        if (!string.IsNullOrWhiteSpace(options.SourceDateEpoch))
        {
            if (!long.TryParse(options.SourceDateEpoch.Trim(), System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture, out var seconds) ||
                seconds > DateTimeOffset.MaxValue.ToUnixTimeSeconds())
                throw new ArgumentException(
                    $"SOURCE_DATE_EPOCH must be a Unix timestamp in seconds, got '{options.SourceDateEpoch}'.");
            return DateTimeOffset.FromUnixTimeSeconds(seconds).UtcDateTime;
        }
        return options.Deterministic ? null : DateTime.UtcNow;
    }

    private bool ComputeViolation(ThirdPartyInfo pkg, ScanOptions options, PolicyConfig policy)
    {
        var typeName = pkg.LicenseType.ToString();

        bool isViolation = options.IncludeLicenseTypes.Count > 0
            ? !options.IncludeLicenseTypes.Contains(typeName, StringComparer.OrdinalIgnoreCase)
            : _analyzer.IsPolicyViolation(pkg.LicenseType, options.Mode, policy);

        if (options.ExcludeLicenseTypes.Contains(typeName, StringComparer.OrdinalIgnoreCase))
            isViolation = true;

        return isViolation;
    }

    private static bool MatchesExcludedNamespace(string packageName, IReadOnlyList<string> patterns)
    {
        foreach (var raw in patterns)
        {
            var p = raw.Trim();
            if (string.IsNullOrEmpty(p)) continue;

            if (p.EndsWith('*'))
            {
                var prefix = p[..^1];
                if (packageName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            else if (string.Equals(packageName, p, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }
}
