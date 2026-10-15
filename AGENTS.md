# AGENTS.md

Instructions for coding agents working in this repository. Usage is in [README.md](README.md); the human workflow and releases are in [CONTRIBUTING.md](CONTRIBUTING.md).

## What this is

ThirdPartyDetective (TPD) is a .NET 10 command-line tool (`thirdpartydetective`), released as a self-contained single-file executable per platform. It reads SPDX 2.2/2.3 JSON SBOMs (Trivy, Syft, Microsoft.Sbom.Tool), classifies every package's license against a deployment-mode policy (SaaS / distribution), and writes `packages.md`, `packages.json`, `packages.html` and `ATTRIBUTION.txt`.

Typical use: a CI job runs Trivy on what a team ships (a .NET publish directory, a Node.js project or a Python environment), and TPD turns the SBOM into the `ATTRIBUTION.txt` that ships with the product, for example inside container images delivered to customers. Treat `ATTRIBUTION.txt` as a legal artifact: a wrong license identity, a wrong license text or a silently dropped package is a high-severity bug. When the evidence is not conclusive, the tool reports "not determined" and a human records the decision in `.tpd-overrides.json`; it never guesses.

## Commands

The SDK is pinned in `global.json`. Everything runs from the repository root.

```bash
dotnet build ThirdPartyDetective.sln
dotnet test                                                   # whole suite, offline, a few seconds
dotnet test --filter "FullyQualifiedName~EcosystemGoldenTests" # one class
TPD_UPDATE_GOLDEN=1 dotnet test                               # rewrite golden files after an intended output change
dotnet run --project ThirdPartyDetective.Cli -- --sbom ThirdPartyDetective.Cli.Tests/Fixtures/npm/trivy-npm.spdx.json --mode distribution --format console --no-fail
dotnet pack ThirdPartyDetective.Cli/ThirdPartyDetective.Cli.csproj -c Release
./install-from-source.sh                                      # pack and install the working copy as a .NET global tool
scripts/build-standalone.sh osx-arm64 dist                    # one platform's release archive (macOS builds need macOS)
scripts/check-standalone.sh <ThirdPartyDetective.dll> <executable> # same reports from both, or it fails
scripts/generate-third-party-notices.sh THIRD-PARTY-NOTICES.txt # after any dependency change; CI fails until it matches
```

CI (`.github/workflows/ci.yml`) builds and tests in Release, fails on vulnerable packages, checks the package contents and `THIRD-PARTY-NOTICES.txt`, checks the linux-x64 executable writes the same reports as the framework-dependent build, and lints the workflows with actionlint. `release.yml` repeats that check on every platform before drafting a release.

## Layout

- `ThirdPartyDetective.Cli/` is the tool (System.CommandLine, Microsoft.Extensions.Hosting/Http); `ThirdPartyDetective.Cli.Tests/` is xUnit. They are the only projects in `ThirdPartyDetective.sln`.
- The version is set once, as `<Version>` in `Directory.Build.props`, and read at runtime through `ToolVersion.Current`.

## Architecture

```
SPDX SBOM(s) -> SpdxSbomReader -> SbomMerger -> RegistryFallbackEnricher (--registry-fallback)
             -> OverrideApplier (.tpd-overrides.json) -> SbomClassifier (.tpd-policy.json) -> OutputGenerator
```

Stages pass `Models/ThirdPartyInfo.cs` along. Paths below are under `ThirdPartyDetective.Cli/Services/`.

