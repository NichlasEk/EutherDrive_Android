#!/bin/sh
set -eu
cd "$(dirname "$0")/../.."
source_dir=${1:?Usage: build.sh PREPARED_PARALLEL_RDP_CHECKOUT [BUILD_DIRECTORY]}
build_dir=${2:-.build-tmp/n64-gpu-probe/build}
source_dir=$(realpath "$source_dir")
expected=1cecd042b2619bc505c12bfdc713808386f2b54d
actual=$(git -C "$source_dir" rev-parse HEAD)
if [ "$actual" != "$expected" ]; then
    echo "Expected paraLLEl-RDP $expected, got $actual" >&2
    exit 1
fi
cmake -S tools/N64GpuProbe -B "$build_dir" -G Ninja \
    -DCMAKE_BUILD_TYPE=Release -DCMAKE_POLICY_VERSION_MINIMUM=3.5 \
    -DPARALLEL_RDP_SOURCE="$source_dir"
cmake --build "$build_dir" --target n64-gpu-probe rdp-validate-dump -j "${N64_GPU_BUILD_JOBS:-6}"
