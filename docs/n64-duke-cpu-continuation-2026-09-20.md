# Duke CPU continuation: measured experiments

Baseline: `87605269` (production core `e4b307a1`). Same read-only Duke slot 1
and extracted snapshots as `n64-duke-cpu-blocks-2026-09-20.md`.
Artifacts: `.build-tmp/duke-block-window-2026-09-20/`.

## Diagnosis

A 128-instruction window alone did not establish a useful improvement over 32:
fixed-work reference medians 981.548/1071.641 ms, candidate 991.186/1031.152 ms.
The full-state hash matched. The production maximum remains 32.

The probe now supports `--profile-cpu-state CPU_STATE ROM`. It reports block
lengths and ordinary-interpreter fallback counts for one 20-million-instruction
replay. Its instrumented elapsed time is not a performance benchmark.
The 128-window diagnostic found BLTZ 141,444 times, BGEZ 30,404, BLEZ 12,898
and BGTZ 6,346. These signed branches were frequent block boundaries.

## Signed branch blocks

The tested BLTZ, BGEZ, BLEZ and BGTZ variant used the existing quiet-block machinery. Branch
conditions use the full signed 64-bit source before the delay instruction.
Reserved register fields are checked; likely/link variants still fall back.
All added branches were rejected as delay-slot instructions. The existing event,
Count, memory, tracing and self-branch protections remained in effect.

789 complete-state/rejection cases pass, including signed extremes, source
changes in delay slots, Count reads, backwards/forwards targets and reserved
encodings. One million decode samples pass (347,220 accepted). The real CPU
thread fixture retains its prior complete-state and instruction-history hashes.

Pinned CPU 6, three warmups and five samples per replay process:

| Build | Process medians (ms) |
| --- | --- |
| Reference | 1176.174, 1190.930 |
| Signed branches | 1097.613, 1058.933 |

The exploratory numbers suggest 9.8% more fixed-work throughput, but the
reference used the earlier replay harness without profiling branches. Treat
this as a lead, not an isolated core speed measurement. All runs match Duke's
full-state hash `6BF947D1D8777DE7F4BCCD98501A4210207F7F87ADEBADF5340784B577C9FA6F`.

## Framebuffer write bounds

The memory experiment derived an enclosing address interval from all tracked
framebuffers. Writes outside it returned before scanning framebuffer descriptors.
RAM page epochs still advanced exactly as before. Writes within the interval
retained the complete existing overlap and dirty-page logic.

The bounds were rebuilt on every descriptor registration/replacement and state
load. They were derived data, preserving the savestate format. Separate
reference-assembly checks cover overlapping buffers, gaps, replacement, shrinking
and growing heights, clipped buffers, range/alias boundaries and restoring both
older and empty snapshots.

## Decisions

The signed-branch variant was removed: four actual 45-second CPU-thread runs
(reference/candidate/candidate/reference), excluding the first ten seconds,
measured 5.500 versus 5.450 graphics tasks/s (-0.9%). The fixed-work gain did
not establish a gameplay improvement. All runs reached their time limit with
zero unknown opcodes. `results.json` contains the full samples. The optional
renderer diagnostic `OK viType=` is not a completion marker: it was absent from
one completed reference run, so completion is checked against elapsed time and
process exit instead.

The framebuffer bounds variant was also removed. Its fixed-work medians were
1229.869/1260.317 ms against signed-branch reference 1108.796/1090.829 ms.
Despite passing 29 differential framebuffer cases, 131,278 word operations,
73 VI cases and 6,845 rendering/interrupt checks, it regressed performance.
Its full Duke replay hash remained identical.

Both patches, the framebuffer checker and their binaries/logs are retained in
the artifact directory. Neither experiment is enabled in the final app. Host
background work causes timing variation; no unrelated processes were stopped.

## Operand validation experiment

A tested small wrapper separated the non-memory opcode check from the full RAM
operand validator. Only kinds 32 through 63 can represent supported primary
load/store instructions; ALU, branch and CP0 instructions returned true directly.
The original width, address, alignment, RAM-boundary and link-register checks
remain unchanged for memory operands. The wrapper could inline into the block
without inlining the larger memory validator.

`reference-harness/` was prepared with the same probe DLL as `operand/`,
substituting only the baseline core. No further operand replay was needed after
the gameplay regression rejected it. Subsequent fetch and multiply comparisons
use identical probe binaries for both cores.

The operand wrapper was removed as well: four 60-second gameplay runs gave
5.379 graphics tasks/s for the reference and 4.959 for the candidate (-7.8%).
It passed all 609 block cases and preserved both the CPU-thread state/history
hashes and Perfect Dark's replay hash. Correctness alone did not justify it.
The exploratory fixed-work improvement used different harness revisions, so
it is not a reliable isolated core speed claim. `operand-results.json` records
the actual-thread samples; `operand-guard.patch` retains the rejected change.

## Direct RAM instruction fetch

