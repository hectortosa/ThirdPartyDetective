using ThirdPartyDetective.Cli.Tests.Infrastructure;

namespace ThirdPartyDetective.Cli.Tests;

// How overrides shape a whole run: which packages registry fallback looks up,
// and what the run tells the user about undetermined licenses.
[Collection(ProcessEnvironmentCollection.Name)]
public class OverridesRunTests
{
    private static string WriteSbom(CliRun run, string copyright = "NOASSERTION")
    {
        var path = Path.Combine(run.Root, "sbom.json");
        File.WriteAllText(path, $$"""
            { "spdxVersion": "SPDX-2.3", "packages": [
              { "SPDXID": "SPDXRef-Big", "name": "Big.Package", "versionInfo": "1.0.0",
                "licenseDeclared": "NOASSERTION", "copyrightText": "{{copyright}}",
                "externalRefs": [ { "referenceCategory": "PACKAGE-MANAGER", "referenceType": "purl",
                                    "referenceLocator": "pkg:nuget/Big.Package@1.0.0" } ] } ] }
            """);
        return path;
    }

    private static string WriteOverrides(CliRun run, string fileName, string entries)
    {
        var path = Path.Combine(run.Root, fileName);
        File.WriteAllText(path, $$"""{ "overrides": [ {{entries}} ] }""");
        return path;
    }

    private static Task<int> RunWithFallbackAsync(CliRun run, string sbom, string overrides) =>
        run.RunAsync("--sbom", sbom, "--overrides", overrides, "--registry-fallback",
            "--registry-fallback-cache", run.RegistryCachePath, "--format", "fulltext", "--output", run.OutputDir);

    [Fact]
    public async Task Registry_fallback_skips_a_package_whose_override_sets_license_and_copyright()
    {
        using var run = new CliRun();
        var overrides = WriteOverrides(run, "overrides.json", """
            { "name": "Big.Package", "version": "1.0.0", "license": "MIT", "copyright": "Copyright (c) 2024 Vendor" }
            """);

        Assert.Equal(0, await RunWithFallbackAsync(run, WriteSbom(run), overrides));

        Assert.DoesNotContain(run.Http.RequestedUrls, url => url.Contains("big.package", StringComparison.OrdinalIgnoreCase));
        var attribution = File.ReadAllText(Path.Combine(run.OutputDir, "ATTRIBUTION.txt"));
        Assert.Contains("License: MIT", attribution);
        Assert.Contains("Copyright: Copyright (c) 2024 Vendor", attribution);
    }

    [Fact]
    public async Task Registry_fallback_skips_a_package_whose_override_sets_the_license_the_SBOM_lacked()
    {
        using var run = new CliRun();
        var overrides = WriteOverrides(run, "overrides.json", """{ "name": "Big.Package", "version": "1.0.0", "license": "MIT" }""");

        Assert.Equal(0, await RunWithFallbackAsync(run, WriteSbom(run, copyright: "Copyright (c) 2024 Vendor"), overrides));

        Assert.DoesNotContain(run.Http.RequestedUrls, url => url.Contains("big.package", StringComparison.OrdinalIgnoreCase));
    }

    // MIT, BSD and ISC need the copyright notice; the registry may still have it.
    [Fact]
    public async Task Registry_fallback_still_looks_up_the_copyright_an_override_leaves_open()
    {
        using var run = new CliRun();
        var overrides = WriteOverrides(run, "overrides.json", """{ "name": "Big.Package", "version": "1.0.0", "license": "MIT" }""");

        Assert.Equal(0, await RunWithFallbackAsync(run, WriteSbom(run), overrides));

        Assert.Contains(run.Http.RequestedUrls, url => url.Contains("big.package", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Undetermined_licenses_point_at_the_overrides_file_passed()
    {
        using var run = new CliRun();
        var overrides = WriteOverrides(run, "licenses-reviewed.json", "");

        Assert.Equal(0, await run.RunAsync("--sbom", WriteSbom(run), "--overrides", overrides, "--format", "json", "--output", run.OutputDir));

        Assert.Contains(run.Logs.Lines, line => line.StartsWith("Warning: License not determined", StringComparison.Ordinal) &&
                                                line.EndsWith($"Add entries to {overrides}.", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Undetermined_licenses_point_at_the_default_overrides_file()
    {
        using var run = new CliRun();

        Assert.Equal(0, await run.RunAsync("--sbom", WriteSbom(run), "--format", "json", "--output", run.OutputDir));

        Assert.Contains(run.Logs.Lines, line => line.EndsWith("Add entries to .tpd-overrides.json.", StringComparison.Ordinal));
    }
}
