# Gauntlet Dark Legacy bringup-checkpoint 2026-07-30

## Resultat

Gauntlet passerar nu den tidigare kraschen efter Temple-resursbygget och
renderar åter en riktig 640x480-scen. Vid f680 gav den rena fortsättningen:

```text
pc              = 0xffffffff800c6f24
frameHash       = 0x7dce6ca2
nonBlack        = 107793
colored         = 107567
textured tris   = 1255
raster pixels   = 426175
swaps           = 3294
```

Vid f700 fortsatte CPU, FIFO och rasterisering utan lågminneskrasch:

```text
pc              = 0xffffffff800b9384
frameHash       = 0x036ed58a
nonBlack        = 114620
colored         = 114015
textured tris   = 4714
raster pixels   = 1209049
```

Gästkoden byggde samtidigt sin riktiga `DIAGNOSTIC MENU` med raden
`Exit menu (FIRE 3)`. Nästa praktiska steg är därför att verifiera
diagnostik-exit/input-vägen till attract/game och därefter korrigera den
fortfarande mörka och delvis trasiga renderstaten.

## Diagnostik-exit verifierad

Efter checkpoint-commiten verifierades FIRE 3-vägen med två tidsstyrda
`turbo`/C-tryck. Det första trycket nådde runtime-inputtabellen som
`p1=0x0800` och startade riktig spelinnehållsladdning, bland annat:

```text
players/dwf/sfxgre
levels/levelE1
monsters/zom2
monsters/ice2
monsters/imp2
monsters/death
```

Vid f740 renderades spelvärld och `CREDITS` bakom diagnostik-overlayn. Ett
andra FIRE 3-tryck från f740 tog bort overlayn och gav en normal svart
skärmövergång. Vid f780 hade gästen gått vidare till attract-starten och
renderade sitt nästa riktiga fel:

```text
AllocMem() called while mem reserved
GetMemBase() called while mem reserved
```

Det betyder att input- och diagnostik-exitvägen nu är bevisad.

## State 0x8005 lämnas naturligt

Den efterföljande kontrollen korrigerade f780-slutsatsen ovan:
`GetMemBase()/AllocMem called while mem reserved` är riktiga
gästdiagnostikmeddelanden, men den långlivade reservationen är avsiktlig och
allokeringarna fortsätter. Flaggan ska inte nollas syntetiskt.

Direkta RAM-prover visade statekedjan:

```text
f740  main state = 0x8002
f760  main state = 0x8005
f800  main state = 0x8005
f1210 main state = 0x8004
```

State `0x8002` lämnas korrekt genom den befintliga FIRE 3-latchbridgen.
State `0x8005` äger däremot sin egen FIRE 3-edge och accepterar den först
efter sin tidsstyrda fade. En tidig puls vid f802 nådde inputrecordet som
`0x0800` men lämnade den interna exitflaggan noll. Game-time-fixen drev
fadevärdena naturligt framåt; en ny puls vid f1200 lämnade sidan utan någon
RAM- eller state-patch och nådde level-loaderstate `0x8004`.

Vid f1310 är den exakta loaderstaten:

```text
main state      = 0x8004
loader state    = 1
load complete   = 0
swaps           = 4942
texture writes  = 5363728
```

Gästen laddar bland annat `contest` och `movies/atarilogo`. Den gamla
diagnostik-/glyphytan ligger fortfarande i frontbufferten medan loadern
arbetar, men CPU, QIO, Type 5 och swaps fortsätter utan de tidigare
heap-stack-krockarna.

Fortsättningen till f1460 bevisar dessutom att loader state 1 inte är ett
hang. Dess interna streamräknare gick:

```text
f1310  0x4e / 0x0b / 0x00085a00
f1410  0x14 / 0x09 / 0x0002d400
f1460  0x50 / 0x0e / 0x000e6a00
```

Rullningen till ett nytt större värde vid f1460 betyder att den första
QIO-/resursvågen avslutades och nästa startade. Mellan f1410 och f1460 ökade
swaps `4986 -> 5018`, texture writes `5410148 -> 5588976` och 178828
texturord berördes. Nästa steg är därför naturlig loaderfortsättning, inte en
ny syntetisk completion.

Den fortsatta naturliga körningen till f2260 nådde nästa tydliga loaderfas.
Loader state gick från `1` till `12` vid f1760 och fortsatte sedan genom den
normala `11`/`12`-cykeln för monsterposterna. De observerade verkliga
resurserna var:

```text
f1760  monsters/zom2 textures.rom + objects.rom   loader state 12
f1860  monsters/ice2 textures.rom + objects.rom   loader state 11
f1960  monsters/imp2 textures.rom + objects.rom   loader state 11
f2060  monsters/pla2 textures.rom + objects.rom   loader state 11
f2160  monsters/golem/levelF/textures.rom         loader state 11
f2260  monsters/death textures.rom + objects.rom  loader state 11
```

Vid f2260 är huvudstaten fortfarande `0x8004`, `load complete` är fortfarande
noll och streamräknarna har nått `0x3b / 0x3b`. `temple.wad` har samtidigt
aktiverats. Texturskrivningarna återupptogs mellan f2160 och f2260:

```text
texture writes  5921605 -> 5951441
touched texels  29836
swaps            5462 -> 5526
frame hash       0xf053602b
```

f2260-bilden innehåller för första gången i den här loadersekvensen ett
riktigt färglagt lila/silverfärgat spelobjekt längst ned. Den gamla korrupta
diagnostik-/glyphytan ligger fortfarande uppe till vänster, så detta bevisar
verklig nivågrafik men ännu inte korrekt slutlig framebufferkomposition.

## Level-loadern är passerad

Fortsättningen från f2260 nådde hela vägen genom nivå- och powerupfaserna:

```text
f2460  loader state 0x0c  /d0/levels/levelE1/objects.rom
f2560  loader state 0x29  /d0/powerups/objects.rom + textures.rom
f2660  loader state 0x2b  /d0/powerups/anim.rom, stream 0x220/0x220
f2760  loader state -1    main state 0x8008
```

Vid f2760 var level-loadern alltså inaktiv och huvudmaskinen hade lämnat
`0x8004` för post-loader/game-init i `0x8008`. Den riktiga object arena-fixen
bar då minst 832 objekt och fortsatte till minst 1024 objekt vid f2860, långt
förbi den gamla stackkollisionen vid objekt 160. Även
`/d0/items/levelF1/objects.rom` publicerades.

f2860 visade Gauntlet Dark Legacy-logotypen och verklig färglagd miljögrafik.
Vid f2960 fyllde en texturerad 3D-scen med lava och stenarkitektur nästan hela
bilden:

```text
main state       0x8008
loader state     0xffffffff
frame hash       0x45be5521
non-black        216483
colored          216250
draw packets     281413
textured tris    6768
```

