#!/usr/bin/env bash
# Configure the Hollow Knight Proton prefix on this machine so BepInEx loads
# without Steam launch options.
#
# Doorstop's winhttp.dll shim only loads if Wine prefers the game directory's
# ("native") winhttp over its builtin one. The usual fix is the launch option
#   WINEDLLOVERRIDES="winhttp=n,b" %command%
# but launch options live in localconfig.vdf, which Steam rewrites from memory
# on exit — racing it is a losing game on a Deck where Steam never really
# stops. The same override written into the prefix's own registry
# (pfx/user.reg) is read by Wine directly, survives Steam restarts, and Steam
# never touches it.
#
# One rule: only edit user.reg while the prefix's wineserver is not running,
# because Wine flushes its in-memory registry over the file on shutdown.
# Steam itself running is fine; the game running is not.
set -euo pipefail

APPID=367520
PREFIX="$HOME/.local/share/Steam/steamapps/compatdata/$APPID/pfx"
REG="$PREFIX/user.reg"

if [ ! -f "$REG" ]; then
  echo "NO_PREFIX: $REG missing — run the game once under Proton first"
  exit 1
fi

# The game (this prefix's wine processes) must not be live.
if pgrep -f "compatdata/$APPID" >/dev/null 2>&1; then
  echo "GAME_RUNNING: close Hollow Knight first"
  exit 2
fi

if grep -q '^\[Software\\\\Wine\\\\DllOverrides\]' "$REG" \
   && grep -A20 '^\[Software\\\\Wine\\\\DllOverrides\]' "$REG" | grep -q '"winhttp"'; then
  echo "ALREADY_SET"
  exit 0
fi

cp "$REG" "$REG.hkcoop.bak"

python3 - "$REG" <<'PY'
import re, sys, time

path = sys.argv[1]
text = open(path, encoding="utf-8", errors="surrogateescape").read()

section = re.search(r'^\[Software\\\\Wine\\\\DllOverrides\][^\n]*\n', text, re.M)
if section:
    insert_at = section.end()
    # Skip the section's #time line if present.
    m = re.match(r'#time=[^\n]*\n', text[insert_at:])
    if m:
        insert_at += m.end()
    text = text[:insert_at] + '"winhttp"="native,builtin"\n' + text[insert_at:]
else:
    stamp = int(time.time())
    text = text.rstrip("\n") + (
        "\n\n[Software\\\\Wine\\\\DllOverrides] %d\n"
        '"winhttp"="native,builtin"\n' % stamp
    )

open(path, "w", encoding="utf-8", errors="surrogateescape").write(text)
print("WRITTEN")
PY

echo "=== verify ==="
grep -A3 'DllOverrides' "$REG" | head -5
