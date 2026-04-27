# SNES MK2 blackout debug notes - 2026-04-26

## Context

Game:

- ROM: `/run/media/nichlas/Atlas/SNES/sd2snes/1 J-R - US/Mortal Kombat II (USA).sfc`
- Savestate: `/run/media/nichlas/Atlas/SNES/sd2snes/1 J-R - US/Mortal_Kombat_II__USA_.sfc_43e36a74.euthstate`
- Slot 1 catches/starts near a black blink. Slot 2 is also useful in-fight.

Latest user feedback: the previous blackout fix is not acceptable. It still has black blinks and feels like a presentation/display workaround. Do not solve this by holding the last frame, ignoring forced blank, or special-casing `$2100`.

## Confirmed symptom

The black blink is real core state, not UI presentation:

- Headless reproduces the transition to black.
- During black frames, PPU state is `forcedBlank=True`, brightness `0`.
- The framebuffer is genuinely black for the affected frames.

Representative slot 1 run showed black intervals around:

- frames 439-445
- frames 468-474
- frames 497-503
- frames 526-532
- frames 557-560

The exact frame numbers may shift with diagnostic changes, but the pattern is stable.

## Important trace findings

PPU `$2100` writes:

- MK2 writes `$2100=0x80` from PC `0x8086E1` during raster IRQ work around scanlines 215/216.
- Normal frames later restore `$2100=0x0F` from PC `0x8087B6` during vblank, commonly around y=225 or y=229.
- During black intervals, the `$2100=0x0F` restore is delayed by several frames. Sometimes it happens at the very end of a frame, too late for visible lines, and visible content returns on the next frame.

CPU/APU relation:

- During the black interval, the main CPU is spending a long time in an APU upload loop at `$84853C-$848575`.
- The loop polls `$2142`, then sends up to 3 bytes through `$2140/$2141/$2143`, then writes the next acknowledge value to `$2142`.
- Relevant loop shape:

```text
84853E CD 42 21    CMP $2142
848541 D0 FB       BNE $853E
848543 B7 2F       LDA [$2F],Y
848545 8D 40 21    STA $2140
...
848553 8D 41 21    STA $2141
...
848561 8D 43 21    STA $2143
...
848572 8D 42 21    STA $2142
848575 85 2B
848578 10 C2       BPL $853C
```

SPC side:

- The SPC upload handler loops around `0x0C41-0x0C68`.
- It reads CPU port 2 (`$F6`), reads ports, writes `$F1=0xB7` to reset ports, writes the acknowledge to `$F6`, copies data, and loops.
- Observed acknowledge-to-acknowledge timing is around 74 APU cycles in the current trace, which may be plausible but still needs comparison.

Diagnostic proof that this is timing-related:

- Temporarily running with `EUTHERDRIVE_TRACE_SNES_APU_CLOCK_SCALE=2` removed the same black transitions.
- That is not a valid fix; it only proves the long CPU/APU handshaking path is what delays the `$2100=0x0F` restore.

IRQ notes:

- Tracing `$4211` showed the TIMEUP read age is usually far above the 4-master-cycle guard, often around 68-642 mclks.
- The duplicate `$2100=0x80` per visible frame appears to be part of MK2's V/H IRQ scheduling, not obviously caused by the 4-mclk TIMEUP guard.
- JGenesis clears TIMEUP pending immediately on `$4211` read; EutherDrive currently has a 4-mclk guard. This is still worth knowing, but it is probably not the main blackout cause.

## What is currently dirty / diagnostic only

Do not commit these as-is:

- `SuperNintendoEmulator/KSNES/SNESSystem/SNESSystem.cs`
  - `EUTHERDRIVE_TRACE_SNES_APU_CLOCK_SCALE` diagnostic.
  - `$4211` TIMEUP trace.
  - timer register trace for `$4207-$420A`.
  - expanded APU port trace output.
  - APU port write tracing in `WriteBBusFast`.
  - an experimental pre-catch-up before CPU APU port reads.
- `SuperNintendoEmulator/KSNES/AudioProcessing/SPC700.cs`
  - `EUTHERDRIVE_TRACE_SPC700_PC_LIMIT` diagnostic.

The pre-catch-up before CPU APU port reads did not fix the black frames and should probably be reverted.

There are also unrelated dirty 32X files in the worktree. Ignore them for this bug unless the user explicitly asks.

## Repro commands

Build:

```sh
dotnet build EutherDrive.Headless/EutherDrive.Headless.csproj -c Release
```

Slot 1 black-frame check:

