#!/usr/bin/env bash
# update-spdx-license-list.sh
#
# Rebuilds the SPDX license list bundled into the tool:
#   ThirdPartyDetective.Cli/Resources/spdx-license-list.tar.gz    embedded resource
#   ThirdPartyDetective.Cli/Resources/spdx-license-list.manifest  SHA-256 of every text
#
# The archive holds index.json (license list version, license and exception IDs
# and names) and texts/<id>.txt for every license and exception, deprecated
# IDs included. Entries are sorted, with zeroed timestamps and owners, so the
# same input produces a byte-identical archive. Review the manifest diff when updating:
# it shows exactly which license texts changed.
#
# Usage: scripts/update-spdx-license-list.sh v3.29.0

set -euo pipefail

TAG="${1:?usage: $0 <license-list-data tag, e.g. v3.29.0>}"
REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
OUT_DIR="$REPO_ROOT/ThirdPartyDetective.Cli/Resources"
WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT

curl -fsSL "https://codeload.github.com/spdx/license-list-data/tar.gz/refs/tags/${TAG}" -o "$WORK/lld.tar.gz"
tar -xzf "$WORK/lld.tar.gz" -C "$WORK"
SRC="$(find "$WORK" -maxdepth 1 -type d -name 'license-list-data-*' | head -n1)"

mkdir -p "$OUT_DIR"
python3 - "$SRC" "$OUT_DIR" "$TAG" <<'EOF'
import gzip, hashlib, io, json, os, sys, tarfile

src, out_dir, tag = sys.argv[1], sys.argv[2], sys.argv[3]
licenses = json.load(open(os.path.join(src, "json", "licenses.json"), encoding="utf-8"))
exceptions = json.load(open(os.path.join(src, "json", "exceptions.json"), encoding="utf-8"))

def entries(items, id_key):
    return sorted(({"id": i[id_key], "name": i.get("name"), "deprecated": bool(i.get("isDeprecatedLicenseId"))}
                   for i in items), key=lambda e: e["id"])

index = {
    "licenseListVersion": licenses["licenseListVersion"],
    "sourceTag": tag,
    "licenses": entries(licenses["licenses"], "licenseId"),
    "exceptions": entries(exceptions["exceptions"], "licenseExceptionId"),
}

texts = {}
for entry in index["licenses"] + index["exceptions"]:
    name = ("deprecated_" if entry["deprecated"] else "") + entry["id"] + ".txt"
    path = os.path.join(src, "text", name)
    if os.path.exists(path):
        with open(path, "rb") as f:
            texts[entry["id"]] = f.read()

def add(tar, name, data):
    info = tarfile.TarInfo(name)
    info.size = len(data)
    info.mtime = 0
    info.mode = 0o644
    info.uid = info.gid = 0
    info.uname = info.gname = ""
    tar.addfile(info, io.BytesIO(data))

with open(os.path.join(out_dir, "spdx-license-list.tar.gz"), "wb") as raw:
    with gzip.GzipFile(filename="", mode="wb", fileobj=raw, mtime=0, compresslevel=9) as gz:
        with tarfile.open(fileobj=gz, mode="w", format=tarfile.USTAR_FORMAT) as tar:
            add(tar, "index.json", json.dumps(index, indent=1, ensure_ascii=False).encode("utf-8"))  # same bytes as index_bytes
            for license_id in sorted(texts):
                add(tar, f"texts/{license_id}.txt", texts[license_id])

index_bytes = json.dumps(index, indent=1, ensure_ascii=False).encode("utf-8")
with open(os.path.join(out_dir, "spdx-license-list.manifest"), "w", encoding="utf-8") as f:
    f.write(f"# SPDX license-list-data {tag} (license list {index['licenseListVersion']}); SHA-256 of index.json and each bundled text\n")
    f.write(f"{hashlib.sha256(index_bytes).hexdigest()}  index.json\n")
    for license_id in sorted(texts):
        f.write(f"{hashlib.sha256(texts[license_id]).hexdigest()}  {license_id}\n")

missing = [e["id"] for e in index["licenses"] + index["exceptions"] if e["id"] not in texts]
print(f"Bundled {len(texts)} texts from license-list-data {tag}; without text: {missing or 'none'}")
EOF
