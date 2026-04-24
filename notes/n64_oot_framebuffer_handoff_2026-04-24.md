# N64 OoT Framebuffer Handoff (2026-04-24)

## Mål

Få fram en riktig och stabil bildbuffer för Zelda: Ocarina of Time i nuvarande N64-kärna, i stället för att falla tillbaka till felaktiga kandidater som råkar se "bildlika" ut.

## Kort slutsats

Det ser inte längre ut som att huvudproblemet bara är att hitta "någon" buffer. Den nuvarande situationen pekar mer på att vi väljer fel sorts producerad buffer:

- tidigare valde heuristiken en uppenbart felaktig, nyligen nollad buffer i hög RDRAM
- efter senaste stramningen väljs inte den längre
- nu hamnar valet i stället ofta på låg RDRAM som innehåller strukturerat men felaktigt innehåll
- den strukturen ser mer ut som djupbuffer, scratchbuffer eller annan mellanbuffer än som riktig färgbuffer

Det betyder att nästa riktiga steg är att skilja färgbuffer från djup/scratch, inte att bara göra fullscan "smartare" på måfå.

## Det som nu är bevisat

### 1. OoT initierar VI, men med misstänkt origin

OoT når riktig VI-init, men `VI_ORIGIN` ligger kvar på låga värden:

- först runt `0x00000280`
- sedan ofta `0x0000027f`

Det innebär två saker:

- VI är inte helt död
- `VI_ORIGIN` kan inte ensam användas som sann bildbufferadress i det här läget

### 2. Den gamla kandidaten `0x007da000` var en falsk träff

Med range-watch såg vi att `0x007da000` CPU-nollades sekventiellt från bootkod. Den såg bra ut i gamla heuristiken eftersom den var:

- sammanhängande
- lugn
- inte brusig

men den var ändå inte en riktig displaybuffer.

Det var ett viktigt steg, för det visade att vår tidigare scoring belönade "ren nollclear" som om det vore en bra bild.

### 3. Efter heuristikändringen väljs inte längre `0x007da000`

Senaste korta OoT-proben gav i stället:

- `Visual framebuffer override used (vi=0x0000027f, recent=0x007da000 -> fb=0x00010200, ...)`

Det är framsteg i den meningen att den gamla falska träffen försvann. Men det är fortfarande inte rätt slutläge, eftersom `0x00010200` är för lågt och bilden fortfarande är fel.

### 4. Nuvarande bild ser producerad ut, men inte som färgbuffer

De senaste bilderna har:

- tydlig struktur
- större sammanhängande former
- återkommande block och gradientliknande fält
- men fel färger och kraftigt skräpigt utseende

Det ser därför mer ut som att vi läser:

- djupbuffer
- workbuffer
- mellanbuffer
- eller rätt område med fel tolkning

än att vi bara läser helt slumpmässigt RAM.

## Viktigaste nuvarande hypotes

Den mest sannolika tekniska förklaringen just nu är:

1. riktig färgproducering finns åtminstone delvis
2. fallbacken väljer ändå fel kandidat, för att den bara mäter "bildlikhet"
3. den gör ännu ingen skillnad mellan:
   - färgbuffer
   - djupbuffer
   - scratch/workbuffer

Det som saknas är alltså inte bara "bättre scan", utan klassificering av vilken sorts buffer vi tittar på.

## Det som redan är ändrat

I [Ryu64Core.cs](/home/nichlas/EutherDrive_Android/Ryu64/Ryu64Core/Ryu64Core.cs):

- bred framebuffer-scan är inte default längre
- låg-RAM-kandidater under `0x00010000` är spärrade från den generella heuristikvägen
- tomma och nästan helt nollade sidor straffas hårdare
- visual override kräver nu mindre eller större marginal beroende på hur svag den nuvarande kandidaten är

Senaste commit för just det här:

- `1ba7439` `Tighten N64 framebuffer fallback heuristics`

## Relevanta filer just nu

- [Ryu64Core.cs](/home/nichlas/EutherDrive_Android/Ryu64/Ryu64Core/Ryu64Core.cs)
  - `TryGetFramebuffer`
  - `ScoreFramebufferCandidate`
  - `FindBestFramebufferOrigin`
  - `RefineFramebufferOriginNearHint`

- [Memory.cs](/home/nichlas/EutherDrive_Android/Ryu64/Ryu64.MIPS/Memory.cs)
  - framebuffer-tracking
  - VI-origin write-event
  - RDP/DPC-relaterad bufferinfo
  - SP/DMEM-spårning som kan behövas om problemet visar sig ligga uppströms

## Hur jag brukar köra

Arbetssättet som gett bäst signal hittills är:

### 1. Bygg först, kort probe sedan

Jag kör nästan alltid:

```bash
dotnet build EutherDrive.Headless/EutherDrive.Headless.csproj -c Release -nologo -v q -nr:false -p:IncludeOptionalCores=true
```

Sedan en kort OoT-probe på typ `10-20` frames i stället för direkt långkörning:

