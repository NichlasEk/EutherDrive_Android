# Death Duel (Mega Drive) debug summary

Date: 2026-02-13

## Symptom
- `deathduel.md` freezes/black screens during boot.
- Image and audio initially present, then screen goes black around frame ~926.

## Key finding
- Black screen is caused by **CRAM (palette) being wiped to mostly zero**, while VRAM stays non-zero.

## Evidence (logs)
- `deathduel_frame_stats.log`:
  - Frame 920–925: `cramNonBlack` is small but non-zero, `fbNonBlack` still non-zero.
  - Frame 926: `cramNonBlack=0`, `fbNonBlack=0`, VRAM remains non-zero.
- `deathduel_cram.log`:
  - At `pc=0x000DEE`, the game writes CRAM indices 0x00–0x3F with mostly 0 values at frame 922 and 926.

## What this implies
- The game is executing and writing to CRAM intentionally.
- The palette data being used for the DMA fill/transfer looks wrong (mostly zero), so the screen goes black.

## Likely root cause (hypothesis)
- **VDP DMA source address handling** is wrong or stale when the game updates DMA source in WRAM.
- The game seems to place DMA source words in WRAM (`0xFF0006` and `0xFF0008`), then triggers DMA.
- If the emulator reads the wrong source address or uses the wrong addressing mode, CRAM receives zeros.

## Supporting notes
- `deathduel_status.log` shows VBlank flag activity is normal enough; the game is not hard-stuck in a status loop.
- ROM code around `0x000800` polls VDP status; that loop is present but not the primary failure.

## Next steps
1. Log **VDP register writes** for regs `0x13–0x17` (DMA source + length) around frame 920–930.
2. Log **actual DMA source address** used by the VDP at `pc=0x000DEE` and confirm it points to valid WRAM data.
3. Verify DMA source calculation (e.g. if `<< 1` is wrong or if address should be byte/word based for this path).

## Files
- `/home/nichlas/EutherDrive/logs/deathduel_frame_stats.log`
- `/home/nichlas/EutherDrive/logs/deathduel_cram.log`
- `/home/nichlas/EutherDrive/logs/deathduel_status.log`
