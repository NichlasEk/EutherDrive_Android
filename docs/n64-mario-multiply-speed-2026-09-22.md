# Mario 64: multi-cycle CPU blocks

Continuation from the accepted [RAM-dispatch checkpoint](n64-mario-ram-speed-2026-09-22.md).
The reference includes the uncommitted RAM and HI/LO changes. Linux only.
Both multi-cycle candidates were removed after timing. They preserved complete
state but did not demonstrate a repeatable gameplay-speed improvement. The
accepted RAM and HI/LO production source and original probe checks were restored
byte for byte; the prototypes and their expanded tests remain in artifacts.

## Rejected candidate

The earlier deterministic JIT trace contains frequent MULTU boundaries in
the code around `0x803815a0`, `0x803815e8` and `0x8038162c`. The candidate
admits MULT, MULTU and DMULTU into quiet interpreted blocks and native JIT
blocks, preserving the existing interpreter's five/five/eight-cycle costs.

Block progress now distinguishes retired instructions from elapsed cycles.
The private native result packs an instruction count and additional cycles
into separate 16-bit halves. Quiet windows are capped at 512 cycles, so
prefix addition cannot carry between the halves. Ordinary one-cycle blocks
keep their original integer result. COUNT/device budgets use elapsed cycles;
RANDOM, instruction history and performance counters use retired instructions.
MFC0 reads observe both values at their original intermediate boundaries,
including the existing delay-slot-before-branch clock accounting.

MULT/MULTU emit the full product and sign-extend each 32-bit HI/LO half.
DMULTU reuses the existing full-width product implementation. All operands
remain live, including r0 normalization and branch-link writes before a
multiply delay slot. A partial memory/CU1 guard exit returns both counters.
Stores still terminate native regions, and code is validated on each entry.
Multiplication loops are excluded from invariant-loop proofs and native
backedges until their HI/LO dependencies and repeated cycle budgets have a
separate proof. They can still execute successive validated blocks.

An alternative retained separate instruction/additional-cycle counters inside
the outer run loop, packing only when calling a native region or an instruction
handler. Although it removed repeated unpacking at loop-budget checks, it
regressed whole-scene throughput and was removed. Its complete CPU/JIT and
five-game correctness checks also passed; source and logs remain in artifacts.

## Correctness validation

The new differential cases cover signed/unsigned extrema, discarded upper
halves in 32-bit multiplication, full-width 64-bit products, r0/source aliases,
reserved bits, exact cycle budgets, live cache reuse, device deadlines,
COUNT/RANDOM and branch delay slots. Additional cases exercise HI/LO feedback
loops, guarded prefixes and self-modifying code across native store exits.

The decoder comparison checks 71,209,355 encodings against the accepted RAM
reference; only valid MULT/MULTU/DMULTU encodings gain admission. Other
instruction encodings, exception paths and reserved-bit rejection stay equal.

- All 2,672 CPU/JIT cases pass, including 2,101 cases that execute compiled
  instructions, complete state/history, stale-code rejection and reset during
  worker compilation.
- All 2,308 bounded-interpreter cases pass against ordinary instruction
  execution, comparing complete serialized state.
- 1,050,624 RANDOM boundary comparisons and one million decoder/cycle samples
  pass. A trace watch at address zero correctly disables batching.
- Native cache admission/retirement, controller ports, audio/resampling,
  RSP scheduling and RDP streaming checks pass.
- Mario, Duke, Rampage and Gauntlet match their complete reference state
  after 50 million instructions; Perfect Dark matches after 50,000,001.
- All 36 live GPU ownership/oracle checks pass with store auditing and Vulkan
  validation, including JIT guards, DMA and partial CPU writes.

## Gameplay comparison

The first four-run series measured 27.9363 seconds for the RAM reference and
27.2606 for the initial packed-counter candidate: **2.48% more throughput**.
All 18 checkpoints, images and final RAM matched. The individual references
took 27.3194 and 28.5532 seconds, while the candidates took 27.2281 and 27.2931;
the variation requires confirmation before calling this an accepted gain.

The separate-counter variant measured 27.2747 seconds reference versus
27.4278 seconds candidate (**-0.56% throughput**), with all 18 checkpoints,
images and final RAM matching. It has been removed.

The packed-counter confirmation measured 27.8724 seconds reference versus
28.1394 seconds candidate (**-0.95% throughput**). Pooling both packed-counter
series gives 27.9043 versus 27.7000 seconds, or +0.74%, with all 18 checkpoints,
images and final RAM equal across eight runs. The initial gain did not repeat,
so this small pooled difference does not justify the additional hot-loop
bookkeeping. No multiplication-block change remains in production.

Both builds use the original-USA
Mario cartridge, identical controller-read inputs and the same unchanged
native Vulkan library. The comparison measures guest seconds 70–90 after
boot, walking and swimming, with two native workers pinned to cores 6/7.
No builds, profiling or correctness tests run alongside these timing samples.

## Continuation

The accepted RAM profile also showed 3.28% of exclusive CPU samples in
`CanAccessCpuBlockOperand`. Its generated code performed five comparisons to
recognize COP1 operations, and native branch-delay guards called it with
constant opcode/link parameters. A separate experiment can simplify that
recognition and allow inlining without introducing new instruction semantics
or cycle accounting. The [operand-guard experiment](n64-mario-operand-guards-speed-2026-09-22.md)
records that investigation. It follows the accepted RAM baseline, not either
of the rejected multiplication candidates.

## Artifacts

`.build-tmp/n64-multiply-2026-09-22/` contains frozen reference probes and
native library, starting source snapshots, candidate builds and validation
logs. ROMs, captures and runtime binaries are not versioned. User savestate
slots are not modified.
