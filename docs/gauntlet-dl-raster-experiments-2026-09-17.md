# Gauntlet DL: rasterförsök efter CPU-dispatchprofilen

Två kandidater testades separat mot oförändrat normalbygge, utan profilerare
eller GPU-flaggor. Warm-replay 6750→7950, 90 000 CPU-steg per probe-anrop,
`DOTNET_TieredCompilation=0`. Varje serie kördes old1/new1/new2/old2/old3/new3/new4/old4
med frysta byggkopior. Ingen kompilering eller state-hashning under tidsproven.

## Sen färgberäkning: återställd

Flyttade interpolerad färg efter djupkontrollen och hoppade över RGB i den
profilerade common-kärnan, som använder textur/konstantfärg. Alpha-maskens
fallback behöll RGB-beräkningen.

| Prov | Referens, ms | Kandidat, ms |
|---|---:|---:|
| 1 | 11316,3 | 10923,6 |
| 2 | 11080,7 | 11114,5 |
| 3 | 11157,0 | 11260,5 |
| 4 | 10905,2 | 11456,8 |
| Medel | 11114,8 | 11188,85 |

0,67 procent långsammare i medel. Ingen stabil vinst; återställd.
Artefakter: `.build-tmp/gaunt-raster-lazy-color-{old1,new1,new2,old2,old3,new3,new4,old4}.log`
och motsvarande `-final.warm.gz`; fryst kandidat `gaunt-raster-lazy-color-candidate`.

## Konstant LOD vid låsta/inverterade gränser: återställd

När `min8p8 >= max8p8` är `min(max(value, min), max)` konstant.
Kandidaten hoppade då över perspektivlogaritm, bias och dithering men behöll
LOD-mask och slutlig clamp. Detta omfattar även spelets inverterade gränser.

| Prov | Referens, ms | Kandidat, ms |
|---|---:|---:|
| 1 | 11334,0 | 10997,5 |
| 2 | 11135,1 | 11034,8 |
| 3 | 11061,8 | 11208,7 |
| 4 | 11098,2 | 11248,0 |
| Medel | 11157,275 | 11122,25 |

Endast 0,31 procent kortare medeltid; den senare halvan går åt motsatt håll.
Ingen stabil vinst; återställd. Artefakter: `.build-tmp/gaunt-fixed-lod-*.log`
och motsvarande slutstater; fryst kandidat `gaunt-fixed-lod-candidate`.

## Korrekthet och fortsatt arbete

Alla 16 fullständiga slutmaskiner matchar oraklet: 98 901 914 dekomprimerade
byte, SHA-256 `32f8e9c49dcc5498c044bb602156ef6e82fe5dec64f27c99749f3a6fe298053a`.
Bildhash `0xe87b12da`, 41 swap-kommandon per replay. Probe-fps är inte spel-FPS.

Ett ROM-fritt regressionstest behålls: `EUTHERDRIVE_GAUNTDL_TEST_TEXTURE_LOD=1`.
Det jämför 1 048 576 fall med den ursprungliga beräkningsordningen: alla
64×64 registergränser, fyra masker, perspektiv av/på, dithering av/på och
alla 16 ditherpositioner samt representativa bias-, overflow- och W-gränser.
Testet använder befintlig logaritmhjälpare som gemensam komponent och är
inte ett oberoende hårdvaruorakel för den hjälparen. Både kandidaten och
det återställda normalbygget passerar. Probe och UI bygger utan fel efter
återställning; ROM-fria stream-boundary-, dirty-writer-, runtime-limit- och
PCI-trace-tester passerar också (40 000 avstängda trace-anrop: 0 byte).

Ingen ny runtimeoptimering behålls. Nästa större kandidat bör mätas mot
texturhämtning/formatavkodning eller arbete per draw; dessa två små
pixelbesparingar har inte visat en användbar end-to-end-vinst.
