# Mario 64: five measured optimization experiments

Linux continuation after the [Mega Man texture pass](n64-megaman-texture-speed-2026-09-23.md).
**No optimization from this pass is retained:** none demonstrated a stable
whole-gameplay improvement. Mario remains around **70% of real time** in the
demanding walking/swimming benchmark on this host. This does not establish
speed throughout the game and is separate from Mega Man's introduction result.

## Reference and method

The reference includes the preceding Castlevania and Mega Man improvements.
The common committed base is `f5771bcf6ccf2f61dd487847e09b59b2853e436c`;
`initial.diff` preserves the already-uncommitted changes at the start of this
pass. They have been preserved exactly.

Artifacts are in `.build-tmp/n64-mario-2026-09-23/`. Frozen inputs include
`reference/`, `reference-native.so`, `initial.diff`, `provenance.json` and the
`source-before*` directories. ROM-derived artifacts stay outside version control.

- Mario ROM SHA-256:
  `f3b0fc9f565f995368b6fa5aefc8ec1e5194c488948b41f3534dece4abcec2b4`.
- Unchanged native GPU library SHA-256:
  `f4bc6fa8ac1c0025165fbf6d025854158ffb72a427775896cbc325f5f1428424`.
- Complete runs boot for 90 guest seconds with deterministic Joybus-read input.
  The measured moving-gameplay interval is guest seconds 70–90, containing
  19.6602157261 seconds of audio. Real-time percentage uses audio duration
  divided by elapsed wall time.
- Timing uses cores 6/7, two GPU workers, synchronous shader compilation and
  the same native library. Profiling, audits and validation are disabled for
  timings. Builds and correctness checks do not overlap timing runs.
- Two observations per variant, ordered ABBA or ABCCBA, are a screening test,
  not statistical evidence for a small gain. The reference varied between
  27.11 and 28.50 seconds across the four series.

## Results

Positive throughput change means faster. Each candidate is compared with
the reference observations in its own series, not a different series.

| Candidate | Reference mean, seconds | Candidate mean, seconds | Throughput change |
| --- | ---: | ---: | ---: |
| Continue native blocks through guarded stores | 27.6375 | 27.2899 | +1.27% |
| Include the currently stubbed CACHE opcode in native blocks | 27.6493 | 28.1342 | -1.72% |
| Increase CPU JIT capacity from 512 to 1,024 | 27.6266 | 27.8348 | -0.75% |
| Increase CPU JIT capacity from 512 to 2,048 | 27.6266 | 27.8054 | -0.64% |
| Update the exact RSP progress hash incrementally | 27.8072 | 28.6116 | -2.81% |

The store candidate's small mean improvement overlaps run variation; it was
not accepted as a reliable gain. The remaining candidates were slower in
their measured series. Implementations and candidate-specific tests were
removed after evaluation. The CPU JIT capacity remains 512.

`store-abba/`, `cache-abba/`, `capacity-abccba/` and `rsp-progress-abba/`
contain individual results, checkpoints and summaries. Rejected source is
preserved locally in `source-store*`, `source-cache-512/`,
`source-rsp-progress/` and `rsp-progress-experiment.patch`.

## What the experiments established

Store continuation used live physical-address guards to exclude writes
overlapping remaining compiled instructions, including aliases and partial
writes. Native backedges containing stores were excluded. This increased
coverage without a convincing gameplay gain.

The CACHE candidate preserved the existing interpreter's PC-only behavior,
cycles and instruction history. At one hot loop, native work per call grew
from 3 to roughly 416 instructions, but gameplay did not improve. Diagnostic
PC addresses were never used as production game-specific conditions.

The 2,048-entry JIT nearly eliminated capacity rejection in the diagnostic
run (2,001 to 1) and increased native instruction coverage from 31.93 to
68.92 million instructions. Those counters did not predict elapsed time.

The RSP candidate preserved the full progress hash by undoing and refolding
the unchanged suffix around one modified GPR. It used the FNV multiplier's
inverse modulo 2^64. A nonserialized memory-valid flag reset at slice entry
and before interpreted work, retaining full calculation after CPU writes,
DMA and lifecycle work. Correctness passed, but the extra update work did
not yield a measured speed benefit.

## Correctness evidence

All **18 complete timing runs** produced identical **18 sampled frames and
18 checkpoints per run**, covering audio, input, cycles, RAM, Mario position
and action, and task counts. Final RAM SHA-256 was
`5488b023dbf86cc030b7c18daada7d5fffcbd9d8187b86ab56daf9486cb1bee7`.
See `complete-gameplay-proof.json`. The Mario harness does not export
complete final CPU/device state; this comparison is limited accordingly.

The candidate validation included:

- Store guards across all six store widths, self-modification and GPU
  ownership; 2,127 CPU cases and 82 live GPU checks.
- CACHE: 2,192 JIT cases and 1,822 interpreter-block cases against complete
  interpreter state/history, one million decoder samples and 1,050,624
  RANDOM-counter cases. Tests include delay slots, deadlines and pointer
  sign extension. An initially incorrect loop-test expectation was fixed
  against interpreter behavior before accepting these checks.
- Admission, retirement and reset checks at both larger JIT capacities.
- RSP: 14,336 comparisons with full hash calculation, 96 block programs and
  768 sliced checkpoints against the unoptimized interpreter, plus RSP
  scheduling checks.
- Fixed-work Rampage, Duke, Mario, Gauntlet and Perfect Dark replays retained
  their established complete state hashes.
- CACHE, 2,048-entry capacity and RSP candidates also matched Mega Man slot 1
  and Castlevania slots 2/3 over 15 guest seconds: all sampled checkpoints
  and frames, plus complete final CPU/device/GPU state. The 58 existing live
  GPU checks passed with validation and write auditing.

## Profile and next experiments

The CPU-thread profile contains 26,419 sampled instruction pointers during
moving gameplay. These are exclusive CPU sample shares, not wall-time
percentages. `TryAdvanceCpuBlock` accounts for 14.32%; RSP interpreter methods
and generated RSP code together account for 36.58%. Within the latter,
`ExecuteSlice` is 7.70%, `EndBlock` 3.69% and `ComputeProgressSignature` 2.89%.
`Memory.GetRspProgressSignature` adds 3.77% outside that RSP-method total.

Future changes should reduce measured work per block or command. Increasing
native coverage alone was insufficient in this workload. Concrete next
experiments, neither implemented nor proven faster here:

1. Measure call counts and cost within CPU block handoff, then evaluate
   avoiding duplicate opcode fetches. Code guards, loop-entry detection,
   history, interrupt deadlines and debugging fallbacks must remain intact.
2. Measure managed GPU batch serialization before changing it. A reserved
   buffer region per record may avoid repeated BinaryWriter/MemoryStream
   calls. Preserve the exact byte snapshots captured by `Writes()`, their
   order relative to `Command()`, same-value writes, ownership and barriers.

Do not repeat the rejected hash or cache-capacity changes based only on
improved internal counters. Any replacement needs the same image/state
comparison and isolated gameplay timings.

## Restored delivery

The tracked source diff is byte-identical to `initial.diff`, whose SHA-256 is
`ba37badc1b6e4b93a6dfd33096cf466ac8cdbd45381ad86d9572a86e41e67ff8`.
`rollback-proof.json` records that check. Both desktop variants are rebuilt
from this restored source; see `desktop-normal-final-build.log`,
`desktop-gpu-final-build.log` and `delivery-rollback-audit.json` for the final
build and binary audit. User savestates and ROM files were not modified.

This note and local diagnostic artifacts are the only new deliverables from
the Mario pass. No source optimization or experimental test is retained.