```bash
dotnet EutherDrive.Headless/bin/Release/net8.0/EutherDrive.Headless.dll "/home/nichlas/roms/N64/Legend of Zelda, The - Ocarina of Time (USA) (Rev 2).z64" 20 > /tmp/oot_probe.log 2>&1
```

Poängen är att:

- få första framebuffer recovery-raden snabbt
- se om ändringen flyttade kandidatvalet alls
- undvika att drunkna i långa loggar innan vi vet att den första effekten blev rätt

### 2. Läs bara de relevanta raderna först

Jag brukar först extrahera:

```bash
rg -n "Framebuffer recovered|Framebuffer steady|Visual framebuffer override used|Recent RDRAM framebuffer used|No framebuffer yet" /tmp/oot_probe.log
```

Det räcker ofta för att avgöra om senaste patchen ens rörde rätt sak.

### 3. När en kandidat ser misstänkt ut, watcha just den sidan

Om en kandidat som `0x007da000` eller `0x00010200` dyker upp kör jag en riktad watch:

```bash
env EUTHERDRIVE_TRACE_N64_WATCH_RANGE_START=0x007da000 EUTHERDRIVE_TRACE_N64_WATCH_RANGE_END=0x007dbfff \
dotnet EutherDrive.Headless/bin/Release/net8.0/EutherDrive.Headless.dll "/home/nichlas/roms/N64/Legend of Zelda, The - Ocarina of Time (USA) (Rev 2).z64" 20 > /tmp/oot_watch.log 2>&1
```

eller motsvarande för den nya kandidaten.

Det ger svar på:

- om området bara nollas av CPU
- om det fylls blockvis
- om det ser ut som producerad data

### 4. Först när kort probe rör sig åt rätt håll kör jag längre

Längre körning är bara värdefull när:

- första recovery-kandidaten ser rimlig ut
- eller vi har en specifik hypotes att verifiera

Annars blir det bara mer brus.

### 5. Jag committar helst små, isolerade steg

Om en patch:

- tydligt ändrar kandidatval
- eller tar bort en uppenbart falsk träff

så är det värt en liten ren commit även om slutbilden fortfarande är fel.

Det gör det mycket lättare att backa tankespår utan att riva upp annat.

## Praktisk plan härifrån

### Steg 1: logga toppkandidater i scan-vägen

Just nu saknas bra synlighet i varför just `0x00010200` vinner över andra kandidater.

Nästa patch bör därför:

- logga topp några kandidater från `FindBestFramebufferOrigin`
- inkludera adress och score
- helst bara när `VI_ORIGIN` är misstänkt låg

Det ska göra att vi kan se om:

- låg-RAM fortfarande vinner för lätt
- hög RDRAM faktiskt finns med men förlorar
- eller om alla "bra" kandidater egentligen ser ut som fel buffertyp

### Steg 2: särskilj färgbuffer från djupbuffer/scratch

Efter topplistan bör heuristiken kompletteras med extra straff eller klassificering för kandidater som beter sig som:

- djupbuffer
- memset-liknande workspace
- blockvis metadata

Troliga signaler att använda:

- för låg entropi på "fel" sätt
- stark vertikal/horisontell struktur men dålig färgvariation
- mönster som ser ut som monotona 16-bitars gradientfält snarare än pixeldata

### Steg 3: när producer-spårning finns, låt den dominera

Om render-sidan redan vet något om vilka buffers som faktiskt användes, ska den informationen väga tyngre än fullscan.

Scanningen ska vara en reservväg, inte första sanningskälla.

### Steg 4: verifiera i två pass

För varje kandidatändring:

1. kort OoT-probe `10-20` frames
2. om första recovery ser bättre ut, längre probe

Sedan snabb regression mot ett annat N64-spel så att vi inte förbättrar OoT genom att förstöra allt annat.

## Vad som inte är värt att jaga först

Just nu bör vi inte börja med:

- ännu en bred scanjustering utan mer observabilitet
- fler generella framebufferkommentarer om byte order ensam
- långa OoT-körningar innan första recovery-raden ser vettig ut

Det riskerar bara att flytta runt fel kandidater utan att förklara varför.

## Praktisk definition av framsteg

Följande räknas som verkligt framsteg:

- första framebuffer recovery går till en hög och stabil kandidat som inte är uppenbar scratch
- bilden blir mindre "djupbuffer-lik" och mer färgbuffer-lik
- kandidatvalet blir mindre vandrande mellan körningar
- längre probe håller samma kandidat utan att falla tillbaka till låg-RAM-skräp

## Kort restart-version

Om man återstartar från den här filen, gör i den här ordningen:

1. bygg headless
2. kör OoT i `20` frames
3. läs första framebuffer recovery-raden
4. logga topprankade scan-kandidater
5. straffa djup/scratch-kandidater explicit
6. verifiera kort igen innan längre körning

## Relevanta loggar från senaste passet

- `/tmp/oot_fbcheck_current.log`
- `/tmp/oot_fbwatch.log`
- `/tmp/oot_fbwatch2.log`
- `/tmp/oot_fbwatch3.log`

## Nuvarande status i en mening

