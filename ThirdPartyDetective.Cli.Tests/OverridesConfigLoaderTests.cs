using Microsoft.Extensions.Logging.Abstractions;
using ThirdPartyDetective.Cli.Services;

namespace ThirdPartyDetective.Cli.Tests;

public class OverridesConfigLoaderTests : IDisposable
{
    private readonly string _tempDir;
    private readonly IOverridesConfigLoader _loader =
        new OverridesConfigLoader(NullLogger<OverridesConfigLoader>.Instance);

    public OverridesConfigLoaderTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "tpd-overrides-tests-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true);
    }

    private string WriteConfig(string content)
    {
        var path = Path.Combine(_tempDir, ".tpd-overrides.json");
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public void Loads_entries()
    {
        var config = _loader.Load(WriteConfig("""
            { "overrides": [ { "name": "dompurify", "version": "3.4.0", "license": "Apache-2.0" } ] }
            """));
        Assert.Single(config.Overrides);
    }

    [Fact]
    public void Rejects_duplicates_that_differ_only_in_name_case()
    {
        var path = WriteConfig("""
            {
              "overrides": [
                { "name": "DomPurify", "version": "3.4.0", "license": "Apache-2.0" },
                { "name": "dompurify", "version": "3.4.0", "license": "MPL-2.0" }
              ]
            }
            """);

        var ex = Assert.Throws<InvalidOperationException>(() => _loader.Load(path));
        Assert.Contains("duplicate entry for 'dompurify@3.4.0'", ex.Message);
    }

    [Fact]
    public void Allows_the_same_name_for_different_versions()
    {
        var config = _loader.Load(WriteConfig("""
            {
              "overrides": [
                { "name": "dompurify", "version": "3.4.0", "license": "Apache-2.0" },
                { "name": "dompurify", "version": "3.5.0", "license": "Apache-2.0" }
              ]
            }
            """));
        Assert.Equal(2, config.Overrides.Count);
    }
}
