# Mario 64: CPU operand-guard experiments

These Linux experiments started from the accepted [RAM fast path](n64-mario-ram-speed-2026-09-22.md),
including HI/LO support. Both production candidates were removed after timing.
The three CPU files were restored byte for byte from the accepted source
snapshot. The earlier [multiplication trials](n64-mario-multiply-speed-2026-09-22.md)
are also absent. No CPU-cycle or instruction-coverage change remains from
these experiments.

## Candidates

The RAM checkpoint attributed 3.28% of exclusive CPU samples to
`CanAccessCpuBlockOperand`. Three alternatives were tested:

1. Simplify COP1 recognition to one masked comparison and request inlining
   of the general validator. This regressed and was removed first.
2. Emit instruction-specific CU1/alignment/RAM guards in native CPU blocks,
   including a branch delay slot's future link value and r0 precedence.
   The bounded interpreter retained its out-of-line validator, with an early
   return for pure COP1 instructions.
3. Also share the direct RAM load/SW emitter with branch delay slots, retaining
   GPU read synchronization, validated-store notifications and branch ordering.

## Exact-state gameplay measurements

Each series used the original-USA Mario ROM, controller-read input, the
unchanged native Vulkan library, two workers and cores 6/7. The interval from
70 to 90 guest seconds walks/swims and generates 19.6602157261 audio seconds.
No builds, correctness tests or profiling overlapped these timing runs.

| Variant / series | Reference mean seconds | Candidate mean seconds | Throughput change |
| --- | ---: | ---: | ---: |
| General validator inlining | 27.8543 | 28.7147 | -3.00% |
| Direct guards, initial | 27.8552 | 27.2143 | +2.36% |
| Direct guards, confirmation | 27.8324 | 28.5655 | -2.57% |
| Direct guards, all eight runs | 27.8438 | 27.8899 | **-0.17%** |
| Direct guards plus delay-slot memory emission | 27.6879 | 27.6684 | **+0.07%** |

All 18 checkpoints, 18 images and final RAM match, including input/audio
hashes, CPU cycles, Mario actions/positions and graphics/audio task counts.
The smaller candidate's initial win did not repeat. The combined candidate
was effectively flat. Neither justifies retaining new production code.
These are headless emulator-core measurements, not desktop FPS or evidence
of full-game real-time speed.

The first combined-candidate series was interrupted during its first reference
when the user started the GPU launcher. It is archived as `delay-interrupted`
and excluded from all measurements. After the user's trial, `delay-abba`
completed the fresh comparison above. The user reported that the interactive
version felt good; the timing comparison still did not establish an incremental
speed benefit from this CPU change.

## Correctness

- 2,099 CPU/JIT cases pass, including 1,843 compiled cases, full state/history,
  stale code, safe guard prefixes and reset during compilation.
- 1,735 bounded-interpreter cases, 1,050,624 RANDOM comparisons and one million
  decoder samples pass.
- Cache admission, controller ports, audio, RSP scheduling and RDP streaming pass.
- Complete states match after 50 million instructions in Mario, Duke, Rampage
  and Gauntlet, and 50,000,001 in Perfect Dark.
- 48 audited live GPU checks pass, including independent oracle frames, DMA,
  mirrored reads and partial writes. The 12 additional load/delay-slot/same-value
  store cases remain as regression coverage for the existing memory contract;
  they do not require the rejected direct-emission implementation.
- The shared core builds for netstandard2.0.

## Profile and continuation

A separate guard-only profile collected 26,590 CPU instruction-pointer samples
and matched all 18 checkpoints, images and final RAM:

| Method | Exclusive sample share |
| --- | ---: |
| CPU block runner | 14.31% |
| RSP slice execution | 7.38% |
| General RSP SIMD vector routine | 7.16% |
| RSP block completion/history | 3.87% |
| Memory-side RSP progress signature | 3.44% |
| CPU operand validation | 3.18% |

These shares are not predicted wall-time savings. The next isolated experiment
specializes existing SIMD operation numbers at RSP block compilation, using
one arithmetic body and the accepted RAM/HI-LO CPU baseline. Its draft and
validation artifacts are in `.build-tmp/n64-rsp-specialize-2026-09-22/`.

## Artifacts and interactive trial

`.build-tmp/n64-operand-guard-2026-09-22/` retains frozen references/native
library, source snapshots, probes, validation logs and all comparisons.
`direct-combined.json` rechecks the eight guard-only runs. `direct-profile/`
contains samples, machine code and exact-state proof. `inline-source`,
`direct-source`, `delay-source` and their corresponding binaries preserve the
rejected variants. Old continuation/build scripts are historical and must not
be run over a newer experiment's source.

The user's launcher built and started the combined candidate during the trial.
`interactive-desktop-proof.json` records its core features and unchanged native
library hash. The normal desktop was not rebuilt for that candidate. Later
builds should follow the selected experiment rather than this historical proof.

Use `./scripts/run-n64-gpu-desktop.sh` for the opt-in GPU build. Start from ROM
or Reset and check for `N64 GPU` in Deck Monitor. OpenGL describes window
presentation; loading an existing savestate switches to software rendering.
User savestate slots and unrelated working-tree changes were preserved.
