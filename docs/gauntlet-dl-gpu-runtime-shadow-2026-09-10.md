# Gauntlet DL: runtime shadow, 2026-09-10

## Ny milstolpe

C#-renderaren anropar nu Vulkan direkt i samma process via ett litet
versionskontrollerat C-ABI. Inga capturefiler eller externa processer behövs.
GPU:n behåller färg/djup mellan draws. CPU:n förblir auktoritativ för spelet.

Vid en synkgräns avslutas segmentet. Annan rendering fortsätter på CPU:n;
nästa lämpliga common-state-draw startar ett nytt GPU-segment med aktuella
CPU-buffertar. Vulkan-contexten återanvänds. Därmed provas även återupptagning
efter CPU-rendering, inte bara ett isolerat inspelat segment.

GPU:n kör först och väntas in, CPU-drawen körs sedan, och hela den valda
fysiska färgbufferten plus den delade djupbufferten jämförs: 2 097 152 pixlar
per draw. CPU-resultat laddas inte upp mellan draws inom samma segment.
Normal presentation och alla guest-data kommer fortfarande från CPU:n.

## Verifierade runtime-fönster

- Start skip 0: 128 draws i tre segment, längder 26, 86 och 16.
- Start skip 120: 128 draws i sex segment, längder 4, 10, 10, 53, 10 och 41.
- Samtliga 256 draws matchar exakt färg/djup. Varje draw jämförs individuellt.
- Synkroniseringsvalidering aktiverad; inga rapporterade fel.
- De mellanliggande gränserna i dessa fönster är unsupported-textured-state;
  sista segmentet avslutas av den explicita testgränsen 128 draws.
- Slutlig ABI-v1-omkörning av första fönstret passerar också, med explicit
  `synchronizationValidationErrors=0` vid native-contextens stängning.
- Avsiktligt ändrad jämförelsebit avbryter korrekt med
  `GPU shadow color/depth mismatches=1 first=0`. Guest-minnet ändras inte av testet.
- Elva befintliga gränstestfall passerar i capture-bygget. Native-ABI-version,
  fel vid saknad shader och avvisning av null-indata har också kontrollerats.

Körningar: `.build-tmp/gpu-shadow.log`, `.build-tmp/gpu-shadow-later.log`.
Full-state-filer: `.build-tmp/gpu-shadow-final.warm.gz` och
`.build-tmp/gpu-shadow-later-final.warm.gz`.
Slutlig ABI-körning och negativ kontroll: `.build-tmp/gpu-shadow-abi1.log`,
`.build-tmp/gpu-shadow-abi1-final.warm.gz`, `.build-tmp/gpu-shadow-negative.log`.

Replay 6750→7950 behåller hash `0xe87b12da`, PC `0xffffffff80079e18`, FIFO
`27113239/2660774`, draw `474871`, swaps `3877`. Dekomprimerad slutmaskin
är 98 901 914 byte med SHA-256
`32f8e9c49dcc5498c044bb602156ef6e82fe5dec64f27c99749f3a6fe298053a`.

Normalbygget är återställt: Probe/UI bygger utan fel. Även med shadow-flaggan
satt och biblioteksadressen pekande på en obefintlig fil kör normal replay
utan GPU-anrop och ger samma fullständiga slutstate, sparat som
`.build-tmp/gpu-shadow-normal-final.warm.gz`. Elva gränstestfall passerar
med hookarna bortkompilerade. PCI-testet passerar, inklusive noll allokeringar
för 40 000 avstängda trace-anrop. Offline-stream och sampler-regressioner
passerar; båda shaders klarar `spirv-val`.

## Begränsningar

Detta är ett korrekthetstest, inte GPU-acceleration av spelet. GPU och CPU
körs seriellt, och varje draw medför full texturuppladdning samt 8 MiB readback.
Den offline-verifierade dirty-page-batchningen är inte inkopplad i runtime-ABI:t.
128 draws är en avsiktlig gräns; det är inte ett helt spelpass eller alla states.
Ingen Android-körning har verifierats; testmaskinen är RTX 4090 på Linux.

Valideringsfel och pixelavvikelser avbryter testet. Contexten stängs vid
testgräns, reset eller render-/jämförelsefel, med finalizer som sista skydd.
Det vanliga bygget kompilerar bort hookarna och laddar inget Vulkan-bibliotek.

## Nästa steg mot verklig acceleration

Anslut dirty-range-uppladdning och köad segmentkörning till runtime-ABI:t,
med jämförelse/readback vid synkgränser istället för varje draw. Behåll
shadow-kontrollen medan fler rasterfamiljer och gränser provas. Först därefter
kan ett separat opt-in-läge låta GPU-resultatet ersätta common-state CPU-
rasterisering, med explicit återläsning inför CPU-fallback/presentation.
Total replaytid och riktiga swaps/sekund blir då de relevanta måtten.

[Bygg- och körinstruktioner](../tools/GauntletGpuProbe/README.md#in-process-runtime-shadow-comparison).
