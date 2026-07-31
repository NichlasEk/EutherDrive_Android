# Gauntlet Dark Legacy – plan till spelbart läge

Datum: 2026-07-31
Startcheckpoint: `767a0fcc Advance Gauntlet player phase scheduler`

## Målet

Första spelbara milstolpen är nådd när en reproducerbar native-kedja kan:

1. ta emot coin/start,
2. skriva tre initialer,
3. välja en karaktär med riktiga spelinput,
4. lämna state `0x400a`,
5. ladda en spelvärld,
6. flytta Player 1 och utföra minst en attack,
7. köra 300 normala frames utan krasch eller syntetisk RAM-statepatch,
8. sparas och återladdas mitt i spel med samma guest-state.

Den första milstolpen kräver inte perfekt pixelkorrekt Voodoo-rendering eller
fullt ljud. Bilden måste däremot vara tillräckligt stabil för att identifiera
spelaren, världen och HUD:n. Horisontella rasterfel som döljer dessa räknas
alltså som en praktisk spelbarhetsblockerare.

## Nuvarande bevisläge

Den native kedjan är verifierad genom:

```text
coin + start
  -> state 0x400a
  -> Player 1 aktiv
  -> initials ZZZ
  -> player phase 3
  -> native phase-3 updater 0x800669c0
  -> caller 0x8002179c..0x800217e8
  -> phase-4 setup 0x800229cc
  -> player phase 5
```

Den reloadbara fas-5-checkpointen är:

```text
.build-tmp/euther-native-phase5-natural-f4232.warm.gz
sha256 2b3059f89bd58e2b088a0cfabf040bd3fc4e5e683f7c8a934c5b7f41a06c174b
```

Vid f4232 gäller:

```text
main state       = 0x400a
active players   = 1
player 1 phase   = 5
entered name     = ZZZ
frameHash        = 0xc766b6b0
nonBlack         = 221190
colored          = 215058
```

En Fight-edge ändrade framebuffer men lämnade fas 5. Det är ännu inte bevisat
om inputet nådde rätt fas-5-handler eller bara den globala
diagnostik-/renderägaren. Nästa blockerare är därför scheduler-/dispatcher-
kedjan för fas 5, inte knappmappningen i Android och inte initials-editorn.

## Arbetsregler

- Driv originalets guestfunktioner och caller-semantik. Patcha inte phase,
  countdown, active-player-mask eller main state direkt för att nå nästa skärm.
- Använd samma snapshot och 60000 CPU-steg per frame vid A/B-jämförelser.
- Separera runtimeframsteg från rendering: state, input och player-data måste
  verifieras även när bilden är korrupt.
- Promota bara ett experiment när en ren baseline-reload reproducerar
  resultatet utan experimentvariabeln.
- Ändra inte den redan verifierade diskbasen, Type 5-dekodern eller
  texture-companion-provenansen utan ett nytt kausalbevis.

## Lagrings- och loggpolicy

Gauntlet-bringup ska inte använda `/tmp` för snapshots, RAM-dumpar,
framebuffers eller växande loggar.

```text
TMPDIR=$PWD/.build-tmp
snapshotformat=.warm.gz
kanoniska artifacts=artifacts/gauntlet-probe/
kortlivade probes=.build-tmp/
```

Regler:

- spara endast explicita milstolpsframes, aldrig en snapshot per frame,
- använd alltid `.warm.gz`, aldrig växande rå `.warm`,
- håll högst aktuell checkpoint och närmast föregående checkpoint per aktiv
  A/B-gren,
- använd konsoloutput för korta traces,
- varje filtrace måste ha frame-, PC- eller radgräns och ett fast slutvillkor,
- flytta endast verifierade bilder/checkpoints till
  `artifacts/gauntlet-probe/`.

## Etapp 1 – identifiera fas-5-handlern

Målet är att bevisa exakt vilken originalfunktion som äger Player 1 när
`playerBase + 0xc8 == 5`.

1. Kör MAME-oraklet från motsvarande character-select-state.
2. Bryt vid spelarcallern `0x80021620` och området
   `0x80021780..0x80021880`.
3. Logga för varje player-update:
   - player-index,
   - phase före och efter,
   - call target,
   - `v0`-retur,
   - input-record och normaliserad input,
   - writes till `0x80229338` och närliggande player-timers.
4. Ta en kontroll utan input och tre separata A/B:
   - Left eller Right,
   - Fight,
   - Turbo/FIRE 3.
5. Stoppa vid första phase-, active-mask- eller main-stateändring.

Leverans:

- exakt fas-5-entry-PC och callergren,
- en liten default-avstängd trace i `GauntletProbe`,
- MAME/Euther-tabell med samma input, retur och state-write.

Stoppkriterium: ändra inte host-schedulern förrän call target och dess
caller-semantik är uppmätta.

## Etapp 2 – ersätt initials-specialfallet med player-dispatch

Nuvarande hostväg kan driva initials och fas 3 men lämnar fas 5 åt en
scheduler som ännu inte körs tillräckligt.

Bygg en kontextbevarande player-dispatch runt den uppmätta callerlogiken:

```text
active player
  -> läs aktuell phase
  -> anropa rätt guest-handler
  -> tillämpa endast callergrenens bevisade writes/calls
  -> återställ CPU-kontext
```

Krav:

- behåll den uppmätta 30 Hz-kadensen,
- stöd minst de faser som behövs från initials till character confirm,
- stoppa på oväntad phase i stället för att gissa nästa handler,
- låt acceleratorn vara default-avstängd och endast användbar för långa,
  redan bevisade countdowns,
