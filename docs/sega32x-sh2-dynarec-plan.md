# Sega 32X SH-2 Dynarec Plan

Goal: grow the dynarec slowly from measured SH-2 paths, then freeze only stable paths into compiled blocks. The first version should behave like a trace cache with better bookkeeping, not like a broad speculative compiler.

## Current Ground

The SH-2 core already has the pieces a dynarec should reuse:

- `FastOpcodeTable` and `DecodedOp` give a compact opcode IR.
- `TryExecuteDecodedBlock` already validates cached blocks against opcode bytes and `Sega32XSh2Bus.GetExecutableVersion`.
- `BuildAndResetPerfPcSummary` already reports PC hotspots.
- `TryExecuteLinkedIdleRing` proves that narrow, measured path fusion can improve After Burner without changing fingerprints.
- `CycleLimit`, `ShouldStopExecution`, and `HasPendingInterrupt` are the hard fences. Compiled paths must respect them.

The naive Expression-based block compiler was the wrong first shape because it compiled too many cold or unstable blocks, missed too often, and did not encode actual branch paths. The next dynarec should start as "paths in amber": observe repeated paths, require stability, then compile exactly those paths.

## Non-Negotiables

- Keep interpreter as the source of truth.
- Compile only when a path is hot and stable across multiple frames.
- Never compile through pending interrupts, delay slots, unknown opcodes, DMA-stop boundaries, or self-modifying executable pages.
- Every compiled trace must have a cheap invalidation key based on executable version and opcode fingerprint.
- Every milestone must match fingerprints before enabling by default.
- Per-title hacks are allowed only as temporary probes. The final mechanism should be global and data-driven.

## Phase 1: Trace Collection

Add a small per-CPU trace collector behind `EUTHERDRIVE_S32X_TRACE_PATHS=1`.

Collect:

- Start PC.
- CPU side: master or slave.
- Sequence of `{ pc, opcode, DecodedOpKind }`.
- Exit reason: branch taken, branch not taken, interrupt fence, cycle limit, unsupported op, delay slot, self-modifying invalidation.
- Register dependency summary for branches: `T`, `R[n]` used by `JMP/JSR/BRAF/BSRF`, memory address for polling loads.
- Hit count and last frame seen.

Rules:

- Max trace length: 64 ops initially.
- Stop at page boundary, unsupported opcode, external write-sensitive bus access, or any branch with unstable target.
- Sample only hot PCs from the existing PC histogram at first, not every PC.

Output:

- Perf summary line like:
  `trace_paths=seen=123 hot=8 stable=3 exits=taken:42/not_taken:11/limit:5`.

## Phase 2: Path Stabilization

Promote a collected trace to "amber candidate" only after it repeats exactly:

- Same start PC.
- Same executable version.
- Same opcode fingerprint.
- Same branch path for at least `N` hits.
- Same exit PC shape.

Suggested thresholds:

- `N=32` hits for branchy traces.
- `N=8` hits for straight-line traces longer than 16 ops.
- Evict traces not seen for 300 frames.

Important: branch traces should not compile generic branch logic first. They should compile the observed route, then guard the branch condition at the exit edge.

Example shape:

```text
trace start 060003C0
  MOV.L literal -> R8
  MOV.W @R8 -> R1
  MOV.W @(1,R8) -> R0
  CMP/EQ R0,R1
  BF not taken guard
  BRA 06000820
  NOP delay
  ...
exit guard: if compare changed, bail to interpreter at 060003C8
```

## Phase 3: Amber Interpreter

Before emitting IL or machine code, add an "amber interpreter" that executes a stabilized trace from a compact op array.

This is not the final dynarec. It is a correctness bridge:

- No dictionary lookup per opcode.
- No decode per opcode.
- One loop over `AmberOp[]`.
- Guards at the path exits.
- Same cycle accounting as interpreter.
- Bailout returns `{ pc, npc, delaySlot, consumedCycles }`.

This lets us test path semantics before adding compile complexity.

Acceptance:

- Faster than `TryExecuteDecodedBlock` on After Burner slave path.
- Same fingerprints for After Burner, Chaotix, Doom, Kolibri.
- No gameplay speedup bug. Capacity FPS may rise, emulated time must not.

## Phase 4: Tiny Compiled Trace Backend

Only after the amber interpreter is stable, add a compiled backend behind `EUTHERDRIVE_S32X_DYNAREC=1`.

Start with one backend target:

- C# delegate generated from a hand-written template, not broad Expression trees.
- Compile only arithmetic/register ops and direct PC-relative literal loads first.
- Leave bus reads/writes as calls to existing `Sega32XSh2Bus` methods.
- Compile guards explicitly.

Initial supported op groups:

