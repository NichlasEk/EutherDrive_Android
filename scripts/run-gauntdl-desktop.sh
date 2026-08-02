#!/usr/bin/env sh
set -eu

SCRIPT_DIR="$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)"
REPO_ROOT="$(CDPATH= cd -- "$SCRIPT_DIR/.." && pwd)"
MAME_BIN="${MAME_BIN:-/home/nichlas/mame/vegas}"
ROM_SOURCE="${1:-/home/nichlas/roms/MAME/Midway/Vegas/gauntd}"
RUNTIME_ROOT="${EUTHERDRIVE_GAUNTDL_MAME_RUNTIME:-$REPO_ROOT/.build-tmp/mame-phase5-state}"
ROM_ROOT="$REPO_ROOT/.build-tmp/mame-rompath"
STATE_PATH="$RUNTIME_ROOT/sta/gauntdl24/phase4-oracle.sta"

if [ ! -x "$MAME_BIN" ]; then
    echo "Gauntlet MAME core not found: $MAME_BIN" >&2
    exit 1
fi
if [ ! -f "$ROM_SOURCE/gauntdl.zip" ] || [ ! -f "$ROM_SOURCE/gauntd24.chd" ]; then
    echo "Gauntlet ROM/CHD not found below: $ROM_SOURCE" >&2
    exit 1
fi

# Symlinks keep the 1.2 GiB CHD in its original location. Runtime writes stay
# in this bounded repo-local tree; nothing is written below /tmp.
mkdir -p "$ROM_ROOT/gauntdl24" "$RUNTIME_ROOT/cfg" "$RUNTIME_ROOT/nvram" \
    "$RUNTIME_ROOT/diff" "$RUNTIME_ROOT/snap" "$RUNTIME_ROOT/sta"
ln -sfn "$ROM_SOURCE/gauntdl.zip" "$ROM_ROOT/gauntdl.zip"
ln -sfn "$ROM_SOURCE/gauntdl24.7z" "$ROM_ROOT/gauntdl24.7z"
ln -sfn "$ROM_SOURCE/gauntd24.chd" "$ROM_ROOT/gauntdl24/gauntd24.chd"

echo "Gauntlet controls: 5 = coin, 1 = start, arrows = move, Left Ctrl/Alt/Space = actions"
echo "Runtime files: $RUNTIME_ROOT"

if [ -f "$STATE_PATH" ] && [ "${EUTHERDRIVE_GAUNTDL_MAME_COLD:-0}" != "1" ]; then
    exec "$MAME_BIN" gauntdl24 \
        -rompath "$ROM_ROOT" \
        -state_directory "$RUNTIME_ROOT/sta" \
        -nvram_directory "$RUNTIME_ROOT/nvram" \
        -cfg_directory "$RUNTIME_ROOT/cfg" \
        -snapshot_directory "$RUNTIME_ROOT/snap" \
        -diff_directory "$RUNTIME_ROOT/diff" \
        -state phase4-oracle -window -skip_gameinfo
fi

exec "$MAME_BIN" gauntdl24 \
    -rompath "$ROM_ROOT" \
    -state_directory "$RUNTIME_ROOT/sta" \
    -nvram_directory "$RUNTIME_ROOT/nvram" \
    -cfg_directory "$RUNTIME_ROOT/cfg" \
    -snapshot_directory "$RUNTIME_ROOT/snap" \
    -diff_directory "$RUNTIME_ROOT/diff" \
    -window -skip_gameinfo
