# MOVEM_Fix

## Summary
Sega CD BIOS boots correctly only when `EUTHERDRIVE_SCD_USE_M68KEMU=0` (tracer CPU). With M68kEmu enabled, the main CPU appears stuck in VDP mode 4 and never writes VDP registers. The failure point is an illegal instruction at `PC=0x000004D4` (`OP=0x3FFF`) which is inside the BIOS data table, implying the CPU jumped into data instead of executing BIOS code. This strongly points to an M68kEmu instruction decode/PC advance bug, most likely around `MOVEM`.

## Key Evidence
- Boot sector dump matches jgenesis exactly.
- PRG RAM and CDC RAM dumps match jgenesis after 300 frames.
- VDP registers differ only when M68kEmu is used:
  - M68kEmu path: Mode 4, H32, DMA off, scroll bases FFFF (no VDP init).
  - Tracer CPU: Mode 5, display enabled (VDP init performed).

## Logs
- Main CPU illegal instruction:
  - `[M68K-EX] cpu=SCD-MAIN kind=IllegalInstruction pc=0x000004D4 op=0x3FFF ...`
- BIOS vector table indicates illegal/address vectors point to RAM `0xFFFFFD7E`.
- RAM jump trace:
  - `[SCD-MAIN-RAMJUMP] pc=0xFFFD7E op=0x0000 prev_pc=0x0004D4 prev_op=0x3FFF`
  - Confirms illegal instruction at 0x4D4 caused exception, then CPU ran from RAM vector.

## BIOS Context
- Reset vector: PC=0x0000042E.
- BIOS init code around 0x42E configures the VDP and uses a data table near 0x4D2.
- The word at 0x4D4 is 0x3FFF (data), not a valid instruction. CPU executing there indicates a bad branch/PC advance.

## Hypothesis (Most Likely)
`MOVEM` handling in `M68000Emu` is incorrectly advancing PC or decoding the extension word when loading the VDP init table, causing execution to fall into the table. This matches:
- jump into 0x4D4 (table region)
- immediate illegal instruction
- no VDP writes afterward

## Likely Fix Area
- `EutherDrive.Core/Cpu/M68000Emu/InstructionExecutor.Load.cs`
  - `MOVEM` handling and extension-word decode
- `EutherDrive.Core/Cpu/M68000Emu/InstructionExecutor.cs`
  - PC advance logic and exception flow (verify PC increments for `MOVEM` vs. data table layout)

## Repro Commands
- EutherDrive headless dump (bad with M68kEmu):
  - `EUTHERDRIVE_HEADLESS_DUMP_VDP_REGS=1 EUTHERDRIVE_HEADLESS_DUMP_DIR=/tmp dotnet /home/nichlas/EutherDrive/EutherDrive.Headless/bin/Debug/net8.0/EutherDrive.Headless.dll "<cue>" 300`
- Force tracer CPU (works):
  - `EUTHERDRIVE_SCD_USE_M68KEMU=0 EUTHERDRIVE_HEADLESS_DUMP_VDP_REGS=1 EUTHERDRIVE_HEADLESS_DUMP_DIR=/tmp dotnet /home/nichlas/EutherDrive/EutherDrive.Headless/bin/Debug/net8.0/EutherDrive.Headless.dll "<cue>" 300`

## Next Steps (Proposed)
1. Add a tight PC/op trace around 0x42E–0x500 for M68kEmu to confirm which instruction mis-decoded.
2. Compare M68kEmu `MOVEM` semantics with known-good implementation (jgenesis or reference).
3. Verify PC advance for MOVEM with pre/post-decrement modes and extension word usage.
4. Once fixed, re-run VDP register dump: should match tracer CPU/jgenesis (Mode 5, VDP writes present).


## Update 2026-02-22
Further tracing showed the illegal instruction at `0x000004D4` is reached because the BIOS branch at `0x00000434/0x0000043C` is taken. That branch is controlled by `tstl 0xA10008` (I/O control regs). Our I/O implementation returns non-zero there (`0x00400040`), so the BIOS skips init and jumps into the data table at `0x4D2`.

Key observation from bus watch:
- `tstl 0xA10008` reads `0x00400040` (non-zero) -> branch taken -> PC lands at `0x4D2` -> illegal `0x3FFF`.

Cause:
- `md_io.read8()` was returning TH status for `0xA10008/0xA1000A` instead of control register contents. On reset, these should be `0x00`, so `tstl 0xA10008` should be zero and not branch.

Fix applied:
- Added `_pad1Ctrl`/`_pad2Ctrl` fields.
- `read8()` for `0xA10008/09` and `0xA1000A/0B` now returns control registers, not TH.
- `write8()` for those addresses now updates control registers; TH is only updated on writes to data registers (`0xA10003/0xA10005`).

Files changed:
- `EutherDrive.Core/MdTracerCore/md_io.cs`

Next steps:
- Re-run headless Sega CD boot with M68kEmu and confirm VDP registers transition to mode 5 and no illegal instruction at `0x4D4`.
- If still failing, revisit branch base PC and/or further I/O register defaults.


## Update 2026-02-22 (post-IO fix)
With control-register reads fixed, BIOS proceeds past 0x4D2 and begins init, but now hits an Address Error:

- `[M68K-EX] cpu=SCD-MAIN kind=AddressError pc=0x00000488 op=0x289D inst=Move size=LongWord src=(A5)+ dst=(A4) addr=0x000004EF op=Read`

`0x4EF` is odd, so the longword read faults. This means A5 has become odd before `movel (A5)+,(A4)` at 0x488.

Hypothesis: PC-relative addressing for LEA/MOVEM table is off-by-2 in `ResolveAddress(PcRelativeDisplacement/Indexed)`. Our implementation uses PC *after* fetching the extension word, but 68000 PC-relative bases on the extension word address. That would shift the table pointer and corrupt subsequent MOVEM loads, likely leading to misalignment/odd A5.

Next step: add A4/A5 to `M68K-PC` trace (done) and capture the A5 value at 0x440/0x444/0x448/0x47C/0x486 to confirm if A5 is miscomputed or MOVEM increments incorrectly.

Potential fix location:
- `EutherDrive.Core/Cpu/M68000Emu/InstructionExecutor.cs` -> `ResolveAddress` for `PcRelativeDisplacement` and `PcRelativeIndexed`.


