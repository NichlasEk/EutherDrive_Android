#!/bin/sh
set -eu
cd "$(dirname "$0")/../.."
source_dir=$(realpath "${1:?Usage: build.sh PREPARED_PARALLEL_RDP_CHECKOUT [BUILD_DIRECTORY]}")
build_dir=${2:-.build-tmp/n64-native-gpu/build}
expected=1cecd042b2619bc505c12bfdc713808386f2b54d
if [ "$(git -C "$source_dir" rev-parse HEAD)" != "$expected" ]; then
    echo "Expected paraLLEl-RDP $expected" >&2
    exit 1
fi
submodule_status=$(git -C "$source_dir" submodule status --recursive)
if printf '%s\n' "$submodule_status" | grep -Eq '^[-+U]'; then
    echo "Initialize all submodules at the pinned revisions before building" >&2
    exit 1
fi
if [ -n "$(git -C "$source_dir" status --porcelain --untracked-files=no)" ]; then
    echo "Use an unmodified pinned source tree for this validated backend" >&2
    exit 1
fi
python3 native/N64Gpu/prepare-state-source.py "$source_dir" "$build_dir/state-source"
prepared_dir=$(realpath "$build_dir/state-source")
cmake -S native/N64Gpu -B "$build_dir" -G Ninja \
    -DCMAKE_BUILD_TYPE=Release -DCMAKE_POLICY_VERSION_MINIMUM=3.5 \
    -DPARALLEL_RDP_SOURCE="$prepared_dir"
cmake --build "$build_dir" --target euther_n64_gpu -j "${N64_GPU_BUILD_JOBS:-6}"
