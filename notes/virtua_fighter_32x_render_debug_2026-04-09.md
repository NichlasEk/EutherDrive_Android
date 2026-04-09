# Virtua Fighter 32X render debug, 2026-04-09

## Situation

Symptom i EutherDrive host bridge-läget för 32X:

- HUD och bakgrund syns.
- Kombattanterna syns inte korrekt.
- Hälsomätarna uppdateras, vilket tyder på att spelet kör vidare och att problemet ligger i videokompositionen eller 32X-framebuffer-skrivningarna.

Testad ROM:

- `/run/media/nichlas/Atlas/roms/Genesis/32x/Virtua Fighter (32X) (JU) [!].32x`

Savestate funnen:

- `/run/media/nichlas/Atlas/roms/Genesis/32x/Virtua_Fighter__32X___JU_c595e136.euthstate`

## Bekräftade observationer

Headless-load av savestate fungerade formellt:

```text
[HEADLESS] Loaded savestate slot 1 payload (38152231 bytes)
[HEADLESS] Savestate loaded successfully from file
```

Men savestaten återställde inte framebuffer direkt:

```text
[HEADLESS] Framebuffer BEFORE running:
[MdTracerAdapter] Framebuffer is empty/transparent
```

Efter en frame fanns bild igen, och dumpad frame matchade det visuella felet: bakgrund/HUD synlig, inga fighters.

Dumpad fil:

- `/home/nichlas/EutherDrive_Android/logs/headless_output.ppm`
- konverterad till `/home/nichlas/EutherDrive_Android/logs/headless_output.png`

## Viktig slutsats om savestate i 32X host bridge

32X-savestate i host bridge-läget är sannolikt inte komplett.

`MdTracerStateSerializer` inkluderar inte `_sega32XCore` eller SH2/32X-VDP-state:

- `/home/nichlas/EutherDrive_Android/EutherDrive.Core/Savestates/MdTracerStateSerializer.cs`

Komponenter som sparas:

- `md_main`
- `md_m68k`
- `md_z80`
- `md_vdp`
- `md_bus`
- `md_music`
- `md_control`

32X-kärnan skapas separat i `MdTracerAdapter`:

- `/home/nichlas/EutherDrive_Android/EutherDrive.Core/MdTracerAdapter.cs`

Relevanta rader:

- init av 32X host bridge kring `1478-1483`

Det betyder att en load kan lämna Mega Drive-sidan i gammalt läge men 32X-sidan i reset/default eller annan osynkad state.

## Huvudmisstanke efter jämförelse mot jgenesis

Det finns en konkret avvikelse i 32X VDP-framebuffer-skrivningarna.

### Vår kod

Fil:

- `/home/nichlas/EutherDrive_Android/EutherDrive.Core/Sega32X/Sega32XVdp.cs`

Metod:

- `WriteFrameBufferWord(uint address, ushort value)` kring rad 316

Nuvarande beteende:

- `0x0000` ignoreras helt
- high byte skrivs bara om det inte är 0
- low byte skrivs bara om det inte är 0

Det gör att vanliga word-skrivningar beter sig som en slags overwrite/transparent write.

### jgenesis

Referensfil:

- `/home/nichlas/jgenesis/backend/s32x-core/src/vdp.rs`

Metod:

- `write_frame_buffer_word()` kring rad 524

Referensbeteende:

- vanliga word writes skriver alltid hela ordet
- endast overwrite-vägen är selektiv för nollbytes

Detta är sannolikt rätt hårdvarubeteende och en mycket stark kandidat för Virtua Fighter-felet.

## Patch jag tänkte göra

Målfil:

- `/home/nichlas/EutherDrive_Android/EutherDrive.Core/Sega32X/Sega32XVdp.cs`

Byt ut `WriteFrameBufferWord()` från nuvarande selektiva skrivning till full ordskrivning.

Avsedd implementation:

```csharp
public void WriteFrameBufferWord(uint address, ushort value)
{
    ushort[] frameBuffer = GetWriteBuffer();
    int index = (int)(((address & 0x1FFFF) >> 1) % frameBuffer.Length);
    frameBuffer[index] = value;
    TraceFrameBufferWriteIfEnabled("word", address, value, frameBuffer);
}
```

## Varför jag inte patchade direkt

Filen var inte skrivbar. Ägarskap vid felsökningstillfället:

```text
-rw-r--r-- 1 nobody nobody ... EutherDrive.Core/Sega32X/Sega32XVdp.cs
```

Försök att patcha med `apply_patch` misslyckades därför.

## Kommando att köra efter reboot

Om filen fortfarande ägs av `nobody`, kör:

```bash
sudo chown nichlas:nichlas /home/nichlas/EutherDrive_Android/EutherDrive.Core/Sega32X/Sega32XVdp.cs
```

## Rekommenderad ordning efter reboot

