using Microsoft.Extensions.Logging.Abstractions;
using ThirdPartyDetective.Cli.Models;
using ThirdPartyDetective.Cli.Services;

namespace ThirdPartyDetective.Cli.Tests;

public class OverrideApplierTests
{
    private readonly IOverrideApplier _applier =
        new OverrideApplier(new SpdxLicenseExpressionAdapter(), NullLogger<OverrideApplier>.Instance);

    [Fact]
    public void Applies_license_override_on_exact_match()
    {
        var pkg = new ThirdPartyInfo
        {
            Name = "dompurify",
            Version = "3.4.0",
            LicenseExpression = "MPL-2.0 OR Apache-2.0",
            LicenseSource = LicenseSource.Spdx
        };
        var config = new OverrideConfig
        {
            Overrides = { new OverrideEntry { Name = "dompurify", Version = "3.4.0", License = "Apache-2.0", Reviewer = "alice" } }
        };

        var applied = _applier.Apply(new[] { pkg }, config);

        Assert.Equal(1, applied);
        Assert.Equal("Apache-2.0", pkg.LicenseExpression);
        Assert.Equal(LicenseSource.Override, pkg.LicenseSource);
    }

    // Registry fallback looks a package up for a missing license or copyright; an
    // override that settles both leaves it nothing to do.
    [Theory]
    [InlineData("MIT", "Copyright (c) 2024 Vendor", null, "1.0.0", true)]
    [InlineData("MIT", null, "Copyright (c) 2024 Vendor (from the SBOM)", "1.0.0", true)]
    [InlineData("MIT", null, null, "1.0.0", false)]
    [InlineData(null, "Copyright (c) 2024 Vendor", null, "1.0.0", false)]
    [InlineData("MIT", "Copyright (c) 2024 Vendor", null, "1.0.1", false)]
    public void Fully_decided_needs_a_license_and_a_copyright_for_the_exact_version(
        string? license, string? overrideCopyright, string? sbomCopyright, string version, bool expected)
    {
        var pkg = new ThirdPartyInfo { Name = "Big.Package", Version = version, Copyright = sbomCopyright ?? string.Empty };
        var config = new OverrideConfig
        {
            Overrides = { new OverrideEntry { Name = "big.package", Version = "1.0.0", License = license, Copyright = overrideCopyright } }
        };

        Assert.Equal(expected, _applier.FullyDecided(new[] { pkg }, config).Contains(pkg));
    }

    [Fact]
    public void Does_not_apply_when_version_differs()
    {
        var pkg = new ThirdPartyInfo
        {
            Name = "dompurify",
            Version = "3.5.0", // different from override
            LicenseExpression = "MPL-2.0 OR Apache-2.0",
            LicenseSource = LicenseSource.Spdx
        };
        var config = new OverrideConfig
        {
            Overrides = { new OverrideEntry { Name = "dompurify", Version = "3.4.0", License = "Apache-2.0" } }
        };

        var applied = _applier.Apply(new[] { pkg }, config);

        Assert.Equal(0, applied);
        Assert.Equal("MPL-2.0 OR Apache-2.0", pkg.LicenseExpression);
        Assert.Equal(LicenseSource.Spdx, pkg.LicenseSource);
    }

    [Fact]
    public void Name_match_is_case_insensitive()
    {
        var pkg = new ThirdPartyInfo { Name = "DomPurify", Version = "3.4.0" };
        var config = new OverrideConfig
        {
            Overrides = { new OverrideEntry { Name = "dompurify", Version = "3.4.0", License = "Apache-2.0" } }
        };

        var applied = _applier.Apply(new[] { pkg }, config);

        Assert.Equal(1, applied);
        Assert.Equal("Apache-2.0", pkg.LicenseExpression);
    }

    [Fact]
    public void Populates_authors_and_copyright_from_override()
    {
        var pkg = new ThirdPartyInfo { Name = "khroma", Version = "2.1.0" };
        var config = new OverrideConfig
        {
            Overrides = { new OverrideEntry
            {
                Name = "khroma",
                Version = "2.1.0",
                License = "MIT",
                Authors = "Fabio Spampinato",
                Copyright = "Copyright (c) Fabio Spampinato"
            }}
        };

        _applier.Apply(new[] { pkg }, config);

        Assert.Equal("Fabio Spampinato", pkg.Authors);
        Assert.Equal("Copyright (c) Fabio Spampinato", pkg.Copyright);
    }

    [Fact]
    public void Empty_config_applies_nothing()
    {
        var pkg = new ThirdPartyInfo { Name = "anything", Version = "1.0.0", LicenseExpression = "MIT" };
        var applied = _applier.Apply(new[] { pkg }, new OverrideConfig());
        Assert.Equal(0, applied);
        Assert.Equal("MIT", pkg.LicenseExpression);
    }

    [Fact]
    public void Override_license_is_normalized_like_sbom_licenses()
    {
        var pkg = new ThirdPartyInfo
        {
            Name = "classpath-lib",
            Version = "1.0.0",
            LicenseExpression = "MIT",
            WarningFlags = { "License has exception clause: WITH LLVM-exception; verify exception scope" }
        };
        var config = new OverrideConfig
        {
            Overrides = { new OverrideEntry { Name = "classpath-lib", Version = "1.0.0", License = "(GPL-2.0+ WITH Classpath-exception-2.0)" } }
        };

        _applier.Apply(new[] { pkg }, config);

        Assert.Equal("GPL-2.0+ WITH Classpath-exception-2.0", pkg.LicenseExpression);
        Assert.Equal(new[] { "GPL-2.0+" }, pkg.LicenseSubExpressions);
        Assert.Equal(new[] { "Classpath-exception-2.0" }, pkg.LicenseExceptions);
        Assert.DoesNotContain(pkg.WarningFlags, w => w.StartsWith("License has exception clause"));
    }
}
