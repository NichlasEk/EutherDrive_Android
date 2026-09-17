# Gauntlet DL: packad bilinjär texturfiltrering

## Val av arbete

En separat replay med `EUTHERDRIVE_GAUNTDL_PROFILE_VOODOO_TEXTURE_RASTER_STATES=1`
visar att de största två rasterlägena är `tm8c22490f` och `tm8c22410f`:
3 259 831 respektive 2 849 890 skrivna rasterpixlar. De använder filtrerade
NCC-format (9 och 1). NCC-avkodningen har redan uppslagstabeller; även RGB565
har en tabell. Profilen räknar rasterpixlar, inte tid eller antal texelhämtningar,
och visar TMU0-läget, inte fullständig TMU1-fördelning.
Logg: `.build-tmp/gaunt-texture-path-profile.log`.

## Behållen ändring

`BilinearTextureRgba` blandar två kanaler per 64-bitars heltal, R/G och B/A,
i stället för fyra separata kanaluttryck. Antalet kanalmultiplikationer minskar
från 16 till 8 utan texturcache eller ändrade gästminnesregler.

Den enda anroparen levererar fraktioner 0…255. Testet inkluderar även 256.
Vikterna är icke-negativa och summerar till 65 536; maximal kanalsumma med
avrundning är `255 * 65536 + 32768 = 16744448`, mindre än 2^24. Två kanaler
i separata 32-bitarsfält kan därför inte påverka varandra. Samma `+0x8000`
och högerskift 16 används; resultatet är redan inom 0…255 utan clamp.
Ett debug-assert dokumenterar det tillåtna fraktionsintervallet.

## Växlad normal-CPU-jämförelse

Replay 6750→7950, warm-snapshot `gaunt-k2-clean2-f6750.warm.gz`, 90 000
CPU-steg per probe-anrop, `DOTNET_TieredCompilation=0`. Inga profilerare,
GPU-flaggor, byggen eller state-hashningar under tidsmätningen.
Frysta byggen: `.build-tmp/gaunt-ref-baseline` och `gaunt-packed-filter-candidate`.
Referensens Core-kod är oförändrad mellan `f7570d35` och `96785933`.

Ordning: old1/new1/new2/old2/old3/new3/new4/old4, därefter new5/old5/old6/new6.

| Par | Referens, ms | Kandidat, ms |
|---|---:|---:|
| 1 | 10852,9 | 10920,5 |
| 2 | 11102,7 | 11001,5 |
| 3 | 11369,9 | 10933,7 |
| 4 | 11182,3 | 10750,2 |
| 5 | 11141,1 | 10714,0 |
| 6 | 10988,1 | 11027,7 |
| Medel | 11106,17 | 10891,27 |
| Median | 11121,9 | 10927,1 |

Medeltiden minskar 1,93 procent, medianen 1,75 procent. Fyra av sex par vinner;
fördelen finns i medel både i första serien och de två extra paren. Det är
en liten host-mätt förbättring, inte en garanti för varje körning eller för
Android/ARM. Ingen spelbarhets- eller spel-FPS-slutsats dras av probe-fps.

Alla tolv slutmaskiner matchar: 98 901 914 dekomprimerade byte, SHA-256
`32f8e9c49dcc5498c044bb602156ef6e82fe5dec64f27c99749f3a6fe298053a`.
Bildhash `0xe87b12da`, 41 swap-kommandon per replay.
Artefakter: `.build-tmp/gaunt-packed-filter-{old1..old6,new1..new6}.log`
och motsvarande `-final.warm.gz` (notationen avser respektive numrerad fil).

## Verifiering

- ROM-fritt filtertest: 1 156 784 fall mot den gamla kanalvisa formeln.
  Alla fraktioner 0…256, 16 hörnextremsmönster med roterade kanalmönster,
  plus 100 000 deterministiska slumpfall. Passerar.
- LOD-regression: 1 048 576 fall passerar.
- 11 stream-boundaries, 5 dirty-writers, 9 runtime-limits samt PCI-trace av/på
  passerar; 40 000 avstängda trace-anrop allokerar 0 byte.
- Normal Probe och UI bygger utan fel; befintliga varningar kvarstår.
- En avslutande replay från återbyggd standard-DLL matchar också hela
  slutmaskinen: 11 055,2 ms, bildhash `0xe87b12da`. Logg
  `.build-tmp/gaunt-packed-filter-final.log`. Den ingår inte i A/B-medeltalen.

Nästa större arbete bör rikta sig mot kostnad per draw eller GPU-synkronisering;
den här förbättringen ändrar inte den tidigare slutsatsen att GPU-vägen behöver
amortera sina förberedelser och väntningar för att slå normal-CPU-vägen.