- `Nop`
- `MovImm`, `MovReg`, `AddImm`, `Add`, `Sub`
- `And`, `Or`, `Xor`, `Not`, `Neg`
- `Extu*`, `Exts*`, `Swap*`
- `Cmp*`, `Tst*`, `MovT`, `SetT`, `ClrT`
- shifts/rotates
- `MovLDispPc`, `MovWDispPc`
- simple direct branches as guarded trace exits

Do not compile first:

- `Jsr`, `Rts`, `Jmp`, `Braf`, `Bsrf`
- multiply/divide until measured hot
- GBR memory ops until bus call overhead is understood
- writes to executable SDRAM without invalidation tests

## Phase 5: Branch Path Compilation

Grow from straight-line traces to branch paths:

- Compile the common branch route.
- Guard the branch condition.
- If guard fails, set PC/NPC to the real branch decision point and return to interpreter.
- Delay-slot ops stay explicit and must be part of the trace fingerprint.

This is where most 32X games should benefit: not from compiling random blocks, but from avoiding repeated dispatch around tight SH-2 branch paths.

## Phase 6: Scheduler-Aware Traces

Once individual traces are stable, allow traces to consume a bounded cycle budget:

- Never execute beyond `bus.CycleLimit`.
- Check `HasPendingInterrupt` at trace entry and at configured guard points.
- For long traces, insert interrupt guard every 16-32 ops.
- Keep `AccumulatePcSample(startPc, cycles)` for summary, plus per-trace counters.

This matters because 32X performance is often scheduler overhead plus SH-2 dispatch, not just arithmetic cost.

## Cache Design

Use separate caches:

- `TraceProbeCache`: cheap hit counters and fingerprints.
- `AmberTraceCache`: stabilized compact traces.
- `CompiledTraceCache`: optional compiled delegates.

Key:

```text
cpu_side + start_pc + executable_version + opcode_fingerprint
```

Eviction:

- Small fixed max, e.g. 256 amber traces and 128 compiled traces.
- Clear compiled trace on executable version mismatch.
- Keep probe cache frame-aged so boot-only paths do not poison gameplay.

## Instrumentation

Add counters before enabling anything by default:

- `trace_probe_hits`
- `amber_compiled`
- `amber_hits`
- `amber_bails`
- `amber_invalidations`
- `native_compiled`
- `native_hits`
- `native_bails`
- `native_invalidations`
- `avg_ops`
- `avg_cycles`

Perf output should let us answer:

- Are we compiling the hot PCs?
- Are compiled traces being reused?
- Are guards bailing too often?
- Did cache invalidation fire?
- Did CPU capacity improve without changing frame fingerprint?

## Verification Matrix

Minimum headless runs after each milestone:

```bash
EUTHERDRIVE_HEADLESS_CORE=32x dotnet run --project EutherDrive.Headless/EutherDrive.Headless.csproj -c Release --no-build -- "/run/media/nichlas/Atlas/roms/Genesis/32x/After Burner Complete (32X) (JU) [!].32x" 300
EUTHERDRIVE_HEADLESS_CORE=32x dotnet run --project EutherDrive.Headless/EutherDrive.Headless.csproj -c Release --no-build -- "/run/media/nichlas/Atlas/roms/Genesis/32x/Knuckles' Chaotix (32X) (JU) [!].32x" 180
EUTHERDRIVE_HEADLESS_CORE=32x dotnet run --project EutherDrive.Headless/EutherDrive.Headless.csproj -c Release --no-build -- "/run/media/nichlas/Atlas/roms/Genesis/32x/Doom (32X) (JU) [!].32x" 180
EUTHERDRIVE_HEADLESS_CORE=32x dotnet run --project EutherDrive.Headless/EutherDrive.Headless.csproj -c Release --no-build -- "/run/media/nichlas/Atlas/roms/Genesis/32x/Kolibri (32X) (W) [!].32x" 300
```

Known fingerprints from current baseline:

- After Burner 300: `0x6F7E35F544D78D46`
- Chaotix 180: `0x68304EA51873A25A`
- Doom 180: `0xAE285731C9C42083`
- Kolibri 300: `0x8B4DB07DE236D7B2`

## First Concrete Patch

The next code patch should not emit native code yet. It should add:

1. `Sh2TraceProbe` and a fixed-size probe dictionary.
2. Path recording for hot PCs only.
3. Trace stability counters.
4. Perf output for stable candidates.
5. No behavioral change unless `EUTHERDRIVE_S32X_TRACE_PATHS=1`.

After that, the next patch can add the amber interpreter for only one or two measured traces. That gives us a stable platform for real dynarec work without gambling on the whole SH-2 ISA at once.

## Handoff: Growing Toward 60 FPS

This is the hopeful path forward from the current state. The useful work is no longer to add one more title-specific turbo switch. The useful work is to turn the measured hot paths into a repeatable trace system that can make After Burner, Chaotix, Doom, Kolibri, and later titles faster for the same reason.

