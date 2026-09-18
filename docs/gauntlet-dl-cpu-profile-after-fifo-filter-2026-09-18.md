# Gauntlet DL: CPU-tidsprofil efter FIFO-PC-filterfixen

## Slutsats

Två nya profiler vid `7fd7d657` ger nästan samma fördelning. På huvudtrådens
replay-stack ligger cirka **41 % i MIPS-vägen**, **36 % i texturrasterisering**
och **17 % i övrig Voodoo/FIFO/presentation**. Detta är normal CPU-rendering,
inte en mätning av Vulkan/GPU-backenden.

Nästa större försök bör träffa en bred körväg. En ny liten, adressspecifik
MIPS-region är inte motiverad av dessa data. Den tydligaste namngivna
grafikkandidaten är två-TMU-sampling/kombinering; på CPU-sidan är det den
gemensamma Step/safe-batch/execute-vägen, inte en bevisat dyr gäst-PC-region.

## Metod och verifiering

- Normal Release, byggd med noll fel (470 varningar).
- `DOTNET_TieredCompilation=0`, cache4096 och FIFO-medlemskapsindex på.
- FIFO-PC-filterfixen ingår. Inga av de förkastade lookup-JIT-prototyperna
  eller deras hookar är kvar.
- Replay 6750→7950, 90000 steg per probe-frame, samma warm snapshot.
- `dotnet-trace collect --profile cpu-sampling --format Speedscope`.
- Inga hot-PC-/opcode-/regionräknare som stänger av CPU-snabbvägar.
- Två sekventiella prov utan samtidiga egna byggen/analyser. Övriga värdjobb
  lämnades orörda. En första provinsamling överlappade bygget och uteslöts;
  rapporten använder bara filer märkta **2 och 3**.

Båda fullständiga dekomprimerade slutdumparna matchar:
`32f8e9c49dcc5498c044bb602156ef6e82fe5dec64f27c99749f3a6fe298053a`.
Bildhash `0xe87b12da`, 41 swap-kommandon. Profilerade replaytider var
11192,4 och 11178,0 ms. Dessa tider är **inte** en oprofilerad benchmark
eller uppmätt Android-/interaktiv spelprestanda.

## Huvudtrådens replay

Endast stackar under probes `RunUntilFrame` ingår här; ROM-/snapshotladdning,
slutdump och trace-konvertering ingår inte. Samplad vikt var 11189,856
respektive 11176,752 ms. Kategorierna nedan är ömsesidigt uteslutande:
raster först, därefter övrig Voodoo, klockor, minnesväg, MIPS och övrigt.

| Synlig stackväg | Prov 2 | Prov 3 |
|---|---:|---:|
| MIPS, inklusive inlinade helpers | 41,52 % | 41,14 % |
| Texturrasterisering, inklusive väntan | 35,95 % | 36,09 % |
| Övrig Voodoo/FIFO/presentation | 16,57 % | 17,06 % |
| Övrig replay | 3,30 % | 3,30 % |
| Övrig synlig minnes-/deviceväg | 1,46 % | 1,29 % |
| Synliga CP0-/Nile-klockor | 1,21 % | 1,12 % |

Detta är **observerad stackvikt**, inte hårdvaruräknade CPU-cykler.
Inlining gör underarbete osynligt. Väntan och profileringspåverkan kan ingå.
Rasterarbetarnas vikt ska inte adderas till huvudtrådens procent: trådarna
kör parallellt, och arbetarnas väntestackar dominerar deras exporterade vikt.

Djupaste synliga metoder, med samma huvudtrådsnämnare:

| Metod | Prov 2 | Prov 3 |
|---|---:|---:|
| RasterRow | 15,79 % | 15,09 % |
| Step | 12,60 % | 12,22 % |
| SampleAndCombineTwoTmusMameFixed | 9,39 % | 9,65 % |
| ExecuteRuntimeSafeInstruction | 6,35 % | 5,58 % |
| RunProbeSteps | 6,05 % | 5,64 % |
| TryRunRuntimeSafeInstructionBatch | 5,02 % | 5,05 % |
| SampleTextureMameFixedForTmu | 4,09 % | 4,17 % |
| WriteFifo | 3,61 % | 3,44 % |
| Execute | 3,19 % | 3,75 % |

Inklusive underanrop står två-TMU-helpern för **14,81/15,19 %** av
huvudtrådens vikt. Detta överlappar textursamplern och rasterkategorin ovan.
Samma tre stora raster-/samplingmetoder syns också på rasterarbetarna.

## CPU-loopen utan FIFO-avkodning

