# Gauntlet Dark Legacy: plan mot spelbar prestanda

Datum: 2026-08-11

## Mål

Huvudmålet är minst 30 verkliga gäst-swaps per sekund under styrbart spel,
med stabilt ljud och låg inputfördröjning. Bild, CPU-state, FIFO-räknare och
draw/swap-räknare ska fortsätta matcha ett tolkorakel bitexakt.

Den nuvarande f6750-basen ger omkring 37 swaps på 12 sekunder vid 90 000
gäststeg per värdanrop, alltså ungefär 3 swaps/s. Vägen till 30 swaps/s kräver
därför ungefär tio gånger högre effektiv gästgenomströmning; isolerade
adresspecifika 5-procentsvinster räcker inte som slutarkitektur.

## Mättrappa

| Nivå | Gäst-swaps/s | Tolkning |
|---|---:|---|
| Bas | cirka 3 | styrbart men inte spelbart |
| Delmål 1 | 6–8 | tydlig accelerering |
| Delmål 2 | 15 | praktiskt spelbart med bildhoppning |
| Huvudmål | 30 | spelbar full uppdatering |
| Slutmål | spelets native-takt | marginal för ljud och UI |

Varje prestandacheckpoint mäts från en versionsbunden warm snapshot med minst
ett kort 300-anropsprov och ett långt 1 200-anropsprov. A/B måste behålla exakt
frame-hash, slut-PC, FIFO, draw-paket och swaps.

## Fas 1: tierad CPU-JIT

1. Behåll tolken för kall och ovanlig kod.
2. Använd safe batches som varm mellannivå.
3. Ersätt den nu avstängda expression-backenden med en billigare block-ABI och
   kodgenerator innan generell kompilering aktiveras igen.
4. Länka redan kompilerade efterföljare utan återgång till `Step()`.
5. Gör branch och delay slot till explicita JIT-terminatorer med side exits.
6. Höj opcode-täckningen utifrån verklig fallback-profil, inte syntetiska test.
7. Ersätt adresspecifika traces med kodsignaturbaserade traceobjekt där samma
   guards och terminator kan återanvändas.

Main-RAM-accesser får direkta guarded vägar. MMIO, interruptkänsliga adresser
och kodsidor använder helpers eller side exits. Exekverbara RAM-sidor får
versionsräknare så att kodcache kan invalideras billigt och säkert.

Första täckningsmålet är 25 procent av kostsamma gästinstruktioner i JIT,
sedan 50–70 procent. Telemetrin ska skilja kompilerade block, länkade hopp,
side exits, guard-missar och invalideringar.

## Fas 2: Voodoo-raster

Rasteriseringen står i den aktuella profilen för ungefär en tredjedel av
runtime. När CPU-JIT har bred täckning ersätts `Parallel.For` per triangel med
en beständig worker-pool och tilejobb. Vanliga kombinationer av texture,
depth, fog och blend får specialiserade bitexakta kernels. SIMD och senare en
valfri compute-backend införs endast bakom samma framebuffer-orakel.

## Fas 3: pacing och bildhoppning

Emulering, raster och presentation separeras med ordnade kommandon och tydliga
fences vid MMIO-läsning, FIFO-status och buffer swap. När värden ligger efter
får färdiga mellanbilder hoppas över, men alla registereffekter och swaps ska
fortfarande exekveras. Input läses varje värduppdatering och ljudbufferten får
egen underrun/overrun-telemetri.

## Beslutsregler

- En optimering som ändrar oraklet förkastas eller hålls avstängd.
- En mikrooptimering utan upprepad mätbar vinst tas bort.
- Handskrivna traces används som bro och referensfall, inte som permanent
  ersättning för en generell JIT.
- Varje godkänd del levereras som en separat commit och pushas innan nästa
  riskklass påbörjas.

## Närmast

