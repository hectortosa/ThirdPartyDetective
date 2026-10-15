using System.Text.Json.Serialization;

namespace ThirdPartyDetective.Cli.Models;

public class PolicyConfig
{
    [JsonPropertyName("modes")]
    public Dictionary<string, ModePolicy> Modes { get; set; } = new();

    public static PolicyConfig Default()
    {
        return new PolicyConfig
        {
            Modes = new Dictionary<string, ModePolicy>(StringComparer.OrdinalIgnoreCase)
            {
                ["saas"] = new ModePolicy
                {
                    Violations = new HashSet<LicenseType>
                    {
                        LicenseType.GPL20,
                        LicenseType.GPL30,
                        LicenseType.AGPL30,
                        LicenseType.Proprietary,
                        LicenseType.Unknown,
                        LicenseType.Multiple
                    }
                },
                ["distribution"] = new ModePolicy
                {
                    Violations = new HashSet<LicenseType>
                    {
                        LicenseType.AGPL30,
                        LicenseType.Proprietary,
                        LicenseType.Unknown,
                        LicenseType.Multiple
                    }
                }
            }
        };
    }

    public ModePolicy? ResolveMode(ScanMode mode)
    {
        if (mode == ScanMode.All) return null;
        var key = mode.ToString();
        // Mode keys are user-supplied JSON ("saas", "distribution") — case-insensitive match.
        foreach (var (k, v) in Modes)
            if (string.Equals(k, key, StringComparison.OrdinalIgnoreCase))
                return v;
        return null;
    }
}

public class ModePolicy
{
    [JsonPropertyName("violations")]
    public HashSet<LicenseType>? Violations { get; set; }

    [JsonPropertyName("allowed")]
    public HashSet<LicenseType>? Allowed { get; set; }
}
