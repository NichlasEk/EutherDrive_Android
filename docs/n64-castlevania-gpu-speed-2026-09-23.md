# Castlevania: palette uploads and CPU compilation capacity

Linux continuation from `f5771bcf6ccf2f61dd487847e09b59b2853e436c` and
[the GPU savestate checkpoint](n64-gpu-savestates-2026-09-22.md). The user
supplied new GPU saves in slots 2 and 3, then requested another optimization
pass. Work uses read-only copies of those saves, without modifying the original
ROM or slot container.

The next pass uses Mega Man 64 slot 1; see
[exact batching of idle jumps](n64-megaman-idle-speed-2026-09-23.md).

## Reproduction inputs

ROM: **Castlevania - Legacy of Darkness (Europe) (En,Fr,De)**,
SHA-256 `e78c172c1d554d1c94865969cec9b87a2149c92ec69ebc52b73a46648b5b2395`.
Container SHA-256:
`dcce5b22a29f3ef40bc2d5330ac7c4700d7d6b58e01b98eeb7d004c8ce57ce23`.
Both new saves use core version 2 and the Vulkan RDP renderer at 320×237.
Slot 1 remains the original software state.

| Slot | Saved UTC | Raw core SHA-256 |
| --- | --- | --- |
| 2 | 2026-09-23 04:10:43 | `af662e817e002e3cc6dfca19ceae5ab58d52932a8562c7025f1d554d3cc21e3d` |
| 3 | 2026-09-23 04:11:04 | `f757656fb8d50f4bcad75aafdb47f87865ba79767458bb2efd3e731495619c2b` |

The saved frames show gameplay on the ship at Foggy Lake. Measurements replay
neutral controller input and observe the same actual Joybus reads at guest
seconds 5, 10 and 15. They are headless core measurements of these scenes,
not desktop FPS or a full-game performance guarantee.

## Findings and changes

An independent CPU-thread sampling run collected 21,066 instruction-pointer
samples. CPU block dispatch accounted for 17.20%, RSP slice execution 5.07%,
and block instruction execution 4.24%. These are exclusive CPU sample shares,
not wall-time percentages. A separate instrumented JIT run recorded 4,878
compilation rejections at the 128-version capacity during guest seconds 5–15.

The compiled-version limit is now 512. Code validation, the 8,192-entry
secondary address dictionary, background compiler, cold-code retirement,
event boundaries and save formats retain their existing behavior. Cache-only
ABBA trials gained 2.26% in slot 2 and 2.44% in slot 3. All sampled images,
audio/input/RAM checkpoints and complete final CPU/device/GPU state matched.

A separate native diagnostic identified 10,847 of 11,956 write barriers in
five guest seconds as TLUT palette-load boundaries. Castlevania loads 16-bit
palette sources through 4-bit RGBA destination tiles. The original bridge
waited at every palette load, even when pending CPU writes were unrelated.

The existing texture batching flag now permits deferral across an aligned
16-bit, single-row TLUT source with 1–256 entries, RGBA destination tiles of
4/8/16 bits, and zero tile stride. This uses the pinned renderer's exact source
width and the shader's source-index bounds. TMEM destination wrapping cannot
extend the read range. S/T use quarter pixels; source-word padding is included.
Overlapping writes and unproven layouts retain the original synchronization.

The address proof is tied to paraLLEl-RDP
`1cecd042b2619bc505c12bfdc713808386f2b54d`, specifically
`Renderer::load_tile_iteration` and `update_tmem_lut` in `tmem_update.comp`.
No shaders, GPU ownership rules, FULL_SYNC interrupts, full-memory readback,
published-frame selection, ABI number or checkpoint layouts were changed.
The optimization uses rendering state; it does not identify a ROM or game.

## Measurements and validation

All timing series use reference/candidate/candidate/reference order, cores 6/7,
two native GPU workers, and synchronous shader compilation. Profiling, Vulkan
validation, builds and correctness suites are excluded from timing runs.
The user's other applications remain running.

