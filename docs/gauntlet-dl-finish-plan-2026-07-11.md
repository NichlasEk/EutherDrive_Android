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

## Cold-generation producer-wrap fix

- Focused CPU/register correlation identifies the leaking writes as one
  64-word Type5 producer at `ra=0x800fe338`. The payload comes from hydrated
  `gei` RAM (`s3=0x802e3968`, `0x802e3d50`, and `0x802e4130`).
- The first block is valid packet traffic until the physical producer pointer
  advances from the Glide command-FIFO aperture through `0xa82fffff` to
  `0xa8300108`. PCI decoding then treated the wrapped packet as registers;
  payload offsets `+0x118/+0x11c` became clip registers `0x46/0x47`.
- Under standard FIFO generations, the PCI path now folds the wrapped
  `0x300000..0x3fffff` producer generation back into the command-FIFO aperture
  before register decoding. The ordinary path is unchanged.
- A cold f700 run in
  `logs/gauntlet/gauntdl-wrapfix-cold-f700-r1.log` has no payload-owned clip
  writes. Producer and consumer finish together at `wb=0xd0000` and
  `cmdrd=0xd8410`; textured coverage rises from `20/81` to `2637/2639` with no
  clip rejects (`frameHash=0xbeefdaf6`).
- This is a FIFO correctness fix, not yet a visual fix. The exposed frame is
  still noisy because `42,301,348 / 42,946,338` textured pixels sample zero.
  The next blocker is the active texture source/target ownership.
- Loading the pre-fix f520 snapshot with the new wrap behavior stalls at its
  old logical reader generation. Do not use that migrated state as a visual
  oracle; serialize/rebuild the standard-generation packet map or regenerate
  snapshots cold with the wrap fix.

## Raw TMU base checkpoint

- With producer wrap fixed, the active loading quads use legitimate Type4 state
  from `pc=0x800c4e5c`: `textureMode=0x8c24100f`,
  `tLOD=0x000020c6`, and `texBaseAddr=0x00004bfc`. These values are not stale
  payload and must not be format-remapped.
- The old baseline's `texBaseAddr << 3` repair resolves this base to `0x25fe0`,
  immediately after the last non-zero texture word around `0x25fd8`. Removing
  only the historical `+0x510` bias is neutral/slightly negative.
- A cold f700 run with raw base addressing and zero bias reduces zero samples
  from `42,301,348` to `12,447,274` and changes `frameHash` from `0xbeefdaf6`
  to `0x6938e29c`. This is a real sampling improvement, but the frame remains
  repeated/noisy loading strips.
- f900 keeps the same selected frame hash while command processing continues:
  textured coverage reaches `3191/3193`, LFB writes rise to `69,082,185`, and
  the CPU still reports `Loading Game.`. The next blocker is therefore runtime
  loading/upload completion and buffer presentation, not TMU format bits.
- `run-gauntdl-baseline.sh` now permits explicit overrides of the historical
  base-shift and sample-bias controls so current-FIFO A/B runs are reproducible;
  defaults remain unchanged.

## Warm-snapshot and late-loading checkpoint

- Warm snapshot format v6 now persists the standard-FIFO logical generation,
  packet ownership, complete-header set, write sequence, and producer state.
  The populated-producer save path was round-trip tested after replacing an
  invalid `DictionaryEntry` cast with deterministic key enumeration.
- A v6 f700-to-f1800 run changes from `0x6938e29c` to `0xf50c22e3` around
  f1100. The runtime starts another world-model/QIO wave, but the visible frame
  stabilizes as larger noisy strips rather than recognizable scene art.
- Repairing repeated asset aliases gives slots 1..8 distinct sources and names
  (`gei`, `snm`, `stk`, `kjh`, `pnk`, `geb`, `nin`, `stg`). This corrects the
  table but does not change the late frame hash.
