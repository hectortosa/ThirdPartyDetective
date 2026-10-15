namespace ThirdPartyDetective.Cli.Services.Registry;

// Turns the license a registry declares (npm "license", PyPI "license" and
// classifiers) into a canonical SPDX expression, or null when the value does
// not name one license unambiguously. A null leaves the package to the license
// file in its archive, or to a human: "BSD" or "Apache Software License" could
// be one of several licenses, and a wrong identity ends up in a customer-facing
// notice.
public static class DeclaredLicense
{
    // Free-text names that identify exactly one SPDX license.
    private static readonly Dictionary<string, string> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["MIT License"] = "MIT",
        ["The MIT License"] = "MIT",
        ["The MIT License (MIT)"] = "MIT",
        ["Apache 2"] = "Apache-2.0",
        ["Apache 2.0"] = "Apache-2.0",
        ["Apache-2"] = "Apache-2.0",
        ["Apache License 2.0"] = "Apache-2.0",
        ["Apache License, Version 2.0"] = "Apache-2.0",
        ["Apache License Version 2.0"] = "Apache-2.0",
        ["Apache Software License 2.0"] = "Apache-2.0",
        ["ASL 2.0"] = "Apache-2.0",
        ["BSD 2-Clause"] = "BSD-2-Clause",
        ["BSD 2-Clause License"] = "BSD-2-Clause",
        ["2-Clause BSD License"] = "BSD-2-Clause",
        ["Simplified BSD License"] = "BSD-2-Clause",
        ["BSD 3-Clause"] = "BSD-3-Clause",
        ["BSD 3-Clause License"] = "BSD-3-Clause",
        ["3-Clause BSD License"] = "BSD-3-Clause",
        ["New BSD License"] = "BSD-3-Clause",
        ["Modified BSD License"] = "BSD-3-Clause",
        ["ISC License"] = "ISC",
        ["Mozilla Public License 2.0"] = "MPL-2.0",
        ["MPL 2.0"] = "MPL-2.0",
        ["The Unlicense"] = "Unlicense",
        ["CC0 1.0 Universal"] = "CC0-1.0",
        ["zlib License"] = "Zlib",
        ["Boost Software License 1.0"] = "BSL-1.0",
        ["Eclipse Public License 2.0"] = "EPL-2.0",
    };

    // PyPI "License ::" classifiers that name one license. "BSD License",
    // "Apache Software License", "GNU General Public License (GPL)", "GNU Library
    // or Lesser General Public License (LGPL)", "Python Software Foundation
    // License" and "GNU Lesser General Public License v2 (LGPLv2)" (there is no
    // Lesser GPL 2.0) leave the version or the variant open, so they are absent.
    private static readonly Dictionary<string, string> Classifiers = new(StringComparer.Ordinal)
    {
        ["License :: OSI Approved :: MIT License"] = "MIT",
        ["License :: OSI Approved :: MIT No Attribution License (MIT-0)"] = "MIT-0",
        ["License :: OSI Approved :: ISC License (ISCL)"] = "ISC",
        ["License :: OSI Approved :: Mozilla Public License 1.1 (MPL 1.1)"] = "MPL-1.1",
        ["License :: OSI Approved :: Mozilla Public License 2.0 (MPL 2.0)"] = "MPL-2.0",
        ["License :: OSI Approved :: GNU General Public License v2 (GPLv2)"] = "GPL-2.0-only",
        ["License :: OSI Approved :: GNU General Public License v2 or later (GPLv2+)"] = "GPL-2.0-or-later",
        ["License :: OSI Approved :: GNU General Public License v3 (GPLv3)"] = "GPL-3.0-only",
        ["License :: OSI Approved :: GNU General Public License v3 or later (GPLv3+)"] = "GPL-3.0-or-later",
        ["License :: OSI Approved :: GNU Lesser General Public License v2 or later (LGPLv2+)"] = "LGPL-2.0-or-later",
        ["License :: OSI Approved :: GNU Lesser General Public License v3 (LGPLv3)"] = "LGPL-3.0-only",
        ["License :: OSI Approved :: GNU Lesser General Public License v3 or later (LGPLv3+)"] = "LGPL-3.0-or-later",
        ["License :: OSI Approved :: GNU Affero General Public License v3"] = "AGPL-3.0-only",
        ["License :: OSI Approved :: GNU Affero General Public License v3 or later (AGPLv3+)"] = "AGPL-3.0-or-later",
        ["License :: OSI Approved :: Eclipse Public License 1.0 (EPL-1.0)"] = "EPL-1.0",
        ["License :: OSI Approved :: Eclipse Public License 2.0 (EPL-2.0)"] = "EPL-2.0",
        ["License :: OSI Approved :: European Union Public Licence 1.1 (EUPL 1.1)"] = "EUPL-1.1",
        ["License :: OSI Approved :: European Union Public Licence 1.2 (EUPL 1.2)"] = "EUPL-1.2",
        ["License :: OSI Approved :: Boost Software License 1.0 (BSL-1.0)"] = "BSL-1.0",
        ["License :: OSI Approved :: Universal Permissive License (UPL)"] = "UPL-1.0",
        ["License :: OSI Approved :: The Unlicense (Unlicense)"] = "Unlicense",
        ["License :: CC0 1.0 Universal (CC0 1.0) Public Domain Dedication"] = "CC0-1.0",
    };

    // Every license ID the tables map to, for checking against the SPDX list.
    internal static IEnumerable<string> MappedIds => Aliases.Values.Concat(Classifiers.Values).Distinct(StringComparer.Ordinal);

    // A canonical SPDX expression for the declared value: a valid expression whose
    // license and exception IDs are all on the SPDX list (or LicenseRefs), or an
    // unambiguous free-text name. Null otherwise.
    public static string? Normalize(string? value, ISpdxLicenseList licenseList)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var trimmed = value.Trim();

        if (SpdxExpression.TryParse(trimmed) is { } parsed && Canonicalize(parsed, licenseList) is { } canonical)
            return canonical.ToString();

        return Aliases.GetValueOrDefault(trimmed);
    }

    // The license the PyPI classifiers declare, when exactly one license
    // classifier is present and it names one license. Several classifiers don't
    // say whether the licenses apply together or as alternatives.
    public static string? FromClassifiers(IEnumerable<string> classifiers)
    {
        var licenses = classifiers
            .Where(c => c.StartsWith("License ::", StringComparison.Ordinal) && c != "License :: OSI Approved")
            .Distinct(StringComparer.Ordinal)
            .ToList();
        return licenses.Count == 1 ? Classifiers.GetValueOrDefault(licenses[0]) : null;
    }

    // npm's "SEE LICENSE IN <file>": the license is the named file in the package.
    public static string? SeeLicenseInFile(string? value)
    {
        const string prefix = "SEE LICENSE IN ";
        if (value == null || !value.Trim().StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return null;
        var file = value.Trim()[prefix.Length..].Trim().TrimStart('.', '/');
        return file.Length == 0 || file.Contains("..", StringComparison.Ordinal) ? null : file;
    }

    private static SpdxExpression? Canonicalize(SpdxExpression expression, ISpdxLicenseList licenseList)
    {
        switch (expression)
        {
            case SpdxLicenseTerm term:
                string? id = term.IsLicenseRef ? term.LicenseId
                    : licenseList.TryGetCanonicalId(term.LicenseId, out var canonicalId) ? canonicalId : null;
                if (id == null) return null;
                string? exception = null;
                if (term.Exception != null && !licenseList.TryGetCanonicalId(term.Exception, out exception))
                    return null;
                return term with { LicenseId = id, Exception = exception };
            case SpdxAnd all:
                var andOperands = all.Operands.Select(o => Canonicalize(o, licenseList)).ToList();
                return andOperands.Any(o => o == null) ? null : new SpdxAnd(andOperands!);
            case SpdxOr any:
                var orOperands = any.Operands.Select(o => Canonicalize(o, licenseList)).ToList();
                return orOperands.Any(o => o == null) ? null : new SpdxOr(orOperands!);
            default:
                return null;
        }
    }
}
