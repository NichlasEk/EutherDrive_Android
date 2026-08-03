# Gauntlet Dark Legacy handoff inför omboot 2026-07-22

## Verifierad fortsättningspunkt

Arbetet fortsatte efter omboot från den pushade checkpointen:

```text
8b70a3d7 Extend Gauntlet runtime asset search
```

Kandidatändringen i
`EutherDrive.Core/Arcade/Vegas/GauntletDarkLegacyAdapter.cs` är nu
runtime-verifierad genom f760, f762 och f770. Den stora atlasmattan försvinner,
FIFO:n fortsätter och både record-0- och glyphbaserna når TMU0.

## Senaste fortsättningspunkt: riktig WAR-upload och hårdvarubyteordning

Den riktiga `WAR_FACE_HS`-payloaden är nu fångad i en obruten
f740 -> f751-körning med 200000 CPU-steg/frame. Placeholdern finns kvar vid
f750; under nästa frame fyller 32 Type-5-paket exakt fysisk wordrange
`0x3e9be..0x3ebbd` med 176 unika payloadbyte. Upload-state är
`00000900/0000080c/0001a0df` och producent-PC är `0x800fe7cc`.

Det avför den tidigare slutsatsen att payloaden aldrig når Type 5. Den gamla
generella Type-5-förswappen är nu explicit compatibility-only och av som
standard. Den registerfiltrerade sampler-byte-swappen är samtidigt borttagen
från probe-baseline. Den nya f770-referensen visar ett tydligt, mindre hackigt
porträtt och reloadar byte-identiskt:

```text
frameHash=0x80f5fb64
PPM sha256=734de15e30aaaf957130addccb4c4cc97b149fb78b689d63f720435e996c9e93
snapshot sha256=ee11fa70b4bf1869de38bb05799de7c61c087ac147b00099fa94ef65b411ebc7
artifacts/gauntlet-probe/gauntdl-war-face-type5-hardware-f770-200k-20260722.warm
artifacts/gauntlet-probe/gauntdl-war-face-type5-hardware-f770-200k-20260722.png
```

Nästa grafikgräns är panel-/glyphblocken och den omgivande scenrenderingen;
WAR-upload, LOD3-adressering och byteordning är nu verifierade.

## Nytt avgörande fynd

Den stora felritade 128x128-spriten använder descriptor `0x802592b0`, vilken
slår upp texture-set record 0 vid `0x802e26d4`. Recorden är giltig:

```text
+00 08020101
+04 00800080
+08 00000000
+0c ffffe000
+10 000002cf
+14 0003d104
```

Gästens spriteväg är nu följd hela vägen:

```text
0x800b07c8 sprite helper
  -> 0x800a9294 record lookup
  -> 0x800a66dc tom/no-op callback
  -> 0x800a6888 texture-state wrapper
  -> 0x800bd738
  -> 0x800bd100 Type-4 packet builder
```

`0x800a66dc` är alltså inte den saknade state-emitteraren; funktionen består
bara av stackjustering och retur.

CPU-spåret vid `0x800bd180` visar att både den fungerande glyph-recorden och
record 0 bygger samma Type-4-kommando `0x00059604`. Record 0 bygger uttryckligen:

```text
command  00059604
mode     8c2412cf
lod      00002104
base     ffffe000
```

Vid `0x800bd19c` lagras `ffffe000` korrekt i gästens kommandobuffer. Felet är
alltså inte descriptor, lookup, cachejämförelse eller gäst-builder.

Det generationsmedvetna command-FIFO-spåret visar den verkliga förlusten. För
paketet vid `0x015ceb0c` skrivs headern först:

```text
w0 storage=0x0eb0c logical=0x015ceb0c value=00059604 valid=1
w1 storage=0x0eb10 logical=0x0158eb10 valid=0
```

Payloadorden skrivs omedelbart efter headern, och `ffffe000` syns senare vid
`0x015ceb18`. Men `IsCommandFifoPacketReady()` resynkar från den giltiga men
ännu ofullständiga headern till nästa redan kompletta paket. Recordens
texture-state överges därför innan kroppen anländer. Nästa generiska
`0x80106a74`/`0x80106448`-packet återställer TMU0 till default
`0000100f/ff802000/00000000`, vilket är exakt state som den felaktiga triangeln
sedan samplar med.

## Verifierad ändring

I standard-FIFO-vägen har readiness ändrats så att en header som:

- är giltig,
- har samma logiska generation som read-head, och
- är markerad som packet header

väntar (`return false`) på återstående payloadord om paketet ännu inte är
komplett. Resync används fortfarande när den aktuella slotten inte kan vara
huvudet i den generationen. Detta bevarar FIFO-ordning för gästens normala
header-först-skrivningar.

Builden efter ändringen lyckades och runtime-verifieringen gav:

```text
dotnet build tools/GauntletProbe/GauntletProbe.csproj -c Release --no-restore
f760 frameHash=0xcdaed1a0 swaps=936
f762 frameHash=0x9aca0a83 swaps=944
f770 frameHash=0x9aca0a83 swaps=952
```

Det finns befintliga warnings, inklusive NU1902 för SharpCompress, men inga
nya kompileringsfel.

TMU-spåret visar `fffff800` från glyphpaketet vid `0x015ce870` och
`ffffe000` från record-0-paketet vid `0x015ceb0c`. Alla fyra payloadord har
rätt generation och producer-PC när paketet konsumeras.

## Nästa steg

1. Utgå från den nya f770-snapshoten nedan; upprepa inte f759-spåret om inte
   FIFO-ordningen misstänks ha regresserat.
2. Klassificera drawsen som producerar den vita bakgrunden respektive de små
   korrupta panel-/glyphblocken. f762 och f770 har identisk framebuffer trots
   fortsatt texturupload, så börja med draw/state-ägarskap i stället för fler
   långa väntkörningar.
3. Behåll record-0-bas `ffffe000` och glyphbas `fffff800` som regressionsoracle.
4. Gör nästa beteendeändring default-off tills en smal A/B-probe visar vilken
   drawklass som ändras.

### Baseline-runnern använder åter korrekt sample-basbias

En f771 -> f780-A/B från samma snapshot isolerade en konfigurationsdrift i
`run-gauntdl-baseline.sh`. Adapterns default och den tidigare verifierade
gradientmatrisen använder sample-basbias `0`, men runnern tvingade fortfarande
det historiska värdet `0x510`.

Med identisk CPU/FIFO/texturstate gav de två körningarna:

```text
bias 0      frameHash=0xb11fe479 colored=11948 zeroTexels=2722766
bias 0x510  frameHash=0x94f513a3 colored=22945 zeroTexels=2676732
```

Fler färgpixlar med `0x510` var falsk coverage: bilden innehöll långa
horisontella linjer genom hela framebuffer-ytan. Bias `0` tar bort linjerna och
bevarar de underliggande UI-, glyph- och face-drawsen. Runnerns default är
därför åter `0`; miljövariabeln kan fortfarande användas för explicita A/B-test.

Repo-lokala visuella orakel:

```text
artifacts/gauntlet-probe/gauntdl-f780-bias0.png
artifacts/gauntlet-probe/gauntdl-f780-bias510.png
```

### Face-drawen väljer LOD3 men dess yta saknas

Ett fokuserat sampler-spår på `WAR_FACE_HS`-descriptorn
`0x805b1be4` visar att baseline tidigare tvingade LOD0 trots
`tLOD=0x0600260c`. Den MAME-liknande derivatberäkningen ger däremot korrekt:

```text
base8p8=-256 perspective8p8=-1024 bias8p8=128
candidate8p8=896 clamped8p8=896 targetLod=3
layout=32x32 base=0x0fa6f8 sampled=0x0fa7be..0x0faef6
```

Den default-off `TEXTURE_USE_LOD_MIN`-hjälparen hade dessutom skalat det
6-bitars quarter-LOD-fältet som ett heltals-LOD. `0x0c` klampades därför till
LOD8. Hjälparen skalar nu fältet med `>> 2`, vilket ger LOD3. Experimentet
ska fortfarande vara avstängt globalt: min-LOD på alla draws förstör övriga
UI-texturer.

Derivatbaserad triangel-LOD väljer LOD3 endast för face-drawen och lämnar
resten av f780 i stort sett oförändrad, men ansiktet blir ett solitt rött
32x32-block. Råorden i ytan domineras av `fe00/0bff`. Ett Type5-spår från
f759 till f770 visar varför: den närliggande uploaden använder annan state
(`mode/lod/base=00000900/00000804/0001b63d`) och börjar vid fysisk byte
`0x0fb1e8`. Ingen Type5-sekvens materialiserar face-intervallet
`0x0fa6f8..0x0faef8`.

Nästa smala gräns är därför inte fler samplerjusteringar. Spåra var
`hiscore/legends`-postens LOD3-upload tappas mellan BGLoadModel-källan och
Type5-produceraren, och använd intervallet ovan som regressionsoracle.

Efter FIFO-checkpointen provades både A8-maskning och undertryckning av den
vita fast-fillen. A8-maskningen tar bort falska linjer men visar ingen scen;
utan den vita fillen återkommer den gamla atlasmattan under UI:t. Båda förblir
default-off. Nästa smala gräns är de nya Type-5-uploadsen f762 -> f770 mot den
första efterföljande scen-Type-3-drawen, inte alpha/clear/swap.

## Diskutrymme och temporära filer

`/tmp` fylls snabbt av `.warm`-filer. Under den här bringup-fasen ska stora
snapshots, RAM-dumpar, PPM/PNG och probe-loggar skapas repo-lokalt, i första
hand under:

```text
artifacts/gauntlet-probe/
```

Använd inte `/tmp` för nya Gauntlet-snapshots eller stora mellanresultat. Om ett
verktyg kräver en tillfällig katalog, skapa en tydligt namngiven katalog under
`artifacts/gauntlet-probe/` och kontrollera `git status` före commit; stora
probe-artifacts ska normalt förbli ignorerade.

## Viktiga repo-lokala checkpoints

```text
artifacts/gauntlet-probe/gauntdl-post-diagnostic-exit-f759-200k.warm
artifacts/gauntlet-probe/gauntdl-f762-fifo-head-wait-200k.warm
artifacts/gauntlet-probe/gauntdl-f770-fifo-head-wait-200k.warm
artifacts/gauntlet-probe/gauntdl-f762-asset-count-extend-200k.warm
artifacts/gauntlet-probe/gauntdl-f770-asset-progress-200k.warm
artifacts/gauntlet-probe/gauntdl-f780-asset-progress-200k.warm
artifacts/gauntlet-probe/gauntdl-f800-asset-progress-200k.warm
```

Den verifierade FIFO-fixen tar bort atlasmattan men löser inte återstående
drawfel. Nästa fortsättning ska börja från f770-checkpointen och isolera den
vita bakgrunden samt panel-/glyphdrawsen, inte med ytterligare lång väntan.

## Senaste fortsättning: f810-kontroller och FIRE3-gränsen

Den hårdvarukorrigerade WAR-referensen vid f770 är fortfarande
regressionsoraklet. Pixel-writer- och Type3-spår visar att de upprepade banden
kommer från A8 setup-draws vid PC `0x800c4e5c`; record-0-familjen samplar
exakt noll i f759 -> f760 och är inte en dold scen. MAME-kontrollen gav inget
stöd för ännu en lokal A8-formatregel.

RAM-state vid f770 är `0x8007`, inte diagnostikstate `0x8000`. Korrigering av
den första tolkningen: coin f771..772 och start f776..777 startar inte den nya
aktiviteten. En no-input-kontroll f770 -> f782 är exakt identisk med
coin/start-körningen (`frameHash=0xd56dede7`, swaps 964 och samma räknare).
Progressen till f800/f810 är tidsstyrd. Coin/start vid f810 är också
icke-kausal.

Fortsätt i första hand från:

```text
artifacts/gauntlet-probe/gauntdl-coin-start-f810-200k-20260722.warm
snapshot sha256=0bf161fc49490f2f59689a709674714e629c2cb528cc7c5d18838f18d77ac640
artifacts/gauntlet-probe/gauntdl-coin-start-f810-200k-20260722.png
png sha256=ddda94740c992bc30f181fc88a49064a987d99c2ad3f1783e47f08fb0ef82a1d
```

Den normala inputbryggan är verifierad genom spelets statusläsare. FIRE3/Turbo
f812..f816 når record 0 som `0x00000800` och gör f820 kausalt annorlunda:
no-input `0x20a6db35`, swaps 992, drawPackets 153396 mot FIRE3
`0xca921eeb`, swaps 988, drawPackets 151788. Det låser ändå inte upp scenen.
Vid f840 är state fortfarande `0x8007`, swaps fortfarande 988 och bilden är
fortfarande UI-band. En kontroll som inverterade hela recordet aktiv-lågt
avfördes eftersom alla spelarbits då blev aktiva i vila.

```text
artifacts/gauntlet-probe/gauntdl-fire3-exit-f840-200k-20260722.warm
snapshot sha256=c95f6ac3b7818dc6576db63fbc270faa5f17e8707d16b9daba97485a497458f3
artifacts/gauntlet-probe/gauntdl-fire3-exit-f840-200k-20260722.png
png sha256=68efa1981ef6f867261bcde4084e5eccc400c17a3a6fd325080e28d5f3304edd
```

f810-renderlistan har 61 poster: 38 med flagga `0x40`, 42 med noll-body och
19 med token. Endast material-set 0 konsumeras; alloc-tabellens `+0x4c` är
delad data på `0x804922b8`, inte en callback. Ett write-owner-spår f770 ->
f811 visar nu att listan inte är en world-lista alls: `0x800b2018` nollställer
count, `0x800b1c8c`/`0x800b1b10` bygger 53--69 poster, och
`0x800b1c78`/`0x800b1afc` pekar deras bodies på textbufferten
`0x8020f268...`. Samma pass emitterar `DIAGNOSTIC MENU` och
`Exit menu (FIRE 3)` via callern `0x800c7aa0`.

Stateblocket `0x80227a00..0x80227cff` uppdaterar flera närliggande räknare,
men själva `0x80227ab0` skrivs inte mellan f770 och f811 och ligger kvar på
`0x8007`. Nästa steg är därför väljaren/callern som håller
diagnostikrenderaren aktiv eller upstream-ägaren som ska ersätta den med en
world-renderer. Försök inte reparera de 61 textposterna till 3D-noder. Ändra
inte WAR-upload/byteordning, sampler-LOD, A8-semantik, display-buffer-val eller
inputpolaritet utan ny kausal evidens.

## Ny exakt WAR_FACE_HS-gräns

Type5-sekvensdiagnostiken spårar nu alla texture-space-3-paket, inte bara
`0xc0000205`. Därmed är den tidigare misstänkta källan `0x805b222c`
avförd: den tillhör en tidig `hiscore/legends`-textur och landar korrekt vid
TMU-byte `0x12a710`.

Korrigering: `0x8061c5a8..+0x1550`/TMU `0x4c25e0` är ett verkligt uppladdat
block men identifierades felaktigt som WAR genom en bristfällig asset-offset.
Det ska inte längre användas som WAR-orakel.

Den faktiska drawen är nu isolerad till bbox `(128,279)-(160,311)` med
descriptor `0x805b1be4` och
`mode/lod/base=8c2419cf/0600260c/0001a0df`. Baselinesamplern väljer LOD0,
layout `256x256`, fysisk bas `0x0d06f8` och läser spritt genom
`0x0d0cfe..0x0efcfe`. Sidbitsproben `0x1a0df -> 0x9a0df` ändrade exakt
porträttrutan men gav röd/vit skräpdata; den höga biten maskas inom TMU0:s
4 MiB-bank och är inte den saknade bindningen.

Två default-avstängda diagnostiker finns nu:

```text
EUTHERDRIVE_GAUNTDL_EXPERIMENT_VOODOO_TEXTURE_LOD_MIN_BASE_LEVEL=1
EUTHERDRIVE_GAUNTDL_EXPERIMENT_RUNTIME_WAR_FACE_TEXTURE_BASE=0x...
```

LOD-min `0x0c` ger korrekt `32x32`-layout. Med lokal bas blir rutan nästan
enfärgad; med `WAR_FACE_TEXTURE_BASE=0x984bc`, vilket mappar till den gamla
`0x4c25e0`-kandidaten, blir den också nästan enfärgad. Båda är negativa.
Fortsätt genom att återskapa upload-proveniens före f759 för bankregionen vid
`0x0d06f8`; snapshotens writer-map säger `none`, så ett senare f759-spår kan
inte identifiera producenten.

## WAR-porträttet är nu synligt i probe-baseline

Uploadgränsen är nu avförd. Rå TMU `0x0d0000..0x0f0000` är byte-identisk vid
f600, f610, f712 och f740, och ett f740 -> f770-Type5-spår ger noll skrivningar
i regionen. Blocket är äldre än den sena WAR-assetvågen.

Felet i den lokala LOD3-proben var koordinatskalningen: layouten blev `32x32`
men float-samplern klampade fortfarande toppnivåkoordinater `4..252` direkt.
När koordinaterna divideras med `2^3` läses hela
`0x0fa6f8..0x0faef6`, och en tydlig porträttsilhuett framträder.

Probe-baseline sätter nu:

```text
EUTHERDRIVE_GAUNTDL_EXPERIMENT_VOODOO_TEXTURE_LOD_MIN_REGISTER_BASE=0x1a0df
```

Detta påverkar endast bbox `(128,279)-(160,311)` (924 pixlar). f770 ger
`frameHash=0x83bda43f`, colored `11951` och PPM SHA-256
`babbf00c7a707c3d319a0f9c7f1beca6f0b851869e58c066283bd588d11b378a`.

`GauntletProbe` stöder dessutom
`EUTHERDRIVE_GAUNTDL_SAVE_FINAL_STATE=/path/state.warm` för stegvis
proveniensspårning. Den fulla baseline-preseten använder MAME fixed-fetch och
når samma LOD3-data, men dess setup-gradienter producerar vertikala ränder.
Fortsätt därför vid fixed-point-gradienterna för just WAR-drawen; ändra inte
payloadadress, TMU-bank eller LOD-min igen.

## Senaste checkpoint: diagnostik-enable undertryckt till f900

Den direkta diagnostikväljaren är nu exakt lokaliserad. Renderaren läser
`0x80227b9c` vid `0x800c7a64` och returnerar vid `0x800c7a6c` om den är noll.
Guest-PC `0x80019ef0` skriver annars ett till adressen. Ett smalt, default-off
experiment finns för fortsatt diagnostik:

```text
EUTHERDRIVE_GAUNTDL_EXPERIMENT_SUPPRESS_DIAGNOSTIC_RENDER_ENABLE=1
```

Det träffar endast fysisk `0x00227b9c`, PC `0x80019ef0`, värde ett och loggar
de första åtta träffarna. f770 -> f780 verifierade tre exakta träffar.

Från samma f770-state till f820 minskar renderlistan från 51 poster/30 flag40
till 21 poster/0 flag40. Samtidigt ökar swaps 964 -> 976 och texture-map
writes 768752 -> 1004768. Det betyder inte att world-renderern låsts upp:
menystate och huvudstate ändras inte. Diagnostiklagret försvinner bara och
asset/QIO/upload-arbetet hinner längre inom samma antal frames.

Den fortsatta f900-punkten når QIO metadata-index 10, destination
`0x802f5718`, 2000 byte. Den ger `frameHash=0xdaabcc41`, swaps 1038,
drawPackets 150769, texWrites 1491566 och texture-map touched 337915. PNG:n
visar fortfarande ingen riktig spelvärld, bara vit bakgrund samt ett brusigt
horisontellt upload-/texturband med WAR-porträttet.

```text
artifacts/gauntlet-probe/gauntdl-no-diagnostic-render-f900-200k-20260722.warm
snapshot sha256=ed7ebc0f8c878c17075657b148b5aba3288e428fac07e1056522d4f8da33c74d
artifacts/gauntlet-probe/gauntdl-no-diagnostic-render-f900-200k-20260722.png
png sha256=e78d45ca20c1cab20504edcacd27074042fa9d6b78af3363e13d3d09898440f5
```

Fortsätt från f900 och följ index-10-requesten till completion/assetägare.
Spåra därefter callern som naturligt ska sluta anropa skrivaren `0x80019ef0`.
Gör inte suppressionen till baseline: den är ett accelerator-orakel, inte en
emuleringsfix. Display-buffer-valet, inputpolariteten och diagnostiklistans
text-bodies är redan avförda.

### Korrigering efter f900: index 10 är redan complete

Index 10 är bundet till asset-tabellindex 9, `levels/levelE1`. Recordet
`0x80252e90` pekar på QIO `0x80218748` i `record+0`; äldre trace läste felaktigt
endast `record+8`. Vid `0x800c9944` finns object `0x80295600`, callback
`0x800ab4e4`, destination `0x802f5718` och längd `0x2000`. Guestinstruktionen
vid `0x800c9940` sätter status 2 (complete), varefter `0x800c9948` avsiktligt
nollställer objectpekaren. Destinationen innehåller icke-nolldata vid f901.

Bevara därför inte QIO-objectfältet och återöppna inte metadata/completion som
blocker. Trace väljer nu giltig `record+0` före fallback `record+8`. Nästa
kausala mål är levelE1-payloadens konsument och vägen som senare skriver
`No Nodes have this object`; detta är närmare world/model-state än själva
diagnostikmenyn.

## MAME-jämförelse: två TMU:er och riktig texture-alpha

Den lokala MAME-källan visar att Vegas-konfigurationen har Voodoo 2 med två
separata 4 MiB-TMU:er. Rasterizern samplar TMU1 först, kombinerar dess texel,
och matar sedan resultatet som `c_other` till TMU0. Den tidigare
bringup-rasterizern valde i stället en enda giltig TMU och tappade dessutom
alpha från Alpha8, NCC16, ARGB1555, ARGB4444 och AI88.

Ett nytt default-off-experiment modellerar den verkliga kedjan, inklusive
per-TMU-register/bank, bilinjär RGBA-sampling, TMU1 -> TMU0-combinern och
texture-alpha in i framebuffer-combinern:

```text
EUTHERDRIVE_GAUNTDL_EXPERIMENT_VOODOO_MAME_TWO_TMU_COMBINE=1
```

Från exakt samma f900-state till f901 gav kontrollen:

```text
baseline                         frameHash=0xdcb2114e colored=182837
two-TMU, gammal riktning          frameHash=0x9d9df305 colored=185854
two-TMU, MAME texelriktning       frameHash=0xeea919dd colored=163014
```

MAME gör varken bringup-vägens globala T-origin-flip eller omvända
8-bitars sample-lanes. Med båda avstängda blir atlasinnehållet visuellt
sammanhängande och det färgade slumpbruset försvinner. Samma atlas upprepas
fortfarande över flera draw-rektanglar; därför är återstående blockerare inte
en helbildsflip utan sannolikt draw-postens material/objektbindning eller dess
per-draw koordinatkälla. Varken två-TMU-vägen eller MAME-riktningen är
promoterad till baseline ännu.

Reproducerbar kandidat:

```text
artifacts/gauntlet-probe/gauntdl-two-tmu-mame-orient-f901-20260722.warm
sha256=2d074ac76c9edaf15f56330b349df97d743993204db814fce73710170f1eeb86
artifacts/gauntlet-probe/gauntdl-two-tmu-mame-orient-f901-20260722.png
sha256=659de9a37293edd7ca2e37a249353b1af0f73170a25f1fd0debfb528fffcf510
```

Nästa smala steg är att korrelera de upprepade Type3-rektanglarna med den
aktiva renderposten och dess textureMode/base/ST-källa. Använd kandidaten som
visuellt orakel och jämför särskilt varför flera poster återanvänder samma
atlasområde trots clampade TMU-lägen.

Den korrelationen visar att rektanglarna är riktiga Type3-par från
`0x800c4e5c`, inte fel hopsatta framebuffer-tiles. Paketen har ST0 och Wb;
MAME kopierar uttryckligen båda till TMU0 och TMU1, vilket vår setupväg redan
gör. Deras skärmkoordinater flyttar rektanglarna avsiktligt medan S/T täcker
hela den valda texturen. TMU0/TMU1-LOD beräknas nu också separat i experimentet.
Det är hårdvarukorrekt men hashneutralt i f901-kandidaten (`0xeea919dd`).

Den kvarvarande upprepningen är därför inte en T-flip, lane-ordning, gemensam
LOD eller sammanfogning av framebuffer-delar. De synliga quads som återstår är
loading/Hall-of-Legends-arbete från den redan kända renderproducenten.

`No Nodes have this object` är inte heller en world-loadergräns. Callern vid
`0x800b2904..0x800b297c` kör diagnostikens object-viewer för objektindex
`0x22`; texten väljs när dess statiska diagnostikrecord saknar fälten vid
`+0x50` och `+0x60`. Syntetisera därför inte nodepekare från den texten.

### Sen checkpoint: diagnostik-exit i huvudstate `0x8007`

Runtime-bryggan för FIRE 3 hade fortfarande den äldre state-guarden `0x8000`,
trots att senare mätningar fastställde att f900 ligger i huvudstate `0x8007`.
Turbo nådde därför det normaliserade inputrecordet men bryggan skrev aldrig
gästlatchen. Guarden accepterar nu både den tidigare verifierade `0x8000`-
transitionen och den sena `0x8007`-varianten.

En smal trace från den rena f900-snapshoten verifierar hela kedjan:

```text
f901 före puls: read32 0x80227ec8 = 0
Turbo:          active=0x40, p1=0x0800
brygga:         write32 0x80227ec8 = 1
gäst:           pc=0x80082ee0 read32 0x80227ec8 = 1

kontroll f906:  frameHash=0x098b0209 swaps=1042 texWrites=1491566
Turbo f906:     frameHash=0x3ac5577d swaps=1094 texWrites=1571630
```

Skillnaden är kausal och sker i gästflödet, inte bara genom vanlig
frame-progression. Med två-TMU-combinern och MAME:s texelriktning blir den
fortsatta f906-bilden `frameHash=0xbe919a03`, `colored=166981`. Den är ren och
rättvänd men visar fortfarande upprepade atlasdelar i de avsiktligt placerade
Type3-rektanglarna.

```text
artifacts/gauntlet-probe/gauntdl-state8007-exit-two-tmu-f906-200k-20260722.warm
sha256=b06972b138a35ab156b3bcc0f13c5080b6a419679f61d9d77131be21a9bb6d30
artifacts/gauntlet-probe/gauntdl-state8007-exit-two-tmu-f906-20260722.png
sha256=86b1b0e73b28488264cef898c9f459561bcc5cf618339a782574438d45f59845
```

Den aktiva kommandotypen `0x0180a8cb` har Wb+ST0 och 19 ord, precis som
MAME-decodern förväntar sig. Ett representativt par ger skärmrektangeln
`(232,167)..(360,295)`, W=`1/96` och S/T=`0..2.666667`; perspektivdivisionen
ger exakt `0..256` texlar. Paketen byter samtidigt TMU1-base mellan bland annat
`0x0009c40e` och `0x0009d964`. Återstående repetitioner uppstår alltså efter
korrekt vertexordning, perspektivskalning och base-registerval. Nästa smala
gräns är TMU1-uploadens fysiska write-pointer/layout mot de base-adresser som
paketen faktiskt samplar.

En rådump från f900 gör gränsen konkret. TMU1:s fysiska 32 KiB-fönster för
base `0x9c40e`, `0x9d964` och `0x9a00a` ligger vid `0x4e2070`, `0x4ecb20`
respektive `0x4d0050`; alla tre är helt noll. Samma lokala offsets i TMU0
innehåller däremot 24935, 30339 respektive 29381 icke-nollbyte och upp till
256 unika bytevärden. Inget av de första 64-byteblocken återfinns på någon
annan offset i TMU1-banken.

En default-off kausal probe låter därför endast TMU1-samplern läsa motsvarande
lokala offset ur TMU0, utan att ändra register, S/T, LOD eller upload:

```text
EUTHERDRIVE_GAUNTDL_EXPERIMENT_VOODOO_TMU1_SAMPLE_TMU0_MEMORY=1
```

Tillsammans med två-TMU/MAME-riktningen ändras f901 från `0xeea919dd` till
`0x1d887fbb`; flera riktiga färgporträtt och tiles blir synliga. Bakgrundens
upprepade mönster kvarstår, så proben får inte promoteras. Den bevisar endast
att avsett bildmaterial finns på rätt lokala offsets i TMU0 medan de aktiva
TMU1-registernas ytor är tomma. Fortsätt vid paket-5-källan som skulle fylla
TMU1 eller vid den register-/assettransition som felaktigt väljer TMU1.

### Snapshot v9 och den ärvda TMU-bankkontamineringen

Warm-snapshotformat v8 sparade hela 8 MiB texture RAM men tappade de separata
TMU0/TMU1-registren samt båda TMU:ernas NCC/palettcache. Format v9 sparar och
återläser nu alla fyra arrayerna. Ett f907 save/reload-prov behåller
`tmu0=tmu1=0C26100F/FF802000/00000000`, `frameHash=0xbe919a03` och exakt samma
PPM SHA-256 `663ec8ec4fa024dcf67a9405cef215b7111446885262a977f1835934ab9ec525`.
Även nästa frame, sammanhängande respektive med reload vid f907, är
byteidentisk. Äldre v1-v8-filer kan fortfarande läsas.

MAME:s `rasterizer_texture::write_ptr` jämfördes därefter med vår sena Type5-
adressväg. Att applicera MAME-writepekaren på hela f900--f906-vågen är
byteidentiskt med kontrollen, så LOD-offset, 32-bitars align och download-
swizzle är inte den återstående orsaken.

Den avgörande Type3-staten för de tomma ytorna är i stället:

```text
tmu0=80000009/FF802000/00000000      passthrough av downstream
tmu1=8C2419CF/06502600/0009C40E      faktisk textur/combine
```

Samma familj väljer även TMU1-bas `0x9d964` och `0x9a00a`. MAME:s equation-
modell bekräftar alltså att drawsen verkligen måste läsa TMU1; detta är inte
ett felaktigt sampler-val. De tre 32 KiB-fönstren är redan noll i TMU1 vid
f710 och förblir byteidentiska till f900, medan motsvarande lokala TMU0-
offsetar redan innehåller bilddata. Även artefakten med namnet
`gauntdl-type5-hardware-endian-cold-f600-200k-20260722.warm` har samma
TMU0/TMU1-fördelning.

Orsaken är att den senare tvåbankskedjan byggdes vidare från warm-states som
skapats innan bankseparationen var aktiv. Planen sade att de två bankreglerna
var promoterade, men `run-gauntdl-baseline.sh` exporterade dem inte. Wrappern
gör nu det. Nästa nödvändiga oracle är därför en verklig kall körning från
frame noll med `TEXTURE_UPLOAD_TMU_BANKS=1` och
`SEPARATE_TMU_TEXTURE_MEMORY=1`; återanvänd inte någon gammal f600--f900-state
för att avgöra var dessa tidiga ytor hör hemma.

### Genuint kall tvåbankslinje bekräftar lineagefelet

En ny frame-0--f600-körning med baseline-wrapperns båda bankregler aktiva och
200000 CPU-steg per frame slutfördes på 1381 sekunder. Den sparades direkt i
snapshotformat v9 och återlästes med exakt `frameHash=0x57fa9f15` samt bevarad
TMU-state:

```text
tmu0=8C2412CF/00302104/FFFFF800
tmu1=0C24100F/FF802000/00000000
```

Rådumpsjämförelsen mot den äldre så kallade cold-f600-filen bekräftar
lineagefelet:

```text
fönster    äldre TMU1  genuint kall TMU1   lokalt TMU0
0x4e2070   0           21972 / 212 unika   24939 / 256 unika
0x4ecb20   0               0 /   1 unikt   30339 / 255 unika
0x4d0050   0           25994 / 226 unika   29381 / 245 unika
```

Två av de tre senare aktiva TMU1-ytorna fylls alltså korrekt när bankningen är
aktiv från frame noll. Aliasproben avslöjade verkligt bildmaterial men
kompenserade för en kontaminerad checkpointkedja, inte för en omvänd sampler.
`0x4ecb20` väntar fortfarande på en senare upload och är därför inte ännu ett
separat felbevis.

Nya reloadbara orakel:

```text
artifacts/gauntlet-probe/gauntdl-cold-two-tmu-f600-v9-20260722.warm
sha256=1568fd1dbe1d73379fada93b4e2478d9b3847ff48de6836abf02627049bd519c
artifacts/gauntlet-probe/gauntdl-cold-two-tmu-f600-v9-20260722.png
sha256=5e7c2093307edd020010cc89162b7d1fc36134a66bc99e74759a36aa742e6dd0

artifacts/gauntlet-probe/gauntdl-cold-two-tmu-f700-v9-20260722.warm
sha256=fa17fc77ec44b8e6398dcbfbac08fed78182eb170d00ec92ff3be8f55d358899
artifacts/gauntlet-probe/gauntdl-cold-two-tmu-f700-v9-20260722.png
sha256=b9b5d7d5bd7ef6adc4a544935c3a8c2a28c648c803c9409801acdf155ded502a
```

f600--f700 gör inga ytterligare texture writes och behåller samma bankinnehåll;
f700 ger `frameHash=0x31a748a7`. Bilden är fortfarande diagnostikmosaik, inte
spelgrafik. Fortsätt diagnostic-exit/assetkedjan från den nya v9-f700-filen
och kassera alla gamla f700--f906-filer som visuella bankorakel.

### Ren f700--f906-kedja tar bort atlasmosaiken

Den verifierade FIRE-3/Turbo-bryggan kördes från den genuint kalla f700-filen,
med MAME:s två-TMU-combiner aktiv. f740 fångades mitt i den nya uploadvågen:
texture writes ökade från 576763 till 1043343 och TMU1 fick eget
`mode/lod/base=00000A00/00300800/00016416`. Vid f770 är de gamla stora
upprepade färgbanden borta. Bilden är en ren vit diagnostikyta med textfragment
och ett korrekt färgat WAR-porträtt, `frameHash=0x25e82a6d`.

Diagnostik-enable-suppressionen fortsatte därefter samma v9-linje till f900.
Den når QIO-index 10/`levelE1`, 846139 icke-noll texture-ord och fysisk sista
adress `0x52aa9c` i TMU1. f900 ger `frameHash=0x50f31d75`. Den sena
state-`0x8007`-pulsen ger vid f906:

```text
frameHash=0x03a897ce
swaps=1552
texWrites=1612470
colored=1815
tmu0=0C26100F/FF802000/00000000
tmu1=0C26100F/FF802000/00000000
```

f906-bilden är nästan vit med ett enda korrekt färgat porträtt. Den gamla
atlasmosaiken och dess upprepade feltexturer är borta. Detta är en kausal
förbättring från korrekt kall bank-lineage plus två-TMU-sampling, men ännu inte
riktig spelgrafik. Nästa blockerare är world-/panelgeometrins eller renderlistans
produktion efter assettransitionen; bildvändning, framebuffer-sammansättning,
MAME-write_ptr och TMU-bankval är nu avförda.

MAME-combinern ingår därför nu tillsammans med de två bankreglerna i
`run-gauntdl-baseline.sh`. Den påverkar endast probe-baseline och förblir
default-off i adaptern utanför bringupverktyget.

