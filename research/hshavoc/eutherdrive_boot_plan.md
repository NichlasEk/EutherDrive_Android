# HSHavoc EutherDrive Boot Plan

## Goal

Boot the Data East `hshavoc.zip` set inside EutherDrive first, using the
existing Mega Drive renderer and audio path as the proof harness. MAME remains
the final upstream target, but EutherDrive is the faster iteration loop for
startup, VDP writes, MMIO polling, and eventual first pixels.

No decrypted ROM image should be checked in. The adapter decodes the archive in
memory, writes only a temporary `.gen` file for the existing MD loader, then
deletes it after load.

## Current Adapter

`EutherDrive.Core.Arcade.DataEast.Hshavoc.HshavocAdapter` is a wrapper around
`MdTracerAdapter`.

Hardware model: treat HSHavoc as a Data East CG-2 arcade board built around the
Sega Genesis/Mega Drive base map, not as Sega System 16. The EutherDrive adapter
should therefore keep the MD VDP/Z80/audio path as the execution harness and add
only the arcade-specific layers that MAME identifies: even/odd program ROM
decode, extra `0x200000-0x2023ff` RAM behavior, JAMMA/DIP input mapping, and a
provisional PIC/startup response model.

It currently:

- Detects a supported archive by the MAME ROM names `d-25.11a` and `d-26.9a`.
- Interleaves the even/odd 68000 ROMs into a 1 MiB Mega Drive-style image.
- Applies the known MAME base decode:
  - main `0x000000-0x0e7fff` data bitswap/xor/typedat pass
  - tail `0x0e8000-0x0fffff` bitswap pass
  - initial vector/header xors
- Applies the current best startup probe patch at `$0c42-$0c9a`.
- Optionally applies the phase-2 operand adjustments when
  `EUTHERDRIVE_HSHAVOC_PHASE2=1`.
- Feeds the resulting temporary image into `MdTracerAdapter`.

Latest checkpoint:

- The startup patch still treats `$0d06d6` as a real subroutine. It reaches that
  block, returns through `$0d0766`, lands at the patched `$0cb2` continuation,
  and jumps into the next startup stub at `$1126`.
- `$1104` was rejected as a continuation because it lands inside the
  `4ef9 000d 0682` long jump operand. `$1126` is the next valid instruction
  boundary and reaches the main dispatch area at `$2c6c`.
- `$fff906` is now modeled centrally with `HshavocBoardBusOverride` instead of
  the old narrow proof patch at `$2a10`. Writes touching `$fff906/$fff907`
  clear that RAM word immediately, matching the observed board/PIC/interrupt
  acknowledgement gate well enough for boot probing.
- With that bus model, execution passes both known acknowledgement waits:
  `$2a16 -> $2a1e -> $1a16a -> $2a24` and `$0aa8 -> $0ab0 -> $1a16a`.
- Deeper runtime probes now reach `$2c6c`, `$2c8e`, `$2c9c`, `$1fe2`,
  `$39328`, `$9530`, `$9aa0`, and repeated `$1a16a` calls. The sampled `$123e`
  path is only a short delay routine (`move.w #$13,D6; dbf; rts`), not the next
  real blocker.
- The remaining visible failure is still black video: the frame buffer has no
  content and VDP display status remains off. Next pass should trace VDP
  register writes, especially display-enable register 1, and compare the
  `$a10005`/I/O reads used by the new runtime loop against expected board
  inputs.

VDP/IO checkpoint:

- `EUTHERDRIVE_HSHAVOC_TRACE_VDP=1` and `EUTHERDRIVE_HSHAVOC_TRACE_IO=1`
  trace HSHavoc-specific MD VDP port writes and `$a10000-$a10fff` reads from
  the board override without taking ownership of those accesses.
- The program does perform VDP work. Observed writes include early setup at
  `$10cc-$10f8`, `$0a1c`, `$0a7c-$0a94`, and runtime data/control writes
  around `$3988e-$398e0`.
- No natural `$81xx` VDP register-1 write has been observed yet, so the game
  never enables display through the normal MD register path.
