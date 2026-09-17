# Gauntlet DL: återanvändning av fortsatt batchminne

Uppföljning: [grupperad dirty-kontroll](gauntlet-dl-gpu-dirty-groups-2026-09-17.md)
provar nästa förberedelsekostnad separat, med batchåteranvändning avstängd.

## Resultat

Opt-in `EUTHERDRIVE_GAUNTDL_GPU_REUSE_BATCH=1` minskar den riktade
CPU-kostnaden för resident fortsättning från 110,961 till 0,903 ms i ett
profilerat par. Native förberedelsetid minskar samtidigt 639,991→521,324 ms.
Det är en lokal kostnadsminskning, **inte en visad genomströmningsvinst**.

Två par utan profilering, ordning gammal→ny→ny→gammal:

| Väg | Prov 1 | Prov 2 |
|---|---:|---:|
| Gammal | 13,6248 s | 13,3761 s |
| Ny | 13,6735 s | 13,6858 s |

Den nya vägen är inte snabbare i dessa prov. Värdbelastning, GPU-väntan
och övrig emulering påverkar totaltiden; proven kan inte fastställa orsaken
till skillnaden. Ingen default ändras och ingen GPU-/spelbarhetsseger hävdas.
Behåll experimentet avstängt för fortsatt isolerad jämförelse.

## Ändring och säkerhetsgränser

Efter en lyckad keep-flush finns minst `batchInitial` ord i batchvektorn.
Vid reset mode 2 gör den nya vägen `resize(batchInitial)` i stället för
att nollställa hela prefixet. Det kastar gamla bild-/patchpayloads utan att
skriva om cirka 8 MiB header/textur/NCC som ändå inte överförs. Metadata-
området nollställs fortfarande, inklusive oanvända draw-slots. Nya patchar
appenderas efter samma fasta gräns och alla aktiva metadata skrivs som förr.

Continuation-upload börjar fortfarande vid `metaAt`; header/textur/NCC
och GPU-bilden är redan residenta. Ny CPU-bildgräns/reset mode 1 är oförändrad.
Dirty-spegel, patchordning, statistik, barriärer och fence-väntan ändras inte.
Flaggan läses vid native-sessionens start, är av som default och ändrar
varken shader eller ABI (fortfarande 4).

## Reproduktion och verifiering

Samma residenta nivå-3-flaggor och replay 6750→7950 som i
[kostnadsprofilen](gauntlet-dl-gpu-cost-profile-2026-09-17.md).
`DOTNET_TieredCompilation=0`. Lägg till `GPU_REUSE_BATCH=0/1`, med prefix
`EUTHERDRIVE_GAUNTDL_`. Profilparet använder `GPU_PROFILE=1`;
tidsparen använder `GPU_PROFILE=0`. Validering och dirty-audit är av under
tidsprov; inga egna byggen/tester kördes samtidigt med dem.

Full shadow med återanvändning, Vulkan-validering och dirty-audit passerar
per-draw-räknare och färg/djup vid bildgränser med noll valideringsfel.
Native batchtest kördes med både flagga 0 och 1: continuation, relocation,
räknare, readback/reset, oanvända statistikslots och negativa texturfall
passerade. Negativkontrollen som tappar patchar ger förväntade 5550 fel.

Alla sju fulla slutmaskiner (shadow, två profilerade och fyra tidsprov)
matchar SHA-256
`32f8e9c49dcc5498c044bb602156ef6e82fe5dec64f27c99749f3a6fe298053a`.
Bildhash `0xe87b12da`. Lokala, ej incheckade artefakter:

- `.build-tmp/gpu-reuse-shadow{.log,-final.warm.gz}`
- `.build-tmp/gpu-reuse-profile-{old,new}{.log,-final.warm.gz}`
- `.build-tmp/gpu-reuse-bench-{old,new}-{1,2}{.log,-final.warm.gz}`
- `.build-tmp/gpu-reuse-native-{0,1}.log`
- `.build-tmp/gpu-reuse-build.log`

Normal Release för Probe/UI återställdes och byggde utan fel. Normalbyggets
gräns-/pending-pixel-/dirty-/limit-tester passerade. De fyra tidsproven hade
identiska 17173 draws, 152 submissions, 643290272 upload-byte och 311623680
readback-byte, med noll kvarvarande GPU-pixlar vid avslut.

Nästa separat mätbara mål är sidkontroll/patchbygge, cirka 313 ms i det
nya profilprovet. Det kräver ett eget korrekthetsbevis för dirty-listor och
NCC; en minskning där får inte heller förväxlas med förbättrad total spelfart.
