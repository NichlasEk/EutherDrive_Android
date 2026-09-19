# Perfect Dark CPU performance — 2026-09-19

Baseline: `05d2fc57`, following the VI height/readback correction.
Local evidence: `.build-tmp/perfect-dark-perf-2026-09-19/`.
Input: the original Perfect Dark (USA) (Rev 1) slot 1, extracted into
`.build-tmp/perfect-dark-vi-2026-09-19/core-state.bin` (adapter) and
`cpu-state.bin` (raw R4300 state). These formats are not interchangeable.

## Profile and scope

A 20-second CPU sampling trace during a 45-second resumed run attributes
54.85% of leaf samples to InterpretOpcode, 12.21% to the CPU loop, 10.09%
to ReadOpcode, and 6.33% to address translation. RSP inclusive cost is 5.82%.
The dominant PC is 0x70001938: BEQ r0,r0,self with a NOP delay slot.
This address uses the TLB and is outside the existing direct-mapped idle
fast-forward recognizer. Extending that recognizer is not part of this change.

This is an intro/idle workload, not a gameplay FPS measurement. Graphics tasks
reach 228 and then remain there while audio and CPU cycles continue. A live
run alone cannot establish whether that pause is expected intro behavior or
a separate compatibility issue. Do not convert the CPU benchmark gain into
a claim about whole-game FPS.

## Retained implementation

NOP and the exact self-branch encoding 0x1000ffff bypass redundant decoding
when debug/dispatch tracing is disabled. The self-branch still calls the
original BEQ handler, including delay-slot fetch and exceptions. The common
per-instruction epilogue remains shared and unchanged in behavior. There is
no batched time advancement or skipped instruction/device tick.

Other instructions obtain a readonly reference to the initialized opcode
table entry, avoiding repeated metadata copies. The public value-returning
lookup remains available, and mask matching/order are unchanged. Unsupported
instruction formatting is kept in a non-inlined throwing helper.

The first fast-path-only version slowed the mixed CPU thread workload by
roughly 11%. It was rejected. The retained reference-lookup combination removes
that regression; see `refpaths-*` logs for the accepted version. The older
`candidate`, `final`, `combined`, `nop`, `helper` and `decode` directories are
experimental builds, not the selected deliverable.

## Measurement method

The new `--bench-cpu-idle RAW_CPU_STATE [REFERENCE_MIPS_DLL]` command replays
500,000 real self-branches, including their delay slots: exactly one million
instructions, preserving device ticks, Count/Compare and RANDOM updates.
The outer CPU thread's interrupt servicing is excluded from this microbenchmark.
Each run serializes and hashes the full CPU/memory state. Forty warm-ups precede
12 measurements; reported times are the median of those measurements.
An optional reference DLL is for differential correctness only, not timing.

Independent processes pinned to CPU 6 are used for timing. The broader
`--bench-cpu-thread` test runs 10,000,001 mixed instructions through the real
CPU thread, with three warm-ups and six measurements. Reference/candidate
order is reversed to check for drift. No profiling or concurrent build runs
during these measurements.

Early short warm-ups exaggerated the idle gain (about 32%); that estimate is
superseded. An experimental TLB cache and several dispatch variants were
rejected because they added no stable benefit or slowed mixed instructions.
Their sources, binaries and logs remain in the local evidence directory.

## Correctness checks

The idle harness also compares 40 cases covering Count wrap, Compare,
RANDOM/WIRED boundaries, a dirty zero register, delay-slot arithmetic/stores,
unsupported instructions and a TLB miss. Diagnostics tests include NOP and
self-branch with tracing/debug enabled. Opcode lookup is checked against the
original ordered instruction scan, including invalid encodings.

Expected full-state SHA-256 values:

- Idle: `769D0CC84817199C9466D680B3B2CB49E4515AD0CB758DC65A0F88C3530C2ED1`
- Idle edge cases: `8396BB29721CA0F6451CCF5FF266FE3FF32FF98A58C3CDD3C6322F215637DDE1`
- Mixed CPU thread: `41CBC873ED5B0FE3C41D49EBDC5166F7D6197F0A35B5EDA74670ABBCD86B0617`

## Accepted measurements

All times in milliseconds, medians within independent processes:

| Workload | Baseline, two runs | Retained, two runs |
| --- | --- | --- |
| Perfect Dark mapped idle, 1 million instructions | 42.679 / 41.291 | 34.448 / 34.737 |
| Mixed CPU thread, 10,000,001 instructions | 808.362 / 810.524 | 802.535 / 819.214 |

The mean of process medians falls from 41.985 to 34.593 ms for idle work:
**17.6% less host time** (about 21.4% more instruction throughput). The mixed
workload changes by only +0.18%, within the observed run variation; no general
CPU speedup is claimed. This is a Linux/.NET 8 measurement, not an Android
performance result.

## Final validation and continuation

- Idle replay and all 40 edge cases match baseline full-state hashes.
- 852,358 opcode cases pass, including 16,617 rejected encodings.
- Diagnostic text and full state match with tracing/debug enabled.
- 709 CPU loop differential cases pass (176 accepted loops).
- Mixed CPU thread state matches in all measured replays.
- Linux UI Release build succeeds (383 warnings, zero errors). Its
  `Ryu64.MIPS.dll` matches the tested binary byte-for-byte:
  `9ba122c4669f2258e6ef86ba0de204c8c541eb695da1f4e561b34382eb544b54`.
- A 60-second resumed smoke run with Start/A input finishes without unknown
  opcodes. Graphics remain at task 228 while audio continues, so reaching
  gameplay is still unproven. This smoke run overlapped build/correctness
  checks and is not used as a performance measurement.
- The original savestate remains unchanged, SHA-256
  `8d16ea33d2e16723c0948f0910bdb9b9209771df62b4af95a9c2a636cfc36d97`.

The accepted binaries are in `accepted/` (identical core to `refpaths/`).
Reproduce differential validation from the repository root:

```sh
dotnet .build-tmp/perfect-dark-perf-2026-09-19/accepted/N64Probe.dll \
  --bench-cpu-idle .build-tmp/perfect-dark-vi-2026-09-19/cpu-state.bin \
  .build-tmp/perfect-dark-perf-2026-09-19/reference/Ryu64.MIPS.dll
```

For timing, omit the reference DLL, run the reference and accepted probes in
separate `taskset -c 6` processes and reverse their order. Avoid other emulation,
builds or profiling during timing. Run `--bench-cpu-thread` in both processes
as the broader regression check.

Next useful Perfect Dark checkpoint: capture an actual menu/gameplay state and
profile that workload. If the intro remains stationary in the UI as well,
investigate the graphics-task plateau and TLB-refill history before treating
more idle throughput as the route to playable speed.
