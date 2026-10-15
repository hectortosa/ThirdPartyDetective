using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace ThirdPartyDetective.Cli.Services.Registry;

// Resolves npm packages from the registry's version document and the version's
// tarball. The declared license ("license", or the legacy "licenses" array) is
// used when it names SPDX licenses; the tarball's license files supply the text
// that ships with the package, its copyright notice (npm metadata has no
// copyright field), and the license when the declaration doesn't name one
// ("SEE LICENSE IN <file>", free text, nothing at all).
public class NpmLicenseLookup : ILicenseLookup
{
    private const string RegistryHost = "registry.npmjs.org";

    private static readonly Regex RepositoryShorthand = new(
        @"^(?:(github|gitlab|bitbucket):)?([A-Za-z0-9_.-]+)/([A-Za-z0-9_.-]+)$", RegexOptions.NonBacktracking);

    private readonly HttpClient _http;
    private readonly ILicenseTextDetector _textDetector;
    private readonly ISpdxLicenseList _licenseList;
    private readonly ILogger<NpmLicenseLookup> _logger;

    public NpmLicenseLookup(HttpClient http, ILicenseTextDetector textDetector, ISpdxLicenseList licenseList, ILogger<NpmLicenseLookup> logger)
    {
        _http = http;
        _textDetector = textDetector;
        _licenseList = licenseList;
        _logger = logger;
    }

    public string Ecosystem => "npm";