- `EUTHERDRIVE_HSHAVOC_FORCE_DISPLAY=1` is a probe-only flag that writes
  `$8174` after load/frame execution. It proves display-enable alone is not the
  final blocker: `vdp_display` becomes `1`, but the framebuffer remains blank.
- A forced-display snapshot after 30 frames shows VRAM is not empty
  (`2208` nonzero bytes in the 64 KiB dump), while CRAM is entirely zero. The
  next concrete target is palette/CRAM initialization: either the protected
  startup has not reached the palette upload path, a board/PIC response gates
  that path, or an address/decode assumption is still wrong for the palette
  setup sequence.
- `EUTHERDRIVE_HSHAVOC_REPAIR_VDP_REG_PENDING=1` is a probe-only repair for the
  MD VDP command-port state. HSHavoc writes register words immediately after
  command words, and the current VDP core can otherwise consume `$8fxx/$90xx`
  style register writes as the second word of a pending control command. The
  repair clears pending command state before register writes; with it enabled,
  autoincrement becomes correct and VRAM nonzero growth increases from about
  `2208` bytes to `4-6 KiB` depending on frame count.
- `EUTHERDRIVE_HSHAVOC_FORCE_TEST_PALETTE=1` writes a synthetic 64-entry CRAM
  ramp after load/frame execution. With forced display + VDP register repair,
  this immediately produces visible nonblack framebuffer content
  (`57344` nonzero pixels at 80 frames). Therefore the MD renderer/name-table
  path is alive; the real missing piece is natural CRAM/palette upload.
- The new `$001e00-$002020` code dump shows a coherent runtime transfer block,
  not random data. It prepares VDP/DMA command words at `$ffeab0-$ffeabc`
  (`$9380/$9400`, `$95c0/$96ec`, `$977f`) and contains byte-copy/pack loops at
  `$1f00-$2010`. Current 45-frame probes reach `$1fdc/$1fe2`, but
  `$ffeab0-$ffeabc` remains zero and no natural CRAM writes occur. Snapshot RAM
  does show packed-looking data at `$ff0800`, while CRAM is all zero and VRAM is
  nonzero (`4122` bytes at 45 frames).
- `EUTHERDRIVE_HSHAVOC_TRACE_RAM_SKIP_ZERO=1` suppresses zero-fill noise in the
  focused RAM trace. With it enabled, `$ff0800-$ff0900` is confirmed as active
  runtime output from the `$1fd0-$209e` pack/unpack routine, not stale init
  memory. PC taps show the routine consuming packed streams from ROM via `A0`
  (`$2a28` early, then `$585bc-$5a029` around frames 19-24) and writing RAM
  through `A1`, with back-reference/copy state in `A2`.
- `EUTHERDRIVE_HSHAVOC_TRACE_VDP_FRAME_START` /
  `EUTHERDRIVE_HSHAVOC_TRACE_VDP_FRAME_END` narrow the VDP trace by frame. A
  frame-18 and frame-19+ pass shows heavy runtime VDP traffic from
  `$398b6/$398d0`, but still no natural CRAM writes; the snapshot CRAM remains
  64 zero words while VRAM contains roughly `4 KiB` of nonzero data. This makes
  the current blocker a palette/CRAM command path or board-gated palette flush,
  not a blank renderer or missing decompression output.
- Core VDP decode logging (`EUTHERDRIVE_TRACE_VDP_CTRL=1`) clarified that the
  late `...0003` longword control writes are VRAM writes, not CRAM writes: the
  first word contributes the low command bits and the second word contributes
  high address bits. Up to frame 23 the core decodes `1198` VRAM commands and
  `0` CRAM commands.
- Focused `$ffe800-$ffeac0` RAM tracing shows the game is building an arcade
  DMA queue. Frame 25 queue entries include source/target/length/active records
  such as source `$ffcc00`, command/destination `$c200`, length `$0080`, active
  `$0001`; the existing adapter was not flushing those entries into the MD VDP.
