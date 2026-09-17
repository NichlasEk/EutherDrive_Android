# Gauntlet DL: bitmasker per tile

## Resultat

Opt-in `EUTHERDRIVE_GAUNTDL_GPU_TILE_MASKS=1` med `GPU_TILE_BATCH=1` ersätter
tile-indexlistor med fyra 32-bitarsmasker. CPU-planeringen sjönk från
10,570 till 3,337 ms i ett profilpar; extra tile-data minskade från 1767616
till 1701560 byte. GPU-dispatch/barriär/patch-intervallet var 250,668→242,723 ms.
Fence-väntan var samtidigt 565,181→570,834 ms. Delkostnaderna bevisar inte
förbättrad total genomströmning.

Tre par utan profilering, samma shader/capture-build, ordning listor→masker,
masker→listor, listor→masker:

| Läge | Prov 1 | Prov 2 | Prov 3 | Median |
|---|---:|---:|---:|---:|
| Indexlistor | 12,8999 s | 13,0964 s | 13,1047 s | 13,0964 s |
| Bitmasker | 12,8869 s | 13,2200 s | 12,6065 s | 12,8869 s |

Maskernas median är cirka 1,6 % lägre, men riktningen vänder i andra paret
och maskspridningen är 0,614 s. **Ingen säker total fartvinst hävdas**;
experimentet förblir av som default. Ingen CPU-only-jämförelse eller
spelbar bildfrekvens har visats. Andra värdjobb var inte isolerade.

## Konstruktion och gränser

Varje aktiv tile serialiseras som packad XY plus fyra maskord. Bit n
representerar draw n inom epoken, högst 128 draws. CPU:n använder en flat
array med fyra nollinitierade ord per tile, i stället för en dynamisk vektor
per tile. Bounding-box-intersektioner sätter bitar; tomma tiles utelämnas.
Shadern går genom maskorden i ordning och tar lägsta satta bit först.
Metadataadressen är epokens start plus draw-index × 256. Därmed bevaras
draw-ordningen utan en separat metadataindexläsning för varje listpost.

Samma textur/NCC- och origin-gränser, pixelägarskap, patchordning och
statistikslots som i [tile-listorna](gauntlet-dl-gpu-tile-lists-2026-09-17.md).
Masker har företräde om både list- och maskflaggan är satta. Payloads ligger
efter batchdata och ingår även i sparse continuation-upload. Kapacitets-
gränsen 64 MiB ger fallback till grova tiles; inga fallbacks inträffade här.
Native och shader ska byggas tillsammans; ABI är fortsatt 4.

I båda jämförelseleden: 186670 draw/tile-intersektioner, 11410944 GPU-
invokationer, 2366 fusionerade dispatchar för 17051 draws, plus 122 separata
draws och totalt 152 queue-submissions. Det som ändras är representation
och genomgång av kandidaterna, inte vilka pixlar/trianglar som behandlas.

## Verifiering

Native-sviten passerar med maskflaggan både av och på, Vulkan-validering
och den tidigare processlokala NVIDIA TLS-workarounden. Differentialfallen
täcker överlappning, 128 statistikslots, byte av origin, tile-kanter och
en ny gles 128-draw-sekvens uppdelad i 31/33/31/33 draws över maskordens
gränser. Pixel-/djupdata och samtliga statistikslots jämförs med separata
dispatchar. Befintliga dirty-, patch-, continuation-, reset- och negativa
kontroller passerar. SPIR-V-valideringen passerar.

Full CPU-shadow med masker, dirty-audit och Vulkan-validering matchar
per-draw-räknare och bild/djup vid bildgränser; noll Vulkan-fel. Nio fulla
replays (shadow, två profilerade och sex tidsprov) matchar SHA-256
`32f8e9c49dcc5498c044bb602156ef6e82fe5dec64f27c99749f3a6fe298053a`.
Bildhash `0xe87b12da`.

Normal Release för Probe/UI är återställd och bygger utan fel. Gräns-,
pending-pixel-, dirty- och limit-tester samt 720 state-, 393216 RGB- och
65536 alfafall passerar. Normalbygget aktiverar ingen runtime-GPU-väg.

Replay 6750→7950, 90000 steg, `DOTNET_TieredCompilation=0`; residenta
nivå-3-flaggor, `GPU_TILE_BATCH=1`, `GPU_TILE_LISTS=1`, `GPU_TILE_MASKS=0/1`.
Batchåteranvändning och grupperad dirty-kontroll av. Profilparet använder
`GPU_PROFILE=1`; tidsparen använder 0. Vulkan-validering och dirty-audit av
i tidsprov, på i shadow. Inga egna samtidiga byggen/tester under tidsproven.
Profilparets runMs 13061,7/12858,2 används inte som totalfartbevis.

Artefakter under `.build-tmp/`, ej incheckade:

- `gpu-tile-mask-{shadow,profile-old,profile-new}{.log,-final.warm.gz}`
- `gpu-tile-mask-bench-{old,new}-{1,2,3}{.log,-final.warm.gz}`
- `gpu-tile-mask-native-{0,1}.log`
- `gpu-tile-mask-build.log`