- Extending the indexed stream limit to 27 and seeding full known payloads
  increases draw/upload traffic but still retains `frameHash=0xf50c22e3` at
  f1300. The stream limit in the probe script is now overridable for focused
  controls; its default remains 9.
- A cold TMU trace shows `tLOD=0xff802000` is produced by normal Glide TMU
  programming, including earlier `0xffffffff`/`0xfffc2fff` states and later
  packet-local values. It is not another wrapped FIFO register corruption.
  MAME setup gradients plus fixed fetch only turn the same surface into denser
  bands (`frameHash=0x4ee884e8`), so that control remains default-off.
- The next blocker is the payload content feeding the active Type5 page stream:
  sampled pages are owned by `pc=0x800fe5d4` and are predominantly zero or
  sparse control-like words. Trace the source cursor and descriptor selected
  for that writer after the late world-QIO transition; do not add more sampler
  transforms or promote full-payload hydration.

## Late Type5 source-boundary controls

- A focused f1000-to-f1100 trace identifies the repeated active upload exactly:
  `pc=0x800fe5d4`, `source=0x802e2c68`, `index=0/255`, `sp74=255`,
  `words=64`. The guest itself requests all 256 packets for both texture banks
  (`sourceBase=0` and `0x200000`); the fastpath is not inventing the run limit.
- The 64 KiB source crosses the synthetic `0x2000` indexed-source windows.
  `0x20000` stride keeps all headers valid, but breaks the intentionally
  contiguous stream and produces a mostly black frame (`f1300=0xb174085a`).
  Keep the default stride.
- Hydrating the full `gei` payload only, or expanding slot 0 from its mapped
  disk range through the full upload span, changes the frame but retains the
  same noise/band family. The latter also collides with later indexed QIO
  writes. Neither model is correct and both probes remain uncommitted.
- Disabling the outer-payload fastpath after the FIFO-wrap fix removes the
  original band blocks at f700 (`0x98a9f813`), but later lets float payloads
  become render registers (`fbz=0xbf4271c5`) and ends as a red solid frame.
  The fastpath is therefore required until the guest loop/register path is more
  complete; disabling it is not a fix.
- The existing fullrect S-from-X candidate is neutral on the current f700/f720
  oracle. The active source still produces the same image, so constant S is not
  the remaining primary blocker after clip ownership was fixed.
- Next target: compare the `0x802e2c68` run's Type5 upload address/endian mapping
  against the guest's alternating bank addresses. The source and 256-packet
  limit are guest-authored; the remaining likely error is how Type5 target
  addresses map those 64-word rows into TMU memory, not another QIO remap.

## MAME fetch and fullrect control checkpoint

- MAME-compatible Type5 write-pointer addressing and the Type5 endian control
  are byte-neutral on the late visible frame. MAME fetch addressing changes the
  sampled bytes but retains the same noise/strip family.
- The MAME fetch helper now mirrors Voodoo 2 multi-base LOD selection through
  `texBaseAddr_1`, `_2`, and `_3_8`, matching the already-correct upload helper.
  The current `tLOD=0xff802000` deliberately disables that mode because its
  magic nibble is non-zero, so this is a correctness repair rather than the
  current visual fix.
- Explicit TMU0/TMU1 sampling, MAME fixed fetch, and MAME setup gradients do
  not reveal scene art. The gradient variants make the bands denser and remain
  default-off.
- Exact Type3 field tracing confirms that the dominant full-screen pair is a
  guest-authored `0x0180a8cb` packet with clean X/Y and intentional
  `S=0xffc00000` NaNs. Reinterpreting alpha/Z as S or forcing zero/rejection is
  worse or neutral. Do not change the Type3 layout.
- The next narrow target remains upstream ownership of the late vertex/source
  state: determine why the `0x800c4e5c` producer receives NaN S after the world
  transition while earlier fullrect pairs carry finite S, alongside the
  `0x802e2c68` Type5 source descriptor selected for that surface.