```text
artifacts/gauntlet-probe/gauntdl-cold-two-tmu-exit-f770-v9-20260722.warm
sha256=12ea0f3340a9434bb6066eb5027fefc093e16a5dbe5f7d8906b9f4e35ffeb433
artifacts/gauntlet-probe/gauntdl-cold-two-tmu-exit-f770-v9-20260722.png
sha256=c9156f7e549ae50039c161f1dd5c9ef9ce127e541f849386b02b240b152bcb91

artifacts/gauntlet-probe/gauntdl-cold-two-tmu-f900-v9-20260722.warm
sha256=dd8b74e56fcae03b84474d8ef348e4884839c63d201cb6eb0dc821048c2cb25a
artifacts/gauntlet-probe/gauntdl-cold-two-tmu-f900-v9-20260722.png
sha256=da5177784275c96032cf6fcb809be103c78576c2d61216bf9834a996d028531c

artifacts/gauntlet-probe/gauntdl-cold-two-tmu-f906-v9-20260722.warm
sha256=4455ebe7412b1dad115f32d578c7f70c739c606c0d42ad2db30bb07bdb5ba6b3
artifacts/gauntlet-probe/gauntdl-cold-two-tmu-f906-v9-20260722.png
sha256=66c8b51974ec27f4655bf09bedd59da9a876e2011046d6997bed6665f396599e
```

### Probe-wrappern kör nu hela MAME-kedjan

Den tidigare promotionen av
`EUTHERDRIVE_GAUNTDL_EXPERIMENT_VOODOO_MAME_TWO_TMU_COMBINE=1` i
`run-gauntdl-baseline.sh` var ofullständig. Två-TMU-anropet ligger inne i
MAME setup-gradient- och fixed-fetch-vägen, men wrappern aktiverade inte dessa
grindar. Den lämnade även texelriktningen implicit trots att den lokala
MAME-jämförelsen redan avfört global T-origin-flip och omvända 8-bitars lanes.

Wrappern sätter nu den sammanhängande probe-konfigurationen:

```text
TEXTURE_T_ORIGIN_FLIP=0
8BIT_TEXTURE_SAMPLE_REVERSE_LANES=0
TEXTURE_MAME_TRIANGLE_LOD=1
TEXTURE_MAME_SETUP_GRADIENTS=1
MAME_TEXTURE_FIXED_FETCH=1
MAME_TWO_TMU_COMBINE=1
```

En enframesverifiering från den genuint kalla v9-f900-snapshoten gav:

```text
frameHash=0x7328b7a0
texturedTriangles=96
texturedPixels=696640
zeroTexturedPixels=500918
framebuffer colored=97779
```

Bilden visar sammanhängande bruna panelytor och flera riktiga färgporträtt.
Det är ett stort visuellt steg jämfört med den nästan vita inerta
wrappervägen, men fortfarande diagnostik-/loadinggeometri med upprepade
paneler och korrupt text, inte en spelvärld.

```text
artifacts/gauntlet-probe/gauntdl-aligned-mame-f901-20260723.warm
sha256=94a7c02c1efd0d00029b57c7e6bf08888670db17fbb1e6212aad5edd724468fa
artifacts/gauntlet-probe/gauntdl-aligned-mame-f901-20260723.png
sha256=bb7944016099467371c5b062168acb629546ff25d63c8767168718cc2bb03384
```

Ett separat f906 -> f940-prov bekräftar nästa upstream-gräns. Framebuffern är
helt oförändrad (`frameHash=0x03a897ce`) under 34 frames och renderlistans
count förblir noll, trots fortsatt Type5-upload och world-validity-scan.
World-/panelrenderposterna publiceras alltså inte efter diagnostikexit; nästa
steg ska spåra publiceraren/callern som normalt skapar world-renderlistan, inte
återöppna flip, lane-ordning, framebuffer-sammansättning eller TMU-bankning.

```text
artifacts/gauntlet-probe/gauntdl-post-exit-f940-20260723.warm
sha256=ab582d5fc377013bc8f011944f3ce309e97cd7b5f38c922a28083da34f7fbbeb
artifacts/gauntlet-probe/gauntdl-post-exit-f940-20260723.ppm
sha256=6ee0c80766525b42bad63efa634ae352572bb6e03a167feb9e253f55e656628e
```

### Diagnostikexit är riktig; en namnlös assetpost blockerar ResetModels

F970-spåret visar att stateövergången redan har skett från `0x8007` till
`0x8008`. Latchen vid `0x80227ec8` återställs under state-`0x8008`-init och
återassertas senare av updatevägen; den ska inte nollas eller användas för en
forcerad transition.

Två exakta, kodsignaturbevakade accelerationer gör den fortsatta vägen
praktiskt körbar utan att ändra gästresultatet:

- modellens inre strängsökning vid `0x8004533c`;
- cache-underhållsloopen `0x800cc650..0x800cc660`.

Den generella runtime-`strcmp`-implementationen vid `0x8011f764` har också en
signaturbevakad fastpath. Efter dessa steg når exekveringen assetparsern och
avslöjar den verkliga korruptionen:

```text
asset entry       0x8024fb80 (index 10, name empty)
source            0x805611e8
asset pointer     0x838b120d
descriptor count  0x034e0001
normal observed   0x00001188
```

Den befintliga implausible-descriptor-rejecten är nu aktiverad i probeprofilen
och nollställer descriptorantalet för exakt detta verifierade fall. Tidigare
bröt den bara den första 55-miljonersloopen men lät samma felantal kopieras
vidare till ResetModels. Med nollningen lämnar gästen både parserloopen och
ResetModels och går vidare till nästa riktiga sökväg:

```text
/d0/monsters/death/textures.rom
```

f1080--f1100 stannar därefter vid den redan kända
`0x800c86b4..0x800c8728`-WaitForQio-vägen. QIO-objektet `0x80295600` har
state `0x207`, worker `0x800f087c`, sökvägsbuffert `0x80218518` och status
noll. Renderpoolen är fortfarande tom och framebuffern har därför samma
`frameHash=0x03a897ce`.

Nästa pass ska följa varför just detta nya `monsters/death/textures.rom`-jobb
inte får status från den redan aktiva runtime-interrupt-/workerpumpen. Forcera
inte world-renderposter, latch, framebufferflip eller worker-entry.

```text
artifacts/gauntlet-probe/gauntdl-invalid-asset-reject-f1100-20260723.warm
sha256=07417d60363c599b828f88b4cbe8707cbe7e7265386d4a99471cc5e24c8bc283
artifacts/gauntlet-probe/gauntdl-invalid-asset-reject-f1100-20260723.ppm
sha256=6ee0c80766525b42bad63efa634ae352572bb6e03a167feb9e253f55e656628e
```

### 2026-07-23: riktig worker-entry och stale filesystem-köhuvud

Den tidigare slutsatsen att callback `0x800f087c` inte nåddes var ett
sondfel: `EXTRA_STOP_PC=800f087c` jämfördes mot den signerade runtime-PC:n
`0xffffffff800f087c`. Med hela adressen träffar callbacken efter 4 124
instruktioner från f1060. Scheduler-dispatchen laddar korrekt:

```text
s1/callback  0xffffffff800f087c
s2/context   0xffffffff80295600
ra           0xffffffff800de480
```

Worker-tracen visar en komplett native livslängd för den första namnlösa
requesten. Producenten skriver owner `0x802c35f0` till `QIO+0x28` vid
`0x800f0c18`; worker-entryn läser samma owner, kör filesystem-anropet och får
gueststatus `0x1803`; epilogen skriver statusen och nollställer därefter
`QIO+0x28` vid `0x800f0ab0`. Callbackfältet är alltså inte korrupt och workern
ska inte forceras.

Ett viktigt A/B-resultat är att den generella
`EUTHERDRIVE_GAUNTDL_FIX_FSYS_QIO_STATUS` maskerar `0x1803` till `0x1800`.
Med just den reparationen explicit avstängd avvisar gästen först
`/d0//textures.rom` och bygger sedan den korrekta sökvägen:

```text
/d0/monsters/death/textures.rom
```

Detta är en renare continuation, men ännu inte en färdig fix. Vid f1120 har
det riktiga QIO-objektet fortfarande state `0x207`, status noll, owner
`0x802c35f0` och callback `0x800f087c`. Filesystem-köhuvudet
`0x8021e97c` pekar på nod `0x80295630`, samtidigt som nodens båda länkfält
redan är noll. Scheduler-ready `0x80262ae0` är tom och CP0 Cause saknar
software-IRQ. Det konkreta blockerande tillståndet är därför ett stale
filesystem-köhuvud/saknad wakeup, inte worker-kod eller QIO-data.

En enda guest-korrekt VBlank-timerpuls från f1120 går genom exceptionvektorn
och `eret`, men publicerar inte filesystem-jobbet. En efterföljande native
f1122--f1142-körning producerar `WaitForQIO: Timeout`; köhuvud, owner och
status är oförändrade och renderpoolen är fortfarande tom. Promota därför
varken timerpulsen eller den globala statusmaskningen. Nästa pass ska spåra
wakeupen vid enqueue/dispatcher-gränsen och varför software-signalen försvinner
när `0x8021e97c` fortfarande äger en avlänkad nod.

Reload-verifierade continuations och bild:

```text
artifacts/gauntlet-probe/gauntdl-native-qio-status-f1120-20260723.warm
sha256=d72844c01ae925049a35549f48f7b3807c891879441ee82d18442b2db258e504
artifacts/gauntlet-probe/gauntdl-native-qio-post-pulse-f1142-20260723.warm
sha256=a1e958e6fe455469ea553b9cb7b2d010170ec0e9c0fd7146888ecddff5df1abc
artifacts/gauntlet-probe/gauntdl-native-qio-status-f1120-20260723.png
sha256=a6ba68fe13d0f0de43cd5870edd08e118a1b341bba9b871eeacec8733a2d5cef
```

### 2026-07-23: ren f1030-rebase återställer native IDE-completion

Den föregående slutsatsen om ett stale filesystem-köhuvud var för tidig.
Write-watch av både filesystem-kön `0x8021e97c` och scheduler-ready-kön
`0x80262ae0` visar att targetnoden verkligen dispatchas. Filesystem-servicen
går därefter state 0 -> 1 -> 2. State 1 startar en riktig bus-master-IDE-read
via `0x800f6c44`; den saknade händelsen var IDE-completion-interrupten, inte
den initiala worker-wakeupen.

MAME-källan kopplar Vegas IDE IRQ direkt till Nile PCI interrupt D. Den lokala
Nile-routingen matchar detta: high control `0x8000ba00` routar PCI-D till
MIPS-interruptvektor 3. Problemet var i stället snapshot-lineage:

```text
f950   deviceControl=0x00
f970   deviceControl=0x00
f980   deviceControl=0x00
f1000  deviceControl=0x00
f1030  deviceControl=0x00
f1060  deviceControl=0x9b
```

`0x9b` har ATA `nIEN`-biten satt. Därför genomförde den förorenade
f1060-continuationen DMA:n men `IdeDiskDevice.SignalInterrupt()` höll
INTRQ låg. En ren replay från f1030 skriver aldrig device-control-porten och
behåller `0x00`; IDE-tracen visar då sector read, DMA-copy och native
completion. Exceptionvektorn får `Cause=0xa000` vid `EPC=0x800f69b4`, och
gästens IDE-handler fortsätter filesystem-state-maskinen utan QIO-statusfix
eller syntetisk wakeup.

Rebasen f1030 -> f1080 ger den första tydliga framflyttningen:

```text
deviceControl=0x00
filesystem state=10
texture writes=2489081
texture-map writes=955824
texture-map touched=238618
```

En fortsatt ren körning till f1120 håller IDE:n frisk och laddar fler riktiga
assets. Bilden är ännu felkomponerad och nästan vit, men innehåller nu en
tydlig figur-/ansiktstextur nere till vänster samt flera upprepade
texturfragment. Disk/QIO är alltså inte längre den aktuella bringup-gränsen.
Nästa pass ska utgå från den rena f1120-snapshoten och följa varför riktiga
texturer hamnar som små upprepade fragment i stället för en sammanhängande
Voodoo-scen: packet 3/setup, koordinat-/extent-proveniens och vald color
buffer är nu relevanta igen.

```text
artifacts/gauntlet-probe/gauntdl-clean-native-irq-f1120-20260723.warm
sha256=ced845742616175768d5b6f3f59fb163a471a96e09e9556b296cb166fd051c51
artifacts/gauntlet-probe/gauntdl-clean-native-irq-f1120-20260723.png
sha256=2af4895020a0d1537bc180fda4a1ec6396f596c7448580ad57062b73880faca9
```

### 2026-07-23: TMU0-scenen, lång renderpassage och verifierad Y-origin

Single-TMU + TMU0-S/T fortsätter ge den klart bästa riktiga spelscenen. En
continuation på ytterligare 20 miljoner råa CPU-instruktioner från
`gauntdl-single-tmu-st0-scene-f1160-e2m-plus10m` ökade Type3 från 187275 till
223886 och producerade en sammanhängande dungeon med golv, pelare, väggar och
spikar. Swaps förblev dock 7150. Den nya snapshoten motsvarar därmed totalt
cirka 30 miljoner extra instruktioner efter f1160:

```text
frameHash=0x7202e3ef
Type3=223886
textured triangles=24687
covered=20049
pixels=6096068
zero=57193
swaps=7150

artifacts/gauntlet-probe/gauntdl-single-tmu-st0-scene-f1160-e2m-plus30m-20260723.warm
sha256=71ce51bf3d730971133ae2bc51a36d60b8741dedb3c0bef15855b83d00bb98aa
artifacts/gauntlet-probe/gauntdl-single-tmu-st0-scene-f1160-e2m-plus30m-20260723.png
sha256=a6af9b2b802813df48b7abefab405432c0572130b7e79050dcb9213cc74a29f5
```

Råa extra-steg är inte ett korrekt spelbarhetstest eftersom de inte kör
`GauntletDarkLegacyMachine.RunFrame()` och därmed inte pulserar VBlank eller
övrig frame-tid. En kontroll med tio riktiga frontend-frames visade ändå ingen
ny swap. Det befintliga kontextbevarande VBlank-tick-experimentet flyttade
gästen men gav upprepade returer till `0x80000000` från `0x800ccc58` och ska
inte promoveras.

MAME jämfördes direkt mot den lokala Voodoo-implementationen. MAME applicerar
`fbzMode.y_origin` per raster-scanline som `fbiInit3.yorigin - y`; den vänder
inte blint den exporterade slutbilden. Samma hårdvarubeteende finns nu i alla
lokala triangelrastervägar och `fbiInit3` syns i debugstatus. Den här scenen har
emellertid:

```text
fbzMode=0x00000460  (y_origin=0)
fbiInit3=0x00110001 (yorigin=0)
```

10k-regressionen är därför avsiktligt byteidentisk med tidigare
`frameHash=0xbdd7887c`. En manuellt vänd aktiv 640x384-yta ser visuellt korrekt
ut, men registren bevisar att en generell framebufferflip vore fel fix.
Nästa pass ska följa vertextransformen/modelltraverseringen som matar
`0x800c6200..0x800c7200`: det är nu den mest sannolika gemensamma orsaken till
den inverterade projektionen och det orimligt långa draw-passet.

### 2026-07-23: två riktiga swaps och läsbar diagnostic-menu i frontbuffern

En default-off Type3-rasterdiscard lades till som ren tidsdiagnostik. Den
avkodar fortsatt hela command-FIFO:n och uppdaterar setup-vertexstrippen, men
hoppar över själva pixelrasteriseringen. Från plus30m-snapshoten gav den:

```text
+5m   swaps=7150 Type3=233352
+16m  swaps=7150 Type3=253039
+18m  swaps=7150 Type3=256024
+20m  swaps=7152 Type3=260533
```

En ny fullrenderad replay av exakt +20m bekräftade samma CPU-, FIFO- och
swapläge och sparades som plus50m. De två interna buffertarna avslöjade att
frontbuffer 0 innehåller en ren, läsbar Gauntlet Dark Legacy
`DIAGNOSTIC MENU` med logotyp, medan backbuffer 1 innehåller den brusiga
dungeon-scenen.

`ChooseRenderBufferIndex()` valde tidigare buffer 1 trots
`front=0/back=1`, eftersom heuristiken premierade kandidatens större färgade
yta. En giltig frontbuffer med fler än 1024 aktiva pixlar och detaljrik
färgpalett väljs nu alltid före fallback-heuristiken. Reload från samma
snapshot ändrade därför endast presentationen:

```text
före: chosen=1 frameHash=0x24617f8b colored=196608
efter: chosen=0 frameHash=0xf95a6bf2 colored=13226
```

Verifierade artefakter:

```text
artifacts/gauntlet-probe/gauntdl-single-tmu-st0-scene-f1160-e2m-plus50m-20260723.warm
sha256=d4133b400dd5f1faa2c36297ab40e69a384e3f203df8fb09bb3a94600e755c5f
artifacts/gauntlet-probe/gauntdl-single-tmu-st0-front-f1160-e2m-plus50m-20260723.png
sha256=5ba88e4218279d6ddc9d0149e5c3d948e727c332464421b8718a912b08545663
```

Detta är den första läsbara fullskärms-UI:n efter den långa scenpassagen och
bevisar att swap/frontbuffer-tillståndet nu kan presenteras korrekt. Nästa pass
ska utgå från plus50m-snapshoten, mata verklig input mot diagnostic-menyn och
kontrollera nästa swap. Dungeon-backbufferns färgbrus ska därefter isoleras
som pixel-/skrivlayout; det är inte längre ett presentationsbufferproblem.

### 2026-07-23: FIRE 3 lämnar menyn och Type3-Y isolerar 3D-orienteringen

En två-frame `INPUT_C`/FIRE 3-puls vid f1160--f1162 från plus50m-snapshoten
fungerar som riktig guest-input. Gästkoden lämnar diagnostic-menyn, passerar
`CREDITS`-vägen och börjar rita dungeon-scenen. Fem riktiga frames gav
`frameHash=0xd92e5a45`; 20 ytterligare frames fyllde scenen vidare till:

```text
frame=1185
frameHash=0x33fd81be
Type3=263875
textured triangles=1893
covered=1517
pixels=426548
swaps=7152
```

2D diagnostic-menyn är rättvänd medan all Type3-dungeon-geometri är
vertikalt inverterad. Det avgränsar felet från slutscanout och LFB/UI. En
default-off Type3-prob speglar därför enbart packet-vertex-Y runt den
verifierade 384-linjersviewporten. Eftersom speglingen byter winding
kompenserar samma probe Type3-cullsignalen. Utan cullkompensation föll
coverage till 92514 pixlar; med kompensation återkom den:

```text
normal Type3:       frameHash=0x33fd81be colored=182065 pixels=426548
Y-flip + cull:      frameHash=0x6b358c8b colored=182053 pixels=493447
CPU/FIFO:           identiskt, pc=0x800c66a0 Type3=263875 swaps=7152
```

Den kompenserade bilden matchar visuellt den manuellt vända aktiva
640x384-referensen, men lämnar 2D-menyn och framebufferpresentationen
orörda. Experimentet ska ännu inte göras till generell Voodoo-default:
gästens `fbzMode.y_origin` är fortfarande noll, så nästa pass ska knyta
signfelet till guestens Glide-origin/CPU-transform innan hårdvarubeteendet
promoveras.

```text
artifacts/gauntlet-probe/gauntdl-post-fire3-f1185-20260723.warm
sha256=3bb6df0218470007407d9611d478d3ebc38fc04aade20d58965e87826cb9ff7c
artifacts/gauntlet-probe/gauntdl-post-fire3-f1185-20260723.png
sha256=571dc55ce04473a1c0dd20c35234040ce0c7c8dd579dccd4d00d405b68a7ef9d
artifacts/gauntlet-probe/gauntdl-fire3-type3-yflip-cull-f1185-20260723.warm
sha256=028493c801a6d7118c4b20d3a33f5f8357679a5a7b78e7d9814016f0facdcc57
artifacts/gauntlet-probe/gauntdl-fire3-type3-yflip-cull-f1185-20260723.png
sha256=67eba303a170773db1359ae23235feb15edcc05c33eee94a7a6f16ee5aaec5b8
```

### 2026-07-23: MAME-origin-A/B och R5000 FCC-korrigering

R5000-implementationens `MOVF/MOVT.S` och `.D` använde tidigare alltid FCC0.
MAME väljer i stället FCC-fältet från instruktionsbitarna 20--18. Den lokala
avkodningen använder nu motsvarande `(ft >> 2) & 7`. Samma FIRE 3-körning från
plus50m till f1185 blev dock exakt oförändrad:

```text
före FCC-fix: frameHash=0x33fd81be
efter FCC-fix: frameHash=0x33fd81be
plus30m zero-step-regression: frameHash=0x7202e3ef
```

FCC-felet var alltså verkligt men orsakar inte den inverterade dungeonscenen.

En andra default-off probe applicerar MAME:s riktiga Voodoo-beteende i
rastersteget: den lämnar vertexdata och winding orörda men mappar skrivraden
som `yorigin - y`. Med en 384-raders aktiv viewport gav den:

```text
EUTHERDRIVE_GAUNTDL_EXPERIMENT_VOODOO_FORCE_RASTER_Y_ORIGIN=1
frame=1185
frameHash=0x61a58b7e
Type3=263875
textured triangles=2156
covered=1691
pixels=493447
swaps=7152
```

Bilden är rättvänd och sammanhängande utan Type3-cullkompensation. Den är
visuellt nästan identisk med vertexflip-proben, men den här vägen modellerar
Voodoo-hårdvaran på rätt nivå.

Kallstartsspårning av register `fbiInit3` visar samtidigt att spelet självt
skriver:

```text
0x00110040 -> 0x00110000 -> 0x00110001
```

Y-originfältet är alltså avsiktligt noll i gästläget; proben ska inte göras
till generell register-default. Ett fokuserat CPU/FPU-spår på en riktig vertex
visar:

```text
före viewport: f25=0.871096909
MADD.S:        f25 = f25 * 192 + 192
FIFO Y:        f25=359.25061
```

Den rättvända motsvarigheten ligger nära rad 25. R5000 COP1X-semantiken
matchar MAME, så nästa rotorsaksmål är tecknet i projektion/matrisvägen som
producerar NDC-Y före `0x800c6f84`, inte slutscanout eller Voodoo-packetformat.

```text
artifacts/gauntlet-probe/gauntdl-fire3-raster-yorigin-f1185-20260723.warm
sha256=1e3a479ea7ebb70abe3cc70ef34e8f6706eba2acc7918d6739a4ef4f219659f0
artifacts/gauntlet-probe/gauntdl-fire3-raster-yorigin-f1185-20260723.png
sha256=d9dd4341d03de2f05d193da29443c6f4e0bc456ef20a7cb68c9e2120cda9ee02
```

En 20-frame fortsättning med samma raster-origin-probe visar att vägen inte
bara producerar en korrekt orienterad stillbild. Gästen och renderströmmen
fortsätter framåt och fyller scenen med mer sammanhängande arkitektur:

```text
frame=1205
frameHash=0xd8514c8b
Type3=266773
textured triangles=1885
covered=1563
pixels=92871
colored=187986
swaps=7152
```

```text
artifacts/gauntlet-probe/gauntdl-raster-yorigin-f1205-20260723.warm
sha256=c68fe9fb461d291e1ee438ac2760b97706b0de675d3e2bc44f212aab1a19c53d
artifacts/gauntlet-probe/gauntdl-raster-yorigin-f1205-20260723.png
sha256=10850295fc1e8626226a4e4dfbcef7865cebacb7b71015eeba9ef77e9555a567
```

### 2026-07-23: rätt Y-origin är nu frontendstandard och input når IOASIC

Den verifierade Gauntlet-specifika raster-originen är inte längre beroende av
ett experimentflagga. Den namngivna fixen
`EUTHERDRIVE_GAUNTDL_FIX_VOODOO_GAUNTLET_RASTER_Y_ORIGIN` ingår i
bringup-standardläget som både desktop- och Android-frontenden använder.
Standardhöjden är 384 och kan vid behov ändras med motsvarande `_HEIGHT`-flagga.
Det gamla experimentnamnet stöds fortsatt som kompatibilitetsalias.

Baseline-runnern aktiverar fixen explicit. En fortsättning från f1185 till
f1205 utan det gamla experimentflagget reproducerar exakt den tidigare
rättvända scenen:

```text
frameHash=0xd8514c8b
```

Ett explicit opt-out,
`EUTHERDRIVE_GAUNTDL_FIX_VOODOO_GAUNTLET_RASTER_Y_ORIGIN=0`, reproducerar
exakt den gamla orienteringen från plus30-snapshoten:

```text
frameHash=0x7202e3ef
```

Frontendens riktiga inputväg är kopplad till Gauntlets IOASIC. En enbildsruta
med `UP` från f1205 gav:

```text
player12=fffe
p1=00000010
```

Det bevisar både aktiv-låg IOASIC-port och rätt runtime-riktningsbit. En
20-rutors kontroll, UP-puls och FIGHT-puls gav alla `0x8f916a6f`; scenen vid
denna snapshot reagerar alltså ännu inte visuellt på spelinput. Kalla inte
gameplay verifierat förrän coin/start har nått en interaktiv spelsekvens.

Desktopfrontenden bygger i Release med 0 fel:

```sh
dotnet build EutherDrive.UI/EutherDrive.UI.csproj -c Release --no-restore
dotnet EutherDrive.UI/bin/Release/net8.0/EutherDrive.UI.dll \
  /home/nichlas/roms/MAME/Midway/Vegas/gauntd
```

Standardtangenter för arcade är pilar, `Z`/`X`/`C` för
fight/magic/turbo, `Enter` för start och `5` för coin. Den varma
probe-snapshoten kan ännu inte laddas av den vanliga UI:n, så frontendtestet
startar kallt och är långsamt. Nästa praktiska slice är att nå en verifierat
interaktiv coin/start-sekvens och därefter flytta warm-state-formatet från
probe-reflektion till en riktig Gauntlet-savestateväg om kallstarten hindrar
snabb användartestning.

### 2026-07-23: frontend använder hela baselineprofilen; FIFO-wrap förblir stabil

En fortsatt körning hittade en exakt bildregression mellan f1220 och f1221.
f1220 visar fortfarande den sammanhängande spelvärlden, men utan global
packet-state tolkade standard-FIFO:n en vertexpayload-float som ett nytt
Type3-kommando:

```text
packetStart=0x0280032c
command=0x3b83928b
words=82
producer pc=0x800c5bd8
raster pixels=475388
frameHash=0x8f916a6f
```

Det falska packetet uppstod när write-triggered decode hann konsumera
payloadord efter att ringlagringen återanvänts. Den redan tidigare verifierade
och promoterade globala packet-state-modellen fanns i
`BaselineBringupEnvironment`, men baseline-scriptet samt normal desktop- och
Android-start använde bara delar av profilen.

Baseline-scriptet exporterar nu
`EUTHERDRIVE_GAUNTDL_EXPERIMENT_VOODOO_STANDARD_FIFO_GLOBAL_PACKET_STATE=1`.
Desktop och Android anropar alltid `ApplyBaselineBringupPreset()` för Gauntlet;
explicit satta miljövariabler fortsätter att vinna över presetvärdena.

Exakt A/B från samma f1220-state:

```text
utan global packet-state:
frameHash=0x8f916a6f
raster pixels=475388

med global packet-state:
frameHash=0xd20ba559
raster pixels=19633
Type3 packets in frame=24
```

Den korrigerade f1221-bilden fortsätter den sammanhängande världen och en
ytterligare 24-frame-körning förblir scenmässigt sammanhängande vid f1245:

```text
frame=1245
frameHash=0x4b605c57
Type3=270228
colored=187743
```

```text
artifacts/gauntlet-probe/gauntdl-global-packet-f1245-20260723.warm
sha256=f796eae53bb35c424758f0ba4bb6f1628ef1af334b76b6af14b56839bcbcebb8
artifacts/gauntlet-probe/gauntdl-global-packet-f1245-20260723.png
sha256=abf3c1721a40ae12438b70605143a46783447f78930cb067f82e196255a8359a
```

Desktop Release bygger med 0 fel. Androidkällan använder samma presetväg, men
den här maskinen saknar installerad Android-workload och lokal kontroll stannar
därför med `NETSDK1139`; det är en SDK-begränsning, inte ett C#-kompilatorfel.
Nästa grafikgräns är de kvarvarande orange/atlasfärgade polygonerna och
småspritarna i den i övrigt riktiga världen, inte raster-Y eller FIFO-wrap.

### 2026-07-23: reserverade packetbitar stoppar atlas- och orangepolygonerna

En framebisektion från den rena f1221-snapshoten visade att den stora orange
polygonen introducerades direkt i f1222. Pixel-last-writer band den till ett
falskt Type3-paket:

```text
packetStart=0x02803524
command=0x43b7ce8b
words=123
producer pc=0x800bd1e4
orange/yellow writes=26762 pixels
```

Kommandot var egentligen vertexdata. Precis före feltolkningen resynkade
standard-FIFO-modellen dessutom till floatvärdet `0x44000000` som ett enords
Type0-NOP. MAME:s Voodoo 2-format visar två hårdvarukrav som tidigare saknades
i `IsImplausibleCommandFifoPacket`:

- Type0 reserverar bit 31:29 och definierar bara funktion 0--4.
- Type3 reserverar bit 27:26 och 21:18.

`0x43b7ce8b` har Type3-fält 21:18 satt till `0xd` och kan därför inte vara en
packetheader. Standard-FIFO-resync validerar nu både dessa reserverade bitar
och att sparad packet-end exakt motsvarar den längd som kommandot kodar. Det
gör även reload av en warm-state med äldre ringmetadata säker.

Exakt körning från samma f1221-state efter korrigeringen:

```text
frame=1222
frameHash=0xb0137750
false cmd 0x43b7ce8b=0 packets
textured=283
covered=283
pixels=46076
```

Vid f1245 är orange- och regnbågsatlaspolygonerna borta och den riktiga
slotts-/dungeonvärlden täcker hela den aktiva spelytan:

```text
frame=1245
frameHash=0x177bad29
textured=2240
covered=1929
pixels=540855
```

Ytterligare 20 frames till f1265 förblir stabila:

```text
frame=1265
frameHash=0xd6819d49
textured=942
covered=762
pixels=41976
```

```text
artifacts/gauntlet-probe/gauntdl-reserved-packet-guard-f1245-20260723.png
sha256=54df2c648d0573a6b030c2baa0f20e57d6e65ca356d295decd892b1dd3c22da1
artifacts/gauntlet-probe/gauntdl-reserved-packet-guard-f1265-20260723.png
sha256=6e74b0ec8dbb5b96ef5de7260634861883d558c4d06b17642a5ca4eb44ca8ae8
artifacts/gauntlet-probe/gauntdl-reserved-packet-guard-f1265-20260723.warm
sha256=f425f3178937d22586839d6d9452aadfd95c5fa603bf487230d1136be5fe7c79
```

Kvarvarande synliga fel är vita bakgrundshål och möjligen enstaka felkopplade
material på nivåobjekt. Nästa steg är därför textur/TMU- och clear/depth-spåret,
inte fler generella FIFO-paketsuppressionsregler. Därefter återstår en
verifierat interaktiv coin/start-sekvens för praktiskt frontendtest.

### 2026-07-23: första riktiga swapen avgränsar world-texturens saknade ägare

En två-frame coin-puls följd av en två-frame start-puls från f1300 gör att
gästen utför nästa riktiga swap vid f1315. Buffer 1 blir aktiv och den gamla
vita bakgrunden försvinner. Geometrin är sammanhängande, men den övre
world-ytan använder mosaikartade texeldata:

```text
frame=1315
frameHash=0xd89cf2a9
swaps=7153
drawBuffer=1
textured=1556
covered=1348
pixels=426590
zero=42
```

De största dragningarna använder bland annat registerbas `0x00029504`,
fysisk bas `0x14a820`, mode `0x8c22410f` och LOD `0x00002604`.
Paketkoordinater, coverage och packetheader-validering är rimliga. Två-TMU,
TMU0-S/T, lane, T-origin och format-A/B ändrar hash men inte mosaikfamiljen.

Writer-proveniens kan nu avgränsas med:

```text
EUTHERDRIVE_GAUNTDL_TRACE_VOODOO_TEXTURE_SAMPLE_WRITERS_RANGE_MIN
EUTHERDRIVE_GAUNTDL_TRACE_VOODOO_TEXTURE_SAMPLE_WRITERS_RANGE_MAX
```

`GauntletProbe` kan dessutom spara/ladda en default-off writer-sidecar med
`EUTHERDRIVE_GAUNTDL_SAVE_TEXTURE_WRITER_SIDECAR` och
`EUTHERDRIVE_GAUNTDL_LOAD_TEXTURE_WRITER_SIDECAR`. Vid import behålls endast
ägare vars slutliga fysiska texturord matchar byte-för-byte, så provenance
från en avvikande warm-gren kan inte märka om en annan sidversion.

En spårad continuation från den rena f1120-kedjan gav noll writer-poster i
intervallet `0x140000..0x160000`. Den sidan tas alltså inte över av någon
senare world-uppladdning. En 64 KiB page-wrap och en page-scan som flyttade
samtliga samplingar till sidorna 00, 04, 0a, 10, 15, 18 och 24 gav alla samma
sammanhängande geometri men fortsatt mosaik. Felet är därför inte ett enkelt
sampler-page-offset: den legitima world-payloaden saknas före Voodoo-samplern.

```text
artifacts/gauntlet-probe/gauntdl-page-scan-f1315-20260723.png
sha256=f73666fb469dbbf2b4ebdf9ffeff44258d85707bfed945447365afdddd206d39
```

Default-neutral regression från den auktoritativa f1306-snapshoten är exakt:

```text
frameHash=0xd89cf2a9
fifoWords=10856116
fifoPackets=1284730
drawPackets=275048
texWrites=2577260
swaps=7153
```

Nästa smala gräns är nu QIO/bundle-deskriptorns page-livstid: följ den
gästvalda world-källan som borde ersätta den äldre GEB/font-sidan och reparera
den saknade senare Type5-uppladdningen. Ändra inte NCC, samplerlayout eller
globala page-wraps utifrån denna scen.

### 2026-07-23: record 45 har egen upload; råbasdumpen använde fel LOD

Den föregående slutsatsen om en saknad senare Type5-uppladdning var fel.
Record 45 i `levels/levelE1`-set 9 är:

```text
record=45 address=0x804a9d68
fileOffset=0x0006e0e0
texBaseAddr=0x00029504
tLOD=0x00002604
lod1Address=0x0015a820
```

Ett nytt default-off Type5-filter på registerbas:

```text
EUTHERDRIVE_GAUNTDL_TRACE_VOODOO_TYPE5_TEXTURE_UPLOAD_SEQUENCE_TBASE
```

visar 160 sammanhängande, icke-noll paket för exakt `tbase=0x29504`.
Uploaden börjar med target `0x008000`, och MAME-write-pointerformeln placerar
första ordet på fysisk `0x56a08`, alltså byteadress `0x15a820`. Det är exakt
recordets LOD1-adress. Sidan vid råbasen `0x14a820` tillhör därför inte den
aktiva nivån; den gamla RGB332-page-scanen dumpade dessutom YIQ-format 1 som
om det vore RGB332 och är inget visuellt texturorakel.

Samplertracen kan nu avgränsas med:

