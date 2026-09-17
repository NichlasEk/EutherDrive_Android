# Gauntlet DL: CPU/GPU-tidsprofil

## Mätning

Valbar `EUTHERDRIVE_GAUNTDL_GPU_PROFILE=1`, native + capture-build av Core.
Samma replay 6750→7950, draw-limit 4 096, 2 890 ersatta draws i 70 segment.
Resident färg/djup och inkrementella texturuppladdningar är på. Två bbox-prov
omger ett full-dispatch-prov; prestandaproven har Vulkan-validering avstängd.
Separata kontroller kör profileraren avstängd respektive Vulkan-validering på.

Host-tider mäts med monotona klockor. GPU-tider använder Vulkan-tidsstämplar
med köfamiljens giltiga timestamp-bitar. Tidsstämplarna hämtas efter redan
befintlig fence-väntan. Samma fulla slutstate-orakel används som tidigare:
98 901 914 byte dekomprimerat, SHA-256
`32f8e9c49dcc5498c044bb602156ef6e82fe5dec64f27c99749f3a6fe298053a`.

## Vad siffrorna betyder

- `prepareMs`: native renderDraw före submit: kontroll, textur/NCC-diffning,
  full snapshot-kopiering, metadata och initial färg/djup-packning vid reset.
  Dessa delar är ännu inte separerade internt. C#-metadata och vanlig
  CPU-emulering ingår inte. Batch-enqueue har inte motsvarande prep-timer.
- `recordMs`, `stagingMs`, `queueMs`, `waitMs`, `queryMs`: skilda host-intervall
  för draw-submission. Fence-väntan inkluderar GPU-köarbete och host-väckning;
  skillnaden mot GPU-tid kan inte automatiskt kallas ren drivrutinskostnad.
- `readPixelsMs`: hela native-gränsanropet, inklusive dess command recording,
  submission, fence och query. Det ligger inte i draw-timernas summor.
- `applyPixelsMs`: C#-kopiering/uppackning till verkliga CPU-färg/djupbuffertar.
- `initMs`: native-konstruktorns kropp inklusive Vulkan-init, inte alla
  minnesallokeringar före konstruktorkroppen eller destruktion.
- Device pre-dispatch: upload, buffertinitiering, stats-clear och barriärer.
  Dispatch: intervallet runt shaderdispatch, inte en isolerad ALU-mätning.
  Post-dispatch: barriärer och statistik-readback. Pixel-read: fulla
  readback-kommandobufferten vid segmentgränser.

**Device-tider överlappar host-väntan. De ska inte adderas till host-tiderna.**
Timrarna förklarar inte hela replaytiden: vanlig CPU-emulering, C#-setup,
runtimeadministration, annan rasterisering och omätta overheadposter kvarstår.

## Första jämförelsen

| Aggregerad tid | Bbox1 | Full dispatch |
|---|---:|---:|
| Native förberedelser | 5 392 ms | 5 256 ms |
| Command recording | 66 ms | 65 ms |
| Host-stagingkopior | 108 ms | 108 ms |
| Queue submission | 39 ms | 39 ms |
| Fence-väntan, draws | 2 517 ms | 2 506 ms |
| Native fullpixel-readback | 180 ms | 176 ms |
| C# pixeluppackning | 221 ms | 216 ms |
| GPU pre-dispatch | 320 ms | 313 ms |
| GPU dispatch | 158 ms | 199 ms |
| GPU post-dispatch | 17 ms | 17 ms |
| GPU fullpixel-readback | 147 ms | 143 ms |

Bounding-box minskar här device-dispatch med cirka 41 ms för hela replayen.
Det är litet jämfört med flera sekunder native-förberedelser och fence-väntan.
Det förklarar varför 338 gånger färre invokationer inte gav en synlig
spelbarhetsvinst. Förberedelser är den största uppmätta native-hostposten;
det är inte ett påstående att den dominerar all emulering.

Omkörningen bbox2 ger 5 332 ms förberedelser, 2 637 ms fence-väntan och
165 ms GPU-dispatch, alltså samma storleksordning. Bbox1/bbox2 replaytider
är 21,794/22,018 s. Profilering avstängd ger 21,496 s (1,907 swaps/s), så
instrumenterade tider ska inte behandlas som en opåverkad benchmark.
Profileraren gör inga extra fence-väntningar, men tillför tidsstämplar,
queryhämtning och host-klockavläsningar.

## Nästa steg

Minska eller dela upp snapshot-förberedelserna före fler shaderoptimeringar.
Nuvarande inkrementella väg jämför allt textur/NCC-minne och kopierar sedan
hela snapshoten även när få block ändrats. Ett avgränsat första försök är
att uppdatera CPU-spegeln endast för ändrade block; scan-kostnaden kvarstår
då och måste mätas separat. Write-path dirty-tracking kräver senare komplett
täckning av skrivvägar, reset och state-load. Fence-kostnaden är också stor,
men räknarnas omedelbara CPU-sidoeffekter hindrar naiv asynkron batchning.

## Artefakter

`.build-tmp/gpu-profile-{bbox1,full,bbox2,disabled,validation}.log` och
motsvarande `-final.warm.gz`. `gpu-profile-abi.log` är ett resident två-draw-
test med syntetisk texturflytt, explicit readback/reset och Vulkan-validering.
Profilering är avstängd som standard och C-ABI är fortsatt v4.

Alla fem runtime-proven matchar full-state-oraklet exakt, inklusive
profilering avstängd och Vulkan-validering på. Valideringsprovet har noll
synkfel. Batch-ABI-regression med profilering på passerar också, inklusive
det negativa testets förväntade 5 550 pixelavvikelser vid borttagna
texturkopior (`gpu-profile-batch.log`).

Normalbygget är återställt: Probe/UI bygger utan fel, elva gränstestfall,
nio limit-testfall och PCI-regressionen passerar. Normal replay ignorerar
alla GPU-/profilflaggor även med ogiltig limit och obefintligt bibliotek,
utan profilutskrifter, och matchar samma full-state-hash
(`gpu-profile-normal-final.warm.gz`). Shadern passerar `spirv-val`.
