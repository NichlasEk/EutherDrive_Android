# Gauntlet Dark Legacy – förbättrad bringup-plan

Datum: 2026-07-14  
Aktiv kodbas: `561f223e` med de avsiktliga, lokala 12-juli-ändringarna kvar  
Primär harness: `tools/GauntletProbe/run-gauntdl-baseline.sh`

## Målet

Gauntlet Dark Legacy ska från kallstart nå attract/start och en spelbar bana
med igenkännbar, stabil grafik i både `GauntletProbe` och den ordinarie
Android-vägen. Lösningen ska följa gästprogrammets FIFO-, objekt-, QIO- och
Voodoo-ägarskap. Den får inte bero på en fast filoffset, global texture-remap,
godtycklig zero-fill eller kommando-/PC-specifik suppression.

## Var bringup faktiskt står

Det här är inte längre främst ett boot-, input- eller FIFO-stopp:

- Den vanliga kalla 60000-step-vägen når f700 reproducerbart och gästen lever.
- Coin/start skapar nya swaps och en stor ny render-/texture-våg genom f1200.
- Standard-FIFO-generationer och producer-wrap-fixen har stängt det tidigare
  stora felet där gamla payloadord blev nya Type1/3/4/5-kommandon.
- Den synliga bilden är fortfarande brus och horisontella band.
- Den aktiva post-input-ytan är en 256x256 Type3-yta från
  `pc=0x800c4e5c`, men stora delar av dess samplade sida är noll eller ägs av
  fel/sen Type5-trafik.
- `textures.rom` är bevisligen kausal, men en fast global source-offset ger bara
  en annan oläsbar bild.
- `font_story`-klassificeringen är felaktig men dess isolerade upload-skip är
  bitidentisk med baseline. Den är ett symptom, inte den synliga grundorsaken.

Den smala kritiska vägen är därför:

`world object/material -> runtime descriptor -> QIO/record page -> Type5 upload -> TMU page lifetime -> Type3 sample`

## Ny arbetsregel

Varje iteration ska besvara en ägarskapsfråga. En ändrad frame hash är inte ett
framsteg i sig.

En iteration får innehålla:

1. en hypotes;
2. en smal trace eller default-off kontroll;
3. baseline och kandidat från samma kalla ursprung och inputschema;
4. provenance, räknare, PPM/PNG och frame hash;
5. beslutet behåll, förkasta eller nästa mätpunkt.

Inga nya globala remaps, sampler-transformer, disk-word-fills eller breda
hydreringar ska läggas till innan provenance-kedjan nedan har ett konkret hål.

## Fas 0 – lås en kanonisk oracle

`/tmp` innehåller inte längre de dokumenterade 12-juli-artifakterna. Börja
därför med en ny, kall och versionsmärkt oracle; återanvänd inte en äldre f520-
eller f700-state med annan flaggstack.

1. Behåll 60000-step-familjen genom att lämna
   `EUTHERDRIVE_GAUNTDL_CPU_STEPS_PER_FRAME` unset.
2. Bygg `GauntletProbe` i Release.
3. Kör kallt till f700 med baseline-scriptet och spara logg, PPM, checksumma och
   v6-snapshot under ett nytt `20260714`-namn.
4. Fortsätt med exakt coin/start-schema till f1100 och vidare till f1200.
5. Skriv ett litet oracle-manifest med commit, MVID, ROM/raw-disk-identitet,
   snapshot-metadata, full flaggstack, frame hashes och filchecksummor.

Förväntad kontrollfamilj från 12 juli:

| Punkt | Förväntat resultat |
| --- | --- |
| kall f700 | `frameHash=0xf4ccc0af`, swap 779, brus/band |
| coin/start f1100 | `frameHash=0x42925e78`, swap 1307 |
| post-input f1200 | `frameHash=0xb38fc156`, swap 1379 |

Om den nya kalla körningen inte reproducerar familjen: stoppa och bisektera
flaggstack, snapshot-metadata och ROM/raw-disk-identitet innan grafikarbete.

Godkänt när samma tre checkpoints kan reproduceras två gånger och den andra
körningen ger identiska framebuffer-checksummor.

## Fas 1 – bygg en surface-provenance-ledger

Nuvarande traces visar vardera änden, men inte en enda sammanhängande kedja.
Lägg till eller kombinera diagnostik så att den aktiva ytan vid f1100/f1200 ger
en kompakt post med:

- render frame, draw-PC och Type3-header;
- runtime descriptor/object/material-adress;
- TMU, mode, LOD, base och faktiskt samplat adressintervall;
- senaste Type5-ägare per berörd 4 KiB-sida eller upload-rad;
- Type5 writer-PC, logical/physical target, source pointer, payload hash och
  upload frame;
