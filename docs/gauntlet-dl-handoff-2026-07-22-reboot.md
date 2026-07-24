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
