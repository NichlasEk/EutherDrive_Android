# Gauntlet DL: blockcacheträffar och större tabell

## Resultat

4096-postersvarianten ger **2,36 % kortare mediantid** i tolv oprofilerade
körningar på denna replay: 11202,40 ms av mot 10938,55 ms på. Medeltiden
minskar 2,50 %, och fem av sex närliggande av/på-par förbättras. Det är en
positiv lokal signal, inte ett bevis för alla spelsekvenser eller Android.
Flaggan förblir opt-in och standardstorleken ändras inte.

| Par | Cache av, ms | 4096 platser, ms |
|---|---:|---:|
| 1 | 11386,3 | 10750,6 |
| 2 | 11269,4 | 10777,4 |
| 3 | 11082,7 | 11115,4 |
| 4 | 11220,5 | 11118,7 |
| 5 | 11181,6 | 10847,7 |
| 6 | 11184,3 | 11029,4 |

Samma slutliga binär, profilflagga uttryckligen 0, sekventiell ordning
av/på/på/av/av/på/på/av/på/av/av/på. Inga egna byggen eller andra tunga
tester samtidigt med tidsserien; övriga värdjobb lämnades orörda.
Jämförelsen gäller ingen snabbcache mot 4096 platser, inte ett direkt
tidsprov 256 mot 4096. Profileringstiderna nedan används inte i resultatet.

Alla tolv tidsprov och tre profilprov har samma fullständiga dekomprimerade
sluttillstånd som referensen:
`32f8e9c49dcc5498c044bb602156ef6e82fe5dec64f27c99749f3a6fe298053a`.
Bildhash `0xe87b12da` och 41 swap-kommandon är oförändrade. Detta gör inte
spelet spelbart: swap-kommandon/s ligger fortfarande kring 3,7, och probens
cirka 110 `fps` är inte verklig spel-FPS.

Fortsättning på [256-postersförsöket](gauntlet-dl-block-fast-cache-2026-09-18.md).
Ny diagnostik `EUTHERDRIVE_GAUNTDL_PROFILE_RUNTIME_BLOCK_CACHE=1` räknar
uppslag, snabbträffar, kollisioner, tomma snabbträffar, dictionaryträffar,
blockbyggen och invalidationer. Den stänger inte av safe batches/branch pairs.
Räknarna återställs vid CPU-reset och ingår inte i gästens sparade tillstånd.

## Träffgrad

Samma normala CPU-only-warm-replay 6750→7950, 90000 steg, tiered compilation
av. Dessa profilerade körningars tider är **inte** optimeringsbevis.

| Räknare | Snabbcache av | 256 platser | 4096 platser |
|---|---:|---:|---:|
| Uppslag | 15447707 | 15447707 | 15447707 |
| Snabbträffar | 0 | 12153117 | 15117769 |
| Träffgrad | – | 78,67 % | 97,86 % |
| Miss med upptagen plats | 0 | 3294334 | 326374 |
| Snabbträff på tomt block | 0 | 5181631 | 6479280 |
| Dictionaryträffar | 15439688 | 3286571 | 321919 |
| Blockbyggen | 8019 | 8019 | 8019 |
| Invalidationer | 0 | 0 | 0 |

Identiteten `uppslag = snabbträffar + dictionaryträffar + blockbyggen`
stämmer i alla tre. Tomma träffar är en delmängd av snabbträffarna. Inga
invalidationer här bevisar inte att andra sekvenser saknar självmodifierande
kod. Befintliga entry-word-guards behålls.

4096 platser tar cirka 64 KiB för posterna på denna 64-bitarsvärd, mot
4 KiB för 256, och tar bort cirka 90 % av snabbcachemissarna. Kvarvarande
kostnader inkluderar fortfarande batchdispatch, entry-word-läsningar,
instruktionsexekvering och enhetsarbete.

## Konfiguration och test

Experimentet är fortfarande av som standard. När det aktiveras är den
befintliga standardstorleken 256 oförändrad. Storleken kan väljas med
`EUTHERDRIVE_GAUNTDL_RUNTIME_BLOCK_FAST_CACHE_SIZE=4096`.
Endast tvåpotenser 64–65536 accepteras; andra värden faller tillbaka på 256.
Indexmasken härleds från tabellängden i både uppslag och invalidation.

45 ROM-fria kontroller passerar: tidigare guards/reset/alias-test samt
exakt räknarsekvens, tomma träffar, avstängd profilering och storlekarna
64/4096/65536 och ogiltiga värden med kollision och invalidation.

Probe och UI byggda i normal Release utan fel; normalbyggets GPU-gräns-
och continuation-tester passerar också. Inga capture-/GPU-flaggor aktiveras.

```sh
env DOTNET_TieredCompilation=0 \
  EUTHERDRIVE_GAUNTDL_PROFILE_RUNTIME_BLOCK_CACHE=1 \
  EUTHERDRIVE_GAUNTDL_EXPERIMENT_RUNTIME_BLOCK_FAST_CACHE=1 \
  EUTHERDRIVE_GAUNTDL_RUNTIME_BLOCK_FAST_CACHE_SIZE=4096 \
  scripts/run-gauntdl-probe-warm.sh \
  /home/nichlas/roms/MAME/Midway/Vegas/gauntd 7950 90000 \
  .build-tmp/gaunt-k2-clean2-f6750.warm.gz 6750
```

För tidsprov: sätt profilflaggan till 0 och jämför experimentflaggan 0/1.
Lokala artefakter: `.build-tmp/block-cache-profile-{0,1,4096}.*` och
`.build-tmp/block-cache-large-{trial}-{mode}.*`, med `-final.warm.gz` för
slutdumpar. ROM-härledda filer checkas inte in.

## Avgränsning inför nästa CPU-försök

Återinför inte gamla C#-kedjeloopar utan nytt skäl. Tidigare tester av både
versionsvaktade länkar och write-barriärfria direktlänkar var långsammare trots
hög träffgrad, dokumenterat i
[prestandaplanen](gauntlet-dl-playable-performance-plan-2026-08-11.md).
Det här försöket ändrar bara blockuppslag, inte exekveringsordning, avbrott,
instruktionsbudget, guards eller GPU-väg.

Med 97,86 % träffar är det inte motiverat att fortsätta skala tabellen utan
nya mätningar. Nästa större kandidat är FIFO-skrivningarnas paketbokföring,
som stackprofilen pekade ut, alternativt en kompakt genererad runner för en
ny bevisat het blocksekvens. Upprepa inte redan förkastade kedjeloopar.