- **Reading.** `SpdxSbomReader` skips Trivy's SOURCE/APPLICATION wrapper packages and ignores Trivy's placeholder `extractedText` ("This component is licensed under ..."), which is not a license text.
- **License identity.** `SpdxExpression` parses expressions; ATTRIBUTION.txt prints the declared expression. `SpdxLicenseList` reads the SPDX license list bundled as `Resources/spdx-license-list.tar.gz` (rebuilt by `scripts/update-spdx-license-list.sh`; review the `.manifest` diff). `LicenseTextProvider` serves Appendix A texts from it and `LicenseMatcher` identifies license files against it. Texts are never downloaded.
- **Policy.** `LicenseAnalyzer` maps expressions to the `LicenseType` enum through `LicenseTypeRegistry`. `LicenseType` is a coarse policy bucket (PSF-2.0 is in the MIT bucket); it is never printed as a license identity.
- **Registry fallback.** `RegistryFallbackEnricher` fills gaps only: it never replaces a license, URL, author or copyright the SBOM declares. Packages an override fully decides (license set, copyright known) are not looked up (`OverrideApplier.FullyDecided`). `Registry/{NuGet,Npm,PyPi}LicenseLookup` read registry metadata and the license files in the exact version's archive (`Registry/PackageArchive`: capped downloads from the registries' own hosts only). `Registry/DeclaredLicense` maps a declaration to SPDX only when it names one license unambiguously. `CopyrightExtractor` takes copyright lines from license and NOTICE files. `Registry/LicenseTextDetector` probes a repository's default branch only when the package declares nothing and ships no license file, and flags the result. Answers are cached in `.tpd-license-cache.json`; bump `LicenseCacheFile.CurrentVersion` when the meaning of an entry changes.
- **Output.** `OutputGenerator` writes every report; every value from an SBOM, registry or license file goes through `OutputText`, which strips control and format characters and escapes Markdown and HTML. Wording with legal weight (how copyleft source code is provided) comes from the notice template (`Models/NoticeTemplate.cs`, `.tpd-notices.json`); the defaults state facts only.

Exit codes: `0` success, `1` policy violations (suppressed by `--no-fail`), `2` operational error, including unresolved licenses or missing texts with `--fail-on-unresolved`.

## Testing

- Behavior changes come with tests. Output changes show up as golden-file diffs in `ThirdPartyDetective.Cli.Tests/Golden/`, committed with the change that causes them; that diff is what gets reviewed.
- Tests never touch the network. `CliRun` runs the CLI in-process with a `FakeHttpHandler` that replays `Fixtures/<trivy|npm|python>/registry-responses.json` (unknown URLs get 404), and the `requests.txt` golden files list every URL a run requested. Lookup unit tests build their responses in place with `FakeHttpHandler.With(...)` and `ArchiveBuilder`.
- Fixtures are regenerated only on purpose, with `Fixtures/<trivy|npm|python>/generate.sh` (needs Trivy, network, and the .NET SDK, npm or uv). They share `Fixtures/record-registry.py`, which trims metadata and cuts package archives down to their license files.
- Golden files show a one-line placeholder for each SPDX text in Appendix A (`PlaceholderTextLicenseList`); license matching still uses the real bundled texts.
- Tests that use `CliRun` belong to the `ProcessEnvironment` collection: they change `TPD_HOME` and the culture, which are process-wide.

## Conventions

- Conventional Commits (`feat:`, `fix:`, `test:`, `docs:`, `build:`, `ci:`, `chore:`), one concern per commit, and every commit builds and passes the tests on its own.
- Every input is untrusted: SBOM fields, registry metadata, package archives and license texts. Regexes that run on fetched text use `RegexOptions.NonBacktracking` (which has no lookarounds); network access goes only to allowlisted hosts, with size and time caps.
- `.tpd-policy.json`, `.tpd-overrides.json` and `.tpd-notices.json` are public schemas: additive changes only.
- Runtime dependencies are System.CommandLine and Microsoft.Extensions.* only.
- Comments say why, not what. Match the surrounding code.

## Boundaries

Always:
- Run `dotnet test` before committing, and read every golden-file diff you produce.
- Prefer leaving a license unresolved, with a warning that says why, to inferring one.

Ask first:
- Regenerating fixtures, updating the bundled SPDX license list, or adding a dependency.
- Changing a CLI option's meaning, an exit code or a config schema.
- Changing any wording in `ATTRIBUTION.txt` that goes beyond stating facts.

Never:
- Push, tag, create releases, publish packages or change repository visibility. These are the maintainer's decisions and need an explicit go-ahead each time.
- Let registry data replace a license the SBOM declares, or map an ambiguous declaration ("BSD", "Apache Software License", "Dual License") to a specific license.
- Make tests depend on the network, or commit credentials, feed tokens or a personal `.tpd-license-cache.json`.
