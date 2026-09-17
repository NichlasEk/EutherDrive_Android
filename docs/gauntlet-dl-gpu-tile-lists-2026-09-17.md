# Gauntlet DL: ordnade triangellistor per tile

Uppföljning: [bitmasker per tile](gauntlet-dl-gpu-tile-masks-2026-09-17.md)
minskar byggkostnaden, men totaltidsvinsten är ännu inte säker.

## Resultat

`EUTHERDRIVE_GAUNTDL_GPU_TILE_LISTS=1` tillsammans med `GPU_TILE_BATCH=1`
minskar GPU-invokationerna med cirka 22,3 % jämfört med den första tile-
prototypen. Men tre tidspar visar **ingen säker total fartvinst**. Listvägen
förblir av som standard; den tidigare tile-prototypen är separat tillgänglig.

| Väg | Prov 1 | Prov 2 | Prov 3 | Median |
|---|---:|---:|---:|---:|
| Alla epoktrianglar per tile | 13,5283 s | 13,3372 s | 13,5653 s | 13,5283 s |
| Ordnade tile-listor | 13,5032 s | 13,5294 s | 13,4107 s | 13,5032 s |

Ordning: gammal→ny, ny→gammal, gammal→ny. Medianavståndet är cirka 0,2 %;
riktningen vänder i andra paret. Samma capture-bygge/shader, profilering
av, dirty-audit av, Vulkan-validering unset, `DOTNET_TieredCompilation=0`.
Batchåteranvändning och grupperad dirty-kontroll är av i båda leden.
Inga egna samtidiga byggen/tester kördes under serien; andra värdjobb är
inte isolerade. Probe-fps är inte spelets bildfrekvens.

## Arkitektur

De tidigare textur/NCC- och origin-gränserna definierar fortfarande epoker.
Inom varje epok byggs 16×8-tile-listor från draw-bounding-boxar. Varje
metadataindex läggs i berörda tiles i ursprunglig draw-ordning. Det exakta
kant-/djup-/rastertestet ligger kvar i shadern; bounding-boxen är bara ett
konservativt urval. Tomma tiles dispatchas inte.

Varje aktiv tile får en header med packad XY, liststart och antal draws.
Headers och metadataindex appenderas efter batchens ursprungliga payload.
De överförs i samma submission, även vid sparse resident fortsättning.
Textur/NCC, ursprungliga metadata och immutable texture-patchpayloads ändras
inte. Listorna lever till den synkrona submissionens slut. Om en epoks
listdata inte ryms i inputbufferten på 64 MiB används den gamla grova
tile-vägen för den epoken i stället. Inga sådana fallbacks inträffade här.

En tråd äger fortfarande en fysisk pixel genom epoken, bevarar packad
färg/djup lokalt och uppdaterar rätt per-draw-statistikslot. Listorna
ändrar inte triangelordning eller minnessynkronisering. Både native och
shader måste byggas om; extern ABI är fortsatt 4 och flaggan är opt-in.

## Arbetsmängd och profil

| Mätare | Grova tiles | Tile-listor |
|---|---:|---:|
| Totala GPU-invokationer | 14678784 | 11410944 |
| Invokationer i fusionerade epoker | 14157824 | 10889984 |
| Fusionerade dispatchar | 2366 | 2366 |
| Draws i fusionerade epoker | 17051 | 17051 |
| Extra listdata, byte | 0 | 1767616 |
| Listposter | 0 | 186670 |

Övriga 122 draws är separata, 152 queue-submissions i båda leden. Readback
och rasterresultat är oförändrade; uppladdningen ökar med listdatans storlek.

Ett separat profilpar gav CPU-planering 0,455→11,960 ms och GPU-intervallet
för dispatch/barriärer/patchar 281,569→299,180 ms. Fence-väntan ökade
629,430→662,143 ms. Native förberedelsetid varierade samtidigt
647,434→664,760 ms, trots att detta fönster ligger utanför listplaneringen.
Det går därför inte att tillskriva all tidsvariation listorna. Däremot visar
profilen ingen självklar GPU-vinst av färre invokationer.

`gpuTile planMs` mäter epok-/listplanering när profilering är på. Tiden
ingår i managed flushInclusiveMs, men inte native recordMs. `gpuTileLists`
rapporterar extra byte, listposter och capacityFallbacks; planMs är noll
utan profilering. Profilparets runMs var 13673,9/14172,4 och används inte
som genomströmningsbevis.

## Verifiering och artefakter

Native differentialtester passerar med listflaggan av och på: överlappande
draws med olika färg/alpha, full 128-slot-batch, origin-byte och glest
placerade 17×9-bounding-boxar vid olika tile-kanter. Hela pixel-/djupytan
och samtliga statistikslots jämförs mot separata dispatchar. Befintliga
patch-, continuation-, reset-, dirty-audit- och negativa tester passerar.
SPIR-V-valideringen passerar. Native-sviten kördes med den tidigare
dokumenterade NVIDIA TLS-workarounden (processlokal LD_PRELOAD).

Full shadow med listor, dirty-audit och Vulkan-validering ger exakt bild/
djup och per-draw-räknare, noll Vulkan-fel. Nio fulla replays (shadow,
två profilerade och sex tidsprov) matchar SHA-256:
`32f8e9c49dcc5498c044bb602156ef6e82fe5dec64f27c99749f3a6fe298053a`.
Bildhash `0xe87b12da`.

Normal Release för Probe/UI är återställd och bygger utan fel. Gräns-,
pending-pixel-, dirty- och limit-tester samt 720 state-, 393216 RGB- och
65536 alfafall passerar. Normalbyggets runtime-GPU-väg är bortkompilerad.

Replay 6750→7950, 90000 steg och övriga flaggor som
[tile-epokprototypen](gauntlet-dl-gpu-tile-epochs-2026-09-17.md).
Lokala artefakter under `.build-tmp/`, ej incheckade:

- `gpu-tile-list-{shadow,profile-old,profile-new}{.log,-final.warm.gz}`
- `gpu-tile-list-bench-{old,new}-{1,2,3}{.log,-final.warm.gz}`
- `gpu-tile-list-native{,-off}.log`
- `gpu-tile-list-build-final.log`

Nästa möjlig hypotes är kompakta bitmasker per tile i stället för dynamiska
indexlistor, för att minska CPU-allokeringar och indexläsningar. Det är inte
implementerat eller ett utlovat fartlyft; dagens enklare tile-väg bör vara
referens i en sådan jämförelse.