Det återstående felet är nu verklig renderkorrekthet, inte bringup eller
assetladdning. Geometrin/kameran är felkomponerad med stora överlappande ytor.
Post-loadertrafiken ger reproducerbara
`VOODOO-CMDFIFO-TEXTURE-STATE-TAIL`-händelser där upp till 13 ord hoppas över,
men den äldre kausalproven visar att detta är den avsedda, redan validerade
ägarskapsfixen som skyddar `fbiInit3`. Den är inte en ny blockerare. Tidigare
rasterprover visar även att viewportprojektionen kommer färdigberäknad från
gästen; lägg därför inte till en host-side projektionsclamp eller ett
downstream triangelfilter. Fortsätt i stället state-`0x8008`-ägaren naturligt
och använd dess stabila counter/inputgräns för nästa övergång mot
attract/gameplay.

En naturlig fortsättning nådde counter `10` vid f3100. En fyrframes FIRE3-edge
vid f3100--f3103 satte exit-latchen, och gästkoden utförde sedan övergången:

```text
f3100  main=0x8008 counter=10 latch=0
f3120  main=0x8008 counter=10 latch=1
f3160  main=0x8002 counter=0  latch=1
f3250  main=0x8002 counter=4  latch=0
```

f3250 visade riktig karaktärsgrafik och `CREDITS` bakom diagnostikmenyn. Den
korrekt tidsatta MAME-inputcykeln
`coin -> start -> fight -> up -> fight` kördes därifrån till f3530 men lämnade
denna active-save-gren i `0x8002` med counter `56`. Det avfärdar inte den
tidigare positiva `0x8001`-vägen: den startade från en separat, isolerad
MAME-timekeeper/PIC-snapshot. Den bevarade f140-snapshotten ska först byggas
om till f240 med sina sparade 200000 CPU-steg per frame; själva inputcykeln
ska därefter använda den verifierade 60000-stegskadensen. Blanda inte den
provenansen med active-save-karusellen.

## Den isolerade grenen når state 0x8001 igen

Den bevarade `/tmp/gaunt-mame-nvram-f140.warm.gz` byggdes om till f240 med
200000 CPU-steg per frame. Den nya snapshotten reproducerade den historiska
orakelns starka räknare exakt:

```text
Type-3 packets  28019
LFB writes      11753881
swaps           464
main state      0x8000
counter         54
```

f240-bilden visar en läsbar diagnostikmeny över full Gauntlet-karaktärs- och
creditsgrafik. Därefter laddades 200k-snapshotten med den uttryckliga,
dokumenterade 60000-stegskadensen och fick MAME-orakelns första inputcykel:

```text
coin@240-245
start@330-335
fight@420-425
up@465-495
fight@510-515
```

Alla inputmasker verifierades i runtime-bridgen. Vid f520 hade gästen fångat
cykeln som `latch=1` men arbetade fortfarande i state-`0x8000`-byggaren.
Ytterligare 100 frames utan input lät ägaren returnera naturligt:

```text
f620 main state  0x8001
f620 counter     4
f620 latch       0
frame hash       0xb32cd5bf
non-black        292471
colored          291681
```

f620-bilden innehåller en riktig 3D-scen med flera figurer på sten-/lavagolv.
Minnesdiagnostikrader ligger fortfarande ovanpå, men detta reproducerar den
positiva state-`0x8001`-grenen med dagens kompletta v15-runtime och är den nya
gameplay-orienterade huvudcheckpointen.

## Andra orakelcykeln når diagnostikägaren, inte initials

State-`0x8001` fortsatte utan input till f1140 och visade en ren
attract-/världsscen med figurer och en stor grön sköld. Den andra
MAME-tidsatta inputcykeln kördes därefter:

```text
coin@1140-1145
start@1230-1235
fight@1320-1325
up@1365-1395
fight@1410-1415
```

Gästen stannade i `0x8001`, men diagnostikmenyn återkom och öppnade senare
sin objektvy. Naturlig fortsättning gav:

```text
f1430 main=0x8001 counter=112 latch=0
f1530 main=0x8001 counter=126 latch=0
f1730 main=0x8001 counter=152 latch=0
f1980 main=0x8001 counter=184 latch=0
```

Detta är en riktig inputrespons men inte MAME-orakelns `ENTER INITIALS`.
Bildrutorna fortsätter rendera levande 3D bakom diagnostiktexten.

Menyn anger själv `Exit menu (FIRE 3)`. En femframes FIRE3/Turbo-edge vid
f1980--f1984 utförde den naturliga gästövergången:

```text
f2080 main=0x8007 counter=0  latch=1 frameHash=0x30e41dc5
f2180 main=0x8007 counter=10 latch=0 frameHash=0x586c7757
```

Mellan dessa checkpoints laddade gästen `hiscore/legends`, ansiktsmodeller
och high-score-paneler. f2180 visar den riktiga `Legends`-sidan med namn som
RIZ, SJB och DON, men diagnostikens gamla textrecords ligger kvar ovanpå.
Detta bekräftar att input-, loader-, state-owner- och high-score-flödena
fungerar. Det bekräftar inte gameplay: EutherDrive väljer fortfarande
diagnostikfamiljen där samma isolerade MAME-provenance når initials.

## Rotorsak och fix

Två oberoende heap-stack-kollisioner orsakade den tidigare cachetrampolin- och
lågminnesloopen:

1. Objektallokatorn vid `0x800af2cc` fortsatte efter 160 objekt och lät den
   generiska heapen växa in i stacken. Objektets konstruktor nollade därefter
   en sparad returadress. Den signaturvaktade object arena-fixen placerar dessa
   0x70-byteobjekt i `0x80a00000..0x80a70000`.
2. Resurskopieraren försökte lägga en `0x22400`-bytes stream i den
   `0x2000`-bytes buffert som började vid `0x807fdcac`. Kopieringen skrev över
   stackens sparade `ra` med `0x2a`. Den signaturvaktade stream arena-fixen
   flyttar just denna transienta stream till
   `0x80b00000..0x80b24000`.

Båda fönstren verifierades helt nollställda i f645-kontrollpunkten innan de
aktiverades. Det avfärdade experimentet som bara återställde `ra` har tagits
bort; det dolde symptom men lämnade den övriga stackkontexten korrupt.

Fixarna ingår nu i både appens bringup-baseline och
`tools/GauntletProbe/run-gauntdl-baseline.sh`:

```text
EUTHERDRIVE_GAUNTDL_FIX_RUNTIME_ASSET_OBJECT_ARENA=1
EUTHERDRIVE_GAUNTDL_FIX_RUNTIME_ASSET_STREAM_ARENA=1
```

## Lokala kontrollpunkter

De här filerna ligger i den git-ignorerade artifact-katalogen:

