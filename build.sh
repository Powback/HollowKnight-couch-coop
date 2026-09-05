#!/usr/bin/env bash
# Build HKCouchCoop, and optionally install it into the game.
#
#   ./build.sh            build only, output in dist/
#   ./build.sh --install  build, then copy the plugin into BepInEx/plugins
#
# Uses a container if no local dotnet SDK is available.
set -euo pipefail

cd "$(dirname "$0")"

HK="${HK_DIR:-$HOME/.local/share/Steam/steamapps/common/Hollow Knight}"

# Game assemblies are not redistributable and are not in this repo; refs/ is
# populated from your own install.
if [ ! -f refs/Assembly-CSharp.dll ]; then
  echo "==> Populating refs/ from: $HK"
  if [ ! -d "$HK/hollow_knight_Data/Managed" ]; then
    echo "Could not find Hollow Knight. Set HK_DIR to your install directory." >&2
    exit 1
  fi
  mkdir -p refs
  cp "$HK/hollow_knight_Data/Managed/"*.dll refs/
fi

# A gate, not a report: new methods start under the cap and existing ones may
# not get worse. Skipped only if python is genuinely absent, because a lint
# that quietly does not run is worse than none.
if command -v python3 >/dev/null 2>&1; then
  echo "==> Lint"
  python3 build/complexity.py --check src/*.cs || {
    echo "Refusing to build: a method got more tangled." >&2
    echo "Simplify it, or run 'python3 build/complexity.py --write-budget src/*.cs'" >&2
    echo "if the new shape is genuinely the simpler one." >&2
    exit 1
  }
fi

echo "==> Building"
if command -v dotnet >/dev/null 2>&1; then
  dotnet build src/HKCouchCoop.csproj -c Release -o dist
else
  # CoopKit is a sibling checkout (../CoopModKit) — mount both repos.
  podman run --rm -v "$PWD":/w/HollowKnightMPMod:z -v "$PWD/../CoopModKit":/w/CoopModKit:z \
    -w /w/HollowKnightMPMod mcr.microsoft.com/dotnet/sdk:9.0 \
    dotnet build src/HKCouchCoop.csproj -c Release -o dist
fi

if [ "${1:-}" = "--install" ]; then
  PLUGINS="$HK/BepInEx/plugins"
  if [ ! -d "$HK/BepInEx" ]; then
    echo "BepInEx is not installed in '$HK'." >&2
    echo "Install BepInEx 5 (x64), run the game once, then re-run this." >&2
    exit 1
  fi
  mkdir -p "$PLUGINS"
  cp dist/HKCouchCoop.dll dist/CoopKit.dll "$PLUGINS/"
  echo "==> Installed to $PLUGINS/HKCouchCoop.dll"
fi

echo "==> Done"
