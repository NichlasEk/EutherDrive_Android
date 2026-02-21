# Sega CD BIOS Debug Notes

Date: 2026-02-21

## Summary (Current State)
- Sega CD BIOS still boots to black screen in UI.
- Sub-CPU **does run** (BUSREQ/RESET released, sub-reg writes observed).
- CDD interrupts are initially disabled; host clock eventually enabled, and CDD commands are sent.
- PRG-RAM content mismatch persists in non-M68K-emulator path (checksum mismatch). With M68K emu, PRG-RAM stays zero.

## Key Findings

### 1) Main vs Sub CPU
- Main writes `A12001 = 0x01` (BUSREQ=0, RESET=0), so sub CPU is running.
- Logs confirm sub CPU register writes (e.g., `SCD-SUBREG-W`, `SCD-SUBBUS`).

### 2) CDD / CDC
- CDD host clock goes on via sub register `0x0037 = 0x04`.
- CDD commands are being sent (e.g., `CMD=2 0 0 4 0 0 0 0 0 9`), status updates observed.
- However, CDC destination writes (`sub reg 0x0004`) are not seen in logs; only `REGADDR` and `REGDATA` writes appear. This is suspicious because CDC DOUT/DMA depends on destination.

### 3) PRG-RAM / Checksum
- In non-M68K-emulator mode, PRG-RAM gets data but checksum mismatch remains.
  - `sum_be=0xAA3B`, expected `0xE9BB` (from PRG RAM @ 0x18E).
- In M68K-emulator mode, PRG-RAM stays all-zero after runs.

### 4) VDP / Display
- VDP registers are written in non-M68K-emulator mode, but display never turns on in Sega CD BIOS flow (black screen persists).

## Changes Made (to align with jgenesis)

### A) CUE / Track Timing Model
**File:** `EutherDrive.Core/SegaCd/SegaCdCdRom.cs`

Changed cue parsing + timing to match jgenesis:
- Data tracks always have 2s pregap + 2s postgap.
- `INDEX 00` is treated as **pause start**, not as track start.
- Track timeline built like jgenesis (`start_time` is pregap-start; `effective_start` = start + pregap + pause).
- Track-relative reads used when decoding CDD.

### B) CDD Sector Read
**File:** `EutherDrive.Core/SegaCd/SegaCdCddStub.cs`

Changed from absolute-time sector reads to track-relative reads:
- `ReadSector(track.Number, relative_time)` (matches jgenesis behavior).

## Still Broken After Changes
- UI Sega CD BIOS is still black.
- PRG RAM checksum mismatch persists.

## jgenesis Reference Paths

### CDC / CDD
- CDC implementation:
  - `/home/nichlas/jgenesis/backend/segacd-core/src/cddrive/cdc.rs`
- CDD implementation:
  - `/home/nichlas/jgenesis/backend/segacd-core/src/cddrive/cdd.rs`
- Sega CD memory map:
  - `/home/nichlas/jgenesis/backend/segacd-core/src/memory.rs`

### CUE / CD-ROM Reader
- CUE parsing logic:
  - `/home/nichlas/jgenesis/common/cdrom/src/reader/cuebin.rs`
- Track time model:
  - `/home/nichlas/jgenesis/common/cdrom/src/cue.rs`
- CD time math:
  - `/home/nichlas/jgenesis/common/cdrom/src/cdtime.rs`
- Sector reading:
  - `/home/nichlas/jgenesis/common/cdrom/src/reader.rs`

## Logs / Dumps (Local)
- Headless PRG RAM dump:
  - `/home/nichlas/EutherDrive/logs/headless_prg_ram.bin`
- Example debug logs:
  - `/tmp/ed_sc_debugscd_nom68kemu.log`
  - `/tmp/ed_sc_regs_aftercue.log`

## Current Hypotheses
1. **CDC destination (`0x0004`) is never being programmed** in BIOS flow, so data is never transferred correctly.
2. **PRG RAM checksum mismatch** indicates data read path still diverges from jgenesis.
3. **M68K emu path** likely not driving sub CPU CDD/CDC steps the same way (PRG RAM stays zero there).

## Next Steps (Planned)
- Add logging around sub register `0x0004` (CDC destination) and CDC IFCTRL/CTRL0/CTRL1 flow.
- Compare main/sub CDC command sequences with jgenesis traces.
- Verify CDC buffer data (first sector should contain `SEGA DISC SYSTEM` header).
- If needed, temporarily force CDC destination / DOUT to mimic jgenesis startup to validate data flow.

