using Microsoft.Extensions.Logging.Abstractions;
using ThirdPartyDetective.Cli.Models;
using ThirdPartyDetective.Cli.Services;

namespace ThirdPartyDetective.Cli.Tests;

public class LicenseAnalyzerTests
{
    private readonly ILicenseAnalyzer _analyzer =
        new LicenseAnalyzer(NullLogger<LicenseAnalyzer>.Instance);

    [Theory]
    [InlineData("MIT", LicenseType.MIT)]
    [InlineData("MIT License", LicenseType.MIT)]
    [InlineData("Apache-2.0", LicenseType.Apache20)]
    [InlineData("Apache License 2.0", LicenseType.Apache20)]
    [InlineData("BSD-3-Clause", LicenseType.BSD3Clause)]
    [InlineData("BSD", LicenseType.BSD3Clause)]
    [InlineData("ISC", LicenseType.ISC)]
    [InlineData("MPL-2.0", LicenseType.MPL20)]
    [InlineData("PSF", LicenseType.MIT)]
    [InlineData("PostgreSQL", LicenseType.MIT)]
    public void ParseLicenseType_recognizes_common_aliases(string raw, LicenseType expected)
    {
        Assert.Equal(expected, _analyzer.ParseLicenseType(raw, null));
    }

    [Theory]
    [InlineData("LGPL-2.1", LicenseType.LGPL21)]
    [InlineData("AGPL-3.0", LicenseType.AGPL30)]
    [InlineData("GPL-2.0", LicenseType.GPL20)]
    [InlineData("GPL-3.0", LicenseType.GPL30)]
    public void ParseLicenseType_handles_GPL_family_with_word_boundaries(string raw, LicenseType expected)
    {
        // Regression for the regex collision: \bGPL must not match inside LGPL/AGPL.
        Assert.Equal(expected, _analyzer.ParseLicenseType(raw, null));
    }

    [Theory]
    [InlineData("MIT OR GPL-3.0")]
    [InlineData("Apache-2.0 AND MIT")]
    public void ParseLicenseType_returns_Multiple_for_compound(string raw)
    {
        Assert.Equal(LicenseType.Multiple, _analyzer.ParseLicenseType(raw, null));
    }

    [Fact]
    public void ParseLicenseType_returns_Unknown_when_nothing_matches()
    {
        Assert.Equal(LicenseType.Unknown, _analyzer.ParseLicenseType("ZZZ-Made-Up-License", null));
    }

    [Fact]
    public void IsPolicyViolation_uses_built_in_defaults_when_policy_is_null()
    {
        Assert.True(_analyzer.IsPolicyViolation(LicenseType.AGPL30, ScanMode.Distribution));
        Assert.True(_analyzer.IsPolicyViolation(LicenseType.GPL30, ScanMode.Saas));
        Assert.False(_analyzer.IsPolicyViolation(LicenseType.MIT, ScanMode.Distribution));
        Assert.False(_analyzer.IsPolicyViolation(LicenseType.GPL30, ScanMode.Distribution));
    }

    [Fact]
    public void IsPolicyViolation_All_mode_never_violates()
    {
        Assert.False(_analyzer.IsPolicyViolation(LicenseType.AGPL30, ScanMode.All));
        Assert.False(_analyzer.IsPolicyViolation(LicenseType.Proprietary, ScanMode.All));
    }

    [Fact]
    public void IsPolicyViolation_uses_policy_violations_blocklist()
    {
        var policy = new PolicyConfig
        {
            Modes = new()
            {
                ["distribution"] = new ModePolicy
                {
                    Violations = new() { LicenseType.GPL30, LicenseType.AGPL30 }
                }
            }
        };
        Assert.True(_analyzer.IsPolicyViolation(LicenseType.GPL30, ScanMode.Distribution, policy));
        Assert.False(_analyzer.IsPolicyViolation(LicenseType.MIT, ScanMode.Distribution, policy));
        Assert.False(_analyzer.IsPolicyViolation(LicenseType.Proprietary, ScanMode.Distribution, policy));
    }

    [Fact]
    public void IsPolicyViolation_uses_policy_allowed_whitelist()
    {
        var policy = new PolicyConfig
        {
            Modes = new()
            {
                ["distribution"] = new ModePolicy
                {
                    Allowed = new() { LicenseType.MIT, LicenseType.Apache20 }
                }
            }
        };
        Assert.False(_analyzer.IsPolicyViolation(LicenseType.MIT, ScanMode.Distribution, policy));
        Assert.True(_analyzer.IsPolicyViolation(LicenseType.GPL30, ScanMode.Distribution, policy));
        Assert.True(_analyzer.IsPolicyViolation(LicenseType.BSD3Clause, ScanMode.Distribution, policy));
    }
}
