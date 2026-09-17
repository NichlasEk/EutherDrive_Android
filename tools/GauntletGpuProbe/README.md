# Gauntlet GPU sampling and draw proofs (Linux/Vulkan)

This is an **offline sampling experiment**, not a Voodoo GPU backend or a
playable-mode switch. It records real sampler calls from individual triangles,
then independently computes their RGBA results on a discrete GPU. No MIPS code
executes in the GPU probe. The existing C# sampler supplies the oracle and a
CPU-only replay timing while the guest is paused at the end of each triangle.

Included: signed 64-bit texture iterators, double-precision perspective divide,
coordinate saturation, negative-W clamp, resolved LOD addressing, clamp/wrap,
TMU memory banks, byte/lane selection, format decoding, NCC tables, and integer
bilinear filtering. Expected RGBA is never read by the shader.

**Not included in the sampler-only proof:** triangle coverage/interpolation, choosing per-pixel LOD,
combining the two TMUs, fog, depth, alpha blending, framebuffer writes,
presentation, texture-update scheduling, or CPU/GPU device synchronization in
the emulator. A result here is not a game FPS measurement.

## Build and capture

Run from the repo root. Requires .NET 8, a C++17 compiler, Vulkan development
headers/loader and `glslangValidator`. The GPU must expose `shaderInt64` and
`shaderFloat64`; the probe refuses a software-device fallback. This is not an
Android portability test.

```sh
dotnet build tools/GauntletProbe/GauntletProbe.csproj -c Release \
  --no-restore -m:1 /clp:ErrorsOnly -p:GauntletGpuCapture=true
sh tools/GauntletGpuProbe/build.sh
mkdir -p .build-tmp
capture_dir=$(mktemp -d .build-tmp/gpu-capture.XXXXXX)
DOTNET_TieredCompilation=0 EUTHERDRIVE_GAUNTDL_GPU_CAPTURE_DIR="$capture_dir" \
  scripts/run-gauntdl-probe-warm.sh \
  /home/nichlas/roms/MAME/Midway/Vegas/gauntd 7050 90000 \
  .build-tmp/gaunt-k2-clean2-f6750.warm.gz 6750
sh tools/GauntletGpuProbe/run.sh "$capture_dir"
sh tools/GauntletGpuProbe/run.sh "$capture_dir" --validate
sh tools/GauntletGpuProbe/run.sh "$capture_dir" --negative-control
```

The optional `EUTHERDRIVE_GAUNTDL_GPU_CAPTURE_SKIP=120` skips 120 eligible
triangles before recording. Each run records eight nonempty triangles with
bounding boxes of at least 8192 pixels. Raster workers are disabled only for
the selected triangles to make recording deterministic. Texture RAM and TMU
state cannot change during that triangle. Sample diagnostics must be disabled.
Files use `CreateNew`: reuse a fresh directory, not a previous capture target.

The CPU replay compares every sample after every pass; it measures the actual
C# sampler, serially and with eight persistent workers in 512-request chunks.
There are 12 passes, the first three excluded, then the median of nine.
`DOTNET_TieredCompilation=0` avoids changes in compilation tier during these
short batches. These timings include recorded-request traversal and worker
dispatch; they are not an exact timing of the live raster loop.

## GPU timing modes

Each mode likewise runs 12 times, discards three warmups, validates all output
outside the timer, and reports the median. GPU timestamps cover dispatch only.
Host timing includes submission, fence wait, transfer/readback and copying
results into CPU memory. Command recording, file I/O, capture construction,
NCC-table preparation and pipeline/device/buffer creation are excluded;
creation time is reported separately as `setupMs`.

- `full-upload`: copies all texture RAM, NCC state and requests to staging and
  uploads them every pass, then computes and reads back all results.
- `requests-upload`: textures stay on the GPU; header, NCC tables and all
  per-sample requests are recopied/uploaded each pass. Results are read back.
- `resident`: **all input, including requests, is already on the GPU**. Only
  computation and result readback occur. This is an optimistic boundary, not
  a complete live-rendering cost.

