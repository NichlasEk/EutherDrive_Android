# Gauntlet DL: specialiserade två-TMU-pixelvägar

## Beslut

Ingen säker end-to-end-vinst. Sexton växlade mätkörningar gav **1,22 %
kortare median men 0,87 % längre medeltid**; fyra av åtta par vanns.
Prototypen är arkiverad lokalt och borttagen ur runtime. Inga nya flaggor,
samplingsvägar eller ändringar av standardbeteendet behålls.

Detta säger inte att specialisering generellt är fel, eller att denna
variant bevisligen är långsammare. Spridningen är stor och resultatet
räcker inte för att hävda en reproducerbar förbättring.

## Försöket

Utgångspunkt: `6250812e` och den
[nya CPU-profilen](gauntlet-dl-cpu-profile-after-fifo-filter-2026-09-18.md).
Två-TMU-sampling/kombinering stod för cirka 15 % av huvudtrådens observerade
replay-stackvikt inklusive underanrop.

Prototypen valde en av två separata helpers **per triangel**:

1. TMU0 local-only, utan onödig TMU1-/diagnostik-/combine-hantering.
2. TMU1 local-only, modulerad med TMU0:s RGB och lokal alpha.

Övriga combine-lägen, saknad nödvändig TMU-state, aktiva samplediagnostikflaggor
och neutral-white-experimentet behöll den generella referensvägen. Beslutet
om båda TMU:erna använder perspektiv flyttades till triangeln. Jämförelsen
av de aktuella W-värdena och själva reciprocalkalkylen gjordes fortfarande
per pixel. Samplingsordningen var fortsatt TMU1 före TMU0 i två-TMU-fallet.

LOD, koordinatavrundning, texelhämtning, NCC-avkodning, bilinjär filtrering,
combine-aritmetik och alpha/depth-vägarna använde samma referenshelpers.
Inga texturcache-invalidationer, gästminnesregler eller snapshotformat ändrades.

Per lång replay valdes local-only för **23029 trianglar** och modulate för
**11959 trianglar**. Detta räknar val vid triangelsetup, inte färdiga pixlar
eller bevis för att varje vald triangel skrev en pixel.

## Riktade tester

Den första harnessen passerade 13210 fall. Den utökade versionen passerade
**26414 differential-/fallbackfall** mot den generella helpern:

- Format 0/1/2/3/4/8/9/10/11/12/13 med deterministiskt fylld textur-RAM.
- Båda NCC-tabellerna för båda TMU:erna fylldes med deterministiska värden;
  format 1/9 krävde uttryckligen icke-null NCC-LUT. 24069 samplefall gav
  icke-noll röd kanal, så testet reducerades inte till enbart svarta pixlar.
- Nearest/bilinear, clamp-/W-flaggor, olika perspektivlägen mellan TMU:erna.
- Lika och olika W, noll, negativa/extrema W, varierande koordinater,
  LOD-baser, explicit LOD och automatisk LOD, ditherpositioner.
- Samtliga relevanta diagnostikflaggor, tracked state, saknad TMU,
  ovanligt combine-läge och avstängt experiment föll tillbaka.

Tester och kod finns i det lokala arkivet nedan; de är inte kopplade till
slutbygget när den prövade implementationen är borttagen.

## Mätning

Normal Release, samma binär under samtliga 16 mätkörningar, experiment av/på.
`DOTNET_TieredCompilation=0`, replay 6750→7950, 90000 steg per probe-frame.
Cache4096, FIFO-medlemskapsindex och FIFO-PC-filterfixen ingick i båda lägena.
Inga profilerare eller egna samtidiga byggen/tunga verifieringar. Andra
värdjobb lämnades orörda. Ingen avvikande mätpunkt har sorterats bort.

Första seriens ordning: av/på/på/av/av/på/på/av.
Bekräftelsens ordning: på/av/av/på/på/av/av/på.

| Par | Referens, ms | Specialiserad, ms |
|---|---:|---:|
| 1 | 10088,4 | 10180,1 |
| 2 | 10227,6 | 10017,4 |
| 3 | 10193,2 | 9871,7 |
| 4 | 11355,5 | 11493,1 |
| 5 | 10285,0 | 10079,7 |
| 6 | 10093,1 | 10300,7 |
| 7 | 11031,1 | 12264,6 |
| 8 | 10284,0 | 10081,5 |

| Serie | Referens medel | Kandidat medel | Förändring medel | Förändring median |
|---|---:|---:|---:|---:|
| Första fyra par | 10466,175 | 10390,575 | 0,72 % kortare | 1,09 % kortare |
| Bekräftande fyra par | 10423,300 | 10681,625 | 2,48 % längre | 0,91 % kortare |
| Alla åtta par | 10444,7375 | 10536,1000 | 0,87 % längre | 1,22 % kortare |

Totala medianer: 10255,8 → 10130,8 ms. Båda delserierna vann två av fyra par.
Det vore missvisande att bara redovisa den positiva medianen eller första
seriens medel. Ingen Android-/spel-FPS-slutsats dras av probe-fps.

Alla 16 mätkörningar och den första sanity-replayen matchade hela den
dekomprimerade sluttillståndsfilen:
`32f8e9c49dcc5498c044bb602156ef6e82fe5dec64f27c99749f3a6fe298053a`.
Bildhash `0xe87b12da`, 41 swap-kommandon per replay.

## Arkiv och nästa avgränsning

Lokalt arkiv:

- `.build-tmp/specialized-tmu-candidate.cs`
- `.build-tmp/specialized-tmu-candidate-checks.cs`
- `.build-tmp/specialized-tmu-candidate-hooks.patch`

De tidigare flaggorna var `EUTHERDRIVE_GAUNTDL_EXPERIMENT_SPECIALIZED_TMU`
och `EUTHERDRIVE_GAUNTDL_TEST_SPECIALIZED_TMU`. Loggar/dumpar heter
`.build-tmp/specialized-tmu-{trial}-{mode}.log`/`-final.warm.gz`, trial 1–16,
mode 0/1. ROM-/snapshotdata checkas inte in.

Detta var en separat-helper-variant, inte samma recordfältändring som det
tidigare [texturförberedelseförsöket](gauntlet-dl-pci-texture-checkpoint-2026-09-10.md),
men båda minskar främst fasta beslut. Nästa större rasterförsök bör eliminera
faktiskt sample-/decode-/filterarbete. En konkret hypotes är sammanfogad
NCC-hämtning och packad filtrering för format 1/9, exempelvis förpackade
LUT-kanaler som undviker mellanliggande packning. Den befintliga packade
bilinjära optimeringen måste återanvändas, inte räknas som en ny vinst.
Det kräver separat prövning av avrundning, alpha och LUT-invalidation vid
registerskrivning/reset/state-load. Ingen sådan förändring gjordes här.

## Slutkontroll

Core och probe-entrypoint är åter exakt som `6250812e`. Release-byggen för
GauntletProbe och UI passerade med noll fel (352 respektive 33 varningar).
Befintliga filtertestet passerade 1156784 fall och LOD-testet 1048576 fall.

En avslutande replay från återbyggd runtime matchade åter hela SHA-256 ovan:
totalt 18 exakta slutdumpar i denna omgång. Logg/dump:
`.build-tmp/specialized-tmu-restored.log` och
`.build-tmp/specialized-tmu-restored-final.warm.gz`. Den körningen gav
11495,9 ms och ingår inte i A/B-statistiken. Tidigare optimeringar behålls.
