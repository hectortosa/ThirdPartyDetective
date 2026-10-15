using System.Text;
using ThirdPartyDetective.Cli.Tests.Infrastructure;

namespace ThirdPartyDetective.Cli.Tests;

// ATTRIBUTION.txt behaviors that golden files only show indirectly.
[Collection(ProcessEnvironmentCollection.Name)]
public class AttributionBehaviorTests
{
    private static string WriteSbom(CliRun run, string fileName, string packagesJson, string extractedJson = "[]")
    {
        var path = Path.Combine(run.Root, fileName);
        File.WriteAllText(path, $$"""
            { "spdxVersion": "SPDX-2.3", "hasExtractedLicensingInfos": {{extractedJson}}, "packages": {{packagesJson}} }
            """);
        return path;
    }

    private static string Package(string name, string license, string extra = "") =>
        $$"""{ "SPDXID": "SPDXRef-{{name}}", "name": "{{name}}", "versionInfo": "1.0.0", "licenseDeclared": "{{license}}"{{extra}} }""";

    private static string Attribution(CliRun run) => File.ReadAllText(Path.Combine(run.OutputDir, "ATTRIBUTION.txt"));

    [Fact]
    public async Task Override_with_a_license_text_file_ships_that_text_for_a_LicenseRef()
    {
        using var run = new CliRun();
        var sbom = WriteSbom(run, "sbom.json", $"[{Package("Acme.Sdk", "NOASSERTION")}]");
        File.WriteAllText(Path.Combine(run.Root, "acme-eula.txt"), "ACME EULA: redistribution as part of your product is permitted.");
        var overrides = Path.Combine(run.Root, "overrides.json");
        File.WriteAllText(overrides, """
            { "overrides": [ { "name": "Acme.Sdk", "version": "1.0.0", "license": "LicenseRef-Acme-EULA",
                               "licenseTextFile": "acme-eula.txt", "reviewer": "legal" } ] }
            """);

        var exitCode = await run.RunAsync("--sbom", sbom, "--overrides", overrides, "--format", "fulltext",
            "--include-license-text", "--fail-on-unresolved", "--output", run.OutputDir);

        Assert.Equal(0, exitCode);
        var text = Attribution(run);
        Assert.Contains("License: LicenseRef-Acme-EULA (see Appendix A)", text);
        Assert.Contains("--- LicenseRef-Acme-EULA ---\n\n   ACME EULA: redistribution as part of your product is permitted.", text.Replace("\r\n", "\n"));
    }

    [Fact]
    public async Task Override_license_text_file_must_stay_next_to_the_overrides_file()
    {
        using var run = new CliRun();
        var sbom = WriteSbom(run, "sbom.json", $"[{Package("Acme.Sdk", "NOASSERTION")}]");
        Directory.CreateDirectory(Path.Combine(run.Root, "config"));
        var overrides = Path.Combine(run.Root, "config", "overrides.json");
        File.WriteAllText(overrides, """
            { "overrides": [ { "name": "Acme.Sdk", "version": "1.0.0", "license": "MIT", "licenseTextFile": "../sbom.json" } ] }
            """);

        Assert.Equal(2, await run.RunAsync("--sbom", sbom, "--overrides", overrides, "--format", "fulltext", "--output", run.OutputDir));
    }

    // The package's own text carries its real copyright line; the canonical text
    // in the appendix only has the template's placeholder.
    [Fact]
    public async Task Package_license_text_is_printed_even_with_an_appendix()
    {
        using var run = new CliRun();
        var sbom = WriteSbom(run, "sbom.json", $"[{Package("Some.Lib", "NOASSERTION")}]");
        File.WriteAllText(Path.Combine(run.Root, "LICENSE"), "MIT License\n\nCopyright (c) 2015-2024 Jane Doe\n\nPermission is hereby granted...");
        var overrides = Path.Combine(run.Root, "overrides.json");
        File.WriteAllText(overrides, """
            { "overrides": [ { "name": "Some.Lib", "version": "1.0.0", "license": "MIT", "licenseTextFile": "LICENSE" } ] }
            """);

        Assert.Equal(0, await run.RunAsync("--sbom", sbom, "--overrides", overrides, "--format", "fulltext",
            "--include-license-text", "--output", run.OutputDir));

        var text = Attribution(run);
        Assert.Contains("License Text (from the package):", text);
        Assert.Contains("   Copyright (c) 2015-2024 Jane Doe", text);
        Assert.Contains("--- MIT ---", text);
    }

    // LicenseRef IDs are document-scoped: two SBOMs may define LicenseRef-Custom differently.
    [Fact]
    public async Task Colliding_LicenseRef_ids_get_numbered_sections_and_references()
    {
        using var run = new CliRun();
        var first = WriteSbom(run, "a.json", $"[{Package("Alpha", "LicenseRef-Custom")}]",
            """[ { "licenseId": "LicenseRef-Custom", "name": "Custom", "extractedText": "Alpha terms" } ]""");
        var second = WriteSbom(run, "b.json", $"[{Package("Beta", "LicenseRef-Custom")}]",
            """[ { "licenseId": "LicenseRef-Custom", "name": "Custom", "extractedText": "Beta terms" } ]""");

        Assert.Equal(0, await run.RunAsync("--sbom", first, "--sbom", second, "--format", "fulltext",
            "--include-license-text", "--output", run.OutputDir));

        var text = Attribution(run).Replace("\r\n", "\n");
        Assert.Contains("License: LicenseRef-Custom (see Appendix A: LicenseRef-Custom (Custom) #1)", text);
        Assert.Contains("License: LicenseRef-Custom (see Appendix A: LicenseRef-Custom (Custom) #2)", text);
        Assert.Contains("--- LicenseRef-Custom (Custom) #1 ---\n\n   Alpha terms", text);
        Assert.Contains("--- LicenseRef-Custom (Custom) #2 ---\n\n   Beta terms", text);
    }

