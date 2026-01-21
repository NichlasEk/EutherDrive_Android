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
  - `\[VINT-SKIP\]`
  - `\[VINT\]`

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

### `EUTHERDRIVE_TRACE_Z80YM=1`
- **Beskrivning**: Aktiverar logging för följande prefix:
  - `\[Z80YM\]`

### `EUTHERDRIVE_TRACE_Z80_BANK=1`
- **Beskrivning**: Aktiverar logging för följande prefix:
  - `\[Z80-BANK\]`
  - `\[Z80BANKRD\]`
  - `\[Z80BANKREG68K\]`
  - `\[Z80BANKREG\]`

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

## Uppdateringshistorik

- **2025-01-17**: Komplett gating implementerad. Alla 567 Console.WriteLine-anrop är nu gated.
- **Implementerat av**: opencode assistant under analys av SMS-emuleringsdebugging
- **Metod**: Automatisk konvertering via Python-script
