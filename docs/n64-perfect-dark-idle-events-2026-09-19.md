# Perfect Dark event-bounded idle execution — 2026-09-19

Baseline `2ce379b8`. Artifacts: `.build-tmp/perfect-dark-events-2026-09-19/`.

## Change

For mapped BEQ-self/NOP loops, execute whole pairs in one step only inside an
interval with no device event, VI line transition, Count wrap or Compare match.
Validate live TLB mappings and RAM opcodes on each entry. Preserve Count,
RANDOM/WIRED, instruction totals and recent-PC history. Fall back for pending
interrupts, tracing/debug, translation misses and non-NOP delay slots. Existing
direct-mapped loop handling is unchanged. There is no cached translation.

The memory interval covers SP DMA, RSP task completion, SP/DP interrupts,
PI/SI/AI completion and VI timing. Event boundaries run through the interpreter.

## Validation and timings

- 269 boundary/full-state differential cases pass, including 140 accepted batches.
- Two million fetched instructions with interrupt delivery yield identical
  state with batching on/off: SHA-256
  `80107609441AF0B6DFE8C9089CDB9E73428CDC9D2C9EBCDEFE8CCC3B621548C8`.
- With 20 warm-ups and eight measurements pinned to CPU 6: median 111.534 ms
  without batching, 16.468 ms with batching (85.2% less time, 6.77x throughput).
  1,915,070 of two million instructions were batched. This is an idle-heavy
  CPU replay, not gameplay FPS, and excludes the main loop's history recording.
- Real mixed CPU thread benchmark: reference 773.321/767.351 ms, candidate
  775.492/756.501 ms; no observed regression. Full-state digest remains
  `41CBC873ED5B0FE3C41D49EBDC5166F7D6197F0A35B5EDA74670ABBCD86B0617`.
- Disabling fast idle or enabling branch, dispatch or hot-PC tracing disables
  batching; all four dedicated checks pass. Debug rejection is also covered.
- Linux UI Release builds successfully (384 warnings, zero errors).

A separate synchronous logger probe captures the *same* failing instruction
from both binaries at PC 0x70008878, after running from the user's slot 1.
The entire CPU/memory snapshot AND recent-PC/register history are identical.
Snapshot SHA-256: `043571db857d284e68f1441b806fb1ceb43c2bbf379ad9e1725acdfca56fa052`.
Time to that point: 8.58 s reference, 3.94 s candidate (one exploratory pair).
This confirms the intro issue predates batching. Artifacts `fault-probe/`,
`fault-reference/` and `fault-candidate/` preserve the reproduction.

## Remaining intro issue

A 60-second run still stops submitting graphics at task 228, while audio
continues. The failed LBU reads from t8=0x47008473. Its base t3=0x47000000 came
from MFC1 f16 at 0x70008610; the COP0 status is 0x0400ff01 (CU1 clear).
There is no coprocessor-unusable check in the existing interpreter. Investigate
missing lazy FPU context switching before further graphics optimization.

## Reproduce

```sh
dotnet tools/N64Probe/bin/Release/net8.0/N64Probe.dll --check-idle-events .build-tmp/perfect-dark-vi-2026-09-19/cpu-state.bin
taskset -c 6 dotnet tools/N64Probe/bin/Release/net8.0/N64Probe.dll --bench-idle-events .build-tmp/perfect-dark-vi-2026-09-19/cpu-state.bin
```

Use `N64_PROBE_EXPECT_IDLE_DISABLED=1` with one of the trace/disable flags to
validate rejection. Benchmarks must run without concurrent emulation/builds.
