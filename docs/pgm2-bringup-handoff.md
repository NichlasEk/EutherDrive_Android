# PGM2 Bringup Handoff

Status: PGM2 `kov2nl` overlay/HUD/legal-screen duplication fixed locally; broader bringup still in progress.

## Current User-Facing Symptom

- `kov2nl` boots and gameplay runs.
- Legal screen and HUD/overlay no longer duplicate the left text section into the right side.
- The root cause was in the native FG renderer: the 96-column text tilemap used `& (TxColumns - 1)` for the X tile index. Since 96 is not a power of two, this wrapped column 32 back to 0. The fix is to use the direct `srcX >> 3` tile column after `srcX` has already been wrapped to the 96-tile map width.

## Current Relevant File

- `EutherDrive.Core/Arcade/Igs/Pgm2Adapter.cs`

There are unrelated dirty files in the worktree. Do not revert them:

- `EutherDrive.Core/Arcade/Konami/TmntAdapter.cs`
- `EutherDrive.Core/Arcade/Vegas/GauntletDarkLegacyAdapter.cs`
- `Ryu64/...`
- `Third_party/MCS/...`
- `docs/sega32x-sh2-dynarec-plan.md`

## Existing Commit

Already committed earlier:

- `85156ae Fix PGM2 shared RAM and frame presentation`

Current PGM2 work is uncommitted.

## What Changed In PGM2 So Far

Useful/likely keep:

- Savestate version bumped from `5` to `6`.
- PGM2 memory card `.pg2` HLE added:
  - Loads default card data.
  - Does not insert cards by default, matching MAME behavior.
  - Opt-in insert: `EUTHERDRIVE_PGM2_INSERT_DEFAULT_CARDS=1`.
  - Implements MCU card commands `C0-C9`.
- Debug instrumentation added:
  - `EUTHERDRIVE_PGM2_TRACE_HUD_WRITES=1`
  - `EUTHERDRIVE_PGM2_TRACE_HUD_START_FRAME=<frame>`
  - `EUTHERDRIVE_PGM2_TRACE_GPU_READS=1`
  - `EUTHERDRIVE_PGM2_TRACE_GPU_WRITES=1`
  - `EUTHERDRIVE_PGM2_DUMP_DECRYPTED_ROM=/tmp/kov2nl_decrypted.bin`

Experimental/not a real fix:

- `EUTHERDRIVE_PGM2_OVERLAY_CLIP_WIDTH`
  - Added as a controlled experiment.
  - Default is now off (`0`).
  - Earlier default clip of 256 removed bad-looking duplicate overlay but also removed valid graphics, so do not treat clipping as the fix.
- `EUTHERDRIVE_PGM2_TEXT_LAYER_WIDTH`
  - Added as a controlled experiment for text/FG source scaling.
  - Tried 256/320/512 against legal screen.
  - User observed none match the real expected behavior.

Actual legal/HUD fix:

- In `DrawTextTilemap`, changed:
  - old: `int tileX = (srcX >> 3) & (TxColumns - 1);`
  - new: `int tileX = srcX >> 3;`
- `srcX` is already modulo `TxColumns * TileSize`, so this yields columns `0..95`.

## MAME Source Facts Checked

From MAME `pgm2_v.cpp` / `pgm2.cpp`:

- Video mode register:
  - `0x3012000c-0x3012000f`, mode is upper halfword.
  - mode `0` = `320x240`
  - mode `1` = `448x224`
  - mode `2` = `512x240`
- For `kov2nl`, our register trace shows mode `1`, so visible width `448` is correct.
- MAME FG tilemap:
  - `96 x 64` tiles
  - `8 x 8` tile size
  - `TILEMAP_SCAN_ROWS`
  - transparent pen `0`
  - drawn directly over visible area.
- MAME BG tilemap:
  - `64 x 32` tiles
  - `32 x 32` tile size
  - row scroll support.
- MAME draws:
  - sprite priority 1
  - BG
  - sprite priority 0
  - FG

Important implication:

- MAME source does not obviously show two separate FG tilemaps. If MAME output is correct, either:
  - the game state/VRAM produced by our emulation differs from MAME, or
  - some subtle tilemap/clip/scroll interpretation is missing, not simply a global text stretch.

## Findings From Traces

GPU reads:

- `EUTHERDRIVE_PGM2_TRACE_GPU_READS=1` showed no GPU register reads in the tested cold-boot window.
- So readback of write-only GPU regs is likely not the root.

GPU writes:

