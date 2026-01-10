# TODO

- Implement a correct VDP HBlank timing model (status bit 2) instead of the current always-on hack.
  - Define per-scanline HBlank windows based on H-counter timing.
  - Ensure status reads reflect HBlank transitions (and DMA gating) accurately.
  - Replace or retire `EUTHERDRIVE_VDP_FORCE_HBLANK` once timing is correct.
