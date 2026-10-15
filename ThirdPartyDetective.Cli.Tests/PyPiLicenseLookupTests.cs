using Microsoft.Extensions.Logging.Abstractions;
using ThirdPartyDetective.Cli.Services;
using ThirdPartyDetective.Cli.Services.Registry;
using ThirdPartyDetective.Cli.Tests.Infrastructure;

namespace ThirdPartyDetective.Cli.Tests;

public class PyPiLicenseLookupTests
{
    private const string MetadataUrl = "https://pypi.org/pypi/pkg/1.0.0/json";
    private const string FilesBase = "https://files.pythonhosted.org/packages/ab/cd/";
    private const string PureWheel = "pkg-1.0.0-py3-none-any.whl";

    private static readonly ISpdxLicenseList LicenseList = new BundledSpdxLicenseList();
    private static readonly LicenseMatcher Matcher = new(LicenseList);
    private static readonly string MitText = LicenseList.GetText("MIT")!.Replace("<year> <copyright holders>", "2020 Jane Doe");

    private static async Task<(LicenseLookupResult? Result, FakeHttpHandler Http)> Lookup(
        string infoJson, (string FileName, string Type, long Size)[] files, Dictionary<string, byte[]>? archives = null, string name = "pkg")
    {
        var urls = string.Join(", ", files.Select(f =>
            $$"""{ "filename": "{{f.FileName}}", "packagetype": "{{f.Type}}", "size": {{f.Size}}, "url": "{{(f.FileName.StartsWith("https://") ? f.FileName : FilesBase + f.FileName)}}" }"""));
        var responses = new Dictionary<string, object> { [MetadataUrl] = $$"""{ "info": { {{infoJson}} }, "urls": [ {{urls}} ] }""" };
        foreach (var (fileName, bytes) in archives ?? new())
            responses[FilesBase + fileName] = bytes;

        var http = FakeHttpHandler.With(responses);
        var detector = new LicenseTextDetector(new HttpClient(http), Matcher, NullLogger<LicenseTextDetector>.Instance);
        var lookup = new PyPiLicenseLookup(new HttpClient(http), detector, LicenseList, NullLogger<PyPiLicenseLookup>.Instance);
        return (await lookup.LookupLicenseAsync(name, "1.0.0"), http);
    }

    private static Dictionary<string, byte[]> Wheel(params (string Path, string Text)[] files) =>
        new() { [PureWheel] = ArchiveBuilder.Zip(files) };

    private static readonly (string, string, long)[] OnlyPureWheel = { (PureWheel, "bdist_wheel", 1000) };

    [Fact]
    public async Task License_expression_wins_and_the_wheel_supplies_the_text()
    {
        var (result, _) = await Lookup(
            """ "license_expression": "mit", "classifiers": [ "License :: OSI Approved :: BSD License" ] """,
            OnlyPureWheel, Wheel(("pkg-1.0.0.dist-info/licenses/LICENSE", MitText), ("pkg-1.0.0.dist-info/METADATA", "Name: pkg")));

        Assert.Equal("MIT", result!.Expression);
        Assert.Equal(MitText, result.LicenseText);
    }

    [Fact]
    public async Task A_single_classifier_makes_a_vague_license_field_precise()
    {
        var (result, _) = await Lookup(
            """ "license": "LGPL", "classifiers": [ "License :: OSI Approved :: GNU Lesser General Public License v2 or later (LGPLv2+)" ] """,
            Array.Empty<(string, string, long)>());

        Assert.Equal("LGPL-2.0-or-later", result!.Expression);
        Assert.Null(result.Note);
    }

    [Fact]
    public async Task Classifiers_never_override_a_license_field_that_names_a_license()
    {
        var (result, _) = await Lookup(
            """ "license": "MIT", "classifiers": [ "License :: OSI Approved :: GNU General Public License v3 (GPLv3)" ] """,
            Array.Empty<(string, string, long)>());

        Assert.Equal("MIT", result!.Expression);
    }

    [Fact]
    public async Task Ambiguous_declarations_are_noted_and_left_to_review()
    {
        var (result, _) = await Lookup(
            """ "license": "Dual License", "classifiers": [ "License :: OSI Approved :: Apache Software License", "License :: OSI Approved :: BSD License" ] """,
            OnlyPureWheel, Wheel(("pkg-1.0.0.dist-info/LICENSE", "This project is dual licensed: Apache 2.0 or BSD 3-Clause.")));

        Assert.Null(result!.Expression);
        Assert.Contains("'Dual License'", result.Note);
        Assert.Equal("This project is dual licensed: Apache 2.0 or BSD 3-Clause.", result.LicenseText);
    }