- At frame 0, game writes `0x3012000e = 0x01`, so mode `1` / `448x224`.
- This matches MAME’s mode handling.

HUD/FG writes:

- HUD duplicate appears already in VRAM/sprite RAM, not produced by UI presenter.
- Write traces showed:
  - Sprite RAM HUD-ish writes are from internal ROM memcpy-like loop around `0x00000A54`.
  - FG right/bottom text writes are from decrypted program ROM text draw routine around `0x100204AA/0x100204C0/0x100204D2`.
  - Caller LR observed around `0x10032BF1`.
- Decrypted ROM disasm indicates that text draw routine is generic and receives layout/slot data; probably not the root itself.

Savestate:

- User’s slot 1 loads fine for them.
- Headless savestate load sometimes crashes after advancing frames, but the pre-rendered “before” image is enough for visual analysis.
- With the clip experiment off, savestate output still shows the duplicated overlay/HUD issue.

## Commands That Were Useful

Build:

```sh
dotnet build EutherDrive.Headless/EutherDrive.Headless.csproj -c Release --no-restore -v:q
```

Cold boot legal dump:

```sh
EUTHERDRIVE_HEADLESS_CORE=pgm2 \
EUTHERDRIVE_HEADLESS_DUMP_DIR=/tmp/pgm2_current_legal \
EUTHERDRIVE_PGM2_HEADLESS_DUMP_FRAMES=119 \
EUTHERDRIVE_PGM2_DUMP_LAYERS=1 \
dotnet run --project EutherDrive.Headless/EutherDrive.Headless.csproj -c Release --no-build -- \
  /home/nichlas/roms/MAME/PGM2/kov2nl.zip 120
```

Savestate visual dump:

```sh
EUTHERDRIVE_HEADLESS_CORE=pgm2 \
EUTHERDRIVE_HEADLESS_DUMP_DIR=/tmp/pgm2_state_nomask \
EUTHERDRIVE_PGM2_DUMP_LAYERS=1 \
dotnet run --project EutherDrive.Headless/EutherDrive.Headless.csproj -c Release --no-build -- \
  --load-savestate /home/nichlas/roms/MAME/PGM2/kov2nl.zip \
  /home/nichlas/roms/MAME/PGM2/kov2nl.zip_3765262c.euthstate 0
```

GPU write trace:

```sh
EUTHERDRIVE_HEADLESS_CORE=pgm2 \
EUTHERDRIVE_PGM2_TRACE_GPU_WRITES=1 \
dotnet run --project EutherDrive.Headless/EutherDrive.Headless.csproj -c Release --no-build -- \
  /home/nichlas/roms/MAME/PGM2/kov2nl.zip 3
```

## MAME Comparison Status

- A local MAME package was downloaded and extracted to `/tmp/mamepkg`.
- Wrapper: `/tmp/eutherdrive-mame`
- Version output: `0.287 (mame0287-dirty)`
- ROM check:
  - `/tmp/eutherdrive-mame -rompath /home/nichlas/roms/MAME/PGM2 -verifyroms kov2nl`
  - result: `romset kov2nl is good`
- Headless MAME legal-screen capture:
  - `/tmp/kov2nl_3s.mng`
  - extracted references: `/tmp/kov2nl_mame_f119.png`, `/tmp/kov2nl_mame_f177.png`
- Fixed EutherDrive-vs-MAME comparison:
  - `/tmp/pgm2_fg96_fix_vs_mame.png`
- Gameplay savestate verification:
  - `/tmp/pgm2_state_fg96_fix/before.png`

## Strong Next Steps

1. Compare remaining visual differences against MAME beyond FG wrapping.
   - Legal screen now matches position/content.
   - Gameplay HUD no longer wraps.

2. Compare deeper VRAM state against MAME if other rendering issues appear.
   - Need MAME debugger/save-state or instrumented source if possible.
   - Check `fg_videoram`, `sp_videoram`, GPU regs at legal screen and gameplay HUD.

3. Continue broader PGM2 bringup.
   - Remaining likely areas: sprite accuracy, BG row scroll, sound, MCU/card edge cases, timing.

4. Do not keep global overlay clipping as a default fix.
   - It is useful only as a visual diagnostic.
   - It removes wrong graphics but also hides valid right-side content.

5. Keep text-layer-width env as diagnostic only until proven.
   - User says 256/320/512 variants are not correct.

## Current Best Hypothesis

The legal/HUD duplication was a renderer indexing bug, not missing game state. The game wrote correct 96-column FG tilemap data; EutherDrive rendered columns 32+ through a bitmask that only works for power-of-two widths.
