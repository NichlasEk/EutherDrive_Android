# Gauntlet DL: CPU-spegel med kopiering av ändrade block

## Resultat

Det här försöket ger en upprepad lokal förbättring av GPU-vägen. Replaytiden
minskar cirka 14,5 procent i medel i den växlade jämförelsen. Det är inte
14,5 procent snabbare normalbygge: standardvägen använder fortfarande CPU.

| Prov | Native förberedelser | Replaytid | Swaps/s |
|---|---:|---:|---:|
| Old1 | 4,940 s | 19,2745 s | 2,127 |
| Sparse1 | 1,952 s | 16,6643 s | 2,460 |
| Old2 | 4,921 s | 18,9932 s | 2,159 |
| Sparse2 | 1,874 s | 16,0648 s | 2,552 |

Alla kör samma 2 890 GPU-draws i 70 segment och utför 41 swaps. Kopierade
CPU-textur/NCC-byte minskar från 24 248 995 840 till 587 870 208, cirka
41 gånger mindre. GPU-överföringar och dispatchantal är oförändrade.
Förberedelserna minskar cirka 61 procent. Fence-väntan varierar också mellan
proven; hela skillnaden i replaytid ska inte tolkas som isolerad memcpy-tid.

Detta är två lokala upprepningar med profilering, inte en bred benchmark.
GPU-vägen är fortfarande långsammare än den tidigare CPU-referensen omkring
3,4 swaps/s. Ingen standardinställning ändras och spelbart tempo är inte nått.
Nästa återstående arbete är fullminnesskanningen och synkronisering per draw.

## Ändringen

`EUTHERDRIVE_GAUNTDL_GPU_SPARSE_SNAPSHOT=1` aktiverar ett avgränsat försök
i den inkrementella resident-vägen. Native jämför fortsatt alla 1 KiB-block
mot senaste CPU-spegeln, men uppdaterar bara spegelblock som faktiskt ändrats.
Tidigare kopierades hela textur/NCC-snapshoten efter varje jämförelse.

Första drawen efter segmentstart/reset kör fortfarande full kopiering.
Metadata och initial färg/djup-packning ändras inte, inte heller GPU-upload,
readback eller synkronisering. Ingen write-path dirty-tracking införs.
Negativtestets borttagna GPU-kopior påverkar inte CPU-spegelns uppdatering,
vilket bevarar testets tidigare betydelse: GPU:n får avsiktligt gamla data.

`gpuSnapshot copiedBytes` räknar textur/NCC-byte som faktiskt kopieras till
native CPU-spegeln under synkrona draws. Det räknar inte lästa/jämförda byte,
metadata, färg/djup-packning eller stagingkopior. Flaggan är avstängd som
standard och har ingen effekt utan inkrementell resident-ersättning.

## Testmetod

Samma capture-build, native ABI v4, RTX 4090/Linux, replay 6750→7950 från
den tidigare warm-snapshoten med 90 000 CPU-steg per probe-anrop och
`DOTNET_TieredCompilation=0`. GPU replacement/resident/incremental/bbox/profile
är på och draw-limit är 4 096. Old1/sparse1/old2/sparse2 körs sekventiellt
utan Vulkan-validering; sist körs ett separat valideringsprov.

Full-state-orakel: 98 901 914 dekomprimerade byte, SHA-256
`32f8e9c49dcc5498c044bb602156ef6e82fe5dec64f27c99749f3a6fe298053a`.
Framehash är `0xe87b12da`. Swaps/s avser utförda swap-kommandon, inte unik
host-presenterad FPS eller input-latens. Profilering är på i båda jämförda
lägena; tiderna är inte en opåverkad produktionsbenchmark.

## Reproduktion och artefakter

Bygg native och capture-Probe enligt
[verktygets README](../tools/GauntletGpuProbe/README.md#resident-framebuffer-replacement).
Lägg `EUTHERDRIVE_GAUNTDL_GPU_SPARSE_SNAPSHOT=1` ovanpå de tidigare flaggorna;
värdet `0` behåller full CPU-snapshotkopiering.

`.build-tmp/gpu-sparse-{old1,sparse1,old2,sparse2,validation}.log` och
motsvarande `-final.warm.gz` är runtimeproven. `gpu-sparse-abi.log` verifierar
två verkliga draws, syntetisk texturflytt samt reset/readback-ordning.
`gpu-sparse-negative.log` upptäcker 5 550 pixelavvikelser när ändrade textur-
kopior till GPU avsiktligt utelämnas. Båda ABI-proven passerar Vulkan-validering.

Alla fem runtimeprov matchar det fullständiga slutstate-oraklet exakt.
Separat Vulkan-validering har noll synkfel och `pendingPixels=0`.
Den oförändrade batch-vägen passerar sitt ABI-regressionstest även med den
nya flaggan satt (`gpu-sparse-batch.log`). Shadern passerar `spirv-val`.

Normalbygget är återställt. Probe/UI bygger utan fel; elva gränstestfall,
nio limit-testfall och PCI-regressionen passerar. Normal replay ignorerar
experimentflaggorna även med ogiltig draw-limit och obefintligt bibliotek,
utan GPU-/profilutskrifter, och matchar hela slutstate-oraklet
(`gpu-sparse-normal-final.warm.gz`). Ändringen aktiveras inte i normalbygget.
