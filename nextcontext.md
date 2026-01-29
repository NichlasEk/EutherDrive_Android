Next Context Summary (EutherDrive)

Goal
- Fix heavy graphics corruption in Madou (~/roms/madou.md). Name tables look sane but rendered output is corrupted; suspect scroll/VRAM pattern issues.

Recent Code Changes
- DMA source addressing now matches hardware 128KiB wrap behavior: source low word wraps while high stays fixed (matches otheremumdemu). File: `EutherDrive.Core/MdTracerCore/md_vdp_dma.cs`.
- HScroll table indexing updated to use per-mode masks (mode 1/2/3) instead of treating mode 1 as full-screen. File: `EutherDrive.Core/MdTracerCore/md_vdp_renderer_snap.cs`.
- HScroll values now treated as signed 10-bit with safe modulo wrap (negative scroll). File: `EutherDrive.Core/MdTracerCore/md_vdp_renderer_snap.cs`.
- Added reg14 tile rebase plumbing (no 128k VRAM yet); TileRebaseKind is internal; direct VRAM read masks to 64KB. Files: `md_vdp_regster.cs`, `md_vdp.cs`, `md_vdp_renderer_line.cs`.
- VRAM byte write parity now respects address parity (odd address writes go to addr ^ 1). File: `md_vdp_memory.cs`.
- New env vars documented in `Enviroment.txt` + `GATED_ENV_VARS.md`: `EUTHERDRIVE_TRACE_DMA_SRC`, `EUTHERDRIVE_TRACE_DMA_SRC_LIMIT`.
- TODO updated: implement 128k VRAM mode (reg1 bit7) and reg14 rebase end-to-end. File: `TODO.md`.

Key Observations (Madou)
- Regs at corruption frame: reg02=0x30 (Plane A base 0xC000), reg04=0x07 (Plane B base 0xE000), reg10=0x11 (64x64 plane), reg0B=0x00 (full-screen HScroll), reg0D=0x2E (HScroll base 0xB800). Dump in `madou_ntdump.log`.
- Name table rows (A/B) contain reasonable data (not all zero/FFFF). Example in `madou_ntdump.log` shows sane tile indices and cache matches.
- DMA source tracing showed reads from RAM (0xFF9xxx) returning 0x0000 for Madou, implying source buffers could be zero at DMA time.
- VSRAM reads during snapshot show zeros; HScroll sample shows A=0x000E B=0x0000 at line 0.
- Madou still corrupt after DMA wrap fix and signed HScroll; other titles may have been sensitive to scroll changes (keep changes minimal).

Current Working Hypotheses
1) Pattern data in VRAM is wrong or missing (tiles referenced by name tables are zero/garbage), possibly due to DMA source or RAM writes not carrying correct data at DMA time.
2) Pattern fetch/decode path mismatch between direct VRAM and cached `g_renderer_vram` (less likely after direct-vs-cache checks, but still possible).
3) Remaining VRAM addressing edge (128k VRAM mode) is NOT active for Madou (reg1 bit7 never set).

Next Suggested Debug Step
- Enable both name table and pattern tile dumps to correlate name table entries with actual pattern data:
  - `EUTHERDRIVE_TRACE_NAMETABLE_ROW_DUMP=1`
  - `EUTHERDRIVE_TRACE_PATTERN_TILE_DUMP=1`
  - Optional: `EUTHERDRIVE_TRACE_VRAM_RANGE=0x0000-0x1FFF` and `EUTHERDRIVE_TRACE_VRAM_RANGE_LIMIT=200`
  - Collect snippet around first `[NT-REG]` in the log.

Relevant Files
- Renderer / scroll: `EutherDrive.Core/MdTracerCore/md_vdp_renderer_snap.cs`, `EutherDrive.Core/MdTracerCore/md_vdp_renderer_line.cs`
- DMA: `EutherDrive.Core/MdTracerCore/md_vdp_dma.cs`
- VRAM writes / pattern cache: `EutherDrive.Core/MdTracerCore/md_vdp_memory.cs`
- VDP regs: `EutherDrive.Core/MdTracerCore/md_vdp_regster.cs`
- TODO: `TODO.md`
- Logs: `madou_ntdump.log`, `madou_headless_dma_src.log`, `madou_headless_ramrange.log`

Notes
- Headless runs preferred; UI logs not visible unless launched from terminal.
- New env vars must be added to `Enviroment.txt` and `GATED_ENV_VARS.md`.