```text
EUTHERDRIVE_GAUNTDL_TRACE_VOODOO_TEXTURE_SAMPLE_REGISTER_BASE
```

För drawen `mode/lod/base=8c22410f/00002604/00029504` väljer MAME:s riktiga
derivat- och perspektivformel LOD 3 eller 4. Exempel:

```text
base8p8=-700
perspective8p8=-1623
bias8p8=128
candidate8p8=1051
targetLod=4
resolvedBase=0x15fc20
size=16x16
```

Type3-paketet är komplett och beskriver en projicerad 512x234-quad med fyra
giltiga XY/W/S/T-hörn. De stora samplerkoordinaterna runt 3000 wrappar över
den valda 16x16-nivån; den synliga upprepningen kommer alltså inte från
råbas-, lane- eller page-val.

Registerbasfiltret för triangle-LOD hade samtidigt ett diagnostikfel: när
filtret var aktivt returnerade beräkningen `-1` för andra texturer och ändrade
renderingen. Filtret påverkar nu endast loggningen. Den filtrerade
f1306--f1315-körningen återger åter exakt:

```text
frameHash=0xd89cf2a9
```

Ny verifieringsbild:

```text
artifacts/gauntlet-probe/gauntdl-lod-provenance-f1315-20260723.png
sha256=95819fa6e8134bdf5549ef2945cdf2b972d8ff77d2586a8efb07ff86ed59bb69

/tmp/gauntdl-lod-provenance-f1315.warm
sha256=1e1c602f72ac99f8c11963e6463a564469339c4f8be57af82ec5e6025689c858
```

Bilden innehåller nu tydligt sammanhängande 3D-geometri: golv-/väggytor och
ett centralt objekt. Geometrin är alltså riktig spel-/scengeometri, men
YIQ/NCC-färgerna är fortfarande kraftigt brusiga. Nästa smala gräns är att
jämföra den valda TMU0/TMU1-NCC-tabellen och två-TMU-combinern mot MAME för
dessa exakta drawpaket. Återöppna inte QIO, Type5-body, råbasen eller
framebuffer-vändning utifrån denna scen.

### 2026-07-23: draw-buffer 0 innehåller en riktig nivåscen

MAME-jämförelsen hittade ett konkret Type3-fel. Paketet har separata
`W0/S0/T0`- och `W1/S1/T1`-iteratorer. MAME låter `Wb` mata båda TMU:erna,
låter `W0` först mata båda och skriver sedan eventuellt över TMU1 med `W1`;
`S0/T0` och `S1/T1` följer samma arv. Vår decoder kastade däremot bort
`S1/T1`, och två-TMU-combinern samplade båda TMU:erna med TMU0:s iterator.

`SetupVertex` och MAME-gradientvägen bevarar nu båda koordinatuppsättningarna.
Den default-off diagnostiken är:

```text
EUTHERDRIVE_GAUNTDL_EXPERIMENT_VOODOO_TYPE3_SEPARATE_TMU_ST=1
```

Med två-TMU-combinern aktiv ändrar splitten den dolda draw-buffer 0 och minskar
nollsamplingarna:

```text
shared S/T:   zero=379592  buffer0 sha256=1541a0dee63d112f4aedae5d15f9cd68003aee666674b6dd56d3d6fa0e3170d9
separate S/T: zero=379288  buffer0 sha256=2859ec2c838704c2610b3380404c173d97224ac8602f4a22c535714b1170d662
```

Den exporterade f1315-frontbufferten är byteidentisk i båda fallen
(`frameHash=0xeeebf255`), eftersom drawsen sker i buffer 0 efter den senaste
swapen medan frontbuffer 1 fortfarande visas.

Det viktigaste fyndet är att single-TMU-kontrollen i buffer 0 inte är brus:
den visar en sammanhängande Gauntlet-nivå med stenarkitektur, trappor,
golvtextur och scenobjekt. Detta är den tydligaste riktiga spelgrafiken hittills:

```text
artifacts/gauntlet-probe/gauntdl-real-scene-draw-buffer-f1315-20260723.png
sha256=cb17d3da2b18626ce29887db46ac9e9e35c178c2dc8ba1ed52065998413ad2b8
```

Två-TMU-resultatet lägger fortfarande trasigt TMU1-material över samma
geometri. Splitten är hårdvarukorrekt men ska därför förbli default-off tills
TMU1:s register-/minneskälla är verifierad. Nästa gräns är:

1. följ den exakta TMU1-basen och dess Type5-ägare för samma scene draws,
2. avgör varför en vblank-synkad swap ligger pending utan att dräneras
   autonomt,
3. visa buffer 0 via en riktig Voodoo-swap, inte genom att tvinga
   `ChooseRenderBufferIndex`.

Ett diagnostiskt försök att dränera pending swap vid varje host-`RenderFrame`
testades och togs bort. Det roterade enligt MAME till den tomma tredje bufferten
när `fbiInit2` hade gått över till triple buffering och var därför ingen giltig
exportfix.

### 2026-07-24: exakt TMU1-sampling träffar ett oskrivet kallt fönster

En fokuserad samplertrace följer nu båda TMU:erna efter LOD-, koordinat- och
bankberäkning:

```text
EUTHERDRIVE_GAUNTDL_TRACE_VOODOO_TWO_TMU_SAMPLES=1
EUTHERDRIVE_GAUNTDL_TRACE_VOODOO_TWO_TMU_SAMPLES_LIMIT=64
EUTHERDRIVE_GAUNTDL_TRACE_VOODOO_TWO_TMU_SAMPLES_MIN_FRAME=0
EUTHERDRIVE_GAUNTDL_TRACE_VOODOO_TWO_TMU_SAMPLES_TMU0_BASE=0x29504
```

För den riktiga scenens paket `0x00c2a10b` är registerparen:

```text
TMU0 mode/lod/base=8C22410F/00002604/00029504
TMU1 mode/lod/base=8C241ACF/00200104/001B1EC4
```

De separata `S0/T0`- och `S1/T1`-iteratorerna ger giltiga men mycket olika
koordinater. En representativ pixel vid `(504,176)` läser:

```text
TMU0 lod3 addr=0x15faa0 raw=0x001e rgba=10110bff
TMU1 lod1 addr=0x5a0bcc raw=0x0000 rgba=000000ff
```

Alla 32 fokuserade samplingar läser varierande, icke-noll data från TMU0 men
noll från TMU1 kring `0x5a0bxx`. En separat råkontroll av TMU1-ytans
64x128-fönster visar `8192/8192` nollord och ett enda unikt värde. Samma
lokala fönster i TMU0 har `8192/8192` icke-nollord och 419 unika värden.

Den gamla default-off-kausalproben
`EUTHERDRIVE_GAUNTDL_EXPERIMENT_VOODOO_TMU1_SAMPLE_TMU0_MEMORY=1` flyttar
exempeladressen till `0x1a0bcc`, där `raw=0x3b80`. Den minskar två-TMU-körningens
nollsamplingar från 379592 till 233093 och ändrar f1315 från `0xeeebf255` till
`0xf6ece2aa`, men ger fortfarande felaktigt/repetitivt material. Den ska därför
inte promoteras.

Type5-uppladdningstracen visar inga skrivningar till TMU1:s fysiska
wordintervall `0x168000..0x16ffff` under vare sig f900--f1080 eller
f1080--f1160. Inte heller det lokala TMU0-spegelintervallet
`0x68000..0x6ffff` skrivs efter f900. Ytan är alltså äldre än
world-/nivåövergången; nästa orakel måste tas ur den genuint kalla
tvåbankslinjen före f600. Återöppna inte S/T-dekodern, LOD-valet eller
world-loadern för just detta nollfönster.

Den default-neutrala single-TMU-regressionen från f1306 till f1315 är fortsatt:

```text
frameHash=0xd89cf2a9
```

#### Korrigering: TMU0-spegeln är en annan textur, inte en felbankad TMU1-upload

En snapshotbisekt och fokuserad Type5-trace korrigerar aliasprobens tolkning.
Fönstret är tomt i båda bankerna vid f600, f740 och f770. Mellan f770 och
f900 fylls endast TMU0:s lokala fönster. De 85 Type5-paketen säger
uttryckligen `tmu=0` och använder:

```text
tmode=00000900 tlod=00000804 tbase=0002F44C
target=0x00a980..0x01139f
physical=0x67d58..0x68d97
producer PC=0x800fe7a0..0x800fe7cc
```

Detta är alltså en legitim TMU0-textur som råkar överlappa samma lokala
RAM-offset. Den är inte innehållet som TMU1-base `0x1b1ec4` skulle ha pekat
på. `TMU1_SAMPLE_TMU0_MEMORY` visar därför mer färg av en slump och får
fortsatt inte användas som fix.

Det tomma TMU1-valet finns däremot i ett verkligt gästrecord. Vid f1306 ligger
den exakta tripeln i recordet kring `0x804b9430`:

```text
base=001B1EC4 mode=8C241ACF lod=00200104
```

Recordserien innehåller baserna `0x1b1e04`, `0x1b1e44`, `0x1b1e84` och
`0x1b1ec4`, alltså steg om `0x40` registerenheter eller 512 byte. MAME:s
LOD-formel verifierar ändå den aktiva samplingens extra `0x10000` byte:
`lod_min=lod_max=4` väljer LOD1, format 10 är RGB565 och LOD0-storleken är
128x256x2 byte. Både råbasen `0x58f620` och LOD1-basen `0x59f620` är tomma,
så detta är inte ett felaktigt LOD-offsetval.

Hela 0x300-byteblocket kring recordet är byteidentiskt i följande kedja:

```text
f770:  0x804d87e0
f975:  0x804d87e0
f980+: 0x804b9300
```

Vid f980 har listan endast kopierats till en ny adress; materialvalet är
oförändrat. Blocket finns inte vid f740 men är komplett vid f770. Vanlig
frameprogression och en ny Turbo-puls från den sparade f740-filen reproducerar
inte den äldre extra-CPU/fastpathgren som skapade f770-checkpointen. Nästa
smala gräns är därför att återskapa just den ursprungliga f740--f770
extra-step-sekvensen eller spåra recordbyggaren när den skriver
`0x804d87e0..0x804d8ae0`. Ändra inte TMU-bankmappningen eller lägg till en
zero-texel-fallback utifrån aliasbilden.

#### Historisk f740--f770-reproduktion hittar recordbyggarens kopieringskedja

Den exakta äldre körningen har nu reproducerats i en isolerad checkout av
commit `b2e954f1`, med f740-snapshoten, 200000 CPU-steg per frame och
`EUTHERDRIVE_GAUNTDL_EXPERIMENT_VOODOO_MAME_TWO_TMU_COMBINE=1`. Den ger åter
det historiska f770-oraklet:

```text
frameHash=0x25e82a6d
texWrites=1247022
```

Samma snapshot och parametrar på nuvarande HEAD ger `frameHash=0x6540de2f`.
Det förklarar varför den tidigare skapelsegrenen inte gick att fånga genom att
bara återanvända f740-checkpointen i dagens kod.

En stegoberoende värdeövergångsvakt visar att renderlistordet
`0x804d8910` ändras från `0x3f0d0000` till `0x001b1ec4` av:

```text
writerPc=0x800d1370
nextPc/return=0x800f7838
destination=0x804d7568
source=0x8029e550
byteCount=0x2000
```

Det är den kända snabbvägen för en 8-byte-justerad blockkopia. Den exakta
TMU1-posten kommer från:

```text
0x8029f8f8: 001B1EC4 8C241ACF
0x8029f900: 00200104 42B40000
```

och kopieras till:

```text
0x804d8910: 001B1EC4 8C241ACF
0x804d8918: 00200104 42B40000
```

Källordets övergångsvakt rapporterade först `writerPc=0x800f69b0`, men en
PC-filtrerad CPU-trace korrigerar den tolkningen. Instruktionen där är ett
`sb` till Voodoo-MMIO-adressen `0xa40001fX`, inte till käll-RAM. PC-värdet är
bara den senast exekverade gästinstruktionen när en asynkron enhetskopia sker.

Den riktiga producenten syns med den centrala minnestracen:

```text
EUTHERDRIVE_GAUNTDL_TRACE_MEM=1
EUTHERDRIVE_GAUNTDL_TRACE_MEM_WRITES_ONLY=1
EUTHERDRIVE_GAUNTDL_TRACE_MEM_ADDRESS=ffffffff8029f8f8:0xc
```

Träffen är `devicecopy`: IDE bus-master DMA återanvänder bufferten
`0x8029e550` och skriver först `0x1ab0` byte och sedan `0x550` byte. Det
relevanta blocket är:

```text
IDE read sectors lba=314923 count=16
DMA first PRD: destination=0x8029e550 count=0x1ab0
buffer offset=0x13a8 -> disk byte offset=0x099c69a8
```

Rådisken innehåller exakt samma ord vid `0x099c69a8`:

```text
001B1EC4 8C241ACF 00200104 42B40000
```

TMU1-state är alltså förbyggd data i spelassetens diskstream, som DMA-kopieras
till en temporär buffert och därefter in i renderlistan. Den uppstår varken i
en RAM-recordbyggare eller genom fel bankmappning i Voodoo-koden. Att samma
DMA-buffert skrivs över många gånger förklarar också varför ett enstaka
värdeövergångs-PC var missvisande.

`EUTHERDRIVE_GAUNTDL_TRACE_MAIN_RAM_WRITES` täcker nu också
`TryFastPathKnownRuntimeAlignedQwordCopy` och loggar
`kind=fast-aligned-qword-copy` med exakt källa, destination, gammalt och nytt
64-bitarsvärde. Nästa smala gräns är nu kallstartens riktiga Type5-historik
före f600: hitta om diskassetets textur för basfamiljen
`1b1e04/44/84/c4` laddas till TMU1 och tappas i paketavkodningen, eller om
den aldrig skickas av den nuvarande gäst-/fastpathlinjen. Rasterizer-,
LOD- och bankmappningen ska inte ändras före den kontrollen.

#### Komplett Type5-historik och MAME:s swapsemantik blottar riktig spelgeometri

Den historiska f740--f770-körningen loggades därefter utan den tidigare
2000-paketsgränsen. Den innehåller totalt 8386 Type5-textursekvenser:

```text
TMU0: 6051 sekvenser, physical word 0x03e46e..0x05d349, 40 tbase-värden
TMU1: 2335 sekvenser, physical word 0x130dec..0x143aab, 30 tbase-värden
```

Det finns noll skrivningar i TMU1:s fysiska wordfönster
`0x160000..0x16ffff`, inklusive målområdet kring `0x167d88`. Den aktiva
LOD1-samplingen vid byteadressen omkring `0x59f620` kan alltså inte ha fått
sin data under den historiska f740--f770-grenen. Samtidigt visar MAME:s
texture equation-dekod att TMU1-läget `0x8C241ACF` producerar sin lokala
RGB565-texel och att TMU0-läget `0x8C22410F` multiplicerar med resultatet
nedströms. TMU1 är därför verkligt aktiv; nollan är inte ett pass-throughläge.

En tillfällig neutral-vit substitution för endast TMU1-base `0x1b1ec4`
träffade de exakta samplingarna men gav samma f1315-hash och byteidentisk
bild som baslinjen:

```text
frameHash=0x128bbd84
```

Den togs bort. Den utökade samplingen förklarade varför försöket inte syntes:

```text
frame=1325 buf=0 xy=504,176
fbzMode=0x00000460 fbzColorPath=0x0C60743A
TMU0 addr=0x15faa0 raw=0x001e
TMU1 addr=0x59f69e raw=0x0000
```

`fbzMode=0x460` aktiverar både RGB- och auxskrivning och väljer frontbuffer.
Drawen skrev alltså verkligen färg till buffer 0. Tre renderframes senare
kom denna swap:

```text
swapbufferCMD=0xFFEEDDCC
pre front=0 back=1
post front=1 back=0
```

Den lokala implementationen tolkade bit 6 som `clearBackBuffer` och rensade
därmed buffer 0 direkt efter rotationen. MAME visar att
`swapbufferCMD`-bitarna 1--8 i stället är vblank-väntantal; kommandot har
ingen clear-back-semantik. Den felaktiga rensningen är nu borttagen.

Vid f1315 visas fortfarande buffer 1 och resultatet är därför oförändrat.
Efter nästa swapgräns vid f1330 ändras däremot resultatet reproducerbart:

```text
före: frameHash=0x128bbd84
efter: frameHash=0xea6e797a
```

Den nya bilden visar en sammanhängande riktig 3D-arena med golv, väggar,
scenobjekt och pelare över nästan hela 640x480-ytan. Texturmaterialet är ännu
kraftigt korrupt, men detta är första gången den bevarade scenen roterar fram
via Voodoos riktiga bufferlivscykel:

```text
artifacts/gauntlet-probe/gauntdl-no-false-swap-clear-f1330-20260724.png
sha256=db4b5fcc68b258f968d46158401c6ad70dac040aed900f0543ceb57a47b6dbd0

artifacts/gauntlet-probe/gauntdl-no-false-swap-clear-f1330-20260724.warm
sha256=4633a76fe6bfeb67b4a502861afa787e1286e157608fb77d07db8d0bb37c8612
```

Snapshoten laddar på under en sekund med `ranFrames=0` och återger exakt
`frameHash=0xea6e797a`. Fortsätt därför från f1330-snapshoten. Nästa smala
gräns är att spåra de senare direkta `swapbufferCMD=0`-anropen och den
felaktiga triple-bufferrotationen till buffer 2, och därefter isolera
texturkorruptionen i den nu synliga arenan. Återinför inte swap-clear och
använd inte bufferheuristik för att dölja rotationsfelet.

#### Statuspollningen dränerade två swaps under samma vblank

En signutökad CPU-trace kring `0xffffffff80105eac` visar att de två
`swapbufferCMD=0`-händelserna vid renderframe 1340 inte kommer från två
gästskrivningar. Instruktionen är:

```text
80105ea0: lui v0,0x8026
80105ea4: lw  v0,0x2c8c(v0)
80105ea8: lw  v0,4(v0)
80105eac: lw  v0,0(v0)
```

De två anropen, med returadresser `0x800a852c` och `0x800a8534`, läser
Voodoo-status. Den lokala modellen växlade vblank-biten på varje statusläsning
och dränerade en pending swap som en sidoeffekt. Två pollningar kunde därför
rotera `front=1 -> 2 -> 0` under samma emulerade vblank.

MAME:s `reg_status_r` är passiv. En väntande swap utförs i stället en gång
från `vblank_start`, varefter nästa FIFO-swap kan behandlas. Fixen
`EUTHERDRIVE_GAUNTDL_FIX_VOODOO_MAME_VBLANK_SWAP_TIMING=1` flyttar därför
dräneringen till maskinens host-vblank och är nu standard i
`run-gauntdl-baseline.sh`. En ny `swap-pending`-trace loggar dessutom
enqueue-värde, väntantal och tidigare ködjup.

Vid samma f1300--f1325-körning får buffer 2 nu en hel host-frame mellan
rotationerna och växer från 441 till 146285 icke-nollpixlar. Triple buffering
är alltså verklig, inte i sig ett fel. Vid f1330 ger den korrigerade timingen:

```text
frameHash=0x11ecb97a
front/back/count=1/2/3
pending=0
```

De tre buffertarna innehåller tre olika sammanhängande delar av samma
3D-scen: mörk arkitektur, ett perspektiviskt texturgolv och den blå arenan.
Nästa gräns är inte att stänga av triple buffering, utan att följa
`swap-pending`-kommandona och avgöra varför kompletta framekompositioner ännu
är splittrade mellan buffertarna. Statusläsningar får inte åter börja utföra
swaps.

#### Pending-kön måste bevara Voodoo 2:s bit 9

Den första `swap-pending`-tracen gav:

```text
packet=0x3dc211ec words=9 value=0x3ea466f9 vblankWait=124
packet=0x3f948254 words=11 value=0x4014ccdf vblankWait=111
```

Den gamla kön sparade bara antalet väntande swaps. Vid vblank utförde den
sedan `ExecuteSwapBuffers(0)`, vilket tappade originalkommandots bit 9.
Dessutom var `dont swap` felaktigt villkorad av den alternativa
MAME-command-FIFO-modellen trots att Gauntlets Voodoo 2 alltid implementerar
biten.

Pending-kön sparar nu varje originalkommando och warm-snapshotformatet är
version 10. Voodoo 2-bit 9 respekteras oberoende av FIFO-experiment. Samma
f1300--f1330-trace visar den avsedda skillnaden:

```text
0x3ea466f9: dont=1, front/back förblir 1/0
0x4014ccdf: dont=0, front/back roterar 1/0 -> 2/0
```

Det tar bort den otillåtna extra rotationen. f1330-resultatet blir:

```text
frameHash=0x8d2177e6
```

Bilden är visuellt vitare och mindre komplett än den tidigare blå arenan.
Det ska inte döljas: den blå kompositionen berodde delvis på den felaktiga
swapen. Den nya checkpointen är reproducerbar och snapshoten rundtrippas med
`ranFrames=0` och samma hash:

```text
artifacts/gauntlet-probe/gauntdl-voodoo2-dont-swap-f1330-20260724.png
sha256=745fc5e4fc992084cc494d6e33810d99efb5ff739b7baa461a8066d651089e27

artifacts/gauntlet-probe/gauntdl-voodoo2-dont-swap-f1330-20260724.warm
sha256=a8ffb151f27224c2cc3b153024bb62192aaaa8fa21ab9a758652629b67f3065a
```

Nästa smala gräns är nu Type4-paketen `0x3dc211ec` och `0x3f948254`.
Swapvärdena ser ut som flyttals-/rasterpayload, precis som de omedelbara
värdena `0xffeeddcc` och `0x43b65566`. Kontrollera paketägarskap, mask och
källa innan fler bufferändringar görs; de kan vara fellästa payloadord snarare
än genuina swapkommandon.

#### Type4-träffarna var Type3-vertexpayload

Den riktade packet-ownership-tracen bekräftade att de misstänkta
Type4-kommandona skrivs av Gauntlets vertexemitter. Exempelvis skrivs
`0x3f2bd1ec` från `swc1` vid `0x800bcaa0`, inte av en registerpacket-rutin.
Ett 32-ords bakåtfönster gav den exakta gränsen:

```text
0x0080a853  Type3 continue, 1 vertex, 7 ord totalt
0x4386961b  payload 1
0x43bbf012  payload 2
0x42cc0000  payload 3
0x3b55861e  payload 4
0x3f2bd1ec  payload 5; tidigare felläst som Type4
0x00000000  payload 6
```

MAME:s Voodoo 2-formel ger också exakt sex payloadord för `0x0080a853`.
Paketlängden var alltså rätt; den separata producenttrackern hade tappat
synkronisering och lät ett flyttal starta ett nytt packet.

Gauntlets två verifierade heltals-store-PC:n, `0x800bc8ec` och `0x800bc91c`,
är nu explicita Type3-headerankare. Float-store-PC:n får fortsätta en aktiv
body men inte återförankra den. Baseline aktiverar därefter
`EUTHERDRIVE_GAUNTDL_EXPERIMENT_VOODOO_FIFO_ADVANCE_TYPE3_PRODUCER_BODY_HEADER`
och hoppar över resten av en känd Type3-body om read-pekaren hamnar mitt i
den. Vid f1300--f1330 dekodas inga av de fyra kända falska Type4-värdena:

```text
0x3dc211ec
0x3f948254
0x3f2bd1ec
0x07f3f9fc
```

Resultatet är en sammanhängande perspektivisk arena med golv, bro, pelare och
port:

```text
frameHash=0x7702569b
framebuffer=640x480 nonBlack=307200 colored=181941
artifacts/gauntlet-probe/gauntdl-type3-explicit-header-f1330-20260724.png
sha256=98924ea7aa22fc6928813b16ab996c3fbf7dd3c2eeaf764ef8bb37832ecb92d5
artifacts/gauntlet-probe/gauntdl-type3-explicit-header-f1330-20260724.warm
sha256=33a283878c5104c87e1aac7b56cb7d2e54694613f18b3849c72617a129b6994c
```

Nästa gräns är nu de vita/oklara ytorna i höger- och nederkant. FIFO-paketens
komposition är tillräckligt stabil för att gå vidare till rasterklippning,
clear-rektangel och frame-buffer-ursprung utan fler swapheuristiker.

#### Voodoo medium-res är 512x384, inte 640x480

Den stora vita nederkanten började exakt på rad 384. MAME:s Voodoo-modell
startar med `m_width=512` och `m_height=384`, och Gauntlets DIP-tabell anger
`Medium Res 512x384`. Vegas-driverns 640x480 är bara en startstorlek innan
Voodoo programmerar om skärmtimingen.

Vår warm snapshot har däremot `videoDimensions=0`, så exporten fortsatte att
kopiera 640x480 råa framebufferpixlar. Baseline-fixen
`EUTHERDRIVE_GAUNTDL_FIX_VOODOO_MAME_MEDIUM_RES_OUTPUT=1` läser nu den verkliga
512x384-ytan och skalar den till appens 640x480-mål. Rasterdata och snapshot
ändras inte; endast presentationen korrigeras.

Snapshot-rundtrippen ger:

```text
ranFrames=0
frameHash=0x04ea0636
framebuffer=640x480 nonBlack=307200 colored=283160

artifacts/gauntlet-probe/gauntdl-medium-res-output-f1330-20260724.png
sha256=e42997f47982c929544d24d5e22f25e8ddba192c3b64cd26090e07965a5317ba
```

Den falska vita neder- och högerkanten är borta. Kvarvarande små vita hål
ligger inne i scenens geometri; nästa gräns är därför saknade
polygoner/texturprov eller depth/clip, inte outputdimensionerna.

#### De vita hålen är otäckta clearpixlar, inte depth-reject

Connected-component-mätning av medium-res-bilden lokaliserade bland annat ett
vitt område kring output `(82,129)-(107,153)`, motsvarande ungefär
512x384-källpixel `(80,112)`. Pixel-last-writer-profilen visar:

```text
b0@80,112 = fill/fastfill color=0xffff
pc=0xffffffff801027cc
command=0x0104824c
packet=0x028b3cc4
```

`0x0104824c` är ett genuint Type4-paket med basregister `0x49`
(`fastfillCMD`). MAME:s `reg_fastfill_w` använder uttryckligen `color1`, precis
som den lokala modellen; vit färg ska därför inte ersättas heuristiskt.

En ny riktad `VOODOO-TEXPIXEL`-trace kan följa texture-write och depth-reject
för en vald rasterpixel:

```text
EUTHERDRIVE_GAUNTDL_TRACE_VOODOO_TEXTURE_PIXEL_X=80
EUTHERDRIVE_GAUNTDL_TRACE_VOODOO_TEXTURE_PIXEL_Y=272
EUTHERDRIVE_GAUNTDL_TRACE_VOODOO_TEXTURE_PIXEL_LIMIT=120
```

Raster-Y 272 motsvarar buffer-Y 112 med det aktiva 384-origoflippet. Ingen
texturerad triangel når den punkten och inga depth-rejects loggas. Att slå av
setup- eller bulk/direct-suppressions är pixelidentiskt (`0x04ea0636`), och
det äldre `SUPPRESS_WHITE_FASTFILL_AFTER_RASTER`-experimentet är också
pixelidentiskt här. Nästa smala gräns är därför paket-/vertexsekvensen för
geometrin runt öppningarna, inte clearfärg, depth-test eller de två
triangel-suppressionsskydden.

En kompletterande bbox/edge-trace runt samma punkt gav åtta kandidat-
trianglar. De två slutliga intilliggande trianglarna möts vid ungefär
`(86.25,270.94)`, men punkt `(80,272)` ligger matematiskt utanför båda:

```text
area=494.648 edge=-621.008/487.812/627.844
verts=(72,384)/(86.25,270.938)/(67.625,384)

area=-936.082 edge=-44.520/-1519.406/627.844
verts=(72,384)/(86.25,270.938)/(93.812,276.625)
```

Det vita området är alltså öppen modell-/bakgrundsgeometri och inte en
förlorad pixel. Fortsatt bringup ska inte lägga fler heuristiker på detta hål.

#### Koherent bild finns kvar när frontbufferten blir brusig

En fortsättning f1330--f1360 gav först den brusiga presenterade hashen
`0x85246f1f`. En explicit dump av alla tre färgbuffertar visade den verkliga
gränsen:

```text
front/back=1/0
buffer 0: sammanhängande borggård, mark, väggar och objekt
buffer 1: RGB-brus över grå mark
buffer 2: tom
```

Buffer 0 ger `frameHash=0x01012c83` och finns sparad som:

```text
artifacts/gauntlet-probe/gauntdl-coherent-buffer-f1360-20260724.png
sha256=d91d34eea78b8cb55ae0a0532e8664d6ad783f9ae04b3f5e6641665759ca05a1
```

Displayvalet mäter nu grova horisontella RGB565-språng på den verkliga
512x384-ytan. Det behåller korrekt front 0 vid f1330 (`d289` mot `d5346`) och
väljer den koherenta back 0 vid f1360 (`d380` mot `d1382`). Detta är ett
skydd mot att publicera en uppenbart trasig sida, inte den slutliga lösningen:
vid f1390 är buffer 0 fortfarande samma scen medan buffer 1 fortsätter ritas.

`EUTHERDRIVE_GAUNTDL_EXPERIMENT_VOODOO_FORCE_RENDER_BUFFER_INDEX=0|1|2`
kan användas för direkta buffer-A/B-dumpar.

#### Type4-body-svans dekodades som ett nytt registerpaket

Vid f1390 hade `lfbMode` blivit MIPS-instruktionsordet `0x8c241acf`.
Packet-ownership-tracen lokaliserade den första felaktiga skrivningen:

```text
false header=0x07f3f9fc packet=0x0297d8a4
real header=0x07ff964c, 13 words
false header producer pc=0x800c6e10
```

`0x07f3f9fc` är ord 9 i den riktiga Type4-kroppen. En tillfällig ogiltig
ringbufferlucka gjorde svansen läsbar som ett nytt paket innan dess riktiga
header. Samma producent-body-skydd som redan används för Type3 finns för
Type4 och är nu aktiverat i baseline:

```text
EUTHERDRIVE_GAUNTDL_EXPERIMENT_VOODOO_FIFO_ADVANCE_TYPE4_PRODUCER_BODY_HEADER=1
```

På f1360--f1390 behåller det de giltiga registren:

```text
fbzMode=0x00000060
lfbMode=0x0182a053
false packet count removed from the path
```

Den framtvingade buffer-1-bilden får fler riktiga objekt och väggfragment,
men övre bakgrunden är fortfarande RGB-brus. Nästa gräns är därför tidigare
korruption/livslängd i buffer 1 före f1360, alternativt nästa återställning av
den sidan. Fortsätt bakåt från den första koherensökningen eller framåt tills
buffer 1 får en full clear; behåll Type4-skyddet och verifiera rörlig bild
innan displayheuristiken betraktas som färdig.

En fortsatt f1390--f1420-körning visar att Type4-skyddet är nödvändigt men
inte tillräckligt. Ett senare gränstapp korrumperar åter registren:

```text
fbzMode=0x3b554890
lfbMode=0x3ed54890
front/back=1/0
frameHash(buffer 1)=0x9cca3449
```

Nästa spårning ska därför rikta register `0x44,0x45` över just
f1390--f1420 och identifiera den första falska packet-headern på samma sätt
som `0x07f3f9fc`; anta inte att den kvarvarande korruptionen bara är gammal
framebufferdata.

Spårningen identifierade falsk header `0x3ed569ec` vid packet
`0x0298b374`. Ordet skrivs av vertexemittern vid `0x800bca50` och ligger i
kroppen till det giltiga tre-vertex-paketet `0x0080a8cb`, vars header skrivs
vid det redan kända ankaret `0x800bc91c`.

Den kvarvarande Type3-trackern föll på att emittern skriver adresserna
`...b354` och `...b350` i omvänd ordning. Trackern kräver därför inte längre
strikt stigande store-adresser; alla ord inom den explicita headerns beräknade
cirkulära body-intervall märks som body. Samma f1390--f1420-körning behåller
nu:

```text
fbzMode=0x00000060
lfbMode=0x0182a053
drawPackets=282417
textured covered/rejected=1005/230
frameHash(buffer 1)=0x54e8c216
```

Buffer 1 visar nu betydligt mer riktig och förändrad borggårdsgeometri, men
har fortfarande RGB-brus i otäckta ytor. Den rörliga renderkedjan fortsätter
alltså framåt; nästa gräns är clear-/bakgrundstäckning eller ännu tidigare
innehåll i buffer 1, inte längre register `0x44/0x45` på denna sträcka.

#### Den sena buffer-1-korruptionen var inbakad i gamla snapshots

En pixelvis differens av de äldre f1360/f1390/f1420-snapshotsen visade att
buffer 1 till stor del behöll gammalt innehåll:

```text
f1360--f1390: 92.46% identiskt
f1390--f1420: 77.27% identiskt
f1360--f1420, nedersta 220 rader: 99.89% identiskt
```

Historiska snapshots avgränsade ursprunget ytterligare. Buffer 1 var helt
svart genom f1220, men brusig vid f1245 och därefter byte-identisk genom
f1265--f1306. Den gamla f1220--f1245-kedjan hade två fastfills och två swaps.

När exakt samma f1220--f1245-sträcka kördes om med dagens Type3/Type4-body-
ägarskap inträffade ingen fastfill eller swap och buffer 1 förblev helt
svart. Den äldre brussidan skapades alltså av falska packet-headerer och ska
inte användas som fortsatt oracle.

En ny ren snapshotkedja byggdes:

```text
f1220 -> f1245 -> f1270 -> f1300 -> f1330 -> f1360 -> f1390
```

Genom hela kedjan:

```text
front/back=0/1
buffer 1 nonzero=0
swaps=7150
fastfills=443 från f1270 och framåt
```

Buffer 0 utvecklas synligt mellan bilderna och visar en sammanhängande
borggård utan RGB-brus. Vid f1390:

```text
frameHash=0x8d656cbc
drawPackets=202416
textured covered/rejected=736/218
buffer 0 nz=307200
buffer 1 nz=0
```

Ny fortsättningspunkt:

```text
artifacts/gauntlet-probe/gauntdl-clean-type3-ownership-f1390-20260724.png
sha256=033e5cd5522213cd0b01db45d5e83529901b1642d9a0bc86a694b4d795cf2aa5

artifacts/gauntlet-probe/gauntdl-clean-type3-ownership-f1390-20260724.warm
sha256=e1bb7cb1a89e6cceb0cea5d843dd963a82df40a12734b89f1182d58f8b062e23
```

Kvarvarande synliga fel är nu några horisontella grå/svarta band och vita
otäckta geometriytor, inte framebufferbrus. Fortsätt från den rena f1390-
snapshoten och spåra bandens sista skrivare/clip-rektangel. Gå inte vidare
från de äldre `type3-explicit-header-f1330` eller `/tmp/...type4...`-
snapshotsen när slutlig bildkvalitet bedöms.

#### Det grå bandet är en texturerad fullbreddstriangel

En riktad pixel-last-writer på bufferpunkt `(272,144)`, motsvarande
outputpunkt ungefär `(340,180)`, identifierar bandets sista skrivare:

```text
pc=0x800c6324
command=0x00c2a10b
packet/read=0x0238c710
fbzMode=0x00000060
color=0x8c0f
```

Triangeln är exakt full medium-res-bredd och cirka 14 rasterrader hög:

