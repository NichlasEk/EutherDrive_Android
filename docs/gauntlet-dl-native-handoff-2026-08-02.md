# Gauntlet Dark Legacy native PC handoff — 2026-08-02

## Mål och avgränsning

Målet är ett spelbart Gauntlet Dark Legacy på PC genom EutherDrives egen
Vegas/R5000/Voodoo 2-kärna. MAME är endast tids-, register- och bildfacit. Den
får inte bli den permanenta körvägen eller döljas bakom EutherDrive-gränssnittet.

`scripts/run-gauntdl-desktop.sh` är därför bara en referens-/fallbackstartare.
Den native startpunkt som ska utvecklas vidare är
`scripts/run-gauntdl-desktop-warm.sh`.

## Pushed bas före detta checkpoint

- `80727207 Reduce Gauntlet Voodoo raster overhead`
- `f40f18db Cache Gauntlet TMU triangle state`
- `b69a5a44 Speed up Gauntlet texture raster loops`

De tre ändringarna behåller synkroniserad output men minskar FIFO- och
rasterkostnaden. Den viktiga varma staten är:

```text
.build-tmp/euther-mame-phase1-fullgpu-f4783.warm.gz
```

CPU/FPU/CP0-facit för samma relativa MAME-punkt är:

```text
artifacts/gauntlet-probe/gauntdl-mame-phase1-rel207-cpu.state
```

Warm-filen är cirka 7,5 MiB och självbärande: framebuffer, aux och båda
TMU-bankerna finns i snapshoten. Råa MAME-dumpar behövs inte vid native start.

## Verifierat nuläge

- Riktig gästkod körs från synkroniserad PC `0xffffffff800158b8`.
- Snapshoten innehåller den riktiga phase-1-världen och 552 world-objekt.
- Coin, start och normaliserad spelarinput når gästen.
- FIFO producerar riktiga Type 3-paket, texturerade trianglar, fast-fill och
  swap-kommandon.
- En 1 200 000-stegs synkkörning är deterministisk med slut-PC
  `0xffffffff800c4fa4`, frame-hash `0x5e902479` och samma Voodoo-räknare efter
  den lilla instruction-fetch-optimeringen i detta checkpoint.
- En aktuell 25 x 60 000-körning gav `frameHash=0x93cca255`, 1 086
  texturerade trianglar, fem täckta trianglar och 117 rasterpixlar. Det är en
  koherent reproducerbar diagnostikpunkt, inte ännu en korrekt spelbild.

Det synliga problemet är inte längre loader, coin eller avsaknad av en varm
värld. Euther-kärnan gör för lite korrekt guest-/FIFO-/rasterarbete per
väggklocksekund och en stor del av de producerade trianglarna avvisas som
tomma. Därför kan en fin importerad bild stå still eller ersättas av en
ofullständig redraw.

## Prestandagräns

Bounded synkprofil, 1 200 000 CPU-steg:

```text
callbacksMs=37.44
cpuMs=1101.05
devicesMs=0.56
renderMs=14.21
fifoDecodeMs=207.20
fifoDecodeCalls=4139
```

CPU-fasen inkluderar synkron FIFO/raster. Cirka 207 ms ligger i FIFO-dekodning;
resten ligger huvudsakligen i R5000-interpretern och dess steady-state-dispatch.
En MAME-bilds guest-arbete tar alltså omkring 1,1 sekunder, vilket förklarar
varför bilden upplevs som stillastående även när UI:t ritar oftare.

Detta checkpoint använder en direkt main-RAM-läsning för runtime-state och
steady-state instruction fetch. Resultatet är exakt; vinsten är liten och ska
inte beskrivas som lösningen på genomströmningsproblemet.

## Fortsättning: PC-dispatch för steady-state

