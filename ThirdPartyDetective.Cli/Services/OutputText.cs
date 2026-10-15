using System.Globalization;
using System.Text;

namespace ThirdPartyDetective.Cli.Services;

// Package metadata comes from third parties (SBOM fields, registry responses),
// and ATTRIBUTION.txt ships to customers. Every value is cleaned before it is
// written to a report or a log line, so metadata can't forge entries with line
// breaks (including U+2028/U+2029), smuggle terminal escape sequences, hide or
// reorder text with invisible formatting characters, or break out of a
// Markdown table or an HTML attribute. Text is processed per Unicode scalar
// value, so cutting a long value never splits a character (a lone surrogate
// would make the UTF-8 writer throw and leave an empty report behind).
internal static class OutputText
{
    private const int MaxLineLength = 1000;

    // Single-line values (names, versions, copyright, URLs, expressions): line
    // breaks and tabs become spaces; control and invisible formatting
    // characters are dropped; overly long values are cut.
    public static string Line(string? value) => Line(value, MaxLineLength);

    public static string Line(string? value, int maxLength)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;

        var sb = new StringBuilder(Math.Min(value.Length, maxLength + 2));
        var truncated = false;
        foreach (var rune in value.EnumerateRunes())
        {
            if (IsLineBreak(rune) || rune.Value == '\t')
            {
                if (sb.Length > 0 && sb[^1] != ' ') sb.Append(' ');
                continue;
            }
            if (IsUnsafe(rune)) continue;
            if (sb.Length + rune.Utf16SequenceLength > maxLength)
            {
                truncated = true;
                break;
            }
            sb.Append(rune.ToString());
        }

        var line = sb.ToString().Trim();
        return truncated ? line + "…" : line;
    }

    // Multi-line text (license texts): line breaks and tabs are kept (all
    // Unicode line terminators become \n); control and invisible formatting
    // characters are dropped.
    public static string Block(string? value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;

        var sb = new StringBuilder(value.Length);
        var previousWasCr = false;
        foreach (var rune in value.EnumerateRunes())
        {
            if (rune.Value == '\n' && previousWasCr)
            {
                previousWasCr = false;
                continue; // second half of \r\n
            }
            previousWasCr = rune.Value == '\r';

            if (IsLineBreak(rune)) sb.Append('\n');
            else if (rune.Value == '\t' || !IsUnsafe(rune)) sb.Append(rune.ToString());
        }
        return sb.ToString();
    }

    // The URL if it is an absolute http(s) URL, otherwise null: "javascript:",
    // "file:" or free text never become links.
    public static string? WebUrl(string? value)
    {
        var line = Line(value);
        return Uri.TryCreate(line, UriKind.Absolute, out var uri) &&
               (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp)
            ? line
            : null;
    }

    // A Markdown table cell: Line() plus escaping of the characters that end a
    // cell or start formatting, links or HTML. '.' and ':' are escaped too, so
    // "www.", "https://" and e-mail addresses in package metadata never become
    // GitHub-flavored autolinks.
    public static string MarkdownCell(string? value)
    {
        var line = Line(value);
        var sb = new StringBuilder(line.Length);
        foreach (var c in line)
        {
            if (c is '\\' or '|' or '`' or '*' or '_' or '[' or ']' or '<' or '>' or '#' or '!' or '.' or ':' or '~')
                sb.Append('\\');
            sb.Append(c);
        }
        return sb.ToString();
    }

    // A Markdown link destination for an http(s) URL: everything outside the
    // unreserved and reserved URL characters is percent-encoded, including '|'
    // (which would end the table cell), spaces, '<', '>' and '\'.
    public static string MarkdownLinkTarget(string url)
    {
        // Parentheses and brackets are encoded too: an unbalanced ')' would end
        // the destination early and let the rest of the URL parse as a new link.
        const string allowed = "-._~:/?#@!$&'*+,;=%";
        var sb = new StringBuilder(url.Length);
        foreach (var b in Encoding.UTF8.GetBytes(url))
        {
            var c = (char)b;
            if (b < 0x80 && (char.IsAsciiLetterOrDigit(c) || allowed.Contains(c)))
                sb.Append(c);
            else
                sb.Append('%').Append(b.ToString("X2", CultureInfo.InvariantCulture));
        }
        return sb.ToString();
    }

    public static string Html(string? value) => System.Net.WebUtility.HtmlEncode(Line(value));

    private static bool IsLineBreak(Rune rune) =>
        rune.Value is '\r' or '\n' or 0x0B or 0x0C or 0x85 or 0x2028 or 0x2029;

    // Control characters (C0, C1, the ESC of terminal sequences) and format
    // characters (Cf: bidi controls, zero-width characters, BOM, soft hyphen).
    // Lone surrogates never get here: EnumerateRunes yields U+FFFD for them.
    private static bool IsUnsafe(Rune rune) =>
        Rune.IsControl(rune) ||
        Rune.GetUnicodeCategory(rune) == UnicodeCategory.Format;
}
