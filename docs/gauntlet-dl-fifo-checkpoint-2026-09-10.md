# Gauntlet: behållen optimering av FIFO-bokföring

## Ändring

`TrackStandardCommandFifoPacketMapWrite` kontrollerar nu om det tidigare
pakethuvudet finns kvar i `_cmdFifoCompletePacketHeaders` innan `Remove`
anropas. Flera FIFO-ord kan tillhöra samma tidigare paket; ett senare ord kan
därför hänvisa till ett huvud som redan tagits bort. En medlemskontroll
undviker borttagningsvägen i det fallet, till priset av ett extra uppslag när
huvudet faktiskt finns kvar. Mätningen avgör nettovinsten för denna workload.

Ändringen tillför ingen cache eller nytt maskintillstånd och ändrar inte vilka
huvuden som är med i mängden, deras sorteringsordning eller FIFO-paketens
innehåll. Den är nu vanlig kod, utan experimentflagga. Tidigare JIT-arbete
och övriga ändringar i arbetskopian är bevarade. Ingen commit/push gjordes.

## Balanserad jämförelse

Samma f6750-warm-state som tidigare, 1 200 anrop till f7950 med 90 000
CPU-steg/anrop. Två inledande kontrollkörningar följdes av fyra balanserade
par i ordningen bas/kandidat, kandidat/bas, bas/kandidat, kandidat/bas:

| Par | Bas | Kandidat |
| --- | ---: | ---: |
| 1 | 11 709,9 ms | 11 654,4 ms |
| 2 | 11 262,9 ms | 11 123,2 ms |
| 3 | 11 470,2 ms | 11 129,6 ms |
| 4 | 11 368,6 ms | 11 360,3 ms |
| Medel | 11 452,9 ms | 11 316,875 ms |
| Median | 11 419,4 ms | 11 244,95 ms |

Kandidaten vann alla fyra par. Medeltiden minskade med 1,19 procent, cirka
136 ms per långprov. Sista paret var nästan neutralt; detta är en liten
uppmätt förbättring, inte en garanti om samma procenttal på alla maskiner.
Den gör inte spelet spelbart i sig.

## Korrekthet

Kortprov från det färdigbyggda experimentet: hash `0x40bd6aae`, PC
`0xffffffff80119038`, FIFO `25628590/2486968`, draw `439076`, swaps `3847`.

Alla tio långkörningar: hash `0xe87b12da`, PC `0xffffffff80079e18`, FIFO
`27113239/2660774`, draw `474871`, swaps `3877`.

Separata långa bas-/kandidatprov sparade fullständiga snapshots. Båda är
98 901 914 byte efter dekomprimering med SHA-256:

`32f8e9c49dcc5498c044bb602156ef6e82fe5dec64f27c99749f3a6fe298053a`

Allt tillstånd som warm-format 18 sparar matchar alltså byte-exakt i detta
replay. Det är inte ett nytt bevis för interaktiv input, ljud eller Android-
prestanda; inga sådana ändringar ingår.

Slutkoden utan experimentflagga byggdes i Release för Core, GauntletProbe och
UI med noll fel. Ett ytterligare långt standardprov behöll både hela oraklet
och samma fullständiga snapshot-SHA-256 ovan. Slutlogg och snapshot finns som
`.build-tmp/gauntdl-fifo-guard-20260910-final.log` respektive `*-final.warm.gz`.

## Artefakter

Under `.build-tmp/`:

- `gauntdl-fifo-guard-20260910-short-final.log`: det giltiga kortprovet efter build.
- `gauntdl-fifo-guard-20260910-long-{1..10}-{base|guard}.log`.
- `gauntdl-fifo-guard-20260910-{base|guard}.warm.gz` och motsvarande `*-state.log`.
- `gauntdl-fifo-guard-20260910-ab-source.cs`: arbetskopian under A/B-proven.

A/B-kopian hade flaggan
`EUTHERDRIVE_GAUNTDL_EXPERIMENT_FIFO_GUARD_ABSENT_HEADER_REMOVAL=0/1`.
Den finns inte i slutkoden; kontrollen görs alltid. Mätningens Core-DLL hade
SHA-256 `0545c45029986bd9d36543d896d04af15d9c47906325ce10d75024e27cb1fc17`.
Warm-inputens SHA-256 är oförändrad:
`312ef133ae70d40e2c437772ea79f96884d0eee687daabd4776ce9c166494df2`.

Starta det vanliga warm-provet för slutkoden:

```sh
scripts/run-gauntdl-probe-warm.sh /home/nichlas/roms/MAME/Midway/Vegas/gauntd \
7950 90000 .build-tmp/gaunt-k2-clean2-f6750.warm.gz 6750
```

Nästa större prestandasteg behöver fortfarande angripa textursampling och
raster/FIFO-flödet enligt [tidsprofilen](gauntlet-dl-runtime-time-profile-2026-09-10.md).
En ytterligare konkret observation är att Voodoo-PCI bygger interpolerade
trace-strängar även när trace är avstängd; den kostnaden är ännu inte ändrad
eller A/B-validerad.
