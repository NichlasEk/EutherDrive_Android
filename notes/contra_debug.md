# Contra.md render debug notes

## Revert notice
Do not re-apply the "window bounds fix" attempted in `md_vdp_regster.cs` (RecomputeWindowBounds) or the associated 8-bit VDP write tracing change in `md_vdp_memory.cs`.

We already tried this window-bound rewrite twice and it did not fix the Contra scroll/platform issue. It also introduced corruption in other cases. The correct path is elsewhere.

## Summary
- The window-left/right logic rewrite was a dead end.
- Reverting those changes is required before further debugging.


## Window X-interval tweak (reverted)
Tried changing window inactive handling to return empty span and adjusting left/right mapping in `md_vdp_renderer_snap.cs`. It did **not** fix the platform pop-in / missing car and made overall output more corrupt. Keep this reverted.

## Status after VSRAM/scroll debugging

### What we tried (and results)
- Window-bound logic changes: reverted. Caused corruption; did not fix Contra platform pop-in.
- Scanline scroll latch (reg11/reg13 + VSRAM[0/1]) added in renderer: no visible improvement for Contra.
- VScroll column index using HScroll (scrolledX >> 4) added: no improvement.
- H40 special “column -1” VScroll handling (VSRAM[38/39]) added: no improvement.

### Observed VSRAM writes
- VSRAM writes seen only at end of frame (scanline ~227):
  - addr 0x0000 <- 0x0100
  - addr 0x0002 <- 0x0000
- No significant reg10/11/13/16 changes during the tested frames.

### Current hypotheses (next candidates)
1) VDP FIFO / timing of VRAM/VSRAM writes (live vs latched) still differs from hardware.
2) Per-line latch timing (H-position) could be off even if we latch once per scanline.
3) DMA ordering or VRAM write gate during DMA could be dropping writes to scroll tables.
4) HScroll table base/addressing or autoincrement behavior might be subtly wrong under DMA.

### Notes
- New flags added for tracing: EUTHERDRIVE_TRACE_VSRAM, EUTHERDRIVE_TRACE_SCROLL_REGS.
- New runtime flags for scroll logic:
  - EUTHERDRIVE_VDP_LATCH_SCROLL (default ON)
  - EUTHERDRIVE_VSCROLL_USE_HSCROLL (default ON)
  - EUTHERDRIVE_VSCROLL_H40_NEG1 (default ON)

