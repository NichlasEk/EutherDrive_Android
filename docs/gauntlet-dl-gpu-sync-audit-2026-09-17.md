# Gauntlet DL: synkroniseringsaudit och nästa GPU-läge

## Ny mätning efter packad CPU-filtrering

Två diagnostiska resident/dirty/bbox-replayer 6750→7950 kördes från aktuell
Core efter `260909e0`, med 90 000 CPU-steg, `DOTNET_TieredCompilation=0`,
GPU-limit 4096 och GPU-profilering. Den andra inkluderar ny avbrottsdiagnostik.
Ingen runtime-synkronisering ändrades.

| Mätning | Första audit | Med lägesdiagnostik |
|---|---:|---:|
| Replaytid | 12,8091 s | 12,7320 s |
| Host draw-fence-väntan | 0,6344 s | 0,6214 s |
| Väntans andel av replay | 4,95 % | 4,88 % |
| Stödda draws | 2890 | 2890 |
| Segment/pixel-readbacks | 70 | 70 |
| Submissions | 2960 | 2960 |

Alla segment slutar med `unsupported-textured-state`, inte med CPU-LFB-läsning
eller presentation. Segmentstorlek: min 6, max 114, medel 41,29 draws.
Med nuvarande gränser och en hypotetisk gräns på 128 draws skulle antalet
draw-submissions kunna räknas till 70, plus 70 readbacks. Detta är bara
en schemaläggningsräkning: den löser inte de omedelbara sidoeffekterna nedan.

Att enbart subtrahera uppmätt draw-väntan ger 12,11–12,17 s. Det är inte en
prognos eller generell gräns för batchning, som också kan ändra andra kostnader.
Device-tider överlappar host-väntan och får inte adderas till den. Pixel-readback-
väntan och CPU-kopiering ingår inte i draw-väntan. Tidigare normal-CPU-mätning
ligger omkring 10,89 s efter filterändringen, men är inte växlad med denna audit.

## Exakta lägen som avslutar segmenten

Gemensamt: fbz `000b4779`, cp `0c602c19`, alpha `00045119`, fog `000000c1`.

| TMU0 mode | TMU1 mode | Avbrott |
|---|---|---:|
| `8c24110f` | `8c241acf` | 36 |
| `80000009` | `8c24110f` | 34 |

Detta visar den första ostödda drawen vid varje segmentgräns, inte alla draws
som därefter kör på CPU. Att stödja dessa två kombinationer garanterar därför
inte att samtliga readbacks försvinner. Andra villkor än registervärden kan
också stoppa capture; nya shaderlägen måste verifieras separat.

## Varför väntan inte bara kan tas bort

`TryApplyGpuReplacement` läser 16 statistikord omedelbart och uppdaterar
pixel-, nollpixel-, fallback-, LOD- och bufferräknare. `coveredAny` går via
`FillTexturedTriangle` till anroparens covered/rejected-räknare och eventuell
diagnostisk kantteckning. `EndGpuDrawCapture` kontrollerar dessutom varje
draws returvärden och räknardelta. Resident färg/djup återläses redan bara
vid segmentgränser.

Nuvarande batch-shadow fortsätter köra CPU-raster och kan därför inte användas
som asynkron replacement. Native har ett gemensamt statistikområde, inte
per-draw-resultat för uppskjutna returvärden. En riktig replacement-batch
behöver per-draw-statistik, ordnad tillämpning av sidoeffekter och en audit
av varje läsare av dessa räknare. Gäst-/CPU-buffertläsningar, fallback,
swap, snapshot och avstängning måste fortfarande vara synkgränser.

## Nästa avgränsade implementation

1. Lägg till exakt CPU-orakel/verifiering för färgkombination `0c602c19` med
   de två observerade TMU-paren; anta inte att common-kärnans RGB/alphaformler gäller.
2. Utöka diagnostisk shader/capture först i shadow-läge. Behåll nuvarande
   per-draw-räknarkontroller, texturuppdateringar och synkgränser.
3. Prova replacement och mät om segment/readbacks faktiskt minskar. Kräv
   full-state-match och negativa kontroller före prestandaslutsats.
4. Ta därefter ställning till uppskjuten per-draw-statistik/batchning.

## Verktyg och verifiering

`summarize-gpu-sync.py` läser en avslutad synkron resident-session, kontrollerar
segment-/draw-/submission-samband och redovisar avbrott samt det uttryckligen
hypotetiska batchantalet. Fem tester täcker normalfall, ofullständiga/felaktiga
sessioner, dubbla sessioner, ogiltig batchgräns och valbar lägesdiagnostik.

`gpuUnsupportedState` loggas endast i aktiv shadow/replacement-capture när
GPU-profilering är på; normalbygget anropar inte capture-hooken.

Båda fullständiga slutmaskinerna matchar 98 901 914 byte och SHA-256
`32f8e9c49dcc5498c044bb602156ef6e82fe5dec64f27c99749f3a6fe298053a`,
bildhash `0xe87b12da`. Capture-testernas 11 boundaries, 5 dirty-writers och
9 limits passerar. Normal Probe/UI har därefter byggts igen utan fel;
normalbyggets boundaries/dirty-writers/limits och 1 156 784 filterfall passerar.
GPU-experimentet förblir avstängt i normalbygget.

Artefakter: `.build-tmp/gaunt-sync-{audit,states}.log` och motsvarande
`-final.warm.gz`. Kör `python3 tools/GauntletProbe/summarize-gpu-sync.py LOG`.
