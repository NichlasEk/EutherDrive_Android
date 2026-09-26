# Gauntlet: whole-emulator profile and TLB hot path

Linux continuation of the accepted texture-read overlap work. This pass keeps
the same Gauntlet Europe slot 1 and the native GPU library. The purpose is to
find the next whole-emulator bottleneck after the 12–15% texture-overlap gain.

## Reproduction and artifacts

Artifacts: `.build-tmp/n64-gauntlet-phases-2026-09-26/`.
ROM: `.build-tmp/n64-gauntlet-legends-2026-09-25/rsp-slice-graphics-verified/input.z64`.
State: `.build-tmp/n64-gauntlet-legends-2026-09-24/slot1-core.bin`, SHA-256
`6fb857e9ae0ff0c2e5784c1fdfe897b62110b7f21684c0a328f02bcff4b09ade`.

The diagnostic probe uses `N64LiveGpu=true`, `N64PerformanceProbe=true`,
`N64GpuBatchProfile=true`, `N64RdpJournalCapture=false`. New opt-in
`N64_PROBE_PHASE_PROFILE=1` prints `core.LastPerformanceStatus` at each
deterministic checkpoint. This does not change the normal desktop build.

The whole run used CPU affinity 26/27, `EUTHERDRIVE_N64_PERF=1`, overlap on,
`PARALLEL_RDP_SMALL_TYPES=1`, synchronous shader compilation, direct command
processing, two Granite workers, and validation off. `dotnet-trace` 8.0.547301
was installed in the artifact directory and collected `cpu-sampling` with
Speedscope output. `PARALLEL_RDP_BENCH=2` and `GRANITE_TIMELINE_TRACE` captured
native GPU phases simultaneously. No instrumented timing is a speed claim.

Raw results: `whole-profile.log`, `whole.nettrace`, `whole.speedscope.json`,
`gpu-timeline.json`, `whole-replay/`. Derived summaries:
`managed-stack-summary.json`, `gpu-phase-summary.json`. The upstream timeline
ends with a trailing comma and no closing array bracket; the summary parser
trimmed the comma and supplied the bracket in memory without changing the raw
file. GPU totals therefore describe recorded event pairs.

All three diagnostic checkpoints and final RAM/CPU snapshots match the accepted
texture-overlap run in `abba-track-texture-10-15/2-candidate/` exactly.

## What the profile says

| Guest window | Wall time | RSP graphics | RSP audio | GPU wait/copy/render |
| --- | ---: | ---: | ---: | ---: |
| 0–5 s | 32.204 s | 17.825 s | 1.942 s | 5.031 s |
| 5–10 s | 35.141 s | 16.736 s | 0.725 s | 3.741 s |
| 10–15 s | 34.924 s | 16.011 s | 0.649 s | 4.568 s |

These are nested timers: RSP includes RDP dispatch and GPU synchronization.
Do not add the columns or call wall time minus RSP pure CPU time. In particular,
the host GPU is not continuously spending the entire wall time rendering.
Recorded GPU render passes total 5.754 s across 46,213 passes. Their nested
depth/blending, tile-binning and shading totals are 2.784, 1.176 and 0.716 s.
Worker timeline waits can overlap and must not be added to emulation-thread
waits. Broad native frame/submission spans are not CPU execution time.

The approximate later ten guest seconds of the emulation thread's managed
sample stack assign 12.263 s to `InterpretOpcode`, 8.526 s to
`RspInterpreter.ExecuteSlice`, 6.369 s to `TLB.TranslateAddress`, and 2.653 s
to `ReadOpcode`. These are sample weights, not exact CPU durations: safepoints,
inlining and opaque native frames affect attribution. The window is aligned
approximately from thread start and the first checkpoint, not an event marker.

The code explains an important limit: Gauntlet executes mapped code near
`0xe0000000`, but both the CPU main loop and `TryAdvanceCpuBlock` admit only
direct-segment PCs (`0x80004000 <= pc < 0xc0000000`). Mapped code therefore
falls back to the interpreter, including repeated TLB translation for fetches.

## Bounded candidate

`TLB.cs` groups each thread's translation entries and generation into one cache
object, eliminating the second thread-static lookup. It moves allocation,
generation refresh and the ordered TLB scan out of the cache-hit method and
marks that method for inlining. It retains 256 entries, ASID tags, 4 KiB
subpages, original scan priority, exceptions and reset/write/load invalidation.
It does not change existing TLB permission semantics.

Frozen builds: `tlb-reference/` and `tlb-candidate/`; original source:
`TLB.before.cs`. Both probes use the same Release settings without batch or
CPU profiling. Differential checks against the frozen reference passed:

- 264,192 translations and 128 cross-thread generation updates.
- 262,405 opcode fetch cases, including exceptions and framebuffer epochs.
- 131,278 word-access cases, including exceptions and framebuffer epochs.

Whole-game results (seconds of host wall time):

