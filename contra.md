# Contra (MD) - current theories and experiments

Status:
- Sprites render and scroll.
- Plane B shows platforms but tile order/streaming appears wrong or late.
- Plane A off -> platforms disappear. Plane B off -> platforms visible but still wrong.

Working hypothesis (current best):
- Background (Plane B) updates are not applied when expected. Could be wrong nametable base usage, wrong source writes (DMA/CPU gating), or wrong scroll table usage. Several common scroll interpretation bugs have been ruled out.

What we tested (and results):
- Force direct VRAM reads for sprites (normal + interlace): no improvement.
- Disable sprite mask (X=0 masking) / ignore sprite priority: no effect.
- Sequential SAT traversal: causes severe sprite bugs, does not restore platforms.
- Plane A base mask fixed (reg2): no change.
- Plane B base mask fixed (reg4): no change.
- HScroll table base mask fixed (reg13): no change.
- HScroll interpretation variants (unsigned / direct): no change.
- VScroll direction (subtract vs add): no change.
- Use outputLine instead of scanline for scroll calculations: no change.

Observations from traces:
- HScroll table base appears at 0xB800 and is written (VRAM writes seen at 0xB800+).
- ScrollLine sample showed hscroll values changing and vscroll values plausible.
- Sprites present on relevant scanlines; platforms are not sprites.
- Disabling Plane A removes platforms => platforms are on Plane B.

Remaining plausible causes (not yet verified):
1) Plane B nametable is read from the wrong base or wrong row/col addressing,
   even though reg4 mask is now fixed. We need to verify actual nametable
   contents vs what renderer reads for the failing frames.
2) VRAM writes to Plane B nametable are dropped during DMA gating,
   or writes are being applied with a wrong autoincrement pattern.
3) We might be indexing the nametable with incorrect row stride for scroll size,
   or using the wrong scroll size (reg16 decode) for Contra's mode.
4) Timing: reading scroll tables / nametable for line N but game updates them
   just-in-time for line N+X. This could show as background "lagging".

Proposed next step (most actionable):
- Add a focused trace that dumps a small window of Plane B nametable words
  around the platform region, for the exact scanline where it is expected.
  Compare VRAM content with what the renderer uses (address + index).
  This should tell us if the data is wrong in VRAM, or if we address it wrong.

Secondary next step (if nametable looks correct):
- Trace DMA/CPU writes into the Plane B nametable range during gameplay
  to see if writes are dropped or delayed.

Notes:
- Current debug toggles available include:
  - EUTHERDRIVE_VDP_DISABLE_PLANE_A / EUTHERDRIVE_VDP_DISABLE_PLANE_B
  - EUTHERDRIVE_SPRITE_DISABLE_MASK / EUTHERDRIVE_SPRITE_DISABLE_LINE_MASK
  - EUTHERDRIVE_SPRITE_IGNORE_PRIO
  - EUTHERDRIVE_HSCROLL_UNSIGNED / EUTHERDRIVE_HSCROLL_DIRECT
  - EUTHERDRIVE_VSCROLL_SUBTRACT
  - EUTHERDRIVE_SCROLL_USE_OUTPUT_LINE