- `EUTHERDRIVE_HSHAVOC_FLUSH_DMA_QUEUE=1` is an opt-in proof bridge for that
  queue. It was useful as a false-color proof, but it is not the final model:
  the `$c200/$c400/...` raw words look palette-like in isolation, while the
  generated VDP command blocks decode them as VRAM destinations
  (`$4200,$0083 -> VRAM $c200`, etc.). With this bridge enabled the framebuffer
  becomes nonblack, but CRAM contains values derived from VRAM queue data and
  the image has incorrect colors.
- `EUTHERDRIVE_HSHAVOC_FLUSH_VDP_COMMAND_BLOCKS=1` is the current higher-value
  proof bridge. It scans `$ffe900-$ffea80` for the 14-byte blocks generated by
  the `$1ebc-$1f00` routine: VDP DMA registers `$93-$97` followed by two control
  words. Feeding those exact words into the MD VDP core starts real DMA from
  decoded ROM (`$000000-$0fffff`) or `$ffxxxx` RAM into VRAM. Guarding out
  zero-length, overlarge, and out-of-map sources avoids partially built/stale
  blocks.
- Command-block-only runs prove the VRAM queue path but still render black:
  300 frames with command-block flushing and CRAM tracing produce `0` CRAM
  writes, all-zero snapshot CRAM, and `fb_has_content=False`.
- Command-block flushing plus the legacy false-color CRAM bridge gives visible
  output: at 180 frames the framebuffer reaches `28613` nonzero pixels. This
  proves the renderer, display, VRAM DMA, and queue scheduler are alive; the
  remaining blocker is the real palette/CRAM source or the board-gated palette
  flush path.
- The first UI screenshots are therefore not random noise. They show coherent
  decoded art/tile data moving through the generated VDP DMA blocks and the MD
  renderer. The horizontal corruption/banding is still expected in proof mode:
  CRAM is seeded synthetically, display is forced, and the adapter is bridging
  queued DMA commands before the real arcade palette/board handshake is fully
  modeled.
- The latest proof bridge reflushes `$ffxxxx` RAM-sourced VDP command blocks
  when their payload changes, instead of deduping only by command/register
  words. That changed the 180-frame headless proof fingerprint to
  `0x2FFF8448C941C38C` with `57344` nonzero pixels, which confirms those reused
  command slots contain live frame data rather than stale setup records.
- Next target: trace the producer of palette RAM separately from the VRAM DMA
  queue. Search for CRAM-style generated command blocks (`codeLow=3`) beyond
  `$ffe900-$ffea80`, trace RAM writes around candidate palette buffers, and
  map callers in `$1e00-$2020`/`$39800-$39900` to determine whether palette
  upload is gated by a missing PIC/board response or by another decode island.
- A full `$ff0000-$ffffff` RAM scan for generated CRAM command blocks found no
  `codeLow=3` DMA candidates through 180 frames. Focused tracing of MAME's
  extra board RAM range `$200000-$2023ff` also showed no accesses through
  120 frames. The current failure is therefore still upstream of any visible
  CRAM queue: the decoded runtime is producing VRAM work, but it is not reaching
  the natural palette/upload state.
- Local MAME confirms this is the Genesis/Mega Drive-derived Data East CG-2
  board with a protected PIC16C55 and incomplete coin-op program decryption.
  The driver maps ROM at `$000000-$1fffff`, board RAM at `$200000-$2023ff`,
  and currently nops writes to `$200000-$201fff` in `init_hshavoc()`.
- Comparing the decoded arcade image against the home `High Seas Havoc (U) [!]`
  ROM gave several exact anchors: the arcade tail `$0e8000-$100000` is
  byte-identical to the home ROM tail, arcade `$001e00` matches home `$0018da`,
  arcade `$039800` matches home `$03c6d6`, home CRAM clear/upload `$000ed8`
  matches arcade `$0013fe`, and home VDP-list startup `$0010f8` matches arcade
  `$00161e`.
- PC taps show the current arcade runtime reaches `$001fd8`, `$03988e`, and
  `$0398e0`, but not `$0013f6/$0013fe/$00161e`. The experimental
  `EUTHERDRIVE_HSHAVOC_DECODE_PROFILE=initmirror` profile patches calls to
  `$0013f6` and `$00161e` into the current startup bridge. It proves those
  mirrored routines are executable VDP setup code, but it is not a working boot
  fix: the routine is entered without the correct startup context, repeatedly
  hits illegal opcode handling via PC `$000000`, leaves CRAM untouched, and
  renders black after 120 frames.
