using ThirdPartyDetective.Cli.Services;

namespace ThirdPartyDetective.Cli.Tests;

public class OutputTextTests
{
    [Theory]
    [InlineData("Copyright Evil\n\n2. Totally.Legit 9.9.9", "Copyright Evil 2. Totally.Legit 9.9.9")]
    [InlineData("tab\there", "tab here")]
    [InlineData("\u001b]8;;https://evil.example\u001b\\click me\u001b]8;;\u001b\\", "]8;;https://evil.example\\click me]8;;\\")]
    [InlineData("\u001b[31mred\u001b[0m", "[31mred[0m")]
    [InlineData("abc‮dcba", "abcdcba")]
    [InlineData("  padded  ", "padded")]
    [InlineData(null, "")]
    public void Line_flattens_and_strips_control_characters(string? input, string expected)
    {
        Assert.Equal(expected, OutputText.Line(input));
    }

    [Theory]
    [InlineData("a\u2028b\u2029c\u0085d", "a b c d")]
    [InlineData("zero\u200Bwidth\uFEFF soft\u00ADhyphen", "zerowidth softhyphen")]
    public void Line_treats_unicode_line_breaks_and_drops_invisible_characters(string input, string expected)
    {
        Assert.Equal(expected, OutputText.Line(input));
    }

    // Cutting between the two halves of a surrogate pair left a lone surrogate
    // that made the UTF-8 writer throw (and the report empty).
    [Fact]
    public void Line_never_splits_a_character_when_cutting()
    {
        var line = OutputText.Line(new string('a', 999) + "\U0001F600 tail");
        Assert.Equal(new string('a', 999) + "…", line);
        Assert.Equal("x\uFFFDy", OutputText.Line("x\uD800y"));
        _ = new System.Text.UTF8Encoding(false, throwOnInvalidBytes: true).GetBytes(line);
    }

    [Fact]
    public void Line_cuts_overly_long_values()
    {
        var line = OutputText.Line(new string('x', 5000));
        Assert.Equal(1001, line.Length);
        Assert.EndsWith("…", line);
    }

    [Fact]
    public void Block_keeps_line_breaks_but_drops_escapes()
    {
        Assert.Equal("line 1\n\nline 2\tend", OutputText.Block("line 1\r\n\r\nline 2\tend\u001b"));
    }

    [Theory]
    [InlineData("https://github.com/owner/repo", "https://github.com/owner/repo")]
    [InlineData("http://example.org/x", "http://example.org/x")]
    [InlineData("javascript:alert(3)", null)]
    [InlineData("file:///etc/passwd", null)]
    [InlineData("not a url", null)]
    public void WebUrl_accepts_only_http_and_https(string input, string? expected)
    {
        Assert.Equal(expected, OutputText.WebUrl(input));
    }

    [Fact]
    public void MarkdownCell_escapes_table_and_link_syntax()
    {
        Assert.Equal(@"Pipe\|Breaks\|Table \[click\](javascript\:alert(4))", OutputText.MarkdownCell("Pipe|Breaks|Table [click](javascript:alert(4))"));
    }

    // GitHub-flavored Markdown autolinks "www.", "https://" and e-mail addresses.
    [Theory]
    [InlineData("www.evil.example/login", @"www\.evil\.example/login")]
    [InlineData("https://evil.example", @"https\://evil\.example")]
    [InlineData("a@b.example", @"a@b\.example")]
    public void MarkdownCell_prevents_autolinks(string input, string expected)
    {
        Assert.Equal(expected, OutputText.MarkdownCell(input));
    }

    [Fact]
    public void MarkdownLinkTarget_encodes_characters_that_could_end_the_link_or_cell()
    {
        Assert.Equal("https://x/a%7Cb%20%5BClick%5D%28javascript:alert%284%29%29",
            OutputText.MarkdownLinkTarget("https://x/a|b [Click](javascript:alert(4))"));
    }

    [Fact]
    public void Html_encodes_markup()
    {
        Assert.Equal("Evil&lt;script&gt;alert(1)&lt;/script&gt;", OutputText.Html("Evil<script>alert(1)</script>"));
    }
}