Den linjära kedjan med aktiva steady-state-acceleratorer är ersatt av två
PC-switchar på varsin sida om phase-5-QIO-servicen. Acceleratorernas befintliga
gästkodssignaturer och inbördes ordning är bevarade. Den vanliga avvisningsvägen
för paired-word-copy och phase-5-QIO testar nu billiga PC-/contextvillkor innan
den läser eller klassificerar gäststate.

Tre identiska 1 200 000-stegsprov före ändringen gav CPU-fas:

```text
1202.83 ms
1164.64 ms
 956.39 ms
median 1164.64 ms
```

Tre identiska prov efter den behållna ändringen gav:

```text
1000.09 ms
1012.72 ms
 937.22 ms
median 1000.09 ms
```

Medianvinsten är cirka 14,1 procent. Ett separat slutligt kontrollprov gav
1022,95 ms. Alla behållna prov slutade bit-/state-exakt på:

```text
pc=0xffffffff800c4fa4
frameHash=0x5e902479
fifoDecodeCalls=4139
rast=438034/117/0/823/5/818/2/0/816/117
```

Ett försök att lägga de vilande BGLoadModel-prolog/epilog- och
`strncmp`-acceleratorerna i samma switch avvisades. Hashen råkade vara samma,
men slut-PC, FIFO-anrop och rasterräknare drev. De acceleratorerna är därför
inte aktiverade i steady-state-checkpointet. Nästa CPU-steg ska optimera den
ordinarie R5000-dispatchen eller en uppmätt rutin utan att ändra gästernas
instruktions-/interrupt-kadens; målet under 700 ms är ännu inte nått.

## Swap-slutsats

Vid renderer-frame 4777 ses två legitima omedelbara `swapbufferCMD=0`:

1. direct write vid guest-PC `0xffffffff80102a80`, front 1/back 0 till front
   0/back 1;
2. FIFO Type 1 `0x00010251` vid guest-PC `0xffffffff80102ab4`, tillbaka till
   front 1/back 0.

MAME gör `update_partial(vpos)` före varje rotation. Två swaps på samma
scanline ger i praktiken ingen ny hel presenterad yta. Ingen av kommandona får
undertryckas och ingen PC-specialregel ska läggas till. Full PCI-FIFO/busy- och
partial-update-semantik behövs senare, men dubbel-swappen förklarar inte den
nuvarande låga guest-takten.

## 2026-08-02 kväll: W-depth och spelbar desktopbild

Type 3-paketen bär `Wb` per vertex. Rasterizern använde tidigare ett gammalt
register-W och skalade både setup-W och fog-W som 32.32, trots att Voodoo-
vägen nedströms läser dem som 16.48. Med packetets `FogW` som Wb och korrekt
48-bitars fractional scale ökade samma synkbild från 117 till 144 477
texturerade pixlar och från 5 till 462 täckta trianglar.

En andra vitkorruption kom från den tidiga diagnostikfallbacken: en helt
depth-avvisad texturtriangel föll igenom till solid vit raster och wireframe.
Texturerade primitives som inte skriver färg avslutas nu korrekt utan en
sådan fallback. Den levande buffer 0 gick därmed från tiotusentals vita pixlar
till en ren `SELECT A JOURNEY`/`SKY EASY`-yta.

Den varma gästkontexten måste behållas i desktopappen. CPU-oraklet vid
relative-207 är endast för synkroniserade regressionstest; om det laddas som
desktop-PC stannar den normaliserade inputkedjan. Med warm-stateens egen CPU
och den riktiga inputpollen når Fight omedelbart runtime-recordet och gästens
normaliserade held-word. Desktopstartaren aktiverar därför native input poll
och visar coherent buffer 1 med icke-tom native redraw från buffer 0. Efter en
Fight/Right/Fight-sekvens försvinner journey-texten och scenen går vidare;
fortsatt Up/Fight/Magic producerar en ny world-frame vid cirka 30--35 probe-
frames/s.

## Avvisade vägar

- MAME som dold permanent backend: spelbart, men inte produkten vi bygger.
- Gamla frontbufferbilder med liveöverlägg: ser rörligt ut men blandar två
  olika frame-generationer och är inte en koherent bild.
