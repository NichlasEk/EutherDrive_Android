# Additional Mario Linux trials, November 8

The accepted core remains the November 7 DMA/fetch/prefix/inline-history build.
Eight further trials did not establish useful performance gains and were
restored. All use .NET 10.0.14, the same saved GPU state at guest second 60,
all 28 CPUs allowed, direct GPU commands, overlap and two GPU workers.
ABBA measures guest seconds 70–90 (19.7122401 audio seconds). Every completed
trial matches six checkpoints/images, audio/input, final RAM and complete
serialized CPU/device/GPU state. Other desktop workloads remained active;
none were stopped or reconfigured to improve the results.

| Trial | Mean reference wall seconds | Mean candidate wall seconds | Throughput |
| --- | ---: | ---: | ---: |
| Fold two RSP hash multiplications into the existing prefix | 21.7893700 | 21.6286613 | +0.743% |
| Inline RSP progress helpers | 22.1960003 | 22.0114116 | +0.839% |
| Existing CPU register cache under .NET 10 | 22.7577671 | 22.7610636 | -0.014% |
| Validate RSP block interiors once per uninterrupted slice period | 22.5561781 | 22.4133908 | +0.637% |
| Also reuse the validated first instruction | 21.4343204 | 21.6315664 | -0.912% |

The sub-one-percent positive differences do not justify new production
complexity. The final slice-entry trial loses both adjacent comparison pairs.
The code-cache candidates pass the full RSP suite and the 32 new interior
edit/within-slice CP0 IMEM rewrite/queued DMA/epoch-overflow checkpoints.
All source candidates also match complete Mega Man 64 and RE2 GPU states;
the register-mode experiment was a Mario timing rerun of existing code.
The fold candidate additionally builds for netstandard2.0.

The four source experiments are archived in `docs/experiments/n64-{fold,
inline-progress,slice-code,slice-entry}-2026-11-08.patch`. These contain only
core changes, so they apply independently of the new committed regression
checks. None are enabled by the playable launcher.

## Fresh accepted-core profile

A separate sampling pass at guest seconds 70–90 matches the same six Mario
checkpoints/images and complete final state. It is diagnostic, not a timing
sample. Managed samples grouped by method names are approximately 29.80%
R4300 CPU, 42.70% RSP/interpreter/dynamic lambdas, and 22.16% other managed
work. Dynamic-lambda attribution assumes the RSP-generated lambda naming.
The largest individual methods are TryAdvanceCpuBlock (14.84%), RSP
ExecuteSlice (10.66%), ComputeProgressSignature (4.59%), CPU fallback
instruction execution (3.55%) and TryRunCpuJit (3.23%). Sample shares are not
independently achievable speedups.

The larger CPU interpreter fallback batch (128 instead of 32, bounded by
unchanged quiet-device/event limits) also failed its speed gate. ABBA wall
seconds were 21.1912884 / 21.3917870 / 20.3392563 / 19.7804373, a 1.82%
throughput loss with both candidate/reference pairs negative. All six Mario
checkpoints/images and complete state match. The full CPU/JIT suites and
cross-game GPU states pass as recorded in the fallback-boundary report.
The core change was restored and archived in
`docs/experiments/n64-cpu-fallback128-2026-11-08.patch`.

Disabling the CPU JIT under .NET 10 loses 2.10% throughput (20.7673035
reference versus 21.2119519 candidate seconds). The CPU JIT stays enabled.

Retaining local Memory and opcode-cache references in the CPU loop initially
gains 1.60% wall throughput and 1.90% thread CPU throughput. A second ABBA
loses 0.50% wall and 0.52% CPU throughput (20.3222713 versus 20.4251400 wall
seconds). Full CPU/JIT checks and Mega Man/RE2 complete states pass, but the
unstable speed result does not qualify. The production change is restored.
Its core-only patch is archived as `n64-cpu-locals-2026-11-08.patch`.

The benchmark now records CLOCK_THREAD_CPUTIME_ID at checkpoints on 64-bit
Linux. CPU time excludes waits and descheduling, but remains sensitive to
frequency changes; non-CPU wall time includes both waits and descheduling.
Wall/audio throughput remains the acceptance gate. Older probes and other
hosts retain wall-only comparisons. The live pilot and both local-reference
ABBA runs validate the new telemetry with identical complete game states.

Local evidence: `.build-tmp/n64-mario-2026-11-08/`, the ABBA directories,
`trial-options.json`, `profile-current/`, `profile-current-proof.json`, the
RSP logs and per-candidate cross-game proof JSON files. Private ROMs and
states remain local. The established `../n64-mario-2026-11-07/play/run.sh`
launcher remains the delivery/rollback point until another change qualifies.

## Further CPU loop trials

The return-value instruction-fetch trial passes full CPU/JIT, GPU and
cross-game state checks, but gains only 0.613% wall throughput. It is
restored; see `n64-cpu-return-fetch-2026-11-08.md` for exact evidence.

A separate experiment splits native dispatch and interpreter fallback into
two loops, removing the repeated tryCompiled condition from the fallback
backedge. It uses the accepted fetch helpers and no local-reference trial.
Mario ABBA wall seconds are 19.6399224 / 19.7279759 / 19.6285605 /
19.9049257. Mean wall throughput gains 0.478%; CPU throughput gains 1.027%.
Adjacent wall pairs disagree. All six checkpoints/images and full final
CPU/device/GPU state match. This does not qualify as a stable performance
win: the core is restored and archived in
`docs/experiments/n64-cpu-split-loop-2026-11-08.patch`. The full CPU/JIT and
cross-game suites were not repeated for this rejected split-loop candidate.
Local evidence: `split-loop-abba/` and `split-loop-abba.log`.

The accepted reference in that final series reaches about 99.0–100.4% audio
realtime on average, but its heavy five-second intervals remain about
91.2–94.0%. Neither averages above 100% nor these rejected experiments
establish full realtime. No delivery binary or launcher was replaced.

Next investigation: profile the heavy 70–80 second interval separately,
including which guest instructions fall back from JIT and their code guards.
The current profile covers 70–90 and mixes those CPU-heavy intervals with
lighter sections. Do not repeat larger fallback batches, register caching,
local references, return-value fetch, or loop splitting without new evidence.
