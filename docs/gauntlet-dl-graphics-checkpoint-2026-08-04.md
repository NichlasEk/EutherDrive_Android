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

Den raka instruktionsbatchen kan nu även absorbera sitt avslutande säkra
branch/delay-slot-par. Branchmålet körs fortfarande först i nästa `Step()`, men
den separata värd-dispatchen mellan blockkropp och branch försvinner. Oraklet
räknade 720 021 sådana sammanslagningar över 300 anrop. Tre interfolierade
körningar per läge gav median `runMs` 4 002 ms utan sammanslagningen och
3 873 ms med den, cirka 3,2 procent ytterligare förbättring. Slut-PC, hash,
FIFO-, draw- och swapräknare var fortsatt identiska.

## 2026-08-10: desktop använder det nya CPU-headroomet

Efter branchoptimeringarna klarar proben mer än 60 kärnanrop/s. Desktopprofilen
behövde därför inte längre stanna vid 60 000 MIPS-steg per 60-Hz-anrop. Ett
300-anropsprov jämförde flera budgetar från samma f6750-state:

```text
steg/anrop  kapacitet       nya swaps  slut-hash
60000       76,7 anrop/s     8          0x78a8ec1a
72000       73,7–74,8        8          0x78a8ec1a
84000       64,9–67,1       10          0xe9d4e439
90000       62,8            11          0x40bd6aae
```

Två 84 000-körningar gav exakt samma PC, hash, FIFO-, draw-, framebuffer- och
swapräknare. Desktopstartaren använder därför 84 000 som försiktig standard:
cirka 25 procent fler riktiga Voodoo-bilder än 60 000-läget, samtidigt som
proben behåller marginal över 60 anrop/s. Det fjärde skriptargumentet kan
fortfarande användas för att välja en annan budget. Det exakta 60 000-stegs-
oraklet i probe-startaren är oförändrat.

## 2026-08-10: servicegränssäkert block/JIT-lager

Den raka runtime-batchcachen har nu ett valfritt kompilerat lager byggt med
.NET expression trees. Det kompilerar endast heta sekventiella MIPS-block som
redan har godkänts av den servicegränssäkra batchningen. Brancher, delay slots,
runtime-service-PC:n, trace/profilering och okända instruktioner ligger kvar i
den verifierade kärnvägen. Generella 32-bitars stores kompileras inte eftersom
de fortfarande kan bära Gauntlets FIFO- och enhetshooks.

För att undvika kostnaden från många små delegates krävs som standard minst 16
kompilerbara instruktioner och 512 träffar innan ett block kompileras. Desktop-
och warm-probe-profilerna aktiverar lagret. Trösklarna kan överstyras med:

```text
EUTHERDRIVE_GAUNTDL_RUNTIME_COMPILED_BLOCK_THRESHOLD
EUTHERDRIVE_GAUNTDL_RUNTIME_COMPILED_BLOCK_MIN_INSTRUCTIONS
```

Ett 1 200-anropsprov från f6750 till f7950 kompilerade sju block och körde
555 355 gästinstruktioner genom dem. Två närliggande A/B-par gav cirka 3,7
respektive 7,3 procent lägre `runMs`; wall-clock varierar dock med värdlast, så
detta ska behandlas som en försiktig första vinst snarare än slutlig dynarec-
prestanda. Det längre oraklet behöll exakt:

```text
frameHash   0x8a71c24a
PC          0xffffffff800c6c34
FIFO        26453151 / 2585612
drawPackets 459391
swaps       3864
```

Proben skriver `compiledBlocks=count/runs/instructions` när lagret är aktivt.
Nästa JIT-steg bör vara servicegränssäkra traces över interna brancher; kortare
raka block gav en tydlig nettoförlust och ska inte återaktiveras.

## 2026-08-10: COP1/COP1X i blockkompilatorn

JIT-backenden kompilerar nu de raka blockens vanligaste bitexakta single-
precision-operationer: `ADD.S`, `SUB.S`, `MUL.S`, `DIV.S`, `ABS.S`, `MOV.S`,
`NEG.S` samt COP1X `MADD.S`, `MSUB.S`, `NMADD.S` och `NMSUB.S`. Varje operation
byggs direkt i expression-trädet med samma `float`- och bitkonvertering som
tolken; brancher och okända COP1-funktioner avslutar fortfarande blocket.

