#!/usr/bin/env sh
set -eu

SCRIPT_DIR="$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)"
REPO_ROOT="$(CDPATH= cd -- "$SCRIPT_DIR/.." && pwd)"

ROM_PATH="${1:-/home/nichlas/roms/MAME/Midway/Vegas/gauntd}"
SNAPSHOT_PATH="${2:-$REPO_ROOT/.build-tmp/euther-mame-phase1-fullgpu-f4783.warm.gz}"
SNAPSHOT_FRAMES="${3:-4783}"
CPU_STEPS="${4:-60000}"
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
# This checkpoint already contains MAME's live phase-1 task/RAM state. Starting
# the synthetic fallback task would duplicate the game loop and tear it down.
export EUTHERDRIVE_GAUNTDL_FIX_RUNTIME_GAME_TASK=0
# Never manufacture a frame by layering the incomplete live back buffer over
# an older front buffer.  That looked busy, but it was not a coherent game
# frame.  Show the live diagnostic surface until native redraw is complete.
export EUTHERDRIVE_GAUNTDL_EXPERIMENT_VOODOO_FORCE_RENDER_BUFFER_INDEX=0
unset EUTHERDRIVE_GAUNTDL_EXPERIMENT_VOODOO_COMPOSITE_BACK_BUFFER_OVER_COHERENT_FRAME
unset EUTHERDRIVE_GAUNTDL_EXPERIMENT_VOODOO_COMPOSITE_BASE_BUFFER_INDEX
unset EUTHERDRIVE_GAUNTDL_EXPERIMENT_VOODOO_COMPOSITE_LIVE_BUFFER_INDEX
export EUTHERDRIVE_GAUNTDL_EXPERIMENT_SUPPRESS_DIAGNOSTIC_RENDER_ENABLE=1
export EUTHERDRIVE_GAUNTDL_FIX_VOODOO_STANDARD_FIFO_DECODE_COMPLETE_PACKETS=1
export EUTHERDRIVE_GAUNTDL_UI_WARMUP_STATE="$SNAPSHOT_PATH"
export EUTHERDRIVE_GAUNTDL_UI_WARMUP_FRAMES="$SNAPSHOT_FRAMES"
export EUTHERDRIVE_GAUNTDL_UI_WARMUP_IGNORE_CPU_STEPS=1
export EUTHERDRIVE_GAUNTDL_UI_FORCE_SPEED_LOCK=1
export EUTHERDRIVE_GAUNTDL_UI_SPEED_SCALE=1.0
export EUTHERDRIVE_GAUNTDL_PROBE_DLL="$PROBE_DLL"

exec dotnet "$UI_DLL" "$ROM_PATH"
