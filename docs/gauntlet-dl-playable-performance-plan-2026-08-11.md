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

Ett billigare register-ABI för den befintliga expression-backenden prövades
därefter. En direkt GPR-arrayvariant var bitexakt vid normal blocktröskel men
cirka 2 procent långsammare i korta interfolierade prov. En liveness-variant
som bara laddade verkliga GPR-källor förbättrade det gamla lagret tydligt, men
var fortfarande regressiv i de beslutande långproven:

```text
safe batches, medel                         11 160,8 ms
liveness-JIT inklusive guarded traces      11 772,5 ms  (+5,48 %)

safe batches, medel                         11 647,4 ms
liveness-JIT utan guarded traces            11 825,9 ms  (+1,53 %)
```

Alla exakta långprov slutade på `frameHash=0xe87b12da`, PC
`0xffffffff80079e18`, FIFO `27113239/2660774`, `drawPackets=474871` och
`swaps=3877`. Att sänka hottröskeln till 64 och minsta blocklängden till fyra
gav hög kompilerad täckning och skenbart mycket hög fart, men drev omedelbart
PC, frame-hash och FIFO-state; korta block i den gamla backenden är alltså inte
ett korrekt sätt att köpa täckning. Båda ABI-prototyperna är helt borttagna.

Nästa JIT ska därför börja vid kontrollflödet: ett kompilerat block ska äga sin
branchterminator och delay slot, behålla ett litet registerset över länkade
efterföljare och göra side exit före observerbara helpers. Den ska mätas först
på de tre hetaste `jal`/`jr`-avslutade blockfamiljerna från safe-block-profilen;
ingen ny standard aktiveras förrän samma långa orakel både är exakt och slår
safe batches.

En ny opt-in blockövergångsprofil räknar nu fullständiga kanter från safe-
blockets start via branchterminatorn till verklig mål-PC. Kortprovet fångade
1 050 767 sammanslagna övergångar över 2 072 kanter. De hetaste är inte de
tidigare långa `jal`-blocken utan korta loopfamiljer kring `0x800c9c98`,
`0x800af794` och `0x80103e48`--`0x80103f3c`; den största enskilda branchens
tagna och otagna vägar kördes 22 929 respektive 22 930 gånger.

En första begränsad kedjemotor körde upp till åtta sådana avkodade block utan
nytt `Step()`-inträde. Den var bitexakt och absorberade 21 277 422
instruktioner över 3 186 876 block i långprovet, men kedjorna blev i snitt bara
drygt två korta block. Extra guards och dictionary-uppslag gjorde därför
motorn 3,54 procent långsammare:

```text
safe batches, medel             11 051,8 ms
safe block chains, medel        11 442,9 ms
```

Kedjemotorn är borttagen medan övergångsprofilen behålls. Nästa blockmotor ska
länka direkta cacheobjekt för tagna och otagna efterföljare och vakta en
versionssatt kodsida en gång per kedjeinträde. Den får inte göra ett globalt
blockuppslag eller en separat entry-word-läsning för varje kort efterföljare.

Direktlänkade efterföljare med två cachekanter per block prövades därefter.
Varje exekverad kodsida fick först en 4 KiB-versionsräknare; skrivningar till
sidan invaliderade länken före återanvändning. Kortprovet var bitexakt och gav
956 875 länkträffar mot 79 610 missar, men blev trots det cirka 2,5 procent
långsammare i det första balanserade paret. Att bara versionsbokföra sidor som
redan setts som kod tog inte bort regressionen. Varianten stoppades därför före
långprov och är helt borttagen.

Det återstående problemet är write-barriären: även en billig kontroll i varje
RAM-skrivning konkurrerar med den redan heta raster/FIFO-vägen. Nästa försök
ska antingen bevisa runtime-texten immutable för den versionsbundna warm-
snapshoten eller återanvända befintlig minnesöversättningsmetadata. En ny
global kontroll får inte läggas i varje data-write bara för JIT-invalidering.

En write-barriärfri kodsideprofil bevisade därefter vad warm-snapshoten
faktiskt gör. Varje 4 KiB-sida sparas första gången ett safe-block avkodas och
jämförs en gång vid provets slut. Både 300- och 1 200-anropsprovet exekverade
148 sidor. Endast sida `0x000b2000` ändrades, med tre byte vid offset
`0xed8`--`0xeda`; täckningsbitmapen visar att ingen av dessa byte någonsin
hämtades som instruktion. Alla faktiskt hämtade safe-block-bytes var alltså
oförändrade i det långa exakta oraklet.

