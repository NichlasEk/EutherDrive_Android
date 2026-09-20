# Duke gameplay: direct dispatch and a bounded multiply routine

Baseline `1d1c6a40`. Artifacts: `.build-tmp/duke-playable-2026-09-20/`.
The user confirmed movement works but gameplay is still too slow and supplied
another slot 1. Source slot-file SHA-256:
`1c18b38b54947268807704d028f281c9c889ce295bac12640a84248677d2730a`.
Extraction verifies its embedded payload hash; the original file is only read.

## Changes

Ten common primary encodings now call their existing instruction handlers
directly: J, JAL, BEQ, BNE, ADDIU, ANDI, ORI, LW, SW, LD. All operand bits are
valid for these encodings and all have one cycle in the current table. The
shared timing epilogue remains. Debug/dispatch tracing uses the original table
path. Reserved encodings, including restricted LUI encodings, are not widened.
COP1 usability and exception handling are unchanged.

Hot-PC samples identify repeated execution of the compiler's 64-bit multiply
leaf routine around `0x800bb928`. A second optimization recognizes all twelve
live instructions, without hardcoding that address. It uses the existing SW,
LD and arithmetic handlers, then accounts for exactly **19 cycles and 12
instructions**. JR's final delay instruction is executed before returning to
RA. It preserves HI/LO, scratch registers, stack writes, register zero,
Count/Compare, RANDOM/WIRED and recent-instruction history. The outer CPU loop
records eleven entries because JR's delay slot is not separately recorded.

Acceptance requires aligned direct-mapped code and a 16-byte stack range
inside backing RAM, no overlap between code and stack (including KSEG aliases),
matching live opcodes, and a quiet device interval covering all 19 cycles.
Pending interrupts, Count mismatch/wrap/Compare boundaries, nearby VI/device
events, delay-slot state, tracing, debugger and single-step modes reject the
batch. Rejection makes no state changes and uses ordinary interpretation.
`EUTHERDRIVE_N64_FAST_IDLE_LOOP=0` also disables it. No emulated clock changes,
frame skipping, save-format changes or cached self-modifying code.

## Measurements

All timed processes are sequential, pinned to CPU 6, without builds/profiling.
The CPU replay uses the actual ROM, 20 million instructions, three warmups and
five measured runs per process. Final comparisons run in both orders:

| Workload | Baseline | Candidate |
| --- | ---: | ---: |
| Gameplay replay, process median 1 | 1493.288 ms | 1356.351 ms |
| Gameplay replay, process median 2 | 1526.123 ms | 1320.845 ms |
| Multiply routine, real CPU thread | 364.353 ms | 157.881 ms |
| Mixed instructions, real CPU thread | 754.212 ms | 726.206 ms |

Gameplay replay gains **12.8% throughput / 11.3% less host time** using the
average process medians. Full-state hash matches:
`6BF947D1D8777DE7F4BCCD98501A4210207F7F87ADEBADF5340784B577C9FA6F`.
Earlier `bench-*.log` measures direct dispatch alone; the `batch-bench-*` and
`reverse-bench-*` logs measure the combined final change.

The synthetic multiply-thread workload executes 4,250,001 instructions and is
about 2.3x faster. It includes the real CPU loop, instruction fetch and history;
its gain must not be presented as whole-game speed. State hash:
`9A6904A58419F2E1EA91288E5AF7D37492D90AF6C80FBB60F14D0B06F4B02B7F`.
History hash (including ring position and every PC/opcode pair):
`C71B9FCD5562BF12809E9438F655F438A2D90A7C87C3628D9FC7044C1BB687F2`.
The mixed benchmark retains state hash
`41CBC873ED5B0FE3C41D49EBDC5166F7D6197F0A35B5EDA74670ABBCD86B0617`.

Separate normal 30-second headless runs from the user's state, excluding the
first five seconds, produce:

| Gameplay run | Cycles/host second | Graphics tasks/host second |
| --- | ---: | ---: |
| Baseline | 12.53 million | 3.95 |
| Candidate | 14.11 million | 4.42 |

This is about **12% more graphics work per second**, not yet playable real-time
speed. Graphics tasks are not necessarily unique displayed frames. Wall-time
runs finish at different emulated points; the fixed-work tests above establish
state equivalence. Broader CPU block execution remains future work; this change
only batches the fully validated multiply routine.

## Validation

- 180 direct-dispatch cases compare full CPU/device/RAM state and exceptions
  with the baseline, including register-zero operands, alignment faults, taken
  and untaken branches, and faulting/non-faulting delay slots.
- 175 multiply-batch cases compare against the ordinary interpreter or verify
  that rejection leaves state unchanged. Coverage includes randomized inputs,
  cached/uncached aliases, overlapping stack/code, live-code edits, Count wrap
  and Compare edges, RANDOM/WIRED, all device deadlines, VI line edges, pending
  interrupts and delay-slot state, debug and single-step.
- Explicit fast-loop-off, hot-PC tracing and memory tracing disable the batch.
  Any nonempty `EUTHERDRIVE_TRACE_*` variable conservatively disables it;
  `WATCH_ADDR=0` is a valid address, so zero is not treated as a universal off.
- 368 COP1, 709 CPU-loop and 270 idle-event cases pass. Existing diagnostics,
  debug output and unsupported-opcode behavior match the reference assembly.

```sh
dotnet tools/N64Probe/bin/Release/net8.0/N64Probe.dll --check-primary-dispatch \
  .build-tmp/duke-playable-2026-09-20/reference/Ryu64.MIPS.dll
dotnet tools/N64Probe/bin/Release/net8.0/N64Probe.dll --check-multiply-batch
dotnet tools/N64Probe/bin/Release/net8.0/N64Probe.dll --bench-multiply-thread
```

The probe's game replay optionally invokes the multiply helper when present,
so the same probe binary also runs against the older baseline assembly.
The real-thread benchmark additionally verifies the history ring, which is not
part of the serialized savestate used by the game replay.

The final build repeats the game replay with the same full-state hash after
hardening the zero-valued watch-address guard. The paired measurements above
are used for the reported speedup. Linux UI Release builds with
383 warnings and zero errors; its MIPS assembly matches the tested final core:
`191e29580f00e5d746ab406579f12e00d8d22c5a2d161483c55b512f588ef85b`.
The source slot hash remains unchanged. Restart the application and load slot 1.
