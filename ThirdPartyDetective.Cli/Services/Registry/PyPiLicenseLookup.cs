using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace ThirdPartyDetective.Cli.Services.Registry;

// Resolves PyPI packages from the JSON API and one artifact of the exact
// version. Declared metadata comes first: the PEP 639 license expression, the
// "license" field when it names SPDX licenses, and a single unambiguous license
// classifier. The artifact's license files (a wheel's .dist-info, or the sdist's
// top level) supply the text that ships with the package, its copyright notice
// (PyPI metadata has no copyright field), and the license when nothing declared
// names one.
public class PyPiLicenseLookup : ILicenseLookup
{
    private const string FilesHost = "files.pythonhosted.org";

    // Longer, or multi-line, values of "license" are license texts, not names.
    private const int MaxLicenseNameLength = 100;

    private static readonly Regex NameSeparators = new(@"[-_.]+", RegexOptions.NonBacktracking);

    // project_urls keys that point at the project, in order of preference.
    private static readonly string[] ProjectUrlKeys = { "Homepage", "Home", "Source", "Source Code", "Code", "Repository", "GitHub" };

    private readonly HttpClient _http;
    private readonly ILicenseTextDetector _textDetector;
    private readonly ISpdxLicenseList _licenseList;
    private readonly ILogger<PyPiLicenseLookup> _logger;

    public PyPiLicenseLookup(HttpClient http, ILicenseTextDetector textDetector, ISpdxLicenseList licenseList, ILogger<PyPiLicenseLookup> logger)
    {
        _http = http;
        _textDetector = textDetector;
        _licenseList = licenseList;
        _logger = logger;
    }

    public string Ecosystem => "pypi";

    // PEP 503: "typing_extensions", "Typing.Extensions" and "typing-extensions" are one project.
    internal static string NormalizeName(string name) => NameSeparators.Replace(name.Trim(), "-").ToLowerInvariant();

