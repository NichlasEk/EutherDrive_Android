# EutherDrive Gated Environment Variables - Complete Reference

Denna fil dokumenterar ALLA gated environment variables som används för att kontrollera logging i EutherDrive emulatorn.

## Översikt

All logging i EutherDrive är nu gated bakom environment variables. Detta gör systemet extremt mycket snabbare när ingen logging är aktiverad.

## Komplett Lista över Environment Variables

### `EUTHERDRIVE_TRACE_AUDIO=1`
- **Beskrivning**: Aktiverar logging för följande prefix:
  - `\[AUDIOCORE\]`
  - `\[AUDLVL\]`
  - `\[AudioEngine\]`
  - `\[Audio\]`
  - `\[OpenAlAudioOutput\]`
  - `\[PwCatAudioSink\]`

### `EUTHERDRIVE_TRACE_M68K=1`
- **Beskrivning**: Aktiverar logging för följande prefix:
  - `\[VINT-TAKEN\]`
  - `\[m68k boot\]`
  - `\[m68k-reset\]`
  - `\[m68k\]`

### `EUTHERDRIVE_TRACE_PSG=1`
- **Beskrivning**: Aktiverar logging för följande prefix:
  - `\[PSGLVL\]`
  - `\[PSG\]`

### `EUTHERDRIVE_TRACE_SMS=1`
- **Beskrivning**: Aktiverar logging för följande prefix:
  - `\[SMS DELAY\]`
  - `\[SMS-DISPLAY-LOCK\]`
  - `\[SMS-FIRST-LINE\]`
  - `\[SMS-FORCE-RENDER\]`
  - `\[SMS-IO-READ\]`
  - `\[SMS-MAPPER\]`
  - `\[SMS-NO-RENDER\]`
  - `\[SMS-RENDER\]`
  - `\[SMS-RESET\]`
  - `\[SMS-ROM\]`
  - `\[SMS-RUNFRAME\]`
  - `\[SMS-STATUS-READ\]`
  - `\[SMS-STATUS\]`
  - `\[SMS-VDP-CMD\]`
  - `\[SMS-VDP-DATA\]`
  - `\[SMS-VDP-FIRST\]`
  - `\[SMS-VDP-NAME\]`
  - `\[SMS-VDP-READ\]`
  - `\[SMS-VDP-REG-DETAIL\]`
  - `\[SMS-VDP\]`
  - `\[SMS-VRAM-READ\]`
  - `\[SMS-WARN\]`
  - `\[SMS-Z80-BLOCK\]`
  - `\[SMS-Z80-RUN\]`
  - `\[SMS-Z80\]`

### `EUTHERDRIVE_TRACE_SMS_AUTOFIX=1`
- **Beskrivning**: Aktiverar logging för följande prefix:
  - `\[SMS-AUTOFIX\]`

### `EUTHERDRIVE_TRACE_SMS_EI_DI=1`
- **Beskrivning**: Aktiverar logging för följande prefix:
  - `\[Z80-DI\]`
  - `\[Z80-EI-DI-FETCH\]`
  - `\[Z80-EI\]`

### `EUTHERDRIVE_TRACE_SRAM=1`
- **Beskrivning**: Aktiverar logging för följande prefix:
  - `\[SRAM-READ\]`
  - `\[SRAM-WRITE\]`
  - `\[SRAM\]`
  - `\[Savestate\]`

### `EUTHERDRIVE_TRACE_TEST=1`
- **Beskrivning**: Aktiverar logging för följande prefix:
  - `\[CACHE-COPY\]`
  - `\[MBXINJ-ENV\]`
  - `\[STALL\]`
  - `\[TEST-FAIL\]`
  - `\[TEST-PASS\]`
  - `\[TEST\]`
  - `\[WARN\]`

### `EUTHERDRIVE_TRACE_UI=1`
- **Beskrivning**: Aktiverar logging för följande prefix:
  - `\[AsciiViewer\]`
  - `\[HEADLESS\]`
  - `\[MainWindow\]`
  - `\[MdTracerAdapter\]`
  - `\[ROMMODE\]`
  - `\[UI\]`

