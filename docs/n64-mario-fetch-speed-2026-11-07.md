# Validated instruction fetch without a second bounds check

The .NET 10 sampled profile places 14.78% of CPU-thread samples in
`TryAdvanceCpuBlock`. Its generated code repeats array/span extent checks
after the physical fetch helper has already masked the address and checked
all four bytes. On modern .NET, the helper now performs an unaligned native
read after the existing guard and GPU synchronization, with explicit host
endian conversion. The RDRAM array reference is readonly and synchronization
updates its bytes in place. The portable target retains the span read.

No code bytes are cached, no GPU ownership check is skipped, and invalid
physical addresses still return false before touching memory or the GPU.

## Validation

`--check-opcode-fetch` matches 271,045 operations and serialized state against
the frozen DMA build, including unaligned/end-of-RAM/aliased addresses,
random physical addresses, mapped code, live writes, TLB/ASID changes,
reset and save/load. Values digest:
`8BB2009A0F40C1404B5B6C4ECEA955BD8141B0F4886D85064DE015338A26204C`.

Four complete Mario runs match all 18 checkpoints and images, audio/input,
cycles, task counts, Mario action/position and final RAM. Separate ten-second
Mega Man 64 and RE2 GPU replays also match complete final CPU/device/GPU
state, images, audio and RAM. Mario's cold-boot harness itself does not export
complete final CPU/device state.

## Timing

Same .NET 10.0.14 runtime, frozen GPU library, cores 6/7/8/9 and two GPU workers.
Reference includes the accepted DMA change. ABBA, guest seconds 70–90:

| Run | Wall seconds | Audio realtime |
| --- | ---: | ---: |
| Reference A1 | 22.0954950 | 89.21% |
| Fetch B1 | 20.9543299 | 94.07% |
| Fetch B2 | 22.5150394 | 87.55% |
| Reference A2 | 23.2008078 | 84.96% |

Mean throughput gain is 4.20%, with both pairs favoring the candidate.
Both binaries vary with host load; this is not a stable universal 4.2%
estimate or proof of full realtime. In particular the heavier 70–80 section
still falls below realtime. Builds and correctness jobs did not overlap the
timed series; unrelated user applications were left running.

Artifacts under `.build-tmp/n64-mario-2026-11-07/`: `fetch-guard/`,
`fetch-check.log`, `fetch-abba/` (hashed manifest and complete results),
`fetch-cross-game-proof.json`. Private ROMs, states and images remain local.