## Update 2026-02-22 (PC-relative fix)
Trace showed `LEA (PC+$90),A5` at 0x440 produced A5=0x4D4, but correct table is 0x4D2. Root cause: PC-relative addressing used PC **after** fetching the extension word. Fixed to base on PC **before** FetchOperand.

Fix:
- `EutherDrive.Core/Cpu/M68000Emu/InstructionExecutor.cs`
  - `PcRelativeDisplacement` and `PcRelativeIndexed` now base on `pcBefore`.

Expected effect:
- A5 should be 0x4D2 after LEA, MOVEM reads proper table, A5 stays even through 0x488, no AddressError, VDP init should proceed.


## Update 2026-02-22 (DBcc base PC)
After PC-relative fix, A5 still became odd and an AddressError occurred at `movel (A5)+,(A4)` (0x488). Trace shows `dbf` at 0x482 branches to 0x47E (skipping `moveb (A5)+`) so A5 increments only once and stays odd.

Cause: DBcc displacement base used PC *after* fetching the extension word. For DBcc, the displacement should be relative to the extension word address (PC before FetchOperand), so the loop targets 0x47C and includes `moveb (A5)+` each iteration.

Fix:
- `EutherDrive.Core/Cpu/M68000Emu/InstructionExecutor.ControlFlow.cs`
  - In `Dbcc`, use `pcBefore` (PC before FetchOperand) when computing branch target.

Expected effect:
- Loop executes `moveb (A5)+` each iteration, A5 ends even, no AddressError at 0x488.


## Update 2026-02-22 (PSG crash)
Headless run crashed on first PSG write:
- `Index was outside the bounds of the array` in `md_sn76489.write8` (freq/vol arrays uninitialized).

Cause:
- `md_music.reset()` (which calls `SN76489_Start()`) was not invoked for Sega CD load, so PSG arrays remained empty.

Fix:
- Added `md_main.g_md_music.reset()` in `SegaCdAdapter.LoadRom()` right after VDP reset.

Expected effect:
- PSG writes no longer crash, boot continues beyond frame 0.


## Update 2026-02-22 (Branch/Bsr base PC)
Illegal instruction now occurs at `PC=0x000014B4` with opcode `0xFDDC`, which is the *middle* of the `moveb d1,0xFFFFFDDC` instruction at 0x14B2. Disassembly shows a `bra.w 0x14b2` at 0x14AE (`6000 0002`). Our branch base was using PC **after** fetching the extension word, so target became 0x14B4 (off by +2). This matches the illegal opcode.

Fix:
- `EutherDrive.Core/Cpu/M68000Emu/InstructionExecutor.ControlFlow.cs`
  - `Branch` and `Bsr` now always base on PC after opcode fetch (extension word address), not after extension fetch.

Expected effect:
- `bra.w` targets correct instruction boundary (0x14B2), eliminating the illegal at 0x14B4.


## Update 2026-02-22 (VDP DMA status bit)
Main CPU was stuck in BIOS loop at `0xB8C` waiting for VDP status bit1 to clear (`btst #1,(VDP status)`). Our status word set DMA active if `g_dma_leng > 0`, but BIOS writes DMA length registers early (0xFFFF) even when DMA is not running. This kept DMA active forever, trapping BIOS.

Fix:
- `EutherDrive.Core/MdTracerCore/md_vdp_regster.cs`
  - `dmaActive` now reflects `g_dma_mode != 0` only (actual DMA in progress), not just nonzero length.

Expected effect:
- VDP status bit1 clears when DMA not running, BIOS loop exits, main CPU proceeds past 0xB8C.


## Update 2026-02-22 (Frankenstein GEN corruption)
Game: `/home/nichlas/roms/frankenstein.gen` shows corrupt graphics from start to end (see snapshot in `/home/nichlas/EutherDrive/logs/snapshots/`).

### Symptoms
- VRAM ends up filled with repeating word `0x012D` instead of decompressed tiles.
- VDP writes occur, but source data is clearly wrong.

### Key Logs (existing)
- `/home/nichlas/EutherDrive/logs/headless_frankenstein_patternpc2.log`
  - Shows VRAM writes of `0x012D` at `pc=0x02D1F2`, with `A1=0xFFA980`, `A3=0xFFA992`, `D1=0x012D`, `D2=0x012D`.
- `/home/nichlas/EutherDrive/logs/headless_frankenstein_ramrange_pc.log`
  - At `pc=0x06F8EE` the routine writes **zero** bytes into the buffer region (`0xFFA980`..), e.g.:
    - `val=0x00 A0=0x00FFA992 A1=0x00FFA993 A2=0x00FF0204 A3=0x0018364F A4=0x00FFA980`
- `/home/nichlas/EutherDrive/logs/headless_frankenstein_pctap_6f8.log`
  - Entry to decompress/copy routine around `0x06F8C0` shows `A4` already set to `0xFFA980`, and `A0/A1` also inside the same RAM buffer.
- `/home/nichlas/EutherDrive/logs/headless_frankenstein_pctap_peek.log`
  - ROM source data at `A3` is **non-zero**, so ROM is fine.

### Disassembly Findings (ROM)
- Routine at `0x06F8C0`:
  - `moveml %d0-%a4,%sp@-`
  - `moveal %a1,%a4` (A4 = base dest)
  - `lea %a4@(d3),%a0` and `lea %a4@(d5),%a1`
  - `moveb %a0@+,%a1@+`
  - `movew %a1@(-2),0xc00000`
- Caller at `0x02D1A8`:
  - `lea 0xffa980,%a1`
  - `jsr 0x6f82c`
  - (A0 should be set by caller before this)

### Conclusion
The decompression/copy routine is being called with a **bad A0** (pointing into the same RAM buffer instead of ROM source). This causes the routine to copy zeros into the buffer, and later the VRAM fill loop at `0x02D1F2` writes `0x012D` across VRAM. That explains fully-corrupt graphics.

### Likely Fix Area
- M68000 register state restore or caller setup before `jsr 0x06F82C`.
- Suspect: incorrect `MOVEM` handling or PC-relative/addressing bug earlier in the call chain that corrupts A0.