Ett första chaining-försök visade att den nuvarande debt-baserade dispatchen
redan amorterar blockreturer effektivt. Endast en fyrinstruktionssuccessor gick
att länka, och det långa A/B-provet blev cirka 0,6 procent långsammare. Försöket
förkastades i stället för att lämnas som passiv komplexitet.

Den första godkända arkitekturella vinsten är direkt exekvering av vanliga
`beq/bne/blez/bgtz` plus delay slot utan återgång till den stora opcode-
dispatchern. Den vägen täcker cirka fem miljoner branchpar över 1 200 anrop och
gav 4,3 procent lägre runtime med exakt orakel.

Närmast profileras återstående branchterminatorer och store-side-exits. Målet
är att låta kompilerade block äga sin terminator och endast lämna JIT när en
service, MMIO-effekt, kodinvalidering eller ovanlig branch kräver det.

Ett direkt parlager för `jal/jr/jalr` prövades därefter. Det var bitexakt över
2 895 270 par men cirka 0,2 procent långsammare i interfolierade långprov;
vanlig `j` visade dessutom att pending-branch-state observeras före vissa
delay slots. Lagret är därför helt borttaget. Nästa implementation ska inte
lägga ännu en kontrollväg bredvid dispatchern, utan låta kompilerade block
inkludera terminator och delay slot med side exit före observerbara helpers.

Rasterfasens första bokföringsexperiment ersatte atomiska radräknare med en
poolad statistikbuffert. Det var bitexakt men 1,7 procent långsammare i det
långa enskilda A/B-paret (13 240,5 mot 13 467,9 ms) och är borttaget. Det
bekräftar att fokus ska ligga på specialiserade kernels för vanliga
texture/depth/fog/blend-state, där antalet villkor och helpers per pixel kan
minskas materiellt.

En ny opt-in-profiler visar nu att de två största raster-signaturerna delar
`fbz=0x000b4779`, color path `0x0c60743a`, alpha `0x00045119` och fog
`0x000000c1`; endast texture mode skiljer (`0x8c22490f`/`0x8c22410f`). De står
tillsammans för 7 182 227 bounding-pixlar i 300-anropsprovet. Det blir den
första specialkernel-familjen, med gemensam depth/fog/alpha/color-kropp och
två texture-varianter bakom exakt framebuffer-orakel.

Den första kernel-familjen är nu implementerad och godkänd. Över två långa
interfolierade A/B-par sjönk medeltiden från 11 947,5 till 11 775,5 ms, cirka
1,4 procent, samtidigt som 6 109 721 pixlar gick genom specialvägen och hela
oraklet förblev exakt. Nästa rastersteg ska använda samma profiler för den
tredje och fjärde största state-familjen, men endast om en gemensam kernel kan
täcka dem utan att duplicera hela rasterloopen.

Det sista villkoret visade sig avgörande: inline-specialisering av profilerens
tredje och fjärde familj var bitexakt men 2,9 respektive 3,6 procent långsammare
i långa interfolierade prov. Försöken är borttagna. Rasterfasens nästa
arkitektursteg ändras därför till separata kernel-loopar/delegater som väljs
en gång per triangel; den redan godkända första kerneln lämnas orörd tills den
kan flyttas till den strukturen utan oracle- eller prestandaförlust.

Den separata strukturen är nu införd utan källduplicering genom tre generiska
JIT-instanser av rasterraden. Kerneltypen väljs en gång per triangel och gör
state-valet konstant i innerloopen. Sex långa A/B-par gav 12 600,3 mot
12 415,3 ms i medel, cirka 1,5 procent vinst, med positiv median och exakt
orakel. Nästa state-kernel kan nu läggas som en ny typinstans i stället för som
ytterligare villkor i den befintliga dynamiska pixelkroppen.