- Follow-up probes split that failure apart. `$13fe`, `$160e`, and `$161e` are
  useful anchors but not safe standalone subroutine entries: they return through
  the shared `movem` restore at `$19ac` and corrupt the caller stack if entered
  without the prologue. The stack-correct mirrored VDP dispatcher entry is
  `$1332`.
- `EUTHERDRIVE_HSHAVOC_DECODE_PROFILE=initdispatcher` patches the startup bridge
  to call `$1332`. With no RAM seed it no longer crashes and it reaches the
  real dispatcher restore/RTS path, but `$fffe00` is zero, so the dispatcher
  emits VDP commands without the expected register-1/DMA-enable state and still
  renders black.
- `EUTHERDRIVE_HSHAVOC_RAM_SEED_WORDS=0xfffe00:0x8164` is a narrow, opt-in RAM
  context probe. Combined with `initdispatcher`, it enables the same VDP
  register-1 baseline observed in the home ROM path: DMA requests decode with
  `dmaEn=1`, Z80 startup becomes active, display status reaches `vdp=1`, and
  natural CRAM write callbacks fire from `$13fe`. The CRAM payload is still all
  zero, so the next blocker is the palette payload/producer or its board/PIC
  gate, not the dispatcher entry or the MD renderer.
- DMA source tracing shows the natural CRAM upload reads 64 words from
  `$fff700-$fff77f` via regs `9340,9400,9580,96fb,977f` and command
  `c000,0080`. The first `$1332` dispatcher call at frame 3 is too early: the
  buffer is still zero. The home ROM control path uses the matching dispatcher
  at `$0e0c` and later writes fade palette words from `$008ac2`; the decoded
  arcade image contains the byte-identical routine at `$009aac`.
- Arcade runtime reaches `$009aac` and writes nonzero words to `$fff700` after
  the early dispatcher call. `EUTHERDRIVE_HSHAVOC_FLUSH_STATIC_PALETTE_PLAN=1`
  is an opt-in proof bridge that hashes `$fff700`, skips the all-zero state, and
  replays the fixed CRAM DMA when the palette buffer changes. With
  `initdispatcher`, `EUTHERDRIVE_HSHAVOC_RAM_SEED_WORDS=0xfffe00:0x8164`,
  VDP command-block flushing, display forcing, and the static palette bridge,
  a 90-frame headless run reaches `fb_has_content=True` with 28,695 nonzero
  pixels. That means the next real target is the missing main-loop dispatcher
  call/gate, not palette data generation.
- The startup bridge now preserves the home-style `andi.w #$f8ff,SR` tail at
  `$0cb2` before jumping to `$1126`. This drops the 68000 interrupt mask after
  protected startup. VINT is then accepted on vector `$0078` and enters the
  arcade handler at `$0ab8`.
- The VBlank handler reads the board/PIC gate at `$fff906` from PC `$000ac2`
  before it reaches `$0ae8 -> $1332 -> $13fe`. A broad frame-start RAM latch
  was useful as a probe, but the current model is narrower:
  `HshavocBoardBusOverride` returns `1` only for that VBlank gate read by
  default. It can be disabled with
  `EUTHERDRIVE_HSHAVOC_FORCE_VBLANK_GATE_READ=0`; the older
  `EUTHERDRIVE_HSHAVOC_LATCH_VBLANK_GATE=1` remains opt-in.
- With the PC-specific VBlank gate read, `$0ae8`, `$1332`, and `$13fe` continue
  after `$009aac` starts filling `$fff700-$fff77f`. CRAM tracing confirms real
  nonzero palette uploads from `$13fe` beginning around frame 26, with values
  matching the `$fff700` fade/palette producer. A pure headless run is still
  black because the real VRAM command-block scheduler is not yet fully modeled;
  the palette path is no longer the primary blocker.

