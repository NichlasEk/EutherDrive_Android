# Predator 2 fix - Z80 CB opcode table

## Result
- Predator 2 boots and runs with graphics and sound.
- The Z80/YM busy-wait loops behave correctly.

## Root cause
- The Z80 CB opcode table was incorrectly defined and too short.
- Because the table was short, index 0x7F (CB 7F = BIT 7,A) mapped to SET instead of BIT.
- This flipped the zero flag logic in busy-waits, so the Z80 never exited YM2612 status loops.
- The 68K then polled Z80 RAM (0xA016DD) forever, freezing before the Sega logo.

## Fix
- Rebuild the CB opcode table programmatically to 256 entries.
- Ensure correct mapping for:
  - 0x00-0x3F: rotate/shift group
  - 0x40-0x7F: BIT b,r
  - 0x80-0xBF: RES b,r
  - 0xC0-0xFF: SET b,r
- Keep (HL) variants in slot r=6.

## Files changed
- EutherDrive.Core/MdTracerCore/md_z80_initialize.cs
  - Build the CB opcode table with loops and correct indices.

## Notes
- Existing savestates created before the fix may be invalid for Z80 state.
- Cold boot or new savestates are recommended for verification.