```text
verts=(0,237.312)/(512,247.375)/(512,233.375)
stq=(6.028,-8.384,0.012241)/
    (21.517,-8.810,0.011477)/
    (22.749,-8.218,0.012538)
```

Den täckta pixeln samplar verklig, icke-noll texturdata:

```text
tmode=0x8c22490f
tlod=0x06002604
tbase=0x0002f050
address=0x1a246c
raw=0x4b85
rgb565=0x8c0f
```

Bandet är alltså varken fastfill, stale clear, clip-rest eller zero-texture-
fallback. S/T/Q kollapsar inte heller till en enda koordinat. Nästa smala
gräns är MAME-jämförelse av fixed-point texture fetch/LOD för just packet
`0x0238c710`, eller återställd texture-writer-proveniens för adress
`0x1a246c`. Den nya riktade pixelrapporten kan läsas med:

```text
EUTHERDRIVE_GAUNTDL_PROFILE_VOODOO_PIXEL_LAST_WRITERS=1
EUTHERDRIVE_GAUNTDL_PROFILE_VOODOO_PIXEL_SAMPLE_BUFFER=0
EUTHERDRIVE_GAUNTDL_PROFILE_VOODOO_PIXEL_SAMPLE_X=272
EUTHERDRIVE_GAUNTDL_PROFILE_VOODOO_PIXEL_SAMPLE_Y=144
```

#### MAME-per-pixel-LOD är nu aktiv även i fixed-fetch

Den officiella MAME-vägen i `voodoo_render.cpp` gör inte ett enda LOD-val per
triangel. Den räknar först `lodbase` från setup-gradienterna och gör sedan
`lod -= fast_log2(iterw, 32)` för varje pixel innan bias, clamp och mipägarskap
appliceras.

Vår per-pixel-probe hade rätt beräkning men fixed-fetch-grenen hoppade direkt
till `SampleTextureRgb565MameFixed` innan den nåddes. Därför använde den
promoterade fixed-point-vägen fortfarande centroidens triangel-LOD.
Fixed-fetch räknar nu pixel-LOD före samplingen, och LOD-basens tidigare
`Math.Log2`-approximation använder samma 7-bitars MAME-tabell som pixelsteget.
Flaggan är promoterad till bringup-baseline:

```text
EUTHERDRIVE_GAUNTDL_EXPERIMENT_VOODOO_TEXTURE_MAME_PIXEL_LOD=1
```

En ren A/B från samma f1390-state till f1400 gav:

```text
triangel-LOD frameHash=0x65f1f229
pixel-LOD    frameHash=0x0a01b41d

pixel-LOD-fördelning:
LOD0=0 LOD1=27979 LOD2=89524 LOD3=22483 LOD4=140
LOD5=8458 LOD6=219 LOD7=0 LOD8=0
```

Packet-, draw-, coverage-, zero-sample- och framebuffer-räknarna är identiska.
Pixel-diffen ligger nästan helt i den utpekade fullbreddspolygonen. Den
försvinner inte utan byter mipmönster och fortsätter visuellt i samma grå
markyta nedanför. Det gör ett falskt packet eller en fastfill mindre sannolikt;
polygonen ser nu snarare ut som en legitim perspektivkomprimerad markyta.

Ny fortsättningspunkt:

```text
artifacts/gauntlet-probe/gauntdl-mame-pixel-lod-f1400-20260724.png
sha256=d58f64d60e02fb0f9362eaa95080402450f4a47ae79b8d22c84b6574f49eb72d

artifacts/gauntlet-probe/gauntdl-mame-pixel-lod-f1400-20260724.warm
sha256=4ee1c4a15f78a6b73a3e700789e51dded52715bb2351c5356bc38a40e5e3e9ad
```

Nästa bildkvalitetsgräns bör därför flyttas från det grå “bandet” till de
regnbågsfärgade/vita objektytorna i övre halvan. Jämför där MAME:s val mellan
minification- och magnification-filter samt TMU0/TMU1 color combine; jaga inte
bandet som framebuffer- eller FIFO-korruption utan ny motbevisande trace.

#### Två-TMU-combinern återställer scenmaterialen

En riktad writer-/samplertrace på bufferpunkt `(368,48)`, rasterpunkt
`(368,336)`, fångade den färgkorrupta pelarfamiljen. Single-TMU-vägen läste
`textureMode=0x80000009`, alltså punktfiltrerad RGB332-data, och visade den
lokala mellantexturen direkt som regnbågsfärger. Minification/magnification-
filter är därför inte orsaken.

MAME:s riktiga TMU1 -> TMU0-kedja ger i stället för samma materialfamilj:

```text
TMU0 mode/lod/base=8C22490F/06002604/0002C5A8
TMU1 mode/lod/base=8C241ACF/00200104/00224944

TMU0 lod2 addr=0x18b3fe raw=0x7484 rgba=83767373
TMU1 lod1 addr=0x53579e raw=0x0025 rgba=00035aff
combined rgba=00012873
```

TMU1-proverna är varierande och icke-noll. Visuellt ersätts de stora
regnbågsbalkarna av sammanhängande mörka metallpelare, räcken och korrekt
modulerade scenytor. En ren f1390 -> f1400-A/B ger:

```text
single TMU                 frameHash=0x0a01b41d zero=9951
two TMU, ärvd S/T          frameHash=0x5dd9800f zero=11349
two TMU, separat S0/T0-S1/T1 frameHash=0x53c06d66 zero=11349
```

Den separata S/T-proben skapar fortfarande stora upprepade gula texelgrupper
i den nedre markytan. Den är därför fortsatt default-off även om separata
iteratorer finns i hårdvaran; dess packet-/arvsdecode måste verifieras innan
den kan användas. Två-TMU-combinern med den befintliga ärvda koordinatvägen
är däremot promoterad till adapter- och probe-baseline:

```text
EUTHERDRIVE_GAUNTDL_EXPERIMENT_VOODOO_MAME_TWO_TMU_COMBINE=1
EUTHERDRIVE_GAUNTDL_EXPERIMENT_VOODOO_TYPE3_SEPARATE_TMU_ST=0
```

Ny fortsättningspunkt:

```text
artifacts/gauntlet-probe/gauntdl-two-tmu-f1400-20260724.png
sha256=a6b0b2805a822803101fa2d49534486223501612beeee776161d31e6f8a57cfe

artifacts/gauntlet-probe/gauntdl-two-tmu-f1400-20260724.warm
sha256=780d7dfe668c2cb4697e6a65890e75062fddd5a93e6dfc6dba57540864e4de9b
```

Nästa smala bildgräns är den separata ST1-arvsregeln: avgör per Type3-mask om
TMU1 ska ärva Wb/W0/ST0 eller använda egna W1/ST1. Använd den blå sammanhängande
markytan som kontroll och avvisa varianter som återinför de gula repetitiva
grupperna.

En efterföljande exakt golvtrace på packet `0x01c2a10b` bekräftar att bit 17
verkligen bär separata ST1-värden och att de avkodas i samma ordning som MAME:

```text
pixel=(240,144)
ST0/Q=(23.849,0.134,0.027587) ... (8.476,-7.210,0.014349)
ST1/Q=(1.238,2.403,0.027587)  ... (1.851,0.863,0.014349)
TMU0 lod2 addr=0x18b078 raw=0x7588
TMU1 lod1 addr=0x5366f4 raw=0x002c
combined rgba=00014677
```

TMU1-träffen är icke-noll och ger mörkblått resultat vid den riktade pixeln.
Det räcker inte för att förklara eller avfärda alla gula grupper i den
separata-ST-bilden. Två-TMU-tracen har därför fått valfria X/Y-filter:

```text
EUTHERDRIVE_GAUNTDL_TRACE_VOODOO_TWO_TMU_SAMPLES_X=240
EUTHERDRIVE_GAUNTDL_TRACE_VOODOO_TWO_TMU_SAMPLES_Y=144
```

Nästa pass bör rikta filtret mot en faktiskt gul pixel och jämföra dess
TMU1-rawvärde och materialbas med en intilliggande blå pixel i samma triangel.

#### Separat ST1 är verifierad speldata

Den riktade jämförelsen träffade två punkter i samma golvtriangel och samma
Type3-paket `0x01c2a10b`:

```text
gul  xy=(192,164): TMU1 lod1 addr=0x536500 raw=0xFF68 rgba=FFF525FF
blå xy=(224,164): TMU1 lod1 addr=0x536680 raw=0x002F rgba=000470FF
```

Båda använder TMU1 `mode/lod/base=8C241ACF/00200104/00224944`. En rå dump av
den aktuella TMU1-ytan, RGB565 LOD1 vid fysisk bankadress `0x534A20`, ger en
sammanhängande `64x128` detaljtextur som uttryckligen innehåller de
gul/vita formerna i regelbundna rader. Fläckarna skapas alltså inte av
framebuffer-val, nollminne eller fel byteordning; separat ST1 placerar verklig
uppladdad texturdata.

MAME-jämförelsen matchar dessutom hela vägen för paketet: ST0 ärvs först till
ST1, bit 17 läser sedan separata S1/T1, setupgradienterna använder
`starts1/ds1dx/ds1dy`, och rastervägen kör perspektivdivision och TMU1 före
TMU0. Den tidigare delade ST-vägen dolde detaljtexturen och var inte
hårdvarukorrekt.

Separat ST1 är därför promoterad i både adapterpreset och probe-baseline:

```text
EUTHERDRIVE_GAUNTDL_EXPERIMENT_VOODOO_TYPE3_SEPARATE_TMU_ST=1
```

Ren f1390 -> f1400 och omedelbar återladdning av den nya snapshoten ger båda:

```text
frameHash=0x53c06d66
framebuffer=640x480 nonBlack=307200 colored=298167

artifacts/gauntlet-probe/gauntdl-separate-st1-f1400-20260724.png
sha256=492a11302b015ecf91da3afc9e95d44e26ec6783099945c26a763d5cf4fd5979

artifacts/gauntlet-probe/gauntdl-separate-st1-f1400-20260724.warm
sha256=47bc186f0c7a1952e3f2a55537340c46d57b30d37e87756668ded19a4232d0b9

artifacts/gauntlet-probe/gauntdl-tmu1-detail-lod1-20260724.png
sha256=7801e079fbab330cdcd5920f68f4e06c263749ad0cf78de2643b24cca1504524
```

Nästa bildgräns är därför inte längre ST1-valet. Fortsätt med materialets
texturproveniens/uppladdningslayout eller gå vidare från debugscenen mot nästa
spelstate; behåll separat ST1 på under båda spåren.

En två-frame FIRE 3-puls från den nya f1400-snapshoten lämnar objektvyn:

```text
EUTHERDRIVE_GAUNTDL_INPUT_C=1
EUTHERDRIVE_GAUNTDL_INPUT_PRESS_FRAME=1400
EUTHERDRIVE_GAUNTDL_INPUT_RELEASE_FRAME=1402
```

Vid f1405 syns en helvit övergångsbuffer. Vid f1420 har gästen ritat en ny
3D-scen (`Type3=204485`, `frameHash=0xf234b661`), men stora polygonpartier är
vita medan enstaka texturer är synliga. Det bevisar fortsatt state- och
inputprogression och flyttar nästa blockerare till den nya scenens
färg/alpha/depth-pipeline:

```text
artifacts/gauntlet-probe/gauntdl-fire3-next-scene-f1420-20260724.png
sha256=caa03fcd77d14fb1d773a97fa7f9e7158aaa11ad570881d0952994fabdb76335
```

Nästa riktade trace bör jämföra en vit pixel med en synlig texturpixel i samma
f1420-frame och fånga sista writer, `fbzColorPath`, alpha mode, depthresultat
och båda TMU-resultaten. Börja från f1400-snapshoten ovan och samma FIRE
3-puls; ST1 behöver inte återutredas.

#### Det vita är utebliven TMU1-residens, inte vita polygoner

Pixel-writer-profilen visar att de vita ytorna fortfarande ägs av den
föregående helskärms-fastfillen:

```text
kind=fill source=fastfill color=0xFFFF
pc=0x801027cc fbzMode=0x460
```

Polygonerna skriver alltså inte vitt. På nästa renderframe försöker 56
texturerade trianglar rasterisera 9038 pixlar, men 8984 sluttexlar blir noll.
TMU0 läser samtidigt varierande giltig NCC-data, medan TMU1 läser enbart noll:

```text
TMU0 base=0x000257B3 lod5 addr=0x1412xx raw=varierande
TMU1 base=0x001C3104 lod2 addr=0x62Dxxx raw=0x0000
combined=0x000000ff
```

Råminnet bekräftar att hela TMU1-området `0x600000..0x65ffff` är noll.
MAME:s Vegas-konfiguration använder 4 MiB per TMU, så detta är inte ett
två-MiB-wrapfel. `texWrites` står dessutom still på `2577260` genom
f1400--f1420; den nya scenen förutsätter att sidorna redan är residenta.

En strikt default-off diagnostik ersätter endast en svart TMU1-sample med
neutralvit före den riktiga TMU0-multiplikationen:

```text
EUTHERDRIVE_GAUNTDL_EXPERIMENT_VOODOO_TMU1_ZERO_AS_NEUTRAL_WHITE=1
```

Den är inte en hårdvarufix och ingår inte i adapter- eller probe-baseline.
Den ger däremot ett tydligt oracle:

```text
f1421: zero 8984 -> 26
f1420 helpass: zero 296786 -> 3304
colored 48233 -> 228048
frameHash=0xb67829a6
```

Nästan hela den riktiga borggården, trapporna, golvet, väggarna och objekten
blir synliga:

```text
artifacts/gauntlet-probe/gauntdl-tmu1-neutral-oracle-f1420-20260724.png
sha256=2f1f6e205684afd78c134c77a0558c16672daa5b3756bd89d61f7426a8d4f3f0
```

Nästa riktiga fix ska därför återställa de höga TMU1-sidorna före
renderfasen. Jämför kallstartens Type5/QIO-historik mot den första referensen
till basefamiljen `0x1c3104` och dess efterföljare. Promotera inte
neutralvit-proben och ändra inte MAME:s 4 MiB-minnesmask.

#### MAME-writepekaren och en enkel TMU1-basförskjutning är avförda

MAME:s `texture.write_ptr()` jämfördes mot bringup-backendens beräkning under
två verkliga uploadfönster, f740--f741 och den aktiva f759--f763-vågen.
`EUTHERDRIVE_GAUNTDL_TRACE_VOODOO_TEXTURE_UPLOAD_MAME_WRITE_PTR=1` gav noll
avvikelser. De höga sidorna skrivs alltså inte till fel adress av den lokala
Type5-pekaren; paketen som skulle fylla dem saknas.

Diskproveniensen kan nu följas ett steg längre. `death`-katalogblocket ligger
vid rådiskoffset `0x12578c00` och innehåller:

```text
inode 0x01000435  textures.rom
inode 0x01000436  anim.rom
inode 0x01000434  objects.rom
```

`textures.rom` börjar direkt efter katalogblocket vid `0x12578e00`.
`objects.rom` börjar vid `0x1257f200`; dess body vid `0x12580d08` motsvarar
byte-för-byte den laddade `AAAWHITE`/`DEATH_ARC`-källan vid `0x80562cf0`.
Materialtripplarna och texturfilen kommer alltså från samma riktiga
death-assetfamilj.

En komplett, ren f1030--f1080-Type5-trace fångade de senare
death/world-uppladdningarna:

```text
TMU0:   937 sekvenser, physical word 0x09a94a..0x09f0e5
TMU1: 11063 sekvenser, physical word 0x121d80..0x15a8e1
```

TMU1-data slutar därmed vid fysisk byteadress `0x56a387`, medan f1420-scenen
samplar runt `0x62dxxx`. Uploaden är stor och verklig men når fortfarande
inte materialfamiljen `0x1c3104/0x1c4104/0x1c5104/0x1c7604`.

En strikt default-off diagnostik gör det möjligt att flytta endast TMU1:s
samplebas utan att påverka TMU0 eller uploadminnet:

```text
EUTHERDRIVE_GAUNTDL_EXPERIMENT_VOODOO_TMU1_TEXTURE_SAMPLE_BASE_BIAS=-786432
```

Tre uppmätta kandidater testades från samma f1400-snapshot och FIRE 3-puls:

```text
bias -0x080000  frameHash=0x1864283d colored=107983
bias -0x0c0000  frameHash=0x16981f46 colored=111277
bias -0x100000  frameHash=0xc9456311 colored=109662
```

Alla tre exponerar mer färg men lägger främmande, repetitiva texturer över den
sammanhängande geometrin och lämnar stora vita hål. Den saknade residensen är
alltså inte en enda konstant basallokeringsförskjutning. Proben ska förbli
default-off.

```text
artifacts/gauntlet-probe/gauntdl-tmu1-bias-scan-f1420-20260724.png
sha256=dc2d983b052e70c644f29daefc12fddabf972178817738e9c4e274372a43e5cb
```

#### LevelE1-proveniensen korrigerar death-hypotesen

Den aktiva `0x1c31xx`-familjen kommer inte från `death`. FSYS-katalogen för
`levels/levelE1` ligger vid rådiskoffset `0x09999000` och ordnar filerna efter
inode:

```text
inode 0x01000251  objects.rom
inode 0x01000252  textures.rom
inode 0x01000253  anim.rom
inode 0x01000254  worlds.rom
```

Extentkedjan är:

```text
objects.rom   disk=0x0999f600 bytes=0x061b10
textures.rom  disk=0x09a01400 bytes=0x22a944
anim.rom      disk=0x09c2c000 bytes=0x0004e0
worlds.rom    disk=0x09c2c800 bytes=0x06d170
```

`objects.rom` finns byte-för-byte vid RAM `0x80492088`. `worlds.rom` följer
byte-för-byte vid `0x804f3b98`, och dess slut plus `anim.rom` når exakt nästa
assetgräns `0x805611e8`. World-/objektladdningen är alltså komplett; den
tidigare teorin om ett saknat `worlds.rom` är avförd.

En jämförelse av hela `levelE1/textures.rom` mot f1400:s Voodoo-minne visar
att nästan alla högentropiblock genom filoffset `0x19xxxx` redan är
materialiserade. Blocken är uppdelade mellan TMU0 och TMU1. Exempelvis
materialposterna `0x1c1104..0x1c7604` har sina primärblock vid samma lokala
`0x20xxxx..0x24xxxx`-adresser i TMU0, medan f1420 använder dessa basfamiljer
som TMU1 och samplar tomma `0x60xxxx`-adresser.

Två nya GauntletProbe-overlayverktyg är default-off:

```text
EUTHERDRIVE_GAUNTDL_EXPERIMENT_DISK_TEXTURE_COPY=disk:texture:length
EUTHERDRIVE_GAUNTDL_EXPERIMENT_TEXTURE_MEMORY_COPY=source:destination:length
```

De appliceras först efter snapshotladdning och påverkar därför inte sparade
states eller normal baseline. Två strikta negativa kontroller från samma
f1400-state gav:

```text
rå textures.rom-svans -> TMU1 0x600000:
frameHash=0x2a0d02f7 colored=47507 zero=430006

TMU0 0x200000..0x2fffff -> TMU1 0x600000..0x6fffff:
frameHash=0xaf667467 colored=49055 zero=431290
```

Båda visar enstaka riktiga modellfragment men är sämre än baseline och bevisar
att TMU1 inte ska ha en linjär råfil eller en kopia av primärtexturen. Nästa
riktiga gräns är companion-valet: följ den sekundära texture-set-post som
byggs tillsammans med en `levelE1`-materialpost och avgör varför dess separata
detail/lightmap-payload aldrig når TMU1:s lokala `0x20xxxx`-familj. Behåll
neutralvit, bias och båda overlaykopiorna som default-off oracles.

#### Objektposten är statisk och den saknade companionbanken är exakt avgränsad

En trace av de sena registerpaketen visar att `0x1c31xx`-värdena skrivs till
TMU1 av gästens vanliga descriptorloop:

```text
0x800c6c78  sw command header
0x800c6c7c  sw mode   från record +0x10
0x800c6c80  sw lod    från record +0x14
0x800c6c84  sw base   från record +0x0c
```

Det aktiva `base=0x001c3104` kommer från `v1=0x804bc964`. Det är inte ett
felbyggt runtime-record: samma ord finns byte-för-byte i
`levelE1/objects.rom` vid filoffset `0x2a8e8`. Hela materialfamiljen ligger i
statiska, normalt 0x7c byte långa objektposter:

```text
base       RAM         objects.rom
001c0104   804bc7fc    0002a774
001c1104   804bc878    0002a7f0
001c2104   804bc8f4    0002a86c
001c3104   804bc970    0002a8e8
001c4104   804bc9ec    0002a964
001c5104   804bca68    0002a9e0
001c5504   804bcae4    0002aa5c
001c6504   804bcb60    0002aad8
001c7604   804bcd74    0002acec
```

Det avför en dynamisk texture-set-lookup som orsak till just dessa basvärden.
Gästen publicerar det av spelet författade materialet oförändrat och väljer
uttryckligen TMU1.

Två kompletta Type5-spår av levelE1-vågen ger den motsatta sidan av gränsen:

```text
f1000--f1030:
  TMU0  4510 sekvenser, phys word 0x074fb2..0x09ecd1
  TMU1  1803 sekvenser, phys word 0x11bd80..0x126b61

f1030--f1080:
  TMU0   937 sekvenser, phys word 0x09a94a..0x09f0e5
  TMU1 11480 sekvenser, phys word 0x121d80..0x15c6f9
```

Den viktiga korrelationen är exakt, inte bara ett närliggande
allokeringsintervall. Med 4 MiB lokal TMU-wrap ger descriptorbas
`0x1c3104` effektiv bas `0x43104`; scenens LOD2-samplingar ligger i lokala
byteintervallet `0x22dxxx`. Den föregående uppladdningen
`tmu=0/tbase=0x434a6` fyller fysiska word
`0x8a94c..0x8be9f`, alltså lokala bytes
`0x22a530..0x22fa7f`, från source-root `0x80599fb4`. Därmed är exakt samma
lokala sampleområde:

```text
TMU0: materialiserat med varierande primärtextur
TMU1: helt noll
```

Det förklarar också varför en linjär TMU0->TMU1-kopia visar enstaka riktiga
fragment men är visuellt fel: den exponerar en verklig textur på ett
sammanfallande lokalt adressintervall, men bevisar inte att detta är
materialets separata TMU1-payload.

Nästa smala gräns är fördelningen av upload-recorden över guestens globala
8 MiB-texturallokator och den saknade source-/recordfamiljen för det statiska
TMU1-materialet. Ändra inte descriptorbasen, Type5-writepekaren, bankmasken
eller rasteriseringen för denna familj.

#### LevelE-atlasen routas bara genom den sekundära/TMU0-vägen

En source-filtrerad producenttrace binder primäruppladdningen till
texture-recordet vid `0x804aaee8` (`objects.rom +0x18e60`):

```text
record      0c010601 00800080 00147524 000434a6
            0000010f 0003d604 00000000 0022a530
source-root 80599fb4
tbase       000434a6
destination 0022a530
```

Vid `0x801096fc` anropar record-emitteraren lågnivåuppladdaren med
`a0=0, a1=0x22a530, a2=1, a3=1` och source `0x80599fb4` på stacken.
Den resulterande Type5-runnen är entydig:

```text
pc=800fe5d4
cmd=c0000105 count=32 packets=128
targetBytes=00020000 targetWord=008000
source=80599fb4
```

Det blir bara en TMU0-run. Inget senare target med TMU1-biten satt använder
samma source-root.

Den tidigare slutsatsen att source-roots `0x80591e90` och `0x80599fd8`
bevisade parade TMU0/TMU1-uppladdningar var fel. Adresserna ligger i en
återanvänd scratcharena. Exempelvis är användningen av `0x80599fd8` vid
frame 1009 ett sekundärt anrop för record `0x804aaad8`, destination
`0x205550` och `a0=0`, medan användningen vid frame 1034 är ett orelaterat
primärt anrop för record `0x80569578`, selector `0x08f748` och `a0=1`.
De två användningarna tillhör alltså olika assets.

Den korrigerade slutsatsen är fortfarande att emitterarens bankrutning
fungerar:

```text
a0=0 -> TMU0
a0=1 -> TMU1
```

En exakt call/return-trace visar dessutom att levelE1-recordet väljs av den
sekundära vägen vid `0x800a7764`, går in i upload-wrappern med `a0=0` och
kommer tillbaka vid `0x800a776c`. Under anropet ändras recordhuvudet
tillfälligt från `0x0c010601` till `0x04010601`; läsningen av `byte[3] == 4`
efter returen är därför ett processerat/pending-tillstånd, inte en
texturformats- eller TMU-selector.

Den kompletta selector-matrisen för f1000--f1030 innehåller 93 anrop:

```text
primary   43 calls, selector 0x06f600..0x09a8c8, a0=1 -> TMU1
secondary 50 calls, selector 0x1df778..0x279df8, a0=0 -> TMU0
```

Alla levelE-poster i det höga lokala intervallet, inklusive
`0x804aaee8 -> 0x22a530`, förekommer bara i sekundärlistan. Det finns inget
primärt anrop nära `0x22a530` i detta fönster. Den saknade TMU1-payloaden
undertrycks alltså inte i lågnivåemitteraren och väljs inte bort av testerna
efter `0x800a776c`; motsvarande primär record publiceras aldrig till
selectorloopen.

f1030--f1087 ger samma uppdelning:

```text
primary   507 calls, selector 0x087600..0x179b68
secondary  29 calls, selector 0x26a528..0x27c348
primary selectors >= 0x200000: 0
```

En full PC-trace av `0x800a7094..0x800a7890` korrigerar även
tvåliststolkningen. Funktionen tar en enda recordbas i `a0`, ett count i
`a3`, avancerar recordpekaren `s2` med `0x50` vid `0x800a7834` och väljer
uploadväg efter en global 8 MiB-allokatoradress i `s0`:

```text
s0 <= 0x003fffff -> sekundär call 0x800a7764, a0=0, local=s0
s0 >= 0x00400000 -> primär call 0x800a761c, a0=1, local=s0-0x400000
```

För levelE-gruppen är funktionsanropet
`a0=0x8056173c, source=0x80591e90, count=26`. Dess första record får globala
`s0=0x26a528` och gruppen gör 25 sekundära men noll primära uploads. Nästa
anrop (`a0=0x80568f88, count=138`) börjar i stället på globala `0x487600`
och gör 47 primära uploads, vars lokala selectors börjar på `0x087600`.
Bankgränsen och subtraktionen är alltså guestberäknade och konsekventa.

Det återstående felet är inte en tappad sekundär/primär listpublicering.
Antingen saknas den separata TMU1-recordfamiljen före denna funktion, eller
så har tidigare QIO-/assetlivstid lämnat den globala allokatorn med fel
innehåll/ordning. Följ därför callern vid `0x800abe10..0x800abe54`, särskilt
de dynamiska globala inputfälten `0x8020f154/0x8020f178..0x8020f184`, och
bind det statiska TMU1-materialet till sin verkliga source-record. Tvinga
inte `s0 += 0x400000`: det skulle bara lägga samma primärtextur i fel bank.

#### Exakt och nollbevarande TMU-kopia avgränsar fel payload

Den första overlaykontrollen råkade köras med en äldre Release-DLL än
`GauntletProbe/Program.cs` och applicerade därför ingen kopia. Proben byggdes
om innan följande resultat; loggen innehåller nu den explicita raden
`textureMemoryCopy`.

En exakt kopia av endast record `0x804aaee8` från TMU0
`0x22a530..0x22fa7f` till samma lokala TMU1-intervall ändrar f1421:

```text
baseline:    frameHash=0xfbce28cc  zero=305770  colored=48309
exact copy:  frameHash=0x3c1992d0  zero=304416  colored=49724
```

Den är kausal men visar bara fler feltexturerade fragment:

```text
artifacts/gauntlet-probe/gauntdl-exact-record-to-tmu1-f1421-20260724.png
sha256=35cb03a26924ba86c61843470abfcbc9170c3d69d1c17a47811f93d5cbcd4c52
```

Probe-overlayn har nu en default-off zero-destination-variant:

```text
EUTHERDRIVE_GAUNTDL_EXPERIMENT_TEXTURE_MEMORY_COPY_ZERO_DESTINATION_ONLY=1
```

Den bevarar redan residenta destinationbytes. En kontroll som fyller bara
nollor i TMU1 `0x600000..0x6fffff` från motsvarande lokala TMU0-megabyte ger:

```text
frameHash=0x13761b53  zero=294513  colored=58892
```

Mer geometri blir synlig, men atlasinnehållet och den stora gröna rampen är
fortfarande fel:

```text
artifacts/gauntlet-probe/gauntdl-tmu0-to-zero-tmu1-f1421-20260724.png
sha256=32c5a71bea4def90e4d1a3b35691d2c43eddebce93ae6bf0a1fce8d05384e931
```

Det avför både en blind bankkopia och den breda kopians tidigare
överskrivningsinvändning. Den lokala adressfamiljen är relevant, men
TMU0-innehållet är inte materialets riktiga TMU1-payload. Nästa trace ska
identifiera source-recordet som hör till Type4-materialkommandot
`0x0082a10b` (`target=0x421`, chipmask TMU1) i stället för att återanvända
TMU0-recordet.

#### Materialkommandot är nu korrekt avkodat och den verkliga TMU1-uppladdningen saknas

Den sista raden ovan innehöll en viktig feltolkning. `0x0082a10b` är inte
självt ett Voodoo Type4-paket: de tre låga bitarna är `3`, inte `4`.
Det är spelmotorns interna material-/drawopcode i `levelE1/objects.rom`.
Renderern vid `0x800c6c78..0x800c6c84` översätter posten till det verkliga
fyra ord långa Voodoo-paketet:

```text
header  0005a604
mode    8c241acf
lod     00300208
base    001c3104
```

`0x0005a604` avkodas enligt MAME som Type4, registerbas `0x4c0`, mask
`0x000b` och chipmask `4` (TMU1). Registerbankrutningen i adaptern är alltså
korrekt. Samma aktiva draw har omedelbart före paketet följande TMU0-state:

```text
TMU0 mode/lod/base = 8c22410f / 00002604 / 000257b3
TMU1 mode/lod/base = 8c241acf / 00300208 / 001c3104
```

Den tidigare kopian av TMU0-recordet `0x804aaee8`, lokalt
`0x22a530..0x22fa7f`, var därför inte bara fel payload utan också fel
TMU0-material. Den riktiga TMU0-halvan är `base=0x000257b3`. Dess Type5-
uppladdning finns i f940--f1030-spåret och kommer från source-root
`0x805b40ec`; den ger varierande NCC-texlar vid scenens samplingar.

En ny exakt kontroll körde från den rena f940-snapshoten till f1422:

```text
EUTHERDRIVE_GAUNTDL_TRACE_VOODOO_TYPE5_TEXTURE_UPLOAD_SEQUENCES=1
EUTHERDRIVE_GAUNTDL_TRACE_VOODOO_TYPE5_TEXTURE_UPLOAD_SEQUENCE_TBASE=0x001c3104

ranFrames=482
frameCounter=1422
Type5 texture sequences med tbase=0x001c3104: 0
frameHash=0x2378bfe0
```

Det stämmer med den tidigare råminneskontrollen: TMU1-samplingarna runt
fysisk `0x62dxxx` är noll och har ingen writer, medan TMU0-samplingarna runt
`0x1412xx` är varierande och icke-noll. Den saknade bilden är därmed inte
orsakad av bildvändning, Type4-avkodning, registerbank, texture-writepekare,
sampler eller rasterisering. Gästens statiska material publiceras korrekt,
men dess TMU1-payload har aldrig materialiserats i texturminnet.

#### MAME Temple-oraklet bekräftar adressen men avför stream-limit 13

Den lokala MAME-builden kan nu nå Temple utan RAM-patchar. Lua-proben matar
den inbyggda referenskaraktären `SJB/964` via vanliga arkadinputs, väljer
Temple och sparar ett gameplay-state. GDB-proben dumpar därefter båda 4 MiB
TMU-bankerna. Dumpen är stabil mellan Voodoo-update 120 och 600:

```text
TMU0 sha256=c1356470f8be70d533b867041830b644eaa0a90293cc36c1c2852fb1dcfc9899
TMU1 sha256=26aa29820758aa421017e1ab01d4530b84ac982d2ff0e872f46dd7f6a9e22d0d
```

EutherDrives TMU0 matchar MAME på 1895 justerade 256-byte-sidor utan
adressdelta. MAME:s TMU1 har samtidigt verkliga texlar vid den saknade
familjen. Exempelvis ligger aktiva `base=0x001c3104`, LOD2-samplingar i
`0x62caa8..0x62d7a6`; motsvarande lokala MAME-data är icke-noll.

Två sammanhängande rådiskprovenienser täcker just gränsen:

```text
local 0x200000..0x22c7ef <- disk 0x04492530..0x044bed1f
local 0x22c7f0..0x285acf <- disk 0x044c2240..0x0451b51f
```

En hel MAME-TMU1-overlay minskar f1420:s nollslutpixlar från `296786` till
`141517` och ökar färgpixlar från `48233` till `187965`. Den är endast ett
oracle: dumpen är ROM-härledd och får inte bli en runtime-resurs eller
checkas in.

En ren f940--f1420-körning testade därefter om den konfigurerade
`BGLOADMODEL_INDEXED_TEXTURE_QIO_STREAM_LIMIT=13` klipper companionposten.
Gränsen 27 gav ingen stream-limit-händelse, ingen ny hög TMU1-write och:

```text
ranFrames=480 runMs=901601.6
frameHash=0x2378bfe0
texture last=0x5ea1ac
zero=798824 colored=124643
```

Det avför en enkel höjning av stream-limit. Nästa gräns är i stället den
redan laddade source-owner-tabellen före f940: identifiera vilken primär
source som ska paras med levelE-sekundärrecordet och publicera just den till
selectorloopen. MAME-overlayn och neutralvitproben ska förbli default-off.

#### Index 9-ownern är korrekt men primärallokatorn stannar för lågt

F940:s source-tabell visar levelE på index 9:

```text
owner=804b1568
asset=80512da8/00000014 levels/levelE1
owner header=f00b0001 body=00061840 table=0000029e count=0000006c
```

Två A/B-prov avför `80512da8` som companion. Den befintliga
`PRESERVE_ASSET_SOURCE_INDEX_MASK=0x200`-hooken är helt inert genom
f940--f1420. En direkt tabellpatch vid f1020 minskar i stället uploads:

```text
control texWrites=2128751 nzWords=849371
patched texWrites=2046775 nzWords=846008
frameHash båda=0x6604904f
```

`80512da8` är alltså worlds/asset-källan, inte den saknade TMU1-payloaden.

En ny callertrace vid `0x801095c8/0x801096fc` fångar hela f1020--f1040-
mipmatrisen. 233 sekundära anrop (`a0=0`, TMU0) fördelas över 41
destinationer och når `0x23fa80`. Bara 17 primära anrop (`a0=1`, TMU1)
fördelas över fyra destinationer:

```text
0006b610
0006cb60
0006f600
000720b0
```

Detta är den direkta allokeringsförklaringen till att TMU1 aldrig når
scenens `0x22dxxx`: primärvägen slutar mer än 1,7 MiB för tidigt.

