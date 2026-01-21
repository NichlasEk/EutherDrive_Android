# Special Stage Debug Summary

## Context
Goal: fix Sonic 2 Special Stage rendering (should be colorful, not white/garbled) in headless + savestate slot 1.

## How headless runs were executed
- Base run:
  - `EUTHERDRIVE_LOAD_SLOT1_ON_BOOT=1 dotnet run --project EutherDrive.Headless ~/roms/sonic2.md 200 > /tmp/ed_run.log 2>&1`
- Debug run:
  - `EUTHERDRIVE_DEBUG_DMA=1 EUTHERDRIVE_TRACE_CRAM=1 EUTHERDRIVE_DEBUG_SH=1 EUTHERDRIVE_DEBUG_VDP=1 EUTHERDRIVE_FB_TRACE=1 EUTHERDRIVE_LOAD_SLOT1_ON_BOOT=1 dotnet run --project EutherDrive.Headless ~/roms/sonic2.md 600 > /tmp/ed_run.log 2>&1`
- Manual savestate load:
  - `dotnet run --project EutherDrive.Headless -- --load-savestate ~/roms/sonic2.md /home/nichlas/EutherDrive/savestates/Sonic_The_Hedgehog_2__W___REVSC02_568e5e95.euthstate 50 > /tmp/ed_run.log 2>&1`

Logs: `/tmp/ed_run.log` and (some DMA/WRAM debug) `rom_start.log`.

## Key observations
1) DMA source and CRAM are now healthy
- `DMA-COPY len=64 src=0xFFFB00 regCode=0x23` shows proper WRAM source.
- `DMA-SRC` shows varied palette words (not all 0x0EEE).
- `[CRAM48-63]` shows varied values (not white-only).

2) S/H mode appears active and producing non-zero color indices
- `[S/H-OUT]` logs show non-zero `orig`/`final` indices and valid colors.
- `[S/H-MODE]` and `[VDP-DISPLAY]` show display off with S/H on (expected behavior in special stage).

3) Framebuffer output still goes white
- `VDP summary` logs show base color = `0xFFFFFFFF` (solid white), diff=0 at times.
- This indicates something in render output gets flattened to white despite valid CRAM and S/H computation.

4) WRAM palette writes are real and frequent
- `[WRAM-PAL-W16]` shows many non-0x0EEE values around frames 4885–4900.
- The WRAM palette buffer is clearly getting updated by the 68K.

## Root cause candidate
The VDP data-port read path used the full `g_vdp_reg_code` without masking off the DMA flag (bit 0x20).
This can cause VRAM reads to go to the default case (0xFFFF), which can corrupt internal state and lead
to the framebuffer being filled with white even though CRAM/S/H are correct.

## Changes applied in this session
1) Fix VDP data-port read target selection
- File: `EutherDrive.Core/MdTracerCore/md_vdp_memory.cs`
- Change: mask the DMA flag off in read selector.
  - `int code = g_vdp_reg_code & 0x0f; switch (code)`

2) Add frame context to WRAM palette write logs
- File: `EutherDrive.Core/MdTracerCore/md_m68k_memory.cs`
- Change: prefix `[WRAM-PAL-W8/W16/W32]` with `frame=...` for better correlation with DMA.

## What the fix did / did not do
- It did NOT instantly fix the white output in headless.
- It is still a valid correctness fix: VDP reads now ignore the DMA flag.
- The headless log still shows some frames with base color white + diff=0.

## Savestate notes
Two .euthstate files exist for Sonic 2 with same hash prefix 568e5e95:
- `/home/nichlas/EutherDrive/savestates/sonic2_568e5e95.euthstate` (name: `sonic2`, slot1 frame=4871)
- `/home/nichlas/EutherDrive/savestates/Sonic_The_Hedgehog_2__W___REVSC02_568e5e95.euthstate` (name: `Sonic The Hedgehog 2 (W) (REVSC02)`, slot1 frame=1375)

Headless auto-load uses `RomIdentity.Name` from the ROM filename (`sonic2`),
so slot1 auto-load goes to `sonic2_568e5e95.euthstate`.

## Most useful log cues
- `rg "\[DMA-COPY\]" /tmp/ed_run.log | head`
- `rg "\[DMA-SRC\]" /tmp/ed_run.log | head`
- `rg "\[CRAM48-63\]" /tmp/ed_run.log | head`
- `rg "\[S/H-OUT\]" /tmp/ed_run.log | head`
- `rg "VDP summary" /tmp/ed_run.log | head`

## Next debugging focus (if needed)
1) Why `VDP summary` shows base `0xFFFFFFFF` (full white) with diff=0 while CRAM/S/H are OK.
2) Whether any VDP read behavior or buffer selection still collapses to white during special stage.
3) Validate that `g_game_screen` is not overwritten by display-off logic (even with S/H on).

