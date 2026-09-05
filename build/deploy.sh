#!/usr/bin/env bash
# Build, then install ONLY if the build succeeded.
#
# Exists because `./build.sh | grep … && cp …` checks grep's exit status, not
# the build's. Three stale DLLs reached the game that way — the complexity gate
# refused, the pipeline reported success, and the copy ran anyway. Testing a
# binary that predates the fix under test wastes a launch and, worse, produces
# a confident wrong conclusion.
set -euo pipefail
cd "$(dirname "$0")/.."
HK="${HK_DIR:-$HOME/.local/share/Steam/steamapps/common/Hollow Knight}"
./build.sh
cp dist/HKCouchCoop.dll dist/CoopKit.dll "$HK/BepInEx/plugins/"
echo "deployed $(strings -a "$HK/BepInEx/plugins/HKCouchCoop.dll" | grep -oE '0\.[0-9]+\.[0-9]+' | sort -u | tail -1)"