    [Fact]
    public async Task Undeclared_license_is_identified_from_the_wheel_license_file()
    {
        var (result, _) = await Lookup(""" "license": "" """, OnlyPureWheel, Wheel(("pkg-1.0.0.dist-info/LICENSE.txt", MitText)));

        Assert.Equal("MIT", result!.Expression);
        Assert.Equal($"{PureWheel}::LICENSE.txt", result.DetectedFrom);
    }

    [Fact]
    public async Task A_license_text_in_the_license_field_is_a_text_not_a_name()
    {
        var license = System.Text.Json.JsonSerializer.Serialize(MitText);
        var (result, _) = await Lookup($""" "license": {license} """, Array.Empty<(string, string, long)>());

        Assert.Equal("MIT", result!.Expression);
        Assert.Equal("the PyPI license field of pkg 1.0.0", result.DetectedFrom);
        Assert.Contains("Copyright (c) 2020 Jane Doe", result.LicenseText);
    }

    [Fact]
    public async Task Wheel_license_files_keep_their_paths_under_licenses()
    {
        var (result, _) = await Lookup(""" "license_expression": "MIT AND BSD-3-Clause" """, OnlyPureWheel, Wheel(
            ("pkg-1.0.0.dist-info/licenses/LICENSE", "MIT terms"),
            ("pkg-1.0.0.dist-info/licenses/vendor/six/LICENSE", "vendored terms"),
            ("pkg/LICENSE.py", "# a module")));

        Assert.Equal("==> LICENSE <==\n\nMIT terms\n\n==> vendor/six/LICENSE <==\n\nvendored terms", result!.LicenseText);
    }

    [Fact]
    public async Task Sdist_license_files_come_from_the_top_level()
    {
        var sdist = ArchiveBuilder.TarGz(("pkg-1.0.0/LICENSE", "MIT terms"), ("pkg-1.0.0/tests/LICENSE", "fixture"));
        var (result, _) = await Lookup(""" "license": "MIT" """, new[] { ("pkg-1.0.0.tar.gz", "sdist", 500L) },
            new Dictionary<string, byte[]> { ["pkg-1.0.0.tar.gz"] = sdist });

        Assert.Equal("MIT terms", result!.LicenseText);
    }

    [Fact]
    public async Task A_pure_wheel_is_preferred_then_the_smallest_wheel()
    {
        var platformWheels = new[]
        {
            ("pkg-1.0.0.tar.gz", "sdist", 100L),
            ("pkg-1.0.0-cp312-cp312-manylinux_2_28_x86_64.whl", "bdist_wheel", 5000L),
            ("pkg-1.0.0-cp312-cp312-musllinux_1_2_x86_64.whl", "bdist_wheel", 3000L),
        };
        var (_, http) = await Lookup(""" "license": "MIT" """, platformWheels);
        Assert.Contains(FilesBase + "pkg-1.0.0-cp312-cp312-musllinux_1_2_x86_64.whl", http.RequestedUrls);

        var (_, withPure) = await Lookup(""" "license": "MIT" """, platformWheels.Append((PureWheel, "bdist_wheel", 9000L)).ToArray());
        Assert.Contains(FilesBase + PureWheel, withPure.RequestedUrls);
    }

    [Fact]
    public async Task Files_outside_pypi_are_never_fetched()
    {
        var (_, http) = await Lookup(""" "license": "MIT" """, new[] { ("https://evil.example/pkg-1.0.0-py3-none-any.whl", "bdist_wheel", 10L) });

        Assert.Equal(new[] { MetadataUrl }, http.RequestedUrls);
    }

    [Theory]
    [InlineData("typing_extensions", "typing-extensions")]
    [InlineData("Typing.Extensions", "typing-extensions")]
    [InlineData("zope.interface", "zope-interface")]
    [InlineData("A__b--C", "a-b-c")]
    public void Names_are_normalized_per_pep_503(string name, string expected)
    {
        Assert.Equal(expected, PyPiLicenseLookup.NormalizeName(name));
    }
}
