# N64 rasterization checkpoint — 2026-09-19

Continues [the RSP checkpoint](n64-rsp-performance-2026-09-19.md), baseline
6b7f3686. This slice targets the textured-triangle cost in the warmed Mario
profile. It preserves sampling, interpolation, pixel counters and output.

## Changes

- Share the textured pixel loop's attribute advancement between successful
  writes, texture misses, alpha rejection and depth rejection. Each attribute
  still advances once per pixel, with the original depth/shading conditions.
- Recognize texture times shade RGB with shade alpha, texture times shade RGBA,
  and texture times environment RGBA when the combiner is configured. Single
  cycle rendering calls the exact multiplication directly instead of sending
  fourteen arguments through the generic combiner. Two-cycle rendering still
  evaluates both cycles. Copy mode still bypasses the combiner. Savestate load
  rebuilds the derived classification. Rounding remains +128 then /256.
- Depth comparison now computes only values used by its selected mode. Disabled
  comparison avoids reading/decompressing old depth. Cleared depth and mode 2
  avoid DZ calculation. Modes 0/1 need only the nearer comparison, while mode 3
  also computes farther. Bounds checks and depth writes keep their behavior.
- Add a reference-differential depth test and increase renderer benchmark
  warm-up from 20 to 60 replays, followed by 40 measured replays.

## Accepted results

Linux x64, logical CPU 6, separate processes in reference/candidate/candidate/
reference order. Both sides use the same final harness. State restoration and
hashing are outside the timer; complete output state is checked every replay.

| Work | Reference medians | Candidate medians | Mean time reduction |
| --- | --- | --- | --- |
| Tree frame, 2,604 chunks | 20.047 / 20.213 ms | 16.642 / 17.074 ms | 16.25% |
| Logo frame, 908 chunks | 7.574 / 7.584 ms | 7.062 / 7.105 ms | 6.54% |
| Complete graphics task, 794,278 instructions | 54.665 / 56.559 ms | 51.285 / 49.844 ms | 9.08% |

The complete graphics task includes RSP execution and rendering, using the
existing 20-warm-up/20-measurement task harness. Compilation count (195) and
executed instruction count also match. It is a broader fixed-work measure,
not whole-game FPS. Android and playable speed have not been verified.

Earlier exploratory renderer trials with 20 warm-ups were unstable, especially
for the logo: its later JIT code can run near 7 ms instead of 20 ms. The table
above supersedes those short-warm-up timings; the JIT transition is not a gain
from this patch. Individual timings still vary on this active workstation.

## Correctness

- 4,194,304 reference-differential depth calls pass: every encoded 16-bit depth,
  all four hidden-bit values, all depth modes and compare/update combinations,
  clipping/wrapping inputs, DZ values and accesses at/beyond the final pixel.
  Returned pass/fail, written pixels, hidden bits and final RAM match.
- 156,352 color-combiner cases match, including cycle modes, randomized colors,
  neighboring mux forms and loading a saved mode over a different live mode.
- 6,845 renderer/interrupt checks and 432 flat/gradient cases pass.
- Both replay framebuffer files and entire RDRAM match reference byte for byte.

The old capture's immediate framebuffer differs from the standalone replay for
both the unchanged reference and candidate (capturedFramebufferMatches=False).
Therefore the image regression check compares reference replay with candidate
replay; it does not claim either matches that original capture image.

Full-state hashes:

- Tree: 5487D77AF344946FE875398F8AE183E77AE11A11C6E0738BF1B444EFA3C86B79
- Logo: 7424CB6134D22E0D860323BD2010DAC37477F9BC23410C6EC54D8D414659A303
- Complete graphics task: BB124B1561F17BF41433FA6A9B0BF0DB97626A17CEB754844AD20C12FD296F75
- Depth test: BA4E2C516BA0E8BDE66489D7D92E00C09262792F76CDCF4C15CD038EAFE5F706

Probe and Linux UI Release builds pass (UI: 383 warnings, zero errors).
The UI core DLL is byte-identical to the tested candidate core.

## Reproduction and continuation

Final binaries, logs, images, source snapshots, results.json and SHA256SUMS are
under .build-tmp/n64-raster-final-2026-09-19/ (untracked). Input captures remain
under .build-tmp/sm64-20260919/. Exploratory stages are saved in the
n64-raster-loop, n64-raster-combine and n64-raster-depth directories for this date.

Run timing processes sequentially and reverse their order:

```sh
taskset -c 6 dotnet .build-tmp/n64-raster-final-2026-09-19/reference/N64Probe.dll --bench-rdp .build-tmp/sm64-20260919/tree-tape-complete
taskset -c 6 dotnet .build-tmp/n64-raster-final-2026-09-19/candidate/N64Probe.dll --bench-rdp .build-tmp/sm64-20260919/tree-tape-complete
dotnet tools/N64Probe/bin/Release/net8.0/N64Probe.dll --check-depth .build-tmp/n64-raster-final-2026-09-19/reference/Ryu64.MIPS.dll
dotnet tools/N64Probe/bin/Release/net8.0/N64Probe.dll --check-combiner .build-tmp/n64-raster-final-2026-09-19/reference/Ryu64.MIPS.dll
dotnet tools/N64Probe/bin/Release/net8.0/N64Probe.dll --check-render
dotnet tools/N64Probe/bin/Release/net8.0/N64Probe.dll --check-flat-shade .build-tmp/n64-raster-final-2026-09-19/reference/Ryu64.MIPS.dll
```

Repeat renderer timings with logo-tape. Use --bench-rsp-task with rsp-task-real
for the complete graphics task. Next step: profile the warmed updated scene to
choose between the remaining texture sampling/shading and RSP vector arithmetic
costs. Preserve these captures as exact regression references.