### Latest Run Log
- `/home/nichlas/EutherDrive/logs/headless_frankenstein_run2.log`
  - Headless run with `EUTHERDRIVE_TRACE_RAM_RANGE_PC=1` and `EUTHERDRIVE_TRACE_PC_TAP_LIST=0x6F8C0,0x6F8EE,0x2D1A8,0x2D1F2` (timed out at 20s but produced initial PSG/boot traces).


## Update 2026-02-22 (Frankenstein A0 tracing)
Goal: verify whether `A0` (compressed source pointer) is wrong when calling the decompressor.

### Evidence
- Caller sets A0/A1 explicitly:
  - Disassembly around `0x29CD0`:
    - `lea 0x37220,%a0`
    - `lea 0x37600,%a1`
    - `jsr 0x2d194` (which calls `jsr 0x6f82c`)
- PCTAP confirms A0 is correct at decompressor entry:
  - `/home/nichlas/EutherDrive/logs/headless_frankenstein_a0tap2.log`
    - `pc=0x02D1A8`: `A0=0x00037220 A1=0x00037600`
    - `pc=0x06F82C`: `A0=0x00037220 A1=0x00FFA980`
    - `pc=0x06F83C`: `A3=0x00037231` (A0+17, compressed data stream)

Conclusion: `A0` is **not** clobbered; decompressor receives the correct ROM source pointer.

### RAM write tracing (copy loops)
- `/home/nichlas/EutherDrive/logs/headless_frankenstein_ramrange3.log`
  - Shows backref copy loop at `0x06F8AC/0x06F8EE/0x06F8F0` writing **zero** bytes into buffer:
    - `pc=0x06F8AC addr=0xFFA981 val=0x00 ... A0=0xFFA981 A1=0xFFA982 A3=0x0018364F`
    - `pc=0x06F8EE addr=0xFFA982 val=0x00 ...`
  - Indicates output buffer is being filled with zeros during backref copies.

### Next step
Instrument ROM reads during literal output (`pc=0x06F91E`, `moveb %a3@+,%a1@+`) to confirm whether ROM bytes are read correctly or if the read path is returning zeros. This will tell whether the issue is in memory reads or in the decompressor control flow.


## Update 2026-02-22 (ROM reads + literal output verified)
Added ROM-read tracing for the `0x06F82C` decompressor.

### ROM reads are non-zero
- `/home/nichlas/EutherDrive/logs/headless_frankenstein_romread.log`
  - `pc=0x06F83E addr=0x183635 val=0x03` (header byte)
  - Multiple `pc=0x06F920` reads returning non-zero bytes (e.g., `0x99, 0x87, 0xE9, 0xEA`)
  - Confirms ROM read path is OK.

### Literal output writes are non-zero
- `/home/nichlas/EutherDrive/logs/headless_frankenstein_ramrange5.log` (non-zero RAM writes only)
  - `pc=0x06F920 addr=0xFFADD1 val=0x99` (literal write)
  - `pc=0x06F920 addr=0xFFADEF val=0xE9`
  - Backref copies use existing buffer bytes (e.g., `0xEE`, `0x9E`), so buffer is not all zeros.

### Current conclusion
The `0x06F82C` decompressor is reading ROM correctly and writing non-zero bytes into the output ring buffer. Corruption is therefore likely **after** this stage (e.g., in the later `0x6FA74` path or in the VRAM transform/fill loop at `0x02D1EC/0x02D1F2`).

### Next step
Trace the `0x06FA74` path (used by `0x02D1BA`) to see if its output buffer matches what the VRAM fill loop consumes. If it writes to a different ring offset than the loop expects (A3 = `0xFFA980`), the loop will read mostly zeros -> `0x012D` fill.


## Update 2026-02-22 (0x06FA74 path traced)
Added focused RAM-range PC logging for the `0x06FA74` path (pc `0x06FB44`–`0x06FB56`) and wired RAM-range logs from `md_m68k_memory` writes.

### Evidence
- `/home/nichlas/EutherDrive/logs/headless_frankenstein_fa74_7.log`
  - PCTAP confirms call chain:
    - `pc=0x02D1BA` then `pc=0x02D1C0` (`A0=0x00037600`), then `pc=0x06FA74` (`A1=0x00FFA980`)
  - PC‑focused RAM writes at `0x06FB48`:
    - Writes **zeros** into `0xFFAC00..` (example: `addr=0xFFAC00 val=0x00`, repeated).
  - Literal writes at `0x06FB56`:
    - Writes a **simple incremental pattern**: `0x01 0x00 0x02 0x00 0x03 0x00 ...` into `0xFFAC15..`
    - Example lines:
      - `pc=0x06FB56 addr=0xFFAC15 val=0x01`
      - `pc=0x06FB56 addr=0xFFAC16 val=0x00`
      - `pc=0x06FB56 addr=0xFFAC17 val=0x02`

### Interpretation
The `0x06FA74` routine is **not** writing tile-like data into the buffer; it is filling the ring with a low-entropy pattern and backref copies of zeros. This strongly suggests either:
1. The input stream (`A0=0x37600`) is not the expected compressed data for graphics, or
2. The routine expects a different destination/consumer than the VRAM fill loop that later writes `0x012D`.

### Code changes to enable tracing
- `EutherDrive.Core/MdTracerCore/md_bus.cs`
  - Added `EUTHERDRIVE_TRACE_RAM_RANGE_PC_LIMIT` and `EUTHERDRIVE_TRACE_RAM_RANGE_PC_FOCUS_6FB`.
  - PC‑focused logs are no longer throttled by general RAM‑range limits.
- `EutherDrive.Core/MdTracerCore/md_m68k_memory.cs`
  - RAM writes now call `md_main.g_md_bus?.LogRamRangeDirect(...)` to capture CPU‑direct RAM stores.


## Update 2026-02-22 (6FBxx backref/literal behavior)
Captured additional traces around `0x06FB36/0x06FB3A/0x06FB44` and disassembled the routine to confirm what is being copied.

### Disassembly (ROM `0x06FB20..`)
```
6fb36: bsrs 0x6fb92
6fb38: negl %d0
6fb3a: lea %a5@(%d0:l),%a1
6fb3e: lea %a2@(256),%a0
6fb42: bsrs 0x6fb92
6fb44: moveb %a1@+,%a5@+
6fb46: moveb %a1@+,%a5@+
6fb48: dbf %d0,0x6fb46
6fb54: moveb %a3@+,%a5@+
6fb56: dbf %d0,0x6fb54
```
This confirms `0x06FB44` is a **backref copy** from `A1` (computed from `A5 + D0`) into the output ring at `A5`, while `0x06FB54` is the **literal** path copying from the compressed stream at `A3`.

