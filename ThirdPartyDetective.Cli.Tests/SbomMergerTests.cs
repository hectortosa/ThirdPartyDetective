using Microsoft.Extensions.Logging.Abstractions;
using ThirdPartyDetective.Cli.Models;
using ThirdPartyDetective.Cli.Services;

namespace ThirdPartyDetective.Cli.Tests;

public class SbomMergerTests : IDisposable
{
    private readonly string _tempDir;

    public SbomMergerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "tpd-merger-tests-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true);
    }

    private string WriteSbom(string filename, string content)
    {
        var path = Path.Combine(_tempDir, filename);
        File.WriteAllText(path, content);
        return path;
    }

    private async Task<IReadOnlyList<ThirdPartyInfo>> Merge(params string[] paths)
    {
        var reader = new SpdxSbomReader(new SpdxLicenseExpressionAdapter());
        var merger = new SbomMerger(reader, NullLogger<SbomMerger>.Instance);
        return await merger.MergeAsync(paths);
    }

    [Fact]
    public async Task First_non_NOASSERTION_license_wins_across_sources()
    {
        var first = WriteSbom("a.spdx.json", """
            {
              "spdxVersion": "SPDX-2.3",
              "packages": [
                { "SPDXID": "SPDXRef-A", "name": "alpha", "versionInfo": "1.0.0", "licenseConcluded": "NOASSERTION" }
              ]
            }
            """);
        var second = WriteSbom("b.spdx.json", """
            {
              "spdxVersion": "SPDX-2.3",
              "packages": [
                { "SPDXID": "SPDXRef-A", "name": "alpha", "versionInfo": "1.0.0", "licenseConcluded": "MIT" }
              ]
            }
            """);

        var merged = await Merge(first, second);

        Assert.Single(merged);
        Assert.Equal("MIT", merged[0].LicenseExpression);
    }

    [Fact]
    public async Task Disagreement_warning_when_two_sources_have_different_licenses()
    {
        var first = WriteSbom("a.spdx.json", """
            {
              "spdxVersion": "SPDX-2.3",
              "packages": [
                { "SPDXID": "SPDXRef-A", "name": "alpha", "versionInfo": "1.0.0", "licenseConcluded": "MIT" }
              ]
            }
            """);
        var second = WriteSbom("b.spdx.json", """
            {
              "spdxVersion": "SPDX-2.3",
              "packages": [
                { "SPDXID": "SPDXRef-A", "name": "alpha", "versionInfo": "1.0.0", "licenseConcluded": "Apache-2.0" }
              ]
            }
            """);

        var merged = await Merge(first, second);

        Assert.Single(merged);
        Assert.Equal("MIT", merged[0].LicenseExpression);
        Assert.Contains(merged[0].WarningFlags, w => w.Contains("disagreement"));
    }

    [Fact]
    public async Task Different_versions_are_treated_as_distinct_packages()
    {
        var first = WriteSbom("a.spdx.json", """
            {
              "spdxVersion": "SPDX-2.3",
              "packages": [
                { "SPDXID": "SPDXRef-A1", "name": "alpha", "versionInfo": "1.0.0", "licenseConcluded": "MIT" },
                { "SPDXID": "SPDXRef-A2", "name": "alpha", "versionInfo": "2.0.0", "licenseConcluded": "Apache-2.0" }
              ]
            }
            """);
        var merged = await Merge(first);
        Assert.Equal(2, merged.Count);
    }

    [Fact]
    public async Task Empty_sbom_path_list_throws()
    {
        var reader = new SpdxSbomReader(new SpdxLicenseExpressionAdapter());
        var merger = new SbomMerger(reader, NullLogger<SbomMerger>.Instance);
        await Assert.ThrowsAsync<ArgumentException>(() => merger.MergeAsync(Array.Empty<string>()));
    }
}
