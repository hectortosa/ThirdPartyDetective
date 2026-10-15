using System.Text.Json.Serialization;

namespace ThirdPartyDetective.Cli.Models;

public class OverrideConfig
{
    [JsonPropertyName("overrides")]
    public List<OverrideEntry> Overrides { get; set; } = new();
}

public class OverrideEntry
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("version")]
    public string Version { get; set; } = string.Empty;

    [JsonPropertyName("license")]
    public string? License { get; set; }

    [JsonPropertyName("authors")]
    public string? Authors { get; set; }

    [JsonPropertyName("copyright")]
    public string? Copyright { get; set; }

    // Path, relative to the overrides file, of the license text to ship with this
    // package, e.g. a vendor EULA when "license" is "LicenseRef-Acme-EULA".
    [JsonPropertyName("licenseTextFile")]
    public string? LicenseTextFile { get; set; }

    // Contents of LicenseTextFile, read by OverridesConfigLoader.
    [JsonIgnore]
    public string? LicenseText { get; set; }

    [JsonPropertyName("reviewer")]
    public string? Reviewer { get; set; }

    [JsonPropertyName("reviewedAt")]
    public string? ReviewedAt { get; set; }

    [JsonPropertyName("notes")]
    public string? Notes { get; set; }
}
