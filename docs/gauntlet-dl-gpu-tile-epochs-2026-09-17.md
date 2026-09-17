# Gauntlet DL: pixelägda tile-epoker

## Resultat: lovande avgränsad arkitekturändring

Native opt-in `EUTHERDRIVE_GAUNTDL_GPU_TILE_BATCH=1` slår ihop sammanhängande
trianglar i en dispatch per oförändrad textur/NCC-epok och bildorigin.
Prototypen är korrekt i den verifierade sekvensen och visar en mindre men
konsekvent replayförbättring i tre par. Default förblir av; ingen seger mot
CPU-only eller spelbar bildfrekvens har visats här.

| Läge | Prov 1 | Prov 2 | Prov 3 | Median |
|---|---:|---:|---:|---:|
| Separata dispatchar | 13,9124 s | 13,8400 s | 14,0983 s | 13,9124 s |
| Tile-epoker | 13,4829 s | 13,5664 s | 13,6277 s | 13,5664 s |

Medianen är cirka **2,5 % lägre** i denna serie. Ordning: gammal→ny,
ny→gammal, gammal→ny. Samma capture-bygge och shader, profilering av,
validering unset, dirty-audit av, `DOTNET_TieredCompilation=0`. Tidigare
experiment för grupperad dirty-kontroll och batchåteranvändning är av i
båda leden. Inga egna samtidiga byggen/tester under tidsproven. Andra
värdjobb är inte isolerade; fler sekvenser behövs före generalisering.

## Hur prototypen fungerar

En tile är 16×8 pixlar, en workgroup om 128 trådar. Varje tråd äger en
fysisk pixel och går igenom epokens trianglar i ursprunglig ordning.
Packad färg/djup läses en gång, hålls lokalt mellan trianglarna och skrivs
tillbaka efter epoken. Den befintliga raster-/samplings-/blandningskoden
återanvänds. Varje triangles statistikslot uppdateras fortfarande atomiskt.

Native planering slår bara ihop draws med sammanhängande metadata,
fysiskt outputläge och samma Y-origin. En textur/NCC-patch bryter epoken
innan sin draw. Patchar på första draw körs före epokens dispatch.
Mellan epoker gäller ursprungliga Vulkan-barriärer; enstaka draws använder
tidigare dispatchväg. Ordinarie batch-/CPU-bildgränser ändras inte.

Trådarna inom en epok behöver ingen inbördes pixelbarriär: varje fysisk
pixel har exakt en ägare och textur/NCC är oförändrade. Origin-gränsen är
viktig för detta argument. Ingen sortering av trianglar sker.

Detta är en första tile-dispatchprototyp, **inte full gles tile-binning**:
unionens rektangel täcks av tiles och varje tile går igenom samtliga
trianglar i epoken. Bounding-box-testet avvisar irrelevanta pixlar.
Glest fördelade trianglar kan därför göra denna väg sämre. Inget antagande
om samma rasterstate mellan trianglarna görs; varje draw läser sina metadata.

## Arbetsmängd och profil

| Mätare | Separata | Tile-epoker |
|---|---:|---:|
| Draws | 17173 | 17173 |
| Compute-dispatchar | 17173 | 2488 |
| GPU-invokationer | 20268800 | 14678784 |
| Queue-submissions | 152 | 152 |
| Upload-byte | 643290272 | 643290272 |
| Readback-byte | 311623680 | 311623680 |

17051 draws fusioneras till 2366 tile-dispatchar; 122 draws är separata.
Det ger cirka 85,5 % färre dispatchar, **inte** färre queue-submissions.
Dirty-räknare och patch-byte är också oförändrade.

Ett separat profilpar gav native kommandoinspelning 33,397→14,740 ms och
GPU-dispatch/barriär/patch-intervallet 360,889→271,266 ms. GPU-före/efter-
intervallen varierade samtidigt (109,702→164,317 respektive 52,310→74,140 ms),
så isolerad shaderfart får inte härledas ur dispatchintervallet. Fence-väntan
var 573,007→582,407 ms. Native tile-planering ligger före record-timern men
ingår i managed flushInclusiveMs och total replaytid.

Profilparets runMs var 14889,0→13594,3, men initMs var 1522,08→290,536.
Den jämförelsen är därför **inte** totalfartbeviset; använd de sex proven
utan profilering i tabellen ovan. Probe-fps är inte spelets bildfrekvens.

## Verifiering

- Full CPU-shadow med tile-epoker, dirty-audit och Vulkan-validering:
  per-draw-räknare och färg/djup vid bildgränser matchar, noll Vulkan-fel.
- Nio replays (shadow, två profilerade, sex tidsprov) matchar hela maskinen:
  SHA-256 `32f8e9c49dcc5498c044bb602156ef6e82fe5dec64f27c99749f3a6fe298053a`.
  Bildhash `0xe87b12da`.
- Native differentialtest jämför samtliga outputord och statistikslots
  mot separata dispatchar för tre överlappande draws med olika färg/alpha,
  en full 128-draw-batch och en grupp med byte av Y-origin.
- Befintliga texture-relocation-, continuation-, reset-, dirty-audit- och
  negativa patchtester passerar. SPIR-V-validering passerar.
- Normal Release för Probe/UI är återställd och bygger utan fel. Normal-
  gräns-/dirty-/pending-/limit-tester och färgtester (720 state-, 393216 RGB-
  och 65536 alfafall) passerar.

Den utökade native-sviten träffade efter de nya differentialtesten värdens
NVIDIA-loaderfel `cannot allocate memory in static TLS block` vid upprepade
Vulkan-contextstarter. Hela sviten passerade med processlokal
`LD_PRELOAD=/usr/lib/libnvidia-tls.so.610.57.04`. Detta är en värdspecifik
test-workaround, ingen ändring av emulatorns eller systemets miljö.

## Reproduktion och fortsättning

Bygg både nativebibliotek och `draw.spv` med `tools/GauntletGpuProbe/build.sh`;
bygg Probe med `-p:GauntletGpuCapture=true`. Använd residenta nivå-3-flaggor
och replay 6750→7950, 90000 steg enligt
[kostnadsprofilen](gauntlet-dl-gpu-cost-profile-2026-09-17.md), med
`GPU_TILE_BATCH=0/1` (prefix `EUTHERDRIVE_GAUNTDL_`). Extern ABI är fortsatt 4;
tile-pushkonstanterna kräver den uppdaterade shadern. Flaggan är native och
av som standard. Normalbygget använder inte GPU-vägen.

Artefakter under `.build-tmp/`, inte incheckade:

- `gpu-tile-{shadow,profile-old,profile-new}{.log,-final.warm.gz}`
- `gpu-tile-bench-{old,new}-{1,2,3}{.log,-final.warm.gz}`
- `gpu-tile-native-v2{,-off}.log` (första, mindre differentialtestet)
- `gpu-tile-native-final.log` (utökade fall passerade före loaderfelet)
- `gpu-tile-native-final-preload.log` (hela utökade sviten passerar)
- `gpu-tile-build-v2.log`

Nästa större steg är ordningsbevarande triangellistor per tile: undvik att
varje tile måste besöka hela epoken. Bör först mätas på glest/överlappande
material och verifieras mot samma pixel- och statistik-orakel. Alternativt
behövs en representativ andra replay innan denna prototyp övervägs som default.
