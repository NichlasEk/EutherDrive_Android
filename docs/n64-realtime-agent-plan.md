# N64 till 100 procent realtid: arbetsplan för agenter och subagenter

Datum: 2026-09-23. Plattform: **Linux desktop**, Ryu64-kärnan med den befintliga
Vulkan-baserade RDP-renderaren. Android ingår inte i detta arbete.

Det här är en genomförandeplan, inte ett löfte om att alla spel redan går att
få till full fart med några små ändringar. Den viktigaste riktningen är att
minska mängden värdarbete per emulerad instruktion och per bild. CPU/RSP ska
kunna göra mer användbart arbete mellan sina kontroller. GPU:n ska behålla
data längre utan att CPU:n får läsa gamla värden. Spelens tid, ljud, grafik och
input ska samtidigt fortsätta vara rätt.

Aktuell standard 2026-09-26: CPU-ägd instruktionssidcache och CACHE-filtrerad
mappad JIT är aktiverade. De uppmätta GPU-vinsterna behåller sina tidigare
standardinställningar och ROM-avgränsningar. Bred mappad JIT, mappade läsningar,
trådlokal sidcache och RSP-försöken utan stabil vinst är inte standard.
Se [senaste resultat, start och återställning](n64-gauntlet-instruction-page-cache-2026-09-26.md).

Tidigare utgångspunkt 2026-09-26: efter Gauntlets texture-read-overlap-pass
finns en ny [profil av hela emulatorn](n64-gauntlet-phase-profile-2026-09-26.md).
CPU-tolken och TLB-översättningen är tydliga kandidater utöver RSP/GPU-väntan.
Nuvarande CPU-JIT tar inte emot Gauntlets mappade programkod. Nästa större
CPU-milstolpe är därför korrekt stöd för mappade instruktionssidor, med
TLB-generation/ASID, fysiska kodkontroller och oförändrade virtuella PC-värden.
Rapporten beskriver avgränsning, tester och resultat från det mindre TLB-försöket.

Senare samma dag byggdes en [prototyp för mappade JIT-block](n64-gauntlet-mapped-jit-2026-09-26.md).
Den klarar riktade tillståndstester och exakt Gauntlet-replay, men två iterationer
blev långsammare än ordinarie väg. Den sparas opt-in och är avstängd som standard.
Prototypen kontrollerar aktuell översättning och ASID vid varje inträde i stället
för att kasta kod vid varje orelaterad TLB-generationsändring. Nästa iteration
ska utgå från blockprofilen och kostnaden per faktiskt utförd instruktion.

Nästa iteration finns i [mappade läsningar och CACHE-loopar](n64-gauntlet-mapped-loads-2026-09-26.md).
Brett stöd för mappade läsningar och CACHE gav ingen stabil vinst i längre test.
En smalare variant som bara startar mappade block vid CACHE gav +3,21 procent
i ett fyrkörningstest över gästsekund 5–15, med exakta tillstånd och bildkontroller.
Den är fortfarande opt-in; rapporten innehåller startkommando och begränsningar.
Nästa steg är fler sekvenser och profilering med det smalare filtret aktivt.

Fortsättningen finns i [instruktionssidans översättningscache](n64-gauntlet-instruction-page-cache-2026-09-26.md).
Profilen med CACHE-filtret aktivt pekade fortsatt på adressöversättning.
En första trådlokal cache gav ingen vinst över åtta körningar. Maskinkoden
visade kvarvarande trådlokal uppslagning; en andra, CPU-ägd variant minskar den
kostnaden och gav +7,47 procent i ett nytt ABBA-test med exakta kontrollresultat.
På användarens begäran är CPU-ägd sidcache (läge 2) och CACHE-filtrerad mappad
JIT nu standard. Bred mappad JIT, mappade läsningar och de långsammare RSP-
experimenten förblir avstängda. Nästa steg är fler sekvenser och ett annat spel med mappad programkod,
följt av ny profilering innan bredare JIT-inträden aktiveras.

## 1. Vad vi faktiskt ska uppnå

**100 procent betyder att en sekund emulerad tid tar högst en verklig sekund.**
Det betyder inte 60 nya spelbilder per sekund. Ett spel som producerar 20 eller
30 bilder per sekund på konsolen ska behålla den rytmen. PAL och NTSC måste
använda sina respektive tidmodeller. UI:ts pollingfrekvens är inte spelhastighet.

Vi ska först nå målet i avgränsade, reproducerbara sekvenser och därefter
utvidga täckningen. Ett lyckat Mario-test är inte ett löfte om hela N64-biblioteket.

Föreslagna leveranskrav för varje godkänd testsekvens:

1. Oförändrade emulerade klockor, instruktioner, DMA-data, avbrott och bildarbete.
2. Minst 100 procent uthållig hastighet i en obromsad körning av hela kärnan.
   Arbetsmålet är minst 105 procent för att lämna marginal åt desktop och variation.
3. I en tio minuter lång desktop-körning följer emulerad tid väggtid utan en
   växande eftersläpning. Efter uppvärmning ska medelhastigheten ligga inom
   99–101 procent när realtidsbegränsningen är aktiv.
4. Fördelningen ska redovisas: median, p95/p99 behandlingstid per VI-fält och
   sämsta ensekundsintervall. Ett föreslaget delkrav är minst 95 procent i
   95 procent av ensekundsintervallen; medelvärdet får inte dölja långa stopp.
5. Inga nya ljudunderruns under testet efter uppvärmning, ingen växande ljudkö,
   inga försvinnande objekt, saknade filmer eller fel vid save/load.
6. Korrekta resultat i både en kall start och återupptagning från en sparning.

Punkt 3–5 kräver bättre mätning i desktop än vi har dokumenterat i dag. Agenten
ska implementera och kontrollera den mätningen innan den sätter en grön bock.
Vi accepterar inte sänkta gästklockor, överhoppad emulering eller sämre grafik
som ett sätt att uppfylla ovanstående mål.

## 2. Utgångsläget och vad siffrorna betyder

De senaste rapporterna är en grund för prioritering. De är **historiska
mätningar med olika referensbyggen**, inte en ny gemensam mätning av den slutliga
leveransen. RE2:s senare ändringar i SP/DP- och PAL-timing gör en ny baslinje
nödvändig innan nästa prestandakampanj.

| Arbetslast | Befintlig evidens | Praktisk slutsats |
| --- | --- | --- |
| Mario, gång/simning, gästsekund 70–90 | Selektiv GPU-readback gav 72,25 → 75,37 procent i sin jämförelse | Ungefär en tredjedel mer genomströmning återstod i just den jämförelsen |
| Mario, CPU-klassificering | +2,28 procent i två jämförelseserier | Dispatcharbete spelar roll; små vinster ska byggas vidare på |
| Mario, registerexperiment | Senaste fulla registerläget gav −2,78 procent i sin serie | Behåll experimentet avstängt som standard och undersök övergångskostnaden |
| Mega Man, senare slot 1 | Tunga gästsekunder 5–15 låg runt 26–33 procent | Detta behöver en större arkitekturvinst, inte bara samma trimning som introt |
| Mega Man, byte stores | Positiva medelvärden i två serier, men stor spridning | Använd en ny parvis baslinje; lova inte en viss vinst i varje körning |
| RE2, start och film | Starten krävde två timingfixar; slot 2 hade rörliga bilder i RAM men gammal GPU-snapshot | En korrekt presentationskedja är ett grundkrav innan fps räknas |

Om en sekvens går i 75 procent behövs ungefär **1,33 gånger genomströmningen**,
vilket motsvarar 25 procent lägre väggtid för samma arbete. Vid 30 procent behövs
ungefär **3,33 gånger genomströmningen**, eller 70 procent lägre väggtid. Den
skillnaden ska styra hur stora ändringar vi överväger.

CPU-trådens tidigare profiler visar återkommande stor kostnad i
`TryAdvanceCpuBlock`, `ExecuteCpuBlockInstruction`, RSP-slicehantering,
progresskontroller och blockavslut. En äldre Mario-profil placerade cirka
36,6 procent av CPU-samplen i RSP-metoder och genererad RSP-kod tillsammans.
De genererade CPU-metoderna stod i en senare profil för cirka 1,45 procent,
medan CPU-blockdispatch låg runt 14 procent. Mega Mans senare scen visar
åter stor dispatchkostnad, men en annan arbetsfördelning.

Detta är exklusiva **CPU-sampleandelar**, inte en väggtidsbudget. De får inte
adderas till inkluderande GPU-timers eller omvandlas direkt till utlovade
procent. Steg B0 nedan ska skilja aktiv CPU-tid, GPU-arbete, väntan, kopiering
och UI/ljud åt. För en uppmätt väggtidsandel `p` och lokal acceleration `s` är
den idealiserade totalvinsten `1 / ((1 - p) + p / s)`. Använd den för att
sålla bort idéer vars maximala effekt är för liten.

Underlag:

- [Mario: dispatch](n64-mario-dispatch-speed-2026-09-23.md)
- [Mario: selektiv readback](n64-mario-live-readback-speed-2026-09-23.md)
- [Mario: registerexperiment och iteration](n64-mario-jit-registers-2026-09-23.md)
- [Fem tidigare experiment utan stabil vinst](n64-mario-optimization-pass-2026-09-23.md)
- [Mega Man: den senare sparningen](n64-megaman-later-speed-2026-09-23.md)
- [RE2: start och PAL-timing](n64-re2-startup-2026-09-23.md)
- [GPU-arkitekturens tidigare plan](n64-gpu-backend-plan-2026-09-21.md)