```text
artifacts/gauntlet-probe/gaunt-asset-arenas-f670.warm.gz
artifacts/gauntlet-probe/gaunt-asset-arenas-f680.warm.gz
artifacts/gauntlet-probe/gaunt-asset-arenas-f680.ppm
artifacts/gauntlet-probe/gaunt-asset-arenas-f680.png
artifacts/gauntlet-probe/gaunt-asset-arenas-f700.ppm
artifacts/gauntlet-probe/gaunt-asset-arenas-f700.png
artifacts/gauntlet-probe/gaunt-fire3-f740.warm.gz
artifacts/gauntlet-probe/gaunt-fire3-f740.ppm
artifacts/gauntlet-probe/gaunt-fire3-f740.png
artifacts/gauntlet-probe/gaunt-fire3-second-f760.warm.gz
artifacts/gauntlet-probe/gaunt-fire3-second-f760.ppm
artifacts/gauntlet-probe/gaunt-post-exit-f780.warm.gz
artifacts/gauntlet-probe/gaunt-post-exit-f780.ppm
artifacts/gauntlet-probe/gaunt-post-exit-f780.png
artifacts/gauntlet-probe/gaunt-post-exit-f800.warm.gz
artifacts/gauntlet-probe/gaunt-post-exit-f800.ppm
artifacts/gauntlet-probe/gaunt-post-exit-f800.png
artifacts/gauntlet-probe/gaunt-state8005-postfade-fire3-f1210-60k.warm.gz
artifacts/gauntlet-probe/gaunt-state8005-postfade-fire3-f1210.ppm
artifacts/gauntlet-probe/gaunt-state8005-postfade-fire3-f1210.png
artifacts/gauntlet-probe/gaunt-level-loader-f1310-60k.warm.gz
artifacts/gauntlet-probe/gaunt-level-loader-f1310.ppm
artifacts/gauntlet-probe/gaunt-level-loader-f1310.png
artifacts/gauntlet-probe/gaunt-level-loader-f1410-60k.warm.gz
artifacts/gauntlet-probe/gaunt-level-loader-f1410.ppm
artifacts/gauntlet-probe/gaunt-level-loader-f1410.png
artifacts/gauntlet-probe/gaunt-level-loader-f1460-60k.warm.gz
artifacts/gauntlet-probe/gaunt-level-loader-f1460.ppm
artifacts/gauntlet-probe/gaunt-level-loader-f1560-60k.warm.gz
artifacts/gauntlet-probe/gaunt-level-loader-f1760-60k.warm.gz
artifacts/gauntlet-probe/gaunt-level-loader-f1860-60k.warm.gz
artifacts/gauntlet-probe/gaunt-level-loader-f1960-60k.warm.gz
artifacts/gauntlet-probe/gaunt-level-loader-f2060-60k.warm.gz
artifacts/gauntlet-probe/gaunt-level-loader-f2160-60k.warm.gz
artifacts/gauntlet-probe/gaunt-level-loader-f2260-60k.warm.gz
artifacts/gauntlet-probe/gaunt-level-loader-f2260.ppm
artifacts/gauntlet-probe/gaunt-level-loader-f2260.png
artifacts/gauntlet-probe/gaunt-level-loader-f2360-60k.warm.gz
artifacts/gauntlet-probe/gaunt-level-loader-f2460-60k.warm.gz
artifacts/gauntlet-probe/gaunt-level-loader-f2560-60k.warm.gz
artifacts/gauntlet-probe/gaunt-level-loader-f2660-60k.warm.gz
artifacts/gauntlet-probe/gaunt-level-loader-f2760-60k.warm.gz
artifacts/gauntlet-probe/gaunt-post-loader-f2860-60k.warm.gz
artifacts/gauntlet-probe/gaunt-post-loader-f2860.png
artifacts/gauntlet-probe/gaunt-post-loader-f2960-60k.warm.gz
artifacts/gauntlet-probe/gaunt-post-loader-f2960.png
artifacts/gauntlet-probe/gaunt-state8008-f3100-60k.warm.gz
artifacts/gauntlet-probe/gaunt-state8008-fire3-f3120-60k.warm.gz
artifacts/gauntlet-probe/gaunt-post-state8008-fire3-f3160-60k.warm.gz
artifacts/gauntlet-probe/gaunt-state8002-f3250-60k.warm.gz
artifacts/gauntlet-probe/gaunt-oracle-cycle-f3530-60k.warm.gz
artifacts/gauntlet-probe/gaunt-oracle-cycle-f3530.png
artifacts/gauntlet-probe/gaunt-mame-nvram-f240-rebuilt-200k.warm.gz
artifacts/gauntlet-probe/gaunt-mame-nvram-f240-rebuilt.png
artifacts/gauntlet-probe/gaunt-mame-oracle-cycle1-f520-rebuilt-60k.warm.gz
artifacts/gauntlet-probe/gaunt-mame-oracle-cycle1-f520-rebuilt.png
artifacts/gauntlet-probe/gaunt-mame-oracle-postcycle-f620-60k.warm.gz
artifacts/gauntlet-probe/gaunt-mame-oracle-postcycle-f620.png
artifacts/gauntlet-probe/gaunt-mame-oracle-precycle2-f1140-60k.warm.gz
artifacts/gauntlet-probe/gaunt-mame-oracle-cycle2-f1430-60k.warm.gz
artifacts/gauntlet-probe/gaunt-mame-oracle-postcycle2-f1530-60k.warm.gz
artifacts/gauntlet-probe/gaunt-mame-oracle-postcycle2-f1730-60k.warm.gz
artifacts/gauntlet-probe/gaunt-mame-oracle-postcycle2-f1980-60k.warm.gz
artifacts/gauntlet-probe/gaunt-fire3-exit-f2080-60k.warm.gz
artifacts/gauntlet-probe/gaunt-fire3-postreturn-f2180-60k.warm.gz
artifacts/gauntlet-probe/gaunt-fire3-postreturn-f2180.png
```

f670-snapshotten återladdades med noll körda frames och gav deterministiskt
`frameHash=0x30e41dc5`, `pc=0xffffffff80078670` och `swaps=3294`.

## Nästa pass

Fortsätt den levande initials-transitionen från f2520 och gör QIO-cleanupen
timer-/completion-korrekt. Patching av huvudstate eller bortfiltrering av
diagnostikens trianglar är inte giltiga lösningar.

## Portabel MAME-orakel och exakt initials-writer

MAME 0.288 kördes portabelt och headless mot en isolerad kopia av den
bevarade NVRAM-provenancen. Samma 900-frame inputcykel upprepades från frame
1200. Vid frame 2280 visar den riktiga MAME-bilden `ENTER INITIALS`, och hela
32 MiB huvud-RAM gav:

```text
main state 0x80227ab0 = 0x400a
request    0x8020c534 = 0x8001
counter    0x80227b74 = 0x38
latch      0x80227ec8 = 0
```

Det avfärdar antagandet att request-ordet måste lämna `0x8001`. En
frame-för-frame-trace visade den verkliga sekvensen:

```text
f1384 main 0x8001  efter första fight-pulsen
f2195 main 0x400a  fem frames efter nästa start-puls
```

MAME-debuggerns skriv-watchpoint fångade själva övergången:

```text
frame 2194
writer PC 0x80085448
value     0x0000400a
```

`0x80085448` ligger i funktionen som börjar vid `0x8008540c`. Dess enda
direkta caller ligger vid `0x800139c0`. Originalets transition-tail börjar
vid `0x80013990` och gör flera cleanup-anrop innan initials-funktionen.

