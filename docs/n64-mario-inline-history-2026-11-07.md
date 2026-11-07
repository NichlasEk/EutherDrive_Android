# Emit exact RSP history stores in compiled blocks

Compiled RSP blocks already know every PC and instruction word. Emit their
ring-history stores as constants instead of calling EndBlock and looping over
an instruction array. Budget and complete code guards still run before any
instruction. The final trace PC, last instruction, repeated-PC count, ring
index and branch destination remain exact. This adds no unsafe memory access.
`EUTHERDRIVE_N64_RSP_INLINE_HISTORY=0` restores the prior path for bisects.

## Validation

The full interpreter differential passes 96 programs, 1,024 sliced progress
checkpoints, 126 exits, 192 lifecycle deadlines and 32 dispatch boundaries.
The netstandard2.0 build passes. Separate ten-guest-second Mega Man 64 and RE2
GPU replays match every checkpoint, image, audio stream, final RAM and complete
serialized CPU/device/GPU state. Their final state hashes remain respectively
`dd63517d893a26f1da97bfaa338322c96512f1cd4a0d4680e7fe156c2e6925c9` and
`8d462b5b27b6c39cb6973d45a6db12a2322002f226a2c56b2b06f54ad1309ad8`.

## Timing

.NET 10.0.14, all 28 CPUs allowed, two GPU workers, direct command processing
and GPU overlap enabled. Both variants use the identical frozen binary with
only the inline-history flag changed. Resume the actual GPU state at guest
second 60; measure seconds 70–90, containing 19.7122401 audio seconds.

| Run | Wall seconds | Audio realtime |
| --- | ---: | ---: |
| A1 old history | 20.4468474 | 96.41% |
| B1 inline history | 20.1088608 | 98.03% |
| B2 inline history | 20.5492943 | 95.93% |
| A2 old history | 22.0943061 | 89.22% |

Mean throughput improves 4.63%, both pairs positive. Host variation is
substantial; this is not a claim of sustained full realtime. Candidate
intervals at guest seconds 70–80 reach only 89.75–92.58%. All six checkpoints,
images, audio/input hashes, final RAM and complete CPU/device/GPU state match.

An initial ABBA omitted the launcher GPU-overlap/direct-command environment
and found +2.84%. It is not evidence for desktop-mode performance. Its repeat
was stopped after discovering the mismatch. The timing harness now defaults
both settings to the launcher values while preserving explicit overrides.
The desktop-mode results above supersede that experiment.

Local evidence under `.build-tmp/n64-mario-2026-11-07/`:
`inline-history/`, `history-rsp-check.log`, `history-portable.log`,
`history-desktop-abba/`, `history-cross-game-proof.json` and
`history-cross-games.py`. The local `bench-history.py` sets the flag to 0 for
reference and 1 for candidate; its source is retained beside the captures.
ROMs, states and images stay local.