    // A lone surrogate left by cutting a long value made the UTF-8 writer throw,
    // and the run exited 2 with an empty ATTRIBUTION.txt.
    [Fact]
    public async Task Long_fields_ending_in_an_emoji_still_produce_a_valid_report()
    {
        using var run = new CliRun();
        var copyright = new string('a', 999) + "\\uD83D\\uDE00";
        var sbom = WriteSbom(run, "sbom.json", $"[{Package("Emoji.Lib", "MIT", $", \"copyrightText\": \"{copyright}\"")}]");

        Assert.Equal(0, await run.RunAsync("--sbom", sbom, "--format", "all", "--include-license-text", "--output", run.OutputDir));

        var bytes = File.ReadAllBytes(Path.Combine(run.OutputDir, "ATTRIBUTION.txt"));
        Assert.Contains("Emoji.Lib", new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes));
        Assert.Empty(Directory.GetFiles(run.OutputDir, "*.tmp"));
    }

    // The default copyleft note states facts only: no source-code promise
    // (that commitment belongs to whoever ships the notices) and no internal advice.
    [Fact]
    public async Task Default_copyleft_notes_make_no_promises()
    {
        using var run = new CliRun();
        var sbom = WriteSbom(run, "sbom.json",
            $"[{Package("Lgpl.Lib", "LGPL-2.1-only")}, {Package("Gpl.Lib", "GPL-3.0-or-later")}, {Package("Agpl.Lib", "AGPL-3.0-only")}]");

        Assert.Equal(0, await run.RunAsync("--sbom", sbom, "--format", "fulltext", "--include-license-text", "--output", run.OutputDir));

        var text = Attribution(run);
        Assert.Contains("   Note: LGPL-2.1-only is a copyleft license. Its conditions for providing source code are in Appendix A.", text);
        Assert.Contains("   Note: GPL-3.0-or-later is a copyleft license.", text);
        Assert.Contains("   Note: AGPL-3.0-only is a copyleft license.", text);
        Assert.DoesNotContain("written request", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("consult legal", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("not recorded", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Notice_template_supplies_the_legal_wording()
    {
        using var run = new CliRun();
        var sbom = WriteSbom(run, "sbom.json",
            $"[{Package("Lgpl.Lib", "LGPL-2.1-only", ", \"homepage\": \"https://example.invalid/lgpl\"")}, {Package("Gpl.Lib", "GPL-2.0-only")}]");
        var template = Path.Combine(run.Root, "notices.json");
        File.WriteAllText(template, """
            {
              "header": "SOURCE CODE\nContoso provides the source code of the components below at https://example.invalid/source.",
              "footer": "Questions: legal@example.invalid",
              "copyleftNotes": {
                "LGPL": "Source of {name} {version} ({license}): {projectUrl}\nRelinking is permitted.",
                "GPL": ""
              }
            }
            """);

        Assert.Equal(0, await run.RunAsync("--sbom", sbom, "--notice-template", template, "--format", "fulltext", "--output", run.OutputDir));

        var text = Attribution(run).Replace("\r\n", "\n");
        Assert.Contains("SOURCE CODE\nContoso provides the source code of the components below at https://example.invalid/source.", text);
        Assert.Contains("   Source of Lgpl.Lib 1.0.0 (LGPL-2.1-only): https://example.invalid/lgpl\n   Relinking is permitted.", text);
        Assert.DoesNotContain("GPL-2.0-only is a copyleft license", text);
        Assert.Contains("Questions: legal@example.invalid", text);
    }

    // Without the package's own text, the appendix says the text is missing rather
    // than printing Trivy's placeholder sentence as if it were the license.
    [Fact]
    public async Task Trivy_placeholder_text_never_reaches_the_attribution()
    {
        using var run = new CliRun();
        var sbom = WriteSbom(run, "sbom.json", $"[{Package("Commercial.Lib", "LicenseRef-abc")}]",
            """[ { "licenseId": "LicenseRef-abc", "name": "SEE-LICENSE-IN-LICENSE", "extractedText": "This component is licensed under \"SEE-LICENSE-IN-LICENSE\"" } ]""");

        Assert.Equal(0, await run.RunAsync("--sbom", sbom, "--format", "fulltext", "--include-license-text", "--output", run.OutputDir));

        var text = Attribution(run);
        Assert.Contains("--- LicenseRef-abc (SEE-LICENSE-IN-LICENSE) ---", text);
        Assert.Contains("[License text for LicenseRef-abc (SEE-LICENSE-IN-LICENSE) is not available.]", text);
        Assert.DoesNotContain("This component is licensed under", text);
    }

    [Fact]
    public async Task Missing_explicit_notice_template_exits_2()
    {
        using var run = new CliRun();
        var sbom = WriteSbom(run, "sbom.json", $"[{Package("Plain", "MIT")}]");
        Assert.Equal(2, await run.RunAsync("--sbom", sbom, "--notice-template", Path.Combine(run.Root, "missing.json"),
            "--format", "fulltext", "--output", run.OutputDir));
    }
}
