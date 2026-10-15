#!/usr/bin/env bash
# install.sh
#
# Installs an exact version of the ThirdPartyDetective executable from its
# GitHub release, after checking the archive against a SHA-256 you pinned.
#
# Usage:
#   TPD_VERSION=1.0.0 \
#   TPD_SHA256=<sha256 of thirdpartydetective_1.0.0_<rid>.tar.gz, from the release's SHA256SUMS> \
#   ./install.sh
#
# TPD_SHA256 is required. Keep it where you pin versions (a CI variable, a
# committed file), not next to the download: a hash fetched from the same place
# as the archive only proves the download wasn't truncated.
#
# Optional:
#   TPD_INSTALL_DIR  where the executable goes (default: ~/.local/bin)
#   TPD_ARCHIVE      a local copy of the archive, for offline mirrors; nothing is downloaded
#   TPD_RID          the platform, when detection is wrong: linux-x64, linux-arm64, osx-x64, osx-arm64
#
# Windows: download the .zip and check it with Get-FileHash (see the README).
# Contributors installing their working copy: see install-from-source.sh.

set -euo pipefail

REPOSITORY="hectortosa/ThirdPartyDetective"
COMMAND_NAME="thirdpartydetective"

VERSION="${TPD_VERSION:-}"
EXPECTED_SHA256="$(tr '[:upper:]' '[:lower:]' <<<"${TPD_SHA256:-}")"
INSTALL_DIR="${TPD_INSTALL_DIR:-$HOME/.local/bin}"

fail() { echo "ERROR: $*" >&2; exit 1; }

[[ "$VERSION" =~ ^[0-9]+\.[0-9]+\.[0-9]+(-[0-9A-Za-z.-]+)?$ ]] || fail "set TPD_VERSION to an exact version, e.g. TPD_VERSION=1.0.0 (no 'latest')."
[[ "$EXPECTED_SHA256" =~ ^[0-9a-f]{64}$ ]] || fail "set TPD_SHA256 to the archive's SHA-256 from the release's SHA256SUMS."

detect_rid() {
  local os arch
  case "$(uname -s)" in
    Linux)
      # The Linux executables are built against glibc.
      if ldd --version 2>&1 | grep -qi musl; then fail "musl-based Linux (e.g. Alpine) is not supported; use a glibc-based image."; fi
      os=linux ;;
    Darwin) os=osx ;;
    *) fail "unsupported OS $(uname -s); on Windows, follow the README." ;;
  esac
  case "$(uname -m)" in
    x86_64 | amd64) arch=x64 ;;
    aarch64 | arm64) arch=arm64 ;;
    *) fail "unsupported CPU $(uname -m)." ;;
  esac
  echo "$os-$arch"
}

RID="${TPD_RID:-$(detect_rid)}"
case "$RID" in
  linux-x64 | linux-arm64 | osx-x64 | osx-arm64) ;;
  *) fail "unsupported TPD_RID '$RID'." ;;
esac
NAME="thirdpartydetective_${VERSION}_${RID}.tar.gz"

work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT

if [ -n "${TPD_ARCHIVE:-}" ]; then
  [ -f "$TPD_ARCHIVE" ] || fail "TPD_ARCHIVE '$TPD_ARCHIVE' is not a file."
  cp "$TPD_ARCHIVE" "$work/$NAME"
else
  command -v curl >/dev/null 2>&1 || fail "curl is required to download the release (or set TPD_ARCHIVE)."
  url="https://github.com/$REPOSITORY/releases/download/v$VERSION/$NAME"
  echo "Downloading $url"
  curl --fail --silent --show-error --location --proto '=https' --tlsv1.2 --output "$work/$NAME" "$url"
fi

actual="$( (sha256sum "$work/$NAME" 2>/dev/null || shasum -a 256 "$work/$NAME") | awk '{print $1}')"
[ "$actual" = "$EXPECTED_SHA256" ] || fail "SHA-256 mismatch for $NAME (expected $EXPECTED_SHA256, got $actual); nothing installed."
echo "Verified SHA-256 of $NAME"

tar -xzf "$work/$NAME" -C "$work" "$COMMAND_NAME"
mkdir -p "$INSTALL_DIR"
# Copy next to the target, then rename, so a running copy is never half-written.
cp "$work/$COMMAND_NAME" "$INSTALL_DIR/.$COMMAND_NAME.tmp"
chmod 755 "$INSTALL_DIR/.$COMMAND_NAME.tmp"
mv -f "$INSTALL_DIR/.$COMMAND_NAME.tmp" "$INSTALL_DIR/$COMMAND_NAME"

"$INSTALL_DIR/$COMMAND_NAME" --version
if ! echo ":$PATH:" | grep -q ":$INSTALL_DIR:"; then
  echo "Installed to $INSTALL_DIR, which is not on PATH: add it to your shell profile." >&2
fi