1. Fixa filägarskap för `Sega32XVdp.cs` om det behövs.
2. Applicera patchen i `WriteFrameBufferWord()`.
3. Bygg eller kör headless med samma savestate.
4. Jämför ny dumpad bild mot:
   - `/home/nichlas/EutherDrive_Android/logs/headless_output.png`
5. Om fighters fortfarande saknas:
   - felsök därefter 32X-prioritetskompositionen i `CompositeBgraOver()`
   - men framebuffer-write-buggen bör fixas först eftersom den är en klar avvikelse mot jgenesis

## Kommando som användes för reproduktion

```bash
dotnet run --project /home/nichlas/EutherDrive_Android/EutherDrive.Headless/EutherDrive.Headless.csproj -c Release -- --load-savestate '/run/media/nichlas/Atlas/roms/Genesis/32x/Virtua Fighter (32X) (JU) [!].32x' '/run/media/nichlas/Atlas/roms/Genesis/32x/Virtua_Fighter__32X___JU_c595e136.euthstate' 1
```

## Sekundär misstanke att kontrollera efter patch

Om buggen kvarstår efter `WriteFrameBufferWord()`-fixen, granska:

- `/home/nichlas/EutherDrive_Android/EutherDrive.Core/Sega32X/Sega32XVdp.cs`
- metod `CompositeBgraOver()`
- metod `ApplyPriorityMask()`

Men just nu är framebuffer-write-avvikelsen den mest konkreta, minst spekulativa buggen.

## Fortsättning efter reboot / återupptaget arbete

Genomfört:

- `Sega32XVdp.WriteFrameBufferWord()` patchades till full ordskrivning enligt planen.
- Headless-repro kördes igen med samma ROM + savestate.
- Ny dump konverterades till PNG och jämfördes visuellt mot tidigare `logs/headless_output.png`.

Resultat:

- Bygg och headless-körning gick igenom.
- Den nya bilden såg oförändrad ut: bakgrund/HUD syns fortfarande, fighters saknas fortfarande.
- Det betyder att framebuffer-write-buggen var en riktig avvikelse mot jgenesis, men den var inte tillräcklig för att lösa Virtua Fighter-felet i den här reproduktionen.

Testad men ej behållen hypotes:

- En mer aggressiv host-bridge-ändring i `CompositeBgraOver()` testades kort: att inte kasta bort icke-noll 32X-pixlar bakom en opak MD-bild.
- Den gav ingen synlig skillnad i dumpad output och revertades direkt.
- Slutsats: det här felet verkar inte bero på just den enkla lågprioritetsblockeringen i nuvarande kompositionsheuristik.

Aktuell status i arbetskopian:

- `WriteFrameBufferWord()`-fixen är kvar i `EutherDrive.Core/Sega32X/Sega32XVdp.cs`.
- Den tillfälliga `CompositeBgraOver()`-experimentpatchen är borttagen igen.

Nästa rimliga steg:

1. Verifiera om fighters redan saknas i ren 32X-rendering innan host-bridge-kompositionen.
2. Om de redan saknas där: felsök 32X-renderbanan vidare i `GetRenderedPixel()`, renderläget, line-address-data eller SH2/VDP-state.
3. Om de finns i ren 32X-rendering men försvinner först vid host bridge: då behövs bättre Genesis-transparens/lagerinformation än dagens opaka MD-framebuffer.

## Savestate-status efter 32X-fix

Det finns nu en riktig 32X-savestateväg i host bridge-läget:

- `MdTracerStateSerializer` har en ny 32X-komponent (`sega32x_core`) när `_sega32XCore` finns.
- Den sparar/laddar explicit 32X core-state i stället för att bara ta Mega Drive-delarna.
- Versionen på MD-statepayloaden höjdes till `3`.

Det som nu ingår i 32X-state:

- 32X-bussens RAM/registerstate via `Sega32XBus`
- 32X VDP-state via `Sega32XVdp`
- systemregister/DREQ/interruptstate via `Sega32XSystemRegisters`
- båda SH2-CPU:ernas register/tickstate
- båda SH2-bussarnas interna cache/timer/divu/dma-register
- 32X core frame counter / comm-sync-flagga

Verifiering som kördes:

- `EUTHERDRIVE_HEADLESS_CORE=md dotnet run --no-build --project /home/nichlas/EutherDrive_Android/EutherDrive.Headless/EutherDrive.Headless.csproj -c Release -- --test-savestate '/run/media/nichlas/Atlas/roms/Genesis/32x/Virtua Fighter (32X) (JU) [!].32x'`
- Resultat: `Savestate roundtrip ok.`

Bakåtkompatibilitet:

- Gamla MD-savestates med payloadversion `2` laddas fortfarande.
- Om ROM:en körs i 32X host bridge-läge loggas nu en tydlig varning om att legacy-save saknar 32X-state och därför kan återställas ofullständigt.
