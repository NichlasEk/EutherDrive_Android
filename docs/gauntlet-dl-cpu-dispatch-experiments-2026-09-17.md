# Gauntlet DL: små CPU-dispatchförsök

Fortsättning efter CPU-profilen: avgränsade implementationer testas mot
oförändrad normalbuild. Inga GPU-flaggor eller profilerare används under
tidsjämförelserna. Samma replay 6750→7950, warm-snapshot och 90 000 CPU-steg
per probe-anrop, med `DOTNET_TieredCompilation=0`.

## Referens till avkodad instruktion: förkastad

I safe-block-loopen byttes den lokala kopian av `RuntimeSafeInstruction`
mot `ref readonly` direkt till blockets oföränderliga instruktionsarray.
Inga guard-/avbrotts-/gästminneskontroller togs bort eller cachades.

Fyra gamla/nya par kördes med frysta byggkopior, i ordningen
old1/new1/new2/old2/old3/new3/new4/old4:

| Prov | Gammal replaytid | Kandidat |
|---|---:|---:|
| 1 | 11,0439 s | 11,6064 s |
| 2 | 11,3314 s | 11,2218 s |
| 3 | 11,4349 s | 11,2297 s |
| 4 | 11,3782 s | 11,0592 s |
| Medel | 11,2971 s | 11,2793 s |

Skillnaden i medel är endast 0,16 procent och resultaten varierar. Ingen
tillförlitlig fartvinst är påvisad; källändringen återställdes. Samtliga åtta
fullständiga slutmaskiner matchar oraklet exakt.

Separat JIT-disassembly bekräftar att kodgenereringen faktiskt ändrades:
två `vmovups` för 16-byte-kopian försvinner, och metodkoden krymper från
1 320 till 1 307 byte på denna host. Det räcker inte som prestandabevis.
Disassembly kördes utanför de tidsmätta proven.

Artefakter: `.build-tmp/gaunt-ref-{old1,new1,new2,old2,old3,new3,new4,old4}.log`
och motsvarande `-final.warm.gz`. JIT-loggarna är
`gaunt-ref-asm-{baseline,candidate}.log`.

## Ordläsning av RuntimeMainState: förkastad

Fyra byte-läsningar i `RuntimeMainState` ersattes tillfälligt av
`BinaryPrimitives.ReadUInt32LittleEndian` över samma RAM-span. Spelstatus
lästes fortsatt live, utan cache. Samma växlade ordning användes:

| Prov | Gammal replaytid | Kandidat |
|---|---:|---:|
| 1 | 11,0498 s | 11,0203 s |
| 2 | 11,6385 s | 11,1179 s |
| 3 | 11,1540 s | 12,1683 s |
| 4 | 10,9722 s | 11,5541 s |
| Medel | 11,2036 s | 11,4652 s |

Kandidaten är 2,33 procent långsammare i medel, med tydlig variation.
Ingen stabil vinst: även denna källändring återställdes. Alla åtta
fullständiga slutmaskiner matchar samma orakel exakt.
Artefakter: `.build-tmp/gaunt-state-word-{old1,new1,new2,old2,old3,new3,new4,old4}.log`
och motsvarande `-final.warm.gz`; kandidatbygget finns i
`.build-tmp/gaunt-state-word-candidate`.

Ingen runtimeändring behålls från dessa två försök. Nästa optimering bör
angripa större arbete i CPU-rasterloopen eller instruktionsdispatchen;
ytterligare små kodgenereringsändringar behöver samma end-to-end-bevis.
Rapporterad probe-`fps` är inte spel-FPS, och swaps/s mäter swap-kommandon,
inte unika presenterade bilder på hosten.

## Reproducerbara A/B-byggen

Warm-runnern accepterar nu `EUTHERDRIVE_GAUNTDL_PROBE_DLL` som alternativ
byggsökväg. Utan den används samma Release-DLL som tidigare. Detta låter
gamla/nya frysta byggkopior köras växelvis utan ombyggnad mellan proven.
Varje kopia måste innehålla hela Probe-utdatakatalogen, inklusive Core,
runtimeconfig och dependencies, inte bara Probe-DLL:n.

Referensen finns lokalt i `.build-tmp/gaunt-ref-baseline`; ref-kandidaten i
`gaunt-ref-candidate`. Dessa byggkopior, snapshots och ROM-data checkas inte in.

Full-state-orakel: 98 901 914 dekomprimerade byte, SHA-256
`32f8e9c49dcc5498c044bb602156ef6e82fe5dec64f27c99749f3a6fe298053a`.

## Slutverifiering

- Normal Probe och UI byggda efter återställning, utan byggfel.
- ROM-fria test: 11 stream-boundaries, 5 dirty-writers, 9 runtime-limits
  samt PCI-trace av/på godkända; 40 000 avstängda trace-anrop allokerar 0 byte.
- Ny replay med återbyggd standard-DLL matchar hela slutmaskinen ovan:
  11,3227 s, 41 swap-kommandon, bildhash `0xe87b12da`.
  Logg: `.build-tmp/gaunt-dispatch-restored.log`.
- Runnerns shellsyntax godkänd; ogiltig DLL-override avbryter med status 1.
