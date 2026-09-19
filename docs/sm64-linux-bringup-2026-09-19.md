# Super Mario 64: Linux bring-up, 2026-09-19

Status: **controllable Mario, but still well below real-time and visually incorrect**. This is the
existing Ryu64 interpreter/RSP/software-RDP path, not Android or a new JIT.

## Verified fixes

- SM64 USA/EUR/JPN cartridge CRC pairs select 4-Kbit EEPROM (512 bytes), not
  the previous unconditional 16-Kbit ID. The USA ROM rejected the old ID in
  `osEepromRead` at `803291b4` and returned without releasing SI access. Its
  game thread then blocked on queue `80367158` before the first graphics task.
  The 2048-byte backing allocation/savestate field stays unchanged; accesses
  are bounded by the actual cartridge capacity. Unknown cartridges retain
  the previous default. This is not a complete cartridge/save-type database.
- Cache PI/SP tracing environment flags at startup. A CPU sampling trace
  attributed most active CPU time during boot to repeated `getenv` calls
  in memory stores and RSP register reads.
- Read the internal MI interrupt wire directly when updating CPU IP2, rather
  than performing two emulated 32-bit bus reads before every instruction.
  The registers have no read side effects; all six bits are in their low byte.
- Reject impossible loop-optimization candidates after the first opcode,
  before reading the remaining four/six words. Recognition and execution of
  matching loops are unchanged.
- Shade values are 16.16, not 18.14; preserve zero alpha instead of promoting
  it to opaque. Correct depth conversion's missing three-bit coverage step.
- Non-perspective texture coordinates use signed S10.5 in the high halfword.
- Do not substitute a solid shaded triangle when a textured triangle writes
  no pixels (transparent texture or depth/alpha rejection).
- Fix mixed-size TMEM byte addressing: this implementation stores halfwords
  as big-endian byte pairs. Its word-address XOR 1 corresponds to byte XOR 2,
  not the native-little-endian XOR 3. Odd rows use byte XOR 6. Loading an
  8-bit texture through a 16-bit LoadBlock previously swapped adjacent pixels.
  Peach's letter is now visibly readable instead of shredded.

## Checks and evidence

`tools/N64Probe` builds against Ryu64Core only:

```sh
dotnet build tools/N64Probe/N64Probe.csproj -c Release
dotnet tools/N64Probe/bin/Release/net8.0/N64Probe.dll --check-eeprom
dotnet tools/N64Probe/bin/Release/net8.0/N64Probe.dll --check-render
```

Results: 18 EEPROM checks, 4391 render/interrupt checks. Includes all 4096
MI pending/mask combinations, 256 shade values, alpha/clamp/fraction cases,
depth clipping boundaries, signed non-perspective coordinates, no solid
fallback for transparent triangles, and 16 mixed-size TMEM samples spanning
both row parities. The TMEM test failed before its fix (`111111ee` instead
of `000000ff` for the first sample), then passed.

Linux UI Release build also succeeds (500 warnings, zero errors). No full
cross-game regression suite or listening/interactive gameplay test was run.

Local primary references inspected (no source copied):

- `/home/nichlas/mupen64plus-core/data/mupen64plus.ini` and
  `src/device/cart/eeprom.c` for cartridge IDs/capacity/bounds.
- `/home/nichlas/mame/src/mame/nintendo/n64_v.cpp`, specifically
  `tc_div_no_perspective`, `rgbaz_correct_triangle`, `rgbaz_clip`, for
  coordinate, shade, and depth conversion.

User ROM: `/home/nichlas/roms/N64/Super_Mario_64_(USA)-.n64`.
Its bytes are actually V64 order (`37804012`); the probe normalizes a COPY
in its output directory. The source ROM and user saves are untouched.

Local untracked artifacts: `.build-tmp/sm64-20260919/`.

- `direct.log`: before EEPROM fix, zero graphics tasks after 25 seconds.
- `eeprom-correct.log`: after EEPROM fix, 26 graphics tasks and 18433
  triangles after 25 seconds. `eeprom-fix.log` is NOT valid after-fix
  evidence: it was accidentally run against an old binary after a failed build.
- `cpu.nettrace`, `cpu.speedscope.json`: initial CPU sampling trace.
- `warm.nettrace`: pinpoints `RefreshRcpInterruptPending` bus-read overhead.
- `ip2-profile.nettrace`: after that fix, RSP vector execution and framebuffer
  snapshots become important remaining costs.
- `perf.log`: 47 graphics tasks in a 20-second resumed title run.
- `final-title-bench.log`: 70 tasks in 20 seconds from the same initial state,
  but it reaches scene loading and overlapped build/other probe activity.
  These are diagnostic task counts, **not a controlled FPS/speedup result**.
- `ip2-fast/`: automated Start/A sequence reaches Peach's intro.
- `final-warm/vi-0010.png`: actual VI buffer with readable letter after TMEM fix.
- `castle/vi-0065.png`: further intro continuation reaches the castle exterior
  and the Peach stained-glass window, verified visually. This does not establish
  controllable Mario or correct graphics. 366 graphics tasks completed during
  the 80-second continuation (about 4.6 tasks/second, not playable real-time).

Probe usage:

```sh
EUTHERDRIVE_N64_PERF=1 dotnet tools/N64Probe/bin/Release/net8.0/N64Probe.dll \
  '/home/nichlas/roms/N64/Super_Mario_64_(USA)-.n64' OUTPUT_DIR 30 [state.bin]
```

`N64_PROBE_AUTO_INPUT=1` optionally holds Start for two seconds every ten
seconds after the first 15 seconds, and A on a separate phase after 35 seconds.
`vi-*.ppm` is a direct 320-wide/240-row (for this ROM) VI-origin snapshot;
`frame-*.ppm` passes through the core's existing framebuffer selection.
They are sampled asynchronously and may include in-progress rendering.
The raw saved state is a diagnostic artifact, not proof of accurate savestates:
RSP vector/accumulator state is not currently serialized. Prefer cold boot
when checking correctness, particularly after renderer/TMEM changes.

