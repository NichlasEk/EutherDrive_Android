# Gauntlet DL: köad runtime shadow, 2026-09-10

## Resultat

Runtime shadow kan nu köa ett helt segment, inklusive ordnade textur/NCC-
patchar, och göra en GPU-inlämning/readback/jämförelse vid segmentgränsen.
C# och native-biblioteket använder ABI version 2. Per-draw-läget finns kvar.

- Skip 0: 128 draws i segment om 26, 86 och 16; tre GPU-inlämningar.
- Skip 120: 128 draws i segment om 4, 10, 10, 53, 10 och 41; sex inlämningar.
- Båda fönstren matchar hela den valda fysiska färgbufferten och delade
  djupbufferten, 2 097 152 packade pixlar, vid varje segmentgräns.
- Båda passerar Vulkan-synkroniseringsvalidering med noll rapporterade fel.
- Per-draw-vägen omkörd med ABI v2: alla 128 draws i första fönstret matchar.
- Samtliga tre körningar behåller byte-exakt fullständig slutmaskin.

## Verifierad överföringskostnad

Första replayfönstret, samma 128 draws:

| Läge | GPU-inlämningar | Uppladdade byte | Återlästa byte |
| --- | ---: | ---: | ---: |
| Per draw | 128 | 1 099 304 960 | 1 073 741 824 |
| Batch | 3 | 50 749 536 | 25 165 824 |

Cirka 21,7 gånger mindre uppladdning och 42,7 gånger mindre readback.
Batchens inter-draw-patchar är 18 432 byte, redan inkluderade i uppladdningen.
Senare fönster: sex inlämningar, 101 483 712 byte upp och 50 331 648 byte ner,
varav 21 504 uppladdade byte är patchar.

Detta är minskad kostnad för **dubbelkörning och verifiering**, inte uppmätt
acceleration av själva spelet. CPU:n renderar fortfarande alla draws och
levererar spelbilden. De enskilda totala replaytiderna är inte ett tillräckligt
prestandaunderlag för någon spel-FPS-vinst.

## Kontroller

- Ändrad lokal oraclebit: exakt en avvikelse, `first=0`, upptäcks.
- Uteblivna inter-draw-uppdateringar i runtime: 37 920 pixelavvikelser upptäcks.
- Direkt ABI-test med två GDR2-draws och råtexturer flyttade 1 024 byte,
  med motsvarande flytt av LOD-baser: exakt oförändrad bild.
  7 011 328 patch-byte i en inlämning; utelämnade kopior ger 5 550 avvikelser.
  Detta är ett konstruerat test av råtexturvägen, inte en observerad spelupload.
- Tom flush, första enqueue utan initialisering samt reset som skulle skriva
  över en pågående batch avvisas. Kön kan sedan köras korrekt.
- Elva befintliga gränstestfall passerar i capture-bygget.

`test-shadow-batch.py` kör ABI-/råtexturtesterna reproducerbart från lokala
capturefiler. Positiv och negativ variant passerar synkroniseringsvalideringen.

## Implementation och avgränsning

Metadata reserveras för högst 128 draws. Initial textur/NCC och framebuffer
tas från CPU:n vid segmentstart. Vid varje senare draw diffas 1 KiB-sidor;
ändrade sidor kopieras till en oföränderlig patchlista. CPU-resultat laddas
aldrig upp mellan draws. GPU-kommandon kopierar patchar före rätt dispatch
med explicita transfer/compute-barriärer. En fence och full readback avslutar
segmentet, och jämförelsen sker innan CPU:n utför gränsoperationen.

Det är fortfarande CPU-snapshot-diffning, inte dirty-tracking i skrivvägen.
Varje nytt segment laddar initial textur/NCC och framebuffer igen. 64 MiB
inputkapacitet och 128 draws är hårda diagnostikgränser; överskridande ger fel.
Renderingslägen utanför common-state-familjen går som tidigare via CPU.
Reset flushar en aktiv batch; en finalizer frigör resurser men kan inte
verifiera en ofärdig batch. Kräv en färdig gräns/test-limit i körloggen.

Jämförelse vid segmentgränser kan missa övergående fel som senare draws
skriver över. Därför finns per-draw-läget kvar och har omkörts. GPU-resultatet
ersätter ännu inte CPU-renderingen och är inte verifierat på Android.

## Artefakter och nästa steg

- `.build-tmp/gpu-batch.log`, `gpu-batch-later.log`, `gpu-batch-perdraw.log`.
- `.build-tmp/gpu-batch-negative.log`, `gpu-batch-drop-updates.log`.
- `.build-tmp/gpu-batch-abi-test.log`, `gpu-batch-offline-check.log`.
- Slutmaskiner: `.build-tmp/gpu-batch-final.warm.gz`,
  `gpu-batch-later-final.warm.gz`, `gpu-batch-perdraw-final.warm.gz`.

6750→7950: hash `0xe87b12da`, PC `0xffffffff80079e18`, FIFO
`27113239/2660774`, draw `474871`, swaps `3877`. Alla fulla slutmaskiner:
98 901 914 byte dekomprimerat, SHA-256
`32f8e9c49dcc5498c044bb602156ef6e82fe5dec64f27c99749f3a6fe298053a`.

Normalbygget är återställt. Probe/UI bygger utan fel; normal replay med
både shadow- och batchflaggor satta samt en obefintlig biblioteksadress
laddar ingen GPU-kod och ger samma fullständiga slutstate
(`.build-tmp/gpu-batch-normal-final.warm.gz`). Elva gränstestfall passerar
med hookarna bortkompilerade, liksom PCI-testet med noll allokeringar för
40 000 avstängda trace-anrop. Offline-stream passerar synkroniseringskontroll
och båda shaders passerar `spirv-val`.

Nästa gräns mot faktisk acceleration är ett opt-in-läge där GPU:n kan ersätta
common-state CPU-rasterisering, med korrekt återläsning inför CPU-fallback,
presentation och guest-LFB-access. Behåll shadow-lägena som kontroll och
verifiera också de CPU-sidoeffekter/räknare som rasterloopen i dag producerar.
Mät därefter total replaytid och faktiska swaps/sekund, inte bara GPU-tid.

[Bygg/körning och batchflagga](../tools/GauntletGpuProbe/README.md#queued-runtime-batches).
