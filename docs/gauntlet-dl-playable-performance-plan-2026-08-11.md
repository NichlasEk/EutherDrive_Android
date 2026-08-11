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
3. Kompilera heta raka block med nuvarande expression-backend.
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
