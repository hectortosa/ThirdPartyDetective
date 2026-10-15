namespace ThirdPartyDetective.Cli.Services;

// Maps license URLs whose path names the license exactly to an SPDX expression.
// Anything else (repository pages, vendor EULAs, homepages) is deliberately not
// guessed from: a URL that merely contains "mit" or "gpl" says nothing reliable
// about the license, and a wrong guess ends up in a customer-facing notice.
public static class KnownLicenseUrls
{
    // Every license in a URL-derived expression must be a listed SPDX ID, so a page
    // name such as "bsd-license" or "index" never passes for one.
    private static readonly ISpdxLicenseList LicenseList = new BundledSpdxLicenseList();

    public static bool TryGetExpression(string? url, out string expression)
    {
        expression = string.Empty;
        if (string.IsNullOrWhiteSpace(url) ||
            !Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            return false;

        var host = uri.Host.ToLowerInvariant();
        if (host.StartsWith("www.", StringComparison.Ordinal)) host = host[4..];
        var segments = uri.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);

        string? candidate = host switch
        {
            // NuGet renders <license type="expression"> as https://licenses.nuget.org/<expression>.
            "licenses.nuget.org" when segments.Length == 1 => Uri.UnescapeDataString(segments[0]),
            "spdx.org" when segments.Length == 2 && segments[0] == "licenses" => StripExtension(segments[1]),
            "opensource.org" when segments.Length == 2 && segments[0] == "licenses" => OsiLegacyAlias(StripExtension(segments[1])),
            "apache.org" when segments.Length == 2 && segments[0] == "licenses" &&
                              StripExtension(segments[1]).Equals("LICENSE-2.0", StringComparison.OrdinalIgnoreCase) => "Apache-2.0",
            _ => null
        };

        if (candidate == null || SpdxExpression.TryParse(candidate) is not { } parsed)
            return false;

        foreach (var term in parsed.Terms)
        {
            if (!LicenseList.TryGetCanonicalId(term.LicenseId, out _) ||
                (term.Exception != null && !LicenseList.TryGetCanonicalId(term.Exception, out _)))
                return false;
        }

        // A single license is reported in the list's casing ("mit" -> "MIT").
        if (parsed is SpdxLicenseTerm single)
        {
            LicenseList.TryGetCanonicalId(single.LicenseId, out var id);
            string? exception = null;
            if (single.Exception != null) LicenseList.TryGetCanonicalId(single.Exception, out exception);
            parsed = single with { LicenseId = id, Exception = exception };
        }

        expression = parsed.ToString();
        return true;
    }

    private static string StripExtension(string segment)
    {
        foreach (var ext in new[] { ".html", ".htm", ".txt", ".json", ".php" })
        {
            if (segment.EndsWith(ext, StringComparison.OrdinalIgnoreCase))
                return segment[..^ext.Length];
        }
        return segment;
    }

    // opensource.org/licenses/mit-license.php was the canonical MIT link for years.
    private static string OsiLegacyAlias(string segment) =>
        segment.Equals("mit-license", StringComparison.OrdinalIgnoreCase) ? "MIT" : segment;
}
