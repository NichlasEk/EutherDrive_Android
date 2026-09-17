# GauntletProbe

Bringup harness for Gauntlet Dark Legacy. It can cache a warm boot checkpoint so
late boot probes do not need to replay the full startup every run.

The opt-in generated compact-block experiment and its differential checks are
documented in [the 2026-09-10 checkpoint](../../docs/gauntlet-dl-generated-jit-checkpoint-2026-09-10.md).

Typical use with the local ROM directory:

```sh
env EUTHERDRIVE_GAUNTDL_CPU_STEPS_PER_FRAME=200000 \
    EUTHERDRIVE_GAUNTDL_WARMUP_STATE=auto \
    dotnet run --project tools/GauntletProbe/GauntletProbe.csproj -- \
    /home/nichlas/roms/MAME/Midway/Vegas/gauntd 450 200000 0
```

If `EUTHERDRIVE_GAUNTDL_RAW_DISK` is not set, the probe now auto-selects
`gauntd24.raw` or `gauntdl.raw` from the ROM directory before constructing the
adapter. This keeps warm-snapshot naming aligned with the actual raw disk path
used by the IDE device.

The first run saves an auto-named checkpoint under `/tmp/eutherdrive-gauntlet-probe`.
Later runs load it directly. To use frame 450 as a reusable start point and then
run farther:

```sh
env EUTHERDRIVE_GAUNTDL_CPU_STEPS_PER_FRAME=200000 \
    EUTHERDRIVE_GAUNTDL_WARMUP_STATE=auto \
    EUTHERDRIVE_GAUNTDL_WARMUP_FRAMES=450 \
    dotnet run --project tools/GauntletProbe/GauntletProbe.csproj -- \
    /home/nichlas/roms/MAME/Midway/Vegas/gauntd 520 200000 0
```

Warmup and final snapshot paths ending in `.gz` are compressed transparently.
This preserves the same snapshot format after decompression while avoiding the
large sparse-memory footprint of raw `.warm` files:

```sh
EUTHERDRIVE_GAUNTDL_WARMUP_STATE=/tmp/gaunt-f35.warm.gz
EUTHERDRIVE_GAUNTDL_SAVE_FINAL_STATE=/tmp/gaunt-f60.warm.gz
```

`run-gauntdl-baseline.sh` enables the core's central bringup baseline and uses
`200000` CPU steps per frame unless
`EUTHERDRIVE_GAUNTDL_CPU_STEPS_PER_FRAME` explicitly overrides it. The same
budget is passed to the probe and stored in every snapshot, so a later load
rejects an accidentally mixed-budget checkpoint instead of silently changing
the replay rate.

Long replays can save compressed, atomic snapshots at selected frame
checkpoints:

```sh
EUTHERDRIVE_GAUNTDL_FRAME_CHECKPOINTS=450,470,490
EUTHERDRIVE_GAUNTDL_FRAME_CHECKPOINT_STATE_PATTERN=/tmp/gaunt-f{frame}.warm.gz
```

The state pattern must contain the literal `{frame}` placeholder. Checkpoint
files use the same format and CPU-budget validation as warmup and final states.
Snapshot version 15 also preserves the M48T37 timekeeper/watchdog state and the
complete DCS/ADSP execution state. This keeps a resumed long replay from
silently disabling an armed watchdog or restarting audio while the main CPU and
framebuffer continue from a later frame.

Two default-off texture provenance overlays can be applied after loading a
snapshot:

```sh
EUTHERDRIVE_GAUNTDL_EXPERIMENT_DISK_TEXTURE_COPY=disk_offset:texture_offset:length
EUTHERDRIVE_GAUNTDL_EXPERIMENT_TEXTURE_MEMORY_COPY=source_offset:destination_offset:length
EUTHERDRIVE_GAUNTDL_EXPERIMENT_TEXTURE_MEMORY_COPY_ZERO_DESTINATION_ONLY=1
```

All fields are hexadecimal byte offsets. The first overlay reads the configured
Gauntlet raw disk; the second copies within the probe's Voodoo texture memory.
The optional third flag makes the texture-memory overlay preserve every
already-populated destination byte. Both overlays reject out-of-range requests
and leave the saved snapshot unchanged.

A third default-off diagnostic can copy an exact file range into guest main
RAM immediately after a warm snapshot is loaded:

```sh
EUTHERDRIVE_GAUNTDL_EXPERIMENT_GUEST_MEMORY_FILE_PATCH=path:file_offset:guest_address:length
```

The three numeric fields are hexadecimal byte values. The probe validates both
the source-file and main-RAM ranges before copying. This is intended for narrow
causal replay against an owned disk or RAM oracle; it is not part of the
runtime baseline.

An opt-in diagnostic build can record real texture-sampling and common-state draw batches for an
offline Vulkan comparison. See [GauntletGpuProbe](../GauntletGpuProbe/README.md).
This is an offline experiment, not a GPU-rendered gameplay mode; ordinary
builds omit its capture calls entirely.

