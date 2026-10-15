using System.Runtime.CompilerServices;
using System.Text.Json;
using ThirdPartyDetective.Cli.Models;

namespace ThirdPartyDetective.Cli.Services;

public class SpdxSbomReader : ISpdxSbomReader
{
    private readonly ISpdxLicenseExpressionAdapter _expressionAdapter;

    public SpdxSbomReader(ISpdxLicenseExpressionAdapter expressionAdapter)
    {
        _expressionAdapter = expressionAdapter;
    }

    public async IAsyncEnumerable<ThirdPartyInfo> ReadAsync(
        string sbomPath,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (!File.Exists(sbomPath))
            throw new FileNotFoundException($"SBOM file not found: {sbomPath}", sbomPath);

        await using var stream = File.OpenRead(sbomPath);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

        if (!doc.RootElement.TryGetProperty("spdxVersion", out var versionElement))
            throw new InvalidOperationException(
                "Input is not a valid SPDX document: missing required 'spdxVersion' field.");

        var version = versionElement.ValueKind == JsonValueKind.String ? versionElement.GetString() ?? "" : "";
        if (version != "SPDX-2.2" && version != "SPDX-2.3")
            throw new InvalidOperationException(
                $"Unsupported SPDX version '{version}'. This tool supports SPDX-2.2 and SPDX-2.3.");

        if (!doc.RootElement.TryGetProperty("packages", out var packages) ||
            packages.ValueKind != JsonValueKind.Array)
            yield break;

        var extractedLicenses = ReadExtractedLicenses(doc.RootElement);

        foreach (var pkg in packages.EnumerateArray())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (pkg.ValueKind != JsonValueKind.Object) continue;

            if (string.Equals(TryGetString(pkg, "SPDXID"), "SPDXRef-DOCUMENT", StringComparison.Ordinal))
                continue;

            if (IsScanTargetWrapper(pkg))
                continue;

            var info = ProjectToThirdPartyInfo(pkg, extractedLicenses);
            if (info != null)
                yield return info;
        }
    }

    // Trivy describes what it scanned with wrapper packages that are not
    // dependencies: SOURCE for the filesystem root and APPLICATION for each
    // deps.json / lock file, with the real packages linked to them via
    // relationships. SPDX 2.3 also allows APPLICATION on a real dependency, so
    // an APPLICATION package is only skipped when it has no purl.
    private static bool IsScanTargetWrapper(JsonElement pkg)
    {
        var purpose = TryGetString(pkg, "primaryPackagePurpose");
        if (string.Equals(purpose, "SOURCE", StringComparison.OrdinalIgnoreCase))
            return true;
        return string.Equals(purpose, "APPLICATION", StringComparison.OrdinalIgnoreCase) && FindPurl(pkg) == null;
    }

    private ThirdPartyInfo? ProjectToThirdPartyInfo(JsonElement pkg, IReadOnlyDictionary<string, ExtractedLicense> extractedLicenses)
    {
        var name = TryGetString(pkg, "name");
        if (string.IsNullOrEmpty(name))
            return null;

        var info = new ThirdPartyInfo { Name = name };

        info.Version = TryGetString(pkg, "versionInfo") ?? string.Empty;
        info.Description = NormalizeNoassertion(TryGetString(pkg, "description") ?? TryGetString(pkg, "summary")) ?? string.Empty;
        info.Authors = ParseSpdxParty(TryGetString(pkg, "originator") ?? TryGetString(pkg, "supplier")) ?? string.Empty;
        info.Copyright = NormalizeNoassertion(TryGetString(pkg, "copyrightText")) ?? string.Empty;
        info.ProjectUrl = NormalizeNoassertion(TryGetString(pkg, "homepage"));

        var rawLicense = ResolveLicenseExpression(pkg);
        var licenseInfo = _expressionAdapter.Normalize(rawLicense);
        info.LicenseExpression = licenseInfo.Normalized;
        info.LicenseSubExpressions = licenseInfo.SubExpressions.ToList();
        info.LicenseExceptions = licenseInfo.Exceptions.ToList();
        foreach (var exception in licenseInfo.Exceptions)
            info.WarningFlags.Add($"License has exception clause: WITH {exception}; verify exception scope");

        foreach (var licenseId in licenseInfo.SubExpressions)
        {
            if (extractedLicenses.TryGetValue(licenseId, out var extracted))
                info.ExtractedLicenses.Add(extracted);
        }

        info.SourceEcosystem = InferEcosystem(FindPurl(pkg));

        return info;
    }

    // licenseConcluded wins over licenseDeclared. When neither is asserted, the
    // licenses found in the package's files all apply, so they are combined
    // with AND rather than picking one.
    private static string? ResolveLicenseExpression(JsonElement pkg)
    {
        var concluded = TryGetString(pkg, "licenseConcluded");
        if (!IsNoassertion(concluded)) return concluded;

        var declared = TryGetString(pkg, "licenseDeclared");
        if (!IsNoassertion(declared)) return declared;

        if (pkg.TryGetProperty("licenseInfoFromFiles", out var infoFromFiles) &&
            infoFromFiles.ValueKind == JsonValueKind.Array)
        {
            var fileLicenses = infoFromFiles.EnumerateArray()
                .Where(item => item.ValueKind == JsonValueKind.String)
                .Select(item => item.GetString())
                .Where(v => !IsNoassertion(v))
                .Select(v => v!.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (fileLicenses.Count == 1) return fileLicenses[0];
            if (fileLicenses.Count > 1)
                return string.Join(" AND ", fileLicenses.Select(l => l.Contains(' ') ? $"({l})" : l));
        }
        return null;
    }

    private static IReadOnlyDictionary<string, ExtractedLicense> ReadExtractedLicenses(JsonElement root)
    {
        var result = new Dictionary<string, ExtractedLicense>(StringComparer.Ordinal);
        if (!root.TryGetProperty("hasExtractedLicensingInfos", out var infos) || infos.ValueKind != JsonValueKind.Array)
            return result;

        foreach (var item in infos.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            var id = TryGetString(item, "licenseId");
            var text = TryGetString(item, "extractedText");
            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(text)) continue;
            result[id] = new ExtractedLicense(id, NormalizeNoassertion(TryGetString(item, "name")),
                IsPlaceholderText(text) ? null : text);
        }
        return result;
    }

    // Trivy has no text for licenses it only knows by name ("SEE LICENSE IN
    // LICENSE", "Public Domain") and writes this sentence as their extractedText.
    // Printed as a license text it would tell the customer nothing; without it,
    // the package's own license file takes its place.
    private static bool IsPlaceholderText(string text)
    {
        const string prefix = "This component is licensed under \"";
        var trimmed = text.Trim();
        return trimmed.StartsWith(prefix, StringComparison.Ordinal) && trimmed.EndsWith('"') &&
               trimmed.IndexOf('\n') < 0 && trimmed.Length < 300;
    }

    private static string? FindPurl(JsonElement pkg)
    {
        if (!pkg.TryGetProperty("externalRefs", out var refs) || refs.ValueKind != JsonValueKind.Array)
            return null;

        foreach (var r in refs.EnumerateArray())
        {
            if (r.ValueKind != JsonValueKind.Object) continue;
            var locator = TryGetString(r, "referenceLocator");
            if (!string.IsNullOrEmpty(locator) && locator.StartsWith("pkg:", StringComparison.Ordinal))
                return locator;
        }
        return null;
    }

    private static string? InferEcosystem(string? purl)
    {
        if (purl == null) return null;

        var slashIdx = purl.IndexOf('/', 4);
        if (slashIdx <= 4) return null;

        var type = purl[4..slashIdx];
        return type switch
        {
            "nuget" => "NuGet",
            "npm" => "npm",
            "pypi" => "Python",
            "maven" => "Maven",
            "cargo" => "Rust",
            "gem" => "Ruby",
            "golang" => "Go",
            "deb" => "Debian",
            "rpm" => "RPM",
            "apk" => "Alpine",
            "github" => "GitHub",
            _ => type
        };
    }

    // Non-string values (numbers, arrays, objects) are treated as absent rather
    // than aborting the whole run.
    private static string? TryGetString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var prop)) return null;
        return prop.ValueKind == JsonValueKind.String ? prop.GetString() : null;
    }

    private static bool IsNoassertion(string? value) =>
        string.IsNullOrWhiteSpace(value) ||
        string.Equals(value.Trim(), "NOASSERTION", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(value.Trim(), "NONE", StringComparison.OrdinalIgnoreCase);

    private static string? NormalizeNoassertion(string? value) =>
        IsNoassertion(value) ? null : value;

    private static string? ParseSpdxParty(string? value)
    {
        if (string.IsNullOrEmpty(value) || IsNoassertion(value)) return null;

        var colonIdx = value.IndexOf(':');
        if (colonIdx > 0 && colonIdx + 1 < value.Length)
            return value[(colonIdx + 1)..].Trim();

        return value;
    }
}
