Madou graphics status report

Symptom
- Madou (~/roms/madou.md) shows heavy background corruption. The corruption is present with and without sprites and window. It appears in headless too.

What we already verified
- Nametable rows (Plane A/B) and sampled pattern tiles show VRAM == renderer cache for the sampled entries.
- VDP regs around the corruption look sane: H40, plane size 64x64, HS base 0xB800, Plane A base 0xC000, Plane B base 0xE000.
- VSRAM samples are 0 (no vscroll), so the issue is not per-column vertical scroll.
- Forcing direct VRAM planes changes output (vertical striping) but does not fix the corruption.

Recent changes
- Fixed scroll size decode to map reg16 values to 32/64/128 (previously 96 for value 2). This avoids bad plane strides in 128-cell modes.
- Added a targeted VRAM write logger: EUTHERDRIVE_TRACE_VRAM_RANGE=<start-end>.

Current hypotheses
- The corruption is caused by incorrect VRAM contents (bad DMA source, byte writes, or unexpected overwrites) rather than renderer caching.
- A subset of VRAM writes during the upload phase is off by an address or missing; we need to see who writes those bytes.

Recent capture (cold boot, 120 frames)
- `madou_vram_range_pattern.log`: VRAM range 0x0000-0x1FFF shows CPU burst writes at frame 7 from PC=0x06297E, auto-inc=2, code=0x21.
- `madou_vram_range_nt.log`: VRAM range 0xC000-0xC7FF shows CPU burst writes at frame 7 from PC=0x014188, auto-inc=2, code=0x21.

Next logging step
- Use EUTHERDRIVE_TRACE_VRAM_RANGE to capture writes to relevant regions:
  - 0x0000-0x7FFF for pattern data
  - 0xC000-0xDFFF for Plane A name table
  - 0xE000-0xFFFF for Plane B name table
- Example run:
  - EUTHERDRIVE_TRACE_VRAM_RANGE=0xC000-0xC7FF (name table slice)
  - EUTHERDRIVE_TRACE_VRAM_RANGE=0x0000-0x1FFF (pattern slice)
- Note: A 30-frame savestate run produced no [VRAM-RANGE] hits, likely because uploads happened before the save. A cold boot run is needed for capture.

What to look for in [VRAM-RANGE]
- Writes with unexpected auto-inc values or odd addresses.
- Large DMA bursts from unexpected source addresses.
- Repeated overwrites of the same range after the expected upload.

Open questions for GPT
- Is Madou known to use byte writes or DMA fill/copy into VRAM in a way that stresses edge cases (odd address, autoinc=1)?
- Any known quirks with name table updates right before display enable for Madou specifically?
