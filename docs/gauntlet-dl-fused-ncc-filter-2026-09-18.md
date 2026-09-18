# Gauntlet DL: sammanslagen NCC-hämtning och filtrering

## Implementation

Experimentet gäller filtrerade NCC-format 1 och 9. Den vanliga vägen gör
fyra texelhämtningar/NCC-uppslag till RGBA och packar sedan kanalerna för
bilinjär filtrering. Kandidaten bygger i stället en härledd NCC-tabell med
R/G förpackade i två 32-bitarsfält och B separat. Fyra råa texelvärden
går direkt till viktningen, utan fyra mellanliggande RGBA-värden.

Format 1 behåller alpha 255. Format 9 hämtar alpha från texelns höga byte
och filtrerar B/A i separata 32-bitarsfält. Samma vikter, `+0x8000` och
skift 16 används. Maximal avrundad kanalsumma är 16744448, under 2^24;
det finns ingen carry mellan de 32-bitarsfälten.

LOD, perspektivdivision, koordinatavrundning, wrap/clamp och Y-flip ligger
kvar i den gemensamma referensvägen. Den nya råhämtningen behåller
bankmappning, delad 32-bitarsläsning när två texlar ligger i samma ord,
omvända 8-/16-bitarslanes, byte-swap och TMU1→TMU0-memory-experimentet.
Andra format, nearest, saknad NCC-LUT och samplediagnostik använder fallback.
GPU-samplecapture-hooken anropas även efter den sammanslagna vägen.

Tabellen är högst 16 KiB totalt för två tabeller per TMU, utöver den
befintliga RGBA-tabellen. Den byggs bara när experimentet används och är
härledd state, inte en ny del av snapshotformatet.

## Cachelivstid

Packningen byggs om tillsammans med befintlig NCC-RGBA-LUT. Den delar
`WriteTmuRegisterOrPalette`-vägens invalidation och återanvänder allokerade
arrayer vid vanliga registeruppdateringar. Om RGBA-cachen redan är giltig
men packningen saknas, byggs båda om.

Prototypen har en explicit hook som tömmer båda härledda cachetyperna vid
probes råa snapshotladdning och direkta TMU-registerimport. Facadens reset
skapar en ny backend. Ingen ny invalidation per textur-RAM-skrivning behövs:
tabellen beskriver NCC-färger, inte tidigare hämtade texlar.

## Tester

**151684 differential-/kontrollfall passerade** i slutversionen:

- Båda TMU:erna, båda NCC-tabellerna och format 1/9.
- Filtrerad sampling med perspektiv/icke-perspektiv, negativa/extrema W,
  varierande koordinater, LOD och clamp-/W-flaggor.
- Omvända lanes, byte-swap och TMU1→TMU0-bankval.
- Verkliga NCC-registerskrivningar följda av kontroll av samtliga 256
  packade poster mot RGBA-tabellen; 49408 postjämförelser utöver fallräknaren.
- Rå registerersättning följd av samma cachetömningshook som snapshotladdning.
- Diagnostikfält jämförs efter att ha nollställts mellan referens och kandidat,
  så att en utebliven diagnostikuppdatering inte döljs av föregående anrop.
- Alla 257×257 filterfraktioner, inklusive ändpunkten 256, för både konstant
  och varierande alpha, mot den befintliga packade filterfunktionen.
- Ett täckningsprov ändrar endast den packade tabellen och kräver att
  samplingsresultatet ändras. Det utesluter att jämförelsen bara kör fallback.

## Mätmetod

Linux, normal Release, `DOTNET_TieredCompilation=0`, replay 6750→7950,
90000 steg per probe-frame. Cache4096 och FIFO-medlemskapsindex på, tidigare
FIFO-PC-filterfix och packad bilinjär filtrering kvar. Inga profilerare eller
egna samtidiga byggen under tiderna. Övriga värdjobb lämnades orörda.

Varje fullständig dekomprimerad slutdump jämförs med:
`32f8e9c49dcc5498c044bb602156ef6e82fe5dec64f27c99749f3a6fe298053a`.
Bildhash `0xe87b12da`, 41 swap-kommandon. Probe-fps är inte spelets fps;
Android och interaktiv spelbarhet verifieras inte av dessa tester.