- Source-write tracing narrows the NaN transition to guest `swc1 f22` at
  `pc=0x800b0a38`, writing `0xffc00000` into vertex `+0x0c`. At that call,
  `s0=0x802593a0` and `s1=0x80332a00`; the FIFO emitter merely copies the
  resulting vertex. Extending the guarded S-from-X reconstruction to all-NaN
  fullrects is visually neutral, so keep that experiment unchanged and trace
  the descriptor/FPU producer before this store.

## Indexed hydration ownership checkpoint

- The probe script now permits explicit `0` overrides for both partial and
  full indexed-source hydration. A cold f700 run with both disabled reaches a
  stable snapshot and removes most of the large false stripe fields; its PPM is
  `logs/gauntlet/gauntdl-cold-no-overlap-hydration-f700-r1.ppm`.
- Chained v6 snapshots verify that the same clean condition progresses through
  f800, f900, f1000, and f1100. The late image remains mostly black with a
  narrow noisy lower band (`gauntdl-no-overlap-hydration-f1100-r1.ppm`), so
  simply disabling hydration is not a finished visual fix.
- The control proves an ownership bug: baseline copies `0x9f60..0xa13c` bytes
  into sources spaced only `0x2000` apart, and those overlapping copies create
  the dominant false bands. Hydrating only the full `gei` payload is also
  negative (`f300=0xd083385f`, nearly black), so do not promote either extreme.
- `GauntletProbe` can now save a deterministic final snapshot with
  `EUTHERDRIVE_GAUNTDL_SAVE_FINAL_STATE`, allowing long progression tests to be
  divided into bounded, reproducible segments.
- Next target: replace fixed-window bulk hydration with ownership-correct QIO
  chunk placement or parsed-output hydration. Preserve the clean no-overlap
  snapshot family as the oracle and require restored scene content without
  reintroducing cross-source stripes.

## Shared QIO scratch ownership checkpoint

- The clean f1000-to-f1050 QIO trace shows repeated slot-0 requests with the
  same destination `0x802e1718`, callback `0x800ab4e4`, and forced
  `static_lr` read offset `0x001b0830`. The model state advances from 0 through
  at least 8 while the repair reloads the same 0x2000-byte chunk.
- The asset parser is deliberately invoked for indices 0 through 8, but its
  source table points every index at the shared `0x802e1718` scratch buffer.
  The hard-coded `gei/snm/stk/...` bulk copies are therefore not derived from
  the active QIO request and cannot be considered owned parser output.
- Partial/full indexed payload hydration is now disabled in both baseline
  presets. It remains explicitly overridable for regression controls. This
  promotes the clean no-overlap cold path and prevents known cross-source
  corruption from being presented as normal bringup output.
- Next trace the slot-0 file-state/current-offset producer and the callback's
  lifetime rule for the shared scratch buffer. The repair must feed the chunk
  selected by the guest request/state, or retain callback-parsed output, rather
  than preloading guessed object bundles into overlapping fixed windows.

## Diagnostic-exit progression checkpoint

- The request-owned f180 state is inside the game's diagnostic object view.
  Its own `Exit menu (FIRE 3)` path is reachable through the runtime input
  bridge. A short P1 Turbo pulse at frames 181..183 is accepted as bit
  `0x0800`, restarts the world/QIO sequence, and raises swaps from 363 to 503
  by f260.
- The pulse is causal but is not the graphics repair. The resulting cold-line
  continuation reaches f700 with 8,278,723 texture writes and 1,277 swaps, then
  stabilizes at f1100 with `frameHash=0x44b29c78` and 1,327 swaps. Reproducible
  v6 states are `/tmp/gauntdl-fire3-exit-f700.warm` and
  `/tmp/gauntdl-fire3-exit-f1100.warm`.