- undvik dubbla uppdateringar om den ordinarie gästschedulern börjar äga
  spelaren igen.

Verifiering:

1. f4200 utan accelerator ska fortsätta deterministiskt i fas 3.
2. Accelererad f4200 ska nå samma fas-5-state som MAME.
3. f4232 utan input ska vara stabil.
4. En riktning ska ändra bevisad character-select-state.
5. Fight ska ge den uppmätta confirm-returen/stateändringen.

## Etapp 3 – lämna character select naturligt

När fas-5-dispatchern fungerar:

1. spela in den minsta riktiga inputsekvensen för ett karaktärsval,
2. verifiera release-edge mellan varje knapp,
3. följ phase, active-player-mask och main state frame för frame,
4. spara checkpoint först efter att state `0x400a` har lämnats naturligt,
5. låt efterföljande loader/QIO arbeta utan syntetisk completion.

Godkänd etapp:

- vald karaktär kan identifieras i guest-RAM,
- state `0x400a` lämnas via originalkod,
- nästa loader/game-state nås två gånger från samma fas-5-snapshot,
- båda körningarna ger samma statekedja och checkpoint-hash.

## Etapp 4 – första styrbara spelvärlden

Fortsätt från post-selection-checkpointen tills level-loadern är inaktiv och
spelaren har ett levande world/player-objekt.

Probe-matris:

| Körning | Input | Bevis |
|---|---|---|
| kontroll | ingen | world/player-state står stabilt |
| rörelse | Right 8 frames, release 8 | position eller velocity ändras |
| attack | Fight 4 frames, release 8 | animation/attack-state eller hitbox ändras |
| magic | Magic 4 frames, release 8 | separat action/resource-state ändras |
| turbo | Turbo 4 frames, release 8 | korrekt tredje knapp, inte diagnostics |

Varje körning ska jämföra:

- main state och player phase,
- player position, velocity och animation,
- health/lives/credits,
- närmaste enemy- eller hit-state,
- frame hash, swaps, draw packets och textured triangles,
- input-record och normaliserad held/edge-mask.

Godkänd etapp:

- samma snapshot kan röra spelaren i minst två riktningar,
- Fight ger en annan guest-state än kontrollen,
- 300 frames med en kort inputsekvens kör utan CPU-halt, FIFO-hang eller
  loader-regression,
- reload efter rörelsen fortsätter från samma position/state.

## Etapp 5 – gör bilden praktiskt speltestbar

Runtime och rendering ska hållas som två separata A/B-spår. Använd den första
styrbara gameplay-checkpointen som ny renderoracle.

Prioritetsordning:

1. klassificera presented, working och draw buffer var för sig,
2. identifiera första frame där de horisontella banden skiljer sig från MAME,
3. jämför FIFO/register-state precis före divergensen,
4. spåra den producer som skriver det felaktiga Type 3-/registerpaketet,
5. korrigera producer-/ägarskap eller Voodoo-semantik vid tidigaste bevisade
   gräns.

Förbjudna genvägar:

- host-side projektionsclamp,
- downstream triangelfilter som bara döljer fel,
- syntetisk framebuffer-clear,
- återgång till en äldre men visuellt lugn frontbuffer.

Godkänd etapp:

- Player 1, närmaste golv/vägg och HUD kan särskiljas,
- diagnostiktext ligger inte permanent över spelbilden,
- rörelse kan följas visuellt över minst 60 frames,
- förbättringen motsvaras av korrektare FIFO/register- eller
  bufferägarskap, inte bara en snyggare hash.

## Etapp 6 – promotera baseline och Android-speltest

När desktop-proben är styrbar och visuellt läsbar:

1. ta bort eller lämna falsifierade experiment default-avstängda,
2. promota endast de fixes som krävs av en cold/native körning,
3. kör en cold GauntletProbe från coin till spelvärld,
4. verifiera warm snapshot round-trip,
5. bygg Android Release,
6. verifiera fysisk gamepad/touch-mappning för riktningar, Fight, Magic,
   Turbo, Start och Coin,
7. kör ett femminuters test på enheten.

Android-godkännande:

- ingen bringup-specifik filväg krävs i appen,
- inga snapshots eller traces skapas under normal användning,
- input release fungerar utan fastnade knappar,
- pause/resume behåller eller återställer ett definierat state,
- fem minuters rörelse och strid utan krasch.

## Kritisk väg

```text
MAME fas-5-orakel
  -> exakt fas-5-handler
  -> generell player-dispatch
  -> native character confirm
  -> lämna 0x400a
  -> level/game state
  -> rörelse + attack i guest-state
  -> 300-frame stabilitet
  -> gameplay-renderoracle
  -> praktiskt läsbar bild
  -> cold baseline
  -> Android-speltest
```

## Nästa konkreta arbetspass

1. Utgå från
   `.build-tmp/euther-native-phase5-natural-f4232.warm.gz`.
2. Lägg en begränsad trace kring `0x80021620` och
   `0x80021780..0x80021880`.
3. Kör no-input, Right och Fight som tre separata korta A/B.
4. Identifiera fas-5-call target och callerreturen.
5. Implementera endast den uppmätta fas-5-grenen.
6. Repetera Right/Fight och stoppa vid första riktiga phase- eller
   main-stateändring.
7. Spara en enda ny `.warm.gz` när character confirm är bevisad.

Detta är den kortaste evidensbaserade vägen från dagens checkpoint till
faktiskt spelbar kontroll.