En 16-instruktionsgräns skapade 19 delegates och blev långsammare. Minsta
blocklängd höjdes därför till 20. Det gav tio heta block och körde 438 891
instruktioner genom JIT-lagret över 1 200 anrop. Två interfolierade A/B-par gav:

```text
tolk/batch   9355,4 ms   9391,5 ms
COP1-JIT     8672,1 ms   8531,2 ms
vinst           7,3 %       9,2 %
```

Båda JIT-körningarna behöll `frameHash=0x8a71c24a`, slut-PC
`0xffffffff800c6c34`, FIFO `26453151/2585612`, 459 391 draw-paket och 3 864
swaps. Nästa lager kan nu fokusera på stabila branchöverskridande traces i
stället för fler små raka delegates.

## 2026-08-10: profilerad gräns för nästa trace-lager

En tillfällig blockerprofil mätte vilka instruktioner som först stoppar JIT-
prefixen. Den hetaste missade kedjan vid `0x800a54a8` hade 10 596 ingångar,
men en minnesskrivning efter tre instruktioner avslutade prefixet före en
efterföljande `MTC1`. Flera 25-op-flyttalsblock nådde 18 instruktioner före
samma typ av övergång. Andra heta kedjor stoppades direkt av stack-/objekt-
stores eller COP1-jämförelser.

Tre avgränsade försök förkastades efter bitexakta prov:

- terminal `beq/bne/blez/bgtz` plus delay slot fick noll JIT-träffar, även när
  COP1-registerflyttar gjorde blocken längre;
- generellt hook-säkert `sw` ökade täckningen med endast 58 instruktioner över
  1 200 anrop;
- `MFC1/DMFC1/MTC1/DMTC1` ökade täckningen till cirka 1,5 miljoner
  instruktioner vid minlängd 24, men samma-gräns-A/B förbättrades bara omkring
  0,3–0,5 procent och bedömdes vara inom wall-clock-bruset.

Inget av försöken ingår i standardprofilen. Nästa trace-backend måste kunna
korsa verifierade main-RAM-stores med ett billigt runtime-guard och hantera den
faktiska terminala branchklassen. Fler isolerade opcodeutökningar ger mycket
täckning men amorterar inte delegate- och blockgränskostnaden.

## 2026-08-11: första guarded store/JAL-tracen

Den hetaste tidigare blockerade kedjan vid `0x800a54a8` är nu en riktig
guarded trace. Expression-delegaten täcker den exakta 20-op-prologen, inklusive
stackstores, två `MTC1`, global räknaruppdatering och terminal `jal` med delay
slot. Före körning krävs att hela nya stackramen och globalräknaren ligger i
main RAM. Signaturens sista kroppsinstruktion, jump och delay slot valideras vid
varje ingång; vid minsta avvikelse används tolken utan partiellt exekverad
state. JAL-målet körs först i nästa `Step()`.

Över 1 200 anrop körde tracen 43 242 gånger och ersatte 951 324
gästinstruktioner. Två interfolierade A/B-par gav:

```text
utan trace   9540,0 ms   9730,2 ms
guarded      9500,9 ms   9319,7 ms
vinst           0,4 %       4,2 %
```

Medianvinsten är cirka 2,3 procent. Samtliga körningar behöll exakt
`frameHash=0x8a71c24a`, slut-PC `0xffffffff800c6c34`, FIFO
`26453151/2585612`, 459 391 draw-paket och 3 864 swaps. Warm-probe och desktop
aktiverar tracen; övriga block använder oförändrad fallback.

## 2026-08-11: guarded-tracens headroom används i desktop

Efter guarded-tracen mättes CPU-budgeten om från samma f6750-state. 84 000
steg gav 10 nya swaps och cirka 63,3 hostanrop/s. 90 000 steg gav 11 swaps vid
cirka 62,7 hostanrop/s. 96 000 nådde samma 11 swaps och varierade mellan 61,4
och 63,8 anrop/s, medan 102 000 och 108 000 föll under 60. Desktopstandarden är
därför 90 000 steg: cirka tio procent fler riktiga gästbilder än 84 000 utan
att använda den smalare och resultatneutrala 96 000-marginalen.

## 2026-08-11: guarded JIT-trace för render-setup

En profil av de block som faktiskt faller igenom befintliga handskrivna
regioner pekade ut `0xffffffff801069f4` som nästa stora JIT-kandidat. Den nya
tracen kompilerar hela den exakta 31-instruktionskedjan: 29 raka instruktioner
med stack- och render-state-stores, terminal `jal 0xffffffff80109360` och dess
delay slot. Före varje körning verifieras kedjans slutord, jump och delay slot,
samt att stackram, källdata och de tre render-state-orden ligger i main RAM.
Alla andra tillstånd faller tillbaka till den befintliga exakta vägen.

