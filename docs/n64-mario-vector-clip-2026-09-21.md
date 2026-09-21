# Mario 64: vector comparisons and clipping, 2026-09-21

Baseline: `2e5db6de` (`Vectorize N64 RSP math and speed up vector transfers`).
This pass targets the Linux desktop emulator.

The retained change makes Mario's repeatable gameplay scene about **5–6% faster**.
It does not reach full real time. In the longer interleaved comparison, the mean
emulated clock rate increased from **70.35% to 74.56% of real time**.
These are measurements of this scene, not a claim about every part of the game.

`RspVectorSimd.cs` now executes VABS, VLT, VEQ, VNE, VGE, VCL, VCH and VCR using
the existing SSSE3 path. All eight vector components are compared/selected
together. The interpreter and compiled RSP blocks share this implementation.
The existing scalar path remains available on other targets and through
`EUTHERDRIVE_N64_RSP_SIMD=0`.

The implementation preserves signed 16-bit wrapping, VABS destination saturation
versus accumulator wrapping, VCO/VCC/VCE state, VCH-to-VCL flag dependencies,
VCL's widened carry boundary and unused upper VCC bits. Operands are captured
before destination writes, including aliased registers. Only the low accumulator
plane changes for these instructions. Scheduling, instruction budgets, CPU code,
renderer state, frame publication and savestate formats are unchanged.

The headless probe also prints its final CPU JIT counters and process peak memory,
so future runs can distinguish a full compilation cache from unsupported code.
This telemetry runs after the measured game loop stops.

## Timing

Artifacts: `.build-tmp/sm64-next-2026-09-21/`.
`reference/` is the baseline and `final/` is the retained build. Their source
fixtures are the normalized Mario USA cartridge at
`.build-tmp/sm64-speed-2026-09-20/native-game/input.z64` and core state at
`.build-tmp/sm64-20260919/mario-input/state.bin`.

All timed processes ran serially with `taskset -c 6,7`, normal runtime settings,
and no simultaneous agent build/test. The user's existing applications continued
running. The first comparison used 40-second runs and discarded the first 10
seconds; the confirmation used 60-second runs and discarded the first 15 seconds.
Throughput is `delta(CycleCounter) / delta(wall seconds) / 93,750,000`.
Graphics-task counts are not display FPS.

| Comparison | Order | Baseline mean | New mean | Relative change |
| --- | --- | ---: | ---: | ---: |
| Initial | new / old / old / new | 0.655520 | 0.689056 | +5.12% |
| Longer confirmation | old / new / new / old | 0.703496 | 0.745581 | +5.98% |

The four longer measurements were 0.702615, 0.734110, 0.757052 and 0.704377 in
the listed order. Host load and asynchronous CPU compilation affect absolute
rates; use the interleaved comparisons instead of adding gains from separate
experiments. The final MIPS DLL is identical to the initial `clip-simd/` candidate:

```text
586b3eb180bca092eb92353dc7f88946f98f5ba8a3582183cddb14851868cd15
```

## Correctness and practical play

The final build passed:

- 359,848 instruction-by-instruction vector comparisons, including all packed
  VCE masks, independent carry/selection masks, register aliases, accumulator
  boundaries and mixed SIMD/scalar sequences.
- 18,560 ordinary RSP instruction/transfer cases, 2,048 vector-copy cases and
  32,768 shuffle cases. Default and legacy half-shuffle modes match their
  respective references. Explicit SIMD-off and runtime SSSE3-off checks pass.
- 96 compiled RSP programs, now mixing clipping with accumulator/reciprocal
  instructions. Full serialized state, history, watchdogs and completion agree.
- 32 RSP slice-boundary cases with save/restore, producer/consumer ordering,
  DP synchronization and framebuffer publication.
- 6,847 renderer/interrupt cases, 1,092 alpha cases, 960 depth cases, 24 blender
  cases, 81 video cases and audio FIFO/snapshot/state checks with 24 resamplers.

Five fixed replays match the baseline's complete serialized CPU/RSP/RAM/device
state. Each executes 50,000,000 instructions except Perfect Dark, which ends at
50,000,001 because its final instruction includes a delay slot.

