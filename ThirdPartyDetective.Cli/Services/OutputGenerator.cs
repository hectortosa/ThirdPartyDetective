using Microsoft.Extensions.Logging;
using System.Text;
using System.Text.Json;
using ThirdPartyDetective.Cli.Models;

namespace ThirdPartyDetective.Cli.Services;

public class OutputGenerator : IOutputGenerator
{
    private readonly ILicenseTextProvider _licenseTextProvider;
    private readonly INoticeTemplateLoader _noticeTemplateLoader;
    private readonly ILogger<OutputGenerator> _logger;

    public OutputGenerator(ILicenseTextProvider licenseTextProvider, INoticeTemplateLoader noticeTemplateLoader, ILogger<OutputGenerator> logger)
    {
        _licenseTextProvider = licenseTextProvider;
        _noticeTemplateLoader = noticeTemplateLoader;
        _logger = logger;
    }

    public async Task GenerateOutputAsync(PackageAnalysisResult result, ScanOptions options, CancellationToken cancellationToken = default)
    {
        // Don't create directory for console output
        if (options.Format != OutputFormat.Console)
        {
            Directory.CreateDirectory(options.Output);
        }

        // Resolve Appendix A up front: texts that are missing are recorded in the
        // summary, which packages.json and the exit code (--fail-on-unresolved) use.
        var writesAttribution = options.Format is OutputFormat.All or OutputFormat.FullText;
        // Loaded before anything is written, so a broken template fails the run cleanly.
        var noticeTemplate = writesAttribution ? _noticeTemplateLoader.Load(options.NoticeTemplatePath) : new NoticeTemplate();
        Appendix? appendix = null;
        if (writesAttribution && options.IncludeLicenseText)
        {
            appendix = await BuildAppendixAsync(result.Packages, cancellationToken);
            result.Summary.MissingLicenseTexts = appendix.Sections
                .Where(section => section.Text == null)
                .Select(section => section.Heading)
                .ToList();
        }

        var tasks = new List<Task>();

        switch (options.Format)
        {
            case OutputFormat.All:
                tasks.Add(GenerateMarkdownAsync(result, options, cancellationToken));
                tasks.Add(GenerateJsonAsync(result, options, cancellationToken));
                tasks.Add(GenerateHtmlAsync(result, options, cancellationToken));
                tasks.Add(GenerateFullTextAsync(result, options, appendix, noticeTemplate, cancellationToken));
                break;

            case OutputFormat.Markdown:
                tasks.Add(GenerateMarkdownAsync(result, options, cancellationToken));
                break;

            case OutputFormat.Json:
                tasks.Add(GenerateJsonAsync(result, options, cancellationToken));
                break;

            case OutputFormat.Html:
                tasks.Add(GenerateHtmlAsync(result, options, cancellationToken));
                break;

            case OutputFormat.FullText:
                tasks.Add(GenerateFullTextAsync(result, options, appendix, noticeTemplate, cancellationToken));
                break;

            case OutputFormat.Console:
                tasks.Add(GenerateConsoleAsync(result, options, cancellationToken));
                break;
        }

        await Task.WhenAll(tasks);

        if (options.Format != OutputFormat.Console)
        {
            _logger.LogInformation("Generated output files in: {OutputPath}", options.Output);
        }
    }

