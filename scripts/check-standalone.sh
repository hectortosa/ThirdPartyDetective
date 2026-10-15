#!/usr/bin/env bash
# check-standalone.sh
#
# Runs the test fixtures' SBOMs through a framework-dependent build and a
# standalone executable, offline, and fails unless every report is
# byte-identical. The golden-file tests pin the framework-dependent output; this
# proves the shipped executable writes the same thing. Build the reference with
# -p:InvariantGlobalization=false to also prove that running without ICU
# changes nothing.
#
# Usage: scripts/check-standalone.sh <reference ThirdPartyDetective.dll> <standalone executable>

set -euo pipefail

REFERENCE="${1:?usage: $0 <reference ThirdPartyDetective.dll> <standalone executable>}"
STANDALONE="${2:?usage: $0 <reference ThirdPartyDetective.dll> <standalone executable>}"
REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
FIXTURES="$REPO_ROOT/ThirdPartyDetective.Cli.Tests/Fixtures"
REFERENCE="$(cd "$(dirname "$REFERENCE")" && pwd)/$(basename "$REFERENCE")"
STANDALONE="$(cd "$(dirname "$STANDALONE")" && pwd)/$(basename "$STANDALONE")"

WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT
# An empty TPD_HOME and working directory: only the bundled license list, and no
# auto-discovered .tpd-*.json.
export TPD_HOME="$WORK/home"
mkdir -p "$WORK/cwd"

SBOMS=(
  trivy/trivy-dotnet-publish.spdx.json
  npm/trivy-npm.spdx.json
  python/trivy-requirements.spdx.json
  python/trivy-site-packages.spdx.json
  spdx/clean.spdx.json
  spdx/injection.spdx.json
  spdx/license-edge-cases.spdx.json
)

run() {
  local out="$1"; shift
  for sbom in "${SBOMS[@]}"; do
    local name
    name="$(basename "$sbom" .spdx.json)"
    (cd "$WORK/cwd" && "$@" --sbom "$FIXTURES/$sbom" --mode distribution --format all --include-license-text \
      --deterministic --offline --no-fail --output "$out/$name" >/dev/null)
  done
}

run "$WORK/reference" dotnet "$REFERENCE"
run "$WORK/standalone" "$STANDALONE"

if ! diff -r "$WORK/reference" "$WORK/standalone"; then
  echo "ERROR: the standalone executable's reports differ from the framework-dependent build's" >&2
  exit 1
fi
echo "Identical reports for ${#SBOMS[@]} SBOMs ($(find "$WORK/standalone" -type f | wc -l | tr -d ' ') files)"
