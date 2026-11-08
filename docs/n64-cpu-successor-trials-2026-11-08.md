# CPU cached-successor trials, November 8

Neither successor-retry candidate is promoted. The accepted COUNT rollover
fix remains the performance baseline for these experiments.

A fresh IP sample of the late Mario sequence attributes 17.76% of samples
to TryAdvanceCpuBlock, 4.13% to ExecuteCpuBlockInstruction, 3.82% to operand
validation, and 3.38% to TryRunCpuJit. Its six shared checkpoints/images
match the accepted replay. Sampling continues to second 125; no final-state
comparison at 125 is claimed.

The separate intrusive dispatch profile from state 90 through 120 reports
219,027,552 fallback instructions and 38,722,926 matching cached successor
entries with a published delegate. Of these, 8,303,406 follow branches and
8,235,246 follow stores. A matching entry is an opportunity, not a guarantee
that its full code/operand guards would pass. This profile matches six
checkpoints/images and full final state. Profiling timings are not benchmarks.

The first candidate retries published native entries after any fallback
instruction. The second restricts this to branch boundaries. Both retain
live word checks, full native code/operand guards, the existing quiet budget,
COUNT/event accounting, and history handling.

| Candidate, guest 95–120 ABBA | Wall throughput | Thread CPU throughput |
| --- | ---: | ---: |
| Every matching successor | -3.895% | -4.621% |
| Branch successors only | -2.022% | -0.163% |

All completed series match six checkpoints/images and full final RAM and
CPU/device/GPU state. The first candidate executes roughly 172–176 million
native instructions by second 120 versus 71.5 million in its reference,
but that increased coverage costs more than it saves. More native dispatch
and code checks are not necessarily faster than short interpreter sequences.

The broad candidate passes 2370 CPU block cases, 2922 JIT cases (2483 compiled),
20 targeted warm-successor/stale-code/live-operand/history cases, those cases
with register caching enabled, 58 GPU checks, portable compilation and both
control-game replays. The restricted candidate passes the adjusted 20 target
cases and its complete Mario ABBA; the broader suites were not repeated for
this rejected subset.

The early-window run of the broad candidate was stopped during its first
reference after the completed late series regressed. That partial series
and the unstarted playback experiment supply no result. Existing desktop
artifacts and launchers were not changed by either trial.

Validated source experiments are retained in
`experiments/n64-cpu-successor-all-2026-11-08.patch` and
`experiments/n64-cpu-successor-branch-2026-11-08.patch`.
Local evidence under `.build-tmp/n64-mario-2026-11-08/` includes
`profile-count-late/`, `successor-profile-run/`, `successor-*.log`,
`successor-late-abba/`, and `successor-branch-abba/`.

The dispatch diagnostic now also prints in the scripted Mario probe and
separates observed cached successors after branches and stores. It remains
behind N64CpuDispatchProfile and is absent from ordinary desktop builds.

The final diagnostic-only rebuild produces byte-identical `Ryu64.MIPS.dll`
and `N64Probe.dll` to the previously replay-validated `successor-profile/`
binaries. Final artifacts are in `diagnostics-final/`; the original user save
and the accepted desktop launcher remain unchanged.
