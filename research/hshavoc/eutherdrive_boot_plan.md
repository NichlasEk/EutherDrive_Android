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

The UI routes `hshavoc.zip` to this adapter before generic arcade archive
fallbacks.

As of the UI visibility pass, normal UI launches automatically enable a
probe-only proof mode unless `EUTHERDRIVE_HSHAVOC_UI_PROOF_MODE=0` is set. That
mode combines VDP register-pending repair, generated VDP command-block flushing,
forced display, and a synthetic CRAM ramp. It is intentionally not claimed as
correct emulation: it makes the UI render the decoded/queued VRAM path while the
real palette/CRAM producer is still being mapped. Headless runs with
`EUTHERDRIVE_HEADLESS_CORE` keep the older opt-in behavior for controlled
experiments.

The synthetic palette is now seeded once per load/reset instead of being
rewritten every frame, and each CRAM/register probe clears pending VDP command
state first. That keeps the proof mode from masking later natural palette writes
while still making UI bring-up visible.

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
