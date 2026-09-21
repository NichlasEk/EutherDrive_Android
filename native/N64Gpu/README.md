# Optional Linux N64 GPU boundary

`libeuther_n64_gpu.so.1` is a headless, versioned C ABI around the pinned
paraLLEl-RDP backend. `Ryu64Core.N64GpuBackend` loads it explicitly through a
`SafeHandle`. The normal emulator still renders in software. This checkpoint
replays ordered journals; it does not run guest CPU/RSP execution against GPU
output or present images to the frontend.

See [results and remaining gates](../../docs/n64-native-gpu-2026-09-21.md)
and the [integration plan](../../docs/n64-gpu-backend-plan-2026-09-21.md).

## Build

Prepare the pinned checkout using [N64GpuProbe's instructions](../../tools/N64GpuProbe/README.md).
Requires Linux, a little-endian host, C++17, CMake, Ninja and Vulkan; the managed
adapter uses .NET 8. Only Linux x86-64 with an RTX 4090 is validated here.

```sh
sh native/N64Gpu/build.sh .build-tmp/n64-gpu-probe/upstream \
  .build-tmp/n64-native-gpu/build
dotnet build tools/N64Probe/N64Probe.csproj -c Release --no-restore -m:1 \
  -p:N64RdpJournalCapture=true -o "$PWD/.build-tmp/n64-native-gpu/probe"
```

The script requires unmodified paraLLEl-RDP revision
`1cecd042b2619bc505c12bfdc713808386f2b54d` and its pinned submodules.
The conservative address proof depends on that renderer implementation and
unscaled rendering. Re-audit it when upgrading the backend.

The shared-library dependency closure includes the permissive backend and
Granite dependencies. It excludes `alp-core`, `rdp-utils` and Angrylion, whose
legacy MAME license restricts commercial use. The separate reference probe
remains a development tool. Do not ship it, upstream source caches, ROM-derived
fixtures or local build output with the emulator.

The library currently compiles shaders from its build-time upstream directory.
Shader packaging, notices and distribution are still open work. This is not
yet a relocatable emulator package.

## ABI contract

See [n64_gpu.h](n64_gpu.h) for exact layouts. ABI 1 owns a native aligned staging
mirror: 8 MiB RDRAM, 4 MiB hidden memory, and opaque 4 KiB TMEM for validation.
Caller RDRAM is big-endian emulated bytes. The bridge handles native word and
halfword conversion, including unaligned byte patches. TMEM bytes are not a
portable savestate format. Initial raw renderer state is reset state only.

Each submission contains ordered write ranges, complete raw commands and VI
observations. The whole envelope is validated before any command executes.
All input pointers are borrowed only during the call. The caller must keep
buffers stable during it. Writes preserve byte lanes, order, and same-value
stores; unchanged neighbouring bytes are never copied from a stale mirror.

Submit returns a timeline; it does not imply GPU completion. `wait` waits for
completion, while `readback` waits and returns **the latest submission's** full
memory. It cannot retrieve historical snapshots. All public submissions finish
staging their writes before returning, even without FULL_SYNC. VI observations
do not trigger scanout or interrupts. Error code 2 invalidates the context's
use contract: destroy it, without attempting partial replay against modified
memory. Device-loss recovery and timeout guarantees are not implemented.

The API serializes calls and validates integer handles. Each instance owns its
Granite managers, workers and Vulkan device. Thread-local managers are rebound
on every call, including finalization from another thread. Managed calls hold
a SafeHandle lease so concurrent disposal cannot unload the library mid-call.

Flags:

| Bit | Behavior |
| --- | --- |
| 1 | Require Khronos validation, including synchronization validation |
| 2 | Defer writes across whitelisted state-only commands |
| 4 | Require a discrete GPU |
| 8 | Also defer writes across draws with provably disjoint RDRAM ranges; includes bit 2's behavior |

No batching flag means a conservative barrier before every non-write record.
Bit 8 considers both color and depth attachments, a full 1024-row pass, every
color size, address alignment and RAM wrap. Missing target state forces a
barrier. Texture loads, unknown opcodes, FULL_SYNC and submission boundaries
always flush pending writes. This only reorders buffered writes inside an
offline batch; it does not solve live CPU/GPU memory ownership.

ABI 1 requires successful coherent `VK_EXT_external_memory_host` import. It
rejects the backend's separate copy/mask fallback rather than using it without
validation. The tested NVIDIA configuration uses `PARALLEL_RDP_SMALL_TYPES=1`;
the pinned default shader path has an outstanding validation issue described
in N64GpuProbe's README.

## Correctness checks

Use new output directories. `$LIB`, `$PROBE`, `$ORACLE` and `$JOURNAL` below
refer to the built library, managed probe DLL, separate native reference probe,
and an existing reset journal directory.

```sh
PARALLEL_RDP_SMALL_TYPES=1 GRANITE_NUM_WORKER_THREADS=2 GRANITE_VULKAN_NO_VALIDATION=0 \
  taskset -c 6,7 python3 native/N64Gpu/check_abi.py "$LIB" .build-tmp/n64-native-gpu/abi
PARALLEL_RDP_SMALL_TYPES=1 GRANITE_NUM_WORKER_THREADS=2 GRANITE_VULKAN_NO_VALIDATION=0 \
  taskset -c 6,7 dotnet "$PROBE" --check-gpu-abi "$LIB"

# Export independent Angrylion checkpoints without Vulkan or production linkage.
GRANITE_NUM_WORKER_THREADS=2 "$ORACLE" "$JOURNAL/journal.bin" \
  .build-tmp/n64-native-gpu/reference --journal-reference
PARALLEL_RDP_SMALL_TYPES=1 GRANITE_NUM_WORKER_THREADS=2 GRANITE_VULKAN_NO_VALIDATION=0 \
  taskset -c 6,7 dotnet "$PROBE" --replay-gpu-journal "$LIB" "$JOURNAL" \
  .build-tmp/n64-native-gpu/reference .build-tmp/n64-native-gpu/checked ranges

# Synthetic color/depth/texture hazards: capture, export and replay as above.
dotnet "$PROBE" --capture-gpu-hazards .build-tmp/n64-native-gpu/hazards
```

Replay modes are `strict`, `batched` (state-only), and `ranges`. Every mode must
match the reference RDRAM, hidden memory and TMEM at every checkpoint. Warm
captures are rejected because raw hardware-state import is not implemented.
The native ABI test intentionally submits one hardware-invalid I4 fill and
requires a reported backend failure; its validation message is expected.

Reference files concatenate 8 MiB native word-swapped RDRAM, 4 MiB native
halfword-swapped hidden memory and 4096 opaque TMEM bytes, named
`frame-0001.bin`, etc. Managed replay converts them to CPU order before hashing.
They describe the independent reference, not the software capture hashes.

## Timing

```sh
python3 native/N64Gpu/benchmark-journal.py "$PROBE" "$LIB" "$JOURNAL" \
  .build-tmp/n64-native-gpu/reference .build-tmp/n64-native-gpu/interleaved
```

This runs serially on cores 6 and 7, validates both paths first, then alternates
three strict and three range-batched runs with validation disabled and
synchronous shader compilation. Every measured checkpoint still checks all
memory hashes. Reported time includes managed/native calls, batch checking,
copy/byte-swap, rendering, waits, full readback and frame-context advancement.
Parsing, reference loading, hashing and initialization are outside it.
The first five checkpoints are excluded from the warm median; complete-run
and initialization times remain in the report. Shader compilation can still
stall new workloads. These are renderer replay timings, not gameplay FPS.

Rebuild the normal probe after capture work and run `--check-journal-production`
against an accepted production `Ryu64.MIPS.dll`, as in N64GpuProbe's README.