| Window / actual order | Old observations | New observations | Old mean | New mean | Throughput change |
| --- | --- | --- | ---: | ---: | ---: |
| 5–10 s / old-new-new-old | 35.0158, 35.0322 | 34.4317, 34.2837 | 35.0240 | 34.3577 | +1.94% |
| 10–15 s / new-old-old-new | 35.1502, 38.8487 | 36.5611, 34.9524 | 36.9995 | 35.7567 | +3.48% |

All eight runs matched every checked guest/frame/audio/input value and final
RAM/CPU snapshot. Both series favor the candidate on mean time, but the later
unchanged reference varies by 10.5% between its observations. Other substantial
host workloads were active (see `host-load.txt`); CPU affinity does not isolate
GPU, shared caches, power limits or other tasks on those CPUs. This does not
establish a stable small gain. It also does not prove the candidate is slower.

**Decision: preserve the experiment, restore the original runtime TLB source.**
`TLB.candidate.cs`, `tlb-candidate.patch`, both frozen binary directories,
`candidate-manifest.json`, `tlb-abba/` and `tlb-reverse-10-15/` remain available
for a quieter-host repeat. The candidate source can be investigated for JIT
inlining/code-size effects before another balanced benchmark; those effects
are hypotheses, not demonstrated causes. The normal desktop retains the
previous accepted TLB implementation. No native GPU code changed in this pass.

The retained source addition is opt-in phase-checkpoint output in N64Probe,
plus this report, the validation follow-up and the plan's continuation pointer.
Candidate and reference builds succeeded; `git diff --check` passed. No new
production speedup or broader game compatibility claim is made from this pass.

Benchmark reproduction (new output directory required):

```sh
root=.build-tmp/n64-gauntlet-phases-2026-09-26
gpu=.build-tmp/n64-live-gpu/native/libeuther_n64_gpu.so
env PARALLEL_RDP_SINGLE_THREADED_COMMAND=1 EUTHERDRIVE_N64_GPU_OVERLAP=1 \
  python tools/N64Probe/bench-state.py \
  --reference "$root/tlb-reference/N64Probe.dll" \
  --candidate "$root/tlb-candidate/N64Probe.dll" \
  --rom .build-tmp/n64-gauntlet-legends-2026-09-25/rsp-slice-graphics-verified/input.z64 \
  --state .build-tmp/n64-gauntlet-legends-2026-09-24/slot1-core.bin \
  --output "$root/tlb-abba-repeat" --cores 26,27 --start 5 --end 10 \
  --reference-gpu-library "$gpu" --candidate-gpu-library "$gpu"
```

The second series swaps the two binary paths and measures guest seconds 10–15.
Thus its JSON calls the **new** binary `reference` and the **old** binary
`candidate`; interpret the labels accordingly. The harness checks checkpoint
RAM, frames, audio, input, cycles, tasks and final RAM/CPU state before timing
acceptance. Timed runs have no phase, batch, JIT or timeline profiling enabled.

## GPU validation follow-up

The 77-case GPU readback suite now passes with **zero validation errors** and
exact memory, hidden bits and TMEM. The previous SPIR-V validation failure was
avoided by using the launcher's `PARALLEL_RDP_SMALL_TYPES=1` shader setting.
There is no new shader fix or disabled validation in this result:

```sh
env PARALLEL_RDP_SMALL_TYPES=1 PARALLEL_RDP_FORCE_SYNC_SHADER=1 \
  PARALLEL_RDP_SINGLE_THREADED_COMMAND=1 GRANITE_NUM_WORKER_THREADS=2 \
  GRANITE_VULKAN_NO_VALIDATION=0 \
  dotnet .build-tmp/n64-gauntlet-legends-2026-09-25/track-texture-final-probe/N64Probe.dll \
  --check-gpu-readback .build-tmp/n64-live-gpu/native/libeuther_n64_gpu.so
```

Log: `readback-small-types-1.log`.

## Next larger step

Explore mapped-code CPU JIT support as a separate correctness milestone. Simply
loosening the PC guard is incorrect: existing code reads physical opcodes using
`pc & 0x1fffffff`. Start with blocks contained in one mapped 4 KiB page, resolve
the physical instruction address, and guard the ASID and translation generation
before execution. Reuse exact opcode guards at that physical address to cover
CPU writes and DMA aliases. Preserve virtual PCs for branches and exceptions;
exit at page crossings, TLB/ASID changes and unsupported instructions. Review
existing branch history, loop-entry lookbehind and block-cache keys too.

The initial scope should keep data-memory guards unchanged: mapped data
loads/stores can still exit to the interpreter. Count those exits before
expanding the scope. `R4300.Jit.cs` also generates fixed physical opcode guards
and `GpuBeforeRead` ranges; both must use the resolved physical address. Its
background compilation/version cache must not publish a block against an
obsolete mapping. A remap to identical opcodes does not prove identical branch
or data semantics, so retain virtual PCs and mapping identity explicitly.

Before enabling it, require interpreter/JIT differential cases for remapping,
large and overlapping pages, ASID switches, invalid mappings, physical aliases,
delay slots and save/load. Then use exact whole-game ABBA tests. Keep this
separate from RSP/GPU changes so any divergence has a narrow cause.
