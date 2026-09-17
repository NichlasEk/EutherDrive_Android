# Gauntlet DL: GPU-data kvar mellan kapacitetsbatcher

## Resultat

Tre roterade jämförelser i samma capture-build, utan Vulkan-validering eller
dirty-audit, med `DOTNET_TieredCompilation=0`:

| Läge | Prov 1 | Prov 2 | Prov 3 | Median |
|---|---:|---:|---:|---:|
| CPU | 12,3787 s | 12,3452 s | 12,6355 s | 12,3787 s |
| Tidigare batchväg | 14,3945 s | 15,0840 s | 14,4769 s | 14,4769 s |
| Resident batchväg | 14,0931 s | 13,7405 s | 14,2100 s | 14,0931 s |

Medianens replaytid minskar **2,65 % jämfört med tidigare GPU-batchning**.
Samtliga tre jämförelser pekar åt samma håll, men detta är ett litet lokalt
prov, inte en generell fartgaranti. CPU-vägen är fortfarande snabbare;
inget speldefault ändras. Siffrorna ska inte jämföras direkt med äldre
turnars absoluta tider eller tolkas som spel-FPS.

Ordning: CPU→gammal→resident, resident→CPU→gammal, gammal→resident→CPU.
Inga byggen eller andra testjobb kördes samtidigt. GPU-init och slutlig
dränering ingår; snapshot-inläsning/slutdump gör det inte. Alla nio fulla
slutmaskiner matchar oraklet nedan. Artefakter:
`.build-tmp/gpu-resident-bench-{cpu,old,resident}-{1,2,3}{.log,-final.warm.gz}`.

## Implementation

Opt-in `EUTHERDRIVE_GAUNTDL_GPU_BATCH_RESIDENT=1` läggs till den verifierade
batch-statistik-/replacement-vägen. Flaggan fungerar också i shadow-läge;
vanliga `GPU_RESIDENT` gäller fortsatt endast per-draw-vägen och ska vara av.
Batch-statistik krävs. Normalbygget laddar fortfarande inget nativebibliotek.

En kapacitetsflush kör batchen och läser bara tillbaka 128×16 statistikord.
Färg/djup och textur/NCC ligger kvar på GPU:n. Nästa batch använder samma
fysiska buffertar och laddar endast metadata och oföränderliga texturpatchar.
Patchar före den nya batchens första draw hanteras också; CPU-bilden används
inte som startbild vid fortsatt GPU-rasterisering.

Native ABI är fortsatt 4, med ny capability
`gauntlet_shadow_batch_resident_version() == 1` och export
`gauntlet_shadow_flush_keep`. Enqueue-reset har tre lägen:

- 0: ytterligare draw i en redan köad batch;
- 1: ny batch från färska CPU-texturer och CPU-färg/djup;
- 2: ny batch från kvarvarande GPU-data efter keep-flush.

Fel sekvens avvisas, inklusive continuation utan kvarvarande batch,
CPU-reset som skulle kasta bort väntande GPU-pixlar och readback medan nya
draws ligger i kö. En riktig pixel-readback ogiltigförklarar continuation.
Full flush använder befintlig barriär-/readback-ordning. Keep-flush ändrar
bara vilka data som kopieras, inte fence-väntan eller draw-ordningen.

Managed kod skiljer nu mellan aktiv kö och väntande GPU-pixlar. CPU-/fallback-
gränser, buffertbyte och shutdown dränerar även det senare tillståndet, där
ingen ny draw har kommit efter kapacitetsflush. Shadow jämför räknare vid
varje batch och färg/djup vid verkliga pixelgränser. Replacement applicerar
räknarna i ordning men kopierar inte tillbaka pixlar förrän de behövs.

## Verifierad replay och dataöverföring

6750→7950, 90 000 steg, färgnivå 2, limit 65 536. Samma 9 012 draws och
104 submissions: 34 kapacitetsflushar och 70 verkliga pixelgränser.

| Mätare | Tidigare batchväg | Resident batchväg |
|---|---:|---:|
| Upload-byte | 1 760 251 136 | 1 189 756 096 |
| Readback-byte | 873 267 200 | 588 054 528 |
| Snapshot-kopierade byte | 874 201 088 | 588 919 808 |
| Patch-byte | 1 572 864 | 1 573 888 |

