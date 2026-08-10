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

## 2026-08-05: levelK2-residensen återställd från originaldisken

Det återstående stora grafikfelet var inte trasig rastergeometri. Gameplay-
checkpointen hade ärvt phase-1-innehåll i TMU-bankerna, medan levelK2:s riktiga
texturström inte längre fanns kvar i huvud-RAM efter den naturliga laddningen.
Exakta samples från felpolygonerna matchade i stället andra riktiga delar av
`gauntd24.raw`.

En disk-till-TMU-korrelation hittade levelK2:s sammanhängande TMU1-residens
`0x0b0bb8..0x14a908`, uppdelad i åtta tvåbytesjusterade uppladdningschunkar.
Den hittade även båtdäcket i separata TMU0-chunkar och bakgrunden som en enda
stor TMU0-körning `0x2056b8..0x2e04a8`. Att återställa dessa ägda ranges från
originaldisken tog bort de stora färgblocken, svarta kilarna, rutmönstret på
båten och den randiga horisonten.

Frame 6750 ger efter korrigeringen:

```text
frameHash=0xd1fdaf35
probe rate=46.69 fps
MAME används endast som jämförelsefacit
```

Den korrigerade staten är självbärande och reload-stabil utan overlays:

```text
.build-tmp/gaunt-k2-clean2-f6750.warm.gz
SHA-256 312ef133ae70d40e2c437772ea79f96884d0eee687daabd4776ce9c166494df2
```

Desktopstartaren använder nu denna state som standard. Bilden skiljer mindre
än en procent av pixlarna från samma CPU/FIFO/framebuffer-state med hela
referensbanken. Nästa grafikpass ska begränsas till den lilla restskillnaden;
prestandaarbete väntar tills ett UI-prov bekräftar samma rena bild över tid.

Ett senare UI-prov motsvarande ungefär frame 9000 exponerade ytterligare en
phase-1-rest i TMU0 `0x10bc40..0x11ac28`. Den andra checkpointen ovan innehåller
även denna diskägda körning. Ett 2 250-frame reloadprov nådde frame 9000 med
sammanhängande vatten, båt och bakgrund utan den tidigare randiga remsan.

## 2026-08-05: verklig videohastighet och DCS-status

Ett användarprov bekräftade att den korrigerade levelK2-bilden är grafiskt
sammanhängande, men också att rörelsen går som sirap. UI-värdet kring 40-46 fps
räknade kärnans `RunFrame`-anrop och inte nya bilder från gästens Voodoo.

Ett deterministiskt reloadprov från f6750 visar skillnaden tydligt. På 300
kärnanrop, med 60 000 MIPS-steg per anrop, ökade Voodoo-swapräknaren bara från
3836 till 3844:

```text
host/core calls        300
probe call capacity    40.25 fps
new Voodoo frames      8
effective guest video  cirka 1.1 fps
frameHash              0x78a8ec1a
```

Desktoptelemetrin räknar nu Gauntlets riktiga `swapbufferCMD`-resultat som
emulerade videobilder. Det tidigare värdet finns fortfarande indirekt som UI-
presentationsfrekvens, men visas inte längre som om spelet faktiskt producerade
40 nya bilder per sekund.

Ljudtexten var också för optimistisk. DCS-enheten returnerar för närvarande en
44,1 kHz stereobuffer, men den innehåller bara nollor. Warm-staten visar ADSP
vid idle-PC `0x0079`, `imask=0`, med IRQ2 väntande och utan överförda DCS-ord.
Deck monitor visar därför `audio silent (DCS pending)` tills kärnan verkligen
producerar en icke-noll PCM-signal.

En handskriven snabbväg för den heta 4x3-floatkopian vid `0x800c9c90` provades
och förkastades. Den gav ungefär tio procent högre anropskapacitet men
divergerade efter längre körning i CPU-PC, FIFO-state och framehash. Den ingår
inte i startaren. Nästa prestandasteg behöver vara större verifierade
basic-block/JIT-regioner med hela CPU/FIFO/framebuffer-oraklet, eftersom den
nuvarande tolken behöver omkring 2,25 miljoner MIPS-instruktioner per verklig
Voodoo-bild i den här scenen.

## 2026-08-05: första bitexakta Voodoo-prestandapasset

Den rena K2-rastervägen profilerades över den första riktiga Voodoo-swappen.
FIFO-avkodning och rasterisering stod för ungefär två tredjedelar av CPU-tiden.
Följande ändringar är därför aktiva i desktopstartaren:

- stora texturerade trianglar delas per scanline mellan värdens kärnor;
- varje scanline begränsas konservativt till triangelns verkliga x-intervall;
- bilinjär sampling publicerar diagnostikstatus en gång per TMU/pixel i stället
  för efter varje tap;
- framebufferkonvertering görs bara när Voodoo faktiskt har bytt frontbuffer;
- `scripts/run-gauntdl-probe-warm.sh` kör samma profil som desktopstartaren för
  repeterbara A/B-prov.

Två 300-anropsprov från f6750 till f7050 gav 6,16 respektive 5,83 sekunder,
jämfört med cirka 8,05 sekunder före passet. Båda behöll exakt:

```text
frameHash  0x78a8ec1a
PC         0xFFFFFFFF800C9C8C
swap       3844
FIFO       25455490 / 2464990
raster     2871022 pixlar
```

Det motsvarar cirka 24–28 procent högre total kapacitet och ungefär 1,3–1,4
verkliga gästbilder/s i den här scenen. Det är en tydlig förbättring men ännu
inte spelbart. En generell rak basic-block-prototyp provades också: den körde
935 803 instruktioner i block men missade en gästservicepunkt, tappade nästa
swap och divergerade i PC/FIFO. Den prototypen är helt borttagen. Nästa CPU-pass
måste därför göra service-/interruptgränser explicita innan block exekveras.

## 2026-08-10: säkra branch/delay-slot-par

Den befintliga raka instruktionsbatchningen stannade före varje MIPS-branch.
Branchen och dess obligatoriska delay slot gick därför genom två fulla
`Step()`-varv. Kärnan kan nu köra vanliga villkorsbrancher (`beq`, `bne`,
`blez`, `bgtz`) tillsammans med en verifierat sekventiell delay slot. Den
kontrollerar båda PC-adressernas servicebehov och återgår till ordinarie väg
vid trace/profilering eller ovanliga instruktioner. Branchmålet servas alltid
först i nästa steg; inga hela basic blocks kan passera en servicepunkt.

Tre interfolierade 300-anropsprov per läge gav median `runMs` 3 932 ms utan
paren och 3 700 ms med dem, cirka 5,9 procent snabbare. Det långa oraklet
behöll exakt:

```text
frameHash   0x78a8ec1a
PC          0xffffffff800c9c8c
FIFO        25455490 / 2464990
drawPackets 434522
swaps       3844
```

Detta är fortfarande långt från spelbar gästvideo; nästa CPU-pass bör utöka
samma servicegränssäkra modell till fler branchklasser eller ett riktigt
block/JIT-lager, inte återinföra den tidigare obegränsade prototypen.
