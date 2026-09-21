# Mario 64: specialized triangle pipelines, 2026-09-21

Baseline: `0c50c2a0`, with the emulator runtime from `f7b05ced`.
The final implementation retains all three common pipelines. The final variant
measured **3.65% higher whole-emulator throughput** in the compared Mario scene,
from 0.844701 to 0.875571 of real time. A separate direct comparison favored it
by 2.01% over the initial two-path variant. These are separate comparisons, not
percentages to add together. Mario is still below real time in this scene.

Target: the Linux desktop emulator. Artifacts, copied builds and scripts are in
`.build-tmp/sm64-pixels-2026-09-21/`.

## Change

The renderer selects common triangle pipelines before entering the pixel loop.
It still uses one source implementation of the scanline walker. Empty value-type
markers specialize sampling, constant-shade combining and depth enablement so the
host JIT can remove mode decisions for each pixel. Other states use the original
general pipeline through the same walker.

The initial two paths use filtered RGBA16 without a palette, constant shade,
one-cycle texture-times-shade RGB with shade alpha, no alpha rejection and no
forced blending. The first wraps texture coordinates and enables depth processing;
the second clamps coordinates and has depth processing disabled. All depth modes,
coverage destinations and perspective-coordinate arithmetic remain unchanged.
The third path uses wrapped, filtered RGBA16 with constant RGBA modulation,
depth processing and forced blending.

No guest clock changes, frame skipping or approximate color/coordinate arithmetic
are used. Depth reads/writes remain after alpha rejection, in the original pixel
order. Dirty ranges, pixel counters, framebuffer completion and state serialization
use the existing code. The new mode selection is recomputed per triangle and needs
no persistent cache or invalidation on state loads.

A temporary 30-second diagnostic counted these textured-pixel workloads:

| Mode | Pixels |
| --- | ---: |
| Wrapped constant-shade RGB modulation, depth | 45,334,744 |
| Clamped constant-shade RGB modulation, no depth | 39,060,622 |
| Wrapped constant-shade RGBA modulation, depth and forced blend | 37,501,133 |

Together the three modes accounted for about 96% of sampled textured pixels.
Diagnostic counters are absent from the candidate and production builds.

## Measurement method

Timed processes run serially on CPUs 6 and 7 with normal runtime settings and no
concurrent builds/tests. Existing user applications keep running. Whole-emulator
comparisons start from the same copied Mario gameplay state and cartridge:

```text
.build-tmp/sm64-20260919/mario-input/state.bin
.build-tmp/sm64-speed-2026-09-20/native-game/input.z64
```

Each process runs for 90 seconds. The comparison uses exactly guest cycles
8,500,000,000 through 12,000,000,000, with crossing times interpolated between
one-second samples. This interval was inherited from the preceding experiment
before these measurements. Real-time ratio is guest cycles per wall second divided
by 93,750,000. This is a specific scene's emulated clock throughput, not displayed
FPS or a claim that every Mario gameplay situation runs at that speed.

The fixed-work renderer benchmark replays the complete 2,604-chunk Mario RDP tape,
with 60 warmup and 40 measured iterations. Every iteration hashes the full saved
Memory state. Speed comparisons use separate ordinary/default assembly contexts;
reference assembly loading within a process is used only for correctness.

## Correctness checks

`--check-triangle-modes REFERENCE_DLL` compares the affected color/depth RAM regions
and hidden coverage bits after each draw against the prior implementation, followed by a hash of
all serialized memory/device state. Its 9,216 cases include broad render modes,
deliberate fast-path cases and single-condition variations around dispatch guards.
They cover perspective and affine coordinates, constant/varying shade, negative
coordinates, fractional triangle origins, wrapping/clamping/mirroring, TMEM row
swaps, alpha rejection, blending, all depth modes, coverage destinations, out-of-range
depth addresses, overlapping color/depth buffers, and empty/clipped draws.

Full state for the complete Mario tape:

```text
55D4D4A3CFB2DF008D36133DF96DD6E3F99C9E88A23BA57B60A8AF2649F19E7F
```

Final triangle differential state:

```text
9701598FACF515F5179325743C146986F8AFCCA17DFBAE5410AF7382497C3DC1
```

Additional checks passed: 6,847 renderer/interrupt cases; 1,092 sprite-alpha,
960 sprite-depth and 24 blender cases; 81 video cases; 197 RDP streaming cases;
65,536 RGBA5551 conversions and 1,048,576 three-point filter cases; 106,496 sampler,
432 flat-shade and 4,194,304 depth differential cases. The complete 1,980-chunk
Rampage RDP replay also matched the reference in every full-state hash:

```text
642B77DCC4D161964CC567FA8FBA1C33424CB4D2186ECE8701DC0FF8B5DD4D0C
```

## Whole-emulator results

The first comparison is A/B/B/A; the next is B/A/A/B; the third compares the two
candidates directly in A/B/B/A order. Per-run measurements and logs are preserved.

