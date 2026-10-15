using Microsoft.Extensions.Logging;
using ThirdPartyDetective.Cli.Models;

namespace ThirdPartyDetective.Cli.Services;

public class SbomMerger : ISbomMerger
{
    private readonly ISpdxSbomReader _reader;
    private readonly ILogger<SbomMerger> _logger;

    public SbomMerger(ISpdxSbomReader reader, ILogger<SbomMerger> logger)
    {
        _reader = reader;
        _logger = logger;
    }

    public async Task<IReadOnlyList<ThirdPartyInfo>> MergeAsync(
        IReadOnlyList<string> sbomPaths,
        CancellationToken cancellationToken = default)
    {
        if (sbomPaths.Count == 0)
            throw new ArgumentException("At least one SBOM path is required.", nameof(sbomPaths));

        var merged = new Dictionary<(string Name, string Version), ThirdPartyInfo>();

        for (int sourceIndex = 0; sourceIndex < sbomPaths.Count; sourceIndex++)
        {
            var path = sbomPaths[sourceIndex];
            var sourceLabel = $"source[{sourceIndex}]:{Path.GetFileName(path)}";
            int packagesFromSource = 0;

            await foreach (var pkg in _reader.ReadAsync(path, cancellationToken))
            {
                cancellationToken.ThrowIfCancellationRequested();
                packagesFromSource++;

                var key = MakeKey(pkg);
                if (merged.TryGetValue(key, out var existing))
                {
                    MergeInto(existing, pkg, sourceLabel);
                }
                else
                {
                    if (!string.IsNullOrEmpty(pkg.LicenseExpression))
                        pkg.LicenseSource = LicenseSource.Spdx;
                    merged[key] = pkg;
                }
            }

            _logger.LogInformation(
                "Merged {Count} packages from {Source} (running total: {Total})",
                packagesFromSource, sourceLabel, merged.Count);
        }

        return merged.Values.ToList();
    }

    private static (string, string) MakeKey(ThirdPartyInfo pkg) =>
        (pkg.Name.ToLowerInvariant(), pkg.Version);

    private static void MergeInto(ThirdPartyInfo target, ThirdPartyInfo source, string sourceLabel)
    {
        if (string.IsNullOrEmpty(target.LicenseExpression) && !string.IsNullOrEmpty(source.LicenseExpression))
        {
            target.LicenseExpression = source.LicenseExpression;
            target.LicenseSubExpressions = source.LicenseSubExpressions;
            target.LicenseExceptions = source.LicenseExceptions;
            target.ExtractedLicenses = source.ExtractedLicenses;
            target.LicenseSource = LicenseSource.Spdx;
        }
        else if (!string.IsNullOrEmpty(target.LicenseExpression)
            && !string.IsNullOrEmpty(source.LicenseExpression)
            && !string.Equals(target.LicenseExpression, source.LicenseExpression, StringComparison.OrdinalIgnoreCase))
        {
            target.WarningFlags.Add(
                $"SBOM source disagreement on license: kept '{target.LicenseExpression}', dropped '{source.LicenseExpression}' from {sourceLabel}");
        }

        if (string.IsNullOrEmpty(target.Description) && !string.IsNullOrEmpty(source.Description))
            target.Description = source.Description;
        if (string.IsNullOrEmpty(target.Authors) && !string.IsNullOrEmpty(source.Authors))
            target.Authors = source.Authors;
        if (string.IsNullOrEmpty(target.Copyright) && !string.IsNullOrEmpty(source.Copyright))
            target.Copyright = source.Copyright;
        if (string.IsNullOrEmpty(target.ProjectUrl) && !string.IsNullOrEmpty(source.ProjectUrl))
            target.ProjectUrl = source.ProjectUrl;
        if (string.IsNullOrEmpty(target.LicenseUrl) && !string.IsNullOrEmpty(source.LicenseUrl))
            target.LicenseUrl = source.LicenseUrl;

        if (IsUnspecified(target.SourceEcosystem) && !IsUnspecified(source.SourceEcosystem))
            target.SourceEcosystem = source.SourceEcosystem;

        foreach (var sp in source.SourceProjects)
            if (!target.SourceProjects.Contains(sp))
                target.SourceProjects.Add(sp);

        foreach (var w in source.WarningFlags)
            if (!target.WarningFlags.Contains(w))
                target.WarningFlags.Add(w);
    }

    private static bool IsUnspecified(string? value) =>
        string.IsNullOrEmpty(value) || string.Equals(value, "Unknown", StringComparison.OrdinalIgnoreCase);
}
