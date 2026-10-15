using Microsoft.Extensions.Logging.Abstractions;
using ThirdPartyDetective.Cli.Services;
using ThirdPartyDetective.Cli.Services.Registry;
using ThirdPartyDetective.Cli.Tests.Infrastructure;

namespace ThirdPartyDetective.Cli.Tests;

public class NpmLicenseLookupTests
{
    private const string MetadataUrl = "https://registry.npmjs.org/pkg/1.0.0";
    private const string TarballUrl = "https://registry.npmjs.org/pkg/-/pkg-1.0.0.tgz";

    private static readonly ISpdxLicenseList LicenseList = new BundledSpdxLicenseList();
    private static readonly LicenseMatcher Matcher = new(LicenseList);

    // The canonical texts with their copyright placeholder filled in, as packages ship them.
    private static readonly string MitText = LicenseList.GetText("MIT")!.Replace("<year> <copyright holders>", "2020 Jane Doe");
    private static readonly string Bsd3Text = LicenseList.GetText("BSD-3-Clause")!.Replace("<year> <owner>", "2020 Jane Doe");

    private static async Task<(LicenseLookupResult? Result, FakeHttpHandler Http)> Lookup(string licenseJson, byte[]? tarball, string tarballUrl = TarballUrl)
    {
        var responses = new Dictionary<string, object>
        {
            [MetadataUrl] = $$"""{ "name": "pkg", "version": "1.0.0"{{licenseJson}}, "dist": { "tarball": "{{tarballUrl}}" } }""",
        };
        if (tarball != null) responses[tarballUrl] = tarball;
        var http = FakeHttpHandler.With(responses);
        var detector = new LicenseTextDetector(new HttpClient(http), Matcher, NullLogger<LicenseTextDetector>.Instance);
        var lookup = new NpmLicenseLookup(new HttpClient(http), detector, LicenseList, NullLogger<NpmLicenseLookup>.Instance);
        return (await lookup.LookupLicenseAsync("pkg", "1.0.0"), http);
    }

    private static byte[] Tarball(params (string Path, string Text)[] files) =>
        ArchiveBuilder.TarGz(files.Select(f => ($"package/{f.Path}", f.Text)).ToArray());

    [Fact]
    public async Task Declared_expression_is_canonicalized_and_the_license_file_kept()
    {
        var (result, _) = await Lookup(""", "license": "(mit OR cc0-1.0)" """, Tarball(("LICENSE", MitText)));

        Assert.Equal("MIT OR CC0-1.0", result!.Expression);
        Assert.Null(result.DetectedFrom);
        Assert.Contains("Copyright (c) 2020 Jane Doe", result.LicenseText);
    }

    [Fact]
    public async Task Legacy_licenses_array_lists_alternatives()
    {
        var (result, _) = await Lookup(""", "licenses": [ { "type": "MIT", "url": "https://example.invalid/mit" }, { "type": "Apache-2.0" } ] """, null);

        Assert.Equal("MIT OR Apache-2.0", result!.Expression);
    }

    [Fact]
    public async Task See_license_in_ships_the_named_file()
    {
        var (result, _) = await Lookup(""", "license": "SEE LICENSE IN EULA.md" """,
            Tarball(("EULA.md", "Acme EULA: you may redistribute this package as part of your product."), ("README.md", "readme")));

        Assert.Null(result!.Expression);
        Assert.Null(result.Note);
        Assert.Equal("Acme EULA: you may redistribute this package as part of your product.", result.LicenseText);
    }

    [Fact]
    public async Task See_license_in_a_known_license_is_identified_from_that_file()
    {
        var (result, _) = await Lookup(""", "license": "SEE LICENSE IN LICENSE.txt" """, Tarball(("LICENSE.txt", MitText)));

        Assert.Equal("MIT", result!.Expression);
        Assert.Equal("pkg-1.0.0.tgz::LICENSE.txt", result.DetectedFrom);
    }

    // UNLICENSED grants nothing: whatever file the tarball holds, no license is inferred.
    [Fact]
    public async Task Unlicensed_is_noted_and_never_detected()
    {
        var (result, _) = await Lookup(""", "license": "UNLICENSED" """, Tarball(("LICENSE", MitText)));

        Assert.Null(result!.Expression);
        Assert.Contains("UNLICENSED", result.Note);
    }

    [Fact]
    public async Task Free_text_is_noted_and_the_license_file_decides()
    {
        var (result, _) = await Lookup(""", "license": "BSD" """, Tarball(("LICENSE", Bsd3Text)));

        Assert.Equal("BSD-3-Clause", result!.Expression);
        Assert.Equal("pkg-1.0.0.tgz::LICENSE", result.DetectedFrom);
        Assert.Contains("'BSD'", result.Note);
    }

    [Fact]
    public async Task Several_license_files_are_kept_but_not_guessed_from()
    {
        var (result, _) = await Lookup("", Tarball(("license-mit", MitText), ("license-cc0", "CC0 terms")));

        Assert.Null(result!.Expression);
        Assert.Contains("==> license-cc0 <==", result.LicenseText);
        Assert.Contains("==> license-mit <==", result.LicenseText);
    }

    [Fact]
    public async Task Tarballs_outside_the_registry_are_not_fetched()
    {
        var (result, http) = await Lookup(""", "license": "MIT" """, Tarball(("LICENSE", MitText)), "https://evil.example/pkg-1.0.0.tgz");

        Assert.Equal("MIT", result!.Expression);
        Assert.Null(result.LicenseText);
        Assert.Equal(new[] { MetadataUrl }, http.RequestedUrls);
    }

    [Theory]
    [InlineData("github:owner/repo", "https://github.com/owner/repo")]
    [InlineData("owner/repo", "https://github.com/owner/repo")]
    [InlineData("gitlab:group/project", "https://gitlab.com/group/project")]
    [InlineData("git+https://github.com/owner/repo.git", "https://github.com/owner/repo")]
    [InlineData("git://github.com/owner/repo.git", "https://github.com/owner/repo")]
    [InlineData("git+ssh://git@github.com/owner/repo.git", "https://github.com/owner/repo")]
    [InlineData("https://github.com/owner/repo/", "https://github.com/owner/repo")]
    [InlineData("git@github.com:owner/repo.git", null)]
    [InlineData("UNKNOWN", null)]
    public void Repository_values_become_browsable_urls(string repository, string? expected)
    {
        Assert.Equal(expected, NpmLicenseLookup.NormalizeRepositoryUrl(repository));
    }
}
