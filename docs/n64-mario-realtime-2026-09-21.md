# Mario 64 real-time attempt, 2026-09-21

Baseline: `64e9d390`. Target: Linux Super Mario 64 (USA), castle-area gameplay
including the moat, from the existing playable savestate. Full real-time play
has not been reached. The accepted change keeps COP1 work inside CPU blocks; it does not
change the emulated clock, audio tempo, rendering quality or frame skipping.

## Change

Previously every COP1 arithmetic, conversion, comparison, transfer or memory
instruction ended a quiet CPU block. These straight-line instructions now
participate in both the block interpreter and native JIT. Native blocks resolve
the existing instruction handler once during compilation. Arithmetic, rounding,
NaN handling, FCSR and FR-dependent paired-register mapping remain shared with
the ordinary interpreter.

Every COP1 instruction checks live CU1 before execution. A failed guard returns
the exact completed prefix and leaves the ordinary CPU path to raise the
exception. Branch delay slots are checked before branch/link state changes.
Memory operands also require aligned, bounded direct RAM. COP1 stores end the
compiled region, preserving instruction fetch after self-modification. COP1
branches retain their ordinary implementation. Event, COUNT/COMPARE and history
boundaries are unchanged.

## Measurements

Artifacts: `.build-tmp/sm64-realtime-2026-09-21/`.
Core state: `.build-tmp/sm64-20260919/mario-input/state.bin`.
Normalized ROM: `.build-tmp/sm64-speed-2026-09-20/native-game/input.z64`.
The source cartridge has V64 byte order despite its `.n64` extension.

Initial four serial 40-second scene runs, reference / CPU / CPU / reference,
affinity 6,7, discarding the first ten seconds:

| Build | Emulated seconds per wall second | Graphics tasks/s |
| --- | ---: | ---: |
| Reference, run 1 | 0.55534 | 33.754 |
| COP1, run 2 | 0.60536 | 36.765 |
| COP1, run 3 | 0.60190 | 36.189 |
| Reference, run 4 | 0.54926 | 33.455 |

Mean emulation throughput: **0.55230 to 0.60363, +9.29%**. The time ratio
uses the cycle-counter delta divided by the core's unchanged 93,750,000
cycles/second and elapsed wall time. Graphics tasks include work split by RSP
yield and are not display FPS. Background applications stayed running; absolute
throughput varied in later trials, so the paired comparison matters.

A second four-run series with the final Release binary, reversed order
(final / reference / reference / final), measured **0.53766 to 0.56637,
+5.34%**. Both comparisons are positive, but the result is best described as
**5–9% higher scene throughput**, with roughly **55–60% of real time** reached
in this fixture. Final logs have unique `scene-final-reference-reference-final`
prefixes; summary: `scene-results-final-reference-reference-final.json`.

## Correctness

Reference and accepted binaries produced identical complete serialized state
after 50,000,000 instructions in Mario, Rampage, Duke and Gauntlet, and
50,000,001 in Perfect Dark. Mario SHA-256:
`60E842A0596E319755B512E43BD696A42D5270ACC53CC02AFCCE24AE9E6C2BFE`.
All five hashes and logs are in `replay-results.json` and `replay-*.log`.
These instrumented correctness replays overlapped other correctness checks;
their elapsed times are not performance measurements.

New CPU cases cover both FR modes, special floating-point payloads, source/
destination aliasing, CU1 transitions inside a block, delay slots, RAM bounds,
cached/uncached aliases and self-modifying integer/COP1 stores. The ordinary
interpreter is the state oracle. No new hardware-accuracy claim is implied.

Final suites pass: **1993 CPU JIT cases (1767 compiled)** and **1629 block
interpreter cases**, each also checking 1,050,624 RANDOM transitions and one
million decoder samples. Stale code, cache reset during background compilation,
event boundaries and exact instruction history pass.

COP1 usability (368 cases), RSP slice/save/load/publication checks, audio FIFO/
snapshot/resampling checks and 81 video checks pass. Linux UI Release builds
with 0 errors and 383 existing warnings. Probe and UI core binaries match.

The final moving-input run lasted 91.29 wall seconds and produced 43.168 seconds
of audio (about 47% of real time including startup and changing scenery).
Mario changed position in 78 samples; graphics and audio tasks kept advancing
with no unknown CPU opcodes. Reloading that newly saved state also advances
graphics, audio and player position for 20.35 seconds. Its 6.754 seconds of audio
include another cold JIT start. Inspected frames show Mario moving/swimming
through the moat and continuing after reload. These runs validate continued
execution and controls, not full-speed play. Outputs: `mario-play/`,
`mario-reload/`, `play-results.json`.

Built `Ryu64.MIPS.dll` SHA-256:
`efeb08bc6ef545dfa986f754f961cac7c11c0002582a6399d2b6e3a8634e1b3e`.
Restart the Linux UI to load it; existing user savestate slots were not replaced.

## Rejected experiments and continuation

- Splitting vector multiply/add into predecoded span-based helpers matched
  instruction state but showed only about 1% in the first scene pair. Reverted.
- Hoisting native CU1 checks showed no stable scene improvement over the simpler
  COP1 implementation. Reverted.
- Allowing several RAM stores per compiled block with code-overlap guards
  preserved the 50-million-instruction Mario state but did not improve repeated
  scene throughput. Reverted; stores still terminate native regions.

Fresh baseline native sampling put textured triangles at 10.8%, vector compute
at 10.0%, vector accumulation at 4.4% and the CPU block runner at 7.5% of leaf
samples. Graphics work remains substantial. Continue from measured complete
gameplay, with exact state checks and independent timing runs.

Final native sampling (29,927 samples) still has textured triangles at 11.5%,
vector compute at 10.3%, vector accumulation at 4.6%, and the CPU block runner
at 9.7%. Leaf shares also change when work moves between interpreter and block
handlers; use the complete-scene timings to judge the accepted CPU change.