Befintliga `summarize-cpu-stacks.py` filtrerar till `RunProbeSteps` och tar
bort stackar som innehåller `DecodeCommandFifoPackets`. Dess nämnare är
5116,627 respektive 5017,350 ms, **inte hela replayen**.

| Djupaste synliga metod | Prov 2 | Prov 3 |
|---|---:|---:|
| Step | 23,99 % | 24,14 % |
| ExecuteRuntimeSafeInstruction | 13,88 % | 12,43 % |
| RunProbeSteps | 13,24 % | 12,57 % |
| TryRunRuntimeSafeInstructionBatch | 10,97 % | 11,25 % |
| WriteFifo | 7,89 % | 7,66 % |
| Execute | 4,89 % | 5,95 % |

`Enumerable.Any` har inga observerade replay-stackar i något prov.
Det stödjer att den tidigare identifierade FIFO-filterkostnaden är borta;
avsaknad av sampling är inte i sig bevis på noll exekveringar/allokeringar.
Den tidigare riktade allokeringstesten är separat evidens.

Viktigt: `WriteFifo` inklusive underanrop är cirka 50 % av hela huvudtrådens
replay, men då ingår paketavkodning och rasterisering. Det får inte beskrivas
som 50 % paketbokföring. På samma sätt är Step-bladets vikt inte bevis för
att allt där är dispatch-overhead.

Miljövariabelläsning syns som 0,73/0,89 % av huvudtrådens vikt. En kontroll
av anropsstackarna visar att merparten kommer från probe-input/checkpoint-
hanteringen, inte från emulatorns pixelväg. Att optimera detta skulle främst
förbättra testharnessens tid, inte spelets tempo.

## Rekommenderad fortsättning

1. **Större grafikkandidat:** specialisera den vanliga två-TMU-vägen vid
   triangel-/stateval, så att konstanta diagnos-/combine-/samplingval inte
   behöver gå genom den generella pixelvägen. Koden har redan vissa
   specialiseringar och återanvänder gemensam reciprocal-W; mät en verkligt
   ny variant, inte samma optimering igen. Behåll exakt LOD, avrundning,
   filtrering, alpha/depth och en generell fallback. Kandidaten gäller
   CPU-renderern; profilen bevisar inte att GPU-offload skulle vinna.
2. **Bred CPU-kandidat:** undersök en mindre separat steady-state-`Step`
   med kall bringup-/tracekod avskild, eller en kompilerad backend som
   täcker många återkommande block. Bevara services, budget, delay slots
   och individuella klocksteg. Detta är en hypotes, inte en utlovad vinst.
3. Om nästa försök uttryckligen ska vara en **större adressspecifik JIT-region**
   behövs gäst-PC-tidsattribution med snabbvägar kvar. Dessa managed stackar
   identifierar C#-metoder, inte vilket MIPS-block som tog tiden. Tidigare
   blockfrekvenser räcker inte för att ärligt utse en viss region som dyrast.

Inga runtimeändringar gjordes i denna profilomgång.

## Artefakter och reproduktion

Lokala filer: `.build-tmp/cpu-after-pc-filter-{2,3}.nettrace`,
`.speedscope.json`, `.log`, `-summary.json`, `-replay.txt`, `-final.warm.gz`.
ROM-/snapshotdata och råa profiler checkas inte in.

Insamling per prov (välj ett nytt N för att inte skriva över en gammal fil):

```sh
env DOTNET_TieredCompilation=0 \
 EUTHERDRIVE_GAUNTDL_EXPERIMENT_RUNTIME_BLOCK_FAST_CACHE=1 \
 EUTHERDRIVE_GAUNTDL_RUNTIME_BLOCK_FAST_CACHE_SIZE=4096 \
 EUTHERDRIVE_GAUNTDL_EXPERIMENT_FIFO_PACKET_MEMBERSHIP=1 \
 EUTHERDRIVE_GAUNTDL_SAVE_FINAL_STATE=.build-tmp/cpu-after-pc-filter-N-final.warm.gz \
 .build-tmp/perf-tools/dotnet-trace collect --profile cpu-sampling \
 --format Speedscope --show-child-io \
 -o .build-tmp/cpu-after-pc-filter-N.nettrace -- \
 scripts/run-gauntdl-probe-warm.sh /home/nichlas/roms/MAME/Midway/Vegas/gauntd \
 7950 90000 .build-tmp/gaunt-k2-clean2-f6750.warm.gz 6750
```

CPU-undergruppen analyseras med `tools/GauntletProbe/summarize-cpu-stacks.py`;
dess fem tester passerade. Huvudtrådstabellen använder den befintliga lokala
`.build-tmp/analyze-gauntdl-time-profile.py` och samma Speedscope-filer.
De exporterade profilerna använder millisekunder. Inga nätkällor behövdes.