Den tidigare host-inputbryggan och ett nytt explicit
`EUTHERDRIVE_GAUNTDL_EXPERIMENT_RUNTIME_NATIVE_INPUT_POLL=1` testades mot
samma snapshot. Native-pollen kör gästens riktiga `0x800eb078`, läser
MAME-korrekta portar och fyller de extra runtime-inputposterna, men den
förklarar inte ensam statevalet. Experimentet är därför avstängt som
standard.

## Levande transition och VBlank-gränsen

En rå patch av state till `0x400a` aktiverade omedelbart gameplay-koden och
gav 520 texturerade trianglar, men ofullständig data. Ett synkront anrop av
initializern gav korrekt `AllocMem() called while mem reserved`; cleanupen
måste köras först och transitionen sträcker sig över flera frames.

Det explicita experimentet
`EUTHERDRIVE_GAUNTDL_EXPERIMENT_ENTER_RUNTIME_INITIALS=1` växlar därför den
levande CPU-kontexten till originalets tail `0x80013990` med funktionens
40-byte caller-frame. Det första naturliga stoppet var frame-waiten vid
`0x800136d4`, som väntar på förändring i `0x80227b44`. Bringupens
`RecordRuntimeVblankTick()` uppdaterade två andra tickord men inte detta.
VBlank-bryggan ökar nu även `0x80227b44` med ett per frame.

Efter fixen lämnar CPU:n waiten på nästa VBlank, kör initials-cleanupen och
fortsätter genom QIO-timeout-fallbacken. Vid f2520 är CPU:n levande och
swaps/packet counters fortsätter, men writer-PC `0x80085448` har ännu inte
nåtts. Den aktuella gränsen är alltså cleanupens QIO-completion, inte längre
input-selector, state-dispatch eller VBlank.

Nya lokala checkpoints:

```text
artifacts/gauntlet-probe/gaunt-live-transition-vblank-f2320-60k.warm.gz
artifacts/gauntlet-probe/gaunt-live-transition-vblank-f2520-60k.warm.gz
artifacts/gauntlet-probe/gaunt-live-transition-vblank-f2520.ppm
```

## Cleanup-QIO:n är konsumerad och huvudstate är 0x400a

f2520-staten avslöjade QIO-objektet `0x80295440` med state `0x7107`,
status noll och noden `0x80295470` publicerad på scheduler-ready-listan.
Den tidigare körningen använde rå `EUTHERDRIVE_GAUNTDL_BRINGUP_FAST=1`.
Eftersom den profilen gör ospecificerade bringup-fixar sanna aktiverade den
också den gamla globala `RUNTIME_INTERRUPT_SUPPRESS`. CPU:n såg Nile-pulsen
som `Cause=0x0800`, men kastade den före exception-entry.

Med den kanoniska `EUTHERDRIVE_GAUNTDL_BRINGUP_BASELINE=1`, som uttryckligen
sätter suppression till noll, och en enda guest-routad timerpuls gick den
obrutna kedjan:

```text
0x80000180  exception vector
0x800dec10  runtime interrupt handler
0x800dea2c  timer/scheduler dispatch
0x800f087c  QIO worker, a0=0x80295440
0x800de480  scheduler return
```

Riktiga IDE-interrupt följde. Vid f2540 är filesystem- och
scheduler-ready-listorna tomma, QIO-objektet har handle `0xffffffff` och
status `0x0500`, och RAM-dumpen visar:

```text
0x80227ab0 = 0x400a
```

Detta är den MAME-verifierade `ENTER INITIALS`-staten, nådd efter originalets
cleanup-tail i stället för genom en state-patch. Initials-experimentet ger nu
självt en guest-korrekt schedulerpuls var hundrade frame medan det är aktivt;
normal baseline påverkas inte. En default-off, begränsad
`EUTHERDRIVE_GAUNTDL_TRACE_RUNTIME_INTERRUPT` visar Nile request, maskning och
exception-entry för framtida IRQ-fel.

Efter statebytet laddar gästen riktiga select/player-resurser. Vid f2660 är
state fortfarande `0x400a`, aktuell sökväg är
`/d0/select/textures.rom`, texture writes har nått `5 213 588`, men inga nya
Type-3 draw packets har ännu emitterats efter framebuffer-clear. Nästa gräns
är därför select-loaderns completion och första initials-draw, därefter
initials-inputen.

Reload-verifierade fortsättningar:

```text
artifacts/gauntlet-probe/gaunt-live-transition-scheduler-f2540-60k.warm.gz
artifacts/gauntlet-probe/gaunt-initials-f2560-60k.warm.gz
artifacts/gauntlet-probe/gaunt-initials-f2660-60k.warm.gz
```

## Första riktiga initials-bilden och hela inputkedjan

Select-loadern blir färdig utan ytterligare fixar. Vid f2760 har gästen
fortfarande huvudstate `0x400a`, men har nu emitterat 510 nya texturerade
trianglar och en verklig fyrspelarscen:

```text
drawPackets  = 170670
rasterPixels = 280827
nonBlack     = 162221
colored      = 156454
frameHash    = 0x4d578d50
```

Bilden matchar MAME:s `ENTER INITIALS`-layout: Player 1:s initials-panel
ligger längst ned till vänster, medan Player 2--4 visar `INSERT ... TO JOIN
GAME`. Den stora rubriken och P1-panelen är delvis dolda av de kvarvarande
horisontella renderfelen, så scenen får inte misstolkas som en vanlig
coin/join-prompt.

```text
artifacts/gauntlet-probe/gaunt-initials-f2760-60k.warm.gz
snapshot sha256 b1db2a8d1930881fb251ed76d77add7f78edb441fe622f37dc9ac216adef74ef
artifacts/gauntlet-probe/gaunt-initials-f2760.ppm
ppm sha256 a40529149f46bb5854d0c3b6d68000ff1d9e367c347b2907dbaac9096c811b7f
```

Ett A/B-test mellan host-inputbryggan och den default-avstängda native-pollen
gav exakt samma f2840-framebuffer och draw-count. Coin/start/fight-sekvensen
ändrade animationen men lämnade korrekt state `0x400a`, eftersom Player 1
redan befinner sig i initials-editorn.

Den riktiga nästa gränsen var tidskvantiseringen. Vid 60 000 CPU-steg per
probe-frame hann en kort knappuls skrivas till runtime-recordet men inte nå
gästens normaliserare. Ett isolerat 10M-stegspass från den rena f2760-staten
visade hela kedjan utan RAM-patch:

```text
runtime record 0x80262b90 = 0x00000020  (P1 DOWN)
0x80019b9c inputnormalisering körs
0x800eb834 index 0 läser a2=0x00000020
0x80227ba8 = 0x00000020                  (held)
```

Det bevisar att initials-editorn får riktig input. Nästa pass ska mata
press/release i hela gästloopskvantum och sedan bekräfta tre initialer med
Fight. Fortsättningar från f2760 ska inte återaktivera
`EUTHERDRIVE_GAUNTDL_EXPERIMENT_ENTER_RUNTIME_INITIALS`; använd vid behov en
explicit 100-frame guest-timer-IRQ. Själva initials-experimentets automatiska
schedulerpuls är nu begränsad till tiden före huvudstate `0x400a`.

