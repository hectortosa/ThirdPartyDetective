using System.Text;

namespace ThirdPartyDetective.Cli.Services;

// A parsed SPDX license expression (SPDX 2.3, Annex D): license IDs with an
// optional "+", LicenseRef-/DocumentRef- references, WITH exceptions, AND/OR
// with AND binding tighter, and parentheses. Operators are accepted in any case
// and rendered in upper case. Parsing is linear in the input; inputs that are
// too long or too deeply nested are rejected rather than parsed.
public abstract record SpdxExpression
{
    public const int MaxLength = 4096;
    private const int MaxDepth = 32;

    // The license terms (leaves) of the expression, in order of appearance.
    public abstract IEnumerable<SpdxLicenseTerm> Terms { get; }

    public bool IsCompound => this is SpdxAnd or SpdxOr;

    // Returns null when the text is not a valid SPDX expression (for example a
    // free-text name such as "Apache License, Version 2.0").
    public static SpdxExpression? TryParse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > MaxLength) return null;

        var tokens = Tokenize(text);
        if (tokens == null || tokens.Count == 0) return null;

        var parser = new Parser(tokens);
        var result = parser.ParseOr(depth: 0);
        return result != null && parser.AtEnd ? result : null;
    }

    // Renders a sub-expression for use as an operand: compound operands are
    // parenthesized so precedence never has to be inferred by the reader.
    private protected static string AsOperand(SpdxExpression e) => e.IsCompound ? $"({e})" : e.ToString()!;

    private static List<string>? Tokenize(string text)
    {
        var tokens = new List<string>();
        var current = new StringBuilder();

        void Flush()
        {
            if (current.Length > 0) tokens.Add(current.ToString());
            current.Clear();
        }

        foreach (var c in text)
        {
            if (char.IsWhiteSpace(c)) { Flush(); continue; }
            if (c is '(' or ')') { Flush(); tokens.Add(c.ToString()); continue; }
            if (!IsIdChar(c)) return null;
            current.Append(c);
        }
        Flush();
        return tokens;
    }

    // SPDX idstring characters, plus '+' (or-later) and ':' (DocumentRef-x:LicenseRef-y).
    private static bool IsIdChar(char c) =>
        c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '-' or '.' or '+' or ':';

    private static bool IsOperator(string token, string op) =>
        string.Equals(token, op, StringComparison.OrdinalIgnoreCase);

    private sealed class Parser
    {
        private readonly List<string> _tokens;
        private int _pos;

        public Parser(List<string> tokens) => _tokens = tokens;

        public bool AtEnd => _pos == _tokens.Count;

        private string? Peek => _pos < _tokens.Count ? _tokens[_pos] : null;

        public SpdxExpression? ParseOr(int depth)
        {
            var operands = new List<SpdxExpression>();
            var first = ParseAnd(depth);
            if (first == null) return null;
            operands.Add(first);

            while (Peek != null && IsOperator(Peek, "OR"))
            {
                _pos++;
                var next = ParseAnd(depth);
                if (next == null) return null;
                operands.Add(next);
            }
            return operands.Count == 1 ? operands[0] : new SpdxOr(Flatten<SpdxOr>(operands));
        }

        private SpdxExpression? ParseAnd(int depth)
        {
            var operands = new List<SpdxExpression>();
            var first = ParseWith(depth);
            if (first == null) return null;
            operands.Add(first);

            while (Peek != null && IsOperator(Peek, "AND"))
            {
                _pos++;
                var next = ParseWith(depth);
                if (next == null) return null;
                operands.Add(next);
            }
            return operands.Count == 1 ? operands[0] : new SpdxAnd(Flatten<SpdxAnd>(operands));
        }

        private SpdxExpression? ParseWith(int depth)
        {
            var primary = ParsePrimary(depth);
            if (primary == null) return null;
            if (Peek == null || !IsOperator(Peek, "WITH")) return primary;

            // WITH applies to a single license, never to a parenthesized compound.
            if (primary is not SpdxLicenseTerm { Exception: null } term) return null;
            _pos++;
            var exception = Peek;
            if (exception == null || !IsLicenseToken(exception) || exception.Contains('+')) return null;
            _pos++;
            return term with { Exception = exception };
        }

        private SpdxExpression? ParsePrimary(int depth)
        {
            var token = Peek;
            if (token == null) return null;

            if (token == "(")
            {
                if (depth >= MaxDepth) return null;
                _pos++;
                var inner = ParseOr(depth + 1);
                if (inner == null || Peek != ")") return null;
                _pos++;
                return inner;
            }

            if (!IsLicenseToken(token)) return null;
            _pos++;

            var orLater = token.EndsWith('+');
            var id = orLater ? token[..^1] : token;
            if (id.Length == 0 || id.Contains('+')) return null;
            return new SpdxLicenseTerm(id, orLater, null);
        }

        private static bool IsLicenseToken(string token) =>
            token != "(" && token != ")" &&
            !IsOperator(token, "AND") && !IsOperator(token, "OR") && !IsOperator(token, "WITH");

        private static IReadOnlyList<SpdxExpression> Flatten<T>(List<SpdxExpression> operands)
            where T : SpdxExpression =>
            operands.SelectMany(o => o switch
            {
                SpdxAnd nestedAnd when typeof(T) == typeof(SpdxAnd) => nestedAnd.Operands,
                SpdxOr nestedOr when typeof(T) == typeof(SpdxOr) => nestedOr.Operands,
                _ => new[] { o }
            }).ToList();
    }
}

// A single license, e.g. "MIT", "GPL-2.0+" or "GPL-2.0-only WITH Classpath-exception-2.0".
public sealed record SpdxLicenseTerm(string LicenseId, bool OrLater, string? Exception) : SpdxExpression
{
    // The license as written in the expression, without any exception: "GPL-2.0+".
    public string License => OrLater ? LicenseId + "+" : LicenseId;

    public bool IsLicenseRef =>
        LicenseId.StartsWith("LicenseRef-", StringComparison.OrdinalIgnoreCase) ||
        LicenseId.StartsWith("DocumentRef-", StringComparison.OrdinalIgnoreCase);

    public override IEnumerable<SpdxLicenseTerm> Terms => new[] { this };

    public override string ToString() => Exception == null ? License : $"{License} WITH {Exception}";
}

public sealed record SpdxAnd(IReadOnlyList<SpdxExpression> Operands) : SpdxExpression
{
    public override IEnumerable<SpdxLicenseTerm> Terms => Operands.SelectMany(o => o.Terms);

    public override string ToString() => string.Join(" AND ", Operands.Select(AsOperand));
}

public sealed record SpdxOr(IReadOnlyList<SpdxExpression> Operands) : SpdxExpression
{
    public override IEnumerable<SpdxLicenseTerm> Terms => Operands.SelectMany(o => o.Terms);

    public override string ToString() => string.Join(" OR ", Operands.Select(AsOperand));
}
