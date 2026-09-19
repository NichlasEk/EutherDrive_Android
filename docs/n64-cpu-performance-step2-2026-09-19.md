# N64 CPU optimization, second checkpoint — 2026-09-19

Continued in [the rendering checkpoint](n64-render-performance-2026-09-19.md).

Continues [the first CPU checkpoint](n64-cpu-performance-2026-09-19.md). The
reference for this round is that checkpoint's saved `candidate` build, **not**
`df914ef7`. Percentages in this document therefore describe additional gains.

## Changes and invariants

`InterpretOpcode` now calls a separate diagnostic helper only when dispatch
tracing or the mutable debug flag is enabled. The instruction handler, cycle
accounting, memory ticks, interrupts and register updates retain their original
order. Diagnostic formatting and trace limits are unchanged.

`TryFastForwardRuntimeLoops` shares the first live RAM instruction read between
the memory-loop and polling-loop filters. Full pattern validation still runs
before accepting a shortcut. Rejected memory candidates re-read before polling,
because byte-zero validation can read an indirect device address. Instruction
reads outside RAM retain the previous sequence. Nothing is cached by PC, and
self-modifying code still gets validated against current memory.

The CPU-loop differential checks now compare the combined filter with the
original ungated memory and polling recognizers. `--check-cpu-diagnostics`
compares debug on/off, dispatch-window boundaries, unsupported instructions,
output text and complete saved state against a reference assembly.

A separate trial moving opcode-error string formatting into a cold helper had
no measurable benefit and was reverted. `OpcodeTable.cs` is unchanged.

## Measurement setup

A fresh 20-second profile of the first checkpoint had 21.70% of sampled CPU-thread
time in textured-triangle drawing, 15.70% in `InterpretOpcode`, and 8.34% in its
CPU-loop caller. These are sampled leaf frames: inlined work is charged to the
visible caller. They guide experiments; they are not hardware cycle accounting.

Benchmarks use the same Linux x64 Xeon E5-2697 v3, logical CPU 6, ROM and snapshot
as the first checkpoint. The fixed CPU benchmark runs 10,000,001 instructions,
with three warm-ups and six measured repetitions per process. Scene runs last
30 seconds and exclude the first five seconds when calculating emulated cycles
per wall second. Default emulation settings are used without tracing, automatic
input, or performance instrumentation. Our builds and checks completed before
timing, but external work later overlapped the scene measurements (see below).
Android performance has not been measured.

## Reproduction

The final probe and Linux UI Release builds pass (UI: 383 warnings, zero errors).
The tested probe and UI core DLLs are byte-identical. The combined-loop check
passes all 709 full-state cases, including 176 accepted shortcuts. The 852,358
opcode cases also pass. Diagnostic comparison passes with full-state SHA-256
`9A89187EDC3675BBC8915FD5D54A0C848A28ED75DA336030C0DF06E68B080038`.

The final fixed CPU runs, before/after/after/before order, give:

| Build | First process median | Second process median | Mean of medians |
| --- | ---: | ---: | ---: |
| First checkpoint | 854.348 ms | 788.484 ms | 821.416 ms |
| Second checkpoint | 768.956 ms | 806.569 ms | 787.763 ms |

This is **4.1% less CPU-test time** on average versus the first checkpoint. There
is visible run-to-run variation; the individual changes' exploratory percentages
must not be added together. Every repetition has exactly the same full-state
SHA-256 as the first checkpoint:
`41CBC873ED5B0FE3C41D49EBDC5166F7D6197F0A35B5EDA74670ABBCD86B0617`.

All four Mario scene runs completed with no unknown opcodes or CPU halts, but
their throughput figures are **not accepted as performance evidence**. In
before/after/after/before order they were 39.866, 39.540, 32.898 and 37.899 million
cycles/second. A new Roslyn compiler started at 18:01:20 local time, during the
second candidate run, and a live EutherDrive.UI process started at 18:02:09,
during the final reference run. These unrelated processes were not stopped.
The first uncontested scene pair alone is effectively flat (-0.8%), so this
checkpoint claims a fixed CPU-work improvement, not a measured gameplay gain.
The CPU benchmark logs completed between 17:59:46 and 18:00:13, before that work.

Durable artifacts are in `.build-tmp/n64-cpu-step2-2026-09-19/`: final candidate
probe binaries, reference/candidate R4300 source, checks, exploratory and final
timing logs, the fresh profile and its summary, and a SHA-256 manifest. This
directory is untracked. The original checkpoint's reference, candidate and
snapshot remain intact. Resume by re-running the scene comparison during a quiet
period; do not average the contaminated scene logs into a claimed speedup.

The next substantial target indicated by the profile is textured-triangle
drawing. Use exact RDP command replay and full-state comparisons before accepting
renderer changes; no rendering or accuracy settings were changed in this round.

### Commands

```sh
dotnet build tools/N64Probe/N64Probe.csproj -c Release --no-restore
dotnet tools/N64Probe/bin/Release/net8.0/N64Probe.dll --check-cpu-loops
EUTHERDRIVE_TRACE_N64_SM64_DISPATCH_WINDOW=1 dotnet tools/N64Probe/bin/Release/net8.0/N64Probe.dll --check-cpu-diagnostics .build-tmp/n64-cpu-2026-09-19/candidate/Ryu64.MIPS.dll
taskset -c 6 dotnet .build-tmp/n64-cpu-2026-09-19/candidate/N64Probe.dll --bench-cpu-thread
taskset -c 6 dotnet tools/N64Probe/bin/Release/net8.0/N64Probe.dll --bench-cpu-thread
```

Run the reference and candidate in separate processes, alternating their order.
Keep the first checkpoint's saved build intact. The source ROM remains
`/home/nichlas/roms/N64/Super_Mario_64_(USA)-.n64`, with the normalized SHA-256 and
snapshot identity recorded in the first checkpoint. Equal wall-time frame
captures are not equal emulated frames and must not be used for pixel equality.