- Raw f1100 buffer dumps rule out presentation selection: buffer 1 owns the
  noise/stripe frame, buffer 0 contains only a thin corrupt top row, and buffer
  2 is empty. The selected-frame dump is
  `/tmp/gauntdl-fire3-exit-f1100.ppm`.
- From f700 through f1100, Type3 remains fixed at 20,774 packets while Type4
  traffic continues and no new setup raster work appears. The runtime emits
  `Hall of Legends`, diagnostic object text, and `No Nodes have this object`;
  render records remain null-body UI/diagnostic records. This moves the narrow
  blocker upstream of Voodoo presentation: trace the world/render-node owner
  that should submit new Type3 primitives after the completed loader, without
  changing the already verified packet-3 layout or adding texture transforms.

## World scratch relocation ownership fix

- Focused CPU and memory tracing found the geometry cutoff in the castle-world
  scan. The relocator at `0x800c9980`, called from `0x8004eb6c`, was handed the
  shared slot-0 scratch object `0x802e1718` as though it were an owned world
  relocation block. Its header begins with `1,4`; relocation converted those
  words into `0x802e1719/0x802e171c` and then rewrote the live scratch header.
  Later traversal consequently received unaligned values such as
  `0x2e171800` and stopped producing world primitives.
- `EUTHERDRIVE_GAUNTDL_FIX_RUNTIME_WORLD_SCRATCH_RELOCATION_OWNERSHIP` now
  guards only that exact callsite, table, scratch base, and `1,4` signature.
  The ordinary relocator and legitimately owned blocks are unchanged. The fix
  follows the bringup-default policy and remains explicitly overridable.
- The f520-to-f700 A/B is causal. The old path freezes at Type3 `20,774`,
  `880/880` covered triangles and `frameHash=0x44b29c78`. The ownership fix
  preserves the scratch header, reaches Type3 `23,320`, covers `2,401/2,506`
  triangles, and produces `frameHash=0x714eb9b4`.
- Later f900 progression remains active: Type3 reaches `31,454`, swaps `1,463`,
  and textured coverage `6,845/7,174` with `frameHash=0xce4baf57`. The image
  now contains a full scene-sized geometry layer, but textures remain noisy;
  this is a verified geometry stage, not completion of the visual bringup.
- Reproducible states and dumps are
  `/tmp/gauntdl-skip-worldreloc-f700.warm`,
  `/tmp/gauntdl-skip-worldreloc-f900.warm`,
  `/tmp/gauntdl-skip-worldreloc-f700.ppm`, and
  `/tmp/gauntdl-skip-worldreloc-f900.ppm`. Next trace texture-page ownership
  for the restored world primitives rather than changing geometry or packet
  framing.

### Relocation-field lifetime proof

- A cold write watch proves the slot-0 QIO hydration is initially correct at
  `source+0x5c`: disk value `0x0000f758` is present before guest execution.
- Guest `swc1 f0` at `pc=0x800c9ca8` later changes that exact word to
  `0x3f800000`, with callers `0x800af31c` and `0x800af3dc`. Reusing the same
  mutable source for later asset indices then makes the relocation parser add
  float-one bits to the source pointer; the pointer-normalize repair only hides
  this lifetime violation.
- A cold header-only distinct-source control seeds indices 1..8 from their
  mapped object headers without overlapping payloads. It reaches f180 with
  `frameHash=0x6a04baad`, `nonBlack=245307`, and distinct relocation offsets
  such as `gei=0x0000a0d0`, `snm=0x00009144`, and `stg=0x0000ac60`.
  This is causal progress but not yet a visual fix, so distinct headers remain
  diagnostic-only.
- Next determine which callback/body-read owns the bytes beyond each distinct
  header. Preserve per-object mutability, hydrate only guest-requested body
  chunks, and then remove the pointer-normalize workaround when all relocation
  fields remain naturally valid.

### Type5 source and FSYS logical-file ownership

