# Third-Party Package Analysis Report

**Generated:** <timestamp> UTC
**Scan Path:** <tests>/Fixtures/spdx/license-edge-cases\.spdx\.json
**Mode:** Distribution

## Summary

- **Total Packages:** 30
- **Requiring Attribution:** 29
- **Warnings:** 21

### Document Warnings

- ⚠️ 26 of 29 attribution-required packages have no copyright/author metadata\. This is typical for Syft/Trivy SBOMs\. Run with --registry-fallback to fetch from package registries, or populate \.tpd-overrides\.json for packages requiring legal review\.

### License Distribution

- **MIT:** 10 packages
- **Unknown:** 6 packages
- **Multiple:** 5 packages
- **GPL30:** 2 packages
- **AGPL30:** 1 packages
- **Apache20:** 1 packages
- **BSD2Clause:** 1 packages
- **GPL20:** 1 packages
- **LGPL21:** 1 packages
- **MPL20:** 1 packages
- **Unlicense:** 1 packages

## 🚫 Policy Violations (12)

Packages flagged as incompatible with mode `Distribution`. Review each before shipping.

| Package | Version | Ecosystem | Policy Class | License Expression | Source |
|---------|---------|-----------|--------------|--------------------|--------|
| Agpl\.Package | 1\.0\.0 | NuGet | AGPL30 | AGPL-3\.0-only | Spdx |
| Boost\.Package | 1\.0\.0 | NuGet | Unknown | BSL-1\.0 | Spdx |
| Combined\.AndLicensed | 1\.0\.0 | NuGet | Multiple | MIT AND BSD-3-Clause | Spdx |
| Dual\.OrLicensed | 1\.0\.0 | NuGet | Multiple | MIT OR Apache-2\.0 | Spdx |
| Files\.Only | 1\.0\.0 | NuGet | Multiple | MIT AND GPL-3\.0-only | Spdx |
| Lowercase\.Operators | 1\.0\.0 | NuGet | Multiple | mit OR apache-2\.0 | Spdx |
| Made\.Up\.License | 1\.0\.0 | NuGet | Unknown | Made-Up-License-1\.0 | Spdx |
| Nested\.Expression | 1\.0\.0 | NuGet | Multiple | (MIT OR Apache-2\.0) AND BSD-2-Clause | Spdx |
| No\.License | 1\.0\.0 | NuGet | Unknown | _(no license info)_ | None |
| None\.License | 1\.0\.0 | NuGet | Unknown | _(no license info)_ | None |
| Vendor\.Proprietary | 1\.0\.0 | NuGet | Unknown | LicenseRef-Vendor-EULA | Spdx |
| left-pad | 1\.0\.0 | npm | Unknown | WTFPL | Spdx |

## All Packages

| Package | Version | License | Policy Class | Attribution Required | Project URL | Warnings |
|---------|---------|---------|--------------|---------------------|-------------|----------|
| Aardvark\.Package | 1\.0\.0 | MIT | MIT | Yes | — | None |
| Agpl\.Package | 1\.0\.0 | AGPL-3\.0-only | AGPL30 | Yes | — | AGPL license detected - requires source code disclosure for network use |
| alpha\.Package | 1\.0\.0 | MIT | MIT | Yes | — | None |
| Apache\.WithLlvm | 1\.0\.0 | Apache-2\.0 WITH LLVM-exception | Apache20 | Yes | — | License has exception clause\: WITH LLVM-exception; verify exception scope |
| App\.Purpose\.Dependency | 1\.0\.0 | GPL-3\.0-only | GPL30 | Yes | — | GPL license detected - may require source code disclosure |
| BlueOak\.Package | 1\.0\.0 | BlueOak-1\.0\.0 | MIT | Yes | — | None |
| Boost\.Package | 1\.0\.0 | BSL-1\.0 | Unknown | Yes | — | Unknown license - manual review required |
| Combined\.AndLicensed | 1\.0\.0 | MIT AND BSD-3-Clause | Multiple | Yes | — | Multiple licenses - requires individual analysis |
| Dual\.OrLicensed | 1\.0\.0 | MIT OR Apache-2\.0 | Multiple | Yes | — | Multiple licenses - requires individual analysis |
| Files\.Only | 1\.0\.0 | MIT AND GPL-3\.0-only | Multiple | Yes | — | Multiple licenses - requires individual analysis, Compound license includes GPL (GPL-3\.0-only) - may require source code disclosure |
| Gpl\.WithClasspath | 1\.0\.0 | GPL-2\.0-only WITH Classpath-exception-2\.0 | GPL20 | Yes | [link](https://example.invalid/gpl-classpath) | License has exception clause\: WITH Classpath-exception-2\.0; verify exception scope, GPL license detected - may require source code disclosure |
| Gpl3\.OrLater | 1\.0\.0 | GPL-3\.0-or-later | GPL30 | Yes | [link](https://example.invalid/gpl3) | GPL license detected - may require source code disclosure |
| left-pad | 1\.0\.0 | WTFPL | Unknown | Yes | — | Unknown license - manual review required |
| Lgpl\.Package | 1\.0\.0 | LGPL-2\.1-only | LGPL21 | Yes | [link](https://example.invalid/lgpl) | None |
| Lowercase\.Operators | 1\.0\.0 | mit OR apache-2\.0 | Multiple | Yes | — | Multiple licenses - requires individual analysis |
| Made\.Up\.License | 1\.0\.0 | Made-Up-License-1\.0 | Unknown | Yes | — | Unknown license - manual review required |
| MitZero\.Package | 1\.0\.0 | MIT-0 | MIT | Yes | — | None |
| Mpl\.Package | 1\.0\.0 | MPL-2\.0 | MPL20 | Yes | — | None |
| MsPl\.Package | 1\.0\.0 | MS-PL | MIT | Yes | — | None |
| Nested\.Expression | 1\.0\.0 | (MIT OR Apache-2\.0) AND BSD-2-Clause | Multiple | Yes | — | Multiple licenses - requires individual analysis |
| No\.License | 1\.0\.0 | not determined | Unknown | Yes | — | Unknown license - manual review required, No license information available |
| None\.License | 1\.0\.0 | not determined | Unknown | Yes | — | Unknown license - manual review required, No license information available |
| Plain\.Mit | 1\.0\.0 | MIT | MIT | Yes | — | None |
| Postgres\.Package | 1\.0\.0 | PostgreSQL | MIT | Yes | — | None |
| Psf\.Package | 1\.0\.0 | PSF-2\.0 | MIT | Yes | — | None |
| Unlicense\.Package | 1\.0\.0 | Unlicense | Unlicense | No | — | None |
| Vendor\.Proprietary | 1\.0\.0 | LicenseRef-Vendor-EULA | Unknown | Yes | — | Unknown license - manual review required |
| ZeroBsd\.Package | 1\.0\.0 | 0BSD | BSD2Clause | Yes | — | None |
| \_underscore\.Package | 1\.0\.0 | MIT | MIT | Yes | — | None |
| Ångström\.Package | 1\.0\.0 | MIT | MIT | Yes | — | None |
