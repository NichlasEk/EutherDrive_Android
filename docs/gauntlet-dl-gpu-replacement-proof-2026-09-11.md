# Gauntlet DL: verklig GPU-ersättning, 2026-09-11

## Resultat

Ett separat opt-in-läge ersätter nu CPU-pixelloopen för den verifierade
common-state-familjen. GPU:n producerar färg/djup som kopieras till emulatorns
riktiga buffertar. CPU:n kör inte samma pixelloop i ersättningsläget.

- Första fönstret: 128 ersatta draws, segment 26/86/16.
- Senare fönster, skip 120: 128 ersatta draws, segment 4/10/10/53/10/41.
- Båda återgår till CPU för andra states och efter testgränsen 128 draws.
- Båda behåller byte-exakt full slutmaskin, inte bara samma skärmbild.
- Vulkan-synkroniseringsvalidering rapporterar noll fel.

Loggar: `.build-tmp/gpu-replace.log`, `gpu-replace-later.log`.
`gpu-replace-verified.log` är slutlig omkörning av första fönstret med explicit
kontroll av täckningsreturvärdet och `cpuRasterSkipped=true rasterCounters=PASS`.

## Varför detta först är synkront per draw

CPU-loopen producerar mer än pixlar. Dess täckningsreturvärde styr fortsatt
CPU-rendering, och LFB-/buffertaktivitet används av andra delar av backend.
En köad GPU-draw kan inte lämna dessa värden för sent eller gissa dem.

ABI v3 och shadern tillför 16 statistikord efter färg/djupbufferten. Atomiska
GPU-räknare ger antal samples efter depth-test, nolltexlar, fallback-pixlar,
färgskrivningar, färdigbehandlade common-pixlar och nio LOD-räknare.
CPU-sidan återskapar även LFB-/buffertskrivräknare och täckningsreturvärdet.
CPU-setup, bounding-box-räknare och efterbehandling körs som tidigare.

Räknarna verifierades oberoende i per-draw shadow-läge i båda tidsfönstren:
GPU-färg/djup och CPU-räknardeltan matchar för 256 draws. Slutlig shadow-
omkörning kontrollerar dessutom `coveredAny`, `coveredPixels` och `zeroPixels`.
Profilering/spårning som kräver per-pixel CPU-sidoeffekter väljs bort från
GPU-vägen. Normala common-state-räknare behålls även när CPU-loopen hoppas över.

## Ingen påvisad fartökning ännu

Ersättningen är avsiktligt **inte** den köade shadow-vägen från förra steget.
Det är 128 inlämningar/fence-väntningar och drygt 1 GiB readback per fönster,
plus full texturuppladdning och CPU-kopiering av färg/djup efter varje draw.
GPU-räknarnas atomiska operationer har också en kostnad. De enskilda långa
replayerna ligger runt 12,7–13,0 s och visar inte någon säker hastighetsvinst.
Detta är ett korrekt ersättningsprov, inte ett spelbart-tempo-resultat.

Nästa prestandagräns är att minska texturöverföringarna och endast läsa tillbaka
de omedelbart nödvändiga räknarna per draw, medan färg/djup kan ligga kvar på
GPU fram till verkliga CPU-läs-/fallback-/presentationsgränser. Det kräver
explicit hantering av när CPU-buffertarna är aktuella. Shadow-vägarna ska
fortsätta vara kontrollreferens. Först därefter är större testtäckning och
verkliga swaps/sekund ett meningsfullt spelbarhetsmått.

## Tester och artefakter

- CPU-räknarorakel: `.build-tmp/gpu-replace-counters.log`,
  `gpu-replace-counters-later.log`, `gpu-replace-coverage-shadow.log`.
- GPU-ersättningens fulla slutmaskiner: `.build-tmp/gpu-replace-final.warm.gz`,
  `gpu-replace-later-final.warm.gz`, `gpu-replace-verified-final.warm.gz`.
- Batch-ABI-regression: `.build-tmp/gpu-replace-batch-abi.log`;
  texturflytt matchar exakt, utelämnade kopior ger 5 550 avvikelser.
- Offline draw-regression: `.build-tmp/gpu-replace-offline.log`.
- Negativt räknarorakel: `.build-tmp/gpu-replace-counter-negative.log`.

6750→7950: framehash `0xe87b12da`, PC `0xffffffff80079e18`, FIFO
`27113239/2660774`, draw `474871`, swaps `3877`. Dekomprimerad slutmaskin:
98 901 914 byte, SHA-256
`32f8e9c49dcc5498c044bb602156ef6e82fe5dec64f27c99749f3a6fe298053a`.

Det negativa räknartestet upptäcker `index=0 cpu=4931 gpu=4930` och avbryter.
Normalbygget är återställt: Probe/UI bygger utan fel, elva gränstestfall och
PCI-regressionen passerar. Normal replay ignorerar ersättningsflaggan även
med en obefintlig biblioteksadress och ger samma fullständiga slutstate
(`.build-tmp/gpu-replace-normal-final.warm.gz`). Slutlig ersättningsomkörnings
full-state-hash är också verifierad. Shadern passerar `spirv-val`.

Bygg med `-p:GauntletGpuCapture=true` och använd
`EUTHERDRIVE_GAUNTDL_GPU_REPLACE=1`, utan batchflagga. Fullständiga kommandon
finns i [verktygets README](../tools/GauntletGpuProbe/README.md#bounded-gpu-replacement-experiment).
Normala builds kompilerar bort ersättningsgrenen. Endast Linux/RTX 4090 är
testat; Android och andra rasterfamiljer är inte därmed verifierade.
