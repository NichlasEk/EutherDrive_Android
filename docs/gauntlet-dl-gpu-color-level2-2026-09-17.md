# Gauntlet DL: andra nivån av GPU-färglägen

## Verifierat funktionssteg, inte snabb standardväg

`EUTHERDRIVE_GAUNTDL_GPU_EXTENDED_COLOR_PATH=2` behåller nivå 1 och tillåter
ytterligare tre observerade kombinationer för cp `0c602c19`, alpha `00045119`
och fog `000000c1`:

| fbz | TMU0 | TMU1 |
|---|---|---|
| `000b4779` | `8c24110f` | `8c24110f` |
| `000b4779` | `8c24190f` | `8c241acf` |
| `000b4379` | `8c24190f` | `8c241acf` |

Skillnaden `b4379` är avstängd aux/djupskrivning (bit `0x400`). Befintlig
shader använder redan metadataord 21 för denna mask; inga shaderformler
behövde ändras. Native tillåter detta fbz endast med färgväg 1.
Nivå 1 behåller sin gamla tillåtelselista; 0/okända nivåer tillåter ingen
utökad färgväg. Normalbygget kompilerar fortfarande bort GPU-anropet.

En ren registerkontroll `IsGpuExtendedColorState` kan nu testas ROM-fritt.
500 tillåtna/otillåtna kombinationer passerar tillsammans med 393 216 RGB-
och 65 536 alfafall. Övriga MAME-/format-/trace-/depth-guards kvarstår utanför
registerkontrollen och omfattas fortsatt av replay/capture-testerna.

## Kontrollresultat

- 128 shadow-draws: färg/djup och per-draw-räknare matchar CPU, inga Vulkan-
  synkroniseringsfel. Första segmentet växer från 30 till 111 draws.
- Full replacement med Vulkan-validering och dirty-kontrollskanning:
  9 012 GPU-draws, 6 122 utökade, noll synkfel, `pendingPixels=0`.
- Kontrollreplayen och samtliga fyra tidsreplayer matchar hela slutmaskinen:
  98 901 914 byte, SHA-256
  `32f8e9c49dcc5498c044bb602156ef6e82fe5dec64f27c99749f3a6fe298053a`.
  Bildhash `0xe87b12da`, 41 swap-kommandon.

Tidsproven använder samma capture-build, shader/native, warm-state 6750→7950,
90 000 steg, `DOTNET_TieredCompilation=0`, resident/incremental/sparse/dirty/bbox,
limit 65 536 och GPU-profilering. Validering och kontrollskanning är av.
Ordning nivå1-1/nivå2-1/nivå2-2/nivå1-2; inga samtidiga byggen eller hashningar.

| Prov | Nivå 1 | Nivå 2 |
|---|---:|---:|
| 1 | 13,0720 s | 14,5142 s |
| 2 | 13,0201 s | 14,3637 s |
| Medel | 13,04605 s | 14,43895 s |

Nivå 2 är cirka 10,7 procent långsammare. Draw-fence-väntan ökar från
0,720–0,733 s till 1,322–1,344 s. Antalet segment/readbacks ligger kvar på 70.
Det är därför fortsatt ett avstängt diagnostiskt utvecklingssteg, inte en
prestandaoptimering för användaren. Inga Android-prestandaprov har gjorts.

## Nästa avbrott

Alla 70 gränser är fortfarande `unsupported-textured-state`:

| fbz | cp | TMU0 | TMU1 | Antal |
|---|---|---|---|---:|
| `000b4779` | `0c602c19` | `8c24110f` | `8c2410cf` | 34 |
| `000b4779` | `0c602439` | `8c24190f` | `8c241acf` | 28 |
| `000b4379` | `0c603430` | `80000009` | `8c2411ce` | 7 |
| `000b4779` | `0c602c19` | `8c24110f` | `00000000` | 1 |

Den tredje gruppen har även alpha `00040219` och fog `000000c0`.
TMU-mode 0 bevisar inte om TMU-staten saknas; det måste undersökas innan
ytterligare allowlist-utökning. Andra färgkombinationer kräver egen verifiering.

Den större täckningen gör kostnaden för en submission/väntan per draw mer
tydlig. Fortsatt arbete bör därför mäta synkroniseringskostnaden, inte bara
tillåta fler lägen och förutsätta att det blir snabbare.

Artefakter: `.build-tmp/gaunt-level2-{shadow,audit,old1,new1,new2,old2}.log`
och fulla kontroll-/tids-slutstater med suffix `-final.warm.gz`.

Normal Probe/UI är återbyggda utan fel. Allowlist-/färgtest, GPU-boundaries,
dirty-writers, runtime-limits, bilinjärt filter, LOD och PCI-trace passerar.
Normal replay med nivå 2, pollning och avsiktligt obefintligt nativebibliotek
ignorerar GPU-vägen och matchar samma slutmaskin (`gaunt-level2-normal.log`).
