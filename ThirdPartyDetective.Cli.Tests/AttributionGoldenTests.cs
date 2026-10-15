using ThirdPartyDetective.Cli.Tests.Infrastructure;

namespace ThirdPartyDetective.Cli.Tests;

// End-to-end characterization of the generated reports. The golden files under
// Golden/ record today's output, including known defects from the 2026-10
// release-readiness review; fixes are expected to change them, one reviewed
// diff at a time.
[Collection(ProcessEnvironmentCollection.Name)]
public class AttributionGoldenTests
{
    private static string TrivySbom => TestPaths.Fixture("trivy", "trivy-dotnet-publish.spdx.json");
    private static string EdgeCasesSbom => TestPaths.Fixture("spdx", "license-edge-cases.spdx.json");
    private static string InjectionSbom => TestPaths.Fixture("spdx", "injection.spdx.json");

    [Fact]
    public async Task Trivy_publish_tree_offline()
    {
        using var run = new CliRun();

        var exitCode = await run.RunAsync(
            "--sbom", TrivySbom, "--mode", "distribution", "--exclude-namespaces", "Contoso.*",
            "--format", "all", "--include-license-text", "--output", run.OutputDir, "--no-fail");

        Assert.Equal(0, exitCode);
        GoldenFile.AssertMatches("trivy-offline/ATTRIBUTION.txt", run.ReadOutput("ATTRIBUTION.txt"));
        GoldenFile.AssertMatches("trivy-offline/packages.json", run.ReadOutput("packages.json"));
    }

    // The CI invocation from the README, with registry responses replayed from
    // Fixtures/trivy/registry-responses.json.
    [Fact]
    public async Task Trivy_publish_tree_with_registry_fallback()
    {
        var http = FakeHttpHandler.FromRecording(TestPaths.Fixture("trivy", "registry-responses.json"));
        using var run = new CliRun(http);

        var exitCode = await run.RunAsync(
            "--sbom", TrivySbom, "--mode", "distribution", "--registry-fallback",
            "--registry-fallback-cache", run.RegistryCachePath, "--exclude-namespaces", "Contoso.*",
            "--format", "fulltext", "--include-license-text", "--output", run.OutputDir, "--no-fail");

        Assert.Equal(0, exitCode);
        GoldenFile.AssertMatches("trivy-registry/ATTRIBUTION.txt", run.ReadOutput("ATTRIBUTION.txt"));
        GoldenFile.AssertMatches("trivy-registry/requests.txt",
            string.Join('\n', http.RequestedUrls.Distinct().Order(StringComparer.Ordinal)) + "\n");
    }

    [Fact]
    public async Task License_edge_cases_in_distribution_mode()
    {
        using var run = new CliRun();

        var exitCode = await run.RunAsync(
            "--sbom", EdgeCasesSbom, "--mode", "distribution", "--exclude-namespaces", "Internal.*",
            "--format", "all", "--include-license-text", "--output", run.OutputDir, "--no-fail");

        Assert.Equal(0, exitCode);
        GoldenFile.AssertMatches("edge-cases-distribution/ATTRIBUTION.txt", run.ReadOutput("ATTRIBUTION.txt"));
        GoldenFile.AssertMatches("edge-cases-distribution/packages.json", run.ReadOutput("packages.json"));
        GoldenFile.AssertMatches("edge-cases-distribution/packages.md", run.ReadOutput("packages.md"));
    }

    [Fact]
    public async Task License_edge_cases_in_default_mode()
    {
        using var run = new CliRun();

        var exitCode = await run.RunAsync(
            "--sbom", EdgeCasesSbom, "--format", "fulltext", "--include-license-text", "--output", run.OutputDir);

        Assert.Equal(0, exitCode);
        GoldenFile.AssertMatches("edge-cases-default-mode/ATTRIBUTION.txt", run.ReadOutput("ATTRIBUTION.txt"));
    }

    [Fact]
    public async Task Injection_payloads_in_package_metadata()
    {
        using var run = new CliRun();

        var exitCode = await run.RunAsync(
            "--sbom", InjectionSbom, "--mode", "distribution", "--format", "all", "--output", run.OutputDir, "--no-fail");

        Assert.Equal(0, exitCode);
        GoldenFile.AssertMatches("injection/packages.html", run.ReadOutput("packages.html"));
        GoldenFile.AssertMatches("injection/packages.md", run.ReadOutput("packages.md"));
        GoldenFile.AssertMatches("injection/ATTRIBUTION.txt", run.ReadOutput("ATTRIBUTION.txt"));
    }
}
