using System.Globalization;
using ThirdPartyDetective.Cli.Tests.Infrastructure;

namespace ThirdPartyDetective.Cli.Tests;

// What CI pipelines rely on: the same SBOM gives byte-identical reports
// regardless of when, where (culture) or whether online the tool runs.
[Collection(ProcessEnvironmentCollection.Name)]
public class ReproducibilityTests
{
    private static string TrivySbom => TestPaths.Fixture("trivy", "trivy-dotnet-publish.spdx.json");
    private static string EdgeCasesSbom => TestPaths.Fixture("spdx", "license-edge-cases.spdx.json");
    private static string Recording => TestPaths.Fixture("trivy", "registry-responses.json");
    private static readonly string[] ReportFiles = { "ATTRIBUTION.txt", "packages.json", "packages.md", "packages.html" };

    private static async Task<Dictionary<string, byte[]>> RunAndCollect(CliRun run, params string[] args)
    {
        Assert.Equal(0, await run.RunAsync(args.Concat(new[] { "--output", run.OutputDir, "--no-fail" }).ToArray()));
        return ReportFiles.ToDictionary(f => f, f => File.ReadAllBytes(Path.Combine(run.OutputDir, f)));
    }

    private static void AssertSameReports(Dictionary<string, byte[]> expected, Dictionary<string, byte[]> actual)
    {
        foreach (var file in ReportFiles)
            Assert.True(expected[file].SequenceEqual(actual[file]), $"{file} differs between runs");
    }

    [Fact]
    public async Task Deterministic_runs_are_byte_identical()
    {
        string[] args = { "--sbom", EdgeCasesSbom, "--mode", "distribution", "--format", "all", "--include-license-text", "--deterministic" };

        Dictionary<string, byte[]> first, second;
        using (var run = new CliRun()) first = await RunAndCollect(run, args);
        await Task.Delay(1100); // a wall-clock timestamp would now differ
        using (var run = new CliRun()) second = await RunAndCollect(run, args);

        AssertSameReports(first, second);
        Assert.DoesNotContain("Generated on", System.Text.Encoding.UTF8.GetString(first["ATTRIBUTION.txt"]));
    }

    // Danish collation sorts "Aa" after "Z" and the Finnish time separator is ".";
    // neither may leak into the reports.
    [Theory]
    [InlineData("da-DK")]
    [InlineData("fi-FI")]
    public async Task Reports_do_not_depend_on_the_culture(string cultureName)
    {
        string[] args = { "--sbom", EdgeCasesSbom, "--mode", "distribution", "--format", "all", "--include-license-text" };

        Dictionary<string, byte[]> invariant, localized;
        using (var run = new CliRun().WithEnvironment("SOURCE_DATE_EPOCH", "1767225600")) invariant = await RunAndCollect(run, args);
        using (var run = new CliRun(culture: new CultureInfo(cultureName)).WithEnvironment("SOURCE_DATE_EPOCH", "1767225600"))
            localized = await RunAndCollect(run, args);

        AssertSameReports(invariant, localized);
        Assert.Contains("Generated on 2026-01-01 00:00:00 UTC", System.Text.Encoding.UTF8.GetString(localized["ATTRIBUTION.txt"]));
    }

    [Fact]
    public async Task Offline_run_reproduces_the_online_result_from_the_cache()
    {
        var cache = Path.Combine(Path.GetTempPath(), "tpd-repro-cache-" + Guid.NewGuid().ToString("N")[..8] + ".json");
        string[] args = { "--sbom", TrivySbom, "--mode", "distribution", "--exclude-namespaces", "Contoso.*", "--registry-fallback",
            "--registry-fallback-cache", cache, "--format", "all", "--include-license-text", "--deterministic" };
        try
        {
            Dictionary<string, byte[]> online, offline;
            using (var run = new CliRun(FakeHttpHandler.FromRecording(Recording))) online = await RunAndCollect(run, args);

            var noNetwork = FakeHttpHandler.Empty();
            using (var run = new CliRun(noNetwork)) offline = await RunAndCollect(run, args.Append("--offline").ToArray());

            Assert.Empty(noNetwork.RequestedUrls);
            AssertSameReports(online, offline);
        }
        finally
        {
            File.Delete(cache);
        }
    }

    [Fact]
    public async Task Offline_without_a_cache_makes_no_requests()
    {
        var noNetwork = FakeHttpHandler.Empty();
        using var run = new CliRun(noNetwork).WithEnvironment("TPD_OFFLINE", "1");

        Assert.Equal(0, await run.RunAsync("--sbom", TrivySbom, "--registry-fallback", "--registry-fallback-cache", run.RegistryCachePath,
            "--format", "json", "--output", run.OutputDir));
        Assert.Empty(noNetwork.RequestedUrls);
    }
}
