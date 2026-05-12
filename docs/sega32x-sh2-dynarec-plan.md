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
