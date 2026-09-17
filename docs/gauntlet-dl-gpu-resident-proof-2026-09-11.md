# Gauntlet DL: färg/djup kvar på GPU mellan draws

Nästa steg efter ersättningsprovet är implementerat i diagnostic-bygget:
GPU:n ersätter CPU-pixelloopen och behåller färg/djup mellan common-state
draws. Endast 16 statistikord (64 byte) läses tillbaka per draw. Vid befintliga
CPU-läs-/skriv-, fallback-, clear-, swap- och presentationsgränser hämtas hela
färg/djupbufferten innan CPU-operationen fortsätter. CPU-buffertarna är alltså
avsiktligt inaktuella inom ett aktivt GPU-segment.

## Verifierat

Två replayfönster (skip 0 respektive 120), vardera med 128 ersatta draws,
ger samma fullständiga slutmaskin som CPU-referensen för 6750→7950:
98 901 914 dekomprimerade byte, SHA-256
`32f8e9c49dcc5498c044bb602156ef6e82fe5dec64f27c99749f3a6fe298053a`.
Framehash är `0xe87b12da`. Båda körningarna har noll Vulkan-
synkroniseringsvalideringsfel och avslutar med `pendingPixels=0`.

| Fönster | Draws | Fulla pixel-readbacks | Totalt readback | Inlämningar |
|---|---:|---:|---:|---:|
| skip 0 | 128 | 3 | 25 174 016 byte | 131 |
| skip 120 | 128 | 6 | 50 339 840 byte | 134 |

Första fönstrets readback minskar cirka 42,7 gånger från tidigare
1 073 750 016 byte. Inlämningarna blir däremot fler: 128 omedelbara
räknarsynkar plus separata pixel-readbacks vid segmentgränser.

Det fristående ABI-testet kör två verkliga draws utan uppdaterade CPU-pixlar,
jämför slutbufferten exakt och kontrollerar att reset före readback samt
readback utan väntande pixlar avvisas. Reset efter readback fungerar.
Batch-ABI-regressionen passerar också: texturflytt matchar exakt, och avsiktligt
utelämnade texturkopior upptäcks (5 550 avvikelser). Elva capture-gränstester
passerar. Native bibliotek och Core använder nu ABI v4.

## Begränsningar och nästa steg

Detta är fortfarande begränsat till 128 stödda draws, Linux/RTX 4090.
Övriga rasterfamiljer körs på CPU. Det är inte en generell Android-GPU-backend.
Full textur/NCC-uppladdning sker fortfarande per draw: cirka 1,10 respektive
1,12 GB i dessa fönster. Räknarna kräver fortfarande fence-väntan per draw.
Replaytiderna omkring 12,6 s visar ingen säker fartökning; probe-fps är inte
spelets bildfrekvens. Nästa avgränsade steg är att återanvända texturdata och
bara ladda upp ändrade delar, med samma state-exakta kontroll.

## Reproduktion och artefakter

Bygg native med `sh tools/GauntletGpuProbe/build.sh` och Probe med
`-p:GauntletGpuCapture=true`. Lägg till både
`EUTHERDRIVE_GAUNTDL_GPU_REPLACE=1` och
`EUTHERDRIVE_GAUNTDL_GPU_RESIDENT=1`; använd inte batchflaggan.
Använd `GAUNTLET_GPU_VALIDATION=1` och vid behov
`EUTHERDRIVE_GAUNTDL_GPU_DRAW_SKIP=120`.

- `.build-tmp/gpu-resident.log` och `gpu-resident-later.log`.
- `.build-tmp/gpu-resident-final.warm.gz` och `gpu-resident-later-final.warm.gz`.
- `.build-tmp/gpu-resident-batch-abi.log`.
- `python3 tools/GauntletGpuProbe/test-resident.py .build-tmp/gpu-stream-first`.

Full readback måste slutföras före reset/lägesbyte. Bibliotekets destroy
rapporterar väntande pixlar men kan inte återföra dem till en bortkastad
CPU-backend; en godkänd körning måste ha avslutade segment och pending=0.

Normalbygget är återställt. Probe och UI bygger utan fel; elva normal-
gränstester och PCI-regressionen passerar. Normal replay med båda GPU-flaggorna
och avsiktligt obefintlig biblioteksadress laddar inget GPU-bibliotek och ger
samma fullständiga slutstate (`.build-tmp/gpu-resident-normal-final.warm.gz`).
Shadern passerar `spirv-val --target-env vulkan1.1`.
