# Headless PCE-CD Mode

## Grundregel

Nuvarande builds auto-detekterar normalt `PCE CD`, `PSX` och `Sega CD` via första datatracken i `.cue`.

Tvinga ändå gärna kärnan när du vill eliminera all routing-osäkerhet under debug:

```bash
EUTHERDRIVE_HEADLESS_CORE=pce
```

Om detta saknas kan `.cue` råka gå genom fel kärna.

För debugkörningar som redan är byggda är den säkra formen:

```bash
dotnet run --no-build --no-restore --project EutherDrive.Headless/EutherDrive.Headless.csproj -c Release -- ...
```

## Snabbstart

```bash
EUTHERDRIVE_HEADLESS_CORE=pce \
dotnet run --no-build --no-restore --project EutherDrive.Headless/EutherDrive.Headless.csproj -c Release -- \
  "/path/to/game.cue" \
  1200
```

## Verbose

För att få konsolutskrifter i headless:

```bash
EUTHERDRIVE_TRACE_VERBOSE=1
```

Detta behövs eftersom `Headless` annars kan tysta stdout/stderr.

## Auto-Run

Detta trycker Start/Run automatiskt för att ta sig förbi BIOS/CD-skärm och få reproducerbar debug.

```bash
EUTHERDRIVE_HEADLESS_CORE=pce \
EUTHERDRIVE_PCE_HEADLESS_AUTO_RUN=1 \
EUTHERDRIVE_PCE_HEADLESS_AUTO_RUN_LOG=1 \
dotnet run --no-build --no-restore --project EutherDrive.Headless/EutherDrive.Headless.csproj -c Release -- \
  "/path/to/game.cue" \
  2000
```

## Auto-Run parametrar

- `EUTHERDRIVE_PCE_HEADLESS_AUTO_RUN=1`
- `EUTHERDRIVE_PCE_HEADLESS_AUTO_RUN_DELAY_FRAMES` default `90`
- `EUTHERDRIVE_PCE_HEADLESS_AUTO_RUN_PULSE_FRAMES` default `3`
- `EUTHERDRIVE_PCE_HEADLESS_AUTO_RUN_PERIOD_FRAMES` default `90`
- `EUTHERDRIVE_PCE_HEADLESS_AUTO_RUN_PULSE_COUNT` default `8`
- `EUTHERDRIVE_PCE_HEADLESS_AUTO_RUN_LOG=1`

## Savestate: rätt väg för PCE

För PCE är den verifierade vägen att använda `--load-savestate` och välja slot via env:

```bash
EUTHERDRIVE_HEADLESS_CORE=pce \
EUTHERDRIVE_TRACE_VERBOSE=1 \
EUTHERDRIVE_SAVESTATE_SLOT=1 \
dotnet run --no-build --no-restore --project EutherDrive.Headless/EutherDrive.Headless.csproj -c Release -- \
  --load-savestate \
  "/path/to/game.cue" \
  "/home/nichlas/EutherDrive/savestates/Game_Name_hash.euthstate" \
  120
```

Det här är den form som faktiskt fungerade för `Steam-Heart's` och gav frame-trace.

## Viktig not om `LOAD_SLOT1_ON_BOOT`

`EUTHERDRIVE_LOAD_SLOT1_ON_BOOT=1` finns i vanlig headless-väg, men den PCE-specifika `RunFromSavestate()`-vägen använder i praktiken `--load-savestate` + `EUTHERDRIVE_SAVESTATE_SLOT`.

Om du vill debugga en specifik PCE-slot ska du därför utgå från:

```bash
--load-savestate ... .euthstate
EUTHERDRIVE_SAVESTATE_SLOT=1
```

## Dumpa output till en egen katalog

```bash
EUTHERDRIVE_HEADLESS_DUMP_DIR=/tmp/pce_debug
```

Nyttigt när man inte vill blanda ihop filer i `logs/`.

## Determinism-trace

Skriv en rad per frame med CPU/VDC/CD och hash för framebuffer, RAM, VRAM, SAT och VCE:

```bash
EUTHERDRIVE_HEADLESS_CORE=pce \
EUTHERDRIVE_TRACE_VERBOSE=1 \
EUTHERDRIVE_SAVESTATE_SLOT=1 \
EUTHERDRIVE_PCE_TRACE_FILE=/tmp/pce_trace.log \
dotnet run --no-build --no-restore --project EutherDrive.Headless/EutherDrive.Headless.csproj -c Release -- \
  --load-savestate \
  "/path/to/game.cue" \
  "/home/nichlas/EutherDrive/savestates/Game_Name_hash.euthstate" \
  120
```

Detta är den viktigaste traceformen när man vill hitta första låsframe eller första grafiska divergens utan att gissa.

Exempel på fält i loggen:

- `cpu_pc`
- `cpu_a`, `cpu_x`, `cpu_y`, `cpu_p`
- `fb_hash`
- `bus_ram`
- `vram_hash`
- `sat_hash`
- `wait_irq`
- `cd_pending`
- `cd_cmd`
- `cd_phase`
- `cd_irqa`
- `cd_irqe`

## Diffa två körningar