`N64_PROBE_REPLAY_RDP=1` replays only the CURRENT DPC range from a loaded
state. RSP output is chunked; **this is not a full-frame replay/oracle**.

## Remaining work

- Title logo is still mostly missing; 3D and texture/rendering accuracy need
  further work. Do not call the current intro picture a correct N64 renderer.
- RSP interpreter remains far below real-time on these probes. Profile from
  a fixed scene; vector temporary allocations and per-command framebuffer
  snapshots have now been addressed (see follow-up below).
- Audit shade/depth interpolation anchors and
  RSP output against a trusted reference. Avoid fake completion/solid-fill
  fallbacks or choosing arbitrary RAM as a framebuffer to disguise bugs.
- Verify collisions, audio, saving, and sustained frame timing in the Linux UI
  before claiming playable. Analog movement and jumps now work in the probe.
- Other N64 games need regression testing; shared renderer changes are not
  a compatibility claim for Zelda/Mega Man/etc. Darius/Gauntlet core code was
  not changed by this milestone.

## Follow-up: control, combiner, and RSP overhead

Mario now exits the pipe; normal controller A presses dismiss the tutorial,
analog Y moves him, and A makes him jump. `mario-input.log` records position
changing from `(-1328,260,4354)` through `(-1248,735,850)` and beyond, with
jump/landing transitions. This is controller input, not patched game RAM.
`N64_PROBE_SM64_INPUT=1` enables the repeatable sequence and read-only USA
MarioState telemetry. Its cartridge CRC guard rejects other revisions.
Structure offsets were checked against the primary SM64 decompilation:
<https://raw.githubusercontent.com/n64decomp/sm64/master/include/types.h>.

Renderer fixes:

- One-cycle mode selects combiner mux bank 1; two-cycle mode feeds bank 0
  through COMBINED into bank 1. Copy mode bypasses the combiner.
- Legitimate black is no longer replaced by texture color or skipped.
  Shaded triangles also apply the programmed combiner and alpha rejection.
- Copy-mode texture rectangles divide the horizontal derivative by four;
  zero derivatives remain zero. This fixes the repeated tiny HUD strips.
- `copy-input/vi-0005.png` visibly shows readable tutorial text, Mario, and
  recognizable life/star counters. Geometry and some textures remain wrong.
- Cold boot `new-cold/vi-0005.png` still shows a largely missing title logo.
  Do not mistake the improved gameplay screenshot for renderer correctness.

Local MAME `n64_v.cpp` one-cycle combiner and copy rectangle paths were
consulted for these behaviors; no source was copied.

Performance changes:

- Flatten RSP vector registers and reuse per-instance operand/result scratch
  arrays, preserving source/destination aliases. Byte DMEM access avoids
  word read-modify-write overhead while retaining descriptor-write hooks;
  flow tracing still uses the original path.
- Publish framebuffer snapshots at FullSync, render-target changes, or task
  completion, not after every streamed primitive. Direct RDP calls retain
  their previous end-of-call publication behavior.
- `rsp-byte-check.log`: one million mixed vector operations, tiering disabled,
  reference 191.53 ms / 125000552 allocated bytes versus current 138.08 ms /
  40 bytes. This is a microbenchmark, not a whole-game FPS claim.
- `copy-input.log`: 80 graphics tasks / 80 snapshots / 12544000 copied bytes
  in about 25 seconds. RSP graphics consumes about 17.1 seconds (including
  its RDP work); RDP about 2.87 seconds. This is still only about 3 tasks/s,
  not real-time, and is not a controlled before/after performance comparison.

Regression commands, in addition to the EEPROM/render commands above:

```sh
EUTHERDRIVE_N64_PERF=1 dotnet tools/N64Probe/bin/Release/net8.0/N64Probe.dll --check-snapshots
DOTNET_TieredCompilation=0 dotnet tools/N64Probe/bin/Release/net8.0/N64Probe.dll \
  --check-rsp .build-tmp/sm64-20260919/rsp-reference/Ryu64.MIPS.dll
```

The local reference DLL was built from the preceding `b085e34c` milestone.
Without a DLL argument, the RSP tool prints a deterministic digest and runs
the current benchmark; that alone is not a differential check.
7168 vector compute/load/store cases match the previous implementation,
including register aliases, selectors, alignments, and DMEM wrapping. This
preserves existing semantics; it does not establish hardware accuracy.
Digest: `30D25383EB20DD2A0FF8A9606B1BF3BDC68CBBC2FD6AE24C34AD39E57126D9C8`.
The snapshot test compares all RDRAM and final published pixels for 240
scanline updates: identical hashes, snapshot copies reduced from 240 to 1.
Render/interrupt coverage is now 4421 cases, plus 18 EEPROM cases.
The differential suite also passes with
`EUTHERDRIVE_N64_RSP_STRICT_HALF_SHUFFLE=0` (a different expected digest).
The Linux UI Release build succeeds with zero errors. Automated controller
telemetry is not a substitute for a listening/manual UI test.

## Follow-up 2: measured DMA and watchdog overhead

Reference for this slice: `265621f8`; its complete probe build is preserved
locally in `.build-tmp/sm64-20260919/next-reference/`.

- SP DMA copies contiguous byte-array segments directly, splitting at the
  independent 4 KiB SP-bank and 8 MiB RDRAM boundaries. Per-byte RDRAM write
  epochs are retained, as are queueing, DMA delays, registers, and skip/count
  behavior. Relevant trace/watch options retain the original byte path.
- Vector shuffle selection is hoisted outside the lane loop; scalar broadcast
  reads the source once. Both half-shuffle compatibility modes are retained.
- The RSP watchdog reuses its signature only when neither a watched GPR nor
  CP0/lifecycle state can have changed. It still checks stagnation each
  instruction, with the same stop count; this is not a longer timeout.
- `--check-dma [reference.dll]`: 108 sequences covering both directions/banks,
  wrapping, unaligned descriptors, length/count/skip, and queued requests.
  Complete serialized memory states match before/after and during DMA:
  `948C6449D713DD5AC2D6553AD3AA2E258E301EC905CD282A1BB20EAC4028E3DD`.