## Native COIN + START når den riktiga initials-skärmen

MAME-tracen hittade input-sidans två saknade periodiska gästcallbacks:

```text
0x80067d94  clock callback
  -> 0x80089e00 input/counter updater
  -> 0x80089ea8 exakt START-counter-writer vid 0x8020c780

0x800e1178  coin callback
  -> 0x800d4cb8 coin poll
  -> 0x800d5058 coin increment helper
  -> 0x800d50a0 exakt byte-writer vid 0x802190d0 + input-index
```

MAME anropar coin-callbacken 68 gånger under fyra videoframes. Bryggan kör
därför originalkoden 17 gånger per frame. Clock-callbacken ersätter den äldre
direkta ökningen av game-time-ordet när den är aktiv.

Efter att debounce-byten initierats nådde en riktig COIN-puls den exakta
coin-writern. En ren START press/release fortsatte sedan genom spelets egen
join-kedja:

```text
0x800138d0  player-join caller
0x80013938  payment -> 0x800d5960, returnerar 1
0x80013954  skriver active-player-mask 0x80227af4 = 1
0x80013990  startar transition-cleanup
0x800139c0  anropar initials-state-funktionen
0x80085448  skriver main state 0x80227ab0 = 0x400a
```

Ingen guest-RAM-patch eller direkt transition-entry ingår. En viktig detalj
för fortsatt inputautomation är att nästa START-edge inte är ny förrän
runtime record 0 har släppt de gamla `0x300` FIRE3/START-bitarna till
`0x200`.

Samma obrutna körning laddade select-resurserna och renderade den riktiga
fyrspelarscenen vid f2800:

```text
state       = 0x400a
player mask = 1
frameHash   = 0x861e3281
nonBlack    = 147374
colored     = 143355
PNG sha256  = 8b7a826f7f87f4b6e073d134ce049b3177298ef00c8d69869f1000848067b28e
RAM sha256  = 83004fd0b6ec49ac31bd87ff03d117eff4dbed60cdb12539cbb1384ed802d0c2
```

Player 1 visar `ENTER INITIALS`; Player 2--4 visar join-prompter.
Horisontella rasterfel och gammal diagnostikgeometri finns kvar, men
spelvägen är nu entydig.

Callbacksen är promoterade till baseline:

```text
EUTHERDRIVE_GAUNTDL_FIX_RUNTIME_CLOCK_CALLBACK=1
EUTHERDRIVE_GAUNTDL_FIX_RUNTIME_COIN_CALLBACK=1
```

Central preset och `tools/GauntletProbe/run-gauntdl-baseline.sh` aktiverar
dem explicit. En ombyggd baseline-only reload behöll `state=0x400a` och
Player 1-masken utan de tidigare experimentvariablerna. Nästa pass ska
kvantisera initials-riktningar och Fight över hela gästloopar, mata tre
bokstäver och följa originalvägen vidare till password/character selection.

## Native Player 1 skriver ZZZ

Den gamla f2760-checkpointen hade Player 1-panel men active-player-mask `0`,
eftersom den kom från den tidigare manuella transitionen. Riktig initials-
interaktion måste därför börja från den nya native-kedjan. Baseline-f2800 har:

```text
state                  = 0x400a
active-player mask     = 1
initials object        = 0x80229460
candidate glyph        = 0x0040
entered name           = 00 5f 5f
snapshot sha256        = b5e5f621767a3d230ff0696bdf4bfc5d21c4e204d0c85994f8113e2058149055
```

Ett korrekt inputkvantum består av:

1. ett vanligt 60k-frame som skriver host-input,
2. CPU-steg till den sign-extended normalizer-entryn
   `0xffffffff80019b9c`,
3. steg till return-PC `0xffffffff80014cc8`,
4. ett release-frame och samma normalizer/return-sekvens.

Att använda `0x80019b9c` som stop-PC missar eftersom CPU-PC:n är
sign-extended. Att köra hela 10M som ett frame är också fel: då hinner
initials-sessionen löpa ut.

Två DOWN-edge:ar flyttade candidate `@ -> _ -> Z`. Editorns cooldown vid
objekt-offset `+0x16` måste nå noll innan Fight accepteras. Fight, Magic,
Turbo och START normaliserades alla korrekt (`0x200`, `0x400`, `0x800`,
`0x100`), men försök under cooldown ändrade inte namnet. Med cooldown noll
bekräftade Fight bokstaven.

Tre stabiliserade Fight-cykler gav:

```text
after letter 1  name=Z__ position=1 completion=0
after letter 2  name=ZZ_ position=2 completion=0
after letter 3  name=ZZZ position=3 completion=1
```

Vid f2812 är state fortfarande `0x400a`, P1-masken är `1`, och
`0x80229478..7a` innehåller ASCII `ZZZ`. Fem miljoner fortsatta CPU-steg och
100 riktiga baseline-frames gav ingen write till main-state. Nästa gräns är
därför post-initials completion/timer efter ett redan färdigt namn, inte
längre inputnormalisering eller bokstavsval.

Bevarade lokala fortsättningar:

```text
/tmp/euther-native-baseline-initials-f2800.warm.gz
/tmp/euther-native-initials-z-ready-f2804.warm.gz
/tmp/euther-native-initials-zzz-confirmed-f2812.warm.gz
/tmp/euther-native-initials-zzz-released-f2812.warm.gz
/tmp/euther-native-post-initials-zzz-plus5m-f2812.warm.gz
```

## Native runtime-klocka efter initials

MAME-debuggerns breakpoint vid `0x80067d94` räknade exakt nio
clock-callbacks per videoframe (`270/30` och `3150/350`). En RAM-dump från
samma oracle visade dessutom att callbackens frekvensord
`0x80228180` är `250000000`. Euther-checkpointen hade noll där, vilket gav:

```text
2.0 / 0 -> +Infinity
MADD vid 0x80067e24 -> NaN
total game time 0x80227dd8 -> NaN
```

Baseline initierar nu frekvensordet till 250 MHz och reparerar bara
icke-finita tidsvärden. Därefter körs originalcallbacken nio gånger per
frame. Eftersom probens accelererade gästvägar inte motsvarar verklig
CP0-Count-tid fryses Count under den kontextbevarande callbacken och den får
ett deterministiskt delta för R5000 Count: `125 MHz / 60 Hz`, fördelat över
de nio anropen. Själva guest-koden räknar fortfarande ut och skriver både
total tid och framedelta.

Ett tioframesprov från den bevarade ZZZ-checkpointen gav:

```text
0x80228180 frequency     = 250000000
0x80227dd8 total time    = 0x3e2aaaa9 ~= 0.16666664 s
0x80227ddc frame delta   = 0x3c88889a ~= 1/60 s
NaN/Infinity            = none
frameHash                = 0xbdcee336
```

Det isolerar nästa post-initialsproblem: objektets countdown vid
`0x80229470` stod kvar på `0x0382`, trots korrekt game time. MAME minskar
den en gång per frame. Nästa spårning ska därför hitta den separata
scheduler/writer-kedjan för countdownen; den ska inte lösas med en direkt
RAM-patch.

