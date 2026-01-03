# EutherDrive Memo

## Safe Z80 Boot (default on, 2025-01-??)
- Boot sequence to make Z80 RAM + upload deterministic and reduce boot races.
- Enabled by default; disable with `EUTHERDRIVE_Z80_SAFE_BOOT=0`.
- Steps (all logged as `[Z80SAFE]`):
  - Force BUSREQ on and keep it asserted through upload.
  - Assert Z80 RESET under BUSREQ.
  - Clear Z80 RAM `0x0000..0x1FFF` to `0x00`.
  - Allow 68k upload while BUSREQ is locked.
  - Verify snapshot: log first 16 bytes and XOR.
  - Release RESET (still under BUSREQ), then wait `EUTHERDRIVE_Z80_SAFE_BOOT_DELAY` frames (default 1).
  - Release BUSREQ.
- Mirror/latch hacks (flag/mirror writes) are suppressed while upload is active to avoid polluting Z80 RAM.
- If bus grant times out, Safe Boot falls back to normal behavior (logs `[Z80SAFE] busreq grant timeout`).
- Observed: some previously failing games (Power Rangers, Toki Densetsu) boot with Safe Boot enabled.
- To avoid log spam, do not set `EUTHERDRIVE_TRACE_Z80SIG` or `EUTHERDRIVE_TRACE_Z80REG_DECODE` during A/B tests.

## Z80 bus request semantics (A11100)
- 68K code expects bit0=1 to request/grant the Z80 bus and bit0=0 to release.
- ROMs spin on `btst #0,$A11100` until it clears after a request (e.g. Sonic at
  0x0011C6 loops on `BNE`).
- Fix applied: treat `0x0100` writes as "request/grant" and return 0 on reads
  when granted. Old inverted logic caused infinite loop and no VDP init.
- Implemented in:
  - `EutherDrive.Core/MdTracerCore/md_bus.cs` (write/read busreq semantics)
  - `EutherDrive.Core/MegaDriveBus.cs` (read busreq semantics)
- Do NOT invert busreq semantics again; it breaks boot loops and causes games to hang.

