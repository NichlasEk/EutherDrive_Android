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
```

f670-snapshotten återladdades med noll körda frames och gav deterministiskt
`frameHash=0x30e41dc5`, `pc=0xffffffff80078670` och `swaps=3294`.

## Nästa körning

Fortsätt från f680 för att prova diagnostik-exit utan att göra om
resursladdningen:

```sh
EUTHERDRIVE_GAUNTDL_WARMUP_STATE=artifacts/gauntlet-probe/gaunt-asset-arenas-f680.warm.gz \
EUTHERDRIVE_GAUNTDL_WARMUP_FRAMES=680 \
EUTHERDRIVE_GAUNTDL_EXTRA_SERIES= \
tools/GauntletProbe/run-gauntdl-baseline.sh \
/home/nichlas/roms/MAME/Midway/Vegas/gauntd 700
```