## 3. Laget: en integratör och högst tre samtidiga arbetare

Vi har fyra samtidiga agentplatser i den aktuella miljön. Planen ska fungera
inom det taket. Rollerna nedan är en arbetskö, inte sex samtidiga processer.
En subagent använder också en plats. En agent får därför inte skapa ett eget
obegränsat underlag av ytterligare agenter.

| Roll | Ansvar | Primärt kodområde | Leverans |
| --- | --- | --- | --- |
| Integratör | Prioritering, baslinje, gemensamma gränssnitt, sammanslagning, slutlig mätning | `Memory.cs`, `Program.cs`, leveransskript och gemensamma format | Validerad checkpoint och uppdaterad resultatmatris |
| CPU-agent | Dispatch, genererade regioner, registerplacering, sidoavslut | `Interpreter/R4300.Blocks.cs`, `R4300.Jit.cs`, `R4300.JitRegisters.cs` | Ett avgränsat experiment med full tillståndsjämförelse |
| RSP-agent | Slice/blockkostnad, SIMD och vektorsekvenser | `RspInterpreter.cs`, `RspBlockJit.cs`, `RspVectorSimd.cs` | Generell mikrocode-oberoende förbättring och differentialprov |
| GPU-agent | CPU/GPU-ägarskap, batchformat, readback och scanout | `N64LiveGpu.cs`, `N64GpuBackend.cs`, `native/N64Gpu/` | Oförändrad kommandosemantik med mindre kopiering eller väntan |
| Korrekthetsagent | Oberoende regressioner, nya sparningar, timing/film/input | Nya separat namngivna kontroller under `tools/N64Probe/` | Reproducerare, oracle och felrapport; behöver inte ändra kärnan |
| Mätagent | Reproducerbar mätning och desktop-beteende | Benchmarkskript och separata artefakter | Parvisa resultat med spridning och komplett provenance |

Första arbetsvågen är integratör + CPU + RSP + GPU. Integratören bygger
mätbasen. Vid en viktig integration byts en ledig specialistplats till en
korrekthetsagent som granskar ändringen oberoende. Under tidtagning får andra
agenter läsa kod eller skriva dokument, men inte köra emulatorer, byggen,
profiler eller GPU-tester på samma värd.

### Hur subagenter ska användas

Subagenter ska få konkreta, separata frågor. Exempel:

- CPU-agenten lämnar en avgränsad granskning av BD/EPC, delay slots och partial
  exits till en korrekthetsagent medan den själv granskar genererad maskinkod.
- RSP-agenten lämnar framtagning av vektorreferensfall till en subagent medan
  den implementerar en enda vald fusion.
- GPU-agenten lämnar inventering av alla RAM-skrivvägar till en granskare
  medan den undersöker batchformatet.

Detta sker endast när integratören har reserverat en ledig plats. Om alla
platser är upptagna köas deluppgiften eller görs lokalt. Två agenter ska inte
ha samma diff, testkörning eller mätserie som uppgift.

### Isolering och ägarskap

1. Commit av den accepterade baslinjen först. Frys dess managed- och native-
   binärer, byggflaggor och SHA-256. Ett gammalt `HEAD` är inte en korrekt
   referens om det finns accepterade lokala ändringar ovanpå.
2. Varje kodexperiment använder en egen git-worktree från samma baslinje och
   egna utdata. Worktree isolerar också projektens `obj`/`bin`; bara ett eget
   `-o` i samma checkout räcker inte för samtidiga konfigurationsbyggen.
3. Varje uppdrag anger exakt vilka filer agenten äger. `Memory.cs` och delade
   kommandoflaggor ändras av integratören, eller genom ett uttryckligt lånat
   filansvar. En agent levererar annars ett patchförslag för dessa filer.
4. Integratören tar in en kandidat i taget, granskar diffen och kör berörda
   kontroller. Kombinationen mäts igen; individuella vinster får inte summeras.
5. Alla sparningar kopieras före användning. ROM, originalslots och lokala
   benchmarkartefakter ska inte läggas i Git. Endast integratören gör slutlig
   commit/push enligt användarens aktuella uppdrag.

## 4. B0: bygg en gemensam mätbas innan större ingrepp

**Leverans:** ett maskinläsbart benchmarkmanifest, frysta referenser, en
spelmatris och en rapport över var väggtiden faktiskt försvinner.