The UI routes `hshavoc.zip` to this adapter before generic arcade archive
fallbacks.

As of the UI visibility pass, normal UI launches automatically enable a
probe-only proof mode unless `EUTHERDRIVE_HSHAVOC_UI_PROOF_MODE=0` is set. That
mode combines VDP register-pending repair, generated VDP command-block flushing,
forced display, low-pattern RAM replay, and a palette fallback. It is
intentionally not claimed as correct emulation: it makes the UI render the
decoded/queued VRAM path while the protected VDP queue timing is still being
mapped. Headless runs with `EUTHERDRIVE_HEADLESS_CORE` keep the older opt-in
behavior for controlled experiments.

The proof palette path now prefers the real `$fff700` palette buffer. A
synthetic CRAM ramp is used only before that producer becomes nonzero; once the
static palette bridge has replayed real palette data, UI-proof stops refreshing
the synthetic ramp so it cannot overwrite the runtime palette. The synthetic
fallback can be disabled explicitly with
`EUTHERDRIVE_HSHAVOC_DISABLE_TEST_PALETTE=1`.

## Phase 1: Startup Probe

Run with no phase-2 adjustments.

Expected proof points:

- The generated temporary image passes Mega Drive vector validation.
- The 68000 reaches the patched startup at `$0c42`.
- VDP register setup from `$0a1c` writes to `$00c00004`.
- `$10ba-$10c0` is reached and performs VDP-control/MMIO setup or polling.
- If execution reaches `$101c`, `$0f26`, `$0f2e`, `$1026`, `$102e`, `$1030`,
  or `$103a`, treat that as token/state-machine entry, not linear code.

Failure classification:

- Reaches `$00f8` before video setup: abs.w startup call is a blocker.
- Reaches `$0d34` before video setup: pointer/table blocker.
- Loops around `$10c0`: MMIO/PIC response model is missing.
- Falls into token entries and stops: table consumer/interpreter must be found.

## Phase 2: Operand Adjustment Probe

Enable:

```sh
EUTHERDRIVE_HSHAVOC_PHASE2=1
```

This applies:

- `$0c7a -> $0e32`
- `$0c86 -> $0ab8`
- `$0c8c -> $0af8`
- `$0c92 -> $0d32`

Expected extra proof points:

- `$0af8-$0b14` should show MMIO writes and a direct VDP control-port write at
  `$0b0e`.
- `$0ab8/$0abc` should behave more like a real prologue/MMIO polling path than
  `$0aba`.
- `$0d32` should act as the immediate-RTS escape if `$0d34` is data/table
  material.

## Phase 3: EutherDrive Instrumentation

Add EutherDrive-side logging only after Phase 1/2 loads through the adapter.

Target logs:

- 68000 PC hits for `$0c42`, `$0a1c`, `$10ba`, `$10c0`, `$0af8`, `$0b0e`,
  `$00f8`, `$0d34`, and the token entries.
- VDP control/data writes at `$00c00000-$00c0001f`.
- MMIO reads/writes in `$00ff0000-$00ffffff`, especially `$00fffe00`,
  `$00ffff86/$87`, and `$00fff900-$00fff916`.

Decision gates:

- VDP writes seen: renderer path can be inspected for first tile/sprite state.
- MMIO loop seen: emulate or stub the missing PIC/board register response.
- Token entries seen: build a token-consumer tracer instead of forcing these
  bytes through the 68000 disassembler.
- No VDP writes and blocker hit: return to startup operand/decode model.

## Phase 4: Port Back

Once the EutherDrive probe proves the minimum boot path, port only the proven
parts back to MAME:

- Base decode remains tied to PEEL 4B/5B evidence.
- Startup patch must be replaced by a hardware-shaped rule where possible.
- Any temporary PIC/MMIO response must be documented as provisional.
- Token/state-table handling needs explicit comments so the driver does not
pretend these regions are ordinary 68000 code.

## 2026-05-09 Low Pattern DMA Checkpoint

