# Gauntlet Dark Legacy - plan till spelbar grafik

Datum: 2026-07-11  
Aktiv checkpoint: `fa40d372` (`Model Gauntlet standard FIFO generations`)

## Mal

Gauntlet Dark Legacy ska starta kallt, visa igenkannbar och stabil grafik och
kunna spelas i minst en bana i den vanliga Android-vagen. Losningen ska bygga
pa korrekt Voodoo/FIFO-beteende, inte pa kommandospecifika suppressions eller
visuella specialfall.

## Nuvarande bevislage

- Type3-vertexdata ateranvands i fel ringgeneration som Type1-huvudet
  `0x432b87d1`.
- Type5/LFB-payload ateranvands och kan skapa hundratals miljoner felaktiga
  LFB-skrivningar.
- Type4-payload ateranvands som `0x0104824c` och utloser fastfill.
- Type1-payload `0x1fffbca9` ager ett senare fill/swap-clear-lager.
- Klassvisa Type1/3/4/5-prober bevisar agarskapet men kan svalta legitim
  geometri. De ar diagnostik, inte slutarkitektur.
- En gemensam, default-off standard-FIFO-generationsmodell finns i
  `fa40d372`.

## Fas 1 - verifiera standard-FIFO-generationer

1. Kor den gemensamma generationsmodellen ensam fran den rena f520-snapshoten
   till f700.
2. Spar producentgeneration, konsumentgeneration, `cmdFifoRdPtr`, Type0-jumps
   och lagrad slotgeneration.
3. Ratta varje kvarvarande maskerad vag som flyttar konsumenten till fel
   generation.
4. Krav exakt generationsmatchning i readiness utan att legitim trafik
   blockeras.
5. Verifiera att riktiga `0x0180a8cb` Type3-paket fortsatter avkodas och att
   FIFO-djupet inte vaxer permanent.

Godkant nar `0x432b87d1`, `0x0000fffd`, `0x0104824c` och `0x1fffbca9` inte
langre exekveras fran gamla generationer, utan klasspecifika filter.

## Fas 2 - en gemensam paketkarta

1. Lagra header, body, paketslut och skrivgeneration i gemensam slotmetadata.
2. Koppla konsumentens readiness och advance till denna metadata.
3. Jamfor mot Type1/3/4/5-proberna som regressionsorakel.
4. Ta bort duplicerad klass-state nar den gemensamma modellen ger samma skydd.
5. Behall proberna tillfalligt som default-off diagnostik.

Godkant nar legitim Type3-geometri fortsatter, gamla body-ord aldrig blir nya
headers och `depth/valid` atergar mot noll.

## Fas 3 - aterstall renderingskedjan

1. Profilera sista pixelagare pa den generationsrena bilden.
2. Verifiera setup-trianglar, direkta trianglar, fastfills, swaps samt
   front/back-buffer.
3. Avaktivera gamla suppressionsflaggor en i taget.
4. Kontrollera koordinater, cliprect, raster-state och TMU-register.
5. Jamfor f700, f900 och senare frames for verklig progression.

Godkant nar bilden visar stabil scengeometri i stallet for enfargade falt,
kilar, brus eller kvarvarande laddningsremsor.

## Fas 4 - modeller och texturer

1. Verifiera att BGLoadModel-data kopplas till ratt runtime-objekt.
2. Kontrollera att texturuppladdningar inte skriver over kommandodata.
3. Matcha aktiva texturagare mot ratt modell och material.
4. Kontrollera format, LOD, palette/NCC, basadress och koordinatskalning.
5. Ta bort texture-remaps som inte langre behovs.

Godkant nar igenkannbara Gauntlet-objekt renderas med stabila texturer.

## Fas 5 - spelbarhet

1. Kor forbi attract- och startsekvensen.
2. Verifiera coin, start och spelkontroller.
3. Kontrollera kontinuerlig rendering under rorelse.
4. Testa watchdog, frysningar, krascher och ljudsynk.
5. Skapa en ny varm snapshot vid den forsta stabila spelvarlden.

Godkant nar en bana kan startas, styras och koras i flera minuter.

## Fas 6 - ordinarie Android-vag

1. Flytta verifierade fixar fran probe till den ordinarie backend-vagen.
2. Ta bort obligatoriska experimentflaggor och kommandospecifika filter.
3. Testa kallstart i headless och Android.
4. Verifiera snapshot-kompatibilitet och display-buffer.
5. Dokumentera ROM-krav och ett kort reproducerbart testkommando.
6. Commit och push efter varje verifierad etapp.

## Definition av klart

- Kallstart fungerar.
- Attract/startbilden ar igenkannbar.
- Minst en bana ar spelbar.
- Geometri och texturer ar stabila under rorelse.
- Inga payloadord exekveras fran fel FIFO-generation.
- Inga obligatoriska experimentflaggor eller kommandospecifika suppressions
  behovs.