- A fresh bringup-preset cold trace at f180 identifies the noisy Type5 page
  `targetWord=0x0f80` as guest packet `0xc0000205`, emitted by
  `pc=0x800fe5f8/0x800fe60c/0x800fe614`. Its payload cursor is
  `s6=0x802e3619`, with 64 words per row. The cursor starts at slot-0 scratch
  `0x802e1718 + 0x1f01` and crosses the synthetic 0x2000-byte QIO window.
- The odd cursor is intentional byte-stream positioning in this path, not the
  older generic pointer flag. Clearing it in the pair-copy fastpath is causal
  but negative: cold f180 changes from `frameHash=0xd083385f`, Type5 `252`,
  and 9,811 non-zero texture words to `frameHash=0xc284aba7`, Type5 `384`,
  and only 1,688 non-zero texture words. At f300, 9,839,830 of 9,840,600
  textured pixels sample zero. Keep both low-bit experiments default-off.
- The QIO trace exposes the upstream mismatch. The guest create call at
  `pc=0x800c9678` names `objects.rom`, while the slot-0 metadata repair later
  reports a synthetic `static_lr` hydration from `textures.rom` base LBA
  `0x7d000` and forced logical offset `0x1b0830`.
- Raw-disk inspection confirms that Gauntlet's FSYS files are not flat
  `baseLba * 512 + logicalOffset` ranges. `c0edbabe` headers describe payload
  extents and directory entries identify `objects.rom`, `textures.rom`, and
  `anim.rom`; extent headers and unrelated payloads occur between their disk
  sectors. The current flat-offset hydration can therefore copy FSYS metadata
  or a neighboring file into the texture upload scratch, matching the observed
  code/control-like words.
- A guessed contiguous second QIO chunk at `destination+0x2000`, including a
  0x4000 distinct-source stride, is byte-neutral at f180 and was removed. The
  body-read repair is not the owner of this initial slot-0 hydration.
- Next implement a narrow read-only FSYS logical-file resolver for the active
  `static_lr/objects.rom` and `static_lr/textures.rom` entries. QIO hydration
  must resolve the guest filename and logical offset through file extents,
  then copy only request-owned bytes. Require the `0x802e3619` payload to map
  back to one logical file and remain within its requested/refilled stream
  before changing any TMU or Type5 addressing.

### FSYS-owned static_lr texture body base

- Raw FSYS directory decoding identifies root id `0x5d` as `/static_lr` and
  its children as `objects.rom=0x5e`, `textures.rom=0x5f`, and
  `anim.rom=0x60`. The `textures.rom` extent header is at disk byte
  `0x00ffee00`; it owns a `0x000b1708`-byte payload beginning at LBA `0x7ff8`,
  disk byte `0x00fff000`.
- The body-read repair previously used synthetic base `0x0fa00000`. Its state-7
  request at logical offset `0x214c0` therefore read disk `0x0fa214c0`, outside
  the resolved file. That old chunk has 256 distinct byte values and begins
  `0xffe60014`; it is retained only through the explicit
  `EUTHERDRIVE_GAUNTDL_EXPERIMENT_RUNTIME_BGLOADMODEL_INDEXED_TEXTURE_QIO_BODY_READ_DISK_BASE`
  regression override.
- The ordinary repair now reads the same request from the FSYS-owned address
  `0x00fff000 + 0x214c0 = 0x010204c0` into guest-owned output cursor
  `0x802e1838`. The chunk has 17 distinct byte values and sparse/indexed
  texture-like content rather than the old unrelated high-entropy bytes.
- From the same `/tmp/gauntdl-owned-bodyread-f180.warm` state, f260 A/B keeps
  CPU, geometry, FIFO, `frameHash=0xd083385f`, and textured coverage
  `396/396` identical while non-zero TMU words rise from 9,811 to 14,220.
  The explicit old-base override reproduces 9,811 exactly. This is a causal
  texture-payload ownership repair; it does not yet make the selected frame
  recognizable.
