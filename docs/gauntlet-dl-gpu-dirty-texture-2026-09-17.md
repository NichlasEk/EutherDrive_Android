# Gauntlet DL: skrivspårade textursidor

## Resultat

| Prov | Förberedelser | Replaytid | Swaps/s |
|---|---:|---:|---:|
| Sparse1, full skanning | 1,877 s | 16,3807 s | 2,503 |
| Dirty1 | 0,220 s | 13,1626 s | 3,115 |
| Sparse2, full skanning | 2,034 s | 16,5330 s | 2,480 |
| Dirty2 | 0,216 s | 12,9333 s | 3,170 |

GPU-replayen tar cirka 20,7 procent kortare tid i medel i denna lokala
jämförelse. Alla fyra fullständiga slutmaskiner matchar CPU-oraklet exakt.
Även separat slutlig kontrollskanning + Vulkan-validering matchar exakt,
utan missade sidor eller synkfel. Alla GPU-sessioner har `pendingPixels=0`.
Detta är fortfarande två korta upprepningar på en host, inte bevis för
generell spelbarhet. Swaps/s räknar swap-kommandon, inte host-presenterade
unika bilder. Experimentet förblir valbart och avstängt i normalbygget.

Återställt normalbygge gav 3,626 swaps/s i en separat slutkontroll, alltså
fortfarande snabbare än GPU-provet. Det är inte en växlad CPU/GPU-benchmark.
Probe/UI bygger utan fel. Elva gränsfall, nio limit-fall, fem skrivspårnings-
fall och PCI-regressionen passerar. Normal replay med alla experimentflaggor,
ogiltig draw-limit och obefintligt bibliotek ignorerar GPU-vägen och matchar
full-state-oraklet (`gpu-dirty-normal-final.warm.gz`). Batch-ABI-regression,
negativ mask-/texturkopiekontroll och `spirv-val` passerar också.

## Implementation och avgränsning

`EUTHERDRIVE_GAUNTDL_GPU_DIRTY_TEXTURE=1` markerar ändrade fysiska 1 KiB-
textursidor i Core och skickar 8 192 markeringar till native per draw.
Det kräver resident replacement, incremental uploads och sparse snapshots.
Flaggan är avstängd som standard. Native har ett nytt valbart ABI-v4-anrop
`gauntlet_shadow_dirty_draw`; gamla anrop fungerar fortfarande som förut.
Bygg om både native och capture-Core.

Auditen av backendens `_textureMemory` hittar en vanlig gästskrivpunkt:
`WriteTextureByte`. Markeringen sker efter den verkliga fysiska skrivningen,
bara om värdet ändras. Maskning, bevarade/suppressade skrivningar och TMU-
mappning är redan avgjorda där. Normala byggen kompilerar bort anropet.
Spårningen ligger i GPU-sessionen och påverkar inte warm-state-formatet.

Native hoppar över innehållsjämförelsen för rena textursidor. NCC-tabellens
två sidor jämförs fortfarande i sin helhet eftersom färdiga LUT-data kan
ändras genom register/state-val. Full uppladdning vid varje segmentstart
behålls. Markeringar nollställs först efter lyckad synkron draw. Backend-reset
stänger gammal session och skapar ny backend. Probe snapshot-load stänger
en aktiv GPU-session före direkt arrayöverskrivning; diagnostisk GPU-körning
återupptas inte i den stängda backend-instansen.

Probe-orakelimporter som skriver direkt i texturarrayen sker före första
drawen och täcks av första fulluppladdningen. Godtyckliga reflection-pokes
under en aktiv session stöds inte. Detta är ett diagnostiskt Linux-prov,
inte generell produktionsintegration eller verifierad Android-backend.

## Kontrollreferens

`EUTHERDRIVE_GAUNTDL_GPU_VERIFY_DIRTY=1` kontrollerar även omarkerade sidor
mot snapshoten och avbryter vid en missad ändring. Det återinför den stora
skanningen och ska inte användas för att bedöma hastigheten.

En replay 6750→7950 med limit 4 096 omfattar 2 890 stödda GPU-draws i
70 segment. Mellan dessa draws hoppas 23 101 440 sidjämförelser över och
5 640 NCC-sidor jämförs. Just detta fönster har inga ändrade textursidor
inne i GPU-segmenten; fulla segmentstarter täcker CPU-fallback/intervening
skrivningar. Det bevisar inte alla framtida texturströmmar.

ROM-fria tester av den verkliga byte-skrivaren täcker första/sista byte,
sidgräns, fysisk wrap och oförändrat värde. De passerar i capture-build och
kontrollerar att normalbygget inte markerar några sidor. Native-testet med
syntetisk texturflytt upptäcker en avsiktligt tom dirty-mask, varefter korrekt
mask ger exakt samma pixelorakel och reset/readback-ordning som tidigare.

Full-state-orakel: 98 901 914 dekomprimerade byte, SHA-256
`32f8e9c49dcc5498c044bb602156ef6e82fe5dec64f27c99749f3a6fe298053a`.
Framehash `0xe87b12da`. Prestandaproven kör samma capture-build, snapshot,
90 000 CPU-steg per probe-anrop, `DOTNET_TieredCompilation=0` och profilering
på, i ordningen sparse1/dirty1/sparse2/dirty2. Bbox är på i samtliga prov.
Verifieringsskanning och Vulkan-validering är avstängda under tidsjämförelsen.

## Fortsättning

Snapshot-skanningen är inte längre den stora kvarvarande kostnaden i detta
prov. Native-förberedelser är omkring 0,22 s och draw-fence-väntan omkring
0,65–0,69 s. Nästa steg bör mäta återstående CPU-emulering/rasterisering och
övergångar till CPU innan fler stora ändringar görs. GPU-räknarnas omedelbara
sidoeffekter hindrar fortfarande naiv asynkron batchning.

## Artefakter

`.build-tmp/gpu-dirty-v2-{sparse1,dirty1,sparse2,dirty2,audit}.log` och
motsvarande `-final.warm.gz`. `gpu-dirty-audit.log` är ett tidigare godkänt
kontrollprov innan verifieringsflaggans avläsning flyttades ut ur sidloopen;
använd v2-serien för slutresultatet. `gpu-dirty-abi.log` innehåller native-
negativtestet. `gpu-dirty-capture-tests.log` innehåller skrivspårningstesterna.