Beviset räckte däremot inte för att göra ännu en C#-kedjeloop snabb. En variant
utan write-barriär eller versionsguard nådde 3 996 752 direkta länkträffar mot
2 381 missar i långprovet och behöll exakt state, men föll efter tiered
recompilation till cirka 20--21 sekunder. `AggressiveOptimization` förbättrade
den till 15 187,0 ms, fortfarande klart sämre än A-basens cirka 11,8 sekunder.
Kedjeexekveringen är helt borttagen; kodsideprofilen behålls.

Nästa implementation ska använda immutable-beviset för en kompakt genererad
runner med terminator och delay slot, inte för ytterligare en host-loop över
avkodade blockobjekt. Den självmodifierande dataslotten på sida `0xb2` behöver
ingen JIT-invalidering eftersom dess ändrade byte aldrig exekverades.

Den hetaste korta blockfamiljen fick därefter en kompakt opt-in-referensrunner
vid `0xffffffff800c9c98`. Signaturen är en sjuinstruktions float-copy-loop:
`lwc1`, tre enkla heltalsoperationer, `swc1`, `bne` och delay-slot. Runnern
validerar hela signaturen en gång, bevarar store-side-exit om
`RuntimeMainState` ändras och bokför exakt samma sju CP0-/gästinstruktioner.
PC-vakten ligger före hjälparanropet så övriga safe-block inte betalar ett
metodanrop för att avvisa kandidaten.

Kandidaten ersatte 1 305 766 dispatchade instruktioner i långprovet. Två
uppvärmda balanserade långserier, totalt fyra A/B-par, gav:

```text
safe batches, medel                 11 193,4 ms
compact conditional block, medel   10 947,3 ms
vinst                                    2,20 %
```

Alla fyra par vanns och samtliga körningar behöll
`frameHash=0xe87b12da`, PC `0xffffffff80079e18`, FIFO
`27113239/2660774`, `drawPackets=474871` och `swaps=3877`. Vägen stannar
opt-in eftersom den är adress-/signaturspecifik, men den är nu det konkreta
prestanda- och korrekthetsmålet för den generella runnergeneratorn: samma
terminator/delay-slot-form ska emitteras från ett immutable safe-block utan
handskriven PC-specifik semantik.

2026-09-10: den första IL-genererade ersättaren matchar nu kort- och lång-
oraklet och kör med ungefär den handskrivna referensens fart. Den är fortsatt
opt-in och innebär ingen påvisad hastighetsvinst. Mätningar, begränsningar och
nästa loopsteg finns i
[generatorns checkpoint](gauntlet-dl-generated-jit-checkpoint-2026-09-10.md).

Ett efterföljande flervarvsförsök behöll registren lokalt och halverade antalet
genererade anrop, men gav bara 0,32 procent skillnad i medel, sämre median och
två vunna långpar av fyra. Hela oraklet var exakt, inklusive riktade budget-
och timertester. Flervarvsvarianten är borttagen ur körkoden; nästa kandidat
ska väljas efter större faktisk CPU-kostnad per inträde.

Två större block (29/38 instruktioner) prövades därefter med direkt IL och
bevarad bokföring per instruktion. Kandidaten körde 2 388 989 instruktioner
men blev 1,93 procent långsammare över fyra balanserade långpar och är
borttagen. Även fullständiga sparade maskintillstånd var byte-exakt lika.
Resultat och nästa mätbehov finns i
[större block-experimentet](gauntlet-dl-large-block-experiment-2026-09-10.md).

En efterföljande faktisk tidsprofil ändrar prioriteringen: texturraster ensam
tar 4,04–4,07 sekunder av cirka 11,8 sekunder. Två stackprofiler visar ungefär
35 procent texturraster, 21 procent övrig Voodoo/FIFO/presentation och 38–39
procent återstående MIPS-kod på huvudtråden. CPU-fasens tid inkluderar alltså
mycket synkron grafik. Om rasterkostnaden består kan CPU-JIT ensam inte nå
30 swaps/s. Metod, begränsningar och konkreta heta vägar finns i
[tidsprofilen](gauntlet-dl-runtime-time-profile-2026-09-10.md).

Den första behållna optimeringen från denna tidsprofil gäller FIFO-bokföring:
kontrollera medlemskap före borttagning av ett tidigare pakethuvud. Fyra
balanserade långpar vanns med 1,19 procent lägre medeltid och byte-exakt
fullständig snapshot. Ändringen är nu vanlig kod utan experimentflagga.
Se [FIFO-checkpointen](gauntlet-dl-fifo-checkpoint-2026-09-10.md).

