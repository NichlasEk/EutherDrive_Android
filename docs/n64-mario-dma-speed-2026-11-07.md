# Batched SP DMA write notifications

The .NET 10 Mario profile found 3.96% of sampled CPU-thread instruction
pointers in `ExecuteSpDma`, including inlined byte-by-byte GPU dirty-mask
updates. SP-to-RDRAM DMA already copied each contiguous chunk before notifying
its writes, but repeated the GPU ownership notification for every byte.

`NoteSpDmaWriteRange` now publishes that chunk once. It retains the old number
of write epochs, each page's last byte epoch, each overlapping framebuffer's
own last byte epoch, and dirty framebuffer pages. Arithmetic retains unsigned
wrap. RAM/SP bank wrapping and DMA scheduling are unchanged. Journal builds
retain their original byte-granular notification path and records.

## Mario measurement

Xeon E5-2697 v3, RTX 4090, .NET 10.0.14, affinity 6/7, two GPU workers,
overlap and single-threaded command processing enabled. Same frozen native
library as the runtime comparison. Four complete cold boots ran in ABBA order,
with timings taken between the guest-second 70 and 90 Joybus checkpoints.
That interval contains 19.7122401 seconds of generated audio.

| Run | Wall seconds | Audio realtime |
| --- | ---: | ---: |
| Reference A1 | 23.5894613 | 83.56% |
| DMA B1 | 20.9822243 | 93.95% |
| DMA B2 | 21.1814530 | 93.06% |
| Reference A2 | 22.5683641 | 87.34% |

Mean throughput improved 9.47% in this series. Both comparison pairs favor
the candidate, but reference variation means this is not a universal speedup
estimate. Every run matched all 18 image/audio/input/cycle/RAM/player/task
checkpoints and final RAM. This Mario harness does not compare complete final
CPU/device state. The heavier 70–80 second section remains at 86–89% audio
realtime; this result does **not** establish sustained full realtime.

## Correctness and artifacts

The DMA differential test now covers 216 cases, with and without tracked
overlapping framebuffers, queued descriptors, busy/completed state, both
directions and SP banks, RAM/SP wrapping, multi-block/skip transfers, and
write-epoch overflow. All serialized states match the frozen reference.
Combined state digest:
`4DD82AA1F6946B6ACD9C44507CAB7D6AAF3E324360CF62F69D9E12744B39AA38`.

Separate ten-guest-second GPU savestate replays of Mega Man 64 and RE2 also
match all checkpoints, images, audio, final RAM and complete serialized
CPU/device/GPU state. Their final state hashes are respectively
`dd63517d893a26f1da97bfaa338322c96512f1cd4a0d4680e7fe156c2e6925c9` and
`8d462b5b27b6c39cb6973d45a6db12a2322002f226a2c56b2b06f54ad1309ad8`.
The GPU desktop Release build succeeds; its candidate is in `desktop-dma/`.

Local artifacts are under `.build-tmp/n64-mario-2026-11-07/`:
`profile10/`, `dma-batch/`, `dma-abba/` (including hashed manifest), and
`dma-check-expanded.log`, and `dma-cross-game-proof.json`. ROMs, saves and
binary captures remain local.

## CPU affinity follow-up

An independent ABBA used the identical DMA binary and runtime for both variants,
comparing cores `6,7` against `6,7,8,9`, with two GPU workers in both. The four
wall times were 21.6597388, 21.1135269, 20.9754654 and 22.9352199 seconds.
Both pairs favor four cores, with a mean throughput difference of +5.95%.
Reference variation is substantial; the four-core runs are still only
93.36–93.98% audio realtime. All 18 checkpoints/images and final RAM match.
Artifacts: `affinity-abba/` and the local `bench-affinity.py` used for this run.

`bench-sm64.py --candidate-cores 6,7,8,9` now supports reproducing that comparison
and records both affinities. The desktop launcher remains unrestricted: this
test does not compare four cores against unrestricted scheduling, and core IDs
are specific to this host. No system-wide scheduler setting was changed.
