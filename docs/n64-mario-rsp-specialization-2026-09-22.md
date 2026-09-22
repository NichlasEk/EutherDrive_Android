# Mario 64: specializing RSP vector operations

Linux experiment from the accepted [RAM/HI-LO checkpoint](n64-mario-ram-speed-2026-09-22.md).
The [operand-guard CPU experiments](n64-mario-operand-guards-speed-2026-09-22.md)
were removed before this candidate was built. The RSP specialization is retained:
eight interleaved Mario runs improve movement throughput by **3.09%**, with
identical states, audio and images. The complete run through guest second 90
improves **2.84%**. Final profiling and both desktop builds are complete.

## Change and generated code

A fresh profile still placed 7.16% of exclusive CPU instruction-pointer samples
inside the general RSP SIMD vector routine. Compiled RSP blocks already knew
the operation number, but the shared runtime routine selected its arithmetic
path on every call.

The block compiler now selects a closed generic helper for each of the 31
existing SIMD operation forms. Small value-type operation tags provide a
constant operation number, allowing the host JIT to eliminate unrelated paths.
The ordinary interpreter calls the same arithmetic body with a runtime tag.
There is one implementation of the arithmetic, accumulator, flag, endian and
register-aliasing rules. No scheduling, instruction-budget or GPU-ownership
behavior changes. The netstandard2.0 and SIMD-disabled paths remain portable.

A diagnostic graphics-task replay verified the resulting Tier1 machine code:
VMULF is 272 bytes, VMADN 409 bytes, and the general runtime form 2,865 bytes.
VMULF has no remaining operation-selection branches; array bounds checks remain.
These listings include vector profiling counters and are evidence of code
specialization, not timings. Cold Tier0 code is larger and must warm up before
comparing steady gameplay. Whole-game measurements use fresh processes and
the same 70–90 guest-second interval as the reference.

## Correctness

- 359,848 vector comparisons pass in each of strict shuffle, alternative
  half-shuffle and SIMD-disabled modes. With SIMD enabled, 281,305 calls per
  run also exercise the specialized helpers against the independent portable
  implementation and the general SIMD path. Cases cover every operation,
  element selector, operand/destination alias, accumulator carry/wrap, packed
  flags, clipping and mixed reciprocal/scalar transitions.
- 18,560 existing RSP instruction cases and task digests match the portable
  reference in each configuration.
- 96 compiled-block programs and 768 slice/history comparisons pass in all
  three configurations. The suite now explicitly disables JIT when loading
  its isolated reference assembly, while requiring actual candidate blocks.
  Every specialized operation also executes through a real compiled block.
- Complete five-game replay states match after 50 million instructions in
  Mario, Duke, Rampage and Gauntlet, and 50,000,001 in Perfect Dark.
- Cache admission, controller, audio, RSP scheduling and RDP streaming pass.
- All 48 audited live GPU checks pass, including independent oracle images,
  RAM ownership, JIT reads, DMA and partial writes.
- The normal/GPU probes and shared netstandard2.0 core build without errors.

## Recorded RSP tasks

The older graphics/audio captures contain an earlier serialized-state format.
Even the frozen unmodified baseline rejects their old expected end states
because the serialized sizes differ. The originals were not modified.

The local `fixture/` helper loads each original start state and register dump,
executes it using the frozen accepted baseline with RSP JIT disabled, and
writes an independent end-state fixture in the current format. Each fixture
records the baseline assembly hash, source paths, instruction count and state
hashes. Both the frozen baseline JIT and the candidate then reproduce the
complete expected CPU/memory and RSP register bytes over cold/warm repetitions.

| Task | Instructions | Complete-state SHA-256 |
| --- | ---: | --- |
| Audio | 18,824 | `AA3A3DE3DB4BCC1FBB4B072DFBF2B36F3CA98E078A41D4BCDC74064B9C1C1D13` |
| Graphics | 794,278 | `A398BDAC1136BBF9972A30361087EF25FC5F8EBB3CBD2CEFBB17EBA5AEC31E35` |

Vector-operation counters also match for the graphics task. Its diagnostic
timings overlap other correctness work and include profiling; they are not a
performance comparison.