En unik-page-trace över f1400--f1420 visar att scenen samplar 257 TMU1-sidor
över lokala pageintervall `0x001..0x3ff`; 182 sidors första prov är noll.
136 använda, avvikande och icke-noll MAME-sidor har exakt rådiskträff.
Exempelvis ligger längre sammanhängande grupper vid:

```text
local pages 0x164..0x167 <- disk 0x043f3800
local pages 0x170..0x173 <- disk 0x04402d20
local pages 0x18b..0x198 <- disk 0x0441dd20
local pages 0x1a1..0x1ae <- disk 0x04433d20
local pages 0x1bd..0x1c3 <- disk 0x0444fd20
```

De höga `0x1d0..0x285`-segmenten ensamma ger bara
`frameHash=0x85288b84`, `zero=292812`, `colored=52296`. Nästa fix måste
alltså återställa alla faktiskt använda primärsidor eller, helst, reparera
den tidigare primära mipgrupp som skulle ha allokerat dem. Hydrera inte bara
`0x1c31`-familjen och använd inte neutralvit som runtime-fix.

Caller-/resursspåret avför dessutom de tomma jobben som en dold companion-
lista. Funktionen vid `0x800abd64..0x800abe10` hämtar recordlistan från
`resource + 0x68 + tableIndex*0x8c` och count från `resource + 0x64`.
De count=0-poster som delar source `0x805611e8` är samma redan kända
malformade/återanvända levelE-asset som
`bgloadmodel-reject-implausible-descriptor-length` avvisar. Nästa giltiga
tabell publicerar 26 record och den efterföljande 0x220 record; ingen av de
tomma aliasposterna är en separat TMU1-tabell.

Nästa smala gräns är därför source-recordet som borde skriva TMU1:s lokala
`0x22dxxx`-område. Bind den verkliga TMU0-recordfamiljen för
`base=0x000257b3`/source-root `0x805b40ec` tillbaka till sin resource- och
QIO-proveniens, och jämför den med den saknade primära recordfamiljen.
Behåll neutralvit och alla TMU-kopior default-off; de är endast oracles.

Den riktiga TMU0-halvan är dessutom bunden ett steg upp från Type5. Source-
root `0x805b40ec` ligger i mip-3-blocket vars selector är `0x805b3ec8`.
Hela mipkedjan publiceras sammanhängande:

```text
mip 2  source=805b1ec8
mip 3  source=805b3ec8  (Type5 root 805b40ec)
mip 4  source=805b46c8
mip 5  source=805b48c8
mip 6  source=805b4948
```

Alla nivåer använder destinationsnyckel `0x001111f0`. Vid callsite
`0x801096fc` är registerbilden:

```text
a0=0
a1=001111f0
a2=mipnivå 2..6
a3=1
s0=vald mip-source
s6=001111f0
s7=3
t7=8c241009
ra=801095c8
```

Det är alltså en enda normal sekundär/TMU0-assetgrupp, inte kvarvarande
FIFO-data eller flera orelaterade scratchanvändningar. Nästa callertrace ska
börja vid `0x801095c8` och jämföra hur denna grupp byggs mot en normal
primär/TMU1-grupp. Där finns nu den närmaste observerbara punkten där den
saknade companion-recorden antingen borde väljas eller redan saknas.

### 2026-07-25: Temple weapons återställer en stor del av primär TMU1

Den avbrutna Temple/QIO-kandidaten återhämtades och kördes först A/B mot
pushad `dfa85c57` från exakt samma f1000-snapshot. Vid f1030 gav båda samma
presenterade hash `0x6604904f`, men kandidaten gjorde betydligt mer verkligt
texturarbete:

```text
                         pushad HEAD       Temple weapons
texWrites                2240354           2500078
intervall writes          495060           1533956
intervall nonzero         434841            853806
texture nonzero words     861477            953169
texture last              0x52aa9c          0x65338c
```

Det nya arbetet kommer från den ROM-härledda `weapons`-containern och den
vanliga guest-resursbyggaren vid `0x800abd64`. Första weapons-recordet
matchar MAME-allokeringen vid `0x563000`; 57 animerade aliases återbinds till
sin faktiska variant. Ingen MAME-TMU-dump eller neutralvit oracle används som
runtime-data.

Körningen fortsätter deterministiskt genom f1400:

```text
f1071  pc=0x800fe7c4 texWrites=2578121 texture nz=1025953 last=0x6a3c7c
f1200  pc=0x800d1364 texWrites=2916631 texture nz=1352940 last=0x77bf00
f1400  pc=0x800c6f00 texWrites=3032458 texture nz=1458348 last=0x7ceb24
```

Warmformat v12 sparar nu också Temple-laddningens fyra lifecycle-flaggor.
En omedelbar omladdning av f1071 var byte-/hashstabil och körde inte om
weapons-byggaren.

#### Varför den vanliga f1400-bilden ser ut som en regression

MAME-vblankspåret visar strikt senast presenterade buffer. Vid f1400 har
gästen ännu inte gjort nästa swap, så den vanliga exporten håller korrekt
kvar den äldre glesa bilden (`frameHash=0x6604904f`). Den aktuella
backbufferten innehåller däremot den nya Temple-scenen. Det är därför
missvisande att bedöma just denna mellanpunkt enbart från den presenterade
PNG-filen.

En read-only dump av arbetsbuffer 1 vid f1420, efter samma två-frame FIRE 3-
puls som tidigare, ger:

```text
framebuffer=640x480 nonBlack=307200 colored=150556
sha256=2552855de20dc76bbbe13a47707425ab32e323cddfb79634335f2458bd62c7db

artifacts/gauntlet-probe/gauntdl-temple-weapons-working-f1420-20260725.png
```

Den gamla f1420-bilden hade stora sammanhängande vita hål och bara cirka
48k färgpixlar. Weapons-kedjan fyller nu stora delar av golv, korridor och
objekt med riktiga texturer. Neutralvit-oraklet är fortfarande mer komplett,
så den återstående gränsen är alltjämt saknad primär TMU1-residens, men
weapons är verifierat en riktig del av lösningen.

Den naturliga gästprogressionen har vid f1420 redan publicerat
`items/levelE` på index 16. Den experimentella direkta items-byggaren ska
därför inte anropas: tidigare prov gav dubbel laddning, count-korruption och
hang. Nästa pass ska i stället jämföra de fortfarande nollande TMU1-sidorna
mot de 815 weapons-recorden och den naturliga items-resursen, med MAME endast
som page-oracle.

`swapbufferCMD.dontSwap` uppdaterar inte längre den presenterade kopian.
Bit 9 betyder uttryckligen att fronten inte roteras; att kopiera drawbuffer
även i det fallet kunde ersätta en giltig presenterad bild med en
övergångs-/clearbuffer.

### 2026-07-25: kvarvarande TMU1-hål och naturlig items-allokering

En ny rawdump av hela Voodoo-texturminnet och writer-proveniens för varje
först samplad TMU1-sida smalnar av den återstående gränsen:

```text
f1420 unika samplade TMU1-sidor       285
första sample raw=0                   98
raw=0 med registrerad writer           0
helt tomma 4 KiB-sidor                30
delvis fyllda men oskrivna sample     68
```

Alla 98 nollsample saknar alltså writer. Det är verkligt saknad residens,
inte svarta texlar eller ett samplerfel. Weapons-checkpointens 8 MiB-dump
har SHA-256
`7c31269cb21e94db168b331ce9cfcf6ace8d6973f177d3dd085696cf54b1452d`.
Jämfört med den äldre f1400-dumpen har 334 tidigare helt tomma TMU0-sidor
och 448 tidigare helt tomma TMU1-sidor fått data.

Den naturliga `items/levelE`-resursen är samtidigt byggd exakt en gång i den
giltiga f1400/f1420-kedjan. Dess första record är:

```text
09010605 00080008 00000000 000f0163
000001cf 0003d614 00000000 00396019
```

Recordformen och huvuddelen av fälten stämmer med referensen, men både ord
3 (`0x000f0163` mot MAME:s `0x000619a3`) och publicerad selector
(`0x00396019` mot `0x00322218`) avviker. Vid f1420 har de relevanta
allocatorglobalerna redan drivit till bland annat `0x003cd148`,
`0x007ceb28` och `0x003960b0`. Nästa riktiga försök ska därför först prova
allokatorkalibreringen precis när gästens naturliga builder `0x800abd64`
går in för index 16 och namnet fortfarande är `items/levelE`, men även
jämföra ord 3 efter försöket; den får inte anropa items-buildern separat.

Det finns nu en default-off kandidat för just detta:

```text
EUTHERDRIVE_GAUNTDL_EXPERIMENT_RUNTIME_TEMPLE_NATURAL_ITEMS_ALLOCATOR=1
```

Kandidaten skriver endast de sju kända post-weapons-globala cursors vid den
naturliga builder-entryn. Den är inte en accepterad fix ännu. Förenklade
direktkörningar divergerade före items eftersom de saknade baseline-
wrapperns source-owner/descriptor-skydd. Den korrekta wrapperkörningen från
`/tmp/gauntlet-current-f1200-v12.warm` nådde f1300 men fastnade därefter i
upprepade `runtime-string-copy` från `0x8055c280` innan hooken triggade och
avbröts manuellt. Börja nästa pass med att fånga PC/RA och frame för den
loopen eller med en snapshot närmare den första `items/levelE`-buildern.

Två diagnostiska hjälpmedel är kvar default-off:

```text
EUTHERDRIVE_GAUNTDL_DUMP_VOODOO_TEXTURE_RAW=/tmp/texture.bin
EUTHERDRIVE_GAUNTDL_TRACE_TMU1_SAMPLE_PAGES=1
```

Den senare loggar nu även `writer`, `source`, `sourceBase` och
`Type5TargetStart` för varje unik TMU1-sida. Checkpointen bygger utan fel;
befintliga varningar kvarstår.

### 2026-07-25: items-gränsen ligger före f1200, string-copy är inte ett hang

Den tidigare f1200-körningen gav en missvisande bild av var items-kandidaten
ska provas. En riktad observer visar att `runtime-string-copy` från
`0x8055c280` är en vanlig runtime-loop:

```text
pc=ffffffff8011f7ac
ra=ffffffff8003c50c
dst=ffffffff807ffca8
src=ffffffff8055c280
len=3
```

Den återkommer ungefär var 10 000:e instruktion, men frame counter fortsätter
genom f1300, f1310 och vidare. Källan innehåller strängen `SPECIAL`; detta är
inte ett fastnat texture-QIO eller en items-builder.

Viktigare: en full f1200--f1420-replay med
`EUTHERDRIVE_GAUNTDL_EXPERIMENT_RUNTIME_TEMPLE_NATURAL_ITEMS_ALLOCATOR=1`
nådde aldrig kandidatloggraden, eftersom snapshoten redan ligger efter den
naturliga items-buildern. Vid f1420 finns:

```text
asset[16]    items/levelE
source[16]   806a9d54
resource[16] 806b3880
```

Körningen är därför en negativ snapshot-oracle, inte ett giltigt A/B-prov av
allocatorn:

```text
frameHash=0x054f411a
texWrites=3014476
snapshot=artifacts/gauntlet-probe/gauntdl-temple-natural-items-f1420-20260725.warm
```

Tre bevarade f1030-snapshots ligger däremot före items-publiceringen:
`source[16]`, `resource[16]` och descriptor 16 är fortfarande noll där. En
replay från `gauntdl-temple-early-names-f1030-20260725.warm` fångade den
tidiga builderordningen och sparade checkpoints vid f1100, f1180 och f1220.
Den naturliga index-15-byggaren går mycket långsamt under den här äldre
lineage:n på grund av den redan väntande interruptkontexten
(`cause=0x8000`, `timerPending=1`); CPU-only drain visar samma beteende och
är alltså inte en genväg. Ett försök att preservera den interna timer-latchen
över context-preserving helpers ändrade inte f1040-PC eller counters och
togs bort.

Det finns nu en default-off, read-only observer:

```text
EUTHERDRIVE_GAUNTDL_TRACE_RUNTIME_TEMPLE_ITEMS_BOUNDARY=1
```

Den loggar builder-entryns instruktionstal, RA, index, assetnamn och alla sju
allocatorcursors, samt de första 16 relevanta `SPECIAL`-kopiorna. Nästa
smala steg är att lägga till ett villkorat probe-stop/snapshot exakt när
`pc=0x800abd64`, `index=16` och assetnamnet är `items/levelE`. Kör därefter
control och allocator-kandidaten från samma pre-entry-snapshot. Använd inte
f1200 eller den nya negativa f1420-snapshoten som pre-entry-baslinje, och
återinför inte den direkta items-buildern.

### 2026-07-25: index-16-builderhypotesen är avförd på den korrigerade linjen

Den föregående fortsättningspunkten var för snäv. Ett exakt probe-stop lades
till för `pc=0x800abd64`, `index=16`, `asset=items/levelE`; probe-runnern
sparar dessutom nu det faktiska frame-numret om ett sådant stopp sker mitt i
en frame. En separat bugg i observatören rättades också: 32-radersgränsen för
vanlig trace får inte längre stänga av själva stoppvillkoret.

Första replayn från f1000 avslöjade samtidigt att
`StartKnownRuntimeTempleWeaponsLoadPreservingContext()` kunde starta medan
den naturliga levelE-kedjan stod på index 9. Recovery-villkoret kräver nu
också `currentIndex == 0xffffffff`; den riktiga producenten vid `0x8003b198`
är oförändrad. Efter rättningen går den naturliga ordningen åter genom
`zom2`, `ice2`, `imp2`, `pla2`, `death`, `weapons` och `powerups`, och den
direkta weapons-byggaren returnerar med count `0x32f`.

En full replay f1000--f1250 med det reparerade stoppet gav ingen
index-16-entry. Det är ett verkligt negativt resultat, inte längre ett
trace-limitfel:

```text
f1000 currentIndex=9, nextIndex=16
      asset[16]=hiscore/legends
      source[16]=805b1370
      resource[16]=805b1464

f1150 currentIndex=ffffffff, nextIndex=17
      asset[16]=items/levelE
      source[16]=80723d04
      resource[16]=8072d830
```

Disassemblyn bekräftar att `0x800abd64` verkligen läser index från
`0x80228060`, men den observerade items-publiceringen sker alltså via en
annan väg. Även descriptor-idén avfördes: strängen kopieras först som
`items/levelE1` till `0x8024fcb0`, men när nästa CPU-step kan observera den
är items-resursen redan publicerad och index återställt till `-1`.

Den första items-recorden på den korrigerade linjen är:

```text
09010605 00080008 00000000 0007577f
000001cf 0003d614 00000000 003c10f8
```

Detta ersätter den äldre recovery-linjens `0x000f0163`/`0x00396019` som
aktuell oracle. MAME-värdena är fortfarande `0x000619a3` respektive
`0x00322218`, men allocator-kandidaten träffar inte den verkliga producenten
och får inte promoveras.

Följande snapshots är post-publication-checkpoints trots sina historiska
arbetsnamn:

```text
f1120  artifacts/gauntlet-probe/gauntdl-temple-items-descriptor-ready-v2-20260725.warm
       sha256 90f43924ca68a687038ae9b6b19b90de953ee16954a00dd3e8016fcd07e94401
f1150  artifacts/gauntlet-probe/gauntdl-temple-items-descriptor-ready-20260725.warm
       sha256 f72119ed63ec20ad0c6ec63f53e1d673849e7f24007df21cf8a3f6ac55929196
f1250  artifacts/gauntlet-probe/gauntdl-temple-items-pre-entry-v3-20260725.warm
       sha256 9cf30d85a4cc9895e9af5b484abe363963b4a5324a53b2a47bd797d016426fa1
```

Använd ingen av dem som pre-entry-oracle. Nästa smala gräns är den faktiska
skrivaren som ersätter `resourceTable[16]` vid `0x802545e0` (och motsvarande
`sourceTable[16]` vid `0x802529e0`). Börja med write-watch över exakt dessa
ord från f1000-linjen, fånga writer-PC/RA och flytta först därefter
allocator-A/B till den bevisade producenten.

#### Pauspunkt: faktisk items-writer och nästa pre-call

Write-watch från den korrigerade f1000-linjen fångade nu den exakta
publiceringen:

```text
pc=0xffffffff800aae64
op=0xacc20000                  sw v0,0(a2)
addr=0xffffffff802545e0
old=0x805b1464
new=0x8072d830
ra=0xffffffff800aae68
a2=0xffffffff802545e0
v0=0xffffffff8072d830
s0=0x10
s2=0xffffffff80723d04
```

Detta är `resourceTable[16]`. Disassemblyn runt `0x800aadc0` visar att
skrivningen ligger i delay-slotten till `jal 0x800b72fc` vid `0x800aae60`.
Items-source laddas tidigare till `s2`, och parseranropet som ska få
allocator-A/B ligger vid:

```text
0x800aae00  lui   a1,0x8014
0x800aae04  addiu a1,a1,0xb024
0x800aae08  move  a2,s1
0x800aae0c  jal   0x800c8a5c
0x800aae10  move  a3,s2
```

Första observerförsöket använde av misstag `0x800aae08`, alltså
argumentflytten före själva `jal`, och träffade därför inte. Konstanten är
korrigerad till `0x800aae0c`, men ingen ny runtime-körning har gjorts efter
korrigeringen eftersom arbetet pausades här.

Nästa fortsättning:

1. Bygg proben och kör f1000-linjen med
   `EUTHERDRIVE_GAUNTDL_STOP_RUNTIME_TEMPLE_ITEMS_BOUNDARY=1`.
2. Verifiera stopp vid `pc=0x800aae0c`, `s0=16`, `s2=source[16]`, och spara
   en neutral pre-call-snapshot.
3. Kör control och
   `EUTHERDRIVE_GAUNTDL_EXPERIMENT_RUNTIME_TEMPLE_NATURAL_ITEMS_ALLOCATOR=1`
   från exakt samma snapshot.
4. Jämför första recordens word 3/selector, `resource[16]`, texWrites och
   TMU1:s återstående nollsidor. Promovera endast kandidaten om MAME-oraclen
   förbättras utan count-korruption eller dubbel laddning.

#### Verifierad fix: naturlig items-allokering kalibrerad vid parser-call

Den korrigerade f1200-linjen nådde den verkliga gränsen vid frame 1264:

```text
pc=0xffffffff800aae0c
s0/index=16
s2/source=0x80723d04
publishedSource=0x805b1370
asset=items/levelE
alloc=003c10f8/003c10f8/007c11b8/007c11b8/007c11b8/003c10f8/003c10f8
```

Det bekräftar varför den första observatören missade gränsen: den krävde att
`s2` redan skulle vara lika med `sourceTable[16]`, men den gamla publicerade
källan ersätts först efter parseranropet. Den neutrala pre-call-snapshoten är:

```text
artifacts/gauntlet-probe/gauntdl-temple-items-parser-precall-exact-20260725.warm
frame=1264
pc=0xffffffff800aae0c
```

Control och allocator-kandidaten kördes från exakt denna snapshot till f1420.
Resurspekaren var stabil (`source[16]=0x80723d04`,
`resource[16]=0x8072d830`) och ingen dubbel laddning eller count-korruption
syntes. Första recorden blev:

```text
control    09010605 00080008 00000000 0007577f
           000001cf 0003d614 00000000 003c10f8

kalibrerad 09010605 00080008 00000000 000619a3
           000001cf 0003d614 00000000 00322218

MAME       word3=000619a3 selector=00322218
```

Kandidaten matchar alltså båda MAME-fälten exakt. Arbetsbuffer 1 visar också
en kausal förbättring: den felaktiga stora mörkblå plattan i förgrunden
försvinner och mer av den centrala Temple-gången får sammanhängande riktig
textur. Enkel mängdstatistik är blandad (`TMU1 first-sample zero` 253 -> 261,
`colored` i den nedsamplade arbetsbufferten 2254 -> 1584), men den statistiken
räknar den felallokerade blå ytan som innehåll. Den exakta record-oraklen och
bildens strukturella förbättring väger tyngre.

Fixen är därför promoterad som
`EUTHERDRIVE_GAUNTDL_FIX_RUNTIME_TEMPLE_NATURAL_ITEMS_ALLOCATOR=1` i både
bringup-preseten och probe-wrappern. Det gamla `EXPERIMENT_...`-namnet fungerar
fortfarande som kompatibilitetsalias. Read-only stop/trace finns kvar för
framtida lineage-kontroll:

```text
EUTHERDRIVE_GAUNTDL_TRACE_RUNTIME_TEMPLE_ITEMS_BOUNDARY=1
EUTHERDRIVE_GAUNTDL_STOP_RUNTIME_TEMPLE_ITEMS_BOUNDARY=1
```

Verifieringsartifakter:

```text
artifacts/gauntlet-probe/gauntdl-items-control-f1420-20260725.warm
artifacts/gauntlet-probe/gauntdl-items-allocator-f1420-20260725.warm
artifacts/gauntlet-probe/gauntdl-items-control-f1420-buffer_buf1.png
artifacts/gauntlet-probe/gauntdl-items-allocator-f1420-buffer_buf1.png
```

Nästa smala gräns är inte längre items-recordens allocator. Den återstående
Temple-bilden har fortfarande många TMU1-sample utan registrerad writer.
Fortsätt från den promoterade f1420-snapshoten och bind de kvarvarande
nollsidorna till nästa naturliga resource-/Type5-producent; ändra inte
sampler eller framebuffer-chooser utifrån dessa hål.

### 2026-07-26: weapons-record 437 producerar en ofylld texelkälla

Den första fokuserade TMU1-nollägaren är nu bunden hela vägen till sin
guestproducent. Type5-serien skriver 32 gånger 16 nollord med:

```text
tbase=0x0004061e
root source=0x80688f04
first payload source=0x8068973c
pc=0x800fe7cc
```

Den tidigare `imp2`-hypotesen är falsifierad. Nolluppladdningen sker mellan
weapons-laddningen och weapons-resursbyggarens retur; `imp2` allokeras först
senare. Att samma RAM-adress då råkar hamna i `imp2`-containern är bara
efterföljande heapåteranvändning.

Callkedjan är:

```text
0x800a761c/0x800a7764 -> 0x801094f4
0x8010957c             läser descriptor+0x10
0x801095c0             väljer sida
0x801096ac             vidarebefordrar källan
0x801096fc             anropar 0x800fe1fc
0x800fe7cc             skriver Type5-payload
```

En exakt write-watch över descriptorfältet visar den verkliga producenten:

```text
pc=0x800a7344  sw v0,0x20(sp)
record=0x805c7a40
ordinal=0x1b5 (437)
weapons base=0x805b93c4
new source=0x80688f04
source delta=0x000cfb40
```

Weapons-containern är bara `0x1f930` byte. Record 437 pekar alltså på en
expanderad texeladress långt utanför den hydratiserade containern, där ingen
tidigare main-RAM-writer finns. Sidväljaren och Type5-writern är nedströms
och återger den redan felaktiga/ofyllda källan korrekt.

Två negativa kontroller är stängda:

1. En sen indexed-QIO-hydrering aktiverades inte, eftersom containern ännu
   inte existerar när weapons-uploaden sker.
2. En bred A/B som hoppade över de matchande nollpaketen minskade
   `texWrites` från `2736773` till `2647485`, men f1080 förblev exakt
   `frameHash=0x6604904f`. Nollpaket får därför inte undertryckas som fix.

Nya default-off-diagnoser kan filtrera texture-record-anrop på `tbase` och
Type5-sekvensloggen visar nu CPU:s `s0..s5/ra`. GauntletProbe rapporterar
dessutom källproveniens och tidigare writers för efterfrågade texture words.

Nästa smala gräns är `0x800a7094..0x800a7344`: avgör vilken weapons-
dekomprimering/expansion som normalt ska fylla `base+0xcfb40`, eller vilken
aliassemantik som ska återanvända en tidigare källa för record 437. Ändra
inte `0x8010957c`, Type5-dekodern eller TMU-samplern innan denna producent är
förklarad.

Den efterföljande source-formula-tracen stänger aliasfrågan ytterligare:

```text
pc=0x800a7338
source=805b93c4
priorOffset=000cfb40
subtract=00000000
computed=80688f04
record=805c7a40
limit=1
```

Grannrecorden avancerar den ackumulerade offseten normalt; record 437 ökar
den bara från `0x0cfa40` till `0x0cfb40`, alltså `0x100` byte. Det stora
spannet är den redan ackumulerade expanderade texellayouten, inte ett trasigt
fält i just record 437. Den direkta weapons-vägen har samtidigt endast låtit
QIO-callbacken rapportera den första `0x2000`-byteschunkens completion innan
resursbyggaren körs mot den råhydratiserade `0x1f930`-bytescontainern.

Nästa kausala gräns är därför weapons-QIO:s body/continuation: bevisa hur den
naturliga callbackkedjan expanderar eller publicerar texelkroppen innan
`0x800abd64`. En korrekt kandidat ska fylla `base+0xcfb40` genom guestens
stream/dekomprimeringssemantik; den ska inte syntetiskt hoppa över nollpaket
eller remappa record 437.

QIO-poststaten visar att callbacken faktiskt publicerar
`streamOffset=0x00191e00`, medan den manuella weapons-byggaren historiskt
nollställde värdet. En default-off-kontroll,
`EUTHERDRIVE_GAUNTDL_EXPERIMENT_RUNTIME_TEMPLE_RESOURCE_PRESERVE_STREAM_OFFSET`,
bevarar detta guestvärde. Den är tekniskt stark men visuellt negativ:

```text
f1080 control texWrites=2736773 nz=926391  zero=1554345
f1080 preserve texWrites=2707167 nz=1075793 zero=1286519
f1420 preserve texWrites=3056512 nz=1831132 zero=1928560
frameHash=0x6604904f
```

Fullkörningen passerar items-gränsen och behåller den korrekta naturliga
allokeringen, men arbetsbuffer 1 får ett stort vitt hål i förgrunden jämfört
med den pushade allocator-baslinjen:

```text
/tmp/gaunt-weapons-stream-offset-f1420-buffer_buf1.png
```

Offsetbevarandet får därför inte promoteras ensamt. Det visar att
normaliseringen och den matchande texelkroppen måste återställas tillsammans.
Nästa pass ska spåra vilka QIO/body-steg som normalt gör
`source + priorOffset - streamOffset` resident; fler allocator-, sampler- och
zero-skip-experiment är nu avvisade.

### 2026-07-26: weapons companion och descriptorgrind promoterade till baseline

FSYS-gränsen bakom record 437 är nu löst. `0x043d1800` är extentheadern för
`weapons/objects.rom`; dess payload börjar vid `0x043d1a00` och innehåller
recordtabellen. Nästa relevanta extentheader ligger vid `0x043f2600` och
deklarerar `0x00145d88` byte. Den tillhör `weapons/textures.rom`, vars payload
börjar vid:

```text
disk base = 0x043f2800
bytes     = 0x00145d88
```

Record 437:s ackumulerade texeloffset `0x000cfb40` ryms i denna companion.
Diskorden vid `0x043f2800 + 0xcfb40 = 0x044c2340` är verkliga texeldata:

```text
22012201 22003301 22013301 22013301
```

Den manuella Temple-vägen hade i stället satt både record-owner och
`streamSource` till `objects.rom`-basen `0x805b93c4`. Det gav den tidigare
nollkällan `0x80688f04`. En ny default-off-kandidat,
`EUTHERDRIVE_GAUNTDL_EXPERIMENT_RUNTIME_TEMPLE_WEAPONS_TEXTURE_COMPANION=1`,
läser den riktiga texture-extenten till en separat transient streambuffer och
låter den vanliga guest-buildern behålla offsetsemantiken. Efter buildern
återställs heapmarkören; nästa `zom2`-allocation återanvänder exakt
streambufferadressen, så companionen blir inte ett permanent heapobjekt.

F1080 från exakt samma f1000-snapshot gav:

```text
                                      tidigare control    companion
texture-map nonzero writes                 926391          1499200
texture-map zero writes                   1554345           387192
```

Källmodellen är alltså kausalt riktig och återställer mycket faktisk
weapons-textur. Den tidigare tolkningen av f1264/f1420 som
timing-/FIFO-korruption var fel. Den transienta varianten bevisar samtidigt
att förloppet inte beror på permanent heapförskjutning:

```text
companion heap 0x00307af4 -> 0x0044d87c
release        0x0044d87c -> 0x00307af4
zom2 dest      0x805e2f34
f1264/f1420    pc=0x800aace4/0x800aad00, frameHash=0xbb62299d
```

En ny snapshot-säker trace finns för parserloopen:

```text
EUTHERDRIVE_GAUNTDL_TRACE_BGLOADMODEL_DESCRIPTOR_LOOP=1
EUTHERDRIVE_GAUNTDL_TRACE_BGLOADMODEL_DESCRIPTOR_LOOP_INDEX=a
EUTHERDRIVE_GAUNTDL_TRACE_BGLOADMODEL_DESCRIPTOR_LOOP_LIMIT=32
```

Den visar den riktiga kedjan. `zom2`-texturekällan `0x80591e90` innehåller råa
texlar och ger därför det orimliga descriptorantalet `0x2056ff66` vid
`source+0x64`. Parsern fortsätter då med 8 MiB RAM-wrap och skriver själv via
`sb/sh` över sin kod:

```text
pc=0x800aace4  addr=0x800aacfb  sb zero
pc=0x800aacf0  addr=0x800aacfc  sh a0
0x800aacfc     0x0082102a -> 0x00824b16
```

`0x4b16` var alltså parserns eget loopindex, inte ett Voodoo-registerfel.
Samma råa `zom2`-källa finns i control utan companion. Companionen skapade
inte felet; den längre körningen gjorde det bara synligt.

Den redan avgränsade descriptorgrinden har därför promoterats till baseline
som
`EUTHERDRIVE_GAUNTDL_FIX_RUNTIME_BGLOADMODEL_REJECT_IMPLAUSIBLE_DESCRIPTOR_LENGTH=1`.
Den verkar bara vid den intakta `slt`-instruktionen `0x800aacfc`, kräver att
registerantalet är exakt samma som `source+0x64`, och avvisar endast antal över
`0x10000`. F1000--f1080 med både baseline och companion gav exakt en reject:

```text
index=10 source=0x80591e90 count=0x2056ff66->0 completed=1
f1080 pc=0x800c9cc4 swaps=5230 texWrites=3216939
texture-map writes=4401400 nonzero=2649115 zero=1752285
```

Ingen skrivning träffade parserkodfönstret med grinden aktiv, och bringup gick
vidare genom `ice2`, `imp2` och `pla2`.

En ren guarded snapshot skapades därefter och verifierades genom återladdning:

```text
/tmp/gaunt-weapons-texture-guarded-f1080.warm
/tmp/gaunt-weapons-texture-guarded-f1120.warm
/tmp/gaunt-weapons-texture-guarded-f1120.ppm
```

Vid f1112 lämnar bilden den statiska vita fasen. F1120 visar en faktisk
3D-scen med golv, arkitektur och karaktärsgeometri:

```text
f1120 frameHash=0xaec7381d pc=0x80102f08
drawPackets=176787 lfbWrites=85402145 swaps=7055
textured triangles=1926 accepted=1582 rejected=344
framebuffer colored=160638
texture-map writes=457832 nonzero=407533 zero=50299
```

Scenen har ännu feltexturer och vita hål, men companionen är nu verifierad
hela vägen genom riktig geometri. Den har därför promoterats till baseline som
`EUTHERDRIVE_GAUNTDL_FIX_RUNTIME_TEMPLE_WEAPONS_TEXTURE_COMPANION=1`.
Den gamla experimentflaggan stöds fortfarande för enskilda körningar.

Verifieringsartefakter:

```text
/tmp/gaunt-weapons-texture-stream-f1080.warm
/tmp/gaunt-weapons-texture-stream-f1420.warm
/tmp/gaunt-weapons-texture-stream-f1420-buffer_buf0.ppm
```

Nästa smala gräns är den första felaktiga textur-/vit-hålsproveniensen i den
riktiga f1120-scenen. Fortsätt från den guarded f1120-snapshoten och bind en
synlig felpatch till dess Type3-record, TMU-val och texture base innan någon
sampler ändras. Ändra inte companionens diskbas eller recordoffset; de är nu
bundna till riktig FSYS-data.

#### F1120--f1128 pixelproveniens

`EUTHERDRIVE_GAUNTDL_PROFILE_VOODOO_PIXEL_LAST_WRITERS=1` skriver nu även
`voodoo writtenPixelWriters=` med färg, koordinat och full
`PixelLastWriterKey` för alla 32x32-provpunkter som faktiskt skrevs under
körningen. Det kompletterar den äldre listan som bara visade vita pixlar.

Åtta frames från guarded f1120 gav:

```text
textured triangles=932 accepted=711 rejected=221
textured pixels=33835 zero=19135
white framebuffer samples: writer=none
```

De stora rena vita hålen är alltså clear-bakgrund som ingen triangel skriver
till, inte vita texlar från en felaktig texture fetch. Samtidigt blir ungefär
56 procent av de rasteriserade texelpixlarna noll, så båda problemen finns men
har olika proveniens.

En skriven golvpunkt `b0@400,48` bands till `pc=0x800c6324`,
Type3 `0x00c2a0cb/0x01c2a10b`. Dess TMU0-fetches var verkliga och icke-noll:

```text
tmode=0x8c22410f tlod=0x00002604/0x00202604
tbase=0x000424a7/0x00043f51
addr=0x227a50..0x22a34a raw=0x7c/0x70/0x6d
```

En nästan svart skriven punkt `b0@176,48` hämtade också verklig data:

```text
pc=0x800c7190 cmd=0x0082a0cb
tmode=0x8c22490f tlod=0x06002604 tbase=0x0001e23e
addr=0x11bbf0 raw=0xfe99 rgb565=0x69c2 final=0x0001
fbzColorPath=0x0c60743a color0=color1=0xff000000
```

Den mörka slutprodukten kan därmed förklaras av den aktuella color-pathens
svarta constant-color-val; den är inte bevis för fel bank eller saknad
companion. Nästa kontroll ska binda en representativ noll-fetch till dess
exakta TMU och texture-upload writer, eller visa vilken saknad yta som ger
`empty-raster`, innan color combine ändras.

#### Aktiv två-TMU-väg: noll-fetch-proveniens

Den äldre `texzero`/`texsamp`-diagnostiken var blind för
`SampleTextureMameFixedForTmu`, trots att rasterräknaren rapporterade 19135
nollpixlar. Den aktiva fetchvägen matar nu samma default-off bucket- och
writer-korrelation som single-TMU-vägen. Adressintervallet för
`EUTHERDRIVE_GAUNTDL_TRACE_VOODOO_TEXTURE_SAMPLE_WRITERS_RANGE_MIN/MAX`
filtrerar nu också själva samplertracen, inte bara insamlingen av framtida
upload-writers.

F1120--f1128 är fortfarande bitstabil med `frameHash=0x770ad7f8` och ger:

```text
TMU-fetches=67670
local zero fetches=17401
top zero buckets:
  0x7fd000:1647  nonzeroWords=0 touchedWords=0 writers=0
  0x7e1000:1334  nonzeroWords=0 touchedWords=0 writers=0
  0x413000:1267  nonzeroWords=120 touchedWords=0 writers=0
```

Det största kalla fönstret är nu bundet till en riktig TMU1-fetch och dess
Type3-producent:

