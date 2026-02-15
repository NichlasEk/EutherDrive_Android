# SA-1 Investigation Notes (Kirby's Dream Land 3)

## Scope
- Goal: match SA-1 behavior with jgenesis using first-divergence tracing.
- Test ROM: `/home/nichlas/roms/Kirby's Dream Land 3 (USA).sfc`.
- EutherDrive: SA-1 trace in `logs/sa1_trace.log` with `EUTHERDRIVE_TRACE_SA1=1`.
- jgenesis: headless trace runner `frontend/jgenesis-cli/src/bin/sa1_trace.rs` with `JGENESIS_TRACE_SA1=1`.

## Tooling Added (EutherDrive)
- SA-1 trace machine (event log + hash blocks): `SuperNintendoEmulator/KSNES/Tracing/Sa1Trace.cs`.
- Trace hooks in `SuperNintendoEmulator/KSNES/Specialchips/SA1/Sa1.cs` and `SuperNintendoEmulator/KSNES/ROM/ROM.cs`.
- Register write tracing: CCNT/SIE/SIC, vectors, BW-RAM mapping regs, DMA regs.

## Tooling Added (jgenesis)
- Headless runner to avoid GPU: `frontend/jgenesis-cli/src/bin/sa1_trace.rs`.
- Minimal SA-1 bus trace logging in `backend/snes-coprocessors/src/sa1/bus.rs` (prints `[JG-SA1-TRACE]`).

## Confirmed Match (Early Init)
The sequence of SNES SA-1 register writes during early init matches jgenesis:
- `CCNT/SIE/SIC` writes
- `C/D/E/F` bank regs (`CXB/DXB/EXB/FXB`)
- `BMAPS`, `BWPA`, `SBWE`, `SIWP`
- `CRV` reset vector
- `CCNT` release
- SA-1 side `CIE/CIC/SCNT/BMAP/CBWE/CIWP`

## First Divergence (SA-1 non-ROM events)
When comparing **SA-1 non-ROM events** (I-RAM, BW-RAM, SA1-IO), first divergence is:
- **jgenesis** does a BW-RAM read at `0x406000` (BW-RAM full) right after zeroing I-RAM.
- **EutherDrive** continues reading I-RAM at `0x300C` instead of BW-RAM.

Window (SA-1 non-ROM events):
- jgenesis: I-RAM writes up to `0x300C`, then `BW-RAM` read at `0x406000`, then back to I-RAM.
- EutherDrive: I-RAM reads/writes continue without the BW-RAM read at that point.

## Hypothesis
This divergence strongly suggests **incorrect M/X flag state** during SA-1 init:
- The opcode stream includes `A9 FE` + `54 00 00` (MVN), which should use 16-bit A if M=0.
- If A stays 8-bit, MVN transfers **1 byte** instead of **0xFF+1 bytes**, changing subsequent program flow and missing the BW-RAM read.
- Potential culprit: CPU status (M flag) not reset/updated correctly for SA-1 execution.

## Patch Applied
- Set IRQ disable on reset:
  - `SuperNintendoEmulator/KSNES/CPU/CPU.cs` sets `_i = true` on reset.
  - Commit: `d0de4d8`.

## Current Status
- Still black screen in Kirby after 20 frames.
- First divergence in SA-1 non-ROM events persists (missing BW-RAM read at `0x406000`).

