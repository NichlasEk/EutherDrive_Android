# Gauntlet DL: kostnadsprofil för residenta batcher

Uppföljning: [batchåteranvändning](gauntlet-dl-gpu-batch-reuse-2026-09-17.md)
minskar den riktade nollställningskostnaden men visar ännu ingen total fartvinst.

## Slutsats

Nivå 3 sparar överföringar men lägger till ritningar/barriärer, sidkontroller
och batchstarter. Profilen förklarar varför halverad bildtrafik inte ger en
motsvarande fartvinst. Ingen ny runtime-optimering eller default aktiveras.

Nästa avgränsade experiment: undvik onödig nollställning av residenta
fortsättningsbatchers texturområde, med bibehållna metadata-/patchgränser.
Den uppmätta kostnaden är cirka 116 ms per replay, alltså ett begränsat mål,
inte vägen till spelbarhet på egen hand. Därefter bör sidkontrollen undersökas:
även rena sidor besöks för varje draw. En gles lista skulle kräva att reset,
NCC, dirty-audit och samtliga texturskrivare fortsatt fungerar exakt.

## Metod och mätgränser

Replay 6750→7950, 90 000 steg, samma warm snapshot och residenta nivå-2/3-
flaggor som [färgexperimentet](gauntlet-dl-gpu-color-level3-2026-09-17.md).
`DOTNET_TieredCompilation=0`, `GPU_PROFILE=1`, dirty-audit av och Vulkan-
validering unset under tidsproven. Inga egna samtidiga byggen/tester.
Övriga värdjobb lämnas orörda; detta är kostnadsprofilering, inte ett rent
genomströmningsbenchmark. Profileringens mätkostnad ingår.

Först kördes 2→3→3→2 med befintliga native-timers och nya managed-timers.
Sedan byggdes nativebiblioteket med underindelad batchförberedelse och
ytterligare ett prov per nivå kördes. Ingen ABI- eller shaderändring.

Managed Render/Flush är inkluderande tider med native-anrop inuti.
Native GPU-tidsstämplar överlappar värdens fence-väntan. Underindelad
batchförberedelse ingår i native `prepareMs`. **Summera inte överlappande
kolumner.** Dispatchintervallet innehåller också inter-draw-barriärer och
texturpatchar, inte bara shaderarbete. Timerfönstren omfattar inte backendens
tillåtelsekontroller/metadata före Render, CPU-fallback eller resten av
CPU/JIT-emuleringen. Profilen förklarar därför inte hela replaytiden.

## Detaljprov, millisekunder

| Mätfönster | Nivå 2 | Nivå 3 |
|---|---:|---:|
| Native init | 290,724 | 321,951 |
| Managed Render, inklusive native | 576,086 | 669,421 |
| Native förberedelse (ingår i Render) | 566,865 | 651,553 |
| ↳ Ny batch: nollställning, textur/bildpackning | 298,625 | 161,355 |
| ↳ Resident fortsättning: nollställning | 33,083 | 115,667 |
| ↳ Sidkontroll och patchbygge | 170,546 | 323,001 |
| ↳ Full textur/NCC-spegelkopia | 51,987 | 26,472 |
| Managed Flush, inklusive native | 1067,159 | 954,596 |
| Native kommandoinspelning | 19,284 | 31,827 |
| Native staging-kopia | 120,817 | 65,348 |
| Native köinlämning | 2,745 | 4,041 |
| Native fence-väntan | 923,558 | 852,509 |
| Managed pixelåterkopiering | 201,279 | 104,532 |
| Managed batchstatistik: allokering/kopia | 0,916 | 1,384 |
| GPU före dispatch | 357,669 | 194,499 |
| GPU dispatch/barriärer/patchar | 325,331 | 493,128 |
| GPU efter dispatch/utkopiering | 179,941 | 89,706 |

9012/17173 Render-anrop och 104/152 Flush-anrop matchar förväntade
draw-/submissionantal. Ingen separat pixel-readback behövdes: slutbilderna
kopierades vid vanliga batchgränser. Övrig förberedelsetid omfattar bland
annat validering, metadata och bokföring.

I de första två proven per nivå låg native förberedelse på 569–583 ms
respektive 646–653 ms; GPU-dispatchintervallet på 320–322 respektive
513–529 ms. Samma kostnadsmönster återkommer, men detta bevisar inte
en genomströmningsvinst. `runMs` för de första proven var 14128,2/15134,7
(nivå 2) och 13877,3/14008,7 (nivå 3). Detaljproven gav 13989,3/14032,7.
Probe-fps ska inte tolkas som spelets bildfrekvens.

## Verifiering och lokala artefakter

Alla sex profilerade replays matchade hela slutmaskinens SHA-256:
`32f8e9c49dcc5498c044bb602156ef6e82fe5dec64f27c99749f3a6fe298053a`.
Bildhash `0xe87b12da`. Artefakter (inte incheckade):

- `.build-tmp/gpu-cost-profile-{2,3}-{1,2}{.log,-final.warm.gz}`
- `.build-tmp/gpu-cost-detail-{2,3}{.log,-final.warm.gz}`
- `.build-tmp/gpu-cost-native-build.log`
- `.build-tmp/gpu-cost-native-tests.log`

Native batchtest kördes med profiling på och Vulkan-validering på, inklusive
reset/continue/readback, texturpatchar, per-draw-räknare och negativa fall.
Capture-byggets gräns-/dirty-/pending-pixel-/limit-tester passerade.

En full nivå-3-replay med `GPU_PROFILE=0` gav samma full-state-hash och
inga `gpuProfile`-rader (`.build-tmp/gpu-cost-profile-off{.log,-final.warm.gz}`).
Normal Release för Probe/UI återställdes och byggde utan fel. Normalbyggets
gräns-/dirty-/pending-pixel-/limit-tester samt 720 tillståndsfall, 393 216
RGB-fall och 65 536 alfafall passerade. Profileringen är fortsatt opt-in;
normalbyggets runtime-GPU-väg är bortkompilerad.