| Comparison | First build mean | Second build mean | Change |
| --- | ---: | ---: | ---: |
| reference to specialized | 0.833742 | 0.870267 | +4.38% |
| reference to blended | 0.844701 | 0.875571 | +3.65% |
| specialized to blended | 0.862687 | 0.880029 | +2.01% |

`reference` is the old implementation, `specialized` is the first two-path
candidate, and `blended`/`final` is the retained three-path variant. Four separate
90-second processes contributed to each row. The host also ran the user's existing
applications, so the variation between runs limits precision.

## Fixed-work graphics results

| Comparison / order | Median times in run order, ms | Mean time reduction |
| --- | --- | ---: |
| reference-specialized-specialized-reference | 16.476, 15.416, 15.056, 16.522 | 7.66% |
| specialized-blended-blended-specialized | 15.533, 15.29, 15.101, 15.345 | 1.58% |

These component comparisons must not be substituted for the whole-emulator results.

## Five-game fixed instruction replays

Every game was replayed from the same saved CPU/device/RAM state in both reference
and final builds. Each pair matched the complete serialized state. Perfect Dark
finishes the delay slot crossing the limit, hence its extra instruction.

| Game | Instructions | Full-state SHA-256 |
| --- | ---: | --- |
| rampage | 50,000,000 | `E7A9A309A25DEC3B90942C81E9D69063496800BA176884AF577C23F426D62291` |
| duke | 50,000,000 | `D0C0B5A733A6F64381130CB17BBDD856F830CB78092F95F2062E6A2496CDC770` |
| mario | 50,000,000 | `60E842A0596E319755B512E43BD696A42D5270ACC53CC02AFCCE24AE9E6C2BFE` |
| gauntlet | 50,000,000 | `BCB8E54F9CEFE529489300D70E909084C59ED30E741F45A6FA23D37D916B311A` |
| perfect-dark | 50,000,001 | `3B9E8C5B821B26AD769B264B8A4687A9D6036D4029C91BC401EF8E4AAF5B993E` |

## Reproducing

Build N64Probe, then use the original baseline MIPS DLL retained under `reference/`:

```sh
dotnet build tools/N64Probe/N64Probe.csproj -c Release --no-restore -m:1
dotnet tools/N64Probe/bin/Release/net8.0/N64Probe.dll --check-triangle-modes \
  .build-tmp/sm64-pixels-2026-09-21/reference/Ryu64.MIPS.dll
```

`compare-steady-scenes.py`, `compare-cycle-window.py`, `compare-tape.py`,
`validate.py` and `compare-replays.py` preserve the comparison procedures locally.
Run timing scripts serially, using fresh series/build labels to preserve prior logs.
The temporary mode-counter source is archived as `Memory.cs.profile`; it is not
part of the production change.

## Native-code inspection

A separate 35-second diagnostic collected 29,957 instruction-pointer samples and
JIT disassembly. The optimized generic instantiations contain no runtime type
helper calls. The common pipelines no longer call the generic color-combiner
entry point. Their native sizes were 6,857 bytes (wrapped/depth), 5,288 bytes
(clamped/no depth) and 6,088 bytes (wrapped/blend), compared with 9,672 bytes for
the general instantiation. Disassembly and the extraction summary are retained
in `final-native/`. Sampling/disassembly runs are not timing comparisons.

## Moving game, save/load and desktop build

The final build ran Mario with the existing automated jump/forward input and audio
capture, then loaded the newly captured state and continued. User save slots were
not modified. All 24 captured gameplay frames and all 6 reload frames had distinct
hashes. Position/action telemetry changed and neither run reported an unknown CPU
instruction or compilation failure. Captured frames were also inspected visually.

| Run | Wall time | Captured PCM duration | Unique positions | Graphics-task counter |
| --- | ---: | ---: | ---: | --- |
| moving | 121.63 s | 65.360 s | 103 | 1940 to 6756 |
| moving-reload | 30.50 s | 15.483 s | 17 | 6758 to 7685 |

Input changes in this acceptance script are scheduled by wall time, so different
builds can follow different trajectories. This run proves continued game/input,
frame, audio and save/load operation; it is not a paired moving-game speedup claim.
Its approximately 54% audio-duration/wall-time ratio also shows why the 88% steady
scene result must not be described as full-game real-time performance.

N64Probe Release built with 0 errors and 12 warnings. The complete desktop UI
Release build used the isolated `ui/` output and finished with 0 errors and 504
warnings. Its MIPS DLL matched the tested final/probe DLL byte for byte. The MIPS
DLL and PDB were installed atomically into `EutherDrive.UI/bin/Release/net8.0/`,
with prior files preserved under `ui-previous/`. The running UI process was left
running; restart the application to load the updated renderer.

Tested and installed `Ryu64.MIPS.dll` SHA-256:

```text
4c9901686976b7b19838ef331c7eedf3ff6620a9dac34b72343445c850f4e337
```