    private async Task GenerateMarkdownAsync(PackageAnalysisResult result, ScanOptions options, CancellationToken cancellationToken)
    {
        var md = OutputText.MarkdownCell;
        var sb = new StringBuilder();

        sb.AppendLine("# Third-Party Package Analysis Report");
        sb.AppendLine();
        if (FormatTimestamp(result.GeneratedAt) is { } generated)
            sb.AppendLine($"**Generated:** {generated}");
        sb.AppendLine($"**Scan Path:** {md(result.ScanPath)}");
        sb.AppendLine($"**Mode:** {result.Mode}");
        sb.AppendLine();

        // Summary
        sb.AppendLine("## Summary");
        sb.AppendLine();
        sb.AppendLine($"- **Total Packages:** {result.Summary.TotalPackages}");
        sb.AppendLine($"- **Requiring Attribution:** {result.Summary.PackagesRequiringAttribution}");
        sb.AppendLine($"- **Warnings:** {result.Summary.WarningCount}");
        sb.AppendLine();

        // Document-level warnings (e.g. universal-missing-attribution summary)
        if (result.Summary.DocumentWarnings.Count > 0)
        {
            sb.AppendLine("### Document Warnings");
            sb.AppendLine();
            foreach (var w in result.Summary.DocumentWarnings)
                sb.AppendLine($"- ⚠️ {md(w)}");
            sb.AppendLine();
        }

        // License distribution
        if (result.Summary.LicenseTypeCounts.Any())
        {
            sb.AppendLine("### License Distribution");
            sb.AppendLine();
            foreach (var license in result.Summary.LicenseTypeCounts.OrderByDescending(x => x.Value).ThenBy(x => x.Key, StringComparer.Ordinal))
            {
                sb.AppendLine($"- **{license.Key}:** {license.Value} packages");
            }
            sb.AppendLine();
        }

        // Policy violations (if any)
        if (result.Violations.Count > 0)
        {
            sb.AppendLine($"## 🚫 Policy Violations ({result.Violations.Count})");
            sb.AppendLine();
            sb.AppendLine($"Packages flagged as incompatible with mode `{result.Mode}`. Review each before shipping.");
            sb.AppendLine();
            sb.AppendLine("| Package | Version | Ecosystem | Policy Class | License Expression | Source |");
            sb.AppendLine("|---------|---------|-----------|--------------|--------------------|--------|");
            foreach (var v in OrderViolations(result.Violations))
            {
                var expression = string.IsNullOrEmpty(v.LicenseExpression) ? "_(no license info)_" : md(v.LicenseExpression);
                sb.AppendLine($"| {md(v.Name)} | {md(v.Version)} | {md(v.SourceEcosystem ?? "?")} | {v.LicenseType} | {expression} | {v.LicenseSource} |");
            }
            sb.AppendLine();
        }

        // Package table
        sb.AppendLine("## All Packages");
        sb.AppendLine();

        if (options.DevInfo)
        {
            sb.AppendLine("| Package | Version | License | Policy Class | Attribution Required | Project URL | Source | Projects | Warnings |");
            sb.AppendLine("|---------|---------|---------|--------------|---------------------|-------------|--------|----------|----------|");
        }
        else
        {
            sb.AppendLine("| Package | Version | License | Policy Class | Attribution Required | Project URL | Warnings |");
            sb.AppendLine("|---------|---------|---------|--------------|---------------------|-------------|----------|");
        }

        foreach (var package in OrderPackages(result.Packages))
        {
            var license = string.IsNullOrEmpty(package.LicenseExpression) ? "not determined" : md(package.LicenseExpression);
            var warningsText = package.WarningFlags.Any() ? string.Join(", ", package.WarningFlags.Select(md)) : "None";
            var projectUrlCell = OutputText.WebUrl(package.ProjectUrl) is { } url ? $"[link]({OutputText.MarkdownLinkTarget(url)})" : "—";
            var attribution = package.RequiresAttribution ? "Yes" : "No";

            if (options.DevInfo)
            {
                var source = md(package.SourceEcosystem ?? "Unknown");
                var projects = package.SourceProjects.Any() ? string.Join(", ", package.SourceProjects.Select(md)) : "N/A";
                sb.AppendLine($"| {md(package.Name)} | {md(package.Version)} | {license} | {package.LicenseType} | {attribution} | {projectUrlCell} | {source} | {projects} | {warningsText} |");
            }
            else
            {
                sb.AppendLine($"| {md(package.Name)} | {md(package.Version)} | {license} | {package.LicenseType} | {attribution} | {projectUrlCell} | {warningsText} |");
            }
        }

        await WriteReportAsync(Path.Combine(options.Output, "packages.md"), sb.ToString(), cancellationToken);
    }

    private static readonly JsonSerializerOptions JsonOutputOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    private async Task GenerateJsonAsync(PackageAnalysisResult result, ScanOptions options, CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(result, JsonOutputOptions);
        await WriteReportAsync(Path.Combine(options.Output, "packages.json"), json, cancellationToken);
    }