    public async Task<LicenseLookupResult?> LookupLicenseAsync(string name, string version, CancellationToken cancellationToken = default)
    {
        var encodedName = name.StartsWith('@')
            ? "@" + Uri.EscapeDataString(name[1..])
            : Uri.EscapeDataString(name);
        var url = $"https://{RegistryHost}/{encodedName}/{Uri.EscapeDataString(version)}";

        try
        {
            Metadata metadata;
            using (var response = await _http.GetAsync(url, cancellationToken))
            {
                if (!response.IsSuccessStatusCode) return null;
                await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
                using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
                if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
                metadata = ReadMetadata(doc.RootElement);
            }

            return await ResolveAsync(name, version, metadata, cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or InvalidDataException ||
                                   (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            _logger.LogDebug(ex, "npm license lookup failed for {Name}@{Version}", name, version);
            return null;
        }
    }

    private sealed record Metadata(
        IReadOnlyList<string> DeclaredLicenses,
        string? Authors,
        string? ProjectUrl,
        string? TarballUrl);

    private async Task<LicenseLookupResult?> ResolveAsync(string name, string version, Metadata metadata, CancellationToken cancellationToken)
    {
        string? expression = null;
        string? licenseUrl = null;
        string? seeLicenseIn = null;
        string? note = null;
        var unlicensed = false;

        var declared = metadata.DeclaredLicenses;
        if (declared.Count == 1 && string.Equals(declared[0], "UNLICENSED", StringComparison.OrdinalIgnoreCase))
        {
            unlicensed = true;
            note = "npm declares the package UNLICENSED: it grants no license to use it";
        }
        else if (declared.Count == 1 && DeclaredLicense.SeeLicenseInFile(declared[0]) is { } file)
        {
            seeLicenseIn = file;
        }
        else if (declared.Count == 1 && IsWebUrl(declared[0]))
        {
            licenseUrl = declared[0];
        }
        else if (declared.Count > 0)
        {
            // The legacy "licenses" array lists alternatives.
            var normalized = declared.Select(d => DeclaredLicense.Normalize(d, _licenseList)).ToList();
            if (normalized.All(n => n != null))
                expression = normalized.Count == 1
                    ? normalized[0]
                    : SpdxExpression.TryParse(string.Join(" OR ", normalized.Select(n => $"({n})")))?.ToString();
            else
                note = $"npm declares the license as '{string.Join(", ", declared)}', which names no SPDX license";
        }

        var files = metadata.TarballUrl == null
            ? Array.Empty<ArchiveFile>()
            : await ReadLicenseFilesAsync(metadata.TarballUrl, seeLicenseIn, cancellationToken);

        string? detectedFrom = null;
        if (expression == null && licenseUrl == null && !unlicensed)
        {
            var candidates = seeLicenseIn != null
                ? files.Where(f => string.Equals(f.Path, seeLicenseIn, StringComparison.OrdinalIgnoreCase)).ToList()
                : PackageArchive.LicenseFilesOnly(files);
            if (candidates.Count == 1 && _textDetector.DetectFromContent(candidates[0].Text) is { } detected)
            {
                expression = detected;
                detectedFrom = $"{name}-{version}.tgz::{candidates[0].Path}";
                _logger.LogInformation("Detected {Spdx} for {Name}@{Version} from {Source}", detected, name, version, detectedFrom);
            }
        }

        var licenseText = PackageArchive.Combine(files);
        if (expression == null && licenseUrl == null && licenseText == null && note == null &&
            metadata.Authors == null && metadata.ProjectUrl == null)
            return null;

        return new LicenseLookupResult(expression, licenseUrl, metadata.Authors, Copyright: null,
            ProjectUrl: metadata.ProjectUrl, LicenseText: licenseText, DetectedFrom: detectedFrom, Note: note);
    }

    // License files at the package root, plus the file "SEE LICENSE IN" names
    // (which can have any name, or sit in a subdirectory). Tarballs put the
    // package under one top-level directory, "package/" for anything recent.
    private async Task<IReadOnlyList<ArchiveFile>> ReadLicenseFilesAsync(string tarballUrl, string? seeLicenseIn, CancellationToken cancellationToken)
    {
        var archive = await PackageArchive.DownloadAsync(_http, tarballUrl, _logger, cancellationToken);
        if (archive == null) return Array.Empty<ArchiveFile>();

        return PackageArchive.ReadTarGz(archive, path =>
        {
            var slash = path.IndexOf('/');
            if (slash < 0) return null;
            var relative = path[(slash + 1)..];
            if (seeLicenseIn != null && string.Equals(relative, seeLicenseIn, StringComparison.OrdinalIgnoreCase))
                return relative;
            return !relative.Contains('/') && PackageArchive.IsLicenseFileName(relative) ? relative : null;
        });
    }

    private static Metadata ReadMetadata(JsonElement root) => new(
        ReadDeclaredLicenses(root),
        ReadAuthor(root),
        ReadRepositoryUrl(root) ?? ReadHomepage(root),
        ReadTarballUrl(root));

    // "license": "MIT" | { "type": "MIT", "url": ... } | ["MIT", ...];
    // legacy "licenses": [{ "type": "MIT", "url": ... }, ...].
    private static IReadOnlyList<string> ReadDeclaredLicenses(JsonElement root)
    {
        static string? Value(JsonElement e) => e.ValueKind switch
        {
            JsonValueKind.String => e.GetString(),
            JsonValueKind.Object => e.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null,
            _ => null
        };

        foreach (var property in new[] { "license", "licenses" })
        {
            if (!root.TryGetProperty(property, out var value)) continue;
            var values = value.ValueKind == JsonValueKind.Array
                ? value.EnumerateArray().Select(Value).ToList()
                : new List<string?> { Value(value) };
            var declared = values.Where(v => !string.IsNullOrWhiteSpace(v)).Select(v => v!.Trim()).Distinct(StringComparer.Ordinal).ToList();
            if (declared.Count > 0) return declared;
        }
        return Array.Empty<string>();
    }

    // "author": "Name <email> (url)" | { "name": ..., "email": ... }.
    private static string? ReadAuthor(JsonElement root)
    {
        if (!root.TryGetProperty("author", out var author)) return null;
        if (author.ValueKind == JsonValueKind.String)
            return NormalizeBlank(author.GetString());
        if (author.ValueKind == JsonValueKind.Object)
            return RegistryAuthorFormatter.Combine(StringProperty(author, "name"), StringProperty(author, "email"));
        return null;
    }

    // "repository": "github:user/repo" | "user/repo" | { "type": "git", "url": "git+https://github.com/user/repo.git" },
    // turned into a browsable https URL.
    internal static string? NormalizeRepositoryUrl(string? value)
    {
        var raw = NormalizeBlank(value);
        if (raw == null) return null;

        var shorthand = RepositoryShorthand.Match(raw);
        if (shorthand.Success)
        {
            var host = shorthand.Groups[1].Value switch { "gitlab" => "gitlab.com", "bitbucket" => "bitbucket.org", _ => "github.com" };
            return $"https://{host}/{shorthand.Groups[2].Value}/{shorthand.Groups[3].Value}";
        }

        var url = raw;
        if (url.StartsWith("git+", StringComparison.OrdinalIgnoreCase)) url = url[4..];
        if (url.StartsWith("git://", StringComparison.OrdinalIgnoreCase)) url = "https://" + url[6..];
        if (url.StartsWith("ssh://git@", StringComparison.OrdinalIgnoreCase)) url = "https://" + url[10..];
        if (url.EndsWith(".git", StringComparison.OrdinalIgnoreCase)) url = url[..^4];
        return IsWebUrl(url) ? url.TrimEnd('/') : null;
    }

    private static string? ReadRepositoryUrl(JsonElement root)
    {
        if (!root.TryGetProperty("repository", out var repo)) return null;
        if (repo.ValueKind == JsonValueKind.String) return NormalizeRepositoryUrl(repo.GetString());
        if (repo.ValueKind == JsonValueKind.Object) return NormalizeRepositoryUrl(StringProperty(repo, "url"));
        return null;
    }

    private static string? ReadHomepage(JsonElement root)
    {
        var homepage = StringProperty(root, "homepage");
        return homepage != null && IsWebUrl(homepage) ? homepage : null;
    }

    // Only the public registry's own tarballs are fetched: the URL comes from
    // the metadata document, and a tarball elsewhere is not this package's.
    private static string? ReadTarballUrl(JsonElement root)
    {
        if (!root.TryGetProperty("dist", out var dist) || dist.ValueKind != JsonValueKind.Object) return null;
        var tarball = StringProperty(dist, "tarball");
        return Uri.TryCreate(tarball, UriKind.Absolute, out var uri) &&
               uri.Scheme == Uri.UriSchemeHttps &&
               string.Equals(uri.Host, RegistryHost, StringComparison.OrdinalIgnoreCase)
            ? tarball
            : null;
    }

    private static bool IsWebUrl(string value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp);

    private static string? StringProperty(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? NormalizeBlank(value.GetString()) : null;

    private static string? NormalizeBlank(string? s) =>
        string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