## Första variant

Samma binär med experiment av/på, ordning av/på/på/av/av/på/på/av.

| Par | Av, ms | På, ms |
|---|---:|---:|
| 1 | 10265,7 | 10035,4 |
| 2 | 10104,6 | 10058,5 |
| 3 | 10078,1 | 10226,7 |
| 4 | 10660,8 | 10221,0 |
| Medel | 10277,30 | 10135,40 |
| Median | 10185,15 | 10139,75 |

1,38 % kortare medeltid, 0,45 % kortare median; tre av fyra par vanns.
Alla åtta slutdumpar var exakta.

## Inlinad filtervariant

`FilterPackedNcc` fick `AggressiveInlining`. JIT-disassembly av den verkliga
samplern visar inga anrop till `FilterPackedNcc` eller `SampleFusedNcc`;
samplerns native-kod är 5305 byte på denna värd. Det är kontrollerad
kodgenerering, inte ett antagande om att attributet alltid får effekt.

Samma av/på-ordning och samma binär inom denna separata serie:

| Par | Av, ms | På, ms |
|---|---:|---:|
| 1 | 10644,7 | 10056,7 |
| 2 | 10355,4 | 10164,7 |
| 3 | 10142,9 | 9929,9 |
| 4 | 10123,4 | 10046,0 |
| Medel | 10316,60 | 10049,325 |
| Median | 10249,15 | 10051,35 |

Alla fyra par vanns och alla åtta slutdumpar var exakta: 2,59 % kortare
medeltid och 1,93 % kortare median.

## Oberoende originalbygge och beslut

Separat Release-bygge av oförändrad `599d05bd` i lokal detached worktree,
mot den inlinade kandidaten med flaggan på. Samma replay och växlingsordning:

| Par | Original, ms | Kandidat, ms |
|---|---:|---:|
| 1 | 10451,8 | 10055,8 |
| 2 | 10069,5 | 10333,1 |
| 3 | 10345,6 | 10058,4 |
| 4 | 10300,4 | 10087,9 |
| Medel | 10291,825 | 10133,800 |
| Median | 10323,00 | 10073,15 |

Tre av fyra par vanns: 1,54 % kortare medeltid och 2,42 % kortare median.
Alla åtta slutdumpar var exakta, totalt 24/24 över de tre serierna.
Det är en liten positiv signal på denna replay, inte ett bevis för generell
eller statistiskt säker förbättring. Originalkontrollen minskar risken att
vinsten bara beror på en långsammare avstängd variant i den nya binären.

Behåll som **opt-in**, utan ändrad standardinställning:
`EUTHERDRIVE_GAUNTDL_EXPERIMENT_FUSED_NCC=1`.
ROM-fria differentialtester körs med
`EUTHERDRIVE_GAUNTDL_TEST_FUSED_NCC=1` och Release-versionen av GauntletProbe.
Nästa steg är en annan spelsekvens och ARM64-mätning innan eventuell
standardaktivering. Detta är inte ett genombrott till spelbart tempo.

Regressionstester: texture filter 1156784 fall, texture LOD 1048576 fall,
samt GPU stream/pending-pixel boundaries, dirty writer, runtime limits,
batch statistics och continuation boundary passerade i normalt bygge.
Release-byggen av Core/Probe och UI passerade; UI hade 33 varningar och
0 fel. Slutkörningen av NCC-testet passerade åter med 151684 fall.

## Artefakter

Lokala loggar/dumpar: `.build-tmp/fused-ncc-{trial}-{mode}.*` och
`.build-tmp/fused-ncc-inline-{trial}-{mode}.*`, trial 1–8, mode 0/1.
Originaljämförelse: `.build-tmp/fused-ncc-head-{trial}-{mode}.*` och
worktree `.build-tmp/fused-ncc-reference-head`.
Testlogg `.build-tmp/fused-ncc-final-checks.log`, disassembly
`.build-tmp/fused-ncc-sampler-asm.log`.
Råa snapshot-/ROM-data checkas inte in.
