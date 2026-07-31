#!/usr/bin/env sh
set -eu

SCRIPT_DIR="$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)"
REPO_ROOT="$(CDPATH= cd -- "$SCRIPT_DIR/.." && pwd)"

ROM_PATH="${1:-/home/nichlas/roms/MAME/Midway/Vegas/gauntd}"
SNAPSHOT_PATH="${2:-$REPO_ROOT/.build-tmp/euther-native-game-phase1-f4733.warm.gz}"
SNAPSHOT_FRAMES="${3:-4733}"
CPU_STEPS="${4:-200000}"
UI_DLL="$REPO_ROOT/EutherDrive.UI/bin/Release/net8.0/EutherDrive.UI.dll"
PROBE_DLL="$REPO_ROOT/tools/GauntletProbe/bin/Release/net8.0/GauntletProbe.dll"

if [ ! -f "$SNAPSHOT_PATH" ]; then
    echo "Gauntlet warm snapshot not found: $SNAPSHOT_PATH" >&2
    exit 1
fi
if [ ! -f "$UI_DLL" ] || [ ! -f "$PROBE_DLL" ]; then
    echo "Release UI or GauntletProbe build missing; build both Release projects first." >&2
    exit 1
fi

export EUTHERDRIVE_GAUNTDL_CPU_STEPS_PER_FRAME="$CPU_STEPS"
export EUTHERDRIVE_GAUNTDL_BRINGUP_BASELINE=1
export EUTHERDRIVE_GAUNTDL_UI_WARMUP_STATE="$SNAPSHOT_PATH"
export EUTHERDRIVE_GAUNTDL_UI_WARMUP_FRAMES="$SNAPSHOT_FRAMES"
export EUTHERDRIVE_GAUNTDL_PROBE_DLL="$PROBE_DLL"

exec dotnet "$UI_DLL" "$ROM_PATH"
