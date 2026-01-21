# Special Stage Debug Findings

## Goal
Fix Sonic 2 Special Stage white/garbled rendering in headless (slot1).

## Repro (headless)
- `EUTHERDRIVE_LOAD_SLOT1_ON_BOOT=1 dotnet run --project EutherDrive.Headless ~/roms/sonic2.md 300 > /tmp/ed_run.log 2>&1`
- Optional: `EUTHERDRIVE_DEBUG_PALBUF=1`, `EUTHERDRIVE_DEBUG_DMA=1`, `EUTHERDRIVE_DEBUG_SH=1`

## Current status
- WRAM mirroring fixed (0xE00000–0xFFFFFF mirrors to 64 KiB WRAM).
- DMA reads go through the real bus, not flat `g_memory`.
- Palette buffer writes are visible at 0xFFFB00–0xFFFB7F and logged.
- S/H rendering logic no longer uses priority as shadow state.
- H40 detection now uses `IsH40Mode()` consistently.

## Key evidence
- Frames ~4906–4907: scanline y=112 goes to pure backdrop.
  - Example: `[SRC] frame=4906 y=112 BD=256 A=0 B=0 S=0 W=0`
  - Name table entries are zero for both planes during those frames.
- DMA logs show VRAM clears at frame 4906:
  - `DMA-FILL dest=0x8000 len=0x1FFF`
  - `DMA-FILL dest=0xC000 len=0x1FFF`
  - `DMA-FILL dest=0xA000 len=0x1FFF`
- Palette DMA copies track WRAM palette buffer:
  - `[DMA-PAL-SRC] frame=4906 ... w0=0x0EEE w1=0x0EEE ...`
- 68K writes to the palette buffer (PC=0x0025D6) ramp toward `0x0EEE`
  and stop around frame 4905 in logs.

## Remaining problem
- Palette buffer never restores after the fade-to-white, so CRAM remains
  white and the framebuffer stays white/gray despite valid DMA and S/H logs.
- Plane data reappears after the VRAM clears, but colors remain white/gray.

## Recent fixes applied
- `EutherDrive.Core/MegaDriveBus.cs`:
  - WRAM mirroring for 0xE00000–0xFFFFFF to WRAM.
  - WRAM writes mirrored into `md_m68k.g_memory` so CPU/bus views match.
- `EutherDrive.Core/MdTracerCore/md_vdp_dma.cs`:
  - DMA source address masked to 24-bit even boundary.
- `EutherDrive.Core/MdTracerCore/md_vdp_memory.cs`:
  - VDP data port 8-bit writes use UDS/LDS properly.
  - VDP read path masks off DMA flag in `g_vdp_reg_code`.
- `EutherDrive.Core/MdTracerCore/md_vdp_regster.cs`:
  - H32/H40 masks for reg2/reg4; recompute on H-mode change.

## Useful grep commands
- `rg "\\[PALBUF-W\\] frame=490[0-9]" /tmp/ed_run.log | head`
- `rg "\\[DMA-PAL-SRC\\] frame=490[4-9]" /tmp/ed_run.log | head`
- `rg "\\[DMA-FILL\\] frame=4906" /tmp/ed_run.log | head`
- `rg "\\[SRC\\] frame=490[4-9]" /tmp/ed_run.log | head`
- `rg "\\[NT-A\\]|\\[NT-B\\]" /tmp/ed_run.log | head -n 120`

## Next tracing ideas
1) Verify why palette buffer stops updating after frame 4905.
2) Trace VDP status reads and H/V interrupt behavior around 4906–4909.
3) Compare with `/home/nichlas/clownmdemu-core/` for DMA source interpretation.
