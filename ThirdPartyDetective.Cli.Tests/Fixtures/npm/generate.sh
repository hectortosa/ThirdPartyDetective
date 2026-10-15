#!/usr/bin/env bash
# generate.sh
#
# Regenerates the npm fixture used by the golden-file tests:
#   trivy-npm.spdx.json      Trivy's SPDX for a small Node.js project
#   registry-responses.json  recorded npm registry responses and trimmed tarballs
#
# The dependency set is chosen for the license shapes npm metadata really has:
# plain and compound SPDX expressions, a scoped package, the legacy "licenses"
# array, "SEE LICENSE IN <file>" (a commercial license), no license field at
# all, and non-OSI licenses (Python-2.0, CC-BY-4.0). None has a known advisory
# in OSV. Needs node/npm, trivy, python3 and network access. Only run it when
# the fixture should change; the tests never touch the network.

set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT

mkdir -p "$WORK/contoso-frontend"
cat > "$WORK/contoso-frontend/package.json" <<'JSON'
{
  "name": "contoso-frontend",
  "version": "1.0.0",
  "private": true,
  "dependencies": {
    "@mui/x-license": "7.29.1",
    "@sindresorhus/is": "7.0.2",
    "argparse": "2.0.1",
    "async": "0.2.10",
    "caniuse-lite": "1.0.30001700",
    "indexof": "0.0.1",
    "jszip": "3.10.1",
    "react": "19.1.0",
    "rxjs": "7.8.2",
    "type-fest": "4.41.0"
  }
}
JSON

(cd "$WORK/contoso-frontend" && npm install --omit=dev --ignore-scripts --no-audit --no-fund --loglevel=error)
# Relative path: keeps the developer's absolute path out of the SPDX document.
(cd "$WORK" && trivy fs --quiet --format spdx-json --output trivy.spdx.json contoso-frontend)

cp "$WORK/trivy.spdx.json" "$HERE/trivy-npm.spdx.json"
python3 "$HERE/../record-registry.py" "$HERE/registry-responses.json" "$HERE/trivy-npm.spdx.json"

echo "npm fixture regenerated with $(trivy --version | head -n1). Review the golden-file diffs."
