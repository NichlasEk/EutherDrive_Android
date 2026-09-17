# Gauntlet DL: bounding-box-dispatch

## Slutsats och tider

Ingen påvisad fartvinst. Bounding-box-läget gör mycket mindre dispatcharbete
men var något långsammare än full-dispatch i den här jämförelsen. Det förblir
avstängt och ändrar inte normalbygget.

| Prov | Replaytid | Swaps/s |
|---|---:|---:|
| CPU | 11,9470 s | 3,432 |
| Bbox1 | 21,1347 s | 1,940 |
| Full GPU-dispatch | 19,5754 s | 2,094 |
| Bbox2 | 20,1100 s | 2,039 |

Alla fyra proven ger byte-exakt samma fullständiga slutmaskin. Samtliga
utför 41 swap-kommandon. Swaps/s är inte unik host-presenterad FPS.
Detta är ett litet, lokalt jämförelseprov, inte en bred benchmark.

Nästa steg bör vara CPU/GPU-tidsprofilering: separera shaderarbete, uppladdning,
readback/fence och CPU-snapshotarbete innan nästa förändring. Atomiska
rasterräknare och per-draw-väntningar kvarstår, men deras tidsandel är inte
fastställd här. Minskade invokationer bevisar inte att flaskhalsen har flyttats.

## Implementerat

`EUTHERDRIVE_GAUNTDL_GPU_BBOX=1` begränsar synkrona runtime-draws till
bounding box i draw-metadata. Push constants anger vänsterkant, överkant
och bredd; dispatchstorleken är bredd × höjd, avrundad till grupper om 128.
Shaderns befintliga bounds-check avvisar extra invokationer i sista gruppen.
Färg/djup skrivs fortsatt till fysisk GDR2-adress, inklusive Y-origin.
`data[5]` ändras inte eftersom det också pekar ut rasterstatistikens svans.

ABI är fortsatt v4 och shaderkoden är oförändrad. Flaggan gäller också
per-draw CPU/GPU-shadow, men ändrar inte offline- eller batch-dispatch.
Default är fortsatt avstängt. Native loggar nu antal faktiskt startade
invokationer, inklusive avrundning till hela workgroups.

## Arbetsmängd

Samma större replay 6750→7950 med limit 4 096 når 2 890 ersatta draws över
70 segment. Dispatchen minskar från 6 060 769 280 till 17 932 160 invokationer,
cirka 338 gånger färre. Överföringar och synkar ändras inte:
2 960 inlämningar, 1 178 034 368 byte upload, 587 387 520 byte readback,
70 fulla pixel-readbacks. CPU-setup och unsupported rasterisering kvarstår.

## Testmetod och artefakter

Sekventiella prestandaprov i samma capture-build, utan Vulkan-validering:
CPU, bbox1, full-dispatch, bbox2. Alla använder samma snapshot,
90 000 CPU-steg per probe-anrop och `DOTNET_TieredCompilation=0`.
GPU-proven använder resident färg/djup och inkrementella texturkopior.
Snapshot-inläsning/slutdumpning ingår inte i replaytiden; GPU-init/avslut gör det.

Full-state-orakel: 98 901 914 dekomprimerade byte, SHA-256
`32f8e9c49dcc5498c044bb602156ef6e82fe5dec64f27c99749f3a6fe298053a`.
Framehash: `0xe87b12da`. Loggar och states finns som
`.build-tmp/gpu-bbox-{cpu,bbox1,full,bbox2,validation,shadow}.log` och
motsvarande `-final.warm.gz`.

Det fristående resident-testet med syntetisk texturflytt matchar pixeloraklet
exakt; avsiktligt borttagna uppdateringar ger förväntade 5 550 avvikelser.
Reset/readback-ordning och den gamla batch-ABI-vägen passerar också.
Artefakter: `gpu-bbox-abi.log`, `gpu-bbox-negative.log`, `gpu-bbox-batch.log`.
Den syntetiska flytten är en testtransformation, inte observerat gästbeteende.

Separat Vulkan-validering över alla 2 890 draws ger noll synkfel och samma
full-state-hash. Per-draw shadow kontrollerar dessutom färg/djup och
rasterräknare oberoende mot CPU för 128 draws med bbox på; inga avvikelser
och samma fullständiga slutmaskin. Alla sessioner har `pendingPixels=0`.

Normalbygget är återställt. Probe/UI bygger utan fel; elva gränstester,
nio limit-testfall och PCI-regressionen passerar. Normal replay ignorerar
GPU-flaggorna även med ogiltig limit och obefintligt bibliotek, och ger samma
full-state-hash (`gpu-bbox-normal-final.warm.gz`). Shadern passerar `spirv-val`.

## Reproduktion

Bygg native med `sh tools/GauntletGpuProbe/build.sh`, Probe med
`-p:GauntletGpuCapture=true`. Använd `EUTHERDRIVE_GAUNTDL_GPU_BBOX=1`
tillsammans med `EUTHERDRIVE_GAUNTDL_GPU_REPLACE=1`,
`EUTHERDRIVE_GAUNTDL_GPU_RESIDENT=1`, `EUTHERDRIVE_GAUNTDL_GPU_INCREMENTAL=1`
och `EUTHERDRIVE_GAUNTDL_GPU_DRAW_LIMIT=4096`. För oberoende pixel-/räknar-
orakel används bara `GPU_SHADOW=1`, `GPU_BBOX=1` och limit 128 med samma
fulla `EUTHERDRIVE_GAUNTDL_`-prefix.
