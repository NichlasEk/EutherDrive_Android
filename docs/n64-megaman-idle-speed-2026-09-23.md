# Mega Man 64: exact batching of idle jumps

Linux continuation after the [Castlevania GPU pass](n64-castlevania-gpu-speed-2026-09-23.md).
The reference includes that pass's 512-version CPU cache and TLUT batching.
The common committed base remains `f5771bcf6ccf2f61dd487847e09b59b2853e436c`;
the frozen binaries and `initial.diff` preserve the working-tree reference.

## Input and diagnosis

The user's slot 1 is a GPU save in the opening sea/airship sequence, captured
at frame 2,716, 2026-09-23 06:01:42 UTC, with a 320×237 framebuffer.
This pass measures that introduction, not an in-game combat scene.

- ROM: `Mega Man 64 (USA).z64`, SHA-256
  `618d49bf62913b376e4858f1422a51a4352792b070fe6305860dd43e28353999`.
- Original container SHA-256:
  `357b179844c30dac331fc2b413fd049dc972fe65696685c6f7105f79e9664385`.
- Extracted core-v2 state SHA-256:
  `d333f47dae0e537857b9c2c2e18b06cef0090f94cc588a09b7ced61225b65339`.

The container and raw state are copied to
`.build-tmp/n64-megaman-2026-09-23/`; the source save and ROM are read-only inputs.

A separate CPU-thread profile collected 27,363 instruction-pointer samples.
Block dispatch accounts for 33.40%, JIT entry lookup for 13.49%, the outer CPU
loop for 13.19%, and runtime-loop recognition for 7.50%. These are exclusive
CPU sample shares, not wall-time percentages. The hot compiled block at
`0x80026a1c` consists only of `J 0x80026a1c; NOP`. Its native delegate returned
after each pair, repeatedly paying the full dispatch and event-check overhead.

## Change and correctness boundary

The existing bounded idle-branch helper now accepts self-targeting `J` with an
exact `NOP` delay slot, in mapped RAM and both direct RAM aliases. It validates
the current instruction bytes on every entry and derives the jump region from
PC+4. Other jumps, JAL, delay-slot work, translation failures, page crossings,
pending interrupts and tracing/debug modes retain normal execution.

The batch counts complete two-instruction pairs and ends before the next VI
line, device event, CP0 COMPARE match or COUNT wrap. COUNT, RANDOM and device
timers advance by the same guest cycles. Instruction history retains the
repeated branch entries. The watchdog budget also stops before its next report,
preserving its cadence even when the same PC executes for millions of iterations.

There are no ROM names, checksums or game addresses in the optimization.
GPU rendering, native code, ABI and save formats are unchanged in this pass.
`CpuStateBenchmark` now exercises the idle helper too, so fixed-work regression
replays cover the path used by the CPU thread.

## Validation and measurements

An initial 30-guest-second continuation matches all six frame, audio, input,
RAM and task checkpoints plus complete final CPU/device/GPU state. The extended
idle suite passes 374 cases (178 accepted batches), including rejection of
changed instructions, non-NOP delays, JAL, page crossings, non-RAM code,
wrong jump regions and watchdog boundaries. Four 2-million-instruction replays with interrupt delivery match
ordinary interpretation for mapped BEQ, mapped J, cached J and uncached J.

The complete reference/candidate/candidate/reference series compares guest
seconds 5–30, with neutral input supplied on the same actual Joybus reads.
It uses CPU cores 6/7, two GPU worker threads and synchronous shader compilation.
Sampling, validation and builds run outside the timed series. Other user
applications remain running.

| Run | Wall seconds | Audio / wall, real time |
| --- | ---: | ---: |
| 1 / reference | 124.4241 | 20.10% |
| 2 / candidate | 34.2085 | 73.11% |
| 3 / candidate | 33.0296 | 75.72% |
| 4 / reference | 120.8326 | 20.70% |

Mean elapsed time falls from **122.6284 to 33.6190 seconds**, a **3.65× speedup
(+264.76% throughput)**. Generated audio divided by mean elapsed time rises
from approximately 20.4% to 74.4% of real time. These are headless measurements
of the saved introduction; desktop overhead and later gameplay can differ.
All six images, audio/input/RAM/task checkpoints and complete final CPU/device/
GPU state match across all four runs. GPU commands, write barriers and guest
event timing remain the same.

Mario's separate boot-and-moving-gameplay ABBA series also matches all 18
sampled frames and audio/input/RAM/task/position checkpoints, plus final RAM.
The guest-second 70–90 interval averages 27.0506 seconds before and 27.1088
seconds after (-0.21% throughput), effectively unchanged within the variation
between runs. This harness does not export complete final CPU/device state.

Both Castlevania GPU saves also match their previously accepted three sampled
frames/checkpoints and complete final CPU/device/GPU states after 15 guest
seconds. Five fixed-work replays of Rampage, Duke, Mario, Gauntlet and Perfect
Dark retain their accepted hashes after 50 million instructions (50,000,001
for Perfect Dark), using the updated replay harness that calls the idle helper.
Controller-port, audio, RSP-scheduling and RDP-streaming checks pass. Setting
`EUTHERDRIVE_N64_FAST_IDLE_LOOP=0` correctly disables the helper.

Normal and GPU desktop Release builds complete with zero errors. Both exclude
performance, JIT-profile and journal hooks; the normal desktop CPU assembly
matches the final validated probe byte for byte. The launcher's native GPU
library is byte-identical to the frozen reference, as expected for this CPU
change. `shipping-audit.json` records the final binary hashes. The original
Mega Man slot container still matches its input SHA-256.

## Trying the build and continuing

Start with `scripts/run-n64-gpu-desktop.sh`, select Mega Man 64 and load slot 1.
The previous Castlevania changes remain part of this build.

Local evidence lives under `.build-tmp/n64-megaman-2026-09-23/`: `profile`,
`idle-smoke`, `smoke-proof.json`, `idle-events-final.log`, `idle-abba`,
`abba-proof.json`, `gpu-work-proof.json`, `mario-abba`, `cross-game-proof.json`,
`replay-results.json`, `validation-driver.log` and `shipping-audit.json`.
The [following texture-upload pass](n64-megaman-texture-speed-2026-09-23.md)
reprofiles this version and reduces GPU barriers. The old profile above is
dominated by idle dispatch that this change removes.