Både PCI-loggning och texturförberedelse har därefter prövats. Avstängd
PCI-loggning skapar nu inga strängar: ett riktat 40 000-anropstest går från
5,76 MB allokeringar till noll, men replaytiden är ungefär neutral.
Texturkandidaten flyttade fasta samplebeslut till triangelns förberedelse;
den första positiva mätningen höll inte i bekräftelsen och kandidaten är
borttagen. Alla tre varianter gav byte-exakt samma fullständiga slutmaskin.
Även probens slutdump med aktiverad spårning har rättats. Se
[PCI- och texturcheckpointen](gauntlet-dl-pci-texture-checkpoint-2026-09-10.md).

Ett första avgränsat GPU-prov finns nu: 558 872 verkliga samplingsanrop från
16 trianglar ger byte-exakt samma RGBA på RTX 4090 som C#-samplern. GPU med
nya requests men kvarliggande texturer är i provet cirka 2,1 gånger snabbare
än åtta CPU-workers; full uppladdning är däremot långsammare. Det är en
fristående sampling-replay, inte en GPU-renderad spelbild eller ny spelhastighet.
Inspelningen kompileras bort i normalbygget. Nästa steg är draw-replay med
kvarliggande textur-/färg-/djupbuffertar och GPU-genererade koordinater, följt
av exakt färg-/djupjämförelse. Se
[GPU-provet](gauntlet-dl-gpu-sampling-proof-2026-09-10.md).

Draw-steget är nu verifierat offline: 16 kompletta common-state-trianglar
matchar exakt färg/djup, liksom ordnade batcher om fyra respektive två draws
med gemensamma GPU-buffertar. Nästa gräns är en renderingsström med
texturuppdateringar, clears och CPU-läsbarriärer, följt av live-integration.
Ingen ny spelhastighet är ännu uppmätt. Se
[draw-checkpointen](gauntlet-dl-gpu-draw-proof-2026-09-10.md).

Nästa steg har nu också verifierats offline: sammanhängande segment med
26, 32 och 4 draws, full färg-/djupjämförelse och ordnade NCC-uppdateringar.
Råtexturkopior testas separat med en pixelbevarande texturflytt och negativ
kontroll. Clear/swap/CPU-access avslutar segmenten; de körs ännu inte på GPU.
Nästa integrationsgräns är runtime shadow-körning med flush/fallback och
återupptagning. Se [stream-checkpointen](gauntlet-dl-gpu-stream-proof-2026-09-10.md).

Runtime shadow fungerar nu via ett in-process C-ABI: 256 draws i två
replayfönster matchar färg/djup exakt, inklusive återupptagning efter CPU-
rendering av unsupported states. CPU:n är fortfarande auktoritativ och
detta är seriell dubbelkörning, inte en prestandavinst. Nästa steg är köad
segmentkörning och dirty-uppladdningar i runtime. Se
[runtime-checkpointen](gauntlet-dl-gpu-runtime-shadow-2026-09-10.md).

Runtime-batchning är nu verifierad: samma 128 draws i första fönstret kräver
tre inlämningar i stället för 128, med cirka 21,7 gånger mindre uppladdning
och 42,7 gånger mindre readback. Full färg/djup och fullständig slutmaskin
matchar. CPU:n levererar fortfarande spelresultatet; detta minskar kostnaden
för shadow-verifieringen, inte ännu spelets rasterarbete. Se
[batch-checkpointen](gauntlet-dl-gpu-runtime-batch-2026-09-10.md).

Ett begränsat faktiskt ersättningsläge är nu verifierat: GPU:n ersätter
CPU-pixelloopen för 128 draws i vardera av två fönster, med byte-exakt samma
fullständiga slutmaskin. GPU-räknare bevarar rasterloopens sidoeffekter och
har kontrollerats mot CPU-shadow. Ersättningen synkar fortfarande per draw,
så ingen säker fartökning är påvisad. Nästa steg är mindre överföringar och
explicit CPU/GPU-buffersynk. Se
[ersättningscheckpointen](gauntlet-dl-gpu-replacement-proof-2026-09-11.md).