- Next follow the newly populated words into the restored f700/f900 world
  packets. Resolve each indexed asset's companion `textures.rom` extent (the
  current `gei/snm/...` table points at `objects.rom` payloads) and keep model
  headers separate from texture-body QIO output.

## Request-owned textures.rom body-read fix

- A clean sequential-QIO cold run proves the post-`stk` request is
  `bytes=0x2000`, `fileOffset=0x214c0`, and state 7. At create time the previous
  QIO destination still says `0x802e7718`, but the guest-owned output cursor at
  `fp+0x28` is `0x802e1838`, immediately after the slot-0 header.
- The old body-read experiment incorrectly reused the previous `stk` window.
  The repaired capture retains `fp+0x28` before the create helper clears it and
  reads `textures.rom + 0x214c0`, disk byte `0x0fa214c0`, into that destination.
  The first word is `0xffe60014`, matching the raw disk control.
- At f260 the fixed and no-body-read controls are frame-identical
  (`frameHash=0x3a8cfb23`), proving the repair does not manufacture an immediate
  visual change. By f400/f520 they diverge in CPU/FIFO progression while the
  visible loading surface remains nearly identical.
- At f600 the fixed path reaches runtime PC `0x8001a1bc`; the no-body-read
  control remains in the render-record loop at `0x800b1e7c`. The fixed path has
  no further QIO requests from f520 through f600. Asset loading therefore
  completes far enough to leave the old parser loop, while presentation stays
  frozen on loading noise.
- The request-owned body read is promoted into the baseline as
  `EUTHERDRIVE_GAUNTDL_FIX_RUNTIME_BGLOADMODEL_INDEXED_TEXTURE_QIO_BODY_READ=1`.
  The legacy experiment variable remains accepted for snapshot/regression
  compatibility.
- Next blocker: from the fixed f600 snapshot, trace why no new Voodoo writes or
  swaps reach the selected buffer after runtime entry. Preserve this completed
  QIO path and diagnose the standard-generation FIFO consumer/presentation
  state instead of changing asset payload mappings again.

### Missing world texture-page checkpoint

- The restored f900 world draws actively, but its representative textured
  triangle (`pc=0x800c4e5c`, base `0x00e510`) samples only zero TMU addresses
  in `0x017c00..0x018f00`. Serialized writer ownership is absent for every
  sampled word; the last non-zero TMU byte is only `0x015554`. This is a
  missing upload-page failure, not a later overwrite.
- No Type5 texture packets occur from f900 through f920, nor in the f610-f700
  transition. The pages must therefore be diagnosed at their earlier upload
  point rather than by changing the late sampler or presentation path.
- A baseline cold f180 trace records 246 ordinary `0xc0000205` texture packets,
  each 64 words, with logical target starts ending at `0x000f80`. Later snapshot
  intervals do not repeat those packets. Type5 sequence diagnostics now record
  physical destination spans unconditionally, without requiring expensive
  per-word writer history, so the next cold capture can correlate the early
  upload map directly with the missing f900 range.

### Body-read consumer and preset checkpoint

- The authoritative f180-to-f260 oracle is reproduced only through
  `tools/GauntletProbe/run-gauntdl-baseline.sh`: `frameHash=0xd083385f`,
  Type3 `19,800`, and `1,630 / 93,904` Type5 packets/words. The broader
  `EUTHERDRIVE_GAUNTDL_BRINGUP_BASELINE` preset is not equivalent because it
  also enables texture-download alignment and enters a contaminated packet
  path.
- A focused guest-load watch proves the state-7 body at `0x802e1838` is
  consumed by the texture descriptor/parser chain at `0x800af7xx`,
  `0x800afaxx`, and `0x800bd1xx`. Reading `/static_lr/objects.rom + 0x214c0`
  instead of `/static_lr/textures.rom + 0x214c0` cuts Type5 to `256 / 16,384`
  and stalls the uploader while preserving the immediate frame. The request
  therefore really owns `textures.rom`; the missing world pages are downstream
  of descriptor consumption, not a filename swap.