- `--check-rsp [reference.dll]` now additionally compares 64 finite synthetic
  full tasks (tracked/untracked GPRs, branches/delay slots, vectors, DMA),
  and verifies raw/task watchdog stagnation stop counts. The test command
  sets a 4096-instruction task watchdog limit solely for its short stop test.

Wall-clock comparison: `watchdog-bench-{reference,current}-{1,2}.log`,
20 seconds each, serial alternating runs from the same `mario-input/state.bin`,
no controller input, `DOTNET_TieredCompilation=0 EUTHERDRIVE_N64_PERF=1`.
Reference completed 72/73 graphics tasks; current completed 87/90, about 22%
more. This is workload throughput including startup and stop-boundary effects,
not an exact-frame deterministic benchmark or a claim of playable speed.
The scene still advances as each build runs, and the normal UI uses runtime
tiering. Sampling evidence: `rsp-next.nettrace` / `.speedscope.json`.

## Follow-up 3: the missing logo was a disabled low-memory Z buffer

The command stream explicitly sets the depth image to physical `0x400`, then
uses the same address as a color target to clear it with `fffcfffc`.
`PlausibleFramebufferOriginFloor=0x1000` incorrectly suppressed both that
clear and ALL depth testing against the buffer. Four far black shaded
triangles (commands 705-708 in the captured frame) then erased the otherwise
rendered SUPER MARIO logo, leaving only the later 64/text overlays.

Rendering now accepts in-bounds low RDRAM addresses for triangles, rectangles,
and depth. Presentation/snapshot heuristics remain separate, so the Z buffer
does not become a candidate display image. Twelve new regression assertions
exercise clear/update/near-far occlusion at bases 0, 0x400, and 0x1000.
Render/interrupt total: 4433.

Evidence:

- `logo-tape/steps.png`: progression through one RDP command stream shows the
  logo drawn, then erased. Before the fix, replaying all 908 recorded command
  chunks reproduced the captured framebuffer byte-for-byte.
- `logo-low-z/rdp-final.png`: replay of precisely the same stream after the
  fix preserves the full logo. Geometry/interpolation is still imperfect.
- `low-z-cold/vi-0010.png`: full logo from cold boot; `vi-0025.png` reaches
  the Mario head. This is not merely a savestate/replay-only improvement.
- `low-z-gameplay/vi-0025.png`: Mario jumping outside the castle; terrain
  occlusion improves. Black tree-billboard backgrounds remain to investigate.

The probe can capture one frame synchronously through its existing trace
writer, without adding per-command callbacks to the production renderer:

```sh
N64_PROBE_CAPTURE_RDP=1 dotnet tools/N64Probe/bin/Release/net8.0/N64Probe.dll \
  '/home/nichlas/roms/N64/Super_Mario_64_(USA)-.n64' OUTPUT_DIR 5 [state.bin]
dotnet tools/N64Probe/bin/Release/net8.0/N64Probe.dll --replay-rdp OUTPUT_DIR REPLAY_DIR
```

Capture requires a leading SyncPipe and stops at FullSync. It saves initial
memory, command bytes, intermediate images, and final RDRAM. Replay reports
whether the final framebuffer matches. It does not capture arbitrary RSP
writes to texture RAM between commands; validate that match before treating
a new tape as an oracle. Incomplete captures are rejected. Artifacts contain
game data and stay local/untracked; do not publish tapes, ROMs, or savestates.

## Follow-up 4: tree cutouts and transparent depth writes

SM64 enables `CVG_X_ALPHA` (OtherModes bit 12) for tree billboards, without
enabling alpha compare. The renderer previously ignored this bit and wrote
opaque-looking black geometry for zero-alpha texels. Zero alpha now rejects
the fragment in one-/two-cycle coverage-times-alpha modes, before both color
and depth writes. Shade-only triangles now also reject alpha before updating
depth. This fixes the black tree backgrounds; partial coverage/AA is still
not modeled. Local primary reference: MAME `n64_v.cpp:get_alpha_cvg`.

- `tree-tape-complete/`: 2604 command chunks, complete FullSync capture and
  byte-identical baseline replay. The earlier `tree-tape/` and
  `tree-tape-full/` attempts were incomplete and are not oracles.
- `tree-coverage/rdp-final.png`: identical captured commands, now with the
  black tree backgrounds removed.
- Captures can now re-arm the trace budget while recording a long frame;
  normal emulator trace limits remain unchanged.
- Render/interrupt checks total 4445, including transparent color/depth
  rejection with coverage enabled/disabled and memory-state round trips.
- Memory savestate version is now 4 to preserve the new coverage flag.
  Versions 1-3 still load; their missing flag resets to false and is supplied
  by the next SetOtherModes command. Older emulator builds cannot load v4.
  DMA/RSP reference checks normalize only this additive schema header/trailer
  after asserting the new flag is false, retaining comparison of all prior
  state. Both differential suites still pass against the prior DLL.

## Follow-up 5: attribute anchors and fractional interpolation

The three software triangle paths now use `floor(YH)` for the initial XH/XM
edge bases, as in the reference edge walker. Shaded color and depth are
interpolated from the major edge, regardless of its left/right orientation.
Fractional X/Y offsets are multiplied by their gradients before rounding;
rounding the offsets to whole pixels first produced alternating bands and
large seams. Primitive depth stays constant rather than accumulating the
triangle's depth gradients. Depth clipping now saturates both overflowing
19-bit ranges rather than wrapping the upper range.

`TriangleInterpolationChecks` exercises 2400 color/depth pixel assertions
across both edge orientations, sloped edges, fractional/negative YH, and
primitive/gradient Z. The original code fails its planar-color assertion
(`4081 != 3881` at pixel 3,2 in the initial test); the corrected code passes.
Render/interrupt total is 6845. These tests validate this pixel-center
renderer, not exact RDP subpixel coverage/antialiasing.

Same-stream images: `logo-interpolated/rdp-final.png` and
`tree-interpolated/rdp-final.png`. `interpolated-head/vi-0015.png` shows a
substantially cleaner Mario head; the previous large triangular shading
discontinuities are gone. Texture filtering, blending, coverage, and timing
still need wider validation. Local reference: MAME `n64_v.cpp` edge setup,
span attribute initialization, and `rgbaz_clip`.

