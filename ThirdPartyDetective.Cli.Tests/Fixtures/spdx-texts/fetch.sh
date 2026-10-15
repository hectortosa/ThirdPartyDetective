#!/usr/bin/env bash
# fetch.sh
#
# Downloads canonical SPDX license texts, pinned to a license-list-data
# release, for the LicenseTextDetector tests. Re-run only to change the pin.

set -euo pipefail

SPDX_LIST_VERSION="v3.29.0"
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

IDS=(
  MIT MIT-0 Apache-2.0 BSD-2-Clause BSD-3-Clause ISC 0BSD BSL-1.0 Zlib
  Unlicense CC0-1.0 MS-PL PostgreSQL MPL-2.0 EPL-2.0
  LGPL-2.0-only LGPL-2.1-only LGPL-3.0-only GPL-2.0-only GPL-3.0-only AGPL-3.0-only
)

for id in "${IDS[@]}"; do
  curl -fsSL "https://raw.githubusercontent.com/spdx/license-list-data/${SPDX_LIST_VERSION}/text/${id}.txt" -o "$HERE/${id}.txt"
done
echo "$SPDX_LIST_VERSION" > "$HERE/VERSION"
echo "Fetched ${#IDS[@]} texts from license-list-data ${SPDX_LIST_VERSION}"