    public async Task<LicenseLookupResult?> LookupLicenseAsync(string name, string version, CancellationToken cancellationToken = default)
    {
        var url = $"https://pypi.org/pypi/{Uri.EscapeDataString(NormalizeName(name))}/{Uri.EscapeDataString(version)}/json";

        try
        {
            Metadata metadata;
            using (var response = await _http.GetAsync(url, cancellationToken))
            {
                if (!response.IsSuccessStatusCode) return null;
                await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
                using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
                if (!doc.RootElement.TryGetProperty("info", out var info) || info.ValueKind != JsonValueKind.Object) return null;
                metadata = ReadMetadata(info, doc.RootElement);
            }

            return await ResolveAsync(name, version, metadata, cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or InvalidDataException ||
                                   (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            _logger.LogDebug(ex, "PyPI license lookup failed for {Name}@{Version}", name, version);
            return null;
        }
    }

    private sealed record Artifact(string FileName, string Url, bool IsWheel);

    private sealed record Metadata(
        string? LicenseExpression,
        string? License,
        IReadOnlyList<string> Classifiers,
        string? Authors,
        string? ProjectUrl,
        Artifact? Artifact);

    private async Task<LicenseLookupResult?> ResolveAsync(string name, string version, Metadata metadata, CancellationToken cancellationToken)
    {
        string? note = null;
        string? licenseFieldText = null;
        string? vagueLicense = null;
        string? expression = null;

        if (metadata.LicenseExpression != null)
        {
            // PEP 639: with a license expression, classifiers no longer declare the license.
            expression = DeclaredLicense.Normalize(metadata.LicenseExpression, _licenseList);
            if (expression == null)
                note = $"PyPI declares the license expression '{metadata.LicenseExpression}', which is not a valid SPDX expression";
        }
        else
        {
            if (metadata.License is { } license)
            {
                if (license.Length > MaxLicenseNameLength || license.Contains('\n'))
                    licenseFieldText = license;
                else if ((expression = DeclaredLicense.Normalize(license, _licenseList)) == null &&
                         !string.Equals(license, "UNKNOWN", StringComparison.OrdinalIgnoreCase))
                    vagueLicense = license;
            }

            // A classifier can make a vague license field precise ("LGPL" with
            // "LGPLv2+"), but never overrides one that names a license.
            expression ??= DeclaredLicense.FromClassifiers(metadata.Classifiers);
            if (expression == null && vagueLicense != null)
                note = $"PyPI declares the license as '{vagueLicense}', which names no single SPDX license";
        }

        var files = metadata.Artifact == null
            ? Array.Empty<ArchiveFile>()
            : await ReadLicenseFilesAsync(metadata.Artifact, cancellationToken);

        string? detectedFrom = null;
        if (expression == null)
        {
            var candidates = PackageArchive.LicenseFilesOnly(files);
            if (candidates.Count == 1 && _textDetector.DetectFromContent(candidates[0].Text) is { } detected)
            {
                expression = detected;
                detectedFrom = $"{metadata.Artifact!.FileName}::{candidates[0].Path}";
            }
            else if (files.Count == 0 && licenseFieldText != null && _textDetector.DetectFromContent(licenseFieldText) is { } fromField)
            {
                expression = fromField;
                detectedFrom = $"the PyPI license field of {name} {version}";
            }
            if (detectedFrom != null)
                _logger.LogInformation("Detected {Spdx} for {Name}@{Version} from {Source}", expression, name, version, detectedFrom);
        }

        var licenseText = PackageArchive.Combine(files) ?? licenseFieldText;
        if (expression == null && licenseText == null && note == null && metadata.Authors == null && metadata.ProjectUrl == null)
            return null;

        return new LicenseLookupResult(expression, Url: null, metadata.Authors, Copyright: null,
            ProjectUrl: metadata.ProjectUrl, LicenseText: licenseText, DetectedFrom: detectedFrom, Note: note);
    }

    // A wheel's license files sit in its .dist-info directory: under licenses/
    // (PEP 639, any depth) or, from older build tools, at its top level. An
    // sdist has them at the top level of the source tree.
    private async Task<IReadOnlyList<ArchiveFile>> ReadLicenseFilesAsync(Artifact artifact, CancellationToken cancellationToken)
    {
        var archive = await PackageArchive.DownloadAsync(_http, artifact.Url, _logger, cancellationToken);
        if (archive == null) return Array.Empty<ArchiveFile>();

        if (artifact.IsWheel)
        {
            return PackageArchive.ReadZip(archive, path =>
            {
                var slash = path.IndexOf('/');
                if (slash < 0 || !path[..slash].EndsWith(".dist-info", StringComparison.OrdinalIgnoreCase)) return null;
                var relative = path[(slash + 1)..];
                if (relative.StartsWith("licenses/", StringComparison.Ordinal)) return relative["licenses/".Length..];
                return !relative.Contains('/') && PackageArchive.IsLicenseFileName(relative) ? relative : null;
            });
        }

        string? SdistLicenseFile(string path)
        {
            var slash = path.IndexOf('/');
            if (slash < 0) return null;
            var relative = path[(slash + 1)..];
            return !relative.Contains('/') && PackageArchive.IsLicenseFileName(relative) ? relative : null;
        }
        return artifact.FileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
            ? PackageArchive.ReadZip(archive, SdistLicenseFile)
            : PackageArchive.ReadTarGz(archive, SdistLicenseFile);
    }

    private static Metadata ReadMetadata(JsonElement info, JsonElement root)
    {
        var classifiers = info.TryGetProperty("classifiers", out var c) && c.ValueKind == JsonValueKind.Array
            ? c.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!).ToList()
            : new List<string>();

        return new Metadata(
            StringProperty(info, "license_expression"),
            StringProperty(info, "license"),
            classifiers,
            RegistryAuthorFormatter.Combine(StringProperty(info, "author"), StringProperty(info, "author_email")),
            ReadProjectUrl(info),
            ChooseArtifact(root));
    }

    private static string? ReadProjectUrl(JsonElement info)
    {
        if (StringProperty(info, "home_page") is { } homePage && IsWebUrl(homePage))
            return homePage;
        if (!info.TryGetProperty("project_urls", out var urls) || urls.ValueKind != JsonValueKind.Object)
            return null;
        foreach (var key in ProjectUrlKeys)
        {
            foreach (var property in urls.EnumerateObject())
            {
                if (string.Equals(property.Name, key, StringComparison.OrdinalIgnoreCase) &&
                    property.Value.ValueKind == JsonValueKind.String &&
                    property.Value.GetString() is { } value && IsWebUrl(value))
                    return value;
            }
        }
        return null;
    }

    // Every artifact of a release is built from the same source tree, so one is
    // enough: a pure-Python wheel, else the smallest wheel, else the sdist.
    // Only PyPI's own file host is fetched.
    private static Artifact? ChooseArtifact(JsonElement root)
    {
        if (!root.TryGetProperty("urls", out var urls) || urls.ValueKind != JsonValueKind.Array)
            return null;

        var candidates = new List<(Artifact Artifact, long Size)>();
        foreach (var file in urls.EnumerateArray())
        {
            if (file.ValueKind != JsonValueKind.Object) continue;
            var fileName = StringProperty(file, "filename");
            var url = StringProperty(file, "url");
            var type = StringProperty(file, "packagetype");
            if (fileName == null || url == null || type is not ("bdist_wheel" or "sdist")) continue;
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps ||
                !string.Equals(uri.Host, FilesHost, StringComparison.OrdinalIgnoreCase)) continue;
            var size = file.TryGetProperty("size", out var s) && s.TryGetInt64(out var bytes) ? bytes : long.MaxValue;
            if (size > PackageArchive.MaxDownloadBytes) continue;
            candidates.Add((new Artifact(fileName, url, type == "bdist_wheel"), size));
        }

        return candidates
            .OrderBy(c => c.Artifact.IsWheel && c.Artifact.FileName.EndsWith("-none-any.whl", StringComparison.OrdinalIgnoreCase) ? 0
                        : c.Artifact.IsWheel ? 1 : 2)
            .ThenBy(c => c.Size)
            .ThenBy(c => c.Artifact.FileName, StringComparer.Ordinal)
            .Select(c => c.Artifact)
            .FirstOrDefault();
    }

    private static bool IsWebUrl(string value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp);

    private static string? StringProperty(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()!.Trim()
            : null;
}