### PCTAP evidence (A1 computed correctly)
From `/home/nichlas/EutherDrive/logs/headless_frankenstein_a1tap.log`:
- `pc=0x06FB36` shows `A1` still at stack (`0xFF01ED`) **before** the `lea` executes.
- `pc=0x06FB44` shows `A1=0xFFA980` and `A5=0xFFA981`, which is correct for backref copies within the output ring.

So the backref address is **not** pointing into the stack; it points into the output ring as expected.

### Input bytes explain the “pattern” output
ROM bytes at `0x37620` are:
```
00 20 0e 00 ae 24 00 e7 3b 01 00 02 00 03 00 04 00 05 00 06 00 07 00 08 00 09 00 0a 00 0b 00 0c
```
These match the simple `0x01 0x00 0x02 0x00 ...` pattern observed in `0x06FB54` literal writes. This suggests the low‑entropy output is **coming from the input stream itself**, not from a CPU read bug.

### Implication
The `0x06FA74` routine appears to be functioning according to its own logic (correct backref addressing and literal reads), but the data it produces does not look like tile data. That pushes suspicion toward:
1. The **wrong input block** being fed to `0x06FA74` (e.g., wrong `A0` base or wrong file offset), or
2. The **consumer loop** (e.g., `0x02D1EC/0x02D1F2`) interpreting this output incorrectly.

Next step: verify whether the same `0x37600` input block is used on real hardware/jgenesis for the VRAM fill, or if the intended block is `0x37220`. If the game expects a different block, our mapping/call chain would be wrong.


## Update 2026-02-22 (VRAM source buffer peek)
Used `EUTHERDRIVE_TRACE_PC_TAP_PEEK_LIST` to dump the source buffer **at the exact VRAM fill loop** (`pc=0x02D1EC`).

Command produced `/home/nichlas/EutherDrive/logs/headless_frankenstein_vrampeek_wide.log`.

Key results:
- At `pc=0x02D1EC`, `A1=0xFFA980` and the loop reads from `A3=A1`:
  ```
  2d1ec: movew %a3@+,%d2
  2d1ee: addw %d1,%d2
  2d1f0: movew %d2,%fp@
  ```
- Buffer contents at this time:
  - `0xFFA980..0xFFAC0F` are **all zeros**
  - Data starts at `0xFFAC10` and matches the low‑entropy pattern:
    ```
    0xFFAC10: 00 01 00 02 00 03 00 04 00 05 00 06
    0xFFAC20: 00 07 00 08 00 09 00 0A 00 0B 00 0C
    ```
  - This aligns with the ROM stream bytes at `0x37620`.

Interpretation:
- The VRAM loop is **reading the correct base** (`0xFFA980`), but the decompressor output has ~0x290 leading zero bytes.
- The first non‑zero output is the same low‑entropy sequence observed in earlier logs.

This suggests the corruption is not due to the VRAM loop reading the wrong address; it is reading the correct base and the buffer is genuinely zero/low‑entropy at that time.


## Update 2026-02-22 (A5 struct + full buffer dump)
Captured the A5‑relative fields used to compute the VRAM addend, and dumped the **full 0x640‑byte buffer** the VRAM loop reads.

### A5 fields (from `/home/nichlas/EutherDrive/logs/headless_frankenstein_a5peek.log`)
At `pc=0x02D1DA` (before `movew %a5@(12),%d0`) and `pc=0x02D1E4` (before `movew %a5@(14),%d1`):
- `A5=0xFFA4DE`
- `A5+12` (`0xFFA4EA`) = `00 00` → `0x0000`
- `A5+14` (`0xFFA4EC`) = `25 A0` → `0x25A0`
- `0x25A0 >> 5 = 0x012D`, which matches the addend used in the VRAM loop.

So the `0x012D` offset is **intentional and data‑driven**, not a CPU bug.

### Full buffer dump
- `/home/nichlas/EutherDrive/logs/headless_frankenstein_fullbuf.log`
  - Generated with `EUTHERDRIVE_TRACE_PC_TAP_PEEK_LIST=0xFFA980-0xFFAFBF` at `pc=0x02D1EC`
  - This captures the entire 0x640‑byte output buffer consumed by the VRAM loop, for comparison against a reference decompressor or jgenesis.


## Update 2026-02-22 (Compare With jgenesis: RAM vs VRAM)
Created a small jgenesis tool to dump working RAM ranges and VRAM from the Genesis core:
- `/home/nichlas/jgenesis/backend/genesis-core/src/bin/gen_mem_dump.rs`
  - Usage: `gen_mem_dump <rom_path> <frames> <out_path> <start_hex> <end_hex>`
  - Also writes VRAM to `<out_path>.vram.bin`

### Result: Working RAM buffer matches
Compared the output buffer used by the VRAM fill (`0xFFA980..0xFFAFBF`) between EutherDrive and jgenesis:
- EutherDrive buffer extracted from `/home/nichlas/EutherDrive/logs/headless_frankenstein_fullbuf.log`
- jgenesis buffer `/tmp/jg_frank_buf.bin`

They are **byte‑for‑byte identical**.

### Result: VRAM diverges
VRAM dumps differ starting at address `0x0000`:
- jgenesis VRAM starts with repeating `0x012D`
- EutherDrive VRAM starts with `0x00F8, 0x80F8, 0x80F8...`

Comparison (first words):
```
EutherDrive: 0x00F8 0x80F8 0x80F8 ...
jgenesis:    0x012D 0x012D 0x012D ...
```

### Evidence: EutherDrive executes a later VRAM fill loop at `0x2093A`
Trace at `pc=0x02093A` shows repeated writes of `0x80F8` to VRAM:
- `/home/nichlas/EutherDrive/logs/headless_frankenstein_vramrange2.log`
  - `[VRAM-RANGE] frame=73 source=FIFO pc=0x02093A ... addr=0x0000 val=0x80F8`
