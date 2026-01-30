Madou.md graphics corruption — current state

Status snapshot (2026-01-30)
- Root symptom: CRAM DMA uses source 0xFF94F8, but palette data is written at 0xFF95F8 (offset +0x100), so CRAM gets zeros and graphics look corrupt.
- Confirmed by headless traces:
  - DMA trigger dumps show 0xFF94F8 all zeros while 0xFF95F8 contains palette words right before CRAM DMA.
  - DMA source registers set to low/mid/high = 0x7C/0xCA/0x7F → srcWord=0x7FCA7C → srcByte=0xFF94F8.

Key traces/logs
- `headless_madou_trace13/madou_step2.log` (pre‑DMA dumps):
  - At `pc=0x00078E`, `PCTAP-PEEK` shows:
    - `0xFF94F8: 00 00 00 00 ...`
    - `0xFF95F8: 00 1F 00 1F ...` (palette data)
  - Immediately after: `DMA-START ... srcByte=0xFF94F8 ... target=CRAM`.
- `headless_madou_trace13/madou_step1.log` (RAM range writes):
  - Range `0xFF94F8–0xFF95FF` is cleared multiple times (PC `0x04BBFA/0x04BBFE/0x04BC00` and `0x0139FA`).
  - Palette writes go to `0xFF95F8/0xFF95FC` at PC `0x013A0A/0x013A10` and later updates at `0x013C50`.
- `headless_madou_trace13/madou_step3.log` (code context):
  - `pc=0x0139E8`:
    - `moveq #$1F,d1`
    - `lea $FF9478,a2`
    - `lea $FF94F8,a3`
  - `pc=0x0139F8` loop:
    - `move.l d0,(a3)+` + `dbf d1,...` → clears 0xFF94F8..0xFF9577.
  - `pc=0x013A04+`:
    - `move.l d0,$FF95F8` and `move.l d0,$FF95FC` → palette buffer is explicitly written at 0xFF95F8 (not 0xFF94F8).
  - At `pc=0x04BBFA`:
    - Repeated `move.l d0` loop clears a large area starting at `A0=0xFF0000`, which includes the palette buffer area.

Why CRAM is wrong (current understanding)
- Game code clearly builds palette at `0xFF95F8`, but CRAM DMA is configured for `0xFF94F8`. That guarantees zero reads unless something later copies/offsets the buffer.
- There is no evidence of a copy from `0xFF95F8` → `0xFF94F8` before CRAM DMA.

Next investigation (no hacks)
- Find the code path that *should* set DMA source to `0xFF95F8` (or adjust buffer base) and why it doesn’t.
  - Check callers around `pc=0x000780–0x000790` (DMA setup). It writes reg21/22/23 as constants `7C/CA/7F` (0xFF94F8).
  - Search for any dynamic DMA source updates or alternate code path that should use `0xFF95F8`.
- Track any pointer arithmetic or table that feeds `0x000780` path to see if a base pointer (likely `A3` or a RAM variable) should be advanced by +0x100.

Update from `madou_step5.log` (palette loop @ 0x013C74)
- Execution repeatedly hits `pc=0x013C74/0x013C7A/0x013C7C/0x013C80/0x013C84/0x013C8C/0x013C90`.
- Register setup at loop entry:
  - `A2=0xFF95F8` (palette buffer)
  - `A3=0xFF9478`
  - `A4=0xFF94F8`
- Loop body:
  - `move.b (A2), D0` then `clr.b (A2)` and `move.b 1(A2), D0`
  - `jsr 0x013C84` (subroutine only sets `0xFF9604=1`, increments `A2`, adjusts `A3/A4`, and loops)
  - No writes observed to `A3` or `A4` in this path.
- Memory snapshots during the loop:
  - `0xFF94F8` stays all zeros.
  - `0xFF95F8` contains palette bytes but gets cleared as the loop progresses.
- There is no execution at `pc=0x013CA0` in this trace (only disassembly shown), so any potential “copy/pack palette to FF94F8” routine there is not being called in this path.

