using System.Text.Json;

namespace ThirdPartyDetective.Cli.Services;

// Shared JsonSerializerOptions baseline for hand-edited config files
// (.tpd-policy.json, .tpd-overrides.json). Tolerant of comments and trailing
// commas so users can document their files inline.
internal static class ConfigJson
{
    public static JsonSerializerOptions CreateOptions()
    {
        return new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true
        };
    }
}
