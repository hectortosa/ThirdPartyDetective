# Contributing to ThirdPartyDetective

Thanks for your interest in ThirdPartyDetective. Outside contributions start as issues: only the repository's collaborators can open pull requests.

Coding agents: the repository's instructions for you are in [AGENTS.md](AGENTS.md).

## Local development

```bash
# Build everything (CLI + tests)
dotnet build ThirdPartyDetective.sln

# Run the test suite
dotnet test

# Install your local working copy as a global tool, for end-to-end testing
./install-from-source.sh
```

Target framework: **.NET 10.0**. The CLI lives in `ThirdPartyDetective.Cli/`, tests in `ThirdPartyDetective.Cli.Tests/`; those are the only projects in the solution. The tool version is set once, as `<Version>` in `Directory.Build.props`.

## Architecture, briefly

```
SPDX SBOM(s) → SpdxSbomReader → SbomMerger
                                    ↓
                           RegistryFallbackEnricher (optional, --registry-fallback)
                                    ↓
                            OverrideApplier (.tpd-overrides.json)
                                    ↓
                              SbomClassifier (uses .tpd-policy.json)
                                    ↓
                              OutputGenerator (md/json/html/text/console)
```

Pipeline stages communicate through `ThirdPartyInfo` (the universal package model in `Models/ThirdPartyInfo.cs`). New ecosystems / new sources of truth slot into the pipeline; they don't touch the core classification logic.

## Contributing changes

If you are not a collaborator, open an issue:

- **A bug, or a license TPD gets wrong:** use the bug report or the license detection form. A minimal SBOM that reproduces it is the most useful thing to attach (see [Reporting bugs](#reporting-bugs)).
- **An idea or a change you would like:** open a feature request that describes the problem. If it fits the project, the maintainer implements it or asks you to contribute it.
- **A vulnerability:** report it privately, as [SECURITY.md](SECURITY.md) describes.

Collaborators:

1. Branch from `main`, make the change, and add tests for new or changed behavior.
2. `dotnet test` must pass.
3. Open a pull request; it merges once CI is green. Keep commits focused: one concern per commit, using [Conventional Commits](https://www.conventionalcommits.org/) (`feat:`, `fix:`, `chore:`, ...).

## What we look for

- **Tests for new behavior**, especially around `SpdxSbomReader`, `LicenseAnalyzer`, `PolicyConfigLoader`, `OverrideApplier`. Plain xUnit, plain `Assert`.
- **Reviewed golden-file diffs for output changes.** Report output is pinned by golden files in `ThirdPartyDetective.Cli.Tests/Golden/`. When a change alters output on purpose, run `TPD_UPDATE_GOLDEN=1 dotnet test` and commit the updated files; the diff is part of the review.
- **Backward compatibility for config files**. The `.tpd-policy.json` and `.tpd-overrides.json` schemas are public APIs: additive changes only.
- **Minimal new dependencies**. The CLI's full dependency tree is only `System.CommandLine` + `Microsoft.Extensions.*`. New runtime deps need a strong justification.

## Releasing

Releases are two steps, so nothing is published without a human looking at it first:

1. Bump `<Version>` in `Directory.Build.props` on `main`. If dependencies changed, regenerate the notices with `scripts/generate-third-party-notices.sh THIRD-PARTY-NOTICES.txt` (CI fails until they match). To try the whole release first, run the **Release** workflow by hand: a dry run that builds and checks every platform and creates no release.
2. Push a tag `vX.Y.Z` pointing at that commit. The **Release** workflow checks the tag matches `<Version>` and is on `main`, runs the tests, builds the standalone executable for each platform on a runner of that platform, checks each one writes the same reports as the framework-dependent build, and creates a **draft** GitHub release with the five archives, an SBOM per executable, `THIRD-PARTY-NOTICES.txt` and `SHA256SUMS`, all with build provenance attestations.
3. Review the draft, then run the **Publish** workflow (from `main`) with the tag. In the `release` environment it verifies every asset against `SHA256SUMS` and its provenance, then publishes the release.
4. Bump the Homebrew formula: run the bump workflow in [hectortosa/homebrew-tap](https://github.com/hectortosa/homebrew-tap) with the tag, and merge its pull request once its test passes.

One-time setup: a `release` environment restricted to `main`, with the maintainer as required reviewer; no secrets are needed. Turn on release immutability (Settings > General > Releases), so a published release's tag and assets can't change.

The executables bundle the .NET runtime that comes with the SDK pinned in `global.json`. When Dependabot bumps the SDK for a .NET security fix, release a patch version.

The bundled SPDX license list is updated with `scripts/update-spdx-license-list.sh <license-list-data tag>`; review the `spdx-license-list.manifest` diff, which lists every text that changed.

## Reporting bugs

Include:
- A minimal SBOM that reproduces the issue (hand-craft if needed).
- The exact command line.
- The actual vs. expected output.
- Output of `thirdpartydetective --version` and `dotnet --version`.

Real-world SBOMs are huge, so please trim down to the minimal failing subset before attaching.