### `EUTHERDRIVE_TRACE_VDP=1`
- **Beskrivning**: Aktiverar logging för följande prefix:
  - `\[INTERLACE-DEBUG\]`
  - `\[VDP-ADDR-SET\]`
  - `\[VDP-AUTO-FIX\]`
  - `\[VDP-CTRL-WRITE\]`
  - `\[VDP-DATA-CODE\]`
  - `\[VDP-DISPLAY\]`
  - `\[VDP-DMAREG\]`
  - `\[VDP-HMODE\]`
  - `\[VDP-REG12-DBG\]`
  - `\[VDP-REG12-SH\]`
  - `\[VDP-REG12\]`
  - `\[VDP-REG1\]`
  - `\[VDP-REG2\]`
  - `\[VDP-REG4\]`
  - `\[VDP-REG7-BD\]`
  - `\[VDP-SMS-DISPLAY\]`
  - `\[VDP-SMS\]`
  - `\[VDP\]`

### `EUTHERDRIVE_TRACE_VDP_STATUS_READ=1`
Enables VDP status read logging.
- Tags:
  - `\[VDP-STATUS-RD\]`

### `EUTHERDRIVE_TRACE_SPRITE_OVERFLOW_FRAME=1`
Logs per-frame sprite overflow summary (max sprites/cells per line).
- Tags:
  - `\[SPRITE-OVERFLOW\]`
  - `\[VINT-SKIP\]`
  - `\[VINT\]`

### `EUTHERDRIVE_TRACE_DMA_SRC=1`
- **Beskrivning**: Aktiverar logging för följande prefix:
  - `\[DMA-SRC-TRACE-START\]`
  - `\[DMA-SRC-READ\]`

### `EUTHERDRIVE_TRACE_DMA_SRC_LIMIT=<int>`
- **Beskrivning**: Begränsar antalet `\[DMA-SRC-READ\]` loggar per DMA (0 = unlimited, default 128).

### `EUTHERDRIVE_TRACE_NAMETABLE_ROW_DUMP=1`
- **Beskrivning**: Aktiverar logging för följande prefix:
  - `\[NT-REG\]`
  - `\[NT-BASE\]`
  - `\[NT-A\]`
  - `\[NT-B\]`
  - `\[NT-A-DEC\]`
  - `\[NT-B-DEC\]`
  - `\[HSCROLL-SAMPLE\]`

### `EUTHERDRIVE_TRACE_PATTERN_TILE_DUMP=1`
- **Beskrivning**: Aktiverar logging för följande prefix:
  - `\[TILE-A\]`
  - `\[TILE-A-ROW\]`
  - `\[TILE-B\]`
  - `\[TILE-B-ROW\]`

### `EUTHERDRIVE_TRACE_VRAM=1`
- **Beskrivning**: Aktiverar logging för följande prefix:
  - `\[PATTERN-WRITE\]`
  - `\[VRAM-CLEAR\]`
  - `\[VRAM-NAME\]`
  - `\[VRAM-PAGE-HIST\]`
  - `\[VRAM-PAGE-STATS\]`
  - `\[VRAM-VS-CACHE\]`
  - `\[VRAM-WATCH\]`
  - `\[VRAM-WRITE-CPU\]`
  - `\[VRAM-WRITE-DETAIL\]`
  - `\[VRAM\]`

### `EUTHERDRIVE_TRACE_VRAM_RANGE=<start-end>`
- **Beskrivning**: Loggar VRAM-writes i valt adressintervall:
  - `\[VRAM-RANGE\]`

### `EUTHERDRIVE_TRACE_VRAM_RANGE_LIMIT=<int>`
- **Beskrivning**: Begränsar `\[VRAM-RANGE\]`-loggar (0 = obegränsat; default 200).

### `EUTHERDRIVE_TRACE_VRAM_RANGE_SKIP_FILL=1`
- **Beskrivning**: Hoppar över `DMA-FILL` i `\[VRAM-RANGE\]`-loggarna.

### `EUTHERDRIVE_TRACE_TILE_FETCH=1`
- **Beskrivning**: Loggar tile-fetch i renderern:
  - `\[TILEFETCH\]`

### `EUTHERDRIVE_TRACE_TILE_FETCH_SCANLINE=<int>`
- **Beskrivning**: Väljer vilken scanline som loggas (default 112).

### `EUTHERDRIVE_TRACE_TILE_FETCH_LIMIT=<int>`
- **Beskrivning**: Begränsar `\[TILEFETCH\]`-loggar per frame (0 = obegränsat; default 128).

### `EUTHERDRIVE_TRACE_SCROLL_LINE=1`
- **Beskrivning**: Loggar scroll‑sampling i renderern:
  - `\[SCROLLLINE\]`

