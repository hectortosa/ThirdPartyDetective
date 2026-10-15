using System.Text.RegularExpressions;

namespace ThirdPartyDetective.Cli.Services;

// Finds the copyright notices in a package's license or NOTICE files. npm and
// PyPI metadata have no copyright field, and MIT, BSD and ISC require the notice
// to accompany the software, so the license file is where it has to come from.
// Lines are returned verbatim; template placeholders and the license's own
// copyright (the FSF's, on GNU texts) are not notices for the package.
public static class CopyrightExtractor
{
    private const int MaxNotices = 5;
    private const int MaxLineLength = 200;

    // Words that follow "copyright" in license prose rather than in a notice.
    private static readonly HashSet<string> ProseWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "notice", "notices", "holder", "holders", "owner", "owners", "law", "laws", "and", "or", "license",
        "licenses", "statement", "protection", "interest", "is", "to", "of", "the", "for", "in", "on", "under",
        "act", "claim", "claims", "infringement", "office", "rights", "may", "shall", "will", "does",
        // A label followed by a sentence: "Copyright: This document has been placed in the public domain."
        "this", "these", "that", "those", "it", "its", "all", "any", "each", "every", "no", "none", "such",
        "see", "please", "unless", "except", "if", "as", "with", "not",
    };

    // "(c)" also numbers list items ("(c) You must retain..."), so it needs a year.
    private static readonly Regex ParenthesizedC = new(@"^\(c\)\s*\d{4}", RegexOptions.IgnoreCase | RegexOptions.NonBacktracking);

    private static readonly Regex Placeholder = new(
        @"[<\[{]\s*(year|yyyy|name|owner|copyright|fullname|author|holder|organization)",
        RegexOptions.IgnoreCase | RegexOptions.NonBacktracking);

    private static readonly Regex Whitespace = new(@"\s+", RegexOptions.NonBacktracking);

    public static string? Extract(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        var notices = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var lines = text.Split('\n').Select(Clean).ToList();
        for (var i = 0; i < lines.Count && notices.Count < MaxNotices; i++)
        {
            var line = lines[i];
            if (line.Length == 0 || line.Length > MaxLineLength || !IsNotice(line)) continue;

            // A notice wrapped after a comma continues on the next line:
            // "Copyright (c) 1991 - 1995, Stichting Mathematisch Centrum Amsterdam," / "The Netherlands."
            if (line.EndsWith(',') && i + 1 < lines.Count && lines[i + 1].Length > 0 &&
                line.Length + lines[i + 1].Length < MaxLineLength && !IsNotice(lines[i + 1]))
                line = $"{line} {lines[++i]}";

            if (seen.Add(line))
                notices.Add(line);
        }
        return notices.Count == 0 ? null : string.Join("; ", notices);
    }

    // Comment and list markers go: "# Copyright ...", " * Copyright ...", "- Copyright ...".
    private static string Clean(string rawLine) =>
        Whitespace.Replace(rawLine.Trim().TrimStart('#', '*', '/', '-', ';', '>').Trim(), " ");

    private static bool IsNotice(string line)
    {
        var isCandidate = line.StartsWith('©') || ParenthesizedC.IsMatch(line) || StartsWithCopyrightWord(line);
        if (!isCandidate || Placeholder.IsMatch(line)) return false;
        // GNU license texts carry the FSF's copyright on the license itself.
        return !line.Contains("Free Software Foundation", StringComparison.OrdinalIgnoreCase);
    }

    // "Copyright" followed by a symbol, a year or a capitalized name. A wrapped
    // license line can also start with the word ("copyright notice and this
    // permission...", "COPYRIGHT HOLDERS AND CONTRIBUTORS"); those are prose.
    private static bool StartsWithCopyrightWord(string line)
    {
        const string word = "copyright";
        if (!line.StartsWith(word, StringComparison.OrdinalIgnoreCase)) return false;
        var rest = line[word.Length..];
        if (rest.Length > 0 && char.IsLetterOrDigit(rest[0])) return false; // "copyrighted"
        rest = rest.TrimStart(' ', '\t', ':');
        if (rest.Length == 0) return false;
        if (rest[0] == '©' || char.IsAsciiDigit(rest[0]) || rest.StartsWith("(c)", StringComparison.OrdinalIgnoreCase))
            return true;
        var next = new string(rest.TakeWhile(char.IsLetter).ToArray());
        return next.Length > 0 && char.IsUpper(next[0]) && !ProseWords.Contains(next);
    }
}
