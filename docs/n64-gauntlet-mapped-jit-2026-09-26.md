# Gauntlet: guarded JIT for mapped instruction pages

Historical broad-JIT experiment. The later [CACHE-entry filter and CPU-owned
instruction-page cache](n64-gauntlet-instruction-page-cache-2026-09-26.md) are now
enabled by default. Broad admission still requires explicitly setting
`EUTHERDRIVE_N64_CPU_JIT_MAPPED_CACHE_ONLY=0`; the frozen probes below retain
their original defaults. Set `EUTHERDRIVE_N64_CPU_JIT_MAPPED=0` to disable
mapped JIT in current builds.

Follow-up to the [whole-emulator profile](n64-gauntlet-phase-profile-2026-09-26.md).
Linux desktop only. The first implementation is experimental and opt-in with
`EUTHERDRIVE_N64_CPU_JIT_MAPPED=1`; unset or `0` retains direct-segment dispatch.

## Scope and safety contract

The existing CPU JIT only admitted direct KSEG0/KSEG1 instructions. Gauntlet's
code near `0xe0000000` consequently used the interpreter even for ordinary ALU
and direct-RAM operations. This implementation resolves the actual instruction
physical address and compiles at most 16 instructions within one mapped 4 KiB
subpage. A branch and its delay instruction must both fit. Virtual PCs still
control branches, links, exception fallback and instruction history.

Each mapped delegate checks current ASID and strictly retranslates its entry
address before execution. This is a refinement of the proposed generation
check: validating the current mapping also permits reuse after unrelated TLB
writes. Physical address and ASID are part of the compiled version identity.
The existing complete opcode-byte guards and GPU read-ownership check use the
resolved physical address. Mapping rejection returns to the interpreter without
executing guest instructions. Reset/load continue clearing derived JIT state.

The worker only consumes copied instruction words and captured mapping values;
its published delegate validates the live mapping when invoked. No worker reads
live CPU or TLB state. A same-page block cannot change TLB entries or ASID:
CP0 operations remain interpreted. Stores end the region and existing data
address guards remain in force. Mapped data accesses still fall back. Only one
mapped compiled block executes per outer loop iteration, using the original
quiet-cycle budget, interrupt checks and Count/RANDOM accounting. No mapped
interpreter batching or polling-chain extension is enabled in this first pass.

This does not change TLB permissions or the interpreter's legacy low-physical
instruction-fetch fallback; compiled mapped code requires a successful strict
translation. It does not lower guest clocks, skip graphics or alter save formats.

## Tests and artifacts

Artifacts are under `.build-tmp/n64-mapped-jit-2026-09-26/`. `R4300*.before`
files preserve the pre-pass sources, including pre-existing uncommitted work.
The probe builds with Release, `N64LiveGpu=true`, `N64PerformanceProbe=true`,
`N64RdpJournalCapture=false`; `candidate/` is the frozen test binary.

`--check-cpu-jit-mapped` requires the opt-in variable. It compares complete
serialized CPU/device/RAM state against ordinary interpretation and checks
virtual instruction history. Its 125 cases cover randomized ALU blocks,
conditional branches, J/JAL/JR/JALR links and delay slots, a bounded backedge,
direct data reads, mapped-data rejection, self-modifying stores, page boundaries,
odd pages, large pages, overlapping mapping priority, global entries, invalid
entries, ASID changes, remaps, reset, save/load, changed code and remapping while
background compilation is in flight. The suite passed in `mapped-tests.log`.

The existing CPU suite passed 2,267 state/rejection/event/self-modification
cases, including 2,004 compiled cases, plus worker stale-code and reset-during-
compilation checks (`direct-tests.log`). Its randomized decoder and RANDOM
checks also passed. This suite took approximately five minutes on this host.

A five-guest-second Gauntlet smoke replay matched every checkpoint field except
wall time against the preceding accepted reference: cycles, reads, tasks, audio,
input, RAM and frame hashes all match (`smoke/`, `smoke.log`). This was a
correctness run while the CPU suite was active, not a performance comparison.

The final probe adds optional `cpuJitTotals` output after emulation stops when
`N64_PROBE_PHASE_PROFILE=1`, reading the existing counters rather than adding
per-instruction instrumentation. No phase output is enabled for timed runs.

Whole-game performance results follow below. No speed claim follows from unit
tests or the correctness smoke replay.

## First measured iteration

`abba/` used the same `final-probe/` binary with mapped mode off/on/on/off,
the same native GPU library, slot 1, neutral controller input and affinity 26/27.
Each run advanced to guest second 10 and measured seconds 5–10. Native settings:
small types, synchronous shader compilation, direct commands, two workers and
GPU overlap enabled; validation and profiling disabled.

| Mode | Host seconds, observations | Mean |
| --- | --- | ---: |
| Off | 35.6769, 35.0186 | 35.3478 |
| First mapped implementation | 42.9280, 40.8460 | 41.8870 |

The first implementation is **15.61% slower in throughput**. All checkpoints,
frame files, audio, input and final CPU/RAM snapshots nevertheless match exactly.
It remains an experiment, not a production optimization. Frozen first sources
are `R4300.Jit.first.cs` and `R4300.Blocks.first.cs` alongside `final-probe/`.

