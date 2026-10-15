using System.Net;
using System.Text.RegularExpressions;

namespace ThirdPartyDetective.Cli.Services;

// Identifies a license from its text by comparing it with the canonical SPDX
// texts. It returns null whenever the answer isn't clear: an unidentified
// license is resolved by a human (an override), while a wrong one ends up in a
// customer-facing notice.
//
// 1. Candidate. GNU licenses are recognised by their title at the very start of
//    the text (their bodies quote each other: GPL-3.0 mentions the Affero
//    license, the LGPL-3.0 text embeds GPL-3.0). Everything else is scored with
//    the Sørensen–Dice coefficient over word sets, ignoring copyright lines: the
//    best template must score at least MinSimilarity and beat the runner-up by
//    MinMargin.
// 2. Confirmation. The text must be the candidate license and nothing more: its
//    word sequence has to align with the template's, where the template may be
//    cut short (files often omit the "how to apply" appendix) but the text may
//    add at most MaxInsertedWords words, in runs of at most MaxInsertedRun
//    (filled-in names, a title line). Word sets alone can't see an appended
//    "no commercial use" clause, a WITH exception or a second license; a run of
//    extra words can.
public sealed class LicenseMatcher
{
    private const double MinSimilarity = 0.90;
    private const double MinMargin = 0.02;
    private const int MinWords = 20;
    private const int MinGnuWords = 300;
    private const int MaxInsertedWords = 6;
    private const int MaxInsertedRun = 3;

    private static readonly (string Title, string LicenseId)[] GnuTitles =
    {
        ("gnu affero general public license version 3", "AGPL-3.0-only"),
        ("gnu lesser general public license version 3", "LGPL-3.0-only"),
        ("gnu lesser general public license version 2 1", "LGPL-2.1-only"),
        ("gnu library general public license version 2", "LGPL-2.0-only"),
        ("gnu general public license version 3", "GPL-3.0-only"),
        ("gnu general public license version 2", "GPL-2.0-only"),
        ("gnu general public license version 1", "GPL-1.0-only"),
    };

    // Non-backtracking: these run on text fetched from arbitrary license URLs.
    // A line opening with "(c)" is a notice only when it names a year: Apache-2.0's
    // clause 4 "(c) You must retain ..." is license text, and dropping its first line
    // from an 80-column file but the whole paragraph from the one-line SPDX text left
    // the rest of the clause looking like inserted words.
    private static readonly Regex CopyrightLine = new(@"^[ \t]*(copyright\b|\(c\)[^\n]*\b(19|20)[0-9][0-9]\b|©|all rights reserved)", RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.NonBacktracking);
    private static readonly Regex ScriptBlocks = new(@"<script\b.*?</script\s*>", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.NonBacktracking);
    private static readonly Regex StyleBlocks = new(@"<style\b.*?</style\s*>", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.NonBacktracking);
    private static readonly Regex HtmlTags = new(@"<[^>]*>", RegexOptions.NonBacktracking);
    private static readonly Regex NonWord = new(@"[^a-z0-9]+", RegexOptions.NonBacktracking);

    private readonly Lazy<IReadOnlyList<Template>> _templates;

    public LicenseMatcher(ISpdxLicenseList licenseList)
    {
        _templates = new Lazy<IReadOnlyList<Template>>(() => BuildTemplates(licenseList));
    }

    // A group of licenses whose canonical texts are word-for-word identical;
    // LicenseId is null when the group has no common base ID to report.
    private sealed class Template
    {
        private readonly Lazy<Dictionary<string, int[]>> _positions;

        public Template(string? licenseId, string[] sequence)
        {
            LicenseId = licenseId;
            Words = new HashSet<string>(sequence, StringComparer.Ordinal);
            _positions = new Lazy<Dictionary<string, int[]>>(() => sequence
                .Select((word, index) => (word, index))
                .GroupBy(x => x.word, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.Select(x => x.index).ToArray(), StringComparer.Ordinal));
        }

        public string? LicenseId { get; }

        public HashSet<string> Words { get; }

        // Ascending positions of each word in the template's word sequence.
        public Dictionary<string, int[]> Positions => _positions.Value;
    }

    public string? Match(string text)
    {
        var sequence = Tokens(Normalize(text));
        if (sequence.Length < MinWords) return null;

        var head = string.Join(' ', sequence.Take(12));
        foreach (var (title, licenseId) in GnuTitles)
        {
            if (!head.StartsWith(title, StringComparison.Ordinal)) continue;
            var gnu = _templates.Value.FirstOrDefault(t => t.LicenseId == licenseId);
            return gnu != null && sequence.Length >= MinGnuWords && AlignsWithTemplate(sequence, gnu) ? licenseId : null;
        }

        var words = new HashSet<string>(sequence, StringComparer.Ordinal);
        Template? best = null;
        double bestScore = 0, secondScore = 0;
        foreach (var template in _templates.Value)
        {
            var score = Dice(words, template.Words);
            if (score > bestScore)
            {
                secondScore = bestScore;
                bestScore = score;
                best = template;
            }
            else if (score > secondScore)
            {
                secondScore = score;
            }
        }

        if (best == null || bestScore < MinSimilarity || bestScore - secondScore < MinMargin)
            return null;

        return AlignsWithTemplate(sequence, best) ? best.LicenseId : null;
    }