    private async Task GenerateHtmlAsync(PackageAnalysisResult result, ScanOptions options, CancellationToken cancellationToken)
    {
        var h = OutputText.Html;
        var sb = new StringBuilder();

        sb.AppendLine("<!DOCTYPE html>");
        sb.AppendLine("<html lang=\"en\">");
        sb.AppendLine("<head>");
        sb.AppendLine("    <meta charset=\"UTF-8\">");
        // No scripts, external resources or form posts: the report is static.
        sb.AppendLine("    <meta http-equiv=\"Content-Security-Policy\" content=\"default-src 'none'; style-src 'unsafe-inline'; form-action 'none'\">");
        sb.AppendLine("    <meta name=\"viewport\" content=\"width=device-width, initial-scale=1.0\">");
        sb.AppendLine("    <title>Third-Party Package Analysis Report</title>");
        sb.AppendLine("    <style>");
        sb.AppendLine(GetHtmlStyles());
        sb.AppendLine("    </style>");
        sb.AppendLine("</head>");
        sb.AppendLine("<body>");

        sb.AppendLine("    <div class=\"container\">");
        sb.AppendLine("        <h1>Third-Party Package Analysis Report</h1>");

        // Summary section
        sb.AppendLine("        <div class=\"summary\">");
        if (FormatTimestamp(result.GeneratedAt) is { } generated)
            sb.AppendLine($"            <p><strong>Generated:</strong> {generated}</p>");
        sb.AppendLine($"            <p><strong>Scan Path:</strong> {h(result.ScanPath)}</p>");
        sb.AppendLine($"            <p><strong>Mode:</strong> {result.Mode}</p>");
        sb.AppendLine("        </div>");

        sb.AppendLine("        <div class=\"stats\">");
        sb.AppendLine($"            <div class=\"stat-card\"><h3>{result.Summary.TotalPackages}</h3><p>Total Packages</p></div>");
        sb.AppendLine($"            <div class=\"stat-card\"><h3>{result.Summary.PackagesRequiringAttribution}</h3><p>Requiring Attribution</p></div>");
        sb.AppendLine($"            <div class=\"stat-card violation-card\"><h3>{result.Violations.Count}</h3><p>Policy Violations</p></div>");
        sb.AppendLine($"            <div class=\"stat-card\"><h3>{result.Summary.WarningCount}</h3><p>Warnings</p></div>");
        sb.AppendLine("        </div>");

        // Document-level warnings (e.g. universal-missing-attribution summary)
        if (result.Summary.DocumentWarnings.Count > 0)
        {
            sb.AppendLine("        <div class=\"document-warnings\">");
            sb.AppendLine("            <h3>Document Warnings</h3>");
            sb.AppendLine("            <ul>");
            foreach (var w in result.Summary.DocumentWarnings)
                sb.AppendLine($"                <li>{h(w)}</li>");
            sb.AppendLine("            </ul>");
            sb.AppendLine("        </div>");
        }

        // Policy violations section (if any)
        if (result.Violations.Count > 0)
        {
            sb.AppendLine("        <h2 class=\"violations-heading\">🚫 Policy Violations</h2>");
            sb.AppendLine($"        <p class=\"violations-intro\">Packages flagged as incompatible with mode <code>{result.Mode}</code>. Review each before shipping.</p>");
            sb.AppendLine("        <table class=\"violations-table\">");
            sb.AppendLine("            <thead>");
            sb.AppendLine("                <tr><th>Package</th><th>Version</th><th>Ecosystem</th><th>Policy Class</th><th>License Expression</th><th>Source</th></tr>");
            sb.AppendLine("            </thead>");
            sb.AppendLine("            <tbody>");
            foreach (var v in OrderViolations(result.Violations))
            {
                var expression = string.IsNullOrEmpty(v.LicenseExpression) ? "<em>(no info)</em>" : $"<code>{h(v.LicenseExpression)}</code>";
                sb.AppendLine($"                <tr><td><strong>{h(v.Name)}</strong></td><td>{h(v.Version)}</td><td>{h(v.SourceEcosystem ?? "?")}</td><td><span class=\"license-badge violation\">{v.LicenseType}</span></td><td>{expression}</td><td>{v.LicenseSource}</td></tr>");
            }
            sb.AppendLine("            </tbody>");
            sb.AppendLine("        </table>");
        }

        // Packages table
        sb.AppendLine("        <h2>All Packages</h2>");
        sb.AppendLine("        <table class=\"packages-table\">");
        sb.AppendLine("            <thead>");
        sb.AppendLine("                <tr>");
        sb.AppendLine("                    <th>Package</th>");
        sb.AppendLine("                    <th>Version</th>");
        sb.AppendLine("                    <th>License</th>");
        sb.AppendLine("                    <th>Policy Class</th>");
        sb.AppendLine("                    <th>Attribution</th>");
        sb.AppendLine("                    <th>SaaS Safe</th>");
        sb.AppendLine("                    <th>Project URL</th>");
        if (options.DevInfo)
        {
            sb.AppendLine("                    <th>Source</th>");
            sb.AppendLine("                    <th>Projects</th>");
        }
        sb.AppendLine("                    <th>Warnings</th>");
        sb.AppendLine("                </tr>");
        sb.AppendLine("            </thead>");
        sb.AppendLine("            <tbody>");

        foreach (var package in OrderPackages(result.Packages))
        {
            var attributionClass = package.RequiresAttribution ? "required" : "not-required";
            var saasClass = package.SaasCompliant ? "compliant" : "non-compliant";
            var warningsClass = package.WarningFlags.Any() ? "has-warnings" : "no-warnings";
            var license = string.IsNullOrEmpty(package.LicenseExpression) ? "<em>not determined</em>" : h(package.LicenseExpression);

            sb.AppendLine("                <tr>");
            sb.AppendLine($"                    <td><strong>{h(package.Name)}</strong></td>");
            sb.AppendLine($"                    <td>{h(package.Version)}</td>");
            sb.AppendLine($"                    <td>{license}</td>");
            sb.AppendLine($"                    <td><span class=\"license-badge\">{package.LicenseType}</span></td>");
            sb.AppendLine($"                    <td><span class=\"attribution-badge {attributionClass}\">{(package.RequiresAttribution ? "Required" : "Not Required")}</span></td>");
            sb.AppendLine($"                    <td><span class=\"saas-badge {saasClass}\">{(package.SaasCompliant ? "Safe" : "Review Needed")}</span></td>");
            var projectUrlCell = OutputText.WebUrl(package.ProjectUrl) is { } url
                ? $"<a href=\"{System.Net.WebUtility.HtmlEncode(url)}\" target=\"_blank\" rel=\"noopener noreferrer\">link</a>"
                : "<span class=\"muted\">—</span>";
            sb.AppendLine($"                    <td>{projectUrlCell}</td>");
            if (options.DevInfo)
            {
                var source = h(package.SourceEcosystem ?? "Unknown");
                var projects = package.SourceProjects.Any() ? string.Join("<br>", package.SourceProjects.Select(h)) : "N/A";
                sb.AppendLine($"                    <td><span class=\"source-badge\">{source}</span></td>");
                sb.AppendLine($"                    <td class=\"projects-cell\">{projects}</td>");
            }
            sb.AppendLine($"                    <td class=\"{warningsClass}\">{(package.WarningFlags.Any() ? string.Join("<br>", package.WarningFlags.Select(h)) : "None")}</td>");
            sb.AppendLine("                </tr>");
        }

        sb.AppendLine("            </tbody>");
        sb.AppendLine("        </table>");
        sb.AppendLine("    </div>");
        sb.AppendLine("</body>");
        sb.AppendLine("</html>");

        await WriteReportAsync(Path.Combine(options.Output, "packages.html"), sb.ToString(), cancellationToken);
    }

