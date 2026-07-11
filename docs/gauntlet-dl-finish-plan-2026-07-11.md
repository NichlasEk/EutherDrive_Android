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
