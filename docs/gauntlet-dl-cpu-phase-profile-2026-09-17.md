# Gauntlet DL: åter till CPU-/rasterkostnaden

Det verifierade GPU-arbetet publicerades i `3eaf4caf`. Fortsättningen här
ändrar inte emulatorns beteende: den använder befintliga fas-timrar och
en EventPipe-samplingsprofil för att välja nästa optimeringsmål.

## Samma replay, två vägar

6750→7950, 90 000 CPU-steg per probe-anrop, samma warm-snapshot,
`DOTNET_TieredCompilation=0`, `EUTHERDRIVE_GAUNTDL_PROFILE_FRAME_PHASES=1`.
GPU-provet har dirty/resident/incremental/sparse/bbox på, limit 4 096 och
native-profilering på, utan Vulkan-validering. Båda ger 1 200 fasrader.

| Ackumulerad tid | CPU-normalbygge | Dirty GPU, capture-build |
|---|---:|---:|
| Callbacks | 692 ms | 690 ms |
| CPU-fas, inklusive synkrona Voodoo-anrop | 10 352 ms | 12 174 ms |
| Enhetssteg | 1 ms | 1 ms |
| Presentation/render | 240 ms | 234 ms |
| FIFO-decode, nästlad | 4 600 ms | 6 359 ms |
| Type3 push, nästlad | 3 979 ms | 5 723 ms |
| Texturrasterisering, nästlad | 3 689 ms | 5 404 ms |
| Replaytid | 11,420 s | 13,332 s |
| Swaps/s | 3,590 | 3,075 |

De tre nästlade Voodoo-raderna får **inte** adderas till CPU-fasen eller
varandra. Summan av de fyra toppfaserna är 11,285 respektive 13,099 s;
loggning och annat omätt arbete förklarar en del av skillnaden mot replaytid.
Källtiderna avrundas till 0,01 ms per fasrad. Enhetsstegets små avrundade
summa betyder inte bokstavligen noll arbete. Proven är instrumenterade och
är inte en bred, växlad prestandabenchmark.

Den extra tiden för GPU-vägen återfinns främst i texturrasterintervallet.
Presentation är inte huvudproblemet här. CPU-fasen inkluderar rasterisering
och ska därför inte likställas med enbart MIPS-instruktionsexekvering.

## Samplingsprofil av normalbygget

Repo-lokalt `dotnet-trace` 8.0.547301 samlade `cpu-sampling` till NetTrace och
Speedscope under samma replay, inklusive processens startup och slutdump.
Sammanställningen filtrerar till stackar inne i
`GauntletDarkLegacyMachine.RunFrame`, och tillskriver intervallen närmaste
symboliserade metod under eventuella `CPU_TIME`/`UNMANAGED_CODE_TIME`-markörer.
Den filtrerade huvudtrådsstackens sammanlagda intervall är cirka 12 205 ms.

| Närmaste symboliserade metod | Andel av filtrerade huvudtrådsintervall |
|---|---:|
| RasterRow i FillTexturedTriangle | 14,51 % |
| MipsR5000Core.Step | 10,38 % |
| SampleAndCombineTwoTmusMameFixed | 9,00 % |
| ExecuteRuntimeSafeInstruction | 5,44 % |
| RunProbeSteps | 5,32 % |
| TryRunRuntimeSafeInstructionBatch | 5,27 % |
| Execute | 3,85 % |
| WriteFifo | 3,78 % |

Det här är kandidater för fördjupning, inte exakta isolerade CPU-tider.
Inlining, väntan och native-arbete kan hamna på managed-anroparen. Rasterarbete
på workertrådar ingår inte i tabellen. Den ofiltrerade topN-rapporten tillskriver
78 % till `DynamicRasterWorkerPool.WorkerLoop`, vars väntväg använder
`Monitor.Wait`; det är inte evidens för att 78 % av huvudtrådens arbete är
aktiv spin. Gzip/snapshot-load/save syns också i ofiltrerad rapport men ligger
utanför den filtrerade replay-stackens intervall.

Nästa avgränsade mål är att undersöka overheaden i `Step` och safe-instruction-
dispatch, eller den återstående CPU-rasterloopen, med samma exact-state-orakel.
Stora inclusive-tal i safe-block-funktioner inkluderar Voodoo-skrivningar och
får inte användas som bevis för motsvarande ren JIT-kostnad. Tidigare avvisade
linked-cache/generated-runner-försök ska inte återintroduceras utan ny evidens.

## Reproduktion

```sh
python3 tools/GauntletProbe/summarize-frame-phases.py \
  .build-tmp/gaunt-cpu-phase.log .build-tmp/gaunt-dirty-phase.log
python3 tools/GauntletProbe/summarize-replay-stacks.py \
  .build-tmp/gaunt-cpu-sampled.speedscope.json
python3 tools/GauntletProbe/test-summarize-frame-phases.py
python3 tools/GauntletProbe/test-summarize-replay-stacks.py
```

Lokalt installerades verktyget med
`dotnet tool install dotnet-trace --tool-path .build-tmp/perf-tools --version 8.0.547301`.
Insamlingen använder `collect --profile cpu-sampling --format Speedscope
--show-child-io --output .build-tmp/gaunt-cpu-sampled.nettrace --`
följt av det vanliga warm-probe-kommandot med ROM/snapshot-argument.
Verktyget, profileringsdata och ROM-/snapshotfiler är inte versionshanterade.

CPU-fasprovet, GPU-fasprovet och det samplade CPU-provet matchar alla samma
fullständiga slutmaskin: 98 901 914 dekomprimerade byte, SHA-256
`32f8e9c49dcc5498c044bb602156ef6e82fe5dec64f27c99749f3a6fe298053a`.
State-filer: `.build-tmp/{gaunt-cpu-phase,gaunt-dirty-phase,gaunt-cpu-sampled}-final.warm.gz`.
Normalbygget är återställt, Probe/UI bygger utan fel och gräns-, dirty-writer-,
limit- och PCI-tester passerar. Sammanställningsskripten har åtta godkända tester.