- QIO/FSYS logical file, record index, record-relative offset och RAM-owner när
  de går att härleda.

Fånga ägarskapet kontinuerligt från kallstart genom inputvågen. En sen snapshot
ensam räcker inte, eftersom den aktiva sidan då kan ha tappat den användbara
writer-historiken. Begränsa output till ägarbyten och den valda ytan så att en
f1200-körning förblir läsbar.

Godkänt när en enda ledger-post kan förklara minst 95 % av den aktiva ytans
samplade bytes som `rätt owner`, `fel owner`, `överskriven` eller `aldrig
uppladdad`.

## Fas 2 – följ world-descriptorn bakåt

Utgå från den aktiva Type3-ytan, inte från ett på förhand valt assetnamn.

1. Fånga descriptorvärdet när `pc=0x800c4e5c` bygger den synliga quaden.
2. Spåra skrivaren/selektorn av descriptor-, material- och page-fälten.
3. Koppla valet till dess runtime object/render node.
4. Koppla objektet till rätt FSYS-logisk fil, QIO-request, record och companion
   texture extent.
5. Jämför vald recordoffset och mip/page-layout mot den Type5-source och target
   som ledgern faktiskt visar.

Den här fasen ska ge en konkret kedja, exempelvis:

`sel_lr object N -> texture record R -> logical offset O -> RAM page P -> Type5 target T -> TMU sample S`

Godkänt när kedjan inte innehåller någon gissad fast offset och samma
identifiering återkommer vid minst två post-input-frames.

## Fas 3 – välj fixgräns från beviset

Välj exakt en gren efter Fas 1–2:

| Ledger-resultat | Rätt nästa fixgräns |
| --- | --- |
| Ingen world-upload äger den samplade sidan | QIO/record completion eller saknad upload-trigger |
| Rätt payload laddas men hamnar på fel sida | Type5 address/LOD/page-tolkning, jämför med Voodoo 2/MAME |
| Rätt sida ägs först och skrivs över senare | descriptor/page lifetime och context switch |
| Rätt bytes finns och rätt sida ägs men fel adress samplas | TMU base/LOD/fetch, först då samplerarbete |
| Descriptorn pekar på fel asset/record | object/material/page-selection i gästkedjan |

Fixens första version ska vara default-off och uttrycka en generell
ägarskapsregel. PC-, source- eller hash-specifika specialfall är endast tillåtna
som korta kausalitetsprober och ska tas bort efter A/B-testet.

Godkänt när kandidaten från kallstart:

- visar en igenkännbar world-/UI-komponent, inte bara fler färger;
- minskar felägda eller aldrig uppladdade sampled bytes;
- behåller FIFO producer/consumer-samstämmighet;
- fortsätter genom coin/start och f1200 utan stopp;
- är stabil över upprepade frames och en andra kall körning.

## Fas 4 – återställ hela scenen

När första korrekta ytan syns, arbeta outward från samma ägarskapsmodell:

1. verifiera UI/font separat från world terrain;
2. återställ återstående world texture records och mipnivåer;
3. verifiera modeller/actors och deras materials;
4. kontrollera depth, alpha, palette/NCC, clip och buffer swap;
5. avaktivera gamla suppressions och historiska texture-repairs en i taget.

Varje avaktivering ska ha en kall före/efter-oracle. Om ett gammalt flaggkrav
överlever ska dess hårdvaruregel dokumenteras och flyttas till ordinarie kod;
annars tas flaggan bort.

Godkänt när attract/start och minst en spelvärld är igenkännbara, rörelse inte
skapar nya band/mosaik och frame progression fortsätter i flera minuter.

## Fas 5 – promotion och Android

1. Gör den generella fixen till ordinarie Vegas/Voodoo-beteende där den hör
   hemma; håll Gauntlet-specifik QIO-kompatibilitet i adapterlagret endast när
   gäst-/diskkontraktet kräver det.
2. Kör normal Release-build och relevanta tester.
3. Kör kall `GauntletProbe` utan diagnostikflaggor.
4. Starta samma ROM i den ordinarie Android-vägen och jämför de första
   igenkännbara checkpoints mot headless-resultatet.
5. Verifiera input, display-buffer, snapshot round-trip och flera minuters
   spel.
6. Dokumentera en kort testinstruktion och commit/push efter varje verifierad
   etapp.

## Definition av klart

