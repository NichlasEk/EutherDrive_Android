# Historical pause checkpoint — live N64 GPU integration

**Resumed at the user's request on 2026-09-22.** The snapshot below describes
the earlier pause, not an active stop instruction. Current implementation,
validation, launch instructions and remaining work are in
[the live integration document](n64-live-gpu-2026-09-21.md).

Paused on 2026-09-21 after the user asked for a break and a clear continuation
point. Tests, builds and optimization were stopped until asked. No active
goal was created. The in-progress launcher build was stopped deliberately;
the user's running desktop emulator was left alone.

## Working tree

Last committed/pushed checkpoint: `fd4a42b4` — native GPU bridge with
dependency-aware write batching. The live integration below is **uncommitted**.
Keep the current changes. No reset, checkout or clean is needed.

Our changes:

- `Directory.Build.props`: opt-in `N64LiveGpu=true` / `N64_LIVE_GPU`.
- New `Ryu64/Ryu64.MIPS/Memory.Gpu.cs`: live dispatch, CPU/GPU ownership,
  exact range checks after page rejection, completed VI-selected snapshots.
- New `Ryu64/Ryu64Core/N64LiveGpu.cs`: ordered immutable command/write batches,
  lazy native initialization, exact-byte CPU patches, synchronization/readback,
  optional independent write audit and error reporting.
- `Memory.cs`, `R4300.cs`, `R4300.Blocks.cs`, `R4300.Jit.cs`: read/write/DMA and
  generated-code hooks; skip software rasterization while GPU is attached.
- `Ryu64Core.cs`: opt-in loading, reset/lifecycle, snapshot presentation,
  rejected GPU saves, existing-save software fallback, explicit disposal.
- `EutherDrive.Core/N64Adapter.cs`: disposal and defined GPU pixel byte order.
- `EutherDrive.UI/MainWindow.axaml.cs`: stop/join/dispose on window close.
- `tools/N64Probe/Program.cs`, new `N64LiveGpuChecks.cs`: live tests, lifecycle
  tests, boot-to-game Mario input and audit diagnostics.
- New `scripts/run-n64-gpu-desktop.sh`, new `docs/n64-live-gpu-2026-09-21.md`,
  links/updates in native README and the GPU integration plan.

Unrelated pre-existing work must be preserved and not staged with this task:
`tools/GauntletProbe/mame-gauntdl-phase5-oracle.lua`, untracked
`tools/GauntletProbe/mame-gauntdl-mainram.lua`, `__pycache__`, `console_history`,
`diff/`, `snap/`, and all local `.build-tmp/` artifacts.

## Verified so far

Artifact root: `.build-tmp/n64-live-2026-09-21/`.

1. `live-checks-v5.log`: **43 checks passed**, with Vulkan synchronization
   validation and the independent live CPU-store audit enabled. This includes
   20 Mario checkpoints byte-exact to Angrylion for full RAM and hidden memory,
   real generated JIT loads, interpreter/mirrored reads, SP/general/AI DMA,
   partial and same-value stores, split/recycled FIFO, range boundaries,
   FullSync, VI origin offsets and protection from intermediate depth clears.
2. `lifecycle.log`: real ROM boot, joined Stop, fresh memory/GPU on restart,
   protection of an existing slot-file sentinel and software-state loading
   after native GPU initialization all passed. Native contexts dispose cleanly.
3. `mario-live-v2/` and its log: live GPU boot reached Mario outside the castle.
   `frame-0150.ppm` was visually inspected and looked coherent. This earlier
   version used page-only read hazards. It exited successfully and reported
   zero renderer validation errors (Khronos validation disabled in that run).
4. `mario-audit/` and its log: 35-second probe with Khronos synchronization
   validation and independent CPU-store audit, no untracked writes or GPU
   errors. All 490 observed read waits hit page zero: the depth buffer started
   at `0x400`, while the CPU read exception vectors at `0x180`.
5. The new exact-range rejection fixes that false dependency. The directed
   tests confirm vector reads do not wait, while a read crossing into the
   actual depth range still does. **Its end-to-end speed has not been measured.**
6. `check-build-v5.log`: opt-in probe built successfully. Later Core ownership
   protection in `LoadROM`/`Dispose` has not been rebuilt/tested yet.

The original first test aborted at process exit because the probe did not
dispose the native device. Explicit Core/adapter disposal was added; subsequent
probe and lifecycle exits were clean. Do not revert that lifecycle work.

## Resume here

1. Finish the interrupted separate desktop build:

   ```sh
   ./scripts/run-n64-gpu-desktop.sh --build-only \
     > .build-tmp/n64-live-2026-09-21/desktop-build.log 2>&1
   ```

   Native build was stopped at approximately 219/361 objects. Ninja will reuse
   those objects. Desktop output is `.build-tmp/n64-live-gpu/desktop`; the new
   library will be `.build-tmp/n64-live-gpu/native/libeuther_n64_gpu.so`.
   A fully working earlier native build already exists at
   `.build-tmp/n64-native-2026-09-21/build/libeuther_n64_gpu.so`.

