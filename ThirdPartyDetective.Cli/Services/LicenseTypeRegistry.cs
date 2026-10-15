using ThirdPartyDetective.Cli.Models;

namespace ThirdPartyDetective.Cli.Services;

// Single source of truth for mapping SPDX IDs and common free-text license names
// to internal LicenseType values. Used by both LicenseAnalyzer (parsing SBOM
// expressions) and SpdxOrNameLicenseTypeConverter (parsing user policy files).
public static class LicenseTypeRegistry
{
    public static IReadOnlyDictionary<string, LicenseType> Mapping { get; } =
        new Dictionary<string, LicenseType>(StringComparer.OrdinalIgnoreCase)
        {
            { "MIT", LicenseType.MIT },
            { "Apache-2.0", LicenseType.Apache20 },
            { "BSD-2-Clause", LicenseType.BSD2Clause },
            { "BSD-3-Clause", LicenseType.BSD3Clause },
            { "GPL-2.0", LicenseType.GPL20 },
            { "GPL-2.0-only", LicenseType.GPL20 },
            { "GPL-2.0-or-later", LicenseType.GPL20 },
            { "GPL-3.0", LicenseType.GPL30 },
            { "GPL-3.0-only", LicenseType.GPL30 },
            { "GPL-3.0-or-later", LicenseType.GPL30 },
            // The Library GPL 2.0 carries the same weak-copyleft obligations as 2.1.
            { "LGPL-2.0", LicenseType.LGPL21 },
            { "LGPL-2.0-only", LicenseType.LGPL21 },
            { "LGPL-2.0-or-later", LicenseType.LGPL21 },
            { "LGPL-2.1", LicenseType.LGPL21 },
            { "LGPL-2.1-only", LicenseType.LGPL21 },
            { "LGPL-2.1-or-later", LicenseType.LGPL21 },
            { "LGPL-3.0", LicenseType.LGPL30 },
            { "LGPL-3.0-only", LicenseType.LGPL30 },
            { "LGPL-3.0-or-later", LicenseType.LGPL30 },
            { "AGPL-3.0", LicenseType.AGPL30 },
            { "AGPL-3.0-only", LicenseType.AGPL30 },
            { "AGPL-3.0-or-later", LicenseType.AGPL30 },
            { "MPL-2.0", LicenseType.MPL20 },
            { "EPL-2.0", LicenseType.EPL20 },
            { "ISC", LicenseType.ISC },
            { "Unlicense", LicenseType.Unlicense },
            { "CC0-1.0", LicenseType.CC0 },
            { "0BSD", LicenseType.BSD2Clause },
            { "BSD-Zero-Clause", LicenseType.BSD2Clause },
            { "BlueOak-1.0.0", LicenseType.MIT },

            // Common free-text aliases (PyPI, npm legacy, manual entries).
            { "MIT License", LicenseType.MIT },
            { "MIT-0", LicenseType.MIT },
            { "MIT-CMU", LicenseType.MIT },
            { "Apache License, Version 2.0", LicenseType.Apache20 },
            { "Apache License 2.0", LicenseType.Apache20 },
            { "Apache Software License", LicenseType.Apache20 },
            { "Apache 2.0", LicenseType.Apache20 },
            { "BSD", LicenseType.BSD3Clause },
            { "BSD License", LicenseType.BSD3Clause },
            { "BSD 3-Clause License", LicenseType.BSD3Clause },
            { "BSD-3-Clause License", LicenseType.BSD3Clause },
            { "Modified BSD License", LicenseType.BSD3Clause },
            { "BSD 2-Clause License", LicenseType.BSD2Clause },
            { "BSD-2-Clause License", LicenseType.BSD2Clause },
            { "ISC License", LicenseType.ISC },
            { "ISC License (ISCL)", LicenseType.ISC },
            // Python Software Foundation License — permissive, BSD-style.
            { "PSF", LicenseType.MIT },
            { "PSF-2.0", LicenseType.MIT },
            { "PSFL", LicenseType.MIT },
            { "Python Software Foundation License", LicenseType.MIT },
            // PostgreSQL License — permissive, BSD-style.
            { "PostgreSQL", LicenseType.MIT },
            // Microsoft Public License — permissive enough for SaaS/Distribution.
            { "MS-PL", LicenseType.MIT },
            { "Microsoft Public License", LicenseType.MIT },
            // Bare "GPL" version unspecified — conservatively treat as GPL-3.0 (newer / stricter).
            { "GPL", LicenseType.GPL30 }
        };

    public static bool TryMap(string identifier, out LicenseType type) =>
        Mapping.TryGetValue(identifier, out type);
}