- Android visar samma korrekta grafik som GauntletProbe.
- Resultatet har skarmdump, frame hash och reproducerbar testinstruktion.

## Kritisk vag

`generationsmodell -> korrekt FIFO-flode -> riktig geometri -> texturer -> spelbarhet -> Android`

Ga inte tillbaka till fler adressremaps eller visuella specialfall innan den
gemensamma generationsmodellen ar verifierad.

## Iteration 2026-07-11 - generationswrap

- Standardvagens gamla `storageIndex == 0` wrap-clear ar identifierad som en
  direkt generationsforstorare och ar bortkopplad endast under experimentet.
- Konsumenten fortsatter nu omaskerad (`cmdrd=0x18410`) och lagrade slots visar
  nyare producentgenerationer, exempelvis `stored=0x61034`.
- En enkel header-slot-catchup ar inte tillracklig: headern kan vara gammal
  medan ett senare body-slot i samma paket redan ar overskrivet av en nyare
  generation.
- Nasta steg i Fas 1 ar darfor hel-paket-validering: alla ord fran header till
  paketslut maste ha sammanhangande logiska index i samma generation. Vid
mismatch ska konsumenten flyttas till nasta registrerade header, aldrig till
ett godtyckligt payloadord.

## Iteration 2026-07-11 - hel-paketkarta

- En gemensam packet map registrerar plausibel header, body och logiskt
  paketslut for samtliga paketklasser.
- Readiness kraver att hela paketintervallet har exakt sammanhangande lagrade
  logiska index.
- f700 registrerar `41946` headers och `321959` body-ord, men hittar bara tva
  kompletta resync-kandidater och `205303` missar.
- Den globala producentsekvensen bryts av separata direkta/bulk-anrop innan
  senare paket ar kompletta. En full ringskanning vid varje miss ar dessutom
  for dyr (`fps=3.16`).
- Nasta Fas 1-iteration ska halla packet assembly per producentstrom och
  bulkgrans, samt indexera kompletta headers direkt. Readiness ska da kunna
  valja nasta kompletta header utan ringskanning.

## Iteration 2026-07-11 - producentlokal assembly

- Packet assembly ar nu separerad per skrivande CPU-PC.
- Ett direkt `SortedSet`-index over kompletta headers ersatter ringskanning och
  aterstaller cirka `4.7 fps`.
- f700 ger `4034` resyncs och tar bort de stora kil-artifakterna.
- Type0-trace visar korrekt jump-skalning; generationsspridningen orsakas i
  stallet av att feltolkade FIFO-paket skriver FIFO:s egna kontrollregister och
  nollstaller write-generationen.
- I generationslaget ignoreras nu endast interna packet-writes till FIFO:s
  kontrollbank. Externa MMIO-skrivningar bevaras. Release-bygget ar verifierat;
  nasta korning ska mata stabil write-base och komplett-header-span.

## Iteration 2026-07-11 - bevarad producentgeneration

- Orsaksraknare visar fyra riktiga producent-wraps men tre skrivningar till
  FIFO-basregistret fran `pc=0xffffffff800fe7c4`. De tre aterstallningarna gav
  tidigare nettobasen `0x10000` medan konsumenten stod pa `0x40000`.
- Registertracen visar en omojlig mellanliggande fonsterdefinition
  (`base=0xc8000`, `end=0x01000`) foljd av tva nollskrivningar mitt i aktiv ko.
- Standard-generationslaget bevarar nu logisk write-generation och senaste
  ringposition over basregisterskrivningar. Ordinarie och MAME-vag ar
  oforandrade.
- f700 med endast generationsflaggan slutar med producentbas `0x50000`, sista
  kompletta header `0x5840a` och konsument `0x58410`. Paketantalet okar fran
  `108643` till `110937` och frame hash andras fran `0x51641411` till
  `0x128080b4` utan renderkollaps.
- Fas 1:s stora producent/konsument-generationsfel ar darmed stangt i denna
  kontroll. Nasta kontroll ar pixelagare och paketstopp vid den sex ord stora
  svansen efter sista kompletta headern, foljt av verklig geometriprogression.
- Profilkorningen
  `logs/gauntlet/gauntdl-f700-generation-preserved-profile-r1.log` bekraftar
  att synlig buffer 0 nu domineras av setup-texturerad `cmd=0x0180a8cb` fran
  `pc=0x800c4e5c`. Den senaste agaren ligger generationsrent vid
  `rd=pkt=0x583d7`; den roda brus-/remsbilden ar alltsa nu en setup/TMU-fraga,
  inte bevis for den tidigare flera-generationer-stora FIFO-forskjutningen.