## Follow-up 6: Linux smoke and CPU loop-recognition overhead

The rebuilt Linux UI boots the original US cartridge in an isolated Xvfb
desktop (`ui-smoke-F47Sdj/ui.png`, `ui.log`). It reaches the animated title
transition without unknown CPU opcodes. This checks the Bitmap UI path, not
interactive Wayland input or audible playback; audio output was disabled.

`post-gfx.speedscope.json` showed about 17.5% of sampled CPU-thread time in
three memory-loop recognizers, scanning backwards on every CPU instruction.
`TryFastForwardMemoryLoops` now reads the current opcode once and dispatches
only to the matching recognizer. Every possible entry opcode is included;
the existing complete live-code checks and loop semantics remain unchanged.
There is no PC-only cache and no stale-code assumption.

`--check-cpu-loops` passes 540 full serialized CPU/memory differential cases
against the old recognizer chain, including all entries in both direct-mapped
segments, every instruction mutated/restored, and segment/RDRAM boundaries.
136 cases accept a loop. Instruction counts are compared separately too.
The profiler summary tool is `tools/N64Probe/summarize-profile.py`; it reports
sampled thread-time, not hardware CPU counters, and marks overlapping
inclusive stacks explicitly.

Serial alternating 20-second runs from `mario-input/state.bin`, with tiering
disabled and no input, completed 86/86 graphics tasks before and 106/104
after (`cpu-gate-{reference,current}-{1,2}.log`): about 22% more tasks.
This is wall-clock throughput through an advancing scene, not exact
frame-for-frame timing or a claim of full-speed playability.

## Follow-up 7: RSP vector operand transfers

The RSP's eight-halfword register copies now transfer two packed 64-bit
words, swapping bytes within each lane on little-endian hosts. Explicit
register/scratch bounds checks precede the pinned copies. Register layout,
lane order, alias handling, and arithmetic are unchanged. VSAR, reciprocal,
VMOV, and VNOP also avoid operand loads they do not consume.

- 2048 randomized direct-copy cases cover all 32 registers, lane order,
  scratch guards, and neighboring-register preservation.
- All 7168 vector cases and full-task/watchdog differential checks match
  `pre-vector-copy/Ryu64.MIPS.dll` in both half-shuffle modes.
- The vector microbenchmark improved from 155 to 107 ms in the first run;
  the alternate-mode run was 130 to 101 ms, with no new allocations.
- Whole-scene runs completed 93/101 tasks before and 94/105 after. Other
  Rust builds were active on the machine during these runs; this is too
  noisy to assign a dependable whole-game percentage. Logs are under
  `vector-copy-{reference,current}-{1,2}.log`.

The post-CPU-gate profile puts about 65% of sampled thread time inside RSP
tasks (including the software RDP renderer), versus 35% outside. Full-speed
playability still requires substantially more work.

## Follow-up 8: avoid texture-state copies and unused fourth sample

Texture decoding takes the tile descriptor by readonly reference instead
of copying its 60-byte state for every texel. Three-point filtering decodes
only the three corners used by its selected triangle, not a fourth unused
corner. Texture formats, coordinate rules, palette lookup, and blending
arithmetic are unchanged.

`--check-sampler REFERENCE_DLL` compares 40960 deterministic samples across
all format/size combinations, TLUT modes, filtered/unfiltered sampling,
fast-clamp/general coordinates, negative/wrapped coordinates, and both
filter triangles. SHA256 matches before/after:
`F4C973F44AE32F1F8A124C9FDC1A659E54BE533507395FA7A87273837AD05CD3`.
All 6845 render/interrupt assertions also pass.

`--bench-rdp CAPTURE_DIR [REFERENCE_DLL]` restores the same captured memory
before every frame, warms twice, and measures 12 identical replays. Loading
and hashing are excluded from timing. Complete serialized memory state is
compared on every repetition and between builds, not just visible pixels.

- Tree scene, 2604 chunks: median 56.557 ms before, 45.978 ms after (~19% less
  rendering time), full-state hash
  `5487D77AF344946FE875398F8AE183E77AE11A11C6E0738BF1B444EFA3C86B79`.
- Logo, 908 chunks: median 17.332 ms before, 14.452 ms after (~17% less), hash
  `7424CB6134D22E0D860323BD2010DAC37477F9BC23410C6EC54D8D414659A303`.
- Logs: `sampler-tree-bench.log`, `sampler-logo-bench.log`, reference binary
  `pre-sampler/Ryu64.MIPS.dll`.

These are software-RDP costs only; CPU/RSP work is additional, so these
numbers must not be presented as whole-game frame rates.

## Follow-up 9: prepared color-combiner cases

SetCombine and state loading now classify each mux bank into exact
passthrough, RGBA multiplication, RGB multiplication with independent alpha,
or the unchanged general evaluator. The derived dispatch is not serialized.
Fast multiplication retains the generic evaluator's `(+128) >> 8` rounding,
not the separate fallback modulation helper's division by 255.

`--check-combiner REFERENCE_DLL` passes 156352 color cases across curated and
random muxes, all cycle modes, edge/random colors, independent RGB/alpha,
COMBINED feedback, and loading a prior mode over a different live mode.
Digest: `63F29003B3E83B0973B4678E0FFF4E616DA4FFFF54CC25CED12BEFA63F5D55C2`.
All 6845 render/interrupt assertions still pass. Fixed-work complete-state
replays match exactly against `pre-combiner/Ryu64.MIPS.dll`:

- Tree: median 51.791 ms before, 40.677 ms after (~21% less rendering time).
- Logo: median 16.467 ms before, 12.371 ms after (~25% less).

Logs: `combiner-{check,render-check,tree-bench,logo-bench}.log`. Compare each
paired run, not absolute timings from different host-load periods.

## Follow-up 10: boot-helper region prefilter

The normal CPU loop no longer calls seven boot recognizers separately for
ordinary game-code PCs. A shared region check retains low kseg0 and the
entire SP DMEM region, then runs the original helpers in their original
order. The generic IPL3 cache-loop path is deliberately retained, not just
the fixed IPL3 addresses. This changes dispatch overhead only.

