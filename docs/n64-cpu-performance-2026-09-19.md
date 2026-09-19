# N64 CPU optimization checkpoint — 2026-09-19

Continued in [the second checkpoint](n64-cpu-performance-step2-2026-09-19.md).
The saved binaries below remain the reference for that next round.

Continued the interrupted R4300 optimization from commit `df914ef7` plus the
uncommitted trace extraction. Other emulator work in the checkout was left alone.

## Changes

- Kept the 23 instruction-window trace blocks outside `StartCpuThread`, behind
  one startup-initialized flag. A source comparison verified that their bodies
  were moved without changes other than indentation. This change alone did not
  produce a clear improvement in the Mario scene measurements.
- Added `TryFastForwardPollingLoops`: inspect a RAM instruction once to reject
  impossible candidates instead of calling three separate polling-loop checks.
  Matching candidates still use the original full live-code validation, register
  updates and cycle accounting. Device-space reads keep their original ordering.
  The special idle-loop entry at `0x80000814` remains supported.
- Extended the full-state differential checks with polling patterns, mutated
  instructions, signed load comparisons, direct-mapped aliases, memory boundaries
  and the special delay-slot entry.
- Added `N64Probe --bench-cpu-thread`, which executes a fixed instruction stream
  through the actual CPU thread. A probe-local replacement SYNC handler stops at
  an exact instruction boundary. Each repetition checks the instruction count,
  final registers and SHA-256 of the complete serialized CPU/memory state.
  Production execution has no benchmark hook.

## Validation and measurements

Both the N64 probe and Linux UI Release builds passed. The UI build reported 500
warnings and zero errors across the repository's projects.

- 709 full-state CPU-loop differential cases passed, including 176 accepted loops.
- 852,358 opcode lookup cases passed against the full ordered scan.
- Audio, DMA (108 cases), and framebuffer snapshot checks passed. The snapshot
  check requires `EUTHERDRIVE_N64_PERF=1`; its initial run without that flag was a
  test invocation error, corrected before accepting the result.
- All 256 enabled PC-window trace records matched the reference byte for byte
  when resuming the same snapshot.
- The final fixed-work runs produced identical complete-state SHA-256 values:
  `41CBC873ED5B0FE3C41D49EBDC5166F7D6197F0A35B5EDA74670ABBCD86B0617`.

Fixed CPU-thread work, pinned to logical CPU 6, baseline/final/final/baseline order:

| Variant | Process 1 median | Process 2 median | Mean of medians |
| --- | ---: | ---: | ---: |
| Reference at `df914ef7` | 949.575 ms | 941.592 ms | 945.584 ms |
| Final trace extraction + polling prefilter | 834.738 ms | 819.162 ms | 826.950 ms |

This is 12.5% less elapsed time for the fixed CPU workload (about 14.3% greater
throughput). Each process performs six measured repetitions after three warm-ups.

The final 30-second Mario scene confirmation, final/reference order, also pinned
to CPU 6, gave 39,380,604 versus 36,759,704 emulated cycles/second: **7.1% higher
throughput**. Earlier four scene runs with the initial polling candidate gave
about 11%, so do not treat that earlier figure as a precise final-version claim.
Wall-time gameplay measurements vary with workload and JIT behavior. All six
scene runs completed without unknown opcodes or CPU halt exceptions.

See `.build-tmp/n64-cpu-2026-09-19/` for the durable local artifacts: snapshot,
reference/candidate probe builds, build/test/measurement logs, final scene images,
and a SHA-256 manifest. These artifacts are untracked; the ROM is not copied into
the repository. The UI's Ryu64.MIPS DLL was verified byte-identical to the final
tested candidate DLL.

Performance
figures are Linux x64 measurements on a Xeon E5-2697 v3; Android performance and
other games have not been measured. Scene throughput means emulated CPU cycles
per wall second, not display FPS. Runs start from the same Mario snapshot, with
no input, and exclude the first five seconds from the reported rate.

## Reproduction and continuation

Build and run the focused checks:

```sh
dotnet build tools/N64Probe/N64Probe.csproj -c Release --no-restore
dotnet tools/N64Probe/bin/Release/net8.0/N64Probe.dll --check-cpu-loops
dotnet tools/N64Probe/bin/Release/net8.0/N64Probe.dll --bench-cpu-thread
dotnet build EutherDrive.UI/EutherDrive.UI.csproj -c Release --no-restore
```

Replay the comparison from the repository root, running each command separately:

```sh
taskset -c 6 dotnet .build-tmp/n64-cpu-2026-09-19/reference/N64Probe.dll --bench-cpu-thread
taskset -c 6 dotnet .build-tmp/n64-cpu-2026-09-19/candidate/N64Probe.dll --bench-cpu-thread
taskset -c 6 dotnet .build-tmp/n64-cpu-2026-09-19/candidate/N64Probe.dll "/home/nichlas/roms/N64/Super_Mario_64_(USA)-.n64" /tmp/n64-scene-replay-candidate 30 .build-tmp/n64-cpu-2026-09-19/scene-state.bin
taskset -c 6 dotnet .build-tmp/n64-cpu-2026-09-19/reference/N64Probe.dll "/home/nichlas/roms/N64/Super_Mario_64_(USA)-.n64" /tmp/n64-scene-replay-reference 30 .build-tmp/n64-cpu-2026-09-19/scene-state.bin
```

Clear any inherited `EUTHERDRIVE_N64_*`, `EUTHERDRIVE_TRACE_N64_*` and `N64_PROBE_*`
overrides first. The measurements above use default emulation settings with
tracing/performance instrumentation disabled. Use the difference between the
last and fifth `cycles` samples divided by their `seconds` difference for scene
throughput; output frame files from equal wall times are not equal emulated frames.

The fixed-work benchmark uses 1,000,000 loop iterations, 10,000,001 instructions,
three warm-up repetitions and six measured repetitions. Early exploratory runs
with only 2,000,001 instructions were too sensitive to JIT warm-up and should not
be used as final performance evidence. Compare separate processes against the
saved reference DLLs, and keep other builds/profilers stopped while measuring.

The source ROM is `/home/nichlas/roms/N64/Super_Mario_64_(USA)-.n64`.
Its normalized big-endian SHA-256 is
`17ce077343c6133f8c9f2d6d6d9a4ab62c8cd2aa57c40aea1f490b4c8bb21d91`.
The scene snapshot SHA-256 is
`22ab826d84b0f816ab69747cb73b45fa6519235e56f4a18a3cd24e1b9c8fa479`.

The earlier profile, summarized by `tools/N64Probe/summarize-profile.py`, placed
17.41% of sampled CPU-thread time in textured-triangle drawing, 16.27% in
`InterpretOpcode`, and 12.53% in the CPU-loop caller. These are pre-change samples;
inlined work is attributed to the visible caller. Capture a fresh profile before
choosing the next optimization. Do not infer current bottlenecks from those old
percentages alone.