Färg/djup kan nu ligga kvar på GPU mellan ersatta draws. Två 128-draw-fönster
ger byte-exakt samma fullständiga slutmaskin, med endast 64 byte räknare per
draw och full pixel-readback vid segmentgränser. Första fönstrets readback
minskar cirka 42,7 gånger. Texturuppladdningen är ännu full per draw och ingen
säker fartökning är påvisad. Nästa steg är inkrementella texturöverföringar.
Se [resident-checkpointen](gauntlet-dl-gpu-resident-proof-2026-09-11.md).

Resident ersättning har nu valbara inkrementella textur/NCC-uppladdningar.
Två fönster matchar hela CPU-slutmaskinen exakt; första fönstrets uppladdning
minskar från 1,10 GB till 50,5 MB. Snapshot-skanning på CPU och fence per draw
kvarstår, så detta är ännu ingen påvisad spelbarhetsvinst. Nästa mätning bör
öka det verifierade ersättningsfönstret och räkna verkliga swaps/sekund.
Se [inkrementell checkpoint](gauntlet-dl-gpu-incremental-proof-2026-09-11.md).

Det större GPU-fönstret är nu mätt: 2 890 ersatta draws över 70 segment
matchar hela CPU-slutmaskinen, men GPU-vägen ger cirka 1,9 swaps/s mot CPU:ns
3,35 i två växlade omkörningar utan Vulkan-validering. Ingen standardändring:
GPU-experimentet är långsammare. Nästa konkreta försök är bounding-box-begränsad
dispatch; nu startas 2 097 152 shader-invokationer per draw. Se
[utökat fönster och mätning](gauntlet-dl-gpu-expanded-window-2026-09-11.md).

Bounding-box-dispatch är nu provad och state-exakt för samma 2 890 draws.
Den minskar invokationerna cirka 338 gånger men visar ingen fartvinst:
1,94–2,04 swaps/s mot full-dispatch 2,09 och CPU 3,43 i denna jämförelse.
Flaggan förblir avstängd. Nästa steg är att profilera CPU/GPU-tidsandelar,
inte anta att färre invokationer betyder högre speltempo. Se
[bounding-box-provet](gauntlet-dl-gpu-bbox-proof-2026-09-11.md).

CPU/GPU-profilering är nu tillagd. Två bbox-replayer mäter cirka 5,3 s native
snapshot-förberedelser, 2,5–2,6 s fence-väntan och endast 0,16–0,17 s device-
dispatch. Full dispatch mäter cirka 0,20 s device-dispatch. Nästa avgränsade
försök är att uppdatera CPU-texturspegeln endast för ändrade block, med scan-
kostnaden kvar som separat begränsning. GPU-tiderna överlappar host-väntan
och får inte adderas till den. Se [tidsprofilen](gauntlet-dl-gpu-profile-2026-09-11.md).

CPU-texturspegeln kan nu uppdateras bara för ändrade block. Två växlade prov
minskar native-förberedelser från cirka 4,93 s till 1,91 s och GPU-replaytid
cirka 14,5 procent i medel, till 2,46–2,55 swaps/s. Fullminnesskanning och
fence per draw kvarstår; CPU-standardvägen är fortfarande snabbare.
Se [sparse snapshot-provet](gauntlet-dl-gpu-sparse-snapshot-2026-09-11.md).

Skrivspårade textursidor har nu provats med full kontrollskanning som orakel.
Två växlade GPU-replayer blir cirka 20,7 procent kortare än sparse-vägen:
3,12–3,17 swaps/s, native-förberedelser cirka 0,22 s. Slutmaskinerna är
byte-exakta och kontrollen hittar inga missade sidor. NCC jämförs fortsatt
fullt och segmentstarter återinitialiseras; normalbygget är oförändrat.
Se [dirty-texture-checkpointen](gauntlet-dl-gpu-dirty-texture-2026-09-17.md).

GPU-checkpointen är pushad som `3eaf4caf`. Fortsatt fasprofilering visar att
GPU-vägens extra kostnad främst ligger i det synkrona rasterintervallet:
5,40 s mot CPU-vägens 3,69 s. Samplingsprofil av normalbygget pekar vidare
mot CPU-rasterloopen och MIPS `Step`/safe-instruction-dispatch. Ingen ny
runtimeoptimering är införd på basis av detta ännu. Se
[CPU-fasprofilen](gauntlet-dl-cpu-phase-profile-2026-09-17.md).

Två små CPU-försök är därefter avklarade: readonly-referens till avkodad
instruktion samt ordläsning av runtime-status. Alla 16 slutmaskiner matchar
exakt, men ingen stabil fartvinst visas; båda runtimeändringarna återställdes.
Warm-runnern stöder nu frysta A/B-byggen. Se
[dispatchförsöken](gauntlet-dl-cpu-dispatch-experiments-2026-09-17.md).

