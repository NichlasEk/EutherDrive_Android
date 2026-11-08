# R4300 operand-validation trials, November 8

The accepted change keeps interpreter validation outlined and exposes an
aggressively inlined body to generated native blocks. The existing CU1,
address-region, alignment, RDRAM bounds, r0 and branch-link rules are shared
unchanged. Native code can specialize constant instruction operands without
expanding the variable-opcode interpreter loop. Guest clocks, instruction
coverage and GPU barriers are unchanged.

`CanAccessCpuBlockOperand` is the non-inlined interpreter entry;
`CanAccessCpuJitOperand` holds the shared implementation, and `JitValidate`
references that body directly. Tier1 disassembly confirms the interpreter
entry contains the implementation with no extra call to the shared body.
Tier0 can still call the body during warmup; no claim of zero cold overhead
is made. These are .NET compiler hints, not a replacement memory policy.

## MULTU lead

The intrusive dispatch profile measured 849,481 successful outer-loop MULTU
instructions in guest seconds 90–120, including opcode execution and its
shared timing/device boundary. Total measured cost was 72.821 ms, including
instrumentation overhead. Seconds 115–120 accounted for 17.934 ms.

This excludes delay-slot multiplies and surrounding outer-loop/block-exit
costs. It is not a bound on potential multi-cycle JIT improvements. The
standalone multiplication did not justify prioritizing a shortcut. No
multiply behavior changed; its five cycles cannot simply enter the current
one-cycle batch path. Counters remain behind N64CpuDispatchProfile, and the
profile replay matches six checkpoints/images and complete final state.

## Rejected broad inlining

Initially the shared helper alone was marked for aggressive inlining,
including its interpreter callers. Late 95–120 ABBA series gained 5.190%
and 9.260% wall throughput (5.797% / 8.810% thread CPU throughput).
However, guest 70–90 regressed 3.526% wall and 3.700% CPU throughput.
The late audio playback ABBA regressed 0.905% wall throughput, with underruns
191/255 reference versus 275/169 candidate. This variant is not promoted.

It passes 2370 CPU and 2902 JIT cases (2469 compiled), control-game/GPU/state
checks, portable and desktop builds, and the exact-desktop UI save/load gate.
Correctness did not establish an acceptable performance tradeoff. Its source
against `e6fe3de9` is retained in
`experiments/n64-operand-inline-all-2026-11-08.patch`; the tested binaries,
logs and manifests remain under the `operand-inline*` local paths.

The first broad GPU check aborted when NVIDIA could not create a Vulkan
device while other GPU work was active. A separate retry passed all 58
checks with unchanged code. Both logs are retained. No workaround or system
GPU policy change was made.

## Accepted native-only inlining

Both variants use the dense Joybus observer, .NET 10.0.14, the same GPU
library/state/input and GPU settings, unrestricted affinity, and ABBA order.
Our own builds and correctness jobs finish before timing. Other user jobs
remain active, so percentages are observed results, not fixed guarantees.

| Window | Mean reference wall | Mean candidate wall | Wall gain | Thread CPU gain |
| --- | ---: | ---: | ---: | ---: |
| Guest 70–90 | 21.27550095 s | 20.8732167 s | +1.927% | +1.661% |
| Guest 95–120 | 25.69520585 s | 24.6526153 s | +4.229% | +5.572% |

Each series matches six checkpoints/images, generated PCM/input and complete
final RAM/CPU/device/GPU state. The early series has one essentially flat
adjacent pair (-0.17% wall); the late pairs both favor the candidate.

One-second audio-production minima excluding checkpoint/image capture:

| Window | Reference runs | Candidate runs |
| --- | --- | --- |
| 70–90 | 74.44%, 67.57% | 74.77%, 75.55% |
| 95–120 | 75.08%, 79.66% | 79.15%, 73.82% |

Not every short interval improves, and the minimum remains below 100%.
These are instrumented replay throughput measurements, not display frame
pacing. Full real time in all gameplay remains unfinished.

The first playback ABBA uses the adapter and AudioEngine with a null sink,
including frame polling and resampling. Its mean wall gain is 1.281%:
25.10685875 s reference versus 24.78933485 s candidate for the same generated
audio. Sampled underruns decrease in both adjacent pairs, 134→113 and 88→59.
One reference run skips 4390 submission frames over the entire 90–120 run;
the other runs skip zero. AudioEngine reports zero dropped frames throughout.
Six guest/RAM/generated-audio checkpoints match exactly. These counters do
not prove audible quality through a physical sound device.

Local evidence is under `.build-tmp/n64-mario-2026-11-08/`:
`multiply-profile*`, `operand-inline*`, `operand-native*`,
`audio-operand-inline*`, `audio-operand-native*`, and their manifests.

The repeated audio series gains 1.662% wall throughput: 27.38348335 s
reference versus 26.93579535 s candidate. Underruns are 406/228 reference
and 256/276 candidate, with no submission skips or engine drops. Thus mean
underruns fall in both series (111→86 and 317→266), but not every adjacent
pair improves. All six guest/audio/RAM checkpoints again match. The repeat
ran only after our final correctness/build jobs had exited. Physical-device
listening remains unverified; the measured short-window minima still fail
the 100% target.

## Final validation and delivery

The exact narrower candidate independently passes:

- 2370 CPU block cases and 2902 JIT cases (2469 compiled), including
  code mutation, event boundaries, history and asynchronous compilation/reset.
- 49 COUNT rollover and 566 likely-branch cases in the focused JIT run,
  plus the shared decoder/RANDOM/eviction checks.
- Exact Mega Man 64 and Resident Evil 2 CPU/RAM/frame/PCM/input replay.
- 58 GPU checks and portable netstandard2.0 compilation.
- Desktop Release build and exact-desktop UI GPU save/load/resume,
  other-slot preservation, empty-slot and injected-save-failure tests.

The desktop and checker DLL/native-library hashes match. The original user
savestate remains SHA256
`e0b139d1eb43ea3a36d331d927bdbfefb1567f09539c30a9927038e99cfe194c`.

Delivery uses `.build-tmp/n64-mario-2026-11-08/desktop-operand-native/` through
`.build-tmp/n64-mario-2026-11-07/play/run.sh`. Its previous contents are saved
as `run-before-operand-native.sh`, pointing to the accepted COUNT-fix build.
`delivery-operand-native-manifest.json` records the artifact hashes and
accepted source commit. The desktop includes the pre-existing local UI/GPU
startup and savestate changes; this scoped commit does not absorb that
unrelated worktree state. No visible game or physical audio session was
started by this delivery, and no unrelated host workload was stopped.