### `EUTHERDRIVE_TRACE_SCROLL_LINE_SCANLINE=<int>`
- **Beskrivning**: Väljer vilken scanline som loggas (default 112).

### `EUTHERDRIVE_TRACE_SCROLL_LINE_LIMIT=<int>`
- **Beskrivning**: Begränsar `\[SCROLLLINE\]`-loggar per frame (0 = obegränsat; default 32).

### `EUTHERDRIVE_FORCE_SCROLL_ZERO=1`
- **Beskrivning**: Forcerar H/V‑scroll till 0 under rendering (debug).

### `EUTHERDRIVE_TRACE_RAM_RANGE=<start-end>`
- **Beskrivning**: Loggar RAM-läsningar/skrivningar i valt adressintervall:
  - `\[RAM-RANGE\]`

### `EUTHERDRIVE_TRACE_RAM_RANGE_LIMIT=<int>`
- **Beskrivning**: Begränsar `\[RAM-RANGE\]`-loggar (0 = obegränsat; default 200).

### `EUTHERDRIVE_TRACE_RAM_RANGE_NONZERO=1`
- **Beskrivning**: Loggar bara icke‑nollvärden i `\[RAM-RANGE\]`.

### `EUTHERDRIVE_TRACE_RAM_RANGE_FIRST_PER_FRAME=1`
- **Beskrivning**: Loggar bara första matchande `\[RAM-RANGE\]` per frame.

### `EUTHERDRIVE_TRACE_RAM_RANGE_WRITE_COUNTER=1`
- **Beskrivning**: Raknar writes i RAM-intervall per frame och loggar summering (`[RAM-RANGE-SUMMARY]`).

### `EUTHERDRIVE_TRACE_RAM_RANGE_FIRST_WRITE=1`
- **Beskrivning**: Loggar forsta write per frame i RAM-intervall (`[RAM-RANGE-FIRST]`).

### `EUTHERDRIVE_TRACE_MEM_WATCH_LIST=<addr,addr,...>`
- **Beskrivning**: Loggar läs/skriv på valda adresser (hex eller dec) via `\[MEMWATCH\]`.

### `EUTHERDRIVE_TRACE_PC_TAP_LIST=<addr,addr,...>`
- **Beskrivning**: Loggar register + disasm nar PC matchar angivna adresser.

### `EUTHERDRIVE_TRACE_PC_TAP_PEEK_LIST=<start-end,...>`
- **Beskrivning**: Dumpar RAM-bytes for angivna intervall vid PC-tap.

### `EUTHERDRIVE_TRACE_PC_TAP_ONCE_PER_FRAME=1`
- **Beskrivning**: Loggar hogst en gang per frame per PC-tap.

### `EUTHERDRIVE_TRACE_YM=1`
- **Beskrivning**: Aktiverar logging för följande prefix:
  - `\[YM-BUSY-COUNTER\]`
  - `\[YM-BUSY\]`
  - `\[YM-STATUS\]`
  - `\[YMDAC\]`
  - `\[YMIRQ\]`
  - `\[YMLVL\]`
  - `\[YMREG\]`
  - `\[YMTRACE\]`

### `EUTHERDRIVE_TRACE_Z80=1`
- **Beskrivning**: Aktiverar logging för följande prefix:
  - `\[Z80-EI-DELAY\]`
  - `\[Z80-IRQ-SIGNAL\]`
  - `\[Z80BUSREQ\]`
  - `\[Z80PC-HIST\]`
  - `\[Z80RESET\]`
  - `\[Z80SAFE-UPLOAD\]`
  - `\[Z80SAFE\]`
  - `\[Z80SCHED\]`

### `EUTHERDRIVE_TRACE_Z80BUSACK_READS=1`
- **Beskrivning**: Loggar BUSACK-läsningar från `0xA11100`:
  - `\[Z80BUSACK-RD\]`

### `EUTHERDRIVE_TRACE_Z80BUSACK_READS_LIMIT`
- **Beskrivning**: Begränsar antal BUSACK-läsloggar (default 64).

### `EUTHERDRIVE_TRACE_Z80INT=1`
- **Beskrivning**: Aktiverar logging för följande prefix:
  - `\[Z80-INT-IM1\]`
  - `\[Z80-INT-IM2\]`
  - `\[Z80INT-STATS\]`

### `EUTHERDRIVE_TRACE_Z80SIG=1`
- **Beskrivning**: Aktiverar logging för följande prefix:
  - `\[Z80SIG\]`