The black-screen blocker moved from palette/CRAM to pattern VRAM. With
`initdispatcher`, the VBlank gate seed at `$fffe00:$8164`, and the precise
`$fff906` gate read, CRAM is live and display is on, but the final snapshot only
has graphics in high VRAM pages (`$b000-$efff`). The first visible nametable
entries reference low tile indices such as `$127` and `$246`, whose pattern
data remains zero.

The broad RAM VDP command-block scan found only one generated block at
`$ffe91a`, targeting `$d800` during early frames and `$b000` at frame 23. No RAM
command block naturally targets `$0000-$7fff`. A home-ROM comparison showed the
matching retail path later DMAing `$ff0000` to VRAM `$0000`, so the arcade path
does build a familiar decompressed buffer but currently never replays the
matching low-pattern copy in EutherDrive.

New probe flags:

- `EUTHERDRIVE_HSHAVOC_FLUSH_LOW_PATTERN_RAM_PROBE=1` replays the observed
  `$ff0000` RAM buffer to VRAM `$0000`.
- `EUTHERDRIVE_HSHAVOC_LOW_PATTERN_RAM_PROBE_WORDS=0xNNNN` controls that replay
  length in VDP words. The original probe used `0x0800`; the stronger proven
  UI-proof length is `0x2000`.
- `EUTHERDRIVE_HSHAVOC_FLUSH_LOW_PATTERN_RAM_PROBE_MIRROR_PAGES=1` additionally
  tries `$2000/$4000/$6000` as opt-in evidence gathering for tile-index paging.
- `EUTHERDRIVE_HSHAVOC_FLUSH_LOW_PATTERN_RAM_PROBE_EVERY_FRAME=1` bypasses the
  hash gate for this runtime-only proof.
- UI-proof mode now enables the low-pattern replay automatically, repeats it
  each frame, and defaults the length to `0x2000` words. Headless experiments
  should keep using `EUTHERDRIVE_HSHAVOC_UI_PROOF_MODE=0` when measuring the
  natural boot path.

Important result: the replay must open a deterministic VDP register-1 DMA
window (`$8174`) because the frame-level adapter runs outside the game's own
DMA-enable timing. The control latch can also be mid-command, so the probe sends
the register-1 transition twice before issuing the DMA block.

Verified headless results:

- Without the low-pattern probe: final 90-frame snapshot is effectively black
  (`570` nonzero pixels) and `$0000-$4fff` pattern VRAM is empty except two
  bytes.
- With `EUTHERDRIVE_HSHAVOC_FLUSH_LOW_PATTERN_RAM_PROBE=1`: final 90-frame
  snapshot rises to `13811` nonzero pixels, and `$0000-$0fff` contains `2104`
  nonzero bytes. This proves the missing low-pattern DMA edge is real.
- After rebuilding the core with the configurable VDP length wired into
  registers 19/20, `EUTHERDRIVE_HSHAVOC_LOW_PATTERN_RAM_PROBE_WORDS=0x2000`
  and repeat-every-frame raises the final 90-frame snapshot to `20977` nonzero
  pixels. The final VRAM pages now mirror the decompressed RAM buffer:
  `$0000:2104`, `$1000:2972`, `$2000:4029`, `$3000:2144`.
- Mirroring the same `0x2000`-word buffer into `$2000/$4000/$6000` does not
  improve the final screen (`20932` nonzero pixels), so higher pattern-bank
  mirroring is not the main missing edge. A real fix should capture the
  protected VDP queue or reconstruct the exact per-page DMA schedule while
  `$ff0000` is still valid, not permanently mirror the same buffer.

## 2026-05-09 Palette Repeat / Active VDP Checkpoint

The latest black-frame regression was not random video data and not a new ROM
decode failure. A final 120-frame snapshot showed CRAM had been cleared back to
64 zero words while VRAM still held decoded pattern/table data. That made the
framebuffer black even though the proof bridge had already found the low-pattern
DMA edge.

UI-proof mode now seeds the synthetic CRAM ramp before frame execution and
refreshes it every frame. Headless verification with default startup profile and
`EUTHERDRIVE_HSHAVOC_UI_PROOF_MODE=1` reaches:

