# Gauntlet DL: större GPU-fönster och verklig swap-takt

## Slutsats

GPU-ersättningen är state-exakt även över ett större fönster, men den är
**långsammare än CPU-vägen**. Den ska fortsatt vara ett avstängt diagnostiskt
experiment, inte standardvägen till spelbarhet.

Med draw-gränsen 4 096 når replayen 6750→7950 totalt 2 890 stödda draws i
70 segment. Dessa ersätter CPU-pixelloopen; unsupported rasterisering körs
fortfarande på CPU. Probe avslutar nu GPU-sessionen explicit vid slutet av
den mätta replayen, även när draw-gränsen inte nåtts.

## Mätning

Samma capture-build, snapshot, 90 000 CPU-steg per probe-anrop och
`DOTNET_TieredCompilation=0`. Sekventiell ordning CPU1/GPU1/CPU2/GPU2, utan
Vulkan-validering under prestandaproven. GPU-läget använder resident färg/djup
och inkrementella texturuppladdningar. Initiering och avslut av GPU-sessionen
ingår i replaytiden; snapshot-inläsning och slutdumpning ingår inte.

| Körning | Replaytid | Utförda swaps | Swaps/s |
|---|---:|---:|---:|
| CPU1 | 12,2594 s | 41 | 3,344 |
| GPU1 | 21,7837 s | 41 | 1,882 |
| CPU2 | 12,1745 s | 41 | 3,368 |
| GPU2 | 21,2583 s | 41 | 1,929 |

GPU-vägen tar cirka 76 procent längre tid i medel i detta avsnitt.
Två upprepningar är ingen bred benchmark, men försämringen är tydlig här.
`displayRate` räknar skillnaden i backendens `_swapBufferCount`, som ökar
i `ExecuteSwapBuffers`: 3 836→3 877. Det är utförda swap-kommandon, inte
unik host-presenterad FPS, skärmens uppdateringsfrekvens eller input-latens.
Det gamla `score fps` räknar fortsatt probe-anrop, inte spelbilder.

GPU-proven gör 2 960 inlämningar (2 890 draws + 70 pixel-readbacks), laddar
upp 1 178 034 368 byte och läser tillbaka 587 387 520 byte. Textur/NCC-patchar
är endast 524 288 byte; fulla segmentstarter dominerar återstående upload.
Sessionerna avslutas med `pendingPixels=0`.

En separat slutlig Vulkan-valideringskörning ger samma 2 890 draws,
noll synkroniseringsvalideringsfel och exakt samma fullständiga slutstate:
`.build-tmp/gpu-expanded-validation.log` och motsvarande `-final.warm.gz`.

Full-state-oraklet är 98 901 914 dekomprimerade byte, SHA-256
`32f8e9c49dcc5498c044bb602156ef6e82fe5dec64f27c99749f3a6fe298053a`.
Framehash är `0xe87b12da`. Artefakter:
`.build-tmp/gpu-expanded-{cpu1,gpu1,cpu2,gpu2}-final.warm.gz` och respektive
`.log`. Den första filen `gpu-expanded-4096.log` ska inte användas: den
startades innan bygget var färdigt och körde föregående normalbinary.
`gpu-expanded-4096-v2.log` är ett tidigare validerat 2 890-draw-prov men
saknar den nya explicita sessionsavslutningen; slutmaskinen matchar också.

## Fortsättning

Nästa konkreta experiment bör begränsa GPU-dispatchen till drawens bounding
box. Native runtime skickar nu 2 097 152 shader-invokationer för varje draw,
och shadern avvisar koordinater utanför bounding box. Det är en verifierad
kodväg, men dess andel av tidskostnaden är ännu inte profilerad. Därefter
återstår atomiska rasterräknare, fence per draw och full CPU-texturskanning.
Ingen av dessa föreslagna optimeringar är implementerad i detta steg.

## Användning

Bygg Probe med `-p:GauntletGpuCapture=true` och native enligt verktygets README.
Sätt `EUTHERDRIVE_GAUNTDL_GPU_DRAW_LIMIT=4096` tillsammans med
`EUTHERDRIVE_GAUNTDL_GPU_REPLACE=1`, `EUTHERDRIVE_GAUNTDL_GPU_RESIDENT=1` och
`EUTHERDRIVE_GAUNTDL_GPU_INCREMENTAL=1`. Defaultgränsen är fortsatt 128;
tillåtna värden är 1–65 536. Batch-shadow får inte höjas över 128 och
offline-capture behåller sina gamla gränser. Nio ROM-fria tester kontrollerar
default, giltiga gränser, ogiltiga värden och batch-begränsningen.

Normalbygget är återställt. Probe/UI bygger utan fel; elva gränstester,
nio limit-testfall och PCI-regressionen passerar. Normal replay med
GPU-flaggor, `GPU_DRAW_LIMIT=bad` (med full env-prefix) och obefintlig
biblioteksadress ignorerar experimentet och matchar hela slutstate-oraklet:
`.build-tmp/gpu-expanded-normal-final.warm.gz`. Den gav 3,649 swaps/s och är
en separat normalbyggd kontroll, inte en del av den växlade capture-jämförelsen.
