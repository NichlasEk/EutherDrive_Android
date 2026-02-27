# Headless PCE-CD Mode

## Snabbstart

```bash
dotnet run --project EutherDrive.Headless -- "/home/nichlas/roms/PCE/Lords_of_Thunder_(NTSC-U)_[TGXCD1033].cue" 1200
```

## Tvinga PCE-kärna

```bash
EUTHERDRIVE_HEADLESS_CORE=pce dotnet run --project EutherDrive.Headless -- "/path/to/game.cue" 1200
```

## Auto-Run (trycker Start/Run automatiskt)

Detta är tillagt för att kunna trigga förbi BIOS/CD-skärm i headless och få reproducerbar debug.

```bash
EUTHERDRIVE_HEADLESS_CORE=pce \
EUTHERDRIVE_PCE_HEADLESS_AUTO_RUN=1 \
EUTHERDRIVE_PCE_HEADLESS_AUTO_RUN_LOG=1 \
dotnet run --project EutherDrive.Headless -- "/path/to/game.cue" 2000
```

## Auto-Run parametrar

- `EUTHERDRIVE_PCE_HEADLESS_AUTO_RUN=1`
  - Aktiverar auto-tryck på Start/Run.
- `EUTHERDRIVE_PCE_HEADLESS_AUTO_RUN_DELAY_FRAMES` (default `90`)
  - Vänta N frames innan första tryck.
- `EUTHERDRIVE_PCE_HEADLESS_AUTO_RUN_PULSE_FRAMES` (default `3`)
  - Hur många frames knappen hålls nere per puls.
- `EUTHERDRIVE_PCE_HEADLESS_AUTO_RUN_PERIOD_FRAMES` (default `90`)
  - Avstånd mellan pulser.
- `EUTHERDRIVE_PCE_HEADLESS_AUTO_RUN_PULSE_COUNT` (default `8`)
  - Antal pulser totalt.
- `EUTHERDRIVE_PCE_HEADLESS_AUTO_RUN_LOG=1`
  - Loggar när Start går 0->1 och 1->0.

## Nyttiga debugflaggor för CD/SCSI

```bash
EUTHERDRIVE_TRACE_VERBOSE=1
EUTHERDRIVE_PCE_CDREG_LOG=1
EUTHERDRIVE_PCE_CDREG_LOG_LIMIT=600
EUTHERDRIVE_PCE_READ_SUM=1
```

Exempel:

```bash
EUTHERDRIVE_HEADLESS_CORE=pce \
EUTHERDRIVE_PCE_HEADLESS_AUTO_RUN=1 \
EUTHERDRIVE_PCE_HEADLESS_AUTO_RUN_LOG=1 \
EUTHERDRIVE_TRACE_VERBOSE=1 \
EUTHERDRIVE_PCE_CDREG_LOG=1 \
EUTHERDRIVE_PCE_CDREG_LOG_LIMIT=600 \
dotnet run --project EutherDrive.Headless -- "/path/to/game.cue" 2400
```

## Verifierade kommandon (nuvarande)

```bash
EUTHERDRIVE_HEADLESS_CORE=pce \
EUTHERDRIVE_PCE_HEADLESS_AUTO_RUN=1 \
dotnet run --project EutherDrive.Headless -- "/home/nichlas/roms/PCE/Lords_of_Thunder_(NTSC-U)_[TGXCD1033].cue" 2500
```

```bash
EUTHERDRIVE_HEADLESS_CORE=pce \
EUTHERDRIVE_PCE_HEADLESS_AUTO_RUN=1 \
dotnet run --project EutherDrive.Headless -- "/home/nichlas/roms/PCE/Castlevania Rondo of Blood [English]/Castlevania Rondo of Blood [English].cue" 3000
```
