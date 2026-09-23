# RE2: färre GPU-väntor vid texturladdning

Detta är en checkpoint för [realtidsplanen](n64-realtime-agent-plan.md), inte ett
påstående om att Resident Evil 2 redan kör i realtid. Alla mätningar nedan är
från Linux, samma R4300/RSP-kärna, samma paraLLEl-RDP-version och en kopia av
användarens RE2-slot 1 från 2026-09-23 20:13. Originalsparningen ändrades inte.

## Fryst jämförelse

- Bas: `d5dfad5c15b4dd2f58e8f8d5147ab90869c6d692`; ROM SHA-256
  `52093e994c89848b17c8e6f26546d66374cf47c9b471ec15da7f322b7ee17ab8`;
  extraherad slot 1 SHA-256
  `b12239503b4178a281b4954d0bbad50c77c0e78937f52dce9ce2045337e62461`.
- Xeon E5-2697 v3, RTX 4090, .NET 8.0.131, två GPU-workers, CPU-affinitet
  `8,9`, register-JIT av, `PARALLEL_RDP_SMALL_TYPES=1`,
  `PARALLEL_RDP_FORCE_SYNC_SHADER=1`, Vulkan-validering av under tidsmätning.
- Körning: `N64Probe --bench-state` till gästsekund 15 med neutral input.
  Mätfönster 5–15 ger 10,002 gästsekunder från N64-cyklerna. ABBA-serierna
  kördes utan samtidiga byggen eller andra emulatortester.
- Lokalt manifest och råloggar:
  `.build-tmp/n64-re2-speed-2026-09-23/baseline-manifest.json`,
  `abba-cap4096/` och `abba-multirow/`. Dessa och ROM/saves är inte i Git.

| Variant | Väggtid per 10,002 gästsekunder, ABBA-medel | Hastighet | Jämförelse |
| --- | ---: | ---: | ---: |
| Bas, högst 128 individuella patchar | 36,132 s | 27,68 % | Referens |
| Högst 4096 patchar | 30,643 s | 32,64 % | +17,91 % genomströmning mot bas |
| 4096 patchar och bevisat flerradsspann | 22,058 s | 45,34 % | +38,93 % mot 4096; +63,81 % mot bas |

ABBA-referensen för det andra steget var 30,645 s, i linje med den första
seriens 30,643 s. Varje serie jämförde tre femsekunderskontrollpunkter, tre
sparade bildrutor samt slutligt CPU- och RAM-tillstånd byte för byte. Ljud,
input, antal grafik- och ljuduppgifter, bildstorlek och spelcykler var lika.
Det är en tung sparad scen, inte ett medelvärde för hela RE2 eller alla spel.

## Ändringen och bevisgränsen

När många CPU-skrivningar väntade i GPU-batchen, spände deras min/max-adresser
ofta över en textur trots att varje faktisk skrivning låg utanför den. Den
befintliga per-patch-kontrollen slutade efter 128 skrivningar. Gränsen är nu
4096; varje skrivning måste fortfarande individuellt bevisas ofarlig, annars
sker den ursprungliga väntan.

`LoadTile` väntade dessutom alltid för flera textrader. För matchande 8- eller
16-bitars format räknar den nya kontrollen ett konservativt sammanhängande
spann från första raden till sista radens åttabytesavrundade slut. Detta
omsluter alla adresser som den pinnade renderarens `load_tile_iteration` och
`update_tmem_16` kan läsa, även om TMEM-stride överlappar eller wrappar.
Okända format, storleksmismatch, YUV, udda startadress, koordinatwrap och
spann utanför installerat RDRAM behåller väntan. Koden ändrar inte gästtid,
kommandoordning eller GPU-shadern.

Den diagnostiska RE2-körningen minskade CPU-skrivbarriärer från 42 079 i bas
till 34 269 efter första steget och 1 888 efter båda. Native-timerns
`waitCopyRenderMs` sjönk från cirka 22,1 s till 13,8 s och sedan 7,6 s över
15 gästsekunder. De tiderna ingår i andra timers; de får inte adderas som
separata kostnader. Diagnostisk instrumentering låg bara i scratchbygget och
ingår inte i ändringen.

## Korrekthet och bredd

- 703 adversariella texturfall jämför hela RAM, hidden memory och TMEM med
  strikt synkronisering och direkt GPU-readback, inklusive 4096/4097-patchars
  gräns, verkligt överlapp efter 128 patchar, olika rad-strides, radslut,
  källoffset, koordinatwrap och RAM-slut. Vulkan-validering: noll fel.
- 77 separata readback-fall: exakt minne och noll valideringsfel.
- RE2 slot 1: samtliga ABBA-kontrollpunkter och bildrutor identiska.
- Mario 64 USA: original-ROM:ens 90-sekunders boot och styrda gångsekvens,
  18 bildkontrollpunkter, slutligt RAM, ljud, input och position identiska.
  Ett par körningar gav 77,15 % i bas och 78,53 % med ändringen under
  gästsekund 70–90. Den lilla skillnaden är inte en säker hastighetsvinst.
  Den gamla Mario-UI-sparningen har en inkompatibel CPU-state-version och
  användes inte; originalfilen lämnades orörd.
- Linux-startskriptet `./scripts/run-n64-gpu-desktop.sh --build-only` byggde
  leveransbibliotek och GPU-desktop med noll fel. Den byggda nativefilen
  `.build-tmp/n64-live-gpu/native/libeuther_n64_gpu.so` har SHA-256
  `a5f51811b52387cdf9ff081125acaf3caef1f640aff9ecf23f69b95c1d0ea9fe`.
  Just den filen klarade 703 texturfall och 77 readback-fall. Ett separat
  RE2-slot-1-prov med den filen gav samma tre bild-/ljud-/RAM-kontrollpunkter
  och samma slutliga CPU-/RAM-state som basen; gästsekund 5–10 och 10–15 tog
  10,69 respektive 11,79 s. Den enda leveransfilens tid är en rökprovning,
  medan ABBA-tabellen ovan gäller experimentbygget med samma källkod.

## Nästa beslut mot 100 %

RE2 behöver fortfarande cirka 2,2 gånger högre genomströmning i detta
scenario. De återstående GPU-väntorna är inte ensamma stora nog att nå dit.
Ett separat instrumenterat körning med den nya GPU-koden tog 39,48 s för 15
gästsekunder: 22,92 s låg *inklusive* i RSP-grafik, varav 9,18 s låg i
RDP-listor. RSP-arbete utanför RDP var därmed ungefär 13,75 s; cirka 16,56 s
låg utanför RSP-grafik. Delarna är inte oberoende, och instrumenteringen gör
denna körning olämplig som hastighetsmått. CPU-JIT-spårningen noterade 17,21
miljoner försök, 10,37 miljoner körningar och 39,20 miljoner instruktioner,
bara 3,78 instruktioner per körning i snitt. Råprofilen ligger lokalt i
`.build-tmp/n64-re2-speed-2026-09-23/cpu-profile-slot1/`.

Fortsätt därför med planens C1/R1-mätning av CPU-blockdispatch och RSP-slices
på den här sloten och Mega Mans senare slot 1. Redovisa exklusiv väggtid och
antal block/slices innan nästa ingrepp; jämför en kandidat i taget mot denna
checkpoint med samma tillstånds- och bildorakel.
