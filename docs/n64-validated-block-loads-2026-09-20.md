# Validated word loads in N64 CPU blocks

Baseline: `d2cd9bc8`. Artifacts are in
`.build-tmp/duke-block-loads-2026-09-20/`; `words/` is the retained variant.

## Change and safety

The block interpreter already validates the effective address, alignment and
complete RAM range before executing each instruction. LW and LD now read that
validated RAM directly with BinaryPrimitives, avoiding the ordinary handlers'
repeated address checks and memory dispatch. Other loads and all stores retain
their original handlers. No instruction timings or block limits change.

The effective address is still calculated after r0 normalization and branch
link writes. LW sign extends to 64 bits, LD preserves all bits, and both advance
PC by four. Existing rejection paths keep MMIO, TLB and misaligned accesses on
the ordinary interpreter. Tracing disables CPU blocks. There is no code cache;
self-modifying instructions continue to be fetched live.

## Measurements

Release, CPU 6 affinity, same probe harness with only the core DLL swapped.
No builds or sampling profiles ran concurrently with the timing runs. Fixed
replays use reference/candidate/candidate/reference order, three warmups and
five measured samples per process. Values average the two process medians.

| 20 million guest instructions | Reference ms | Candidate ms | Change |
| --- | ---: | ---: | ---: |
| Duke | 945.178 | 919.514 | 2.72% less time |
| Perfect Dark | 1358.728 | 1387.044 | 2.08% more time |

Duke host cycles decrease 2.49% (2,796,695,640 to 2,727,172,149.5), and host
instructions decrease 1.21% (7,398,398,458.5 to 7,308,675,295). Perfect Dark is
slightly slower in this series: cycles increase 2.01%, instructions 0.35%.
This change is retained for Duke; it does not establish a Perfect Dark gain.
Host counters cover only execution, excluding state loading and hashing, with
no multiplexing.

Six actual Duke CPU-thread runs use the unchanged playable snapshot, 45 seconds
per run, excluding the first ten seconds. Order: reference/words/words/reference,
then words/reference as an additional confirmation pair. Unknown opcodes remain
zero throughout.

| Pair | Reference graphics jobs/s | Candidate graphics jobs/s |
| --- | ---: | ---: |
| 1 | 5.999 | 6.333 |
| 2 | 6.282 | 7.350 |
| Confirmation | 5.923 | 6.621 |

Combined by measured duration: **6.068 to 6.768 graphics jobs/s (+11.53%)**.
All three pairs improve, but host/run variation is substantial, especially the
second candidate. Treat this as a snapshot-specific measured result, not an
11.5% universal speedup or a displayed-FPS measurement. The fixed-work CPU
result above is much smaller.

## Correctness

The block suite adds 84 full-state comparisons covering all supported load
widths, signed payloads, cached/uncached aliases, final valid RAM addresses,
negative offsets, destination/base aliasing, writes to r0, and delay slots.
Existing checks include link-register address changes, misalignment, MMIO/TLB
rejection, event boundaries and self-modifying code.

Twenty-million-instruction replay hashes match the baseline exactly:

- Duke: `F123855881E70B914F7AACAE83BB43BB44B73B9AEDDCC510EF06A3FA3F76275C`.
- Perfect Dark: `6DF70AF2557F2C750643A18E9D8876C365A3CBF8F3E761BC06A95B657C29CF74`.

## Alternatives rejected

`candidate/` implements every supported load through an extra shared helper.
It passed 693 block cases, but actual game throughput was essentially flat.
`inline/` embeds every supported load in the main dispatch; its game result
was noisy and did not establish an improvement. The final version embeds only
LW/LD and keeps the dispatch smaller. Prototype source snapshots remain in
`helper-variant.cs` and `inline-variant.cs`. Final fixed-work reference logs
replace the earlier prototype reference logs; do not pair the old candidate
replay logs with these final references.

A fresh CPU sample before edits still attributed most managed execution to
the CPU outer loop, block interpreter, multiply shortcut and RAM writes.
`profile-summary.txt` excludes synthetic Speedscope CPU_TIME markers when
attributing exclusive time; inlined work remains charged to its parent.

Final validation: **693 block cases**, 1,000,000 decode samples and 264,192
RANDOM cases pass. The tracing-disabled check also passes with a watch address
of zero. Release UI builds with 0 errors and 383 warnings. Its core DLL matches
the measured and tested `words/` variant, SHA-256
`7527e76413adbb5e1a57bc4f1c25b59679b8296630f91827cf6610f9de822e34`.
