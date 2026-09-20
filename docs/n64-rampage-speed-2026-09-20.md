# Rampage 2: quiet CPU timer updates

Baseline: `38ba85c8`. Tested Rampage 2 - Universal Tour (Europe), slot 3,
extracted read-only into `.build-tmp/rampage-speed-2026-09-20/`.
Original savestate container SHA-256 at extraction:
`c38698d2677d99c509aaa26e106cbc4066f6bf129f7c174659159640fde1b580`.

## Change

Native CPU sampling attributed 23.95% of samples to `TryAdvanceCpuBlock`,
9.86% to `Memory.Tick`, and 2.74% to `GetQuietCpuCycles`. CPU blocks already
stop before the next device completion, VI line transition, or interrupt.
Their final timer update now directly advances counters within that validated
interval, avoiding a second set of event/completion checks.

Only validated CPU blocks use this path. Ordinary execution and event
boundaries retain `Tick`. A halted RSP continuation keeps its remaining time;
disabled VI interrupts do not advance their countdown. Blocks cannot write
MMIO, and the savestate format is unchanged.

## Measurements

Timing runs were serial, pinned to CPU 6, with no overlapping builds or our
tests. Existing user applications remained running. Fixed-work replays use
three warmups and five measured samples. Hardware counters were not multiplexed.

| Guest instructions | Baseline median ms | Updated median ms | Baseline host instructions | Updated host instructions |
| --- | ---: | ---: | ---: | ---: |
| 10,000,000 | 538.785 | 501.607 | 4,165,848,241 | 4,062,643,792 |
| 50,000,000 | 2501.216 | 2485.660 | 20,741,342,289 | 20,490,611,838 |

The longer replay, run in reverse variant order, shows about 1.2% less host
instruction work but only 0.6% less elapsed time. The initial 6.9% time reduction
does not establish a comparable game-speed improvement.

Full serialized machine states match between baseline and updated builds:

- 10 million: `27E019A68B2AD8DC1EF32DBC141DAB85BB0AC835BCC3857486ADE23ACDD1BF8C`
- 50 million: `CC87243FFF6860A0A94AA0CBAD900F6CE32FFD44012E21AB8A8EE82E2184D344`

Four 25-second scene runs, excluding the first five seconds, measured
10.606 (updated), 10.931 (baseline), 10.640 (baseline), and 11.002 (updated)
graphics tasks/s. There is **no convincing scene-rate improvement** in these
measurements. This is a small CPU cost reduction, not a full-speed claim.

## Validation

`--check-cpu-blocks` passes 833 full-state comparison cases, including 128 new
combinations of simultaneously armed timers, suspended RSP continuations and
disabled VI interrupts. Existing coverage includes exact event boundaries,
self-modifying code and rejected blocks. One million opcode classifications
and 264,192 RANDOM-register cases also pass.

- RSP scheduling: 32 slice boundaries, save/restore, CPU producer/RSP consumer
  publication and DP synchronization pass.
- Duke, Mario and Gauntlet each produce identical full-state hashes after
  one million guest instructions in baseline and updated builds. These runs
  overlapped other checks and are correctness evidence only.
- Rampage continues for 60 seconds with graphics tasks advancing from 4102 to
  4754, zero unknown CPU instructions, then resumes its newly saved state for
  another 15 seconds successfully.
- Release UI build succeeds with zero errors. UI and tested probe core SHA-256:
  `bf4e58efced71c105bafed6626fb6cb2982e7171bf9ececc8fd8d2639880d3bc`.
- The original savestate container checksum remains unchanged.

Artifacts, raw counters, scene telemetry and build/check logs are retained in
the directory above. Restart the Release application to use the new core;
existing savestates remain usable.
