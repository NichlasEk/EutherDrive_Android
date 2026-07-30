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
```

f670-snapshotten återladdades med noll körda frames och gav deterministiskt
`frameHash=0x30e41dc5`, `pc=0xffffffff80078670` och `swaps=3294`.

## Nästa körning

Fortsätt från f2260 för att låta den avslutade `death`-/`temple`-resursvågen
gå mot load-complete/publicering utan att göra om monsterladdningen:

```sh
EUTHERDRIVE_GAUNTDL_WARMUP_STATE=artifacts/gauntlet-probe/gaunt-level-loader-f2260-60k.warm.gz \
EUTHERDRIVE_GAUNTDL_WARMUP_FRAMES=2260 \
EUTHERDRIVE_GAUNTDL_CPU_STEPS_PER_FRAME=60000 \
EUTHERDRIVE_GAUNTDL_EXTRA_SERIES= \
tools/GauntletProbe/run-gauntdl-baseline.sh \
/home/nichlas/roms/MAME/Midway/Vegas/gauntd 2360
```