| Game | SHA-256 |
| --- | --- |
| Mario 64 | `60E842A0596E319755B512E43BD696A42D5270ACC53CC02AFCCE24AE9E6C2BFE` |
| Rampage 2 | `E7A9A309A25DEC3B90942C81E9D69063496800BA176884AF577C23F426D62291` |
| Duke Nukem 64 | `D0C0B5A733A6F64381130CB17BBDD856F830CB78092F95F2062E6A2496CDC770` |
| Gauntlet Legends | `BCB8E54F9CEFE529489300D70E909084C59ED30E741F45A6FA23D37D916B311A` |
| Perfect Dark | `3B9E8C5B821B26AD769B264B8A4687A9D6036D4029C91BC401EF8E4AAF5B993E` |

The final moving run used periodic A presses and forward input, captured audio,
and saved only into its own artifact directory. It completed 121.46 wall seconds,
visited 103 distinct sampled positions, and advanced graphics tasks from 1,940
to 6,394 and audio tasks from 4,113 to 7,680. It produced 60.022 seconds of PCM
(about 49.4% of real time, including startup and changing views). Reloading that
new state completed another 30.45 wall seconds, visited 19 positions, advanced
both task counters and produced 9.613 seconds of PCM (31.6%). Neither run logged
an unknown CPU opcode or compilation failure.

Frames at 60, 90 and 120 seconds and after reload were visually inspected: Mario
swims in the moat, reaches the grass beside the castle, and the camera later
approaches foliage. These runs verify movement, continuing graphics/audio and
reload behavior. Inputs are scheduled by wall time, so their changing paths are
not paired performance comparisons. They also make the remaining limitation
clear: the 75% steady-scene result does not imply full-speed active play.

The Linux UI Release build completed with 0 errors and 383 existing warnings.
Its `Ryu64.MIPS.dll` matches the tested final DLL hash above. The existing user
application was not stopped, and the user's save slots were not replaced.

The separate native sampling run (`final-native/native-summary.json`, 29,956
samples) still puts textured triangles at about 15%, quiet CPU execution at 11%,
SIMD vector execution at 9%, the two common texture filters together at 9% and
RSP slice execution at 5%. These sampled shares identify remaining work; they
are not an additional timing benchmark. Larger gains still require reducing
work across the CPU/RSP execution and software rasterization paths.

## Experiments not retained

Each experiment used its own copied build. The figures below are screening
measurements from separate interleaved sets, not universal regression estimates.
They did not establish an additional gameplay speedup, so they are absent from
the final code.

| Candidate | Compared with | Observed change |
| --- | --- | ---: |
| Retry CPU JIT after an interpreted branch | baseline | -1.43% |
| Early depth rejection for valid RGBA16 samplers | baseline | -1.33% |
| SIMD three-point texture filtering | baseline | -6.31% |
| Increase CPU compiled-version limit from 128 to 2048 | baseline | -2.16% |
| Compile HI/LO moves and CACHE | clipping SIMD | -2.20% |
| SIMD accumulator/reciprocal operand transfers | clipping SIMD | -0.45% |
| Retain RSP cache entries after short-budget rejection | clipping SIMD | -2.53% |

The native baseline profile contained exactly 128 compiled CPU methods. Raising
the cap compiled 1,365 in one run, but did not improve Mario throughput. Merely
increasing the limit is therefore not a demonstrated route to real time.

Early depth rejection passed 4,096 differential triangle cases and the complete
2,604-chunk Mario RDP tape with matching serialized state. SIMD texture filtering
passed 1,048,576 filter cases. The RSP cache experiment passed explicit budget,
watchdog, unchanged-state and modified-IMEM checks. Correctness alone did not
justify retaining their performance changes. The HI/LO candidate was screened
before running the full CPU differential suite and must be validated if revived.

Source snapshots/patches and per-run JSON/logs remain in the artifact directory.
An RSP history-copy variant was also prepared there but was not benchmarked;
do not treat it as a measured failure.