The combined CPU-gate suite now passes 648 full-state differential cases,
149 accepting a loop. Added cases cover region boundaries, all fixed boot
addresses, fixed and generic cache loops at every entry, and boot RAM clear.
Paired 20-second runs completed 108/108 graphics tasks before and 111/115
after (`boot-gate-{reference,current}-{1,2}.log`). This is a smaller gain
than the earlier memory-loop prefilter; host/scene variation still applies.

An actual .NET disassembly (`cpu-disasm.log`) confirmed that the large CPU
loop was already compiled with FullOpts and disabled trace branches folded
away. It was therefore not refactored speculatively to chase trace overhead.

## Linux checkpoint, 2026-09-19 10:20 Europe/Paris

All implementation slices above are committed and pushed. The Release UI
was rebuilt after the final CPU change: 0 errors, 501 warnings
(`final-ui-build.log`). User-owned Gauntlet files and unrelated artifacts
were left untouched. ROMs, savestates, command tapes, traces, and screenshots
remain untracked under `.build-tmp/sm64-20260919/`.

Final verification:

- 90-second fresh boot, normal tiering, scripted Start/A: reaches file
  selection and Peach's introductory letter, no reported unknown CPU
  opcodes or halt. `final-cold/frame-0030.png` and `frame-0075.png`.
- Latest Linux Bitmap UI in isolated Xvfb: recognizable Mario head and
  PRESS START. `final-ui-smoke-cMESZ5/ui.png`, `ui.log`. It was stopped after
  the smoke check; no test emulator processes remain running.
- Thirty-second saved-scene controller check: Mario jumps and moves from
  Z=4354 to Z=2955.64, with forward velocity 31.45 at the end.
  `final-input.log`, `final-input/`. This is emulated controller input,
  not a physical keyboard/gamepad test.
- Final suites: render/interrupt 6845, EEPROM 18, DMA 108 complete-state
  differential sequences, RSP 7168 vector cases plus 2048 direct-copy cases
  and finite-task/watchdog comparisons. Snapshot coalescing check passes.
  CPU gates: 648; sampler: 40960; combiner: 156352 in their dedicated logs.

Still not full-speed gameplay. The fixed test scene is roughly 5-6 graphics
tasks per wall-clock second, and RSP execution/software rendering remain
the main cost. Some small visual seams/texture artifacts remain visible,
including the Mario-cap emblem in the UI head shot. Audio processing was
covered by unchanged RSP results, but playback was not listened to; the UI
smoke explicitly disabled audio output. Savestates still do not include all
private RSP execution state, so cold-boot checks remain essential.

To try the freshly built Linux app (restart an older instance first):

```sh
dotnet run --project EutherDrive.UI -c Release --no-build -- "/home/nichlas/roms/N64/Super_Mario_64_(USA)-.n64"
```

Next session: profile this exact checkpoint before choosing a larger RSP/CPU
execution change; use fixed-work RDP replays to protect graphics. Do not
trade away watchdog, interrupt, depth, coverage, or arithmetic behavior for
a headline frame-rate gain. Validate real desktop input and audible output
separately from the automated probes.

## Follow-up 11: instruction decode and honest N64 rate display

The user's OpenGL screenshot showed 56.3 fps in the deck monitor. Source
inspection confirms that this counted N64Adapter.RunFrame calls, including
calls reusing an existing framebuffer. It was not evidence of 56.3 newly
rendered game frames per second. Presentation/backend selection does not
turn the N64 software RDP into an OpenGL hardware renderer.

The MIPS opcode index now selects the six primary-opcode bits and six
function bits, instead of mixing primary-opcode and operand-register bits.
It retains the same 4096 buckets, full mask checks, and original first-match
precedence. `--check-opcodes REFERENCE_DLL` compares 852358 encodings against
an ordered scan of every instruction definition, including 16617 rejected
encodings. It checks handler, mask, value, cycle cost, and disassembly text.

The fixed 799110-instruction lookup workload fell from median 49.707 ms to
19.294 ms, with identical checksum 5851944. Whole-scene alternating runs
were only 116/114 tasks before versus 117/115 after in 20 seconds: a very
small change, not a demonstrated large game-speed gain. Logs:
`opcode-{check,reference-1,current-1,reference-2,current-2}.log`.

A separate runtime-loop prefilter was tested (671 complete-state cases
passed) but removed: 122/121 versus 123/121 tasks was not a dependable
whole-scene win. `runtime-gate-*` artifacts describe the rejected candidate,
not the committed core. Do not accidentally reintroduce that experiment.

The N64-only deck monitor now labels real RSP graphics-task throughput as
`gfx/s` and labels the old poll frequency `UI polling .../s; not game fps`.
Graphics tasks are not claimed to equal unique displayed frames. The meter
resets across cartridge/reset/savestate generations and pause/restart. It
does not alter pacing, the existing FrameCounter, controller handling, or
audio. Audio work remains deferred as requested.

Final accepted-source rebuild: `faster-final-ui-build.log`, 0 errors, 384
warnings. Repeated opcode check: 50.173 versus 19.814 ms; all 852358 cases
pass. Render/interrupt checks: 6845 passed; snapshot 240-to-1 check passed.
The isolated final UI smoke (`fps-final-smoke-u8PAi2/ui.png`) visibly shows
`6.0 gfx/s` alongside `UI polling 44.2/s; not game fps`, confirming the two
rates are separate. The test used Bitmap/Xvfb with audio output disabled;
it does not assert an OpenGL or audible-playback improvement. The diagnostic
emulator was stopped after capture, and no user settings were modified.

This pass improves instruction lookup but does not achieve 60 actual game
frames per second. The next major performance work remains RSP execution
and software RDP rendering, not increasing UI polling frequency.

## Follow-up 12: Linux audio output

