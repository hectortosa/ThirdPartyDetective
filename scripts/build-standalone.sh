#!/usr/bin/env bash
# build-standalone.sh
#
# Builds the standalone executable for one runtime and packs its release archive:
#   thirdpartydetective_<version>_<rid>.tar.gz   (.zip for win-x64)
# The archive holds, at its root: the executable (thirdpartydetective, or
# thirdpartydetective.exe), LICENSE, README.md, THIRD-PARTY-NOTICES.txt, and the
# license and third-party notices of the .NET runtime bundled in the executable.
#
# Usage: scripts/build-standalone.sh <rid> <output-dir>
#   rid: linux-x64, linux-arm64, osx-x64, osx-arm64 or win-x64
#
# Build the macOS executables on macOS: Apple silicon runs only signed code, and
# the SDK signs the executable (ad hoc) when it publishes on macOS.
# Needs the .NET 10 SDK and jq; tar, or zip / 7z for win-x64.

set -euo pipefail

RID="${1:?usage: $0 <rid> <output-dir>}"
OUT="${2:?usage: $0 <rid> <output-dir>}"
REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
PROJECT="$REPO_ROOT/ThirdPartyDetective.Cli/ThirdPartyDetective.Cli.csproj"

case "$RID" in
  linux-x64 | linux-arm64 | osx-x64 | osx-arm64) EXE=thirdpartydetective ;;
  win-x64) EXE=thirdpartydetective.exe ;;
  *) echo "ERROR: unsupported runtime '$RID'" >&2; exit 1 ;;
esac

mkdir -p "$OUT"
OUT="$(cd "$OUT" && pwd)"
WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT

VERSION="$(dotnet msbuild "$PROJECT" -getProperty:Version)"
NAME="thirdpartydetective_${VERSION}_${RID}"

dotnet publish "$PROJECT" --configuration Release --runtime "$RID" --self-contained \
  -p:PublishSingleFile=true --output "$WORK/publish" --nologo --verbosity quiet

# The runtime pack this publish bundled, as resolved by the SDK, so the notices
# always match the runtime inside the executable.
RUNTIME_PACK="$(dotnet msbuild "$PROJECT" -t:ResolveFrameworkReferences \
  -p:RuntimeIdentifier="$RID" -p:SelfContained=true -getItem:ResolvedRuntimePack |
  jq -r '.Items.ResolvedRuntimePack[] | select(.FrameworkName == "Microsoft.NETCore.App") | .PackageDirectory')"
if command -v cygpath >/dev/null 2>&1; then RUNTIME_PACK="$(cygpath -u "$RUNTIME_PACK")"; fi
for f in LICENSE.TXT THIRD-PARTY-NOTICES.TXT; do
  [ -f "$RUNTIME_PACK/$f" ] || { echo "ERROR: $f not found in the runtime pack at $RUNTIME_PACK" >&2; exit 1; }
done

STAGE="$WORK/stage"
mkdir -p "$STAGE"
case "$RID" in
  win-*) cp "$WORK/publish/ThirdPartyDetective.exe" "$STAGE/$EXE" ;;
  *) cp "$WORK/publish/ThirdPartyDetective" "$STAGE/$EXE"; chmod 755 "$STAGE/$EXE" ;;
esac
cp "$REPO_ROOT/LICENSE" "$REPO_ROOT/README.md" "$REPO_ROOT/THIRD-PARTY-NOTICES.txt" "$STAGE/"
cp "$RUNTIME_PACK/LICENSE.TXT" "$STAGE/DOTNET-RUNTIME-LICENSE.txt"
cp "$RUNTIME_PACK/THIRD-PARTY-NOTICES.TXT" "$STAGE/DOTNET-RUNTIME-THIRD-PARTY-NOTICES.txt"
chmod 644 "$STAGE/LICENSE" "$STAGE/README.md" "$STAGE/THIRD-PARTY-NOTICES.txt" "$STAGE"/DOTNET-RUNTIME-*.txt

# Members are named explicitly (no "./" prefix), so `tar -xzf <archive> thirdpartydetective` works.
FILES=("$EXE" LICENSE README.md THIRD-PARTY-NOTICES.txt DOTNET-RUNTIME-LICENSE.txt DOTNET-RUNTIME-THIRD-PARTY-NOTICES.txt)
case "$RID" in
  win-*)
    ARCHIVE="$OUT/$NAME.zip"
    rm -f "$ARCHIVE"
    if command -v zip >/dev/null 2>&1; then
      (cd "$STAGE" && zip -X -q "$ARCHIVE" "${FILES[@]}")
    elif command -v 7z >/dev/null 2>&1; then
      (cd "$STAGE" && 7z a -tzip -bso0 -bsp0 "$ARCHIVE" "${FILES[@]}")
    else
      echo "ERROR: zip or 7z is required for $RID" >&2; exit 1
    fi
    ;;
  *)
    ARCHIVE="$OUT/$NAME.tar.gz"
    tar -czf "$ARCHIVE" -C "$STAGE" "${FILES[@]}"
    ;;
esac

echo "Wrote $ARCHIVE"
