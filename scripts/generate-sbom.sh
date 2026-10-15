#!/usr/bin/env bash
# generate-sbom.sh
#
# Writes the SPDX SBOM of the standalone executable for one runtime: TPD's NuGet
# dependencies plus the bundled .NET runtime (Microsoft.NETCore.App.Runtime.<rid>).
# Trivy reads a self-contained publish that is not single-file, whose
# .deps.json lists the same packages the single-file executable bundles.
#
# Usage: scripts/generate-sbom.sh <rid> <sbom.spdx.json>
# Needs the .NET 10 SDK and trivy.

set -euo pipefail

RID="${1:?usage: $0 <rid> <sbom.spdx.json>}"
SBOM_OUT="${2:?usage: $0 <rid> <sbom.spdx.json>}"
REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT

dotnet publish "$REPO_ROOT/ThirdPartyDetective.Cli/ThirdPartyDetective.Cli.csproj" \
  --configuration Release --runtime "$RID" --self-contained \
  --output "$WORK/publish" --nologo --verbosity quiet

# Relative path: keeps the build machine's directory layout out of the SBOM.
(cd "$WORK" && trivy fs --quiet --format spdx-json --output sbom.spdx.json publish)

grep -q "\"Microsoft.NETCore.App.Runtime.$RID\"" "$WORK/sbom.spdx.json" ||
  { echo "ERROR: the SBOM does not list the bundled .NET runtime" >&2; exit 1; }
cp "$WORK/sbom.spdx.json" "$SBOM_OUT"
echo "Wrote $SBOM_OUT"
