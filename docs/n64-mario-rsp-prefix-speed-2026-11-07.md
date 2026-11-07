# Preserve live RSP countdowns while reusing unchanged register hashing

The progress watchdog hashes seven SP registers, DMA lifecycle fields and
selected RSP GPRs. During DMA, the countdown changes every interpreted
instruction while the register bytes often stay unchanged. The earlier
whole-memory-signature cache trial could not reuse work during those ticks.

This change caches only the seven-register prefix, owned by `Memory` so every
RSP executor sharing that memory receives the same invalidation. Slice entry,
RSP state loading, CP0 instructions, DMA completion, task completion and
SP/DP interrupt completion invalidate it. The DMA armed flag, countdown,
queued flag and GPRs remain live on every existing observation. The complete
signature, stagnation behavior, guest clocks and device boundaries are unchanged.

## Correctness

Against an isolated ordinary-interpreter reference, the fixed candidate passes:

- 96 mixed scalar/vector/block programs, including a separately constructed
  RSP executor sharing Memory (not just Memory's own executor).
- 1,024 sliced progress/history checkpoints, including CPU writes between
  slices, short and long DMA, queued work and IMEM changes.
- 126 slice/watchdog/budget/BREAK/invalid-instruction exits.
- 192 DMA, task and SP/DP interrupt deadline checkpoints.
- 32 block budget/branch/code-mutation boundary checks.

The first draft invalidated only Memory's own RSP instance and failed the
separate-instance comparison. It was corrected before timing. All timings
below use `rsp-prefix-fixed/`, which passes the complete suite.

## Mario timing

ABBA against the accepted instruction-fetch build, .NET 10.0.14, cores
6/7/8/9, two GPU workers, unchanged native library and GPU flags. Four complete
cold boots; timing window is guest seconds 70–90 (19.7122401 audio seconds).

| Run | Wall seconds | Audio realtime |
| --- | ---: | ---: |
| Reference A1 | 23.4851414 | 83.93% |
| Prefix B1 | 21.9917584 | 89.63% |
| Prefix B2 | 21.3117094 | 92.49% |
| Reference A2 | 22.5523596 | 87.41% |

Mean throughput improves 6.31%, with both comparison pairs positive.
Host variation remains visible; do not multiply the gains from different
sessions to claim an aggregate speedup. Every run matches all 18 checkpoints,
18 images and final RAM, including audio/input, cycles, task counts and Mario
action/position. Complete final CPU/device state is not exported by this
Mario harness. Sustained full realtime is still not established.

## Diagnostic direction

A separate instrumented cold boot counted 57,213,375 lifecycle skips:
57,070,767 with only DMA pending, 122,949 with only DP pending, and 19,659 with
both. About 71.8 million interpreted primary-opcode 16 instructions also
occurred. These are work counts, not wall-time gains. The diagnostic used the
initial prefix draft with the normal owned RSP instance; its 18 gameplay
checkpoints/images and final RAM match the reference. The separate-instance
problem above is covered by the fixed production candidate's tests.

Separate ten-guest-second Mega Man 64 and RE2 GPU replays match checkpoints,
images, audio, final RAM and complete serialized CPU/device/GPU state against
the accepted fetch build. See `prefix-cross-game-proof.json`.

`N64RspProfile=true` now reports lifecycle masks (DMA=1, task=2, SP=4, DP=8)
and fallback opcode counts. Mario writes an `rsp-profile.txt` marker, and the
timing harness rejects such instrumented captures.

Artifacts under `.build-tmp/n64-mario-2026-11-07/`: `rsp-prefix-fixed/`,
`prefix-fixed-rsp-check.log`, `prefix-abba/`, `rsp-lifecycle-profile/`,
`rsp-lifecycle-proof.json`. ROMs and captures remain local.
