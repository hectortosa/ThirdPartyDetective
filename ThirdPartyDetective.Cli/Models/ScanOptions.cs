using System.ComponentModel.DataAnnotations;

namespace ThirdPartyDetective.Cli.Models;

public class ScanOptions
{
    [Required]
    public List<string> SbomPaths { get; set; } = new();

    public string Output { get; set; } = "./licenses";

    public OutputFormat Format { get; set; } = OutputFormat.All;

    public ScanMode Mode { get; set; } = ScanMode.All;

    public List<string> ExcludeLicenseTypes { get; set; } = new();

    public List<string> IncludeLicenseTypes { get; set; } = new();

    public List<string> ExcludeNamespaces { get; set; } = new();

    public bool NoFail { get; set; } = false;

    public bool RegistryFallback { get; set; } = false;

    public string RegistryFallbackCachePath { get; set; } = "./.tpd-license-cache.json";

    public string? PolicyConfigPath { get; set; }

    public string? OverridesConfigPath { get; set; }

    // Legal wording for ATTRIBUTION.txt; auto-discovered as .tpd-notices.json.
    public string? NoticeTemplatePath { get; set; }

    public bool Verbose { get; set; } = false;

    public bool DevInfo { get; set; } = false;

    public bool IncludeLicenseText { get; set; } = false;

    // Exit 2 when any package's license is undetermined or a license text is
    // missing from the ATTRIBUTION.txt appendix.
    public bool FailOnUnresolved { get; set; } = false;

    // No wall-clock timestamp in the reports (SOURCE_DATE_EPOCH still applies).
    public bool Deterministic { get; set; } = false;

    // The SOURCE_DATE_EPOCH environment variable (reproducible-builds.org), if set.
    public string? SourceDateEpoch { get; set; }

    // No network requests at all: registry fallback only reads its cache.
    public bool Offline { get; set; } = false;
}

public enum OutputFormat
{
    All,
    Markdown,
    Json,
    Html,
    FullText,
    Console
}

public enum ScanMode
{
    All,
    Saas,
    Distribution
}
