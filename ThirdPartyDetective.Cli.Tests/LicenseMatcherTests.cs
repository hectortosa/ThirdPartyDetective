using ThirdPartyDetective.Cli.Services;

namespace ThirdPartyDetective.Cli.Tests;

public class LicenseMatcherTests
{
    private static readonly BundledSpdxLicenseList List = new();
    private static readonly LicenseMatcher Matcher = new(List);

    // Identical texts under different versions are not variants of each other.
    [Fact]
    public void Identical_texts_of_different_versions_are_not_reported_as_one()
    {
        Assert.Null(Matcher.Match(List.GetText("OLDAP-2.2.1")!));
        Assert.Null(Matcher.Match(List.GetText("OLDAP-2.2")!));
    }

    [Theory]
    [InlineData("MPL-2.0-no-copyleft-exception", "MPL-2.0")]
    [InlineData("OFL-1.1-RFN", "OFL-1.1")]
    [InlineData("GPL-3.0-or-later", "GPL-3.0-only")]
    public void Identical_variant_texts_report_the_base_id(string textOf, string expected)
    {
        Assert.Equal(expected, Matcher.Match(List.GetText(textOf)!));
    }

    // License files wrap at about 80 columns while the SPDX texts keep a paragraph
    // per line. These licenses have a list item opening with "(c)", which must stay
    // license text whatever the layout.
    [Theory]
    [InlineData("Apache-2.0")]
    [InlineData("MS-PL")]
    [InlineData("CDDL-1.0")]
    [InlineData("Artistic-2.0")]
    public void Texts_wrapped_like_license_files_are_identified(string licenseId)
    {
        var wrapped = Wrap(List.GetText(licenseId)!, 76);

        Assert.Equal(licenseId, Matcher.Match(wrapped));
        Assert.Equal(licenseId, Matcher.Match(wrapped.Replace("\n", "\r\n")));
    }

    [Fact]
    public void A_line_opening_with_c_and_a_year_is_a_copyright_notice()
    {
        var mit = List.GetText("MIT")!;

        Assert.Equal("MIT", Matcher.Match("(c) 2015-2024 Jane Doe and contributors, Contoso Ltd.\n\n" + mit));
    }

    private static string Wrap(string text, int width) => string.Join("\n", text.Split('\n').SelectMany(line =>
    {
        var indent = line[..(line.Length - line.TrimStart().Length)];
        var lines = new List<string>();
        var current = indent;
        foreach (var word in line.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (current.Length + word.Length + 1 > width && current.Length > indent.Length)
            {
                lines.Add(current);
                current = indent;
            }
            current += current.Length > indent.Length ? " " + word : word;
        }
        lines.Add(current);
        return lines;
    }));
}
