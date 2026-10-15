using Microsoft.Extensions.Logging;
using ThirdPartyDetective.Cli.Models;

namespace ThirdPartyDetective.Cli.Services;

public interface IOverrideApplier
{
    int Apply(IEnumerable<ThirdPartyInfo> packages, OverrideConfig config);

    // Packages whose override sets the license and which then have a copyright
    // notice (the override's or the SBOM's): --registry-fallback has nothing
    // left to fill for them.
    IReadOnlySet<ThirdPartyInfo> FullyDecided(IEnumerable<ThirdPartyInfo> packages, OverrideConfig config);
}

public class OverrideApplier : IOverrideApplier
{
    private readonly ISpdxLicenseExpressionAdapter _expressionAdapter;
    private readonly ILogger<OverrideApplier> _logger;

    public OverrideApplier(ISpdxLicenseExpressionAdapter expressionAdapter, ILogger<OverrideApplier> logger)
    {
        _expressionAdapter = expressionAdapter;
        _logger = logger;
    }

    public int Apply(IEnumerable<ThirdPartyInfo> packages, OverrideConfig config)
    {
        if (config.Overrides.Count == 0) return 0;

        var index = Index(config);

        int applied = 0;
        foreach (var pkg in packages)
        {
            var key = Key(pkg.Name, pkg.Version);
            if (!index.TryGetValue(key, out var entry)) continue;

            if (!string.IsNullOrWhiteSpace(entry.License))
            {
                // Same normalization as SBOM and registry values, so "GPL-2.0+" or
                // "(MIT)" in an override classify and print like they would anywhere else.
                var info = _expressionAdapter.Normalize(entry.License);
                pkg.LicenseExpression = info.Normalized;
                pkg.LicenseSubExpressions = info.SubExpressions.ToList();
                pkg.LicenseExceptions = info.Exceptions.ToList();
                pkg.ExtractedLicenses.RemoveAll(e => !info.SubExpressions.Contains(e.LicenseId, StringComparer.Ordinal));
                pkg.WarningFlags.RemoveAll(w => w.StartsWith(ExceptionWarningPrefix, StringComparison.Ordinal));
                pkg.LicenseSource = LicenseSource.Override;
            }
            if (entry.LicenseText != null)
                pkg.LicenseText = entry.LicenseText;
            if (!string.IsNullOrWhiteSpace(entry.Authors))
                pkg.Authors = entry.Authors;
            if (!string.IsNullOrWhiteSpace(entry.Copyright))
                pkg.Copyright = entry.Copyright;

            var reviewer = string.IsNullOrWhiteSpace(entry.Reviewer) ? "(no reviewer)" : entry.Reviewer;
            pkg.WarningFlags.Add($"License resolved via override (reviewer: {reviewer})");

            _logger.LogInformation(
                "Applied override for {Name}@{Version}: license={License}, reviewer={Reviewer}",
                OutputText.Line(pkg.Name), OutputText.Line(pkg.Version), OutputText.Line(entry.License ?? "(unchanged)"), OutputText.Line(reviewer));
            applied++;
        }

        return applied;
    }

    public IReadOnlySet<ThirdPartyInfo> FullyDecided(IEnumerable<ThirdPartyInfo> packages, OverrideConfig config)
    {
        var decided = new HashSet<ThirdPartyInfo>(ReferenceEqualityComparer.Instance);
        if (config.Overrides.Count == 0) return decided;

        var index = Index(config);
        foreach (var pkg in packages)
        {
            if (index.TryGetValue(Key(pkg.Name, pkg.Version), out var entry) &&
                !string.IsNullOrWhiteSpace(entry.License) &&
                (!string.IsNullOrWhiteSpace(entry.Copyright) || !string.IsNullOrEmpty(pkg.Copyright)))
                decided.Add(pkg);
        }
        return decided;
    }

    // Exact (name, version) match — case-insensitive on name. Version bumps invalidate
    // the override, which is the self-correcting property: a re-reviewed package gets
    // a new entry, a stale entry harmlessly stops matching once the version changes.
    private static Dictionary<string, OverrideEntry> Index(OverrideConfig config) =>
        config.Overrides.ToDictionary(e => Key(e.Name, e.Version), e => e, StringComparer.Ordinal);

    // Must match the prefix SpdxSbomReader uses for WITH-exception warnings.
    private const string ExceptionWarningPrefix = "License has exception clause:";

    private static string Key(string name, string version) =>
        $"{name.ToLowerInvariant()}|{version}";
}
