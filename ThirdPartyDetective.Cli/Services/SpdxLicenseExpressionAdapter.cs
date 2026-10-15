namespace ThirdPartyDetective.Cli.Services;

// Normalizes a raw license value from an SBOM, registry or override into the
// canonical SPDX expression that is stored on the package and printed in
// ATTRIBUTION.txt. The expression is kept whole: "GPL-3.0-or-later" stays
// "GPL-3.0-or-later" and exceptions stay attached, because the attribution has
// to name the license the package actually declares. Mapping to a coarse
// LicenseType bucket for policy purposes happens later, in LicenseAnalyzer.
public class SpdxLicenseExpressionAdapter : ISpdxLicenseExpressionAdapter
{
    public LicenseExpressionInfo Normalize(string? rawSpdxExpression)
    {
        if (string.IsNullOrWhiteSpace(rawSpdxExpression))
            return Empty;

        var trimmed = rawSpdxExpression.Trim();
        if (IsNoassertion(trimmed))
            return Empty;

        var parsed = SpdxExpression.TryParse(trimmed);
        if (parsed == null)
        {
            // Not an SPDX expression (free text such as "Apache License, Version 2.0"):
            // keep it verbatim so LicenseAnalyzer can try its free-text aliases.
            return new LicenseExpressionInfo(trimmed, new[] { trimmed }, false, null, Array.Empty<string>());
        }

        var terms = parsed.Terms.ToList();
        var exceptions = terms
            .Where(t => t.Exception != null)
            .Select(t => t.Exception!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new LicenseExpressionInfo(
            Normalized: parsed.ToString(),
            SubExpressions: terms.Select(t => t.License).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            IsCompound: parsed.IsCompound,
            WithException: exceptions.FirstOrDefault(),
            Exceptions: exceptions);
    }

    private static readonly LicenseExpressionInfo Empty =
        new(null, Array.Empty<string>(), false, null, Array.Empty<string>());

    private static bool IsNoassertion(string? value) =>
        string.IsNullOrEmpty(value) ||
        string.Equals(value, "NOASSERTION", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(value, "NONE", StringComparison.OrdinalIgnoreCase);
}
