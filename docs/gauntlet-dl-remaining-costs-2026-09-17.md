# Gauntlet DL: återstående CPU- och GPU-förberedelsekostnader

## Slutsats

Profilen pekar på ett större nästa mål än fler små tile-varianter: cirka
5,72 sekunder i CPU-fasen **utanför FIFO-avkodningen** i både CPU- och
GPU-provet. Detta är inte bevis på ren JIT-tid; minnesaccesser, andra enheter
och övrig CPU-exekvering ingår. Nästa instrumentering bör skilja dessa åt
utan att avaktivera befintliga CPU-snabbvägar.

GPU-hookens kontroller kostar samtidigt cirka 468 ms i CPU-only-capture
trots att ingen GPU-session skapas. Miljövariabelläsningar/tillåtelsekontroller
är därför ett separat möjligt mål, men ingen cache-optimering är gjord här.
Detta gäller capture-bygget: hooken och dess argument är bortkompilerade i
normalbygget. Kostnaden ska inte tillskrivas vanlig CPU-only-körning.

## Ny diagnostik

- `EUTHERDRIVE_GAUNTDL_GPU_BACKEND_PROFILE=1`: aggregate BeginGpuDrawCapture-
  tid och antal, plus det återstående CPU-rasterfönstrets tid och antal.
  Inga loggrader per draw. Begin-timern följer samtliga returer men omfattar
  inte argumentallokeringar före anropet. Den inkluderar native Render,
  GPU-init och gränsarbete som anropas där. Rastertimern börjar omedelbart
  före CPU-rastervägen, inkluderar worker-väntan och stannar före End-hookens
  oracle/loggar. GPU-ersatta draws räknas inte som CPU-raster.
- Probe skriver sammanfattningen vid mätperiodens slut även utan GPU-session.
  Dubbel Close skriver inte om redan rapporterade backend-summor.
- Befintlig `EUTHERDRIVE_GAUNTDL_PROFILE_FRAME_PHASES=1` får `cpuFifoMs`
  genom differens på FIFO-ticks vid CPU-fasens början/slut, samt
  `cpuOutsideFifoMs`. Hela framens `fifoDecodeMs` kan innehålla arbete före/
  efter CPU-fasen och får därför inte subtraheras direkt från `cpuMs`.

Profiler är opt-in. Ingen runtime-optimering, shaderändring eller ändring
av GPU-defaults införs i denna checkpoint. Begin-/rasterprofilerna kräver
capture-build; de normala frame-phase-mätarna finns också i normalbygget.

## Slutlig fasjämförelse, summerade millisekunder över 1200 frames

Samma capture-build, CPU-only respektive resident nivå-3-GPU med grova
tile-epoker. Listor, masker, batchåteranvändning och grupperad dirty-kontroll
är av. `DOTNET_TieredCompilation=0`. Båda nya profiler är på; GPU-provet
har dessutom native/session-profilering på. Inga egna samtidiga byggen
eller tester. Detta är kostnadsprofilering, inte ett fartbenchmark.

| Fönster | CPU-only capture | GPU med tiles |
|---|---:|---:|
| CPU-fas, inklusive FIFO | 10878,10 | 11642,50 |
| FIFO inom CPU-fasen | 5150,43 | 5918,83 |
| CPU-fas utanför FIFO | 5727,52 | 5723,60 |
| Hela framens FIFO | 5179,50 | 5946,69 |
| Texturerad rasterfunktion, inklusive hooks | 4276,52 | 5038,67 |
| BeginGpuDrawCapture, inkluderande | 468,160 | 1673,911 |
| CPU-rasterfönster | 3405,817 | 2453,544 |
| Begin-anrop | 39237 | 39237 |
| CPU-rasteranrop | 39237 | 22064 |
| Frame callbacks | 686,16 | 675,42 |
| Enhetsfas | 1,33 | 1,25 |
| Presentation/renderfas | 239,07 | 241,07 |

Tabellens tider överlappar! Rasterfunktionen ingår i FIFO, och Begin/
CPU-rasterfönstret ingår i rasterfunktionen. Native/session-tider ingår i
GPU-Begin och/eller andra raster-/boundary-anrop. Summera inte tabellen.
Frame-profilerna avrundar varje rad till hundradels ms, så delsummor kan
skilja något efter summering.

GPU-provet ersätter 17173 draws och sparar ungefär 0,95 s i CPU-rasterfönstret,
men bredare raster-/FIFO-tid ökar. Det visar varför GPU-delkostnader inte
ensamma räcker som framgångsmått. Det isolerar inte vilken enskild GPU-
förberedelse eller väntsituation som orsakar skillnaden.

RunMs i dessa två profilprov: 11937,8 respektive 12785,5. GPU native-init
var 287,188 ms, managed Render 622,300 ms, managed Flush 614,461 ms och
pixelåterkopiering 98,176 ms (överlappande med tabellen). Inga speedup-
eller spelbarhetsanspråk dras ur dessa enstaka profilerade prov.

## Verifiering och artefakter

Slutliga fasprov: `.build-tmp/gpu-rest-{cpu-phase,tile-phase}{.log,-final.warm.gz}`.
Föregående profilpass: `gpu-rest-cpu`, `gpu-rest-capture-cpu`,
`gpu-rest-capture-cpu-v2`, `gpu-rest-tile` med samma suffix. Första capture-
CPU-passet saknar backend-sammanfattning: slut-hooken kördes då bara vid
aktiv GPU-session; Probe är nu korrigerad för CPU-only-profilering.

Alla åtta slutmaskiner matchade det etablerade oraklet:
`32f8e9c49dcc5498c044bb602156ef6e82fe5dec64f27c99749f3a6fe298053a`.
Bildhash `0xe87b12da`. Filerna är lokala och checkas inte in.

`gpu-rest-off` testar att profilerna är tysta när de är av. `gpu-rest-normal`
kör återställt normalbygge med profilerings-/GPU-flaggor och obefintligt
nativebibliotek: GPU-backend-hooken ska fortfarande inte exekveras.

Båda kontrollerna passerade: inga profileringsrader med profilerna av och
ingen backend-/native-GPU-logg i normalbygget. Probe/UI bygger utan fel;
capture-/normal-gränstester och normala färgtester passerar. 3600 slutliga
frame-rader verifierades: cpuFifoMs + cpuOutsideFifoMs matchar cpuMs inom
avrundningen, och ingen cpuOutsideFifoMs är negativ.

Nästa riktade steg: mät CPU-instruktionsdispatch och minnes-/MMIO-vägar inom
de 5,72 sekunderna, innan ytterligare shader-specialisering. Som ett separat
GPU-mål kan cache av experimentkonfiguration provas mot hookens 468 ms,
med oförändrade tillåtelsevillkor och exakt slutmaskin.
