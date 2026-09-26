#!/bin/sh
set -eu

repo_dir=$(CDPATH= cd -- "$(dirname -- "$0")/.." && pwd)
cd "$repo_dir"
build_dir="$repo_dir/.build-tmp/n64-live-gpu"
build_only=0
if [ "${1:-}" = "--build-only" ]; then build_only=1; shift; fi

if [ -z "${EUTHERDRIVE_N64_GPU_LIBRARY:-}" ]; then
    source_dir=${N64_GPU_SOURCE:-$repo_dir/.build-tmp/n64-gpu-probe/upstream}
    # Reuse the pinned development checkout on an existing workspace.
    if [ ! -d "$source_dir" ] && [ -z "${N64_GPU_SOURCE:-}" ] &&
       [ -d "$repo_dir/.build-tmp/n64-gpu-plan-2026-09-21/upstream" ]; then
        source_dir="$repo_dir/.build-tmp/n64-gpu-plan-2026-09-21/upstream"
    fi
    if [ ! -d "$source_dir" ]; then
        mkdir -p "$(dirname -- "$source_dir")"
        git clone https://github.com/Themaister/parallel-rdp.git "$source_dir"
        git -C "$source_dir" checkout 1cecd042b2619bc505c12bfdc713808386f2b54d
        git -C "$source_dir" submodule update --init --recursive --depth 1
    fi
    sh native/N64Gpu/build.sh "$source_dir" "$build_dir/native"
    EUTHERDRIVE_N64_GPU_LIBRARY="$build_dir/native/libeuther_n64_gpu.so"
fi
EUTHERDRIVE_N64_GPU_LIBRARY=$(realpath "$EUTHERDRIVE_N64_GPU_LIBRARY")
export EUTHERDRIVE_N64_GPU_LIBRARY
export EUTHERDRIVE_N64_GPU_VALIDATE=${EUTHERDRIVE_N64_GPU_VALIDATE:-0}
export PARALLEL_RDP_SMALL_TYPES=1
export PARALLEL_RDP_FORCE_SYNC_SHADER=1
# Direct command processing avoids the RDP command-ring cost in measured
# gameplay. Keep an explicit override for graphics bisects.
export PARALLEL_RDP_SINGLE_THREADED_COMMAND=${PARALLEL_RDP_SINGLE_THREADED_COMMAND:-1}
export GRANITE_NUM_WORKER_THREADS=${GRANITE_NUM_WORKER_THREADS:-2}
# Gauntlet slot 1 and later Mega Man states benefit from early GPU submission.
# Preserve an explicit off switch for other scenes and graphics bisects.
export EUTHERDRIVE_N64_GPU_OVERLAP=${EUTHERDRIVE_N64_GPU_OVERLAP:-1}
case "$EUTHERDRIVE_N64_GPU_VALIDATE" in
    0) export GRANITE_VULKAN_NO_VALIDATION=1 ;;
    1) export GRANITE_VULKAN_NO_VALIDATION=0 ;;
    *) echo 'EUTHERDRIVE_N64_GPU_VALIDATE must be 0 or 1' >&2; exit 1 ;;
esac

dotnet build EutherDrive.UI/EutherDrive.UI.csproj -c Release -m:1 \
    -p:N64LiveGpu=true -p:N64RdpJournalCapture=false -o "$build_dir/desktop" /clp:ErrorsOnly
echo 'Experimental N64 Vulkan RDP: start from ROM/reset. Loading an old save uses software.'
echo 'New GPU savestates resume with GPU rendering; use Save/Load in the savestate panel.'
if [ "$build_only" = 1 ]; then exit 0; fi
exec dotnet "$build_dir/desktop/EutherDrive.UI.dll" "$@"