Current position:

- The interpreter is still the reference implementation and should remain the fallback.
- Several narrow SH-2 fusions already prove the approach works when the shape is measured first.
- The perf summary can now report existing fusion families, so the next measurements can show whether a game is helped by an old fusion, missed by it, or needs a new trace shape.
- The cast catalog below contains the first stable loop shapes. Treat these as seed crystals, not as the whole dynarec.

Near-term goal:

- Reach a real 60 fps capacity path by removing SH-2 dispatch and scheduler overhead from the hottest repeated loops first.
- Preserve emulated speed. The monitor can show more headroom, but gameplay and audio must not run fast.
- Make every speedup visible in perf output before it becomes default.

Recommended next patch order:

1. Add `Sh2TraceProbe` behind `EUTHERDRIVE_S32X_TRACE_PATHS=1`.
2. Use the existing PC histogram to start traces only from hot PCs.
3. Record exact opcode paths, branch decisions, exit PCs, cycle count, and executable version.
4. Emit a compact candidate report after each headless run.
5. Promote only repeated paths into `AmberTrace` objects.
6. Execute promoted traces with an amber interpreter before any native/IL backend exists.

The amber interpreter is the key bridge. It should feel almost boring:

- Predecoded ops in a tight array.
- No opcode lookup during the hot path.
- Bus reads and writes still call the existing bus.
- Branch route is guarded rather than re-decided generically.
- Bailout restores PC/NPC/delay-slot state and returns to the interpreter.
- Cycle accounting remains explicit.

Expected first wins:

- After Burner should mostly confirm the linked idle-ring work and show whether more master-side paths remain.
- Doom should benefit from clean self-branch/idling treatment on the slave.
- Chaotix should expose whether the PC-relative polling loop is hitting the right fast path or only appearing in the histogram.
- Kolibri should identify the slave-side `0207416A` path, which is likely the next interesting cast.

60 fps strategy:

- First remove wasted cycles from wait loops and polling loops.
- Then remove decode/dispatch from stable gameplay traces.
- Then compile only the top 5-10 amber traces if the amber interpreter still cannot reach target.
- Avoid broad opcode coverage until the measured traces demand it.

Do not do this next:

- Do not resurrect the broad Expression block compiler as the main path.
- Do not make SH-2 clocks run faster globally.
- Do not special-case individual ROM names except as temporary trace probes.
- Do not compile through interrupts, DMA-sensitive bus traffic, unknown delay-slot behavior, or executable SDRAM writes.

Success criteria for each step:

- Fingerprint matches baseline for the verification matrix.
- Perf output shows trace hits, bailouts, and invalidations.
- UI fps/capacity improves without audio or gameplay running too fast.
- A disabled dynarec path still leaves the interpreter behavior unchanged.

If the amber interpreter gives a measurable win, the first compiled backend should be tiny and template-driven. Compile register math, compares, literal loads, and guarded direct branches first. Keep bus access as calls. That gives most of the dispatch win without pretending the entire SH-2 is solved.

## Current Cast Catalog

These are the first measured "amber" shapes from the current 32X verification set. They should stay generic even when discovered from one title.

### After Burner Complete

Slave SH-2 linked idle ring:

```text
060003C0 D834  MOV.L literal,R8
060003C2 6181  MOV.W @R8,R1
060003C4 8581  MOV.W @(1,R8),R0
060003C6 3100  CMP/EQ R0,R1
060003C8 8B02  BF exit
060003CA A229  BRA poll
060003CC 0009  NOP
06000820 D808  MOV.L literal,R8
06000822 6081  MOV.W @R8,R0
06000824 2008  TST R0,R0
06000826 89E5  BT back
060007F4 ADE4  BRA 060003C0
060007F6 0009  NOP
```

Current implementation: `idle_ring_fusion`.

### Knuckles' Chaotix

Master SH-2 PC-relative register polling:

```text
060008F8 D10D  MOV.L literal,R1
060008FA 6211  MOV.W @R1,R2
060008FC 2228  TST R2,R2
060008FE 89FB  BT 060008F8
```

Current implementation: `pc_relative_polling_fusion`.

### Doom 32X

Slave SH-2 self branch:

```text
0203612A AFFE  BRA 0203612A
0203612C 0009  NOP
```

Current implementation: `idle_branch_fusion`.

### Kolibri

Master SH-2 register polling:

```text
06000B34 6011  MOV.W @R1,R0
06000B36 2008  TST R0,R0
06000B38 8BFC  BF 06000B34
```

Current implementation: `polling_fusion`.

Kolibri also has a slave-side hot PC around `0207416A`. Keep it as a candidate for trace collection rather than hand-coding it until we have a stable path shape from longer gameplay captures.
