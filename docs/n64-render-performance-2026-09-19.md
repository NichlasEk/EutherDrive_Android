# N64 rendering checkpoint — 2026-09-19

Continues [CPU checkpoint two](n64-cpu-performance-step2-2026-09-19.md). The
reference here includes both earlier CPU improvements. Renderer percentages
below are additional fixed-render-work measurements, not FPS or whole-game gains.

## Changes

- Point sampling now uses the already prepared wrap masks when the tile has no
  shift, clamp or mirror and both masks are valid. This is the same coordinate
  transformation as the general path, including negative coordinates and nonzero
  origins. Filtered sampling already used this specialization.
- Normalize and encode `DzPix` once per triangle, instead of repeating those
  calculations for every depth-tested pixel. Textured, flat and shaded triangles
  pass the prepared values to the existing depth test. Depth comparison, depth
  writes, hidden bits, coverage, filtering and blending retain their semantics.
- The RDP benchmark now uses 20 warm-up replays and 20 measured replays. Two or
  five warm-ups were insufficient: tiered JIT sometimes changed a tree replay
  from roughly 63 ms to 21 ms inside the measurement window. Those early timings
  are exploratory evidence only and are not the accepted result.

A trial conditionally skipping trace-only pixel counters was reverted because
it did not give a consistent gain. All original trace counters remain intact.

## Accepted measurements

Separate processes pinned to logical CPU 6 on the same Linux x64 Xeon E5-2697 v3.
For each tape, process order was reference/candidate/candidate/reference. Each
process measured 20 exact command replays after 20 warm-ups. State restoration
and hashing were outside the timed region. Defaults were used without tracing
or performance instrumentation. Other interactive work on the machine means
these small gains should be treated as approximate; individual runs vary.

| Captured frame | Reference medians | Candidate medians | Mean time reduction |
| --- | ---: | ---: | ---: |
| Tree, 2,604 command chunks | 20.908 / 21.614 ms | 21.324 / 20.338 ms | 2.02% |
| Logo, 908 command chunks | 21.503 / 21.661 ms | 20.760 / 21.395 ms | 2.34% |

The full serialized state is identical before/after and across all repetitions:

- Tree: `5487D77AF344946FE875398F8AE183E77AE11A11C6E0738BF1B444EFA3C86B79`
- Logo: `7424CB6134D22E0D860323BD2010DAC37477F9BC23410C6EC54D8D414659A303`

Both final framebuffer files were also compared byte for byte against reference
replays and match exactly. This checks the real captured command stream, not
frames sampled at different wall times during gameplay.

## Checks and build

- 6,845 renderer/interrupt cases passed, including interpolation and depth checks.
- 106,496 texture sampler cases match the reference implementation.
- All 65,536 RGBA5551 conversions and 1,048,576 filter cases pass.
- 432 flat/gradient shading cases match the reference full-state digest.
- Probe and Linux UI Release builds pass. UI reported 383 warnings, zero errors.
- The UI core DLL is byte-identical to the tested final core DLL.

Android and whole-game FPS gains have not been measured. Do not add the CPU-test
percentage and renderer-test percentage together; they measure different work.

## Durable artifacts and reproduction

`.build-tmp/n64-render-2026-09-19/` contains reference/candidate probe builds,
source snapshots, final and exploratory logs, before/after images and a SHA-256
manifest. It is untracked. The input tapes and starting states remain under
`.build-tmp/sm64-20260919/{tree-tape-complete,logo-tape}/`; their hashes are in the
manifest. Earlier CPU checkpoint binaries and snapshots remain intact.

Run from the repository root, one process at a time:

```sh
taskset -c 6 dotnet .build-tmp/n64-render-2026-09-19/reference/N64Probe.dll --bench-rdp .build-tmp/sm64-20260919/tree-tape-complete
taskset -c 6 dotnet .build-tmp/n64-render-2026-09-19/candidate/N64Probe.dll --bench-rdp .build-tmp/sm64-20260919/tree-tape-complete
dotnet tools/N64Probe/bin/Release/net8.0/N64Probe.dll --check-render
dotnet tools/N64Probe/bin/Release/net8.0/N64Probe.dll --check-sampler .build-tmp/n64-render-2026-09-19/reference/Ryu64.MIPS.dll
dotnet tools/N64Probe/bin/Release/net8.0/N64Probe.dll --check-filter
dotnet tools/N64Probe/bin/Release/net8.0/N64Probe.dll --check-flat-shade .build-tmp/n64-render-2026-09-19/reference/Ryu64.MIPS.dll
```

Repeat the timing pair with `logo-tape` and reverse process order. Clear inherited
N64 trace/performance overrides first. Next profiling should use the final build
after adequate JIT warm-up, with these two tapes as correctness references.