Den fjärde profilerade state-familjen är nu den första utbyggnaden av den
strukturen. Som separat typad iterated-color-kernel behandlar den 2 702 827
pixlar i långprovet utan att belasta andra states med en dynamisk innerloop-
gren. Sex långa A/B-par gav 11 610,5 mot 11 417,4 ms i medel, 1,66 procent
vinst och fem vunna par av sex, med exakt orakel i samtliga körningar. Nästa
rastersteg ska profileras på nytt från denna kombinerade bas; endast en state-
familj med stor faktisk pixelkostnad och en tydligt förenklad typinstans bör
läggas till.

Två efterföljande små rasterutvidgningar bekräftade den gränsen. En separat
fogfri additiv typinstans nådde bara 181 016 behandlade pixlar och gav neutral
medeltid med negativ median. Att återanvända common-typen för texture mode
`0x8c22498f` nådde 140 849 extra pixlar men gav cirka 0,7 procent regression.
Båda är borttagna. Bounding-pixlar räcker därför inte längre som urvalsmått;
nästa profilering ska räkna behandlade pixlar per fullständig state-signatur,
och en ny kernel bör inte byggas innan den mätningen visar minst cirka en
miljon relevanta pixlar i långprovet.

Profilern räknar nu faktiskt rasterbehandlade pixlar per fullständig signatur
utan någon ny innerloop-gren. Den bekräftar 6 109 721 pixlar för common-
familjen och 2 702 827 för iterated-familjen. Efter den redan neutrala
texture/color0-familjen på 1 724 167 är nästa nya grupperingskandidat tre
`cp=0x0c482435`-states som tillsammans står för 1 429 132 pixlar. Nästa
kernelprov ska dela deras color/fog/alpha-väg över texture modes i en enda
typinstans; om den inte ger stabil långvinst flyttas fokus tillbaka till bred
CPU-JIT-täckning i stället för mindre rasterstates.

Det grupperade `cp=0x0c482435`-provet var bitexakt men gav 12 339,1 mot
12 433,7 ms över tre långpar, cirka 0,77 procent regression och bara ett
vunnet par. Kerneln är borttagen. Rasterfasen har därmed uttömt alla nu kända
oprövade familjer över en miljon behandlade pixlar. Nästa aktiva arbete är åter
fas 1: profilera kompilerade blockterminatorer, fallback-opcodes och side exits
från den nuvarande basen och välj den bredaste verkliga exekveringskostnaden.

En ny opt-in safe-block-profiler kör med normal batch/JIT aktiv och rangordnar
block efter verkliga entryn gånger blocklängd. Långprovet visar 57 453 127
safe-block-instruktioner men bara 4 615 076 kompilerade instruktioner. Flera
heta block stoppas av `mtc1`; expression-backenden kunde redan generera den
operationen, så filtret öppnades bakom en A/B-flagga. Det ökade kortprovets
kompilerade täckning från 1 094 020 till 1 636 173 instruktioner men försämrade
medeltiden från cirka 4 695 till 5 114 ms, nästan 9 procent. Försöket är helt
borttaget. Nästa beslutspunkt är därför om dagens expression-block alls slår
safe batches på den nuvarande kombinerade basen; därefter ska eventuell ny JIT
fokusera på en billigare kodgenerator/ABI, inte bara fler accepterade opcodes.

En ordningsbalanserad långkontroll visar nu att både delarna och hela dagens
expression-lager är regressiva på den kombinerade basen. Guarded traces var
cirka 9,2 procent långsammare än samma generella JIT utan traces. De tolv
generella blocken var i sin tur cirka 3,3 procent långsammare än rena safe
batches. Den direkta `på/av/av/på`-slutkontrollen gav:

```text
expression-block på, medel   12058,6 ms
expression-block av, medel   11665,2 ms
vinst utan lagret                3,26 %
```

Båda av-körningarna slog båda på-körningarna och samtliga behöll exakt
`frameHash=0xe87b12da`. Warm-probe och desktop har därför compiled blocks av
som standard. Implementation och experimentflaggor finns kvar opt-in som
referens för nästa backend, men nästa JIT får inte använda samma dyra
expression-ABI med bred registerinläsning/utskrivning per block.

