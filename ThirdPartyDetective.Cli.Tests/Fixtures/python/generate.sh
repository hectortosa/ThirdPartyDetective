#!/usr/bin/env bash
# generate.sh
#
# Regenerates the Python fixture used by the golden-file tests:
#   trivy-requirements.spdx.json   Trivy's SPDX for a requirements.txt (no licenses:
#                                  lock files don't carry them)
#   trivy-site-packages.spdx.json  Trivy's SPDX for the same packages installed
#                                  (trivy rootfs: `trivy fs` skips installed packages)
#   registry-responses.json        recorded PyPI responses and trimmed wheels
#
# Packages are installed for Linux x86_64 / CPython 3.12 (what our container
# images run), whatever machine regenerates the fixture. The set covers the
# license shapes PyPI metadata really has: PEP 639 license expressions
# (including a compound one), short SPDX IDs in the free-text "license" field,
# free text ("Dual License", "LGPL with exceptions"), and classifier-only
# metadata, including ambiguous classifiers and several license files. None has
# a known advisory in OSV. Needs uv, trivy, python3 and network access.

set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT

mkdir -p "$WORK/contoso-api"
cat > "$WORK/contoso-api/requirements.txt" <<'TXT'
attrs==25.3.0
certifi==2025.8.3
chardet==5.2.0
charset-normalizer==3.4.3
docutils==0.22
idna==3.20
packaging==25.0
psycopg2-binary==2.9.10
python-dateutil==2.9.0.post0
regex==2025.9.1
requests==2.34.2
six==1.17.0
typing_extensions==4.15.0
urllib3==2.8.0
TXT

uv pip install --quiet --no-deps --target "$WORK/site-packages" \
  --python-version 3.12 --python-platform x86_64-manylinux_2_28 --only-binary :all: \
  -r "$WORK/contoso-api/requirements.txt"
# Relative paths: keep the developer's absolute path out of the SPDX documents.
(cd "$WORK" && trivy fs --quiet --format spdx-json --output "$HERE/trivy-requirements.spdx.json" contoso-api)
(cd "$WORK" && trivy rootfs --quiet --format spdx-json --output "$HERE/trivy-site-packages.spdx.json" site-packages)

python3 "$HERE/../record-registry.py" "$HERE/registry-responses.json" \
  "$HERE/trivy-requirements.spdx.json" "$HERE/trivy-site-packages.spdx.json"

echo "Python fixture regenerated with $(trivy --version | head -n1). Review the golden-file diffs."
