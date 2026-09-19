# Darius Gaiden: HUD correction and stutter investigation

Linux follow-up to `f11a7747`, 2026-09-19. The user confirmed smoother audio
and improved speed, but reported incorrect HUD colors and occasional stutter.
New slots 1 and 2 capture gameplay around the Zone A boss.

## Accepted change

`DecodeF3CharPixel` and `DecodeF3PivotPixel` used plane weights `{1,2,4,8}`
for layout planes `{0,1,2,3}`. These must be `{8,4,2,1}`. The old decoder
therefore selected bit-reversed palette pens, affecting the HUD and pivot
graphics. Each pixel is an aligned nibble, so one byte read and nibble
extraction both correct the order and replace four per-plane reads.
Byte-lane mapping, layout offsets, palette attributes and transparent pen 0
are unchanged.

Local reference evidence:

- `/home/nichlas/mame/src/mame/taito/taito_f3.cpp`: character and pivot layouts,
  x offsets `{20,16,28,24,4,0,12,8}`, four consecutive planes.
- `/home/nichlas/mame/src/emu/drawgfx.cpp`: decoding starts with
  `planebit = 1 << (planes - 1)` and shifts down for each plane.
- `/home/nichlas/mame/src/mame/taito/taito_f3_v.cpp`: existing text palette
  attribute extraction agrees; no palette-attribute change is needed.

`DariusProbe --check-gfx-planes` compares both actual private decoders with an
independent bit-by-bit reference for every byte value, every pixel coordinate,
first/second/last tiles and invalid tile bounds: **98,308 checks passed**.
The corrected slot-2 frame-10 image was visually inspected. This verifies the
specific plane-order bug, not all rendering behavior against original hardware.

## Replay safety and correctness

The original `/home/nichlas/roms/MAME/TAITO/dariusg_7fbb2f16.euthstate` was
only read. Tests load a copied snapshot under `.build-tmp/darius-user-snapshot`.
Original file SHA-256:

```text
3213841b754a6e4005dca61de9b358378bef4546eb3f1f157c59ca2f4b21d37f
```

The probe now accepts a state directory and slot, holds fire during snapshot
replay, and reports p95/p99/max plus counts over 25/33 ms. Example:

```sh
EUTHERDRIVE_DARIUSG_ADAPTIVE_RENDER=0 dotnet tools/DariusProbe/bin/Release/net8.0/DariusProbe.dll /home/nichlas/roms/MAME/TAITO/dariusg.zip 360 .build-tmp/darius-user-snapshot 2
```

For 360 frames per slot, old and corrected decoders produced identical audio
streams and final serialized machine states. Video intentionally changed.
These are the corrected replay hashes (uppercase hexadecimal SHA-256):

| Slot | Stream | Hash |
| --- | --- | --- |
| 1 | Video | `4765BE0FAF5EB778478A3437002A2923A8BBBE04EB36EB15AF232A1336B73D96` |
| 1 | Audio | `252AE5F3002E2AA6AFFF830BDE8F5B4A197028D99DD173DC3F66DD53775CA26D` |
| 1 | State | `F1F1D6A12A28F2E719152B6145DF1158B94E4CEF2C7DA0084E9DAD5D3077E3CD` |
| 2 | Video | `E1AFEB3890488E80124497A0BAEB46AC9BA7AC49CE92BCE46578E9DFED1C0AB1` |
| 2 | Audio | `5FBA69271EA58D5B241BD442B4DD9443C7BFA1E28B4B12DF329CE44AE0097071` |
| 2 | State | `C26EFE88424202975721A90894146DF16ADE361C7067CC722EF105FB40F94AB6` |

Single old/new decoder timing pairs totaled 5112.852/4927.811 ms for slot 1
and 5928.805/5421.343 ms for slot 2. These are exploratory measurements,
not a controlled multi-pair speedup claim.

## Rejected JIT experiment and remaining stutter

Phase tracing of no-input headless replay showed occasional 19–30 ms
`BuildMameLineStates` phases without a GC event. JIT disassembly confirmed
instrumented Tier0 followed by multiple large Tier1-OSR compilations.
Adding `AggressiveOptimization` produced a single optimized version and
removed those large line-state phases in a diagnostic replay. However,
balanced probe runs did **not** establish an end-to-end latency improvement.

Both variants below already include the HUD correction. Normal .NET tiering,
adaptive rendering disabled, fresh process for each 360-frame replay; no
concurrent builds/profilers. Order baseline/candidate/candidate/baseline.

| Slot | Variant | Total ms | p99 ms | Max ms | Frames >25 ms |
| --- | --- | ---: | ---: | ---: | ---: |
| 1 | Baseline | 5033.825 | 16.997 | 17.422 | 0 |
| 1 | Candidate | 4987.834 | 16.374 | 18.108 | 0 |
| 1 | Candidate | 5124.606 | 24.193 | 32.679 | 4 |
| 1 | Baseline | 5207.301 | 20.066 | 24.991 | 0 |
| 2 | Baseline | 5433.779 | 17.081 | 17.802 | 0 |
| 2 | Candidate | 5820.078 | 28.005 | 28.586 | 12 |
| 2 | Candidate | 5383.777 | 17.328 | 19.208 | 0 |
| 2 | Baseline | 5560.458 | 17.665 | 25.400 | 1 |

All three hashes matched within each slot across all four runs. The candidate
attribute was removed: a nicer isolated phase is insufficient evidence to
ship it. No global tiering setting was changed.

Headless no-input replay still shows reload/initial warmup spikes and occasional
later spikes. Probe replay holds fire and does not run the UI/audio presentation
loop; its timings cannot certify interactive pacing. Next work should separate
UI presentation/audio pacing, first-use JIT and steady-state emulation costs,
using these preserved slots. No claim that all stutter is fixed.

Local diagnostic artifacts (not committed): `.build-tmp/darius-hud-compare.log`,
`.build-tmp/darius-line-ab.log`, `.build-tmp/darius-line-original.asm`,
`.build-tmp/darius-line-opt.asm`, and `.build-tmp/darius-hud-slot2-view/`.
