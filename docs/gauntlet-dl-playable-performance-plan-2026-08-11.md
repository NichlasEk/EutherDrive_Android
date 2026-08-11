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