- En generationsren upload-target-korning i
  `logs/gauntlet/gauntdl-f700-generation-preserved-upload-targets-r1.log`
  visar att Type5 nar de avsedda malsidorna, men att RAM-kallorna for
  `targetWord=0x100..0x1400` och `0x7d00..0x7f00` till stor del ar nollade.
  Exempelvis har `source=0xffffffff80312da4` `nz0/64`, medan motsvarande
  diskutsnitt ar tatt och icke-noll.
- Gamla breda diskordsersattningar ska inte aterupptas: de behandlade ra
  assetbytes som FIFO-struktur och skapade registerbrus. Nasta Fas 4-grans ar
  i stallet producenten/hydreringen av descriptorparet kring
  `0xffffffff80312998/0xffffffff803129a4`, och dess avsedda source/extent.

## Iteration 2026-07-11 - kall hydrering och source extent

- Den nuvarande partiella hydreringen kopierar upp till `0x9f60` byte men
  placerar indexkallor med bara `0x2000` bytes mellanrum. Ett kallt
  `0x20000`-strideprov eliminerar overlap men visar fortfarande tre
  brusremsor och `Loading Game.` vid f700 (`frameHash=0x7e8f9588`).
- Body-only-hydrering med samma stride ar kraftigt negativ: f300 producerar
  `7436357` FIFO-ord, bara `19` Type3-paket och inga tackta texturerade
  trianglar (`frameHash=0x4ef1de5d`). Containerhuvudet far inte hoppas over.
- Full containerhydrering ar strukturellt stabil och generationsren vid f300
  (`wb=0x50000`, sista header `0x5677a`, lasare `0x56780`), men bilden ar
  fortfarande samma brusremsor (`frameHash=0xf8321b56`).
- Slutsats: overlap och for kort extent ar verkliga brister men inte den sista
  kopplingen. Nasta Fas 4-steg ska folja bundle-recordets `+0x08/+0x0c`-falt
  och stride-tabellerna som valjer varje per-page source/extent; varken hela
  containern eller dess body ska matas platt till uploadservicen.

## Iteration 2026-07-11 - sen page selection

- En ny default-off `EUTHERDRIVE_GAUNTDL_TRACE_TEXTURE_UPLOAD_PAGE_SELECTION`
  loggar sidvalet vid `0x801095c0` och source-additionen vid `0x80109620`.
  Valfria `SOURCE_MIN/SOURCE_MAX`-filter gor den sena bundlen sparbar utan att
  tracebudgeten forbrukas av den tidiga `0x802e...`-kedjan.
- Den normala bundlen avancerar deterministiskt med helper-resultat som
  `0x1000`, `0x4000`, `0x800` och mindre mip-steg. Detta ar inte en enkel
  konstant-stridebugg.
- Den sena problemkedjan ar exakt:
  `source=0xffffffff80312998`, `desc08=3`, `desc0c=0`, helper-resultat
  `v0=0x10000`. Caller behandlar alltsa `font_story`-descriptorn som en
  64 KiB textsida.
- Lag-nivan foljer redan descriptorstartens `+0x0c`-pekare till
  `0xffffffff803129a4`; den gamla pointer-start-flaggan ar darfor bitidentisk
  med generationskontrollen (`frameHash=0x128080b4`). Felet ar att de 64 KiB
  efter pekaren ar ohydrerade, inte att descriptorordet sjalvt laddas upp.
- Ett varmt index-9/full-container-overwrite ar inert eftersom f520-state redan
  har byggt den gamla kallkedjan. Nasta kausala kontroll maste byggas kallt och
  bevara `font_story`-kallans ursprungliga langd/assetkoppling innan
  parsersteget ersatter den med allocator-descriptorn.

## Iteration 2026-07-11 - fontobjektets producent

- Dagens kalla preset visar att index 9 har langd `0` redan som `credits` och
  senare som `font_story`. Den historiska `0x2006f`-langden tillhor en aldre
  experimentgren och far inte anvandas som aktuell invariant.
- En ny default-off `EUTHERDRIVE_GAUNTDL_TRACE_TEXTURE_SOURCE_CALL_A3_PRODUCER`
  foljer ett konfigurerbart mal genom valfritt `PC_MIN/PC_MAX`-fonster.
- `0x800546f0..0x80054784` tar emot `a3=0x80312998` oforandrat; det ar en
  konsument, inte producenten.
- Callerkedjan visar den verkliga agaren: `0x800548fc` anropar allocatorn och
  far `v0=0x80312998`, varefter `0x80054900` lagrar objektet i global
  `0x8019d1f0`. `0x800549ec` skickar samma objekt vidare som `a0`.
