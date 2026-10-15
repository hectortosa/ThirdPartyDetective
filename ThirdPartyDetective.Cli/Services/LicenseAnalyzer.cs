using Microsoft.Extensions.Logging;
using System.Text.RegularExpressions;
using ThirdPartyDetective.Cli.Models;

namespace ThirdPartyDetective.Cli.Services;

public class LicenseAnalyzer : ILicenseAnalyzer
{
    private readonly ILogger<LicenseAnalyzer> _logger;

    private static readonly HashSet<LicenseType> DistributionViolations = new()
    {
        LicenseType.AGPL30,
        LicenseType.Proprietary,
        LicenseType.Unknown,
        LicenseType.Multiple
    };

    private static readonly Dictionary<LicenseType, bool> SaasComplianceLookup = new()
    {
        { LicenseType.MIT, true },
        { LicenseType.Apache20, true },
        { LicenseType.BSD2Clause, true },
        { LicenseType.BSD3Clause, true },
        { LicenseType.ISC, true },
        { LicenseType.Unlicense, true },
        { LicenseType.CC0, true },
        { LicenseType.MPL20, true }, // Generally safe for SaaS
        { LicenseType.EPL20, true }, // Generally safe for SaaS
        { LicenseType.LGPL21, true }, // Generally safe for SaaS
        { LicenseType.LGPL30, true }, // Generally safe for SaaS
        { LicenseType.GPL20, false }, // Copyleft - risky for SaaS
        { LicenseType.GPL30, false }, // Copyleft - risky for SaaS
        { LicenseType.AGPL30, false }, // Network copyleft - definitely not safe for SaaS
        { LicenseType.Proprietary, false },
        { LicenseType.Unknown, false },
        { LicenseType.Multiple, false } // Needs case-by-case analysis
    };

    public LicenseAnalyzer(ILogger<LicenseAnalyzer> logger)
    {
        _logger = logger;
    }

    // Maps a license expression to the coarse LicenseType bucket used for policy
    // decisions. The bucket is never used to name the license in reports.
    public LicenseType ParseLicenseType(string? licenseExpression, string? licenseUrl)
    {
        if (!string.IsNullOrWhiteSpace(licenseExpression))
        {
            if (SpdxExpression.TryParse(licenseExpression) is { } parsed)
            {
                if (parsed.IsCompound)
                    return LicenseType.Multiple;
                if (TryMapLicenseId(((SpdxLicenseTerm)parsed).LicenseId, out var termType))
                    return termType;
            }

            // Free text from registries and hand-written overrides ("Apache License 2.0").
            var normalizedExpression = NormalizeLicenseExpression(licenseExpression);
            if (normalizedExpression.Contains(" OR ", StringComparison.OrdinalIgnoreCase) ||
                normalizedExpression.Contains(" AND ", StringComparison.OrdinalIgnoreCase))
                return LicenseType.Multiple;

            if (LicenseTypeRegistry.TryMap(normalizedExpression, out var licenseType))
                return licenseType;
        }

        // Only URLs that name the license exactly are used; see KnownLicenseUrls.
        if (KnownLicenseUrls.TryGetExpression(licenseUrl, out var fromUrl))
            return ParseLicenseType(fromUrl, null);

        return LicenseType.Unknown;
    }