## Gameplay comparison and artifacts

The full Mario comparison uses original-USA ROM/controller-read input, the
unchanged native Vulkan library, two workers and cores 6/7. The 70–90 guest-second
walking/swimming interval produces 19.6602157261 audio seconds. Builds, tests
and profiling do not overlap timing runs. All captured states/images must
match before any timing sample is accepted.

Two reference/candidate/candidate/reference series completed with all 18
checkpoints, 18 images and final RAM equal across all eight runs:

| Series / interval | Reference seconds | Candidate seconds | Throughput change |
| --- | ---: | ---: | ---: |
| Initial movement interval | 28.5199 | 27.4278 | +3.98% |
| Confirmation movement interval | 28.1203 | 27.5136 | +2.20% |
| Movement interval, all eight runs | **28.3201** | **27.4707** | **+3.09%** |
| Complete run through guest second 90, all eight runs | **124.3653** | **120.9308** | **+2.84%** |

The initial movement times are 28.5591 / 27.1765 / 27.6792 / 28.4807 seconds
in execution order. Confirmation is 27.5226 / 27.6294 / 27.3978 / 28.7180.
Both series improve in aggregate, although not every adjacent pair does.
The variation is material; report the pooled result rather than the best pair.
Complete-run timing also improves in both series and checks that later gains
do not hide extra JIT startup cost.

The selected movement scene advances from 69.42% to 71.57% of audio-derived
real time under this session's host load and affinity. These are headless core
measurements with live Vulkan RDP, not desktop FPS, whole-game speed or 100%
real-time proof. Do not combine these absolute rates with earlier sessions or
add their percentage gains arithmetically.

## Final profile and next target

A separate final profile collected 26,572 CPU instruction-pointer samples.
All 18 gameplay checkpoints, images and final RAM match the timing reference.
The combined specialized/general vector helpers account for 5.32% of samples,
compared with 7.16% in the preceding diagnostic profile. These exclusive shares
identify where execution spends time; they are not independent speed gains.

| Remaining work | Exclusive sample share |
| --- | ---: |
| CPU block runner | 15.96% |
| RSP slice execution | 7.46% |
| All SIMD vector helpers combined | 5.32% |
| General CPU block instruction execution | 4.06% |
| Memory-side RSP progress signature | 3.74% |
| RSP block completion/history | 3.62% |
| CPU operand validation | 3.05% |

The next larger CPU experiment should preserve the fast one-cycle block loop
while targeting the hot multiplication boundaries. Earlier prototypes added
per-instruction cycle bookkeeping and did not improve the game reliably.
A different approach could account for known extra cycles at native block
boundaries, using prefix metadata for guarded exits. COUNT/RANDOM, intermediate
MFC0 reads, branch-delay order and exact device deadlines need explicit proof
before accepting any new coverage. Do not reintroduce the rejected packed
hot-loop bookkeeping unchanged. The rejected RSP history/signature trials are
also documented in the HI/LO checkpoint.

Selective GPU readback remains a separate ownership/synchronization project.
The native library, full-copy behavior and GPU savestate limitations are
unchanged by this RSP work.

## Desktop builds

The normal and opt-in GPU Release desktop builds complete with zero errors.
`desktop-proof.json` verifies that both contain the retained specialization,
RAM fast path and HI/LO support, without the rejected CPU guards/multi-cycle
experiments or CPU performance callbacks/JIT profiling. The normal desktop's
MIPS assembly matches the validated normal probe byte for byte. Only the GPU
build contains GPU hooks. The native library hash remains unchanged.

The normal build is in `EutherDrive.UI/bin/Release/net8.0`; the GPU build is in
`.build-tmp/n64-live-gpu/desktop`. Start the latter with
`./scripts/run-n64-gpu-desktop.sh`, from ROM or Reset. Existing savestates still
switch to software rendering. No application was launched or user slot changed
by this final build step.

## Artifacts

`.build-tmp/n64-rsp-specialize-2026-09-22/` contains frozen probes/native library,
starting source, the candidate, validation logs, regenerated task fixtures,
machine-code listings, and measurement/profiling scripts. User savestate slots
and unrelated working-tree changes are preserved.
