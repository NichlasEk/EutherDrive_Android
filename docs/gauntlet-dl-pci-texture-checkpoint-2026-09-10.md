# Gauntlet: PCI-loggning och texturförberedelse

## PCI-loggning

Åtta PCI-anropsplatser kontrollerar nu `_traceEnabled` före interpolering.
Kontrollen inuti `Trace` finns kvar: när spårning är på är utskriftsgräns och
räknarens inkrementering oförändrade, även efter att gränsen passerats.
Ingen experimentflagga behövs för den färdiga ändringen.

Ett ROM-fritt regressionstest finns i `VoodooPciTraceChecks.cs` och körs med
`EUTHERDRIVE_GAUNTDL_TEST_PCI_TRACE=1`. Det binder delegater före mätningen och
värmer 40 000 anrop innan ytterligare 40 000 mäts. Config-läsning, LFB-läsning,
LFB-skrivning och texturskrivning ingår. Referensens avstängda loggning skapade
5 760 000 byte; den nya vägen skapar 0 byte. Detta är ett syntetiskt test av
PCI-vägen, inte en mätning av hela spelets allokeringar.

Två separata långserier användes. Varje serie har två inledande uppvärmnings-
körningar följda av fyra växlade par. Ordningen är A/B, B/A, A/B, B/A.

| PCI-variant | Referens, medel | Kandidat, medel | Bedömning |
| --- | ---: | ---: | --- |
| Med tillfällig A/B-flagga | 12 093,95 ms | 12 237,60 ms | Ingen vinst |
| Ren guard utan experimentflagga | 11 882,575 ms | 11 850,575 ms | Cirka 0,27 %, inte säker vinst |

Rena kandidatens fyra tider är 11 264,6 / 11 801,8 / 11 987,6 / 12 348,3 ms;
referensen 11 291,8 / 12 375,0 / 12 004,4 / 11 859,1 ms. Tre av fyra par
vanns men spridningen är stor. Referensbinären använder den tidigare
strängvägen via den tillfälliga flaggans avstängda läge. Andra CPU-tunga jobb
kördes samtidigt på värddatorn; inga andra processer stoppades.

Ändringen behålls för den verifierade allokeringsminskningen, **inte** som en
påstådd förbättring av spelets uppdateringsfrekvens. Alla 20 långa replay-
körningar behöll bildhash, PC, FIFO-, draw- och swap-räknare.

## Texturförberedelse: prövad och borttagen

Kandidaten flyttade `ClampS`, `ClampT`, standard-LOD och beslutet att hoppa
över den andra TMU:n till `BuildMameTextureTriangleState`. Diagnostikens
undantag bevarades exakt. Perspektivdivision, koordinatavrundning, bilinjär
filtrering och färgkombinering ändrades inte. Ingen beständig cache eller
snapshot-layout lades till.

Kandidaten jämfördes separat mot en referens med den rena PCI-guarden.
Första serien hade två inledande uppvärmningskörningar, sedan fyra växlade
par. Efter tillstånds- och spårningskontroller gjordes fyra ytterligare
växlade par. Samma Core-binärer användes under båda serierna.

| Serie | Referens, medel | Texturkandidat, medel | Resultat |
| --- | ---: | ---: | --- |
| Första fyra par | 12 033,525 ms | 11 666,700 ms | 3,05 % snabbare, 3/4 par |
| Bekräftande fyra par | 10 885,225 ms | 11 348,125 ms | 4,25 % långsammare, 0/4 par |
| Alla åtta par | 11 459,375 ms | 11 507,4125 ms | 0,42 % långsammare |

Bekräftelsens referenstider: 11 100,6 / 10 775,1 / 10 981,4 / 10 683,8 ms.
Kandidattider: 11 148,1 / 11 399,9 / 11 480,6 / 11 363,9 ms.
Samtliga 18 långa replay-körningar behöll det exakta oraklet. Korrekthet
räcker dock inte för en prestandaändring: kandidaten är borttagen ur körkoden.
Den första seriens positiva resultat ska inte citeras ensamt som en vinst.

Försöket finns lokalt i `.build-tmp/texture-setup-candidate-20260910.cs` och
`.build-tmp/texture-setup-candidate-bin/`. Mätloggarna är
`.build-tmp/texture-setup-*.log` och `.build-tmp/texture-repeat-*.log`.
Nästa rasterförsök behöver eliminera mer faktiskt sample-/decode-arbete;
denna begränsade flytt av fasta beslut gav ingen reproducerbar förbättring.

## Korrekthetskontroll

Original, enbart PCI-guard och PCI-guard plus texturförberedelse gav exakt
samma fullständiga sparade slutmaskin: 98 901 914 byte okomprimerat och SHA-256
`32f8e9c49dcc5498c044bb602156ef6e82fe5dec64f27c99749f3a6fe298053a`.
Det korta 300-anropsoraklet är också lika: hash `0x40bd6aae`, PC
`0xffffffff80119038`, FIFO `25628590/2486968`, draw `439076`, swaps `3847`.

En separat 20-anropsreplay med PCI- och två-TMU-spårning aktiverad och
parallell raster avstängd gav identiska åtta PCI-rader och åtta TMU-rader i
alla tre varianter. Även regressionstestets påslagna spårning, gräns på två
rader och räknare på tre anrop passerar. Kontrollartefakter:
`.build-tmp/pci-texture-{original,pci,texture}-{short,state,trace}.log` och
`.build-tmp/pci-texture-{original,pci,texture}-final.warm.gz`.

Efter att texturförsöket tagits bort byggdes Core/probe och UI i Release:
0 fel (349 respektive 33 varningar). PCI-regressionstestet passerade igen.
En sista lång replay av den behållna koden gav åter exakt samma fullständiga
snapshot-hash ovan: `.build-tmp/pci-kept-final.log` och
`.build-tmp/pci-kept-final.warm.gz`.

## Reproduktion

Under kontroll med aktiverad Voodoo-spårning upptäcktes även ett befintligt
probe-fel: `DumpVoodoo` sökte en privat metod direkt på `VoodooTraceBackend`,
trots att den deklareras i basklassen. Slutdumpen kraschade efter avslutad
replay. Metoduppslaget vandrar nu basklasserna, precis som fältuppslagen.
Det ändrar inte emulatorns körning. För tillstånds- och spårningskontrollerna
används samma rättade probe-assembly med respektive sparad Core-assembly.

Använd Release-proben och samma warm-start som FIFO-checkpointen:

```sh
scripts/run-gauntdl-probe-warm.sh \
  /home/nichlas/roms/MAME/Midway/Vegas/gauntd 7950 90000 \
  .build-tmp/gaunt-k2-clean2-f6750.warm.gz 6750
```

Det långa oraklet är `frameHash=0xe87b12da`, PC `0xffffffff80079e18`,
FIFO `27113239/2660774`, draw `474871` och swaps `3877`.
Proben kör 1 200 host-anrop men bara 41 nya gäst-swaps. Dess `fps` är inte
antal faktiska spelbilder per sekund och dessa försök visar inte spelbarhet.

Lokala, ej incheckade artefakter: `.build-tmp/lazy-pci-*.log`,
`.build-tmp/pci-clean-*.log`, `.build-tmp/pci-ab-baseline-bin/`,
`.build-tmp/pci-clean-bin/` och den fristående testharnessen
`.build-tmp/pci-trace-check/`.
