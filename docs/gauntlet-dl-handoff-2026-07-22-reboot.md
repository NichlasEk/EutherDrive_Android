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
