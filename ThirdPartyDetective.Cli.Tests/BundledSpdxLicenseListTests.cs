using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using ThirdPartyDetective.Cli.Services;
using ThirdPartyDetective.Cli.Tests.Infrastructure;

namespace ThirdPartyDetective.Cli.Tests;

public class BundledSpdxLicenseListTests
{
    private static readonly BundledSpdxLicenseList List = new();

    // The archive is binary; the manifest is the reviewable record of it. Every
    // entry of the embedded archive (index.json included) must appear in the
    // manifest with the same SHA-256, and the manifest must list nothing else.
    [Fact]
    public void Embedded_archive_matches_the_committed_manifest_exactly()
    {
        var manifestPath = Path.Combine(TestPaths.RepoRoot, "ThirdPartyDetective.Cli", "Resources", "spdx-license-list.manifest");
        var manifest = File.ReadAllLines(manifestPath)
            .Where(l => l.Length > 0 && !l.StartsWith('#'))
            .ToDictionary(l => l[66..], l => l[..64], StringComparer.Ordinal);

        var archive = new Dictionary<string, string>(StringComparer.Ordinal);
        using (var resource = typeof(BundledSpdxLicenseList).Assembly.GetManifestResourceStream(BundledSpdxLicenseList.ResourceName)!)
        using (var gzip = new GZipStream(resource, CompressionMode.Decompress))
        using (var tar = new TarReader(gzip))
        {
            while (tar.GetNextEntry() is { DataStream: { } data } entry)
            {
                var name = entry.Name == "index.json" ? "index.json" : entry.Name["texts/".Length..^".txt".Length];
                archive[name] = Convert.ToHexStringLower(SHA256.HashData(data));
            }
        }

        Assert.Equal(826, manifest.Count);
        Assert.Equal(manifest.OrderBy(kv => kv.Key, StringComparer.Ordinal), archive.OrderBy(kv => kv.Key, StringComparer.Ordinal));
    }

    [Fact]
    public void Reports_its_version_and_license_ids()
    {
        Assert.Equal("3.29.0", List.Version);
        Assert.Equal(708, List.LicenseIds.Count);
        Assert.Contains("PostgreSQL", List.LicenseIds);
        Assert.DoesNotContain("Classpath-exception-2.0", List.LicenseIds);
    }

    [Theory]
    [InlineData("mit", "MIT")]
    [InlineData("apache-2.0", "Apache-2.0")]
    [InlineData("classpath-exception-2.0", "Classpath-exception-2.0")]
    [InlineData("GPL-2.0", "GPL-2.0")]
    public void Canonicalizes_id_casing(string id, string expected)
    {
        Assert.True(List.TryGetCanonicalId(id, out var canonical));
        Assert.Equal(expected, canonical);
    }

    [Fact]
    public void Serves_license_and_exception_texts()
    {
        Assert.StartsWith("GNU GENERAL PUBLIC LICENSE", List.GetText("GPL-3.0-only")!.TrimStart());
        Assert.StartsWith("Linking this library statically or dynamically", List.GetText("Classpath-exception-2.0")!.TrimStart());
        Assert.False(List.TryGetCanonicalId("Made-Up-License-1.0", out _));
    }
}
