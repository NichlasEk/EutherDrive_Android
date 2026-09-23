# Mario 64: keep CPU JIT operands in host registers

Linux desktop continuation of the accepted
[selective GPU readback change](n64-mario-live-readback-speed-2026-09-23.md).
Artifacts and frozen source/binaries are in
`.build-tmp/n64-mario-registers-2026-09-23/`.

**The register experiment is retained, opt-in, with a new lifetime-aware
emitter. It is not a default speedup.** The first
zero-register candidate improved one series by 6.07%, but regressed by 1.10%
in independent confirmation. That is not an established speedup. The next
iteration below reduces entry/exit work and isolates r0 in a common binary.
Mario's four initial reference runs average 74.57% of audio-derived real time
in the selected walking/swimming interval on this host.

## Candidates

The experiment changes register placement when emitting an existing compiled
CPU block. It does not extend instruction coverage, link blocks, increase the
JIT cache, change guest clocks or skip device deadlines.

`candidate/` selects at most four nonzero guest registers with at least three
uses in the block. It emits IL locals for these registers and publishes their
written values at the shared exit. Locals are initialized only after the
complete live code guard succeeds, including destinations first written later
in the block. This preserves old values on a failed early memory guard.
Loop backedges retain the local values across iterations.

The entire block must contain only the existing integer ALU, direct RAM loads,
direct word stores and branches with an ALU delay slot. Blocks with interpreter
helpers retain array-based register access. GPU RAM reconciliation and the
validated word-store helper do not access CPU GPRs. Compilation analyzes copied
instruction words; its worker never reads live registers.

`candidate-zero/` additionally holds register zero in a local. Normalization
still occurs at the original instruction and delay-slot boundaries. It is not
replaced by an unconditional final zero: the interpreter can temporarily leave
a nonzero value after a write to r0, and a failed memory guard must preserve it
until the instruction actually executes.

Neither variant keeps private CPU state across block returns or in savestates.
Code mismatches and inadequate entry budgets return before initializing locals.
Normal and partial exits publish values before recording instruction history.
JALR captures its target before writing the link; its delay slot observes the
updated link and the original r0 normalization order.

## Correctness and generated code

- The original bounded-interpreter suite passes all 1,735 cases.
- The first candidate passes 2,199 CPU/JIT cases, including 1,940 compiled cases.
- The zero-register candidate passes 2,220 CPU/JIT cases, including 1,961
  compiled cases. Both check full serialized CPU/RAM/device state and exact
  instruction history against ordinary instruction stepping.
- Added cases cover each early memory-guard position, misalignment, MMIO,
  out-of-RAM addresses, dirty r0 at a partial exit, JALR source/link/delay-slot
  aliases, and deterministic ALU chains with more live operands than locals.
- Both suites pass the existing stale-code, reset-during-compilation,
  event-boundary and self-modification checks, plus 1,050,624 RANDOM comparisons,
  one million decoder samples and 4,000,012 continuation-decoder comparisons.

An isolated six-instruction counter loop was compiled through the actual
`BuildCpuJit` method using the default .NET assembly load context. All variants
execute exactly 480 instructions and agree on output registers and PC. Native
code sizes are 676 bytes (reference), 670 (candidate), and 651 (candidate-zero).
The latter keeps repeated register operands in host registers and publishes
them at exit. This inspection is evidence of generated code, not a gameplay
speed measurement. Earlier collectible-load-context listings are archived
separately; they introduce different static access and are not representative
of the desktop/probe.

The preceding CPU-thread profile attributes only 1.45% of exclusive samples
to dynamic CPU methods, versus 13.60% in `TryAdvanceCpuBlock` and 5.35% in
`ExecuteCpuBlockInstruction`. These are diagnostic sample shares from before
the accepted readback change, not a current wall-time budget. They limit the
expectation that register allocation alone can close the gap to real time.

## Gameplay measurement

The reference includes every previously accepted optimization, including
selective readback. All candidates use the same native GPU library, SHA-256
`7b594ea883d196e61c9ea2d457843b6eb71809c1062350375b4e7154d8ec3f07`.
`provenance.json`, `frozen-builds.json` and `source-before/` identify the exact
pre-pass state; the reference is not just committed HEAD.

