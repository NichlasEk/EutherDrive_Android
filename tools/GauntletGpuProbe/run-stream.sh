#!/bin/sh
set -eu
cd "$(dirname "$0")/../.."
capture_dir="${1:?usage: run-stream.sh stream-directory [--validate|--negative-control|--drop-texture-updates]}"
case "${2:-}" in
    --validate) export GAUNTLET_GPU_VALIDATION=1; suffix=validation ;;
    --negative-control) export GAUNTLET_GPU_CORRUPT_ORACLE=1; suffix=negative ;;
    --drop-texture-updates) export GAUNTLET_GPU_DROP_TEXTURE_UPDATES=1; suffix=drop-updates ;;
    '') suffix=gpu ;;
    *) echo "Unknown test option" >&2; exit 1 ;;
esac
[ -f "$capture_dir/boundary.txt" ] || { echo "Missing completed capture boundary" >&2; exit 1; }
set -- "$capture_dir"/draw-*.gdr
[ -f "$1" ] || { echo "No stream captures found" >&2; exit 1; }
expected_count=$(sed -n 's/^gpuStreamBoundary draws=\([0-9][0-9]*\) reason=.*/\1/p' "$capture_dir/boundary.txt")
[ "$#" = "$expected_count" ] || { echo "Capture count does not match boundary" >&2; exit 1; }
first="$1"
shift
log="$capture_dir/stream.$suffix.log"
if ! .build-tmp/gauntlet-gpu-probe/gauntlet-gpu-probe \
    "$first" .build-tmp/gauntlet-gpu-probe/draw.spv "$@" > "$log" 2>&1; then
    cat "$log"
    if [ "$suffix" = negative ] && rg -q '^1 color/depth mismatches$' "$log" && rg -q '^mismatch i=0 ' "$log"; then
        echo "negativeControl=PASS (corrupt oracle rejected)"; exit 0
    fi
    if [ "$suffix" = drop-updates ] && rg -q '^[1-9][0-9]* color/depth mismatches$' "$log"; then
        echo "negativeControl=PASS (missing texture updates detected)"; exit 0
    fi
    exit 1
fi
cat "$log"
case "$suffix" in negative|drop-updates) echo "Negative control did not cause a mismatch" >&2; exit 1 ;; esac
