# Gauntlet DL: riktig GPU-ersättning i batcher

## Resultat: korrekt men ännu långsammare än CPU

Tre växlade CPU/GPU-par i samma capture-build, utan Vulkan-validering eller
dirty-audit, med `DOTNET_TieredCompilation=0`:

| Läge | Prov 1 | Prov 2 | Prov 3 | Median |
|---|---:|---:|---:|---:|
| CPU | 11,8358 s | 11,7463 s | 11,9434 s | 11,8358 s |
| Batch-GPU, bbox + dirty/sparse | 13,4096 s | 13,3207 s | 13,9021 s | 13,4096 s |

GPU-medianen är cirka 13,3 % långsammare. Ingen default ändras. Alla sex
slutmaskiner matchar oraklet nedan. Loggar/states:
`.build-tmp/gpu-batch-bench-{cpu,batch}-{1,2,3}{.log,-final.warm.gz}`.
Tiderna omfattar GPU-init och slutlig dränering, men inte snapshot-inläsning/
slutdump. Detta är en lokal replay, inte uppmätt spel-FPS.
Viktigt: `GAUNTLET_GPU_VALIDATION` måste vara **unset**, inte `0`;
native-koden aktiverar validering när variabeln finns.

Separat tidsprofil (`.build-tmp/gpu-batch-profile.log`, replay 13,5782 s):
GPU-dispatchintervallet 266,5 ms; GPU före/efter dispatch 403,1/185,6 ms.
Host-init 276,6 ms, batchförberedelse 586,2 ms, command recording 18,5 ms,
staging 165,0 ms, submit 3,1 ms, fence-väntan 920,0 ms och managed bildkopiering
277,9 ms. GPU-tider överlappar host-väntan och ska inte adderas till den.
Den profilen pekar på kopiering/förberedelse snarare än shaderberäkning som
nästa GPU-problem; merparten av total replaytid ligger utanför dessa GPU-mätare.

## Implementation och avgränsning

Capture-bygget kan nu hoppa över CPU-rasterisering för en hel batch och
konsumera dess per-draw-resultat vid synkgränsen. Normalbygget är oförändrat;
detta är fortfarande ett opt-in-experiment, inte ett nytt spellägesdefault.

- `GPU_REPLACE=1`, `GPU_SHADOW_BATCH=1`, `GPU_BATCH_STATS=1` aktiverar vägen.
  Alla flaggor här har prefixet `EUTHERDRIVE_GAUNTDL_`.
- `GPU_RESIDENT` ska vara av. Varje batch läser tillbaka färg/djup, även vid
  128-draw-kapacitet. Nästa batch startar från denna CPU-kopia. Resident
  fortsättning mellan batcher är ett separat återstående steg.
- `GPU_BBOX=1` fungerar nu även i batcher. Varje draw har sin egen
  dispatchstorlek och egna push constants; shaderns bounds-check och fysiska
  färg/djup-adressering är oförändrade. Offline-vägen ändras inte.
- `GPU_INCREMENTAL=1`, `GPU_SPARSE_SNAPSHOT=1`, `GPU_DIRTY_TEXTURE=1` använder
  texturskrivarens 1-KiB-dirtymask i den nya native-exporten
  `gauntlet_shadow_dirty_enqueue`. Ändrade snapshot-sidor kopieras separat;
  NCC-sidor jämförs alltid. Reset laddar alltid hela starttexturen.
- `GPU_VERIFY_DIRTY=1` kontrollerar oberoende att överhoppade sidor faktiskt
  är oförändrade. Den kontrollen ska vara av vid tidsmätning.
- ABI är fortsatt 4, batch-statistik-capability 1. Bygg om native och
  capture-Core tillsammans. Dirty-batchläget kräver den nya exporten.

## Audit av uppskjutna resultat

`FillTexturedTriangle` har en enda anropare. Dess bool-resultat styr covered/
rejected-räknarna och eventuell diagnostisk kantteckning, inte gästprogrammets
CPU-exekvering. En köad draw lämnar en separat deferred-markering som denna
anropare konsumerar utan att klassificera triangeln i förtid. Vid flush används
GPU-raden för pixel-, nollpixel-, fallback-, raster-, LFB-, buffert- och LOD-
räknare samt covered/rejected/empty-raster. Resultaten konsumeras i draw-ordning.

Kantvisualisering, covered/rejected-tracing och raster-state-profilering avvisas
explicit i batch-replacement eftersom de kräver omedelbara triangelresultat.
Befintlig gating utesluter sample-/pixeltracing från GPU-draws.
Debugstatus och rasterprofilstatus har fått egna flushgränser. LFB-läsning/
skrivning, CPU-fallback, fast fill, swap, presentation, pending clear och reset
behåller sina gränser. Probe dränerar också vid tidsmätningens slut och före
snapshot. `HasVideoActivity` kan inte bli falskt för en köad draw eftersom
register-/FIFO-aktivitet redan gör det sant.

## Korrekthetsbevis

Replay 6750→7950, 90 000 CPU-steg per probe-anrop, färgnivå 2, limit 65 536:

- 9 012 draws, 104 submissions, högst 128 draws per batch.
- 18 932 608 startade GPU-invokationer med bbox, inklusive workgroup-avrundning.
  Full dispatch hade 18 899 533 824. Färre invokationer är inte i sig en fartvinst.