```text
pc=0x800c7190 command=0x0182a0cb
TMU1 mode/lod/base=8c241acf/00400410/001fe624
resolved LOD base=0x7fdb20 size=4x16
sample addr=0x7fdb82..0x7fdb92 raw=0x0000 result=0x0000 writer=none
```

Den parade TMU0-ytan i samma pixlar (`base=0x1c0100`, 16x16) läser varierande
icke-noll texlar. Nollan skapas alltså inte av color combine och inte av en
vit framebuffer-clear: Type3-recordet väljer en TMU1-LOD-yta som aldrig har
fyllts. Tidigare TMU0-aliasprov visar redan att bankbyte kan ge orelaterat
material av en slump, så nästa steg är record-/assetproveniensen för
`001fe624/8c241acf/00400410`; ändra inte TMU-bankmappningen eller inför en
zero-fallback.

#### Nolltransparens var ett bringup-hack, inte Voodoo-beteende

Den stora vita patchen vid PNG-pixel `(352,304)`, rasterpixel `(352,80)`,
spårades genom hela pixelvägen. Flera stora trianglar täcker punkten, TMU0
läser varierande riktiga texlar, och TMU1 -> TMU0-kombineringen ger ibland
exakt noll:

```text
TMU0  8c22410f/00002608/00027d06  addr=0x1532f8 raw=0x0065 rgba=53637dff
TMU1  8c241acf/0030030c/001f67c4  addr=0x7c8ebe raw=0x0000 rgba=000000ff
final combined=000000ff
```

Den tidigare bringup-regeln
`EUTHERDRIVE_GAUNTDL_FIX_VOODOO_ZERO_TEXTURE_TRANSPARENCY` kastade därefter
pixeln och lämnade framebuffer-clearen `0xffff` synlig. Det är inte
hårdvarubeteende i denna draw. `fbzMode=0x00000460` har varken chroma-key
(bit 1) eller alpha-mask (bit 13) aktiv, och RGB-masken är aktiv. En
RGB-nollpixel ska därför skrivas, inte bli implicit transparent.

En strikt A/B från samma guarded f1080-snapshot till f1120 med endast regeln
avstängd gav:

```text
baseline med gammal regel  pixel(352,304)=ffffff
regel av                   pixel(352,304)=000000
frameHash=0xa6f86452
```

En fortsatt f1120--f1128-replay är stabil:

```text
frameHash=0xdcdf3c32
textured triangles=932 accepted=711 rejected=221
textured pixels=33835 zero=19135
framebuffer colored=148746
```

Preseten sätter nu uttryckligen
`EUTHERDRIVE_GAUNTDL_FIX_VOODOO_ZERO_TEXTURE_TRANSPARENCY=0`, så
`BRINGUP_FAST` kan inte återaktivera den historiska fallbacken implicit.
Default-off-pixeltracen rapporterar samtidigt `coverage`,
`zero-transparent`, `alpha-bit-reject`, `alpha8-reject` och
`rgb-mask-disabled`, vilket gör framtida bortfall direkt klassificerbara.

Två närliggande sidospår är också stängda:

1. Alla 217 `empty-raster`-rejects i f1120--f1128 är genuina subpixel- eller
   tunna slivertrianglar; de förklarar inte de stora vita ytorna.
2. FSYS-headern vid `0x0515c800` deklarerar visserligen en separat
   `0x0006fb64`-bytespayload vid `0x0515ca00`, men den hör inte till den
   aktiva natural-items-parsern. Vid dess exakta pre-call-gräns matchar
   `source=0x80723d04` redan rådisken från `0x045335d0` genom de sista
   `0x4fb8` byten av `weapons/textures.rom`; guestparsern omvandlar denna
   källa in-place till samtliga 247 records. Ett items-companion-experiment
   nådde därför inte den aktiva index-16-vägen och behölls inte.

Den statiska materialproveniensen är samtidigt bekräftad: det omgivande
`0x280`-bytesblocket för `001fe624/8c241acf/00400410` förekommer oförändrat
på rådisken vid `0x099eae78`, alltså
`levelE1/objects.rom + 0x4b878`. Nästa riktiga gräns är därför fortfarande
vilken naturlig primär/TMU1-residens som MAME har för de höga
`0x7cxxxx..0x7fxxxx`-ytorna. Återinför inte nolltransparens, bankalias eller
en syntetisk companion för att dölja den gränsen.

### 2026-07-27: MAME-Temple-orakel och powerups-residens

Det officiella Temple-oraklet är återställt från en normal MAME-savekedja
(initialer `SJB`, lösenord `964`, Temple-valet) utan runtime-overlay. En
GDB-dump vid stabil Temple-gameplay matchar de tidigare dokumenterade
bankhasharna exakt:

```text
TMU0 c1356470f8be70d533b867041830b644eaa0a90293cc36c1c2852fb1dcfc9899
TMU1 26aa29820758aa421017e1ab01d4530b84ac982d2ff0e872f46dd7f6a9e22d0d
```

Det avförde först ett falskt mål: MAME har också noll vid TMU1-local
`0x3c8ebe` och i den aktiva `001fe624`-familjens exakta fetchadresser.
Nolltexeln där är legitim; den bevisar inte saknad upload.

En full exakt fetchjämförelse från guarded f1120 hittade däremot en verklig
powerups-familj. Euther hade 9 895 fetches där Euther var noll och MAME
icke-noll, fördelade över 1 800 unika adresser. De största sidorna låg vid
TMU1-local `0x2dccxx`, `0x2e47xx`, `0x2f89xx`, `0x2e63xx` och `0x2c80xx`.
Exakta 256-bytesblock från MAME förekommer i powerups-filen på rådisken.

En 32 MiB MAME-RAM-dump stängde allocatorgränsen:

```text
MAME   weapons slot 15, powerups slot 16
MAME   powerups first selector = 0x0029c0a9
Euther powerups slot 15
Euther powerups first selector = 0x00345561
delta                           = 0x000a94b8
```

Writer-watch på Euther visade den verkliga producenten, inte den vanliga
resource-parsern:

```text
0x800a75c8 record+0x1c = 0x00345560
0x800a7638 record+0x1c = 0x00345561
s0/s3 före store       = 0x00745560 / 0x00745ab0
```

Baselinefixen
`EUTHERDRIVE_GAUNTDL_FIX_RUNTIME_TEMPLE_NATURAL_POWERUPS_ALLOCATOR=1`
grindas på exakt PC, slot 15:s source/resource, header
`table=0xa4,count=0x220`, tomt första selectorord och de två observerade
cursorvärdena. Den flyttar endast första loopens `s0/s3` med `0x000a94b8`
och korrigerar det redan materialiserade första råordet på nästa PC.
Experimentflaggan
`EUTHERDRIVE_GAUNTDL_EXPERIMENT_RUNTIME_TEMPLE_NATURAL_POWERUPS_ALLOCATOR`
stöds fortfarande separat.

De första 51 selectorposterna följer därefter MAME; de första 16 normala
recorden matchar ord för ord. Senare aliasrecords driver fortfarande isär
eftersom Euther lämnar flera `0x08xxxxxx`-alias ofärdiga. Det är nästa
guestparsergräns och ska inte döljas med MAME-data.

Ett strikt post-upload-A/B flyttade endast Euthers egna TMU1-powerupsbytes
från `0x745560` till `0x69c0a8`; inga MAME-bytes användes. Samma guarded
f1120-state, samma 52 166 fetches, framehash och triangelantal gav:

```text
                                      control   flyttad powerups
Euther zero / MAME nonzero              7 528              1 480
unika saknade adresser                   1 130                420
rasteriserade nolltexlar                16 711             11 804
TMU1-local sida 0x2dc000                   382                  0
TMU1-local sida 0x2f8000                   766                  0
```

Det bekräftar att allocatorbasen är kausal och återställer den dominerande
residensen. Nästa smala steg är att få aliasposterna efter cirka record 51
att konsumera samma cursorutrymme som MAME, därefter köra om exakt
fetchjämförelse. Lägg inte in den post-upload-blockflytten som runtimefix.

#### Korrigering: natural powerups före weapons

Den föregående `0x805b93c4/0x805beddc`-tabellen var inte natural powerups.
Warm-fallbacken startade weapons innan slot 15 hade byggts och lät därefter
weapons-källan passera som en artificiell powerups-tabell. Den tidigare
`0x000a94b8`-kalibreringen och påståendet att de första 51 selectors matchade
MAME gäller därför inte den riktiga natural-kedjan.

Den verifierade natural-tabellen är:

```text
source   = 0x80563570
resource = 0x80568f88
count    = 0x220
```

Dess första allocatorpass går in med
`s0/s3=0x00487600/0x00487b50`. Baselinefixen flyttar dessa med
`+0x00214aa8` till `0x0069c0a8/0x0069c5f8`, varefter record 0 publicerar
MAME:s selector `0x0029c0a9`. Records `0`, `1`, `2` och `4..8` matchar MAME
exakt. Record 3 är ett delat alias som inte ska konsumera egen yta. Den första
nya metadata-/geometriavvikelsen i natural-tabellen börjar vid record 9, inte
record 0x37; ingen record-hårdkodning har införts för den.

Weapons-fallbacken kräver nu att slot 15 faktiskt heter `powerups`, har både
source och resource och att den naturliga parsern har återgått till idle.
Powerups kopieras då undan till slot 17, weapons tar slot 15 och guest får
fortsätta sin naturliga items-load i slot 16. Den ordningen är avsiktligt
guest-kompatibel; ett försök att lägga powerups i slot 16 och items i slot 17
gjorde att guest därefter skrev över slot 16 med sin egen items-callback.

Efter natural powerups saknades `0x145c4` bytes för den transienta
`0x00145d88`-bytes weapons-texturströmmen i den gamla 8 MiB-heapen.
RAM-fönstret `0x80800000..0x80945d88` var helt tomt före injektionen och
används nu endast som default-on companionens transienta parserström när den
ordinarie heapen inte räcker. Objektkropp, resource records och den synliga
guest-heapens cursor ligger kvar i sina ordinarie områden.

En ren f1000--f1120-replay med samma `200000` CPU-steg/frame som den äldre
guarded-referensen verifierade hela kedjan utan overlays eller MAME-bytes:

```text
powerups first selector = 0x0029c0a9
weapons first selector  = 0x00163001
weapons stream          = 0x80800000
weapons returned        = True
items first selector    = 0x00322218

frameHash                = 0x391954c5
framebuffer colored      = 205387
swaps                     = 2713
rasterized texture pixels = 284297
```

Verifieringsloggar och RAM-dumpar:

```text
/tmp/gaunt-natural-powerups-trace-f1000-f1080.log
/tmp/gaunt-natural-powerups-calibrated-f1080-mainram.bin
/tmp/gaunt-powerups-weapons-ordered-f1120-mainram.bin
/tmp/gaunt-temple-guest-ordered-200k-f1000-f1120.log
```

Nästa smala gräns är record 9:s natural-metadata och dess alternativa
allokeringsgeometri. Använd den default-off riktade tracen
`EUTHERDRIVE_GAUNTDL_TRACE_RUNTIME_TEMPLE_POWERUPS_RECORD_LOOP=1`; återanvänd
inte den artificiella `0x805beddc`-tabellen som selector-orakel.

#### Natural powerups low-cursor kalibrerad mot record 55

Den riktade tracen över `0x800a7100..0x800a7800` visar att record 9 inte
kommer från en felaktig lookup eller cacheträff. Gästens egen
allocatorgräns vid `0x800a7264..0x800a7278` räknade:

```text
record 8:  0x0069e9f0 - 0x00400000 = 0x0029e9f0 < 0x0029ef58 -> primary
record 9:  0x0069f1f0 - 0x00400000 = 0x0029f1f0 < 0x0029ef58 -> false
```

Den äldre natural-fixen flyttade high-cursorn till rätt selectorlinje men
lämnade low-cursorparet `0x8020f108/0x8020f10c` på `0x0029ef58`. Bara
`0x2eb0` bytes återstod därför och gästen valde secondary/reuse redan vid
record 9.

MAME-oraklet ger en exakt, senare gräns: record 54 är fortfarande primary
med selector `0x002b4371`, medan record 55 ska växla till secondary på
`0x002b4448`. Natural-powerups-fixen kräver nu dessutom att båda gamla
low-cursororden är exakt `0x0029ef58` och kalibrerar dem till
`0x002b4448` vid samma hårt guardade första selectorwriter som high-cursorn.
Ingen record, metadata eller texturpayload skrivs direkt.

Den korta f1080-dumpen gav först:

```text
före low-cursorfix:  selectors/metadata lika MAME = 51 / 544
efter fix:           selectors/metadata lika MAME = 348 / 544
```

Den senare 200k/f1120-kontrollen korrigerar tolkningen av detta resultat.
Record 368 var bara mitt i sin upload när f1080 stoppades; det fanns inget
felaktigt R5000-returhopp där. När gästens natural-builder är helt färdig
matchar 511 av 544 records MAME ord för ord. Exakt 33 `01/00...`-aliasrecords
har då fortfarande rå owner `0x0824191f` och nollselector.

Den befintliga guest-härledda
`RepairKnownRuntimeTempleAnimatedTextureAliases` kördes tidigare endast
efter context-preserving weapons/items. Natural-kedjan anropar den nu också,
men först när:

```text
currentIndex = 0xffffffff
source       = 0x80563570
resource     = 0x80568f88
table/count  = 0x000000a4 / 0x00000220
```

Reparationen hittar en redan färdig record i samma natural-tabell med samma
texturoffset, kopierar endast guestens producerade record och återställer
aliasets ursprungliga typord. Inga MAME-bytes används. Därefter bevaras
powerups i slot 17 och weapons får ta slot 15.

Full A/B gav:

```text
före aliasreparation: ordexakta records = 511 / 544
efter aliasreparation: ordexakta records = 536 / 544
reparerade aliasrecords                         = 33
nollselectors efter reparation                   = 0
råa alias-owners efter reparation                = 0
```

De återstående åtta MAME-skillnaderna är fullt processade records i samma
sammanhängande animationsgrupp, inte tomma alias. Reparationen väljer
gruppens första frame medan MAME-snapshoten oftast visar frame +1 och i ett
fall frame +7. Dessa ögonblicksfaser ska inte hårdkodas.

En senare f1260-kontroll hade fler totala guestinstruktioner än
200k/f1120. Natural-tabellen ändrade då noll metadata-/selectorposter,
source-headerns count var fortsatt `0x220`, weapons-QIO var helt nollställd
och weapons-buildern hade returnerat `True`. Den äldre count/QIO-regressionen
från syntetisk items-prebuild reproduceras alltså inte på denna naturliga
ordning.

Slutverifieringen kördes från samma f1000-snapshot med `200000`
CPU-steg/frame till f1120. Weapons-buildern returnerade `True`, items-kedjan
fortsatte, och renderpumpen rapporterade:

```text
frameHash                 = 0xd5af7199
framebuffer colored       = 211685
swaps                      = 2713
rasterized texture pixels = 296262
```

Verifieringsartefakter:

```text
/tmp/gaunt-natural-powerups-record7-11-producer2-f1000-f1080.log
/tmp/gaunt-natural-powerups-lowtarget-f1080-mainram.bin
/tmp/gaunt-natural-powerups-lowtarget-200k-f1000-f1120.log
/tmp/gaunt-natural-powerups-lowtarget-200k-f1120-mainram.bin
/tmp/gaunt-natural-powerups-aliasfix-200k-f1000-f1120.log
/tmp/gaunt-natural-powerups-aliasfix-200k-f1120-mainram.bin
/tmp/gaunt-natural-powerups-aliasfix-f1000-f1260.log
/tmp/gaunt-natural-powerups-aliasfix-f1260-mainram.bin
```

#### MAME-korrekta RGB/depth-masker och bufferroller

Det första visuella 200k/f1120-passet efter natural-aliasreparationen
bekräftade en riktig Temple-scen i den presenterade framebuffern:

```text
frameHash                 = 0xd5af7199
framebuffer colored       = 211685
swaps                     = 2713
rasterized texture pixels = 296262
```

Råbuffertarna visade samtidigt att buffer 1 bar scenen medan buffer 0 och 2
var vitdominerade. Jämförelse med MAME:s `voodoo.cpp`,
`voodoo_regs.h` och `voodoo_render.cpp` hittade fyra lokala
buffersemantikfel:

1. RGB-write-mask är `fbzMode` bit 9 (`0x200`), inte aux-maskens bit 10
   (`0x400`).
2. draw-buffer-select `0/1` betyder alltid front/back; vblank-timing ska inte
   invertera mappningen.
3. När `fbiInit2` går från tre till två RGB-buffertar måste front/back-index
   2 normaliseras till 0, eftersom den tredje ytan då är aux.
4. En swap presenterar den nya frontbufferten efter rotationen. Den ska inte
   snapshotta den yta som råkade vara aktiv före rotationen.

Draw-buffer-select 2/3 saknar dessutom RGB-destination i MAME och får inte
skriva färg till den lokala aux-slotten.

Ett checkpointat f1120--f1200-A/B gav 1 152 484 täckta texturpixlar. Den
gamla vägen skrev 572 667 av dem som RGB i buffer 1 trots att det dominerande
`fbzMode=0x460` var aux-only. Med MAME-mask/depth-vägen skrevs noll av dessa
som RGB och gäst-/paketflödet var identiskt.

Den första f1320-fortsättningen avslöjade att dess f1200-checkpoint redan bar
ett ogiltigt `front/back=1/2` från tiden före indexnormaliseringen. En ren
f1120--f1320-replay lät därför den nya koden observera `fbiInit2`-skrivningen
innan nästa swap. Resultatet:

```text
frameHash                  = 0x27f2f065
swaps                      = 2714
front/back/count           = 0/1/2
textured triangles         = 22062
covered/rejected           = 18420/3642
textured pixels            = 4896782
raster RGB pixels          = 106855/0/0
```

Den presenterade bilden är åter en komplett, igenkännbar Temple-frame, men
har fortfarande mörka ytor, vita topprader och felaktiga texturer:

```text
artifacts/gauntlet-probe/gauntdl-mame-buffer-roles-depth-f1320-20260727.png
/tmp/gaunt-clean-mame-buffers-depth-f1120-f1320.log
/tmp/gaunt-clean-mame-buffers-depth-f1320.warm
```

Baseline-preseten aktiverar nu RGB-mask och MAME aux/depth tillsammans.
Nästa smala gräns är inte längre displaybuffer-valet. Spåra de kvarvarande
vita toppradernas sista RGB-skrivare och jämför deras color-combine/textur-
proveniens med MAME; återinför inte inverterad draw-buffer-mappning eller
presentation av buffer 2.

#### Exakt toppradsskrivare och fortsatt depth-pass

En godtycklig pixel-proveniensmätare ersatte den tidigare fasta 32x32-gridens
begränsning. Den kan nu följa exakt råpixel och fryser även writer-ID:t som
hör till den presenterade snapshotten. Pixeln i den vita toppraden som
motsvarar rå `buffer 0 @ 400,6` var fortfarande `0xffff`, men hade ingen
skrivare vare sig under f1080--f1120 eller f1120--f1320:

```text
voodoo pixelWriterSample=b0@400,6:cFFFF:id0:none:presented=FFFF:id0:none
```

Den vita pixeln är alltså äldre bufferinnehåll, inte resultatet av en
post-f1120-textur- eller color-combine-write.

En ny command-only-variant av fastfill/swap-spåret reproducerade den exakta
f1320-bilden utan att tömma loggbudgeten på passiva registerrader:

```text
PPM sha256 = 04cf3567fa13db339091ebb533dfe4879435311108582a9f5dd7aadf8075d283
frameHash  = 0x27f2f065
```

Ordningen kring swapen var:

1. svart fastfill och nästa draws byggde buffer 1 medan swapen väntade;
2. vblank roterade `front/back` från `1/0` till `0/1`;
3. buffer 0 presenterades korrekt;
4. efterföljande arbete fortsatte med bufferrollerna intakta.

Det finns därför inget stöd för att flytta clearen över swapen eller välja
buffer 2. f1320 är den första presenterade buffer-0-scenen och bär äldre vitt
innehåll i pixlar som aldrig täcktes.

Fortsättningen f1320--f1520 gav ingen ny swap. Gäst/FIFO fortsatte aktivt från
208 192 till 248 147 draw-paket, men alla 24 604 rasteriserade trianglar låg
i `fbzMode=0x460/0x60` med endast aux/depth-write. RGB-statistiken stod helt
stilla och ett separat f1520--f1525-spår såg ingen `fbzMode`-skrivning.
Snapshotten och dess hash förblev därför identiska med f1320.

Verifieringsfiler:

```text
/tmp/gaunt-fill-swap-commands-f1120-f1320.log
/tmp/gaunt-fill-swap-commands-f1320.warm
/tmp/gaunt-topwhite-writer-f1080-f1120-current.log
/tmp/gaunt-next-swap-f1320-f1520.log
/tmp/gaunt-next-swap-f1520.warm
/tmp/gaunt-fbz-writes-f1520-f1525.log
```

Nästa smala gräns är att fortsätta från f1520 till nästa render-stateövergång
och avgöra varför det långa aux/depth-passet inte följs av RGB-pass/swap.
Ändra inte RGB-maskbiten, draw-buffer-mappningen eller presentationsbufferten
för att syntetisera en ljusare frame.

#### Ny Type3-emitter stoppar den falska f1942-swappen

En passiv f1920--f1943-fortsättning hittade att nästa swap inte heller var
ett trovärdigt spelkommando:

```text
frame=1942
packet=0x03b1af8c
false Type4 header=0x4358920c
false swap value=0x43bff874
producer pc=0x800c5bd4..0x800c5bdc
```

Packet-ownership-spåret visade den riktiga packetgränsen åtta byte tidigare:

```text
real Type3 header=0x01c0a8cb
packet=0x03b1af80
producer pc=0x800c5b80
false header offset=3 words
```

`0x4358920c` och `0x43bff874` är alltså vertexpayload under en riktig
Type3-header. Type3-producentfiltret kände bara emittrarna `0x800bc8ec` och
`0x800bc91c`, så body-ägarskapet startade aldrig för familjen vid
`0x800c5b80`.

Ett exakt A/B från samma f1920-state, med `0x800c5b80` tillagd i
`EUTHERDRIVE_GAUNTDL_EXPERIMENT_VOODOO_FIFO_TYPE3_PRODUCER_HEADER_PCS`, gav:

```text
false Type4 ownership hits = 0
swaps                      = 2714  (ingen ny swap)
frameHash                  = 0x27f2f065
front/back                 = 0/1
```

Utan den nya header-PC:n blev resultatet `swaps=2715`,
`frameHash=0x392437d8` och `front/back=1/0`. Den verifierade Type3-headern
ingår nu i både probe-scriptet och apparnas baselineprofil. Baselineprofilen
aktiverar samtidigt de redan verifierade Type3-/Type4-body-advance-skydden så
Android, desktop och probe använder samma packet-ownershipmodell.

Nästa pass ska fortsätta framåt från den rena f1943-checkpointen och söka
första riktiga RGB-pass/swap. Behandla fortsatt flyttalsliknande Type4-header
eller swapvärde som packet-ownershipbevis, inte som en giltig bildgräns.

#### Ren f1943--f2140-fortsättning når en legitim depth-only-menypass

Den reparerade Type3-gränsen håller genom den fortsatta körningen. f1943--f1980
ger inga nya swaps och behåller den presenterade Temple-framen med
`frameHash=0x27f2f065`. Vid f2003/f2005 sker däremot en trovärdig
Voodoo-omkonfigurering:

```text
f2003: fyra direkta swapbufferCMD value=0 vid pc=0x80102a80
f2005: fyra Type1-paket cmd=0x00010251 target=0x4a value=0
front/back/count efter sekvensen: 0/1/3
swaps: 2722
frameHash: 0xe50a1c19
```

Det jämna antalet swaps och de rena heltalsvärdena skiljer sekvensen från den
falska f1942-swappen. Efter reseten är rå buffer 0 och 1 nästan svarta med en
tunn äldre remsa överst. Buffer 2 behåller den riktiga, tidigare renderade
Temple-scenen men är inte aktuell frontbuffer. Den ska därför användas som
bevis på riktig bilddata, inte väljas artificiellt som presentation.

Alla efterföljande `fbzMode`-paket genom f2140 är exakt `0x60` eller `0x460`.
Packet-ownership och CPU-trace visar hela gästkedjan:

```text
Type1 header writer: 0x80103540
Type1 value writer:  0x80103544
state word:          0x80262d64 + 0x26c = 0x80262fd0
clear aux mask:      caller 0x800b91c0, delay-slot a0=0
set aux mask:        return address 0x800b9b18, a0=1
```

Funktionen laddar stateordet, rensar eller sätter bit `0x400`, skriver tillbaka
det och publicerar därefter Type1-paketet. Ett RAM-watchpoint från f1980 till
f2006 visar bara gästskrivare vid `0x80103510`, `0x80102b40` och `0x80103478`;
inga värden med RGB-maskbit `0x200` förekommer. MAME-referensen bekräftar att
bit 9 är `rgb_buffer_mask`, så den fortsatta rasteriseringen är avsiktligt
aux/depth-only och färgskrivning ska inte forceras.

Vid f2141 finns 32 render-records: 21 tomma textfält och 11 giltiga strängar,
bland annat `Code version`, `FOG`, `render Mask` och `render mode`. De loggade
`render-record-null-body`-träffarna är alltså de tomma menyfälten och inte
modellbodies som ska syntetiseras. RAM-state `0x80227ab0=0x8008` bekräftar
samtidigt att körningen redan är efter den riktiga diagnostikexiten; exit-latch
`0x80227ec8` är noll och ska inte återassertas i state `0x8008`.

Reproducerbara checkpoint- och verifieringsfiler:

```text
/tmp/gaunt-clean-f1980.warm
/tmp/gaunt-clean-f2020.warm
/tmp/gaunt-clean-f2100.warm
/tmp/gaunt-clean-f2140.warm
/tmp/gaunt-clean-f2030-buffer_buf0.ppm
/tmp/gaunt-clean-f2030-buffer_buf1.ppm
/tmp/gaunt-clean-f2030-buffer_buf2.ppm
/tmp/gaunt-fbz-builder-f2020-f2060.log
/tmp/gaunt-fbz-caller91-f2020-f2030.log
/tmp/gaunt-fbz-state-owner-f1980-f2006.log
/tmp/gaunt-clean-f2140-render-records.log
/tmp/gaunt-fire3-exit-state-trace-f2140-f2142.log
```

Nästa smala gräns ligger därför ovanför Voodoo: spåra state-`0x8008`-ägaren
som ska avsluta den långa depth-only-passen och publicera nästa world/RGB-pass.
Ändra inte RGB-masken, buffer 2-valet, diagnostic-exit-latchen eller
null-textposterna utan ny kausal evidens.

#### State 0x8008 fortsätter legitimt men publicerar inget RGB-pass genom f2300

Den dynamiska state-`0x8008`-vägen är inte låst. Dispatchern anropar
`0x80018800`, som uppdaterar HUD-/spelardata och avslutar normalt. Den tagna
grenen vid `0x80018d2c` skapar två riktiga textobjekt via `0x8004ca84` och
`0x8004c93c`. Strängtabellen vid `0x80129df0` innehåller bland annat
`CREDITS`, `PLAYTIME` och `FINAL STATS`; träffen på `CREDITS` är därför
UI/HUD-data och inte bevis för en null body eller en fastnad state-dispatch.

En ren fullrenderad f2140--f2180-fortsättning förblev på state `0x8008` och
`frameHash=0xe50a1c19`. Räknaren vid `0x80227b74` gick från `0x62` till
`0x66`, medan inga nya swaps skedde. Ett default-off Type3-rasterdiscard
användes därefter enbart som CPU/FIFO-sond från den rena f2180-checkpointen.
Vid f2300 hade gästen avancerat till:

```text
swaps             2722 -> 2730
fastfills         488 -> 494
front/back/count  2/0/3
draw packets      391658
fbzMode           0x460
```

Alla åtta swaps inträffade vid frame 2202. De var samma legitima,
heltalsbaserade resetfamilj som tidigare: direkta writes vid `0x80102a80`
varvade med Type1 `target=0x4a` vid `0x80102ab4`. En exakt replay med
`fbzMode`- och swapfilter fångade 120 registerskrivningar genom intervallet.
Varje värde var `0x60` eller `0x460`; RGB-maskbit `0x200` förekom aldrig,
inte ens kort mellan swaparna. Presentation eller sampling av slutregistret
kan därför inte förklara den uteblivna färgen.

Gästkoden har en riktig RGB-initierare vid `0x801028f8`:

```text
state = (state & 0xfffdffff) | 0x200
store state at object + 0x26c
```

Ett PC-spår över hela f2180--f2204-resetfönstret gav noll träffar i
`0x80102880..0x80102920`; resetsekvensen anropar alltså inte initieraren.
De senare FIFO-publicerarna kring `0x80103030`/`0x80103190` kopierar redan
byggda stateord till Type1-paket och är inte källan som rensar RGB-biten.

Reproducerbara filer:

```text
/tmp/gaunt-clean-f2180.warm
/tmp/gaunt-clean-f2140-f2180.log
/tmp/gaunt-discard-f2300.warm
/tmp/gaunt-discard-f2180-f2300-fbz-swaps.log
/tmp/gaunt-rgb-init-f2180-f2204.log
/tmp/gaunt-fbz-publishers-f2140-f2141.log
/tmp/gaunt-state8008-handler-full-f2140-f2141.log
/tmp/gaunt-state8008-strings.log
```

Nästa smala gräns är RGB-stateobjektets livscykel före publiceringen: hitta
callern som normalt går genom `0x801028f8`, jämför den med resetvägen vid
f2202 och identifiera vilken legitim renderfas som väljer depth-only-
initialisering. Forcera fortfarande inte bit `0x200`; vi har nu bevis för
att avvikelsen ligger före FIFO-publiceringen.

#### Isolerad RGB-masksond bevisar komplett scenrendering bakom depth-passen

Den riktiga funktionsgränsen för RGB-initieraren är `0x8010281c`. Dess två
direkta callers är `0x80102adc` och `0x80106028`. I resetvägen laddar
`0x80102ad0` pekaren vid `0x80262d64 + 0x394`; den är noll i samtliga
kontrollerade checkpoints från f1080 till f2180, så grenen vid `0x80102ad4`
hoppar normalt över initieraren och anropar `0x80105ea0`. Den uteblivna
initierarkörningen är därmed inte i sig en ny regression.

För att se vad den verifierade depth-only-passen faktiskt bär infördes den
default-off diagnostiska flaggan:

```text
EUTHERDRIVE_GAUNTDL_EXPERIMENT_VOODOO_SETUP_FORCE_RGB_MASK=1
```

Flaggan påverkar endast setup-rasteriserarens färgmask. Aux-write,
depth-test, gäststate, FIFO och swaplogik lämnas orörda. En viktig
reproducerbarhetsdetalj är att `run-gauntdl-baseline.sh` kör den befintliga
`Release`-DLL:n; ett vanligt Debug-bygge uppdaterar inte proben.

Efter ett korrekt `Release`-bygge gav en enda f2140--f2141-frame:

```text
ren:    texturedRasterPixels=0       buffer0 nz=8960
force:  texturedRasterPixels=267     buffer0 nz=9168
CPU/FIFO/trianglar: identiska
```

En full A/B-replay från den gemensamma rena f2180-checkpointen genom swaparna
vid f2202 till f2204 gav identiska CPU-, FIFO-, triangel- och swapräknare.
Endast färgskrivningen skilde:

```text
ren:    texturedRasterPixels=0       raster buffers=0/0/0
force:  texturedRasterPixels=269188  raster buffers=5384/0/263804
front/back/count:                     2/0/3 i båda grenarna
```

Den tvingade buffer 2 visar en verklig Gauntlet Dark Legacy-scen med
texturerad värld, objekt, diagnostiktext och Gauntlet-logotyp. Assetkedjan,
Type3-geometrin, textursamplingen och stora delar av färgkombineringen är
alltså verkliga och sammanhängande. Den rena buffer 2 är samtidigt nästan
helt vit. A/B-bilden ligger här:

```text
artifacts/gauntlet-probe/gauntdl-rgb-mask-ab-f2204-20260727.png
```

Force-resultatet innehåller överritning och är inte en kandidat för
produktionsdefault. Det bevisar i stället att nästa kausala gräns är varför
gästen endast publicerar depth-only-state `0x60`/`0x460` i den här fasen, och
varför den avsedda RGB-passen inte följer. Behåll flaggan default-off och
använd den som en visuell provenance-sond.

Den explicita råbuffertsonden
`EUTHERDRIVE_GAUNTDL_EXPERIMENT_VOODOO_FORCE_RENDER_BUFFER_INDEX` valde
tidigare rätt index men `TryRenderLfb` läste ändå den frusna
`_presentedColorBuffer` när MAME-vblank-timing var aktiv. Den vägen hoppar nu
över swapkopian endast när ett explicit giltigt råbuffertindex anges. Normal
presentation är oförändrad. Från den sparade force-checkpointen gav
`FORCE_RENDER_BUFFER_INDEX=2`:

```text
frameHash=0x0dc6a910
framebuffer nonBlack=307200 colored=216273
chosen buffer=2
```

Utan explicit buffertval är den tidigare vblank-presentationen fortfarande
`frameHash=0xd0bd343b` och `colored=21937`. Den exporterade 640x480-bilden
finns i:

```text
artifacts/gauntlet-probe/gauntdl-rgb-mask-forced-presented-f2204-20260727.png
```

En fortsatt fullrenderad force-replay till f2220 bekräftar att bilden inte är
en statisk buffertrest. Med samma råa frontbuffer 2 ändrades framehashen:

```text
f2205  0x6d2f6f93  drawPackets=374572
f2208  0x8db79678  drawPackets=375365
f2212  0xdcea7f9b  drawPackets=376108
f2216  0x62e4700f  drawPackets=377113
f2224  0x53118bb5  drawPackets=377249
```

Vid f2220 hade force-passen skrivit ytterligare `233619` texturerade pixlar
till buffer 2 och den exporterade bilden innehöll `276706` färgade pixlar.
Värld, objekt och diagnostiköverlägg förändras alltså över tid. Den korta
platån f2216--f2223 var en normal FIFO-väntan: CPU:n rörde sig genom flera
gästfunktioner och Type3-publiceringen återupptogs vid f2224. Den ska inte
behandlas som ett nytt stall.

Den senare bilden finns i:

```text
artifacts/gauntlet-probe/gauntdl-rgb-mask-forced-presented-f2220-20260727.png
```

Reproducerbara filer:

```text
/tmp/gaunt-rgb-ab-release-clean.log
/tmp/gaunt-rgb-ab-release-force.log
/tmp/gaunt-rgb-clean-f2180-f2204.log
/tmp/gaunt-rgb-force-f2180-f2204.log
/tmp/gaunt-rgb-force-f2204.warm
/tmp/gaunt-rgb-clean-f2204_buf2.ppm
/tmp/gaunt-rgb-force-f2204_buf2.ppm
/tmp/gaunt-rgb-force-f2204-raw-present.log
/tmp/gaunt-rgb-force-f2204-default-present.log
/tmp/gaunt-rgb-force-f2204-f2220.log
/tmp/gaunt-rgb-force-f2220.warm
/tmp/gaunt-rgb-force-f2220-f2224.log
```