I 300-anropsoraklet kördes den nya tracen 12 600 gånger och flyttade ytterligare
390 600 gästinstruktioner till JIT. Två interfolierade A/B-par gav 9,2 respektive
0,2 procent lägre `runMs`; värdlasten var tydligt varierande. Det längre
1 200-anropsprovet gav en stabilare skillnad:

```text
utan render-setup-trace   13049,8 ms
med render-setup-trace    11950,8 ms
vinst                         8,4 %
```

Det långa provet körde tracen 49 258 gånger och ersatte 1 527 000 extra
gästinstruktioner. A/B behöll exakt `frameHash=0xe87b12da`, slut-PC
`0xffffffff80079e18`, FIFO `27113239/2660774`, 474 871 draw-paket och 3 877
swaps vid desktopbudgeten 90 000 steg. Warm-probe och desktop aktiverar den
separata render-setup-flaggan tillsammans med det befintliga guarded-trace-
lagret.

## 2026-08-11: guarded JIT-returtrace för render-submit

Nästa verkliga fallback-block vid `0xffffffff80106b1c` kompileras nu över 38
kroppsinstruktioner, terminal `jr ra` och dess stackåterställande delay slot.
Tracen korsar både main-RAM-stores och fyra ord till Voodoos mappade
kommandoområde. Runtime-guarden verifierar kodsvansen, returparet, stack- och
state-områdena samt att kommandopointern är justerad och ligger inom
`0xa8000000..0xa83ffff0`; annars används den gamla vägen.

Korta A/B-par var motsägelsefulla, men det avgörande 1 200-anropsprovet gav:

```text
utan render-submit-trace   12854,3 ms
med render-submit-trace    12071,0 ms
vinst                          6,1 %
```

Tracen kördes 24 371 gånger och flyttade ytterligare 974 840 instruktioner
till JIT. Båda körningarna behöll exakt `frameHash=0xe87b12da`, slut-PC
`0xffffffff80079e18`, FIFO `27113239/2660774`, 474 871 draw-paket och 3 877
swaps. Warm-probe och desktop aktiverar den separata render-submit-flaggan.

## 2026-08-11: direkt backend för branch plus delay slot

Den generella safe-branch-vägen exekverar nu `beq`, `bne`, `blez` och `bgtz`
direkt tillsammans med sin verifierat säkra delay slot. Det undviker den stora
opcode-dispatchern, pending-branch-state och efterföljande targetupplösning för
varje par, men använder samma kanoniska branchmål och samma instruktionstiming.

Över 300 anrop tog den direkta vägen 1 298 192 branchpar. Två interfolierade
A/B-par gav 7,2 respektive 3,1 procent lägre `runMs`. Det längre provet gav:

```text
generisk branch-dispatch   12099,7 ms
direkt branchpar           11584,7 ms
vinst                          4,3 %
```

Långprovet exekverade 5 014 413 direkta branchpar och behöll exakt
`frameHash=0xe87b12da`, slut-PC `0xffffffff80079e18`, FIFO
`27113239/2660774`, 474 871 draw-paket och 3 877 swaps. Ett parallellt försök
att länka små successor-block förkastades efter en cirka 0,6-procentig
regression i det långa A/B-provet.

## 2026-08-11: direkta jump/return-par förkastade

Ett generellt terminatorförsök körde `jal`, `jr` och `jalr` direkt tillsammans
med en verifierat säker delay slot. Vanlig `j` kunde inte tas med: kortoraklet
behöll slut-PC och antal FIFO-ord men divergerade i paketavkodning och
framebuffer, vilket visar att pending-branch-state fortfarande observeras av
minst en steady-state-helper före dess delay slot.

Den säkra delmängden var bitexakt och körde 2 895 270 par över 1 200 anrop,
men två interfolierade långprov gav ingen vinst:

```text
generisk väg, medel   13963,5 ms
direkta kontrollpar  13986,4 ms
skillnad                 -0,2 %
```

En tidigare separat dispatchplacering var cirka 6,2 procent långsammare.
Hela försöket och dess flaggor är borttagna. Resultatet stärker att nästa steg
måste kompilera och äga hela blockterminatorn; ännu ett fristående parlager
amorterar inte dispatch- och guardkostnaden.
