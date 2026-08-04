# Gauntlet Dark Legacy grafikcheckpoint — 2026-08-04

## Resultat

Den native EutherDrive-kärnan visar nu en sammanhängande, rörlig spelscen med
spelare, fiender, tunnor, däck, ljus och HUD. De stora vita/gula polygonerna
som täckte skärmen är borta. Körvägen använder inte MAME som backend.

Tre separata fel orsakade huvuddelen av korruptionen:

1. Den vanliga Voodoo-command-FIFO:n kunde hoppa förbi en giltig header medan
   resten av paketet fortfarande skrevs. Tre äldre producentheuristiker gjorde
   dessutom packetordningen sämre. FIFO:n väntar nu på kompletta paket, töms
   ordnat vid rendergränsen och kan återhämta sig från en verkligt ogiltig
   läsposition. De tre heuristikerna är avstängda i Gauntlet-baslinjen.
2. Texturernas alpha-värden beräknades inte genom Voodoo 2:s `fbzColorPath`
   och `alphaMode`. Genomskinlig geometri ritades därför som ogenomskinliga
   vita ytor. Rasterizern har nu alpha-test, RGB-blendfaktorer och alpha-plane-
   skrivning för den setup-baserade texturväg som Gauntlet använder.
3. Type 3-paketens riktiga per-vertex-alpha kastades bort och iterated alpha
   var hårdkodad till 255. Spåret visar verkliga värden från 102 till 255 i
   samma scen. Rasterizern bär nu packetets RGBA per vertex, interpolerar alla
   fyra kanaler med Voodoo-setupens 12-bitars färgprecision och använder
   resultatet i `fbzColorPath`, fog, alpha-test och blend. Det riktar sig mot
   de ryckvisa blå/röda helskärmsytorna och den stora rosa triangeln.

En fjärde, fristående Voodoo 2-avvikelse hittades därefter i bufferrotationen.
Gauntlet skriver först `swapbufferCMD` genom den vanliga registeraperturen och
skickar sedan samma operation genom command-FIFO:n. När command-FIFO är aktiv
ska Voodoo 2 ignorera direkta skrivningar till FIFO-bara register; kärnan körde
tidigare båda och roterade därför front/back två gånger. PCI-enheten känner nu
av effektiv command-FIFO-state och släpper endast igenom `NOFIFO`-registren på
den direkta vägen. Den gamla presented-buffer-bevaringen i desktopstartaren
behövs därmed inte längre.

Checkpointet innehåller även MIPS `movz/movn` för single/double, korrekt
Y-origin när uppskjutna fast-fill-clear materialiseras samt gameplay-state
`0x400f` i det befintliga diagnostic-textskyddet.

## Mätning

Före FIFO-rättningen gav frame 5383 cirka 4 298 packet-resyncs, 2 708
texturerade trianglar och bara 30 503 icke-svarta pixlar i aktiv buffer.

Efter rätt FIFO-ordning gav samma punkt 21 resyncs, 5 641 texturerade
trianglar och 139 761 icke-svarta pixlar. Scenen blev sammanhängande, men
alpha-geometrin syntes fortfarande som stora vita polygoner.

Efter alpha-rättningen gav frame 5383:

```text
frameHash=0x25fcc1fc
probe rate=45.48 fps
active-buffer nonzero pixels=133272
display non-black pixels=233592
```

Ett självständigt reloadprov utan MAME-import fortsatte i ytterligare 300
frames till frame 5683:

```text
frameHash=0xb9bc5687
probe rate=45.14 fps
packet resyncs=12
```

Den riktiga desktopappen mättes också, maximalt fönster och OpenGL-renderer.
Telemetrin visade 52,6 fps video och cirka 45,4/60,0 fps guest-headroom.

## Starta och prova

Bygg Release om koden har ändrats:

```sh
dotnet build EutherDrive.UI/EutherDrive.UI.csproj -c Release --no-restore
```

Starta sedan via den native warm-startaren:

```sh
scripts/run-gauntdl-desktop-warm.sh
```

Startaren använder nu som standard den tidigaste sammanhängande native-
gameplaybilden direkt vid banningången:

```text
.build-tmp/euther-journey-native-f4808.warm.gz
SHA-256 1e0312b5f4d2600688095c7902e38dcee6b75b6ea1498de5e7338b73f5f0b1df
```

Snapshoten är självbärande. MAME-dumpar behövs inte när den körs. Den tidigare
f5383-standarden låg cirka 575 emulerade bilder senare och hade skapats efter
en lång automatiserad Up-sekvens. Den var därför en felaktig användarstart,
även om den var användbar som grafikdiagnostik.

Ett 40-frame reloadprov från f4808 fortsatte utan halt i main state `0x400f`
och behöll samma presenterade banningång. Ett riktigt Avalonia/OpenGL-prov
visade rätt startposition men också kvarvarande mörk/röd felgeometri i däcket.
Inga inputscript körs av desktopstartaren efter reload; all fortsatt rörelse
kommer från användaren.

Efter swap-rättningen ger ett 80-frameprov från f4808 exakt två riktiga FIFO-
swaps. De två färgbuffertarna alternerar 0/1 och innehåller båda kompletta,
igenkännbara närliggande gameplaybilder; den direkta skrivningen syns inte
längre som exekverad swap. Frame 4888 gav `frameHash=0xff0b80d9`.

Ett längre kontrollprov fortsatte 2 400 emulerade frames. Det visade att den
senare svartbilden också går att reproducera i proben och alltså inte är ett
Avalonia/OpenGL-fel. Bilden är fortfarande synlig vid frame 6900, men allt fler
Type 3-trianglar avvisas utanför cliprektangeln och en senare värld-/render-
övergång lämnar synlig front tom. Detta är separat från den nu rättade dubbla
swappen.

## Kvarvarande grafikfel

Svarta/ofyllda ytor förekommer fortfarande i senare redraw-pass. Scenen är
spelbar nog för input- och rörelseprov, men inte grafikfärdig. Nästa
grafikarbete ska spåra de Type 3-paket som övergår från täckta till helt
clip-avvisade trianglar mellan de synliga frame 6900-data och den senare tomma
fronten. Snapshot/reload mitt i detta intervall behåller en äldre frame och
ändrar FIFO-fortsättningen, så transient command-FIFO-state måste tas med i
spåret. Alpha ska inte kringgås och gamla frame-generationer ska inte
kompositeras för att maskera felet.

MAME-importfunktionerna i `GauntletProbe` är endast ett utvecklingsfacit för
RAM, FBI/TMU-register, framebuffer och FIFO-position. Den sparade native
snapshoten och desktopstartaren är den normala körvägen.
