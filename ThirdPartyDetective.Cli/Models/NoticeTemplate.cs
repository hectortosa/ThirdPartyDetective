using System.Text.Json.Serialization;

namespace ThirdPartyDetective.Cli.Models;

// Wording for ATTRIBUTION.txt that carries legal weight and therefore belongs to
// whoever ships the notices (legal), not to the tool: how source code of copyleft
// components is made available, and any statement about it. Without a template
// the tool prints only facts.
public class NoticeTemplate
{
    public static readonly IReadOnlyList<string> CopyleftFamilies = new[] { "GPL", "LGPL", "AGPL" };

    // Printed after the title, e.g. a document-wide source code offer.
    [JsonPropertyName("header")]
    public string? Header { get; set; }

    // Printed after the package list, before Appendix A.
    [JsonPropertyName("footer")]
    public string? Footer { get; set; }

    // Per-package note for GPL, LGPL and AGPL components, replacing the default.
    // Placeholders: {name} {version} {license} {projectUrl}. An empty string
    // prints no note for that family.
    [JsonPropertyName("copyleftNotes")]
    public Dictionary<string, string> CopyleftNotes { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}
