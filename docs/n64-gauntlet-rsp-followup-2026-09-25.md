# Gauntlet RSP follow-up — 2026-09-25

This pass used Gauntlet Legends (Europe) slot 1 from
`.build-tmp/n64-gauntlet-legends-2026-09-24/slot1-core.bin`, the current
64 KiB Vulkan overlap path, CPU affinity 26/27, and neutral input. No tested
RSP execution change showed a reliable whole-emulator gain; all three
candidates were removed. Only diagnostic counters compiled with
`-p:N64RspProfile=true` remain.

The diagnostic five-guest-second run is
`.build-tmp/n64-gauntlet-legends-2026-09-25/rsp-byte-profile-5.log`.
It recorded 67.97 million block attempts, 35.58 million executed blocks,
21.44 million cached non-block attempts, and 291.54 million instructions.
The instrumented block path took 23.53 s, including 5.36 s dispatch and
14.15 s delegate execution. These inclusive, instrumented times are for
locating work, not gameplay-speed percentages.

The new diagnostic `vectorBytes` counters showed that the common short
vector transfers already use their contiguous fast path. In this scene,
SSV had 11,850,455 fast and zero fallback calls. SDV had 7,660,903 fast
and 165,444 fallback calls. The other measured 1/2/4/8-byte loads and
stores had zero fallbacks. Optimizing only the generic fallback cannot
materially improve this scene.

Three isolated candidates were compared against the same frozen Release
reference, with the same native GPU library and interleaved
reference/candidate/candidate/reference runs over guest seconds 5–10:

| Candidate | Reference mean | Candidate mean | Throughput change | Decision |
| --- | ---: | ---: | ---: | --- |
| Decode the aligned block first word with one native load | 44.069 s | 43.963 s | +0.24% | Removed: below stable resolution |
| Return directly for a cached `NoBlock` delegate | 46.140 s | 46.699 s | −1.20% | Removed |
| Specialize compiled SSV/SDV stores | 42.974 s | 62.337 s | −31.06% | Removed: GPU-wait outlier, no demonstrated gain |

All runs matched guest checkpoints, frames, audio/input hashes, final RAM,
and serialized CPU state. Each candidate also passed the 96-program RSP
block differential and 768 progress-slice comparisons. Raw results are in
`abba-rsp-word-5-10/`, `abba-rsp-noblock-5-10/`, and
`abba-rsp-store-5-10/` under `.build-tmp/n64-gauntlet-legends-2026-09-25/`.

The last series had a large external-looking GPU-wait excursion: the first
reference reported 17.5 s `waitCopyRenderMs`, the first candidate 54.7 s,
while both issued the same 2,161,481 commands and produced identical
frames. The next candidate returned to 44.65 s wall time; the two references
were 44.83 s and 41.11 s. The measured −31.06% is therefore not an
isolated RSP cost. The host needs a more stable GPU window or a fixed-work
RSP replay before small RSP improvements can be trusted.

Next useful experiment: build a fixed-work RSP task replay that includes
the GPU-backed task state exactly, then measure individual compiled vector
helpers with a low-overhead profiler. Only promote an RSP change after
that focused win survives the same full-scene ABBA and exact-state checks.

The fixed-work slice replay and its separate full-state GPU slice oracle are now
documented in [the slice report](n64-gauntlet-rsp-slice-2026-09-25.md).
