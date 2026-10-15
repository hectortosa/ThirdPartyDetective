using System.Formats.Tar;
using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace ThirdPartyDetective.Cli.Services;

// The SPDX license list (IDs, names and canonical texts of every license and
// exception) as bundled into the tool by scripts/update-spdx-license-list.sh.
// Bundling keeps ATTRIBUTION.txt reproducible and offline: texts never come
// from the network at runtime.
public interface ISpdxLicenseList
{
    // license-list-data version, e.g. "3.29.0".
    string Version { get; }

    // Non-deprecated license IDs (exceptions excluded), for text matching.
    IReadOnlyList<string> LicenseIds { get; }

    // The listed casing of a license or exception ID ("mit" → "MIT").
    bool TryGetCanonicalId(string id, out string canonicalId);

    // Canonical text for a listed ID, or null if the list has none.
    string? GetText(string canonicalId);
}

public sealed class BundledSpdxLicenseList : ISpdxLicenseList
{
    public const string ResourceName = "spdx-license-list.tar.gz";

    private readonly Lazy<Contents> _contents = new(Load);

    public string Version => _contents.Value.Version;

    public IReadOnlyList<string> LicenseIds => _contents.Value.LicenseIds;

    public bool TryGetCanonicalId(string id, out string canonicalId) =>
        _contents.Value.CanonicalIds.TryGetValue(id, out canonicalId!);

    public string? GetText(string canonicalId) =>
        _contents.Value.Texts.GetValueOrDefault(canonicalId);

    private sealed record Contents(
        string Version,
        IReadOnlyList<string> LicenseIds,
        IReadOnlyDictionary<string, string> CanonicalIds,
        IReadOnlyDictionary<string, string> Texts);

    private sealed record IndexFile(string LicenseListVersion, List<IndexEntry> Licenses, List<IndexEntry> Exceptions);

    private sealed record IndexEntry(string Id, string? Name, bool Deprecated);

    private static Contents Load()
    {
        using var resource = typeof(BundledSpdxLicenseList).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"Embedded resource {ResourceName} is missing from the build.");
        using var gzip = new GZipStream(resource, CompressionMode.Decompress);
        using var tar = new TarReader(gzip);

        IndexFile? index = null;
        var texts = new Dictionary<string, string>(StringComparer.Ordinal);
        while (tar.GetNextEntry() is { } entry)
        {
            if (entry.DataStream == null) continue;
            using var reader = new StreamReader(entry.DataStream, Encoding.UTF8);
            var content = reader.ReadToEnd();

            if (entry.Name == "index.json")
                index = JsonSerializer.Deserialize<IndexFile>(content, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            else if (entry.Name.StartsWith("texts/", StringComparison.Ordinal) && entry.Name.EndsWith(".txt", StringComparison.Ordinal))
                texts[entry.Name["texts/".Length..^".txt".Length]] = content;
        }

        if (index == null)
            throw new InvalidOperationException($"Embedded resource {ResourceName} has no index.json.");

        var canonical = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in index.Licenses.Concat(index.Exceptions))
            canonical.TryAdd(e.Id, e.Id);

        var licenseIds = index.Licenses.Where(l => !l.Deprecated).Select(l => l.Id).ToList();
        return new Contents(index.LicenseListVersion, licenseIds, canonical, texts);
    }
}
