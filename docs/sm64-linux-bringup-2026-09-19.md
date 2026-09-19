# Super Mario 64: Linux bring-up, 2026-09-19

Status: **boot and intro progress, not yet verified playable**. This is the
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
  a fixed scene; next candidates are per-vector temporary allocations and
  full framebuffer snapshots after individual streamed RDP commands.
- Audit combiner cycle selection, shade/depth interpolation anchors, and
  RSP output against a trusted reference. Avoid fake completion/solid-fill
  fallbacks or choosing arbitrary RAM as a framebuffer to disguise bugs.
- Reach controllable Mario and verify analog input, collisions, audio,
  saving, and sustained frame timing in the Linux UI before claiming playable.
- Other N64 games need regression testing; shared renderer changes are not
  a compatibility claim for Zelda/Mega Man/etc. Darius/Gauntlet core code was
  not changed by this milestone.