- Att undertrycka en av de två swappsen: hårdvarumässigt fel.
- PC-specifika swapregler: hårdvarumässigt fel och skört.
- En intern `strncmp`-loop-fastpath provades under denna session. Bild- och
  Voodoo-resultat var samma, men slut-PC försköts sju instruktioner efter 25
  frames. Ändringen avvisades och finns inte i checkpointet.

## Exakt reproduktion

Release-build:

```sh
dotnet build tools/GauntletProbe/GauntletProbe.csproj -c Release --no-restore -m:1 /clp:ErrorsOnly
```

En synkroniserad 1 200 000-stegsbild utan växande logg eller nytt snapshot:

```sh
env \
  EUTHERDRIVE_GAUNTDL_WARMUP_STATE=.build-tmp/euther-mame-phase1-fullgpu-f4783.warm.gz \
  EUTHERDRIVE_GAUNTDL_WARMUP_FRAMES=4783 \
  EUTHERDRIVE_GAUNTDL_LOAD_WARMUP_IGNORE_CPU_STEPS=1 \
  EUTHERDRIVE_GAUNTDL_LOAD_MAME_CPU_STATE=artifacts/gauntlet-probe/gauntdl-mame-phase1-rel207-cpu.state \
  EUTHERDRIVE_GAUNTDL_FIX_RUNTIME_GAME_TASK=0 \
  EUTHERDRIVE_GAUNTDL_SUMMARY=1 \
  EUTHERDRIVE_GAUNTDL_PROFILE_FRAME_PHASES=1 \
  EUTHERDRIVE_GAUNTDL_EXTRA_SERIES= \
  EUTHERDRIVE_GAUNTDL_CPU_STEPS_PER_FRAME=1200000 \
  tools/GauntletProbe/run-gauntdl-baseline.sh \
  /home/nichlas/roms/MAME/Midway/Vegas/gauntd 4784
```

Native desktopstart:

```sh
scripts/run-gauntdl-desktop-warm.sh
```

Körningarna ovan skriver inga loggar eller snapshots i `/tmp`. Behåll även
framtida profiler bounded och stdout-baserade. Repo-lokala `.build-tmp` var
cirka 463 MiB vid pausen; skapa inte en ny warm-state för varje prov.

## Nästa arbetsordning

1. Fortsätt CPU-profileringen från den verifierade PC-dispatchen. Optimera
   ordinarie R5000-dispatch eller en uppmätt rutin utan att aktivera vilande
   fastpaths som ändrar instruktions-/interrupt-kadensen.
2. Mät minst tre identiska 1 200 000-stegsprov och använd medianen. Första
   delmålet är fortfarande CPU-fas under 700 ms utan state-drift; därefter
   under 500 ms.
3. Profilera därefter de återstående 4 139 FIFO-dekoderanropen och batcha endast
   kompletta packetgenerationer. Packetordning och 1 200 000-stegsresultat
   måste förbli exakta.
4. När guest-takten är tydligt förbättrad: jämför Type 3-pass från fast-fill
   till swap mot MAME-oraklet och hitta varför 818 av 823 trianglar i
   enbildsprovet blir tomma/avvisade.
5. Implementera därefter generell Voodoo PCI-FIFO/busy och scanline-partial
   presentation. Ingen gammal-frame-komposit och ingen PC-specialregel.
6. Slutprov: coin, start, riktning, fight och magic ska ge kontinuerligt
   förändrad koherent native bild i desktopappen. Först då tas MAME-fallbacken
   bort från den normala användarvägen.

## Worktree vid paus

Följande fanns redan som otrackat och tillhör inte checkpointet:

```text
.build-tmp/
console_history
diff/
snap/
tools/GauntletProbe/mame-gauntdl-mainram.lua
```

Staga eller radera dem inte av misstag.

## 2026-08-02 natt: koherent MAME-RAM och native portalutgång

