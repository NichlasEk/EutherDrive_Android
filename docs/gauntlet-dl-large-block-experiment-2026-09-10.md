# Gauntlet: större genererade block, förkastat försök

## Resultat

En ny profil med ordinarie safe batches aktiva valde två större block i samma
funktion: `0xffffffff801069f4` (29 instruktioner, 49 804 profilentryn) och
`0xffffffff80106b1c` (38 instruktioner, 24 903 profilentryn). Detta är en
rangordning efter exekverat instruktionsantal, inte uppmätt CPU-tid per block.
Profilen innehöll totalt 57 308 389 safe-block-instruktioner.

En separat IL-generator emitterade blockens heltalsoperationer, skift,
registerflyttar, loads och stores direkt. Registret noll, CP0/Nile-tid,
instruktionsräknare, PC och store-side-exits behölls per instruktion.
Skrivningar använde samma audio-/Glide-/minneshelpers som safe-tolken.
Branch och delay slot lämnades till den befintliga branchparsvägen/dispatchen.
Den gamla expression-backenden användes inte.

Generatorn körde 74 656 block och 2 388 989 instruktioner i långprovet.
Efter två inledande kontrollkörningar gav fyra ordningsbalanserade par:

| Variant | Medel | Median |
| --- | ---: | ---: |
| Safe batches | 11 350,5 ms | 11 275,15 ms |
| Genererade större block | 11 569,625 ms | 11 501,95 ms |

Kandidaten var 1,93 procent långsammare i medel och vann bara ett av fyra par.
Den är borttagen ur de aktiva projekten, inklusive experimentflagga och
diagnostikutskrift. Den tidigare kompakta enblocks-generatorn är kvar.
Ingen commit eller push gjordes.

Efter borttagningen passerade Release-byggen av Core/probe/UI med noll fel
och det korta replayprovet matchade åter tidigare PC, bildhash och
grafikräknare. Logg: `.build-tmp/generated-large-20260910-restored-short.log`.

## Korrekthet och starkare referenskontroll

Kortprovet gav exakt tidigare hash `0x40bd6aae`, PC `0xffffffff80119038`,
FIFO `25628590/2486968`, draw `439076` och swaps `3847`.

Alla tio långprov gav hash `0xe87b12da`, PC `0xffffffff80079e18`,
FIFO `27113239/2660774`, draw `474871` och swaps `3877`.

Därefter sparades ett fullständigt warm-state efter separata långa
referens- och kandidatprov. Båda de dekomprimerade snapshotfilerna är
98 901 914 byte och har samma SHA-256:

`32f8e9c49dcc5498c044bb602156ef6e82fe5dec64f27c99749f3a6fe298053a`

Det bevisar byte-exakt likhet för allt tillstånd som snapshotformat 18 sparar,
inklusive CPU-register, CP0/FPR, RAM och dess sparade enhetstillstånd, i detta
replay. Det är inte ett generellt bevis för alla indata, all självmodifierande
kod eller interaktiv spelbarhet. Snapshotprovens tider ingår inte i den
förutbestämda balanserade mätserien.

## Artefakter och reproduktion

Warm-start: `.build-tmp/gaunt-k2-clean2-f6750.warm.gz`, SHA-256
`312ef133ae70d40e2c437772ea79f96884d0eee687daabd4776ce9c166494df2`.
Körparametrar: start 6750, mål 7950, 90 000 CPU-steg per anrop.
Kortprovet slutade vid 7050.

- Profil: `.build-tmp/generated-large-20260910-profile.log`
- Kod/RAM-underlag: `.build-tmp/generated-large-20260910-ram.bin`
- Arkiverad kandidat: `.build-tmp/generated-large-20260910-candidate.cs`
- Kortprov: `.build-tmp/generated-large-20260910-short.log`
- Långprov: `.build-tmp/generated-large-20260910-long-{1..10}-{safe|large}.log`
- Snapshotfiler: `.build-tmp/generated-large-20260910-{safe|candidate}-final.warm.gz`
- Snapshotloggar: `.build-tmp/generated-large-20260910-{safe|candidate}-state.log`

Långseriens ordning: safe/large/safe/large/large/safe/safe/large/large/safe.
Körning 1–2 var inledande kontroller; 3–10 utgjorde fyra jämförelsepar.
Core-DLL under experimentet hade SHA-256
`99cedac6c18392467fdcf67936b09d894985858d1c0943067ab3a31b19c089bb`.
Den arkiverade koden behöver återkopplas till safe-block-dispatch och probe
för att köras; den utgör inte en installerad eller aktiverad backend.

Profilen kan upprepas på den återställda koden:

```sh
EUTHERDRIVE_GAUNTDL_PROFILE_RUNTIME_SAFE_BLOCKS=1 \
EUTHERDRIVE_GAUNTDL_PROFILE_RUNTIME_BLOCK_TRANSITIONS=1 \
scripts/run-gauntdl-probe-warm.sh /home/nichlas/roms/MAME/Midway/Vegas/gauntd \
7950 90000 .build-tmp/gaunt-k2-clean2-f6750.warm.gz 6750
```

## Nästa beslut

Enbart större block och borttagen opcode-dispatch gav inte en nettovinst med
den här utformningen. Blocken täcker ungefär 4,2 procent av de profilerade
safe-instruktionerna; instruktionsandel är dessutom inte samma sak som
tidsandel. Välj därför nästa arkitektur utifrån uppmätt tidsfördelning mellan
CPU-dispatch, register/minneshelpers, klockbokföring och raster, inte enbart
blocklängd eller entryräknare. Försöket isolerar inte vilken av dessa
kostnader som orsakade regressionen och motiverar inte att återaktivera den
gamla expression-backenden.
