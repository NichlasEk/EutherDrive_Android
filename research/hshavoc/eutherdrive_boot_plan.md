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

The UI routes `hshavoc.zip` to this adapter before generic arcade archive
fallbacks.

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
