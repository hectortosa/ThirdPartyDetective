using Microsoft.Extensions.Logging.Abstractions;
using ThirdPartyDetective.Cli.Models;
using ThirdPartyDetective.Cli.Services;

namespace ThirdPartyDetective.Cli.Tests;

public class PolicyConfigLoaderTests : IDisposable
{
    private readonly string _tempDir;
    private readonly IPolicyConfigLoader _loader =
        new PolicyConfigLoader(NullLogger<PolicyConfigLoader>.Instance);

    public PolicyConfigLoaderTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "tpd-policy-tests-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true);
    }

    private string WriteConfig(string content)
    {
        var path = Path.Combine(_tempDir, "policy.json");
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public void Returns_built_in_default_when_no_file_present()
    {
        var policy = _loader.Load(null);
        Assert.NotNull(policy);
        Assert.Contains("saas", policy.Modes.Keys, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("distribution", policy.Modes.Keys, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void Throws_when_explicit_path_does_not_exist()
    {
        Assert.Throws<FileNotFoundException>(() => _loader.Load("/nonexistent/path.json"));
    }

    [Fact]
    public void Loads_policy_with_violations_blocklist()
    {
        var path = WriteConfig("""
            {
              "modes": {
                "distribution": { "violations": ["AGPL-3.0", "GPL-3.0"] }
              }
            }
            """);
        var policy = _loader.Load(path);
        var mode = policy.ResolveMode(ScanMode.Distribution);
        Assert.NotNull(mode);
        Assert.NotNull(mode!.Violations);
        Assert.Contains(LicenseType.AGPL30, mode.Violations!);
        Assert.Contains(LicenseType.GPL30, mode.Violations!);
    }

    [Fact]
    public void Loads_policy_with_allowed_whitelist()
    {
        var path = WriteConfig("""
            {
              "modes": {
                "distribution": { "allowed": ["MIT", "Apache-2.0", "BSD-3-Clause"] }
              }
            }
            """);
        var policy = _loader.Load(path);
        var mode = policy.ResolveMode(ScanMode.Distribution);
        Assert.NotNull(mode!.Allowed);
        Assert.Contains(LicenseType.MIT, mode.Allowed!);
        Assert.Contains(LicenseType.Apache20, mode.Allowed!);
        Assert.Contains(LicenseType.BSD3Clause, mode.Allowed!);
    }

    [Fact]
    public void Rejects_both_violations_and_allowed_in_same_mode()
    {
        var path = WriteConfig("""
            {
              "modes": {
                "distribution": {
                  "violations": ["AGPL-3.0"],
                  "allowed": ["MIT"]
                }
              }
            }
            """);
        var ex = Assert.Throws<InvalidOperationException>(() => _loader.Load(path));
        Assert.Contains("mutually exclusive", ex.Message);
    }

    [Fact]
    public void Rejects_unknown_license_identifier()
    {
        var path = WriteConfig("""
            {
              "modes": {
                "distribution": { "violations": ["NotARealLicense"] }
              }
            }
            """);
        Assert.Throws<InvalidOperationException>(() => _loader.Load(path));
    }

    [Fact]
    public void Accepts_LicenseType_enum_names_alongside_SPDX_ids()
    {
        var path = WriteConfig("""
            {
              "modes": {
                "distribution": { "violations": ["AGPL-3.0", "Proprietary", "Unknown", "Multiple"] }
              }
            }
            """);
        var policy = _loader.Load(path);
        var mode = policy.ResolveMode(ScanMode.Distribution);
        Assert.Contains(LicenseType.AGPL30, mode!.Violations!);
        Assert.Contains(LicenseType.Proprietary, mode.Violations!);
        Assert.Contains(LicenseType.Unknown, mode.Violations!);
        Assert.Contains(LicenseType.Multiple, mode.Violations!);
    }

    [Fact]
    public void Tolerates_comments_and_trailing_commas()
    {
        var path = WriteConfig("""
            {
              // top-level comment
              "modes": {
                "distribution": {
                  "violations": [
                    "AGPL-3.0", // inline comment
                  ],
                },
              },
            }
            """);
        var policy = _loader.Load(path);
        Assert.NotNull(policy.ResolveMode(ScanMode.Distribution));
    }
}