- Kallstart är reproducerbar med dokumenterad ROM/raw-disk och cadence.
- Attract/start samt minst en bana är igenkännbara och stabila.
- Coin, start och spelkontroller fungerar.
- Varje aktiv world-yta kan kopplas till rätt object/record/upload/TMU-owner.
- Inga gamla FIFO-payloadord exekveras som nya paket.
- Inga globala source-remaps, zero-fills eller visuella specialfall krävs.
- Headless och Android visar samma korrekta grafiska progression.
- Release-build, artefakter, checksummor och en kort testrecipe finns.

## Första konkreta arbetspasset

1. Checkpointa de avsiktliga lokala 12-juli-ändringarna separat från ny kod.
2. Reproducera och manifestera den kalla f700/f1100/f1200-oraclen.
3. Kör en smal owner-trace över den aktiva post-input-ytan.
4. Om befintliga traces inte kan länka draw till senaste upload, implementera
   surface-provenance-ledgern som första kodslice.
5. Låt ledger-resultatet välja exakt en av grenarna i Fas 3.

Det första målet är alltså inte en snyggare hash. Det är en komplett,
maskinläsbar ägarskapskedja för den yta som faktiskt syns fel.

## Utfört 14 juli – kall oracle och första provenance-ledgern

Den vanliga 60000-step-vägen är nu återbaserad från kallstart med v6-state:

| Checkpoint | Resultat |
| --- | --- |
| kall f700 | `frameHash=0xf4ccc0af`, swap 779, PPM SHA-256 `14efebcd674d1daf00fe00a26b19957a9e7e4b849e188fb9bdbe29bb1866c458` |
| f1000 före input | `frameHash=0xf4ccc0af`, swap 779 |
| coin+start f1001..f1004, f1100 | `frameHash=0x42925e78`, swap 1307 |
| samma input, f1200 | `frameHash=0xacaece21`, swap 1387 |

f1200 avviker från den äldre `0xb38fc156`/swap-1379-noteringen men är
reproducerbar två gånger från den nya f1100-staten. Den äldre punkten är därför
inte längre promotion-oracle; f700 och f1100 matchar fortfarande exakt.

Den nya default-off writer-provenancen bevarar den logiska FIFO-källan över
ring-slot-överskrivningar och lägger följande fält på varje samplad Type5-owner:

```text
source pointer / sourceBase / packet source / packet+index / upload frame
```

Kontinuerlig f1000–f1201-trace kopplar den aktiva ytan till:

```text
draw pc=0x800c4e5c cmd=0x0180a8cb
mode=0x8c24100f lod=0x000020c6 regbase=0x1c00
sample byte range=0x00e510..0x013e0f
writer pc=0x800fe5d4 cmd=0xc0000205
logical targets=0x087300..0x087f80
upload frame=1028 sourceBase=0x00200000
source bytes=0x802f1274..0x802f2a74
run start=0x802e2c68, 256 packets x 64 words
```

CPU-trace bekräftar samtidigt att gästkedjan själv väljer källan:

```text
0x8010957c  source = descriptor+0x10
0x801095b4  source -> outgoing stack argument
0x801096c0  low-level wrapper reloads source
0x801096fc  call 0x800fe1fc
```

En tillfällig, source-specifik kausalitetsprobe hoppade över endast
`0x802e2c68` med 256 paket. f1200 behöll swap 1387 men ändrades från
`0xacaece21` till `0xaed77688`; stora vänster- och bottenfält av brus
försvann. Bilden var fortfarande inte igenkännbar. Proben togs därför bort:
uploaden är en bevisad korruptionskälla, men suppression är inte lösningen.

State-7-parsern och dess record-/companion-kontrakt är redan kartlagda i den
senare 11–12-juli-evidensen. Nästa mätpunkt är därför den efterföljande
sidlivstiden: klassificera de aktiva sample-sidorna separat som aldrig skrivna,
kvarlämnade av en äldre world/mip-upload eller omskrivna av
`0x802e2c68`-familjen. Följ sedan den korrekta record-ownern till den senare
world-upload som skulle återta sidan. Ändra inte Type5-adressering eller
TMU-fetch innan den kedjan är bevisad eller utesluten.

## Utfört 14 juli – kall writer-livstid

Writer-ledgern utökades med föregående distinkta innehållsägare. Upprepningar
av samma RAM-source via olika writer-PC:n och TMU0/TMU1-targets räknas som en
ägare, så bankduplikat skymmer inte föregående innehåll.

En full kall f0–f1201-körning med `min render frame=1028` behöll den ordinarie
slutpunkten exakt:

```text
frameHash=0xe5a96eee
swap=1387
fifoWords=10495079
texWrites=8788243
```

Ledgern visar ett blandat livstidsfel:

- LOD0-orden i den lägre samplade sidan skrivs om vid render frame 1027 av
  `pc=0x800fe5d4`, källa `0x802f1274..0x802f2974`, ur 256-paketsrunnen som
  börjar vid `0x802e2c68`.
