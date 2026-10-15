using ThirdPartyDetective.Cli.Tests.Infrastructure;

namespace ThirdPartyDetective.Cli.Tests;

// Documented contract: 0 success, 1 policy violations (unless --no-fail),
// 2 operational error. Cases marked "known defect" record current behavior
// that does not meet the contract yet.
[Collection(ProcessEnvironmentCollection.Name)]
public class ExitCodeTests
{
    private static string Clean => TestPaths.Fixture("spdx", "clean.spdx.json");
    private static string WithViolations => TestPaths.Fixture("spdx", "license-edge-cases.spdx.json");

    [Fact]
    public async Task Clean_sbom_exits_0()
    {
        using var run = new CliRun();
        Assert.Equal(0, await run.RunAsync("--sbom", Clean, "--mode", "distribution", "--format", "json", "--output", run.OutputDir));
    }

    [Fact]
    public async Task Violations_exit_1()
    {
        using var run = new CliRun();
        Assert.Equal(1, await run.RunAsync("--sbom", WithViolations, "--mode", "distribution", "--format", "json", "--output", run.OutputDir));
    }

    [Fact]
    public async Task No_fail_turns_violations_into_exit_0()
    {
        using var run = new CliRun();
        Assert.Equal(0, await run.RunAsync("--sbom", WithViolations, "--mode", "distribution", "--format", "json", "--output", run.OutputDir, "--no-fail"));
    }

    [Fact]
    public async Task Missing_sbom_file_exits_2()
    {
        using var run = new CliRun();
        Assert.Equal(2, await run.RunAsync("--sbom", Path.Combine(run.Root, "missing.json"), "--output", run.OutputDir));
    }

    [Fact]
    public async Task Unknown_mode_exits_2()
    {
        using var run = new CliRun();
        Assert.Equal(2, await run.RunAsync("--sbom", Clean, "--mode", "bogus", "--output", run.OutputDir));
    }

    [Fact]
    public async Task Missing_explicit_policy_file_exits_2()
    {
        using var run = new CliRun();
        Assert.Equal(2, await run.RunAsync("--sbom", Clean, "--policy", Path.Combine(run.Root, "missing-policy.json"), "--output", run.OutputDir));
    }

    [Fact]
    public async Task Malformed_json_exits_2()
    {
        using var run = new CliRun();
        var path = Path.Combine(run.Root, "malformed.json");
        File.WriteAllText(path, "{ \"spdxVersion\": \"SPDX-2.3\", \"packages\": [ ");
        Assert.Equal(2, await run.RunAsync("--sbom", path, "--output", run.OutputDir));
    }

    [Fact]
    public async Task Unsupported_spdx_version_exits_2()
    {
        using var run = new CliRun();
        var path = Path.Combine(run.Root, "spdx-2.1.json");
        File.WriteAllText(path, "{ \"spdxVersion\": \"SPDX-2.1\", \"packages\": [] }");
        Assert.Equal(2, await run.RunAsync("--sbom", path, "--output", run.OutputDir));
    }

    [Fact]
    public async Task Fail_on_unresolved_exits_2_for_undetermined_licenses_even_with_no_fail()
    {
        using var run = new CliRun();
        Assert.Equal(2, await run.RunAsync("--sbom", WithViolations, "--mode", "distribution", "--format", "json",
            "--output", run.OutputDir, "--no-fail", "--fail-on-unresolved"));
    }

    [Fact]
    public async Task Fail_on_unresolved_exits_2_for_a_missing_license_text()
    {
        using var run = new CliRun();
        Assert.Equal(2, await run.RunAsync("--sbom", WriteSingleLicenseSbom(run, "Made-Up-License-1.0"), "--format", "fulltext",
            "--include-license-text", "--output", run.OutputDir, "--fail-on-unresolved"));
    }

    [Fact]
    public async Task Fail_on_unresolved_accepts_a_text_supplied_in_tpd_home()
    {
        using var run = new CliRun().WithLicenseTexts("Made-Up-License-1.0");
        Assert.Equal(0, await run.RunAsync("--sbom", WriteSingleLicenseSbom(run, "Made-Up-License-1.0"), "--format", "fulltext",
            "--include-license-text", "--output", run.OutputDir, "--fail-on-unresolved"));
    }

    [Fact]
    public async Task Fail_on_unresolved_exits_0_when_everything_resolves()
    {
        using var run = new CliRun();
        Assert.Equal(0, await run.RunAsync("--sbom", Clean, "--format", "fulltext", "--include-license-text",
            "--output", run.OutputDir, "--fail-on-unresolved"));
    }

    private static string WriteSingleLicenseSbom(CliRun run, string license)
    {
        var path = Path.Combine(run.Root, "single.spdx.json");
        File.WriteAllText(path, $$"""
            { "spdxVersion": "SPDX-2.3", "packages": [
              { "SPDXID": "SPDXRef-A", "name": "alpha", "versionInfo": "1.0.0", "licenseDeclared": "{{license}}" } ] }
            """);
        return path;
    }

    // Command-line errors are operational errors (2), never the violations code (1).
    [Fact]
    public async Task Missing_sbom_option_exits_2()
    {
        using var run = new CliRun();
        Assert.Equal(2, await run.RunAsync("--output", run.OutputDir));
    }

    [Theory]
    [InlineData("99")]
    [InlineData("Json,Html")]
    public async Task Numeric_or_combined_format_exits_2(string format)
    {
        using var run = new CliRun();
        Assert.Equal(2, await run.RunAsync("--sbom", Clean, "--format", format, "--output", run.OutputDir));
    }

    [Fact]
    public async Task Invalid_format_exits_2()
    {
        using var run = new CliRun();
        Assert.Equal(2, await run.RunAsync("--sbom", Clean, "--format", "nope", "--output", run.OutputDir));
    }

    [Fact]
    public async Task Unknown_flag_after_sbom_is_reported_not_read_as_a_path()
    {
        using var run = new CliRun();
        var exitCode = await run.RunAsync("--sbom", Clean, "--bogus-flag", "--output", run.OutputDir);
        Assert.Equal(2, exitCode);
        Assert.Contains("Unrecognized command or argument '--bogus-flag'", run.Error.ToString());
    }

    [Fact]
    public async Task Several_sboms_are_passed_by_repeating_the_option()
    {
        using var run = new CliRun();
        Assert.Equal(0, await run.RunAsync("--sbom", Clean, "--sbom", Clean, "--format", "json", "--output", run.OutputDir));
    }

    [Fact]
    public async Task Malformed_SOURCE_DATE_EPOCH_exits_2()
    {
        using var run = new CliRun().WithEnvironment("SOURCE_DATE_EPOCH", "yesterday");
        Assert.Equal(2, await run.RunAsync("--sbom", Clean, "--format", "json", "--output", run.OutputDir));
    }
}