Den äldre phase-1-checkpointen blandade MAME:s övre RAM med EutherDrives
gamla schedulerstack. En ny lokal checkpoint innehåller hela 32 MiB main RAM,
matchande R5000-register samt befintligt Voodoo/TMU-tillstånd:

```text
.build-tmp/euther-mame-phase1-coherent-fullgpu-f4783.warm.gz
```

Lång `Up` från den checkpointen passerar den riktiga SKY-portalen. Vid f5583
väntar gästen i audio-init-hjälparen `0x800457f8`; dess signaturvaktade
count-delay-accelerator anropades tidigare aldrig eftersom state `0x400c`
gick direkt från steady-state-dispatchen till instruktionstolkningen. Samma
accelerator körs nu också i den dispatchvägen. Det verifierade anropet hade
`s0=1`, `v0=0x02faf080`, `v1=0x0008eec8`, flagga 1 och full kodsignatur.

Efter fixen går gästen omedelbart `0x400c -> 0x400e`, startar sin ordinarie
asset-QIO och ritar en ny native world-yta. Första 10-frame-provet gav två nya
swaps, 717 texturerade trianglar, 708 täckta trianglar och 546 712 pixlar.
Den lokala fortsättningspunkten är:

```text
.build-tmp/coherent-world-f5593.warm.gz
frameHash=0x5f91e7bc
```

Den fortsatta levelK2-laddningen når f5793 med 3 836 nya täckta Type-3-
trianglar och 138 096 rasterpixlar. Den presenterade fronten är ännu den gamla
SKY/world-ytan medan buffer 0 byggs vidare; fortsätt därifrån tills nästa swap
innan coin/start och kontrollprov räknas som spelbarhetsbevis.

Desktop-preseten fortsätter med diagnostic-render-suppression och når f5821
med Player 1 redan aktiv (`0x80227af4=1`). Från den checkpointen når både
frontendrecordet och gästens normaliserade held-word rätt separata värden:

```text
Fight  0x80262b90=0x00000400  0x80227ba8=0x00000400
Magic  0x80262b90=0x00000200  0x80227ba8=0x00000200
```

Ett kombinerat Up/Fight-prov till f5851 gav composite-hash `0x91132263`,
medan den byte-exakta no-input-kontrollen från samma f5821-checkpoint gav
`0xc6b43182`. Inputen påverkar alltså den levande guest-/renderkedjan kausalt.
Desktopstartaren använder nu `.build-tmp/coherent-desktop-f5821.warm.gz`.
Kvarvarande blockerare för visuellt spelbar status är glyph-/diagnostikblocket
i live-buffer 0 samt korrekt presentation/skaling av gästens 512x384-yta.

## 2026-08-03: SKY-spärren och callbackkostnaden är borta

MAME-oraklet visar att levelK2-övergången håller den aktiva audioräknaren
`0x80227c80` på noll. Native-spåret från den rena f5583-staten visade i stället
den exakta avvikelsen:

```text
pc=0x80045884  0x80227c80: 1 -> 0
pc=0x80045b90  0x80227c80: 0 -> -1
```

Det andra anropet är en försenad DCS-completion efter att gästen redan har
nollställt räknaren. En ny signaturvaktad baseline-fix saturerar endast denna
bevisade `0 -> -1`-skrivning. Samma f5583 -> f5593-prov lämnar därefter
räknaren på noll och gästens egen kod går `0x400c -> 0x400e`.

Den kompletta loadern når `loader=-1`, `cd90=1` och negativ `cd84`. Med den
korrigerade audioräknaren returnerar `0x80051b0c` ett och den ordinarie
state-writern `0x800520ec` gör `0x400e -> 0x400d`, samma väg som MAME. Det
rekonstruerade kontrollcheckpointet är lokalt:

```text
.build-tmp/coherent-gameplay-reconstructed-f6284.warm.gz
state=0x400d  audioActive=0  activePlayers=1
```

