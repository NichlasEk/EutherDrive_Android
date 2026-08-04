# Gauntlet Dark Legacy grafikcheckpoint — 2026-08-04

## Resultat

Den native EutherDrive-kärnan visar nu en sammanhängande, rörlig spelscen med
spelare, fiender, tunnor, däck, ljus och HUD. De stora vita/gula polygonerna
som täckte skärmen är borta. Körvägen använder inte MAME som backend.

Två separata fel orsakade huvuddelen av korruptionen:

1. Den vanliga Voodoo-command-FIFO:n kunde hoppa förbi en giltig header medan
   resten av paketet fortfarande skrevs. Tre äldre producentheuristiker gjorde
   dessutom packetordningen sämre. FIFO:n väntar nu på kompletta paket, töms
   ordnat vid rendergränsen och kan återhämta sig från en verkligt ogiltig
   läsposition. De tre heuristikerna är avstängda i Gauntlet-baslinjen.
2. Texturernas alpha-värden beräknades inte genom Voodoo 2:s `fbzColorPath`
   och `alphaMode`. Genomskinlig geometri ritades därför som ogenomskinliga
   vita ytor. Rasterizern har nu alpha-test, RGB-blendfaktorer och alpha-plane-
   skrivning för den setup-baserade texturväg som Gauntlet använder.

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

Startaren använder som standard:

```text
.build-tmp/euther-gauntdl-native-alpha-f5383.warm.gz
SHA-256 6b9b9f6a30c21021a255465185d2e4aef6fea177ed8b55cdf2c643c03715120a
```

Snapshoten är 8,9 MiB och självbärande. MAME-dumpar behövs inte när den körs.

## Kvarvarande grafikfel

En svart/ofylld yta finns fortfarande i nedre mitten av däcket. Scenen är
spelbar nog för input- och rörelseprov, men inte grafikfärdig. Nästa
grafikarbete ska spåra just denna ytas Type 3-paket och avgöra om den försvinner
i depth/alpha-test, texture lookup eller packet completion. Alpha ska inte
kringgås och gamla frame-generationer ska inte kompositeras för att maskera
felet.

MAME-importfunktionerna i `GauntletProbe` är endast ett utvecklingsfacit för
RAM, FBI/TMU-register, framebuffer och FIFO-position. Den sparade native
snapshoten och desktopstartaren är den normala körvägen.
