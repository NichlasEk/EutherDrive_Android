# Sonic / Sega CD Debug Notes (EutherDrive)

## Current symptoms (user report)
- Sonic 2: cannot break monitors, cannot get shields; gets hit but does not lose rings; can kill enemies but hit state logic is wrong.
- Sonic 3: freezes.
- Sega CD BIOS/boot: unstable earlier; now focus on MD core issues.
- Contra: black screen.

## Key observations
- PC‑relative base fix (PC after extension) was implemented to match jgenesis, but **caused blackscreen in all games**. It was reverted.
- There is a **built‑in Sonic 2 RAM tracer** in `md_main.cs`:
  - Env: `EUTHERDRIVE_TRACE_SONIC2_RAM=1`
  - Optional limit: `EUTHERDRIVE_TRACE_SONIC2_RAM_LIMIT=<n>`
  - Logs changes for RAM range `0xFFB000..0xFFB07F` as `[S2-RAM]` lines.
  - Needs to be run during an actual hit/monitor event to capture deltas.

## Savestate handling
- The user provided savestate: `/home/nichlas/EutherDrive/savestates/sonic2_568e5e95.euthstate`.
- Headless load failed due to md_bus array length mismatch:
  - Error: `_pendingMbxValid` rank = 16777216 (unreasonable). Looks like format/field mismatch.
  - With `EUTHERDRIVE_SAVESTATE_LENIENT=1`, load continues but the core immediately hits **missing opcode 0x00C0 at PC 0x00052E** and triggers illegal instruction.
- Recommendation: **create a fresh savestate with current build** if using headless load.

## Current repo state
- Only uncommitted change after revert: `EutherDrive.Core/MdTracerCore/opc/md_m68k_opeRTS.cs`
  - Debug trace removal for `TraceRtsRanges` (previously caused build error after reverting other changes).
- PC‑relative addressing and branch changes were reverted to avoid global blackscreen.

## Implementations compared
- Jgenesis PC‑relative and branch base semantics:
  - PC‑relative uses **PC after reading extension word** as base.
  - Branch (BRA/BSR/Bcc word) uses **PC after extension word**.
- These changes were applied but reverted due to blackscreen regression.

## Next steps planned
1. **Run Sonic 2 RAM tracer** and capture `[S2-RAM]` lines during:
   - taking damage (spikes) and
   - breaking a monitor.
   This will show which state variables fail to update.
2. If RAM tracer shows expected values not changing, investigate:
   - CPU flags / instruction semantics
   - wrong address computation
   - RAM writes blocked or overwritten
3. For headless diagnostics, use a **fresh savestate** created by the current build.

## Latest findings (2026-02-21)
- MOVEM predecrement work was investigated and adjusted, but it is **not the root cause** of the core gameplay bugs:
  - Sonic 2 still shows wrong gameplay logic (monitor/shield/rings-hit behavior still wrong).
  - Sonic 3 still has freeze reports in user flow.
- Therefore: keep focus on broader MD CPU/state correctness, not MOVEM as primary blocker.
- Sonic 3 illegal opcode status:
  - Clean headless boot test (`/home/nichlas/roms/sonic3.md`, 2000 frames) produced **no** `missing opcode`/`ILLEGAL` in this run.
  - This suggests illegal opcode is likely **state-dependent** (e.g. specific runtime path or savestate/session conditions), not guaranteed on cold boot.

## Useful commands
- UI run with Sonic2 RAM trace:
  ```
  EUTHERDRIVE_TRACE_SONIC2_RAM=1 EUTHERDRIVE_TRACE_SONIC2_RAM_LIMIT=512 \
  dotnet run --project EutherDrive.UI -c Debug --no-restore
  ```
- Headless load savestate (if valid):
  ```
  EUTHERDRIVE_SAVESTATE_LENIENT=1 \
  dotnet run --project EutherDrive.Headless -c Debug --no-build --no-restore -- \
    --load-savestate /home/nichlas/roms/sonic2.md /home/nichlas/EutherDrive/savestates/<new>.euthstate 120
  ```

## Files referenced
- Sonic2 RAM tracer: `EutherDrive.Core/MdTracerCore/md_main.cs`
- Savestate serializer: `EutherDrive.Core/Savestates/MdTracerStateSerializer.cs`
- Savestate file format: `EutherDrive.Core/Savestates/SavestateService.cs`
- M68k PC‑relative logic (jgenesis emulator in repo):
  - `EutherDrive.Core/Cpu/M68000Emu/InstructionExecutor.cs`

- Confirmed again: MOVEM is not the root cause for Sonic 2/3 behavior. Keeping focus on core fetch/dispatch path.
- Contra trace: at PC=0x005D62 the core reads word from addr 0x007B6A as 0x6000 (BUSWATCH), then JSR via D0 jumps to 0x00BD8E and hits illegal (0x124D). So failure is earlier state/data path, not MOVEM.

## Branch base correction after jgenesis compare (2026-02-21)
- Verified against jgenesis `cpu/m68000-emu`:
  - For `BRA/Bcc/BSR` with word displacement and `DBcc`, the branch base is `PC` at extension word (`opcode+2` in this core model), not `opcode+4`.
  - `BSR` push/return address is still post-extension (`opcode+4`).
- A temporary change to post-extension branch base caused global regressions (black screen in all games) and was reverted.
- Active opcode behavior is now aligned to jgenesis model:
  - `BRA.w`, `Bcc.w`, `BSR.w`, `DBcc` use extension-word PC as branch base.
  - `BSR.w` keeps pushed return address at post-extension PC.

## ROM normalization alignment with jgenesis (2026-02-21)
- Implemented stricter Mega Drive ROM normalization to match jgenesis behavior:
  - Remove 512-byte copier header only when TMSS/interleaved header signatures indicate it.
  - Deinterleave only for strict SMD pattern (`EA` at `0x80` and `SG` at `0x2080`) and 16KB block-aligned size.
  - Apply word byteswap only when TMSS is `ESAG` at `0x100`.
- Goal: avoid false-positive deinterleave/transform that can corrupt code/data paths (notably Contra black-screen/illegal-path scenarios).
- File: `EutherDrive.Core/MdTracerCore/md_rom_utils.cs`.