- Raising the synthetic indexed stream limit from 9 to 27 walks source windows
  that the guest has not hydrated and reintroduces the full-screen false Type5
  stream (`frameHash=0xf4ccc0af` at f400). Keep limit 9. The next trace should
  follow the parsed descriptor/output cursor at `0x802e2158` and establish why
  legitimate upload ownership ends at physical TMU word `0x5554` before the
  world samples `0x5f00..0x6380`.

### Physical 64 KiB page boundary checkpoint

- Raw f260/f900 TMU dumps correct the previous high-water interpretation:
  word `0x5555=0xdeadbeef` is a sentinel, not uploaded texture content. All
  genuine non-zero data ends at word `0x3fff` (byte `0xffff`), and every
  f260-to-f900 texture change also remains below that 64 KiB boundary.
- Live writer ownership confirms the page layout. LOD0 Type5 rows fill through
  physical word `0x3fff`; subsequent LOD clears reach `0x554f` with zero
  payload. The last clear is `cmd=0xc0000015`, target
  `0x0a8380..0x0a8381`. Words `0x5550+` have no writer.
- A default-off `EUTHERDRIVE_GAUNTDL_EXPERIMENT_VOODOO_TEXTURE_SAMPLE_64K_PAGE_WRAP`
  masks sample addresses to the populated 64 KiB page. From the same f900
  snapshot, f920 changes from `frameHash=0x5c54e46b` to `0x52cbc54b` and
  reduces zero-colored textured pixels from about 5.79M to 0.51M. The exposed
  image is still noisy and therefore the global wrap is diagnostic-only, but
  it proves that the active render object expects page-local addressing while
  the current sampler treats its base as one flat TMU address.
- Probe diagnostics now support focused main-RAM read watches and arbitrary
  texture-word owner dumps. Next constrain page-local addressing to the actual
  64 KiB render/font object ownership transition instead of globally wrapping
  every texture.

### Post-body mip-source ownership checkpoint

- The request-owned f180-to-f260 run reaches the ordinary page loop at
  `0x801095c0`; the extra Type5 traffic is not invented solely by the backend
  fastpath. The state-7 descriptor has `desc08=3`, `desc0c=0`, and its first
  helper result is `0x10000` bytes from source `0x802e1718`.
- The following mip sources advance to `0x802f1718`, `0x802f5718`,
  `0x802f6718`, and smaller tails. Those sources are entirely zero in the
  current ordinary run. A second descriptor starts at `0x802e1918`, inside the
  new `textures.rom` body, and likewise declares a `0x10000`-byte first level.
- This exposes the remaining ownership mismatch: the state-7 QIO request owns
  only `0x2000` bytes at `0x802e1838`, while the upload descriptor consumes a
  complete mip chain extending well beyond that request. Earlier synthetic
  indexed windows at `0x802e3718`, `0x802e5718`, and `0x802e7718` also fall
  inside the declared first-level span.
- Repeating the request payload across the declared mip span is causal but
  invalid: Type5 rises from `1630/93904` to `2144/126800`, activates additional
  synthetic stream indices, and leaves the f260 frame bit-identical at
  `0xd083385f`. The experiment was removed.
- Next trace the real `0x800ab4e4` completion/parser contract for state 7 and
  identify the owned expanded/output allocation. Do not enlarge or repeat the
  raw disk request, and do not promote the global 64 KiB sampler wrap.

### Callback and stream-limit boundary checkpoint

- Canonical cold and f180-to-f260 CPU traces show no execution at the QIO
  callback pointer `0x800ab4e4`. The current completion fastpath marks requests
  complete and the model dispatcher consumes their records without entering
  that address.
