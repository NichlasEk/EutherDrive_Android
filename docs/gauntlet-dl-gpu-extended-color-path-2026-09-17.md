# Gauntlet DL: diagnostiskt stöd för färgkombination 0c602c19

## Status

De två efterfrågade TMU-kombinationerna stöds bakom
`EUTHERDRIVE_GAUNTDL_GPU_EXTENDED_COLOR_PATH=1`. Flaggan är av som standard
och påverkar bara capture-bygget. Det är ett verifierat funktionssteg,
inte en ny snabb standardväg.

Gemensamma register: fbz `000b4779`, cp `0c602c19`, alpha `00045119`,
fog `000000c1`. Tillåtna par av TMU-mode:

- `8c24110f` / `8c241acf`
- `80000009` / `8c24110f`

Båda TMU-staterna måste finnas; MAME-färgkombination, fog och aux-depth
krävs. Alpha8-mask och pixeldiagnostik är inte tillåtna för denna väg.
Övriga capture-guards, formatkontroller och synkgränser behålls.

## Exakt shaderändring

RGB är kvantiserad texturfärg multiplicerad med `(iteratedAlpha + 1) / 256`.
Den itererade alfan beräknas med CPU:ns 32-bitars wrap i rad-/pixelgradienten,
aritmetiskt skift 12 och clamp 0…255. Utgående alfa är fortfarande
`Color1Alpha * (textureAlpha + 1) / 256`; fog, djup och blandning använder
befintliga formler för dessa registervärden.

Metadataord 28: 0 = tidigare common-kärna, 1 = nya färgkombinationen.
Ord 116–118: subpixeljusterad startalfa, dA/dX och dA/dY. Gamla captures har
noll i dessa tidigare oanvända ord och behåller sin betydelse. Native avvisar
andra färgvägsnummer. Statistikslot 4 (common-pixlar) ökas endast för väg 0.
`gpuShadowBoundary` redovisar nu kumulativa `extendedDraws`.

Bygg om **Core, nativebibliotek och shader tillsammans**. ABI-numret är
oförändrat eftersom anropssignaturer och metadataformatets storlek är samma;
en gammal shader ska inte användas med den nya experimentflaggan.

## Verifiering

- Shadow: 128 draws med färg/djup och per-draw-räknare jämförda mot CPU,
  noll mismatch och noll Vulkan-synkroniseringsfel.
- Full resident replacement 6750→7950 med dirty-kontrollskanning och Vulkan-
  validering: 3 463 draws, varav 573 nya, `pendingPixels=0`, noll synkfel.
- Den fulla slutmaskinen och samtliga fyra tidsreplayers slutmaskiner matchar
  CPU-oraklet: 98 901 914 byte, SHA-256
  `32f8e9c49dcc5498c044bb602156ef6e82fe5dec64f27c99749f3a6fe298053a`.
  Bildhash `0xe87b12da`, 41 swap-kommandon.
- ROM-fritt test jämför specialformeln mot den befintliga generella CPU-
  färgkombinationen: 393 216 RGB-fall och alla 65 536 textur-/konstantalfapar.
  Det är ett formeltest, inte ett separat hårdvaruorakel eller shaderexekvering.
- Avsiktlig räknarkorruption stoppas med `GPU raster counter mismatch`.
- Native resident-test med äldre captures: oförändrade pixlar, korrekt
  readback/reset-ordning och avvisning av färgväg 2. Vulkan-validering: noll fel.
- `spirv-val` samt capture-boundaries/dirty-writers/runtime-limits passerar.
- Normal Probe/UI är återbyggda utan fel. Formeltest, boundaries/dirty-writers/
  limits och filtertest passerar även där. En normal replay med GPU-flaggorna
  på och avsiktligt obefintligt nativebibliotek ignorerar GPU-vägen och matchar
  hela slutmaskinen (`gaunt-extended-normal-final.warm.gz`).

## Tidsprov och kvarvarande gränser

Samma capture-build, resident + incremental + sparse + dirty + bbox,
GPU-profilering på, limit 65 536, `DOTNET_TieredCompilation=0`. Validering och
dirty-kontrollskanning av under tidsproven. Ordning gammal1/ny1/ny2/gammal2.

| Prov | Flagga av | Flagga på |
|---|---:|---:|
| 1 | 13,2826 s | 13,2993 s |
| 2 | 12,7809 s | 13,0185 s |
| Medel | 13,03175 s | 13,1589 s |

Ingen fartvinst: cirka 0,98 procent längre medeltid i dessa två par.
Antalet GPU-draws ökar från 2 890 till 3 463, men antalet segment/readbacks
förblir 70. Nästa avbrott i kontrollreplayen är:

| fbz | TMU0 | TMU1 | Antal |
|---|---|---|---:|
| `000b4779` | `8c24190f` | `8c241acf` | 35 |
| `000b4779` | `8c24110f` | `8c24110f` | 34 |
| `000b4379` | `8c24190f` | `8c241acf` | 1 |

Alla har samma cp/alpha/fog som ovan. Dessa par/lägen är ännu inte tillåtna.
Nästa steg är att verifiera dem separat och se om faktiska readbacks kan
elimineras; fler GPU-draws ensamt har inte förbättrat tempot här. Normal-CPU-
vägen är fortfarande standard. Ingen Android-prestanda verifierades.

## Körning och artefakter

Bygg enligt GPU-probens README med `-p:GauntletGpuCapture=true` och
`sh tools/GauntletGpuProbe/build.sh`. Lägg till den nya flaggan till befintlig
shadow/resident-körning. Återställ normal Probe/UI efter diagnostiken.
Formeltest: `EUTHERDRIVE_GAUNTDL_TEST_EXTENDED_COLOR_PATH=1 dotnet tools/GauntletProbe/bin/Release/net8.0/GauntletProbe.dll`.

Lokala loggar: `.build-tmp/gaunt-extended-{shadow,audit,old1,new1,new2,old2,negative,native-tests}.log`.
Kontroll- och tidsproven har motsvarande `-final.warm.gz`; captures/ROM-data
checkas inte in.
