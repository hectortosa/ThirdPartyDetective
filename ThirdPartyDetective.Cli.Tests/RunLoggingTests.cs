using System.Text.RegularExpressions;
using ThirdPartyDetective.Cli.Tests.Infrastructure;

namespace ThirdPartyDetective.Cli.Tests;

// What a run writes to its log, which in CI is the job log.
[Collection(ProcessEnvironmentCollection.Name)]
public class RunLoggingTests
{
    private static async Task<IReadOnlyList<string>> RunWithRegistryFallbackAsync(params string[] extraArgs)
    {
        using var run = new CliRun();
        var sbom = Path.Combine(run.Root, "sbom.json");
        File.WriteAllText(sbom, """
            { "spdxVersion": "SPDX-2.3", "packages": [
              { "SPDXID": "SPDXRef-A", "name": "Some.Package", "versionInfo": "1.0.0", "licenseDeclared": "NOASSERTION",
                "externalRefs": [ { "referenceCategory": "PACKAGE-MANAGER", "referenceType": "purl",
                                    "referenceLocator": "pkg:nuget/Some.Package@1.0.0" } ] } ] }
            """);

        Assert.Equal(0, await run.RunAsync(["--sbom", sbom, "--registry-fallback", "--registry-fallback-cache", run.RegistryCachePath,
            "--format", "json", "--output", run.OutputDir, .. extraArgs]));
        Assert.NotEmpty(run.Http.RequestedUrls);
        return run.Logs.Lines;
    }

    [Fact]
    public async Task Per_request_HTTP_lines_are_left_out_by_default()
    {
        var lines = await RunWithRegistryFallbackAsync();

        Assert.DoesNotContain(lines, line => line.Contains("HTTP request", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Per_request_HTTP_lines_are_kept_with_verbose()
    {
        var lines = await RunWithRegistryFallbackAsync("--verbose");

        Assert.Contains(lines, line => line.Contains("Sending HTTP request GET https://api.nuget.org/", StringComparison.Ordinal));
    }

    // Registry metadata names package authors with their email addresses; the
    // reports keep them, but a CI job log has no use for them.
    [Theory]
    [InlineData("npm", "trivy-npm.spdx.json")]
    [InlineData("python", "trivy-site-packages.spdx.json")]
    public async Task Author_email_addresses_stay_out_of_the_log(string fixture, string sbom)
    {
        var http = FakeHttpHandler.FromRecording(TestPaths.Fixture(fixture, "registry-responses.json"));
        using var run = new CliRun(http);

        Assert.Equal(0, await run.RunAsync("--sbom", TestPaths.Fixture(fixture, sbom), "--registry-fallback",
            "--registry-fallback-cache", run.RegistryCachePath, "--format", "json", "--output", run.OutputDir, "--no-fail"));

        // A domain starts with a letter; "name@1.2.3" is how the log names a package version.
        var email = new Regex(@"[A-Za-z0-9._%+-]+@[A-Za-z][A-Za-z0-9-]*(\.[A-Za-z0-9-]+)*\.[A-Za-z]{2,}\b");
        Assert.Contains(run.Logs.Lines, line => line.Contains("from " + (fixture == "npm" ? "npm" : "pypi") + " registry", StringComparison.Ordinal));
        Assert.Empty(run.Logs.Lines.Where(line => email.IsMatch(line)));
        Assert.Matches(email, File.ReadAllText(Path.Combine(run.OutputDir, "packages.json")));
    }
}
