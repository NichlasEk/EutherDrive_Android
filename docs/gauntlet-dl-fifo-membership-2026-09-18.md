# Gauntlet DL: hash-index för kompletta FIFO-paket

## Resultat

**4,21 % kortare mediantid** i åtta körningar: 11100,00 ms utan hash-index
mot 10632,85 ms med index. Medeltiden minskar 4,23 %; alla fyra växlade par
förbättras (2,45–6,52 %). CPU-blockcachen på 4096 platser är gemensam för
båda lägena, så vinsten är utöver den i just denna jämförelse. Addera inte
procenttal från olika tidsserier som en uppmätt totalvinst.

| Par | FIFO-index av, ms | FIFO-index på, ms |
|---|---:|---:|
| 1 | 11281,8 | 10688,3 |
| 2 | 11216,6 | 10485,6 |
| 3 | 10861,0 | 10577,4 |
| 4 | 10983,4 | 10714,2 |

Alla åtta fullständiga dekomprimerade sluttillstånd matchar referensen:
`32f8e9c49dcc5498c044bb602156ef6e82fe5dec64f27c99749f3a6fe298053a`.
Bildhash `0xe87b12da`, 41 swap-kommandon. Resultatet är en lokal CPU-only-
replayvinst; inga slutsatser om andra banor eller Android-enheter är verifierade.
Experimentet förblir av som standard.

## Försök

`EUTHERDRIVE_GAUNTDL_EXPERIMENT_FIFO_PACKET_MEMBERSHIP=1` lägger ett
`HashSet<int>` bredvid det befintliga `SortedSet<int>`. Hash-indexet svarar på
medlemskap och undviker trädsökningen för ägare som redan tagits bort av en
tidigare överskrivning. Det sorterade trädet behålls för ordning, intervallsökning,
diagnostik och snapshotformat. Flaggan är av som standard, utan hashallokering.

Nyckeln är hela logiska headerindexet, inte maskerat till fysisk FIFO-adress.
Add/remove/clear uppdaterar båda samlingarna. Warm-loadern bygger om det
härledda indexet från det laddade trädet, även för spårningsbackenden.
FIFO-bulk-/wrap-clear tömmer båda; Voodoo-fasadens reset skapar en ny backend.
Ingen ny gäststate eller snapshotversion införs. Befintlig avkodningsordning,
paketägande, generationer och GPU-gränser ändras inte.

## ROM-fria kontroller

10167 kontroller med index av/på: dubbla adds/removes, positiva/negativa/extrema
logiska index, fysisk aliasering mellan generationer, 2000 deterministiska
blandade operationer per läge, identisk sorterad ordning, ombyggnad från
laddad state, rensning och bulk-first-write. Återbyggnadsmetoden kontrolleras
också vara tillgänglig på spårningsbackenden.

Normal Release av Probe och UI byggd utan fel. De 45 blockcachekontrollerna
och normalbyggets GPU-stream/pending-pixel/dirty-writer/runtime-limit/
batch-limit/continuation-kontroller passerar också.

```sh
dotnet build tools/GauntletProbe/GauntletProbe.csproj -c Release --no-restore -m:1 /clp:ErrorsOnly
EUTHERDRIVE_GAUNTDL_TEST_FIFO_PACKET_MEMBERSHIP=1 dotnet tools/GauntletProbe/bin/Release/net8.0/GauntletProbe.dll
```

## Replaymetod

Normal Release, CPU-only, 6750→7950, 90000 steg, `DOTNET_TieredCompilation=0`.
Samma binär, inga profilerare. CPU-blockcachen är aktiverad med 4096 platser
i **båda** lägena. Endast FIFO-medlemskapsflaggan växlas: av/på/på/av/av/på/på/av.
Alla körningar sparar fullständig slutstate. Inga egna byggen eller tunga
kontroller körs samtidigt; andra värdjobb lämnas orörda.

```sh
env DOTNET_TieredCompilation=0 \
  EUTHERDRIVE_GAUNTDL_EXPERIMENT_RUNTIME_BLOCK_FAST_CACHE=1 \
  EUTHERDRIVE_GAUNTDL_RUNTIME_BLOCK_FAST_CACHE_SIZE=4096 \
  EUTHERDRIVE_GAUNTDL_EXPERIMENT_FIFO_PACKET_MEMBERSHIP=1 \
  EUTHERDRIVE_GAUNTDL_SAVE_FINAL_STATE=.build-tmp/fifo-membership-manual-final.warm.gz \
  scripts/run-gauntdl-probe-warm.sh \
  /home/nichlas/roms/MAME/Midway/Vegas/gauntd 7950 90000 \
  .build-tmp/gaunt-k2-clean2-f6750.warm.gz 6750
```

Lokala artefakter: `.build-tmp/fifo-membership-{trial}-{mode}.log` och
motsvarande `-final.warm.gz`. ROM-härledd state checkas inte in.

## Vad säger detta om speltempot?

Föregående 4096-postersserie gav 41 swap-kommandon på cirka 10,8–11,1 sekunder,
ungefär 3,7–3,8 swap/s. Det är en indikator från Linux-hostens replay, inte
en ny Android-mätning eller uppmätta unika presenterade bilder. Probe-`fps`
räknar interna schemaläggningsframes och är inte spel-FPS. Vi är fortfarande
långt från spelbart tempo; en förbättring på ett par procent ändrar inte det.

Med **både** blockcache och FIFO-index aktiverade gav denna serie
**3,83–3,91 swap-kommandon/s**, omkring **3,86** räknat från mediantiden.
Detta är den senaste uppmätta tempoindikatorn, inte faktisk Android-FPS.

Nästa avgränsade försök kan minska dubbeluppslagen i paketproducenternas
dictionary (`TryGetValue` följt av indexer-skrivning), med bibehållen
per-producent/global-paketsemantik. Fortsatt krav: full-state-exakthet och
växlade oprofilerade tidsprov. Större steg mot spelbarhet återstår.