- Samma innehåll passerar först alternativa writer-PC:n och logiska
  TMU0/TMU1-targets; det är duplikat, inte en separat terrain-owner.
- Stora delar av `0x015000..0x019000` saknar aktuell owner. De små ägda delarna
  ligger kvar från LOD2/3-paket vid render frame 662, bland annat sources
  `0x802f6424`, `0x802f6724` och `0x802f6a24`.
- För de fyra representativa post-input-halvorna är ungefär 0 %, 12 %, 57 %
  respektive 89 % av samples owner-lösa.

Detta väljer Fas-3-grenen `saknad senare upload/page lifetime`. Den synliga
ytan kombinerar en sen felaktig LOD0-owner med aldrig återtagna högre sidor.
Nästa kodarbete ska följa de redan korrekta `snm/stk/kjh`-selektorerna och
state-7-recordens companion-offset till den world-upload som uteblir efter
frame 662. En source-skip, global page-wrap eller linjär targetplacering är
fortfarande endast kausalitetsprober och får inte promoveras.

## Utfört 14 juli – dynamisk source-table-owner

Coin/start-övergången från den rena f1000-staten visar nu var den sena
`0x802e2c68`-runnen publiceras. BGLoadModel laddar först `kjh`, passerar
stream-index 2 och 3, och bygger sedan arenaobjektet. Vid `0x800aac18` skriver
parsern samma pekare till flera source-table-slots:

```text
writer pc=0x800aac18
source=0x802e2c68
slot 9 -> font_story
slot 10 -> movies/movie3
slots 11..15 -> namnlösa asset-poster
slot 16 -> namnlös asset-post med asset pointer 0x80304220
```

Före övergången pekar slot 9 och 10 fortfarande på sina separata
asset-deskriptorer `0x80312998` respektive `0x80332998`. Vid den faktiska
upload-selecten har alla slots 9..16 aliaserats till `0x802e2c68`, vars första
ord är `0001e69c/00001188/0000000b/00000000`. Det förklarar varför den äldre
statiska BG-payload-klassificeringen rapporterade `bgsrc=none`: källan ägs av
den dynamiska source-tabellen, inte av ett fast hydratiserat payloadintervall.

Texture-selector-tracen rapporterar nu även `selectedTableOwners`, med
asset-namn, ursprunglig asset-pointer och side-pointer för varje exakt matchande
slot. Den fokuserade f1000–f1050-körningen bekräftade aliaseringen och behöll
oraklet exakt:

```text
frameHash=0xf4ccc0af
swap=1263
selectedTableOwners=9:font_story,10:movies/movie3,11:<empty>,...,16:<empty>
```

Nästa kausalitetsprobe ska därför ligga vid parserns source-table-store, inte i
Type5-routingen: avgör vilka av slots 9..16 som record-token-listan verkligen
avser och varför samma arenaobjekt publiceras för hela intervallet. Testa sedan
en exakt owner-/klassgräns mot den befintliga f1050/f1100/f1200-orakeln; ändra
inte payloadadress eller target stride innan den gränsen är bevisad.

### Kausalitetsresultat för source-table-store

En default-off mask lades på exakt `0x800aac18`:

```text
EUTHERDRIVE_GAUNTDL_EXPERIMENT_RUNTIME_BGLOADMODEL_SOURCE_TABLE_STORE_SKIP_INDEX_MASK=0x1fe00
```

Den stoppar endast `sourceTable[index] = s2` för slots 9..16. I den fokuserade
f1000–f1050-körningen träffade den slots 9..15 och bevarade bland annat
`font_story=0x80312998` och `movies/movie3=0x80332998`. Trots det valde
upload-wrappern fortfarande `0x802e2c68`. Slot 16, som publiceras via en annan
väg, var då den enda exakta table-ownern.

Resultatet var bitstabilt mot ordinarie f1050:

```text
frameHash=0xf4ccc0af
swap=1263
fifoWords=10292873
texWrites=8788243
selected=0x802e2c68
selectedTableOwners=16:<empty>/asset=80304220
```

Det utesluter stores för slots 9..15 som orsak till upload-valet. Aliaseringen
är en följd av record-processningen, inte den länk som matar wrappern. Nästa
spårpunkt flyttas därför tillbaka till record-loopens direkta anrop
`0x800ab3b0 -> 0x800a7094`: klassificera entryn som ger `a2=0`, `a3=0x1188`
och `a1=0x802e2c68`, och identifiera det body-/payload-offset som borde nå
upload-wrappern. Store-masken ska förbli default-off som regressionsprobe.