    // Report order is culture-independent: ordinal, case-insensitive by name, then version.
    private static IEnumerable<ThirdPartyInfo> OrderPackages(IEnumerable<ThirdPartyInfo> packages) =>
        packages.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(p => p.Name, StringComparer.Ordinal)
            .ThenBy(p => p.Version, StringComparer.Ordinal);

    private static IEnumerable<ThirdPartyInfo> OrderViolations(IEnumerable<ThirdPartyInfo> violations) =>
        OrderPackages(violations).OrderBy(p => p.SourceEcosystem ?? "", StringComparer.Ordinal);

    private static string? FormatTimestamp(DateTime? generatedAt) =>
        generatedAt?.ToString("yyyy-MM-dd HH:mm:ss 'UTC'", System.Globalization.CultureInfo.InvariantCulture);

    // One appendix entry per distinct license, exception or LicenseRef text.
    // Key identifies the entry (LicenseRef IDs are document-scoped, so two SBOMs
    // may use the same ID for different texts); Heading is unique within the
    // appendix; Text is null when unavailable. Untrusted texts (from the SBOM or
    // the package) are printed indented, so they can't fake an appendix heading.
    private sealed record AppendixSection(string Key, string Heading, string? Text, bool Untrusted);

    private sealed class Appendix
    {
        public required IReadOnlyList<AppendixSection> Sections { get; init; }
        public required IReadOnlyDictionary<string, AppendixSection> ByKey { get; init; }
    }

    private enum TextSource { SpdxList, Package, None }

    private sealed record AppendixReference(string Key, string LookupId, string BaseHeading, TextSource Source, string? PackageText);

    private async Task<Appendix> BuildAppendixAsync(
        IReadOnlyList<ThirdPartyInfo> packages,
        CancellationToken cancellationToken)
    {
        var sections = new Dictionary<string, AppendixSection>(StringComparer.OrdinalIgnoreCase);

        foreach (var package in OrderPackages(packages))
        {
            foreach (var reference in AppendixReferences(package))
            {
                if (sections.ContainsKey(reference.Key)) continue;

                var text = reference.Source switch
                {
                    TextSource.SpdxList => await _licenseTextProvider.GetTextAsync(reference.LookupId, cancellationToken),
                    TextSource.Package => reference.PackageText,
                    _ => null
                };
                sections[reference.Key] = new AppendixSection(reference.Key, reference.BaseHeading, text, reference.Source == TextSource.Package);

                if (text == null)
                    _logger.LogWarning("No license text available for {License} (used by {Name}@{Version})",
                        OutputText.Line(reference.BaseHeading), OutputText.Line(package.Name), OutputText.Line(package.Version));
            }
        }

        // Same heading, different texts (e.g. LicenseRef-1 from two SBOMs): number them.
        var final = new Dictionary<string, AppendixSection>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in sections.Values.GroupBy(sec => sec.Heading, StringComparer.OrdinalIgnoreCase))
        {
            var members = group.ToList();
            for (int i = 0; i < members.Count; i++)
            {
                var section = members.Count == 1 ? members[i] : members[i] with { Heading = $"{members[i].Heading} #{i + 1}" };
                final[section.Key] = section;
            }
        }