    // Whether every word of the text can be matched, in order, to the template's
    // word sequence, leaving at most MaxInsertedWords unmatched text words and no
    // unmatched run longer than MaxInsertedRun. Template words may be skipped
    // freely. Dynamic programming over (inserted so far, current run): for each
    // state it keeps the earliest template position reached, which dominates any
    // later one, so the check is exact and linear in the text length.
    private static bool AlignsWithTemplate(string[] text, Template template)
    {
        const int unreachable = int.MaxValue;
        var current = NewStates(unreachable);
        current[0, 0] = 0;

        foreach (var word in text)
        {
            var next = NewStates(unreachable);
            template.Positions.TryGetValue(word, out var positions);
            var anyReachable = false;

            for (int inserted = 0; inserted <= MaxInsertedWords; inserted++)
            {
                for (int run = 0; run <= MaxInsertedRun; run++)
                {
                    var position = current[inserted, run];
                    if (position == unreachable) continue;

                    // Match the word at its first occurrence at or after the position.
                    if (positions != null)
                    {
                        var index = Array.BinarySearch(positions, position);
                        if (index < 0) index = ~index;
                        if (index < positions.Length)
                        {
                            next[inserted, 0] = Math.Min(next[inserted, 0], positions[index] + 1);
                            anyReachable = true;
                        }
                    }

                    // Or count it as inserted text.
                    if (inserted < MaxInsertedWords && run < MaxInsertedRun)
                    {
                        next[inserted + 1, run + 1] = Math.Min(next[inserted + 1, run + 1], position);
                        anyReachable = true;
                    }
                }
            }

            if (!anyReachable) return false;
            current = next;
        }
        return true;
    }

    private static int[,] NewStates(int value)
    {
        var states = new int[MaxInsertedWords + 1, MaxInsertedRun + 1];
        for (int i = 0; i <= MaxInsertedWords; i++)
            for (int j = 0; j <= MaxInsertedRun; j++)
                states[i, j] = value;
        return states;
    }

    private static IReadOnlyList<Template> BuildTemplates(ISpdxLicenseList licenseList)
    {
        var groups = new Dictionary<string, (string[] Sequence, List<string> Ids)>(StringComparer.Ordinal);
        foreach (var id in licenseList.LicenseIds)
        {
            var text = licenseList.GetText(id);
            if (text == null) continue;
            var sequence = Tokens(Normalize(text));
            var signature = string.Join(' ', sequence.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal));
            if (groups.TryGetValue(signature, out var group)) group.Ids.Add(id);
            else groups[signature] = (sequence, new List<string> { id });
        }

        return groups.Values.Select(g => new Template(Representative(g.Ids), g.Sequence)).ToList();
    }

    // Identical texts under several IDs (GPL-3.0-only / -or-later, MPL-2.0 /
    // MPL-2.0-no-copyleft-exception, OFL-1.1 / -RFN / -no-RFN) are reported as
    // the shortest ID when every other ID is a hyphenated variant of it
    // (OLDAP-2.2 and OLDAP-2.2.1 are different versions, not variants); otherwise
    // the text alone can't tell them apart.
    private static string? Representative(List<string> ids)
    {
        if (ids.Count == 1) return ids[0];
        var shortest = ids.OrderBy(id => id.Length).ThenBy(id => id, StringComparer.Ordinal).First();
        var stem = Stem(shortest);
        return ids.All(id => Stem(id).Equals(stem, StringComparison.OrdinalIgnoreCase) ||
                             Stem(id).StartsWith(stem + "-", StringComparison.OrdinalIgnoreCase))
            ? shortest
            : null;
    }

    private static string Stem(string id)
    {
        foreach (var suffix in new[] { "-only", "-or-later" })
        {
            if (id.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) return id[..^suffix.Length];
        }
        return id;
    }

    internal static string Normalize(string text)
    {
        if (text.Contains("<html", StringComparison.OrdinalIgnoreCase) || text.Contains("<body", StringComparison.OrdinalIgnoreCase))
            text = WebUtility.HtmlDecode(HtmlTags.Replace(StyleBlocks.Replace(ScriptBlocks.Replace(text, " "), " "), "\n"));

        text = CopyrightLine.Replace(text.Replace("\r\n", "\n"), m => "\u0000");
        var lines = text.Split('\n').Where(l => !l.StartsWith('\u0000'));
        return NonWord.Replace(string.Join('\n', lines).ToLowerInvariant(), " ").Trim();
    }

    private static string[] Tokens(string normalized) =>
        normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries);

    private static double Dice(HashSet<string> a, HashSet<string> b)
    {
        var (small, large) = a.Count <= b.Count ? (a, b) : (b, a);
        var overlap = small.Count(large.Contains);
        return 2.0 * overlap / (a.Count + b.Count);
    }
}
