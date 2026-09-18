# Gauntlet DL: liten snabbcache för CPU-block

## Resultat

Ingen säker fartvinst: tolv körningar gav sammanlagd median **11146,45 ms av**
mot **11116,75 ms på**, bara **0,27 %** kortare körtid. Första seriens median
förbättrades 0,76 %, andra seriens försämrades 0,46 %. Spridningen är större
än skillnaden. Flaggan lämnas av som standard; detta är ett korrekthetstestat
experiment, inte ett nytt spelbarhetslyft.

| Körning | Snabbcache | runMs |
|---|---|---:|
| 1 | av | 11134,1 |
| 2 | på | 10929,5 |
| 3 | på | 11117,2 |
| 4 | av | 11748,8 |
| 5 | av | 11158,8 |
| 6 | på | 11073,9 |
| 7 | på | 11239,7 |
| 8 | av | 11109,1 |
| 9 | av | 11853,1 |
| 10 | på | 11116,3 |
| 11 | på | 11160,7 |
| 12 | av | 11051,2 |

Alla tolv slutdumpar matchar hela den dekomprimerade referensen:
`32f8e9c49dcc5498c044bb602156ef6e82fe5dec64f27c99749f3a6fe298053a`.
Bildhash `0xe87b12da`, 41 swaps i varje körning. Det är ungefär 3,5–3,8
visade bildväxlingar/s, inte spelbart tempo; probens cirka 100–110 `fps`
räknar emulerade schemaläggningsframes och får inte förväxlas med visad FPS.

## Ändring

Ett opt-in-försök efter [stackprofilen](gauntlet-dl-cpu-stack-profile-2026-09-17.md):
256 direktmappade platser framför `Dictionary<ulong, RuntimeSafeBlock>`.
Varje plats har hela 64-bitars-PC som tagg och en blockreferens. Kollisioner
faller tillbaka på den befintliga dictionaryn; även tomma block kan cachas.
Uppslagsvägen är separerad från blockbyggandet för att kunna inlinas.

Aktivering: `EUTHERDRIVE_GAUNTDL_EXPERIMENT_RUNTIME_BLOCK_FAST_CACHE=1`.
Standard är av, utan allokering av cachetabellen. Inga GPU-flaggor ändras.

Båda befintliga entry-word-guarderna använder nu samma invalidationsmetod,
som tar bort huvudposten och rätt snabbcachepost. Reset tömmer båda nivåerna.
Detta ändrar inte den befintliga modellens begränsning: entry-word-kontrollen
är **inte** en fullständig kontroll av självmodifierande kod inne i blocket,
och tomma block har samma valideringsbeteende som tidigare.
Warm-proben laddar sitt tillstånd i en ny, redan resetad CPU; cacheinnehållet
är värdmetadata och läggs inte till i snapshotformatet.

## Regressionstester

ROM-fria tester, 20 godkända kontroller: återanvändning, kollisioner,
fullbredds-PC/alias, invalidation med kolliderande post, tomma block, PC noll,
reset av båda nivåerna, ändrat entry-word genom både batch- och compiled-guard,
samt utebliven cacheallokering när flaggan är av.

Probe och UI byggda i normal Release utan fel (befintliga projektvarningar
kvar). Normalbyggets `EUTHERDRIVE_GAUNTDL_TEST_GPU_STREAM_BOUNDARIES=normal`
passerar också: stream/pending-pixel-gränser, deferred counters, dirty writers,
runtime/batch-gränser och continuation boundary.

```sh
dotnet build tools/GauntletProbe/GauntletProbe.csproj -c Release --no-restore -m:1 /clp:ErrorsOnly
EUTHERDRIVE_GAUNTDL_TEST_BLOCK_FAST_CACHE=1 dotnet tools/GauntletProbe/bin/Release/net8.0/GauntletProbe.dll
```

## Replaymetod

Normal Release, CPU-only, `DOTNET_TieredCompilation=0`, inga stack- eller
frameprofilers. Samma binär med flaggan av/på, ordningen av/på/på/av/av/på,
sedan på/av/av/på/på/av.
6750-snapshot till frame 7950, 90000 CPU-steg per frame, via den befintliga
warm-runnern. `runMs` är den tidsatta körningen, inte laddning eller slutdump.
Andra värdjobb lämnas orörda; små skillnader behöver upprepas.

```sh
env DOTNET_TieredCompilation=0 \
  EUTHERDRIVE_GAUNTDL_EXPERIMENT_RUNTIME_BLOCK_FAST_CACHE=1 \
  EUTHERDRIVE_GAUNTDL_SAVE_FINAL_STATE=.build-tmp/block-cache-manual-final.warm.gz \
  scripts/run-gauntdl-probe-warm.sh \
  /home/nichlas/roms/MAME/Midway/Vegas/gauntd 7950 90000 \
  .build-tmp/gaunt-k2-clean2-f6750.warm.gz 6750
```

Lokala loggar/slutdumpar: `.build-tmp/block-cache-{trial}-{mode}.*`.
Dessa innehåller ROM-härlett tillstånd och checkas inte in.

## Nästa steg

Mät träffgrad/kollisioner i en separat diagnostikkörning innan cachetabellen
görs större. Om uppslagen redan träffar bra är det mer motiverat att undersöka
batch-/blockövergångar och minska återgångarna till `Step` än att fortsätta
putsa just dictionaryuppslaget. Fortsätt kräva exakt slutmaskin och separata,
oprofilerade tidsserier. Inga slutsatser om andra banor eller Android-enheter
kan dras från denna enda värdsekvens.
