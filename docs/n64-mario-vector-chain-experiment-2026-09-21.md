# Mario 64: RSP vector-chain experiment, 2026-09-21

Baseline: `f7b05ced` (`Vectorize N64 RSP comparisons and clipping`).
Target: the Linux desktop emulator.

**No emulator change was retained.** Fusing vector instructions improved the
isolated kernels, but longer Mario comparisons did not establish a speedup.
The final candidate measured **+0.08%** over the same emulated clock interval,
which is within the variation between runs. Production sources were restored.

Artifacts and the complete source patch are preserved locally in
`.build-tmp/sm64-fusion-2026-09-21/`. In particular,
`selected-experiment.patch` contains the last implementation, probe entry points,
and its differential checks. The directory named `final/` is an experimental
build, **not** a build to deploy. `reference/` is the accepted baseline.

## Implementation and checks

The block JIT previously called `ExecuteVectorSimd` for each instruction, loading
and saving vector registers and accumulator planes at each step. The experiment
emitted SIMD expressions for consecutive multiply/accumulate instructions and
kept their register values and three accumulator planes in locals. Modified
registers were published before the next ordinary instruction. The existing
16-instruction limits, whole-block IMEM guards, event budgets, progress checks,
and exact instruction history were preserved.

A 25-second diagnostic counted about 1.9 million executions of the common
VMUDL / VMADM / VMADN / VMADH sequence. Across observed blocks, 506 chain copies
represented 199 distinct sequences. These counts identify work to investigate;
the diagnostic run is not a speed measurement.

Four forms were tried:

1. Inline expressions with vector constants. `Expression.Constant(Vector128)`
   introduced unboxing checks and register spills in generated code. This was
   slower and was replaced.
2. Inline expressions with intrinsic constants and native shuffle helpers.
   An inspected two-instruction block shrank from 1,078 to 810 native bytes, with
   the stack reservation falling from 184 to 24 bytes.
3. Shared compiled delegates for identical chains, with a 512-entry cache per
   interpreter and inline fallback. Reducing duplicated code did not improve
   overall throughput.
4. Inline chains with cached element selections. Untouched RHS operands fused
   endian conversion and selection into one shuffle; selections were reused
   until their source register was overwritten.

The final two implementations each passed **9,984 differential chain cases**
and **1,800 unchanged-state budget/watchdog rejection checks** against the
baseline. Tests covered every supported multiply/accumulate opcode, all element
selectors, register aliases, signed boundaries, accumulator carry/wrap, interior
IMEM changes, and transitions through loads, stores, clipping and reciprocal
instructions. Architectural RSP state, history, and SP memory matched. The final
test digest was:

```text
310338C689B4982D8EF0477C10B247F53157190B9A42683234A87D76A8BA5B04
```

The first inline implementation also passed the existing 96 whole-block cases.
Since no candidate met the performance requirement, the additional five-game
replays and moving-game acceptance tests were not run for deployment.

## Measurements

All timed processes ran serially on CPUs 6 and 7, with ordinary runtime settings
and no concurrent build/test. The user's existing applications continued running.
Every run started from the same copied Mario core state and normalized cartridge:

```text
.build-tmp/sm64-20260919/mario-input/state.bin
.build-tmp/sm64-speed-2026-09-20/native-game/input.z64
```

The metric is elapsed guest CPU cycles divided by wall seconds and 93,750,000.
It measures emulated clock throughput, not displayed FPS or full-game playability.

| Candidate | Run duration / discarded warmup | Baseline mean | Candidate mean | Change |
| --- | --- | ---: | ---: | ---: |
| Boxed constants | 40 s / 10 s | 0.734266 | 0.705351 | -3.94% |
| Intrinsic constants | 40 s / 10 s | 0.733082 | 0.728208 | -0.66% |
| Intrinsic constants | 60 s / 15 s | 0.759142 | 0.795591 | +4.80% |
| Shared delegates | 40 s / 10 s | 0.741977 | 0.737643 | -0.58% |
| Intrinsic constants | 90 s / 30 s | 0.861943 | 0.850179 | -1.36% |
| Cached selections | 90 s / 30 s | 0.852700 | 0.857658 | +0.58% |

Each row is a separate four-run A/B/B/A comparison. The promising 60-second
result did not survive the longer confirmation and must not be reported as a
retained 5% improvement.

The 90-second logs were also compared over exactly the same guest cycle range,
8,500,000,000 through 12,000,000,000. This range was chosen before the first
control runs finished. Crossing times were interpolated between one-second
samples to avoid comparing different animation phases solely because one build
had advanced further by a given wall-clock time.

| Candidate | Baseline mean | Candidate mean | Change |
| --- | ---: | ---: | ---: |
| Intrinsic constants | 0.853582 | 0.849313 | -0.50% |
| Cached selections | 0.846522 | 0.847202 | +0.08% |

Six isolated chain benchmarks, warmed for one second each, had lower medians
with fusion, approximately 19–38% shorter execution times in that component
comparison. These component measurements do not establish a Mario speedup.
An earlier iteration-count-only warmup exposed .NET tiering transitions during
measurement; those preliminary component timings were discarded.

Native samples also showed less time attributed to RSP execution, but compiled
RSP code grew from about 1.50 MB to 1.80 MB in the sampled processes. Neither the
sample proportions nor the smaller kernel timings explain the whole-emulator
result on their own.

## Continuation

To revisit the experiment from the baseline, first inspect and apply
`.build-tmp/sm64-fusion-2026-09-21/selected-experiment.patch`, then build N64Probe.
The patch adds `--check-rsp-chains REFERENCE_DLL` and `--bench-rsp-chains`.
`EUTHERDRIVE_N64_RSP_VECTOR_CHAINS=0` provides a runtime control in those
experimental builds. The timing scripts and all individual logs are alongside
the patch.

Do not enable fusion based only on the kernel results. A future revision needs
a repeatable end-to-end gain, followed by the existing complete replay and
moving-game checks. The next separate performance candidate is specializing the
software renderer's pixel loops for stable rendering modes; triangle drawing and
the common texture filters remain substantial costs in the existing profiles.

Accepted baseline and existing UI MIPS DLL SHA-256:

```text
586b3eb180bca092eb92353dc7f88946f98f5ba8a3582183cddb14851868cd15
```

The restored probe rebuilt with 0 errors. Its MIPS DLL hash is
`46d1efc5f12db80c9f214429168e80e2a3d53136fde3f90647f932cc34f76e4f`;
the assembly now records revision `f7b05ced`, whereas the earlier build records
`2e5db6de`. The core sources match Git, and the restored probe passed all 96
whole-block comparisons with the baseline digest
`628309387B5BC5CE21CE79B8E29ACB12EF6472D61B367F2236C9C51B72D66747`.
The UI DLL was not replaced by the experiment.

Last experimental MIPS DLL SHA-256 (`selected/`):

```text
0c3329225f4e46e9fa3bb4d684b547662e342dc8ceefc616e1dc078693bc6064
```