#### Gästens cached RGB-bit är den kausala gränsen

En allmän, default-off och exakt PC-watch finns nu för smala caller-spår:

```text
EUTHERDRIVE_GAUNTDL_TRACE_CPU_WATCH_PCS=80102ad0,80102adc,8010281c,80106028
EUTHERDRIVE_GAUNTDL_TRACE_CPU_WATCH_LIMIT=128
```

Den jämför fysisk PC och loggar frame, instruktion, `ra`, argument-, retur-,
saved- och stackregister utan att aktivera den betydligt dyrare breda
CPU-tracen. En f2180--f2204-körning träffade `0x80102ad0` fyra gånger men
aldrig `0x80102adc`, `0x8010281c` eller `0x80106028`. Samtliga fyra
resetanrop såg alltså stateobjektets `+0x394` som noll och grenade runt
RGB-initieraren.

Den andra direkta callern till initieraren ligger vid `0x80106028` i
funktionen `0x80105fd0`. Den funktionen sätter eller rensar RGB-bit `0x200`
i cached state vid `+0x26c` och publicerar ändringen när det behövs. Den enda
direkta callern till `0x80105fd0` som hittats i spelbinären är
`0x80105980`, inne i den stora Glide-/window-initieringen som börjar vid
`0x8010520c`.

Direkt avläsning av gästens cached state vid `0x80262fd0` visar att den
saknade RGB-biten föregår den senare bringupen:

```text
f600   0x460
f1320  0x460
f1520  0x460
f1720  0x460
f1920  0x460
f1943  0x060
f1980  0x460
```

Den äldre synliga f1320-bilden motsäger inte detta. När den skapades var
MAME-kompatibel aux/depth-maskning avstängd, och setup-rasteriseraren skrev
då RGB utan att kräva gästens bit `0x200`. När korrekt aux/depth-maskning
aktiverades blev den redan existerande cached-state-avvikelsen synlig som
depth-only-rendering.

Den kausala A/B:n gjordes från samma rena f2180-checkpoint. Endast ett
gästminnesord ändrades före fortsättningen:

```text
0xffffffff80262fd0: 0x460 -> 0x660
```

Backendens RGB-force var inte aktiv. Vid f2202 publicerade gästen själv
`0x660` från `0x80102b40` och `0x801034ac`. Följande paket växlade sedan
legitimt mellan `0x260` och `0x660`, bland annat från `0x80103544` och
`0x80103030`: aux-biten ändrades medan RGB-biten bevarades. Resultatet vid
f2204 blev:

```text
frameHash                  0x0dc6a910
slutligt fbzMode           0x660
texturedRasterPixels       263804
raster buffers             0/0/263804
presenterad colored pixels 216273
```

Den presenterade PPM-filen är byte för byte identisk med backend-force-
resultatet:

```text
sha256  da9cfd705743658599cfdff06ca3ee07fade3610e9e1a94f8a513f78dcaeea76
cmp     identisk
ImageMagick AE  0
```

Detta isolerar felet till initiering eller bevarande av gästens cached
`fbzMode`, före FIFO-publiceringen. Backend-force ska förbli en diagnostisk
sond. Nästa riktiga fix ska sätta den initiala RGB-biten vid den verifierade
Glide-initgränsen, inte OR:a `0x200` på varje senare paket eftersom spelet
har legitima depth-only-lägen.

Den gäststate-reparerade bilden finns i:

```text
artifacts/gauntlet-probe/gauntdl-rgb-state660-presented-f2204-20260727.png
```

Reproducerbara filer:

```text
/tmp/gaunt-rgb-watch-f2180-f2204.log
/tmp/gaunt-f600-rgbstate-patch-f601.log
/tmp/gaunt-f600-rgbstate-patch-f610.log
/tmp/gaunt-rgb-state660-f2180-f2204.log
/tmp/gaunt-rgb-state660-f2204.warm
/tmp/gaunt-rgb-state660-f2204-presented.ppm
/tmp/gaunt-rgb-state660-f2204-presented.png
```

#### Kallspår isolerar och reparerar den uteblivna WinOpen-RGB-initen

En kallkörning med exakt PC-watch nådde den laddade WinOpen-funktionen
`0x8010520c` vid Voodoo-frame 416. Callern vid `0x800e1b50` skickade:

```text
a0/a1/a2/a3       0/7/0/0
stack +0x10..1c   1/2/1/0x802954b0
```

Det första stackargumentet är alltså `nColorBuffers=1`; den uteblivna
RGB-masken beror inte på ett trasigt callerargument. Binärkoden senare i
samma WinOpen-funktion laddar detta ord vid `0x80105978` och anropar
RGB-maskfunktionen `0x80105fd0`, men kallspåret nådde aldrig den sena delen.
Bringupens WinOpen-felåterhämtning låter spelet fortsätta efter den partiella
initen utan att återskapa denna avsedda stateeffekt.

En exakt skrivvakt på `0x80262fd0` visade kontrollens statekedja:

```text
loader  0x800103a4   0x00000 -> 0x00000
state   0x801033e8   0x00000 -> 0x00060
state   0x80103478   0x00060 -> 0x10078
state   0x80103510   0x10078 -> 0x10478
reset   0x80103478   0x10478 -> 0x00460
```

Resetanropet kommer från `0x801035a8` med `a0=0`. Funktionen maskar om flera
andra `fbzMode`-fält men bevarar bit `0x200`. Den skapar därför `0x660` om
RGB-biten redan finns och `0x460` om den saknas.

Den nya bringup-fixen:

```text
EUTHERDRIVE_GAUNTDL_FIX_RUNTIME_GLIDE_WINOPEN_RGB_MASK=1
```

är guardad på exakt WinOpen-entry, dess verifierade prolog, statepekaren
`0x80262d64` och ett rimligt faktiskt `nColorBuffers`-argument. Den sätter
endast bit `0x200` en gång när WinOpen begär minst en färgbuffer.
`run-gauntdl-baseline.sh` aktiverar den som default men accepterar explicit
`=0` för kontrollkörningar.

Kall kandidat-A/B till f450 gav:

```text
frame 416 fix        0x00000 -> 0x00200
gäststate            0x00260 -> 0x10278 -> 0x10678
f450 cached fbzMode  0x10678
f450 Voodoo reg 0x44 0x10678
```

En fortsättning med en miljon CPU-steg tog sedan samma resetväg som
kontrollen:

```text
kontroll  0x10478 -> 0x00460
kandidat  0x10678 -> 0x00660
```

Kandidatens Voodoo-register `0x44` slutade också på `0x660`. Detta binder
samman kall WinOpen-init, gästsidans bevarande, FIFO-publiceringen och den
tidigare byte-identiska f2204-bilden. Backendens force-RGB-flagga behövs inte
för fixvägen och förblir default-off.

Reproducerbara filer:

```text
/tmp/gaunt-stop-winopen.log
/tmp/gaunt-rgb-state-write-cold-f450.log
/tmp/gaunt-rgb-state-write-f450.warm
/tmp/gaunt-rgb-winopen-fix-cold-f450.log
/tmp/gaunt-rgb-winopen-fix-f450.warm
/tmp/gaunt-rgb-winopen-fix-f450-extra1m.log
```

Den kallstartade kandidatgrenen fortsattes därefter från f450 till f600 utan
gästminnespatch, backend-force eller extra CPU-serie. Resultatet behöll
`fbzMode=0x660` genom 120 swaps och 248045 texturwrites. Den rena
fortsättningscheckpointen är:

```text
/tmp/gaunt-rgb-winopen-fix-f600.warm
/tmp/gaunt-rgb-winopen-fix-f450-f600.log
```

#### Ren WinOpen-kedja bevarar RGB genom f2300

Den kallstartade kandidatgrenen fortsattes fullrenderad, utan gästminnespatch,
backendens force-RGB eller extra CPU-serie:

```text
f600 -> f900 -> f1200 -> f1500 -> f1800 -> f2000 -> f2204
slutligt fbzMode vid varje checkpoint: 0x660
f2204 Type3=135176 swaps=400
```

Rå buffer 0 vid både f900 och f1200 innehåller riktig Gauntlet-grafik:
diagnostik-/objektvyn har läsbar text, modeller och bakgrund. Detta är den
första fulla kallkedjan som visar att WinOpen-fixen ensam bevarar RGB långt
efter initieringen; den diagnostiska backend-forcen behövs inte.

Frame-numret är däremot inte fas-ekvivalent med den äldre f2204-referensen.
Den här vanliga fast-cadencen har vid f2204 bara 400 swaps och 135176
Type3-paket, medan den äldre scenreferensen låg kring 2730 swaps och 391000
Type3-paket. Den rena kedjan är alltså fortfarande tidigare i guestens
CPU/FIFO-fas trots samma frame counter.

En två-frame `INPUT_C`/FIRE 3-puls vid f2204--f2206 gav en riktig
gästreaktion: till f2224 ökade swaps med 56, Type5-uppladdningar återupptogs
och frontbufferten blev vit under övergången. Fortsättning till f2300 gav:

```text
fbzMode=0x660
swaps=850
Type3=165229
Type5=29643
texture writes=835109
buffer 0 colored pixels=302221
```

Rå buffer 0 visar fortfarande diagnostik-/objektvyn, inte den senare
dungeon-scenen. FIRE 3-resultatet ska därför inte användas som bevis för att
den rena fast-cadencen redan nått samma spelstate som den äldre scenreferensen.
Nästa pass ska nå den senare CPU/FIFO-fasen reproducerbart och därefter göra
inputprovet där.

Verifierad bild:

```text
artifacts/gauntlet-probe/gauntdl-winopen-rgb-clean-fire3-f2300-20260728.png
sha256=43eed62e34f817bed1587eae6f7a7c493fd4c2d8381bae720eeaff3bb00b5221
```

Reproducerbara checkpointar och loggar:

```text
/tmp/gaunt-rgb-winopen-fix-f900.warm
/tmp/gaunt-rgb-winopen-fix-f1200.warm
/tmp/gaunt-rgb-winopen-fix-f1500.warm
/tmp/gaunt-rgb-winopen-fix-f1800.warm
/tmp/gaunt-rgb-winopen-fix-f2000.warm
/tmp/gaunt-rgb-winopen-fix-f2204.warm
/tmp/gaunt-rgb-winopen-fix-fire3-f2224.warm
/tmp/gaunt-rgb-winopen-fix-fire3-f2300.warm
/tmp/gaunt-rgb-winopen-fix-f600-f900.log
/tmp/gaunt-rgb-winopen-fix-f900-f1200.log
/tmp/gaunt-rgb-winopen-fix-f1200-f1500.log
/tmp/gaunt-rgb-winopen-fix-f1500-f1800.log
/tmp/gaunt-rgb-winopen-fix-f1800-f2000.log
/tmp/gaunt-rgb-winopen-fix-f2000-f2204.log
/tmp/gaunt-rgb-winopen-fix-fire3-f2204-f2224.log
/tmp/gaunt-rgb-winopen-fix-fire3-f2224-f2300.log
```

#### Ren senfas visar riktig world-grafik utan RGB-force

Frame counter var inte rätt mått för att nå den äldre world-referensen.
Fortsättningen gjordes därför från den rena pre-input-snapshoten vid f2204
med full rasterisering och mätta extra CPU-steg. Ingen guest-patch,
backend-force eller input var aktiv.

Från den tidigare sparade +20m-checkpointen gav ytterligare 100m steg:

```text
total extra 45m   Type3=200457 swaps=400
total extra 70m   Type3=236647 swaps=400
total extra 95m   Type3=272869 swaps=400
total extra 120m  Type3=309107 swaps=400
```

Rå buffer 0 vid total +120m visar en stor färgad Gauntlet-figur,
miljödetaljer och diagnostiktext. Den korrekta RGB-biten producerar alltså
verklig texturerad grafik långt in i objektpasset.

Nästa fullrenderade 60m-intervall träffade den sena fasgränsen:

```text
total extra 140m  Type3=338291 swaps=400
total extra 160m  Type3=357516 swaps=860 Type5=31227
total extra 180m  Type3=376255 swaps=860 Type5=36232
```

Vid total +180m innehåller rå buffer 0 en sammanhängande dungeon/world-bild:
golv och väggar, flera figurer och en stor grön effekt är synliga under
diagnostiköverlägget. Buffer 1 och 2 är tomma i denna exakta fas. Bilden har
fortfarande horisontella band och stora färg-/blendfel, men den kommer från
den rena kallstartade WinOpen-fixkedjan. Detta sluter den tidigare saknade
kausala länken från WinOpen-init till verklig world-rendering utan
`SETUP_FORCE_RGB_MASK` eller minnespatch.

Verifierad bild:

```text
artifacts/gauntlet-probe/gauntdl-winopen-rgb-clean-world-extra180m-20260728.png
sha256=0037cbd065b7ee57f4da6c91b53782311af50bfef7536d54f987d69bb652f180
```

Reproducerbara filer:

```text
/tmp/gaunt-rgb-winopen-fix-f2204-extra120m.warm
sha256=2e1b1ba02710eb18bddce995a61481083a1597ccf989ff111824dd01700ac315
/tmp/gaunt-rgb-winopen-fix-f2204-extra180m.warm
sha256=542a638cab9ec70d8882b940a4137d9a72791198401205a231b8d8c12961a5dc
/tmp/gaunt-rgb-winopen-fix-f2204-extra20m-to120m.log
/tmp/gaunt-rgb-winopen-fix-f2204-extra120m-to180m.log
/tmp/gaunt-rgb-winopen-fix-f2204-extra120m-buffer0.png
/tmp/gaunt-rgb-winopen-fix-f2204-extra180m-buffer0.png
```

Nästa bildgräns är nu de horisontella banden och den gröna
färg-/blenddominansen i world-bilden. RGB-state, scene progression,
buffertval, assetkedja och grundläggande textursampling är verifierade och
ska inte öppnas igen utan ny motsägande evidens.

#### Naturlig state-0x8008-progression rensar world-bilden

FIRE 3 når fortfarande runtime-inputbryggan korrekt i den sena checkpointen:

```text
kontroll p1=0x00000000
FIRE 3  p1=0x00000800
```

Två- och tio-frame-pulser gav ändå identiska CPU-, FIFO-, swap- och
bildresultat. Detta är korrekt för denna fas: huvudstate är redan `0x8008`,
medan den kodsignaturbevakade diagnostic-exit-bryggan avsiktligt gäller
`0x8000` och `0x8007`. Latchen ska inte forceras igen efter genomförd exit.

Den rena +180m-snapshoten fortsattes därför utan input i ytterligare 20m
fullrenderade CPU-steg:

```text
total extra 185m  Type3=380917 swaps=860
total extra 190m  Type3=385591 swaps=860
total extra 195m  Type3=390177 swaps=860
total extra 200m  Type3=394803 swaps=860
fbzMode=0x660
```

Vid +200m har den tidigare kraftiga horisontella bandningen nästan helt
försvunnit. Buffer 0 visar sammanhängande golvperspektiv, flera figurer,
miljötexturer och en mjuk grön ljus-/partikeleffekt. Den gröna dominansen vid
+180m var därför huvudsakligen en pågående animerad world-effekt, inte ett
globalt RGB- eller color-combine-fel.

Den äldre sena referensen låg kring 391000 Type3-paket men hade fler swaps
från en annan frame-/CPU-cadence. Den rena +200m-körningen når nu samma
Type3-klass och en starkare world-bild; swap-totalen ska inte längre användas
ensam som fasidentifierare.

Verifierad bild:

```text
artifacts/gauntlet-probe/gauntdl-winopen-rgb-clean-world-extra200m-20260728.png
sha256=f6b8b9ebd6a71fc77255f20c77595e8f133862182bd2153be34085a85e3dcab4
```

Reproducerbara filer:

```text
/tmp/gaunt-rgb-winopen-fix-f2204-extra200m.warm
sha256=404a9dbcc1974db568e5af87a000357080f8c26429b03c83035e25a34a98a246
/tmp/gaunt-rgb-winopen-fix-f2204-extra180m-to200m.log
/tmp/gaunt-rgb-winopen-fix-f2204-extra200m-buffer0.png
/tmp/gaunt-rgb-winopen-fix-extra180m-inputtrace-off.log
/tmp/gaunt-rgb-winopen-fix-extra180m-inputtrace-on.log
```

Nästa gräns är nu diagnostik-/objektöverläggets livscykel och fortsatt
normal state-`0x8008`-progression, inte RGB-init, grön color-combine eller
den nästan försvunna övergångsbandningen.

#### Isolerad diagnostic-enable A/B visar den rena world-scenen

Det kvarvarande kod-/objekttextlagret styrs fortfarande av guestordet
`0x80227b9c`. Renderaren läser ordet vid `0x800c7a64`; guest-PC
`0x80019ef0` återassertar värde ett. Den redan befintliga default-off-sonden
blockerar endast exakt denna adress/PC/värde-kombination.

En en-frame A/B var hashneutral eftersom inga nya trianglar ritades och den
gamla frontbufferten låg kvar. En 20-frame A/B från den rena +200m-snapshoten
gjorde däremot rendergränsen synlig:

```text
kontroll:
  Type3-delta=1028
  textured pixels=429056
  frameHash=0xa4bb2452

diagnostic-enable undertryckt:
  fyra verifierade träffar vid pc=0x80019ef0
  Type3-delta=592
  textured pixels=805408
  frameHash=0xe69ba51a
```

I sondgrenen försvinner `Code version`, fog-/renderinställningarna,
`No Nodes have this object` och `Extra Info`. Den underliggande bilden visar
samtidigt en sammanhängande world-scen med fyra figurer, perspektivgolv,
dimma och den gröna ljus-/partikeleffekten. `Version DL 2.4` ligger kvar som
ett separat lager.

Detta bevisar att de stora bildfelen vid +200m inte kommer från
world-renderern. De återstående textraderna är ett separat
diagnostik-/objektlager ovanpå en fungerande scen. Sonden ska ändå förbli
default-off: den ändrar drawlistan och därmed CPU/FIFO-arbetet, och tidigare
tester visar att undertryckning ensam inte ändrar huvudstate. Nästa riktiga
gräns är varför `0x80019ef0` fortsätter återasserta enable i state `0x8008`,
inte att permanent blockera skrivningen.

Visuellt orakel:

```text
artifacts/gauntlet-probe/gauntdl-clean-world-without-diagnostic-overlay-f2224-20260728.png
sha256=d0ba40c583964fd4d3c74703ac5e68b3cf0d4f12bddbc9d815e96abda575da73
```

Reproducerbara filer:

```text
/tmp/gaunt-rgb-winopen-fix-extra200m-overlay-control-f2224.log
/tmp/gaunt-rgb-winopen-fix-extra200m-overlay-control-f2224.png
/tmp/gaunt-rgb-winopen-fix-extra200m-overlay-suppress-f2224.log
/tmp/gaunt-rgb-winopen-fix-extra200m-overlay-suppress-f2224.png
```

#### Korrigering: den rena +200m-punkten är state 0x8001

En direkt RAM-dump korrigerar antagandet ovan: little-endian-ordet vid
`0x80227ab0` är `0x00008001`, inte `0x8008`. FIRE 3 når runtime-record 0
som current/latch `0x0800/0x0800`, medan edgefältet förblir noll.
Exit-bryggan ska ändå inte utökas till `0x8001` utan mer evidens.

En kausal A/B satte endast spelets egen exit-latch `0x80227ec8` till ett:

```text
kontroll f2224:
  Type3-delta=1028
  buffer 0 behåller den färgade world-/object-vyn

latch f2224:
  Type3-delta=0
  buffer 0 blir helvit
  swaps fortsätter 860 -> 924
```

Latchgrenen fortsattes därefter naturligt. Till f2304 var bufferten
fortfarande vit medan Type5 och swaps fortsatte. Mellan f2304 och f2324
startade en ny renderer; vid f2364 var resultatet:

```text
swaps=1394
Type3-delta=3665
textured pixels=8354568
frameHash=0xe9b8da99
```

Rå buffer 0 visar då en annan diagnostiksida med brun panelmosaik, upprepade
glypher och de gamla diagnostiktexterna. Latchen lämnar alltså den färgade
object-vyn men går inte till gameplay. `0x8001` får därför inte läggas till i
`FIX_RUNTIME_DIAGNOSTIC_EXIT_BRIDGE`.

Callerspåret visar samtidigt:

```text
inputnormalisering  0x80014cc0 -> 0x80019b9c
statisk debuglista  0x800157fc -> 0x800b36bc
object-viewer       0x800b39c4 -> 0x800b2904 (index 0x22)
```

`0x80227b9c=1` skrivs ovillkorligt av den generella inputnormaliseringen och
är inte en state-specifik debugflagga. Den befintliga suppressionen ska
förbli en visuell accelerator/sond. Den riktiga fortsättningen är
diagnostikens state-/sidlivscykel som håller statiska record 0x22 aktivt,
inte inputpolaritets-, RGB- eller render-enable-heuristik.

Reproducerbara filer:

```text
/tmp/gaunt-rgb-winopen-fix-extra200m-mainram.bin
/tmp/gaunt-rgb-winopen-fix-extra200m-input-callers-diagrender-f2204-f2224.log
/tmp/gaunt-rgb-winopen-fix-extra200m-object-viewer-callers-f2204-f2224.log
/tmp/gaunt-rgb-winopen-fix-extra200m-state8001-control-f2224.log
/tmp/gaunt-rgb-winopen-fix-extra200m-state8001-latch-f2224.log
/tmp/gaunt-rgb-winopen-fix-extra200m-state8001-latch-f2364.warm
/tmp/gaunt-rgb-winopen-fix-extra200m-state8001-latch-f2364-buffer0.png
```

#### State 0x8001 är en tidsstyrd copyright-fas, inte en input-blockering

Dispatchen för huvudstate `0x8001` och `0x8002` går till
`0x800823fc`. Handlern visar copyright-/versionsidan och läser
`0x80227ec8` som sin exit-latch. Den accepterar input-edge via
`0x80081cc8(0)` med mask `0x06aa`, men endast i development-läge.
`0x80227a68=1` kommer från `0x800c80b4` och betyder produktionsläge
(`Version`, inte `Development Version`); i detta läge är sidan avsiktligt
inte knappskippbar.

En sen Fight-puls bevisar att inputkedjan fram till handlern fungerar:

```text
0x80019b9c normaliserar pulsen
0x80227ba4: 0x00000000 -> 0x00000200
0x800823fc kör direkt därefter i state 0x8001
```

Handlern väntar i stället på fade-/timer-maskinen `0x80054960`.
Dess objektläge i `0x8019d328..0x8019d33c` avancerar naturligt:

```text
timer  0x8019d330: 0x000000a8 -> 0x000000aa
fas    0x8019d334: 0x00000053 -> 0x00000054
hold-mål från tabellen: 0x18f
fade-out-längd:          0x00dc
```

Vid nuvarande 60000-step-cadence anropas state-handlern ungefär var nionde
Voodoo-renderbild. Den kvarvarande hold- och fade-tiden motsvarar därför
ungefär 2800 emulerade bilder från +200m-checkpointen. Timern är långsam men
inte frusen.

Två default-off RAM-patcher användes endast som kausala tidsbrackets:

```text
0x8019d330=0x031e:
  hold-målet nås och fade-state går 2 -> 3

0x8019d330=0x03fa:
  fade-out blir klar
  0x80054960 returnerar 2
  0x80082940 skriver exit-latch 1
  huvudstate går naturligt 0x8001 -> 0x8007
```

Detta är en starkare gräns än den tidigare direkta latchpatchen: spelets egen
timer, fade-funktion och handler producerar övergången. Ingen inputbrygga,
state-guard eller produktionsfix ska utökas för state `0x8001`.

Fortsättning från den nya state-`0x8007`-snapshoten visar en riktig tung
player-/model-load. Loggen innehåller `ResetModels Timeout`,
`players/%s/%s` och flera `/d0/%s/%s`-laddningar. Till f2284 har swaps gått
`924 -> 1092`, texture writes under passet är `630088`, och CPU:n arbetar vid
`0x801093dc`. Råbufferten är helvit medan laddningen pågår; dispatchen har
ännu inte hunnit tillbaka till state-`0x8007`-handlern `0x80082e1c`.

Reproducerbara filer:

```text
/tmp/gaunt-state8001-fight-late-f2224.log
/tmp/gaunt-state8001-timer-trace-f2215.log
/tmp/gaunt-state8001-timer-threshold-f2244.log
/tmp/gaunt-state8001-timer-complete-f2244.log
/tmp/gaunt-state8001-timer-complete-f2244.warm
/tmp/gaunt-state8007-natural-f2284.log
/tmp/gaunt-state8007-natural-f2284.warm
```

Nästa gräns är att fortsätta state-`0x8007`-asset-loaden tills
`0x80082e1c` körs, och därefter avgöra om dess befintliga latch fullbordar
övergången eller om player-/model-livscykeln har en ny konkret blockerare.

#### State 0x8007 når naturligt loader state 11 och en riktig färgbuffert

Fortsättningen från f2284 visar att `0x80082e1c` återkommer och att den gamla
exit-latchen rensas korrekt `1 -> 0`. Handlern väntar därefter på
load-complete-flaggan `0x8020c54c`, inte på en fastlåst state-latch.

Den första QIO-slotten (`0x80252da0`, stride `0xd8`) stannade länge i
substate 6, men dess interna streamvärden fortsatte att ändras. Detta var
verklig aktivitet, inte en utebliven callback:

```text
f2376  0x8020f178/17c = <tidigare fas>/0x17
f2416  0x8020f178/17c = 0x3c/0x0f
f2456  0x8020f178/17c = 0x63/0x06
```

Vid f2510 fullbordades QIO-steget och den övergripande loader-maskinen vid
`0x8019ccb0` gick därefter naturligt:

```text
f2510..f2536  state 1 -> 2 -> 3
f2544..f2569  state 3 -> 4 -> 5 -> 10 -> 11
```

State 11 anropar `0x800ac2b4` och går vidare till state 12 först när
funktionen returnerar icke-noll. En mikrokörning från f2616 fångade en riktig
QIO-completion vid `0x800ac128`; den andra record-slotten nollställdes och
den aktuella resursen var:

```text
/d0/monsters/zom2/objects.rom
```

Bildsidan är samtidigt klart förbi den helvita laddningsbufferten. Vid f2536
rapporterar Voodoo buffer 0 cirka `163210` färgade pixlar, och vid f2616
cirka `156194`. Detta är riktig renderad färgdata medan den synkrona
modell-/objektbyggaren arbetar.

Efter f2616 återkommer dock inte main-state-handlern inom ett
9,6-miljonersinstruktionsfönster. CPU:n är inte statisk: endpoint-PC flyttar
`0x800ef628 -> 0x800efb74`, och en hot-PC-sond visar fortsatt arbete genom
`0x800efb1c`, `0x800de4fc`, `0x800d13c8` och status-helpern
`0x800111c8`. Kontexten har samma redan kända långsamma timerläge som den
äldre levelE-buildern:

```text
cp0 cause       = 0x00008000
timerPending    = 1
cp0 status      = 0x34007f01
loader state    = 11
load complete   = 0
main state      = 0x8007
```

Detta ska behandlas som nästa konkreta bringup-gräns. Fortsätt inte med en
syntetisk state-11- eller completion-patch. Nästa smala pass ska avgöra varför
den väntande timerkontexten gör den naturliga `0x800ac2b4`-kedjan extremt
långsam och jämföra interrupt-/schedulerbeteendet precis före och efter
`zom2`-completion.

Reproducerbara filer:

```text
/tmp/gaunt-state8007-streamtrace-f2376.warm
/tmp/gaunt-state8007-natural-f2456.warm
/tmp/gaunt-state8007-natural-f2536.warm
/tmp/gaunt-state8007-natural-f2616.warm
/tmp/gaunt-state8007-natural-f2856.warm
/tmp/gaunt-f2617-probe.log
/tmp/gaunt-f2857-hotpc.log
```

#### Temple-katalogens livscykel löser state 11 naturligt

Ett kausalt pass 2026-07-28 spårade den gamla nollängdsrequesten bakåt från
QIO-deskriptorn. `0x800c9810` skrev inte över en giltig längd med noll; gästen
fick redan noll från sidotabellen `0x802549a0 + index*4`. Den tabellen fylldes
vid `0x800aac28` med resultatet från filstorlekshjälparen `0x800c8e24`.

För Temple-index 10--13 fick hjälparen en tom transient katalogsträng och
formaterade därför `/d0//objects.rom`. Uppslaget fungerade korrekt för den
felaktiga sökvägen och returnerade noll. Katalogen var redan tom när
asset-ägaren gick genom `0x800aab8c`; de fyra stabila asset-nycklarna var:

```text
index 10  key 0x80231444  monsters/zom2
index 11  key 0x80231450  monsters/ice2
index 12  key 0x8023146c  monsters/imp2
index 13  key 0x80231448  monsters/pla2
```

Den befintliga `EUTHERDRIVE_GAUNTDL_FIX_RUNTIME_BGLOADMODEL_ASSET_NAMES`
reparerar nu den tomma, ägda stacksträngen exakt vid `0x800aab8c`. Reparationen
är begränsad av PC, instruktionssignatur, index, förväntad nyckel och tom
destination. Den patchar varken filstorlekstabellen, QIO-längden eller loader
state.

Replay från den orörda f2284-snapshoten gav därefter gästens naturliga
FSYS-resultat:

```text
index 10  /d0/monsters/zom2/objects.rom  = 0x27558
index 11  /d0/monsters/ice2/objects.rom  = 0x2b354
index 12  /d0/monsters/imp2/objects.rom  = 0x2fd2c
index 13  /d0/monsters/pla2/objects.rom  = 0x23770
```

Samma fyra värden skrevs av gästen till sidotabellen. En ny kausal checkpoint
vid f2324 fortsatte till f2616 med hash `0xe9b8da99`. Där arbetade QIO med
`/d0/monsters/zom2/textures.rom` och verkliga streamlängder `0x20/0x8020`.
Vid f2696 hade loadern naturligt lämnat state 11 och nått state 13:

```text
f2616  loader state 11, zom2 textures.rom aktiv
f2696  loader state 13, main state 0x8007
```

Ett ytterligare 80-rutorspass till f2776 fullbordades utan gästavbrott och
slutade i den tunga modellbyggaren vid `0x8006eb28`; snapshot-skrivningen
misslyckades endast därför att `/tmp`-kvoten var full. Nästa konkreta gräns är
att fortsätta från den fungerande f2696-checkpointen tills index 10 avslutas
och asset-loopen väljer index 11. Ingen syntetisk state-11- eller
completion-patch behövs.

Verifierbara filer:

```text
/tmp/gaunt-f2284-f2324-temple-directory-owner-fix.log
/tmp/gaunt-f2324-temple-directory-owner-fixed.warm
/tmp/gaunt-f2324-f2616-temple-directory-owner-fixed.log
/tmp/gaunt-f2616-temple-directory-owner-fixed.warm
/tmp/gaunt-f2616-f2696-temple-directory-owner-fixed.log
/tmp/gaunt-f2696-temple-directory-owner-fixed.warm
/tmp/gaunt-f2696-f2776-temple-directory-owner-fixed.log
```

#### Temple-animationerna och hela fyrgruppen går nu naturligt vidare

Den kvarvarande modellbyggarloopen vid `0x8006eb04..0x8006eb30` var inte en
ny schedulerblockering. Modellbufferten innehöll fortfarande initmönstret
`0x01010101`, eftersom animationen öppnades som `/d0//anim.rom` och därför
fick längd noll. Vid animationsöppningen är `s0` filnamnspekaren
`0x8012eb2c`, inte BGLoadModel-recordet; `s1` bär däremot samma stabila
asset-nyckel som katalogreparationen ovan.

Den befintliga, default-påslagna Temple-reparationen känner därför även igen
`anim.rom` och väljer katalog från exakt de fyra kända `s1`-nycklarna. Den
ändrar bara den tomma sökvägen före FSYS-uppslaget och patchar fortfarande
varken QIO-längd, completionflagga eller loader state.

Fokuserade replaypass från f2616 till f3016 verifierade hela kedjan:

```text
index 10  /d0/monsters/zom2/anim.rom  = 0x04ec  -> status 2
index 11  /d0/monsters/ice2/anim.rom  = 0x10f0  -> status 2
index 12  /d0/monsters/imp2/anim.rom  = 0x0494  -> status 2
index 13  /d0/monsters/pla2/anim.rom  = 0x0cec  -> status 2
```

Mellan animationerna strömmades respektive `textures.rom` och `objects.rom`
med verkliga offsetsteg och slutlängder. Vid f3016 hade loopen lämnat hela
Temple-fyrgruppen och arbetade naturligt på index 14,
`/d0/monsters/death/objects.rom`. Samtliga checkpointpass gav
`frameHash=0xe9b8da99`; den patologiska `0x01010101`-loopen återkom inte.

Senaste fortsättningspunkt:

```text
/tmp/gaunt-f3016-temple-four-assets-fixed.warm
/tmp/gaunt-f2936-f3016-temple-pla2-anim-trace.log
```

Nästa smala gräns är att fortsätta från f3016 genom de efterföljande
indexposterna och observera när BGLoadModel lämnar asset-loopen och
`0x8020c54c` sätts. Reparationen ska inte breddas till fler asset-familjer
utan en ny spårad tomkataloghändelse.

#### BGLoadModel sätter load-complete naturligt vid f3256

Fortsättningen efter Temple-gruppen behövde ingen ytterligare pathfix.
Index 14 (`monsters/death`) och index 15 (`powerups`) strömmade sina verkliga
textur-, objekt- och animationsfiler. För `powerups/objects.rom` var den
fullbordade längden `0x1fee0`; den efterföljande `powerups/anim.rom` gick
också till QIO-status 2.

Direkt efter modellbyggaren skrev spelets egen main-state-handler:

```text
[GAUNTDL:MEM] pc=ffffffff80082e74 write32 ffffffff8020c54c 00000001 mainram
```

Detta är den tidigare väntade load-complete-flaggan. Ingen RAM-, state- eller
completionpatch var aktiv. Samma pass nådde f3256 med en ny naturlig
`frameHash=0xf1cbbd0f`, jämfört med `0xe9b8da99` under asset-loopen.

Ny bästa fortsättningspunkt:

```text
/tmp/gaunt-f3256-post-temple-assets.warm
/tmp/gaunt-f3176-f3256-post-temple-trace.log
```

Nästa pass ska börja från f3256 och observera vad state-`0x8007` gör efter
load-complete, särskilt nästa huvudstate och den första stabila scenbilden.
Asset-path- eller completionreparationen ska inte breddas.

#### FIRE3 lämnar state 0x8007 och återstartar world/render-fasen

Efter load-complete stannade huvudstate på `0x8007` och den presenterade
diagnostikbilden på `frameHash=0xf1cbbd0f`. Draw-paket fortsatte produceras,
men inga nya swapkommandon kom. En fyrarutors FIRE3/Turbo-puls via den normala
inputbryggan vid f3417--f3420 gav den avsedda kausala kedjan:

```text
p1 input bitfield = 0x00000800
diagnostic exit latch 0x80227ec8 = 1
pc 0x80081bec writes main state 0x80227ab0 = 0x8008
```

Ingen direkt state- eller latchpatch användes. Vid f3496 var state `0x8008`,
load-complete fortfarande `1` och latchen rensades därefter naturligt.

