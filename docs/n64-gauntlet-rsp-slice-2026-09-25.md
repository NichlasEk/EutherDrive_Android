# Gauntlet: fixed-work RSP slice replay (Linux)

The Gauntlet Legends (Europe) slot 1 GPU scene now has a reusable, bounded
RSP fixture. `N64_PROBE_CAPTURE_RSP_SLICE=1` records the state immediately
before and after one real `ExecuteSlice` call. The hook is compiled only with
`-p:N64PerformanceProbe=true`; normal emulator builds do not capture or hash
commands. The original ROM and user savestate are unchanged.

Two fixtures were captured from
`.build-tmp/n64-gauntlet-legends-2026-09-24/slot1-core.bin`:

| Fixture | Work | RDP commands | Capture | Isolated median |
| --- | ---: | ---: | --- | ---: |
| Audio | 16,384 instructions | 0 | `.build-tmp/n64-gauntlet-legends-2026-09-25/rsp-slice-audio-verified/` | 0.418 ms |
| Graphics | 16,384 instructions | 44 | `.build-tmp/n64-gauntlet-legends-2026-09-25/rsp-slice-graphics-verified/` | 0.619 ms |

`--bench-rsp-slice DIR` reloads the same captured state for 100 warmup and
1,000 timed iterations on a pinned Linux CPU. The timer encloses only the RSP
slice; state restoration and comparison are outside it. The audio replay
matches the captured CPU/memory and RSP-register state, allowing only the
AI length register to change asynchronously. The graphics timing uses a
snapshot renderer, so it checks CPU/SP/MMIO state, all RSP registers, the
slice stop reason and instruction count, and the exact 44-command RDP stream.
It deliberately does **not** call its GPU-owned RDRAM and renderer bytes an
exact match.

Disable .NET tiered compilation for timing (`DOTNET_TieredCompilation=0`).
Without this, a same-binary four-run comparison falsely reported +9.24%
because methods were recompiled during the run. With tiering disabled, the
same-binary ABBA means were 0.6189 and 0.6159 ms, a residual +0.49%.
The four raw runs are in
`.build-tmp/n64-gauntlet-legends-2026-09-25/abba-rsp-slice-self-no-tier/`.
The 0.418 ms audio median was measured with tiering disabled. These timings
describe isolated captured slices, not gameplay frames per second.

An instruction-mix build (`-p:N64RspProfile=true`) replayed the same graphics
slice 1,100 times (warmup plus measured iterations). Dividing its counters by
1,100 gives roughly 779 vector-op `0f`, 664 `0e`, 553 L3 transfers, and
418 S1 transfers per slice. The instrumented timing is not comparable to
the uninstrumented timings above. Its log is
`.build-tmp/n64-gauntlet-legends-2026-09-25/rsp-slice-profile-graphics.log`.

One S1/SSV store specialization was then tried against the frozen probe.
It kept the exact graphics and audio slice oracles. The interleaved graphics
comparison was +0.53% (0.61945 vs 0.6162 ms), about the same size as the
+0.49% same-binary noise. The audio slice was +1.82% (0.4186 vs 0.4111 ms).
The candidate was removed from the normal RSP path because the graphics
result does not establish a stable whole-game improvement. The candidate
binary and raw results remain in `rsp-slice-ssv-candidate/`,
`abba-rsp-slice-ssv/`, and `abba-rsp-slice-ssv-audio/` under the same
September 25 `.build-tmp` directory.

A separate graphics replay with the real Vulkan renderer did match the
entire captured CPU/memory/RDRAM/GPU state apart from two bytes of
`AI_LEN_REG_RW`, which the frontend can read concurrently. Its one-slice
wall time was about 297 ms and is **not** the isolated RSP time. The
snapshot-renderer run is the useful fixed-work RSP timing fixture; the live
renderer run is its full-state slice oracle. Every replay checked the
RSP register snapshot and the command-stream SHA-256.

The Release GPU desktop build passed, as did the RSP scheduling check (32
slice boundaries, save/restore, producer/consumer, DP ordering, and frame
publication). A deliberately corrupted graphics command hash was rejected
before timing, confirming that the RDP-stream oracle is active.

The raw capture metadata and end-state SHA-256 values are:

- Audio: `CBC2D2CDB086AF29AB41545EDE28BA060F582561FED44F911D90A0EEBDB6A4A8`.
- Graphics: `C62E22D399412E92F56991F50A829F9F2F6DB13D5D985DE189E0411735325544`.
- Graphics RDP stream: `FDC4713BADA60DADEC87BC71AC68D063A0FC65B8EB4C6368DF9661522DF382FD`.

To rebuild the diagnostic probe, use:

```sh
dotnet build tools/N64Probe/N64Probe.csproj -c Release -m:1 \
  -p:N64LiveGpu=true -p:N64PerformanceProbe=true \
  -p:N64RdpJournalCapture=false \
  -o .build-tmp/n64-gauntlet-legends-2026-09-25/rsp-slice-probe /clp:ErrorsOnly
```

Run either fixture without a GPU library:

```sh
DOTNET_TieredCompilation=0 taskset -c 26 dotnet .build-tmp/n64-gauntlet-legends-2026-09-25/rsp-slice-probe/N64Probe.dll \
  --bench-rsp-slice .build-tmp/n64-gauntlet-legends-2026-09-25/rsp-slice-graphics-verified
```

`tools/N64Probe/bench-rsp-slice.py` runs reference/candidate/candidate/reference
with tiering disabled and rejects mismatched work or output state. A small
isolated gain still needs repetition and the full-game checkpoint benchmark.

```sh
python tools/N64Probe/bench-rsp-slice.py \
  --reference PATH/TO/REFERENCE/N64Probe.dll \
  --candidate PATH/TO/CANDIDATE/N64Probe.dll \
  --capture .build-tmp/n64-gauntlet-legends-2026-09-25/rsp-slice-graphics-verified \
  --output .build-tmp/NEW-ABBA-OUTPUT --core 26
```

For the one-pass full GPU oracle, set
`N64_PROBE_RSP_SLICE_LIVE_GPU=1` and
`EUTHERDRIVE_N64_GPU_LIBRARY` to the current native library. Use the same
GPU environment as `scripts/run-n64-gpu-desktop.sh`. The graphics fixture
must not be treated as a full-state oracle when run with the snapshot
renderer. A candidate needs the isolated result, the live oracle, and then
the original full-scene ABBA/checkpoint comparison before claiming a game
speedup.

This pass establishes the measurement and correctness fixtures. It does not
change normal RSP execution or claim a new gameplay-speed improvement.