Update from `headless_madou_trace14/madou_step6.log` (JSR target check)
- The `jsr` at `0x013C80` **does** jump into `0x013CA0` (PC‑relative), and we see `[PCTAP] pc=0x013CA0` repeatedly.
- So the palette helper at `0x013CA0` *is* running; the issue is not “never called”.

Update from `headless_madou_trace15/madou_step7.log` (RAM range + 0x013CA0)
- RAM range trace for `0xFF94F8–0xFF95FF` shows:
  - Writes that clear the whole block at `pc=0x04BBFA/0x04BBFE/0x04BC00` and `pc=0x0139FA`.
  - Palette writes to `0xFF95F8/0xFF95FC` at `pc=0x013A0A/0x013A10`.
  - **Only** writes to `0xFF94F8` later are `W2` with value `0x0000` at `pc=0x013CF8`.
- No non‑zero writes to `0xFF94F8` were observed; the routine path that touches `0xFF94F8` writes zeros.
- Reads of `0xFF94F8` occur at `pc=0x000794/0x0007A2` (DMA setup path), always reading `0x0000`.

Update from `headless_madou_trace19/madou_step11.log` + ROM bytes (helper decode)
- ROM around `pc=0x013C60` clearly sets:
  - `A2=0xFF95F8`, `A3=0xFF9478`, `A4=0xFF94F8`.
  - The loop uses `move.b (A2),D0` + `jsr 0x013CA0` and increments `A2`.
- Helper at `0x013CA0` *does* read palette entries from `A3`:
  - `0x341B` = `move.w (A3)+,D2`.
  - That means the conversion **expects palette data to already be in 0xFF9478**, not in 0xFF95F8.
- In `madou_step11.log`, `D2` stays `0x0000` at `pc=0x013CF2/0x013CF6`, which implies the `A3` buffer is still all zeros during conversion.
- Conclusion shift: the DMA source itself is correct for the conversion output (0xFF94F8), **but the *input* buffer 0xFF9478 never gets filled**, while palette words are written to 0xFF95F8.

Hypothesis (no hacks)
- The ROM has a separate staging buffer at `0xFF9478` that should be populated before conversion, but our emulator never writes to it.
- Likely root cause is a CPU‑core execution error in the routine that should populate `0xFF9478` (around `pc=0x013A10–0x013A40`), or a flag/branch misbehavior that skips that copy path.
- Next step: trace writes to `0xFF9478–0xFF94B8` and confirm whether the population routine runs; if it doesn’t, focus on instruction correctness in that routine (not DMA).

Update from `headless_madou_trace20/madou_step12.log` (targeted RAM‑range + PC taps)
- RAM range `0xFF9478–0xFF94B8` shows only **clears** at:
  - `pc=0x04BBFA/0x04BBFC/0x04BBFE/0x04BC00`
  - `pc=0x0139F8`
- The **only non‑zero writes** in the range are at `pc=0x013A64–0x013A72`, but those start at `0xFF9498` and look like a lookup table:
  - `0xFF9498=0x0000739C`, `0xFF949C=0x73007280`, `0xFF94A0=0x72007180`, etc.
  - Addresses `0xFF9478–0xFF9496` remain **all zeros**.
- Reads during conversion (`pc=0x013CC0`) show `0xFF9478–0xFF9496` = 0, while `0xFF949A+` contains table values.
- PC taps inside the `0x013A10` region hit `pc=0x013A1E` with:
  - `A2=0xFF94F8`, `A3=0xFF9578`, `A4=0x00000000` (i.e., **not** the expected A4=0xFF9478 for the copy loop).
  - So the “copy into 0xFF9478” loop either doesn’t run or runs in a different state.

Interpretation
- The range **is being used for a lookup table**, but the **first 0x20 bytes (0xFF9478..0xFF9496)** stay zero.
- The conversion helper reads from `0xFF9478` upward, so it is effectively reading zeros for the low indices.
- We still do **not** see any routine that populates `0xFF9478..0xFF9496` with palette‑derived values.