```bash
python scripts/pce_trace_diff.py pce_trace_a.log pce_trace_b.log
```

## CD/SCSI-spårning

Bra första uppsättning:

```bash
EUTHERDRIVE_TRACE_VERBOSE=1
EUTHERDRIVE_PCE_CDREG_LOG=1
EUTHERDRIVE_PCE_CDREG_LOG_LIMIT=600
EUTHERDRIVE_PCE_IRQ_LOG=1
EUTHERDRIVE_PCE_SCSI_LOG=1
```

Exempel:

```bash
EUTHERDRIVE_HEADLESS_CORE=pce \
EUTHERDRIVE_TRACE_VERBOSE=1 \
EUTHERDRIVE_SAVESTATE_SLOT=1 \
EUTHERDRIVE_PCE_CDREG_LOG=1 \
EUTHERDRIVE_PCE_CDREG_LOG_LIMIT=600 \
EUTHERDRIVE_PCE_IRQ_LOG=1 \
EUTHERDRIVE_PCE_SCSI_LOG=1 \
dotnet run --no-build --no-restore --project EutherDrive.Headless/EutherDrive.Headless.csproj -c Release -- \
  --load-savestate \
  "/path/to/game.cue" \
  "/home/nichlas/EutherDrive/savestates/Game_Name_hash.euthstate" \
  85
```

Det här är bra när spelet fastnar i CD-IRQ-poll eller SCSI-handshake.

## PCE-specifik grafiktrace

Riktade flaggor som redan finns:

- `EUTHERDRIVE_PCE_BLOCK_TRACE=1`
- `EUTHERDRIVE_PCE_VDC_BUS_TRACE=1`
- `EUTHERDRIVE_PCE_VRAM_WRITE_TRACE=1`
- `EUTHERDRIVE_PCE_SPR_LINE_TRACE=1`
- `EUTHERDRIVE_PCE_PIXEL_TRACE=1`
- `EUTHERDRIVE_PCE_SAT_FRAME_TRACE=1`

Exempel:

```bash
EUTHERDRIVE_HEADLESS_CORE=pce \
EUTHERDRIVE_TRACE_VERBOSE=1 \
EUTHERDRIVE_SAVESTATE_SLOT=1 \
EUTHERDRIVE_PCE_BLOCK_TRACE=1 \
EUTHERDRIVE_PCE_VDC_BUS_TRACE=1 \
EUTHERDRIVE_PCE_VRAM_WRITE_TRACE=1 \
dotnet run --no-build --no-restore --project EutherDrive.Headless/EutherDrive.Headless.csproj -c Release -- \
  --load-savestate \
  "/path/to/game.cue" \
  "/home/nichlas/EutherDrive/savestates/Game_Name_hash.euthstate" \
  3
```

## Snapshot från headless

PCE-headless tar snapshots i `logs/` eller i katalogen från `EUTHERDRIVE_HEADLESS_DUMP_DIR`.

Typiska filer:

- `pcesnap_*_meta.txt`
- `pcesnap_*_bus.txt`
- `pcesnap_*_ppu.txt`
- `pcesnap_*_sprites.txt`
- `pcesnap_*_vram.bin`
- `pcesnap_*_sat_raw.bin`
- `pcesnap_*_state_raw.bin`

Detta är bra när man vill:

- dissekera SAT/VRAM exakt i ett låst läge
- återköra samma tillstånd via `--load-raw-state`
- jämföra två snapshots binärt

## Verifierade exempel

### Golden Axe slot 1

```bash
EUTHERDRIVE_HEADLESS_CORE=pce \
EUTHERDRIVE_TRACE_VERBOSE=1 \
EUTHERDRIVE_SAVESTATE_SLOT=1 \
dotnet run --no-build --no-restore --project EutherDrive.Headless/EutherDrive.Headless.csproj -c Release -- \
  --load-savestate \
  "/home/nichlas/roms/PCE/GoldenAxe/Golden Axe (JP).cue" \
  "/home/nichlas/EutherDrive/savestates/Golden_Axe__JP_.cue_aa34155e.euthstate" \
  1
```

### Steam-Heart's slot 1

```bash
EUTHERDRIVE_HEADLESS_CORE=pce \
EUTHERDRIVE_TRACE_VERBOSE=1 \
EUTHERDRIVE_SAVESTATE_SLOT=1 \
EUTHERDRIVE_PCE_TRACE_FILE=/tmp/steam_pce_trace.log \
dotnet run --no-build --no-restore --project EutherDrive.Headless/EutherDrive.Headless.csproj -c Release -- \
  --load-savestate \
  "/home/nichlas/roms/PCE/Steam-Heart's (Japan)/Steam-Heart's (Japan).cue" \
  "/home/nichlas/EutherDrive/savestates/Steam-Heart_s__Japan_.cue_294bdeed.euthstate" \
  120
```

I den körningen såg vi att låset började mellan frame `72` och `73`, med:

- `wait_irq=1`
- `cd_pending=1`
- stillastående `fb_hash`
- stillastående `bus_ram`
- stillastående `vram_hash`

Det är ett bra mönster att leta efter vid PCE-CD-häng.