Complete runs boot the original USA Mario ROM and apply identical input at
each Joybus read. The walking/swimming interval is guest seconds 70–90 and
contains 19.6602157261 seconds of audio. Acceptance requires identical input,
audio, RAM, cycles, game coordinates and task counts at all 18 checkpoints,
all 18 sampled images, and final RAM. This gameplay harness does not serialize
complete final CPU/device state; the differential suite checks that separately.

The first series runs reference, candidate, candidate-zero, candidate-zero,
candidate, reference. The independent confirmation runs reference,
candidate-zero, candidate-zero, reference. Cores 6/7, two GPU workers and synchronous shader
compilation are identical across samples. Profiling, audits, builds and
correctness tests do not overlap timing. The host is an Intel Xeon E5-2697 v3;
`host-cpu.json` records its topology.

| Series / candidate | Reference mean seconds | Candidate mean seconds | Throughput change |
| --- | ---: | ---: | ---: |
| First series, nonzero locals | 26.8425 | 26.1878 | +2.50% |
| First series, including r0 | 26.8425 | 25.3058 | +6.07% |
| Independent confirmation, including r0 | 25.8889 | 26.1756 | -1.10% |
| Pooled eight reference/r0 runs | 26.3657 | 25.7407 | +2.43% |

The pooled positive mean does not establish a stable gain: the independent
confirmation reverses the result, and run ranges overlap substantially.
Reference movement times range from 25.0147 to 27.4618 seconds; the r0
candidate ranges from 24.9003 to 26.9124 seconds. Complete-run timing likewise
reverses from +5.26% in the first series to -2.82% in confirmation.
The nonzero-only variant also overlaps its references and was not retained.

All **10 complete gameplay runs** match one common oracle: 18 checkpoints,
18 sampled images and final RAM per run. `combined.json` rechecks every capture
and records final RAM SHA-256
`5488b023dbf86cc030b7c18daada7d5fffcbd9d8187b86ab56daf9486cb1bee7`.
Asynchronous JIT compilation/instruction counters vary between runs and are
diagnostic metadata, not architectural state or evidence of a speed gain.

## First checkpoint and continuation

`source-variant1/`, `source-variant2/`, `registers-experiment.patch`, both frozen
probes and the differential tests preserve the first experiment locally.
Before continuing with the requested iteration, the source was temporarily
restored and both desktop builds completed. `rollback-proof.json` verifies
that this intermediate tracked source diff was byte-identical to `initial.diff`, SHA-256
`5ec7a9712424683c877cbfdf1529278ac6207318a7ae44f427e586bdd4fb9142`.
All pre-existing uncommitted optimizations are preserved.

The cross-game validation scripts and a new profile were prepared but not run
for those initial candidates. The correctness evidence above is the completed
differential suite and ten Mario captures. ROMs and user savestate slots were
not modified.

## Lifetime-aware iteration

Artifacts for the continued work are in `iteration/`. The first heuristic
missed a common short-block pattern: a temporary written once and read once.
It also loaded original values which a straight-line block overwrites before
using. The new emitter analyzes reads before first writes, includes these
temporary write/read pairs, and initializes only registers whose incoming
value is used. Each early guard selects an epilogue that publishes only its
completed writes. Guards with equal output sets share one epilogue. Native
backedges conservatively initialize all cached registers and publish all loop
outputs, preserving values left by preceding iterations.

`EUTHERDRIVE_N64_CPU_JIT_REGISTERS` chooses a compile-time experiment:

- `0` (default): array register access.
- `zero`: apply the new exit handling to r0 only.
- `1`: additionally cache up to four reused nonzero registers.

The flag is read when the CPU class initializes and consulted when compiling
a block; no option check runs per guest instruction. A single frozen binary
supports all three modes for the next comparison. No game identifier or
special PC is used to select production behavior.

Validation adds 47 cases for temporaries first defined before/after failed
guards and for failure at the start of a later native-loop iteration. All
three modes pass **2,267 CPU/JIT cases each**, including 2,004 compiled cases,
complete serialized state, exact history and the existing worker/decoder
checks. The shared MIPS project also builds for netstandard2.0. Normal,
performance-GPU and audited-GPU probes are frozen separately.

