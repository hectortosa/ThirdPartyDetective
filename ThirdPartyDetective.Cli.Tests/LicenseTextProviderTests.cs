using Microsoft.Extensions.Logging.Abstractions;
using ThirdPartyDetective.Cli.Services;
using ThirdPartyDetective.Cli.Tests.Infrastructure;

namespace ThirdPartyDetective.Cli.Tests;

// Mutates TPD_HOME, which CliRun-based tests also use.
[Collection(ProcessEnvironmentCollection.Name)]
public class LicenseTextProviderTests : IDisposable
{
    private readonly string _tempHome;
    private readonly string? _origTpdHome;

    public LicenseTextProviderTests()
    {
        _tempHome = Path.Combine(Path.GetTempPath(), "tpd-text-tests-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempHome);
        _origTpdHome = Environment.GetEnvironmentVariable("TPD_HOME");
        Environment.SetEnvironmentVariable("TPD_HOME", _tempHome);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("TPD_HOME", _origTpdHome);
        if (Directory.Exists(_tempHome)) Directory.Delete(_tempHome, recursive: true);
    }

    private LicenseTextProvider CreateProvider() =>
        new(new BundledSpdxLicenseList(), NullLogger<LicenseTextProvider>.Instance);

    private async Task WriteTpdHomeText(string fileId, string text)
    {
        var licensesDir = Path.Combine(_tempHome, "licenses");
        Directory.CreateDirectory(licensesDir);
        await File.WriteAllTextAsync(Path.Combine(licensesDir, $"{fileId}.txt"), text);
    }

    [Theory]
    [InlineData("MIT", "MIT")]
    [InlineData("Apache-2.0", "Apache-2.0")]
    [InlineData("Classpath-exception-2.0", "Classpath-exception-2.0")]
    [InlineData("GPL-3.0", "GPL-3.0-only")]
    [InlineData("GPL-2.0+", "GPL-2.0-or-later")]
    [InlineData("LGPL-2.1-or-later", "LGPL-2.1-or-later")]
    [InlineData("AGPL-3.0-only", "AGPL-3.0-only")]
    public void TextFileId_maps_expression_ids_to_license_list_files(string licenseId, string expected)
    {
        Assert.Equal(expected, LicenseTextProvider.TextFileId(licenseId));
    }

    [Theory]
    [InlineData("LicenseRef-Vendor-EULA")]
    [InlineData("DocumentRef-other:LicenseRef-1")]
    [InlineData("../../etc/passwd")]
    [InlineData("MIT/../../secrets")]
    [InlineData("")]
    public void TextFileId_rejects_ids_without_a_canonical_text_file(string licenseId)
    {
        Assert.Null(LicenseTextProvider.TextFileId(licenseId));
    }

    [Fact]
    public async Task GetTextAsync_serves_bundled_spdx_texts()
    {
        var text = await CreateProvider().GetTextAsync("MIT");
        Assert.Contains("Permission is hereby granted, free of charge", text);
    }

    [Fact]
    public async Task GetTextAsync_reads_deprecated_gnu_ids_from_the_only_text()
    {
        var provider = CreateProvider();
        Assert.Equal(await provider.GetTextAsync("GPL-3.0-only"), await provider.GetTextAsync("GPL-3.0"));
    }

    [Fact]
    public async Task GetTextAsync_prefers_the_bundle_over_tpd_home()
    {
        await WriteTpdHomeText("MIT", "tampered text");
        Assert.DoesNotContain("tampered", await CreateProvider().GetTextAsync("MIT"));
    }

    [Fact]
    public async Task GetTextAsync_falls_back_to_tpd_home_for_ids_outside_the_bundle()
    {
        await WriteTpdHomeText("Made-Up-License-1.0", "custom text");
        Assert.Equal("custom text", await CreateProvider().GetTextAsync("Made-Up-License-1.0"));
    }

    [Fact]
    public async Task GetTextAsync_returns_null_for_unknown_ids_and_license_refs()
    {
        var provider = CreateProvider();
        Assert.Null(await provider.GetTextAsync("Made-Up-License-1.0"));
        Assert.Null(await provider.GetTextAsync("LicenseRef-Vendor-EULA"));
    }
}
