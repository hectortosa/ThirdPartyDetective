using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace ThirdPartyDetective.Cli.Services.Registry;

public class NuGetLicenseLookup : ILicenseLookup
{
    // NuGet's deprecation placeholder URL — packages that should have migrated from
    // licenseUrl to licenseExpression but didn't get updated. Has no actual license content.
    private const string DeprecatedLicenseUrl = "https://aka.ms/deprecateLicenseUrl";

    private readonly HttpClient _http;
    private readonly ILicenseTextDetector _textDetector;
    private readonly ILogger<NuGetLicenseLookup> _logger;

    public NuGetLicenseLookup(HttpClient http, ILicenseTextDetector textDetector, ILogger<NuGetLicenseLookup> logger)
    {
        _http = http;
        _textDetector = textDetector;
        _logger = logger;
    }

    public string Ecosystem => "nuget";

    public async Task<LicenseLookupResult?> LookupLicenseAsync(string name, string version, CancellationToken cancellationToken = default)
    {
        // The gz-semver2 registration hive lists every package version. The semver1 hive
        // leaves out SemVer 2.0 versions (dotted prerelease labels such as "1.15.3-beta.1",
        // or build metadata), which left those packages without a license.
        var url = $"https://api.nuget.org/v3/registration5-gz-semver2/{Uri.EscapeDataString(name.ToLowerInvariant())}/{Uri.EscapeDataString(NormalizeVersion(version))}.json";
        try
        {
            using var response = await _http.GetAsync(url, cancellationToken);
            if (!response.IsSuccessStatusCode) return null;

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

            JsonElement catalogEntry;
            if (!doc.RootElement.TryGetProperty("catalogEntry", out catalogEntry))
                return null;

            // catalogEntry can be inline or a URL reference; resolve to the inline JSON.
            if (catalogEntry.ValueKind == JsonValueKind.String)
            {
                var innerUrl = catalogEntry.GetString();
                if (string.IsNullOrEmpty(innerUrl)) return null;

                using var innerResponse = await _http.GetAsync(innerUrl, cancellationToken);
                if (!innerResponse.IsSuccessStatusCode) return null;
                await using var innerStream = await innerResponse.Content.ReadAsStreamAsync(cancellationToken);
                using var innerDoc = await JsonDocument.ParseAsync(innerStream, cancellationToken: cancellationToken);
                return await ExtractLicenseAsync(innerDoc.RootElement, name, version, cancellationToken);
            }

            return await ExtractLicenseAsync(catalogEntry, name, version, cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException ||
                                   (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            _logger.LogDebug(ex, "NuGet license lookup failed for {Name}@{Version}", name, version);
            return null;
        }
    }

    // NuGet's normalized version string, as used in registration and flat-container
    // URLs: build metadata dropped, leading zeros removed, at least three parts and a
    // zero fourth part dropped ("1.0.0.0" → "1.0.0", "1.2" → "1.2.0"), lower case.
    internal static string NormalizeVersion(string version)
    {
        var v = version.Trim();
        var plus = v.IndexOf('+');
        if (plus >= 0) v = v[..plus];

        var dash = v.IndexOf('-');
        var release = dash >= 0 ? v[..dash] : v;
        var prerelease = dash >= 0 ? v[dash..] : string.Empty;

        var parts = release.Split('.').ToList();
        if (parts.Count is >= 1 and <= 4 && parts.All(p => p.Length > 0 && p.All(char.IsAsciiDigit)))
        {
            parts = parts.Select(p => p.TrimStart('0') is { Length: > 0 } trimmed ? trimmed : "0").ToList();
            while (parts.Count < 3) parts.Add("0");
            if (parts.Count == 4 && parts[3] == "0") parts.RemoveAt(3);
        }
        return (string.Join('.', parts) + prerelease).ToLowerInvariant();
    }

    private async Task<LicenseLookupResult?> ExtractLicenseAsync(JsonElement catalogEntry, string name, string version, CancellationToken cancellationToken)
    {
        string? expression = null;
        string? licenseUrl = null;
        string? licenseFile = null;
        string? authors = null;
        string? copyright = null;

        if (catalogEntry.TryGetProperty("licenseExpression", out var expr) && expr.ValueKind == JsonValueKind.String)
        {
            var v = expr.GetString();
            if (!string.IsNullOrWhiteSpace(v)) expression = v;
        }
        if (catalogEntry.TryGetProperty("licenseUrl", out var lu) && lu.ValueKind == JsonValueKind.String)
        {
            var v = lu.GetString();
            // Filter out the deprecation placeholder URL — it has no actual license content,
            // and surfacing it just gives the LicenseTextDetector a useless hop.
            if (!string.IsNullOrWhiteSpace(v) && !string.Equals(v, DeprecatedLicenseUrl, StringComparison.OrdinalIgnoreCase))
                licenseUrl = v;
        }
        if (catalogEntry.TryGetProperty("licenseFile", out var lf) && lf.ValueKind == JsonValueKind.String)
        {
            var v = lf.GetString();
            if (!string.IsNullOrWhiteSpace(v)) licenseFile = v;
        }
        if (catalogEntry.TryGetProperty("authors", out var au) && au.ValueKind == JsonValueKind.String)
        {
            var v = au.GetString();
            if (!string.IsNullOrWhiteSpace(v)) authors = v;
        }
        if (catalogEntry.TryGetProperty("copyright", out var cp) && cp.ValueKind == JsonValueKind.String)
        {
            var v = cp.GetString();
            if (!string.IsNullOrWhiteSpace(v)) copyright = v;
        }

        // If we have an explicit SPDX expression, that's authoritative — done.
        if (!string.IsNullOrEmpty(expression))
            return new LicenseLookupResult(expression, licenseUrl, authors, copyright);

        // For modern <license type="file"> packages, the actual license sits inside the
        // .nupkg at the named path. Its text is kept either way: when it doesn't match a
        // known license, it is still the text that has to accompany the package.
        string? licenseText = null;
        if (!string.IsNullOrEmpty(licenseFile))
        {
            licenseText = await TryReadLicenseFileAsync(name, version, licenseFile, cancellationToken);
            var detected = licenseText == null ? null : _textDetector.DetectFromContent(licenseText);
            if (!string.IsNullOrEmpty(detected))
            {
                _logger.LogInformation("Detected {Spdx} for {Name}@{Version} from .nupkg::{File}",
                    detected, name, version, licenseFile);
                return new LicenseLookupResult(detected, licenseUrl, authors, copyright,
                    LicenseText: licenseText, DetectedFrom: $"{name}.{version}.nupkg::{licenseFile}");
            }
        }

        return (expression == null && licenseUrl == null && authors == null && copyright == null && licenseText == null)
            ? null
            : new LicenseLookupResult(expression, licenseUrl, authors, copyright, LicenseText: licenseText);
    }

    private async Task<string?> TryReadLicenseFileAsync(string name, string version, string licenseFilePath, CancellationToken cancellationToken)
    {
        var lowerName = name.ToLowerInvariant();
        var lowerVersion = NormalizeVersion(version);
        var nupkgUrl = $"https://api.nuget.org/v3-flatcontainer/{Uri.EscapeDataString(lowerName)}/{Uri.EscapeDataString(lowerVersion)}/{Uri.EscapeDataString(lowerName)}.{Uri.EscapeDataString(lowerVersion)}.nupkg";

        var archive = await PackageArchive.DownloadAsync(_http, nupkgUrl, _logger, cancellationToken);
        if (archive == null) return null;

        try
        {
            // nuspec paths may use either separator.
            var wanted = licenseFilePath.Replace('\\', '/');
            var files = PackageArchive.ReadZip(archive, path =>
                string.Equals(path, wanted, StringComparison.OrdinalIgnoreCase) ? path : null);
            if (files.Count == 0)
                _logger.LogDebug("License file '{File}' not found inside .nupkg for {Name}@{Version}", licenseFilePath, name, version);
            return files.FirstOrDefault()?.Text;
        }
        catch (InvalidDataException ex)
        {
            _logger.LogDebug(ex, "Failed to extract license file from .nupkg for {Name}@{Version}", name, version);
            return null;
        }
    }
}