Replaced host polling of AI DRAM/LEN registers with a bounded, locked queue
of immutable stereo PCM snapshots taken when an AI FIFO entry starts.
Polling the remaining DMA length previously replayed buffer prefixes and
could discard reused buffers with identical addresses and lengths. AI timing,
interrupts and savestate layout are unchanged; loading a state clears host
audio rather than replaying old queued samples. An already active restored
DMA is not replayed; playback resumes at the next FIFO activation.

The adapter now continuously resamples the cartridge DAC rate to the UI
output rate (44100 Hz by default, respecting EUTHERDRIVE_AUDIO_OUTPUT_HZ).
Previously the shared UI engine rejected N64 buffers at other rates.
Integer phase preserves interpolation continuity across DMA blocks; reset,
ROM load and savestate load clear resampler history.

Validation: `N64Probe --check-audio` covers snapshots, endian/stereo ordering,
FIFO activation, identical buffer reuse, DAC rate, unpopulated RAM, bounded
queue, savestate flush and 24 source/output-rate streaming combinations.
Existing 6845 render/interrupt, 648 CPU-loop differential and 18 EEPROM
checks pass; Linux Release UI builds cleanly
(existing warnings remain).

`N64_PROBE_CAPTURE_AUDIO=1` now captures normalized stereo s16le PCM locally.
Gameplay capture produced 424572 samples (4.814 seconds) in a 20-second run,
424453 nonzero, peak 20778. Cold boot produced 595892 samples (6.756 seconds)
in 25 seconds, 466503 nonzero, peak 29481. Artifacts are under
`.build-tmp/sm64-20260919/audio-gameplay` and `audio-cold`.
A separate Xvfb Linux UI run with SDL's dummy audio device confirmed 44100 Hz
stereo accepted by AudioEngine, nonzero production and no format rejection.
This validates the output path, not listening quality or physical speakers.
Underruns remain: emulation produces substantially less than one second of
audio per wall-clock second. This is not yet smooth real-time audio and does
not establish correct music/instrument/effect emulation by listening.

## Follow-up 13: RSP vector transfer/shuffle optimization

Kept two local RSP improvements: LQV/SQV use a contiguous block copy where
neither DMEM nor register boundaries can wrap, and shuffled vector operands
use packed repeated-halfword stores. Descriptor writes and flow tracing
retain the byte-at-a-time path. Both strict and legacy half-shuffle layouts
are preserved. No instruction counts, watchdog limits, DMA timing, frame
skipping or audio settings were changed.

An experimental memory-side watchdog-signature cache passed differential
tests but did not improve whole-scene throughput; it was removed. Logs named
`rsp-cache-*` describe that rejected experiment, not the accepted code.

Expanded `--check-rsp REFERENCE_DLL`: 32768 shuffle cases, 2048 register-copy
cases, 9216 instruction cases (including extra LQV/SQV boundary and descriptor
addresses), 64 finite tasks and two watchdog cases per shuffle mode. Both
modes match the pre-change core, including full serialized task state.
Reference binaries: `.build-tmp/sm64-20260919/pre-packed-shuffle/`.

Fixed-work measurements in `rsp-quad-check-final.log`: one million quad
transfers fell from 65.66 to 28.32 ms; mixed shuffled vector arithmetic fell
from 108.87 to 101.67 ms. Repeated quad tests measured roughly 66 to 27 ms.
These are microbenchmarks, NOT a corresponding game FPS improvement.
Twenty-second whole-scene runs remained around 150 graphics tasks, including
152 before, 151 shuffle-only and 150 with quad copies. No reliable whole-game
FPS increase was established. A user-owned desktop emulator was also running
and was left untouched. `rsp-final-before/after-audio` additionally overlapped
a build and must not be used for a speedup claim; `rsp-verified-*` repeats the
audio-enabled comparison after the build.
That final comparison completed 151 versus 152 graphics tasks in about
20 seconds, still too small a difference to claim a reliable FPS gain.

Audio checks and all 6845 render/interrupt checks pass. Linux Release UI
build: `rsp-final-ui-build.log`, zero errors (501 existing warnings).
Cold boot reached the Mario head screen and generated 612306 stereo samples
(6.942 seconds) over a 25-second capture; 478820 nonzero, peak 29481. This is
functional audio-path coverage, not an audible-quality test. Artifacts:
`rsp-final-cold/`, `rsp-final-check.log`, `rsp-final-check-legacy.log`.

Next useful step is a fixed-work real RSP task capture/profile or a larger
execution-engine improvement. Do not treat these small vector optimizations
as a solution to the remaining interpreter/RDP cost or audio underruns.

## Follow-up 14: real RSP task replay and accumulator arithmetic

Added `N64_PROBE_CAPTURE_RSP=1` to N64Probe. It captures one graphics task
through existing synchronous task logging, saving CPU/memory plus all private
RSP registers before and after execution. `N64_PROBE_CAPTURE_RSP_TYPE=2`
selects an audio task instead. Use a fresh output directory. This is a probe
facility, not a change to the emulator's normal savestate format.

`--bench-rsp-task CAPTURE_DIR [REFERENCE_MIPS_DLL]` restores and replays the
exact task, checking every serialized CPU/memory byte and every RSP register
against the actual captured ending on every iteration. Five warmups precede
twelve timed iterations. A reference DLL is for correctness validation only:
collectible AssemblyLoadContext loading changes JIT/static-field access costs
and yielded misleading timing comparisons in the first experiment. Compare
speed in separate processes using the same probe harness, with each core in
the default load context. Tiered JIT warmup can also skew short task timings;
`DOTNET_TieredCompilation=0` makes fixed-work comparisons more stable but is
not the normal app configuration.

Rejected an SSE2 multiply experiment: instruction and complete-task state
checks passed, but whole-scene runs were 148/145/147 versus 151/154 graphics
tasks in approximately 20 seconds. Its net8 multi-targeting and all SIMD paths
were removed. `rejected-simd/` and `rsp-simd-*` artifacts are NOT accepted code.
The initial apparent 10% task gain included the load-context measurement bias.

The accepted change instead simplifies scalar 48-bit accumulator work:
VMADH updates only the upper 32 bits with exact wrapping and preserves LO;
VMADM/VMADN/VMADL clamp directly from their computed results; accumulator
writes no longer mask/sign-extend upper bits that are immediately discarded.
No instruction count, watchdog, DMA timing, renderer or audio-format changes.
The core still targets netstandard2.0; no new hardware requirement.