- PCTAP confirms `D1=0x400080F8` at `pc=0x02093A` and `A6=0xC00000` (VDP data port):
  - `/home/nichlas/EutherDrive/logs/headless_frankenstein_pc209.log`

This loop comes from ROM at `0x2092A/0x2093A`:
```
2092a: movew %a5@(18),%d1
2092e: lsrw #5,%d1
20930: oriw #0x8000,%d1
2093a: movew %d1,%fp@     ; VDP data port
2093c: dbf %d0,0x2093a
```

### Interpretation
Since the RAM buffer matches jgenesis but VRAM diverges, the corruption is **after** decompression:
1. Our CPU executes the `0x2093A` VRAM fill and overwrites VRAM with `0x80F8`.
2. jgenesis VRAM does **not** show that overwrite (it remains `0x012D`), so either:
   - jgenesis does **not** execute that loop, or
   - jgenesis executes it but the VDP target/behavior differs (e.g., control port decode/gating).

Next step: identify why the `0x2091C/0x2092A` routine is affecting VRAM in EutherDrive but not in jgenesis.


## Update 2026-02-22 (VDP ctrl decode + pattern writes around 0x208xx/0x209xx)
Ran headless with VDP control decoding + PC range filtering:
```
EUTHERDRIVE_LOG_VERBOSE=1
EUTHERDRIVE_HEADLESS_CORE=md
EUTHERDRIVE_TRACE_VDP_CTRL=1
EUTHERDRIVE_TRACE_VDP_CTRL_ALL=1
EUTHERDRIVE_TRACE_VDP_CTRL_PC=1
EUTHERDRIVE_TRACE_VDP_CTRL_PC_RANGE=0x20800-0x20A00
EUTHERDRIVE_TRACE_VDP_CTRL_LIMIT=5000
EUTHERDRIVE_TRACE_VDP_CTRL_PC_LIMIT=5000
```
Log: `/home/nichlas/EutherDrive/logs/headless_frankenstein_vdpctrl3.log`

### Control-port writes from the suspicious PC range
From the log (frame 73):
- `pc=0x020868` writes `0x4000 / 0x0000` → `VDP-CTRL-DECODE target=VRAM addr=0x0000`
- `pc=0x020868` writes `0x5000 / 0x0000` → `VDP-CTRL-DECODE target=VRAM addr=0x1000`
- `pc=0x020868` writes `0x5F00 / 0x0000` → `VDP-CTRL-DECODE target=VRAM addr=0x1F00`
- `pc=0x020868` writes `0x65A0 / 0x0000` → `VDP-CTRL-DECODE target=VRAM addr=0x25A0`

So the **control-port decode is working** and is setting VRAM addresses as expected.

### Pattern writes confirm VRAM data overwrite at 0x0000
Ran with pattern writes + PC range:
```
EUTHERDRIVE_LOG_VERBOSE=1
EUTHERDRIVE_HEADLESS_CORE=md
EUTHERDRIVE_TRACE_VDP_PATTERN_WRITES_PC=1
EUTHERDRIVE_TRACE_VDP_PATTERN_WRITES_PC_RANGE=0x20900-0x20980
EUTHERDRIVE_TRACE_VDP_PATTERN_WRITES_PC_LIMIT=5000
EUTHERDRIVE_TRACE_VDP_PATTERN_WRITES_PC_FRAMES=90
```
Log: `/home/nichlas/EutherDrive/logs/headless_frankenstein_patternpc_209.log`

At `pc=0x02093A` (the suspected loop), the VDP data-port writes are **definitely hitting VRAM address 0x0000**, not 0x1F00/0x25A0:
```
[PATTERN-PC] frame=73 pc=0x02093A vram_addr=0x0000 val=0x80F8 A5=0x00FFA4DE D1=0x400080F8
[PATTERN-PC] frame=73 pc=0x02093A vram_addr=0x0002 val=0x80F8 ...
...
```
This is consistent with the VRAM corruption (`0x80F8`) observed earlier.

### Implication
Even though the control-port writes at `pc=0x020868` set VRAM addresses like `0x1F00` and `0x25A0`, the **subsequent data-port writes at `pc=0x02093A` are landing at VRAM addr `0x0000`**.

This suggests one of:
1. The VDP address register is being **reprogrammed to 0x0000** after those control writes (e.g., another control write happens between `0x20868` and `0x2093A`), or
2. Our VDP state machine / FIFO / DMA gating differs from jgenesis, so the data writes are **applied to the wrong target or at the wrong time**.

Next step: trace VDP DMA status and FIFO gating around frame 73 to see if these CPU writes should have been blocked/buffered (e.g., `EUTHERDRIVE_TRACE_DMA_STATUS=1` + VDP FIFO/CPU write gating state).


## Update 2026-02-22 (PC-tap at 0x2093A + DMA gate check)
Captured full register state at `pc=0x02093A` (once per frame):
- Log: `/home/nichlas/EutherDrive/logs/headless_frankenstein_pctap_2093a_once.log`
```
[PCTAP] frame=73 pc=0x02093A SR=0x2000
  D0=0x0000037F D1=0x400080F8 D2=0x00000000 D3=0x0000FFFF D4=0x00000F00
  A5=0x00FFA4DE A6=0x00C00000
```

Interpretation:
- `D0=0x037F` means the loop runs **0x380 iterations** (DBF counts down), so it overwrites a large span of VRAM starting at the current dest address.

### Strict DMA write gating does not stop these writes
Tried strict gating:
```
EUTHERDRIVE_VDP_DMA_WRITE_GATE=1
EUTHERDRIVE_VDP_DMA_WRITE_GATE_STRICT=1
```
Log: `/home/nichlas/EutherDrive/logs/headless_frankenstein_patternpc_209_gate.log`

The `PATTERN-PC` lines at `pc=0x02093A` still appear with the same `0x80F8` writes, indicating `dmaActive` is **not** set during this loop. So DMA gating is not the culprit.


