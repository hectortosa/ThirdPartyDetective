using ThirdPartyDetective.Cli.Models;
using ThirdPartyDetective.Cli.Services;

namespace ThirdPartyDetective.Cli.Tests;

public class SpdxSbomReaderTests : IDisposable
{
    private readonly string _tempDir;
    private readonly ISpdxSbomReader _reader = new SpdxSbomReader(new SpdxLicenseExpressionAdapter());

    public SpdxSbomReaderTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "tpd-reader-tests-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true);
    }

    private string WriteSbom(string content)
    {
        var path = Path.Combine(_tempDir, "test.spdx.json");
        File.WriteAllText(path, content);
        return path;
    }

    private async Task<List<ThirdPartyInfo>> Read(string content) =>
        await _reader.ReadAsync(WriteSbom(content)).ToListAsync();

    [Fact]
    public async Task Parses_minimal_spdx_2_3_document()
    {
        var pkgs = await Read("""
            {
              "spdxVersion": "SPDX-2.3",
              "SPDXID": "SPDXRef-DOCUMENT",
              "packages": [
                { "SPDXID": "SPDXRef-A", "name": "alpha", "versionInfo": "1.0.0", "licenseConcluded": "MIT" }
              ]
            }
            """);
        Assert.Single(pkgs);
        Assert.Equal("alpha", pkgs[0].Name);
        Assert.Equal("1.0.0", pkgs[0].Version);
        Assert.Equal("MIT", pkgs[0].LicenseExpression);
    }

    [Fact]
    public async Task Parses_spdx_2_2()
    {
        var pkgs = await Read("""
            {
              "spdxVersion": "SPDX-2.2",
              "SPDXID": "SPDXRef-DOCUMENT",
              "packages": [
                { "SPDXID": "SPDXRef-A", "name": "alpha", "versionInfo": "1.0.0", "licenseDeclared": "Apache-2.0" }
              ]
            }
            """);
        Assert.Single(pkgs);
        Assert.Equal("Apache-2.0", pkgs[0].LicenseExpression);
    }

    [Fact]
    public async Task Rejects_unsupported_spdx_version()
    {
        var path = WriteSbom("""
            { "spdxVersion": "SPDX-3.0", "SPDXID": "SPDXRef-DOCUMENT", "packages": [] }
            """);
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await foreach (var _ in _reader.ReadAsync(path)) { }
        });
    }

    [Fact]
    public async Task Throws_when_spdx_version_field_missing()
    {
        var path = WriteSbom("""{ "packages": [] }""");
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await foreach (var _ in _reader.ReadAsync(path)) { }
        });
    }

    [Fact]
    public async Task Skips_SPDXRef_DOCUMENT_entries()
    {
        var pkgs = await Read("""
            {
              "spdxVersion": "SPDX-2.3",
              "packages": [
                { "SPDXID": "SPDXRef-DOCUMENT", "name": "self", "versionInfo": "0.0.0" },
                { "SPDXID": "SPDXRef-A", "name": "alpha", "versionInfo": "1.0.0", "licenseConcluded": "MIT" }
              ]
            }
            """);
        Assert.Single(pkgs);
        Assert.Equal("alpha", pkgs[0].Name);
    }

    [Theory]
    [InlineData("SOURCE")]
    [InlineData("APPLICATION")]
    [InlineData("source")]
    [InlineData("application")]
    public async Task Skips_SOURCE_and_APPLICATION_purpose_packages(string purpose)
    {
        var pkgs = await Read($$"""
            {
              "spdxVersion": "SPDX-2.3",
              "packages": [
                { "SPDXID": "SPDXRef-Wrap", "name": "scan-target", "versionInfo": "1.0.0", "primaryPackagePurpose": "{{purpose}}", "licenseConcluded": "MIT" },
                { "SPDXID": "SPDXRef-A", "name": "real-dep", "versionInfo": "2.0.0", "licenseConcluded": "MIT" }
              ]
            }
            """);
        Assert.Single(pkgs);
        Assert.Equal("real-dep", pkgs[0].Name);
    }

    [Fact]
    public async Task Normalizes_NOASSERTION_license_to_null()
    {
        var pkgs = await Read("""
            {
              "spdxVersion": "SPDX-2.3",
              "packages": [
                { "SPDXID": "SPDXRef-A", "name": "alpha", "versionInfo": "1.0.0", "licenseConcluded": "NOASSERTION", "licenseDeclared": "NOASSERTION" }
              ]
            }
            """);
        Assert.Single(pkgs);
        Assert.Null(pkgs[0].LicenseExpression);
    }

    [Fact]
    public async Task Concluded_takes_precedence_over_declared()
    {
        var pkgs = await Read("""
            {
              "spdxVersion": "SPDX-2.3",
              "packages": [
                { "SPDXID": "SPDXRef-A", "name": "alpha", "versionInfo": "1.0.0", "licenseConcluded": "MIT", "licenseDeclared": "Apache-2.0" }
              ]
            }
            """);
        Assert.Equal("MIT", pkgs[0].LicenseExpression);
    }

    [Theory]
    [InlineData("pkg:nuget/Newtonsoft.Json@13.0.3", "NuGet")]
    [InlineData("pkg:npm/lodash@4.17.21", "npm")]
    [InlineData("pkg:pypi/requests@2.31.0", "Python")]
    public async Task Infers_ecosystem_from_purl(string purl, string expected)
    {
        var pkgs = await Read($$"""
            {
              "spdxVersion": "SPDX-2.3",
              "packages": [
                {
                  "SPDXID": "SPDXRef-A",
                  "name": "alpha",
                  "versionInfo": "1.0.0",
                  "externalRefs": [{ "referenceCategory": "PACKAGE-MANAGER", "referenceType": "purl", "referenceLocator": "{{purl}}" }]
                }
              ]
            }
            """);
        Assert.Equal(expected, pkgs[0].SourceEcosystem);
    }

    [Fact]
    public async Task Combines_all_file_licenses_with_AND()
    {
        var pkgs = await Read("""
            {
              "spdxVersion": "SPDX-2.3",
              "packages": [
                { "SPDXID": "SPDXRef-A", "name": "alpha", "versionInfo": "1.0.0",
                  "licenseConcluded": "NOASSERTION", "licenseDeclared": "NOASSERTION",
                  "licenseInfoFromFiles": ["MIT", "NOASSERTION", "GPL-3.0-only", "MIT"] }
              ]
            }
            """);
        Assert.Equal("MIT AND GPL-3.0-only", pkgs[0].LicenseExpression);
    }

    [Fact]
    public async Task Keeps_APPLICATION_packages_that_have_a_purl()
    {
        var pkgs = await Read("""
            {
              "spdxVersion": "SPDX-2.3",
              "packages": [
                { "SPDXID": "SPDXRef-A", "name": "real-app-dependency", "versionInfo": "1.0.0",
                  "primaryPackagePurpose": "APPLICATION", "licenseDeclared": "GPL-3.0-only",
                  "externalRefs": [ { "referenceCategory": "PACKAGE-MANAGER", "referenceType": "purl",
                                      "referenceLocator": "pkg:npm/real-app-dependency@1.0.0" } ] }
              ]
            }
            """);
        Assert.Single(pkgs);
        Assert.Equal("GPL-3.0-only", pkgs[0].LicenseExpression);
    }

    [Fact]
    public async Task Attaches_extracted_text_for_LicenseRef_ids()
    {
        var pkgs = await Read("""
            {
              "spdxVersion": "SPDX-2.3",
              "hasExtractedLicensingInfos": [
                { "licenseId": "LicenseRef-EULA", "name": "Vendor EULA", "extractedText": "EULA text" },
                { "licenseId": "LicenseRef-Unused", "extractedText": "unused" }
              ],
              "packages": [
                { "SPDXID": "SPDXRef-A", "name": "alpha", "versionInfo": "1.0.0", "licenseDeclared": "LicenseRef-EULA OR MIT" }
              ]
            }
            """);
        var extracted = Assert.Single(pkgs[0].ExtractedLicenses);
        Assert.Equal(new ExtractedLicense("LicenseRef-EULA", "Vendor EULA", "EULA text"), extracted);
    }

    // Trivy writes this sentence when it only knows a license by name; it is not the license text.
    [Fact]
    public async Task Trivy_placeholder_sentence_is_not_taken_as_a_license_text()
    {
        var pkgs = await Read("""
            {
              "spdxVersion": "SPDX-2.3",
              "hasExtractedLicensingInfos": [
                { "licenseId": "LicenseRef-be3dcefa7e8e646a", "name": "SEE-LICENSE-IN-LICENSE",
                  "extractedText": "This component is licensed under \"SEE-LICENSE-IN-LICENSE\"" }
              ],
              "packages": [
                { "SPDXID": "SPDXRef-A", "name": "@mui/x-license", "versionInfo": "7.29.1", "licenseDeclared": "LicenseRef-be3dcefa7e8e646a" }
              ]
            }
            """);
        var extracted = Assert.Single(pkgs[0].ExtractedLicenses);
        Assert.Equal(new ExtractedLicense("LicenseRef-be3dcefa7e8e646a", "SEE-LICENSE-IN-LICENSE", null), extracted);
    }

    [Fact]
    public async Task Keeps_exceptions_in_the_expression()
    {
        var pkgs = await Read("""
            {
              "spdxVersion": "SPDX-2.3",
              "packages": [
                { "SPDXID": "SPDXRef-A", "name": "alpha", "versionInfo": "1.0.0",
                  "licenseDeclared": "GPL-2.0-only WITH Classpath-exception-2.0" }
              ]
            }
            """);
        Assert.Equal("GPL-2.0-only WITH Classpath-exception-2.0", pkgs[0].LicenseExpression);
        Assert.Equal(new[] { "Classpath-exception-2.0" }, pkgs[0].LicenseExceptions);
    }

    [Fact]
    public async Task Treats_non_string_values_as_absent()
    {
        var pkgs = await Read("""
            {
              "spdxVersion": "SPDX-2.3",
              "packages": [
                { "SPDXID": 42, "name": "alpha", "versionInfo": "1.0.0",
                  "licenseConcluded": ["MIT"], "licenseDeclared": "Apache-2.0" }
              ]
            }
            """);
        Assert.Equal("Apache-2.0", pkgs[0].LicenseExpression);
    }
}

internal static class AsyncEnumerableEx
{
    public static async Task<List<T>> ToListAsync<T>(this IAsyncEnumerable<T> source)
    {
        var list = new List<T>();
        await foreach (var item in source) list.Add(item);
        return list;
    }
}