## Native post-initials-scheduler och fas 3

MAME-trace visar att caller `0x80021620` anropar spelaruppdateraren
`0x800662c4` en gång vartannat videoframe. Uppdateraren läser scheduler-delta
`2` från `0x80227b48` och minskar både initials- och completion-timrarna med
det värdet. När den returnerar nonzero fortsätter originalcallern till
fas-3-initialiseraren `0x80066510`.

Baseline kör nu samma originalfunktion med samma 30 Hz-kadens för aktiva
spelare. Nonzero-returen driver den riktiga fas-3-initialiseraren och stoppar
sedan den syntetiska caller-kedjan för spelaren. Completion-masken sparas i
warm-snapshot v16; läsaren är fortsatt bakåtkompatibel med v1-v15.

Från den bevarade ZZZ-snapshoten gav åtta frames:

```text
completion 0x8022947c  = 1 -> 0 (write vid PC 0x80066574)
player phase +0xc8     = 3
phase-3 timer +0x216   = 0x0e10
entered name           = ZZZ
frameHash              = 0xec504e1e
```

Efter reload av v16-snapshoten gav fyra fortsatta frames ingen ny skrivning
till completion-flaggan. Native fas-3-timrar fortsatte däremot att ticka:

```text
+0x216  0x0e10 -> 0x0e0e
+0x218  0x0384 -> 0x0382
+0x21e  0x001e -> 0x001c
completion remains     = 0
frameHash              = 0xeeffdbb3
```

Detta är den första reloadbara checkpointen efter färdig initialinmatning.
Nästa oracle är övergången när fas-3-timern löper ut; timrarna ska fortsatt
drivas av native guest-kod och inte genom direkta RAM-patchar.

## Avbrottsåterstart 2026-07-31: fas 3 passerad

Efter datorfrysningen återfanns den senaste lokala fortsättningen som:

```text
.build-tmp/euther-native-phase3-to-game-f4100.warm.gz
```

Snapshotten var skapad före den ocommittade fas-3-callerändringen. En ren
fortsättning från f4100 till f4200 med den ändringen reproducerades två gånger
med exakt samma slutresultat:

```text
frameHash       = 0x44ef1458
nonBlack        = 217882
colored         = 209055
draw packets    = 326464
swaps           = 2790
```

Player 1 låg fortfarande i fas 3. Dess guest-timer minskade naturligt från
`0x0d38` till `0x0cc4` under de 100 framesen, vilket visade att vägen levde
men också att ett fullständigt renderat väntetest skulle kräva tusentals dyra
probe-frames.

En default-avstängd accelerator lades därför till:

```text
EUTHERDRIVE_GAUNTDL_EXPERIMENT_RUNTIME_PHASE_THREE_SCHEDULER_TICKS_PER_SERVICE
```

Den upprepar samma originalfunktion `0x800669c0`; den skriver inte timern
direkt och ändrar inte baseline när variabeln saknas. Med värdet `4096` från
den rena f4200-snapshotten nådde gästkoden sin riktiga nonzero-retur på två
probe-frames. Host-caller-steget följer den uppmätta
`0x8002179c..0x800217e8`-kedjan:

```text
phase 3 updater   0x800669c0
phase field       3 -> 4
phase-4 timer     300
phase-4 setup     0x800229cc
state refresh     0x80020810
resulting phase   5
```

Vid f4202 hade diagnostikmenyn försvunnit och Player 1:s karaktärsvy
renderades. En normal, oaccelererad fortsättning till f4232 var stabil,
återställde active-player-masken till `1` och behöll fas `5`:

```text
frameHash       = 0xc766b6b0
nonBlack        = 221190
colored         = 215058
draw packets    = 329258
```

Ett första riktigt speltestinput från denna checkpoint,
`fight@4232-4236`, ändrade framebuffer men lämnade fasen på `5`. Nästa
avgränsade blockerare är därför den native fas-5-dispatcher som följer
karaktärsvyn, inte längre initials-editorn eller fas-3-countdownen. Spåra
MAME-callergrenen efter `0x800217e8` och driv samma guestägda fas-5-funktion
kontextbevarande innan fler inputsekvenser provas.

Reloadbara lokala checkpoints:

```text
.build-tmp/euther-native-phase3-to-game-f4200.warm.gz
.build-tmp/euther-native-phase4-fast-f4202.warm.gz
.build-tmp/euther-native-phase5-natural-f4232.warm.gz
.build-tmp/euther-native-phase5-fight-f4260.warm.gz
```

Alla nya snapshots är gzip-komprimerade och ligger repo-lokalt. Ingen
Gauntlet-snapshot eller växande logg skrevs till `/tmp`; efter passen var
vanliga filer direkt under `/tmp` cirka 200 KB totalt.

## Desktopfortsättning 2026-07-31: fas-5-dispatch uppmätt

Ett nytt bounded MAME-orakel,
`tools/GauntletProbe/mame-gauntdl-phase5-oracle.lua`, reproducerar
character-select-kedjan från en repo-lokal fas-4-save utan växande
instruktionstrace. Den sparar endast explicita checkpoints och kan köra
separata no-input-, Right-, Fight- och Turbo-fönster.

MAME bekräftade följande originalväg:

```text
phase-4 timer                300 -> 0
released action edge         phase 4 -> 5
phase-5 dispatcher           0x800862f0
phase-5 caller branch        0x8008665c
phase-5 player handler       0x80085034
handler argument a0          player index
per-player call gate         playerBase + 0x93c == 0
caller-side return write     none
```

En enda tvåframes-instruktionstrace användes för att bevisa entryn. Den
begränsades till ett fast inputfönster, relevanta rader extraherades till
en 35 KiB-fil och den 22 MiB stora råtracen raderades direkt. Alla MAME
states, bilder och kortlivade probes ligger under `.build-tmp/`; `/tmp`
användes inte.

Fas 5 är den färdiga Sorceress-karaktärsvyn, inte ännu en
karaktärsväljare. MAME lämnar den naturligt utan mer input:

```text
relative frame 100   state=0x400a phase=5
relative frame 200   state=0x400c phase=5
relative frame 300   state=0x400c phase=1
```

EutherDrive har nu en kontextbevarande anropsväg till `0x80085034` med
samma fas- och slotvillkor. Två rena f4232 -> f4236-körningar gav identiskt
resultat:

```text
frameHash       = 0x634ca87c
nonBlack        = 221841
colored         = 215709
main state      = 0x400a
player phase    = 5
```

Vid f4248 hade handlern gjort fler riktiga fill/swap-anrop men samtliga tre
host-färgbuffertar var tomma. Det är nästa separata desktop-rendergräns;
den får inte döljas med en RAM-patch eller syntetisk framebuffer. Nästa
state-milstolpe är att driva samma handler tills EutherDrive naturligt når
`0x400c`, samtidigt som den första felande phase-5-triangeln eller
buffer-clearen ringas in.