## Next Steps (Minimal)
1. Verify SA-1 CPU status at entry:
   - Confirm M/X flags after reset and after `C2 30` (REP #$30).
2. Verify MVN/MVP handling uses correct A width.
3. Re-run traces and re-compare non-ROM event sequence.


## Instrumentation Update (Pending)
- Added optional SA-1 register snapshot in trace lines when `EUTHERDRIVE_TRACE_SA1_REGS=1`.
- Purpose: capture A/X/Y/DP/DBR/PB/P/E/M/X at the first divergence (pc=0x0082C7) without heavy logging.
- Added SA-1 mirror trace for SNES reads of I-RAM/BW-RAM to match jgenesis dual-logging.
- Added SNES DMA -> SA-1 CCDMA notification hook (NotifyDmaStart/End) to let SA-1 provide CCDMA data during SNES DMA from BW-RAM.
- Expanded PPU bus trace to include VRAM/CGRAM/OAM regs ($2115-$2119, $2121-$2122, $2102-$2104) for DMA visibility.
- Added interrupt control trace for writes to $4200 when DMA tracing is enabled (NMI/VIRQ/HIRQ/AUTOJOY).
- Added DMA register write trace for $4300-$437F when DMA tracing is enabled.
- Expanded MDMAEN trace to dump active channel state (mode/bbus/aaddr/abank/size).
- Added SA-1 SCNT/SIC trace to see SNES IRQ/message handshakes.
- Added RDNMI ($4210) read trace when DMA tracing is enabled.
- Added optional SNES vector read trace (RDNMI/IRQ vectors) via `EUTHERDRIVE_TRACE_SNES_VECTORS=1`.
- Added targeted WRAM write trace for $0028/$002A/$002B/$00AD when `EUTHERDRIVE_TRACE_SNES_WRAM=1`.
- Added WRAM write trace for $7E/7F:0028/002A/002B/00AD when `EUTHERDRIVE_TRACE_SNES_WRAM=1`.

## 2026-02-15 Update (Kirby 3 black screen)
- SA-1 trace (non-ROM events) now matches jgenesis for ~150k SA-1 events; no early divergence found.
- SNES PPU bus trace shows DMA to CGRAM ($2122) and OAM ($2104), but no VRAM writes ($2118/$2119).
- INIDISP ($2100) stays at 0x80 (forced blank never cleared).
- RDNMI ($4210) reads occur; NMI loop likely running, but game never flips display.

### New Instrumentation
- DMA register writes ($4300-$437F) now logged under `EUTHERDRIVE_TRACE_SNES_DMA=1` as `[DMA-REG]`.
- WRAM writes now log for low page (`$0000-$03FF`) when `EUTHERDRIVE_TRACE_SNES_WRAM=1`.

### Next trace focus
1. Capture DMA reg programming sequence before MDMAEN to see if VRAM DMA is ever configured.
2. Observe WRAM low-page writes to find the flag that should enable display (INIDISP clear).
3. Correlate INIDISP write (if any) with a specific WRAM flag.

## 2026-02-15 Update (BW-RAM flag discovery)
- BMAPS/BMAP are set to `0x03`, so SNES and SA-1 BW-RAM windows map to base `0x6000`.
- INIDISP ($2100) writes are fed from BW-RAM offset `0x604C` (direct page `D=0x6000`, `LDA $4C`).
- BW-RAM `0x604C` is written by SNES as `0x80` and then repeatedly read as `0x80` (forced blank).
- BW-RAM `0x604E` is read as `0x0009`, and that value is used for $2105 (BGMODE), but it **does not** affect $2100.
- No SA-1 BW-RAM writes observed at `0x604C` (no `src=SA1` in BW-RAM watch).

### Implication
Kirby 3 stays black because the BW-RAM flag that drives INIDISP (`0x604C`) never clears. The SA-1 is likely supposed to clear or update it, but currently does not.

### Next Steps
1. Compare SA-1 trace vs jgenesis around the point where it should update BW-RAM `0x604C` (likely near PC `0x0082C7` or when BW-RAM flag flips in jgenesis).
2. Verify SA-1 DMA-to-BW-RAM or IRQ/message handshakes; missing SA-1-side update would keep the screen blank.
3. If SA-1 writes are blocked, check CBWE/SBWE timing or SA-1 DMA destination mapping.

## 2026-02-15 Update (PPU bus + forced blank persistence)
- PPU bus trace confirms initial `INIDISP` write to `$2100=0x80` (forced blank), coming from ROM sequence at `0x008041` (`8D 00 21`).
- The following ROM instruction is `8D 4C 60` (write to BW-RAM `$604C`), which matches the op bytes shown in the PPU bus log (PC already advanced).
- After the initial `$2100=0x80`, **no later writes to `$2100`** were observed within 5 frames.
- jgenesis trace shows the same BW-RAM write `$00604C=0x80` and no later write to clear it, so `$604C` is **not** the flag that should release forced blank.

### Implication
The forced blank is not being cleared by the BW-RAM flag at `$604C`. We should instead identify **which flag** (likely in WRAM/SA-1 mailbox/IRQ) drives the later `$2100` write, and why that write is never issued in EutherDrive.

### Next Trace Targets
1. SNES CPU: locate the code path that should write `$2100` after init (look for `8D 00 21` after the initial one).
2. SNES WRAM low-page trace: find the state flag that gates that write.
3. SA-1 mailbox / IRQ handshake: confirm message/IRQ bits that might unblock the SNES-side flow.

## 2026-02-15 Update (PC range trace + WRAM $004C watch)
### Why
We need to confirm whether the SNES ever reaches the routine at `00:83FC` (ROM offset `0x0003FC`) that does `LDA $4C` → `STA $2100`.

### New Instrumentation
- `EUTHERDRIVE_TRACE_SNES_CPU_PC_RANGE=0083C0-008410` logs a focused execution trace around the `$2100` write routine.
- `EUTHERDRIVE_TRACE_SNES_CPU_PC_RANGE_LIMIT` controls log volume (default 200).
- WRAM read trace now includes `$004C` alongside `$002E/$002F`.

### Next Steps
1. Run Kirby 3 with the PC range trace to confirm if `00:83FC` is ever executed.
2. If not reached, inspect the gating flags in `$002E/$002F/$004C` and track which code path should set them.

## 2026-02-15 Update (PC range trace was SA-1 CPU)
### Finding
The new PC range trace was logging the **SA-1 CPU**, not the SNES CPU. The trace used `SNESSystem.Peek` only, so when the SA-1 CPU hit the same addresses the op bytes appeared as `00` even though the ROM byte is `0x54`.

### Fix
PC range trace now logs **only when the CPU is SNES** and labels `cpu=SNES`. For SA-1 CPU execution, use `EUTHERDRIVE_TRACE_SA1=1`.

### Note
If you only set `EUTHERDRIVE_TRACE_SNES_CPU_PC_RANGE` (non-`1` value), headless logging is silenced unless `EUTHERDRIVE_LOG_VERBOSE=1` or another `EUTHERDRIVE_TRACE_*` is set to `1`.

### Follow-up
Standard SNES PC trace (`EUTHERDRIVE_TRACE_SNES_CPU_PC=1`) now logs only for the SNES CPU (SA-1 CPU no longer consumes the trace budget).

## 2026-02-15 Update (Current State + Thoughts)
### What I did
- Added a focused SNES PC range trace and clarified headless logging (requires `EUTHERDRIVE_LOG_VERBOSE=1` if the trace env var is not `"1"`).
- Confirmed the SNES PC trace now logs only the SNES CPU.
- Captured SNES PC traces during Kirby 3 boot and checked op bytes against ROM.

### What I saw
- SNES spends a long time in `MVN` loops (`op=0x54`) early in boot (e.g., `pc=0x008019` and later `pc=0x008039`), likely clearing/copying memory.
- The PC range trace around `00:83FC` (the `LDA $004C -> STA $2100` routine) still did not trigger within the short window tested.
- This suggests we may simply not be reaching the display-enable routine within a few frames, or a gating condition is never satisfied.

### Hypothesis
We might be stuck in early init block moves longer than expected (or a flag that should be set by SA-1 / DMA never flips). The next check is whether the SNES ever reaches the `00:83FC` routine when running longer (e.g., 60–120 frames).

### Next actions
1. Run Kirby 3 for a longer window with `EUTHERDRIVE_TRACE_SNES_CPU_PC_RANGE=0083C0-008410` and `EUTHERDRIVE_LOG_VERBOSE=1`.
2. Keep `EUTHERDRIVE_TRACE_SNES_INIDISP=1` on and confirm if `$2100` is ever written after the initial `$80`.
3. If still not reached, compare SA-1 side progress (SA-1 trace) to see if the SA-1 ever signals the SNES to proceed past init.

## Next Step Checklist
1. Run Kirby 3 (120–600 frames) with:
   - `EUTHERDRIVE_LOG_VERBOSE=1`
   - `EUTHERDRIVE_TRACE_SNES_INIDISP=1`
   - `EUTHERDRIVE_TRACE_SNES_WRAM=1`
2. Verify whether WRAM `$004C` changes and whether `$2100` ever gets a value `< 0x80`.
3. If `$004C` never changes:
   - Track writes to `$002E/$002F/$004C` to find the gating logic.
   - Cross-check SA-1 trace for message/IRQ that should trigger the SNES update path.
