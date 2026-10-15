#!/usr/bin/env bash
# generate-third-party-notices.sh
#
# Produces the third-party notices for ThirdPartyDetective's own dependencies,
# with ThirdPartyDetective itself and the pipeline the README describes for CI:
# dotnet publish -> trivy fs (SPDX) -> thirdpartydetective -> ATTRIBUTION.txt.
# The tool package bundles its dependencies' assemblies, so it has to carry
# their notices.
#
# Usage: scripts/generate-third-party-notices.sh <notices.txt> [<sbom.spdx.json>]
# Needs the .NET 10 SDK, trivy and network access (NuGet registry fallback).

set -euo pipefail

NOTICES_OUT="${1:?usage: $0 <notices.txt> [<sbom.spdx.json>]}"
SBOM_OUT="${2:-}"
REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT

dotnet publish "$REPO_ROOT/ThirdPartyDetective.Cli/ThirdPartyDetective.Cli.csproj" \
  --configuration Release --output "$WORK/publish" --nologo --verbosity quiet

# Relative path: keeps the build machine's directory layout out of the SBOM.
(cd "$WORK" && trivy fs --quiet --format spdx-json --output tpd.spdx.json publish)

dotnet "$WORK/publish/ThirdPartyDetective.dll" \
  --sbom "$WORK/tpd.spdx.json" \
  --mode distribution \
  --exclude-namespaces 'ThirdPartyDetective' \
  --registry-fallback --registry-fallback-cache "$WORK/license-cache.json" \
  --format fulltext --include-license-text \
  --deterministic --fail-on-unresolved --no-fail \
  --output "$WORK/notices"

cp "$WORK/notices/ATTRIBUTION.txt" "$NOTICES_OUT"
if [ -n "$SBOM_OUT" ]; then
  cp "$WORK/tpd.spdx.json" "$SBOM_OUT"
fi
echo "Wrote $NOTICES_OUT${SBOM_OUT:+ and $SBOM_OUT}"
