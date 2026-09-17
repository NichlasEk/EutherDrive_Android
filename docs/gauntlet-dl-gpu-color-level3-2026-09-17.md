# Gauntlet DL: färgnivå 3, TMU1-format 0

## Resultat: korrekt, fartvinsten är inte fastställd

Tre roterade jämförelser i samma capture-build gav följande tider:

| Läge | Prov 1 | Prov 2 | Prov 3 | Median |
|---|---:|---:|---:|---:|
| CPU | 12,0266 s | 13,7980 s | 14,9768 s | 13,7980 s |
| GPU nivå 2 | 13,6095 s | 14,5145 s | 13,8121 s | 13,8121 s |
| GPU nivå 3 | 13,4396 s | 13,4425 s | 14,4043 s | 13,4425 s |

Nivå 3 har lägre median, men variationen är större än den möjliga vinsten
och riktningen vänder i tredje GPU-jämförelsen. CPU-referensen varierar
nästan tre sekunder. Efter serien observerades load average 9,42 och flera
andra CPU-tunga processer (Java, Python, QEMU och videodekodning). De
lämnades orörda. Det går inte att skilja ändringens effekt från samtidiga
värdjobb tillräckligt väl här: **ingen säker fartvinst eller CPU-seger hävdas**.

Behåll nivå 3 som diagnostiskt opt-in-stöd. Nivå 2 och normalbyggets default
ändras inte. Nästa prestandabevis kräver en lugnare mätperiod, inte ett
antagande om att fler GPU-draws automatiskt är snabbare.

Ordning: CPU→2→3, 3→CPU→2, 2→3→CPU. Våra byggen/övriga testjobb kördes inte
parallellt med mätserien. Vulkan-validering, dirty-audit och target-profiler
var av; `DOTNET_TieredCompilation=0`. GPU-init/slutdränering ingår, men inte
snapshot-inläsning/slutdump. Samtliga nio fulla slutmaskiner matchar oraklet.
Artefakter: `.build-tmp/gpu-level3-bench-{cpu,level2,level3}-{1,2,3}{.log,-final.warm.gz}`.

## Avgränsat stöd

`EUTHERDRIVE_GAUNTDL_GPU_EXTENDED_COLOR_PATH=3` inkluderar nivå 1/2 och
exakt en ytterligare kombination:

- FBZ `000b4779`, color path `0c602c19`;
- alpha `00045119`, fog `000000c1`;
- TMU0 `8c24110f`, TMU1 `8c2410cf`.

Jämfört med det redan stödda TMU1-läget `8c241acf` skiljer sig formatfältet,
inte kombinationsoperationerna. Shaderns befintliga format-0-sampling kan
återanvändas. Nativebibliotek, shader och ABI är oförändrade; endast managed
tillåtelselista utökas. Tidigare nivåer är oförändrade, andra nivåvärden
avvisas av tillåtelselistan och experimentet förblir avstängt som default.
ROM-fria tester täcker nu 720 tillåtna/otillåtna tillståndskombinationer.

## CPU-profil först

Den tidigare GPU-profilen visade 34 segmentavbrott för denna kombination.
Ny CPU-only-profil mäter alla dess draws som uppfyller övriga GPU-villkor,
även de som är för små för att börja en ny GPU-sekvens:

- 7 419 draws, varav 1 284 utan rasterpixlar;
- 211 772 bounding-box-pixlar; medianbbox 20 pixlar, största 154;
- 41 191 covered/rasterpixlar, inga nolltexelpixlar;
- sammanlagt 27,7201 ms i det instrumenterade CPU-rasterfönstret.

Det är alltså många mycket små trianglar. Den stora möjliga vinsten är
färre avbrott och färre fulla bildöverföringar, inte mycket sparad CPU-
pixelberäkning. Fler GPU-dispatcher kan samtidigt kosta tid.