Även sen färgberäkning och konstant-LOD-snabbväg har provats i rasterloopen.
Alla 16 slutmaskiner matchar exakt, men ingen stabil fartvinst visas; båda
återställdes. Ett ROM-fritt LOD-regressionstest med 1 048 576 fall behålls.
Se [rasterförsöken](gauntlet-dl-raster-experiments-2026-09-17.md).

Packad bilinjär texturfiltrering behålls: sex växlade gamla/nya par ger
1,93 procent kortare replaytid i medel på denna host (fyra av sex par vinner).
Alla tolv slutmaskiner matchar exakt och 1 156 784 filtertestfall passerar.
Android-vinst är ännu inte mätt. Se
[filtercheckpointen](gauntlet-dl-packed-texture-filter-2026-09-17.md).

Ny GPU-audit visar att alla 70 segment bryts av två TMU-kombinationer med
färgkombination `0c602c19`. Draw-fence-väntan är cirka 0,62–0,63 s av
12,73–12,81 s. Nästa steg är verifierat stöd för dessa renderlägen innan
större asynkron replacement införs. Ingen synkronisering är borttagen.
Se [synkroniseringsauditen](gauntlet-dl-gpu-sync-audit-2026-09-17.md).

De två första `0c602c19`-kombinationerna stöds nu bakom en separat diagnostisk
flagga. Full-state och shadow/validering passerar: 573 ytterligare GPU-draws.
Readbacks är fortfarande 70 och tidsproven visar ingen vinst. Nästa två
TMU-par och ett avvikande fbz-läge återstår innan färre segment kan påvisas.
Se [utökad färgväg](gauntlet-dl-gpu-extended-color-path-2026-09-17.md).

Färgvägens nivå 2 tillåter nästa tre registerkombinationer med exakta slutstater
och godkänd Vulkan-validering. 9 012 draws flyttas till GPU, men 70 readbacks
kvarstår och tiden ökar cirka 10,7 procent mot nivå 1. Detta behålls som
avstängd diagnostik, inte snabb standardväg. Se
[nivå 2-checkpointen](gauntlet-dl-gpu-color-level2-2026-09-17.md).

Kort valbar fence-pollning före oförändrad ordinarie väntan minskar nivå 2:s
replaytid cirka 4,86 procent i fyra växlade par; alla slutmaskiner matchar.
CPU-/energikostnad är inte mätt, normal CPU är fortfarande snabbare, och
pollning är av som standard. Se [väntprovet](gauntlet-dl-gpu-fence-poll-2026-09-17.md).

Batch-shadow kan nu verifiera varje draws statistik separat: samma 9 012
draws går i 104 submissions med full bild-/räknarmatch och byte-exakt
slutmaskin. CPU-oraklet kör fortfarande, så detta är inte snabb replacement
ännu. Kapacitetsfortsättning, texturpatchar och negativkontroller är testade.
Se [batch-statistik-checkpointen](gauntlet-dl-gpu-batch-statistics-2026-09-17.md).

## GPU batch replacement checkpoint, 2026-09-17

Riktig opt-in-batchersättning är implementerad: 9 012 CPU-rasteriseringar
ersätts av 104 GPU-submissions, med per-draw-räknare och byte-exakt slutmaskin.
Bbox-dispatch och dirty/sparse-textursnapshots fungerar även för batcher.
Tre växlade prov ger CPU-median 11,8358 s och GPU-median 13,4096 s: ingen
defaultändring eller påstådd spelbarhetsvinst. Nästa GPU-steg är resident
data mellan batcher; mätprofilen visar betydligt mer överförings-/förberedelse-
kostnad än shaderberäkning. Se [checkpoint](gauntlet-dl-gpu-batch-replacement-2026-09-17.md).

## Residenta kapacitetsbatcher, 2026-09-17

GPU-bild och texturer kan nu ligga kvar över kapacitetsflushar. Samma 9 012
draws/104 submissions ger 32,5 % mindre upload/readback och byte-exakt
slutmaskin. Tre roterade prov ger median 14,4769→14,0931 s jämfört med gamla
batchvägen, 2,65 % kortare tid. CPU-medianen i samma prov är 12,3787 s;
GPU-läget är fortfarande opt-in och inte snabbare än CPU. Se
[resident batch-checkpoint](gauntlet-dl-gpu-resident-batches-2026-09-17.md).