Slot 2's first combined series measures 29.3770 seconds before and 24.0758
seconds after for guest seconds 5–15: **22.02% greater throughput**. All three
checkpoints, three frame images and the complete final state match in all four
runs. Across the full 15 guest seconds, write barriers fall from 35,483 to
4,476, while commands, CPU-written bytes, synchronizations and frames match.

Slot 3 independently measures 28.7476 seconds before and 22.3719 seconds after:
**28.50% greater throughput**, again with every image/checkpoint and the full
final state matching across all four runs. Generated audio divided by elapsed
wall time is approximately 41.2% of real time in slot 2 and 44.2% in slot 3.
These absolute rates depend on host load and the chosen scene.

| Scene/run | Reference seconds | Candidate seconds |
| --- | ---: | ---: |
| Slot 2 / 1 | 30.2236 | |
| Slot 2 / 2 | | 24.0892 |
| Slot 2 / 3 | | 24.0624 |
| Slot 2 / 4 | 28.5305 | |
| Slot 3 / 1 | 28.8615 | |
| Slot 3 / 2 | | 22.4333 |
| Slot 3 / 3 | | 22.3104 |
| Slot 3 / 4 | 28.6336 | |

The expanded texture suite passes 317 cases against strict GPU ordering,
comparing all RDRAM, hidden memory and TMEM with Vulkan synchronization
validation enabled. Palette cases cover partial/same-value/source-end writes,
source changes, quarter-pixel fields, 4/8/16-bit destinations, two tile slots,
TMEM wrap, the last installed halfword, and fallback layouts.

Mario's independent boot-and-gameplay ABBA series reaches guest second 90 in
each run. All 18 sampled images, audio/input/RAM/task checkpoints, movement
states and final RAM match. The guest-second 70–90 interval takes 27.6551
seconds before and 27.5043 seconds after (+0.55% throughput), effectively
unchanged within timing variation. Unlike the Castlevania state series, this
Mario harness does not export or compare the complete final CPU/device state.

CPU validation passes 2,099 state/event/self-modification cases, including
1,843 compiled cases, 1,050,624 RANDOM-register batching cases and one million
decoder samples. The cache test verifies the new 512-version bound, active-code
retention, cold-code replacement, released executable references and reset.
Controller ports, audio, RSP scheduling and 197 RDP streaming cases pass.

Five fixed-work replays of Rampage, Duke, Mario, Gauntlet and Perfect Dark retain
their previously accepted complete-state hashes after 50 million instructions
(50,000,001 for Perfect Dark). Native ABI checks (68), managed ABI checks (13),
live GPU checks (58, including 20 exact oracle frames) and six GPU savestate
checks also pass. Savestate validation reports zero errors.

Both normal and GPU desktop Release builds complete with zero errors. The
normal desktop MIPS assembly matches the validated normal probe byte for byte;
both desktop outputs exclude performance, JIT-profile and journal hooks.
The launcher's final native library also passes all 317 texture checks with
Vulkan validation enabled. `shipping-audit.json` records the final binary hashes. The original slot
container still matches its recorded SHA-256, and the pinned upstream source
checkout remains clean.

## Artifacts and commands

Local artifacts: `.build-tmp/n64-castlevania-2026-09-23/`. This contains the
preserved container and extracted slots, frozen reference binaries/library,
profiles, isolated cache trials, native diagnostics, final candidates and
complete benchmark outputs. None of these ROM-derived files are versioned.
`combined-slot2`, `combined-slot3` and `mario-abba` contain the final ABBA series;
`replay-results.json` and `validation-driver.log` record the regression checks.

`tools/N64Probe/bench-state.py` now accepts `--reference-gpu-library` and
`--candidate-gpu-library` together. Omitting both retains software rendering.
GPU runs require probes built with `N64LiveGpu=true` and
`N64PerformanceProbe=true`, without JIT profiling or journal capture.

Use `scripts/run-n64-gpu-desktop.sh` and load slot 2 or 3 to try these scenes.
For the next pass, profile both GPU saves again with this version: palette
waiting has changed substantially, so the old profile should not dictate the
next target. CPU block dispatch/RSP execution and full RDRAM/hidden/TMEM
readback are candidates. Reducing readback requires a separate memory-ownership
proof and complete-state/frame/audio comparison.
