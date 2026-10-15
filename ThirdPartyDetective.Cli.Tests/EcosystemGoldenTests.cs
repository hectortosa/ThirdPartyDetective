using ThirdPartyDetective.Cli.Tests.Infrastructure;

namespace ThirdPartyDetective.Cli.Tests;

// npm and PyPI packages through the production invocation, with the registry
// responses (metadata, and package archives cut down to their license files)
// replayed from Fixtures/npm and Fixtures/python. The Python fixture is scanned
// twice: from requirements.txt, where Trivy finds no licenses and the registry
// resolves everything, and from the installed site-packages.
[Collection(ProcessEnvironmentCollection.Name)]
public class EcosystemGoldenTests
{
    [Theory]
    [InlineData("npm", "trivy-npm.spdx.json", "npm-registry")]
    [InlineData("python", "trivy-requirements.spdx.json", "python-requirements")]
    [InlineData("python", "trivy-site-packages.spdx.json", "python-site-packages")]
    public async Task Registry_fallback(string fixture, string sbom, string golden)
    {
        var http = FakeHttpHandler.FromRecording(TestPaths.Fixture(fixture, "registry-responses.json"));
        using var run = new CliRun(http);

        var exitCode = await run.RunAsync(
            "--sbom", TestPaths.Fixture(fixture, sbom), "--mode", "distribution", "--registry-fallback",
            "--registry-fallback-cache", run.RegistryCachePath,
            "--format", "all", "--include-license-text", "--output", run.OutputDir, "--no-fail");

        Assert.Equal(0, exitCode);
        GoldenFile.AssertMatches($"{golden}/ATTRIBUTION.txt", run.ReadOutput("ATTRIBUTION.txt"));
        GoldenFile.AssertMatches($"{golden}/packages.md", run.ReadOutput("packages.md"));
        GoldenFile.AssertMatches($"{golden}/requests.txt",
            string.Join('\n', http.RequestedUrls.Distinct().Order(StringComparer.Ordinal)) + "\n");
    }
}
