# Gauntlet DL: batch-shadow med separata draw-resultat

## Verifierad milstolpe

Samma 9 012 draws som färgnivå 2 kan nu köras i 104 GPU-submissions med
individuell kontroll av varje draws räknare. Slutbilden kontrolleras vid
varje flush. Alla kontroller och Vulkan-synkroniseringsvalideringen passerar.
Hela slutmaskinen är byte-exakt.

Detta är **shadow**, inte batchad replacement: CPU:n rasteriserar fortfarande
allt som oberoende referens. Ingen FPS-/spelbarhetsvinst är visad av denna
körning. Batchad replacement är fortsatt explicit avvisad av sessionen.

## Implementation

- `EUTHERDRIVE_GAUNTDL_GPU_BATCH_STATS=1` kräver `GPU_SHADOW_BATCH=1`.
  Native-exporten `gauntlet_shadow_batch_stats_version()` måste returnera 1.
  Huvud-ABI förblir 4; äldre bibliotek saknar capability och avvisas för detta läge.
- Outputbufferten rymmer 128×16 statistikord efter färg/djupytan. Ordinarie
  per-draw-väg använder fortfarande bara första raden. Gamla batcher utan
  statistik fungerar också.
- Native tilldelar varje köad draw en separat offset i metadataord 119.
  Inkommande metadata måste ha ord 119 = 0; anroparen får inte välja index.
  Shadern adresserar sin statistikrad via denna offset.
- Vid flush nollställs hela statistiktabellen, inklusive oanvända rader.
  Befintliga draw-/texturbarriärer och den ordinarie fence-väntan behålls.
- CPU-deltan och covered/zero-returvärden sparas enbart i managed kod. De
  skickas aldrig till shadern. Alla 17 befintliga räknarkontroller görs efter
  flush; LFB-/buffertskrivräknare jämförs mot motsvarande GPU-rasterräknare.
- En full batch flushas efter 128 draws. Total diagnostiklimit kan i detta
  läge vara upp till 65 536; utan den nya flaggan gäller gamla batchgränsen.
- Nästa draw får fortsätta direkt även om dess bbox är mindre än tröskeln
  för en ny GPU-sekvens. En vanlig CPU-/rendergräns avbryter denna fortsättning
  även om den föregående batchen redan flushats. Reset stänger den också.

## Replay och regressioner

Slutlig replay: `.build-tmp/gaunt-batch-stats-v2-full.log`, 6750→7950,
90 000 steg, `DOTNET_TieredCompilation=0`, färgnivå 2, total limit 65 536,
Vulkan-validering på. 104 batcher innehåller totalt 9 012 draws, högst 128
per batch. Gränser: 70 ostödda renderlägen och 34 kapacitetsgränser.
Alla 104 slutytor och samtliga per-draw-räknare matchar CPU:n.

Den tidigare resident-replacement-vägen hade 9 012 draw-submissions plus
70 readback-submissions. Jämförelsen visar möjligheten till färre väntningar,
inte en direkt tidsjämförelse: batch-shadow har annan överföringsstrategi
och kör dessutom hela CPU-oraklet. Den validerade shadow-replayen tog 29,56 s.

Slutmaskin: 98 901 914 dekomprimerade byte, SHA-256
`32f8e9c49dcc5498c044bb602156ef6e82fe5dec64f27c99749f3a6fe298053a`,
bildhash `0xe87b12da`, 41 swap-kommandon.

Native-tester (`gaunt-batch-stats-native-v2.log`):

- två separata draw-dispatcher ger statistikreferens för två olika batchrader;
- slutbild jämförs fortsatt mot CPU-genererade fixtures;
- texturflytt mellan draws ger korrekt bild; borttagna uppdateringar ger
  5 550 felaktiga pixlar och upptäcks av negativkontrollen;
- egna statistikoffsetar avvisas; raderna blandas inte ihop;
- en kortare följande batch rensar föregående resultat, liksom äldre
  batchläge med statistik av;
- äldre resident-/per-draw-test, djupmask och readback/reset-ordning passerar;
- noll Vulkan-synkroniseringsfel, `spirv-val` passerar.

Managed negativkontroll med `GPU_COUNTER_CORRUPT_ORACLE=1` stoppas vid flush:
`GPU batch counter mismatch draw=0 index=0 cpu=4931 gpu=4930`.
ROM-fria boundary-/dirty-writer-/limit-tester inkluderar tre nya totallimiter
och en CPU-LFB-läsning som måste avbryta kapacitetsfortsättningen.

Ett tidigare mellanprov (`gaunt-batch-stats-full.log`) matchade också, men
släppte små draws till CPU efter kapacitetsgränsen och täckte bara 7 869 draws.
Använd v2-provet för den slutliga täckningen och jämförelsen.

Normal Release för Probe och UI har återställts och byggts. Boundary-/dirty-
writer-/limit-tester, 500 färgtillstånd, 393 216 RGB-/65 536 alfafall,
1 156 784 filterfall, 1 048 576 LOD-fall samt PCI-tracetester passerar.
Normal replay kördes med samtliga nya GPU-flaggor och en avsiktligt obefintlig
nativebibliotekssökväg: GPU-vägen är bortkompilerad och slutmaskinen har samma
SHA-256 som ovan. Logg: `.build-tmp/gaunt-batch-stats-normal.log`.

## Körning

Bygg om capture-Core, nativebibliotek och shader tillsammans enligt GPU-README.
Sätt `EUTHERDRIVE_GAUNTDL_GPU_SHADOW=1`,
`EUTHERDRIVE_GAUNTDL_GPU_SHADOW_BATCH=1`,
`EUTHERDRIVE_GAUNTDL_GPU_BATCH_STATS=1`,
`EUTHERDRIVE_GAUNTDL_GPU_EXTENDED_COLOR_PATH=2` och
`EUTHERDRIVE_GAUNTDL_GPU_DRAW_LIMIT=65536` på warm-runnern.
Sätt **inte** replacement/resident/dirty-flaggorna för detta shadow-prov.
Återställ normal Probe/UI efteråt. Native-test:
`python3 tools/GauntletGpuProbe/test-shadow-batch.py .build-tmp/gpu-stream-first`.

## Nästa steg mot snabb replacement

1. Låt replacement konsumera de verifierade per-draw-resultaten i ordning,
   först vid verkliga synkgränser eller full batch. Auditera alla läsare av
   räknare/covered-returvärden; diagnostisk kantteckning får inte köra för tidigt.
2. Behåll GPU-färg/djup mellan kapacitetsbatcher i stället för att återuppladda
   hela CPU-ytan. Använd dirty-texturuppdateringar även i batchvägen.
3. Använd bbox per draw i batchen. Nuvarande batch-dispatch använder fortfarande
   hela fysiska ytan, till skillnad från den optimerade per-draw-vägen.
4. Först därefter jämförs riktig replacement med normal CPU i växlade prov.
   Snapshot, LFB-läsningar/skrivningar, fallback, presentation och avstängning
   ska förbli kontrollerade synkgränser.