The fetch candidate separated the direct kseg0/kseg1 RAM fetch into a short
wrapper. The original fetch implementation remained intact in `ReadOpcodeSlow`,
including TLB translation, instruction-TLB fallback, device reads and the
`EUTHERDRIVE_N64_FAST_RDRAM_FETCH=0` setting. No instruction cache was added.
The live RAM word was still fetched on every call.

The probe's `--check-opcode-fetch REFERENCE_DLL` reuses the differential word
access workload through `ReadOpcode`: 131,281 operations covering both direct
aliases, unaligned/page/RAM boundaries, ROM, SP semaphore read effects and mapped
addresses. It also runs with fast fetching disabled and with strict ITLB enabled.
Reference and candidate now use identical probe binaries for fixed-work tests.

This fetch candidate was removed. With identical replay harnesses, reference
medians were 1170.784/1208.153 ms and candidate 1156.587/1277.770 ms. Four
45-second gameplay runs measured 5.150 versus 4.999 graphics tasks/s (-2.9%).
The real-thread synthetic medians were effectively unchanged (355.943 versus
353.770 ms); state and history hashes matched. The new opcode-fetch diagnostic
remains available for future changes; it does not alter the app's fetch path.

## Multiply routine code validation (removed)

The final tested candidate compared the multiply routine's 48 live code bytes as six
native 64-bit words, instead of decoding twelve big-endian opcodes. A one-time
initializer derives the comparison words from the existing opcode array using
BitConverter's host byte order. BitConverter also supports the four-byte-aligned
array offsets permitted for guest instructions. The complete RAM range is still
checked before any reads. The routine's arithmetic, memory writes, cycle counts,
interrupt guards and instruction history are unchanged. There is no code cache:
every byte is read and checked at every invocation.

The expanded differential suite changes each of the 48 bytes individually and
checks entry addresses four bytes off an eight-byte boundary, both direct aliases
and the final 48 bytes of RAM, in addition to existing event/state cases.

The multiply candidate passed all 215 cases. The actual CPU-thread multiply
fixture retained both hashes: state
`9A6904A58419F2E1EA91288E5AF7D37492D90AF6C80FBB60F14D0B06F4B02B7F`, history
`C71B9FCD5562BF12809E9438F655F438A2D90A7C87C3628D9FC7044C1BB687F2`.
Its medians were 153.651 ms reference and 146.790 ms candidate. Duke fixed-work
medians were 1038.541/1112.363 ms reference versus 1173.051/1132.280 ms candidate,
with identical full-state hashes. The synthetic gain alone is not a gameplay claim.

## Runtime compilation inspection

`COMPlus_JitDisasm='*StartCpuThread*'` on the original core showed the thread
body as `Tier0-FullOpts` with `Tier-0 switched to FullOpts code`. Thus this
particular loop is already fully optimized, rather than stranded in minimally
optimized Tier0 code. No global runtime compilation setting was changed.
`jit-inspect.log` contains the generated code. For interpretation of runtime
tiers, see the [.NET runtime OSR design notes](https://github.com/dotnet/runtime/blob/main/docs/design/features/OsrDetailsAndDebugging.md).

## Final decision and continuation

The multiply candidate also lost in four actual 45-second runs: reference
5.098 graphics tasks/s, candidate 4.828 (-5.3%). It was removed. The final
production core source is identical to the start of this continuation. All six
experiments were rejected; none provides a substantiated additional speed gain.
The previous session's improvements remain intact. Absolute rates differ from
earlier sessions because host load varies, so only paired controls are compared.

Retained changes are probe diagnostics (`--profile-cpu-state`,
`--check-opcode-fetch`), expanded multiply byte/boundary checks, and this record.
The expanded checks also run against the restored original core. Candidate code
is retained only under `.build-tmp/duke-block-window-2026-09-20/`.

Next work should distinguish block rejection reasons and actual sampled cost
before extending instruction coverage. Fallback counts alone did not predict
gameplay gains. The current runtime already fully optimizes the long CPU thread
method. The earlier compiled-block prototype also regressed (see
`n64-compiled-block-experiment-2026-09-20.md`); avoid retrying handler-call
compilation or a larger instruction limit without a new measured reason.

Duke slot 1 remains unchanged, SHA-256
`1c18b38b54947268807704d028f281c9c889ce295bac12640a84248677d2730a`.

Final verification: the restored core passes all 215 expanded multiply cases
and 131,281 opcode-fetch operations. The diagnostic replay retains Duke's exact
20-million-instruction state hash. Profile output is explicitly labeled
`gameProfile instrumentedMs`, keeping it distinct from benchmark medians.
The rebuilt Linux UI reports zero errors (383 existing warnings). UI and probe
share core SHA-256
`f9d1d780d0f79b6fddbb509a27e5e22bb9ac66cc7570224ff3d9f6f0785d1195`;
the source is unchanged and the rebuilt assembly carries the newer version stamp.
