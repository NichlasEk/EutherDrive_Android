# Mario 64: reuse instruction classification between CPU blocks

Continuation of the [five rejected experiments](n64-mario-optimization-pass-2026-09-23.md).
Linux only. The reference includes the accepted Castlevania/Mega Man changes;
it is not just the committed HEAD. Artifacts are in
`.build-tmp/n64-mario-dispatch-2026-09-23/`.

**Retained:** two independent four-run series improve moving-gameplay
throughput by **2.28%** in aggregate. All captured game/audio/image checkpoints
match. Cross-game checks, the final profile and both desktop builds pass.

## Candidate

The existing profile attributed 14.32% of exclusive CPU-thread samples to
`TryAdvanceCpuBlock` and 2.54% to `IsExistingLoopEntry`. Between instructions,
the block runner fetches a word, checks whether it belongs to an existing
loop shortcut and decodes its instruction class. Repeated execution repeats
the latter two operations even if the fetched word is unchanged.

A 4,096-entry array stores each full instruction word with its classification
for continuation inside a block. The PC selects a slot for locality; it is
not used to establish validity. A hit must match all 32 instruction bits.
Unsupported instructions and existing loop-entry markers cache a negative
result. No ROM name, instruction address or game identity is hardcoded.

The zero word (NOP) is handled separately: its loop-entry status depends on
the preceding live word. That read remains on the existing memory/GPU path.
The first instruction of a block retains its original classifier because
the caller has already considered the longer loop shortcuts.

This cache contains no code bytes used for execution and no register/device
state. The next instruction is still fetched through the original memory
path before every lookup, including GPU synchronization. Operand validation,
native code guards, interrupt deadlines, cycle counting and history remain
unchanged. Code replacement, address aliases and savestate loads do not
require invalidating metadata whose full instruction word still matches.

## Validation and timing

The decoder checks compare four million cached results with the original
classifier/loop recognizer. They cover hits, conflicting words at the same
slot, address aliases and live NOP predecessor changes. The full CPU block
suite checks serialized CPU/RAM/device state against ordinary stepping;
the JIT configuration additionally checks exact instruction history.

Gameplay timing uses the frozen `reference/`, unchanged `reference-native.so`
and original-USA Mario ROM. The comparison runs complete 90-guest-second
boots, with identical input at each Joybus read. The measured walking/swimming
window is guest seconds 70–90, containing 19.6602157261 seconds of audio.
Every accepted run must match all 18 sampled images/checkpoints and final
RAM. This harness does not export complete final CPU/device state.

Both sides use cores 6/7, two GPU workers and synchronous shader compilation.
No profiling, correctness tests or builds overlap the timing series.

| Series / interval | Reference mean seconds | Candidate mean seconds | Throughput gain |
| --- | ---: | ---: | ---: |
| First movement series | 28.1093 | 27.5368 | +2.08% |
| Confirmation movement series | 28.1818 | 27.4981 | +2.49% |
| All eight movement runs | **28.1456** | **27.5175** | **+2.28%** |
| Complete run through guest second 90 | 122.3024 | 121.0078 | +1.07% |

Both series improve, and each candidate's moving interval is faster than
either reference in its own series. The result remains specific to this
workload and host. The selected interval advances from **69.85% to 71.45%**
of audio-derived real time, not 100%. This measures the headless core with
live Vulkan RDP, not desktop frame rate or speed throughout the game.

`combined.json` rechecks all eight runs against one common reference:
18 checkpoints and 18 images per run, with identical final RAM SHA-256
`5488b023dbf86cc030b7c18daada7d5fffcbd9d8187b86ab56daf9486cb1bee7`.
Asynchronous JIT compilation counters are diagnostic data, not architectural
state, and are excluded from this equality comparison. The complete fixed-work
state comparisons below provide separate CPU/device-state coverage.

The original 1,735 bounded-interpreter cases and 2,099 CPU/JIT cases pass,
including 1,843 compiled cases, complete state, event boundaries, live code
replacement, exact history and reset during background compilation. Each
configuration also passes 1,050,624 RANDOM comparisons, one million decoder
samples and the added 4,000,012 continuation-classification comparisons.

## Cross-game and compatibility checks

- CPU JIT admission, retirement, worker publication and reset checks pass.
- Complete serialized CPU/RAM/device states retain their established digests
  after 50 million instructions in Mario, Duke, Rampage and Gauntlet, and
  50,000,001 instructions in Perfect Dark. See `replay-results.json`.
- Mega Man slot 1 and Castlevania slots 2/3 match the accepted reference after
  15 guest seconds: all three sampled image/audio/input/RAM/cycle/task
  checkpoints and complete final CPU/device/GPU state. These continuations
  are correctness checks, not performance comparisons for those games.
  See `cross-game-proof.json`.
- All 58 live GPU checks pass with Vulkan validation and CPU-write auditing,
  including independent Mario oracle frames, read ownership, code overwritten
  by GPU work, partial/same-value stores, DMA and restored images.
- With `EUTHERDRIVE_N64_FAST_IDLE_LOOP=0`, the block runner rejects execution
  without changing machine state, as before. The shared MIPS project builds
  for both net8.0 and netstandard2.0.

Pre-existing tracked changes match `initial.diff` byte for byte. This pass
changes only `R4300.Blocks.cs`, its tests and this note; the native GPU library,
ROM files and user savestate slots are unchanged.

## Final profile and continuation

A separate profile collected 26,248 CPU-thread instruction-pointer samples
and matched all 18 gameplay checkpoints/images and final RAM. The loop-entry
recognizer fell from 2.54% of samples in the preceding profile to 0.70%.
These are exclusive sample shares, not independent wall-time improvements.

| Remaining method | Exclusive sample share |
| --- | ---: |
| CPU block runner | 13.60% |
| RSP slice execution | 7.79% |
| CPU block instruction execution | 5.35% |
| Memory-side RSP progress signature | 3.80% |
| RSP block completion/history | 3.75% |
| CPU operand guards | 3.00% |

`profile/` retains samples and machine-code listings; `profile-proof.json`
records the matching gameplay capture. Use this accepted source as the next
reference. The next CPU investigation should distinguish instruction dispatch
from actual load/ALU work inside `ExecuteCpuBlockInstruction` before choosing
another change. Prior larger JIT capacities, broader instruction coverage and
RSP hash/history variants did not give stable gains and should not be restored
based on coverage counters alone. This checkpoint does not establish 100%
real-time Mario gameplay.

## Desktop delivery

Normal and opt-in GPU Release desktop builds complete with zero errors
(504 and 505 warnings respectively). `delivery-audit.json` verifies that both
contain the classification cache, with no CPU performance callbacks, JIT
profiling or RDP journal instrumentation. The normal desktop's MIPS assembly
matches the validated normal probe byte for byte. Only the GPU build contains
GPU hooks, and the native library is byte-identical to the timing reference.

The normal build is in `EutherDrive.UI/bin/Release/net8.0`; the GPU build is
in `.build-tmp/n64-live-gpu/desktop`. Start the latter with
`./scripts/run-n64-gpu-desktop.sh`. The optimization is enabled by default
within the existing CPU block path and needs no new UI setting. No application
was launched or restarted, and no user savestate slot was modified.
