# Third-Party Package Analysis Report

**Generated:** <timestamp> UTC
**Scan Path:** <tests>/Fixtures/python/trivy-requirements\.spdx\.json
**Mode:** Distribution

## Summary

- **Total Packages:** 14
- **Requiring Attribution:** 14
- **Warnings:** 7

### License Distribution

- **MIT:** 5 packages
- **Unknown:** 4 packages
- **Apache20:** 1 packages
- **BSD3Clause:** 1 packages
- **LGPL21:** 1 packages
- **MPL20:** 1 packages
- **Multiple:** 1 packages

## 🚫 Policy Violations (5)

Packages flagged as incompatible with mode `Distribution`. Review each before shipping.

| Package | Version | Ecosystem | Policy Class | License Expression | Source |
|---------|---------|-----------|--------------|--------------------|--------|
| docutils | 0\.22 | Python | Unknown | _(no license info)_ | None |
| packaging | 25\.0 | Python | Unknown | _(no license info)_ | None |
| psycopg2-binary | 2\.9\.10 | Python | Unknown | _(no license info)_ | None |
| python-dateutil | 2\.9\.0\.post0 | Python | Unknown | _(no license info)_ | None |
| regex | 2025\.9\.1 | Python | Multiple | Apache-2\.0 AND CNRI-Python | Registry |

## All Packages

| Package | Version | License | Policy Class | Attribution Required | Project URL | Warnings |
|---------|---------|---------|--------------|---------------------|-------------|----------|
| attrs | 25\.3\.0 | MIT | MIT | Yes | [link](https://github.com/python-attrs/attrs) | None |
| certifi | 2025\.8\.3 | MPL-2\.0 | MPL20 | Yes | [link](https://github.com/certifi/python-certifi) | None |
| chardet | 5\.2\.0 | LGPL-2\.0-or-later | LGPL21 | Yes | [link](https://github.com/chardet/chardet) | None |
| charset-normalizer | 3\.4\.3 | MIT | MIT | Yes | [link](https://github.com/jawah/charset_normalizer) | None |
| docutils | 0\.22 | not determined | Unknown | Yes | [link](https://docutils.sourceforge.io) | Unknown license - manual review required |
| idna | 3\.20 | BSD-3-Clause | BSD3Clause | Yes | [link](https://github.com/kjd/idna) | None |
| packaging | 25\.0 | not determined | Unknown | Yes | [link](https://github.com/pypa/packaging) | Unknown license - manual review required |
| psycopg2-binary | 2\.9\.10 | not determined | Unknown | Yes | [link](https://psycopg.org/) | PyPI declares the license as 'LGPL with exceptions', which names no single SPDX license, Unknown license - manual review required |
| python-dateutil | 2\.9\.0\.post0 | not determined | Unknown | Yes | [link](https://github.com/dateutil/dateutil) | PyPI declares the license as 'Dual License', which names no single SPDX license, Unknown license - manual review required |
| regex | 2025\.9\.1 | Apache-2\.0 AND CNRI-Python | Multiple | Yes | [link](https://github.com/mrabarnett/mrab-regex) | Multiple licenses - requires individual analysis |
| requests | 2\.34\.2 | Apache-2\.0 | Apache20 | Yes | [link](https://github.com/psf/requests) | None |
| six | 1\.17\.0 | MIT | MIT | Yes | [link](https://github.com/benjaminp/six) | None |
| typing\_extensions | 4\.15\.0 | PSF-2\.0 | MIT | Yes | [link](https://github.com/python/typing_extensions) | None |
| urllib3 | 2\.8\.0 | MIT | MIT | Yes | [link](https://github.com/urllib3/urllib3) | None |
