# Gauntlet Legends: retain safe CPU writes across texture uploads

Gauntlet Legends (Europe) slot 1 spent much of its GPU time waiting before
RDP triangles even after the narrow framebuffer-write optimization. The
profiled first five guest seconds had 19,914 triangle barriers and about
7.44 seconds of triangle-idle time. A diagnostic classification found that
every remaining triangle barrier was rejected by the conservative
unsafe-epoch guard before the narrow color/depth overlap test. Safe epochs
had no rejected target overlaps in that run.
Most unsafe epochs began at LoadBlock (RDP opcode `0x33`) or LoadTLUT
(`0x30`).

The optional native `ED_N64_GPU_TRACK_TEXTURE_READS` mode records a
conservative enclosing interval for the RDRAM source of every provable
LoadBlock and LoadTLUT in a safe triangle epoch. The bounds reuse the
existing, pinned-renderer source proofs. A CPU patch may stay queued across
those uploads only when it is disjoint from both the current narrow
color/depth rectangles and every recorded texture-source interval. A patch
that intersects the recorded interval forces the existing GPU idle and
resets the epoch. An unknown source, unsupported load, other draw, VI update,
or target/scissor change continues to end the safe epoch. Full sync,
readback, and savestate completion still use their existing barriers.
The interval is a union envelope, so it may reject safe patches; it cannot
exclude a known source that was added to the epoch.

The mode is on by default only for the verified Gauntlet Legends (Europe)
ROM signature, which already enables narrow triangle writes and a 64 KiB
overlap batch. Other games retain the previous path.
`EUTHERDRIVE_N64_GPU_TRACK_TEXTURE_READS=0` disables the new mode;
`=1` enables it when narrow triangle writes are active. The original ROM and
slot 1 savestate were not changed.

In a diagnostic first-five-seconds run, tracking both LoadBlock and LoadTLUT
reduced triangle barriers from about 19,900 to 4,250 and total write barriers
from about 28,900 to 13,500. Triangle idle fell from about 7.4 to 1.3 seconds.
These are instrumented components, not whole-game timings. The new
diagnostic-only `gpuNativeTriangleNarrowReject` and
`gpuNativeTriangleUnsafe` counters are compiled with `N64_GPU_PROFILE=ON`.

The uninstrumented Linux benchmark reused the same .NET binary and native
library for both modes, neutral input, CPU affinity 26/27, direct RDP
commands, two GPU workers, and the same core savestate. Each run checked
guest cycles, controller reads, audio and input hashes, rendered frame hashes,
RDRAM checkpoints, final RDRAM, and serialized CPU state before accepting
timing. The tested windows were:

| Window and order | Old path mean | Tracked mean | Throughput gain |
| --- | ---: | ---: | ---: |
| 5–10 s, old/new/new/old | 37.987 s | 32.991 s | 15.14% |
| 10–15 s, old/new/new/old | 37.087 s | 32.928 s | 12.63% |
| 5–10 s, new/old/old/new, final build | 37.818 s | 33.365 s | 13.35% |

All accepted runs matched their two or three checkpoints and frames exactly.
The Gauntlet GPU savestate check saved after two guest seconds, then compared
five seconds of uninterrupted play with load-and-resume: CPU/devices/RAM,
hidden bits, TMEM, audio, input, and all five frames matched. The final
launcher build succeeded. A replay through the final binary with no mode
override produced the candidate's exact checkpoint and about 13,500 write
barriers. Forcing `TRACK_TEXTURE_READS=0` produced the same checkpoint with
about 28,900 write barriers, confirming the rollback switch.

Raw artifacts are under `.build-tmp/n64-gauntlet-legends-2026-09-25/`:
`vmadh-followup-gpu-causes-5.log`, `track-texture-smoke-5.log`,
`abba-track-texture-5-10/`, `abba-track-texture-10-15/`,
`abba-track-texture-final-reverse-5-10/`,
`track-texture-save-resume.log`, `track-texture-default-5.log`, and
`track-texture-disabled-5.log`. The regular native library and desktop are
in `.build-tmp/n64-live-gpu/`.

The RSP-side VMADH shortcut was separately tested against the exact
16,384-instruction, 44-command graphics slice. It matched the captured
state but was 0.69% slower in the four-run isolated comparison, so it remains
disabled. The final RSP scheduling check passed 32 slice boundaries.

The managed GPU ABI test passed 13 checks. A fresh GPU readback suite run
could not complete because Khronos SPIR-V validation rejected constants of
8/16-bit types in an upstream shader. The same failure occurred with the
unchanged pre-experiment native library, so this run cannot assess the new
mode; the earlier 77-case readback pass is recorded in the preceding
triangle-wait report. The exact whole-game and save/load comparisons above
are the current correctness evidence for this change.

This is a repeatable Gauntlet improvement, but the measured segment is still
only about 15% of real-time speed. Remaining work needs a fresh profile of
the roughly 4,250 triangle barriers and the other GPU waits, with the same
dependency and exact-state checks.

Follow-up on 2026-09-26: the readback suite completed all 77 checks with exact
memory/hidden bits/TMEM and zero validation errors when run with the launcher's
`PARALLEL_RDP_SMALL_TYPES=1` setting. See
[the whole-emulator profile](n64-gauntlet-phase-profile-2026-09-26.md) for the
exact validation command and the next CPU/TLB bottleneck investigation.
