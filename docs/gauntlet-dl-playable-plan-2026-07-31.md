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
   `.build-tmp/euther-native-game-phase1-f4733.warm.gz`.
2. Avgränsa de stale diagnostikposterna mot den riktiga phase-1-scenen; behåll
   guestvärld, HUD och input oförändrade.
3. Profilera en enda phase-1-frame och rangordna CPU-, FIFO- och rasterkostnad.
4. Optimera den största generella desktopkostnaden utan Gauntlet-PC-specialfall.
5. Repetera Right 8 frames och Fight 4 frames; kräv guest-state eller visuell
   rörelse/attack, inte bara inputtabellträff.
6. Kör 300 frames med en kort inputsekvens utan halt eller växande artefakter.
7. Starta Avalonia-desktopappen med den rena baselinen först när bilden är
   läsbar och framekadensen praktiskt spelbar.

### Uppdatering 2026-07-31

Punkt 3-6 är nu uppmätta för steady-state: 300 frames med rörelse- och
attackinput kör 50.30 fps i genomsnitt vid 60k och håller guest state utan
halt. Nästa kritiska desktopsteg är därför inte mer generell prestandajakt,
utan att identifiera och stoppa den diagnostiska textköägaren före
`0x800c4efc/0x800c4fc8`, utan att filtrera bort HUD eller annan speltext.
Direkt därefter ska samma f4733-checkpoint göras laddningsbar i Avalonia så
att tangentbordskontrollerna kan speltestas i ett riktigt desktopfönster.

Detta är den kortaste evidensbaserade vägen från dagens checkpoint till
faktiskt spelbar kontroll.

### Källägd diagnostikspärr

Pixel-last-writer-profilen binder nu varje rasterpost till FIFO-paketets
faktiska CPU-producent. Diagnosglypherna kommer från `0x800c4e6c` och
`0x800c4f20`; deras två triangelanrop görs från `0x800b0d0c` och
`0x800b0d20`. Anropsramen innehåller samtidigt den aktiva textposten.

Baselinen hoppar därför bara över en glyphquad när alla följande villkor
stämmer:

- main state är gameplay `0x400c`,
- gäst-PC är en av de två verifierade triangelanropsplatserna,
- textposten pekar in i den sammanhängande diagnostiktextbloben, och
- bloben fortfarande börjar med `DIAGNOSTIC MENU`.

Detta tar bort 911 stale diagnosglyph-trianglar redan i första fortsatta
framen utan skärmpositionsfilter och utan att stänga av HUD-renderaren. En
300-frame-körning från f4733 nådde 63.99--65.27 fps, stannade i fungerande
guestkörning och gav `frameHash=0x964a21b5`. Kontrollbilden
`.build-tmp/gauntdl-source-clean-f5033.png` visar att diagnosmenyn är borta.

Coin/start/fight-provet når därefter `Easy`/buy-health-vyn och konsumerar
krediter, så inputkedjan fungerar. Den praktiska blockeraren är nu tydligare:
Voodoo får fortfarande inte den stora 3D-världsscenen, bara portal, HUD och
menygeometri. Nästa renderingstest ska därför spåra varför gameplayvärldens
Type3-paket inte emitteras; diagnosmenyn ska inte återinföras som förklaring.
### 2026-07-31: diagnostic render-record starvation removed

- The gameplay-state renderer was spending most of its 60,000-instruction frame budget copying and drawing records whose text pointers belonged to the stale `DIAGNOSTIC MENU` blob (`0x8020f268..0x8020f606`).
- The desktop baseline now skips those records at the verified render-record body (`0x800b1e7c`) before the per-character stack-copy and triangle loops. The fast path requires state `0x400c`, exact code signatures, the diagnostic blob sentinel, and a text pointer inside that blob.
- A 300-frame continuation from `euther-native-game-phase1-f4733.warm.gz` reached 7,958 textured triangles and 1,237,256 covered raster pixels (`frameHash=0xb58f5f59`, 48.96 fps). Before this source-level skip the same continuation emitted no gameplay Type 3 draw packets in the first profiled frame.
- The final image is still mostly black, so the next blocker is no longer guest submission starvation. The next slice should inspect why 758,113 of those texture samples resolve to zero and why the remaining textured coverage does not survive into the selected display buffer. Enabling zero-texture transparency changed the hash to `0x51b1e32e` but did not reveal the world, so it remains disabled.
- Temp hygiene held during both long probes: raw PPM files were converted immediately to small PNGs and deleted; repo-local `.build-tmp` stayed at 134 MB and `/tmp` stayed at 431 MB.

### 2026-07-31: gameplay scene registration is the next upstream boundary

