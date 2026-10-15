using System.Diagnostics;
using ThirdPartyDetective.Cli.Services;

namespace ThirdPartyDetective.Cli.Tests;

public class SpdxExpressionTests
{
    [Theory]
    [InlineData("MIT", "MIT")]
    [InlineData("  MIT  ", "MIT")]
    [InlineData("(MIT)", "MIT")]
    [InlineData("((MIT))", "MIT")]
    [InlineData("GPL-2.0+", "GPL-2.0+")]
    [InlineData("MIT OR Apache-2.0", "MIT OR Apache-2.0")]
    [InlineData("MIT AND Apache-2.0 OR BSD-3-Clause", "(MIT AND Apache-2.0) OR BSD-3-Clause")]
    [InlineData("MIT OR Apache-2.0 AND BSD-3-Clause", "MIT OR (Apache-2.0 AND BSD-3-Clause)")]
    [InlineData("(MIT OR Apache-2.0) AND BSD-2-Clause", "(MIT OR Apache-2.0) AND BSD-2-Clause")]
    [InlineData("MIT AND (Apache-2.0 AND ISC)", "MIT AND Apache-2.0 AND ISC")]
    [InlineData("GPL-2.0-only WITH Classpath-exception-2.0", "GPL-2.0-only WITH Classpath-exception-2.0")]
    [InlineData("(GPL-2.0-only WITH Classpath-exception-2.0) OR MIT", "GPL-2.0-only WITH Classpath-exception-2.0 OR MIT")]
    [InlineData("mit or apache-2.0", "mit OR apache-2.0")]
    [InlineData("LicenseRef-Vendor-EULA", "LicenseRef-Vendor-EULA")]
    [InlineData("DocumentRef-other:LicenseRef-1 AND MIT", "DocumentRef-other:LicenseRef-1 AND MIT")]
    public void Parses_and_renders_canonically(string input, string expected)
    {
        var parsed = SpdxExpression.TryParse(input);
        Assert.NotNull(parsed);
        Assert.Equal(expected, parsed.ToString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("Apache License, Version 2.0")]
    [InlineData("MIT License")]
    [InlineData("MIT OR")]
    [InlineData("AND MIT")]
    [InlineData("(MIT")]
    [InlineData("MIT)")]
    [InlineData("()")]
    [InlineData("MIT WITH")]
    [InlineData("(MIT OR ISC) WITH Classpath-exception-2.0")]
    [InlineData("MIT WITH A WITH B")]
    [InlineData("+")]
    public void Rejects_text_that_is_not_an_spdx_expression(string input)
    {
        Assert.Null(SpdxExpression.TryParse(input));
    }

    [Fact]
    public void Terms_list_every_license_with_its_exception()
    {
        var parsed = SpdxExpression.TryParse("(GPL-2.0+ WITH Classpath-exception-2.0) OR (MIT AND LicenseRef-X)")!;

        var terms = parsed.Terms.ToList();

        Assert.Equal(new[] { "GPL-2.0+", "MIT", "LicenseRef-X" }, terms.Select(t => t.License));
        Assert.Equal("Classpath-exception-2.0", terms[0].Exception);
        Assert.True(terms[0].OrLater);
        Assert.True(terms[2].IsLicenseRef);
    }

    [Fact]
    public void Rejects_excessive_nesting_instead_of_recursing()
    {
        var deep = new string('(', 10_000) + "MIT" + new string(')', 10_000);
        Assert.Null(SpdxExpression.TryParse(deep));
    }

    // The previous adapter stripped parentheses one layer at a time, which was
    // quadratic: a 320 KB expression took ~22 s.
    [Fact]
    public void Parsing_is_linear_in_the_input()
    {
        var longExpression = string.Join(" OR ", Enumerable.Range(0, 200).Select(i => $"LicenseRef-{i}"));
        Assert.True(longExpression.Length < SpdxExpression.MaxLength);

        var stopwatch = Stopwatch.StartNew();
        for (int i = 0; i < 100; i++) SpdxExpression.TryParse(longExpression);
        stopwatch.Stop();

        Assert.True(stopwatch.ElapsedMilliseconds < 2000, $"100 parses took {stopwatch.ElapsedMilliseconds} ms");
        Assert.Null(SpdxExpression.TryParse(new string('M', SpdxExpression.MaxLength + 1)));
    }
}
