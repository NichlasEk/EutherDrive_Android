#!/bin/sh
set -eu
cd "$(dirname "$0")/../.."
mkdir -p .build-tmp/gauntlet-gpu-probe
glslangValidator -V tools/GauntletGpuProbe/sample.comp -o .build-tmp/gauntlet-gpu-probe/sample.spv
glslangValidator -V tools/GauntletGpuProbe/draw.comp -o .build-tmp/gauntlet-gpu-probe/draw.spv
g++ -std=c++17 -O2 -Wall -Wextra -Wno-missing-field-initializers \
    tools/GauntletGpuProbe/main.cpp -lvulkan -o .build-tmp/gauntlet-gpu-probe/gauntlet-gpu-probe
g++ -std=c++17 -O2 -Wall -Wextra -Wno-missing-field-initializers -fPIC -shared \
    tools/GauntletGpuProbe/shadow.cpp -lvulkan -o .build-tmp/gauntlet-gpu-probe/libgauntlet_shadow.so