Manifestet ska för varje arbetslast innehålla ROM-hash, kopierad slot-hash,
core-state-hash, PAL/NTSC, startpunkt, inputsekvens, slutvillkor, binärhashar,
commit, diffstatus, runtime, CPU/GPU/driver, CPU-affinitet, antal GPU-workers
och samtliga experimentflaggor. Lokal sökväg får finnas i en lokal konfiguration;
reproducerbarheten i Git ska inte bero på att alla har `/home/nichlas/roms`.

| Prioritet | Sekvens | Varför den måste vara med |
| --- | --- | --- |
| P0 | Mario 90-sekunders boot + gång/simning, särskilt 70–90 | Etablerad deterministisk jämförelse och första realtidsmålet |
| P0 | Mega Man, nya slot 1, separata fönster 5–15 och 15–30 | Avslöjar om en förbättring bara hjälper lätt introarbete |
| P0 | RE2 slot 2-film, bildsekvens och övergång tillbaka till spel | CPU/RSP-skrivna framebufferbilder och ljud/video-timing |
| P1 | Castlevania slots 2 och 3 | Texturbelastning och andra RSP-sekvenser |
| P1 | Rampage nuvarande slot 1 | Historiska blinkningar och bildfrysning |
| P1 | Duke slots 1–3 | Spelbar scen, vapensprite/alpha och djupordning |
| P1 | Gauntlet slot 1 + boot | PAL, eventväntan och annan mikrocode |
| P1 | Perfect Dark sparad scen | FPU, minnesåtkomst och hög komplexitet |

Inventera de faktiska filerna först. Ett slotnummer är inte en versionsidentitet:
användaren kan ha skrivit över det. Saknas en fil ska den raden markeras som
otestad och nästa självständiga uppgift fortsätta.

### Mätprotokoll

- Använd samma arbete: input vid verkliga Joybus-läsningar, slut vid bestämda
  gästcykler/inputhändelser och jämförelse av ljud, bilder och tillstånd.
- Mät både `gästcykler / 93 750 000 / väggtid` och ljudets producerade tid när
  den är meningsfull. Ljud kan saknas under menyer/filmsekvenser; då är
  ljudduration ensam ingen giltig hastighetsmätare. Redovisa VI-fält separat.
- Börja med ABBA-screening. Små eller varierande vinster behöver en oberoende
  serie, helst med omvänd startordning, och fler prover tills slutsatsen är
  tydlig. Redovisa alla prover, spridning och osäkerhet; välj inte bästa körningen.
- Testa varm och kall JIT separat. Låt inte första shaderkompilering eller
  olika cachevärme bli en påstådd CPU-vinst.
- Kör tidtagning ensam på värden med samma affinitet och konfiguration. Kontrollera
  bakgrundslast, frekvens och temperatur. En annan agent med andra CPU-kärnor
  kan ändå störa minnesbandbredd, cache, temperatur eller GPU.
- Profilering/audits och korrekthetsprov körs i separata serier. Logga inkluderande
  och exklusiva tider tydligt så att barnfasen inte räknas dubbelt.
- Mät sedan den riktiga desktop-appen med dess bildpublicering och ljudutmatning.
  En headless-vinst ska inte benämnas desktop-realtid förrän den är verifierad där.

Verktyg som redan finns:

```sh
dotnet build tools/N64Probe/N64Probe.csproj -c Release -m:1 \
  -p:N64LiveGpu=true -p:N64PerformanceProbe=true \
  -o .build-tmp/n64-realtime-baseline/probe

# Ange faktiska lokala filer. STATE är extraherad core-state, inte EUTHSTAT-behållaren.
dotnet "$PROBE" --bench-state "$ROM" "$STATE" "$OUTPUT" 30
dotnet "$PROBE" --bench-sm64 "$MARIO_ROM" "$OUTPUT" 90

python3 tools/N64Probe/bench-state.py \
  --reference "$REFERENCE_PROBE" --candidate "$CANDIDATE_PROBE" \
  --rom "$ROM" --state "$STATE" --output "$OUTPUT" \
  --reference-gpu-library "$REFERENCE_GPU" \
  --candidate-gpu-library "$CANDIDATE_GPU" --start 5 --end 30
```

GPU-körningar kräver också `EUTHERDRIVE_N64_GPU_LIBRARY`. Övriga miljöval,
inklusive registerexperimentets läge, ska sättas explicit och sparas. Det
nuvarande `bench-state.py` använder ljudduration för sitt procenttal och stoppar
vid controllerläsningar. Det behöver utökas för filmer utan sådan regelbunden
input och för en oberoende cykelbaserad hastighet; fram till dess ska film inte
pressas in i det protokollet och kallas färdigmätt.

## 5. CPU-spåret: gör färre dyra övergångar

### C1. Mät och minska dubblerat arbete i blockdispatch

