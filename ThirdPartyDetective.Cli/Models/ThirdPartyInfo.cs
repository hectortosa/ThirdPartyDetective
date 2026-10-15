using System.Text.Json.Serialization;

namespace ThirdPartyDetective.Cli.Models;

public class ThirdPartyInfo
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("version")]
    public string Version { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    public string Description { get; set; } = string.Empty;

    [JsonPropertyName("authors")]
    public string Authors { get; set; } = string.Empty;

    [JsonPropertyName("copyright")]
    public string Copyright { get; set; } = string.Empty;

    [JsonPropertyName("projectUrl")]
    public string? ProjectUrl { get; set; }

    [JsonPropertyName("licenseUrl")]
    public string? LicenseUrl { get; set; }

    [JsonPropertyName("licenseExpression")]
    public string? LicenseExpression { get; set; }

    [JsonPropertyName("licenseType")]
    public LicenseType LicenseType { get; set; }

    [JsonPropertyName("licenseText")]
    public string? LicenseText { get; set; }

    [JsonPropertyName("requiresAttribution")]
    public bool RequiresAttribution { get; set; }

    [JsonPropertyName("saasCompliant")]
    public bool SaasCompliant { get; set; }

    [JsonPropertyName("warningFlags")]
    public List<string> WarningFlags { get; set; } = new();

    [JsonPropertyName("sourceEcosystem")]
    public string? SourceEcosystem { get; set; }

    [JsonPropertyName("sourceProjects")]
    public List<string> SourceProjects { get; set; } = new();

    [JsonPropertyName("licenseSubExpressions")]
    public List<string> LicenseSubExpressions { get; set; } = new();

    // WITH exceptions in the license expression, e.g. "Classpath-exception-2.0".
    [JsonPropertyName("licenseExceptions")]
    public List<string> LicenseExceptions { get; set; } = new();

    // Texts for the LicenseRef-* identifiers this package's expression uses,
    // taken from the SBOM's hasExtractedLicensingInfos.
    [JsonPropertyName("extractedLicenses")]
    public List<ExtractedLicense> ExtractedLicenses { get; set; } = new();

    [JsonPropertyName("licenseSource")]
    public LicenseSource LicenseSource { get; set; } = LicenseSource.None;

    [JsonPropertyName("isViolation")]
    public bool IsViolation { get; set; }
}

// Text is null when the SBOM has no real text for the LicenseRef (Trivy writes
// a placeholder sentence when it only knows the license's name).
public record ExtractedLicense(
    [property: JsonPropertyName("licenseId")] string LicenseId,
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("extractedText")] string? Text);

public enum LicenseType
{
    Unknown,
    MIT,
    Apache20,
    BSD2Clause,
    BSD3Clause,
    GPL20,
    GPL30,
    LGPL21,
    LGPL30,
    AGPL30,
    MPL20,
    EPL20,
    ISC,
    Unlicense,
    CC0,
    Proprietary,
    Multiple
}

public class PackageAnalysisResult
{
    [JsonPropertyName("packages")]
    public List<ThirdPartyInfo> Packages { get; set; } = new();

    [JsonPropertyName("violations")]
    public List<ThirdPartyInfo> Violations { get; set; } = new();

    [JsonPropertyName("summary")]
    public AnalysisSummary Summary { get; set; } = new();

    // Null with --deterministic (and no SOURCE_DATE_EPOCH): the reports then carry no
    // wall-clock time, so identical inputs give byte-identical outputs.
    [JsonPropertyName("generatedAt")]
    public DateTime? GeneratedAt { get; set; }

    [JsonPropertyName("scanPath")]
    public string ScanPath { get; set; } = string.Empty;

    [JsonPropertyName("mode")]
    public string Mode { get; set; } = string.Empty;
}

public class AnalysisSummary
{
    [JsonPropertyName("totalPackages")]
    public int TotalPackages { get; set; }

    [JsonPropertyName("packagesRequiringAttribution")]
    public int PackagesRequiringAttribution { get; set; }

    [JsonPropertyName("warningCount")]
    public int WarningCount { get; set; }

    [JsonPropertyName("licenseTypeCounts")]
    public Dictionary<string, int> LicenseTypeCounts { get; set; } = new();

    [JsonPropertyName("documentWarnings")]
    public List<string> DocumentWarnings { get; set; } = new();

    // "name@version" of packages with no license information at all after
    // SBOM merge, registry fallback and overrides.
    [JsonPropertyName("unresolvedLicenses")]
    public List<string> UnresolvedLicenses { get; set; } = new();

    // License or exception IDs whose text could not be included in the
    // ATTRIBUTION.txt appendix (only populated with --include-license-text).
    [JsonPropertyName("missingLicenseTexts")]
    public List<string> MissingLicenseTexts { get; set; } = new();
}