2. Rebuild the final probe with both flags and rerun the 43 checks and lifecycle
   after the last Core ownership change. Exact fixture paths:

   ```sh
   dotnet build tools/N64Probe/N64Probe.csproj -c Release --no-restore -m:1 \
     -p:N64LiveGpu=true -p:N64RdpJournalCapture=true \
     -o "$PWD/.build-tmp/n64-live-2026-09-21/probe" /clp:ErrorsOnly
   EUTHERDRIVE_N64_GPU_AUDIT=1 PARALLEL_RDP_SMALL_TYPES=1 \
     GRANITE_NUM_WORKER_THREADS=2 GRANITE_VULKAN_NO_VALIDATION=0 \
     taskset -c 6,7 dotnet .build-tmp/n64-live-2026-09-21/probe/N64Probe.dll \
     --check-live-gpu .build-tmp/n64-native-2026-09-21/build/libeuther_n64_gpu.so \
     .build-tmp/n64-journal-2026-09-21/mario-reset-20/journal \
     .build-tmp/n64-native-2026-09-21/mario-reference
   EUTHERDRIVE_N64_GPU_VALIDATE=1 PARALLEL_RDP_SMALL_TYPES=1 \
     GRANITE_NUM_WORKER_THREADS=2 GRANITE_VULKAN_NO_VALIDATION=0 \
     taskset -c 6,7 dotnet .build-tmp/n64-live-2026-09-21/probe/N64Probe.dll \
     --check-live-gpu-lifecycle \
     "$PWD/.build-tmp/n64-native-2026-09-21/build/libeuther_n64_gpu.so" \
     "$PWD/.build-tmp/n64-live-2026-09-21/mario-live-v2/input.z64" \
     .build-tmp/n64-live-2026-09-21/lifecycle-final
   ```

3. Build a GPU-only probe (`-p:N64RdpJournalCapture=false`) and run Mario from
   reset for about 150–180 seconds with `N64_PROBE_SM64_BOOT_INPUT=1`, audio and
   frame capture. This new input mode uses guest cycles and avoids repeatedly
   pressing Start once a Mario action exists. Verify it actually reaches and
   controls gameplay; only the older wall-clock auto-input run reached the
   castle so far. Inspect frames and position telemetry. Repeat a short run
   with audit/validation enabled. Do not call diagnostic timings game speed.

   ROM: `/home/nichlas/roms/N64/Super_Mario_64_(USA)-.n64` (probe normalizes it).
   Live env: `EUTHERDRIVE_N64_GPU_LIBRARY` absolute path,
   `PARALLEL_RDP_SMALL_TYPES=1`, `PARALLEL_RDP_FORCE_SYNC_SHADER=1`,
   `GRANITE_NUM_WORKER_THREADS=2`, `GRANITE_VULKAN_NO_VALIDATION=1` for timing.
   Leave `EUTHERDRIVE_N64_GPU_VALIDATE` and `EUTHERDRIVE_N64_GPU_AUDIT` unset
   during timing. Always pin benchmarks to CPUs 6,7 and run them serially.

4. Compare against a normal software probe built from the same working tree,
   with the same boot/input setup. Use matching guest-cycle intervals, not
   different scenes or the frontend polling rate. No build/test processes in
   parallel with timing, and do not stop the user's emulator. Keep performance
   claims exploratory unless repeated comparisons support them.

5. Verify the real desktop frontend. `Xvfb`, `xvfb-run`, `xwininfo` and `wmctrl`
   are installed; `xdotool` is not. An isolated working directory/display avoids
   touching user UI settings or save slots. Check boot/rendering, native pixel
   conversion and clean window close. Do not use the user's running window.

6. Rebuild normal Release, run appropriate CPU/JIT/RDP checks, and check that
   normal Memory fields/methods/hot-path IL are unchanged:

   ```sh
   dotnet build tools/N64Probe/N64Probe.csproj -c Release --no-restore -m:1 \
     -p:N64LiveGpu=false -p:N64RdpJournalCapture=false \
     -o "$PWD/.build-tmp/n64-live-2026-09-21/production" /clp:ErrorsOnly
   dotnet .build-tmp/n64-live-2026-09-21/production/N64Probe.dll \
     --check-journal-production \
     "$PWD/.build-tmp/sm64-pixels-2026-09-21/final/Ryu64.MIPS.dll"
   ```

   Expected historical Memory: 382 fields, 453 methods, 108179 identical IL
   bytes. Live-only hooks should compile out completely. `--check-cpu-jit`,
   `--check-rdp-streaming`, `--check-audio` and snapshot checks are relevant.

7. Update the live document with actual final evidence/limitations, review the
   focused diff, then commit/push the completed slice under the existing session
   authorization. Do not include local ROM-derived data or unrelated Gauntlet
   work. The user has not yet been told the launcher is ready to use.

## Limits and implementation follow-ups

- GPU saves are deliberately rejected before a save file is opened. Existing
  saves switch to software. Raw GPU renderer state import/export is unfinished.
- Every sync currently reads back all 8 MiB RAM, 4 MiB hidden data and TMEM.
  Range-limited readback and keeping hidden/TMEM resident are the next larger
  transfer improvement, after measuring the corrected false read hazards.
- Completed raw color snapshots are presented; GPU VI filtering is not wired.
- Cold shader compilation can stall. Packaging/distribution and five-game live
  coverage remain unfinished; the software path remains default.
- Review target-cache bounds and frontend error visibility as needed. No
  silent fallback is permitted after a partially executed native failure.
- No sub-agents are authorized. Communicate in Swedish; keep periodic updates.
- Memory registry used for Linux scope/performance interpretation was
  `MEMORY.md:1706-1710`; keep required memory citation in the final response.