- The textured triangles unlocked above are UI and glyph quads from `0x800c4efc`, not arbitrary 3D world geometry. A 200,000-instruction/frame comparison produced the same final hash and the same sole Type 3 producer, so the missing world is not caused by the 60,000-instruction desktop budget.
- The earlier interpretation of `0x8016c130` as late world-global corruption was false. The address is reused across overlays: it is zero/a loader global before the world-selection overlay changes, then legitimately contains `OWTHVOX`/`S_3WAYSHOTVOX` data in every available snapshot from f2896 onward. A late reconstructed table was bit-identical because the original consumer had already finished; that experiment remains removed.
- Input and scheduling are alive in state `0x400c`. The native player scheduler reaches its gameplay branch every few frames, and held inputs reach both the runtime table and normalized guest words (`Up=0x03`, `Down=0x0c`, `Left=0x30`, `Right=0xc0`, `Fight=0x400`). A 1200-frame Fight continuation advances the player timer at 71--73 fps but cannot create the missing scene.
- The first real divergence is the scene/camera root at `0x80213618`. The working post-loader 3D oracle contains camera matrices, object pointers and transforms there and reaches `0x800b89b0 -> 0x800b4b40 -> 0x800b4d9c -> 0x800ba020`, producing arbitrary Type 3 packets at `0x800bc8ec/0x800bc91c`. The f4733 gameplay checkpoint leaves the same root almost entirely zero and never reaches that call chain.
- Two differing static-root words at `0x8016291c/0x80162938` were tested individually and together; all probes were hash-identical and reached no scene caller, so no state patch was retained. Next: trace the gameplay-owned registration writer that should populate `0x80213618` or enqueue its `0x805xxxxx` scene objects, starting at the level-init/scene-manager caller rather than patching matrices or copying the diagnostic oracle's scene state.

### 2026-07-31: phase-5-exiten blockeras av synkrona async-väntor

- `0x80086cec` skriver huvudstate `0x400c` tidigt men ska därefter slutföra
  cleanup, level-loader och scenregistrering innan den returnerar. Den
  context-preserving hostkörningen når inte returen inom sin 8M-budget och
  återställer därför CPU-kontexten efter den tidiga state-skrivningen.
- Den första verifierade spärren var UI-kommandoloopen vid `0x800c83b0`.
  Gauntlet 2.4 har anropet vid `+0x20`, retur-PC `0x800c83d8` och loopen vid
  `+0x28..+0x34`; den gamla signaturen låg fyra byte för tidigt. Den korrigerade
  signaturen och samma fastpath i `0x400c`-dispatchen tar bort cirka 788 000
  loopvarv per 8M-probe. Steady-state-regressionen från f4733 behåller
  `frameHash=0xb58f5f59` över 300 frames.
- Nästa synkrona gräns är QIO-väntan vid `0x800edac4` på objektets `+0x14`.
  Ett live-transfer-experiment visade att den riktiga scheduler/IDE-kedjan kan
  föra objekt `0x80295440` till handle `-1`, status `0x0500`, men den ad hoc
  CPU-injektionen gav senare en ogiltig låg PC och har därför tagits bort.
- Nästa säkra implementation ska inte forcera QIO-status eller injicera
  `0x80086cec` ovanpå en godtycklig renderstack. Gör i stället phase-5-anropet
  resumable med en separat serialiserad CPU-kontext, serva en vanlig frame
  mellan yields och återställ ursprungskontexten först när guestfunktionen
  faktiskt returnerar. Kräv därefter att `0x80213618` blir aktiv innan den nya
  vägen får gå in i baselinen.

### 2026-07-31: phase-5-exiten returnerar via riktig IRQ/QIO-kedja

- Timeout-PC och QIO-state visade att `0x80086cec` skapar en ny request på
  `0x80295440` (`handle=0x1009`, `status=0`) efter den tidigare färdiga
  requesten. Problemet var därför inte en stale status utan att den
  context-bevarande callbacken inte gav gästsystemets interrupt/scheduler
  möjlighet att slutföra den nya requesten.
- Vid den signerade väntan `0x800edac4` pulserar baselinen nu det emulerade
  timeravbrottet, högst åtta gånger. Under just phase-5-anropet får den vanliga
  fulla dispatchkedjan köras efter steady-state-fastpathsen. Gästens egen
  timer-, IDE- och software-IRQ-kedja för då samma objekt naturligt till
  `handle=-1`, `status=0x0500`; hosten skriver aldrig completion-statusen.
- Loadern når därefter Voodoo-kopieringsslingan `0x800fe7bc..0x800fe7e0`.
  Den kopierar par av 32-bitarsord från main RAM till det mappade
  `0xa8000000..0xa83fffff`-fönstret. En signerad bulkfastpath behåller varje
  `Write32`-sideffekt men tar bort gästloopens instruktioner.
- Från den rena f4264-checkpointen returnerar `0x80086cec` nu inom ordinarie
  8M-budget: `returned=1`, state `0x400a -> 0x400c`, QIO `-1/0x0500` och
  `frameHash=0xc59cfdee`. Den komprimerade kontrollcheckpointen är
  `.build-tmp/euther-native-world-init-f4265.warm.gz`, SHA-256
  `b65ab50ec6e5b5779c758fb1c0593867197fa7fc6fde7bfcd34a354269671192`.
- `0x80213618` är fortfarande noll efter returen och efter 300 normala frames.
  Ett default-avstängt prov av main-state-rutinen i state `0x400c` kunde också
  returnera via den riktiga QIO-kedjan men registrerade ingen scen. Nästa steg
  är därför att hitta state-400c-callern som äger scene-init, inte att återgå
  till syntetisk RAM-state eller kopiera scene-root från oraklet.
- En negativ A/B med båda nya fixarna avstängda och 1M-budget stannar åter med
  `returned=0` och oförändrad svart hash `0x30e41dc5`; den rena baselinen med
  fixarna når returen. En separat 300-frame-fortsättning från f4733 gav samma
  `frameHash=0x667f3b94` både med fixarna på och explicit avstängda. De nya
  phase-5-vägarna påverkar alltså inte steady-state-körningen när inget
  phase-5-exitanrop pågår.