Utgå från `TryAdvanceCpuBlock` och `TryRunCpuJit`. Registrera i ett separat
diagnostikbygge antal försök, lyckade körningar, instruktioner per anrop,
orsaker till avslag/sidoavslut samt kostnad för fetch, klassificering, guard,
historik och återgång. Spara även genererad värdkod för representativa block.

Första kandidaten ska återanvända redan verifierad information vid övergången
till ett block, där samma opcode eller klassificering annars hämtas igen.
Det räcker inte att två läsningar normalt har samma värde: verifiera CPU-store,
GPU-readback, DMA, kodalias och TLB-ändring mellan observationerna.

Leverans: en avgränsad dispatchändring, antalet borttagna kontroller i den
aktuella arbetslasten, exakt differentialprov och isolerad gameplaymätning.
Ingen ny större JIT-cache som ersättning för denna analys.

### C2. Direkt övergång mellan redan kompilerade block

Om C1 bekräftar stor kostnad vid blockgränsen: bygg en experimentell bounded
runner som kan följa en verifierad efterföljare utan att gå hela varvet genom
den generella dispatchern. Börja med direkta grenar mellan två block och
vanlig publicering av register vid varje blockslut.

Kontraktet måste vara uttryckligt:

- Efterföljaren identifieras av rätt virtuell/översatt kodidentitet och levande
  kodvalidering; en adress ensam räcker inte.
- Budgeten minskas korrekt och får inte passera närmaste enhetshändelse,
  avbrott, COUNT/COMPARE eller nödvändig instruktionsgräns.
- Självmodifiering, GPU-skriven kod, reset, TLB-ändring och savestate-load
  bryter länken. En bakgrundskompilering får inte återpublicera en gammal länk.
- Alla avslag och partial exits återlämnar samma PC, delay-slot-status,
  BD/EPC, register, cykler och historik som den ordinarie vägen.

Undvik att börja med rekursiva delegatanrop som riskerar stacktillväxt.
Instrumentera länkarnas träffgrad, kostnad och antal instruktioner per
övergång. Högre täckning är ett hjälpmedel, inte ett godkännandekriterium.

### C3. Koppla registeridén till större nyttigt arbete

Behåll `EUTHERDRIVE_N64_CPU_JIT_REGISTERS=0` som standard tills nya jämförelser
visar en bred vinst. Den befintliga livstidsanalysen, r0-fallen och epilogerna
är en användbar bas som inte ska kastas bort.

Mät fyra varianter från samma binär där det går: vanlig dispatch, enbart
länkning/region, enbart registerplacering och kombinationen. Då ser vi om
registerkostnaden verkligen amorteras och vilken del som bidrar.

Först när C2 fungerar utvidgas registerlivslängden över en blockgräns. Varje
helper eller händelse som kan observera tillstånd måste se publicerade värden.
Undantag och misslyckade minneskontroller behöver precisa sidoavslut. Återanvänd
testerna för destinationer skrivna före/efter guard, r0-normalisering, JALR-
alias och failure på en senare loopiteration. Privata register får aldrig
läcka över save/load eller reset.

### C4. Bättre minnesåtkomst och möjlig större backend

Undersök direkta RAM-load/store-funktioner först när profilen visar att de
dominerar kvarvarande tid. Batcha adresskontroller endast om hela spannet
bevisligen är samma RAM-region, inga sidoeffekter eller faror passeras och
varje nödvändig GPU-ägarskapsnotifiering bevaras. TLB, MMIO, endianordning,
alignment, unaligned stores och LL/SC behöver egna fall.

Om managed IL fortfarande producerar dyr kod efter C1–C3 ska CPU-agenten
skriva ett separat beslutsunderlag för en native JIT. Prototypen ska först
täcka en liten generell instruktionstyp, återanvända differentialsviten och
mäta både anropskostnad och faktisk gameplay. Varken omskrivning till C++
eller en ny IR är i sig en hastighetsvinst. Utvidga först om prototypen visar
att den angriper den uppmätta flaskhalsen bättre än IL-spåret.

## 6. RSP-spåret: arbeta längre mellan bokföringspunkterna

### R1. Skilj exekvering från slice- och progresskostnad

Profilera `ExecuteSlice`, `EndBlock`, kodvalidering och både RSP:ns och
Memory:s progresskontroller i Mario och Mega Mans tunga scen. Räkna hur
ofta de utför samma arbete för identiskt mikrocode och oförändrad status.

Första experimentet ska minska dubblerat arbete kring **en redan giltig
slice**, utan att ändra vilka instruktioner som körs eller när ett producer-
wait upptäcks. En giltighetstoken får endast återanvändas inom tydligt
avgränsad IMEM/DMEM/DMA-livstid. Den måste brytas när CPU:n skriver, DMA
byter kod, en task yieldar eller nytt tillstånd laddas.