## Admission iteration

The first version paid mapped translation and quiet-cycle-budget discovery
even while a candidate was still cold, being compiled, unsupported or unable to
perform its first data access. The second version warms the existing bounded
JIT cache before that setup and admits only a published delegate. First-in-block
CP0 operations, terminal stores and unsafe operands reject early. The original
direct-segment execution path is left intact. The additional cache preparation
is deliberately isolated to mapped dispatch for this experiment.

`EUTHERDRIVE_N64_CPU_JIT_MAPPED_ADMISSION=0` restores the first algorithm;
the default admission setting is on, but mapped JIT itself still requires its
explicit opt-in. `admission-probe/` and `admission-tests.log` hold the second
build and its passing 125-case mapped-state/history/guard suite.

The admission diagnostic (`admission-diagnostic.log`, guest seconds 0–5)
matched the accepted reference checkpoint exactly. End-of-run counters reported
879 compilations, 34,879,192 compiled instructions, zero invalidations, 18,842
rejected compilation attempts and `CpuJitUnavailable=false`. Those are aggregate
JIT counters, including direct code, not per-mapped-block coverage. The many
rejections are a useful future profiling target; the counter alone does not
identify why those blocks failed admission or prove a speedup.

The second ABBA comparison (`admission-abba/`, same settings and guest window)
again matched both checkpoints, frame files and final CPU/RAM state in all runs:

| Mode | Host seconds, observations | Mean |
| --- | --- | ---: |
| Off | 37.2700, 35.9729 | 36.6215 |
| Mapped with readiness admission | 39.5516, 39.7829 | 39.6673 |

This is **7.68% slower in throughput** than its own control. It narrows the
regression but does not establish a production speedup. Keep mapped JIT off by
default. Preserve both implementations and test data for iteration; do not
enable either in the normal launcher. Other host workloads remained active,
so the exact size of the regression is not a universal performance estimate.

The bounded-cache suite also passed: recent code survives, cold code can be
replaced, retired code is released, late addresses compile, the 1,024-version
cap holds and reset clears derived state (`cache-tests.log`). The normal GPU
desktop was rebuilt successfully through `scripts/run-n64-gpu-desktop.sh
--build-only`, using the existing native library (`desktop-build.log`). Mapped
JIT remains disabled without an explicit environment override.

For development reproduction only (currently slower):

```sh
EUTHERDRIVE_N64_CPU_JIT_MAPPED=1 scripts/run-n64-gpu-desktop.sh
```

Run normally without this variable to retain the accepted production path.
No ROM or user savestate was modified, and no commit/push was performed in this
pass. The next measurement must use a new artifact output directory and compare
against mode `0`, not merely against the slower first prototype.

## What the warmed block profile establishes

`profile-probe/` enables `N64CpuJitProfile=true`. `profile-10/` runs through
guest second 10; the harness activates collection only at second 5. The earlier
five-second `profile/` run therefore has an empty per-PC profile and must not
be treated as evidence of zero JIT activity. `profile-summary.json` summarizes
the actual warmed 5–10 second sample. Both checkpoints match the uninstrumented
reference exactly. Instrumented wall times are not used for performance claims.

All recorded JIT execution PCs are mapped. The sample records 25,340,789 runner
attempts, 24,788,626 successful executions and 68,730,493 executed instructions:
**2.773 instructions per successful execution**. There are 552,163 zero-work
guard exits, 6,910 worker-busy events, 8,138 version-cap events and 3,402
short/unsupported compilation events. Preparation attempts are not counted as
runner attempts, so those totals should not be interpreted as an admission ratio.

| Hot virtual PC | Executions | Instructions per execution |
| --- | ---: | ---: |
| `e00996a4` | 2,472,615 | 3 |
| `e0019668` | 719,112 | 4 |
| `e0019660` | 718,602 | 1 |
| `e0019534` | 519,341 | 2 |
| `e001952c` | 518,923 | 1 |

This refines the earlier rejection hypothesis: the dominant visible problem is
millions of successful but tiny blocks. Increasing compilation capacity alone
does not address their per-entry translation, code guard, delegate and quiet-
budget overhead. The profile is consistent with this overhead outweighing the
saved interpretation, although it does not separately time each guard.

Next bounded experiment:

1. Capture/disassemble the live mapped blocks at `e0019660` and `e001952c` and
   identify the exact instruction/operand guard ending their one-instruction
   prefixes. Do not assume the reason from the execution count alone.
2. If mapped data accesses cause those exits, add guarded mapped RAM operands
   while retaining MMIO, alignment and exception fallback. Otherwise evaluate
   running consecutive ready mapped blocks within one proven quiet interval,
   committing prior cycles before any possible fetch exception. Keep both
   experiments separate; do not add a speculative unbounded chaining loop.
3. Require the existing mapped-state/history/worker tests plus targeted cases
   for the newly admitted boundary, then repeat exact ABBA against mode off.
   A faster result than the first prototype is insufficient for acceptance.

The current code is retained opt-in as a correctness foundation. No new default
speedup, real-time claim or compatibility claim for untested games is made.
