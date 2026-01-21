# Viktigt: Inget logging till /tmp/ mappen!

## Problem identifierat
Emulatorn skriver massor av loggfiler till `/tmp/`-mappen utan att vara "gated":
- `/tmp/eutherdrive_render.log`
- `/tmp/eutherdrive_sram.log` 
- `/tmp/eutherdrive.log`
- `/tmp/eutherdrive_ascii_adapter.log`
- `/tmp/ed_*` (många filer)

## Varför detta är ett problem
1. **Prestanda**: `File.AppendAllText` är långsamt och körs för varje loggrad
2. **Disk-I/O**: Skriver till filsystemet kontinuerligt
3. **Ingen gating**: Körs alltid, oavsett om debug är aktiverat
4. **Svårt att felsöka**: Loggar sprids över många filer

## Lösning
All logging måste vara "gated" genom `MdLog`-klassen och kontrolleras av env-vars:
- `EUTHERDRIVE_TRACE_VDP=1` för VDP-logging
- `EUTHERDRIVE_TRACE_SRAM=1` för SRAM-logging
- `EUTHERDRIVE_TRACE_UI=1` för UI-logging
- etc.

## Åtgärder som behövs
1. Ta bort alla `File.AppendAllText` till `/tmp/`-filer
2. Konvertera till `MdLog.WriteLineXxx()` med env-var kontroll
3. Använd `Console.WriteLine` (som redan är gated) istället för fil-I/O

## Undantag
Endast undantag är om användaren explicit vill logga till fil, t.ex.:
- `EUTHERDRIVE_LOG_TO_FILE=1` (inte implementerat än)
- Specifika debug-scenarion som kräver filoutput

## Status
- [x] Identifierat problemet
- [ ] Fixat alla `/tmp/`-loggar
- [ ] Testat prestandaförbättring