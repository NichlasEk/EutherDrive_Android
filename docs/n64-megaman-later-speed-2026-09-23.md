# Mega Man 64: the later gameplay save

The user's new slot 1 is gameplay in a ruin, facing a doorway, not the intro
used by the earlier near-realtime measurement. Artifacts and frozen binaries:
`.build-tmp/n64-megaman-later-2026-09-23/`.

The save was written at 2026-09-23 14:19:38 UTC, adapter frame 3608, 320x237,
GPU core state version 2. Original container SHA-256:
`50f64843ada773d1f3397f31623a5a26a9aafe0a035d123b4aaf420c551c4f4a`.
Extracted core SHA-256:
`5c5c9d812a74368d2f792d488b3679eb92143d0f94749df16c5badbff839dd93`.
The original container hash was rechecked after testing and is unchanged.

## Findings

A CPU-thread profile collected 16,802 instruction-pointer samples. The largest
exclusive sample shares were CPU block dispatch (16.27%), RSP slice execution
(6.56%), GPU RAM-write notification (3.47%), block instruction execution
(3.20%) and RSP progress signatures (3.05%). `WriteUInt8` and the byte indexer
together accounted for about 3.2%. The native GPU library accounted for 3.43%.
These are diagnostic sample shares, not expected wall-time speedups.

The heavy interval is guest seconds 5–15. Later parts of the 30-second capture
are considerably lighter. This scene is still far from realtime: the timed
heavy intervals span roughly 26–33% of audio-derived realtime on this host.
The earlier intro result must not be generalized to this gameplay state.

## Retained change

Direct KSEG0/KSEG1 byte stores into populated RDRAM now take a small checked
path, matching the existing fast word-access approach. They no longer perform
repeated virtual translation/map lookup or redundant GPU ownership notification.
Every write, including same-value writes, still updates the framebuffer write
epoch. TLB, MMIO, mirrored/unpopulated RAM and enabled tracing use the original
path. There is no game-specific address, identifier or reduced graphics work.

The existing register-local experiment is retained and remains **opt-in**.
One same-binary ABBA comparison in this scene gave +7.21% throughput with mode
`1`, versus the default mode `0`: 38.9666 to 36.3473 mean seconds for guest
seconds 5–15. That does not override the earlier Mario regression or establish
a universal default win.

## Measurements

Byte-store comparisons use register mode `1` in both variants, the same native
GPU library, CPU cores 6/7, two GPU workers, synchronous shader compilation,
neutral input at actual Joybus reads, and no concurrent builds, profiles or
correctness runs. Each series interleaves reference, candidate, candidate,
reference. The reference is the frozen pre-pass build, including the previous
uncommitted register and GPU changes.

| Series / interval | Reference mean seconds | Candidate mean seconds | Throughput change |
| --- | ---: | ---: | ---: |
| First ABBA, guest 5–15 | 32.5193 | 30.4984 | +6.63% |
| Independent ABBA, guest 5–30 | 57.8524 | 54.8902 | +5.40% |
| Same confirmation, heavy 5–15 only | 36.0642 | 34.4563 | +4.67% |

Both series have positive means, but substantial host/run variation remains.
For example, confirmation candidate heavy-interval times are 37.7589 and
31.1536 seconds. These percentages describe the measured scene and series,
not a promised per-run or whole-game gain. All compared runs have identical
sampled images, audio, controller checkpoints and complete final serialized
CPU/device/RAM/GPU state. The longer confirmation checks six checkpoints.

The candidate also contained the independently checked self-counter idle-loop
extension, which does not alter any compared state. These measurements were
completed **before** the subsequent RE2 interrupt/PAL fixes; frozen candidates
and oracles remain available to reproduce the isolated comparison.

## Correctness and next work

- 131,346 byte-access operations agree with the old implementation, including
  returned values, exceptions, framebuffer epochs and complete serialized state.
- 48 framebuffer write/restore phases remain exact.
- The final GPU suite passes all 58 live checks and six savestate checks with
  audits and Vulkan validation enabled.
- After the RE2 interrupt-order fix, this save completes another 30 seconds
  with all six images, audio and task counts unchanged. A few dozen cycles of
  input-read timing change the state hashes, as recorded in the RE2 report.
- Both desktop variants have been rebuilt with all retained changes. The
  register experiment still uses `EUTHERDRIVE_N64_CPU_JIT_REGISTERS=1`.

The next larger opportunity is still CPU/RSP dispatch and progress bookkeeping.
This capture provides a later-game oracle for that work. Increasing host
register reuse alone cannot remove the majority of the measured overhead.
See [the RE2 report](n64-re2-startup-2026-09-23.md) for the generic timing fixes
completed during this pass and their broader validation.
