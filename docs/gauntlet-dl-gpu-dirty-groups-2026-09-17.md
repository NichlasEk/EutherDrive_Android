# Gauntlet DL: grupperad dirty-sidkontroll

## Resultat och avgränsning

Opt-in `EUTHERDRIVE_GAUNTDL_GPU_DIRTY_GROUP_SCAN=1` sänker profilerad
sidkontroll/patchbyggnad från 342,766 till 64,133 ms i ett jämförelsepar.
Native förberedelsetid minskar 682,933→399,125 ms. Den tidigare flaggan
`GPU_REUSE_BATCH` är av i båda leden för att isolera ändringen.

Det är en lokal kostnadsminskning, inte en säker total fartvinst. Två par
utan profilering i ordning gammal→ny→ny→gammal gav:

| Väg | Prov 1 | Prov 2 |
|---|---:|---:|
| Gammal | 14,2174 s | 14,2402 s |
| Grupperad | 14,1649 s | 15,4379 s |

Första paret är nästan lika, andra paret är sämre för den nya vägen.
Proven fastställer inte varför; övriga värdjobb är inte isolerade.
Ingen ändring av default och inget påstående om spelbar bildfrekvens.

## Algoritm

Vid batch enqueue kontrolleras dirty-flaggor i grupper om 16 textursidor
(16 KiB texturdata). Bitvis OR av 16 flaggor ger en snabb kontroll av om
hela gruppen är ren. I så fall hoppas alla 16 siditerationer över, med
oförändrat `pagesSkipped`-antal. Blandade grupper använder tidigare
sid-för-sid-logik. OR används i stället för summering för att även
icke-boolska flaggvärden ska räknas som dirty utan overflow-utsläckning.

Om dirty-audit är på jämförs samtliga 4096 texturord i en överhoppad grupp
mot snapshot-spegeln. Missade writes ger fortsatt fel. Sista gruppen slutar
exakt vid texturgränsen; de två NCC-sidorna kontrolleras alltid separat.
Utan dirty-mask används ursprunglig väg. Dirty-format, ABI, shader,
patchordning, reset och GPU-synkronisering ändras inte. Flaggan läses vid
native-sessionens start och är av som standard.

## Verifiering

Native batchsviten passerar med flaggan både av och på samt Vulkan-
validering. Nya tester täcker sidor 0, 15, 16 och 8191, sista ordet i sidan,
två höga dirty-bitar som inte får ta ut varandra, ändrade NCC-ord samt
negativ audit för en felaktigt ren mask. Befintliga continuation-/reset-/
patch-/statistik- och negativa tester passerar också.

Full nivå-3-shadow med gruppkontroll, dirty-audit och Vulkan-validering
matchar per-draw-räknare och bild/djup vid gränser; noll Vulkan-fel.
Alla sju replays (shadow, två profilerade och fyra utan profilering) matchar
hela slutmaskinens SHA-256:
`32f8e9c49dcc5498c044bb602156ef6e82fe5dec64f27c99749f3a6fe298053a`.
Bildhash `0xe87b12da`.

De fyra tidsproven har identiska dirty-räknare: 34272 jämförda och
140378112 överhoppade sidor. Även draws (17173), submissions (152),
upload (643290272 byte), readback (311623680 byte) och GPU-invokationer
(20268800) är oförändrade. Normal Release för Probe/UI återställdes och
byggde utan fel; normalbyggets gräns-/pending-/dirty-/limit-tester passerade.

Replay 6750→7950, 90 000 steg, `DOTNET_TieredCompilation=0`, övriga flaggor
som [kostnadsprofilen](gauntlet-dl-gpu-cost-profile-2026-09-17.md).
Vulkan-validering och dirty-audit är av vid profil-/tidsprov, på i shadow.
Inga egna samtidiga byggen/tester under tidsproven. Lokala artefakter:

- `.build-tmp/gpu-group-shadow{.log,-final.warm.gz}`
- `.build-tmp/gpu-group-profile-{old,new}{.log,-final.warm.gz}`
- `.build-tmp/gpu-group-bench-{old,new}-{1,2}{.log,-final.warm.gz}`
- `.build-tmp/gpu-group-native-{0,1}.log`
- `.build-tmp/gpu-group-build.log`

Profilparets runMs var 15502,8/13812,9; skillnaden är mycket större än den
isolerade sidkontrollvinsten och ska inte tillskrivas denna ändring ensam.
