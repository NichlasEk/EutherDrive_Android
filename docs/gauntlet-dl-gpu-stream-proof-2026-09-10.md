# Gauntlet DL: sammanhängande GPU-segment, 2026-09-10

## Vad som nu fungerar

Offline-replay kan nu behålla texturer, vald färgbuffert och delad djupbuffert
på GPU:n genom ett sammanhängande common-state-segment. Textur/NCC-ändringar
läggs in före rätt draw med explicita transfer/compute-barriärer. Resultatet
läses tillbaka en gång efter segmentet. CPU-oraclen laddas aldrig upp.

GDR2 sparar hela den valda fysiska färgbufferten och delade djupbufferten:
2 097 152 packade färg/djup-pixlar. Både kedjan mellan CPU-snapshots och
GPU-resultatet jämförs över hela bufferten, inte bara överlappande rutor.
Logiskt Y mappas till fysisk buffertadress; aliaserade Y-layouter avvisas.

Tre verkliga segment har verifierats:

| Lokal capture | Start: skip | Draws | Slutgräns | Uppdaterade byte inkl. reset |
| --- | ---: | ---: | --- | ---: |
| `.build-tmp/gpu-stream-first` | 0 | 26 | unsupported-textured-state | 3 072 NCC |
| `.build-tmp/gpu-stream-next` | 4 | 32 | capture-limit | 6 144 NCC |
| `.build-tmp/gpu-stream-later` | 120 | 4 | unsupported-textured-state | 2 048 NCC |

Samtliga segment och samtliga 62 draws separat matchar exakt färg/djup.
Segmenten passerar Khronos synkroniseringsvalidering utan fel. Alla lägen körs
12 gånger, tre uppvärmningar borttagna; alla outputpixlar kontrolleras varje gång.

## Negativa kontroller och faktisk täckning

- Ändrad oracle: exakt en avvikelse upptäcks.
- Uteblivna NCC-uppdateringar i 32-drawssegmentet: 8 764 avvikande pixlar.
- Råtexturminnet ändrades inte inom dessa verkliga segment. Ett separat
  metamorfiskt tvådrawstest roterar texturbankerna 1 024 byte och justerar
  alla LOD-baser lika mycket. Samma oracle är då fortfarande korrekt.
  Testet passerar exakt och med ren synkroniseringsvalidering.
- Det riktade råtexturtestet använder 14 022 656 patch-byte inkl. reset,
  208 regioner. Utelämnade kopior ger 5 550 pixelavvikelser. Det är ett
  konstruerat test av uppdateringsvägen, inte en observerad speluppladdning.
- Elva ROM-fria gränstestfall passerar i `GpuStreamBoundaryChecks.cs`, för
  capture-bygge och normalbygge. Spårning sker respektive kompileras bort.

Tidigare GDR1- och samplerprover finns kvar; den nya vägen är fortfarande
opt-in och fristående från normal emulatorrendering.

## Synkgränser och begränsningar

Capture slutar vid unsupported raster-state, flat/gradient/wire-rendering,
clear/materialiserad clear, faktisk swap, ändrad draw-buffer, CPU-LFB-access,
host-presentation eller capture-gränsen 32 draws. `boundary.txt` registrerar
antal och orsak. Clear/swap/CPU-access **avslutar** segmentet: de har inte
implementerats som GPU-kommandon. Det finns ännu ingen automatisk live-
flush/fallback/återupptagning och ingen komplett renderingsström över en frame.

Textur/NCC-patchar tas fram offline genom diff av 1 KiB-sidor; det är ännu
inte dirty-tracking i emulatorns skrivväg. Fullständiga snapshots är dyra
(cirka 24 MiB per draw) och stannar lokalt. Oförändrad CPU-pre/post-kedja
är ett krav, aldrig något som repareras med uppladdat mellanresultat.

## Mätning och nästa steg

De första mätningarna ligger ungefär på 0,26 ms GPU för 26 draws och
0,32 ms för 32 draws. Full readback/submit och reset ger cirka 1,8–1,9 ms
hosttid med alla indata redan residenta (omkörning cirka 1,9–2,0 ms). Initial framebuffer återställs
device-locally varje repetition; 8 MiB resultat läses tillbaka. Med nya
draw-data/initial framebuffer är hosttiden cirka 3,4–3,7 ms. Snapshotdiff,
fil-I/O och kommandoinspelning ingår inte. Ingen CPU/GPU-spelkvot följer av detta.

Det finns fortfarande ingen uppmätt ökning av speltempo. Nästa steg är att
exponera segmentkörningen som ett opt-in runtime-backend med riktig flush vid
gränserna, CPU-fallback för unsupported states och återupptagning efter
dirty-textur-/färg-/djupuppladdning. Börja i shadow-läge och jämför hela
buffertar vid gränserna innan GPU-resultatet får ersätta CPU-resultatet.
Först därefter kan faktisk replaytid och swaps/sekund säga något om spelbarhet.

## Reproduktion och maskinstate

[Verktygsinstruktioner](../tools/GauntletGpuProbe/README.md#contiguous-segments-with-texturencc-updates-gdr2).
GPU/valideringsloggar ligger i respektive capture-katalog som `stream.*.log`.
Individuella draws: `.build-tmp/gpu-stream-{first,next,later}-individual.log`.
Råtexturtest: `.build-tmp/gpu-stream-relocation.validation.log`.

Capture- och normal-replay 6750→7950 ger hash `0xe87b12da`, PC
`0xffffffff80079e18`, FIFO `27113239/2660774`, draw `474871`, swaps `3877`.
Full slutmaskin är byte-exakt: 98 901 914 byte dekomprimerat, SHA-256
`32f8e9c49dcc5498c044bb602156ef6e82fe5dec64f27c99749f3a6fe298053a`.
Verifierade filer: `.build-tmp/gpu-stream-first-final.warm.gz`,
`gpu-stream-later-final.warm.gz`, `gpu-stream-normal-final.warm.gz`.
Normalbygget skapar ingen stream-capture ens med katalogvariabeln satt.

Slutlig capture-omkörning med samtliga elva gränser finns i
`.build-tmp/gpu-stream-final/` (32 draws, skip 4), med validerad GPU-körning.
Probe/UI bygger utan fel; PCI-regressionstestet passerar med noll allokeringar
för 40 000 avstängda trace-anrop. Även tidigare 16 GDR1-draws och 16 GSC1-
samplerfiler har omkörts utan avvikelser. Båda shaders passerar `spirv-val`.