Fortsättning till f3576 körde den redan kända legitima resetfamiljen med
direkta `swapbufferCMD=0` vid `0x80102a80` varvade med Type1 target `0x4a`
vid `0x80102ab4`. Swapcount gick `1764 -> 3352`, Type5-upload återkom och
hashen blev `0xe9b8da99`. Detta par ska inte undertryckas; äldre MAME-grundade
pass har redan klassificerat det som state-`0x8008`:s depth-only-reset.

Den vanliga presenterade kopian visar ännu den gamla korrupta overlayn, men
rå Voodoo-buffer 0 innehåller nu gles, tydlig perspektivisk worldgeometri.
Buffer 1 och 2 är tomma. Det bevisar att world-rendering har börjat, men att
en komplett RGB-frame ännu inte publicerats.

State-räknaren vid `0x80227b74` gick `0 -> 2` mellan f3496 och f3576. Fram
till f3656 kom ingen ny räknar- eller state-skrivning och Voodoo förblev
stilla, medan hot-PC-profilen visade fortsatt CPU-side world/modelarbete i
`0x800af794..0x800afa80` och `0x800ca58c`.

Ny bästa fortsättningspunkt:

```text
/tmp/gaunt-f3656-state8008-counter.warm
/tmp/gaunt-f3576-f3656-state8008-counter.log
/tmp/gaunt-f3656-f3657-hotpc.log
```

Nästa smala gräns är ägarskapet för world/model-fasen kring
`0x800af794..0x800afa80`: avgör vilken completion eller listpublicering som
ska återvända till state-`0x8008`-handlern och nästa RGB-pass. Ändra inte
swap-paret, RGB-masken eller buffer-valet utan ny kausal evidens.

#### Den ogiltiga world-listcykeln är avslutad vid f3668

En fokuserad CPU-watch vid loophuvudet `0x800af794` och loopslutet
`0x800afa78` visade att den stillastående fasen inte traverserade verkliga
world-noder. `s0` växlade deterministiskt mellan sentinelvärdet
`0xffffffffffffffff` och `0x00000000165cf000`. Det senare ligger utanför
32 MiB main RAM och uppstår när sentinel-adressens nästa-pekare läses genom
den fysiska adressmasken.

Den nya, signaturvaktade bringup-reparationen
`EUTHERDRIVE_GAUNTDL_FIX_RUNTIME_WORLD_LIST_INVALID_SENTINEL_END` känner bara
igen exakt funktionen `0x800af794..0x800afa8b`, de två observerade ogiltiga
nodvärdena och hoppar då till funktionens befintliga epilog. Den patchar
varken listminne, main state eller Voodoo-register.

Ett rent Debug-replay från f3656 till f3676 verifierade:

```text
fixträffar                         2
PC                                0x800ad3a8
gamla hot-PC-listcykeln            borta ur hot-PC-toppen
draw packets                       480103 -> 480714
LFB writes                         253525185 -> 253912190
Type 0/1/3/4-paket                 åter aktiva
main state 0x80227ab0              0x8008
state-räknare 0x80227b74           2
```

Ett separat Release-A/B-pass från samma f3656-snapshot till f3670 gav samma
kausala gräns. Med fixen avstängd låg PC kvar i `0x800af794..0x800afa80`,
draw packets stod kvar på 480103 och LFB writes på 253525185. Med baseline-
fixen på gick PC vidare till `0x800ba3dc`, draw packets till 480529 och LFB
writes till 253690347.

Rå Voodoo-buffer 0 innehöll efter passet 6 254 färgade samplingspixlar och
613 unika färger, med tätare perspektivisk worldgeometri. Den presenterade
640x480-kopian är fortfarande den gamla korrupta diagnostikoverlayn och
swapcount låg kvar på 3352 under de 20 ramstegen. Nästa smala gräns är därför
publiceringen/valet av den aktiva RGB-buffern efter det nu återupptagna
world-rastret, inte ytterligare förändringar av world-listan.

Ny bästa fortsättningspunkt:

```text
/tmp/gaunt-f3676-world-list-repaired.warm
/tmp/gaunt-f3656-f3676-world-list-repaired-clean.log
/tmp/gaunt-f3676-world-list-repaired_buf0.png
```

#### World-buffern publiceras naturligt efter ytterligare 750k CPU-steg

Ingen ny display- eller swapreparation behövdes efter listfixen.
`EUTHERDRIVE_GAUNTDL_FIX_VOODOO_MAME_VBLANK_SWAP_TIMING` visar avsiktligt
den senast faktiskt swappade frontbuffern, så den gamla overlayn vid f3676
var korrekt för det dåvarande Voodoo-tillståndet.

Ett Release-pass från f3676 med kontrollpunkter vid +250k, +500k, +750k och
+1m CPU-steg visade den naturliga gränsen:

```text
+500k  draw packets=480979  swaps=3352
+750k  den legitima 0x80102a80-resetfamiljen börjar swappa
+1m    swaps=3368  frameHash=0xbd21f8ae  colored=300130
```

Alla 16 swappar var gästinitierade direkta `swapbufferCMD=0`, alternerade
front/back `0/1` enligt den redan verifierade dubbelbufferlogiken och
publicerade slutligen rå buffer 0. Den presenterade bilden och rå buffer 0
visar då samma nya world-frame. Bilden består av verkliga perspektiviska
ytor med textur-/färgvariation, men trianglarna är fortfarande kraftigt
överdimensionerade och överlappar fel.

Detta avgränsar nästa fel till world-rastrets vertex-/projektiondata före
Voodoo-publiceringen. Tvinga inte `ChooseRenderBufferIndex`, dränera inte
pending swaps från host-`RenderFrame` och ändra inte det legitima
`0x80102a80`/Type1-0x4a-paret.

Ny bästa fortsättningspunkt:

```text
/tmp/gaunt-f3676-plus1m-natural-swaps.warm
/tmp/gaunt-f3676-plus1m-natural-swaps-save.log
/tmp/gaunt-f3676-plus1m-swap-trace.log
/tmp/gaunt-f3676-plus1m-presented.png
```

#### Type3-proveniens överlever nu warm-snapshot round-trip

Efter den naturliga world-swappen stod command-FIFO-read-headen vid
`0x1400904` framför ordet `0x3c455205`. Tolkat fristående ser det ut som en
Type5 space-3-header med 43 584 payloadord, men en rå FIFO-fönsterdump visade
att ordet återkom mellan vertexfloatar som `0x43935bad`, `0x436aaaa7` och
`0x42cc0000`.

Den riktiga Type3-headern låg fyra ord tidigare:

```text
header 0x0080a8cb  vertices=3  packetWords=19
read   0x3c455205  offset=4 i packet body
next   0x0080a853  exakt vid packet-end + 1
```

Runtime-trackern hade redan en signaturvaktad
`FIFO_ADVANCE_TYPE3_PRODUCER_BODY_HEADER`-väg, men probe-snapshot v12 sparade
FIFO-data och logiska generationer utan Type3 body/end-metadata. Efter reload
stoppade dessutom readiness-kontrollen på den falska Type5-längden innan
body-advance-grenen kunde nås.

Snapshotformat v13 sparar nu Type3 body/end-arrayerna och producentens
pågående packet-state. Äldre snapshots får en smal engångsrekonstruktion
endast när en tidigare giltig Type3-header matematiskt spänner över
read-headen och exakt följs av ytterligare en giltig Type3-header.
Readiness låter därefter den bevisade body-proveniensen nå den befintliga
advance-grenen före vanlig paketlängdsvalidering.

A/B från samma v12-snapshot till f3690 +1m:

```text
före  rd=0x1400904  packets=2563521  Type3=480979
efter rd=0x140ff47  packets=2563620  Type3=481034
```

Efter fixen konsumerades 99 nya paket och 55 nya Type3-draws. Vertextracen
återkom och visade efter de första stripresterna normala små trianglar kring
bland annat `(68,310)` och `(480,318)`. En v13-save/reload behöll exakt
read-index, räknare och `frameHash=0xbd21f8ae` utan legacy-rekonstruktion.

Ny bästa fortsättningspunkt:

```text
artifacts/gauntlet-probe/gaunt-f3690-plus1m-type3-ready-fixed-v13.warm
/tmp/gaunt-f3676-f3690-plus1m-type3-ready-fixed-save.log
/tmp/gaunt-f3690-plus1m-type3-ready-fixed-v13-reload.log
```

Nästa gräns är att fortsätta från v13-checkpointen till nästa naturliga swap
och jämföra den nya presenterade world-framen. Återöppna inte den falska
Type5-headern eller buffer-valet.

#### Type4-proveniens passerar nästa falska Type5-header

Fortsättningen från v13 nådde read-head `0x14115e5`, där `0x00052345`
fristående såg ut som en Type5-header med 42 090 ord. Probens första
FIFO-dump maskade den logiska adressen direkt mot framebufferstorleken och
visade därför fel lagringsfönster. Diagnostiken och legacy-rekonstruktionen
använder nu backendens riktiga `CommandFifoReadStorageIndex`, inklusive
MAME-window-normalisering.

Det korrigerade fönstret visade en exakt Type4-gräns:

```text
header 0x00059604  writer PC=0x800bd18c  packetWords=4
body   0x8c24110f  writer PC=0x800bd190
body   0x00002608  writer PC=0x800bd194
read   0x00052345  writer PC=0x800bd19c  offset=3
next   0x07ff964c  writer PC=0x800bd1bc
```

Type4-trackern hade tidigare misstolkat en Type3-vertexfloat vars låga bitar
råkade vara fyra som Type4-header. Det gjorde att den riktiga headern i
stället märktes som gammal kropp. Type4-headerresynk är nu begränsad till de
tre observerade headerinstruktionerna `0x80103190`, `0x800bd18c` och
`0x800bd1bc`. Readiness släpper verifierad Type4-kropp till advance-grenen
före vanlig paketlängdsvalidering.

Snapshotformat v14 sparar Type4 body/end-arrayerna och producentens pågående
packet-state. Vid laddning av v13 återskapades just detta fyrords-paket från
header, exakt packet-end och nästa giltiga Type4-header.

A/B från samma f3704 +1m v13-stopp med ytterligare en miljon CPU-steg:

```text
före  rd=0x14115e5  packets=2563878  Type3=481170  setupTriangles=2
efter rd=0x141e94e  packets=2568020  Type3=482660  setupTriangles=3
```

Det är +4 142 paket, +1 490 Type3-draws, +1 860 447 LFB-skrivningar, en
fastfill och en ny setup-triangel. Ingen ny swap kom ännu, så den presenterade
hashen är fortsatt `0xbd21f8ae`. En ren v14-reload behöll read-index,
räknare och framehash utan legacy-rekonstruktion.

Ny bästa fortsättningspunkt:

```text
artifacts/gauntlet-probe/gaunt-f3704-plus2m-type4-ready-fixed-v14.warm
/tmp/gaunt-f3704-plus2m-type4-ready-fix.log
/tmp/gaunt-f3704-plus2m-type4-ready-v14-reload.log
```

Nästa gräns är lagringsgenerationen vid read-head `0x141e94e`: det synliga
ordet är `0x42200000`, men slotten bär en äldre logisk generation. Fortsätt
med generation/window-proveniens därifrån; återöppna inte Type3-, Type4-
eller display-bufferfixarna.

#### Direkt frontbuffer-rendering presenteras nu utan påhittad swap

Generationsgränsen vid `0x141e94e` var inte ett nytt stopp. Fortsatt naturlig
produktion flyttade read-headen genom flera generationer och gav tiotusentals
nya paket. Efter ytterligare drygt tjugo miljoner CPU-steg och riktiga frames
var FIFO:n fortsatt aktiv:

```text
packets=2603524  Type3=495889  fastFills=888  swaps=3368
```

Vertexpasset matchade samtidigt MAME:s Type3-dekoder: packet code,
strip/fan-reset, X/Y samt W/S/T låg på samma ord och nya koordinater var
normala 512x384-skärmkoordinater. Ändra därför inte vertexformatet för att
förklara den frusna presenterade bilden.

En registerordnings-trace runt två nya fastfills visade i stället:

```text
fbzMode=0x00000660  front=0  back=1  draw=0
kind=fastfill       swaps=3368
```

Gästen ritade alltså avsiktligt direkt till den aktuella frontbuffern.
Voodoo-scanout ska då visa minnesändringarna utan `swapbufferCMD`, men
`TryRenderLfb` valde alltid den senaste `_presentedColorBuffer`-kopian när
vblank-swap-timingfixen var aktiv. Den kopian uppdateras bara vid swap och
frös därför UI:n trots miljontals nya frontbufferpixlar.

Presentationsvägen använder nu den levande råa frontbuffern när både vald
renderbuffer och aktuell drawbuffer är front. Vanlig backbuffer-rendering
behåller den senast swappade presenterade kopian, och ingen swap eller
bufferrotation forceras.

A/B från samma f3724 +10m v14-snapshot med ett riktigt frame-anrop:

```text
före  swaps=3368  frameHash=0xbd21f8ae
efter swaps=3368  frameHash=0xf78ff636
```

Den nya bilden visar aktuell debugtext och levande worldgeometri. Den är
fortfarande feltexturerad och ofärdig, men är inte längre en gammal
swap-kopia. En v14-reload behöll exakt `frameHash=0xf78ff636`, read-head
`0x1481def` och samtliga Voodoo-räknare.

Ny bästa fortsättningspunkt:

```text
artifacts/gauntlet-probe/gaunt-f3725-direct-front-v14.warm
/tmp/gaunt-f3725-direct-front-frame.png
/tmp/gaunt-f3725-direct-front-present.log
/tmp/gaunt-f3725-direct-front-v14-reload.log
```

Nästa visuella gräns är textur-/depth-fidelitet i den nu korrekt levande
frontbuffern. Återinför inte krav på swap när `fbzMode` väljer front som
drawbuffer.

#### Direkt frontbuffer fångas atomiskt vid VBlank

Fortsatt körning från f3725 visade att den levande frontbuffern ändrades
naturligt utan nya swappar. Mellan f3730 och f3820 avancerade FIFO:n från
`2605892` till `2617842` paket och Type3 från `496363` till `500748`.
Bildinnehållet växlade samtidigt mellan debugtext, stora world-polygoner,
glesa gångytor och vit fastfill-bakgrund.

Detta var två skilda presentationskrav. Den tidigare direkt-frontfixen gjorde
de nya pixlarna synliga, men `TryRenderLfb` läste den råa frontbuffern efter
ett godtyckligt CPU-steg. När gästen ritar direkt till front kan den läsningen
ligga mitt mellan clear och sista draw. Voodoo-backenden kopierar nu den
aktuella frontbuffern till `_presentedColorBuffer` vid `OnVblankStart` när
drawbuffer och frontbuffer är samma. Den vanliga renderingsvägen visar därefter
den stabila VBlank-kopian. Pending riktiga swaps dräneras först och behåller
samma befintliga rotationssemantik.

Fem riktiga frames från samma f3760-snapshot gav efter ändringen:

```text
frame=3765
frameHash=0xba45a267
fifoPackets=2609786
Type3=498014
fastFills=889
swaps=3368
```

Bilden visar en atomisk kombination av versions-/fogtext, world-objekt och
Gauntlet-loggan i den aktiva renderfasen. En ren v14-reload behöll exakt
`frameHash=0xba45a267` och samtliga räknare.

Huvudstate lästes samtidigt direkt ur RAM:

```text
0x80227ab0 = 0x00008008
0x80227ec8 = 0x00000000
```

FIRE3 hade alltså redan lämnat diagnostikstate `0x8007`; exit-latchen är
rensad. Skicka inte ännu en FIRE3-puls för att förklara den nuvarande
objektmixen. Nästa gräns är state-`0x8008`:s world-/kamerapublicering eller
frame completion, inte FIFO-, culling-, TMU-combine- eller buffer-val.

Ny bästa fortsättningspunkt:

```text
artifacts/gauntlet-probe/gaunt-f3765-vblank-front-capture-v14.warm
/tmp/gaunt-f3765-vblank-front-capture.png
/tmp/gaunt-f3765-vblank-front-capture.log
/tmp/gaunt-f3765-vblank-front-capture-reload.log
```

#### Type3-raster använder nu Voodoo 2:s W-baserade fogtabell

En writer-profil av en vit pixel i den stabila VBlank-bilden pekade på
Type3-paket `0x0180a853` vid gäst-PC `0x800bcaa8`. Paketet fortsätter en
strip med vertex `(0,0)` och läser en verklig Alpha-8-texel `0xff` från
TMU0; MAME:s packet-, strip-, TMU- och base-address-semantik gav samma
resultat. Den vita texeln ska därför inte filtreras bort som diagnostikhack.

Den saknade hårdvarudelen låg senare i pixelkedjan. Snapshotten bar:

```text
fogMode  = 0x000000c1
fogColor = 0x00000000
alphaMode= 0x00040400
```

Fogtabell, fog-zoner och 4x4-fogdither var aktiva medan backenden inte
applicerade fog alls. Alpha-test och alpha-blend var däremot avstängda, så
det finns ingen grund för att göra Alpha-8-passet transparent.

Setup-rastret följer nu MAME:s Voodoo 2-ordning efter color combine:

1. interpolera separat framebuffer-W (`Wb`), utan att blanda ihop den med
   TMU0/TMU1-W,
2. konvertera W till Voodoos 16-bitars pseudo-float,
3. slå upp blend/delta i de 64 fogtabellposterna,
4. applicera fog-zontecken och 4x4-dither,
5. blanda med gästens fogColor.

Ett strikt f3820 A/B från samma sparade f3785-state gav identisk
CPU/FIFO/raster-telemetri men skilda bilder:

```text
fog av: frameHash=0xdd7e7069
fog på: frameHash=0xf40cc539
```

Fogskillnaden ligger över perspektivytan och ändrar inte geometri,
texeladresser, FIFO-räknare eller swapantal. f3785 med fog sparades och
laddades om med exakt samma `frameHash=0xc44fefbf` och byteidentisk PPM.

Ny bästa fortsättningspunkt:

```text
artifacts/gauntlet-probe/gaunt-f3820-mame-fog-v14.warm
/tmp/gaunt-f3820-mame-fog.ppm
/tmp/gaunt-f3820-mame-fog.log
/tmp/gaunt-f3785-mame-fog-wb-reload.log
```

Nästa visuella gräns är de stora stripparnas geometri/vertexinnehåll och
återstående pixelsteg (itererad färg/alpha, chroma och blending), inte
single-TMU-val, displaybuffer eller ett påhittat transparent Alpha-8-pass.

#### Explicit pixelsond följer nu även rektangulära fastfills

En fokuserad replay av den vita outputpixeln `(150,180)`, råpixel
`b0@(120,144)`, visade först en motsägelse: det spårade Type3-paketet vid
FIFO `0x149d28e` skrev foggad mörkgrön `0x2fe0`, medan slutpixeln var vit.
En senare vit fastfill fanns vid gäst-PC `0x801027cc` och FIFO `0x14a23af`.

Motsägelsen låg bara i profilen. `TrackPixelLastWriterRect` uppdaterade de
ordinarie rutnätsproven men inte den explicit begärda pixeln. Den begärda
punkten uppdateras nu också när den ligger inuti rektangeln. Samma
f3785-till-f3820-replay ger:

```text
frameHash=0xf40cc539
pixelWriterSample=b0@120,144:cFFFF
writer=PC 0x801027cc fill fastfill FIFO 0x14a23af
```

PPM-filen är byteidentisk med föregående fogreferens. Ändringen är alltså
ren observabilitet och påverkar inte rasterresultatet.

```text
/tmp/gaunt-f3820-pixel-rect-fix.log
/tmp/gaunt-f3820-pixel-rect-fix.ppm
```

Den vita luckan ska nu behandlas som en verklig clear-/framefas, inte som
ett fel i det äldre Type3-paketets textur- eller fogkedja. Nästa kontroll är
ordningen mellan fastfill, färdig world-raster och VBlank-kopian.

#### Fastfill använder nu Voodoos auktoritativa color1 även när den är svart

Den korrekta pixelproveniensen gjorde nästa fel direkt synligt. Fastfillen
vid gäst-PC `0x801027cc`, FIFO `0x14a23af`, kördes med:

```text
color0 = 0xffffffff
color1 = 0x00000000
zaColor= 0x00ffffff
fbzMode= 0x00000660
```

Backenden läste först `color1`, men behandlade RGB565-värdet noll som om
registret saknades och föll tillbaka till `color0`, sedan `zaColor`. Därmed
blev gästens avsiktliga svarta clear vit. Voodoos fastfill-RGB kommer
ovillkorligen från `color1`; `zaColor` hör till aux/depth-clearen. Fallbacken
är borttagen. Det befintliga skyddet mot läckta Type5-headerord är kvar.

Strikt replay från samma f3785-state till f3820:

```text
före: frameHash=0xf40cc539  pixel b0@(120,144)=0xffff
efter:frameHash=0x9daad2a0  pixel b0@(120,144)=0x0000
FIFO packets=2617842  Type3=500748  fastfills=891  swaps=3368
```

Den nya bilden har svart bakgrund i stället för de stora vita luckorna och
visar den riktiga perspektiviska worldgeometrin betydligt tydligare. En
sparad v14-state laddades om med exakt samma hash och byteidentisk PPM.

Ny bästa fortsättningspunkt:

```text
artifacts/gauntlet-probe/gaunt-f3820-mame-color1-fastfill-v14.warm
/tmp/gaunt-f3820-mame-color1-fastfill.ppm
/tmp/gaunt-f3820-mame-color1-fastfill.log
/tmp/gaunt-f3820-mame-color1-fastfill-save.log
/tmp/gaunt-f3820-mame-color1-fastfill-reload.log
```

Nästa visuella gräns ligger åter i worldytornas rasterfidelitet och
kamerainnehåll. Den svarta bakgrunden är nu gästens verkliga clear och ska
inte ersättas av `color0` eller `zaColor`.

#### Baseline-wrappern återaktiverar inte längre nolltransparens

En pixeltrace av det randiga worldgolvet vid f3850 visade flera
`zero-transparent`-rejects trots att den accepterade presetens
`EUTHERDRIVE_GAUNTDL_FIX_VOODOO_ZERO_TEXTURE_TRANSPARENCY` är `0`.
Orsaken var att `run-gauntdl-baseline.sh` sätter `BRINGUP_FAST=1` men inte
exporterade det explicita nollvärdet. Den generiska bringup-fallbacken gjorde
därför nolltexlar transparenta igen i wrapperkörningar och lämnade äldre
polygonlager synliga.

Wrappern exporterar nu hårdvarukorrekt default `0`, men tillåter fortfarande
ett explicit env-override för diagnostiska A/B-körningar. Samma
f3830-till-f3850-state och identisk rastertelemetri gav:

```text
gammal wrapper: frameHash=0xb838f67d
zero write:     frameHash=0xb458dedf
Type3=502044  textured=499  pixels=1814636  zero=970417
```

Ny state och bild:

```text
artifacts/gauntlet-probe/gaunt-f3850-zero-write-v14.warm
/tmp/gaunt-f3850-zero-write.ppm
/tmp/gaunt-f3850-zero-write.png
/tmp/gaunt-f3850-zero-write.log
```

Den tidigare f3850-bilden är fortfarande användbar för geometrifasens
proveniens, men inte som pixelkorrekt baseline. Fortsatta wrapperreplays ska
behålla `ZERO_TEXTURE_TRANSPARENCY=0`.

#### Den randiga panelen är riktig A8-diagnostik och state 0x8008 avancerar

En atomisk VBlank-bild vid f3907 visar den läsbara objektvyn med
`Code version`, `No Nodes have this object`, Gauntlet-loggan och en stor
svartvit panel. Explicit writer-proveniens för presenterad pixel `(450,250)`,
råpixel `b0@(360,200)`, pekar på Type3-paketet vid FIFO `0x053553e4` och
gäst-PC `0x800c5be0`.

Pixeltracen följer paketet hela vägen:

```text
tmode = 0x8c2412cf
tlod  = 0x00002104
tbase = 0xffffe000
addr  = 0x001980
raw   = 0xff
out   = 0xc618
```

Det är den redan auktoritativa record-0-basen och en riktig Alpha-8-texel.
Den vita texeln moduleras till det presenterade gråvita värdet; panelen är
alltså inte en saknad texture page, fel vald TMU eller ett skäl att återinföra
nolltransparens.

Fortsatt naturlig körning med 600 000 CPU-steg per frame växlar mellan
objektvyn och world-/kamerafaser. Huvudstate, completion och latch förblir
korrekta samtidigt som state-räknaren avancerar:

```text
f3927  state=0x8008  counter=0x2c  load-complete=1  exit-latch=0
f3947  state=0x8008  counter=0x36  load-complete=1  exit-latch=0
f3987  state=0x8008  counter=0x4a  load-complete=1  exit-latch=0
```

En två-frame coin-puls vid f3917 och efterföljande två-frame startpuls var
byte- och hashidentiska med respektive no-input-kontroll. Ingen ny FIRE3-,
coin-, start- eller RAM-patch ska därför användas för att forcera denna fas.
Nästa kontroll ska fortsätta samma rena state-`0x8008`-sekvens mot den
tidigare observerade räknarregionen `0x62..0x68` och avgöra om gästen själv
publicerar nästa stabila worldkamera.

Senaste lokala fortsättningspunkt:

```text
artifacts/gauntlet-probe/gaunt-f3987-600k-control-v14.warm
/tmp/gaunt-f3987-600k-control.log
/tmp/gaunt-f3987-600k-control.png
/tmp/gaunt-f3907-white-panel-pixel-trace.log
```

#### Räknaren klassar HUD-tid och probe-watch accepterar nu hexlängd

Fullrenderad progression nådde f4047 utan input eller RAM-patch:

```text
state=0x8008  counter=0x68  load-complete=1  exit-latch=0
frameHash=0xafeda57c  Type3=576283
```

Räknarens riktiga skrivare är gäst-PC `0x800188d0`. Den adderar det
16-bitars tickvärdet `0x80227b48=2` till `0x80227b74` och anropar därefter
hjälpfunktionen vid `0x800187b0`. Disassemblyn visar klassgränser vid
`1`, `0x41` och `0xc1`; `0x68` är alltså inte en state- eller kameratröskel.
En separat default-off sond satte endast räknaren till `0xbe` och verifierade
naturliga writes `0xc0 -> 0xc2 -> 0xc4` utan ändring av huvudstate,
load-complete eller exit-latch. Sonden ska inte promoveras.

De stora screenytorna flyttar sig samtidigt mjukt mellan frames, till exempel
den undertryckta setup-triangeln `x=-611.938 -> -599.938 -> -588.250 ->
-576.812`. Det är deterministisk gästgeometri/kamera, inte återanvänd
FIFO-payload. `fbzMode=0x660` har enligt Voodoo 2-semantiken RGB- och
aux-write på men depth-test av; backenden följer detta och ska inte tvinga
depth-test för att dölja ytorna.

Minneswatchen avslöjade också ett verkligt observabilitetsfel. Dokumenterade
filter som `EUTHERDRIVE_GAUNTDL_TRACE_MEM_ADDRESS=...:0xc` accepterade hex i
adressen men bara decimal i längden och föll tyst tillbaka till en byte.
Längdparsern accepterar nu både decimal och `0x`-prefixad hex, så befintliga
recept bevakar avsett intervall.

Reproducerbara filer:

```text
artifacts/gauntlet-probe/gaunt-f4047-600k-control-v14.warm
artifacts/gauntlet-probe/gaunt-f4059-counter-be-probe-v14.warm
/tmp/gaunt-f4027-f4039-state-counter-owner-corrected.log
/tmp/gaunt-f4047-f4059-counter-be-probe.log
/tmp/gaunt-f4059-counter-be-probe.png
```

Nästa bildgräns är fortsatt ägarskap för world-/kameradata före
vertexemittern. Lägg inte till en `0x68`/`0xc1`-statepatch, tvingat depth-test
eller bredare triangelundertryckning.

#### Sena checkpoints regenererade utan fbiInit3-förorening

En jämförelse av sparade Voodoo-register hittade en verklig skillnad som de
sena checkpointsen hade dolt:

```text
f3676 gammal ren state       fbiInit3=0x00110001
f3690 v13                    fbiInit3=0x00110001
f3704 gammal plus1m v13      fbiInit3=0x428137da
f3704 gammal plus2m v14      fbiInit3=0x43a0afac
f3765..f4047 gamla states    fbiInit3=0x43a0afac
```

De floatlika värdena uppstod i det gamla Type3/Type4-recoveryfönstret och
ärvdes därefter av alla sena snapshots. En register-`0x87`-trace från en
gammal sen state gav inga nya writes; föroreningen fanns redan i snapshotten.

Samma f3690-till-f3704/+1m-väg med nuvarande FIFO-recovery konsumerar
paketströmmen utan någon write till `0x87` och behåller:

```text
fbiInit3=0x00110001
packets=2565606
Type3=481946
read=0x14167e9
```

Vägen regenererades därför fullrenderad från sista rena f3690-v13-staten och
fortsattes med riktig 60k/VBlank-cadence. `fbiInit3` förblev exakt
`0x00110001` genom samtliga nya kontrollpunkter:

```text
f3704  Type3=481946  frameHash=0x7d4fbf45
f3765  Type3=485021  frameHash=0xe6c0d2b5
f3820  Type3=487889  frameHash=0x4c47ea75
f4020  Type3=497639  frameHash=0x2803cbae
f4080  Type3=500773  frameHash=0x9c89e0cb
f4225  Type3=508177  frameHash=0xbfc81d3d
```

Vid ungefär samma Type3-nivå som den gamla f3820-referensen visar nya f4080
samma sammanhängande perspektivgolv, vilket bekräftar att den rena FIFO-vägen
fortsätter rendera normalt. Vid f4225 visar den fortfarande en extrem
world-/kamerafas; korrekt `fbiInit3` löser alltså inte ensamt de stora
screenytorna. Jämför framåt endast inom den regenererade kedjan och använd
inte gammal f3704--f4047-state som registeroracle.

Ny ren fortsättningspunkt:

```text
artifacts/gauntlet-probe/gaunt-f4225-fbi3-clean-60k-current-v14.warm
/tmp/gaunt-f4080-fbi3-clean-60k-current.png
/tmp/gaunt-f4225-fbi3-clean-60k-current.png
/tmp/gaunt-f3690-f3704-plus1m-fbi3-clean-current.log
/tmp/gaunt-f4080-f4225-fbi3-clean-60k-current.log
```

#### Warm-state-replay kräver baseline-presettet

En direkt replay av f4225 utan baseline-presettet såg först ut att ha fastnat:
175 tomma VBlank och 20 miljoner extra CPU-steg gav inga nya FIFO- eller
draw-paket. CPU:n låg i IRQ-dispatchern `0x800de8e0..0x800dea10` med:

```text
status=0x34007f00
cause=0x00008800
epc=0x800bbc34
nileState=0x0020
nilePins=0x02
```

Det var inte ett nytt gästfel. Warm-state serialiserar maskinstate men inte
processens env-konfiguration. Replayen saknade
`EUTHERDRIVE_GAUNTDL_BRINGUP_BASELINE=1` och körde därför inte samma
signaturvaktade bringup-fixar som staten skapades med.

En default-off bred IRQ-suppress-A/B bekräftade varför IRQ:n inte ska forceras:
den återstartade draw inom 100 000 steg men återgick till en gammal fysisk
FIFO-generation. Första felaktiga paketet var Type4 `0x3bf2ac0c` vid
FIFO-offset `0x000232e8`; det skrev `fbiInit3=0x38000000` vid gäst-PC
`0x800c5bd0`. Suppressorn ska förbli av.

Samma f4225-state med baseline-presettet fortsätter omedelbart och rent:

```text
f4225 Type3=508177 LFB=297480631 fbiInit3=0x00110001
f4226 Type3=508205 LFB=297788121 frameHash=0xd855450a
f4227 Type3=508244 LFB=297843859 frameHash=0x3e39c862
f4230 Type3=508385 LFB=297848935 frameHash=0xdaf80748
f4230 fbiInit3=0x00110001
```

CPU-watch-raden visar nu även CP0 status/cause/EPC och Nile-källstatus, så en
framtida riktig IRQ-stall kan skiljas från en replay-konfigurationsmiss utan
att aktivera en suppressor.

Reproducerbara filer:

```text
artifacts/gauntlet-probe/gaunt-f4230-fbi3-clean-baseline-20260729.png
sha256=1ac564bab00e42cb9b83867c59c161b9155cb8ca56872cd48bdd67f8c6de5df6
/tmp/gaunt-f4225-f4230-baseline.log
/tmp/gaunt-f4230-fbi3-clean-baseline-v14.warm
sha256=c3f40b1c4d484b988dd2fb02f8b0c1cb3c023b10ffc4a15b7b997349454f0714
```

#### Async world-loader fortsätter genom state 13 och korta slutchunks

Den rena K2-kedjan nådde loader state 13 med aktiv indexpost 13 och såg först
ut att ligga kvar i modellbyggaren. Gästkoden vid `0x800511f8..0x80051278`
visade i stället ett separat loaderägt async-QIO: callback-recordet publiceras
via `0x8019cd00`, callbacken är `0x80050e84` och completion pollas vid
`0x8005121c`. Begäran var `/d0/monsters/golem/levelK/anim.rom`; QIO-objektet
var korrekt mappat men hade status noll eftersom denna poll inte fick någon
timer opportunity.

Den befintliga, begränsade QIO-servicen känner nu igen tre exakta asyncvägar:
BgLoadModel objects state 1, textures state 3 och world-animation state 13.
Alla kräver sina instruktioner, huvudstate, loader/entry-identitet, callback,
record och mappade QIO-objekt. Servicen skriver varken status, completion,
handle eller loader state; den begär endast samma gästtimer som den generiska
WaitForQio-vägen redan använder.

Replay från f7863 gav därefter naturlig completion:

```text
f7863 loader=13 callback completion=0 qio handle=0x0186 status=0
f7893 loader=31 callback completion=-1 qio handle=-1 status=0x0500
```

I state 31 nådde index 14 `/d0/weapons/textures.rom`. Sista legitima
QIO-chunken var `0x3f0` byte, medan den första vakten endast accepterade en
full `0x2000`-chunk. Asyncvakten accepterar därför nu det faktiska
chunkintervallet `1..0x2000`, fortfarande bakom samma callback-, entry- och
stateidentitet. Replay från f8043 bekräftade att den korta chunken avslutades
av gästen och att nästa riktiga streamchunk startade:

```text
f8043 index=14 entryState=3 request=0x03f0 completion=0 handle=0x7485
f8073 index=14 entryState=3 request=0x1550 completion=2 handle=-1 status=0x0500
f8113 index=14 entryState=3 request=0x2000 completion=0 handle=0x7605
```

Mellan f8043 och f8113 ökade texturskrivningarna från `10,174,731` till
`10,372,039`, swaps från 4,833 till 4,858 och CPU:n förblev aktiv. State 31
är alltså en pågående flerchunkström, inte en ny loaderblockering. Fortsätt
från f8113 tills index 14 lämnar state 3; syntetisera inte completion eller
state 32.

Lokala, reproducerbara checkpoints:

```text
.build-tmp/state13-qio-f7893.warm.gz
.build-tmp/short-qio-f8073.warm.gz
.build-tmp/post-short-qio-f8113.warm.gz
```
