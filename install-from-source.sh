#!/usr/bin/env bash
# install-from-source.sh
#
# Installs the locally-checked-out ThirdPartyDetective CLI as a global
# .NET tool. Idempotent: if already installed, updates it.
#
# Usage:
#   ./install-from-source.sh              # build local source, install/update global tool
#
# After install, `thirdpartydetective` is on PATH (assuming ~/.dotnet/tools
# is on your PATH — script warns if not).
#
# Installs exactly the packed version from the local build output, with a
# NuGet config that contains only that folder: with --add-source, NuGet would
# also consult nuget.org and pick a higher version published there.
# For released versions, use install.sh.

set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
CSPROJ="$REPO_ROOT/ThirdPartyDetective.Cli/ThirdPartyDetective.Cli.csproj"

[[ -f "$CSPROJ" ]] || { echo "ERROR: not in TPD repo root (no $CSPROJ)" >&2; exit 1; }
command -v dotnet >/dev/null 2>&1 || { echo "ERROR: dotnet SDK required on PATH" >&2; exit 2; }

# Ensure ~/.dotnet/tools is on PATH for the rest of this script
if [[ ":$PATH:" != *":$HOME/.dotnet/tools:"* ]]; then
  export PATH="$PATH:$HOME/.dotnet/tools"
fi

echo "Packing ThirdPartyDetective..."
dotnet pack "$CSPROJ" \
  --configuration Release \
  --nologo \
  --verbosity quiet

NUPKG_DIR="$REPO_ROOT/ThirdPartyDetective.Cli/bin/Release"
VERSION="$(dotnet msbuild "$CSPROJ" -getProperty:Version)"

WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT
cat > "$WORK/nuget.config" <<EOF
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="local-build" value="$NUPKG_DIR" />
  </packageSources>
</configuration>
EOF

# Always uninstall + install rather than relying on `dotnet tool update`. The
# update path is a no-op when the version hasn't changed, which leaves the
# previously-installed binary on PATH even after a fresh `dotnet pack`. For
# local-dev iteration we want every re-run to swap in the freshly-built dll.
if dotnet tool list --global 2>/dev/null | awk '{print tolower($1)}' | grep -qx "thirdpartydetective"; then
  echo "Removing previously-installed global tool"
  dotnet tool uninstall --global ThirdPartyDetective >/dev/null
fi
echo "Installing global tool ThirdPartyDetective $VERSION from $NUPKG_DIR"
dotnet tool install --global ThirdPartyDetective --version "$VERSION" --configfile "$WORK/nuget.config"

if ! command -v thirdpartydetective >/dev/null 2>&1; then
  cat <<EOF >&2

WARNING: thirdpartydetective installed but not on PATH in this shell.

Add this to your shell profile (~/.zshrc, ~/.bashrc, etc.):

  export PATH="\$PATH:\$HOME/.dotnet/tools"

Then open a new terminal or source your profile to pick it up.

EOF
  exit 1
fi

echo "✓ thirdpartydetective installed: $(command -v thirdpartydetective)"
