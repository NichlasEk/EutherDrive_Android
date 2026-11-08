# CPU branch-likely compilation, November 8

The R4300 block interpreter and native JIT now support BEQL, BNEL, BLEZL
and BGTZL. An untaken likely branch annuls its delay instruction: it performs
no delay operand validation or execution and consumes one instruction cycle.
A taken branch retains the existing guarded two-instruction behavior.

The longer Mario replay improved throughput by 4.117%, with adjacent pairs
of +4.229% and +4.011%. This is a modest CPU improvement, **not** sustained
full realtime or a verified cure for audible music interruptions. The short
repeats and playback diagnostic remain noisier, as recorded below.

## Why this change

A fresh CPU JIT profile of Mario identified short blocks ending at unsupported
likely branches. Examples include PCs 8031563c, 8031d9a8 and 80318264, with
32246, 28125 and 28094 attempts and no compiled executions in the diagnostic
window. The profile is intrusive and supplies coverage evidence, not timing.
Its four guest checkpoints match the accepted reference.

Native code tests the condition before delay operand guards. Untaken exits
publish only the executed prefix and preserve exact instruction history.
Branch inputs use all 64 bits, with architectural zero even if the retained
r0 slot contains a temporary value. The register-cache read set includes
both comparison inputs. Likely backedges do not use native loop folding;
existing budget, code-coherence, mapped-code and watchdog policies remain.

## Correctness

- 566 new likely cases compare full serialized state with ordinary stepping:
  signed inputs, r0, prefixes, taken/annulled slots, unusable COP1, rejected
  memory and trap slots, valid loads/stores, changed delay code, deadlines,
  budgets, self branches, backedges and history-ring wrap.
- Full CPU block suite: 2321 cases. Full JIT suite: 2853 cases, including
  2456 compiled cases and asynchronous worker/code-reset checks.
- The 566 cases also pass with `EUTHERDRIVE_N64_CPU_JIT_REGISTERS=1`.
- 1,000,000 decoder samples, 4,000,012 next-decode checks and 1,050,624 RANDOM
  batching cases pass in each full CPU run.
- 58 live GPU checks pass with validation. Portable netstandard2.0 builds.
- Mega Man 64 and Resident Evil 2 replays match full state, frames, PCM audio
  and input checkpoints against the accepted core.
- Every Mario comparison matches all checkpoint hashes, images, final RAM
  and full CPU/device/GPU state: six checkpoints for the short window and
  twelve for the longer replay through guest second 120.

## Performance

Original local SM64 USA cartridge; common GPU state at guest second 60;
.NET 10.0.14; fixed native GPU library; overlap and single-threaded commands;
all 28 host CPUs available. Each series is reference/candidate/candidate/
reference. Builds and correctness jobs finished before timing. Unrelated
host workloads were neither stopped nor reconfigured.

| Measurement | Guest window | Wall throughput | Thread CPU throughput |
| --- | --- | ---: | ---: |
| Ordinary first ABBA | 70–90 | +2.487% | +1.574% |
| Ordinary repeat | 70–90 | -0.825% | -2.551% |
| Hardware-counter diagnostic | 70–90 | +4.022% | +4.922% |
| Ordinary longer ABBA | 70–120 | +4.117% | +5.749% |
| Adapter/audio null-sink ABBA | 70–90 | +1.151% | not collected |

Hardware counters report 3.385% fewer user-space cycles for equal guest
work (3.504% throughput equivalent), while retired instructions increase
0.637%. Both adjacent cycle pairs improve; counters were not multiplexed.
These instrumented wall numbers are diagnostic, separate from ordinary
performance acceptance.

The longer candidate runs achieve 74.865% and 71.569% realtime in this host
session, versus 71.828% and 68.809% for their adjacent references. The later investigation in
[n64-count-rollover-2026-11-08.md](n64-count-rollover-2026-11-08.md) identifies
a COUNT rollover guard that stopped all CPU batching in this post-90 section.
These historical measurements precede that correction.

The adapter/audio diagnostic matches six guest checkpoints and reports no
dropped output frames. Sampled buffer underruns are 375/322/256/174 across
ABBA: they do **not** establish a consistent audible improvement. It excludes
Avalonia rendering, the actual sound driver and listening validation.

## Local evidence and reproduction

Evidence is under `.build-tmp/n64-mario-2026-11-08/`: `likely-full-*.log`,
`likely-registers.log`, `likely-gpu-check.log`, `likely-cross-game-proof.json`,
`likely-abba/`, `likely-repeat-abba/`, `likely-counter-abba/`,
`likely-long-abba/`, `audio-likely-abba-results.json`, and
`likely-artifacts.json`. Private ROMs and states are not committed.

Build the probe with `N64LiveGpu=true`, `N64PerformanceProbe=true`,
`N64RdpJournalCapture=false`, and `N64RspProfile=false`. Run
`--check-cpu-blocks` and `--check-cpu-jit`; set
`N64_PROBE_CPU_LIKELY_ONLY=1` for just the targeted branch cases.
Use `tools/N64Probe/bench-sm64.py` with common `--state`, runtimes 10.0.14,
`--cores 0-27`, and `--end 120` for the longer comparison.

## Desktop delivery

`desktop-likely/` is an uninstrumented UI build. The UI savestate checker
loaded its exact DLLs and native library and passed GPU save/load/resume,
other-slot preservation, empty-slot handling and injected save failure.
`ui-likely-proof.log` records that gate; the build includes existing local
UI changes and is not a clean-HEAD build.

The existing `.build-tmp/n64-mario-2026-11-07/play/run.sh` now selects this
build on .NET 10.0.14. `run-before-cpu-likely.sh` restores the previous
accepted desktop. The original user save retains its previous SHA-256.
`delivery-likely-manifest.json` records delivered artifact hashes. No new
interactive listening or physical gameplay result is claimed.