- 1 760 251 136 upload-byte och 873 267 200 readback-byte.
- 874 201 088 snapshot-kopierade byte med dirty/sparse. 17 816 sidor jämförs
  och 72 974 336 sidkontroller kan hoppas över (auditläget verifierar dem ändå).
- Full shadow kontrollerar varje draws räknare mot oberoende CPU-rasterisering
  samt färg/djup vid alla 104 flushar. Noll avvikelser.
- Riktig replacement hoppar över CPU-pixelloopen och ger samma slutmaskin.
- Vulkan-synkroniseringsvalideringen rapporterar noll fel i båda lägena.

Full-state-orakel: 98 901 914 dekomprimerade byte, SHA-256
`32f8e9c49dcc5498c044bb602156ef6e82fe5dec64f27c99749f3a6fe298053a`.
Bildhash `0xe87b12da`, 41 swap-kommandon. Loggar/states:
`.build-tmp/gpu-batch-dirty-{SHADOW,REPLACE}{.log,-final.warm.gz}`.

Native-testet täcker bbox av/på, dirty av/på och borttagna texturuppdateringar.
Positiva fall matchar CPU-fixturernas pixlar och oberoende full-dispatch-räknare.
Negativ texturkontroll ger 5 550 pixelavvikelser. Felaktigt ren dirtymask avvisas;
en faktiskt oförändrad draw får hoppa över alla textursidor. Statistikrader
rensar gamla resultat även vid kortare batch och äldre statistik-av-läge.
Artefakt: `.build-tmp/gpu-batch-replace-native.log`. `spirv-val` passerar.

ROM-fria tester omfattar nu 13 läs-/skrivgränser samt separata covered/empty,
buffert- och LOD-räknare för uppskjutna resultat, fem dirty-writer-fall,
nio vanliga limiter och tre batch-statistiklimiter.

Managed negativkontroll förstör CPU-oraklets första räknare och avvisas vid
flush (`cpu=4931 gpu=4930`). Alla fyra förbjudna triangel-diagnostiklägen
avvisas också som avsett. Äldre resident-/per-draw-native-regression passerar.
Replay med replacement-limit 1 och 129 (stopp direkt respektive senare i
strömmen, utan att nå en full kapacitetsbatch) ger också exakt samma full-state-hash:
`.build-tmp/gpu-batch-limit-{1,129}{.log,-final.warm.gz}`.

Normal Release för Probe/UI är återställd och bygger utan fel. Samtliga
ROM-fria tester passerar även där: gränser/räknare/dirty/limiter, 500
färgtillstånd, 393 216 RGB-/65 536 alfafall, 1 156 784 filterfall, 1 048 576
LOD-fall och PCI-trace utan allokering i avstängt läge. Normal replay med
GPU-flaggorna satta och obefintligt nativebibliotek ger samma full-state-hash,
vilket verifierar att GPU-vägen är bortkompilerad. Den enstaka kontrollkörningen
tog 11,0556 s; den ingår inte i capture-buildens medianjämförelse ovan.
Artefakter: `.build-tmp/gpu-batch-replace-normal-build.log`,
`.build-tmp/gpu-batch-replace-normal.log` och motsvarande `-final.warm.gz`.

## Återstående arbete

Behåll färg/djup och textur på GPU:n över kapacitetsgränser. Nuvarande version
betalar fortfarande full bild-readback och återuppladdning för varje batch.
Verifiera fortsatt resultat och räknare innan fler renderlägen tillåts.
Normal Linux-/Android-integration och verkligt speltest återstår; probe-fps
är inte spelets bildfrekvens.

## Reproduktion

Bygg native med `sh tools/GauntletGpuProbe/build.sh` och Probe med
`dotnet build tools/GauntletProbe/GauntletProbe.csproj -c Release --no-restore -m:1 -p:GauntletGpuCapture=true`.
För tidsprov:

```sh
env -u GAUNTLET_GPU_VALIDATION DOTNET_TieredCompilation=0 \
  EUTHERDRIVE_GAUNTDL_GPU_REPLACE=1 \
  EUTHERDRIVE_GAUNTDL_GPU_SHADOW_BATCH=1 \
  EUTHERDRIVE_GAUNTDL_GPU_BATCH_STATS=1 \
  EUTHERDRIVE_GAUNTDL_GPU_BBOX=1 \
  EUTHERDRIVE_GAUNTDL_GPU_INCREMENTAL=1 \
  EUTHERDRIVE_GAUNTDL_GPU_SPARSE_SNAPSHOT=1 \
  EUTHERDRIVE_GAUNTDL_GPU_DIRTY_TEXTURE=1 \
  EUTHERDRIVE_GAUNTDL_GPU_VERIFY_DIRTY=0 \
  EUTHERDRIVE_GAUNTDL_GPU_EXTENDED_COLOR_PATH=2 \
  EUTHERDRIVE_GAUNTDL_GPU_DRAW_LIMIT=65536 \
  scripts/run-gauntdl-probe-warm.sh \
  /home/nichlas/roms/MAME/Midway/Vegas/gauntd 7950 90000 \
  .build-tmp/gaunt-k2-clean2-f6750.warm.gz 6750
```

För korrekthetsprov: byt `GPU_REPLACE=1` mot `GPU_SHADOW=1`, sätt
`GAUNTLET_GPU_VALIDATION=1` och `GPU_VERIFY_DIRTY=1`. För profil: lägg till
`GPU_PROFILE=1` utan validering/audit. Återställ normal Probe/UI efteråt genom
att bygga utan `-p:GauntletGpuCapture=true`.
