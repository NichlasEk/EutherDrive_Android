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

1. Ersätt kedjan av steady-state `TryFastPath...`-anrop per instruktion med en
   PC-dispatchtabell/switch. Varje befintlig accelerator ska fortfarande
   signaturkontrollera gästkoden. Verifiera exakt slut-PC, hash och Voodoo-
   räknare före och efter.
2. Mät minst tre identiska 1 200 000-stegsprov och använd medianen. Första
   delmålet är CPU-fas under 700 ms utan state-drift; därefter under 500 ms.
3. Profilera de återstående 4 139 FIFO-dekoderanropen och batcha endast
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