### `EUTHERDRIVE_TRACE_Z80STEP=1`
- **Beskrivning**: Aktiverar logging för följande prefix:
  - `\[Z80STEP\]`

### `EUTHERDRIVE_TRACE_Z80WIN=1`
- **Beskrivning**: Aktiverar logging för följande prefix:
  - `\[Z80-?WIN\]`
  - `\[Z80WIN-HIST\]`

### `EUTHERDRIVE_TRACE_Z80WIN_DROP=1`
- **Beskrivning**: Loggar droppade 68k-skrivningar till Z80-busfönstret:
  - `\[Z80WIN-DROP\]`

### `EUTHERDRIVE_TRACE_Z80WIN_DROP_LIMIT`
- **Beskrivning**: Begränsar antal `Z80WIN-DROP`-loggar (default 64).

### `EUTHERDRIVE_TRACE_BUS_WATCH`
- **Beskrivning**: Loggar läs/skriv på exakt M68K-bussadress:
  - `\[BUSWATCH\]`

### `EUTHERDRIVE_TRACE_BUS_WATCH_LIMIT`
- **Beskrivning**: Begränsar antal BUSWATCH-loggar (default 64).

### `EUTHERDRIVE_TRACE_BUS_WATCH_ALL=1`
- **Beskrivning**: Loggar även om adressen inte matchar exakt (all reads/writes som passerar loggen).

### `EUTHERDRIVE_TRACE_Z80YM=1`
- **Beskrivning**: Aktiverar logging för följande prefix:
  - `\[Z80YM\]`

### `EUTHERDRIVE_TRACE_Z80_FRAME_CYCLES=1`
- **Beskrivning**: Aktiverar logging för följande prefix:
  - `\[Z80-CYCLES\]`

### `EUTHERDRIVE_TRACE_Z80_AUDIO_RATE=1`
- **Beskrivning**: Aktiverar logging för följande prefix:
  - `\[Z80AUDIO\]`

### `EUTHERDRIVE_TRACE_Z80_FRAME_CYCLES_EVERY=<int>`
### `EUTHERDRIVE_TRACE_Z80_AUDIO_RATE_EVERY=<int>`
### `EUTHERDRIVE_TRACE_Z80_AUDIO_RATE_START_FRAME=<int>`

### `EUTHERDRIVE_TRACE_YM_TIMER=1`
- **Beskrivning**: Aktiverar logging för följande prefix:
  - `\[YMTIMER\]`
  - `\[YMTIMER-REG\]`

### `EUTHERDRIVE_TRACE_YM_TIMER_LIMIT=<int>`

### `EUTHERDRIVE_TRACE_YM_KEY=1`
- **Beskrivning**: Aktiverar logging för följande prefix:
  - `\[YMKEY\]`
  - `\[YMKEYSTAT\]`

### `EUTHERDRIVE_TRACE_YM_KEY_LIMIT=<int>`

### `EUTHERDRIVE_TRACE_YM_WRITE_STATS=1`
- **Beskrivning**: Aktiverar logging för följande prefix:
  - `\[YMWRITE\]`

### `EUTHERDRIVE_TRACE_YM_WRITE_STATS_LIMIT=<int>`

### `EUTHERDRIVE_TRACE_Z80_BANK=1`
- **Beskrivning**: Aktiverar logging för följande prefix:
  - `\[Z80-BANK\]`
  - `\[Z80BANKRD\]`
  - `\[Z80BANKREG68K\]`
  - `\[Z80BANKREG\]`

### `EUTHERDRIVE_TRACE_Z80BANK_READ_LIMIT=<int>`
- **Beskrivning**: Begränsar `\[Z80BANKRD\]`-loggar (0 = obegränsat).

### `EUTHERDRIVE_TRACE_Z80_BOOT=1`
- **Beskrivning**: Aktiverar logging för följande prefix:
  - `\[Z80-BOOT-CODE\]`
  - `\[Z80-BOOT-DUMP\]`
  - `\[Z80-DRIVER-ENTRY\]`
  - `\[Z80BOOTIO\]`

### `EUTHERDRIVE_TRACE_Z80_FIRST_100=1`
- **Beskrivning**: Aktiverar logging för följande prefix:
  - `\[Z80-FIRST-100\]`

### `EUTHERDRIVE_TRACE_Z80_INT_VECTOR=1`
- **Beskrivning**: Aktiverar logging för följande prefix:
  - `\[Z80-INT-VECTOR\]`

