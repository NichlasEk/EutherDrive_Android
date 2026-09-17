#!/bin/sh
set -eu
cd "$(dirname "$0")/../.."
capture_dir="${1:?usage: run.sh capture-directory [--validate|--negative-control]}"
case "${2:-}" in
    --validate) export GAUNTLET_GPU_VALIDATION=1; suffix=validation ;;
    --negative-control) export GAUNTLET_GPU_CORRUPT_ORACLE=1; suffix=negative ;;
    '') suffix=gpu ;;
    *) echo "Unknown test option" >&2; exit 1 ;;
esac
found=0
for capture in "$capture_dir"/triangle-*.gsc "$capture_dir"/draw-*.gdr; do
    [ -f "$capture" ] || continue
    found=1
    case "$capture" in *.gdr) shader=draw ;; *) shader=sample ;; esac
    log="$capture.$suffix.log"
    if ! .build-tmp/gauntlet-gpu-probe/gauntlet-gpu-probe \
        "$capture" ".build-tmp/gauntlet-gpu-probe/$shader.spv" > "$log" 2>&1; then
        cat "$log"
        if [ "$suffix" = negative ] && rg -q '^1 (RGBA|color/depth) mismatches$' "$log" && rg -q '^mismatch i=0 ' "$log"; then
            echo "negativeControl=PASS (corrupt oracle rejected)"
            exit 0
        fi
        exit 1
    fi
    if [ "$suffix" = negative ]; then
        echo "Corrupt oracle was not rejected" >&2
        exit 1
    fi
    echo "$capture"
    cat "$log"
done
[ "$found" = 1 ] || { echo "No captures found" >&2; exit 1; }