Ny flagga `EUTHERDRIVE_GAUNTDL_GPU_TARGET_PROFILE=1` kräver capture-build
men CPU-only-körning: GPU-shadow/replacement och filcapture avvisas. Timern
startar vid pixelloopen efter metadata/setup och stoppas innan loggraden
skrivs. Övrig triangelsetup och logg-I/O ingår inte. Flaggan ska inte vara
satt vid genomströmningsmätningar. Logg och exakt slutmaskin:
`.build-tmp/gpu-level3-target-profile-v2{.log,-final.warm.gz}`.
Ett första mellanprov utan `-v2` inkluderade också capture-setup i timern;
använd inte dess 137,5 ms som ren rastertid.

## Korrekthet och överföringar

Full resident-batch-shadow och faktisk CPU-ersättning kördes med
Vulkan-validering och full dirty-page-audit. Alla per-draw-räknare och
färg/djup vid pixelgränser matchar i shadow; hela slutmaskinen är exakt
i både shadow och replacement. Noll Vulkan-synkfel och inga kvarvarande
GPU-pixlar vid avslut.

| Mätare | Nivå 2 | Nivå 3 |
|---|---:|---:|
| GPU-draws | 9 012 | 17 173 |
| Fulla pixelgränser | 70 | 37 |
| Kapacitetsflushar | 34 | 115 |
| Submissions totalt | 104 | 152 |
| Upload-byte | 1 189 756 096 | 643 290 272 |
| Readback-byte | 588 054 528 | 311 623 680 |
| GPU-invokationer | 18 932 608 | 20 268 800 |

Draw-ökningen omfattar också tidigare stödda små draws som nu kan fortsätta
i GPU-sekvensen. Antal draw/fallback är inte i sig ett prestandabevis.

Replay 6750→7950, 90 000 steg, total limit 65 536. Full-state-orakel:
98 901 914 dekomprimerade byte, SHA-256
`32f8e9c49dcc5498c044bb602156ef6e82fe5dec64f27c99749f3a6fe298053a`.
Bildhash `0xe87b12da`, 41 swaps. Artefakter:
`.build-tmp/gpu-level3-{SHADOW,REPLACE}{.log,-final.warm.gz}`.

Dessutom kördes per-draw-shadow för de första 512 draws, inklusive den nya
kombinationen efter den tidigare gränsen vid draw 285. Varje mellanliggande
färg/djup-yta och räknarrad jämförs då mot CPU:n, inte bara batchernas slutytor.
Full-state-hash och Vulkan-validering passerar även där:
`.build-tmp/gpu-level3-per-draw{.log,-final.warm.gz}`.

Target-profilering tillsammans med GPU-shadow avvisas som avsett med
`Target raster timing requires CPU-only execution without GPU/file capture`
(`.build-tmp/gpu-level3-profile-negative.log`).

Normal Release för Probe/UI är återställd och bygger utan fel. Normal- och
capture-byggets gräns-/pending-pixel-/dirty-/limit-tester samt 720 tillstånds-
tester passerar. Normalbygget passerar också 393 216 RGB-/65 536 alfafall,
1 156 784 filterfall, 1 048 576 LOD-fall och PCI-tracetester.
Normal replay med nya profil-/GPU-flaggor och obefintlig nativebibliotekssökväg
ger samma full-state-hash och ingen GPU-/target-profillogg: vägen är fortfarande
bortkompilerad. Artefakter: `.build-tmp/gpu-level3-normal-build.log`,
`.build-tmp/gpu-level3-normal.log` och motsvarande `-final.warm.gz`.

## Reproduktion

Använd [resident-batch-kommandot](gauntlet-dl-gpu-resident-batches-2026-09-17.md)
med `GPU_EXTENDED_COLOR_PATH=3`. Behåll bbox, dirty/sparse och residenta
kapacitetsbatcher. För tidsprov ska `GAUNTLET_GPU_VALIDATION` vara unset
och `GPU_VERIFY_DIRTY=0`. För korrekthetsprov aktiveras båda kontrollerna.
För CPU-profil används bara `GPU_TARGET_PROFILE=1` och vanliga warm-runnern,
utan runtime-GPU-/filcapture-flaggor, men fortfarande byggd med
`-p:GauntletGpuCapture=true`. Samtliga `GPU_*`-flaggor har prefixet
`EUTHERDRIVE_GAUNTDL_`.