Vi har redan provat inkrementell progresshash och flyttad validering utan
stabil gameplayvinst. Gör inte om samma variant. Ett nytt förslag måste visa
vilken ytterligare kostnad som försvinner och varför tidigare nackdel försvinner.

### R2. Fusera vanliga generella vektorsekvenser

Samla frekvens och värdkostnad för instruktionspar/kedjor. Välj en kedja som
finns i flera mikrocode-varianter och håller intermediate accumulator/register
lokalt längre. Fokus är färre metodanrop, arrayaccesser och ompackningar;
SIMD ska användas där den ger rätt värden och faktiskt bättre maskinkod.

Kontrollera 48-bitars accumulator, signedness, saturation/rounding, lane-
selektion, VCO/VCC/VCE, alias mellan in/ut, varje tillåten slicegräns och
DMA runt sekvensen. Om ett avbrott eller synligt sidoavslut kan ske mitt i
kedjan måste det materialiserade delresultatet vara exakt.

Godkänn generella opcode-mönster och mikrocodevalidering. ROM-namn eller
hårdkodade speladresser är inte en lösning. Kör både syntetiska gränsvärden
och de riktiga gameplaysekvenserna innan nästa kedja väljs.

### R3. Större steg först efter R1/R2

En ny RSP-regionrunner kan minska antalet returresor, men måste följa samma
event- och DMA-kontrakt som CPU:n. Flytta inte RSP till en parallell värdtråd
som första åtgärd: CPU/RSP delar minne och observerbara register. Först måste
en exakt producer/consumer-gräns, immutable indata och resultatpublicering
vara bevisade. Mät också om synkroniseringen kostar mer än den sparar.

## 7. GPU/minnesspåret: behåll ordningen och flytta mindre data

GPU-renderaren finns redan. Huvudfrågan är nu arbetet mellan CPU, RSP, RAM,
RDP, readback och presentation, inte att börja om med en ny rasteriserare.

### G1. Billigare kommandobatcher

Mät `N64LiveGpu.Writes`, `Command`, `Synchronize`, `BinaryWriter` och
`MemoryStream`. Prova en kapacitetskontrollerad sammanhängande skrivbuffert
för samma binära records. CPU-bytes måste fångas vid samma tidpunkt och i
samma ordning mot nästa RDP-kommando. Kolla om närliggande CPU-patchar verkligen går
att slå ihop utan att passera ett observerande kommando.

Jämför först identiska recordströmmar och därefter full RAM/hidden/TMEM med
native-renderaren. Gränser, wrap, same-value stores och den befintliga
64-MiB-envelopegränsen får inte tappas bort. Acceptera utifrån hela kärnan,
inte enbart antal färre `Write`-anrop.

### G2. Hidden-memory/TMEM och de kvarvarande barriärerna

Selektiv RDRAM-readback är redan införd. Hidden memory och TMEM kopieras
fortfarande i sin helhet. Undersök om skrivmängder kan bevisas även där och
om CPU:n behöver materialisera dem vid varje synkronisering eller först vid
en faktisk observation/save. Bevisa först att nästa draw, framebuffer-read
och save får exakt samma värden och att flera samtidiga snapshots inte
förbrukar varandras metadata.

Granska sedan de barriärer som **faktiskt** återstår. En tidigare sparse-
patchvariant tog inte bort några Mario-barriärer och blev långsammare.
Inventera därför varje barriärs orsak och överlapp innan nya intervallträd
eller finare bitmaps införs.

### G3. Asynkron överlappning med explicit dataägarskap

Ett större steg är att låta GPU:n arbeta vidare mellan observationer medan
CPU:n fortsätter med data som inte överlappar. Dela då upp submit, completion
och materialisering med generationsnummer/timeline-token per relevant region.

Det kräver ett skrivet kontrakt för CPU read-after-GPU-write, CPU write-after-
GPU-read/write, texturladdningens tidpunkt, DMA, code fetch, FULL_SYNC/DP,
hidden bits och save/load. Ingen agent får bara ta bort `Synchronize()` och
förlita sig på att vanliga spel ser rätt ut. En konservativ fallback till
nuvarande synkrona väg måste finnas för okända eller överlappande fall.

Den senaste readback-rapporten visar mycket liten slutlig timeline-väntan
men större kopierings- och patchkostnader. G3 ska därför bara gå före G1/G2
om en ny väggtidsprofil visar att verklig överlappning har tillräcklig potential.