Den första gameplayprofilen hittade dessutom att coin-callback-bryggan
återstartade samma icke-returnerande 100 000-stegs gästfunktion 17 gånger per
frame. Callbackfasen kostade cirka 26,2 sekunder. Bryggan avbryter nu efter
första icke-returnerande callback och undertrycker nya försök så länge samma
main-state består. Ett tiobildsprov i `0x400d` gav därefter cirka 0,95 sekunder
CPU per probe-frame; efter den första suppressionsbilden låg callbackfasen på
20--35 ms i stället för 26 sekunder.

Den nya lokala gameplay-checkpointen är:

```text
.build-tmp/coherent-gameplay-fast-f6297.warm.gz
state=0x400d  heap=0x00474650  activePlayers=1
```

State- och loadergränsen är alltså passerad, men spelbarhetsbeviset är ännu
inte komplett: buffer 0 fast-fillas svart och de efterföljande Type 3-paketen
producerar ingen sammanhängande gameplay-yta. Nästa kausala gräns är därför
Type 3-passens draw-buffer/rasterresultat efter fast-fill i state `0x400d`,
inte portalinput, loader eller fler state-patchar.

## 2026-08-02 sen kväll: ren native loaderkedja når gameplayvärlden

Det rekonstruerade f6284-spåret ovan visade sig ha ytterligare importerad
RAM-korruption och ska inte längre användas som spelbarhetsbevis. Ett färskt
MAME-orakel vid journey rel1000 verifierade i stället följande invariants:

```text
0x8019ccb0 = 0xffffffff       verklig loader klar
0x80229344 = 200.0f          Player 1 X
0x80229348 = 150.0f          Player 1 Y
0x8022934c = 600.0f          Player 1 Z
0x802280fc = 0x00474650      gameplay heap
```

Den rena audiofixade native-kedjan skrev sönder koordinaterna i state
`0x400e` via fyra anrop till bounds-clampen vid `0x8001ac78`, `0x8001acc4`,
`0x8001ad10` och `0x8001ad5c`. Level-loadern hade då redan återanvänt den
gamla bounds-tabellen vid `0x804c5640`; bland annat blev X-värdet NaN. MAME
gör inga motsvarande koordinatskrivningar under `0x400e`. Baseline-fixen
hoppar därför endast över dessa fyra sex-instruktionssignaturer, endast för
de fyra exakta player-recorden och endast medan loader-state är `0x400e`.

Samma rena körning hittade därefter en för tidig `0x400e -> 0x400d` vid
`0x800520ec`, medan den verkliga loaderflaggan `0x8019ccb0` ännu inte var
`0xffffffff`. En andra signaturvaktad fix skjuter upp just transition-
funktionen tills flaggan är klar. Inga state-, koordinat- eller RAM-patchar
behövs därefter. Den verifierade kedjan är:

```text
.build-tmp/coherent-genuine-transitionfix3-f6253.warm.gz
state=0x400e  loader=0xffffffff  xyz=200/150/600

.build-tmp/coherent-genuine-gameplay2-f6263.warm.gz
state=0x400d  heap=0x00474650   xyz=200/150/600
```

Efter transitionen renderar den permanenta R5000/Voodoo-vägen en faktisk
SKY-värld med broar, skepp och komplett HUD. Vid f6315 innehöll den
presenterade 640x480-ytan 222 397 icke-svarta pixlar och Voodoo hade rasterat
1 245 085 gameplaypixlar i 3 038 texturerade trianglar sedan checkpointen.
Detta är inte ännu ett spelbarhetsbevis: swapräknaren ligger kvar på 4363,
bilden står still och höger/coin/start ändrar ännu varken den presenterade
bilden eller Player 1-koordinaterna. Nästa exakta gräns är varför den första
`0x400d`-renderpassagen fortsätter producera Type 3-paket men aldrig når
`grBufferSwap`; forcera inte en framebuffer-sammansättning runt den.