The diagnostic build also captures contiguous common-state segments with full
color/depth oracles and ordered texture/NCC updates. See the
[stream checkpoint](../../docs/gauntlet-dl-gpu-stream-proof-2026-09-10.md).
ROM-free boundary checks use `EUTHERDRIVE_GAUNTDL_TEST_GPU_STREAM_BOUNDARIES=capture`
on a capture-enabled build and `=normal` after restoring the ordinary build.
These also check nine runtime draw-limit cases. The optional
`EUTHERDRIVE_GAUNTDL_GPU_DRAW_LIMIT` defaults to 128; see the
[expanded-window checkpoint](../../docs/gauntlet-dl-gpu-expanded-window-2026-09-11.md)
for limits and the measured GPU slowdown.

`displayRate` reports executed swap-command delta and swaps/second over the
timed replay, unlike `score fps`, which counts probe calls. It does not measure
distinct frames displayed by a host window or input latency. Probe drains any
active diagnostic GPU session at the timed endpoint before extra CPU steps.

For `EUTHERDRIVE_GAUNTDL_PROFILE_FRAME_PHASES=1` logs, use
`python3 tools/GauntletProbe/summarize-frame-phases.py <log>`. Nested Voodoo
timers must not be added to CPU phase totals. A dotnet-trace Speedscope export
can be filtered to RunFrame stacks with `summarize-replay-stacks.py <json>`;
this excludes startup, snapshot serialization and worker-only profiles, but
method attribution still includes inlining/native/wait ambiguities. See the
[CPU phase checkpoint](../../docs/gauntlet-dl-cpu-phase-profile-2026-09-17.md).

For alternating frozen-build benchmarks, `scripts/run-gauntdl-probe-warm.sh`
accepts `EUTHERDRIVE_GAUNTDL_PROBE_DLL=/absolute/path/GauntletProbe.dll`.
Keep the complete build output beside the DLL. The default path is unchanged.
See [CPU dispatch experiments](../../docs/gauntlet-dl-cpu-dispatch-experiments-2026-09-17.md).

ROM-free texture LOD regression checks:
`EUTHERDRIVE_GAUNTDL_TEST_TEXTURE_LOD=1 dotnet tools/GauntletProbe/bin/Release/net8.0/GauntletProbe.dll`.
The checks cover 1,048,576 input combinations against the original calculation
order. See [raster experiments](../../docs/gauntlet-dl-raster-experiments-2026-09-17.md).

ROM-free bilinear filter checks:
`EUTHERDRIVE_GAUNTDL_TEST_TEXTURE_FILTER=1 dotnet tools/GauntletProbe/bin/Release/net8.0/GauntletProbe.dll`.
This compares 1,156,784 cases with the original per-channel formula.
See [packed texture filtering](../../docs/gauntlet-dl-packed-texture-filter-2026-09-17.md).

`python3 tools/GauntletProbe/summarize-gpu-sync.py LOG` audits a completed
profiled synchronous resident GPU session, reports segment boundaries and
an explicitly hypothetical batch count. `gpuUnsupportedState` profile rows
identify the first unsupported render state ending each segment.
See [GPU synchronization audit](../../docs/gauntlet-dl-gpu-sync-audit-2026-09-17.md).

In-process GPU shadow comparison is available in diagnostic builds with
`EUTHERDRIVE_GAUNTDL_GPU_SHADOW=1`; it checks up to 128 draws while the CPU
continues to supply all game results. Build the native library first as shown
in [GauntletGpuProbe](../GauntletGpuProbe/README.md#in-process-runtime-shadow-comparison).

Add `EUTHERDRIVE_GAUNTDL_GPU_SHADOW_BATCH=1` for queued segment comparison
and ordered dirty-page uploads. CPU rendering remains authoritative; see
the [batch checkpoint](../../docs/gauntlet-dl-gpu-runtime-batch-2026-09-10.md).

PCI trace allocation and logging regression checks can run without ROMs:

```sh
EUTHERDRIVE_GAUNTDL_TEST_PCI_TRACE=1 \
dotnet tools/GauntletProbe/bin/Release/net8.0/GauntletProbe.dll
```

This checks zero allocations across 40,000 warmed, trace-disabled PCI accesses,
then verifies enabled trace text, the output limit, and the trace counter. The
check creates disposable PCI devices and restores its environment settings.

The Gauntlet bringup baseline also keeps the Temple weapons object source
distinct from the resource builder's writable output:

```sh
EUTHERDRIVE_GAUNTDL_FIX_RUNTIME_TEMPLE_WEAPONS_DISTINCT_RESOURCE_SOURCE=1
```

The builder uses the free high-RAM range immediately after the transient
weapons texture companion as its work arena, restores the source-table slot
after returning, and publishes the resource-table pointer into the work arena.
When an older warm snapshot still has an aliased resource pointer, the repair
first preserves its built records there and then rehydrates the immutable
`weapons/objects.rom` source from the configured raw disk.