### `EUTHERDRIVE_TRACE_Z80_IO=1`
- **Beskrivning**: Aktiverar logging för följande prefix:
  - `\[Z80IO\]`

### `EUTHERDRIVE_TRACE_Z80_IRQ=1`
- **Beskrivning**: Aktiverar logging för följande prefix:
  - `\[Z80-INTERRUPT-ACCEPT\]`
  - `\[Z80-IRQ-ACCEPT\]`
  - `\[Z80-IRQ-DROP\]`

### `EUTHERDRIVE_TRACE_Z80_MBX=1`
- **Beskrivning**: Aktiverar logging för följande prefix:
  - `\[Z80MBX-DATA\]`
  - `\[Z80MBX-POLL-EDGE\]`
  - `\[Z80MBX-POLL\]`
  - `\[Z80MBXRD\]`
  - `\[Z80MBXWR\]`

### `EUTHERDRIVE_TRACE_Z80_MEMORY=1`
- **Beskrivning**: Aktiverar logging för följande prefix:
  - `\[Z80RAMRD\]`
  - `\[Z80RAMWR\]`
  - `\[Z80RD\]`
  - `\[Z80WR65\]`

### `EUTHERDRIVE_TRACE_Z80_RET=1`
- **Beskrivning**: Aktiverar logging för följande prefix:
  - `\[Z80-RET\]`

### `EUTHERDRIVE_TRACE_PC_FRAME=1`
- **Beskrivning**: Loggar M68K-PC per frame:
  - `\[PCFRAME\]`

### `EUTHERDRIVE_TRACE_PC_FRAME_EVERY`
- **Beskrivning**: Logga var N:te frame (default 60; 0 = varje frame).

### `EUTHERDRIVE_Z80_BUSREQ_STABLE_TICKS`
- **Beskrivning**: Antal Z80-instruktions-ticks som BUSREQ måste vara stabilt innan grant (default 0).

### `EUTHERDRIVE_Z80_BANK_WAIT`
- **Beskrivning**: Extra Z80-wait-cycles för 68k-banked reads/writes (default 16).

## Användningsexempel

```bash
# Ingen logging (snabbast)
# (inga env-vars satta)

# Minimal SMS debugging
export EUTHERDRIVE_TRACE_SMS_AUTOFIX=1
export EUTHERDRIVE_TRACE_SMS_EI_DI=1

# Full Z80 debugging
export EUTHERDRIVE_TRACE_Z80=1
export EUTHERDRIVE_TRACE_Z80_MEMORY=1
export EUTHERDRIVE_TRACE_Z80_IO=1
export EUTHERDRIVE_TRACE_Z80_IRQ=1

# Full VDP/VRAM debugging
export EUTHERDRIVE_TRACE_VDP=1
export EUTHERDRIVE_TRACE_VRAM=1

# All logging (extremt långsamt)
export EUTHERDRIVE_TRACE_ALL=1

# Kör emulatorn
dotnet run --project EutherDrive.UI
```

## Prestandaöverväganden

- **Utan gating**: Systemet blir extremt långsamt (flera sekunder per frame)
- **Med gating**: Normala prestandanivåer
- **Rekommenderat**: Använd endast de env-vars du behöver för debugging

### `EUTHERDRIVE_TRACE_PAD=1`
- **Beskrivning**: Aktiverar logging för pad-port access ($A10003, $A10005, $A10009, $A1000B)
- **Loggar**: `[PAD-READ]`, `[PAD-WRITE]`, `[PAD-CTRL-READ]`, `[PAD-HANDSHAKE]`, `[PAD-6BTN-HIGH]`, `[PAD-6BTN-LOW]`

### `EUTHERDRIVE_DIAG_FRAME=1`
- **Beskrivning**: Aktiverar per-frame diagnostisk logging för timing-analys
- **Loggar**: `[DIAG-FRAME]` med Z80 cycles, M68K cycles, YM advance calls per frame
- **Användning**: För att analysera om knapptryckningar påverkar Z80 timing och musik tempo

## Uppdateringshistorik

- **2025-01-17**: Komplett gating implementerad. Alla 567 Console.WriteLine-anrop är nu gated.
- **Implementerat av**: opencode assistant under analys av SMS-emuleringsdebugging
- **Metod**: Automatisk konvertering via Python-script
- **2025-01-26**: Lagt till `EUTHERDRIVE_TRACE_PAD` och `EUTHERDRIVE_DIAG_FRAME` för debugging av pad-port timing problem
