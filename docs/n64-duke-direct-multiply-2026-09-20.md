# Direct arithmetic in the N64 multiply shortcut

Baseline: `95d8c60f`. Artifacts:
`.build-tmp/duke-direct-multiply-2026-09-20/`.

Duke's hot clock loop repeatedly calls the existing compiler-generated multiply
leaf shortcut. The candidate removes its two redundant RAM reads and arithmetic
instruction-handler calls. Four ordinary word stores still occur through `Memory.WriteUInt32`, preserving
RAM write epochs and framebuffer tracking. The arguments' low words reconstruct
the exact big-endian operands those stores produce. The existing unsigned
64-by-64 multiply implementation produces both HI and LO; the candidate writes
all affected registers, including sign-extended result halves, directly.

The core targets netstandard2.0, so the implementation reuses its portable
multiply helper rather than requiring UInt128 or changing the target framework.
All existing live-code validation, stack alias/bounds checks, interrupt and
Count guards, history entries, and 19-cycle/12-instruction accounting remain.
The optimization does not skip iterations of Duke's timing loop.

The expanded differential suite adds 64 operand pairs with carries, signed
boundaries, zero, all-one halves and unrelated high argument bits. All 279
cases compare complete serialized state against ordinary instruction execution;
rejected entries must leave state untouched.

The real-thread fixture retains state hash
`9A6904A58419F2E1EA91288E5AF7D37492D90AF6C80FBB60F14D0B06F4B02B7F`
and instruction-history hash
`C71B9FCD5562BF12809E9438F655F438A2D90A7C87C3628D9FC7044C1BB687F2`.
Duke's 20-million-instruction replay retains state hash
`6BF947D1D8777DE7F4BCCD98501A4210207F7F87ADEBADF5340784B577C9FA6F`.

Actual gameplay uses identical probe assemblies with only the core exchanged,
CPU 6 affinity, and the same read-only slot-1 snapshot. Four 60-second runs use
reference/candidate/candidate/reference order; the first ten seconds are excluded.
Graphics tasks/s measure game progress, not necessarily displayed frames/s.


The arithmetic-only first variant retained the SW handlers. Its four actual
runs measured reference 5.325/6.125 and candidate 5.066/5.416 graphics tasks/s
(combined 5.725 versus 5.241). This did not establish a gain. The second variant
also bypasses those four handlers: stack address/alignment were already checked,
and the normal RAM writes have no PC-dependent side effects with tracing off.
It still performs four distinct writes, preserving each write epoch. Both
variants pass all 279 cases and preserve Duke's replay hash.

## Final measurements and decision

| Series | Reference graphics tasks/s | Direct graphics tasks/s | Difference |
| --- | ---: | ---: | ---: |
| Four 60-second runs, R/D/D/R | 5.398 | 6.009 | +11.32% |
| Four 60-second runs, D/R/R/D | 6.145 | 6.096 | -0.80% |
| Four 30-second runs, R/D/D/R | 6.130 | 6.206 | +1.24% |

The first apparent gameplay gain did not reproduce consistently. These results
must not be presented as an established FPS improvement. The final shorter
series uses a probe that also reports cumulative `cpuSeconds` from the probe
process, excluding initialization. It includes all probe threads, not just the
emulated CPU. In these runs CPU time was almost equal to elapsed wall time, so
CPU scheduling contention alone cannot explain the observed variation. CPU
frequency and workload-phase variation were not separately controlled.

In the actual CPU-thread multiply fixture, six-run medians were 147.556 ms
reference and 135.982 ms direct (7.84% less elapsed time; 8.51% higher synthetic
throughput). Both produce exactly the same full-state and history hashes.
This fixture isolates repeated calls to the leaf routine; it is not a game FPS
benchmark. The final small simplification is retained for this isolated cost
reduction and simpler execution path, with no claimed stable game-speed gain.
Unlike the earlier coverage/cache experiments, it introduces no new dispatch
checks, cache, instruction coverage, or timer-loop shortcuts.

Validation: all 279 differential multiply cases pass. Duke and Perfect Dark
20-million-instruction replay hashes match their reference values. Perfect Dark:
`6DF70AF2557F2C750643A18E9D8876C365A3CBF8F3E761BC06A95B657C29CF74`.
Checks with batching disabled and tracing enabled also pass, confirming fallback.
Every timed gameplay run completed with zero unknown opcodes. User slot 1 is
unchanged, SHA-256
`1c18b38b54947268807704d028f281c9c889ce295bac12640a84248677d2730a`.

The arithmetic-only patch, final candidate/reference assemblies and all raw
measurement logs remain in the artifact folder. The probe CPU-time field is
retained to make future performance claims easier to audit. A larger improvement
still requires reducing the main instruction-dispatch cost or a rigorously
validated timer-loop transformation; this change does not make Duke full-speed.

The final Release UI build completed with zero errors and 500 warnings from the
full solution build. Its core is byte-identical to the tested candidate, SHA-256
`767e35ac9a14861cd3bc20ea9760ff12b6de24ffd87da62e33c91c746ecd1f1a`.
