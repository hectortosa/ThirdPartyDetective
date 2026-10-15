using ThirdPartyDetective.Cli.Services.Registry;
using ThirdPartyDetective.Cli.Tests.Infrastructure;

namespace ThirdPartyDetective.Cli.Tests;

public class PackageArchiveTests
{
    private static string? DepthOne(string path)
    {
        var parts = path.Split('/');
        return parts.Length == 2 && PackageArchive.IsLicenseFileName(parts[1]) ? parts[1] : null;
    }

    [Theory]
    [InlineData("LICENSE", true)]
    [InlineData("license", true)]
    [InlineData("LICENSE.md", true)]
    [InlineData("LICENCE.txt", true)]
    [InlineData("LICENSE-MIT", true)]
    [InlineData("license-cc0", true)]
    [InlineData("LICENSE.APACHE", true)]
    [InlineData("COPYING.LESSER", true)]
    [InlineData("NOTICE", true)]
    [InlineData("UNLICENSE", true)]
    [InlineData("MIT-LICENSE.txt", true)]
    [InlineData("license.js", false)]
    [InlineData("license.json", false)]
    [InlineData("licenses.py", false)]
    [InlineData("README.md", false)]
    [InlineData("index.js", false)]
    public void Recognizes_license_file_names(string fileName, bool expected)
    {
        Assert.Equal(expected, PackageArchive.IsLicenseFileName(fileName));
    }

    [Fact]
    public void Reads_only_the_selected_tar_entries()
    {
        var archive = ArchiveBuilder.TarGz(
            ("package/LICENSE", "MIT terms"),
            ("package/index.js", "module.exports = 1;"),
            ("package/vendor/LICENSE", "vendored terms"),
            ("package/NOTICE", "Acme notice"));

        var files = PackageArchive.ReadTarGz(archive, DepthOne);

        Assert.Equal(new[] { new ArchiveFile("LICENSE", "MIT terms"), new ArchiveFile("NOTICE", "Acme notice") }, files);
        Assert.Equal(new[] { "LICENSE" }, PackageArchive.LicenseFilesOnly(files).Select(f => f.Path));
    }

    [Fact]
    public void Skips_binary_and_empty_entries()
    {
        var archive = ArchiveBuilder.Zip(("pkg.dist-info/LICENSE", "\0\u0001binary"), ("pkg.dist-info/COPYING", "  \n"));

        Assert.Empty(PackageArchive.ReadZip(archive, DepthOne));
    }

    [Fact]
    public void Caps_each_file_text()
    {
        var archive = ArchiveBuilder.Zip(("pkg.dist-info/LICENSE", new string('x', PackageArchive.MaxFileChars + 1000)));

        Assert.Equal(PackageArchive.MaxFileChars, PackageArchive.ReadZip(archive, DepthOne).Single().Text.Length);
    }

    // A tar.gz is decompressed in full to find its entries, so the expansion is capped.
    [Fact]
    public void Refuses_a_tarball_that_expands_beyond_the_cap()
    {
        var archive = ArchiveBuilder.TarGz(("package/data.bin", new string('a', 64 * 1024)), ("package/LICENSE", "MIT terms"));

        Assert.Throws<InvalidDataException>(() => PackageArchive.ReadTarGz(archive, DepthOne, maxDecompressedBytes: 16 * 1024));
    }

    [Fact]
    public void Combine_names_the_files_only_when_there_are_several()
    {
        Assert.Null(PackageArchive.Combine(Array.Empty<ArchiveFile>()));
        Assert.Equal("MIT terms", PackageArchive.Combine(new[] { new ArchiveFile("LICENSE", "MIT terms") }));
        Assert.Equal("==> LICENSE <==\n\nMIT terms\n\n==> NOTICE <==\n\nAcme notice",
            PackageArchive.Combine(new[] { new ArchiveFile("LICENSE", "MIT terms\n"), new ArchiveFile("NOTICE", "Acme notice") }));
    }
}