## Update 2026-02-22 (Disasm 0x20900..0x20940 + PCTAP at 0x2091C)
Disassembly from `frankenstein.gen` around the suspicious routine (offset `0x20900`):
```
20900: 000c            .short 0x000c
20902: 4eba ff46       jsr 0x2084a
20906: 6000 0022       bra 0x2092a
2090a: 302d 000e       movew %a5@(14),%d0
2090e: ea48            lsrw #5,%d0
20910: 323c 037f       movew #895,%d1
20914: 3c80            movew %d0,%fp@
20916: 51c9 fffc       dbf %d1,0x20914
2091a: 4e75            rts
2091c: 3d7c 8f02 0004  movew #0x8F02,%fp@(4)   ; VDP ctrl port
20922: 302d 0010       movew %a5@(16),%d0
20926: 4eba ff22       jsr 0x2084a
2092a: 322d 0012       movew %a5@(18),%d1
2092e: ea49            lsrw #5,%d1
20930: 0041 8000       oriw #0x8000,%d1
20934: 203c 0000 037f  movel #895,%d0
2093a: 3c81            movew %d1,%fp@
2093c: 51c8 fffc       dbf %d0,0x2093a
20940: 4e75            rts
```

PCTAP (once per frame) shows this routine is actually hit in EutherDrive at frame 73:
- `/home/nichlas/EutherDrive/logs/headless_frankenstein_pctap_2091c_2093a.log`
```
[PCTAP] frame=73 pc=0x02091C SR=0x2008 D0=0x0000FFFF D1=0x400080F8 ...
[PCTAP] frame=73 pc=0x02093A SR=0x2000 D0=0x0000037F D1=0x400080F8 ...
```

This confirms the loop executes with `D0=0x037F` (DBF count) and writes `D1` to the VDP data port.


## Update 2026-02-22 (VDP data-port dest at pc=0x2093A)
Added a data-port trace that logs `dest/code/autoinc` at specific PCs. Running with:
```
EUTHERDRIVE_TRACE_VDP_DATA_ADDR_PC=1
EUTHERDRIVE_TRACE_VDP_DATA_ADDR_PC_LIST=0x02093A,0x02093C,0x02093E,0x020940
```
Log: `/home/nichlas/EutherDrive/logs/headless_frankenstein_vdpdatapc_2093a.log`

Result:
```
[VDP-DATA-PC] frame=73 pc=0x02093C dest=0x0000 code=0x01 autoinc=0x02 val=0x80F8
[VDP-DATA-PC] frame=73 pc=0x02093C dest=0x0002 code=0x01 autoinc=0x02 val=0x80F8
...
```

So **at the time of each data-port write in the loop, `g_vdp_reg_dest_address` is 0x0000 and increments by autoinc**. This confirms the VDP destination register has been set to `0x0000` by the time `0x2093A` runs.


## Update 2026-02-22 (Timing: 0x2091C vs 0x02D1EC)
Captured when the two VRAM‑write loops run:

### `0x2091C/0x2093A` init fill
Runs at **frame 73** (see PCTAP):
- `/home/nichlas/EutherDrive/logs/headless_frankenstein_pctap_29ca6.log`

This writes `0x80F8` (seen as `0xF880` in VRAM dumps) to VRAM starting at `0x0000` and `0x1000`.

### Decompression VRAM loop `0x02D1EC`
Runs later at **frame 77**:
- `/home/nichlas/EutherDrive/logs/headless_frankenstein_pctap_2d1ec.log`
- `/home/nichlas/EutherDrive/logs/headless_frankenstein_vdpdatapc_2d1f0.log` shows:
```
[VDP-DATA-PC] frame=77 pc=0x02D1F2 dest=0x0000 code=0x01 autoinc=0x02 val=0x012D
```

### VRAM state over time
VRAM at frame 74 (still init fill):
- `/home/nichlas/EutherDrive/logs/headless_frankenstein_vramdump74.log`
  shows `0xF880` at 0x0000.

VRAM at frame 78 (after decompression loop):
- `/home/nichlas/EutherDrive/logs/headless_frankenstein_vramdump78.log`
  shows `0x2D01` at 0x0000, **matching jgenesis**.

### Implication
The data in VRAM **does become correct by frame 78**, so the long‑term corruption is not coming from the VRAM write path itself. If the UI still shows corrupted graphics after this point, the issue is likely **in the renderer / display timing**, not the VRAM write logic.


## Update 2026-02-22 (Packed row cache)
Suspect: renderer expects **packed 4bpp nibbles** in `g_renderer_vram`, but `pattern_chk` was storing raw planar words, causing striped corruption.

Change made:
- `pattern_chk` now repacks each 4‑byte row into two packed words and **updates both words** of the row in the reverse‑page caches (horizontal/vertical/both flips), instead of only the word corresponding to the current byte address.

File:
- `EutherDrive.Core/MdTracerCore/md_vdp_memory.cs`

### Reverted
This change **was reverted** because it caused other games to become corrupted and did not change the `frankenstein.gen` frame 78 output (hash unchanged across dumps).


## Update 2026-02-22 (Tile fetch vs renderer cache)
Added tile-fetch raw logging to compare VRAM bytes and renderer cache:
- Log: `/home/nichlas/EutherDrive/logs/headless_frankenstein_tilefetch_raw.log`

Example (frame 78, plane B):
```
[TILEFETCH-RAW] nameAddr=0x0314 bytes=01/46 BE=0x0146 LE=0x4601 tileBase=0x28C0 rowBase=0x28D0 rowBytes=000F11CC rv=000F/11CC
```
This shows:
- `g_vram` holds planar row bytes (4 bytes per row).
- `g_renderer_vram` stores the **raw words** for the row (`rv=000F/11CC`), i.e. **no packing**.

Given the renderer extracts **packed nibbles** from `g_renderer_vram`, this is a strong mismatch signal.

### Planar decode test (gated env)
Added `EUTHERDRIVE_RENDER_PLANAR=1` to force planar decode for Plane A/B only (no cache use):
- `ReadPatternPixelPlanarDirect(...)` reads 4 bytes and combines bitplanes.

Result:
- Frame 78 PPM hash did **not** change (still `7ac0f52b3217b593e1983d201f2f89e0`), even with planar decode enabled.
- The headless run produced no console output, so no `PLANAR-PIX` marker seen.

Implication:
- Either the `RenderPlanar` path is not used in the actual render pipeline that produces the headless frame,
  or the frame dump is coming from a different code path than `md_vdp_renderer_line.cs`.


## Update 2026-02-22 (Planar pixel trace + window)
Ran headless with `EUTHERDRIVE_RENDER_PLANAR=1` and the per-pixel trace:
- Log: `/home/nichlas/EutherDrive/logs/headless_frankenstein_pixeltrace2.log`

