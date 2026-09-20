# N64 block opcode classification table

Baseline: `67a80e5b`, including native endian word accesses. Artifacts:
`.build-tmp/duke-block-decode-2026-09-20/`.

## Change

Replace the block interpreter's large opcode-classification switch with a
128-entry table of reserved-bit masks. Ordinary primary encodings and SPECIAL
functions retain their existing dispatch keys. COP0 classification retains the
original MFC0 and MTC0-STATUS checks. The smaller classifier is explicitly
inlined. The table is initialized once and contains encoding rules, not cached
game instructions. Every instruction and delay slot is still fetched live.

Unrecognized entries use an all-ones mask. Every unrecognized encoding is
nonzero; the one zero encoding is the valid SLL/NOP SPECIAL entry. Branches,
operand checks, quiet-interval limits, loop shortcuts, history and timing are
unchanged.

## Equivalence

`--check-block-decode REFERENCE_MIPS_DLL` compares the old and new classifiers:
all 67,108,864 SPECIAL encodings, primary opcode single/complemented-bit cases,
2,097,152 CP0 selector/low-bit combinations, and two million arbitrary words.
**71,209,355 cases pass.** The comparison detects both incorrectly accepted and
incorrectly rejected encodings.

The existing CPU block suite also passes all 609 full-state, rejection,
event-boundary and self-modifying-code cases, its one-million-word decoder
check, and all 264,192 RANDOM counter cases.

CPU-thread state remains
`96D35D3917651781BFF0C4CC30D90EF217B7BDA695EAF27A9BD5A6EC28BDAF77`,
with instruction history
`507E8C28AFAEBC3F055AED63FC2D3CA4D3A79BFE1F30EBFB801E8D5A6654AD2C`.
Twenty-million-instruction replay state hashes remain:

- Duke: `F123855881E70B914F7AACAE83BB43BB44B73B9AEDDCC510EF06A3FA3F76275C`.
- Perfect Dark: `6DF70AF2557F2C750643A18E9D8876C365A3CBF8F3E761BC06A95B657C29CF74`.

## Fixed-work measurements

Release, CPU 6 affinity, identical probe binaries/dependencies except the core
DLL. Each series uses reference/candidate/candidate/reference order. Replays use
three warmups and five measured samples; the CPU-thread fixture uses six measured
samples. Numbers below average the two process medians. Host counters cover the
measured interval and were not multiplexed.

| Workload | Reference ms | Candidate ms | Time reduction |
| --- | ---: | ---: | ---: |
| CPU-thread fixture, 11 million instructions | 307.576 | 291.445 | 5.24% |
| Duke replay, 20 million instructions | 1143.867 | 1092.420 | 4.50% |
| Perfect Dark replay, 20 million instructions | 1584.792 | 1591.216 | -0.41% |

Duke's measured host CPU cycles fall from 3,254,145,600 to 3,158,074,744
(**2.95%**), and host instructions from 7,570,039,796 to 7,400,557,291
(**2.24%**). These are CPU-workload measurements, not displayed FPS.
Perfect Dark is essentially flat in elapsed time in this noisy series; its
host cycles increase 2.45% while host instructions decrease 0.47%. Do not claim
an established Perfect Dark performance gain.

## Actual Duke CPU-thread runs

Four 45-second runs in reference/candidate/candidate/reference order, first ten
seconds excluded. No concurrent builds or profiling. All runs completed with
zero unknown opcodes. The baseline is the previous word-access optimization,
not an older emulator build.

| Version | First run, graphics tasks/s | Second run | Combined |
| --- | ---: | ---: | ---: |
| Reference | 5.608 | 5.595 | 5.601 |
| Table | 5.877 | 5.971 | 5.924 |

This series shows **about 5.8% higher graphics-task throughput**, with both pairs
improving. These are graphics jobs, not displayed FPS. It is a measured gain for
this Duke snapshot and host, not a guarantee for every game or scene.

## Rejected alternatives

A bitset-based classifier also passed the full equivalence comparison, but the
CPU-thread result was too small to justify retaining it. `bitset.patch`, the
`candidate/` directory and `thread-*` logs preserve that prototype.

A hash-bit prefilter before `IsExistingLoopEntry` passed another 2,004,752
comparisons, including all candidate words, single-bit changes and NOP's live
preceding-instruction dependency. It did not reduce measured host work and was
removed. `filter.patch`, `filter/`, `filter-thread-*`, `filter-checks.log` and
`CpuBlockGateChecks.cs` preserve that experiment. The final code keeps the
original loop recognizer unchanged. The retained table variant is in `table/`.

Release UI build: 0 errors, 500 warnings. The UI core matches the measured
and tested table variant, SHA-256
`d22305425322dc260853cc1a04b0ea31ac7d50baba9460b8b10e65f7ebc536be`.