    // SPDX IDs map directly; "-only"/"-or-later" variants fall back to the base
    // ID for licenses the registry only knows in their bare form.
    private static bool TryMapLicenseId(string licenseId, out LicenseType type)
    {
        if (LicenseTypeRegistry.TryMap(licenseId, out type)) return true;

        foreach (var suffix in new[] { "-only", "-or-later" })
        {
            if (licenseId.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) &&
                LicenseTypeRegistry.TryMap(licenseId[..^suffix.Length], out type))
                return true;
        }
        return false;
    }

    public bool RequiresAttribution(LicenseType licenseType, ScanMode mode)
    {
        return licenseType switch
        {
            LicenseType.MIT => true,
            LicenseType.Apache20 => true,
            LicenseType.BSD2Clause => true,
            LicenseType.BSD3Clause => true,
            LicenseType.ISC => true,
            LicenseType.MPL20 => mode == ScanMode.Distribution,
            LicenseType.EPL20 => mode == ScanMode.Distribution,
            LicenseType.LGPL21 => mode == ScanMode.Distribution,
            LicenseType.LGPL30 => mode == ScanMode.Distribution,
            LicenseType.GPL20 => true,
            LicenseType.GPL30 => true,
            LicenseType.AGPL30 => true, // Always requires attribution
            LicenseType.Unlicense => false,
            LicenseType.CC0 => false,
            LicenseType.Proprietary => false, // Typically no attribution required
            LicenseType.Multiple => true, // Conservative approach
            _ => true // Conservative default
        };
    }

    public bool IsSaasCompliant(LicenseType licenseType)
    {
        return SaasComplianceLookup.GetValueOrDefault(licenseType, false);
    }

    public bool IsPolicyViolation(LicenseType licenseType, ScanMode mode, PolicyConfig? policy = null)
    {
        if (mode == ScanMode.All) return false;

        var modePolicy = policy?.ResolveMode(mode);
        if (modePolicy != null)
        {
            if (modePolicy.Allowed != null) return !modePolicy.Allowed.Contains(licenseType);
            if (modePolicy.Violations != null) return modePolicy.Violations.Contains(licenseType);
        }

        // Fall back to built-in defaults — preserves v3.x behavior when no policy file is supplied.
        return mode switch
        {
            ScanMode.Saas => !IsSaasCompliant(licenseType),
            ScanMode.Distribution => DistributionViolations.Contains(licenseType),
            _ => false
        };
    }

    public List<string> GetWarningFlags(ThirdPartyInfo package)
    {
        var warnings = new List<string>();

        // License-specific warnings
        switch (package.LicenseType)
        {
            case LicenseType.GPL20:
            case LicenseType.GPL30:
                warnings.Add("GPL license detected - may require source code disclosure");
                break;

            case LicenseType.AGPL30:
                warnings.Add("AGPL license detected - requires source code disclosure for network use");
                break;

            case LicenseType.Unknown:
                warnings.Add("Unknown license - manual review required");
                break;

            case LicenseType.Multiple:
                warnings.Add("Multiple licenses - requires individual analysis");
                foreach (var sub in package.LicenseSubExpressions)
                {
                    var subType = ParseLicenseType(sub, null);
                    switch (subType)
                    {
                        case LicenseType.GPL20:
                        case LicenseType.GPL30:
                            warnings.Add($"Compound license includes GPL ({sub}) - may require source code disclosure");
                            break;
                        case LicenseType.AGPL30:
                            warnings.Add($"Compound license includes AGPL ({sub}) - network use requires source disclosure");
                            break;
                        case LicenseType.Proprietary:
                            warnings.Add($"Compound license includes proprietary clause ({sub}) - verify usage terms");
                            break;
                    }
                }
                break;

            case LicenseType.Proprietary:
                warnings.Add("Proprietary license - verify usage terms");
                break;
        }

        // Missing license information
        if (string.IsNullOrEmpty(package.LicenseText) &&
            string.IsNullOrEmpty(package.LicenseUrl) &&
            string.IsNullOrEmpty(package.LicenseExpression))
        {
            warnings.Add("No license information available");
        }

        // Large package without clear licensing
        if (package.LicenseType == LicenseType.Unknown &&
            !string.IsNullOrEmpty(package.Description) &&
            package.Description.Length > 100)
        {
            warnings.Add("Substantial package with unclear licensing");
        }

        return warnings;
    }

    private static string NormalizeLicenseExpression(string licenseExpression)
    {
        // Remove common prefixes/suffixes and normalize spacing
        var normalized = licenseExpression.Trim();

        // Handle common variations
        // \b word-boundary anchors are required: without them, "GPL[-\s]*2" would match
        // inside "LGPL-2.1" or "AGPL-3.0" and corrupt the string before the LGPL/AGPL
        // patterns get a chance to match. LGPL and AGPL patterns also use \b for
        // symmetry and future-proofing.
        var patterns = new Dictionary<string, string>
        {
            { @"\bApache[-\s]*2(\.0)?", "Apache-2.0" },
            { @"\bBSD[-\s]*3[-\s]*Clause", "BSD-3-Clause" },
            { @"\bBSD[-\s]*2[-\s]*Clause", "BSD-2-Clause" },
            { @"\bAGPL[-\s]*3(\.0)?", "AGPL-3.0" },
            { @"\bLGPL[-\s]*2\.1", "LGPL-2.1" },
            { @"\bLGPL[-\s]*3(\.0)?", "LGPL-3.0" },
            { @"\bGPL[-\s]*2(\.0)?", "GPL-2.0" },
            { @"\bGPL[-\s]*3(\.0)?", "GPL-3.0" },
            { @"\bMPL[-\s]*2(\.0)?", "MPL-2.0" }
        };

        foreach (var pattern in patterns)
        {
            normalized = Regex.Replace(normalized, pattern.Key, pattern.Value, RegexOptions.IgnoreCase);
        }

        return normalized;
    }
}