Tre billigare basoptimeringar prövades därefter och togs bort:

- Att skjuta upp PC-, CP0- och instruktionsbokföring till batchslutet ändrade
  först timer/FIFO-oraklet. Med exakt observerbar ordning återställd täckte en
  variant 31 528 234 instruktioner men blev 1,13 procent långsammare i tre
  långpar och vann inget par.
- Att skriva MIPS-register noll en gång per block för bevisat säkra block
  täckte 57 293 795 instruktioner. Tre kortpar såg cirka 5 procent bättre ut,
  men sex ordningsbalanserade långpar gav 11 687,4 mot 11 706,3 ms, neutral
  med tre vinster av sex. Försöket är borttaget.
- Seriell raster vann kortprov, men förlorade alla tre långpar med cirka 4
  procent. En sänkt `Parallel.For`-tröskel från 8 192 till 2 048 bounding-
  pixlar var efter sex balanserade långpar helt neutral, 11 566,9 mot
  11 562,1 ms. Den konfigurerbara tröskelprototypen är borttagen.

Nästa rasterexperiment bör därför inte ändra mängden parallellt arbete med en
fast tröskel. Det ska angripa själva schemaläggningskostnaden, exempelvis med
en beständig workerpool eller återanvända tilejobb. Nästa CPU-JIT behöver på
motsvarande sätt eliminera tolkdispatch, inte bara några predikterbara
bokföringsgrenar inne i samma safe-loop.

Innan en beständig workerpool införs mättes även graden i nuvarande
`Parallel.For`. På 28 logiska värdprocessorer var 16 arbetare bäst i ett första
2/4/8/16/28-svep. Sex ordningsbalanserade långpar mellan 16 och 28 gav
11 461,9 mot 11 706,3 ms, 2,09 procent vinst och fyra vunna par av sex med
exakt orakel. Warm-probe och desktop begränsar nu rastergraden till 16. Nästa
workerpool ska jämföras mot denna förbättrade bas och måste slå både tiden och
den befintliga exakta ordningen; dess främsta mål är återanvända jobb/workers,
inte fler samtidiga trådar.

Ett första försök med högst 16 fasta sammanhängande radsegment blev cirka 7
procent långsammare i tre kortpar och förlorade samtliga. Det är borttaget.
Workerpool-designen måste därför kombinera beständiga workers med dynamisk
rad-/tilehämtning, exempelvis ett atomiskt nästa-jobb-index, så att stora
trianglars ojämna scanlinekostnad inte lämnar arbetare sysslolösa.

Den dynamiska poolen löste båda problemen: 15 beständiga bakgrundsarbetare
plus anropstråden delar ett atomiskt nästa-rad-index och behåller därmed
lastbalanseringen utan `Parallel.For`-uppsättning per triangel. Kortprovet vann
två av två par med cirka 6,6 procent. Två balanserade långpar vann också båda,
10 527,0 mot 11 399,3 ms i medel, 7,65 procent, med exakt frame-, CPU-, FIFO-,
draw- och swap-orakel. Poolen är nu standard i warm-probe och desktop. Nästa
rastersteg bör flytta samma dynamiska köprincip från hela scanlines till
återanvända tilejobb först när en profil visar att radgranulariteten lämnar
mätbar kärnobalans.

Den beständiga poolen trimmades slutligen om från den gamla `Parallel.For`-
graden 16 till grad 8. Två balanserade långpar gav 10 651,4 mot 10 850,8 ms,
1,84 procent extra vinst och två vunna par av två med exakt orakel. Lägre grad
minskar både atomisk kökonkurrens och CPU-överbokning mot gästtolken; grad 8 är
nu Gauntlet-standard medan miljövariabeln fortfarande tillåter värdspecifik
mätning.
