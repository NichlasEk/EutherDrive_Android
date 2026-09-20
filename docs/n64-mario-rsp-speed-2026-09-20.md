# Mario 64: measured RSP optimization

Baseline: `10aea1e0`, including cooperative RSP scheduling and FULL_SYNC IRQs.
ROM: local Super Mario 64 USA cartridge, normalized by N64Probe.
Scene: `.build-tmp/sm64-20260919/mario-input/state.bin`.
Evidence and isolated binaries: `.build-tmp/sm64-speed-2026-09-20/`.

## Change

Native instruction-pointer sampling found ExecuteVectorCompute (7.0%),
ComputeVectorAccumulate (4.4%), and vector operand loading among the principal
Mario costs. The CPU block runner accounted for 5.4%; optimizing only that
runner would miss substantial RSP work in this game.

VMADM/VMADN/VMADH now operate on eight-lane spans validated before the loops.
The accumulator's three planes are accessed directly through those spans,
removing repeated per-array range checks. Signed/unsigned products, 48-bit
storage, overflow, saturation and destination aliasing retain the same rules.

Whole-register loads/stores use two unaligned native-word copies through
GC-tracked managed references instead of pinning both arrays. Explicit register
and scratch-array bounds checks remain. Existing endian conversion is unchanged.
No framework target or package dependency was changed.

A trial applying spans throughout the larger vector instruction switch was
rejected: it retired more host instructions than the smaller combined change.
An intrinsic SIMD trial was abandoned because this shared core targets
netstandard2.0; no retargeting or intrinsic code remains.

## Fixed-work evidence

50,000,000 guest instructions, three warmups and five samples per process,
serial processes pinned to CPU 6. All variants produced full serialized-state
SHA-256 `D14DBC4767958E314135861F394F54430A90953B2B635AC64C56D7DFB1944ECF`.
The state includes CPU, devices, RAM and architectural RSP state.

| Variant | Median ms | Median host instructions | Median host cycles |
| --- | ---: | ---: | ---: |
| Baseline | 2922.175 | 24,927,592,587 | 8,490,461,924 |
| Accumulator spans only | 2889.101 | 24,560,929,940 | 8,408,311,389 |
| Retained: spans + managed register copies | 2885.034 | 24,479,690,901 | 8,370,077,854 |
| Rejected: larger vector-switch span rewrite | 2916.953 | 24,642,516,788 | 8,485,969,610 |

The retained variant reduces host instructions by 1.8%, cycles by 1.4%, and
elapsed time by 1.3%. An earlier opposite-order pair measured 2788.874 ms for
the retained variant versus 2835.723 ms baseline (1.7% less time).
Counters were not multiplexed. Builds and correctness tests did not overlap
these measurements. Existing user processes were left running.

Real-time scene trials were noisy: even the baseline varied from approximately
17 to 26 graphics tasks/s. Candidates often scored higher, but these runs do
not establish a reliable whole-game FPS percentage. Use the fixed-work result
for the claimed gain; graphics task rate is not display FPS.

## Validation

- 10,368 RSP cases and synthetic task differential checks against the baseline,
  in both half-shuffle modes.
- 2,048 register-copy cases and 32,768 shuffle cases, including register edges,
  endian conversion, partial scratch arrays, and operand aliasing.
- 96 compiled-block differential programs.
- Cooperative scheduling/save-load/branch-delay/producer-consumer/DP interrupt
  checks continue passing.
- Final Mario run uses automatic jumping and stick input from the same snapshot;
  positions change and the game continues rendering.
- Release probe and UI builds. Changes apply after restarting the application.

These are small CPU/RSP efficiency improvements, not a full-speed claim or a
change to guest clocks, rendering quality, audio speed, or frame skipping.