## Shinobi III busreq wait loop
- Shinobi III (EU) can hang in a tight loop at 0x06A4AC/0x06A4B4:
  - `33FC 0100 00A1 1100` (move.w #$0100,$A11100)
  - `0839 0000 00A1 1100` (btst #0,$A11100)
  - `66F6` (BNE back to btst)
- If this loop spins, A11100 reads are still returning bit0=1 (bus not granted).
- Verify busreq writes are seen and A11100 reads drop to 0 after the write.
  If not, consider accepting bit0 or bit8 for word writes in busreq handling.

## Z80 window odd-byte reads (A00000..A0FFFF)
- Some games poll odd Z80-window addresses like A01FFD for mailbox/handshake bits.
- 68k byte reads on odd Z80-window addresses should map to the next even byte (addr+1).
- Fix: apply odd-to-next mapping only for reads; keep writes unchanged to avoid corruption.
- Controlled by `EUTHERDRIVE_Z80_ODD_READ_TO_NEXT` (default on) in:
  - `EutherDrive.Core/MdTracerCore/md_bus.cs`
  - `EutherDrive.Core/MegaDriveBus.cs`

## Z80 window UDS-only writes (A00000..A03FFF)
- The Z80 RAM is 8-bit on the upper data bus; odd-byte writes from the 68k should be ignored.
- When enabled, word/long writes only store the high byte of each word to the even address.
- Controlled by `EUTHERDRIVE_Z80_UDS_ONLY=1` in:
  - `EutherDrive.Core/MdTracerCore/md_bus.cs`

## Z80 IM2 interrupt vector
- IM2 is now handled by fetching a vector pointer at `(I << 8) | vector`.
- Default vector is `0xFF` (floating bus); override with `EUTHERDRIVE_Z80_IM2_VECTOR` if needed.

## VDP strict access
- Invalid VDP accesses throw by default for debugging.
- Disable strict mode with `EUTHERDRIVE_VDP_STRICT=0` to log and ignore invalid reads/writes.

## ROM write protection
- Writes to ROM space are ignored by default to avoid corrupting code/data.
- Controlled by `EUTHERDRIVE_ROM_WRITE_PROTECT` (default on).

## Memory watch (debug)
- Set `EUTHERDRIVE_TRACE_MEM_WATCH=FFB154` (hex) to log 68k accesses at that address.
- By default only logs on value change or writes; set `EUTHERDRIVE_TRACE_MEM_WATCH_ALL=1` to log every access.
- Increase log budget with `EUTHERDRIVE_TRACE_MEM_WATCH_LIMIT=1024` (0 = unlimited).
- Z80 reads/writes to watched 68k addresses log as `[Z80->68K]` with Z80 PC and bank.
 - Z80 address watch: `EUTHERDRIVE_TRACE_Z80_ADDR=B154` logs `[Z80ADDR]` when Z80 touches that address (shows bank + mapped 68k addr).
 - Limit Z80 addr logs with `EUTHERDRIVE_TRACE_Z80_ADDR_LIMIT=128` (0 = unlimited).
 - Limit Z80 window bus logs with `EUTHERDRIVE_TRACE_Z80WIN_LIMIT=256` (0 = unlimited).

## PC watch (debug)
- Enable with `EUTHERDRIVE_TRACE_PC=1`.
- Override the default range (`0x000320..0x000340`) with `EUTHERDRIVE_TRACE_PCWATCH_START` and `EUTHERDRIVE_TRACE_PCWATCH_END` (hex).
- Altered Beast handshake range: `0x00A7D0..0x00A840` (FFB154 loop).
- Altered Beast reset/busreq init range: `0x0048E0..0x004920`.

## Aladdin no-audio notes (2024-12-31)
- Symptom: audio mix is flat zero (`psgNZ=0`, `ymNZ=0`, peaks 0) while the game runs.
- Busreq semantics OK: `[m68k] BTST ... A11100 val=0x00` appears; no stuck busreq wait.
- Z80 is executing the driver: RAM contains non-zero image; `Z80STEP` advances from 0x0000 into 0x08xx.
- PSG is being muted: Z80 writes to 0x7F11 with 0x9F/0xBF/0xDF/0xFF (max attenuation).
- YM activity is limited to TL regs: Z80 writes 0x40/0x44/0x48/0x4C with 0x7F; no key-on (addr 0x28), no `[KEY28]` / `[YMIRQ]` seen.
- Mailbox traffic uses the wide range: `MBXW68K-W` shows non-zero writes at 0x1B40+ while `MBXW68K` stays zero; keep `EUTHERDRIVE_Z80_MBX_WIDE` enabled (default on).
- Z80 is confirmed reading wide mailbox values: `Z80MBXRD-W` shows reads of 0x1B40..0x1B42 with values matching `MBXW68K-W`.
- Z80 wide mailbox read-all trace works: `EUTHERDRIVE_TRACE_Z80MBX_WIDE_READ_ALL=1` logs `Z80MBXRD-WA`, showing reads at 0x1B40..0x1B47 and 0x1B1A..0x1B1F (0x1B1C appears 0 in samples).
- Optional workaround: `EUTHERDRIVE_Z80_MBX_WIDE_CMD_MIRROR=1` mirrors 68k writes at 0x1B20/0x1B21 to 0x1B1C/0x1B1D for drivers polling 0x1B1C.
- Optional latch: `EUTHERDRIVE_Z80_MBX_WIDE_CMD_LATCH=1` returns the last non-zero 0x1B20 mirror on Z80 reads of 0x1B1C if RAM is still 0.
- Trace helper: `EUTHERDRIVE_TRACE_Z80MBX_WIDE_CMD=1` logs Z80 writes to 0x1B1C/0x1B1D as `[Z80MBXWR-WC]`.
- Z80 is active (not halted): `Z80Stats` shows `active=1`/`halt=0` and PC cycling around 0x0DF7/0x0DFB.
- No Z80 bank-register traces observed (`Z80BANKREG`/`Z80BANKRD`) even with `EUTHERDRIVE_TRACE_Z80BANK=1`.
- `MBXSYNC` stays zeroed in logs while `Z80MBXRD-W` shows activity, so this driver likely uses the wide mailbox path instead of the narrow sync path.

## MDTracer compatibility (2024-12-31)
- New `EUTHERDRIVE_MDTRACER_COMPAT` flag (default off as of 2025-01-02) makes the Z80 window behave like MDTracer:
  - 68k reads/writes at `0xA00000..0xA0FFFF` call `md_z80.read8/16/32` and `write8/16/32` directly (no busreq gating, no mailbox mirroring).
  - Z80 mailbox mirror/latch/shadow logic is bypassed.
  - YM2612 reads use `ym2612.read8` for all 0x4000..0x5FFF reads (no `ReadStatus` special-case).
- Z80 banked ROM accesses use `md_m68k.read8/write8` directly.
- Note: when compat is on, mailbox tracing (`Z80MBX*`, `MBXSYNC`) is bypassed; keep compat off for mailbox debugging.

## Altered Beast audio handshake (2025-01-01)
- 68k uses a RAM flag at `FFB154` as a handshake; it writes `0x00` then `0x03`, then spins reading `FFB154` at `pc=0x00A828` (never changes in log).
- Z80 never touches `FFB154` in `/tmp/ed.log`: no `[Z80->68K]` or `[Z80ADDR]` hits for that address while the 68k spins.
- `rom_start.log` PCWATCH shows the loop bytes at `0x00A81C` (`move.b D0,$B154` -> `move.b $B154,D0` -> `bne`) and the Z80 reset/busreq init routine at `0x0048F2`.
- `rom_start.log` MEMWATCH shows repeated 68k writes only (`pc=0x00A7DE` writes `0x00`, `pc=0x00A820` writes `0x03`); no Z80 reads/writes to `B154` were logged.
- Wide mailbox path works: 68k writes `MBXW68K-W` to `0x1B00..0x1B7F` and Z80 reads them (`Z80MBXRD-WA` at `pc=0x007E`) with matching non-zero values.
- Narrow mailbox writes (`MBXW68K` at `0x1B80..0x1B8F`) are also present, but the handshake still stalls on `FFB154`.
- Latest `/tmp/ed.log` run shows only `Z80RESET` + `Z80BANKREG` churn; bank bases cycle through values like `0x7F8000`, `0xBF8000`, down to `0x000000` with no `0xFF8000` seen, so the Z80 never maps the `0xFFB154` area via banked window in that run.

## Altered Beast PCM buffer / DAC tracing (2025-01-01)
- New trace controls added:
  - `EUTHERDRIVE_TRACE_Z80_IO` + `EUTHERDRIVE_TRACE_Z80_IO_LIMIT` (Z80 reads 0x6000..0x7FFF).
  - `EUTHERDRIVE_TRACE_Z80_BOOT_IO=1` + `EUTHERDRIVE_TRACE_Z80_BOOT_IO_LIMIT` (first N Z80 instructions after reset).
  - `EUTHERDRIVE_TRACE_Z80WIN_RANGE_START/END/LIMIT` (68k window writes into Z80 RAM; log tag is `[Z80WIN-W]` with W8/W16/W32, uds/lds, exact bytes, busReq/busAck/reset/blocked).
  - `EUTHERDRIVE_TRACE_Z80_RAM_WRITE_RANGE_START/END/LIMIT` (`[Z80RAMWR]` with pc/hl/de/bc/sp + last read info).
  - `EUTHERDRIVE_TRACE_Z80_RAM_READ_RANGE_START/END/LIMIT` (`[Z80RAMRD]` with pc/hl).
  - `EUTHERDRIVE_TRACE_YM_DAC_BANK=1` + `EUTHERDRIVE_TRACE_YM_DAC_BANK_LIMIT` (DAC writes with last Z80 read + bank info).
  - `EUTHERDRIVE_TRACE_Z80YM_LIMIT` and `EUTHERDRIVE_TRACE_KEY28_LIMIT` to cap noisy YM/key-on logs.
  - `EUTHERDRIVE_TRACE_DACRATE=1` (per-frame DAC cadence: writes, avg/min/max Z80 cycles between writes, estimated Hz).
- Combined run with all three range traces shows the PCM buffer path is working:
  - `[Z80WIN-R]` shows 68k writes to `0x0DC8..0x101E` (PCM buffer window), `blocked=0`, `busReq=1`, `busAck=0`, `reset=0`.
  - `[Z80RAMWR]` shows the same values landing in Z80 RAM at those offsets.
  - `[Z80RAMRD] pc=0x007E` reads non-0x7F/0xFF data from `0x0DC8..0x101E`.
- `YMDACBANK` shows DAC values sourced from `last=0x1FF8` (Z80 RAM latch) with non-center values, so the DAC pipeline is active.
- Combined run (same log) confirms buffer→read path:
  - `[Z80WIN-R]` logs the 68k window writes to `0x0DC8..0x101E` (`blocked=0`).
  - `[Z80RAMWR]` shows those values landing in Z80 RAM (some writes logged at `pc=0x0000` while Z80 is reset, then later real Z80 PCs appear).
  - `[Z80RAMRD] pc=0x007E` reads non-0x7F/0xFF values from the same buffer range.
- Current suspicion: PCM buffer is filled and read correctly; issue is likely sample source (wrong ROM bank/offset) or timing (DAC rate/step), not a dead read path.
- Next steps:
  - Correlate `Z80RAMWR addr=0x1FF8` with `YMDACBANK last=0x1FF8` to confirm buffer→DAC sequencing in the same run.
  - Trace banked ROM reads during PCM fill to verify bankBase/shift points at the correct sample data.
- Use `EUTHERDRIVE_TRACE_DACRATE=1` to confirm sample rate vs hardware expectations.
- Note: runs with `EUTHERDRIVE_TRACE_DACRATE=1` still produced no `[DACRATE]` lines in `rom_start.log` or `/tmp/ed.log`; added an early `[DACRATE] env='...' enabled=...` banner at YM2612 init to verify env visibility (`md_music_ym2612_init.cs`).
- Banner appears in console (`[DACRATE] env='1' enabled=True`), but no per-frame `[DACRATE] frame=...` lines yet; likely `FlushDacRateFrame` is not firing or its output is not reaching the log sink.
- In the combined trace run, `Z80RAMRD pc=0x007E` reads `0x0DC8..0x101E` with varied values (not just 0x7F/0xFF), matching the DAC stream range, so the buffer→read path looks sane.
- Follow-up run (mailbox + DAC copy):
  - `Z80RAMRD pc=0x007E` reads non-zero bytes from `0x1B00..0x1B7F`, so mailbox data reaches Z80.
  - `Z80RAMWR pc=0x009B addr=0x1FF8` shows `hl` walking `0x01B3..0x027E` with non-zero values, so the copy loop is feeding the DAC latch.
- PCM buffer read stride check:
  - `Z80RAMRD pc=0x007E` reads `0x0DC8, 0x0DC9, 0x0DCA, 0x0DCB...` (stride +1, not +2), so Z80 is consuming every byte in the buffer range.
- Z80 repacks the buffer before playback:
  - 68k writes show interleaved `0x00` on odd addresses (e.g. `Z80WIN-W z80=0x0EE0..0x0EEF`), but Z80 later overwrites the full range at `pc=0x00AF` with non-zero bytes.
  - `Z80RAMWR pc=0x00AF` covers `0x0DC8..0x0EEF`, and `Z80RAMRD pc=0x007E` reads those rewritten values, so byte-lane/endian looks OK; the "zero‑every‑other‑byte" pattern is not what the DAC ultimately sees.
- Added Z80 fill tracer for ROM→RAM copy verification:
  - New env limit `EUTHERDRIVE_TRACE_Z80_RAM_FILL_LIMIT` (defaults to 64 like other watch limits).
  - When `EUTHERDRIVE_TRACE_Z80_RAM_WRITE_RANGE_START/END` is set, `[Z80FILL]` logs start `ram=...`, `len=...`, inferred `rom=...`, `sumRam`, `sumRom`, and 16‑byte dumps (`ram16`, `rom16`), plus `romAvail` if ROM data is truncated.
  - Uses last banked ROM read to infer the source; sequences must be contiguous in both ROM and RAM. Logs `reason=out|nobank|nonseq|rangeend` on finalize.
- DAC rate captured:
  - `DACRATE` shows steady ~`4176.83 Hz` with `avg=857` cycles and `writes=69/70` per frame (starts around frame 347, then stops after the sample).
  - If the expected sample rate is ~8 kHz, current playback is roughly half-speed.
- Note: `EUTHERDRIVE_TRACE_Z80WIN_RANGE_*` logs 68k window writes with the `[Z80WIN-W]` tag; includes W8/W16/W32 + uds/lds + per-byte addresses, and `busReq=1` with `busAck=0` still indicates bus granted to 68k.
- Change: `md_music_ym2612_core.cs` `dac_control()` now always processes `g_reg_2a_dac_data` (removed `!= 0` guard) to avoid skipping 0x80 samples; no audible improvement reported yet.
- Current hunch: not DAC rate; possible data-format or mix/handoff issue between Z80 DAC feed and the audio sink.
- DAC conversion check:
  - `g_reg_2a_dac_data = (in_val - 0x80) << DAC_SHIFT` in `md_music_ym2612_regster.cs` (signed-center + scale).
- Added `EUTHERDRIVE_DAC_INPUT_SIGNED=1` to treat DAC input as signed (`(sbyte)in_val << DAC_SHIFT`) vs unsigned center; `[YM-DAC]` log now reports `center=0x00` or `0x80` and `nonCenter` accordingly.
  - `dac_control()` was using a `uint` cast; switched to signed math (`(g_reg_2a_dac_data << 15) - g_dac_high_level`) to avoid wrap when `g_dac_high_level` goes negative.
- Debug patch: bypass DC-blocking in `dac_control()` and return raw `g_reg_2a_dac_data` (keeps `g_dac_high_level` update for later comparison).
- Added YM2612 busy-bit emulation + trace in `md_music_ym2612_regster.cs`:
  - `EUTHERDRIVE_EMULATE_YM_BUSY=1` drops YM writes while busy; `EUTHERDRIVE_TRACE_YM_BUSY=1` logs `[YM-BUSY]` set/busy/drop/status; `EUTHERDRIVE_TRACE_YM_BUSY_LIMIT` limits logs.
  - Busy length defaults to ~32us derived from `Z80_CLOCK`; override with `EUTHERDRIVE_YM_BUSY_Z80_CYCLES=<n>`.
- Busy emulation run (`EMULATE_YM_BUSY=1`, `TRACE_YM_BUSY=1`) produced no audible change:
  - Only early drops at `pc=0x0000` during reset init; later writes mostly show `[YM-BUSY] set` with no drops.
  - At `pc=0x00A2` there are occasional drops for rapid `addr=0x28` key writes (alternating), but DAC writes (`addr=0x2A`) are not dropped.
  - No `[YM-BUSY] status` lines observed, suggesting status polling isn’t happening during busy (or reads are not logged because they aren’t busy).
- Follow-up to confirm busy/status and sample data:
  - Log YM status reads even when not busy to confirm whether Z80 ever polls the status register.
  - Force longer busy via `EUTHERDRIVE_YM_BUSY_Z80_CYCLES=<n>` to provoke drops and see if DAC writes start dropping.
  - Compare `[Z80FILL]` `sumRam/sumRom` and 16-byte dumps to verify the ROM source matches the Z80 buffer.
  - Log min/max/mean or a small histogram for DAC bytes to validate the PCM format (unsigned 0x80 center vs signed).
- YM status trace (`EUTHERDRIVE_TRACE_YM_STATUS=1`, limit 512) shows a burst of reads with `pc=0x4000..0x41FF`, `status=0x00`, `busy=0`, `clear=0`; confirms status reads occur (at least early), but no busy overlap in this window.
- DAC stats (`EUTHERDRIVE_TRACE_DAC=1`) show min/max reaching `0x00/0xFF`, mean around `0x7C..0x7D`, and hist16 clusters near mid-range with some extremes, so data isn’t constant silence but has slight DC bias and occasional saturation.
- Z80 YM trace (`EUTHERDRIVE_TRACE_Z80YM=1`) during playback shows only write pairs `addr=0x4000 val=0x2A` then `addr=0x4001 val=<sample>`; no status reads interleaved with DAC writes.
- Status-read burst aligns with `pc=0x43CE..0x43FF` while reading `addr=0x4000/0x4002` (YM status ports), suggesting those reads happen early and not as part of the playback loop.
- DAC loop writes show `pc=0x009D` (addr set `0x2A`) and `pc=0x00A2` (data write), confirming the DAC stream is driven by real Z80 code and not a log artefact.
- Attempted Z80FILL for `0x0DC8..0x101E` produced no `[Z80FILL]`/`[Z80RAMWR]` logs in that run; likely no Z80 writes to that range (buffer might be filled by 68k or different range used).
- Full range traces for `0x0DC8..0x101E` (`[Z80RAMRD]`, `[Z80RAMWR]`, `[Z80FILL]`, `[Z80WIN-W]`) were empty in the latest run, so the PCM buffer likely moved or that sample path didn’t touch the range.
- `YMDACBANK` + `Z80RAMWR` trace shows DAC samples come from `last=0x1FF8` (Z80 latch) with `pc=0x00A2`, no `m68k=...` suffix → DAC feed is from Z80 RAM, not banked ROM reads.
- `Z80RAMWR` at `pc=0x0CFC` reads `hl=0x0DC8..0x0DD2` with alternating `0x80/0x00/0xA0/0x00...` suggesting zero‑every‑other‑byte source data (likely 16‑bit or interleaved).

## YM2612 status register note (hardware reference)
- YM2612 status is only valid on reads from `0xA04000` (68k) / `0x4000` (Z80).
- Only bits 7, 1, 0 are meaningful; bits 6..2 vary by revision.
- If the driver polls other addresses or expects stable bits 6..2, it can stall or drift on some hardware.
- Potential emulator check: mask status reads to `0x83` and return status only for `0x4000/0x4002`.

## Hardware delay note (audio)
- Real hardware requires short delays between YM2612 writes, and after Z80/YM reset (e.g., 192 68k cycles).
- Emulation often skips these waits; code that relies on them may behave differently.
- Existing knob: `EUTHERDRIVE_EMULATE_YM_BUSY=1` + `EUTHERDRIVE_YM_BUSY_Z80_CYCLES=<n>` can simulate YM busy time (see earlier YM busy notes).
- Potential follow-up: add an env-controlled post-reset delay gate for Z80/YM if needed.
 - Added: `EUTHERDRIVE_Z80_RESET_HOLD_Z80_CYCLES=<int>` holds Z80 execution for N cycles after reset deassert; log with `[Z80RESET-HOLD]`.
 - Result: Z80 reset hold (e.g. 1024 cycles) shows repeated `[Z80RESET-HOLD]` events but still no audio; suggests reset/busreq is toggling frequently and Z80 never reaches the sound loop.
- Later loop at `pc=0x009B` reads `hl=0x0180..` with varied values and writes to `0x1FF8`; this appears to be the actual DAC stream buffer (post‑repack).
- Loops at `pc=0x0E72/0x0E81` read `hl=0x0FF0..0x101E` with mostly `0x7F/0xFF`, likely silence/fill or a second buffer.
- Range trace shows:
  - `pc=0x0000` writes to `0x0180..0x01BF` look like boot‑time code/data init while Z80 is reset (not audio).
  - `pc=0x0CFB` reads `0x0DC8..0x0DD2` with zero‑every‑other‑byte pattern early; later `pc=0x007E` reads the same range with dense PCM‑looking values.
  - `pc=0x00AF` overwrites `0x0180..0x01BF` with the actual PCM stream bytes that later feed the DAC via `0x009B`.
- Z80 window trace confirms the 68k is filling `0x0DC8..0x101E` via `A00000` window:
  - `[Z80WIN-W] pc68k=0x004958` writes `z80=0x0DC8..` with alternating UDS/LDS; `busReq=1 busAck=0 reset=0` (bus granted to 68k).
  - Matching `[Z80RAMWR] pc=0x0000` lines are a side‑effect of the window write while Z80 is idle/reset; treat `pc=0x0000` here as non‑Z80.
  - No `[Z80FILL]` because this copy is 68k→Z80 window, not Z80 banked ROM reads.
- Added `EUTHERDRIVE_TRACE_Z80WIN_REGS=1` to append `a0/a1` to `[Z80WIN-W]` so we can infer the 68k source pointer during buffer fills.

## Altered Beast 0x0065 flag probe (2025-01-02)
- Added Z80 flag override to source `0x0065` from mailbox RAM:
  - `EUTHERDRIVE_Z80FLAG65_READ_FROM_MBX=1`, `EUTHERDRIVE_Z80FLAG65_READ_SOURCE=0x1B8F`, log `[Z80FLAG65-READ]`.
  - New masks: `EUTHERDRIVE_Z80FLAG65_READ_AND` and `EUTHERDRIVE_Z80FLAG65_READ_OR` (apply AND then OR).
- DDCB trace confirms the opcode at `0x0DDC` is `RET NZ` (`0xC0`):
  - When `bit2=1`, `Z=0`, `RET NZ` is taken and execution returns to `0x0E75`.
  - Forcing the JR path (`EUTHERDRIVE_Z80FLAG_FORCE_JR=1`) jumps to `0x0DEA/0x0DF0`, but still returns quickly via `RET` at `0x0DEE`/`0x0E84`; no new mailbox/YM activity observed and still no audio.
- Implication: to reach the non-return path, `bit2` must be **0** (`Z=1`), so we need to clear bit2 rather than OR it in.
  - Try `EUTHERDRIVE_Z80FLAG65_READ_AND=0xFB` (clear bit2) and avoid OR=0x04.
- Narrow/wide mailbox mirroring into `0x0060..0x006F` and `EUTHERDRIVE_FORCE_B154_READ=0` can freeze (SEGA logo or black screen), so keep those off for now.
- Clearing bit2 (`EUTHERDRIVE_Z80FLAG65_READ_AND=0xFB`) yields:
  - `[Z80DDCB] ... bit2=0 Z=1 nextpc=0x0DDD` and `RET NZ` is **not** taken.
  - No obvious follow‑on mailbox/YM activity yet; need to trace the code path from `0x0DDD`.
- Added Z80 PC range tracer:
  - `EUTHERDRIVE_TRACE_Z80_PC_RANGE_START/END/LIMIT` logs `[Z80PC]` with opcode bytes and nextpc to map the post‑0x0DDD path.
- First `Z80PC` run with bit2 cleared shows `op=0x00` (NOP) for `pc=0x0DD8..0x0E13` in frame 0:
  - Indicates Z80 RAM at those addresses is **zero**, so the driver code is not present at that moment.
  - Likely the Z80 is executing empty RAM before/without a successful 68k upload.
  - Next check: enable `EUTHERDRIVE_TRACE_Z80WIN_RANGE_START/END` around `0x0D00..0x0E50` to see whether the 68k ever writes those bytes, and inspect `blocked`/`busReq` in `[Z80WIN-W]`.
- Z80 upload trace shows 68k **does** write non‑zero bytes into `0x0D00..` while `busReq=1` and `busAck=0`, so the code is loaded but the Z80 appears to run earlier than the upload.
- Added debug reset on BUSREQ release:
  - `EUTHERDRIVE_Z80_RESET_ON_BUSREQ_RELEASE=1` resets the Z80 once when BUSREQ deasserts to restart from `PC=0` after upload.
  - Limit with `EUTHERDRIVE_Z80_RESET_ON_BUSREQ_RELEASE_LIMIT`.
- Z80 still executes NOPs at `PC=0x0000` even after upload; likely no boot stub at 0x0000.
- Added debug boot‑jump patch:
  - `EUTHERDRIVE_Z80_BOOT_JP=1` writes `JP <target>` to `0x0000` on Z80 reset.
  - `EUTHERDRIVE_Z80_BOOT_JP_TARGET=0x0D00` (default) to jump into the uploaded driver code.

## De Cap Attack boot race (2025-01-03)
- De Cap Attack is highly timing sensitive: 8/10 boots freeze at the SEGA logo, but when it does boot it is 100% stable for the whole run.
- Only reliable kick is toggling region override in the UI (Auto <-> US/JP/EU). This works even when the region bits are effectively the same, so the **extra Reset** is the likely fix, not the region bits themselves.
- A delayed full reset (after frames) causes wrong colors/lockups (VDP state gets wiped after render). Avoid full reset after rendering starts.
- Z80 boot stub is uploaded by the 68k into low Z80 RAM:
  - `0x0000: F3 ED 56 C3 69 00 ...` (DI/IM + `JP 0x0069`).
  - `0x0038..0x0061` contains real code; `0x0066` has a `JP 0x0000`.
  - Dump at `0x0020..0x0080` shows loops that touch `0x6000` (bank reg) and YM ports.
  - Entry point for this driver is likely `0x0069` (from the stub).
- Bad boots show BUSREQ/RESET spam and Z80 stalls with `busReq=1`, `active=0`, PC stuck (e.g. `0x1116`), then `Z80Stats` become `instr=0`.
- Good boots show Z80 PCs spanning normal code ranges; after the logo it eventually reaches a stable audio path.
- Tweaks tried with no stabilization: `EUTHERDRIVE_Z80_CYCLES_MULT != 1.0` (1.0 is best), `EUTHERDRIVE_Z80_RUN_PER_LINE`, `EUTHERDRIVE_Z80_RUN_BEFORE_M68K`, `EUTHERDRIVE_Z80_INTERLEAVE_SLICES`, and `EUTHERDRIVE_Z80_RESET_ASSERT_ON_BOOT` + `EUTHERDRIVE_Z80_RESET_HOLD_Z80_CYCLES`.
- Implemented a **boot recover** that only resets the Z80 (keeps VDP state intact):
  - `EUTHERDRIVE_BOOT_RECOVER_STALL_FRAMES=<n>` triggers when Z80 is inactive, `busReq=1`, `reset=0`, and PC stays constant for N frames.
  - `EUTHERDRIVE_BOOT_RECOVER_WINDOW_FRAMES=<n>` limits this to early boot.
  - `EUTHERDRIVE_BOOT_RECOVER_LOG=1` prints `[BOOTRECOVER]`.
  - This uses a Z80‑only reset (no VDP reset) to avoid color corruption.
- Boot recover alone still does **not** remove the need for manual region toggle; it may not be firing under the exact failing condition yet.
- UI note: region override toggling calls `Reset()` immediately (`MainWindow.ApplyRegionOverrideToCore`), which is likely the actual stabilizer.
  - Logs `[Z80BOOT-JP]` once per reset (limit via `EUTHERDRIVE_Z80_BOOT_JP_LIMIT`).
- Boot‑jump alone still hits NOPs at `0x0D00`, likely because the Z80 runs before upload completes.
- Added upload‑triggered PC force:
  - `EUTHERDRIVE_Z80_FORCE_PC_ON_UPLOAD=1` arms a forced PC jump when a 68k write hits the Z80 window range.
  - Defaults: range `0x0D00..0x0E50`, target `0x0D00`.
  - Logs `[Z80PC-ARM]` on upload and `[Z80PC-FORCE]` when applied.
- `[Z80PC-FORCE]` now includes `bytes=` dump, and PC‑range trace budget resets when forcing so we see post‑force execution even if early logs consumed the limit.
- With force‑on‑upload active, `[Z80PC-FORCE]` dumps non‑zero code at `0x0D00` (e.g., `10 F9 C1 E1 C9 C5 F5 06`).
  - First opcode `0x10` (`DJNZ`) jumps to `0x0CFB`, so the execution path immediately leaves `0x0D00`.
- At `0x0CFB` the opcode is `0x7E` (`LD A,(HL)`), followed by `CALL 0x1163`:
  - Need `HL` value and what `0x1163` reads/polls (likely a flag/mailbox gate).
  - Next step: enable `EUTHERDRIVE_TRACE_Z80_RAM_READ_RANGE_*` and trace `0x1160..0x1180` to see the subroutine flow.
- Trace shows `HL=0x0000` at `pc=0x0CFB` and `val=0xF3` read from `0x0000` (`LD A,(HL)`).
- PC trace for `0x1160..` currently shows NOPs at frame 0 (pre‑upload); must confirm opcodes at frame 43 (post‑upload).
 - Post‑upload trace (frame 43) confirms `0x1163` is real code (not NOPs) and writes to `0x6000` repeatedly:
   - Opcode pattern shows `LD (0x6000),A` (`0x32 00 60`) in a loop, so the driver is touching the bank register.
   - `Z80PC` path is `0x0D00` (`DJNZ -7`) → `0x0CFB` (`LD A,(HL)`) → `CALL 0x1163`.
 - `Z80RAMRD` shows `HL=0x0000` at `0x0CFB` with `val=0xF3`, suggesting the forced entry at `0x0D00` skips required init (registers not set).
 - Next: trace 68k writes to `0x0000..0x01FF` to see if a boot stub is uploaded; if not, try forcing PC to `0x1100` or decode the upload blob for a JP target.
 - `Z80PC` logs now include `A/BC/DE/HL/SP` for register sanity when tracing entrypoints.
## Mailbox injection debug (2025-01-02)
- Added 68k mailbox injection to test the command chain:
  - `EUTHERDRIVE_INJECT_MBX=1` writes a non‑zero byte once to the Z80 mailbox address (defaults to the polled addr, `0x1B8F`).
  - Optional `EUTHERDRIVE_INJECT_MBX_FRAME=<n>` (default 10) picks the frame to inject.
  - Logs: `[MBXINJ-ENV]` (env read), `[MBXINJ-ARM]` (armed), `[MBXINJ]` (actual write).
- Injection defaults changed for command-byte probing:
  - Default value is now `0x83` and the injected byte clears after Z80 reads it (`[MBXINJ-ACK]` + `[MBXINJ-CLR]`).
- Added mailbox edge log for command visibility:
  - `[MBXEDGE]` logs when `0x1B8F` changes or when Z80 reads non-zero.
  - Includes a `0x1B80..0x1B8F` dump plus `mode`/`wide`/`compat` status (emits when mailbox tracing is enabled).
- Added mailbox source trace for `1B8F`:
  - `EUTHERDRIVE_TRACE_MBX_SRC=1` logs `[MBXSRC]` with `a0`, computed `src` (`a0+0x0F`) and a peeked `srcVal`, so we can confirm if 68k ever sets a non-zero command byte.
- Added decisive mailbox byte dump for `1B8F` writes:
  - `EUTHERDRIVE_TRACE_MBX_SRC_DUMP=1` logs `[MBXBYTE]` when a 68k write hits `0x1B8F` (W8/W16/W32).
  - Includes `pc68k`, opcode words at `pc` and `pc-2/4/6/8`, `uds/lds`, `busReq/busAck/reset/blocked`, `a0..a7`, `d0..d7`, write value/bytes, and a `pcdump` (33 bytes from `pc-0x10`).
  - Adds `a5dump` and `a6dump` (48 bytes from `a5-0x10` and `a6-0x10`) and a one-time `[MBXLOOP]` line when `a5/a6` change to show copy loop setup.
  - Limit with `EUTHERDRIVE_TRACE_MBX_SRC_DUMP_LIMIT` (default 16).
- New finding from `[MBXBYTE]`:
  - The command byte is copied by `MOVE.B (A5)+,(A6)+` (`0x1CDD`) in a loop (`DBRA D0,-0x22`), so the source is `A5`, not `A0`/`D` regs.
  - `A6` is post-increment: logged `a6=0xA01B90` means the actual write hit `0xA01B8F` (dest is correct, mailbox base).
  - The written value matches `A5-1`, proving the non-zero command comes from the source buffer; zeros are because the source table at `A5` is zero at that time.
  - This points away from bus/byte-lane visibility issues and toward the source buffer contents/selection timing.
- Added Z80 mailbox data-read logger (non-opcode-fetch):
  - `EUTHERDRIVE_TRACE_Z80MBX_POLL_DATA=1` logs `[Z80MBX-DATA]` only for data reads in `0x1B80..0x1B8F` (skips opcode fetch), and only on changes/non-zero values.
  - Limit with `EUTHERDRIVE_TRACE_Z80MBX_POLL_DATA_LIMIT`.
- Added generic Z80 read-range logger:
  - `EUTHERDRIVE_TRACE_Z80_RD_RANGE_START/END/LIMIT` logs `[Z80RD]` for any Z80 read address range.
  - Includes `frame`, `pc`, `addr`, `val`, and `opcode=1/0` (opcode fetch detection).
- `Z80INT` trace now includes `frame` for correlation.
- New finding from 0x0065 range trace:
  - Z80 does data reads at `0x0065` (`opcode=0`) with `pc=0x0DD8/0x0DEA`, so the driver is polling Z80 RAM, not mailbox.
  - 68k writes to `A00065` are `0x00` (`[Z80WIN-W]`), and Z80 writes to `0x0065` are also `0x00` (`[Z80RAMWR]`).
  - This explains the silent FM/PSG: the polled flag at `0x0065` never goes non‑zero in the current flow.
- Added focused 0x0065 tracing:
  - `EUTHERDRIVE_TRACE_Z80_0065=1` logs `[Z80RD65]` (data reads only, edge/non‑zero) and `[Z80WR65]` writes to `0x0065`; limit with `EUTHERDRIVE_TRACE_Z80_0065_LIMIT`.
  - `[Z80RD65]` includes a `dump=0x0060:..` of `0x0060..0x0070` when the value is non‑zero.
  - `EUTHERDRIVE_TRACE_Z80_0065_WIN=1` logs `[Z80WIN65]` for 68k writes into `A00060..A0006F` (edge/non‑zero); limit with `EUTHERDRIVE_TRACE_Z80_0065_WIN_LIMIT`.
- Post-flag trace (after `0x0065` goes non-zero):
  - `EUTHERDRIVE_TRACE_Z80_POSTFLAG=1` arms a one-shot trace when `0x0065` transitions `0x00 -> nonzero` (data read).
   - Logs `[Z80PF-ARM]` on arm (now includes `pcdump` bytes around `pc`) and `[Z80RDPF]` for the next N data reads within a range.
   - Range defaults to `0x0060..0x01FF`; override with `EUTHERDRIVE_TRACE_Z80_POSTFLAG_START/END`.
   - Limit with `EUTHERDRIVE_TRACE_Z80_POSTFLAG_LIMIT` (default 64; <=0 = unlimited).

## Z80 0x0065 flag + DDCB BIT loop (2025-01-02)
- Z80 driver polls data at `0x0065` (pc `0x0DD8/0x0DEA`, `opcode=0`), not the mailbox.
- 68k upload writes `A00060..A0006F` repeatedly and clears `0x0065` to `0x00`, preventing the flag from sticking.
- Added mirror test:
  - `EUTHERDRIVE_MIRROR_MBX_TO_Z80FLAG=1` mirrors the command byte into Z80 RAM `0x0065`.
  - `EUTHERDRIVE_MIRROR_MBX_TO_Z80FLAG_VALUE=<hex>` forces a fixed value (e.g. `0x01`, `0x04`, `0x80`).
  - `[Z80FLAG65]` logs the write + readback via the Z80 RAM store.
- Added latch:
  - `EUTHERDRIVE_Z80FLAG65_LATCH=1` suppresses 68k writes of `0x00` to `A00065` if Z80 RAM `0x0065` is already non-zero.
  - `EUTHERDRIVE_Z80FLAG65_LATCH_LIMIT` caps `[Z80FLAG65-LATCH]` logs.
- With mirror + latch, `0x0065` stays non-zero and Z80 reads it as non-zero (`[Z80RD65]`/`[Z80RDPF]` show `0x01/0x04/0x80`), but the loop still does not exit.
- Post-flag trace shows the loop bytes at `pc=0x0DD8`:
  - `DD CB 01 56 20 12 DD CB 00 56 C0`
  - Interprets as `BIT 2,(IX+1)`, `JR NZ +0x12`, `BIT 2,(IX+0)`, `RET NZ`.
  - This suggests either IX base does not point at `0x0064/0x0065`, or BIT/JR/RET flag behavior is wrong.
- Added targeted DDCB BIT/JR/RET trace:
  - `EUTHERDRIVE_TRACE_Z80_DDCB_BIT=1` + `EUTHERDRIVE_TRACE_Z80_DDCB_BIT_LIMIT` logs `[Z80DDCB]`.
  - Logs `pc`, `ix/iy/sp`, disp+EA, memory byte, bit2, F before/after, Z, nextpc, instr, taken.
- DDCB trace run (with forced flag bit set) confirms the BIT sees `mem=0x04` and Z=0, but the flow still returns at `0x0DEE`.
- Added forced JR debug patch:
  - `EUTHERDRIVE_Z80FLAG_FORCE_JR=1` overrides PC after the BIT at `0x0DD8`.
  - `EUTHERDRIVE_Z80FLAG_FORCE_JR_TARGET=<hex>` sets target (default uses the JR+disp at `0x0DDC`, i.e. `0x0DF0`).
  - `EUTHERDRIVE_Z80FLAG_FORCE_JR_LIMIT=<int>` limits `[Z80FLAG-JR]` logs (default 8, 0 = unlimited).
  - Uses the DDCB displacement when computing EA, so the forced path mirrors the real BIT operand.
- Forced JR run result:
  - `[Z80FLAG-JR]` shows `pc=0x0DD8 -> 0x0DEA` with `ea=0x0065 mem=0x04`.
  - Execution still does `BIT` at `0x0DEA` (disp `0x00`) then `RET` at `0x0DEE` to `0x0E75`.
  - Conclusion: forcing the JR path alone does not reach the command processing route.
 - Latest trace (JR forced + Z80YM/MBX enabled):
   - `[MBX68K-EDGE]` shows 68k writes to `0x1B8F` with `val=0x00` at frames 43/44.
   - A later edge shows `val=0x83` at frame 320 (`pc68k=0x004A42`), so non-zero commands do appear.
   - No `Z80MBX*` reads appear in the snippet, suggesting the Z80 is not consuming `0x1B8F` data.
   - Z80 YM writes occur (`0x4000/0x4002` with `0x2F/0x2D`, and `0x4001` with `0x40`) but still no evidence of command-driven playback.
- Current implication: 68k is issuing a non-zero command, but the Z80 loop still exits to `RET` without reading the mailbox data.
- Next step: force the handshake to advance (e.g., override `0xFFB154`) or mirror mailbox data into `0x0060..0x006F` to verify the command path.

## Altered Beast handshake override (2025-01-02)
- Added env override to force the 68k read value at `0xFFB154`:
  - `EUTHERDRIVE_FORCE_B154_READ=<byte>` returns a forced value on 68k reads (hex or dec).
  - `EUTHERDRIVE_FORCE_B154_READ_LIMIT=<int>` caps `[B154-OVERRIDE]` logs (default 8; 0 = unlimited).
- Use case: set `EUTHERDRIVE_FORCE_B154_READ=0` to break the `bne` spin and see if command/YM playback proceeds.
- Result: forcing `EUTHERDRIVE_FORCE_B154_READ=0` skips the intro but freezes on the first background (no sprites).

## Mailbox range mirror (2025-01-02)
- Added env mirror to copy `0x1B80..0x1B8F` writes into Z80 RAM (defaults to `0x0060..0x006F`):
  - `EUTHERDRIVE_MIRROR_MBX_TO_Z80FLAG_RANGE=1` enables the mirror.
  - `EUTHERDRIVE_MIRROR_MBX_TO_Z80FLAG_RANGE_ADDR=<hex|dec>` sets the base address (default `0x0060`).
  - `EUTHERDRIVE_MIRROR_MBX_TO_Z80FLAG_RANGE_LIMIT=<int>` caps `[Z80FLAG-RANGE]` logs (default 32; 0 = unlimited).
- Purpose: feed the Z80 poll block with mailbox data to see if the command path wakes up.
- Result: enabling the range mirror causes a freeze at the SEGA logo in this run.

## Wide mailbox mirror (2025-01-02)
- Added env mirror to copy a sub-range of the wide mailbox (`0x1B00..0x1B7F`) into Z80 RAM:
  - `EUTHERDRIVE_MIRROR_MBX_WIDE_TO_Z80FLAG_RANGE=1` enables the mirror.
  - `EUTHERDRIVE_MIRROR_MBX_WIDE_TO_Z80FLAG_RANGE_START=<hex|dec>` source start (default `0x1B20`).
  - `EUTHERDRIVE_MIRROR_MBX_WIDE_TO_Z80FLAG_RANGE_END=<hex|dec>` source end (default `0x1B2F`).
  - `EUTHERDRIVE_MIRROR_MBX_WIDE_TO_Z80FLAG_RANGE_ADDR=<hex|dec>` destination base (default `0x0060`).
  - `EUTHERDRIVE_MIRROR_MBX_WIDE_TO_Z80FLAG_RANGE_LIMIT=<int>` caps `[Z80FLAG-WIDE]` logs (default 32; 0 = unlimited).
- Use case: 68k writes non-zero data in `0x1B20..0x1B2F` (wide mailbox), but Z80 polls `0x0065`; mirror this block to test the command path without touching `0x1B8F`.
- Result: enabling the wide mirror (default range) produced a black screen in the current run.

## Z80 B154 mapping override (2025-01-02)
- Added env override to force Z80 `0x8000..0xFFFF` accesses at `0xB154` to map to `0xFFB154`:
  - `EUTHERDRIVE_FORCE_Z80_B154_MAP=1` enables.
  - `EUTHERDRIVE_FORCE_Z80_B154_ADDR=<hex|dec>` sets the target 68k address (default `0xFFB154`).
  - `EUTHERDRIVE_FORCE_Z80_B154_Z80ADDR=<hex|dec>` sets the Z80 address to intercept (default `0xB154`).
  - `EUTHERDRIVE_FORCE_Z80_B154_Z80ADDR2=<hex|dec>` optional second Z80 address.
  - `EUTHERDRIVE_FORCE_Z80_B154_LIMIT=<int>` caps `[Z80B154-OVERRIDE]` logs (default 64; 0 = unlimited).
- Purpose: simulate the expected bank mapping without forcing 68k reads to zero.

## Z80 busreq/reset trace (2025-01-02)
- `Z80Stats` shows Z80 is active (pc ~ `0x00AF/0x00B0`) and not stuck in reset.
- BUSREQ/RESET writes occur in bursts but do not hold Z80 in reset for long.
- Current suspicion: Z80 is spinning in a low‑RAM poll loop; need to identify the polled address.
- Z80 read-range trace confirms polling on `0x0065` with `val=0x00` at `pc=0x0DD8/0x0DEA`.

## Z80 flag read override (2025-01-02)
- Added env override to return mailbox data on reads of `0x0065` without writing to RAM:
  - `EUTHERDRIVE_Z80FLAG65_READ_FROM_MBX=1` enables the override.
  - `EUTHERDRIVE_Z80FLAG65_READ_ADDR=<hex|dec>` sets the flag addr (default `0x0065`).
  - `EUTHERDRIVE_Z80FLAG65_READ_SOURCE=<hex|dec>` selects source addr in Z80 RAM (default `0x1B8F`).
  - `EUTHERDRIVE_Z80FLAG65_READ_OR=<hex|dec>` OR mask for the source value.
  - `EUTHERDRIVE_Z80FLAG65_READ_LIMIT=<int>` caps `[Z80FLAG65-READ]` logs (default 32; 0 = unlimited).
- Goal: feed the poll loop without mutating RAM (avoid the freezes seen with mirror writes).
 - Fix: apply the override in `PeekZ80ByteNoSideEffect` as well, so DDCB BIT reads see the overridden value.

### Next run
```
EUTHERDRIVE_TRACE_Z80_DDCB_BIT=1 \
EUTHERDRIVE_TRACE_Z80_DDCB_BIT_LIMIT=64 \
EUTHERDRIVE_MIRROR_MBX_TO_Z80FLAG=1 \
EUTHERDRIVE_MIRROR_MBX_TO_Z80FLAG_VALUE=0x04 \
EUTHERDRIVE_Z80FLAG65_LATCH=1 \
dotnet run --project EutherDrive.UI
```
```
rg -n "Z80DDCB|Z80PF-ARM|Z80RDPF|Z80FLAG65|Z80FLAG65-LATCH" rom_start.log
```
- Mirror test result:
  - `EUTHERDRIVE_MIRROR_MBX_TO_Z80FLAG=1` mirrors the 68k mailbox command byte (`1B8F`) into Z80 RAM `0x0065` and logs `[Z80FLAG65]` with a readback.
  - Forcing `EUTHERDRIVE_MIRROR_MBX_TO_Z80FLAG_VALUE=0x01` makes Z80 reads at `pc=0x0DD8/0x0DEA` see `val=0x01` (`opcode=0`), so the Z80 poll path is working; normal flow just never sets `0x0065` non‑zero.
 - Latch test (prevents 68k upload from clearing 0x0065):
   - `EUTHERDRIVE_Z80FLAG65_LATCH=1` suppresses 68k writes of `0x00` to `A00065` when `0x0065` is already non‑zero; logs `[Z80FLAG65-LATCH]` with kept value.
   - With latch + mirror, `[Z80RD65] pc=0x0DD8` sees `val=0x01` and stays non‑zero across upload cycles.
   - `Z80WIN65` shows the 68k is re-uploading `0x0060..0x006F` from `pc=0x004948..0x004966` and later `0x004A24..0x004A42`, which explains why `0x0065` keeps getting cleared without the latch.
 - Current implication: Z80 is polling `0x0065`, but the command payload appears to live elsewhere (no `Z80MBX-DATA` hits at `0x1B80..0x1B8F` even after the flag is set). Next step is to identify where the driver expects command bytes (trace data reads after `0x0065` goes non‑zero or mirror `0x1B80..0x1B8F` into `0x0060..0x006F` under a flag).
- Fix: injection now runs in the MD loop:
  - Moved logic into `md_main.MaybeInjectMbx()` and call it from `MdTracerAdapter.RunFrame`.
  - Reason: `md_main.RunFrame` is not used in the MD adapter, so the original injection never fired.
- Latest run shows `[MBXINJ-ENV] value='1'` and `[MBXINJ-ARM] frame=1 target=60 addr=0x1B8F val=0x01`; no `[MBXINJ]` yet in that snippet, so the run likely ended before frame 60 (or set `EUTHERDRIVE_INJECT_MBX_FRAME=1` to force immediate).