- `0x80312998` ar darmed ett legitimt allokerat font/renderobjekt. Det ska inte
  ersattas platt med WTR-header, WTR-body eller en annan BGLoadModel-kalla.
  Nasta Fas 4-steg ar init/QIO-kedjan efter allokeringen: identifiera vem som
  ska fylla den 64 KiB payload som borjar vid objektets `+0x0c` och varfor den
  forblir nollad.
# 2026-07-11: font_story source is a live render object, not an unhydrated asset

- The late `0x80312998` source is a legitimate `0x20000`-byte allocation stored
  through global `0x8019d1f0`; replacing it wholesale with a WTR body/header is
  therefore not a valid repair.
- The constructor at `0x80054480..0x800546ef` contains no nested calls. It
  computes layout directly and retains the allocated object in `a3`.
- A focused main-RAM write watch over `0x80312998..0x80312ba4` shows the object
  header being maintained by `0x8004c850` and `0x8004c858`. In particular,
  object `+0x08` deliberately points to object `+0x0c` (`0x803129a4`).
- The same watch shows generated float/render data beginning around object
  `+0xac`, written by `0x800c9ca8`; the apparent zero payload at `+0x0c` is not
  evidence of a missed disk read.
- Consequently the next blocker is the ownership/type transition that passes
  this render object to the 64 KiB texture-page uploader. Trace that handoff
  backward from `0x801095c0`, rather than adding another source remap or cold
  hydration experiment.

## Pointer ownership checkpoint

- A snapshot-wide pointer-reference scan finds ten consecutive references to
  `0x80312998` at `0x8019cba0..0x8019cbc4`, plus one at `0x80312948`.
- Cold write tracing proves `0x8004c86c` deliberately fills the ten global
  slots while walking 0x50-byte glyph records. The final slot at `0x8019cbc8`
  receives the related `0x80312a08` pointer.
- The `0x80312948` reference belongs to a render node based at `0x80312928`:
  `0x800af25c` writes asset pointer `0x802e1788` at node `+0x18`, and
  `0x800af294` writes font object `0x80312998` at node `+0x20`.
- Both node fields are intentional; substituting `+0x18` for `+0x20` would
  conflate the node's asset and font-object roles. Continue through the font
  object's own `+0x08 -> +0x0c` representation and its upload consumer.

## Cold generations rebaseline

- The late `0x80312998` page-selection path does not execute again in either
  f180->f300 or f520->f700 CPU traces. The texture-write count also stays fixed
  at 55,183 through the non-generation f180->f300 control.
- A checkpoint produced with standard FIFO generations enabled from cold boot
  (`/tmp/gauntdl-target-cold-f520-20260711.warm`) reaches f700 cleanly with
  `frameHash=0xf4659d04`.
- That run has only 7,628 non-black framebuffer pixels and 20 covered textured
  triangles, versus the fully colored/noisy old warm-f520 continuation. The
  old late font upload was therefore substantially inherited FIFO state from a
  snapshot created without generation tracking.
- Stop treating the old warm-f520 noise frame as the canonical visual oracle.
  Continue from the cold-generation checkpoint and identify why its small set
  of legitimate primitives lacks complete texture and coverage. Keep the old
  snapshot only as a regression control for FIFO migration behavior.

## Cold-generation clip ownership checkpoint

- The exact baseline-script cold checkpoint was rebuilt after the host freeze.
  The probe must use `run-gauntdl-baseline.sh`; the broader bringup preset is not
  equivalent and produced a contaminated `0x969428e2` control.
- The rebuilt `/tmp/gauntdl-target-cold-f520-20260711.warm` reproduces f700
  `frameHash=0xf4659d04`, `nonBlack=7628`, and textured coverage `20/81`.
- Reject tracing shows `60/61` rejected textured triangles fail clip. Active
  clip windows include `(0,0)-(7,200)` and `(0,0)-(664,15)`, despite otherwise
  plausible 180x229 loading quads.
- Register-write tracing in
  `logs/gauntlet/gauntdl-cold-generations-f700-clip-writers-r1.log` identifies
  direct writers `pc=0x800fe7c4/0x800fe7cc`. They emit clip pairs
  `0x00000007/0x000018c8`, `0x00003298/0x0000000f`, and
  `0x00000022/0x000051a4`; these look like stream payload, not stable clip
  rectangles.
- Forcing the visible 640x480 clip is causal but not corrective: coverage rises
  to `78/81`, `nonBlack=128708`, and `frameHash=0xf8689d8b`, while the exposed
  image is colored noise and horizontal stripes. Keep the force-clip flag
  diagnostic-only.
- Next trace the source packet/producer generation that feeds the paired
  `0x800fe7c4/0x800fe7cc` writes to registers `0x46/0x47`. The safe repair
  boundary is preventing stale or misclassified payload from becoming global
  clip state, not clamping arbitrary clip values.