Den extra KiB-patchen ersätter en NCC-uppdatering som tidigare ingick i en
full ny batchsnapshot. Bbox-dispatchen är oförändrad: 18 932 608 invokationer.
Totala upload/readback-mängden sjunker cirka 32,5 %. Detta är inte i sig ett
bevis på lika stor tidsvinst.

Både full shadow och faktisk CPU-ersättning passerar med Vulkan-validering
och oberoende full dirty-page-audit. Samtliga per-draw-räknare matchar i shadow;
slutmaskinen är byte-exakt i båda lägena, noll synkroniseringsfel och inga
kvarvarande pixlar vid avslut.

Full-state-orakel: 98 901 914 dekomprimerade byte, SHA-256
`32f8e9c49dcc5498c044bb602156ef6e82fe5dec64f27c99749f3a6fe298053a`.
Bildhash `0xe87b12da`, 41 swaps. Artefakter:
`.build-tmp/gpu-resident-batch-{SHADOW,REPLACE}{.log,-final.warm.gz}`.

## Säkerhetstester

Native-fixturer testar fortsatt bild/djup och flyttade texturer över två
batcher, med dirty-läget av/på. Per-draw-räknarna jämförs med separata
full-dispatch-resultat; pixlar jämförs med CPU-genererade fixturer.
Direkt readback efter keep-flush utan nästa draw och därefter ny CPU-reset
testas. Äldre batch-/resident-flöden ska fortsätta fungera.

Native-testet avvisar även ogiltiga resetvärden. En negativ kontroll som
utelämnar texturpatchar på continuation-draw 0 ger 5 550 felaktiga pixlar;
pixeloraklet upptäcker alltså också just detta nya fel. Hela native-sviten,
äldre resident-testet och `spirv-val` passerar. Loggar:
`.build-tmp/gpu-batch-resident-native-v2.log` och `gpu-batch-resident-legacy.log`.

Runtime-limit 240 med färgnivå 2 ger först 111 draws, sedan en full batch om
128 (keep-flush vid draw 239), sedan en enda continuation-draw innan stopp.
Slutmaskinen är exakt och Vulkan-valideringen ren. Det testar stopp/dränering
omedelbart efter verklig kapacitetsfortsättning:
`.build-tmp/gpu-resident-batch-limit240{.log,-final.warm.gz}`.
Limit 129 utan färgnivå 2 passerar också, men når ingen kapacitetsflush.

ROM-fria tester når pixel-synk vid 13 CPU-läs-/skrivgränser plus shutdown,
även med inaktiv kö men väntande GPU-pixlar. De använder avsiktligt ett
disponerat kontextobjekt som markör, inte Vulkan; native-fixturerna verifierar
den faktiska readbacken separat. De vanliga 13 gränstesterna, deferred-räknarna,
dirty-writer-testerna och limiterna passerar också.

Normal Release för Probe/UI är återställd och bygger utan fel. Gräns-/dirty-
writer-/limit-tester, 500 färgtillstånd, 393 216 RGB-/65 536 alfafall,
1 156 784 filterfall, 1 048 576 LOD-fall och PCI-tracetester passerar.
Normal replay med de nya GPU-flaggorna och obefintlig nativebibliotekssökväg
ger samma full-state-hash: vägen är fortsatt bortkompilerad. Loggar:
`.build-tmp/gpu-resident-batch-normal-build.log`,
`.build-tmp/gpu-resident-batch-normal.log` och motsvarande `-final.warm.gz`.

## Reproduktion

Utgå från kommandot i [batch replacement](gauntlet-dl-gpu-batch-replacement-2026-09-17.md)
och lägg till `EUTHERDRIVE_GAUNTDL_GPU_BATCH_RESIDENT=1`. Bygg om native och
capture-Probe tillsammans. För shadow byts `GPU_REPLACE=1` mot `GPU_SHADOW=1`.
Vid korrekthetsprov används `GAUNTLET_GPU_VALIDATION=1` och `GPU_VERIFY_DIRTY=1`.
Vid tidsprov ska `GAUNTLET_GPU_VALIDATION` vara unset, inte `0`, och dirty-audit
vara av. Återställ normal Probe/UI efter provet.