- A narrow state-7 callback kick using the established QIO ABI (`a0=qio`) is
  structurally safe but exactly neutral: f260 remains `0xd083385f`, Type5 stays
  `1630/93904`, and the TMU map is unchanged. The callback is not the missing
  texture expansion stage, so the experiment was removed.
- Restoring the guest's observed stream limit `2` prevents state 7 entirely:
  f260 retains only the inherited 252 Type5 packets and 9,811 non-zero TMU
  words. Limit 9 is therefore required to reach the request-owned body read,
  even though the current synthetic source windows behind entries 4..8 are not
  ownership-correct.
- The next narrow boundary is the missing per-entry QIO lifetime for stream
  entries 4..8. Recover their guest-selected filename, logical offset,
  destination, and completion order instead of reducing the count or filling
  their windows from guessed object payloads.

### Generations-clean baseline promotion

- Every request-owned body-read verification above was cold-built with the
  standard FIFO generation model and its producer-wrap repair. The older
  masked path is already proven to re-enter stale Type1/3/4/5 bodies and is not
  a valid ordinary-path oracle.
- Standard FIFO generations are now enabled in both baseline presets. The
  environment variable remains explicitly overridable for historical control
  snapshots.
- Diagnostic literal traces now include their text. The fixed f900 state shows
  `Hall of Legends` alongside internal object/debug strings; this proves the
  runtime has left the initial `Loading Game.` state, but does not by itself
  prove that world nodes are missing. Input active-low service/test defaults
  are neutral (`0xffff`), so do not patch operator bits without a direct read
  trace showing an asserted line.

### Source-owned stream-count checkpoint

- The loop at `0x800abe30..0x800abea0` does not index the synthetic `0x2000`
  source windows. It derives a record table at `source + 0x68 +
  source[0x60] * 0x8c`, then addresses records as `table + index * 0x50`.
  The old stream-limit guard therefore validated the wrong allocation.
- The hydrated source header owns the real record count at `source + 0x64`.
  The `snm` header reports 13 and the later `stk` header reports 9. The repair
  now accepts only a larger count read from that exact hydrated source, capped
  at 13; it no longer assigns the configured maximum directly.
- From `/tmp/gauntdl-owned-bodyread-f180.warm`, the chained transitions
  `2->13` and `2->9` reproduce the authoritative f260 oracle exactly:
  `frameHash=0xd083385f`, Type5 `1630/93904`, and 47,147 non-zero texture-map
  writes. This preserves the state-7 body read without the invalid limit-27
  full-screen stream.
- A 4-bpp expansion of the state-7 body was also rejected. It causally raised
  final non-zero TMU words from 14,220 to 21,840, but the f700 image became a
  denser corrupt mosaic. The experiment was removed; the body is structured
  parser input, not a raw texture page to scale into RGB332.

### Upload/render texture-page lifetime checkpoint

- A complete f180-to-f260 Type5 sequence capture proves every texture payload
  write lands in physical TMU words `0x0000..0x3fff`. Logical targets continue
  through later LOD ranges, but no packet owns the world sample page beginning
  near word `0x5f00`.
- Type5 sequence diagnostics now include the live upload TMU, texture mode,
  LOD and base registers. All 1,382 captured `0xc0000205` texture sequences use
  mode `0x0c26100f`, LOD `0xff802000`, and texture base zero; 1,126 target TMU0
  and 256 target TMU1.
- The TMU base-register trace shows an intentional-looking context switch:
  upload setup at guest PC `0x80106a74` writes base zero, while the active world
  descriptor at `0x800bd19c` repeatedly writes base `0x1c00`. The later world
  sampler resolves that to byte base `0xe510` and walks into the unowned next
  64 KiB page.
- A mode-only 64 KiB sample wrap reproduces the earlier global-wrap hash
  `0x52cbc54b` exactly but still renders noisy mosaic data. It was removed.
  The next fix belongs in upload page placement/lifetime: identify why several
  logical upload passes overwrite page zero instead of owning the continuation
  selected by the `0x1c00` render base.