A sampling trace (`rsp-baseline-profile.nettrace`) identified vector compute
and textured rendering as major hotspots. The sleeping probe-main thread must
not be mistaken for emulation CPU cost. Optional
setting `EUTHERDRIVE_N64_PROFILE_VECTOR_OPS=1` when running `--bench-rsp-task`
reports opcode counts.
The 794278-instruction graphics task executed VMADN 40320, VMADH 31547 and
VMADM 21662 times, motivating the arithmetic changes.

Accepted-source validation:

- 10368 instruction cases, 32768 shuffle cases, 2048 register-copy cases,
  finite tasks and watchdog tests match the previous core in both shuffle
  modes. Additional multiplication/accumulator tests cover extrema, aliases,
  carry/wrap and saturation (`rsp-acc-final-{check,legacy}.log`).
- Real graphics and audio replays match their captured CPU/memory and RSP
  state byte-for-byte: 794278 and 18824 instructions respectively.
- Separate-process graphics replay with tiering disabled: median 110.029
  to 106.684 ms. Audio timing was noisy and does not establish an audio-task
  speedup (`acc-stable-*.log`).
- Alternating normal-runtime, audio-enabled 20-second scene runs: 152 -> 157
  and 151 -> 155 completed graphics tasks, approximately 3% more work on this
  host (`acc-before-{1,2}.log`, `acc-after-{1,2}.log`). A user-owned emulator
  remained running throughout; this is a modest local result, not a universal
  FPS guarantee or a claim of real-time N64 speed.

Reference core binaries remain in `pre-rsp-simd/`; only its N64Probe harness
was updated to make the isolated-process tests use identical benchmark code.
Keep cartridge/state/PCM/task captures and traces local under `.build-tmp`.

Final Linux Release UI build passed (`acc-final-ui-build.log`, zero errors,
501 existing warnings); probe rebuild also passed. Audio regression checks,
6845 render/interrupt checks and 18 EEPROM checks pass. A fresh cold boot
reached the Mario-head screen with nonzero audio (`acc-cold/`); this was a
functional check overlapping build activity, not a performance measurement
or listening test. No user-owned emulator was stopped or settings changed.

## Follow-up 15: exact texture filtering/color expansion fast paths

Three software-RDP changes, without changing filtering quality, depth,
coverage, frame skipping or guest timing:

- Three-point filtering evaluates R/B and G/A as packed 16-bit lanes.
  The selected triangle's nonnegative weights sum to 32; the convex sum
  plus rounding is algebraically identical to the old signed-difference
  formula. Lane sums never exceed 8176, so neither cross-lane carry nor
  channel saturation is needed.
- RGBA5551 expansion uses a shared immutable 65536-entry table (256 KiB).
  This caches only a color conversion, not texture contents or palette state;
  texture and palette writes remain immediately visible.
- Unshifted, non-mirrored wrapping tiles precompute their coordinate masks.
  Adjacent filter samples use modulo-mask addition instead of four general
  coordinate transforms. Other modes retain the existing paths.

Validation: `--check-filter` passes 1048576 filter cases covering every 5-bit
S/T fraction, triangle diagonal, channel extrema and mixed random RGBA, plus
all 65536 color conversions. Expanded `--check-sampler REFERENCE_MIPS_DLL`
passes 81920 differential cases, including forced wrapping, negative
coordinates, nonzero origins, palettes and texture formats. Render/interrupt
(6845), audio and EEPROM (18) checks pass. Logo and tree command replays,
and the 794278-instruction captured graphics task, preserve full end state.

Isolated-process fixed-work tree replay (2604 chunks): 40.617 ms baseline,
39.435 ms packed filtering alone, 36.507 ms with color table, 34.202 ms with
wrap specialization. Final versus baseline is about 16% less rendering time.
The complete captured RSP graphics task also retains its exact state hash.
RDP reference-ALC runs now label themselves validation-only, matching the
RSP harness rule: compare speed using separate default-context processes.

Alternating audio-enabled whole-scene runs completed 158 -> 168 and 156 ->
167 graphics tasks in about 20 seconds: roughly 7% more work on this host.
These are graphics tasks, not UI polls or guaranteed unique displayed frames.
They do not establish real-time speed or smooth audio yet. User-owned emulator
processes were left running and no user settings were changed.

Evidence under `.build-tmp/sm64-20260919/`: `pre-packed-filter/` baseline,
`filter-{before,after}-{1,2}.log`, `filter-{before,after}-rdp.log`,
`filter-{table,wrap}-rdp.log`, `filter-check.log`, `filter-sampler-check.log`,
`filter-logo-check.log`, `filter-final-task-check.log`. Do not use timings from
the final functional task check or cold boot: those overlap validation/build
activity. Cartridge, state, trace and PCM artifacts remain local.

Final Linux Release UI build passed with zero errors (501 existing warnings).
A 25-second cold-boot smoke test reached the Mario-head title screen, visually
checked in `filter-cold/frame-0025.png`, and produced nonzero captured audio
(570522 samples, 442604 nonzero). This verifies startup/audio output, not
listening quality or real-time performance. Logs: `filter-final-ui-build.log`
and `filter-cold.log`.

## Follow-up 16: shared RGBA16 three-texel fetch

The three-point filter now has a direct RGBA16/no-TLUT fetch, after normal
coordinate transformation. It computes the two TMEM row bases and odd-row
swaps once, reads the three selected texels directly from live TMEM and uses
the same exact color expansion, interpolation and alpha cutoff. Even masked
addresses stay within the fixed 4096-byte TMEM. Other formats and palette
modes retain the generic decoder. There is no texture cache, timing change,
frame skipping or reduced filtering quality.

Sampler differential coverage adds 24576 RGBA16 cases, covering all three
coordinate paths, random TMEM bases/strides, negative coordinates, and changes
to TMEM contents and TLUT interpretation after sampler preparation.

Evidence is local under `.build-tmp/sm64-20260919/`, with reference binaries
in `pre-texel-fetch/` (commit `56eecfc5`) and new logs prefixed `fetch-`.

