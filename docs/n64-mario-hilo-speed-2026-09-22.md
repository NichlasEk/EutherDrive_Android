# Mario 64: CPU HI/LO block coverage

Linux continuation from `ba8ff476`, using the same original-USA cartridge,
live Vulkan RDP backend and [deterministic gameplay benchmark](n64-mario-deterministic-speed-2026-09-22.md).
The measured interval remains guest seconds 70–90, walking and swimming.
This is a headless core measurement, not desktop presentation FPS.

## CPU change

MFHI and MFLO now run in both the bounded CPU interpreter and native JIT
blocks. Previously either instruction ended a supported arithmetic sequence.
The native code reads the current 64-bit HI/LO field on execution, including
when a cached block is reused after those registers change.

Reserved rs/rt/shift bits must remain zero. Both instructions keep their
existing one-cycle timing, register-zero normalization and delay-slot behavior.
The invariant-loop proof explicitly treats HI/LO as stable within its pure
ALU/load subset: their writers, multiplication and division still terminate
that subset and retain their ordinary execution and timing.

## Moving-gameplay measurements

Two independent reference/candidate/candidate/reference batches, pinned to
cores 6/7 with two native GPU workers. Builds, correctness suites, native
sampling and Vulkan validation did not overlap the measured intervals.
The user's background applications remained running.

| Batch/run | Reference seconds | Candidate seconds |
| --- | ---: | ---: |
| First / 1 | 28.0168 | |
| First / 2 | | 26.1210 |
| First / 3 | | 26.4911 |
| First / 4 | 27.0083 | |
| Confirmation / 1 | 27.0903 | |
| Confirmation / 2 | | 26.5957 |
| Confirmation / 3 | | 26.4097 |
| Confirmation / 4 | 27.0525 | |

The two batches improve throughput by **4.59%** and **2.15%**, respectively.
Across all eight runs, mean time falls from **27.2920 to 26.4044 seconds**:
**3.36% more throughput** for the same 19.6602 seconds of generated audio.
All 18 checkpoints, all 18 frame images and final RAM match across all eight
runs, including inputs, audio, positions/actions and task counts.

The candidate runs at **73.92–75.27% real time** in this selected interval.
Absolute speed varies with host conditions and asynchronous JIT admission;
use the paired comparisons rather than subtracting an older headline speed.
This does not establish 100% speed, other scenes' performance, or desktop FPS.

Reproduce with `tools/N64Probe/bench-sm64.py`, using the `reference/` and
`hilo/` probe directories under the artifact root below, the existing native
GPU library and a new output directory. `hilo-combined.json` contains the
pooled results; `summarize-hilo.py` rechecks all eight runs before combining.

## Verification

- 2,099 CPU/JIT differential cases, including 1,843 compiled cases, pass.
  These compare full serialized state and instruction history with ordinary
  instruction execution, including stale code, event boundaries, self
  modification and reset during background compilation.
- 1,735 bounded-interpreter cases pass independently of the native JIT.
- New HI/LO cases cover full-width values, r0/r31 destinations, live values
  on cached-code reuse, link writes before delay slots, every reserved bit,
  and taken/not-taken invariant backedges.
- The existing 1,050,624 RANDOM transitions and million-opcode decoder
  sample pass in both CPU suites.
- Controller ports, audio/resampling, RSP scheduling/save-load and 197 RDP
  streaming cases pass.
- Full CPU/device/RAM replay state matches the accepted reference after
  50,000,000 instructions in Mario, Duke, Rampage and Gauntlet, and
  50,000,001 in Perfect Dark.
- 96 RSP block programs and 768 additional slice checkpoints match the old
  implementation, including CPU writes between slices, DMA, mixed block
  lengths, history wraparound and long sequences of short blocks.
- The shared core builds for both net8.0 and netstandard2.0.
- Both normal and opt-in GPU Linux desktop Release builds finish with zero
  errors and 504 existing warnings. The normal desktop's MIPS assembly is
  byte-identical to the tested normal probe. Both shipping variants contain
  the HI/LO JIT support and omit the performance-probe callbacks/counters.

## Updated profile and next work

The final build's separate 90-guest-second profiling run also matches all
18 checkpoints, images and final RAM. Native sampling collected 27,502 CPU
instruction-pointer samples, with 25,371 mapped to managed code. The CPU
block runner remains the largest sampled method (15.45%); block instruction
execution is 4.99%, memory operand validation 2.48%, memory entry lookup
2.36%, and existing-loop recognition 2.31%. RSP slice execution is 6.91%
and SIMD vector execution 6.77%. These are exclusive sample shares, not
wall-time savings or an inclusive call tree.

The [RAM-dispatch follow-up](n64-mario-ram-speed-2026-09-22.md) uses this
profile to move ordinary RAM accesses ahead of the peripheral decoder. Its
branch-likely JIT trials preserved state but regressed throughput, so their
production changes were removed. Multiplication still needs separate cycle
and instruction accounting before it can extend native coverage; treating
multi-cycle instructions as one-cycle ALU operations would be incorrect.
GPU selective readback remains a larger, separate ownership/synchronization
project.

## RSP experiments rejected

A fresh native profile of the starting build collected 27,744 CPU-thread
instruction-pointer samples. Block history/completion accounted for 4.80%,
the memory-side progress signature 3.38%, and the RSP-side signature 2.55%.
These are sampled CPU instruction locations, not portions of total wall time.

Three isolated experiments preserved all 18 gameplay checkpoints and images
but failed to improve the whole moving-gameplay interval reliably:

| Experiment | Reference mean seconds | Candidate mean seconds | Throughput change |
| --- | ---: | ---: | ---: |
| Cache the invariant memory part of the progress signature | 27.0935 | 27.1592 | -0.24% |
| Precompute PCs and copy block history using spans | 27.1923 | 28.7093 | -5.28% |
| Materialize only the surviving tail of deferred block history | 26.9963 | 27.1154 | -0.44% |

All three production changes were removed. The additional differential
coverage remains, so future RSP experiments can reuse the boundary checks.

## Artifacts

`.build-tmp/n64-rsp-bookkeeping-2026-09-22/` contains isolated reference and
candidate binaries, the fresh native profile, rejected patches, interleaved
gameplay comparisons, complete replay digests and validation logs. ROMs,
captures and runtime binaries are not versioned. Existing user slots were
not modified.
