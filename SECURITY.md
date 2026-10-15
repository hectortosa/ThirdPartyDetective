# Security policy

## Supported versions

Security fixes go into a new release of the latest version. The executables bundle the .NET runtime, so a .NET security update also leads to a new TPD release.

## Reporting a vulnerability

Report vulnerabilities privately through GitHub: on the repository's **Security** tab, choose **Report a vulnerability** ([direct link](https://github.com/hectortosa/ThirdPartyDetective/security/advisories/new)). Please don't open a public issue for them.

Include what you can:

- the version (`thirdpartydetective --version`) and platform;
- the input that triggers it: a minimal SBOM, registry response, package archive or license file;
- what happens, and what you expected.

The report stays private while a fix is prepared, and the advisory is published with the release that fixes it, crediting you unless you prefer otherwise.

## Scope

TPD treats every input as untrusted: SBOM fields, registry metadata, package archives and license texts. In scope, for example:

- crafted input that ends up unescaped in a report or a CI log (Markdown, HTML, terminal control sequences), or that makes TPD run code, touch files outside its output directory, or use memory or time beyond its caps;
- requests to hosts outside the allowlist, directly or through redirects;
- crafted input that gets a wrong license identity or license text into `ATTRIBUTION.txt`, such as a license file with added restrictions identified as a plain permissive license;
- weaknesses in the release workflows or the published artifacts.

Out of scope: a third party's own license metadata being wrong (open an issue with the license detection form), and vulnerabilities in TPD's dependencies that TPD doesn't expose (report those upstream; Dependabot tracks them here).