Readback prefers host-cached coherent memory. Upload-to-shader, shader-to-copy,
reuse and copy-to-host dependencies are explicit. `--validate` enables Khronos
validation plus synchronization validation and fails on validation errors.
Validation timings must not be used for performance conclusions. `--negative-control`
flips one CPU-oracle bit and succeeds only if exactly that mismatch is detected.
See the [Vulkan synchronization specification](https://docs.vulkan.org/spec/latest/chapters/synchronization.html)
for the API memory and execution dependencies used here.

## Capture format

Little-endian `uint32` words, with a maximum file size of 128 MiB:

- Header: `0x31435347`, version `1`, texture word count `2097152`, NCC word
  count `512`, request stride `20`, request count, internal render frame, zero.
- The complete 8 MiB raw texture memory, then two 256-entry packed RGBA NCC LUTs.
- Requests: words 0–5 are signed 64-bit S/T/W; 6–7 hold a double reciprocal
  override; 8 flags; 9 resolved LOD; 10/11 width/height; 12 base address;
  13 format; 14 NCC offset; 15 expected RGBA; 16/17 memory-bank base/mask;
  18 texture mode (provenance); 19 zero. RGBA bytes are R,G,B,A low-to-high.
- Flag bits 0–8: perspective, clamp-negative-W, filtered, clamp-S, clamp-T,
  flip-T, swap-16-bit-bytes, reverse-16-bit-lanes, reverse-8-bit-lanes.

Formats outside 0–4 and 8–13 are explicitly rejected. The shader has more
branches than the real captures exercised; the checkpoint lists actual test
coverage. It does not establish every format/edge case as correct.

Captures contain ROM-derived data and stay local under `.build-tmp/`. Never
commit them. Restore the regular build afterwards:

```sh
dotnet build tools/GauntletProbe/GauntletProbe.csproj -c Release --no-restore -m:1 /clp:ErrorsOnly
```

The capture calls are `[Conditional("GAUNTLET_GPU_CAPTURE")]`; normal builds
omit them entirely, including the per-sample hook. Nothing enables GPU execution
in the normal emulator. Results and next integration boundary are recorded in
[the checkpoint](../../docs/gauntlet-dl-gpu-sampling-proof-2026-09-10.md).

## Common-state draws and ordered batches

`EUTHERDRIVE_GAUNTDL_GPU_BATCH_STATS=1` adds per-draw counter validation to
`GPU_SHADOW_BATCH=1`. It permits a total draw limit up to 65,536 with automatic
flush/continuation at 128 draws, while keeping CPU rasterization as oracle.
Rebuild Core, native and shader together; native batch-statistics capability 1
is required. Add `GPU_REPLACE=1` for experimental batched replacement (see below).
Metadata word 119
is reserved for native-assigned statistics offsets. See
[batch statistics checkpoint](../../docs/gauntlet-dl-gpu-batch-statistics-2026-09-17.md).

Optional diagnostic extension: `EUTHERDRIVE_GAUNTDL_GPU_EXTENDED_COLOR_PATH=1`
also admits two strictly gated `0x0c602c19` color-path/TMU combinations.
Rebuild capture-Core, native library and shader together. Metadata word 28
selects the color path; words 116–118 hold iterated-alpha gradients.
The extension is off by default and has not improved replay speed yet.
See [extended color-path checkpoint](../../docs/gauntlet-dl-gpu-extended-color-path-2026-09-17.md).

Value `2` of the same flag adds three more strictly allowlisted combinations,
including the observed depth-write-disabled state. Value `1` is unchanged.
Both levels remain diagnostic: the larger coverage currently increases total
replay time with synchronous per-draw submission. See
[level 2 checkpoint](../../docs/gauntlet-dl-gpu-color-level2-2026-09-17.md).

Value `3` also admits the exact FBZ `000b4779`, TMU0 `8c24110f`, TMU1
`8c2410cf` combination with the existing extended color/alpha/fog gates.
This reuses existing TMU1 format-0 sampling; native/shader/ABI are unchanged.
Levels 1 and 2 retain their existing allowlists. For CPU-only cost diagnosis,
`EUTHERDRIVE_GAUNTDL_GPU_TARGET_PROFILE=1` logs this state's per-draw raster
time and pixel counts in a capture-build; GPU execution and file capture are
rejected while this profiler is enabled. Logging is outside the timed raster
window. See [level 3 checkpoint](../../docs/gauntlet-dl-gpu-color-level3-2026-09-17.md).

`EUTHERDRIVE_GAUNTDL_GPU_FENCE_POLL=1` optionally polls draw-fence status for
a 50-microsecond deadline before the unchanged mandatory fence wait.
It remains synchronous and may consume additional CPU/energy. It is off by
default. See [fence polling measurements](../../docs/gauntlet-dl-gpu-fence-poll-2026-09-17.md).

`draw.comp` adds coverage, fixed-point gradients, perspective divide,
per-pixel LOD, both TMUs and combination, fog, alpha blending, RGB565/depth
writes. It shares `sampling.glsl` with the sampler. Only the existing
`useProfiledCommonRasterKernel` state family is supported, not arbitrary
Voodoo states. Capture requires fixed setup/fetch/pixel-LOD and no sampler
capture/diagnostics. CPU raster parallelism stays unchanged.

Build with `-p:GauntletGpuCapture=true` as above, then replace the sampler
directory variable with `EUTHERDRIVE_GAUNTDL_GPU_DRAW_DIR`.
`EUTHERDRIVE_GAUNTDL_GPU_DRAW_SKIP=120` selects a later window. `run.sh`
also tests `.gdr`, including `--validate` and `--negative-control`.
Restore the normal build afterwards; its draw hooks are compiled out.

GDR1: eight header words (magic `0x31524447`, version 1, texture words
2097152, NCC words 512, metadata words 256, rectangle pixels, render frame,
buffer index), raw textures, NCC, 256 metadata words (see
`VoodooBringupBackend.GpuDrawCapture.cs`), initial rectangle, expected
rectangle. Pixels pack RGB565 low and depth high. Expected draw pixels
are **never uploaded to the GPU**.

Replay compatible draws with one submission and readback:

```sh
.build-tmp/gauntlet-gpu-probe/gauntlet-gpu-probe \
  "$capture_dir/draw-00.gdr" .build-tmp/gauntlet-gpu-probe/draw.spv \
  "$capture_dir/draw-01.gdr" "$capture_dir/draw-02.gdr" "$capture_dir/draw-03.gdr"
```

The host rejects changed texture/NCC state or buffer index, and checks
every overlapping pixel's pre-state against the preceding post-state.
These are selected compatible draws, not a full command stream. The union
rectangle uses first-observed initial pixels; uncovered holes are zero-filled
test padding, not observed guest framebuffer content.

Each repetition resets shared output with one device-local copy, then
dispatches draws with compute read/write barriers between them. There is
no CPU readback between triangles. `draw-upload` retains textures but uploads
metadata, NCC and initial framebuffer. `resident` retains all inputs but
still resets output device-locally. Host timing includes reset and final
readback; GPU timestamps cover dispatches/inter-draw barriers, not reset.
`cpuRasterMs` is a single instrumented live draw, not a warmed comparable CPU
benchmark. Do not derive game speedups from it.

See [draw results and next boundary](../../docs/gauntlet-dl-gpu-draw-proof-2026-09-10.md).

## Contiguous segments with texture/NCC updates (GDR2)

Set `EUTHERDRIVE_GAUNTDL_GPU_STREAM_DIR` instead of `GPU_DRAW_DIR` in a
capture-enabled build. `EUTHERDRIVE_GAUNTDL_GPU_DRAW_SKIP` still chooses the
first eligible large triangle. Once started, capture includes small common-state
draws too. It stops after 32 draws or a boundary: unsupported rasterization,
clear, actual swap, LFB read/write, host presentation read, draw-buffer change,
or an aliased Y origin. These operations **end** the segment; they are not yet
executed on the GPU. `boundary.txt` records the reason and expected draw count.

```sh
stream_dir=$(mktemp -d .build-tmp/gpu-stream.XXXXXX)
DOTNET_TieredCompilation=0 EUTHERDRIVE_GAUNTDL_GPU_STREAM_DIR="$stream_dir" \
  EUTHERDRIVE_GAUNTDL_GPU_DRAW_SKIP=4 scripts/run-gauntdl-probe-warm.sh \
  /home/nichlas/roms/MAME/Midway/Vegas/gauntd 7050 90000 \
  .build-tmp/gaunt-k2-clean2-f6750.warm.gz 6750
sh tools/GauntletGpuProbe/run-stream.sh "$stream_dir"
sh tools/GauntletGpuProbe/run-stream.sh "$stream_dir" --validate
sh tools/GauntletGpuProbe/run-stream.sh "$stream_dir" --negative-control
sh tools/GauntletGpuProbe/run-stream.sh "$stream_dir" --drop-texture-updates
sh tools/GauntletGpuProbe/run.sh "$stream_dir" # each draw independently
```

The last negative control requires updates that actually affect output; a
segment without such updates cannot pass that test. The runner checks a
completed boundary and file count. The native program rejects any missing
framebuffer transition instead of repairing it with oracle pixels.

GDR2 uses magic `0x32524447`, version 2, otherwise the GDR1 header layout.
Pixel count is exactly 2097152: the entire selected physical color buffer
and shared auxiliary buffer, not all three color buffers. Metadata word 23
is 1, word 30 is signed raster Y origin (-1 means direct mapping). Both
initial and expected buffers use physical order. The shader maps logical
coordinates to physical addresses; aliased/clamped Y layouts are rejected.
Every capture is approximately 24 MiB, up to 768 MiB per segment. Keep local.

The host compares **all** pre/post pixels across draws and diffs texture/NCC
snapshots into 1 KiB pages. Adjacent changed pages are coalesced into copy
regions. Immutable patch payloads are uploaded once per batch, then copied
to the shared device input immediately before the corresponding draw with
transfer/compute dependencies. No oracle or intermediate framebuffer is
uploaded between draws. Every repetition resets changed input pages to the
first draw's state (also for `resident`). Metrics include those reset patches.

`draw-upload` now includes patch payloads plus the full initial framebuffer.
`resident` retains all input and still performs device-local resets and a full
8 MiB result readback. GPU timestamps include input reset patches, ordered
updates and dispatches, but exclude initial framebuffer reset/readback.
Snapshot diffing, file I/O and command recording are outside the timing.

Real tested segments changed NCC tables, not raw texture RAM. A separate
metamorphic test rotates texture banks by 1024 bytes and moves every LOD base
equally in alternate draws. Expected pixels must remain unchanged. It is
test-only input transformation, **not a newly observed guest texture upload**:

```sh
GAUNTLET_GPU_TEST_TEXTURE_RELOCATION=1 GAUNTLET_GPU_VALIDATION=1 \
  .build-tmp/gauntlet-gpu-probe/gauntlet-gpu-probe \
  "$stream_dir/draw-00.gdr" .build-tmp/gauntlet-gpu-probe/draw.spv \
  "$stream_dir/draw-01.gdr"
```

Add `GAUNTLET_GPU_DROP_TEXTURE_UPDATES=1` to verify missing texture copies
produce mismatches. ROM-free call-site tests run with
`EUTHERDRIVE_GAUNTDL_TEST_GPU_STREAM_BOUNDARIES=capture` on a capture-enabled
GauntletProbe binary, then `=normal` after restoring the regular build.
They check eleven boundary sites, including that ordinary builds omit hooks.

[Segment checkpoint and limitations](../../docs/gauntlet-dl-gpu-stream-proof-2026-09-10.md).

## In-process runtime shadow comparison

`build.sh` also builds `libgauntlet_shadow.so`. A capture-enabled Core can
call this library directly, without capture files or subprocesses:

```sh
sh tools/GauntletGpuProbe/build.sh
dotnet build tools/GauntletProbe/GauntletProbe.csproj -c Release \
  --no-restore -m:1 /clp:ErrorsOnly -p:GauntletGpuCapture=true
DOTNET_TieredCompilation=0 EUTHERDRIVE_GAUNTDL_GPU_SHADOW=1 \
  GAUNTLET_GPU_VALIDATION=1 scripts/run-gauntdl-probe-warm.sh \
  /home/nichlas/roms/MAME/Midway/Vegas/gauntd 7950 90000 \
  .build-tmp/gaunt-k2-clean2-f6750.warm.gz 6750
```

The default native library/shader paths are under `.build-tmp/gauntlet-gpu-probe`
relative to the working directory. Override with
`EUTHERDRIVE_GAUNTDL_GPU_SHADOW_LIBRARY` and
`EUTHERDRIVE_GAUNTDL_GPU_SHADOW_SHADER`. ABI version 4 is checked on load.
Shadow mode is mutually exclusive with draw/stream file capture. Existing
draw-skip selects the first eligible large triangle.

One native Vulkan context persists across segments. A segment starts with
CPU color/depth initialization. Between draws, the GPU framebuffer remains
resident: **no CPU result is uploaded**. At an existing boundary the segment
ends; unsupported work stays on the CPU, and the next eligible common draw
starts a new segment from current CPU state. Every GPU draw completes before
the CPU draw, then the entire selected physical color/depth buffer is compared
after the CPU draw. This is synchronous double execution, not parallel or
accelerated gameplay. Failures abort the diagnostic run; no silent recovery
or replacement of guest pixels occurs.

By default the test stops after 128 verified draws and disposes the native context.
Backend reset also closes it; a managed finalizer is a last-resort cleanup.
In this per-draw mode, every draw uploads complete texture/NCC/metadata and reads back
8 MiB of output. Queue submission/fence/readback happen per draw, not per
segment. Use queued runtime batches below for dirty-page batching.

`EUTHERDRIVE_GAUNTDL_GPU_SHADOW_CORRUPT_ORACLE=1` flips only a local expected
bit, not emulated memory. The diagnostic must fail with
`GPU shadow color/depth mismatches=1 first=0`. Use it only as a negative test.
Restore the normal build afterwards; it neither loads the library nor runs
shadow hooks, even if these environment variables remain set.

[Runtime shadow checkpoint](../../docs/gauntlet-dl-gpu-runtime-shadow-2026-09-10.md).

### Queued runtime batches

Add `EUTHERDRIVE_GAUNTDL_GPU_SHADOW_BATCH=1` to the shadow command to queue
draws until a segment boundary. Without this flag, the per-draw comparison
mode above remains available. Rebuild the native library and capture-enabled
Core together after the ABI update.

In batch mode, enqueue copies current metadata and diffs texture/NCC snapshots
into 1 KiB pages, coalescing adjacent changes. The initial selected color/depth
buffer is copied only at segment start. CPU draws then proceed normally.
At the boundary, all queued draws and ordered input updates are submitted
together, followed by one full readback and comparison against current CPU
buffers, before the boundary's CPU operation executes. Reset flushes and
compares an active segment before releasing its native context.

By default each batch uploads its initial full texture/NCC state and initial
framebuffer, metadata, and patch payloads. Between draws it uploads no CPU
framebuffer results. By default, texture change detection scans/copies CPU snapshots.
With `GPU_BATCH_STATS=1`, `GPU_INCREMENTAL=1`, `GPU_SPARSE_SNAPSHOT=1` and
`GPU_DIRTY_TEXTURE=1` (all with the `EUTHERDRIVE_GAUNTDL_` prefix), the native
`gauntlet_shadow_dirty_enqueue` entry point uses the emulated texture writer's
dirty pages and copies only changed snapshot pages. NCC pages are always checked.
`GPU_VERIFY_DIRTY=1` independently scans skipped pages and rejects missed writes.
Capacity
is bounded to 128 draws and 64 MiB of input; exceeding it aborts the diagnostic
run instead of silently dropping work. Without batch statistics the overall
shadow limit remains 128; with statistics, full batches flush and continue up
to the configured total limit (maximum 65,536). A finalizer can release resources but cannot verify an unfinished batch;
require a completed shadow-limit/boundary log when evaluating a run.

`gpuShadowTotals` reports submissions, uploaded/readback bytes and patch bytes.
These counters are transfer costs, not game FPS. CPU rendering still supplies
all game results. Batch comparison is at the segment's final state; retain
per-draw mode to detect transient errors that later draws could overwrite.

Negative tests:

- `EUTHERDRIVE_GAUNTDL_GPU_SHADOW_CORRUPT_ORACLE=1`: must detect one wrong bit.
- `GAUNTLET_GPU_DROP_TEXTURE_UPDATES=1`: skips inter-draw device copies;
  tested real segments must produce pixel mismatches.
- `python3 tools/GauntletGpuProbe/test-shadow-batch.py .build-tmp/gpu-stream-first`:
  uses the first two local GDR2 captures to check empty flush, reset ordering,
  and pixel-preserving raw-texture relocation, including omitted-copy failure.
  The relocation is a constructed test, not an observed guest upload.

[Batch checkpoint](../../docs/gauntlet-dl-gpu-runtime-batch-2026-09-10.md).

## Resident framebuffer replacement

`EUTHERDRIVE_GAUNTDL_GPU_DIRTY_TEXTURE=1` tracks physical texture writes in
the diagnostic Core, passing a per-draw 1 KiB dirty-page mask to the native
renderer. Requires resident replacement, incremental uploads and sparse CPU
snapshots. NCC's two pages still use content comparison. Resets fully upload;
backend reset disposes the old session. Probe snapshot restoration drains and
stops an active session before overwriting buffers. Direct reflective texture
pokes during a session are unsupported; startup oracle imports occur before it.
`EUTHERDRIVE_GAUNTDL_GPU_VERIFY_DIRTY=1` additionally checks supposedly clean
pages and aborts on a missed change; omit it for performance measurements.
Native adds `gauntlet_shadow_dirty_draw` as an optional ABI-v4 extension: rebuild
both Core and native. Existing entrypoints retain snapshot-scanning behavior.
See [dirty tracking checkpoint](../../docs/gauntlet-dl-gpu-dirty-texture-2026-09-17.md).

`EUTHERDRIVE_GAUNTDL_GPU_SPARSE_SNAPSHOT=1` additionally updates only changed
1 KiB pages in the native CPU texture/NCC mirror when incremental uploads are
active. Segment resets still copy the full mirror. Every page is still scanned;
this is not write-path dirty tracking. `gpuSnapshot copiedBytes` counts actual
texture/NCC mirror copies in synchronous draws, excluding metadata/framebuffer
packing and host staging. The default remains the full snapshot copy path.
See [sparse snapshot experiment](../../docs/gauntlet-dl-gpu-sparse-snapshot-2026-09-11.md).

`EUTHERDRIVE_GAUNTDL_GPU_PROFILE=1` enables opt-in aggregate timings printed
on session disposal. `gpuProfileHost` measures native initialization, snapshot
preparation, command recording, host staging copies, queue submission, fence
wait, query retrieval and full-pixel readback calls. `gpuProfileManaged`
measures unpacking completed pixels into CPU color/depth arrays (`applyPixelsMs`),
the complete managed Render and FlushBatch calls (`renderInclusiveMs` and
`flushInclusiveMs`, including native work), and replacement batch statistics
allocation/copy (`batchStatisticsMs`). Call counts allow checking draw/submission
coverage. Inclusive times overlap native totals: do not add them together.
They do not cover backend eligibility checks, metadata construction before Render,
or CPU fallback rasterization. Timers run only with profiling enabled; no per-draw
timing log is emitted. The managed summary uses invariant decimal formatting.
`gpuProfileDevice` uses Vulkan timestamps for pre-dispatch (upload/setup),
dispatch (including inter-draw barriers and texture patches), post-dispatch
(barriers/output copy), and boundary pixel readback.
Device times overlap host waits; do not add them to host timings. These are
coarse pipeline intervals, not isolated shader-instruction or PCIe bandwidth
measurements. Readback host time includes its recording/submission/wait/query.
Snapshot preparation is timed for synchronous draws and batch enqueue.
`gpuProfileBatchPrepare` further splits batch preparation into fresh framebuffer/
texture packing (`resetMs`), resident batch zero-initialization (`continuationMs`),
page scanning and patch construction (`scanPatchMs`), and full texture/NCC mirror
copies (`mirrorMs`). These intervals are nested inside `prepareMs`; their sum
does not include validation, metadata, environment checks and other bookkeeping.
Rebuild native and capture-enabled Core; ABI remains v4. Profiling defaults off.
Measured results and timing boundaries are in the
[CPU/GPU profile](../../docs/gauntlet-dl-gpu-profile-2026-09-11.md).
For resident batches and color levels 2/3, see the
[batch cost profile](../../docs/gauntlet-dl-gpu-cost-profile-2026-09-17.md).

`EUTHERDRIVE_GAUNTDL_GPU_BBOX=1` optionally restricts each synchronous or batched runtime
draw dispatch to its metadata bounding box. The push constants map invocation
indices to that box, while GDR2 still writes to physical color/depth addresses.
The shader's output/statistics buffer layout is unchanged. Batches retain an
individual dispatch size for each draw; offline draws retain their original
dispatch. Rebuild native; ABI remains v4.
`gpuDispatch invocations` counts launched invocations, including 128-thread
workgroup rounding. The flag also works with per-draw CPU/GPU shadow checks.
See [bounding-box results](../../docs/gauntlet-dl-gpu-bbox-proof-2026-09-11.md).

`EUTHERDRIVE_GAUNTDL_GPU_DRAW_LIMIT=4096` expands the diagnostic runtime
window (default 128, accepted range 1–65536). Batches without statistics remain
capped at 128; offline capture limits are unchanged. The limit is cached per backend.
Probe explicitly drains/disposes an active GPU session at the timed endpoint,
even when fewer eligible draws occur. A completed run must report no pending
pixels. The `displayRate` line measures the delta in executed swap commands
over the timed replay, excluding extra post-replay CPU steps; this is not a
measurement of distinct host-presented frames or input latency.

The expanded experiment and CPU comparison are recorded in the
[expanded-window checkpoint](../../docs/gauntlet-dl-gpu-expanded-window-2026-09-11.md).

Optional incremental texture/NCC uploads are enabled with
`EUTHERDRIVE_GAUNTDL_GPU_INCREMENTAL=1` in resident replacement mode.
The first draw of each segment still uploads everything; subsequent draws
compare 1 KiB pages against the last submitted CPU snapshot and copy only
changed pages plus metadata, including sparse host staging copies. This is
snapshot comparison, not write-path dirty tracking: the CPU still scans and
copies the full texture snapshot per draw. ABI remains v4; rebuild native.

To test updates, run the resident test below with both
`EUTHERDRIVE_GAUNTDL_GPU_INCREMENTAL=1` and
`GAUNTLET_GPU_TEST_TEXTURE_RELOCATION=1`. Adding
`GAUNTLET_GPU_DROP_TEXTURE_UPDATES=1` must produce pixel mismatches, which this
negative test expects. Relocation is synthetic, not an observed guest upload.
See [incremental checkpoint](../../docs/gauntlet-dl-gpu-incremental-proof-2026-09-11.md).

Add `EUTHERDRIVE_GAUNTDL_GPU_RESIDENT=1` to the replacement command below.
Rebuild both the native library and capture-enabled Core (ABI v4).
This keeps color/depth on the GPU between draws: only 64 bytes of immediate
raster statistics are read back per draw. Existing CPU/fallback/presentation
boundaries explicitly read back and apply the full framebuffer before CPU use.
The default 128-draw limit remains; per-draw fences remain too. Without the
incremental flag, full texture uploads also remain.
This is not yet a demonstrated gameplay speedup.

`python3 tools/GauntletGpuProbe/test-resident.py .build-tmp/gpu-stream-first`
checks retained pixels and invalid readback/reset ordering using two real draws.
See [resident checkpoint](../../docs/gauntlet-dl-gpu-resident-proof-2026-09-11.md)
for full-state verification and transfer counts.

## Bounded GPU replacement experiment

`EUTHERDRIVE_GAUNTDL_GPU_REPLACE=1` enables actual replacement of the common
CPU pixel loop for up to 128 selected draws by default. Build the native library and
capture-enabled Core as above; ABI version 4 includes GPU raster statistics
and explicit resident framebuffer readback.
For experimental batch replacement set `EUTHERDRIVE_GAUNTDL_GPU_SHADOW_BATCH=1`
and `EUTHERDRIVE_GAUNTDL_GPU_BATCH_STATS=1`. Do **not** set `GPU_RESIDENT`:
batch replacement by default reads back the full framebuffer at each boundary,
including capacity boundaries. Pixel, LOD, covered/rejected-triangle and empty
raster counters are consumed in draw order at flush, before CPU readers/writers.
Triangle-edge visualization, covered/rejected-triangle tracing and raster-state
profiling are explicitly rejected because they need immediate per-draw results.
Debug/profile status reads also flush. Ordinary non-capture builds ignore this
path. See [batch replacement checkpoint](../../docs/gauntlet-dl-gpu-batch-replacement-2026-09-17.md).

`EUTHERDRIVE_GAUNTDL_GPU_BATCH_RESIDENT=1` additionally retains GPU color/depth
and texture/NCC across capacity boundaries. It requires batched statistics,
works with both shadow and replacement, and is distinct from per-draw
`GPU_RESIDENT` (do not combine them). Capacity flush reads only the 128-row
statistics table; the next batch uploads metadata and texture/NCC patches.
True CPU boundaries still read/compare or apply all pixels, even when no new
draw was enqueued after the capacity flush. Native batch-resident capability 1
and `gauntlet_shadow_flush_keep` are required. Enqueue reset modes are 0
(append), 1 (fresh CPU snapshot), and 2 (continue a retained batch). Mode 2
is rejected after a full readback or without a preceding keep flush.
See [resident batch checkpoint](../../docs/gauntlet-dl-gpu-resident-batches-2026-09-17.md).

```sh
DOTNET_TieredCompilation=0 EUTHERDRIVE_GAUNTDL_GPU_REPLACE=1 \
  GAUNTLET_GPU_VALIDATION=1 scripts/run-gauntdl-probe-warm.sh \
  /home/nichlas/roms/MAME/Midway/Vegas/gauntd 7950 90000 \
  .build-tmp/gaunt-k2-clean2-f6750.warm.gz 6750
```

The GPU result is copied into the real selected color/depth buffers, while
GPU statistics reproduce the CPU loop's sample/zero/fallback/write/common
pixel counters, LOD histogram, LFB write activity, buffer activity and coverage
return value. CPU setup and post-draw processing still run. Unsupported states
and pixel-writer tracing stay on the CPU; sampler diagnostics must be disabled.
After the configured draw limit,
the native context is released and all rendering continues on the CPU.

Per-draw shadow mode now independently compares those counters against the
CPU, in addition to comparing every color/depth pixel. The output buffer has
16 statistic words per draw, zeroed before dispatch (128 rows for batches);
metadata word 31 enables native runtime statistics. Offline GDR captures must
keep that flag zero. Batch shadow supports both statistics-off and independent
per-draw CPU-oracle validation modes.

`EUTHERDRIVE_GAUNTDL_GPU_COUNTER_CORRUPT_ORACLE=1` changes only a local expected
counter and must trigger a counter mismatch. Pixel-oracle corruption is a
shadow-only test, since replacement mode does not execute a CPU pixel oracle.
Use shadow tests and full-state replay comparison to validate replacement.

This is **not yet a faster playable mode**: every replaced draw still uploads
full texture state, synchronizes, reads back the full framebuffer, and copies
it into CPU buffers. It is deliberately not the queued shadow path. Normal
builds compile out the replacement branch and ignore its environment flag.

[Replacement checkpoint](../../docs/gauntlet-dl-gpu-replacement-proof-2026-09-11.md).