Vi har tagit bort minst en konkret falsk framebufferträff, men det som återstår är att skilja riktig färgbuffer från andra producerade bildlika buffers, inte att bara fortsätta skanna bredare.

## Senare status samma dag: boot tar sig längre

Efter vidare semantisk jämförelse är läget inte längre samma som i första framebuffer-passningen. Det gamla stoppet runt `0x80000810` är passerat, och OoT kommer nu in i OS/VI/RSP-interruptvägar.

Direkta ändringar som är viktiga för det:

- RSP `break` markerar inte längre alltid `HALT|BROKE` för en giltig task. För taskar låter vi status/signaler ligga kvar så att RSP-interruptvägen kan fortsätta.
- COP1/FPR lagras nu som råa 64-bitars register i stället för som `double`.
- `LWC1`, `SWC1`, `LDC1` och `SDC1` använder rå FPR-lagring.
- FPR-mappningen respekterar CP0 Status `FR`: i FR=0 mappar single-register till even/odd-halvor i ett registerpar, medan double går till even-paret; i FR=1 används registret direkt.
- De tidigare brusiga bringup-loggarna är fortfarande huvudsakligen opt-in, så nästa körningar bör kunna fokusera på faktisk progression.

Verifierat buildläge:

```bash
dotnet build EutherDrive.Headless/EutherDrive.Headless.csproj -c Release -nologo -v minimal -nr:false -m:1 -p:IncludeOptionalCores=true
```

Build går igenom med `0 Error(s)`. Varningsmängden är fortfarande hög och verkar inte introducerad av det här passet.

Senaste relevanta körningar:

- `/tmp/oot_pc_1300_fpr_fr.log`
- `/tmp/oot_long_fpr_fr.log`

Viktig observation från PC-window-körningen:

- `0x80001300..0x800013b8` är inte ett fast PC-stopp.
- Loopen rör `s0/s1`, skiftar `a3` som bitbuffer och hoppar vidare till senare OS/interrupt-kod.
- Den ska därför inte behandlas som en deadlock utan som dekompression/bitstream-arbete som bara är dyrt i interpreter.

Senaste längre körningen nådde ungefär hit innan paus:

- frame cirka `5040`
- `VI_STATUS=0x0000311e`
- `VI_WIDTH=320`
- `MI_INTR=0x00000001`
- `MI_MASK=0x0000003f`
- `COP0_CAUSE=0x00000400`
- PC syns runt `0x80002660`, `0x80002d9c`, `0x80002ddc`
- `VI_ORIGIN` är fortfarande låg, runt `0x0000027f`/`0x00000280`

Det betyder:

- VI är aktiv.
- RCP/VI-interrupten levereras.
- Det finns fortfarande ingen trovärdig färg-framebuffer.
- Den låga `VI_ORIGIN` ska fortfarande betraktas som misstänkt eller ofärdig, inte som sann framebuffer.

Ljudstatus:

- Ljud init är inte ett krav för att ta sig vidare i det här bootläget.
- Loggraderna `No audio yet` är därför sekundära så länge CPU/VI/RSP fortsätter röra sig.
- Om ett spel senare väntar på AI-interrupt kan audio bli blockerande, men det är inte vad det aktuella OoT-spåret visar.

Nästa konkreta spår:

1. Fortsätt från `/tmp/oot_long_fpr_fr.log`-läget med längre körning eller smalare PC-window runt `0x80002d70..0x80002e20`.
2. Avgör om `MI_INTR=VI` hålls pending för länge eller om exception handlern återvänder korrekt efter VI-ack.
3. Jämför VI current/ack-semantik: skriv till `VI_CURRENT` ska rensa VI-interrupt utan att skriva nytt current-värde.
4. Kontrollera om `VI_ORIGIN` faktiskt skrivs med en riktig framebuffer senare, eller om vi saknar en upstream RDP/RSP-producer.
5. Om det fortfarande saknas plausibel origin efter längre run, gå tillbaka till producer-spårning via DPC/RSP snarare än bredare framebuffer-scan.

Praktisk restart-version från det nya läget:

```bash
dotnet build EutherDrive.Headless/EutherDrive.Headless.csproj -c Release -nologo -v minimal -nr:false -m:1 -p:IncludeOptionalCores=true
timeout 220s env EUTHERDRIVE_N64_RUNFRAME_WAIT_MS=5 EUTHERDRIVE_N64_BRINGUP_RUNFRAME_WAIT_MS=5 EUTHERDRIVE_N64_BRINGUP_FRAME_LIMIT=400 \
  dotnet EutherDrive.Headless/bin/Release/net8.0/EutherDrive.Headless.dll \
  "/home/nichlas/roms/N64/Legend of Zelda, The - Ocarina of Time (USA) (Rev 2).z64" 8311 \
  > /tmp/oot_long_fpr_fr.log 2>&1
```

Snabb extract efter körning:

```bash
rg -n "Address error|TLB refill|CPU exception|R4300 watchdog|No framebuffer yet|Exec:" /tmp/oot_long_fpr_fr.log
```
