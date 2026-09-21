# Offline N64 Vulkan replay proof

This Linux tool replays explicit RDP inputs through paraLLEl-RDP and compares
every byte of RDRAM, hidden memory and TMEM with its Angrylion reference. It
does **not** enable GPU rendering in EutherDrive. The runtime integration plan
and capture limitations are in [the design document](../../docs/n64-gpu-backend-plan-2026-09-21.md).

The next milestone's separate shared library and C# replay adapter are in
[native/N64Gpu](../../native/N64Gpu/README.md). This reference executable stays
a development tool; the shared library does not link its Angrylion dependency.
`--journal-reference` exports independent full-memory checkpoints for the
managed/native comparison without requiring a Vulkan device.

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

## Ordered multi-frame journals

The `NRDPJ001` path records **complete** commands from the real FIFO assembler,
plus exact external RDRAM write ranges before the next command. It retains
stores of unchanged values: GPU rendering may have left a different value in
its own memory. It never replaces a whole page when only a byte was written.
CPU/JIT stores, byte-indexer/bulk/mirrored writes, SP DMA and cartridge DMA are
covered. Repeated writes between two commands coalesce to their final bytes.
Software RDP output is excluded from these patches. A separate whole-RDRAM
shadow audit rejects changed bytes missed by the hooks. This diagnostic audit
is intentionally expensive and cannot detect an unhooked same-value store;
the write-path tests cover that separate case.

Build and capture into separate local directories. Do not use this instrumented
build for speed measurements or install it as the emulator:

```sh
dotnet build tools/N64Probe/N64Probe.csproj -c Release --no-restore -m:1 \
  -p:N64RdpJournalCapture=true -o "$PWD/.build-tmp/n64-journal/capture"
dotnet .build-tmp/n64-journal/capture/N64Probe.dll --check-rdp-journal

# ROM is a local cartridge path; no savestate argument means capture from reset.
N64_PROBE_RDP_JOURNAL_FRAMES=20 taskset -c 6,7 \
  dotnet .build-tmp/n64-journal/capture/N64Probe.dll "$ROM" \
  .build-tmp/n64-journal/mario-reset 180
dotnet .build-tmp/n64-journal/capture/N64Probe.dll --replay-rdp-journal \
  .build-tmp/n64-journal/mario-reset/journal

PARALLEL_RDP_SMALL_TYPES=1 GRANITE_NUM_WORKER_THREADS=2 GRANITE_VULKAN_NO_VALIDATION=0 \
  taskset -c 6,7 .build-tmp/n64-gpu-probe/build/n64-gpu-probe \
  .build-tmp/n64-journal/mario-reset/journal/journal.bin \
  .build-tmp/n64-journal/mario-gpu --negative-control
python3 tools/N64GpuProbe/check-journal.py \
  .build-tmp/n64-gpu-probe/build/n64-gpu-probe \
  .build-tmp/n64-journal/mario-reset/journal/journal.bin \
  .build-tmp/n64-journal/parser-checks
```

The capture ends at the requested number of FULL_SYNC checkpoints (1–120),
or fails if the time limit expires first. Output includes `start.bin`,
`journal.bin` and `manifest.json`; keep them together. `start.bin` is a software
Memory snapshot taken before the first recorded command. The already assembled
command is stored in the journal, so its transient complete FIFO is cleared
only while serializing that snapshot, then restored for live execution.

Managed replay requires exact SHA-256 matches for all RDRAM, hidden bits, TMEM
and TLUT at **every** checkpoint, plus image-register state and the DP interrupt
bit. A corrupt patch, wrong start state, missing record or incomplete file fails.
It can replay a warm savestate because it restores the software renderer state.
Native replay deliberately rejects warm captures: importing raw warm hardware
state is not implemented. Reset captures need no guessed register primer.

The native path keeps one renderer instance alive across all checkpoints and
compares paraLLEl-RDP with Angrylion at each FULL_SYNC. Captured software hashes
are checked by managed replay; they are not hardware-reference expectations.
Before each group of external writes it waits, reads back, modifies only the
recorded bytes in word-swapped RDRAM, and flushes caches. This preserves GPU
results in untouched neighbouring bytes. It is a conservative correctness
path with excessive synchronization, **not a performance result**. `--bench`
is rejected for journals. `--validate-journal` checks the format without Vulkan.

VI values are observations at command boundaries, not a record of every VI
write or scanout. CPU/DMA read dependencies, DPC submission timing and live
memory ownership are not captured yet. This replay does not prove that CPU
execution with GPU-generated data will behave identically. See the remaining
gates in the design document before enabling a live backend.

Rebuild normally after working on capture, and verify the absence of hooks
against an accepted pre-journal `Ryu64.MIPS.dll`:

```sh
dotnet build tools/N64Probe/N64Probe.csproj -c Release --no-restore -m:1 \
  -o "$PWD/.build-tmp/n64-journal/production"
dotnet .build-tmp/n64-journal/production/N64Probe.dll \
  --check-journal-production "$REFERENCE_MIPS_DLL"
```

### NRDPJ001 format

Integers are little-endian unless specified. The header is 8 ASCII magic bytes,
`u32 flags` (bit 0: started from reset; other bits rejected), `u32 rdramSize`
(8 MiB), `u32 hiddenSize` (4 MiB), 32 bytes SHA-256 of `start.bin`, then initial
RDRAM (big-endian emulated byte order) and hidden memory (logical halfword order).
Native conversion uses `rdram[i ^ 3]` and `hidden[i ^ 1]`.

Each record is `u8 kind, u32 payloadLength, u64 sequence, payload`; sequence
starts at 1 and increases by one. Length is bounded to 8 MiB + 4 bytes.

| Kind | Payload |
| --- | --- |
| 1: external write | `u32 byteOffset`, followed by 1 or more exact RDRAM bytes |
| 2: command | 2–44 `u32` raw command words; exact opcode length required |
| 3: VI observation | 14 registers in address order, 4 big-endian bytes each |
| 4: FULL_SYNC checkpoint | `u32 index`, 4 image values (color address, width, size, depth address), `u32 DP interrupt bit`, 4 SHA-256 hashes (RDRAM, hidden, TMEM, little-endian TLUT) |
| 5: end | `u32 checkpoints`, `u64 commands`, `u64 patches`, `u64 patchBytes` |

A FULL_SYNC command must immediately precede its checkpoint. End must follow a
checkpoint, its counts must match, and no trailing bytes are allowed. Software-
rendered pixels are never injected as correction patches to force a comparison
to pass.