Trace at frame 78, scanline 100, x=80:
```
[PIX-B] frame=78 scanline=100 x=80 tile=0x146 pal=0 prio=0 rev=0 pic=8 planar=8 packed=0
[PIX-A] frame=78 scanline=100 x=80 tile=0x1C2 pal=1 prio=0 rev=0 pic=15 planar=15 packed=15
[PIX-W] frame=78 scanline=100 x=80 tile=0x0F8 pal=0 prio=1 rev=0 pic=0 planar=0 packed=0
[PIX-FINAL] frame=78 scanline=100 x=80 cmap=0x01F primap=0 shadow=0
```

Interpretation:
- Plane B **definitely differs** between planar and packed decode at this pixel (planar=8 vs packed=0).
- Final color still comes from Plane A (non-zero pixel) so this pixel doesn’t show the B mismatch.
- Window contributes 0 at this pixel.

Conclusion:
Packed decode is wrong for Plane B at least at this location, but the corruption seen on-screen is not yet pinned to Plane B alone (Plane A still dominates this pixel). Need a broader mismatch scan or a pixel where Plane A is transparent.


## Update 2026-02-22 (VDP frame regs, late boot)
Enabled `EUTHERDRIVE_TRACE_VDP_FRAME=1` and ran 120 frames:
- Log: `/home/nichlas/EutherDrive/logs/headless_frankenstein_vdpframe120.log`

By frames 116–120:
- `reg12=0x00` (H32 / 256 width)
- `reg1.display=1`
- `reg2.base=0xC000`, `reg4.base=0x0000`, `reg5.base=0xD400`
- `reg16=0x00` (32x32 plane)

So VDP regs look consistent by the time the game is running; no obvious bad base register.


## Update 2026-02-22 (Snapshot VRAM tile sheet dump)
Generated a planar VRAM tile sheet from the snapshot:
- `/home/nichlas/EutherDrive/logs/snapshots/mdsnap_20260222_110552_427_vram_tiles.ppm`

This renders tiles 0x000–0x1FF as a 64x8 tile sheet (planar decode using CRAM palette). Inspecting this image should tell whether **tile data itself** looks reasonable or if corruption is already present in VRAM.


## Update 2026-02-22 (Planar vs packed mismatch rate)
Added a per-frame mismatch counter between planar decode (from `g_vram`) and packed decode (from `g_renderer_vram`), gated by:
```
EUTHERDRIVE_TRACE_PLANAR_DIFF=1
EUTHERDRIVE_TRACE_PLANAR_DIFF_FRAME=78
```

Result (frame 78):
- Log: `/home/nichlas/EutherDrive/logs/headless_frankenstein_planardiff.log`
```
[PLANAR-DIFF] frame=78 A=48872/57344 B=2701/57344 W=0/57088
```

Interpretation:
- **Plane A**: ~85% of pixels differ between planar and packed decode.
- **Plane B**: smaller but still non-trivial mismatch rate.
- **Window**: no mismatches (at least for this frame).

This strongly indicates the renderer is using a packed cache format that does **not** match the actual VRAM layout (planar bytes). This is likely the root cause of the corruption.


## Update 2026-02-22 (First mismatch pixel + cache row bytes)
Added a one-time log of the first planar/packed mismatch:
- Log: `/home/nichlas/EutherDrive/logs/headless_frankenstein_planardiff3.log`

```
[PLANAR-DIFF-FIRST] frame=78 scanline=0 x=1 layer=A tile=0x1C0 pal=1 prio=0 rev=0 planar=3 packed=9
[PLANAR-DIFF-ROW] tile=0x1C0 row=0 rowBase=0x3800 bytes=F9FF9F99 rv=F9FF/9F99 picAddr=0x1C00
```

Interpretation:
- Row bytes in VRAM are `F9 FF 9F 99` (planar bitplanes).
- Renderer cache words at `picAddr=0x1C00` are `F9FF/9F99` (raw planar words, **not packed**).
- Packed decode therefore yields the wrong pixel (9 instead of 3).


## Update 2026-02-22 (Cache decode change)
Changed the cached decode path to **interpret `g_renderer_vram` as planar**:
- Added `ReadPatternPixelPlanarCached(...)` and used it for Plane A/B/Window when `w_pic_addr >= 0`.
- Kept `ReadPatternPixelPackedCached(...)` only for comparison logs.

Result:
- Pixel trace now shows `pic=3` for the mismatch pixel (planar value), so the **render path uses planar** for cached rows.

Note:
- `headless_frame78.ppm` hash still unchanged after this change, so the visual impact is not yet confirmed by the PPM dump (possibly because scanline 0 is in border/overscan or because the dump path is not reflecting this change). Need a focused screen diff on a visible pixel to confirm.

## Update 2026-02-22 (Frankenstein palette source RAM vs CRAM at PC=0x1FF20)
Ran headless with PCTAP at `0x01FF20` (the `move.l (A2)+, (A6)` loop that writes to VDP data port) and dumped RAM around `0xFFA0C4` and `0xFFA3C4`.

Command:
```
EUTHERDRIVE_HEADLESS_CORE=md \
EUTHERDRIVE_TRACE_PC_TAP_LIST=0x01FF20 \
EUTHERDRIVE_TRACE_PC_TAP_ONCE_PER_FRAME=1 \
EUTHERDRIVE_TRACE_PC_TAP_PEEK_LIST=0xFFA0C4-0xFFA0FF,0xFFA3C4-0xFFA43F \
  dotnet /home/nichlas/EutherDrive/EutherDrive.Headless/bin/Debug/net8.0/EutherDrive.Headless.dll "/home/nichlas/roms/frankenstein.gen" 90 \
  > /home/nichlas/EutherDrive/logs/headless_frankenstein_peek_ffa0.log 2>&1
```

