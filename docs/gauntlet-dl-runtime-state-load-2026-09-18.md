# Gauntlet DL: CPU-instruktionsreferens och runtime-statusläsning

Linux, utgångspunkt `52a3cf40`, korrekt aktiva Nile-timers. Samma långa
replay-fönster, 90000 steg, cache4096/FIFO på, tiering/NCC/GPU av som i
[föregående rapport](gauntlet-dl-nile-single-tick-2026-09-18.md).
Inga egna samtidiga byggen/profilerare under tidtagning.
Fryst referens: `.build-tmp/safe-ref-baseline/`.

## Förkastat: referens i stället för instruktionskopia

Prövade `ref readonly RuntimeSafeInstruction` i safe-block-loopen.
Blockens instruktioner är oföränderliga efter konstruktion; invalidation
ersätter block i stället för att skriva över deras instruktioner.
Ändringen var state-exakt, men gav ingen stabil vinst:

| Par | Referens, ms | Referens till instruktion, ms |
|---|---:|---:|
| 1 | 11025,6 | 10894,8 |
| 2 | 10686,1 | 10852,9 |
| 3 | 10878,4 | 11075,4 |
| 4 | 11071,1 | 10759,6 |

Endast två av fyra par snabbare. Alla åtta slutdumpar matchade fe8b-oraklet.
30740 Nile-, 2528 COP1- och 45 cachekontroller passerade.
Ändringen återställdes innan nästa kandidat byggdes.
Lokala loggar/slutdumpar: `.build-tmp/safe-ref-{1..8}-{old,new}.*`.

## Förkastat: samlad runtime-statusläsning

`RuntimeMainState` läses i `Step` och vid minnesskrivningarnas side-exit-
kontroller. Ersätt fyra bytehämtningar, skiftningar och OR med
`BinaryPrimitives.ReadUInt32LittleEndian` över exakt fyra bytes RAM.
Ingen cache, ingen ändring av klocksteg eller side-exit-villkor.
Span-gränskontroll och little-endian-tolkning behålls även på annan host.

Nytt test `EUTHERDRIVE_GAUNTDL_TEST_RUNTIME_MAIN_STATE=1`: alla 256 värden
i var och en av fyra bytepositioner samt 4096 deterministiska slumpvärden.
Förväntat resultat beräknas med den gamla bytevisa expressionen; RAM och
grannbytes ska lämnas orörda. Ny direkt RAM-skrivning före varje läsning
kontrollerar att värdet inte cachas.

## Långmätning 6750→7950

| Par | Referens, ms | Samlad statusläsning, ms |
|---|---:|---:|
| 1 | 10898,3 | 10572,0 |
| 2 | 10573,9 | 10816,3 |
| 3 | 10980,1 | 10570,2 |
| 4 | 10678,1 | 10824,4 |
| Medel | 10782,600 | 10695,725 |
| Median | 10788,20 | 10694,15 |

0,81 % kortare medeltid och 0,87 % kortare median, men bara två vunna
par av fyra. Signalen är liten jämfört med variationen mellan körningarna.
Alla åtta kompletta slutdumpar matchar SHA256
`fe8b7adabe915e51785414d2c1f6e3a5ef836b9159d14062bbf7f8e2dd1fd7ce`.
Ordning gammal/ny/ny/gammal/gammal/ny/ny/gammal.
Lokala artefakter: `.build-tmp/main-state-bench.log` samt numrerade
`.build-tmp/main-state-{1..8}-{old,new}.{log,warm.gz}`.

## Fortsättningsfönster 7950→9150

Start från `.build-tmp/nile-restored-first.warm.gz`, samma parametrar,
ordning gammal/ny/ny/gammal:

| Par | Referens, ms | Samlad statusläsning, ms |
|---|---:|---:|
| 1 | 10921,0 | 11028,0 |
| 2 | 10912,2 | 10716,6 |
| Medel | 10916,60 | 10872,30 |

0,41 % kortare medeltid, men bara ett vunnet par av två. Alla fyra
slutdumpar matchar SHA256
`469865d377c99469b1133a9067435593754cde7dfa2f00e4c3770efd99ecba5a`.
Lokala loggar: `.build-tmp/main-state-next-bench.log` och tillhörande
numrerade loggar/slutdumpar. Kandidaten vinner totalt tre av sex par;
det räcker inte för att kalla detta en stabil prestandavinst.
Även denna runtime-ändring återställs. Ingen av de två CPU-kandidaterna
behålls; endast testtäckning och mätunderlag checkas in.

## Slutverifiering och fortsättning

Det nya testet körs med:

```sh
env EUTHERDRIVE_GAUNTDL_TEST_RUNTIME_MAIN_STATE=1 \
 dotnet tools/GauntletProbe/bin/Release/net8.0/GauntletProbe.dll
```

5120 statusfall, 30740 Nile-fall, 2528 COP1-fall och 45 cachefall passerar
både på kandidaten och i slutbygget efter återställning. Release Probe och
UI bygger utan fel; UI har 33 befintliga varningar. Slutloggar:
`.build-tmp/cpu-dispatch-final-{state,nile,cop1,cache}-checks.log`,
`cpu-dispatch-final-build.log` och `cpu-dispatch-final-ui-build.log`.
Slutreplay efter återställning matchar fe8b-oraklet exakt, bildhash
`0xe87b12da`, 41 swaps, 10893,4 ms (3,764 swaps/s). Artefakter:
`.build-tmp/cpu-dispatch-final.{log,warm.gz}`. Detta är ett slutprov,
inte en separat A/B-vinst. Befintliga Lua-ändringar och lokala artefakter
lämnas orörda och utanför commit.
Referens-DLL för A/B väljs med
`EUTHERDRIVE_GAUNTDL_PROBE_DLL=.build-tmp/safe-ref-baseline/GauntletProbe.dll`.
Se föregående rapport för övriga reproduktionsflaggor; välj nya filnamn.

Nästa CPU/JIT-arbete bör börja med genererad maskinkod och anropskostnader
i `Step`/safe-block-dispatch, inte fler antaganden om att en kortare
C#-expression automatiskt blir snabbare. Behåll korrekt per-instruktions-
klockning och exakt full-state-orakel. Inget i detta pass visar förbättrad
spelbarhet; runtime-koden är tillbaka på `52a3cf40`.
