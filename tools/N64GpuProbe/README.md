# Offline N64 Vulkan replay proof

This Linux tool replays explicit RDP inputs through paraLLEl-RDP and compares
every byte of RDRAM, hidden memory and TMEM with its Angrylion reference. It
does **not** enable GPU rendering in EutherDrive. The runtime integration plan
and capture limitations are in [the design document](../../docs/n64-gpu-backend-plan-2026-09-21.md).

Requires .NET 8 for export; CMake, Ninja, a C/C++17 compiler and Vulkan for the
native test. The native probe refuses software/integrated-device timings.
Khronos validation is optional but required for the synchronization check.
The tested GPU is an RTX 4090 on Linux.

## Prepare and build

Keep the upstream checkout, dependencies, captured ROM-derived data and test
binaries under `.build-tmp/`, outside version control. The native oracle has
different licensing from paraLLEl-RDP; do not distribute this test executable
with the emulator or link `rdp-utils` into it.

```sh
git clone https://github.com/Themaister/parallel-rdp.git .build-tmp/n64-gpu-probe/upstream
git -C .build-tmp/n64-gpu-probe/upstream checkout 1cecd042b2619bc505c12bfdc713808386f2b54d
git -C .build-tmp/n64-gpu-probe/upstream submodule update --init --recursive --depth 1
sh tools/N64GpuProbe/build.sh .build-tmp/n64-gpu-probe/upstream
dotnet build tools/N64Probe/N64Probe.csproj -c Release --no-restore -m:1 /clp:ErrorsOnly
```

The build script checks the top-level upstream revision. Submodules are pinned
by that checkout; keep them unchanged. First build includes the shader compiler
and takes longer than later incremental builds. Two missing standard-header
includes in the pinned dependencies are supplied through CMake for GCC 15,
without editing upstream files. `N64_GPU_BUILD_JOBS` controls build concurrency.

## Export and compare

All output directories must be new. Set `TAPE` to a local directory containing
`rdp-start.bin`, `rdp-tape.bin` and the completed `rdp-final.ppm` marker.

```sh
dotnet tools/N64Probe/bin/Release/net8.0/N64Probe.dll --check-rdp-export
dotnet tools/N64Probe/bin/Release/net8.0/N64Probe.dll \
  --export-rdp-dump "$TAPE" .build-tmp/n64-gpu-probe/export

PARALLEL_RDP_SMALL_TYPES=1 GRANITE_NUM_WORKER_THREADS=2 GRANITE_VULKAN_NO_VALIDATION=0 \
  .build-tmp/n64-gpu-probe/build/n64-gpu-probe \
  .build-tmp/n64-gpu-probe/export/frame.rdp .build-tmp/n64-gpu-probe/validation

PARALLEL_RDP_SMALL_TYPES=1 GRANITE_NUM_WORKER_THREADS=2 GRANITE_VULKAN_NO_VALIDATION=1 \
  .build-tmp/n64-gpu-probe/build/n64-gpu-probe \
  .build-tmp/n64-gpu-probe/export/frame.rdp .build-tmp/n64-gpu-probe/negative --negative-control
```

Validation explicitly enables synchronization checking through instance
creation and requires the Khronos layer. Check `khronosValidation=1`,
`synchronizationValidation=1` and `vulkanValidationErrors=0`.
Any RDRAM/hidden/TMEM difference fails the ordinary
comparison. The negative control first requires an exact match, then flips
one reference framebuffer bit and requires exactly one differing byte.

Outputs `gpu.ppm` and `angrylion.ppm` decode the raw framebuffer at 240 rows.
These are not VI-filtered presentation images. Compare with the software tape
image separately: the hardware oracle and our existing software renderer have
different approximations. A hardware-reference pass alone does not validate
the conversion from the original live game.

## Timing

Stop other **test** processes before timing; do not terminate the user's app.
Use the same CPU affinity and alternate runs when comparing backends:

```sh
PARALLEL_RDP_SMALL_TYPES=1 PARALLEL_RDP_FORCE_SYNC_SHADER=1 \
  GRANITE_NUM_WORKER_THREADS=2 GRANITE_VULKAN_NO_VALIDATION=1 \
  taskset -c 6,7 .build-tmp/n64-gpu-probe/build/n64-gpu-probe \
  .build-tmp/n64-gpu-probe/export/frame.rdp .build-tmp/n64-gpu-probe/bench --bench
```

The first frame reports cold shader compilation. `FORCE_SYNC_SHADER` finishes
new pipeline compilation before continuing, keeping background compilation
out of warm comparisons. Then ten warmups precede 40
measured frames. Each starts from the same RDRAM/hidden memory and must produce
the same complete output as the first frame. The timer includes command
submission, rendering, completion and host waiting. Memory restoration, file
I/O, output copies, comparison and frame-context advancement are outside it.
Raw register/TMEM state is
not independently reset between repeats; repeatability is checked for these
fixtures, not asserted for arbitrary dumps. This is not the future managed/
native transfer cost or a game FPS result.

A second timer, `fixtureRestoreRenderReadbackMedianMs`, includes restoring the
entire RDRAM and hidden memory, rendering, waiting, invalidating caches and
copying all three output buffers into host vectors. It excludes comparison,
managed/native marshaling, byte-order conversion, frame-context advancement,
VI and presentation. This
deliberately large reset/copy workload shows the cost of moving whole memories;
it is neither the required per-frame transfer volume nor a live speed estimate.

For newly recorded tapes, `N64_PROBE_CAPTURE_RDP_AFTER_FULL_SYNC=1` alongside
`N64_PROBE_CAPTURE_RDP=1` waits for FullSync before taking the next SyncPipe as
the start. This reduces accidental mid-frame captures. It still does not
capture every interleaved RDRAM mutation or restore all raw hardware state.
The export manifest is part of the evidence and must stay with its dump.

The pinned upstream defaults NVIDIA to its 32-bit arithmetic path. On the
inspected setup that path emitted SPIR-V containing an 8-bit constant without
the arithmetic capability required by current validation. It matched pixels
but failed validation. `PARALLEL_RDP_SMALL_TYPES=1` selects the backend's
existing 8/16-bit arithmetic path, which passed both validation and the three
recorded-frame comparisons on this RTX 4090. This is a tested prototype
configuration, not a claim that the unmodified default or other GPUs passed.