Default-load-context inspections pass both the six-instruction counter loop
and a three-instruction temporary/branch pattern, including both branch
outcomes. Native sizes for the short pattern are 485 bytes (off), 463 (r0),
and 457 (full). Counter-loop sizes are 676, 655 and 666 bytes respectively;
the full variant stores fewer operands inside the loop but has a larger exit.
These numbers describe emitted code and do not establish throughput.

The six-run three-mode comparison is complete. Every run uses the same
`iteration/probe/` assemblies and native library, changing only the register
mode in its environment. Both candidate observations are slower than either
reference observation in this series.

| Register mode | Movement mean seconds | Throughput change versus off |
| --- | ---: | ---: |
| Off | 25.1876 | reference |
| r0 only | 25.7535 | -2.20% |
| Full lifetime-aware placement | 25.9067 | -2.78% |

Complete 90-guest-second mean times are 110.9730, 113.4117 and 113.2304 seconds
respectively. Reduced register traffic has not translated into faster Mario
gameplay. The new design and tests remain available for the requested continued
iteration; default mode is `0` and the accepted optimizations stay enabled.

`iteration/gameplay-proof.json` compares all **16 timed runs across both
iterations** to one common reference: all 18 sampled checkpoints/images and
final RAM match exactly. As before, this capture is not a complete CPU/device
state export. The separate checks below provide that coverage.

With full register placement (`EUTHERDRIVE_N64_CPU_JIT_REGISTERS=1`):

- CPU JIT admission, retirement, worker publication and reset checks pass.
- Mario, Duke, Rampage, Gauntlet and Perfect Dark retain their established
  complete state digests after roughly 50 million instructions each.
- Mega Man slot 1 and Castlevania slots 2/3 match all three 15-guest-second
  checkpoints, sampled frames/audio/input/RAM and complete final CPU/device/GPU
  state against the accepted reference.
- All 58 live GPU checks pass with Vulkan validation and CPU-write auditing.
- All six GPU savestate checks pass, with zero validation errors.
- Disabling CPU batching still rejects it without changing machine state.

These checks establish compatibility for the tested workloads, not a speed
gain in those other games.

## Final profile and desktop delivery

A separate full-register diagnostic collected 26,480 CPU-thread samples after
timing and correctness tests completed. Its 18 checkpoints/images and final
RAM match the common gameplay reference (`iteration/profile-proof.json`).
Dynamic CPU methods account for 1.45% of exclusive samples, while
`TryAdvanceCpuBlock` is 14.12%, `ExecuteCpuBlockInstruction` 4.28%, operand
guards 3.47%, and RSP slice execution 8.75%. These sample shares identify work
to investigate; they are not independent wall-time savings or an explanation
of the measured regression by themselves.

The useful next register experiment is to amortize entry/exit work over more
native instructions, then compare region growth alone with region growth plus
register placement. Prior wider-coverage and block-linking attempts already
show why increasing native instruction counts alone is insufficient. Retain
the current early-exit/full-state tests, keep CPU event boundaries intact, and
require a whole-gameplay comparison for the combination. No region-growth
change is included in this checkpoint.

Both normal and GPU Release desktop builds complete with zero errors.
`iteration/delivery-audit.json` verifies that both include the experimental
register emitter and prior optimizations, without performance callbacks,
JIT profiling or RDP journal instrumentation. The normal MIPS assembly matches
the validated normal probe byte for byte; the native GPU library is unchanged.
All pre-existing tracked changes outside this pass match the initial diff,
and the emitter/test sources match the frozen, measured iteration.

From the repository directory, enable the full experiment with:

```sh
EUTHERDRIVE_N64_CPU_JIT_REGISTERS=1 ./scripts/run-n64-gpu-desktop.sh
```

Use `zero` for the isolated r0 mode, or omit the variable for the default `0`.
The option is read once per process, so changing modes requires a new emulator
process. Existing savestates remain usable. No application was launched or
restarted by this pass, and no user savestate slot was changed.