Ny RE2-slot-1-profil med direkt RDP-kommandoväg (2026-09-24): 989
`FULL_SYNC`-väntor tog cirka 2,8 s under tio gästsekunder. Alla väntande
CPU-patchar låg utanför de kända framebufferintervallen, men texturkällor
och okända GPU-läsningar är ännu inte bevisat oberoende. `FULL_SYNC` anropar
dessutom omedelbart readback och publicering före DP-avbrottet. Att bara
skjuta upp patchbarriären flyttar därför normalt väntan till readback; G3
måste först definiera när CPU:n får fortsätta, när DP-avbrottet blir synligt
och vilka minnesregioner som får förbli GPU-ägda. Även en perfekt eliminering
av dessa 2,8 s räcker inte ensam för RE2:s realtid i den uppmätta scenen.

Ett första, mindre överlappningssteg är nu provat: färdiga RDP-batchar kan
skickas till GPU:n medan CPU/RSP bygger nästa batch, men samma synkrona
readback och DP-publicering används fortfarande vid `FULL_SYNC`. Se
[mätningen av tidig RDP-submit](n64-gpu-overlap-experiment-2026-09-24.md).
Det gav stor vinst i Mega Mans senare scen men i princip ingen i RE2; därför
är läget än så länge opt-in. G3:s större minnesägarskapskontrakt återstår.

### G4. Presentation och filmer

GPU-renderade och CPU/RSP-skrivna bilder ska båda nå skärmen. RE2 slot 2
är ett obligatoriskt prov. UI-tråden ska läsa en publicerad immutable bild,
inte ta över emulatorns synkronisering eller läsa en buffert som skrivs om.
Ett RDRAM-readback betyder inte automatiskt att ett RDP-frame är klart.

Mät mängden kopiering/allokering från färdig bild till OpenGL/Avalonia.
Återanvänd buffertar eller överför en ägarskapsreferens först när livslängden
mellan producent, UI och save är tydlig. Presentationens generationsnummer
och den verkliga bildfrekvensen ska skiljas från UI-polling och från antalet
FULL_SYNC-kommandon. Förbättra inte räknaren utan att bilden verkligen följer med.

## 8. Regressioner är grindar, inte en rapport efteråt

Varje kandidat passerar dessa nivåer i ordning:

1. **Bygge och lokal semantik.** Berörda projekt/targets, differentialprov
   mot interpreter eller strikt GPU-väg och ett test som träffar ändringen.
2. **Fasta tillstånd.** CPU/RAM/enheter, instruktionshistorik, deadlines,
   reset och save/load. JIT-kompilationstal och väggtid är inte gästtillstånd.
3. **Verkliga sekvenser.** Bilder, ljud, input, cykler och tillstånd för
   primärspelet plus minst två andra spel som belastar samma mekanism.
4. **Isolerad prestanda.** Samma arbetsmängd och oberoende bekräftelse när
   den behövs. Inga parallella tunga jobb på mätvärden.
5. **Kombinerad integration.** Alla accepterade kandidater tillsammans,
   hela spelmatrisen och riktig desktop. Bygg leveransbinärer utan profiler.

Exempel på befintliga ingångar i `N64Probe`:

```sh
dotnet "$PROBE" --check-cpu-blocks
dotnet "$PROBE" --check-cpu-jit
dotnet "$PROBE" --check-cpu-jit-cache
dotnet "$PROBE" --check-rsp-scheduling
dotnet "$PROBE" --check-rdp-streaming
dotnet "$PROBE" --check-video
dotnet "$PROBE" --check-audio
dotnet "$PROBE" --check-controller-ports
dotnet "$PROBE" --check-gpu-readback "$GPU_LIBRARY"
dotnet "$PROBE" --check-gpu-textures-live "$GPU_LIBRARY"
dotnet "$PROBE" --check-live-gpu "$GPU_LIBRARY" "$JOURNAL" "$REFERENCE"
dotnet "$PROBE" --check-gpu-savestates "$GPU_LIBRARY" "$OUTPUT"
dotnet "$PROBE" --check-gpu-cpu-frames "$GPU_LIBRARY"
```

GPU-journal/save/CPU-framekontrollerna kräver ett separat kontrollbygge med
`N64LiveGpu=true` och `N64RdpJournalCapture=true`; starta Vulkan validation
och ägarskapsaudit för dessa körningar. Använd inte det bygget för tider.
Komplettera med de befintliga RSP-, byte-/word-, framebuffer-, idle-event-
och fixed-work-replaykontroller som respektive ändring berör.

**En korrekthetsfix kan avsiktligt ändra ett gammalt oracle.** Då bevaras det
gamla resultatet, första avvikelsen förklaras och en oberoende referens eller
riktad felreproducerare visar varför det nya beteendet är rätt. Byt aldrig
bara hash och skriv att regressionerna passerar. RE2:s SP/DP-fix är ett
konkret exempel på varför detta behövs.

## 9. Genomförande i vågor och beslutspunkter