        return new Appendix
        {
            Sections = final.Values.OrderBy(sec => sec.Heading, StringComparer.OrdinalIgnoreCase).ToList(),
            ByKey = final
        };
    }

    // The licenses, exceptions and LicenseRef texts a package's expression refers to.
    // A free-text license that isn't an SPDX expression has no canonical text.
    private static IEnumerable<AppendixReference> AppendixReferences(ThirdPartyInfo package)
    {
        if (string.IsNullOrEmpty(package.LicenseExpression)) yield break;

        var parsed = SpdxExpression.TryParse(package.LicenseExpression);
        if (parsed == null)
        {
            yield return new AppendixReference(package.LicenseExpression, package.LicenseExpression,
                package.LicenseExpression, TextSource.None, null);
            yield break;
        }

        foreach (var term in parsed.Terms)
        {
            if (term.IsLicenseRef)
            {
                var text = LicenseRefText(package, term, parsed);
                var name = package.ExtractedLicenses.FirstOrDefault(e => string.Equals(e.LicenseId, term.LicenseId, StringComparison.Ordinal))?.Name;
                var heading = name is { Length: > 0 } ? $"{term.LicenseId} ({name})" : term.LicenseId;
                var key = text == null ? term.LicenseId : $"{term.LicenseId}\u0000{text}";
                yield return new AppendixReference(key, term.LicenseId, heading, text == null ? TextSource.None : TextSource.Package, text);
            }
            else
            {
                yield return new AppendixReference(term.License, term.License, term.License, TextSource.SpdxList, null);
            }

            if (term.Exception != null)
                yield return new AppendixReference(term.Exception, term.Exception, term.Exception, TextSource.SpdxList, null);
        }
    }

    // A LicenseRef's text: the SBOM's extracted text, or, when the package's whole
    // license is that one LicenseRef (an override for a custom license, or an npm
    // "SEE LICENSE IN" file), the package's own license text.
    private static string? LicenseRefText(ThirdPartyInfo package, SpdxLicenseTerm term, SpdxExpression expression)
    {
        var extracted = package.ExtractedLicenses.FirstOrDefault(e => string.Equals(e.LicenseId, term.LicenseId, StringComparison.Ordinal));
        if (extracted?.Text != null) return extracted.Text;
        return !expression.IsCompound && !string.IsNullOrWhiteSpace(package.LicenseText) ? package.LicenseText : null;
    }

    private async Task GenerateFullTextAsync(
        PackageAnalysisResult result,
        ScanOptions options,
        Appendix? appendix,
        NoticeTemplate noticeTemplate,
        CancellationToken cancellationToken)
    {
        var sb = new StringBuilder();

        sb.AppendLine("THIRD PARTY SOFTWARE NOTICES AND INFORMATION");
        sb.AppendLine(new string('=', 60));
        sb.AppendLine();
        sb.AppendLine($"This software incorporates components from the projects listed below.");
        if (FormatTimestamp(result.GeneratedAt) is { } generated)
            sb.AppendLine($"Generated on {generated}");
        sb.AppendLine();

        if (!string.IsNullOrWhiteSpace(noticeTemplate.Header))
        {
            sb.AppendLine(OutputText.Block(noticeTemplate.Header).TrimEnd());
            sb.AppendLine();
        }

        // Every shipped third-party component is listed, whatever its license; the
        // file doubles as the component inventory delivered to customers.
        var packages = OrderPackages(result.Packages).ToList();

        for (int i = 0; i < packages.Count; i++)
        {
            var package = packages[i];
            var references = AppendixReferences(package).ToList();

            sb.AppendLine($"{i + 1}. {OutputText.Line(package.Name)} {OutputText.Line(package.Version)}");

            if (!string.IsNullOrEmpty(package.Copyright))
                sb.AppendLine($"   Copyright: {OutputText.Line(package.Copyright)}");
            else if (!string.IsNullOrEmpty(package.Authors))
                sb.AppendLine($"   Authors: {OutputText.Line(package.Authors)}");

            sb.AppendLine($"   License: {OutputText.Line(DescribeLicense(package, references, appendix))}");

            if (OutputText.WebUrl(package.ProjectUrl) is { } projectUrl)
                sb.AppendLine($"   Project URL: {projectUrl}");

            if (OutputText.WebUrl(package.LicenseUrl) is { } licenseUrl)
                sb.AppendLine($"   License URL: {licenseUrl}");

            var copyleftNote = BuildCopyleftNote(package, noticeTemplate, appendix != null);
            if (copyleftNote != null)
            {
                sb.AppendLine();
                AppendIndentedLines(sb, copyleftNote);
            }

            // Without an appendix, LicenseRef texts from the SBOM are printed inline.
            if (appendix == null)
            {
                foreach (var extracted in package.ExtractedLicenses.Where(e => e.Text != null))
                    AppendIndentedText(sb, $"License Text ({OutputText.Line(extracted.LicenseId)}):", extracted.Text!);
            }

            // The package's own license text (e.g. its .nupkg license file) is always
            // printed: it carries the real copyright lines, and it may be a license
            // no canonical text describes. Skipped only when it already is this
            // package's LicenseRef section in the appendix.
            var licenseTextInAppendix = appendix != null &&
                references.Any(r => r.Source == TextSource.Package && r.PackageText == package.LicenseText);
            if (!string.IsNullOrWhiteSpace(package.LicenseText) && !licenseTextInAppendix)
                AppendIndentedText(sb, "License Text (from the package):", package.LicenseText);

            sb.AppendLine();
            sb.AppendLine(new string('-', 60));
            sb.AppendLine();
        }

        if (!string.IsNullOrWhiteSpace(noticeTemplate.Footer))
        {
            sb.AppendLine(OutputText.Block(noticeTemplate.Footer).TrimEnd());
            sb.AppendLine();
        }

        if (appendix != null)
            AppendLicenseTextAppendix(sb, appendix.Sections);

        await WriteReportAsync(Path.Combine(options.Output, "ATTRIBUTION.txt"), sb.ToString(), cancellationToken);
    }

    private static string DescribeLicense(ThirdPartyInfo package, IReadOnlyList<AppendixReference> references, Appendix? appendix)
    {
        if (string.IsNullOrEmpty(package.LicenseExpression))
            return "not determined (NOASSERTION)";
        if (appendix == null)
            return package.LicenseExpression;

        var sections = references.Select(r => appendix.ByKey[r.Key]).ToList();
        if (sections.Any(sec => sec.Text == null))
            return $"{package.LicenseExpression} (license text not available)";

        // Numbered headings are named, so the package points at the right text.
        var renamed = sections
            .Where(sec => !references.Any(r => string.Equals(r.Key, sec.Key, StringComparison.OrdinalIgnoreCase) &&
                                               string.Equals(r.BaseHeading, sec.Heading, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        return renamed.Count == 0
            ? $"{package.LicenseExpression} (see Appendix A)"
            : $"{package.LicenseExpression} (see Appendix A: {string.Join("; ", renamed.Select(sec => sec.Heading))})";
    }

    private static void AppendIndentedText(StringBuilder sb, string heading, string text)
    {
        sb.AppendLine();
        sb.AppendLine($"   {heading}");
        sb.AppendLine("   " + new string('-', 40));
        AppendIndentedLines(sb, text);
    }

    private static void AppendIndentedLines(StringBuilder sb, string text)
    {
        foreach (var line in OutputText.Block(text).TrimEnd().Split('\n'))
            sb.AppendLine(line.Length == 0 ? string.Empty : $"   {line.TrimEnd()}");
    }

    private static void AppendLicenseTextAppendix(StringBuilder sb, IReadOnlyList<AppendixSection> appendix)
    {
        sb.AppendLine();
        sb.AppendLine("=".PadRight(60, '='));
        sb.AppendLine("APPENDIX A: LICENSE TEXTS");
        sb.AppendLine("=".PadRight(60, '='));
        sb.AppendLine();
        sb.AppendLine("The following license texts apply to the packages listed above.");
        sb.AppendLine($"Texts are sourced from the SPDX license-list-data repository ({"https://github.com/spdx/license-list-data"}),");
        sb.AppendLine("except LicenseRef-* texts, which come from the SBOM or the package (shown indented).");
        sb.AppendLine();

        foreach (var section in appendix)
        {
            var heading = OutputText.Line(section.Heading);
            sb.AppendLine($"--- {heading} ---");
            sb.AppendLine();
            if (section.Text == null)
                sb.AppendLine($"[License text for {heading} is not available.]");
            else if (section.Untrusted)
                AppendIndentedLines(sb, section.Text);
            else
                sb.AppendLine(OutputText.Block(section.Text).TrimEnd());
            sb.AppendLine();
            sb.AppendLine();
        }
    }

    // Reports are written to a temporary file and renamed, so a failure part-way
    // never leaves an empty or truncated ATTRIBUTION.txt behind.
    private static async Task WriteReportAsync(string path, string content, CancellationToken cancellationToken)
    {
        var temp = path + ".tmp";
        await File.WriteAllTextAsync(temp, content, cancellationToken);
        File.Move(temp, path, overwrite: true);
    }

    // A note for GPL, LGPL and AGPL components. By default it states facts only:
    // which license, and where its source code conditions are. How source code is
    // actually made available (shipped alongside, offered from the same place, or a
    // written offer) is a commitment by whoever ships the notices, so that wording
    // comes from the notice template that legal maintains. Returns null when no
    // note applies.
    private static string? BuildCopyleftNote(ThirdPartyInfo package, NoticeTemplate template, bool hasAppendix)
    {
        // An exception (e.g. Classpath-exception-2.0) changes these obligations;
        // its text is in the appendix instead.
        if (package.LicenseExceptions.Count > 0) return null;

        var family = package.LicenseType switch
        {
            LicenseType.LGPL21 or LicenseType.LGPL30 => "LGPL",
            LicenseType.GPL20 or LicenseType.GPL30 => "GPL",
            LicenseType.AGPL30 => "AGPL",
            _ => null
        };
        if (family == null) return null;

        var license = OutputText.Line(package.LicenseExpression);
        if (template.CopyleftNotes.TryGetValue(family, out var wording))
        {
            if (string.IsNullOrWhiteSpace(wording)) return null;
            return wording
                .Replace("{name}", OutputText.Line(package.Name))
                .Replace("{version}", OutputText.Line(package.Version))
                .Replace("{license}", license)
                .Replace("{projectUrl}", OutputText.WebUrl(package.ProjectUrl) ?? "");
        }

        var where = hasAppendix ? "Appendix A" : "its license text";
        return $"Note: {license} is a copyleft license. Its conditions for providing source code are in {where}.";
    }

    private Task GenerateConsoleAsync(PackageAnalysisResult result, ScanOptions options, CancellationToken cancellationToken)
    {
        Console.WriteLine();
        Console.WriteLine("╔══════════════════════════════════════════════════════════════════════════════════╗");
        Console.WriteLine("║                           Third-Party Package Summary                           ║");
        Console.WriteLine("╚══════════════════════════════════════════════════════════════════════════════════╝");
        Console.WriteLine();

        // Summary
        Console.WriteLine($"📊 Total packages: {result.Summary.TotalPackages}");
        Console.WriteLine($"⚠️  Requiring attribution: {result.Summary.PackagesRequiringAttribution}");
        Console.WriteLine($"🚨 Warnings: {result.Summary.WarningCount}");
        Console.WriteLine($"📁 Scanned: {OutputText.Line(result.ScanPath)}");
        Console.WriteLine($"🎯 Mode: {result.Mode}");
        Console.WriteLine();

        if (result.Summary.DocumentWarnings.Count > 0)
        {
            foreach (var w in result.Summary.DocumentWarnings)
                Console.WriteLine($"⚠️  {OutputText.Line(w)}");
            Console.WriteLine();
        }

        if (result.Summary.LicenseTypeCounts.Any())
        {
            Console.WriteLine("📋 License Distribution:");
            foreach (var license in result.Summary.LicenseTypeCounts.OrderByDescending(x => x.Value))
            {
                var emoji = GetLicenseEmoji(license.Key);
                Console.WriteLine($"   {emoji} {license.Key}: {license.Value} packages");
            }
            Console.WriteLine();
        }

        if (!result.Packages.Any())
        {
            Console.WriteLine("✅ No third-party packages requiring attention found!");
            return Task.CompletedTask;
        }

        Console.WriteLine("📦 Third-Party Packages:");

        if (options.DevInfo)
        {
            Console.WriteLine("┌─────────────────────────────────────┬─────────┬──────────────┬──────────┬─────────────┬────────┬─────────────────────────────────────┐");
            Console.WriteLine("│ Package                             │ Version │ License      │ SaaS Safe│ Attribution │ Source │ Projects                            │");
            Console.WriteLine("├─────────────────────────────────────┼─────────┼──────────────┼──────────┼─────────────┼────────┼─────────────────────────────────────┤");
        }
        else
        {
            Console.WriteLine("┌─────────────────────────────────────┬─────────┬──────────────┬──────────┬─────────────┐");
            Console.WriteLine("│ Package                             │ Version │ License      │ SaaS Safe│ Attribution │");
            Console.WriteLine("├─────────────────────────────────────┼─────────┼──────────────┼──────────┼─────────────┤");
        }

        foreach (var package in OrderPackages(result.Packages))
        {
            var nameColumn = TruncateString(OutputText.Line(package.Name), 35);
            var versionColumn = TruncateString(OutputText.Line(package.Version), 7);
            var licenseColumn = TruncateString(package.LicenseType.ToString(), 12);
            var saasIcon = package.SaasCompliant ? "✅" : "❌";
            var attributionIcon = package.RequiresAttribution ? "⚠️ " : "✅";

            if (options.DevInfo)
            {
                var sourceColumn = TruncateString(OutputText.Line(package.SourceEcosystem ?? "Unknown"), 6);
                var projectsColumn = TruncateString(OutputText.Line(package.SourceProjects.Any() ? string.Join(", ", package.SourceProjects) : "N/A"), 35);

                Console.WriteLine($"│ {nameColumn,-35} │ {versionColumn,-7} │ {licenseColumn,-12} │    {saasIcon}    │     {attributionIcon}     │ {sourceColumn,-6} │ {projectsColumn,-35} │");
            }
            else
            {
                Console.WriteLine($"│ {nameColumn,-35} │ {versionColumn,-7} │ {licenseColumn,-12} │    {saasIcon}    │     {attributionIcon}     │");
            }

            // Show warnings if any
            if (package.WarningFlags.Any())
            {
                foreach (var warning in package.WarningFlags)
                {
                    var warningRowWidth = options.DevInfo ? 133 : 91;
                    var warningText = TruncateString($"  🚨 {OutputText.Line(warning)}", warningRowWidth);
                    var paddedWarningText = warningText.PadRight(warningRowWidth);
                    Console.WriteLine($"│ {paddedWarningText} │");
                }
            }
        }

        if (options.DevInfo)
        {
            Console.WriteLine("└─────────────────────────────────────┴─────────┴──────────────┴──────────┴─────────────┴────────┴─────────────────────────────────────┘");
        }
        else
        {
            Console.WriteLine("└─────────────────────────────────────┴─────────┴──────────────┴──────────┴─────────────┘");
        }

        Console.WriteLine();
        var registryResolved = result.Packages.Count(p => p.LicenseSource == ThirdPartyDetective.Cli.Models.LicenseSource.Registry);
        if (registryResolved > 0)
        {
            Console.WriteLine($"📡 {registryResolved} package(s) had license info resolved via registry fallback (cached for next run)");
            Console.WriteLine();
        }

        if (result.Violations.Count > 0)
        {
            Console.WriteLine($"🚫 POLICY VIOLATIONS ({result.Violations.Count}) for mode {result.Mode}:");
            foreach (var v in result.Violations)
            {
                var version = string.IsNullOrEmpty(v.Version) ? "" : $"@{v.Version}";
                Console.WriteLine($"   ⚠ {OutputText.Line(v.Name)}{OutputText.Line(version)} — {v.LicenseType} (incompatible with {result.Mode} mode)");
            }
            Console.WriteLine();
            Console.WriteLine("   Use --no-fail to suppress non-zero exit, or --exclude-license-types/--include-license-types to refine policy.");
        }
        else if (result.Packages.Any(p => p.RequiresAttribution))
        {
            Console.WriteLine("📝 INFO: Some packages require attribution in your software.");
            Console.WriteLine("   Use --format fulltext to generate complete attribution text.");
        }

        Console.WriteLine();
        Console.WriteLine("💡 TIP: For detailed reports, use --format all --output ./licenses");
        return Task.CompletedTask;
    }

    private static string GetLicenseEmoji(string licenseType)
    {
        return licenseType.ToLower() switch
        {
            "mit" => "🟢",
            "apache20" => "🔵",
            "bsd2clause" or "bsd3clause" => "🟠",
            "gpl20" or "gpl30" => "🔴",
            "lgpl21" or "lgpl30" => "🟡",
            "agpl30" => "🚫",
            "unknown" => "❓",
            "proprietary" => "🔒",
            _ => "📄"
        };
    }

    private static string TruncateString(string input, int maxLength)
    {
        if (string.IsNullOrEmpty(input))
            return new string(' ', maxLength);

        if (input.Length <= maxLength)
            return input.PadRight(maxLength);

        return input.Substring(0, maxLength - 3) + "...";
    }

    private static string GetHtmlStyles()
    {
        return @"
        body {
            font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, sans-serif;
            margin: 0;
            padding: 20px;
            background-color: #f5f5f5;
        }
        .container {
            max-width: 1200px;
            margin: 0 auto;
            background: white;
            padding: 30px;
            border-radius: 8px;
            box-shadow: 0 2px 10px rgba(0,0,0,0.1);
        }
        h1 {
            color: #333;
            border-bottom: 2px solid #007acc;
            padding-bottom: 10px;
        }
        .summary {
            background: #f8f9fa;
            padding: 15px;
            border-radius: 6px;
            margin: 20px 0;
        }
        .stats {
            display: flex;
            gap: 20px;
            margin: 20px 0;
        }
        .stat-card {
            flex: 1;
            background: linear-gradient(135deg, #007acc, #0056b3);
            color: white;
            padding: 20px;
            border-radius: 8px;
            text-align: center;
        }
        .stat-card.violation-card {
            background: linear-gradient(135deg, #d32f2f, #b71c1c);
        }
        .violations-heading {
            color: #d32f2f;
            border-bottom: 2px solid #d32f2f;
            padding-bottom: 8px;
            margin-top: 30px;
        }
        .violations-intro {
            color: #555;
            font-size: 0.95em;
        }
        .violations-table {
            width: 100%;
            border-collapse: collapse;
            margin: 12px 0 30px 0;
            background: #fff5f5;
            border: 1px solid #ffcdd2;
        }
        .violations-table th, .violations-table td {
            padding: 10px;
            text-align: left;
            border: 1px solid #ffcdd2;
        }
        .violations-table th {
            background: #d32f2f;
            color: white;
            font-weight: 600;
        }
        .license-badge.violation {
            background: #ffebee;
            color: #c62828;
            font-weight: 600;
        }
        .stat-card h3 {
            margin: 0;
            font-size: 2em;
        }
        .stat-card p {
            margin: 5px 0 0 0;
            font-size: 0.9em;
            opacity: 0.9;
        }
        .packages-table {
            width: 100%;
            border-collapse: collapse;
            margin: 20px 0;
        }
        .packages-table th, .packages-table td {
            padding: 12px;
            text-align: left;
            border: 1px solid #ddd;
        }
        .packages-table th {
            background: #007acc;
            color: white;
            font-weight: 600;
        }
        .packages-table tr:nth-child(even) {
            background: #f9f9f9;
        }
        .license-badge {
            background: #e3f2fd;
            color: #1976d2;
            padding: 4px 8px;
            border-radius: 4px;
            font-size: 0.85em;
            font-weight: 500;
        }
        .attribution-badge.required {
            background: #fff3e0;
            color: #f57c00;
            padding: 4px 8px;
            border-radius: 4px;
            font-size: 0.85em;
            font-weight: 500;
        }
        .attribution-badge.not-required {
            background: #e8f5e8;
            color: #388e3c;
            padding: 4px 8px;
            border-radius: 4px;
            font-size: 0.85em;
            font-weight: 500;
        }
        .saas-badge.compliant {
            background: #e8f5e8;
            color: #388e3c;
            padding: 4px 8px;
            border-radius: 4px;
            font-size: 0.85em;
            font-weight: 500;
        }
        .saas-badge.non-compliant {
            background: #ffebee;
            color: #d32f2f;
            padding: 4px 8px;
            border-radius: 4px;
            font-size: 0.85em;
            font-weight: 500;
        }
        .has-warnings {
            color: #d32f2f;
            font-size: 0.9em;
        }
        .no-warnings {
            color: #666;
            font-style: italic;
        }
        .source-badge {
            background: #e8f4fd;
            color: #1565c0;
            padding: 4px 8px;
            border-radius: 4px;
            font-size: 0.85em;
            font-weight: 500;
        }
        .projects-cell {
            font-size: 0.9em;
            color: #666;
            max-width: 200px;
            word-break: break-all;
        }";
    }
}