# Gauntlet DL: stackprovtagning av CPU-loopen utanför FIFO

## Resultat

Två provtagningar av samma normala CPU-only-replay pekar på samma områden:
CPU-/batchdispatch, blockcache-uppslag och enhets-/FIFO-skrivningar. Ingen
runtimekod eller optimeringsflagga ändrades. Detta är en profilcheckpoint,
inte ett nytt fartlyft.

Andelar av observerad stackvikt **inom RunProbeSteps men utanför
DecodeCommandFifoPackets** (ömsesidigt uteslutande klassificering):

| Synlig stackväg | Prov 1 | Prov 2 |
|---|---:|---:|
| Övrig CPU-loop, inklusive inlinat arbete | 58,25 % | 59,15 % |
| Execute/ExecuteRuntimeSafeInstruction utan synlig minnes-/enhetsram | 17,96 % | 18,88 % |
| Synlig enhetsväg | 20,99 % | 19,86 % |
| Synlig MemoryMap-väg utan enhetsram | 2,80 % | 2,11 % |

**Detta är inte en exakt uppdelning i ren instruktionstid/RAM/MMIO.**
ReadRuntimeData8/16/32/64 är aggressivt inlinade; deras arbete kan ligga i
CPU-ramen. Frånvaro av en minnesram betyder inte frånvaro av RAM-accesser.
Den synliga minnesandelen får inte användas som hela RAM-kostnaden.

Enhetsandelen består nästan helt av VegasVoodooPciDevice.TryWriteMemory32
och dess barn. Utanför FIFO-*avkodningen* finns alltså fortfarande
FIFO-*skrivning och bokföring*. Det var inte isolerat i föregående fasprofil.

## Konkreta hotspots

Andelar för djupaste synliga metod, efter att Speedscope-markörerna CPU_TIME
och UNMANAGED_CODE_TIME tagits bort från själva metodnamnsrankningen:

| Metod | Prov 1 | Prov 2 |
|---|---:|---:|
| MipsR5000Core.Step | 18,09 % | 17,95 % |
| RunProbeSteps | 11,39 % | 10,36 % |
| TryRunRuntimeSafeInstructionBatch | 11,18 % | 11,28 % |
| ExecuteRuntimeSafeInstruction | 10,50 % | 11,17 % |
| VoodooBringupBackend.WriteFifo | 7,81 % | 7,76 % |
| Dictionary.FindValue | 5,62 % | 6,23 % |
| MipsR5000Core.Execute | 5,49 % | 5,99 % |
| SortedSet.Contains | 4,85 % | 4,09 % |

Inklusive underanrop (överlappande, ska inte adderas):

- TryRunRuntimeSafeInstructionBatch: 56,29/56,81 %.
- GetRuntimeSafeInstructionBlock: 6,46/6,92 %.
- WriteFifo: 18,53/17,17 %.
- TrackStandardCommandFifoPacketMapWrite: 6,12/4,93 %.

Kodkontroll visar Dictionary-uppslag i GetRuntimeSafeInstructionBlock och
SortedSet.Contains/Remove för gamla packet owners i packet-map-skrivningen.
Det ger två avgränsade nästa kandidater: billigare blockcache-uppslag respektive
medlemskapskontroll för FIFO-paket. Båda måste behålla invalidation, generationer,
ordning och exakt slutmaskin. Ingen sådan optimering är implementerad här.
Det större batch-/dispatchområdet återstår också; procentsiffrorna är inte
ett löfte om motsvarande möjlig speedup.

## Metod och begränsningar

Lokalt verktyg: dotnet-trace 8.0.547301, installerat i
`.build-tmp/dotnet-tools`, profil `cpu-sampling`, Speedscope-export. Microsoft
har senare bytt namn på detta slags profil eftersom den samplar trådar även
när de inte använder CPU. Vikterna är därför observerad trådtid, inte exakta
CPU-cykler eller tid exklusivt på kärnan. [Officiell verktygsdokumentation](https://learn.microsoft.com/en-us/dotnet/core/diagnostics/dotnet-trace).

Analysverktyget `tools/GauntletProbe/summarize-cpu-stacks.py`:

- tar bara stackintervall med MipsR5000Core.RunProbeSteps;
- utesluter stackar med VoodooBringupBackend.DecodeCommandFifoPackets;
- utesluter därmed snapshot-I/O, startup och andra trådar utan CPU-loopen;
- bevarar vikt för CPU_TIME/UNMANAGED_CODE_TIME separat men använder närmast
  verkliga ram för metodrankning;
- redovisar synliga vägkategorier separat från överlappande inklusive vikter;
- avvisar felaktiga/eventuellt obalanserade stackar och spår utan rätt scope.

Profileringsflaggorna för hot-PC/opcodes/runtime-regions aktiverades **inte**.
Kodkontroll av TryRunRuntimeSafeInstructionBatch visar att de skulle stänga
av den väg vi vill mäta. EventPipe-spåren visar att den faktiskt exekverar.
Ingen DisableInlining/DisableOptimizations sattes.

Capture-build behövs inte. Normal Release, tiered compilation av,
frame-phase-profilering på, samma warm replay 6750→7950 med 90000 steg:

| Körning | runMs | cpuOutsideFifoMs från frame-timers |
|---|---:|---:|
| Provtagning 1 | 12121,4 | 6034,29 |
| Provtagning 2 | 12274,7 | 6121,33 |
| Kontroll utan stackprovtagning | 11135,1 | 5660,07 |

Kontrollen och spåren kördes sekventiellt utan egna samtidiga tunga jobb.
Andra värdjobb är inte isolerade. Samplingen stör mätningen; dessa är inte
benchmarkresultat för en optimering. Observerad stackvikt utanför FIFO var
4289,765/4029,632 ms, vilket inte täcker hela timerfönstret. **Skala inte
procenten till de tidigare 5,72 sekunderna som en exakt tidsbudget.**

## Reproduktion och verifiering

```sh
env DOTNET_TieredCompilation=0 EUTHERDRIVE_GAUNTDL_PROFILE_FRAME_PHASES=1 \
  EUTHERDRIVE_GAUNTDL_SAVE_FINAL_STATE=.build-tmp/cpu-stacks-example-final.warm.gz \
  .build-tmp/dotnet-tools/dotnet-trace collect --profile cpu-sampling \
  --format Speedscope --show-child-io --output .build-tmp/cpu-stacks-example.nettrace \
  -- scripts/run-gauntdl-probe-warm.sh /home/nichlas/roms/MAME/Midway/Vegas/gauntd \
  7950 90000 .build-tmp/gaunt-k2-clean2-f6750.warm.gz 6750
python tools/GauntletProbe/summarize-cpu-stacks.py .build-tmp/cpu-stacks-example.speedscope.json
python tools/GauntletProbe/test-summarize-cpu-stacks.py
```

Alla tre fulla slutmaskiner matchar SHA-256
`32f8e9c49dcc5498c044bb602156ef6e82fe5dec64f27c99749f3a6fe298053a`, bildhash
`0xe87b12da`. Analysverktygets fem ROM-fria tester täcker scope, FIFO-filter,
markörer, bucket-prioritet, enhetskonvertering, rekursiva ramar och felaktiga
stackar/tidsstämplar. Ingen emulatorbuild behövde ändras eller återställas.

Lokala, ej incheckade artefakter:

- `.build-tmp/cpu-stacks-{1,2}.{nettrace,speedscope.json,log}`
- `.build-tmp/cpu-stacks-{1,2}-summary.json`
- `.build-tmp/cpu-stacks-{1,2,control}-final.warm.gz`
- `.build-tmp/cpu-stacks-control.log`