Key observations (from `/home/nichlas/EutherDrive/logs/headless_frankenstein_peek_ffa0.log`):
- At `pc=0x01FF20`, `A2=0xFFA3C4` and the loop writes CRAM directly from RAM (this matches the disassembly at 0x1FF20).
- **RAM at `0xFFA3C4` contains the exact CRAM values we see**, e.g.:
  - frame 78: `00 00 03 33 02 00 02 00 03 00 03 10 03 10 03 20 ...`
  - frame 80: `00 00 05 55 03 00 04 00 04 00 05 10 05 20 05 30 ...`
  - frame 83: `00 00 0B BB 06 00 08 00 09 10 0B 30 0B 40 0B 60 ...`
  - frame 85: `00 00 0F FF 09 00 0B 00 0D 20 0F 40 0F 60 0F 80 ...`
  This looks like a **ramping palette fade** (0x333 → 0x555 → 0x777 → 0xBBB → 0xDDD → 0xFFF).

- **RAM at `0xFFA0C4` (the palette source for the conversion routine) contains non-zero word values**, e.g. at frame 78:
  `00 00 00 00 00 00 01 E0 01 E0 01 E0 00 00 00 00 01 20 ...`

- **Jgenesis comparison (frame 78):**
  - `gen_mem_dump` of working RAM `0xFFA0C4-0xFFA3FF` yields **all zeros**:
    `/tmp/jg_frank_ram_ffa0c4.bin`
  - This implies **jgenesis is not populating or using `0xFFA0C4/0xFFA3C4` at this time**, while EutherDrive *is*.

Implication:
- EutherDrive appears to be executing the `0x1FF20` palette write loop and feeding CRAM from RAM (`0xFFA3C4`), while jgenesis likely follows a different path (or reaches it at a different time).
- The palette values in ED are **internally consistent** (RAM → CRAM), suggesting the corruption is **upstream of CRAM** (logic that populates `0xFFA0C4/0xFFA3C4` or a CPU execution divergence), not a CRAM write bug itself.

Next steps:
- Identify why ED reaches/uses `0x1FF20` at frame ~78 while jgenesis doesn’t (or does with different RAM contents). Possible causes: CPU instruction bug affecting control flow/flags, or a RAM mapping write bug.
- Dump ED vs jgenesis **working RAM** around `0xFFA0C4` at matching frames and check for the earliest divergence.
- Trace writes to `0xFFA0C4` in ED (`EUTHERDRIVE_TRACE_MEM_WATCH_LIST=0xFFA0C4-0xFFA0FF` with a tight limit) to see which PC is populating it.

## Update 2026-02-22 (Who writes 0xFFA0C4 + shift count)
Used RAM-range logging and PCTAP around the palette-diff routine to identify the writer and shift count.

RAM range trace:
```
EUTHERDRIVE_HEADLESS_CORE=md \
EUTHERDRIVE_TRACE_RAM_RANGE=0xFFA0C4-0xFFA0FF \
EUTHERDRIVE_TRACE_RAM_RANGE_LIMIT=2000 \
EUTHERDRIVE_TRACE_RAM_RANGE_NONZERO=1 \
  dotnet /home/nichlas/EutherDrive/EutherDrive.Headless/bin/Debug/net8.0/EutherDrive.Headless.dll "/home/nichlas/roms/frankenstein.gen" 90 \
  > /home/nichlas/EutherDrive/logs/headless_frankenstein_ramrange_ffa0.log 2>&1
```

Findings:
- The values in `0xFFA0C4-0xFFA0FF` are written by **PC=0x020024 / 0x020030 / 0x02003C**.
- This matches the loop at **0x020018–0x02003C**:
  - `move.w (A0)+,D1`
  - `move.w (A2)+,D2`
  - `sub.w D2,D1`
  - `asr.w D3,D1`
  - `move.w D1,(A1)+`

PCTAP at `0x020018` / `0x020020`:
```
EUTHERDRIVE_TRACE_PC_TAP_LIST=0x020018,0x020020
EUTHERDRIVE_TRACE_PC_TAP_ONCE_PER_FRAME=1
```
- At frame 69: **`D3=0x00000007`**, so `asr.w d3,d1` is shifting by **7**.
- That explains why `0xFFA0C4` values are small (`0x001E`, `0x001A`, `0x000C`, ...). It is an intentional fade/scale (not a CRAM write bug).

So the palette values seen at `0xFFA3C4` (and CRAM) are consistent with the fade routine output.
The remaining open question is why jgenesis seems to skip this path (or runs it at a different time).

## Update 2026-02-22 (Frame alignment: ED 0x0BBB/0x0DDD/0x0FFF vs jgenesis RAM)
Ran ED headless with PCTAP peek to locate the first frames where `0xFFA3C4` reaches 0x0BBB/0x0DDD/0x0FFF:
- frame 83: `0x0BBB`
- frame 84: `0x0DDD`
- frame 85: `0x0FFF`

Log:
- `/home/nichlas/EutherDrive/logs/headless_frankenstein_peek_ffa3c4_140.log`

Then dumped jgenesis working RAM at the matching frame (83):
```
cd /home/nichlas/jgenesis
cargo run --release --bin gen_mem_dump -- "/home/nichlas/roms/frankenstein.gen" 83 /tmp/jg_frank_ram_ffa0c4_f83.bin 0xFFA0C4 0xFFA3FF
```
Result:
- jgenesis `0xFFA0C4` and `0xFFA3C4` are **all zeros** at frame 83:
  - `/tmp/jg_frank_ram_ffa0c4_f83.bin` (verified by hexdump)

So the divergence persists even when ED’s palette ramp has reached 0x0BBB/0x0DDD/0x0FFF. This strongly indicates a **control-flow/timing divergence** (ED executing the fade routine and populating RAM while jgenesis does not at the same frame).


## Update 2026-02-22 (Renderer revert + control-flow confirmation)
The latest renderer/window/planar-cache changes were reverted because they caused widespread corruption in other games. Baseline renderer behavior is restored.

Re-ran headless on `/home/nichlas/roms/frankenstein.gen` (no savestate) with PC taps at `0x01FF20` and the palette-diff loop (`0x020018..0x02003C`):
- Log: `/home/nichlas/EutherDrive/logs/headless_frankenstein_pc_1ff20.log`
- `pc=0x01FF20` hits at **frame 68** (palette write loop to VDP data port).
- `pc=0x020018/0x020024/0x020030/0x02003C` hits at **frame 69** with `D3=0x00000007` (shift by 7) and A0/A1/A2 pointing into `0xFF9Fxx` / `0xFFA0C4` / `0xFFA24x`.

This confirms the fade/scale routine is actively running in ED at the same time the RAM palette buffer is being populated.
