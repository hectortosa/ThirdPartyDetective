using ThirdPartyDetective.Cli.Models;

namespace ThirdPartyDetective.Cli.Services;

public interface ISbomClassifier
{
    Task<PackageAnalysisResult> ClassifyAsync(ScanOptions options, CancellationToken cancellationToken = default);
}

public interface ISpdxSbomReader
{
    IAsyncEnumerable<ThirdPartyInfo> ReadAsync(string sbomPath, CancellationToken cancellationToken = default);
}

public interface ISbomMerger
{
    Task<IReadOnlyList<ThirdPartyInfo>> MergeAsync(
        IReadOnlyList<string> sbomPaths,
        CancellationToken cancellationToken = default);
}

public interface IRegistryFallbackEnricher
{
    Task<IReadOnlyList<ThirdPartyInfo>> EnrichAsync(
        IReadOnlyList<ThirdPartyInfo> packages,
        ScanOptions options,
        CancellationToken cancellationToken = default);
}

public interface ILicenseLookup
{
    string Ecosystem { get; }
    Task<LicenseLookupResult?> LookupLicenseAsync(string name, string version, CancellationToken cancellationToken = default);
}

// Url is a license URL (shown as "License URL"). ProjectUrl is the package's
// repository or homepage: it may be probed for a LICENSE file, but is never
// presented as a license URL. LicenseText is the package's own license text
// (its license and NOTICE files) when the registry or the package archive has
// one; DetectedFrom says where the text that the expression was identified from
// came from. Note is what a reviewer should know about a declaration the lookup
// could not turn into an expression ("npm declares UNLICENSED").
public record LicenseLookupResult(
    string? Expression,
    string? Url,
    string? Authors = null,
    string? Copyright = null,
    string? ProjectUrl = null,
    string? LicenseText = null,
    string? DetectedFrom = null,
    string? Note = null);

public interface ISpdxLicenseExpressionAdapter
{
    LicenseExpressionInfo Normalize(string? rawSpdxExpression);
}

// SubExpressions are the distinct licenses in the expression as written
// ("GPL-2.0+", "MIT"); Exceptions are the distinct WITH exceptions.
public record LicenseExpressionInfo(
    string? Normalized,
    IReadOnlyList<string> SubExpressions,
    bool IsCompound,
    string? WithException,
    IReadOnlyList<string> Exceptions);

public interface ILicenseAnalyzer
{
    LicenseType ParseLicenseType(string? licenseExpression, string? licenseUrl);
    bool RequiresAttribution(LicenseType licenseType, ScanMode mode);
    bool IsSaasCompliant(LicenseType licenseType);
    bool IsPolicyViolation(LicenseType licenseType, ScanMode mode, PolicyConfig? policy = null);
    List<string> GetWarningFlags(ThirdPartyInfo package);
}

public interface IOutputGenerator
{
    Task GenerateOutputAsync(PackageAnalysisResult result, ScanOptions options, CancellationToken cancellationToken = default);
}