Separate-process fixed-work measurements (`DOTNET_TieredCompilation=0`):

- Tree RDP replay (2604 chunks, 12 measured runs): median 34.354 -> 28.968 ms,
  about 16% less renderer time. Full state SHA remains
  `5487D77AF344946FE875398F8AE183E77AE11A11C6E0738BF1B444EFA3C86B79`.
- Complete graphics RSP task (794278 instructions): median 99.843 -> 92.576 ms,
  about 7% less time. Full CPU/memory/private-RSP state remains identical,
  SHA `BB124B1561F17BF41433FA6A9B0BF0DB97626A17CEB754844AD20C12FD296F75`.
- Audio-enabled whole-scene 20-second pairs: 158 -> 156 and 148 -> 167 graphics
  tasks. Combined throughput is about 6% higher, but individual pairs range
  from slightly worse to 13% better. This is noisy supporting evidence, not
  a reliable FPS promise. The user's active emulator was not stopped.

The expanded sampler check passes all 106496 cases against the pre-change
core, SHA `C2B10FE95B3599B8DF82E834D39FE126B3C9F29F2CE526BBC51A826D049A7BBB`.
Performance logs: `fetch-{before,after}-{rdp,task}.log` and
`fetch-{before,after}-{1,2}.log`. Final differential replay timings overlap
the UI build and are correctness-only; do not use them as speed measurements.

Final checks passed: 1048576 filter cases, all 65536 color conversions,
6845 render/interrupt cases, audio snapshot/resampler checks and 18 EEPROM
cases. Logo and tree differential replays preserve complete state. Linux
Release UI build succeeded with zero errors (501 existing warnings).

## Follow-up 17: RSP dispatch/history overhead

After publishing follow-up 16 as `b4ec32eb`, three small execution-loop changes:

- Use an explicit power-of-two constant for the 16-entry instruction-history
  ring and mask the index instead of dividing by the array's runtime length.
  The complete diagnostic history, index and watchdog state remain unchanged.
- Send vector arithmetic directly to its decoder before extracting scalar
  fields and passing through the COP2 transfer decoder. The same opcode
  predicate and vector implementation are used; scalar COP2 is unchanged.
- Check the immutable trace flag at the call site, avoiding a call into the
  large flow-logging method when tracing is disabled. Enabled tracing retains
  the original path. Instruction counts, lifecycle ticks and watchdog checks
  are unchanged.

Reference binaries: `.build-tmp/sm64-20260919/pre-rsp-loop/`. Fixed-work graphics
task replay, separate processes with tiering disabled, 794278 instructions:
baseline/changed medians 93.047/87.010 ms, then reversed-order changed/baseline
87.660/93.546 ms. This is about 6% less task time on top of follow-up 16.
Earlier runs were noisier (91.118/88.439 before the trace guard, and a 104.180 ms
baseline outlier); keep the raw logs instead of extrapolating to a promised FPS.
All graphics replays match the captured CPU/memory/private-RSP end state.
The 18824-instruction audio task also matches its complete captured end state.
Evidence: `loop-{before,after}-task-{3,4}.log`, `loop-{before,after}-audio.log`.

An audio-enabled 20-second whole-scene pair completed 176 -> 180 graphics
tasks (about 2% more); this is only one noisy pair, not a stable FPS estimate.
Logs: `loop-{before,after}-scene.log`. No user emulator was stopped.
RSP instruction/task differential checks pass in both shuffle modes (10368
instruction cases each), including branch/delay-slot, DMA and watchdog tasks;
2048 vector-copy and 32768 shuffle checks also pass in each mode. Audio and
6845 render/interrupt checks pass. These functional checks overlap the final
UI build; their embedded benchmark timings are not performance evidence.
Final Linux Release UI build passed with zero errors and 501 existing warnings
(`loop-ui-build.log`).

## Follow-up 18: scalar RSP access and cold trace code

Split register-write logging out of `WriteGpr`, keep scalar load/store trace
calls behind the existing immutable trace flag, and request inlining for the
small register/word/halfword access helpers. The memory functions, wrap rules,
zero-register rule and progress-dirty tracking are unchanged. Trace messages
still run before the same writes and include the same old/new values.

New `--check-rsp-scalar REFERENCE_DLL` compares 2048 deterministic cases with
all 32 GPRs, unchanged-value writes, unaligned and wrapping DMEM addresses,
and word/halfword/byte reads and writes. It hashes results, dirty flags, final
register/memory state and exact trace text. Run it separately with
`EUTHERDRIVE_TRACE_N64_RSP_FLOW=0` and `=1`; enabled tracing must emit text.

Baseline is `e2bfa115`, copied to `.build-tmp/sm64-20260919/pre-gpr-inline/`.
Initial complete graphics-task medians (794278 instructions, separate
processes, tiering disabled) were 94.607 ms baseline, 85.416 changed,
88.946 baseline, 85.931 changed, 86.325 baseline. The first baseline is noisy;
the later runs suggest only a small gain, not a claimed 10% improvement.
Every replay preserves the complete captured end state. An extra experiment
forcing accumulator helpers inline did not clearly improve further and was
reverted. Accepted intermediate binaries are in `gpr-inline-only/`.

One audio-enabled 20-second scene pair completed 186 -> 191 graphics tasks
(about 3% more). This is not a guaranteed FPS improvement; host load varies.
Logs: `gpr-{before,after}-scene.log`. No user process or settings were changed.

Final validation passes: scalar state/trace checks in both trace modes,
10368 RSP instruction cases plus synthetic DMA/branch/watchdog tasks in both
shuffle modes, vector-copy/shuffle checks, 6845 render/interrupt checks, and
audio FIFO/resampling checks. Captured audio task (18824 instructions) retains
its exact CPU/memory/private-RSP end state. Final checks ran alongside the UI
build; their timing output is not used as performance evidence. Logs use the
`gpr-` prefix, including `gpr-scalar-final{,-trace}-check.log`.
Linux Release UI build passed with zero errors and 501 existing warnings
(`gpr-ui-build.log`).
