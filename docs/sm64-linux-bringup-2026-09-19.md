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