Update from `headless_madou_trace21/madou_step13.log` (tap 0x013C60 loop + peeks)
- The `0x013C60` loop *does* run with expected pointers:
  - `A2=0xFF95F8` (palette bytes), `A3=0xFF9478` (lookup), `A4=0xFF94F8` (packed output).
  - `PCTAP-PEEK` confirms `0xFF95F8` contains non‑zero palette bytes, while `0xFF9478..0xFF9496` are all zeros.
- As the loop progresses, `A2` increments (`0xFF95F8 → 0xFF95FA → 0xFF95FC → ...`) and `A3/A4` advance to `0xFF9498 / 0xFF9518`, then `0xFF94B8 / 0xFF9538`, etc.
  - The lookup table values exist only at `0xFF9498+`, so the early iterations read zero lookup values.
- This confirms the problem is **not** in the `0x013C60` loop itself: it’s reading palette bytes fine, but the **lookup table base for low indices (0xFF9478..0xFF9496) is empty**.

Update from headless traces 28–30 (2026-01-30)
- `headless_madou_trace28/madou_step18.log` confirms caller at `0x000629BC` does `jsr 0x013A46` with `D0=0x00070000` and `A2=0x00062AE0` (table). The PCTAP peek shows ROM table at `0x078A8C` starts with **32 bytes of zeros**, then data.
- In `headless_madou_trace25/madou_step17.log`, `0x013A46` executes as:
  - `rol.l #8,d0` → `D0=0x07000000`
  - `andi.w #$00FF,d0` → `D0=0x00000000`
  - `asl.w #1,d0` → `D0=0x00000000`
  - `lea 0x078A8C,a3` + `adda.w d0,a3` → `A3=0x078A8C`
  - Eight `move.l (a3)+,(a4)+` copy **zeros** into `0xFF9478..0xFF9496`.
  - Next loop iteration (D1=2) yields `D0=0x000000E0` and copies from `0x078B6C` into `0xFF9498..` (this is where non‑zero lookup values appear).
- ROM bytes at `0x078A8C` verified (first 0x20 bytes are zero), and `0x078B6C` holds the 0x739C/0x7300/... lookup values.
- `0x013A20` routine (ROM at 0x013A20) exists but **never executes** in traces; only `0x013A46` is called.
- `headless_madou_trace30/madou_step20.log` shows the palette conversion loop at `0x013C80`:
  - It increments `A2/A3/A4` by `0x20` each step (`moveq #$20` + `adda.l`) so the first conversion uses lookup base `0xFF9478` (zeros), then `0xFF9498` (non‑zero).
- CRAM DMA length is `0x0040` words (`madou_headless_cram.log`), so **only the first 32 palette entries** (from `0xFF94F8..0xFF9538`) are sent to CRAM. Those entries are produced using the **zero lookup segment** and thus remain zero → corrupt palette.

Working hypothesis now
- The lookup table population at `0x013A46` is using the **wrong source offset (index 0)** for the first block, and because the DMA only uploads the first 0x40 words, the palette upload is effectively all zeros.
- This looks like a **control‑flow / state issue before the `jsr 0x013A46`**, not a bad ROL/ADDA implementation: the ROM table itself has zeros at the base, and the code is legitimately selecting index 0 on the first block.
- Next target: find why the caller supplies `D0=0x00070000` (which becomes index 0 after `rol/andi/asl`) instead of an index that maps to the non‑zero block; or why the first block should be skipped/offset before DMA.

Open questions
- Why does the caller feed `D0=0x00070000` to `0x013A46`? Is a prior computation or flag wrong?
- Why is DMA length only `0x0040` (32 words) if the conversion loop processes more entries? Should DMA be longer or should the base be offset to the non‑zero part?

Artifacts referenced
- `headless_madou_trace25/madou_step17.log`
- `headless_madou_trace28/madou_step18.log`
- `headless_madou_trace30/madou_step20.log`
- `madou_headless_cram.log`
