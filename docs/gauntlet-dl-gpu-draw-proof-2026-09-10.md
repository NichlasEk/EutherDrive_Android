# Gauntlet DL: GPU draw och ordnad batch, 2026-09-10

## Resultat

Offlineprovet har gått från individuella textursamplingar till hela trianglar
inom den befintliga vanliga rasterfamiljen. GPU:n beräknar coverage,
64-bitarsgradienter, perspektivdivision, per-pixel-LOD, TMU-sampling/kombination,
fog, alpha/blending samt färg- och djupskrivning.

- 16 riktiga draws i två tidsfönster: 1 044 488 rektangelpixlar, exakt färg
  och djup. Även avvisade/oförändrade pixlar jämförs.
- Fyra överlappande draws delar GPU-buffert: 163 840 pixlar, exakt slutresultat.
  Ytterligare en tvådrawsbatch: 12 545 pixlar, också exakt.
- En submit, fence och readback per batch; ingen CPU-rundtur mellan trianglar.
- Synkroniseringsvalidering: noll fel för fyrabatchen och åtta senare draws.
- Avsiktligt ändrad oracle upptäcks som exakt ett fel, även i fyrabatchen.
  Draw-oraclen laddas aldrig upp till GPU:n.
- Fel draw-ordning och ändrat textur/NCC-innehåll avvisas före GPU-körning.
- Alla tidigare 16 samplerfiler (558 872 requests) matchar fortfarande efter
  flytten till gemensam `sampling.glsl`. Båda shaders passerar `spirv-val`.

Testad maskin: RTX 4090, Linux/Vulkan. Inte verifierat på Android.

## Avgränsning

Inspelningen accepterar bara `useProfiledCommonRasterKernel`: FBZ mode
`0x000b4779`, color path `0x0c60743a`, alpha mode `0x00045119`, fog mode
`0xc1` och de befintliga vanliga TMU0-moderna. Faktiska texturformat är
1, 9 och 10; observerade TMU-moder är `0x8c22410f`, `0x8c22490f`,
`0x8c241acf`. Övriga shadergrenar är inte därmed verifierade.

Batchbyggaren kräver samma textur/NCC-innehåll och färgbuffert samt exakt
pre/post-kedja på överlappande pixlar. Initialtillståndet rekonstrueras från
varje pixels första observation. Hål i unionrektangeln är nollfylld testyta.
Detta är utvalda kompatibla draws, inte hela spelets renderingsström.
Inga texturuppdateringar, clears, swaps eller CPU-läsningar emuleras här.

## Mätning, inte spel-FPS

12 körningar per läge, tre uppvärmningar borttagna, median av nio. Separat
pipeline/device-start och kommandoinspelning ingår inte. Hosttiden inkluderar
submit, fence, device-local återställning och slutlig readback. GPU-tiden
täcker dispatch och barriärer mellan draws.

| Batch | Alla texturer uppladdade | Texturer kvar, nya draw-data | Alla indata kvar | GPU dispatch/barriärer |
| --- | ---: | ---: | ---: | ---: |
| 4 draws | 1,7867 ms | 0,3465 ms | 0,2299 ms | 0,0369 ms |
| 2 draws | 1,6275 ms | 0,0921 ms | 0,0712 ms | cirka 0,013 ms |

`draw-upload` laddar fortfarande initial framebuffer, metadata och NCC.
`resident` är en optimistisk gräns. Capturens `cpuRasterMs` är en enda live-
mätning med instrumentering/JIT/GC-effekter; ingen rättvis CPU/GPU-kvot kan
räknas från den. Detta tillför ännu ingen uppmätt förbättring av speltempo.

## Reproduktion och lokala artefakter

Se [verktygets README](../tools/GauntletGpuProbe/README.md) för byggflaggor,
inspelning, enskilda tester och batch. Första fönstret använder draw-skip 0,
det senare 120. Filerna innehåller ROM-data och ska inte committas.

- `.build-tmp/gpu-draw-first/`: åtta draws; `00,01,02,03` är fyrabatchen,
  `05,06` tvåbatchen. `03,04` avvisas för texturändring, `03,02` för fel kedja.
- `.build-tmp/gpu-draw-later/`: åtta senare draws, samtliga validerade.
- `.build-tmp/gpu-draw-{first,later}-suite.log`: omkörda GPU-resultat.
- `.build-tmp/gpu-draw-first/batch{2,4}.gpu.log`, `batch4.validation.log`.
- `.build-tmp/gpu-draw-later-validation.log`: åtta validerade draws.
- `.build-tmp/gpu-draw-later-final.warm.gz`: full slutmaskin med capture på.

Capture-replay 6750→7950 behåller hash `0xe87b12da`, PC
`0xffffffff80079e18`, FIFO `27113239/2660774`, draw `474871`, swaps `3877`.
Dekomprimerad slutmaskin: 98 901 914 byte, SHA-256
`32f8e9c49dcc5498c044bb602156ef6e82fe5dec64f27c99749f3a6fe298053a`.

Normalbygget är återställt. Probe och UI bygger utan fel. Normal replay
ger samma fullständiga slutstate (även med draw-directory satt skapas ingen
capture), sparat i `.build-tmp/gpu-draw-default-final.warm.gz`.
PCI-regressionen passerar: 40 000 avstängda trace-anrop allokerar noll byte,
och aktiverad loggning/limit/räknare fungerar. Normal replay tog 11,967 s;
det är en kontrollkörning, ingen ny verifierad prestandavinst.

## Nästa steg mot spelbart

Bygg en avgränsad renderingsström med draws, textur-dirty ranges, clears,
buffertbyten och CPU-läsbarriärer. Behåll färg/djup/texturer på GPU mellan
batcher. Jämför hela färg-/djupbuffertar vid säkra synkpunkter mot CPU-oraclen.
Först därefter aktiveras opt-in live-rendering med fallback för andra states,
och total replaytid samt verkliga swaps/sekund mäts. JIT/CPU-kostnaden kvarstår;
snabb GPU-rasterisering ensam garanterar inte spelbart tempo.