```sh
env EUTHERDRIVE_HEADLESS_CORE=snes EUTHERDRIVE_SAVESTATE_SLOT=1 EUTHERDRIVE_HEADLESS_TRACE_FRAMES=1 EUTHERDRIVE_HEADLESS_DUMP_DIR=/tmp/mk2_slot1_check dotnet EutherDrive.Headless/bin/Release/net8.0/EutherDrive.Headless.dll --load-savestate "/run/media/nichlas/Atlas/SNES/sd2snes/1 J-R - US/Mortal Kombat II (USA).sfc" "/run/media/nichlas/Atlas/SNES/sd2snes/1 J-R - US/Mortal_Kombat_II__USA_.sfc_43e36a74.euthstate" 600 > /tmp/mk2_slot1_check.out
rg -n "transition|snes_fb_has_content=False|forcedBlank=True" /tmp/mk2_slot1_check.out
```

PPU `$2100` / IRQ trace window:

```sh
env EUTHERDRIVE_HEADLESS_CORE=snes EUTHERDRIVE_SAVESTATE_SLOT=1 EUTHERDRIVE_TRACE_SNES_DMA=1 EUTHERDRIVE_TRACE_SNES_PPU_BUS=1 EUTHERDRIVE_TRACE_SNES_PPU_BUS_ADDRS=00 EUTHERDRIVE_TRACE_SNES_PPU_BUS_FRAME_START=430 EUTHERDRIVE_TRACE_SNES_PPU_BUS_FRAME_END=446 EUTHERDRIVE_TRACE_SNES_PPU_BUS_LIMIT=1000 EUTHERDRIVE_HEADLESS_TRACE_FRAMES=1 EUTHERDRIVE_HEADLESS_DUMP_DIR=/tmp/mk2_irq_timeup dotnet EutherDrive.Headless/bin/Release/net8.0/EutherDrive.Headless.dll --load-savestate "/run/media/nichlas/Atlas/SNES/sd2snes/1 J-R - US/Mortal Kombat II (USA).sfc" "/run/media/nichlas/Atlas/SNES/sd2snes/1 J-R - US/Mortal_Kombat_II__USA_.sfc_43e36a74.euthstate" 447 > /tmp/mk2_irq_timeup.out
rg -n "TIMEUP|PPU-BUS|transition" /tmp/mk2_irq_timeup.out
```

APU/SPC upload trace:

```sh
env EUTHERDRIVE_HEADLESS_CORE=snes EUTHERDRIVE_SAVESTATE_SLOT=1 EUTHERDRIVE_TRACE_SPC700_PC=1 EUTHERDRIVE_TRACE_SPC700_PC_LIMIT=400 EUTHERDRIVE_TRACE_SNES_APU_SPC_READS=1 EUTHERDRIVE_TRACE_SNES_APU_PORTS=1 EUTHERDRIVE_TRACE_SNES_APU_PORTS_LIMIT=2000 EUTHERDRIVE_HEADLESS_TRACE_FRAMES=1 EUTHERDRIVE_HEADLESS_DUMP_DIR=/tmp/mk2_spcpc dotnet EutherDrive.Headless/bin/Release/net8.0/EutherDrive.Headless.dll --load-savestate "/run/media/nichlas/Atlas/SNES/sd2snes/1 J-R - US/Mortal Kombat II (USA).sfc" "/run/media/nichlas/Atlas/SNES/sd2snes/1 J-R - US/Mortal_Kombat_II__USA_.sfc_43e36a74.euthstate" 20 > /tmp/mk2_spcpc.out
```

## JGenesis comparison points

Local reference path: `~/jgenesis`.

Useful differences/suspects:

- JGenesis APU register reads return real state for `$F1` and `$FA-$FC`; EutherDrive currently returns `0` for `$F1/$FA/$FB/$FC`.
- JGenesis CPU writes are applied after the CPU access has advanced components. EutherDrive defers B-bus/CPU register writes to the next access boundary. This was added for correctness in other titles, but the exact timing should be rechecked for APU ports.
- JGenesis reads APU ports before ticking APU for that CPU access. EutherDrive originally matched this better than the experimental pre-catch-up read change.
- JGenesis clears TIMEUP pending immediately on `$4211` read. EutherDrive uses a 4-mclk non-clear guard.

## Next experiments

1. Revert the failed pre-catch-up APU read experiment and keep only tracing if more measurement is needed.
2. Compare SPC700 instruction timing for the exact upload handler opcodes (`E4`, `F0`, `2E`, `BA`, `DA`, `8F`, `C4`, `D5`, `1D`, `30`, `DD`, `10`) against a trusted table/JGenesis.
3. Implement correct APU reads for `$F1` and `$FA-$FC`, then retest slot 1. This is low-risk correctness work, but still speculative for MK2.
4. Audit CPU/APU port write visibility:
   - CPU read should see the port value before that access advances APU time.
   - CPU write should become visible to SPC after the write access completes, not before.
   - The same-cycle `$F1` port-reset drop rule must remain.
5. If the upload loop is still too slow after correctness fixes, measure exact main CPU polls per SPC ack and compare against JGenesis or hardware docs.

## Do not do

- Do not hold the previous rendered frame.
- Do not ignore or clamp `$2100=0x80`.
- Do not overclock APU as a fix.
- Do not special-case Mortal Kombat II by ROM name or PC.

