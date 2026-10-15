using ThirdPartyDetective.Cli.Services;
using ThirdPartyDetective.Cli.Services.Registry;

namespace ThirdPartyDetective.Cli.Tests;

public class DeclaredLicenseTests
{
    private static readonly ISpdxLicenseList LicenseList = new BundledSpdxLicenseList();

    [Theory]
    [InlineData("MIT", "MIT")]
    [InlineData("mit", "MIT")]
    [InlineData("apache-2.0 or mit", "Apache-2.0 OR MIT")]
    [InlineData("(MIT AND Zlib)", "MIT AND Zlib")]
    [InlineData("GPL-2.0-only WITH classpath-exception-2.0", "GPL-2.0-only WITH Classpath-exception-2.0")]
    [InlineData("LicenseRef-Acme-EULA", "LicenseRef-Acme-EULA")]
    [InlineData("Apache License, Version 2.0", "Apache-2.0")]
    [InlineData("New BSD License", "BSD-3-Clause")]
    public void Normalizes_declared_licenses_to_canonical_spdx(string declared, string expected)
    {
        Assert.Equal(expected, DeclaredLicense.Normalize(declared, LicenseList));
    }

    [Theory]
    [InlineData("BSD")]
    [InlineData("LGPL")]
    [InlineData("Apache Software License")]
    [InlineData("Commercial")]
    [InlineData("Dual License")]
    [InlineData("MIT/X11")]
    [InlineData("SEE LICENSE IN LICENSE")]
    [InlineData("UNLICENSED")]
    [InlineData("MIT WITH Not-An-Exception")]
    public void Leaves_ambiguous_or_unknown_declarations_unresolved(string declared)
    {
        Assert.Null(DeclaredLicense.Normalize(declared, LicenseList));
    }

    [Fact]
    public void Every_mapped_license_is_on_the_spdx_list()
    {
        Assert.All(DeclaredLicense.MappedIds, id => Assert.True(LicenseList.TryGetCanonicalId(id, out var canonical) && canonical == id, id));
    }

    [Fact]
    public void A_single_unambiguous_classifier_names_the_license()
    {
        Assert.Equal("MIT", DeclaredLicense.FromClassifiers(new[] { "Programming Language :: Python", "License :: OSI Approved :: MIT License" }));
        Assert.Equal("LGPL-2.0-or-later", DeclaredLicense.FromClassifiers(new[]
        {
            "License :: OSI Approved",
            "License :: OSI Approved :: GNU Lesser General Public License v2 or later (LGPLv2+)",
        }));
    }

    [Fact]
    public void Ambiguous_or_several_classifiers_name_nothing()
    {
        Assert.Null(DeclaredLicense.FromClassifiers(new[] { "License :: OSI Approved :: BSD License" }));
        Assert.Null(DeclaredLicense.FromClassifiers(new[] { "License :: OSI Approved :: Apache Software License" }));
        Assert.Null(DeclaredLicense.FromClassifiers(new[]
        {
            "License :: OSI Approved :: MIT License",
            "License :: OSI Approved :: Mozilla Public License 2.0 (MPL 2.0)",
        }));
    }

    [Theory]
    [InlineData("SEE LICENSE IN LICENSE", "LICENSE")]
    [InlineData("see license in ./EULA.md", "EULA.md")]
    [InlineData("SEE LICENSE IN docs/terms.txt", "docs/terms.txt")]
    [InlineData("SEE LICENSE IN ", null)]
    [InlineData("MIT", null)]
    public void Reads_the_file_named_by_see_license_in(string declared, string? expected)
    {
        Assert.Equal(expected, DeclaredLicense.SeeLicenseInFile(declared));
    }
}
