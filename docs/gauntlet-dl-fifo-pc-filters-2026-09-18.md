# Gauntlet DL: allokeringsfria FIFO-PC-filter

## Resultat

**5,71 % kortare mediantid**, 10607,05 → 10001,35 ms, i åtta växlade körningar.
Alla fyra par förbättrades. Ändringen behålls som vanlig runtimekod; inga
nya flaggor behövs och tidigare experimentflaggors standardvärden ändras inte.

| Par | Gammalt bygge, ms | Nytt bygge, ms |
|---|---:|---:|
| 1 | 10626,2 | 10019,4 |
| 2 | 10981,3 | 10007,2 |
| 3 | 10552,8 | 9995,5 |
| 4 | 10587,9 | 9994,6 |

Alla åtta dekomprimerade sluttillstånd matchar hela referensen:
`32f8e9c49dcc5498c044bb602156ef6e82fe5dec64f27c99749f3a6fe298053a`.
Bildhash `0xe87b12da`, 41 swap-kommandon. Med de tidigare optimeringarna
aktiverade enligt metoden nedan är tempoindikatorn nu **cirka 4,10 swap/s**.
Det är inte uppmätta unika spelbilder eller Android-FPS, och fortfarande
långt från spelbart tempo. Detta är en lokal replayvinst, inte en garanti
för andra sekvenser eller enheter. Procenttal från olika mätserier summeras inte.

## Ändring

Tre fångande `Enumerable.Any`-predikat ersätts av samma lilla arraysökning:
defer-decode i `WriteFifo` och explicit header-PC i type-3/type-4-producenterna.
Jämförelsen använder fortsatt adressens låga 32 bitar. PC-providern anropas
på samma plats och samma antal gånger. Tom lista betyder fortsatt falskt
för defer-decode, men wildcard för header-PC (befintlig `Length == 0 ||`
behålls). Inga FIFO-state-, snapshot-, ägar-, generations- eller GPU-regler
ändras. Det är en direkt refaktorering utan ytterligare experimentflagga.

## Regression och allokering

`EUTHERDRIVE_GAUNTDL_TEST_FIFO_PC_FILTER=1` kör 2035 jämförelser mot det gamla
predikatet: tomt/enstaka/flera element, missar, dubbletter och 64-bitarsadresser
med samma låga 32 bitar samt deterministiska slumpfall.

Separat allokeringsprov, 100000 uppvärmda sökningar med lika många träffar och
missar: gamla predikatet **12000000 byte**, nya loopen **0 byte** på denna
.NET-värd. Delegater binds före mätningen. Detta är ett syntetiskt prov, inte
ett mått på hela spelets allokeringar eller en uppskattning av total speedup.

Verkliga type-3/type-4-anropare mäts också med tom respektive ifylld PC-lista:
20000 anrop per fall ger **0 byte**, med växlande matchande/icke-matchande PC.
Slutlig body-/packet-end-metadata kontrolleras också: tomma headerfilter
måste fortfarande fungera som wildcard, inte som ett filter utan träffar.

```sh
dotnet build tools/GauntletProbe/GauntletProbe.csproj -c Release --no-restore -m:1 /clp:ErrorsOnly
EUTHERDRIVE_GAUNTDL_TEST_FIFO_PC_FILTER=1 dotnet tools/GauntletProbe/bin/Release/net8.0/GauntletProbe.dll
```

## Replaymetod

Gammal komplett normal Release-output fryst före ändringen i
`.build-tmp/fifo-pc-baseline-LDX13m`. Nytt bygge i Probe-utdatakatalogen.
`EUTHERDRIVE_GAUNTDL_PROBE_DLL` väljer mellan dem. Inga profilerare, samma
warm-replay 6750→7950, 90000 steg och `DOTNET_TieredCompilation=0`.
4096-posters blockcache och FIFO-medlemskapsindex aktiverade i båda fallen.
Ordning gammal/ny/ny/gammal/gammal/ny/ny/gammal, inga samtidiga egna byggen
eller tunga kontroller. Andra värdjobb lämnas orörda.

Loggar: `.build-tmp/fifo-pc-{trial}-{old,new}.log`; slutdumpar med suffix
`-final.warm.gz`. ROM-härledda artefakter checkas inte in.

## Slutverifiering

- Normal Release av Probe och UI byggd utan fel.
- PC-filterkontroller och allokeringsprov passerar även efter sista teständringen.
- FIFO-medlemskapets 10167 kontroller, blockcachens 45 kontroller och normalbyggets
  GPU-stream/pending-pixel/dirty-writer/runtime-limit/batch-limit/continuation-tester passerar.
- En extra replay med både blockcache och FIFO-medlemskapsindex uttryckligen av
  matchar samma full-state-orakel: `.build-tmp/fifo-pc-default.log` och
  `fifo-pc-default-final.warm.gz`. Totalt nio exakta sluttillstånd. Denna extra
  kontroll är inte en separat A/B-tidsserie för standardläget.