- frame 5: `51712` nonzero pixels
- frame 59: `54615` nonzero pixels
- frame 119/final: `54683` nonzero pixels, first nonzero at `(0,0)`

That restores the intended UI proof: the renderer is visibly receiving coherent
VDP state while the real palette/board handshake remains under investigation.
This is still not claimed as correct emulation; it is a controlled bring-up
mode that prevents CRAM zeroing from hiding the VRAM/VDP progress.

New opt-in register probes:

- `EUTHERDRIVE_HSHAVOC_FORCE_PLANE_A_BASE=0xNNNN`
- `EUTHERDRIVE_HSHAVOC_FORCE_PLANE_B_BASE=0xNNNN`
- `EUTHERDRIVE_HSHAVOC_TRACE_FORCE_PLANE_BASES=1`

These force MD Plane A/B nametable base registers before and after each frame.
Testing likely high-table candidates (`$c000/$e000`, `$e000/$c000`,
`$c000/$c000`, `$e000/$e000`, `$6000/$7000`, and mixed low/high pairs) did not
change the black result before palette repeat. Therefore the immediate UI black
failure was CRAM/palette visibility, not simply the active Plane A/B base.

Next step: keep the UI proof palette fallback in place for visibility, but
return the real investigation to the natural CRAM producer and VDP command
queue: trace why the arcade path needs the adapter to replay the nonzero
`$fff700` palette data, and model the protected queue timing closely enough that
the bridge can be removed.

## 2026-05-09 Real Palette Framebuffer Checkpoint

A controlled 120-frame headless run with UI-proof disabled, no synthetic test
palette, forced display, VDP register-pending repair, generated VDP command
block flushing, low-pattern RAM replay, and the `$fff700` static palette bridge
now reaches a visible framebuffer:

- frame 59: `20615` nonzero pixels
- frame 119/final: `20615` nonzero pixels, first nonzero at `(0,0)`
- final fingerprint: `0x479ED41C9943AFB6`

The key command set was:

```sh
EUTHERDRIVE_HEADLESS_CORE=hshavoc \
EUTHERDRIVE_HSHAVOC_UI_PROOF_MODE=0 \
EUTHERDRIVE_HSHAVOC_FORCE_DISPLAY=1 \
EUTHERDRIVE_HSHAVOC_REPAIR_VDP_REG_PENDING=1 \
EUTHERDRIVE_HSHAVOC_FLUSH_VDP_COMMAND_BLOCKS=1 \
EUTHERDRIVE_HSHAVOC_FLUSH_LOW_PATTERN_RAM_PROBE=1 \
EUTHERDRIVE_HSHAVOC_FLUSH_LOW_PATTERN_RAM_PROBE_EVERY_FRAME=1 \
EUTHERDRIVE_HSHAVOC_LOW_PATTERN_RAM_PROBE_WORDS=0x2000 \
EUTHERDRIVE_HSHAVOC_FLUSH_STATIC_PALETTE_PLAN=1 \
dotnet run --project EutherDrive.Headless/EutherDrive.Headless.csproj --no-build -- \
  /home/nichlas/roms/MAME/DataEast/hshavoc/hshavoc.zip 120
```

This is the first stronger framebuffer proof because the colors come from the
arcade runtime's `$fff700-$fff77f` palette buffer rather than from the synthetic
CRAM ramp. The bridge still replays that palette into the MD VDP from adapter
code, so the remaining target is not color generation; it is the protected board
queue/timing edge that should naturally issue the same CRAM DMA at the right
time.

New palette controls:

- `EUTHERDRIVE_HSHAVOC_FLUSH_STATIC_PALETTE_PLAN_EVERY_FRAME=1` repeats the
  `$fff700` CRAM replay even when the palette hash is unchanged.
- UI-proof mode enables the real static palette bridge and repeat automatically.
  This lets the UI start with synthetic fallback pixels, then switch to the
  real palette once `$fff700` becomes nonzero.
- `EUTHERDRIVE_HSHAVOC_DISABLE_TEST_PALETTE=1` disables the synthetic fallback,
  useful when verifying that any visible framebuffer is using only runtime
  palette data.
