# PCE CD Debug Log

This file tracks current observations, regressions, and active hypotheses for PCE CD issues.
Keep it short, append newest entries at the top, and include date/time if relevant.

## 2026-02-25
- 2026-02-28 (LOCKED): In `PCE_CD_Core/PPU.cs`, `int tileLineOffset = tileY * 128;` is the correct behavior and must remain unchanged.
- Do not change this to a width-dependent stride (`width==2 ? 128 : 64`); that variant regressed rendering and was reverted.
- 2026-02-27: Do **not** re-apply the PPU sprite tile-line stride change `tileLineOffset = tileY * (width==2 ? 128 : 64)` in `PCE_CD_Core/PPU.cs`. It made Golden Axe sprite corruption worse and was reverted.
- UI stall signature (Valis II): BIOS polls status reg `0x00` at PC `0xEA1D` with `val=0xF8` (MessageIn). Repeated `STATUS STALL` entries show `phase=MessageIn`, `buf=1/1`, `req=True`, `ack=False`, `irqA=0x20`, `irqE=0x00` after `ReadTOC` and `ReadSector`.
- UI log shows stalls already after `TestUnitReady` (cmd `0x00`) and after each `ReadTOC`/`ReadSector`, always `MessageIn` with `buf=1/1` and `ack=False` while BIOS keeps reading status at PC `0xEA1D/0xEB5E`.
- Tried default MessageIn immediate BusFree: broke all discs. Reverted to legacy default; keep `EUTHERDRIVE_PCE_MSGIN_NEW=1` as opt-in only.
- Tried: avoid reasserting REQ when no pending data bytes in DataIn/MessageIn. This broke all discs; reverted.
- Added `EUTHERDRIVE_PCE_DATA_LOG=1` to log MessageIn byte emission and DataPort reads with PC.
- Added `EUTHERDRIVE_PCE_MSGIN_STATUS_ADVANCE=1`: on status read during MessageIn, if message byte already consumed, force BusFree. Intended to stop BIOS polling loop at `pc=0xEA1D`.
- Added `EUTHERDRIVE_PCE_MSGIN_PULSE=1`: drop REQ and end MessageIn immediately after the message byte is consumed.
- Added log when MSGIN_PULSE triggers: `MSGIN pulse -> BusFree`.
- Guard: when phase is `BusFree`, dispose `dataBuffer` and ignore DataPort reads (return `0x00`) to prevent reads continuing in BusFree.
- Added `EUTHERDRIVE_PCE_CDREG_RING=1`: dumps last 64 CD register accesses on STATUS STALL.
- Observed with MSGIN status-advance: BIOS still stalls with `val=0x40` in `BusFree` (REQ asserted while BSY=0). Added guard so `ProcessACK` does not reassert REQ in `BusFree`.
- Headless (Valis II, 600 frames, `EUTHERDRIVE_PCE_HEADLESS_MAX_TICKS=20000`): still no CD command loop in logs (no `TestUnitReady/ReadTOC/ReadSector` or `reg 0x00` reads). Only early init writes to regs `0x04/0x02/0x0D/0x0B/0x0E`.
- UI: BIOS reads again. Rondo of Blood works again. Valis II/III still hang as before, but now produce logs.
- MessageIn handling: default legacy behavior restored. Optional new behavior (MessageIn -> BusFree after single byte) is behind `EUTHERDRIVE_PCE_MSGIN_NEW=1`.
- Headless: added PCE CD core support to `EutherDrive.Headless` and a configurable tick budget.
  - `EUTHERDRIVE_PCE_HEADLESS_MAX_TICKS` increases tick safety per frame.
  - Even with higher ticks, headless did not reach CD command loop (no `TestUnitReady/ReadTOC/ReadSector`).

## Active Logging
- Use `EUTHERDRIVE_TRACE_VERBOSE=1` for stall detection.
- Optional: `EUTHERDRIVE_PCE_CDREG_LOG=1` with `EUTHERDRIVE_PCE_CDREG_LOG_LIMIT=<N>` to capture more IO.

## Key Signals
- Valis II/III: hangs during BIOS CD init (same behavior as before).
- Rondo: works with legacy MessageIn handling.
