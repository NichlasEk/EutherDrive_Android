# Duke CPU block boundary investigation

Baseline: `7c97346e`, production core unchanged from `e4b307a1`.
Artifacts: `.build-tmp/duke-block-reasons-2026-09-20/`.
All gameplay comparisons use the same probe with only the core DLL exchanged,
CPU 6 affinity, and the immutable Duke slot-1 snapshot. No user processes were
stopped. Rates count graphics tasks, not displayed frames.

## Diagnostic findings

Temporary core counters in a 30-second gameplay run counted 26,750,365 blocks
and 407,175,350 accepted instructions. Stops included 11,158,945 unsupported next
instructions, 7,462,656 existing-loop entries, 4,386,742 budget boundaries, and
4,266,689 branch/delay boundaries. Some branch/delay rejections are insufficient
remaining budget, so their default opcode cannot identify an unsupported NOP.
The instrumentation was removed before performance measurements.

The existing-loop stops overwhelmingly preserve the multiply shortcut. Pending
interrupts (248,586 entry rejections) were comparatively uncommon. The counters
do not justify weakening interrupt checks or bypassing existing shortcuts.

The probe now reports `blockStopOpcodes` in `--profile-cpu-state`. It reads only
in-range direct RAM, without an extra device read or address translation. The
profile is diagnostic and its elapsed time is not a benchmark. The original
20-million-instruction full-state hash is preserved:
`6BF947D1D8777DE7F4BCCD98501A4210207F7F87ADEBADF5340784B577C9FA6F`.

A prominent boundary is BLTZ at `0x8004c0f0`, targeting `0x8004bf78`. This clock
loop calls osGetTime, multiplies, updates RAM accumulators and can dispatch game
updates. It is not safe to treat it as a side-effect-free idle loop. The frequent
JAL `0c030f88` calls the short interrupt-status restore helper at `0x800c3e20`.

## COP1 blocks: rejected

A candidate admitted valid nonbranch COP1 operations and aligned direct-RAM FP
loads/stores. It reused the existing handlers and checked CU1 on each operation,
including delay slots and STATUS changes. COP1 branches still fell back.

The candidate passed 1,149 full-state/rejection cases and one million decoder
samples (410,437 accepted). Checks include FR modes, odd registers, special FP
values, CU1 transitions, delay slots and self-modifying stores. Six additional
FCR31 cases were written after that run but were not executed before rejection.
The rejected patch is retained as `cop1-blocks.patch` only in the artifact folder.

A retained `--bench-cop1-block-thread` fixture executes 14,000,001 instructions
through the actual CPU thread and verifies the FP result, final state and
instruction history. Candidate median 465.537 ms versus reference 681.123 ms
was a synthetic improvement. Duke fixed-work replay medians were
1026.926/991.698 ms versus reference 1065.082/1037.752 ms, with equal state hashes.

Four actual 60-second Duke runs, excluding their first ten seconds, measured
reference 6.416/5.595 versus candidate 5.636/5.560 graphics tasks/s. This did not
establish a gameplay gain. The core change was removed despite the synthetic
improvement. Host variability prevents interpreting these as precise regressions.

COP1 fixture hashes:
- State: `9A8816B90BAC8BB73C20AF54CCE971ED81E343D1E6C23EF0C4D2758727FBDB99`
- History: `116AAD3F040482B15EC10062CD280D19053FB589574A220AD8382A864450157A`

## Larger blocks: actual gameplay comparison

The earlier 128-instruction experiment tested fixed replay only, which omits
much of the outer CPU loop. New profiling found many 31/32-instruction blocks
and 132,029 length-74 blocks in the 128-window replay. This warranted an actual
CPU-thread gameplay comparison before discarding the idea.

Six 45-second runs use reference/candidate/reference/candidate/candidate/reference
order. Both builds retain all event, Count, code-fetch and self-branch guards.
Results and the final decision are recorded below.

Reference rates: 5.774, 6.286, 5.946 graphics tasks/s. Candidate rates:
6.085, 6.070, 5.770. Combined reference 6.002 versus candidate 5.975 (-0.45%);
CPU cycles/s changed -0.51%. All six runs completed with zero unknown opcodes.
`window-results.json` records the samples. There is no demonstrated gain, so the
production limit remains 32. No production core edits are retained this turn.

## Continuation

Retained changes are the side-effect-free block-stop diagnostic, the COP1 CPU
thread fixture, and this record. A useful next investigation should measure
cost inside the hot clock loop or assess direct arithmetic code generation;
adding handler calls to compiled blocks was already rejected in the earlier
compiled-block experiment. Merely extending instruction coverage or increasing
the block limit has not translated to higher gameplay throughput. Any timer-loop
shortcut must preserve RAM writes, exact Count/Random reads and event boundaries.

Final verification: Release probe build completed with zero errors. Against the
restored production core, the new COP1 thread fixture retains both hashes above;
the diagnostic replay retains Duke's complete-state hash. The user's slot-1 file
is unchanged, SHA-256
`1c18b38b54947268807704d028f281c9c889ce295bac12640a84248677d2730a`.