## Desktopfortsättning 2026-07-31: fas 5 körs i originalets 60 Hz

Ett frame-för-frame-orakel i MAME visade att fas-5-handlern anropas varje
videoframe. Initialinmatningen och fas 3 ligger däremot kvar på sin uppmätta
30 Hz-takt. Spelarschedulern har därför delats så att endast
`0x80085034`-vägen för fas 5 körs i 60 Hz.

En ren f4232 -> f4234-körning efter ändringen gav:

```text
player timer    = 0x141f -> 0x1426
frameHash       = 0x75c2c4e3
nonBlack        = 219221
colored         = 213117
main state      = 0x400a
player phase    = 5
```

En längre naturlig körning till f4264 ökade samma guestägda timer till
`0x148c` och sparades som:

```text
.build-tmp/euther-native-phase5-60hz-f4264.warm.gz
SHA-256 b56571a0675e468f42e6e3b625c232c631c93ec9543d08383ba7ad17dd69ac76
```

Snapshotten är 6,8 MiB och gzip-komprimerad. Vid f4264 är state fortfarande
`0x400a` och fasen fortfarande `5`, vilket stämmer med att MAME först når
`0x400c` omkring relative frame 200. Fortsatta test ska därför starta från
f4264-snapshotten i stället för f4232.

Rendergränsen är samtidigt reproducerad: f4264 har fortsatt FIFO-, raster-,
texture-write-, fill- och swapaktivitet, men alla tre färgbuffertar samt
exporterad framebuffer är helt noll. Nästa renderarbete ska börja vid den
första övergången mellan den fortfarande synliga f4234-bilden och de tomma
buffertarna, inte med ytterligare display-buffer-val eller syntetisk output.

## Desktopfortsättning 2026-07-31: första svarta frame avgränsad

En binärsökning från f4232-snapshotten avgränsade rendergränsen till en enda
frame:

```text
f4245  frameHash=0xdc79dc3a  nonBlack=212410  colored=207347
f4246  frameHash=0x30e41dc5  nonBlack=0       colored=0
```

Sista synliga läget är sparat repo-lokalt som:

```text
.build-tmp/euther-native-phase5-last-visible-f4245.warm.gz
SHA-256 d2b004a026fdc31ac8b0e87d570458f65c28057b9a52eb7b5200f9939fd5b0c2
```

En bounded enframesdiagnos från den snapshotten visade att den svarta
övergången inte orsakas av en ny triangel:

```text
draw packets     330348 -> 330348
fast fills       326 -> 327
swaps            2792 -> 2832
LFB writes       142427595 -> 142581195
```

Den enda nya fillen är ett legitimt svart `fastfillCMD` från FIFO-paket
`0x0104824c`, vid guest-PC `0x801027cc`. Samma frame verkställer dessutom
20 direkta och 20 FIFO-dekodade `swapbufferCMD=0` från
`0x80102a80` respektive `0x80102ab4`. En registertrace verifierade att den
direkta vägen skriver via `s3=0xffffffffa8000000`, medan den andra vägen
dekodar type-1-paket `0x00010251`.

MAME:s Voodoo-modell bekräftar att bit 0 noll betyder omedelbar swap, men
den köar FIFO-märkta registerskrivningar medan en operation är pending och
gör en scanline-partial-update innan buffertrotationen. EutherDrive saknar
fortfarande båda dessa tidsdelar och konsumerar därför hela burstsekvensen
omedelbart mot sina förenklade hela-frame-buffertar.

Den befintliga default-avstängda
`EUTHERDRIVE_GAUNTDL_EXPERIMENT_VOODOO_MAME_FIFO_OPERATION_PENDING_GATE`
ändrade inte f4246-resultatet, eftersom den endast stoppar fortsatt
command-FIFO-dekodning efter renderarbete och inte köar direkta
FIFO-registerskrivningar. Nästa implementation ska därför modellera den
generella Voodoo busy/PCI-FIFO-kön och dess swap-presentation. Den ska inte
specialfalla de två Gauntlet-PC-adresserna, behålla en gammal framebuffer
eller kopiera fram en syntetisk bild.

## Desktopfortsättning 2026-07-31: warm snapshot återställer schedulern

En kontroll av den bevarade f4264-snapshotten visade att varje ny
GauntletProbe-process felaktigt körde `StartRuntimeInitialsTransition()` en
gång till. Snapshotformat 16 sparade active-player completion-masken men
inte maskinens övriga schedulerläge, framför allt
`_runtimeInitialsEntered`. Alla tidigare korta reload-prober efter state
`0x400a` hade därför en falsk initials-cleanup i sin första frame.

Snapshotformat 17 sparar nu även:

```text
runtime timer accumulator
runtime clock remainder
30 Hz player update phase
vblank timer-IRQ countdown
runtime initials entered
```

Äldre snapshots förblir läsbara. För version 16 och äldre härleds
`runtime initials entered` endast när det guestägda main state redan är
`0x400a` eller `0x400c`.

En f4264 -> f4265-kontroll efter fixen gav ingen
`runtime-initials-transition`. I stället fortsatte den pågående asset- och
command-FIFO-vägen naturligt:

```text
texture writes    6,438,119 -> 6,548,047
type-5 packets    +2,171
swaps             2,832 -> 2,848
fast fills        oförändrat 327
```

En ny version-17-snapshot vid f4265 reloadades därefter till f4266. PC,
framehash och huvudräknarna för FIFO, texturer, fills och swaps matchade en
obruten f4264 -> f4266-körning. Fortsatta state- och renderprober ska använda
denna korrigerade reloadväg; den tidigare f4245 -> f4246-slutsatsen måste
betraktas som en diagnos av den falska återinträdesframen, inte som
spelvägens verkliga nästa frame.

## Desktopfortsättning 2026-07-31: originalrenderingen återstartar

Med schedulerläget korrekt återställt fortsatte f4265 i avgränsade,
komprimerade block till f4540. Inga råtraces skrevs och inget lades i
`/tmp`. Under den svarta perioden fortsatte originalkoden att ladda
spelvärldens resurser, bland annat:

```text
players/dwf
select
monsters/zom2
monsters/ice2
monsters/imp2
monsters/pla2
monsters/death
weapons
SCORE_ATT8
NAMEFONT
AAAWHITE
```

Vid f4530 nådde player-timern sin fasgräns. Den gick från den tidigare
stigande serien till `0x30`, player phase ändrades `5 -> 2` och riktig
rasterisering återstartade:

```text
frameHash       = 0xaa1516f5
draw packets    = 331668  (+1320)
raster pixels   = 47520
nonBlack        = 18410
colored         = 18410
main state      = 0x400a
```

Tio naturliga frames senare vid f4540 hade scenen byggts ut till den riktiga
fyra-player-vyn med `ENTER INITIALS`, slotramar, bakgrund och central figur:

```text
frameHash       = 0xbe3300c6
draw packets    = 332490
textured tris   = 822
raster pixels   = 526782
nonBlack        = 247949
colored         = 241090
main state      = 0x400a
player phase    = 2
player timer    = 0x52
```

Reloadbar desktopcheckpoint:

```text
.build-tmp/euther-native-phase2-v17-f4540.warm.gz
SHA-256 756402a111c9b227bae30607bdc24fd1cf88a05c90f38cd3b1a50d747314645a
```

Referensbilden ligger i
`.build-tmp/euther-native-phase2-v17-f4540.png` med SHA-256
`4b22a865e13f160a3be7ca79f3ceac90812479eb3d34d33ae17f0d9385f291b1`.

Scenen är ännu inte spelbar: mittpartiet har tydliga textur-/recordfel och
guest state har inte lämnat `0x400a`. Nästa arbete ska börja från f4540,
köra fas 2 naturligt fram till nästa phase/state-write och ringa in de
`render-record-null-body`-poster som motsvarar de trasiga mittpanelerna.
Inputtest hör hemma först när denna naturliga statekedja är verifierad; gå
inte tillbaka till den falska f4246 clear-bursten.

## Desktopfortsättning 2026-07-31: full spelarscheduler genom fas 2 och 4

Den tidigare host-schedulern körde bara separata guestfunktioner för fas 3
och fas 5. En kodump från den reloadbara f4620-checkpointen visar att spelets
fulla per-frame-scheduler börjar vid `0x80020ab4`, loopar över alla fyra
spelarposter och hoppar genom fasfältet vid `playerBase + 0xc8`. Att anropa
den senare `0x800862f0`-dispatchern gav en bit-identisk A/B och var inte
fas-2-ägaren.

Baseline kör nu hela `0x80020ab4` kontextbevarande när en färdig aktiv
spelare ligger i fas 2 eller 4. Inga fasfält eller timrar skrivs av hosten.
Normal kadens är en native scheduler-tick per videoframe. En default-off
accelerator finns bara för bounded bringup:

```text
EUTHERDRIVE_GAUNTDL_EXPERIMENT_RUNTIME_PLAYER_SCHEDULER_TICKS_PER_FRAME
```

Från f4620 minskade fas-2-timrarna med exakt två per native tick. Bounded
64-tickssegment, med en komprimerad checkpoint per process, nådde den riktiga
guestövergången efter 441 ticks:

```text
phase                 2 -> 4
final segment ticks   57 (stoppade automatiskt vid fasbyte)
phase-4 timer         298
main state            0x400a
```

Samma native scheduler minskade därefter fas-4-timern
`298 -> 170 -> 42 -> 0`. En riktig Fight-kant vid timer noll gav:

```text
phase                 4 -> 5
main state            0x400a
frameHash             0x5f8fc447
nonBlack              159076
colored               151149
```

En ren baseline-reload utan accelerator verifierar både fas 2 och fas 4:
fas-2-timrarna tickar med två och fas-4-timern går `298 -> 296` på en frame.
Fas 5 förblir stabil genom minst fem fortsatta frames.

Senaste reloadbara desktopcheckpoint:

```text
.build-tmp/euther-native-phase4-fight-f4631.warm.gz
SHA-256 b4a68d1e88be335218f0871c8f0ac060181a08e53cb77f1de531d06bb4013b13
```

Alla råa 32 MiB RAM-dumpar raderades direkt efter att de små caller-regionerna
extraherats. Inga snapshots eller loggar skrevs till `/tmp`; längre körningar
delades i en-frame-processer för att hålla processminnet bounded.

## Desktopfortsättning 2026-07-31: fas-5-ägaren avgränsad

Från f4631 gav separata no-input-, Right- och Fight-körningar exakt samma
nästa frame:

```text
frameHash   0xa8994587
nonBlack    248012
colored     235712
```

`GauntletProbe` kan nu skriva ut en explicit lista av 32-bitars guestord före
och efter körningen via `EUTHERDRIVE_GAUNTDL_GUEST_MEMORY_WORDS`. Funktionen
är default-avstängd, skriver bara två korta terminalrader och skapar inga
RAM-dumpar.

En en-framekontroll visade att fas-5-gaten var öppen men att den kända
handlern inte ändrade de observerade stateorden:

```text
main state              0x400a -> 0x400a
active players          1 -> 1
player phase            5 -> 5
phase-4/global timer    0 -> 0
player +0x93c gate      0 -> 0
```

Ett bounded prov med 256 extra anrop av `0x80085034` gav inte heller någon
transition. Acceleratorn och dess enda 7 MiB-testsnapshot togs därför bort
direkt. Nästa ägare att spåra är den yttre game-state-dispatchen som i MAME
driver `0x400a -> 0x400c`; fler inputpulser eller direkta handleranrop är inte
motiverade innan den caller-kedjan är uppmätt.

## Desktopfortsättning 2026-07-31: game state och phase 1 nådda

En RAM-kodscan lokaliserade den exakta `0x400c`-skrivaren till den ursprungliga
guestfunktionen `0x80086cec`. Dess två callers ligger vid `0x80013e7c` och
`0x800146a4` inuti den fulla main-state-rutinen `0x80013a10`.

Baselinen kör nu den fulla main-state-rutinen i fas 5 i stället för enbart den
isolerade `0x80085034`-handlern. När den guestägda fas-5-timern når `0x400`
körs originalets transition-initialiserare kontextbevarande. Ingen direkt
RAM-statepatch används. Ett bounded bringupsegment gav:

```text
phase-5 timer       0x0187 -> 0x04ed
main state          0x400a -> 0x400c
transition entry    0x80086cec
```

Från `0x400c` fortsatte den vanliga CPU-vägen utan experiment. Efter 100
naturliga frames nåddes spelarfas 1:

```text
frame               4733
main state          0x400c
active players      1
player phase        1
frameHash           0x19ab6cf9
```

Reloadbar desktopcheckpoint:

```text
.build-tmp/euther-native-game-phase1-f4733.warm.gz
SHA-256 aeffc6cf00ddf2832bce47801e40b4f73db49ccf59714d3727a9e4a40f4883d2
```

Right och Fight når nu den riktiga guest-inputtabellen i phase 1 som `0x80`
respektive `0x200`. Fyraframes A/B ändrade ännu inte framebuffer; den gamla
diagnostikrenderingen ligger kvar över den nya scenen. De två återstående
desktopblockerarna före praktiskt speltest är därför en läsbar framebuffer
utan stale diagnostikposter och runtimeprestanda över probens nuvarande
cirka `0.2-0.27 fps`.

En default-avstängd `EUTHERDRIVE_GAUNTDL_PROFILE_FRAME_PHASES` delar en frame
i callbacks, CPU/Voodoo, devices och presentation. På f4733 gav 200k-budgeten:

```text
callbacks    393 ms
CPU/Voodoo  4586 ms
devices       <1 ms
render        13 ms
```

60k och 10k CPU-steg behöll main state, phase och guest-timerns `+3`-tick men
gav cirka `0.40` respektive `0.89 fps`. Ett prov att batcha MAME:s nio
clockcallbacks och sjutton coincallbacks tappade däremot timerticket och togs
bort. Nästa prestandasteg ska därför profilera/optimera den generella
CPU/Voodoo-hotpathen med korrekt callbackkadens.
