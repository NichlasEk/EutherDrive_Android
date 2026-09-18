# Gauntlet DL: producentstate i FIFO-skrivvägen

## Slutsats

Båda försöken är förkastade och runtimekoden återställd till `d5817654`.
Ingen ny fartvinst eller standardflagga införs. Tidigare blockcache- och
FIFO-medlemskapsoptimeringar behålls oförändrade. Kandidaternas tillfälliga
testharness och snapshotkopplingar är också borttagna.

## Metod

Normal Release, samma binär med respektive experiment av/på, inga profilerare,
`DOTNET_TieredCompilation=0`. Replay 6750→7950, 90000 steg. CPU-blockcachen
med 4096 platser och FIFO-medlemskapsindexet är på i båda lägena. Ordningen
är av/på/på/av/av/på/på/av i varje serie. Andra värdjobb lämnas orörda; inga
egna byggen eller tunga verifieringar samtidigt med tidsserierna.

## Försök 1: en dictionarysökning i stället för två

`CollectionsMarshal.GetValueRefOrAddDefault` ersatte `TryGetValue` plus
indexer-skrivning. Referensen hölls bara i den synkrona paketbokföringen;
inga callbacks eller andra dictionarymutationer sker där.

| Par | Referens, ms | En sökning, ms |
|---|---:|---:|
| 1 | 10795,9 | 10670,9 |
| 2 | 10544,3 | 10531,9 |
| 3 | 10341,4 | 10768,4 |
| 4 | 10694,9 | 10832,1 |

Median 10619,60 → 10719,65 ms: **0,94 % långsammare**, två par åt vardera håll.
Varianten är borttagen. 32768 syntetiska skrivningar per implementation gav
identiska arrayer, producentposter, räknare och sorterade kompletta headers
för global/per-PC-state och medlemskapsindex av/på. Alla åtta fulla
sluttillstånd matchade oraklet nedan.

Artefakter: `.build-tmp/fifo-producer-{trial}-{mode}.log` och `-final.warm.gz`.

## Försök 2: direkt state för global producent

| Par | Referens, ms | Direktfält, ms |
|---|---:|---:|
| 1 | 10504,3 | 10699,4 |
| 2 | 10556,2 | 10671,5 |
| 3 | 10686,2 | 10591,0 |
| 4 | 10750,7 | 10474,4 |

Median 10621,20 → 10631,25 ms: **0,09 % långsammare**, återigen två par åt
vardera håll. Ingen stabil vinst för den ökade state-/snapshotkomplexiteten.
Alla åtta fullständiga sluttillstånd matchade samma orakel. Varianten togs bort.
Artefakter: `.build-tmp/fifo-global-{trial}-{mode}.log` och `-final.warm.gz`.

Den befintliga globala paketmodellen använder bara producentnyckel 0.
Kandidaten höll den posten direkt i ett fält; per-PC-modellen behöll
den ursprungliga dictionaryvägen. Första globala skrivningen läste eventuell
laddad post. Därefter behövdes inga dictionaryuppslag i denna del av skrivvägen.
CPU-PC-providern anropades fortfarande som tidigare. Den gemensamma kodvägen
använde en villkorlig `ref` till direktfältet eller en lokal struct; andra
kodgenereringsvarianter, exempelvis explicit lokal värdekopia, är inte testade.

Snapshotskrivning måste synkronisera fältet till den kanoniska producentposten
innan serialisering. Snapshotladdning måste invalidera fältet efter att
producenttabellen lästs in. Clear invaliderar också fältet. Formatet för
sparad state är oförändrat. Ingen ny GPU- eller avbrottssemantik införs.

16 jämförelser av komplett paketmetadata efter 32772 syntetiska skrivningar
per implementation passerade, inklusive återläsning av ändrad producentstate
i redan använda backends. Global/per-PC-state och medlemskapsindex av/på
testades. Det säkerställer också att en gammal direktpost inte återanvänds
efter restore eller återuppstår vid snapshot efter clear.

## Full-state-orakel

SHA-256 för hela den dekomprimerade sluttillståndsfilen:
`32f8e9c49dcc5498c044bb602156ef6e82fe5dec64f27c99749f3a6fe298053a`.
Bildhash `0xe87b12da`, 41 swap-kommandon. Snapshot-/ROM-data checkas inte in.

## Ny profil efter återställning

Två `dotnet-trace cpu-sampling`-prov med 4096-posters blockcache och FIFO-index
aktiverade, analyserade med befintliga `summarize-cpu-stacks.py`. Båda fulla
sluttillstånd matchar oraklet: totalt 18 exakta slutdumpar i denna omgång.
Inga hot-PC-/opcode-/regionprofiler som stänger av CPU-snabbvägar användes.

Andelar nedan är observerad stackvikt inom `RunProbeSteps` men utanför
`DecodeCommandFifoPackets`, inte exakta CPU-cykler eller hela replaytiden.
Inlining döljer arbete, och skillnaden mellan proven visar samplingens brus.

| Djupaste synliga metod | Prov 1 | Prov 2 |
|---|---:|---:|
| Step | 33,30 % | 22,07 % |
| ExecuteRuntimeSafeInstruction | 10,66 % | 13,51 % |
| RunProbeSteps | 10,62 % | 11,94 % |
| TryRunRuntimeSafeInstructionBatch | 10,42 % | 10,56 % |
| WriteFifo | 6,83 % | 7,39 % |
| Execute | 4,96 % | 7,21 % |
| Enumerable.Any | 1,81 % | 2,02 % |

Inklusive underanrop (överlappande): paketbokföringen
`TrackStandardCommandFifoPacketMapWrite` står för 3,80/4,40 %, hela
`WriteFifo` för 13,23/14,57 %. Producent-dictionaryns synliga blad är små
jämfört med CPU-dispatchen. Detta är inte en absolut gammal/ny tidsjämförelse;
de profilerade replaytiderna 12,20/11,62 s ska inte användas som benchmark.

En ny avgränsad kandidat är PC-filter med `Enumerable.Any` i FIFO-skrivvägen.
Stackföräldrarna för Any i prov 2 fördelar cirka 110 ms observerad vikt på:

- `TrackCommandFifoType4ProducerWord`: 50,94 ms.
- `TrackCommandFifoType3ProducerWord`: 47,74 ms.
- `WriteFifo`: 11,40 ms.

Koden bekräftar fångande lambdor för jämförelse av adressens låga 32 bitar.
Nästa försök: en enkel allokeringsfri arraysökning, med samma tom-lista- och
adressaliassemantik. Mät allokeringar separat och kräv oprofilerad replayvinst;
samplingprocenten är inte ett löfte om motsvarande speedup.

Artefakter: `.build-tmp/cpu-post-fifo-{1,2}.{nettrace,speedscope.json,log}`,
`-summary.json` och `-final.warm.gz`. Probe och UI byggda efter återställning.
FIFO-medlemskapstestet (10167 kontroller) och stackanalysens fem tester passerar.