| Våg | Parallellt arbete | Integratörens beslut innan nästa våg |
| --- | --- | --- |
| 0 | Frys checkpoint, slotinventering, B0 och RE2-filmregression | Ny gemensam baslinje; varje spelrad har tydlig status |
| 1 | C1 dispatch, R1 sliceprofil, G1 batchkostnad | Rangordna kandidater efter möjlig väggtidsvinst och beviskostnad |
| 2 | C2 länkad runner, R2 en vektorfusion, bästa G1/G2-kandidat | Ta in en i taget; mät även den kombinerade kärnan |
| 3 | C3 region + register, nästa RSP-kedja, återstående minneskostnad | Mario når uthållig realtid eller profilen visar tydligt nästa begränsning |
| 4 | Mega Man tung scen, Castlevania och kompatibilitetsmatris | Ingen generell 100-procentsclaim från enbart Mario |
| 5 | Desktop-pacing, kallstart, save/load, längre spelpass | Leveranskraven i avsnitt 1 är uppfyllda för namngivna sekvenser |

Detta är inte fem tidsbestämda pass. En våg får inte avslutas bara för att
agenten har förbrukat sin kontext. Varje deluppgift ska däremot vara liten nog
att ge ett begripligt resultat eller ett tydligt falsifierat antagande.

Om två väl avgränsade iterationer på samma idé inte visar någon vinst ska
integratören omprioritera efter en ny profil. Behåll en experimentgren när
den har fortsatt värde, men lägg inte allt bakom fler standardflaggor i
produktionsvägen. En reproducerbar regression stoppar integrationen direkt.
Tillfällig mätstörning leder till en ny serie, inte en godtycklig slutsats.

## 10. Uppdragsmall för en agent

```text
Uppgift: [en konkret hypotes, exempelvis C1]
Baslinje: [commit + binärmanifest + exakt diffstatus]
Ägda filer: [lista]; delade filer kräver integratörens samordning.
Primär sekvens: [hashidentifierad ROM/state och gästintervall]
Kontrollsekvenser: [minst två relevanta alternativ]
Förväntad borttagen kostnad: [mätning; inte gissad totalprocent]
Semantiskt kontrakt: [register/minne/events/ägarskap som måste bevaras]
Tillåtna deluppgifter: [granskning/fixture/etc.; endast reserverade agentplatser]
Leverera: diff/commit, byggkommando, regressioner, råmätningar,
          genererad kod/profil vid behov och rekommendation behåll/avvisa.
Stoppa integration vid: oförklarad state/frame/audio-skillnad.
Kör inte tidtagning förrän integratören reserverat hela mätvärden.
```

För första CPU-uppdraget: lokalisera dubblerad fetch/klassificering i övergången
`TryAdvanceCpuBlock` → `TryRunCpuJit`, föreslå en borttagen kontroll med
oförändrad validering och leverera ett litet differentialtest för invalidation.

För första RSP-uppdraget: jämför två riktiga scener och skilj exekvering från
`ExecuteSlice`/`EndBlock`/progresskostnad; föreslå en ändring som inte upprepar
den tidigare inkrementella hashvarianten.

För första GPU-uppdraget: mät batchserialiseringens väggtidsandel och verifiera
om identiska recordbytes kan produceras billigare. Leverera en tydlig
break-even-bedömning innan native ägarskapsmodellen byggs om.

För korrekthetsagenten: angrip kandidatens svagaste antagande med ett fall
som inte skrivits av implementeraren. För C2 är det exempelvis kodändring
mellan länkade block; för G3 en CPU-store mellan två GPU-läsningar och save.

## 11. Dokumentation och överlämning efter varje checkpoint

Varje accepterad eller avvisad kandidat får en kort `.md` med hypotes,
utgångsläge, exakt ändring, tester, alla jämförelsetider, begränsningar och
nästa konkreta steg. Artefaktmanifestet följer samma namn. Rapporterna ska
kunna läsas av nästa agent efter en reset utan att den måste gissa vad som
redan har provats.

Den löpande resultattabellen ska minst ha:

| Fält | Innehåll |
| --- | --- |
| Identitet | Kandidat, baslinje, commits, konfiguration |
| Korrekthet | Exakt/avsiktlig skillnad/avvisad; första relevanta avvikelse |
| Prestanda | Alla tider, medel/median/spridning, parvis vinst, gästintervall |
| Omfattning | Vilka spel och scener som faktiskt körts |
| Leverans | Default/opt-in/experiment borttaget; rätt desktop-binär byggd |
| Nästa arbete | En konkret fråga med fil och reproducerare |

Den närmaste starten är alltså: avsluta RE2-filmfixen och checkpointen,
återmät Mario och Mega Man med samma slutliga kärna, och starta därefter
C1/R1/G1 som tre tydligt separerade agentuppdrag. Vi jagar först de stora
återkommande kostnaderna och låter verifierade resultat bestämma nästa steg.
