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

### Record-call vid `0x800ab3b0`

En source-filtrerad record-call-trace visar att bara ett anrop matar den
aktuella `0x802e2c68`-familjen före f1050:

```text
source=0x802e2c68
a0/tableEntry=0x813815a0
a2/recordOffset=0
a3/cursor/limit=0x1188
outer=0
phase=-1
candidate=0
```

Källans första 0x50 byte är en följd av 0x20-byte-liknande poster, inte råa
texels:

```text
0001e69c/00001188/0000000b/00000000/0000000d/000000ca/00021118/000215f8
0001e67c/00001188/0000000b/00000000/0000000d/000000ca/000210f8/000215d8
0001e65c/00001188/0000000b/...
```

Detta gör kontraktet tydligare: `0x800ab3b0` skickar medvetet katalogroten,
offset noll och spannet `0x1188` till `0x800a7094`. Källan uppstår alltså inte
genom ett tappat source-table-index. Nästa gräns är inne i `0x800a7094` och
dess call till `0x800a64fc`: verifiera om gästkoden först tolkar
0x20-byte-posterna till payloadpekare, medan vår Voodoo/FIFO-fastpath råkar
behandla den bevarade katalogpekaren som rå texturdata. Record-call-tracen är
default-off och den fokuserade körningen behöll `frameHash=0xf4ccc0af`.

### Source-offset-kontraktet och den riktiga nästa gränsen

Den hypotesen är nu falsifierad. `0x800a64fc` är en liten jump-table som mappar
klass `0..6` till offset `3,2,1,0,1,2,3`. Den problematiska körningen går in med
klass `3`, så nolloffseten är ett avsiktligt gästresultat, inte en tappad
payloadpekare. Den nya default-off-tracen
`EUTHERDRIVE_GAUNTDL_TRACE_BGLOADMODEL_TEXTURE_SOURCE_OFFSETS=1`, filtrerad med
`EUTHERDRIVE_GAUNTDL_TRACE_TEXTURE_UPLOAD_RUN_SOURCE=802e2c68`, visar att alla
observerade poster behåller:

```text
source=802e2c68 priorOffset=00000000 subtract=00000000 computed=802e2c68
classIn=00000003
```

Samtidigt flyttar output-/record-sidan vidare från `813815a0` i steg om
`0x50`, och `s0` växlar genom de väntade `0x40xxxx`/`0x0xxxxx`-familjerna.
Som kontroll visar den tidigare källan `802e1718` ett verkligt offsetfall:
`0x18f20 - 0x18e00 = 0x120`, alltså fungerar samma beräkning när klassen och
recordet kräver det.

En separat main-RAM-read-watch över `802e2c68..802e3df0` visar dessutom att
gästkoden själv läser området sekventiellt vid `0x800fe604/0x800fe608` och har
Voodoo-FIFO-destinationer i `a0=0xa8299a..`. Exempelvis läses
`802e2c69 -> 0x880001e6`, följt av `802e2c6d -> 0x0b000011`. Därmed är det inte
bara EutherDrives bulk-/FIFO-fastpath som råkar behandla katalogroten som
payload; gästprogrammet bygger och skickar denna källa som en FIFO-ström.

Nästa gräns är därför källans innehåll/ägare före `0x800fe604`, inte ännu en
pekaremappning i `0x800a7094`: följ vem som fyller `802e2c68`-familjen och om
den hör till fel asset-/recordklass när den kopplas in i den aktiva FIFO-
strömmen. Båda fokuserade körningarna var observationsrena och behöll exakt
`frameHash=0xf4ccc0af`, `fifoWords=10292873`, `texWrites=8788243` och
`swap=1263` vid f1050.

### Källägaren före `0x800fe604`

Den generiska main-RAM-writetracen omfattar nu även direkta disk-LBA-/byte-
kopior, PCI-window-writes och device-to-RAM-kopior. Detta stängde hålet där
fastpath-hydrering skrev direkt i `_mainRam` utan att passera CPU:ns vanliga
`Write32`/`Write8`-spårning.

Den rena f1000-staten har fortfarande noll vid källroten:

```text
mem[802e2c68] = 00000000/00000000/00000000/00000000
```

Med den kanoniska baseline-flaggstacken och coin+start på f1001..f1004 fångar
f1000–f1050-körningen den första skrivaren:

```text
[GAUNTDL:MEM] pc=ffffffff800c9944 disk-byte-copy
address=ffffffff802e2c68 value=0001e69c old=00000000
byteOffset=0x0fbb0830 lba=0x7dd8e sourceOffset=0 count=0x200 offset=0x180
```

Hela QIO-kopian börjar på `0x802e1718`, är `0x2000` byte och kommer från
`static_lr/textures.rom` vid raw-disk-offset `0x0fbb0830`. Källroten ligger
alltså `0x1550` byte in i detta riktiga assetblock, motsvarande raw-disk-offset
`0x0fbb1d80`; den byggs inte av record-parsern och är inte en senare slumpmässig
RAM-överskrivning. Samma hydrering och kontrolltabellens byte-exakta diskägare
var redan observerad i 30-juni-planens hydration-range-checkpoint, men den nya
tracen knyter nu den specifika post-input-runnen till samma ägare.

Körningen förblev exakt på f1050-oraklet:

```text
frameHash=0xf4ccc0af
swap=1263
fifoWords=10292873
texWrites=8788243
```

Detta falsifierar hypotesen att `0x802e2c68` hör till fel QIO-asset eller får
fel recordklass vid hydreringen. Gästprogrammet väljer och skickar byte-exakt
data ur `static_lr/textures.rom`. Nästa gräns flyttas tillbaka framåt till
tolkningen av denna verkliga kontroll-/payload-stream: följ ett representativt
0x20-byte-record från raw bytes genom `0x800a7094`, FIFO-orden vid
`0x800fe604/0x800fe608` och Type5-target/payload-dekodningen. Jämför där mot
Voodoo-kommandots förväntade byte-/word-/masklayout innan någon owner-skip eller
source-remap övervägs.

### Type5-källan genom TMU-bankerna

Type5-sekvenstracen kan nu återanvända
`EUTHERDRIVE_GAUNTDL_TRACE_TEXTURE_UPLOAD_RUN_SOURCE` som source-filter och
rapporterar RAM-källa, packet source, target, råa/avkodade ord, TMU-state och
fysisk destination i samma rad. För `0x802e2c68` visar den första uploaden:

```text
cmd=0xc0000205 count=64
raw=0001e69c/00001188/0000000b/00000000/...
decoded=9ce60100/88110000/0b000000/00000000/...
TMU1 target=080000..08003f
TMU0 target=000000..00003f
mode=0c26100f lod=ff802000 base=00000000
```

Upload-fälten matchar MAMEs packet-5-kontrakt: word 0 anger space/count, word 1
är byteadressen som divideras med fyra, och space 3 går genom texture-porten.
Tracen avslöjade däremot att EutherDrive hade en gemensam fysisk texture-array
för båda TMU-targetfamiljerna. MAME initierar separata RAM-ytor för
`m_tmu[0]` och `m_tmu[1]`.

En default-off, state-formatneutral probe delar därför den befintliga 8 MiB-
arrayen i två 4 MiB-banker:

```text
EUTHERDRIVE_GAUNTDL_EXPERIMENT_VOODOO_SEPARATE_TMU_TEXTURE_MEMORY=1
```

Med proben landar TMU0-paketet på word `0x000000..0x00003f` och TMU1-paketet
på `0x100000..0x10003f`. Antalet touched words vid f1050 ökar från 16384 till
32768 och sista touched word flyttar från `0x003fff` till `0x103fff`, vilket
bekräftar att bankseparationen verkligen sker.

Den visuella A/B-kontrollen är samtidigt negativ: f1100 ger fortfarande exakt
`frameHash=0x42925e78`, och PPM-filerna är byteidentiska med SHA-256
`4a6a28ea91fed6f9c8c4c2cd401f7da63a0a6b651cb4918e6ed9b111062e543b`.
De växelvisa TMU0/TMU1-uppladdningarna innehåller samma bytes, och den nuvarande
samplaren väljer TMU0, så cross-TMU-aliaseringen är verklig men inte den synliga
f1100-orsaken. Proben ska förbli default-off tills två-TMU-combinern modelleras;
nästa mätpunkt är i stället varför samma static-lr-kontrollstream tolkas som
den aktiva TMU0-ytan och hur dess Type3-primitive väljer texture mode/base.

### Upload-semantikens f1100-matris

Den separata registerbanken (`TEXTURE_UPLOAD_TMU_BANKS=1`) är också visuellt
neutral tillsammans med separata TMU-minnen: TMU1 behåller då sitt tidiga
`mode=0/lod=0x800`, medan TMU0 använder `0x0c26100f/0xff802000`, men f1100-PPM
är fortfarande byteidentisk med oraklet. Det bekräftar att den aktiva
samplingen i denna scen går genom TMU0 och inte påverkas av TMU1:s state.

En kombinerad MAME-layoutprobe med separata banker, bankade upload-register,
MAME-writepekare och de tre historiska bringup-reglerna avstängda gav
`frameHash=0xfc42f6eb`. Bilden fyllde större del av framebufferområdet men var
fortfarande samma korrupta randfamilj; en hashändring är därför inte en
visuell förbättring här.

Fyra isolerade A/B-körningar från samma f1000-snapshot gav:

```text
baseline             frameHash=0x42925e78  ppmSha256=4a6a28ea91fe...
Type5 endian=0       frameHash=0x01000566  ppmSha256=ef6dbe6d1b78...
seq8 download=0      frameHash=0x0d982fd4  ppmSha256=32a9e75398b0...
sparse8 upload=0     frameHash=0x91b6b557  ppmSha256=a710030efb2d...
MAME write ptr=1     frameHash=0x42925e78  ppmSha256=4a6a28ea91fe...
```

MAME-writepekaren är alltså helt neutral i den nuvarande TMU0-layouten. De tre
bringup-reglerna för endian/seq8/sparse8 är var för sig bildbärande, men ingen
av deras alternativa bilder innehåller begriplig scenkonst. De ska därför inte
promotas eller tas bort på visuell gissning. Nästa spårgräns är Type3-state:
identifiera de sista bankade skrivningarna till TMU0 `textureMode`, `tLOD` och
`texBaseAddr` före den aktiva `pc=0x800c4e5c`-primitiven, och bind dem till
exakt upload-owner/target innan fler layoutkombinationer testas.

### Type3-stateägaren och MAME-kontraktet

Type3-tracen har nu ett command-filter och bär med senaste writer-PC, FIFO-
kommando, packet-offset och sekvens för mode/lod/base i båda TMU-bankerna:

```text
EUTHERDRIVE_GAUNTDL_TRACE_VOODOO_TYPE3_COMMANDS=0180a8cb
```

Den fokuserade f1050-körningen visar samma deterministiska kedja för varje
aktiv fullrect:

```text
TMU0 base <- pc 80106a74, cmd 00059604, packet ...7e74
TMU0 mode/lod <- pc 80106448, cmd 00019604, packet ...7e94
TMU1 mode/lod <- pc 80106448, cmd 0001a604, packet ...7ea0
Type3 0180a8cb <- pc 800c4e5c, packet ...7ebc
```

Vid Type3 är TMU0 exakt `0c24100f/ff802000/00000000`. State är alltså inte
stale, och draw-paketet läser inte upload-tjänstens tillfälliga `0000100f`-
mode. Tracen är observationsren och behåller f1050-oraklet
`frameHash=0xf4ccc0af`.

En direkt jämförelse med MAMEs `voodoo_1_device::internal_texture_w` visar
däremot att tre äldre baseline-regler avviker från hårdvarukontraktet:

1. MAME swizzlar endast när `tLOD.tdata_swizzle` är satt; vår Type5-endianregel
   för-swizzlar dessutom payloaden och ger dubbel transform för `ff802000`.
2. MAME hämtar `seq_8_downld` från TMU0 bit 31; baseline tvingar den för alla
   8-bitarsformat trots att `0c24100f` har biten noll.
3. MAME skriver alla fyra byte; baseline-regeln för sparse8 undertrycker
   nollbyte.

Alla tre MAME-semantiker tillsammans, utan bank- eller write-pointerprober,
ger `frameHash=0x638009cc` och PPM-SHA-256
`07075c0d4b4cb04bdeb2807782d2d2babe0853a8eae69010bf8346bac6f40eb3`.
Bilden är fortfarande korrupta fullrect-ränder. Reglerna är alltså teknisk
skuld som senare bör ersättas, men deras MAME-korrekta värden är inte ensamma
bringup-fixen. Nästa kausala gräns är write_ptr/fetch-layoutens gemensamma
LOD-offset och adressmask för TMU0, inte registerägare eller payload-endian.

### Hela `0x802e2c68`-runens adressmatris

Type5-sekvensens source-filter matchar nu både aktuell packet-source och den
beräknade run-roten:

```text
root = source - index * payloadWords * 4
```

Det gör att ett filter på `802e2c68` följer alla 256 packet i stället för bara
index noll. Den fulla f1200-tracen stänger adressfrågan:

```text
TMU1 index 0:   target 080000..08003f -> phys 00000..0003f
TMU1 index 255: target 087f80..087fbf -> phys 03fc0..03fff
TMU0 index 0:   target 000000..00003f -> phys 00000..0003f
TMU0 index 255: target 007f80..007fbf -> phys 03fc0..03fff
```

Varje source-packet flyttar `0x100` byte, guest-target flyttar `0x80` words,
och den aktiva seq8-layouten packar detta till en helt sammanhängande fysisk
64 KiB-yta. Runnen fullbordar först TMU1, därefter TMU0, och börjar sedan om
med samma root. Med gemensamt texture-minne är den andra banken därför en
byteidentisk omskrivning; det förklarar både den neutrala bankproben och varför
separata TMU-minnen fördubblar touched-ytan utan att ändra den aktiva bilden.

En första trace såg exakt 160 poster, men det var trace-defaulttaket: rätt
variabel är singular `EUTHERDRIVE_GAUNTDL_TRACE_VOODOO_TYPE5_TEXTURE_UPLOAD_SEQUENCE_LIMIT`.
Med `600` syns completionen ovan. Det finns inget producerstopp vid 160.

Som kontroll kördes både baseline och de tre MAME-uploadsemantikerna från den
tidigare rena f700-snapshoten till f1100. Baseline reproducerade PPM-oraklet
byteexakt (`4a6a28ea91fe...`, `frameHash=0x42925e78`); MAME-varianten gav
`frameHash=0x1b334252` och SHA-256 `3bb043fc1917...`, men fortfarande samma
korrupta randfamilj. Resultatet från f1000-snapshoten var alltså inte en
warmstate-artefakt.

Targetsteg, completion, bankordning, writer-state och MAME write_ptr är nu
bracketerade. Nästa slice ska klassificera de 64 KiB source-byten som korsar
QIO-fönstren: mät kontrollord/entropi per 0x100-packet och bind varje fönster
till dess asset-/diskintervall. Om kontrollposterna dominerar ska nästa fix
ligga i source-owner/klassvalet före `0x800fe604`, inte i Voodoo-adresseringen.

### Sen draw-owner och den saknade texture-sidan

Source-klassificeringen för första 256-packetsrunnen visar 111 unika packet
och 146 helt nollade packet. De tidiga `gei`, `snm` och `kjh`-fönstren bär
data, medan nästan hela den senare halvan (`pnk`, `geb`, `nin`, `stg`) är
ohydrerad. `stk` har bara två icke-nollade packet. Baselines sparse8-regel gör
de helt nollade packeten inerta, så detta är inte ensamt en förklaring till
bilden, men det bevisar att den syntetiska QIO-sourceägaren inte fyller hela
den sida som upload-tjänsten publicerar.

Type3-tracen kan nu avgränsas på render-frame med:

```text
EUTHERDRIVE_GAUNTDL_TRACE_VOODOO_TYPE3_MIN_FRAME=1150
```

På den sena f1200-ytan skrivs TMU0:s hela state om omedelbart före draw av
`pc=0x800bd19c`, `cmd=0x00059604`:

```text
mode=8c24100f  lod=000020c6  regbase=00001c00
Type3 pc=800c4e5c cmd=0180a8cb
```

Detta är alltså inte stale state. Triangle-sample/writer-tracen resolverar
basen till `0x00e510` för RGB332 256x256 och ser adresser upp till cirka
`0x01900f`. Mellan 75 och 98 procent av sample-pixlarna är nollade, och de
flesta sample-adresser saknar helt direct writer. De få träffarna ägs av
`pc=0x800fe5d4`, frame 1028, source-index 230--253 och TMU1-targets nära
slutet av sidan (`0x087300`--`0x087e80`) -- exakt den nästan helt ohydrerade
`nin`/`stg`-svansen.

Den kausala gränsen är därmed smal: sena drawen använder en bas nära slutet av
en fysisk 64 KiB upload-sida och fortsätter att sampla förbi `0xffff`, medan
runnen endast har writer-ägarskap inom sidan. Den äldre globala 64 KiB-wrap-
proben minskade nollpixlarna kraftigt men gav fortfarande brus, så global wrap
är inte en slutfix. Nästa observationsrena slice ska för varje direct
writer-miss även slå upp `address & 0xffff` och summera dess writer/root. Om
de wrapade adresserna binds till samma run får vi välja mellan fel descriptor-
page/base och en snävt owner-scopad sampler-wrap; om de binds till en annan
hydratiserad run ligger felet i page-/descriptorvalet före draw.

### Wrap-owner-kontrollen väljer descriptor/page-spåret

Sample-summaryn skriver nu även `wrap64writers=...` för direct writer-missar
över `0xffff`. Detta är en ren parallell provenance-lookup på
`address & 0xffff`; sampleradress, texture-minne och framebuffer ändras inte.
Den rena f1000--f1200-replayen behåller därför exakt orakel
`frameHash=0xacaece21`.

Resultatet avvisar en generell 64 KiB sampler-wrap. De wrapade missarna binds
inte entydigt till den sena `0x802e2c68`-runnen. De främsta ägarna är en mix
av:

- tidiga packet i samma run, exempelvis index 3 och 6;
- äldre sourcelösa Type5-uploads med `lod=0x00700800` och base 0/0x800;
- adresser som fortfarande saknar writer efter wrap.

Exempelvis har första triangeln 8 124 direct misses; dess största wrapade
bucketar är `none:89`, en äldre source-lös upload `:72`, source-index 3 `:65`
och source-index 6 `:62`. Den parade triangeln som huvudsakligen samplar över
`0x10000` pekar i stället främst på äldre base-0x800-packet. Samma mönster
upprepas för alla sex quads.

En adressmask kan alltså bara välja historiskt innehåll som råkar ligga i den
låga sidan, inte rätt asset. Nästa slice flyttas bakåt till descriptorägaren:
bind sena `regbase=0x1c00` och dess deklarerade `0x14fe8/0x17a94/0x17f60`-
layout till exakt QIO/body-record, file offset och material-owner som
`pc=0x800bd19c` konsumerar. Först därefter finns underlag för rätt upload-page
eller rätt source hydration.

### Sena state-7-descriptorn är stabil över world-drawen

World-descriptor-tracen har fått samma render-frame-avgränsning som Voodoo-
tracerna:

```text
EUTHERDRIVE_GAUNTDL_TRACE_RUNTIME_WORLD_TEXTURE_DESCRIPTOR_MIN_FRAME=1150
```

Den läser Voodoo-backendens serialiserade render-frame via en read-only kedja;
ingen separat adapterräknare eller emulerad state introduceras. En ren f1200-
replay behåller `frameHash=0xacaece21`.

F1150--f1151 växlar bara mellan två descriptors, båda med samma material
`0x80262d64` och owner `0x80213618`:

```text
primary   descriptor=802e2158 mode=8c24100f lod=20c6 base=1c00
secondary descriptor=802e21a8 mode=8c241faf lod=2cea base=2000
```

Primären är state-7-body `0x802e1838 + 0x920`; sekundären ligger exakt
`0x50` byte efter den. Primärens egna ord deklarerar `0xe000`, `0x14fe8`,
`0x17a94` och `0x17f60`, medan sekundären deklarerar nästa format/base. Det
senare draw-felet är därför inte ett descriptorbyte mellan upload och render:
samma request-owned layout är aktiv hela tiden. Nästa trace ska dumpa och
deduplicera material-/owner-strukturerna och följa deras record/file-offset-
pekare tillbaka till companion-texturen som ska materialisera dessa ytor.

### Texture-set-tabellen och packed record-ABI:n

Den deduplicerade related-struct-tracen visar bara de två förväntade
descriptors och behåller f1200-oraklet. `owner=0x80213618` är en tunn rendernod
vars `+0x08` pekar på `material=0x80262d64`; materialets enda tydliga state är
Voodoo-basen `0xa8000000`, en self-pointer och `0x00620000`. Varken owner eller
material bär companion-file-offseten.

En RAM-pointer-scan hittar däremot nio referenser till primärdescriptorn i ett
sammanhängande texture-set-table vid `0x802545a0..0x802545c0`. Nästa två poster
är `0x80312a00` och `0x80332a00`. Read-watch visar att bara
`pc=0x800a92a8` läser tabellen i den observerade world-loopen.

Kodorden vid `0x800a9290` ger den exakta ABI:n:

```text
setIndex    = a0 >> 16
recordIndex = a0 & 0xffff
result      = *(0x802545a0 + setIndex * 4) + recordIndex * 0x50
```

Alla 15 observerade f1000--f1001-anrop har `a0=0` vid tabelläsningen och väljer
alltså set 0, record 0, dvs `0x802e2158`. Descriptorn når drawen genom ett
avsiktligt texture-set/record-uppslag, inte som en lös RAM-pekare. Tracen
skriver nu även `textureSetTable=` så nästa cold producerkörning kan fånga när
de nio aliasen skapas och vilket parserresultat som borde ersätta dem. Den
närmaste fixgränsen är set-tabellens producent/aliasering före `0x800a9290`,
inte sampleradressen eller rendernodens materialpekare.

### Cold producer-watch bevisar static-source-aliaseringen

En cold write-watch på `0x802545a0..0x802545d0` hittar hela tabellens
producentkedja utan Voodoo-ingrepp. Initieringen vid `pc=0x800103a4` nollar
12 poster. Därefter:

```text
set 0    pc=800ac42c  -> 802e2158
set 1-8  pc=800aae64  -> 802e2158
set 9    pc=800aae64  80312a70 -> 80312a00
set 10   pc=800aae64  00000000 -> 80332a00
```

F300-kontrollen ger oraklet `frameHash=0xd083385f`; f300--f700-replayen ger
det rena kalla f700-oraklet `frameHash=0xf4ccc0af`. Watchen är alltså
observationsren.

Registerstate vid samma `0x800aae64`-store skiljer den felaktiga familjen från
de självständiga setten. För varje set 1--8 är parser-source `s2` exakt samma
`0x802e1718`, så helper-resultatet `v0` blir samma `0x802e2158`. För set 9 är
`s2=0x80312998` och `v0=s2+0x68=0x80312a00`; för set 10 är motsvarande värden
`0x80332998` och `0x80332a00`. De nio descriptoraliasen skapas alltså därför
att asset-tabellen matar samma static source till guest-parsern, inte därför
att helpern eller texture-set-tabellen tappar indexet.

Koden har redan den exakt avgränsade default-off-repairen
`EUTHERDRIVE_GAUNTDL_FIX_RUNTIME_BGLOADMODEL_ASSET_STATIC_ALIAS_SOURCE`. Den
ersätter ett asset-entry-source endast när det fortfarande är static-aliasen,
ett känt index har ett hydratiserat distinct-source-fönster och det fönstret
inte är tomt. Nästa A/B ska därför köras kallt med just denna flagga, först till
f300 för descriptor-diversitet och orakel, därefter till f700 för faktisk
bild/hash. Ingen sampler-, upload- eller file-offset-probe ska blandas in.

Den kalla A/B:n avvisar denna befintliga repair som lösning på tabellen. Med
flaggan aktiv repareras endast index 1 från den tillfälliga asset-entry-källan
`0x802f0e70` till `0x802e3718`, dessutom efter att set 1 redan producerats.
Set 1--8 når fortfarande `0x800aae64` med `s2=0x802e1718`, alla tabellposter
förblir `0x802e2158`, och f300 är byte-/hashmässigt neutral
(`frameHash=0xd083385f`). Att även acceptera den konstanta
`repeatedStaticSource` i repair-guardet ändrar inte detta; den provändringen är
borttagen.

Rätt nästa A/B-punkt är därför producentanropet självt. Vid `pc=0x800aae64`
finns både set-index i `s0` och den felaktiga parser-source i `s2`; de
hydratiserade distinct-source-fönstren ligger deterministiskt på
`0x802e1718 + index * 0x2000`. En default-off registerremap precis före den
guest-helper som beräknar `v0` kan testa denna hypotes utan att mutera den
globala asset-tabellen eller röra sampler/upload-layout. Först descriptor-
diversitet vid f300, sedan bildvärde vid f700, får avgöra om spåret lever.

Den nya default-off-proben
`EUTHERDRIVE_GAUNTDL_EXPERIMENT_RUNTIME_BGLOADMODEL_TEXTURE_SET_DISTINCT_SOURCE`
gör exakt denna remap vid descriptor-loaden `pc=0x800aae3c`. I den kalla
f300-körningen är bara index 1-fönstret hydratiserat vid producentögonblicket:
set 1 ändras från `0x802e2158` till `0x802e4900` via source `0x802e3718` och
`tableIndex=0x20`; set 2--8 lämnas oförändrade av range-/empty-guarderna.
Resultatet är kausalt (`frameHash=0x38072b81` mot baseline `0xd083385f`).
F700-bildtestet avbröts på användarens begäran innan resultat, så flaggan
förblir strikt experimentell och default-off vid denna checkpoint.

### F700 avvisar texture-set-indexremappen

Den avbrutna fortsättningen kördes färdigt 15 juli från exakt den sparade
f300-staten. Set 1-remappen överlever till f700 men är en tydlig regression:

```text
baseline     frameHash=0xf4ccc0af  swap=779
set 1 remap  frameHash=0x61974e27  swap=456
```

Kandidatbilden blir nästan vit med två små brus-/randytor och innehåller ingen
igenkännbar grafik. Hypotesen att texture-set-index 1--8 motsvarar de syntetiska
QIO-indexen `gei/snm/stk/...` är därmed avvisad. Proben ligger kvar default-off
endast som kausalitetskontroll.

En ny observationsren lookup-trace vid `pc=0x800a92a8` följer den faktiska
packade ABI:n genom hela orakeln. Kall f0--f700 och f700--f1000 använder bara:

```text
set 0, record 0 -> 0x802e2158
set 10, record 0 -> 0x80332a00
```

Efter coin/start tillkommer exakt:

```text
set 0, record 1 -> 0x802e21a8
```

Inget lookup av set 1--8 observeras. Tracen behåller f700
`frameHash=0xf4ccc0af`/swap 779 och f1200
`frameHash=0xacaece21`/swap 1387 exakt. Den aktiva world-vägen väljer alltså
avsiktligt de två intilliggande recorden i set 0; aliaseringen av set 1--8 är
inte dess descriptorfel.

CPU-trace runt `0x800abeb0 -> 0x800a64a0` stänger även den misstänkta
chunk-offseten. Gästkoden läser recordets `+0x08`, maskar till 0x200-gräns och
publicerar resultatet i `state+0xf180`. Exemplet `0x18f20 -> 0x18e00` är korrekt
gästsemantik; nollresultatet för record 0 är inte en tappad filread.

### Sammanhängande static-texture-källa är kausal men otillräcklig

Den sena 256-paketsrunnen läser 64 KiB sammanhängande från
`0x802e2c68`, men baseline har efter första 8 KiB fyllt samma RAM-arena med
starten av flera separata indexed assets. En default-off-probe hydrerar därför
`0x802e3718..0x802f3717` med den byte-exakta fortsättningen av
`static_lr/textures.rom` från raw-disk-offset `0x0fbb2830`.

Vid f1050 är proben bild-/hashneutral men ökar icke-noll texture-writes från
cirka 1,55 miljoner till 4,33 miljoner. Vid f1200 är den kausal:

```text
baseline    frameHash=0xacaece21  swap=1387  zero samples=61,264,014 / 79,658,746
contiguous  frameHash=0x6813a734  swap=1379  zero samples=65,613,776 / 89,848,972
```

Den relativa nollsample-andelen sjunker från cirka 77 % till 73 %, men bilden
är fortfarande oläsbart brus och horisontella band. Proben förblir därför
default-off. Resultatet visar att source-innehållet påverkar den aktiva ytan,
men återställer inte den saknade page-livstiden.

Nästa slice ska utgå från de bevisat konsumerade `set 0 / record 0--1` och
koppla deras deklarerade page-/LOD-intervall till den fysiska upload-sida som
ska äga samples över `0xffff`. Ändra inte set 1--8, den korrekta
`0x800a64a0`-offsetberäkningen eller global sampler-wrap. Den första kandidaten
ska antingen materialisera record 0/1:s companion-page på rätt Type5-target
eller visa exakt vilken senare upload-trigger som uteblir.

### `sel_lr`-payloaden når den aktiva sena upload-runnen

Den sammanhängande `static_lr`-kontrollen kombinerades först med den befintliga
default-off 64 KiB-sampler-wrapen. Vid f1200 sjönk nollsamplingen från cirka
73 % till cirka 5 % (`4 474 325 / 89 848 972`), men bilden förblev brus och
band (`frameHash=0xbb99f9de`). MAME-korrekta Type5/seq8/sparse-uploadregler
ändrade innehållet till `0x408bbc43` men gav fortfarande ingen scen. Adress-
täckning är alltså kausal, men `static_lr` är fel logisk payload för world-
recordet.

Den äldre extent-kartan pekar i stället ut Hall of Legends-världens
`sel_lr/textures.rom` vid raw-diskbas `0x01407000`. Source-hooken vid
`pc=0x800fe228` accepterar nu en default-off matchadress via:

```text
EUTHERDRIVE_GAUNTDL_EXPERIMENT_RUNTIME_WORLD_TEXTURE_UPLOAD_SOURCE_ADDRESS
EUTHERDRIVE_GAUNTDL_EXPERIMENT_RUNTIME_WORLD_TEXTURE_UPLOAD_SEQUENTIAL_ROWS
```

Med match `0x802e2c68`, filoffset `0x18f20`, diskadress `0x0141ff20` och den
redan experimentella linjära download-placeringen träffar hooken den verkliga
f1000--f1050-runnen. Scratch-bas för varje packet ger 8 167 424 texture-writes,
32 768 berörda ord och f1200 `frameHash=0xddd5b6b5`; bilden fylls med den
förväntade grön/blå payloadfamiljen men upprepar samma källa i band.

En första kontroll tolkade `a1 >> 16` som radindex. Anropen ser ut så här:

```text
hit 0 -> selector 0
hit 1 -> selector 1
hit 2 -> selector 1
hit 3 -> selector 2
hit 4 -> selector 2
```

Att felaktigt använda hit-numret som 0x100-byte-offset gav f1200
`frameHash=0x45e253b3`. När scratchadressen i stället blir
`scratch + ((a1 >> 16) & 0xff) * 0x100` blir f1200 `0x3241abcd` och mittfältet
får tydligare sammanhängande orange/grön struktur. Bilden är fortfarande inte
en korrekt scen, så både source-remappen, selector-offseten och den linjära download-
placeringen förblir default-off.

En full payload/link-trace korrigerar dock även denna ABI-tolkning. Varje
selector-träff startar själv en komplett 256-paketsrun. Inom runnen gör
låg-nivåkoden redan den riktiga radförflyttningen:

```text
packet 0  source=scratch+0x0000 targetWord=base+0x0000
packet 1  source=scratch+0x0100 targetWord=base+0x0080
packet 2  source=scratch+0x0200 targetWord=base+0x0100
...
packet 255
```

Varje selector-index kör två sådana runs. Den första använder
`sourceBase=0x00200000` och Type5-targets från `0x00080000`; den andra använder
`sourceBase=0` och targets från noll. Detta är de två TMU-bankerna, inte två
texelrader. `a1 >> 16` är därmed ett yttre selector-/surface-index och den
befintliga `SEQUENTIAL_ROWS`-flaggan är endast en negativ offsetkontroll trots
sitt äldre namn; den ska inte promoveras.

Det förkastade försöket att skriva payloaden direkt över levande RAM vid
f1000 togs bort; det förstörde arenaägarskap och stoppade coin/start-
progressionen. Nästa slice ska koppla varje selector-index till recordets
verkliga companion-file-offset/LOD-span före de parade TMU-uploadsen. Att ge
alla selectors samma 64 KiB-bild eller bara flytta den `index * 0x100` är båda
fel abstraktionsnivå. Det är nu ett selector-till-surface-problem, inte ett
RGB332-format- eller `static_lr`-filproblem.

### Selector-recorden bevisar 64 KiB surface-steg

En ny observationsren call-trace vid `0x800a761c` och `0x800a7764` binder
record, selector, output och TMU-anrop i samma rad. Den sena
`0x802e2c68`-familjen bygger 0x50-byte-record där `record+0x1c` och `a1`
fortskrider som `0x00000000`, `0x00010000`, `0x00020000`, ... . Varje selector
körs i ett primärt och ett sekundärt anrop för de två TMU-bankerna. Den äldre
`SEQUENTIAL_ROWS`-kontrollen använde alltså fel storleksordning när den lade
till `selectorIndex * 0x100`; selectorn uttrycker ett 64 KiB surface-steg.

Den nya default-off-kontrollen
`EUTHERDRIVE_GAUNTDL_EXPERIMENT_RUNTIME_WORLD_TEXTURE_UPLOAD_SELECTOR_BYTE_OFFSETS`
hydrerar den kvarvarande `sel_lr/textures.rom`-extenten efter recordoffset
`0x18f20` en gång och väljer `scratch + a1`, med en strikt extent-/64 KiB-
range-guard. Från samma rena f1000-state ger den vid f1200:

```text
baseline              frameHash=0xacaece21  zero=61,264,014 / 79,658,746
selector byte offsets frameHash=0xc8ffb828  zero=57,623,434 / 79,658,746
```

Payloaden blir kausalt grön/blå men bilden förblir brus och band. När samma
source-kontroll kombineras med de redan default-off linjära download- och
separata TMU-bankkontrollerna blir resultatet:

```text
frameHash=0xdfc93708
zero=3,891,897 / 79,658,746
touched=32,768 words, last=0x187fbf
swap=1387
```

Nollsample-andelen sjunker därmed till cirka 4,9 procent utan att en
igenkännbar scen uppstår. Detta stänger ren payloadtäckning som ensam blockerare.
Nästa slice ska länka selector-recordens logiska 64 KiB-surface till den
fysiska LOD/base-layout som den aktiva Type3-descriptorn (`base=0x1c00`,
`lod=0x20c6`) samplar. Source-, linear- och TMU-bankflaggorna förblir
default-off tills den layouten kan uttryckas som en generell Voodoo-regel.

Primärdescriptorns companion-kandidat `0x14fe8` testades därefter via den nya
default-off-parametern
`EUTHERDRIVE_GAUNTDL_EXPERIMENT_RUNTIME_WORLD_TEXTURE_UPLOAD_FILE_OFFSET`.
Med samma selector-, linear- och bankstack gav den f1200
`frameHash=0x70c9db06`, `zero=4,075,149 / 79,658,746` och samma rand-/mosaikklass.
Den är alltså inte bättre än record `0x18f20` trots nästan full texeltäckning.

Det visar att de många selector-anropen inte får lösas mot en enda companion-
extent. Nästa ägarskapsgräns är gruppbytet som ska välja record/asset före
varje serie selector-anrop; först därefter kan `fileOffset + selector` vara en
giltig source-regel. En global `sel_lr`-extent, oavsett om den börjar vid
`0x14fe8` eller `0x18f20`, ska inte promoveras.

### Gästkoden materialiserar separata TMU-mål före Type5

Main-RAM-writetracen täcker nu även `sb`, `sh` och den kända byte-move-
fastpathen. Det stängde ett observationshål där den sparsamma assettabellen såg
ut att uppstå utan någon producent. Tabellen vid `asset[16]=0x80304220` byggs
av `0x800aace4/0x800aacf0` som `0x1188` stycken 0x24-byte-entries. Endast
`entry+0x1d/+0x1e` initieras här, med sekventiella 16-bitars ID:n. Tabellen är
alltså en gästägd objekt-/record-pool, inte en lista med companion-filoffsetar.

Den senare 0x50-byte-uploadtabellen vid `0x813815a0` har en separat och mer
direkt betydelse. En write-watch från den rena f1000-staten visar:

```text
primary   pc=0x800a75c8  record+0x1c = selector/page
primary   pc=0x800a7620  record+0x0c = 0x80000, 0x82000, 0x84000, ...
secondary pc=0x800a7710  record+0x1c = selector/page
secondary pc=0x800a7768  record+0x0c = 0x02000, 0x04000, 0x06000, ...
source owner in t2 = 0x802e2c68
```

Primär- och sekundärvägen räknar själv fram olika 8 KiB-steg för de två
TMU-bankerna innan upload-wrappern anropas. Selectorvärdet är därför en del av
gästens target-/surface-layout, inte en rå offset in i `sel_lr/textures.rom`.
Det förklarar varför nästan full texeltäckning med `fileOffset + selector`
fortfarande gav mosaik.

Körningen behöll den kanoniska f1050-orakeln exakt:

```text
frameHash=0xf4ccc0af
swap=1263
fifoWords=10292873
texWrites=8788243
```

Nästa slice ska följa `record+0x0c` från `0x800a7620/0x800a7768` till det
Type5-targetord som Voodoo-backenden tar emot. Om targetsteget tappas där ska
det repareras i den generella packet-/download-avkodningen; selectorvärdet ska
inte längre användas för att välja en ny filkälla.

### Type5-targeten är frisk men source-ordinariet tappas

En sammanhängande selector-/payload-link-trace korrigerar targethypotesen. Det
fysiska Type5-fönstret är avsiktligt fast per TMU:

```text
primary   packetSource=0x00200000 targetWord=0x00080000
secondary packetSource=0x00000000 targetWord=0x00000000
```

Före varje payload skriver gästen den selectorberoende startadressen till
texture-base-registret. `record+0x0c` är returvärdet/nästa lediga adress, inte
ett Type5-targetord som fastpathen har tappat. Två hårdvarunära A/B-kontroller
bekräftade detta från ren f1000 till f1200:

```text
separat 4 MiB texture-RAM per TMU       frameHash=0xacaece21
separata banker + MAME write-pointer    frameHash=0xacaece21
zero samples                            61,264,014 / 79,658,746
swap                                    1387
```

Bankseparationen ökade antalet icke-nollord från 14 244 till 20 560 men
ändrade varken den synliga ytan eller samplerresultatet. Den är korrekt som
hårdvarumodell men inte den aktuella synliga blockeraren och förblir därför
default-off i denna slice.

Den verkliga brutna kedjan ligger på source-sidan. För selectors
`0x00000000`, `0x00010000`, `0x00020000`, ... ändras record/base korrekt, men
varje 256-paketsrun börjar ändå på samma RAM-källa:

```text
source=0x802e2c68 words=64 packets=256
```

Texture-info-strukturen vid `a3` har samtidigt ett separat fält vid `+0x18`
som fortskrider `0,1,2,3,...`, medan `+0x10` förblir `0x802e2c68`. Nästa
kausalitetsprobe ska därför följa producenten av `info+0x10` tillsammans med
ordinalen i `info+0x18`. Målet är att avgöra om pekaren ska materialiseras som
en page-specifik source eller om den lägre upload-funktionen ska applicera
ordinalens stride. Fler Type5-target-, bank- eller write-pointer-remaps är nu
falsifierade för den synliga ytan.

### Source-producenten bekräftar katalogrot, inte sidstride

En generell, default-off värdeövergångsprobe kan nu följa ett valfritt
main-RAM-ord över både vanliga CPU-instruktioner och direkta fastpaths:

```text
EUTHERDRIVE_GAUNTDL_TRACE_MAIN_RAM_VALUE_TRANSITION_ADDRESS
EUTHERDRIVE_GAUNTDL_TRACE_MAIN_RAM_VALUE_TRANSITION_MIN_VALUE
EUTHERDRIVE_GAUNTDL_TRACE_MAIN_RAM_VALUE_TRANSITION_MAX_VALUE
EUTHERDRIVE_GAUNTDL_TRACE_MAIN_RAM_VALUE_TRANSITION_LIMIT
```

Proben rapporterar både föregående och nästa PC, så delay-slot-skrivningar kan
identifieras utan att varje direkt RAM-väg först måste instrumenteras. Den
tidiga sekundärserien bekräftar att gästtolkningen kan välja skilda källor:

```text
writerPc=0x800a7344 source=0x802e1719 record=0x802e2158
writerPc=0x800a7344 source=0x802f918c record=0x802e21a8
```

Den fokuserade `0x802e2c68`-familjen går genom exakt samma delay-slot-store,
`0x800a7344: sw v0,0x20(sp)`. Den kanoniska f1000+1M-körningen visar däremot
att källan medvetet hålls konstant medan output-recordet fortskrider:

```text
a0=3 a1=0x813815a0 a3=0x1188 source=0x802e2c68 ordinal=0
a0=3 a1=0x813815f0 a3=0      source=0x802e2c68 ordinal=1
a0=3 a1=0x81381640 a3=0      source=0x802e2c68 ordinal=2
```

`a0=3` väljer nolloffset i den redan verifierade `0x800a64fc`-tabellen, medan
`a1` väljer nästa 0x50-byte-outputpost. Disassembly av hela `0x800a7094`
stänger dessutom den tidigare strukturmissen: det observerade ordet vid
`output+0x18` är samma adress som funktionens `sp+0x28`. Det nollställs av
`afa00028` före loopen och inkrementeras vid `0x800a7834`; det är alltså
funktionens loopräknare, inte ett texture-info-fält. Den faktiska infon upptar
sex ord genom `+0x14`. Efter source-store vid `0x800a7344` skickar anropet vid
`0x800a7354` endast info `+0x00`, `+0x04`, helperresultatet och `+0x0c`.

En generell `source + ordinal * stride`-reparation är därmed definitivt
falsifierad och ska inte implementeras. Nästa kausala gräns ligger efter
katalogtolkningen: följ hur respektive outputrecord binder den fasta Type5-
uppladdningen till den Type3-descriptor/LOD/base-layout som samplar den.

Observationskörningarna behöll f1050-oraklet exakt:

```text
frameHash=0xf4ccc0af
swap=1263
fifoWords=10292873
texWrites=8788243
```

### Outputrecordets statusfält och den aktiva sampler-ownern

En efterföljande read-watch över `0x813815a0..0x813825a0` visar att tabellen
inte lämnas över till någon separat sen konsument före uploaden. Efter den
inledande recordscannen läser byggloopen själv `record+0x0c/+0x10/+0x14/+0x1c`
och anropar `0x801094f4` inline. Disassembly efter `0x800a7800` stänger även
tolkningen av `record+0x10`: ordet maskas med `0x0fef`, bitarna `0x0700`
testas mot `0x0100`, och `record+3` markeras. Det är statusflaggor, inte en
saknad texture-base eller companion-offset.

Selector-call-tracen namnger därför nu de tre relevanta recordfälten direkt:

```text
recordNext     = record+0x0c
recordStatus   = record+0x10
recordSelector = record+0x1c
```

En kontinuerlig owner-trace från den rena f1000-staten till nästa Type3-burst
kopplar samtidigt den synliga drawen till den sena uploadfamiljen utan en
ordinalhypotes:

```text
sampler: mode=0x8c24100f lod=0x000020c6 regbase=0x00001c00
sample:  base=0x00e510, adresser upp till 0x01900f
writer:  pc=0x800fe5d4 mode=0x0c26100f lod=0xff802000 base=0
source:  run 0x802e2c68, packet sources 0x802e2f74..0x802f2974
target:  0x080180..0x087e80
```

För representativa trianglar är merparten av samples fortfarande `writers=none`;
de ägda låga adresserna kommer från de sena LOD0-paketen, medan högre delar av
`0x015000..0x019000` inte återtas. Detta bekräftar page-livstids/layoutgränsen
med den korrigerade info-/record-layouten. Nästa ändring ska därför binda den
uteblivna senare world-uploaden till samplerbasen; `record+0x10`, loopIndex och
en syntetisk source-ordinal är nu uteslutna.

### Set 0 record 1 publicerar en egen descriptor

En samtidig lookup-/descriptor-trace stänger hypotesen att den senare
world-descriptorn aldrig publiceras. Set 0/record 0 muteras först från en tidig
`base=0x12000/lod=0x2000`-form till den faktiskt samplade formen:

```text
set 0 record 0 -> descriptor 0x802e2158
mode=0x8c24100f lod=0x20c6 base=0x1c00
```

Därefter gör gästkoden ett verkligt lookup av grannrecordet och publicerar en
annan descriptor:

```text
set 0 record 1 -> descriptor 0x802e21a8
mode=0x8c241faf lod=0x2cea base=0x2000
```

Den uteblivna sidägaren beror alltså inte på att record 1 saknas ur settabellen
eller att dess descriptor-store hoppas över. Descriptor-tracen rapporterar nu
`textureSetRecord=set:record` genom att matcha descriptoradressen mot de 16
levande setbaserna. Nästa gräns är smalare: bind record 1:s separata
`mode/lod/base` till dess Type3-draw och Type5-owner, och avgör varför den inte
återtar de owner-lösa adresser som record 0 senare samplar.

### Record 1 publiceras men når inget Type3-paket

Type3-tracen har nu default-off-filter för det texture-state som är aktivt när
paketet konsumeras:

```text
EUTHERDRIVE_GAUNTDL_TRACE_VOODOO_TYPE3_TEXTURE_MODES
EUTHERDRIVE_GAUNTDL_TRACE_VOODOO_TYPE3_TEXTURE_LODS
EUTHERDRIVE_GAUNTDL_TRACE_VOODOO_TYPE3_TEXTURE_BASES
```

Från samma rena f1000-state och över 5,1 miljoner CPU-steg fyllde record 0:s
exakta state tracegränsen med 64 Type3-paket:

```text
mode=0x8c24100f lod=0x20c6 base=0x1c00
cmd=0x0180a8cb writer pc=0x800bd19c
```

En separat körning som endast accepterade record 1:s
`0x8c241faf/0x2cea/0x2000` gav noll Type3-träffar, trots att samma tidsfönster
bevisligen publicerar descriptorn. Record 1 är alltså descriptor-only i den
aktuella bursten och ska inte förväntas återta record 0:s samplesidor.

Returvägen vid `0x800bd764` testar dessutom objektets `byte[3] & 0x10` efter
descriptorfunktionen; den är en separat objektflagga och inte ett uteblivet
texture-set-lookup. Nästa bringupgräns flyttas tillbaka till record 0 självt:
dess beräknade texture-state innehåller basregistren `0xe000`, `0x14fe8`,
`0x17a94` och `0x17f60`, medan den sena ägartracen huvudsakligen ser en upload
vid den första basen och owner-lösa högre sampleadresser. Följ vilka fysiska
Type5-intervall som faktiskt materialiseras innan någon record-1-remap testas.

### Record 0 laddar endast den första 256x32-stripen

Type5-sekvenstracen kan nu filtrera på det färdigmappade fysiska ordintervallet:

```text
EUTHERDRIVE_GAUNTDL_TRACE_VOODOO_TYPE5_TEXTURE_UPLOAD_SEQUENCE_PHYSICAL_WORD_MIN
EUTHERDRIVE_GAUNTDL_TRACE_VOODOO_TYPE5_TEXTURE_UPLOAD_SEQUENCE_PHYSICAL_WORD_MAX
```

Från den rena f1000-staten materialiserar upload-state
`mode=0/lod=0x00700800/base=0x1c00` exakt 32 paket om 64 ord i
`phys=0x3800..0x3fff`, alltså byte `0xe000..0xffff`. Den LOD-layouten är
256x32 och de 32 paketen fyller hela dess 8192 byte. Ett separat filter över
`phys=0x4000..0x7943` gav noll träffar både efter 1,0 och 5,1 miljoner
CPU-steg. Den aktiva draw-state som vår sampler ser är samtidigt
`mode=0x8c24100f/lod=0x20c6/base=0x1c00`; nuvarande LOD-avkodning gör den
256x256 och kan därför adressera långt ovanför den enda materialiserade
stripen.

Det sena `base=0`-flödet är en separat överlappande ägare, inte den saknade
övre delen. Dess 64 KiB-fönster slutar vid byte `0xffff`, så endast de sista
32 paketen träffar `0xe000..0xffff`; inga av dem kan äga adresser från
`0x10000` och uppåt. Hela f1000+5,1M-körningen behöll sin kanoniska endpoint:

```text
frameHash=0x42925e78
swap=1299
fifoWords=10323854
texWrites=8788243
```

En default-off kausalitetsprobe kan OR:a en mask endast i samplerns LOD-read:

```text
EUTHERDRIVE_GAUNTDL_EXPERIMENT_VOODOO_TEXTURE_SAMPLE_LOD_OR_MASK
```

Att testa `0x00700000` tillsammans med sample-bias `0` tvingade samma
256x32-aspect och ändrade f1050-hashen från `0xf4ccc0af` till `0x13ddbe72`,
men dumpen blev fortfarande felaktiga vertikala/bandade data. Aspectmasken är
alltså kausal men inte en fix och förblir default-off.

Bakåtspårningen korrigerar dessutom ordet "descriptor" i tidigare slutsatser.
`0x802e2158` är den råa 0x50-byte-materialposten, inte ett färdigt block med
sex Voodoo-register. Gästkoden vid `0x800bd130..0x800bd19c` beräknar bland
annat:

```text
textureMode = a2 | ((raw+0x10 & 0x0fe0) | globalModeBits)
textureLod  = (raw+0x14 & 0xfffc0fff) | owner+0x144
```

I f300-staten är `raw+0x14=0x000000c6`; ownerfältet bidrar `0x2000`, så
gästen skapar själv `lod=0x20c6`. Inga aspectbitar tappas i vår
registeravkodning. Nästa gräns är därför att binda record 0:s verkliga
texturekoordinater och hårdvaru-LOD-val till 256x32-uploaden: avgör om den är
en avsiktlig staging/atlas-strip som vår sampler adresserar fel, eller om
övriga strip-runs uteblir tidigare i assetkedjan.

### Record 0:s koordinater bekräftar clamp-banden men inte payloaden

`VOODOO-TEXSUMMARY` återanvänder nu de exakta Type3-statefiltren för mode,
LOD och base. Det gör att record 0 kan isoleras utan att andra material fyller
summarygränsen. 32 synliga buffer-1-trianglar visar konsekvent:

```text
mode=0x8c24100f lod=0x20c6 base=0x1c00
sample S ungefär 0..511
sample T ungefär 0..171
sample byteadresser ungefär 0xe510..0x1900f
```

Modebitarna `0x40/0x80` begär inte S/T-clamp, men den kanoniska bringup-
baselinen tvingar clamp. Det förklarar de stora konstanta edge-banden när S
passerar 255. En ny default-off kontroll kan tvinga faktisk wrap genom både
nearest-, bilinear- och MAME-fixed-fetch-vägarna:

```text
EUTHERDRIVE_GAUNTDL_EXPERIMENT_VOODOO_TEXTURE_COORDINATE_WRAP=1
```

Den kompletta 256x32-hypotesen testades med LOD-OR `0x00700000`, coordinate
wrap och sample-bias `0`. Den tog bort de stora horisontella clamp-banden och
ändrade f1000+5,1M-hashen till `0x3cb672e9`, men framebufferdumpen innehåller
fortfarande repetitivt texturbrus i stället för en scen. Wrap är alltså en
verklig presentationseffekt men inte heller en fix.

Nästa kausala gräns ligger nu före samplern: identifiera ursprunget och den
avsedda layouten för de 32 `base=0x1c00`-paketen som redan ligger i FIFO vid
f1000. Deras snapshot-proveniens är tom (`source=-`); följ dem från en tidigare
state eller jämför payloaden mot assetkällan innan fler sample-remaps provas.

### Base 1c-stripen kommer nästan byte-exakt från static_lr/textures.rom

Bakåtspårningen behöver ta hänsyn till att den optimerade guestloopen kan
konsumera hela `0x800fe5d4..0x800fe654` utan att den vanliga instruktionstracen
ser `0x800fe5e8`. En ny default-off run-trace ligger därför inne i den
validerade fastpathen och rapporterar slutlig RAM-källa, FIFO-bas, Type5-target,
packetantal och de första källorden:

```text
EUTHERDRIVE_GAUNTDL_TRACE_TYPE5_PRODUCER_HEADS
EUTHERDRIVE_GAUNTDL_TRACE_TYPE5_PRODUCER_HEAD_FIFO_MIN
EUTHERDRIVE_GAUNTDL_TRACE_TYPE5_PRODUCER_HEAD_FIFO_MAX
EUTHERDRIVE_GAUNTDL_TRACE_TYPE5_PRODUCER_HEAD_SOURCE
EUTHERDRIVE_GAUNTDL_TRACE_TYPE5_PRODUCER_HEAD_LIMIT
```

Proben namnger raderna `GAUNTDL:TYPE5-PRODUCER-RUN`; de äldre `HEAD`-namnen
på miljövariablerna behålls för kompatibilitet med pågående bringup-skript.
GauntletProbe kan dessutom söka en ordsekvens i hela snapshotens main RAM i
båda byteordningarna:

```text
EUTHERDRIVE_GAUNTDL_SCAN_MAIN_RAM_WORDS=02000000,00000000,...
```

Den fulla payloaden från de 32 kanoniska paketen dumpades via
`VOODOO-TYPE5-TEXSEQ` med 64 ord per packet. Resultatet är 2048 ord/8192 byte.
De första 16 orden matchar raw-disken vid `0x0fbb0831`, och en jämförelse av
hela runnen mot intervallet `0x0fbb0831..0x0fbb2830` visar:

```text
payload bytes = 8192
payload sha256 = fcc2e3249bd1137180215a741a960c4c87c6896c38d7afd947a9a99fd5c9b3c3
equal bytes = 8162 / 8192
different bytes = 30
```

Diskintervallet ligger i det redan identifierade
`static_lr/textures.rom`-blocket som börjar vid `0x0fbb0830`. Avvikelserna är
inte en generell swizzle eller packetförskjutning: 29 byte ligger i små
kluster i packet 11--13 och den sista byten av packet 32 avviker. Alla andra
8162 byte, inklusive packetgränserna, är identiska och i rätt ordning.

Det falsifierar att record 0:s 256x32-strip består av korrupt FIFO-data eller
fel packetordning. Den är en nästan byte-exakt kopia av en riktig assetkälla;
de få avvikelserna är förenliga med guestmutation eller arenaöverlapp och ska
spåras separat. Exakt ordsekvens finns inte längre kvar i main RAM i vare sig
f700- eller f1000-snapshoten, så snapshotens tomma `source=-` kan inte
återskapas genom en sen RAM-sökning.

En ny kanonisk mellanstate finns vid
`/tmp/eutherdrive-gauntlet-probe/gauntdl-ordinary60-f900-20260716.warm` och
behåller `frameHash=0xf4ccc0af`. Den gör fortsatta producentförsök begränsade
till f900--f1000. Nästa kausala gräns är nu de 30 muterade bytena och bindningen
mellan denna bevisat källtrogna 256x32-strip och draw-state
`lod=0x20c6/base=0x1c00`; fler generella FIFO-/endianness-remaps saknar stöd.

### Base 1c-stripens exakta RAM-källa är nu bevarad

Warmup-format 7 serialiserar även standard-FIFO:ns två proveniensmappar. Format
1--6 kan fortfarande läsas; de saknar bara denna metadata. Ett f700--f900-save
och f900--f1000-reload bevarade tidigare fastpath-källor, inklusive
`source/root=0x803129a4`, utan ändrad baselinehash.

Den långsamma gästvägen behövde separat proveniens eftersom den skriver samma
packetloop instruktion för instruktion i stället för genom bulk-fastpathen.
Kärnan märker nu endast de fyra verifierade FIFO-store-instruktionerna och
röjer märkningen direkt efter varje write:

```text
0x800fe5e8  header
0x800fe5f8  target
0x800fe60c  payload word 0,2,...
0x800fe614  payload word 1,3,...
```

Den kanoniska baseline-runnern från v7-f900, följd av `+5100000` CPU-steg,
reproducerar den tidigare referensen exakt:

```text
packet=0x01eb04d0
target=0x000000..0x00003f
phys=0x03800..0x0383f
tmode=0x00000000 tlod=0x00700800 tbase=0x00001c00
source=0xffffffff802e1719 root=0x802e1719
packetSource=0x00000000 packet=0 index=0/31 payloadWords=64
writer PCs=0x800fe5e8/0x800fe5f8/0x800fe60c
frameHash=0x42925e78 swaps=1299
```

Det ersätter den sista manuella gissningen med ett direkt samband: de 32
paketen kommer från den avsiktligt udda byte-stream-cursorn `0x802e1719`.
Lågbitshypotesen ska inte öppnas igen som generell fix; tidigare cold-runs har
redan visat att maskning till `0x802e1718` ändrar bilden men inte ger riktig
grafik. Nästa användbara gräns är i stället att följa hur draw-state
`lod=0x20c6/base=0x1c00` väljer och tolkar just denna bevisade 256x32-asset,
eller att lokalisera de 30 guestmuterade bytenas writers före uploaden.

### De 30 avvikelserna är arenaöverlapp, men drawen ser senare writers

29 av de 30 byteavvikelserna mot rådisken ligger i sju poster med start
`0x802e2158`, stride `0x50` och fält vid bland annat `+0x03`, `+0x0c`,
`+0x10`, `+0x18` och `+0x1c`. Den sista avvikelsen ligger exakt på nästa
`0x2000`-slotgräns, `0x802e3718`. Det är strukturerad arenaöverlapp, inte
slumpmässig texturkorruption.

En cold--f300 main-RAM-write-watch fångade de verkliga postkonstruktörerna:

```text
0x800a7124  sh ...,0x18(s2)
0x800a7710  sw ...,0x1c(s2)
0x800a773c/0x800a7744/0x800a7768  writes vid +0x0c
0x800a780c  write vid +0x10
0x800a7830  byte-write vid +0x03
snapshot=/tmp/eutherdrive-gauntlet-probe/gauntdl-record-overlap-watch-f300-20260716.warm
frameHash=0xd083385f
```

Det default-off kirurgiska experimentet
`EUTHERDRIVE_GAUNTDL_EXPERIMENT_STATIC_LR_RECORD0_CLEAN_DISK_PAYLOAD=1`
ersätter endast de verifierade 32 packetens payloadord från root
`0x802e1719` med motsvarande rådiskord. Det ändrade den kanoniska hashens
`0x42925e78` till `0x27928536` utan att ändra packet-/swapräknarna, men den
dumpade bilden förblev oigenkännlig. De muterade orden når alltså renderingen,
men en ren record0-payload är inte den visuella lösningen.

Triangelsammanfattningen kan nu avgränsas direkt med
`EUTHERDRIVE_GAUNTDL_TRACE_VOODOO_TEXTURE_TRIANGLE_SAMPLE_SUMMARY_REGISTER_BASE`.
En v7-f900--f1000-`+5100000`-körning med värdet `0x1c00` reproducerade åter
`frameHash=0x42925e78`, `swaps=1299` och fångade 16 aktiva trianglar med:

```text
mode=0x8c24100f lod=0x000020c6 regbase=0x00001c00
base=0x00e510 sampled=0x00e510..0x01900f
record0 writer source=0xffffffff802e1725 packet=0/31 frame=986
later writers sourceBase=0x00200000 frame=986
record0 förekommer främst som prev=...src0xffffffff802e... eller wrap64writer
```

Det flyttar nästa kausala gräns: draw-state väljer den förväntade base-1c-
regionen, men de lästa writer-generationerna domineras av senare 2 MiB-
streamuppladdningar och många ord saknar writer. Nästa försök ska därför följa
varför `srcBase=0x00200000` aliaserar/ersätter base-1c-generationen i
texturadresskartan, inte göra fler raw-disk-remaps av record0.

### MAME-LOD-proben och den inverterade tLOD-gränsen

MAMEs Voodoo-rasterizer väljer inte ett konstant LOD. Den beräknar först
`log2(max(ds²+dt²))/2` från 32.32-gradienterna, subtraherar `log2(W)` när
texture-mode bit 0 begär perspektiv, lägger på bias och klampar till tLOD:s
8.8-intervall. En ny observationsren trace gör samma beräkning per triangel:

```text
EUTHERDRIVE_GAUNTDL_TRACE_VOODOO_TEXTURE_TRIANGLE_LOD=1
EUTHERDRIVE_GAUNTDL_TRACE_VOODOO_TEXTURE_TRIANGLE_LOD_LIMIT=16
```

Tillsammans med register-base-filtret `0x1c00` visar den kanoniska bursten:

```text
mode=0x8c24100f lod=0x000020c6 base=0x1c00
bias8p8=128 min8p8=384 max8p8=192
fullrect: centroidW=1       candidate=245
world:    centroidW=0.015625 candidate=729..278
```

Min/max är alltså inverterade i det guestproducerade `0x20c6`-värdet.
Den första proben modellerade detta med två sekventiella jämförelser och
rapporterade därför LOD0/LOD1. Det var inte MAME-paritet.

Det default-off kausalitetsexperimentet
`EUTHERDRIVE_GAUNTDL_EXPERIMENT_VOODOO_TEXTURE_MAME_TRIANGLE_LOD=1` matar den
beräknade nivån till den befintliga samplern. Från v7-f900 till den kanoniska
f1000+5,1M-endpointen gav det:

```text
felaktig probe frameHash=0xc82dc520 zero=21,908,043
```

Det resultatet är bevarat som negativ historik men ska inte användas som
hårdvaruevidens eller promoveras.

### Faktisk MAME-hostsemantik håller alla samples på LOD0

3dfx Voodoo2-specifikationen bekräftar att `tLOD[5:0]` är unsigned 4.2
`lodmin`, `[11:6]` är unsigned 4.2 `lodmax` och `[17:12]` är signed 4.2
`lodbias`. `0x20c6` är alltså verkligen `min=1,5`, `max=0,75`, `bias=+0,5`;
det är inte ett namn- eller endianfel. Specen beskriver det normala intervallet
som `[lodmin,min(8,lodmax)]`. Dagens MAME anropar `std::clamp(lod, lodmin,
lodmax)`; inverterade gränser bryter funktionens formella precondition och kan
därför inte tolkas som en dokumenterad hårdvarutröskel.

Den MAME-host som är relevant för referenskörningen använder libstdc++, vars
implementation (utan assertion-build) är:

```text
min(max(lod, lodmin), lodmax)
```

För `lodmin=384` och `lodmax=192` blir slutvärdet alltid 192, alltså heltals-
LOD0. Proben använder nu exakt den ordningen i en egen helper eftersom C#
`Math.Clamp` korrekt vägrar det inverterade intervallet.

Den default-off proben
`EUTHERDRIVE_GAUNTDL_EXPERIMENT_VOODOO_TEXTURE_MAME_PIXEL_LOD=1` går ett steg
längre än föregående triangelapproximation. Den bygger 32.32-W-gradienter från
samma tre Q-värden som setup-triangeln fångar, använder MAMEs 7-bitars
`fast_log2`-tabell per pixel, applicerar bias, optional 4x4-LOD-dither och
libstdc++-ordningen ovan. Debugstatus visar `plod=` endast när
experimentet är aktivt.

Den kanoniska v7-f900--f1000+5,1M-körningen gav:

```text
baseline       frameHash=0x42925e78 zero=20,927,120
pixel LOD      frameHash=0x42925e78 zero=20,927,120
pixel nivåer   LOD0=47,883,532 LOD1..8=0
packets/swaps  oförändrade, fifoWords=10,323,854 swaps=1299
```

Den korrigerade per-pixelvägen är alltså bit-/hashmässigt neutral mot baseline
och samstämmig med guestens LOD0-only-upload. Det finns inte längre stöd för
att jaga en saknad LOD1-mip i den här endpointen. Proben behålls default-off
som paritetskontroll; den synliga brus-/mosaikbilden ligger kvar i LOD0:s
upload-, ägar- eller sampleadressrelation.

Primärkällor:

```text
https://www.bitsavers.org/components/3dfx/Voodoo2_Spec_r1.16_199912.pdf
https://github.com/mamedev/mame/blob/master/src/devices/video/voodoo_render.cpp
https://github.com/mamedev/mame/blob/master/src/devices/video/voodoo_regs.h
```

### Den tidigare LOD1-writertracen var en probe-artefakt

Triangelsammanfattningen grupperar nu sample-writers per faktiskt dynamiskt
vald LOD när pixel-LOD-experimentet är aktivt. `targetLod=dynamic` skiljs från
`layoutLod=0`, som bara är summaryns bakåtkompatibla nominella layout;
`lodwriters=lN=...` och det observerade adressintervallet är sanningen för
varje pixelgrupp.

Den tidigare felaktiga klampordningen skickade vissa samples till LOD1:

```text
LOD1 addrs=0x01e510..0x02250f
lodwriters=l1=32625[none:32625]
lodwriters=l1=33277[none:33277]
råvärde/rgb = 0x00/0x0000 för samtliga observerade samples
```

LOD0-worldtrianglarna väljer i stället `0x00e510..0x01900f`. Där saknar
ungefär 71--98 procent av samples en direkt writer beroende på triangeln. De
få direkta ägarna är konsekvent den sena LOD0-familjen från
`srcBase=0x00200000`; den tidigare record0-generationen förekommer bara som
`prev=` eller i 64 KiB-wrapjämförelsen.

Det är fortfarande sant att den syntetiska regionen saknar writers, men den
korrigerade MAME-proben väljer den aldrig. Evidensen ska därför inte användas
för att motivera en guest-LOD1-upload eller en mip-remap.

### Uploadströmmen innehåller bara LOD0

Den default-off diagnostiken
`EUTHERDRIVE_GAUNTDL_DEBUG_VOODOO_TEXTURE_UPLOAD_LODS=1` räknar accepterade
texture-port-writes per LOD-fält, hur många som kommer från Type5, hur många
payloadord som är icke-noll samt slutlig fysisk byteadress min--max. Den
ändrar inte upload- eller renderingsvägen och visar `twlod=` endast när den är
aktiverad.

Samma kanoniska v7-f900--f1000+5,1M-körning, tillsammans med pixel-LOD-proben,
gav:

```text
twlod=l0:3390208/3390208/689992@000000-00FFFC
plod=47883532/0/0/0/0/0/0/0/0
frameHash=0x42925e78 fifoWords=10323854 packets=362333 swaps=1299
```

### Emittern använder default-sentinel, inte en materialstorlek

Bounds-tracen omfattar nu även entry `0x801096ac` och avkodar dess verkliga
ABI. Wrappern väljer tabellpost enligt
`0x80158050 + 4 * (9 * stackArg0 + a2)` och läser postens andra ord innan
låg-nivåanropet `0x800fe1fc`. Alla filtrerade record-0-anrop gav:

```text
a2=0
stackArgs=00000000/00000000/00000003/802e1719
table=80158050:00000100/00000020
selectors a1=0000,2000,4000,6000,8000,a000,c000,e000
```

Wrappern dekrementerar tabellens andra ord `0x20` och skickar därmed ett
explicit inklusivt slut `0x1f`. Den tidigare `0x80168050`-adressen kom från
att diagnosticen behandlade instruktionens `addiu ... 0x8050` som unsigned;
MIPS signextendar immediaten och väljer `0x80158050`.

Nästa gräns flyttas därför exakt till `0x800fe1fc`: bind det explicita
packet-/radslutet till selectorserien och de 64-ords Type5-paketen. Ingen
tabellpatch eller syntetisk materialstorlek har stöd.
Den observationsrena kontrollen behöll åter `frameHash=0x42925e78`.

Det finns alltså inte en enda accepterad LOD1--8-write i den observerade
f900--f1000-strömmen. Alla 3 390 208 ord kodar LOD0 i targetfältet, och den
korrigerade samplern väljer också LOD0 för samtliga 47 883 532 pixlar. Detta
är konsistens, inte en saknad packetserie. Nästa gräns är åter den redan
observerade glesa LOD0-ägningen och relationen mellan Type5-target, fysisk
texturadress och den samplade `base=0x1c00`-layouten.

### Den sena LOD0-runnen återpublicerar nästan hela record0 byteidentiskt

Den befintliga overwrite-proben seedades med record0:s 32 Type5-paket
(`targetStart=0x000000..0x000f80`) och avgränsades till den fysiska delen av
den aktiva base-1c-stripen, `0x00e000..0x00ffff`. Proben räknar nu unika ord
som senare återpubliceras byteidentiskt respektive faktiskt ändras. Debugfältet
är:

```text
tovr=seeded/reasserted/changed/stillSeeded
```

Den kanoniska v7-f900--f1000+5,1M-körningen gav:

```text
tovr=2048/1989/1/2047
frameHash=0x42925e78 fifoWords=10323854 packets=362333 swaps=1299
```

Endast ett ord ändras, vid byteadress `0x00e248`:

```text
old=0x10010064 new=0x10010001 mask=0x000000ff
previous target=0x000100 mode=0 lod=0x00700800 base=0x1c00
current  target=0x087100 mode=0x0c26100f lod=0xff802000 base=0
```

Av de 2 048 seedade orden återbesöks alltså 1 990 av den sena runnen: 1 989
med exakt samma ordvärde och ett med en enda ändrad byte. De återstående 58
orden berörs inte senare. Detta falsifierar att den sena
`sourceBase=0x00200000`-familjen generellt korrumperar record0-stripen.
`lastWriter` byts nästan överallt, men texeldata gör det inte.

Nästa gräns ska därför inte vara en owner-preserve- eller overwrite-skip-fix.
Den ligger i varför den bevisat stabila 256x32-stripen tolkas som den aktiva
256x256/base-1c-ytan, samt var de återstående fysiska adresserna som drawen
samplar skulle materialiseras. Den ensamma byteändringen är för liten för att
förklara mosaikbilden och ska endast följas om en senare sample-korrelation
träffar exakt `0x00e248`.

### Den aktiva drawens övre fem stripar materialiseras aldrig

En ny default-off range-summary räknar per valfri fysisk texture-RAM-strip
antalet icke-nollbyte, icke-nollord och ord med observerad writer:

```text
EUTHERDRIVE_GAUNTDL_DEBUG_VOODOO_TEXTURE_RANGE_MIN=0xe000
EUTHERDRIVE_GAUNTDL_DEBUG_VOODOO_TEXTURE_RANGE_MAX=0x1a000
EUTHERDRIVE_GAUNTDL_DEBUG_VOODOO_TEXTURE_RANGE_BLOCK_BYTES=0x2000
```

Fältet `trange=` använder formen `start-slut:nonZeroBytes/nonZeroWords/writerWords`.
Den korrigerade kanoniska körningen satte explicit `WARMUP_FRAMES=900`, laddade
v7-f900-snapshoten, körde till f1000 och därefter ytterligare 5,1 miljoner
CPU-steg. Den reproducerade oraklet exakt:

```text
frameHash=0x42925e78 fifoWords=10323854 packets=362333 swaps=1299
trange=
  00E000-00FFFF:3434/1723/2048
  010000-011FFF:0/0/0
  012000-013FFF:0/0/0
  014000-015FFF:4/1/0
  016000-017FFF:0/0/0
  018000-019FFF:0/0/0
textureMap touched=16384 words, first=0x000000, last=0x00fffc
```

Det finns alltså inte bara ett hål i writer-proveniensen. Ingen observerad
texture-write lämnar den låga 64 KiB-sidan, och av hela området som record 0:s
draw samplar ovanför `0xffff` finns endast ett gammalt fyrbytesord vid
`0x15554`. Den enda verkligt materialiserade delen är 256x32-stripen
`0xe000..0xffff`.

`GauntletProbe` kan nu också dumpa en explicit RGB332-yta med
`EUTHERDRIVE_GAUNTDL_DUMP_VOODOO_TEXTURE_BYTE_SURFACE_SPECS`; specifikationen
`0xe000:256:32:256` gav `nz=2429`, `unique=156`, `transitions=3487`. Den direkta
bilden är en tät högentropisk mosaik, inte en fristående igenkännbar 256x32-
textur. Tillsammans med de tidigare negativa aspect/wrap-proverna talar detta
för att stripen är en sida eller staging-del av en större layout, inte att
drawen enbart ska tvingas till 256x32.

Nästa gräns ligger därför före samplern: identifiera den guest-trigger eller
surface/page-bindning som ska materialisera `0x10000..0x19fff`. Globala
aspect-, wrap-, owner-preserve- och source-remappar saknar fortfarande stöd.

En direkt negativ kontroll flyttade baseline-samplebiasen exakt `-0xe000`, så
record 0:s `base=0x1c00` i praktiken läser den redan fyllda låga 64 KiB-sidan.
FIFO, packet och swaps förblev identiska, medan:

```text
baseline       frameHash=0x42925e78 zero=20,927,120
base-0 binding frameHash=0xe2d1a1e4 zero=20,223,316
```

Framebufferdumpen är fortfarande texturbrus och horisontella band, utan
igenkännbar scen. En ensam base-1c-till-base-0-remap är därmed också avvisad;
den saknade producent-/surface-bindningen kan inte ersättas med historiskt
innehåll från lågsidan.

### Materialposten hydreras och muteras av samma avsiktliga uploadkedja

En stop-run vid den första materialpubliceringen, `0x800bd19c`, visar att
record 0 börjar som den råa posten `0x802e2158` med bland annat:

```text
raw+0x0c = 0x00012000
raw+0x14 = 0x00000000
ownerbidrag = 0x00002000
beräknat lod/base = 0x00002000/0x00012000
```

Två observationsrena main-RAM-writetraces knyter därefter båda relevanta
fältändringarna till den kända slot-0-kedjan. QIO:s direkta disk-byte-kopia vid
`pc=0x800c9944` hydratiserar hela `0x2000`-blocket till `0x802e1718`; posten
ligger `0xa40` byte in i blocket. Samma kopia skriver `raw+0x14` från noll till
`0x000000c6` och ersätter det ursprungliga `raw+0x0c=0x12000` med noll.
Uploadallokatorn använder sedan samma post i stället för en fristående
descriptor och publicerar sekvensen:

```text
pc=0x800a7768  raw+0x0c: 0 -> 0x0400
pc=0x800a7768  raw+0x0c: 0 -> 0x0800
...
pc=0x800a7768  raw+0x0c: 0 -> 0x1c00
next pc=0x801094f4  a1/s0=0xe000  s2=0x802e2158  s3=0x10000
```

Senare läser drawvägen samma in-place-post och kombinerar `raw+0x14=c6` med
ownerfältets `0x2000`, vilket producerar det redan observerade
`lod=0x20c6/base=0x1c00`. Detta falsifierar att värdena uppstår genom en
oavsiktlig descriptoralias eller en tappad postkopia: QIO-hydreringen och
uploadallokatorn bygger materialet avsiktligt i samma arena.

Den befintliga default-off-kontrollen
`EUTHERDRIVE_GAUNTDL_EXPERIMENT_RUNTIME_BGLOADMODEL_STATIC_TEXTURE_CONTIGUOUS_SOURCE`
läser ytterligare `0x10000` byte direkt efter slot-0-blocket. En strikt A/B från
samma v7-f900-state till f1000+5,1M gav:

```text
baseline    frameHash=0x42925e78 packets=362333 Type3=31354
continuation frameHash=0x4a03dffe packets=362531 Type3=31552

continuation trange:
  00E000-00FFFF:7239/2042/2048
  010000-011FFF:0/0/0
  012000-013FFF:0/0/0
  014000-015FFF:4/1/0
  016000-017FFF:0/0/0
  018000-019FFF:0/0/0
```

De extra RAM-bytena är kausala och stör även valet av senare indexed-QIO-källa,
men Voodoo får fortfarande inga writes i de fem saknade striparna ovanför
`0xffff`. Att utöka slot-0-QIO:n eller läsa en blind sammanhängande 64 KiB-yta
är därmed avvisat. Nästa gräns är den guestberäknade uploadgeometrin vid
`0x801094f4`: följ hur den avslutande `a1=0xe000`, `s3=0x10000` och postens
råmetadata blir `lod=0x00700800` och exakt 32 Type5-paket, innan någon ny
sampler- eller diskextent-remap övervägs.

### Uploadhjälparens bounds är noll; geometrin väljs en nivå längre ned

Den nya observationsrena tracen
`EUTHERDRIVE_GAUNTDL_TRACE_RUNTIME_WORLD_TEXTURE_UPLOAD_BOUNDS=1` fångar
upload-info både vid `0x801094f4` och direkt efter dess prepare-anrop vid
`0x8010953c`. Filtrerad på record-0-källan `0x802e1719` visar varje träff:

```text
entry    info=00000000/00000000/00000000/00000000/802e1719/00000000
prepared info=00000000/00000000/00000000/00000000/802e1719/00000000
```

Bounds-/countorden skapas alltså inte av prepare-funktionen. Samma record
anropas i stället upprepade gånger med `a1/s0` i 8 KiB-steg från `0x0000` till
`0xe000`; vissa selectorvärden förekommer för båda `s1`-grenarna. Funktionen
går därefter vidare till den underliggande emittern vid `0x801096ac`, med
tabellval från `0x80157f34/0x80157f50` och stackmetadata.

Det korrigerar den föregående arbetsformuleringen: de 32 Type5-paketen kommer
inte från ett enkelt `info[0]-info[1]`-count som kan repareras vid
`0x801094f4`. Nästa kausala gräns är `0x801096ac`: bind dess tabellindex,
stackparametrar och packetantal per selectoranrop till de slutliga 32 paketen,
och avgör där varför endast `0xe000..0xffff` materialiseras. Den kanoniska
körningen förblev exakt observationsren:

```text
frameHash=0x42925e78 fifoWords=10323854 packets=362333 swaps=1299
```

### Lågnivåtracen korrigerar sentineltesen: gränsen är explicit 31

Bounds-tracen följer nu samma filtrerade source vidare genom `0x800fe1fc` vid
entry, tabellval, FIFO-geometriberäkning och första/sista Type5-paket. Den
återbyggda f900-staten och den kanoniska f1000+5,1M-körningen behöll oraklet
exakt, men visade att den tidigare slutsatsen från wrapper-entryn var fel:

```text
low-entry    source=802e1719 limit=0000001f
low-table    s5=00000100
low-geometry s4=00000040 t0=00000800
low-packet   s2=00000000 sourceCursor=802e1719
low-packet   s2=0000001f sourceCursor=802e3619
```

`0x1f` är alltså ett explicit inklusivt rad-/packet-slut, inte
`0xffffffff`. Den nya `emitter-table`-fasen bekräftar att guestens signerade
`addiu` väljer `0x80158050: 0x100/0x20`, varefter `0x20 - 1` blir `0x1f`.
`s5=0x100`, `s4=0x40` och FIFO-reservationen `t0=0x800`
beskriver exakt 32 paket gånger 64 ord: 2048 ord eller 8192 byte. Den nya
fasen fångar det faktiskt laddade andra tabellordet direkt före `addiu -1`.

Selectorserien ändrar den beräknade targetbasen i lågnivåvägen från `0x0000`
till `0x1c00`, men varken source, bredd eller packetantal. Det förklarar den
observerade låga 64 KiB-publiceringen som åtta avsiktliga 8 KiB-sidor; den är
inte en enda upload som kapas efter första stripen. Det finns därför inget
stöd för att förlänga packetloopen eller ersätta limitvärdet syntetiskt.

Nästa kausala gräns flyttas tillbaka till bindningen mellan dessa åtta
publicerade sidor och draw-state `lod=0x20c6/base=0x1c00`: avgör varför drawen
tolkar den sista sidans bas som en 256x256-yta och samplar vidare till
`0x1900f`, trots att uploadkedjan uttryckligen publicerar separata
256x32-sidor. De tidigare negativa globala aspect-, wrap- och base-remapparna
ska förbli kontroller, inte promoveras till fixar.

Den observationsrena endpointen var oförändrad:

```text
frameHash=0x42925e78 fifoWords=10323854 packets=362333 swaps=1299
```

### Selectorbasen är guestberäknad, inte ett oavsiktligt restvärde

En write-watch över record 0 följd av en kod-dump av `0x800a76c0` visar den
exakta produktionen av de två muterade adressfälten. För varje selectoranrop
gör guestkoden i huvudsak:

```text
0x800a770c  record+0x1c = selector + allocatorReturn
0x800a7734  record+0x0c = tableBase
0x800a7740  record+0x0c = tableBase >> 1  (för byte 2 < 8)
0x800a7750  v0 = selector >> 3
0x800a775c  v0 -= record+0x0c
0x800a7764  delay slot: record+0x0c = v0
0x800a7760  call 0x801094f4
```

För den aktuella posten väljer `raw+0x14=0xc6` tabellpost noll vid
`0x8016200c`; dess värde är noll. Selectorserien
`0x0000,0x2000,...,0xe000` blir därför avsiktligt Voodoo-baserna
`0x0000,0x0400,...,0x1c00`. `record+0x1c` behåller samtidigt den bytebaserade
selectoradressen. Slutvärdet `base=0x1c00` är alltså visserligen sista sidans
värde i den muterbara posten, men det är explicit beräknat av guestens
Glide-liknande adressformel. Att syntetiskt bevara första sidans nollbas har
inte stöd och den tidigare globala base-0-kontrollen förblir avvisad.

Även ownerfältet vid `owner+0x144` kan avgränsas: writern vid `0x800a4d1c`
skiftar ett beräknat värde 12 steg och maskar det med `0x3f000` innan store.
Fältet kan därmed endast bidra med LOD-biasbitar; det kan inte återställa de
aspectbitar som drawens `lod=0x20c6` saknar. `raw+0x14=0xc6` finns dessutom
redan i den råa diskposten och är inte en senare runtime-korruption.

Den smalaste kvarvarande frågan är därför inte hur första basen bevaras, utan
varför samma guestflöde avsiktligt replikerar en explicit 256x32-källa över
åtta 8 KiB-sidor och därefter binder sista sidan med en square-256 draw-LOD.
Nästa spårning ska binda postens byte 0/2 och `raw+0x14` till tabellindexet vid
`0x8016200c` och den senare draw-state-byggaren; ingen backendremap ska göras
innan den relationen är förklarad.

### Upload-aspect byggs separat och förklarar 256x32-tabellvalet

Kodvägen före selectorloopen visar att stack-info inte är en opak struktur.
`0x800a66e8` bygger den från materialposten: byte 1 blir min-LOD, byte 0 blir
max-LOD, byte 2 blir format, `raw+0x08` blir source och halvorden vid
`raw+0x04/+0x06` översätts till Glide-aspect `0..6`. Lika halvord ger aspect
`3` (1:1); exakta 2x/4x/8x-förhållanden ger de övriga enumvärdena. Om inget
sådant förhållande matchar skrivs aspectfältet inte i denna funktion.

Detta binder de tidigare nollorna i upload-info till geometrin. Emittern
indexerar `0x80158050` som `9 * aspect + lod`. För LOD 0 visar tabellen:

```text
aspect 0 (8:1) -> 0x100 / 0x020 = 256x32
aspect 3 (1:1) -> 0x100 / 0x100 = 256x256
```

Drawens square-LOD och uploadens 256x32 är alltså exakt skillnaden mellan
aspect 3 och det observerade nollvärdet; packetloopen återger tabellvalet
korrekt. Det är ännu inte bevisat om nollan kommer från en avsiktlig 8:1-post
eller från konverterarens no-match-fallthrough. Bounds-tracen fångar därför nu
även `phase=aspect-builder` vid `0x800a675c`, filtrerad på samma source, och
skriver recordets packade dimensioner samt de fem producerade infoorden.
Detta är nästa dynamiska beslutspunkt; en aspect- eller samplerpatch innan den
träffen skulle blanda ihop producentfel och korrekt 8:1-metadata.

### Record 0 faller bevisligen genom aspect-konverteraren

En kanonisk f900--f1000+5,1M-körning via baseline-wrappern fångade recordet
precis vid selectoranropet på render-frame 984:

```text
record=802e2158
recordWords=00000000/40e560da/00000001/00000000/0000000d/000000c6/...
info=00000000/00000000/00000000/00000000/802e1719/00000000
```

Guestens två `lhu` vid `raw+0x04/+0x06` ser alltså `0x60da` och `0x40e5`.
De är varken lika eller ett exakt 2x-, 4x- eller 8x-förhållande. Funktionen
`0x800a66e8` når därför no-match-utgången utan att skriva `info+0x08`.
Det kvarvarande nollvärdet blir sedan Glide-aspect 0 (8:1) och väljer
`0x80158050: 0x100/0x20`. 256x32-geometrin är därmed inte deklarerad av en
giltig aspectpost; den är en följd av ogiltiga packade dimensioner och ett
oskrivet stackfält.

`phase=aspect-builder` flyttas till caller-returen `0x800a731c`, där både
recordpekaren och stack-info fortfarande är stabila. En ny strikt default-off
kausalitetsprobe kan dessutom ändra endast record-0-uploadens aspect efter
producenten:

```text
EUTHERDRIVE_GAUNTDL_EXPERIMENT_RUNTIME_WORLD_TEXTURE_UPLOAD_SQUARE_ASPECT=1
```

Den skriver `info+0x08: 0 -> 3` endast när source är `0x802e1719`. Proben
ändrar varken draw-LOD eller samplern. Nästa A/B avgör om en square-upload ger
den väntade 256x256-geometrin och om de 64 KiB som följer source faktiskt är
en sammanhängande textur; resultatet ska fortfarande behandlas som diagnostik,
inte som fix, tills ursprunget till `0x40e560da` är känt.

### Square-upload är kausal men läser inte en sammanhängande 256x256-asset

Den default-off-proben kördes från samma f900-state genom f1000+5,1M. Den
träffade varje selectoranrop och byggde `info=0/0/3/0/802e1719`, alltså den
avsedda 1:1-tabellposten. Jämfört med kanoniska endpointen divergerade både
geometri och exekvering tydligt:

```text
baseline frameHash=0x42925e78 fifoWords=10323854 packets=362333 swaps=1299
square   frameHash=0x3fc1f918 fifoWords=10489288 packets=370117 swaps=1265

baseline texture touched words=16384  last=0x00fffc
square   texture touched words=45184  last=0x415f04
```

Square-dumpen ersätter delar av de långa horisontella banden med tätt
upprepade diagonala och horisontella mönster, men visar fortfarande ingen
igenkännbar scen. De 64 KiB som emittern konsumerar från `0x802e1719` är
alltså inte en sammanhängande square-textur. Proben är en negativ kontroll och
ska förbli default-off.

En direkt kontroll mot `gauntd24.raw` vid `0x0fbb1270` visar dessutom samma
ord `00000000/40e560da/01000000/...`; `0x40e560da` introduceras inte av
QIO-kopian eller RAM-endianness. Nästa kausala gräns är därför varför guestens
materialpekare `0x802e2158` skickas till en helper som tolkar `+0x04/+0x06`
som aspectdimensioner, trots att diskposten inte innehåller ett giltigt
power-of-two-förhållande där. Följ callerns val av record/subrecord-offset före
`0x800a66e8`; fler syntetiska aspect- eller packetförlängningar saknar stöd.

Kod-dumpen av den omgivande funktionen visar dessutom `move s2,a0` vid
`0x800a7110`; ingen intern `+0x10/+0x30`-justering görs innan
aspect-buildern får `s2`. Om fel strukturdel används måste den därför komma
från callerns argumentval eller den tidigare record-lookupen, inte från en
tappad offset inne i selectorloopen.

### Callern väljer descriptorposten explicit med 0x50-byte stride

Bounds-tracen följer nu record 0 ett steg längre ut och fångar
`0x800a7110`, precis innan `move s2,a0`. Den kanoniska
f900--f1000+5,1M-körningen gav två callsites men behöll oraklet exakt:

```text
ra=0x800abe54 a0=0x802e2158 a1=0x802e1718 a2=0 a3=2
ra=0x800ab3b8 a0=0x802e2158 a1=0x802e1718 a2=0 a3=2
frameHash=0x42925e78 fifoWords=10323854 packets=362333 swaps=1299
```

Kod-dumparna visar att båda callsitesen anropar den verkliga entryn
`0x800a7094` och räknar `a0` med 0x50-byte stride. En andra observationsren
körning läste callerns sparade register från callee-stacken och stängde även
frågan om stride-basen:

```text
initial:  callerS1=80210000 callerS3=802e2158 callerS4=00000002 callerFp=80252da0
repeat:   callerS1=00000002 callerS3=188d2303 callerS4=00000000 callerFp=802e2158
```

Den upprepade vägen vid `0x800ab3b4` bygger alltså uttryckligen
`a0 = fp + s4 * 0x50 = 0x802e2158 + 0 * 0x50`. Initvägen bär samma
descriptorpekare direkt i `s3`. `a1=0x802e1718` är separat source/arena-data,
inte basen för descriptorindexeringen. Record 0 skickas därför inte till
aspect-buildern genom ett tappat internt subrecord-offset eller ett felaktigt
index; den är en medvetet vald descriptorpost i båda callerflödena.

En dump av de omgivande 0x50-byteposterna stänger också den enkla
off-by-one-kontrollen. Nästa post `0x802e21a8` är den redan kända sekundära
draw-descriptorn och innehåller inte heller en vanlig power-of-two
width/height-header vid `+0x04/+0x06`. Square-uploaden ska därför fortsätta
vara en negativ kontroll. Nästa dynamiska gräns är nu den separata
upload-till-draw-propagationen: bind den valda postens fallback-aspect noll till
den senare draw-state-byggaren och avgör varför `textureLod=0x20c6` saknar
aspectbitar trots att upload-tabellen väljer 256x32.

### Upload-aspect når aldrig draw-state-byggaren

En separat default-off probe följer nu övergången vid `0x800bd180`, efter
att guestkoden har byggt både texture mode och texture LOD:

```text
EUTHERDRIVE_GAUNTDL_TRACE_RUNTIME_WORLD_TEXTURE_UPLOAD_TO_DRAW=1
```

En kall baseline till f150 träffade den primära posten 16 gånger vid f133 med
identiska värden:

```text
record=0x802e2158 raw+0x0c=0x1c00 raw+0x14=0x000000c6 raw+0x1c=0x0000e000
owner=0x80213618 owner+0x144=0x00002000 globalMode=0x00000006
inputMode=0x8c241009 inputBaseShift=0
builtMode=0x8c24100f builtLod=0x000020c6 stateOwner=0x80262d64
```

Detta binder den tidigare statiska formeln dynamiskt: draw-buildern gör
`(raw+0x14 & 0xfffc0fff) | owner+0x144` och producerar själv `0x20c6`.
Upload-helperns fallback-aspect noll finns inte bland dess inputs. Callern vid
`0x800bd750` skickar endast materialposten, global mode-state och
`raw+0x1c & 1`; det finns ingen separat aspectparameter som vår implementation
kan ha tappat.

Den nya återanvändbara observationsstaten är
`/tmp/eutherdrive-gauntlet-probe/gauntdl-upload-to-draw-f150-20260718.warm`
och cold-runnen slutade på `frameHash=0xf29eb67c`. Den fulla kanoniska
f900--f1000+5,1M-kedjan verifierades separat och är fortfarande exakt
`frameHash=0x42925e78`, `fifoWords=10323854`, `packets=362333`, `swaps=1299`.

Nästa kausala gräns flyttas därför före båda konsumenterna: följ producenten
av materialpostens `raw+0x04/+0x06 = 0x40e5/0x60da`. Upload-helpern tolkar
halvorden som dimensioner men de matchar inget giltigt power-of-two-aspect,
medan draw-buildern helt förlitar sig på det separata råfältet `raw+0x14`.
Avgör om QIO-/parserflödet ska konvertera dimensionsmetadata innan posten
publiceras; ändra inte Voodoo-samplern eller tvinga square-aspect innan den
producentgränsen är stängd.

### Record-selector bekräftar ett producentkontraktsfel

En byte-dump av den orörda f150-staten visar att `0x40e560da` inte är en
isolerad felpost. Från `0x802e2158` syns en återkommande form med 0x90 bytes
mellan de floatlika orden:

```text
0x802e2158: 08000000 40e560da 00000001 ...
0x802e21e8: 40dbc0e0 00000001 00000000 ...
0x802e2278: 40d99c6f 00000001 00000000 ...
0x802e2308: 40e12b7c 00000001 00000000 ...
0x802e2398: 40eab70f 00000001 00000000 ...
```

Orden följs av count ett och flera 0x20-byteformer med de återkommande fälten
`0x0d`, `0xc6`, tre offsets, storlek och `0x0b`. Den tidigare neighbor-
tolkningen var därför för stark: `0x802e21a8` ligger 0x50 byte in i detta
synliga mönster, men den statiska 0x90-recurrensen ensam bevisar inte ett
containerstride.

Den nya default-off-tracen
`EUTHERDRIVE_GAUNTDL_TRACE_RUNTIME_WORLD_TEXTURE_RECORD_SELECTION=1` stänger
den dynamiska delen. Guestkoden använder uttryckligen `fp=0x802e2158`,
`count=2`, anropar storlekshjälparen `0x800a64a0` på exakt två poster med
0x50 stride och skickar därefter vald post till upload-helpern:

```text
record 0 @ 0x802e2158 -> size 0x188d2302
record 1 @ 0x802e21a8 -> size 0x00000000
selected index 0      -> upload a0=0x802e2158
```

Hjälparen är den redan dekodade mip-storleksfunktionen. För record 0 läser
den `width=0x40e5`, `height=0x60da`, en mipnivå och beräknar bokstavligen
`0x40e5 * 0x60da = 0x188d2302`. Record 1 har en nolldimension och ger noll.
Callern initierar sin maxkandidat från record 0 utan en cap-kontroll och byter
bara kandidat om en senare giltig post har större end-offset. Den enorma
förstaposten vinner därför deterministiskt över record 1; detta är inte ett
fel i vår descriptorindexering eller aspectfallback.

`fp` är också guest-byggd, inte en adaptergissning: `0x800ab2b0..0x800ab2d0`
beräknar den från outer-objektets `+0x68 + 0x8c * field60`, medan `field64=2`
anger antalet 0x50-poster. Samma råbytes finns på disk vid
`static_lr/textures.rom + 0xa40`, så QIO-kopian och endianhanteringen har inte
skapat dem. Den återstående smala gränsen är parser-/callbackpubliceringen
före detta outer-objekt: avgör var råa containerformer ska materialiseras som
runtimeposter med giltiga dimensioner innan `+0x68`, `field60` och `field64`
blir synliga. Ändra inte selector-algoritmen, Voodoo-samplern, stride eller
aspect syntetiskt innan den producentgränsen är stängd.

Den verifierade observationsloggen är
`/tmp/eutherdrive-gauntlet-probe/record-selection-release-f100-f150-20260718.log`.
Runnen förblev exakt `frameHash=0xf29eb67c`; tracen är alltså observationsren.

### Selector-outer är den återanvända QIO-scratchen

Outer-fasen i samma trace stänger den återstående pekarberäkningen. Vid varje
observerat selector-anrop är outer inte ett separat publicerat runtimeobjekt
utan slot-0-scratchens råa QIO-destination:

```text
outer=0x802e1718
outer+0x60=0x00000012
outer+0x64=0x00000002
table=outer+0x68+0x12*0x8c=0x802e2158
```

De första outer-orden är samtidigt den kända texture-payloadformen
`12/2/0/a/2b/.../f798`, inte en header byggd av adaptern. Slot-0-repairen har
precis före detta kopierat den requestägda 0x2000-byteblocket från
`static_lr/textures.rom` till samma `0x802e1718`. En CPU-write-watch över
`0x802e1718..0x802e1780` ser därefter endast guestens bevarade store av värdet
två till `outer+0x44` vid `0x800aad0c`; ingen parser konverterar `+0x60/+0x64`
eller recordtabellen före selectorn.

Adressen har dessutom bevisat mer än en livstid. Den sparade kalla f150-staten
`gauntdl-upload-to-draw-f150-20260718.warm` innehåller på samma adress en
runtime-lik `1/4`-header med interna pekare och `1.0`-floats, medan den
requestdrivna f100--f150-kedjan visar den råa texture-chunken under
selector-anropen. De tidigare set 9/10-adresserna är inte ett giltigt
format-orakel; den aktuella snapshoten innehåller MIPS-kod där.

Den smalaste kvarvarande gränsen är därför scratch-/source-tabellens livstid,
inte width/height-halvorden isolerat. Följ varför `0x802e1718` fortsätter vara
descriptor-owner när QIO-refillen har ersatt dess tidigare innehåll med en rå
texture-chunk. En fix måste behålla den guestägda descriptor/header-instansen
eller publicera requestens verkliga parsed output; den får inte klona hela
0x2000-byte-scratchen, gissa ett inner-offset eller skriva om selectorn.

Observationsloggen för tidsordningen är
`/tmp/eutherdrive-gauntlet-probe/outer-write-order-f100-f150-20260718.log` och
behåller åter `frameHash=0xf29eb67c`.

### Slot 0 pekar på static_lr-objektet, inte på texture-payloaden

En write-watch på source-tabellens slot 0 (`0x802529a0`) stänger vem som
publicerar descriptor-ownern. Guestinstruktionen `sw s1,0(v0)` vid
`0x800ac208` skriver `s1=0x802e1718` till tabellen innan QIO-hydreringen.
Senare byter assetposten till `0x802f0e70`, men source-tabellen behåller
scratchadressen. Koden runt `0x800ac160` allokerar och publicerar alltså ett
objekt på denna adress; den förväntar sig inte att en rå texture-chunk ska
ersätta objektet där.

Raw-disken innehåller den matchande `static_lr/objects.rom`-extentheadern vid
`0x0fb2de00`. Samma bevisade FSYS-headerlayout som för `gei` anger:

```text
payload disk base = 0x0fb2e000
payload bytes     = 0x00067b4c
payload LBA       = 0x0007d970
object signature  = source+0x40 = 0xf00b0001
table index/count = 0x149 / 0x2e
record table      = source+0xb454, 0xe60 bytes
```

Recordtabellen ligger helt inom den ägda objektextenten och dess 46 poster
har rimliga 0x50-byteformer. Storlekshjälparen returnerar exempelvis `0x50`,
`0x150`, `0x28`, `0x550` och `0xaa0`, i stället för den falska
`0x188d2302`-arean från texture-bytes. Nästa FSYS-grupp visar också den exakta
companiongränsen: extentheadern vid `0x0fbb0400` äger en `0x0c3674` byte
texture-payload från `0x0fbb0600`. Den tidigare källan `0x0fbb0830` är
alltså `textures.rom + 0x230`, inte ett objekt med descriptorposter.

Två default-off A/B-försök bekräftar kausaliteten. Att bara rikta slot 0:s
0x2000-byte-QIO till `0x0fb2e000` ger rätt objekthuvud men lämnar tabellen vid
`+0xb454` tom. Hashen stannar på `0xf29eb67c`, medan gästen skannar 46
nollposter och FIFO-aktiviteten exploderar. Experimentet
`EUTHERDRIVE_GAUNTDL_EXPERIMENT_RUNTIME_BGLOADMODEL_STATIC_OBJECT_OWNER=1`
hydrerar därför diagnostiskt endast huvudet och den headerhärledda tabellen.
F100--f150 ger då:

```text
                        baseline     object + record table
frameHash               0xf29eb67c   0xad79a01f
fifoWords                29,805       68,038
fifoPackets              1,395        9,192
texture writes           3            1,841
swaps                    206          24
```

Den ändrade hashen, de giltiga mipstorlekarna och den stora ökningen av
texture-/FIFO-arbete bevisar object/texture-separationen även för den
statiska slot-0-källan. Men tabellkopian ligger utanför den aktuella
0x2000-byte-requesten och swapparna stannar nästan; experimentet är därför
en diagnostisk sond och får inte bli standardfix.

Nästa gräns är requestägd streaming av resten av object-body samt den
separata texture-companionen. Spåra vilka QIO-requests som materialiserar
`objects.rom + 0x2000..0x67b4b` och vilka recordoffsetar som därefter läser
`textures.rom + 0x230...`; utöka inte den första requesten syntetiskt och
promota inte table-only-sonden.

Verifieringsloggar:

- `/tmp/eutherdrive-gauntlet-probe/source-owner-slot0-f100-f150-20260718.log`
- `/tmp/eutherdrive-gauntlet-probe/source-owner-publisher-code-20260718.log`
- `/tmp/eutherdrive-gauntlet-probe/slot0-static-objects-f100-f150-20260718.log`
- `/tmp/eutherdrive-gauntlet-probe/slot0-static-object-table-f100-f150-20260718.log`

### Recordspanet identifierar slot 0:s exakta companion-extent

Recordtabellens offsetar stänger vilket av de efterföljande FSYS-blocken som
är texture-companionen. Den sista posten bör `offset=0x11b3c` och ger
mipstorlek `0x2a8`; dess slut är alltså exakt `0x11de4`. Extentheadern direkt
efter `objects.rom` ligger vid `0x0fb95c00` och deklarerar exakt samma
payloadstorlek, med data från `0x0fb95e00`:

```text
last record offset + size = 0x11b3c + 0x2a8 = 0x11de4
companion payload bytes   = 0x11de4
companion disk base       = 0x0fb95e00
```

Companionen börjar med `f0`-padding och därefter packade texture-liknande
bytes. Den senare extenten från `0x0fbb0600`, som den gamla slot-0-repairen
träffade vid `+0x230`, är därmed definitivt inte companionen till just denna
46-posters objekttabell.

En separat default-off sond,
`EUTHERDRIVE_GAUNTDL_EXPERIMENT_RUNTIME_BGLOADMODEL_STATIC_OBJECT_COMPANION_OWNER=1`,
hydrerar den exakta companionen till den första adressen efter objektpayloaden:

```text
object RAM base       = 0x802e1718
object payload bytes  = 0x00067b4c
companion RAM base    = 0x80349264
```

Vid entryn `0x800a7094` ersätter sonden endast `a1` för callsites
`ra=0x800abe54/0x800ab3b8`, medan `a0` fortfarande pekar på den giltiga
objekttabellen. Source-offset-tracen visar sedan att gästen själv räknar
`0x80349264 + recordOffset`; exempelvis `0`, `0x50`, `0x1a0` och `0x11b3c`.
Det är den saknade object/companion-bindningen, inte en adaptergissad stride.

F100--f150-resultatet är kausalt men fortfarande diagnostiskt:

```text
                                  table only    table + companion
frameHash                         0xad79a01f    0xf29eb67c
fifoWords                         68,038        68,038
fifoPackets                       9,192         3,042
Type5 packets                     0             185
texture writes                    1,841         1,093
texture-map writes/touched words  -             4,368 / 41
swaps                             24            18
```

Att hashen råkar återgå till den vita baselinebildens `0xf29eb67c` är inte
en visuell fix: CPU:n står nu i upload-loopen vid `0x800fe7d0`, Type5-trafik
har uppstått och swapparna är fortfarande nästan stoppade. Resultatet bevisar
däremot både companionens diskextent och dess roll som `a1`-källa.

Nästa produktionsgräns är guestens uteblivna arenareservation. Den riktiga
vägen ska låta `objects.rom`-parsen reservera `0x67b4c`, behålla/publicera
recordtabellen och därefter låta QIO fylla `0x11de4` companion-bytes vid den
nya arena-cursorn. Promota inte callee-remappen; spåra i stället
allokeringsstorleken som idag blir noll före `0x800c9088`.

Verifieringslogg:

- `/tmp/eutherdrive-gauntlet-probe/slot0-static-object-companion-f100-f150-20260718.log`

### `objects.rom`-storleken stänger arenareservationens nollkedja

Den fokuserade entrytracen visar att `0x800c8f70` är den generella
arenaallokatorn. Den tar emot storleken i `a0`, sparar den i sitt frame och
adderar den till arena-cursorn; den producerar alltså inte nollan själv.
Slot-0-anropet kommer från `ra=0x800aae84`, där `a0` väljs från returvärdet
hos filstorleksvägen `0x800c8a5c -> 0x800c8828`.

En default-off result-struct-trace vid `0x800c893c` stänger kedjan:

```text
file                  objects.rom
directory pointer     0x80166370 -> ""
fifth/source argument 0x802e1718
result words          alla noll
returned bytes        0
AllocMem a0           0
```

Den tidigare `ra=0x800c8fa4` var endast den interna diagnostikutskriften
`AllocMem() called while mem reserved`; den riktiga allokeringscallern är
`0x800aae84`.

En separat default-off size-owner,
`EUTHERDRIVE_GAUNTDL_EXPERIMENT_RUNTIME_BGLOADMODEL_STATIC_OBJECT_SIZE_OWNER=1`,
matchar endast `objects.rom`, femte argumentet `0x802e1718` och
objektsignaturen `f00b0001`. Den returnerar den diskbevisade payloadstorleken
`0x67b4c` från `0x800c8828`. Guestens egen allocator tar därefter emot exakt
`a0=0x67b4c`; basen är fortfarande `0x802e1718`, så nästa arenaadress blir
det förväntade `0x80349264`.

F100--f150 A/B med table-owner visar att reservationen är kausal men ännu
inte en bildfix:

```text
                                  table only    table + size owner
frameHash                         0xad79a01f    0xad79a01f
fifoPackets                       9,192         24,491
Type3 packets                     0             1
Type5 packets                     315           874
texture writes                    1,841         5,167
CPU endpoint                      0x800fe7d0    0x800fe31c
```

Med companion-owner samtidigt återkommer companionprofilen
`frameHash=0xf29eb67c`, `fifoPackets=3,042`, `Type5=185`,
`texture writes=1,093` och `swaps=18`, men CPU-endpointen är fortfarande den
nya `0x800fe31c`. Size-owner ska därför förbli diagnostisk. Nästa gräns är
varför den riktiga filstorleksvägen vid `0x800c8828` lämnar ett helt tomt
resultat trots korrekt `objects.rom`-namn och source-argument; promota varken
size-owner eller companion-remappen innan den vägen ägs av guest/QIO.

Verifieringsloggar:

- `/tmp/eutherdrive-gauntlet-probe/static-object-file-size-result-f100-f150-20260718.log`
- `/tmp/eutherdrive-gauntlet-probe/static-object-size-owner-f100-f150-20260718.log`
- `/tmp/eutherdrive-gauntlet-probe/static-object-size-owner-alloc-f100-f150-20260718.log`
- `/tmp/eutherdrive-gauntlet-probe/static-object-size-companion-owner-f100-f150-20260718.log`

### `objects.rom`-sökvägen når QIO men saknar open-completion

En default-off callsite-trace runt `0x800c8828` visar att formatteringsanropet
vid `0x800c886c` har rätt destination, format och synliga argument:

```text
destination  0x80218530
format       /d0/%s/%s
directory    0x80166370 -> ""
filename     objects.rom
```

Efter retur från `0x8011f3c0` är destinationen fortfarande tom. CPU-tracen
visar samtidigt att wrapperns varargs inte längre motsvarar callsite-registren,
och den diagnostiska format-buffer-fastpathen returnerar noll samt NUL-terminerar
destinationen. Felet uppstår alltså före FSYS-uppslaget.

Den smala default-off path-ownern
`EUTHERDRIVE_GAUNTDL_EXPERIMENT_RUNTIME_BGLOADMODEL_STATIC_OBJECT_PATH_OWNER=1`
matchar endast detta `objects.rom`-anrop, femte argumentet `0x802e1718` och den
tomma katalogpekaren. Den skriver den rå-FSYS-bevisade sökvägen
`/d0/static_lr/objects.rom` och fortsätter i guestens ordinarie kod.

Det flyttar gränsen framåt men ger ännu ingen filstorlek:

```text
0x800c88ec  open-call    object=0x80295750 path=/d0/static_lr/objects.rom
0x800c88fc  open-return  v0=0
0x800c890c  status-wait  object+0x14=0
```

Efter ytterligare en miljon instruktioner är tråden fortfarande i
`0x800c86b4..0x800c8728`-pollningen och `0x800c893c` har inte nåtts. Samma
resultat fås med rotvarianten `/d0/objects.rom`; den giltiga katalogen ensam
skapar alltså inte completion. QIO-objektet har den väntade runtime-signaturen
(`+0x00=0x8021e88c`, `+0x20=0x80218518`, `+0x38=0x800f087c`,
`+0x3c=0x80295750`) men status `+0x14` förblir noll.

Nästa gräns är därför open/callback-ägarskapet bakom `0x800ec748`, inte fler
sökvägsgissningar och inte filstorleksresultatet. Spåra vilken request/callback
som skapas för `0x80295750` och varför den befintliga QIO-completion-bridgen
bara avslutar mount/modellrequests men inte denna filöppning. Path-ownern ska
förbli default-off.

Verifieringsloggar:

- `/tmp/eutherdrive-gauntlet-probe/static-object-file-path-f100-f150-20260718.log`
- `/tmp/eutherdrive-gauntlet-probe/static-object-path-stages-f100-f150-20260718.log`
- `/tmp/eutherdrive-gauntlet-probe/static-object-static-lr-path-f100-f150-20260718.log`
- `/tmp/eutherdrive-gauntlet-probe/static-object-path-owner-final-f100-f150-20260718.log`
- `/tmp/eutherdrive-gauntlet-probe/static-object-open-qio-bytes-f100-f150-20260718.log`
- `/tmp/eutherdrive-gauntlet-probe/default-off-after-static-object-path-owner-f100-f150-20260718.log`

### Open-workern är enqueuad men dispatchas aldrig

En andra entry-trace stänger ytterligare en klobbergräns. Path-ownern har
fortfarande `/d0/static_lr/objects.rom` vid `0x800c88ec`, men vid första
instruktionen i callee `0x800ec748` är `a1=0x80218530` åter tom. Det är alltså
inte resolveraren som tömmer sökvägen; bufferten klobbras i övergången mellan
`jal` och callee, precis som formatterarens varargs tidigare gjorde.

Samma default-off path-owner har därför en andra exakt guard vid
`0x800ec748`, matchad på `ra=0x800c88f8`, QIO-objekt `0x80295750`, den tomma
fasta path-bufferten och open-prologens kodsignatur. När sökvägen återställs
där accepterar guestens riktiga resolver filen:

```text
resolved node       0x80154c68
fallback used       no
request id          6
request record      0x8021dd78
open callback       0x800f0af8
```

Callbacken får korrekt relativ sökväg `a1=0x80218533`, alltså
`/static_lr/objects.rom`. Den hittar request-id 6, allokerar deskriptorn
`0x802ac5a0`, länkar worker `0x800f087c` och enqueue-anropet vid
`0x800f0c14` returnerar `v0=0` (success). Därefter händer inget:

- objektstatus `0x80295750+0x14` förblir noll;
- `0x800c893c` nås inte;
- en exakt PC-sond ser inga träffar på worker `0x800f087c`, inte heller efter
  ytterligare en miljon instruktioner.

Den nya exakta gränsen är därmed scheduler-dispatchen för kön som matas av
`0x800ed4ac`. Nästa sond ska följa dess queue-node/list-head och jämföra med
den fungerande mount-completionen. Promota inte en syntetisk filstorlek eller
direktanropa workern innan dess ABI och köägarskap är verifierade.

Verifieringsloggar:

- `/tmp/eutherdrive-gauntlet-probe/static-object-open-dispatch-args-f100-f150-20260718.log`
- `/tmp/eutherdrive-gauntlet-probe/static-object-open-entry-owner-f100-f150-20260718.log`
- `/tmp/eutherdrive-gauntlet-probe/static-object-open-callback-entry-f100-f150-20260718.log`
- `/tmp/eutherdrive-gauntlet-probe/static-object-open-callback-request-f100-f150-20260718.log`
- `/tmp/eutherdrive-gauntlet-probe/static-object-open-callback-alloc-f100-f150-20260718.log`
- `/tmp/eutherdrive-gauntlet-probe/static-object-open-callback-enqueue-return-f100-f150-20260718.log`
- `/tmp/eutherdrive-gauntlet-probe/static-object-open-worker-hit-f100-f150-20260718.log`
- `/tmp/eutherdrive-gauntlet-probe/default-off-after-static-object-open-worker-trace-f100-f150-20260718.log`

### Runtime-interrupt bridge öppnar schedulergränsen

Queue-watch på `0x800ed4ac` verifierar att enqueue-resultatet är strukturellt
korrekt. Queue-head `0x8021e97c` går från noll till `0x80295780`, alltså den
intrusiva noden vid QIO-objektets `+0x30`. Noden innehåller därefter:

```text
object              0x80295750
queue node          0x80295780
node + 0x04         0x80262ae0
node + 0x08 worker  0x800f087c
node + 0x0c context 0x80295750
```

Anropet från enqueue till `0x800de4fc` gör den riktiga runtime-signaleringen
för tråd 0 och returnerar success. Under den gamla baseline-konfigurationen
lästes queue-head aldrig igen efter skrivningen; `WaitForQIO` pollade i stället
objektstatus `+0x14` tills timeout. Orsaken var inte en trasig nod eller ABI utan
att `EUTHERDRIVE_GAUNTDL_FIX_RUNTIME_INTERRUPT_SUPPRESS`, indirekt aktiverad av
`BRINGUP_FAST`, stoppade samtliga runtime-interrupts innan guestens handler och
context switch kunde köras.

Den isolerade kombinationen

```text
EUTHERDRIVE_GAUNTDL_FIX_RUNTIME_INTERRUPT_BRIDGE=1
EUTHERDRIVE_GAUNTDL_FIX_RUNTIME_INTERRUPT_SUPPRESS=0
```

låter device-interrupts gå genom guestens riktiga exception/restore-väg medan
bridge-koden fortfarande filtrerar den ofärdiga timer-sidan. Utan static-path-
experimentet går samma warm snapshot vid frame 132 nu från `Initializing Disk`
till `Loading Game`, väljer `castle`, allokerar åtta model-records och når
`pc=0x800ebf40`. Med path-ownern aktiv når den dessutom 1 825 texture writes,
707 touched texture words, 12 swaps och 61 färgade framebuffer-pixlar
(`frameHash=0xad79a01f`) i stället för timeoutens tre texture writes och noll
färgade pixlar.

Interrupt-kombinationen är därför promoterad till både adapter-baseline och
probe-runnern. Static-object path-ownern är fortfarande default-off: den nya
interruptordningen flyttar dess första open senare och nästa sond ska följa den
från `Loading Game` samt reda ut diagnostiken `GetMemBase()/AllocMem called
while mem reserved` innan path-reparationen promoteras.

Verifieringsloggar:

- `/tmp/eutherdrive-gauntlet-probe/static-object-worker-queue-watch-f100-f150-20260718.log`
- `/tmp/eutherdrive-gauntlet-probe/static-object-worker-node-watch-f100-f132-20260718.log`
- `/tmp/eutherdrive-gauntlet-probe/static-object-worker-kernel-enqueue-code-20260718.log`
- `/tmp/eutherdrive-gauntlet-probe/static-object-worker-runtime-interrupt-bridge-f100-f132-20260718.log`
- `/tmp/eutherdrive-gauntlet-probe/static-object-worker-runtime-interrupt-bridge-f100-f150-20260718.log`
- `/tmp/eutherdrive-gauntlet-probe/runtime-interrupt-bridge-no-static-path-owner-f100-f132-20260718.log`

### Lång interrupt-baseline och static-object A/B

Den promoterade interrupt-konfigurationen har nu körts vidare från frame 100
till frame 132 plus tio miljoner instruktioner utan static-object-experiment.
Guestkoden fortsätter genom `Loading Game`, genererar 5 730 Type3-paket och
764 texturerade trianglar samt skriver 15 747 texturord. Den exporterade bilden
har 5 979 färgade pixlar, men de ligger fortfarande huvudsakligen som ett tunt
brusigt band längs bildens ovankant:

```text
frameHash             0xd083385f
Type3 packets         5730
texture writes        15747
textured triangles    764
framebuffer colored   5979
```

Frontbuffer 1 är nästan helt vit med två smala duplicerade band, buffer 0
innehåller det mindre felaktiga toppbandet och buffer 2 är svart.
`ChooseRenderBufferIndex()` väljer därför avsiktligt buffer 0; detta är inte
en enkel fel-buffer-regression. Efter ytterligare fem miljoner instruktioner
genererar guesten credit-strängar men frame-hashen och buffertinnehållet är
oförändrade. Den upprepade Type3-signaturen `0x0180a8cb` med de råa
koordinaterna `x=49076`/`y=-16614` är den redan dokumenterade loading-
fullrecten, inte en ny packet-decoder-regression.

Path-ownern ovanpå samma interrupt-baseline är en tydlig negativ A/B. Vid
+10M ligger den kvar i `WaitForQIO`, har noll Type3-paket och endast 61 färgade
pixlar:

```text
variant                    Type3  texture writes  colored pixels  hash
promoted baseline +10M       5730           15747            5979  d083385f
static path owner +10M           0            1825              61  ad79a01f
```

En PC-sond runt `0x800f087c` och en queue-head-sond på `0x8021e97c` visar
fortfarande ingen worker-dispatch eller senare queue-head-läsning. Device-IRQ
når alltså guestens handler, men just async file-open-kön saknar fortfarande
en konsument. Path-ownern ska förbli default-off.

Den separata, exakt guardade size-ownern
`EUTHERDRIVE_GAUNTDL_EXPERIMENT_RUNTIME_BGLOADMODEL_STATIC_OBJECT_SIZE_OWNER=1`
isolerar denna schedulergräns genom att returnera den verifierade storleken
`0x67b4c` för den redan hydratiserade `objects.rom`-källan. Den går vidare till
`castle`, allokerar åtta model-records och producerar vid +5M 976 Type3-paket
och 5 177 texture writes. Den är ändå inte redo att promoteras: samtliga
35 904 rasteriserade texturpixlar samplar noll, framebuffer-hashen stannar på
`0xad79a01f`, och guesten rapporterar fortfarande `GetMemBase()/AllocMem called
while mem reserved`. Nästa gräns är därför den saknade body/companion-källan
efter statiska objekttabellen, inte fler display-buffer- eller Type3-ändringar.

Återanvändbara snapshots och bilder:

- `/tmp/eutherdrive-gauntlet-probe/gauntdl-interrupt-bridge-render-f132-plus10m-20260718.warm`
- `/tmp/eutherdrive-gauntlet-probe/gauntdl-interrupt-bridge-render-f132-plus15m-20260718.warm`
- `/tmp/eutherdrive-gauntlet-probe/gauntdl-interrupt-bridge-render-f132-plus10m-20260718.png`
- `/tmp/eutherdrive-gauntlet-probe/gauntdl-interrupt-static-owner-f132-plus10m-20260718.warm`
- `/tmp/eutherdrive-gauntlet-probe/gauntdl-interrupt-static-owner-f132-plus10m-20260718.ppm`
- `/tmp/eutherdrive-gauntlet-probe/gauntdl-interrupt-static-size-owner-f132-plus5m-20260718.warm`
- `/tmp/eutherdrive-gauntlet-probe/gauntdl-interrupt-static-size-owner-f132-plus5m-20260718.ppm`

Verifieringsloggar:

- `/tmp/eutherdrive-gauntlet-probe/promoted-runtime-interrupt-long-f100-f132-20260718.log`
- `/tmp/eutherdrive-gauntlet-probe/promoted-runtime-interrupt-render-snapshot-f100-f132-20260718.log`
- `/tmp/eutherdrive-gauntlet-probe/promoted-runtime-interrupt-render-plus5m-20260718.log`
- `/tmp/eutherdrive-gauntlet-probe/type3-fields-correct-from-render-f132-plus15m-20260718.log`
- `/tmp/eutherdrive-gauntlet-probe/gauntdl-interrupt-static-owner-f100-f132-plus10m-20260718.log`

### Static-object companion-offseten är förkastad

Den befintliga default-off companion-ownern har körts ovanpå static-object-
och size-owner-kombinationen. Den hydratiserar `0x11de4` byte från diskoffset
`0x0fb95e00` till `0x80349264` och flyttar record `0x802ecb6c` från
`0x802e1718` till den nya källan. Källans första ord är upprepade
`0xf0f0f0f0`, och A/B-resultatet visar att detta inte är rätt companion-body:

```text
variant                    Type3  texture writes  sampled pixels  colored pixels  hash
static + size +5M             976            5177           35904              61  ad79a01f
static + size + companion    2160            1093           78336               0  f29eb67c
```

Samtliga 78 336 rasteriserade texturpixlar samplar noll. Den exporterade
bilden är svart bortsett från den gamla vita hörntriangeln. Companion-ownern
ska därför förbli default-off och offseten `0x0fb95e00` ska inte användas som
modell-body utan ny proveniens från guestens index/descriptor. Nästa sond ska
följa skrivaren av record `0x802ecb6c` och dess source-fält, eller härleda
body-offseten från den hydratiserade objekttabellen i stället för att gissa en
angränsande diskregion.

Regressionsartefakter:

- `/tmp/eutherdrive-gauntlet-probe/gauntdl-interrupt-static-size-companion-f132-plus5m-20260718.warm`
- `/tmp/eutherdrive-gauntlet-probe/gauntdl-interrupt-static-size-companion-f132-plus5m-20260718.ppm`
- `/tmp/eutherdrive-gauntlet-probe/gauntdl-interrupt-static-size-companion-f132-plus5m-20260718.png`

En efterföljande source-proveniens visar en fyrabytes headergräns som den
första companion-sonden missade. Efter att alla 46 poster skannats anropar
guesten `0x800a7094` med:

```text
arena cursor          0x80349264
guest body source     0x80349268
record payload span   0x00011de4
```

Det default-off experimentet hydrerar och binder nu companionen vid
`object base + 0x67b4c + 4`, exakt samma `0x80349268` som guesten själv
producerar. En explicit rebuild och ny +5M A/B verifierar att korrigeringen är
aktiv, men slutresultatet är fortfarande pixelneutralt: 2 160 Type3-paket,
1 093 texture writes, 78 336 nollsamplade pixlar, noll färgade pixlar och
`frameHash=0xf29eb67c`. Den tidigare RAM-adressen var alltså fyra byte fel,
men det är inte den kvarvarande bildgränsen. Companion-ownern ska fortfarande
vara default-off medan payloadformat/upload-proveniens spåras vidare.

Korrigerad regressionssnapshot:

- `/tmp/eutherdrive-gauntlet-probe/gauntdl-interrupt-static-size-companion-plus4-rebuilt-f132-plus5m-20260718.warm`
- `/tmp/eutherdrive-gauntlet-probe/gauntdl-interrupt-static-size-companion-plus4-rebuilt-f132-plus5m-20260718.ppm`

### Companion-uploaden når Voodoo men äger inte den samplade ytan

En payload-/upload-proveniens från den korrigerade companion-adressen visar
att varierande, icke-noll payloadord når guestens Type5-loop. Källan ligger i
den hydrerade companion-arenan kring `0x8034d2b0`, och Type5-paketen behåller
både target och payload fram till `WriteTexturePort32()`. Den kvarvarande
nollbilden orsakas alltså inte av tom companiondata eller en byte-order-klobber
före Voodoo.

Två rena upload-address-A/B har därefter körts på samma frame 132 +5M-gräns:

```text
variant             Type3  Type5  tex writes  touched words  sampled zero  hash
companion baseline    2160    185        1093             41         78336  f29eb67c
SEQ8 download off     2160    185        1093             26         78336  f29eb67c
MAME write pointer    2160    185        1093            257         78336  f29eb67c
```

Att stänga av den av `BRINGUP_FAST` aktiverade sekventiella 8-bitarsmodellen
minskar det fysiska touched-spannet och är därmed en regression. MAME-
write-pointermodellen sprider däremot samma 4 368 map-writes från 41 till 257
unika ord, men är pixelneutral och ska ännu inte promoteras.

En efterföljande sample/writer-trace förklarar neutraliteten. Uploadpaketen
har format 6, LOD 3 och storlek 256x32, med de första MAME-adresserna kring
`0x000890`. De 2 160 texturerade dragen är i stället den redan kända loading-
fullrecten `0x0180a8cb`; den samplar format 15, LOD 0, storlek 256x256 och
adresser kring `0x007c17..0x00fc17`. Samtliga saknar writer. Upload och draw
beskriver alltså två olika ytor; en sampler-rebase vore bara en visuell gissning.

Nästa gräns ligger ovanför Voodoo: körningen rapporterar åtta
`render-record-null-body`-poster och de återkommande runtime-strängkopiorna
har längd noll. Spåra varför scene-/render-recordens body eller namn aldrig
publiceras efter model-uploaden och varför loading-fullrecten fortsätter vara
enda Type3-familjen. Behåll både companion-ownern och MAME-pekaren default-off.

Verifieringsartefakter:

- `/tmp/eutherdrive-gauntlet-probe/static-companion-plus4-upload-provenance-f100-f132-plus1m-20260718.log`
- `/tmp/eutherdrive-gauntlet-probe/gauntdl-companion-seq8off-f132-plus5m-20260718.warm`
- `/tmp/eutherdrive-gauntlet-probe/companion-mameptr-f100-f132-plus5m-20260718.log`
- `/tmp/eutherdrive-gauntlet-probe/gauntdl-companion-mameptr-f132-plus5m-20260718.warm`
- `/tmp/eutherdrive-gauntlet-probe/companion-mameptr-sampletrace-plus1m-20260718.log`

### Riktiga frames stänger loading-/scene-publiceringsgränsen

Companion-kombinationen har nu körts från frame 100 till 300 med riktiga
frames i stället för en fryst frame 132 och extra CPU-instruktioner. Den
fortsätter att exekvera och ökar Type3-trafiken, men lämnar aldrig loading-
familjen:

```text
frame                     132 +5M       300
Type3 packets                 2160      5429
texture writes                1093      1093
sampled pixels               78336    196928
zero samples                 78336    196928
swaps                          196       196
frameHash                 f29eb67c  f29eb67c
```

Detta avfärdar att den tidigare extra-instruction-metoden ensam höll loaden
vid liv. Den guardade S-from-X-fixen är också neutral ovanpå f300: ytterligare
22 848 samples förblir noll och inga nya swaps sker.

Den utökade texture-set-tracen visar samtidigt att publiceringen fram till
lookup är korrekt. Set-tabellen `0x802545a0` innehåller bas `0x802ecb6c`, och
både `0x800b0800`- och storlekshjälparens retur får samma giltiga record:

```text
record words  0d010605/00080008/00000000/fffff91d/
              000001cf/0003d614/00000000/00011de8/
              33221100/77665544/bbaa9988/ffeeddcc
```

TMU-stateproducenten efter f300 är däremot uteslutande den återkommande
loading-/blit-vägen `0x80106a74/0x80106448`. Den växlar mode
`0x0000100f/0x0c24100f`, behåller LOD `0xff802000` och base noll. Model-
uploadens producent `0x800fe5d4` återkommer inte, och inga andra texture-set-
index än set 0 / record 0 observeras.

En record-list-dump vid `0x800b11d4` visar att listan är levande och länkad;
de första posterna är aktiva och delar body `0x80349498`. Samma bodyadress
finns redan i size-owner-staten utan companion, så companion-remappen har inte
kollapsat separata bodypekare. Den fyller en adress som guesten redan hade
publicerat.

Nästa gräns är därför world-loadens completion/publicering före scene-
materialkonsumenten. Spåra vilket status/resultat som ska göra att renderaren
slutar slå upp endast set 0 / record 0 och börjar publicera castle-material;
ändra inte sampleradress, bodypekare eller frame-pumpning syntetiskt.

Den default-off diagnostiken har utökats så att
`EUTHERDRIVE_GAUNTDL_TRACE_RUNTIME_TEXTURE_SET_LOOKUPS=1` även visar recordord
vid entry/retur, och `EUTHERDRIVE_GAUNTDL_TRACE_RECORD_SCAN_ALLOCATE=1` gör en
engångsdump av de första 16 länkade recordsen vid konsumentloopen.

Verifieringsartefakter:

- `/tmp/eutherdrive-gauntlet-probe/companion-realframes-f100-f300-20260718.log`
- `/tmp/eutherdrive-gauntlet-probe/gauntdl-companion-realframes-f300-20260718.warm`
- `/tmp/eutherdrive-gauntlet-probe/companion-f300-texture-record-return-plus200k-20260718.log`
- `/tmp/eutherdrive-gauntlet-probe/companion-f300-tmu-state-producer-plus1m-20260718.log`
- `/tmp/eutherdrive-gauntlet-probe/companion-f300-record-list-state-plus200k-20260718.log`
- `/tmp/eutherdrive-gauntlet-probe/static-size-no-companion-record-list-plus200k-20260718.log`

### Arena-reservationen är äkta men inte en saknad initiering

Den första allocator-tracen rapporterade felaktigt fem nollor eftersom
diagnostikhjälparen läste `0x802380f4..0x80238104`, medan guestkoden använder
`0x802280f4..0x80228104`. Efter korrigering visar samma object-load-loop en
fullt initierad arena:

```text
reserved  0x802280f8 = 00000001
cursor    0x802280fc = 000062d8
limit     0x80228100 = 0051aac0
base      0x80228104 = 802db440
```

`GetMemBase() called while mem reserved` och `AllocMem() called while mem
reserved` är alltså riktiga, men de betyder inte att arena-base/cursor saknas.
En write-watch på reservationsflaggan stänger dessutom livscykeln exakt:

```text
800c9220  0 -> 1  ra=800ac10c
800c9248  1 -> 0  ra=800abe8c
800c9220  0 -> 1  ra=800abc80
800c9220  1 -> 1  ra=800abc80 (följande record)
```

Den andra reservationen kommer från record-publiceraren kring `0x800abc78`.
Den reserverar hela den återstående arenan, binder den nya recordtabellen vid
`0x80252da0` och returnerar utan lokal release. En canonical frame-1000-state
har fortfarande flaggan `1`, medan frame-100-snapshoten startar med `0`; den
långlivade reservationen är därför reproducerbar guest-state och ska inte
nollas syntetiskt. Allokeringarna fortsätter dessutom att flytta cursor trots
diagnostikvarningarna.

Slutsatsen är att allocator-varningarna inte förklarar den frysta
loading-texturen. Fortsätt vid world-load completion/publicering och leta efter
statusen som ska växla materialkonsumenten bort från set 0 / record 0.

Default-off diagnostiken skriver nu initialvärdet för
`EUTHERDRIVE_GAUNTDL_TRACE_MAIN_RAM_VALUE_TRANSITION_ADDRESS`, visar rätt
allocator-globals och inkluderar dem i record-list-headern.

Verifieringsartefakter:

- `/tmp/eutherdrive-gauntlet-probe/companion-reservation-lifecycle-corrected-f100-f132-plus1m-20260718.log`
- `/tmp/eutherdrive-gauntlet-probe/f100-reservation-flag-writer-plus1m-20260718.log`
- `/tmp/eutherdrive-gauntlet-probe/f100-reservation-helper-cputrace-plus1m-20260718.log`
- `/tmp/eutherdrive-gauntlet-probe/f100-reservation-owner-cputrace-plus1m-20260718.log`
- `/tmp/eutherdrive-gauntlet-probe/f100-reservation-flag-initial-20260718.log`
- `/tmp/eutherdrive-gauntlet-probe/f1000-reservation-flag-initial-20260718.log`

### Companion-spåret avfärdat; första saknade streamheadern är index 4

En riktig frame-körning av static-size/companion-grenen från f300 till f500
fortsätter exekvera Type3-paket (`5429 -> 12899`), men texture writes stannar
på `1093`, swaps på `196` och bilden förblir svart förutom den lilla vita
triangeln. Hashen är fortsatt `0xf29eb67c`. Den experimentfria f132-orakeln
har däremot `15747` texture writes, `363` swaps och hash `0xd083385f`.
Companion-grenen är därför en regression och ska inte vara grund för nästa
fix.

En ny initial snapshot i den default-off indexerade source-tracen gör det
möjligt att läsa en sen state utan att först behöva träffa en historisk
writer-PC. Experimentfri f900 visar följande gräns:

- index 2 har en separat `snm`-header vid `0x802e5718`, offset `0x9144`, count
  `13`;
- index 3 har en separat `stk`-header vid `0x802e7718`, offset `0xa3a4`, count
  `9`;
- index 4..8 pekar fortfarande på source 0 (`0x802e1718`), har tomma egna
  `0x2000`-fönster, nollade side slots och tomma assetnamn;
- index 9 är ett separat `font_story`-specialfall och inte del av samma
  object-stream.

Record- och QIO-fälten i samma trace visar att posterna 2..4 är återställda
till noll och att deras förväntade QIO-objekt är färdigställda/fria vid f900.
Den sena snapshoten kan alltså bevisa slutresultatet men inte ensam återskapa
filnamn och destination efter completion.

Den tidiga f100->f132-livscykeln lokaliserar ordningsfelet mer precist. Vid
`0x800abe78` når record-loopen stream index 2 med limit 2 medan
`0x802e5718` ännu är tom. Guardens source-owned limit kan då inte läsas och
loopen avslutas. Den befintliga QIO-fixen hydratiserar `snm` först senare, utan
att loopen återbesöker gränsen. En default-off kontroll hydrerade endast det
bevisat tomma fönstret när gränsen träffades. Då skedde:

```text
index 2  snm  limit 2 -> 13
index 3  stk  short-read, limit 2 -> 9
index 7  nin  hydreras, men count 19 avvisas av configuredMax 13
```

Kontrollen är neutral vid f132 men en rättvis f100->f200 A/B avfärdar den
tydligt. Baseline når den färgade `frameHash=0xd083385f` med `62984` texture
writes och `11649` berörda ord. Tidig boundary-hydrering faller tillbaka till
den svarta `0xf29eb67c`-bilden, producerar `1004872` texture writes och berör
bara `2049` ord. Kandidaten är därför borttagen, inte bara default-off.

Den viktiga nya slutsatsen är att stream 4..8 inte kan lagas genom att fylla
det första tomma source-fönstret före request completion. Den riktiga gränsen
är fortfarande per-entry-QIO: guestvalt filnamn, logical offset, destination
och completion-ordning måste bevaras innan source ownership publiceras. Höj
inte max-count till 27; index 7/count 19 är sedan tidigare ett separat
diagnostiskt spår.

Verifieringsartefakter:

- `/tmp/eutherdrive-gauntlet-probe/companion-realframes-f300-f500-20260718.log`
- `/tmp/eutherdrive-gauntlet-probe/f900-indexed-source-4-initial-20260718.log`
- `/tmp/eutherdrive-gauntlet-probe/f900-indexed-source-2-qio-initial-v2-20260718.log`
- `/tmp/eutherdrive-gauntlet-probe/f100-f132-plus1m-index4-source-state-20260718.log`
- `/tmp/eutherdrive-gauntlet-probe/f100-f132-plus1m-qio-lifetime-20260718.log`
- `/tmp/eutherdrive-gauntlet-probe/f100-f132-plus1m-stream-source-state-20260718.log`
- `/tmp/eutherdrive-gauntlet-probe/f100-f132-plus1m-stream-boundary-hydrate-20260718.log`
- `/tmp/eutherdrive-gauntlet-probe/stream-boundary-control-f100-f200-20260718.log`
- `/tmp/eutherdrive-gauntlet-probe/stream-boundary-experiment-f100-f200-20260718.log`

### Streamgränsen är descriptorbygge, inte per-entry-QIO

En observationsren sond över `0x800a7094 -> 0x800abe54` binder nu ihop den
tidigare CPU-tracen med record 0 och dess QIO-slot. Den aktiveras med
`EUTHERDRIVE_GAUNTDL_TRACE_BGLOADMODEL_STREAM_DESCRIPTOR_BUILDER=1` och visar
att hjälparen får descriptor `0x802e2158`, source `0x802e1718`, offset `0` och
count `2`. Den ändrar descriptorordet vid `+0x18` från `0x00017ab4` till
`0x00010000` och returnerar `-1`; callern flyttar därefter sin descriptorcursor
två poster och lämnar loopen.

Den tidigare record-selector-tracen identifierar de två posterna exakt. De är
de två 0x50-byte-materialposterna vid `0x802e2158` och `0x802e21a8`, inte
asset/source-index 0 och 1. Post 0 ger den redan kända felstora
`0x188d2302`-extentberäkningen från råhalvorden `0x40e5/0x60da`; post 1 har
nolldimension och ger extent noll. Två är därmed det korrekta tabellantalet
för just denna source. De äldre tracefälten `streamIndex` och `streamLimit`
ska läsas som descriptorcursor och descriptorantal vid denna callsite.

QIO:n vid samma gräns är redan färdig: slot `record+4` pekar på `0x80217c58`
med callback `0x800ab4e4`, destination `0x802e3718`, request/read `0x2000` och
status `2`. Det betyder att `0x800a7094` konsumerar och transformerar
descriptor-state; den väljer inte fil, offset eller destination för en ny
request.

En ombyggd f100--f132-körning med bara denna trace aktiverad behåller den
tidiga kontrollens `frameHash=0xf29eb67c`; sonden muterar inte guest-state.

Den tidigare QIO-tracen gör proveniensen ännu tydligare. Guestens enda request
före denna gräns är generisk:

```text
file=textures.rom  offset=0  bytes=0x2000  destination=0x802e1718
```

Först vid return-PC `0x800c9944` mappar det befintliga adapterexperimentet om
den till den fördefinierade index-1-payloaden `gei`, disk `0x14a6f600`, och
destination `0x802e3718`. `gei` är alltså inte ett guestvalt per-entry-QIO i
den observerade livscykeln. Den föregående slutsatsen att nästa fix bara skulle
bevara guestens per-entry-filnamn/offset var för stark.

Nästa kausala gräns är därför inte att höja denna tvåpoststabell. Gå tillbaka
till parser-/callbackpubliceringen som skapar post 0:s orimliga
`0x40e5/0x60da`-dimensioner och som ska materialisera/upload:a ytorna i den
senare state-7-descriptorn. Behåll den separata source-owned maxgränsen 13 för
`snm`-kontrollen, men återinför inte tidig source-hydrering eller bulk-fill och
tolka inte descriptorcursorn som ett globalt asset-index.

Verifieringsartefakter:

- `/tmp/eutherdrive-gauntlet-probe/stream-loop-cpu-qio-f100-f200-20260719.log`
- `/tmp/eutherdrive-gauntlet-probe/stream-helper-cpu-f100-f200-20260719.log`
- `/tmp/eutherdrive-gauntlet-probe/stream-descriptor-builder-f100-f132-20260719.log`

### Index 4 når riktig QIO och publicerar `kjh`

En observationsren fortsättning från den experimentfria f300-checkpointen till
f700 visar att den indexerade laddningen inte har fastnat på index 2. Den
befintliga QIO-sekvensen laddar först `snm` som en full `0x2000`-request till
`0x802e5718`, därefter `stk` som short-read-header till `0x802e7718`, och gör
sedan sin state-7 body-read. Vid f700 har index 1--3 därför separata headers,
medan index 4 fortfarande är det första helt tomma source-fönstret.

En fortsatt, opatchad f700--f1100-replay träffar sedan nästa riktiga cykel:

```text
index=4 code=kjh dest=0x802e9718 bytes=0x2000 disk=0x15130e00
header: marker=0xf00b0001 bodyOffset=0x9a58 count=0x1e kind=0x0d
```

Det avfärdar både teorin att urvalsloopen återväljer index 2 och den äldre
f900-slutsatsen att index 4 aldrig når QIO. F900-snapshoten låg helt enkelt
före nästa laddningscykel. Kontrollens f1100-resultat är
`frameHash=0xb86ea0ec`, `197112` färgade pixlar och `33068032` texture-map
writes.

Efter hydreringen pekade source-slot 4 fortfarande på den statiska source 0.
Ett default-off A/B med den redan befintliga hydrated-source-owner-reparationen
begränsad till mask `0x10` publicerar `0x802e9718` i slot 4. Parsern konsumerar
då rätt header/body och skapar asset entry 4 som
`0x802f3170 / length 4 / "kjh"`. Publiceringen överlever det efterföljande
parserpasset. Vid f1100 är renderresultatet exakt neutralt mot kontrollen:
samma hash, pixelantal och texture-map-statistik. Det är alltså en semantisk
livscykelfix utan tidig renderregression, men ännu inte en omedelbar visuell
förbättring vid denna frame.

Baseline-scriptet aktiverar därför hydrated-source-owner endast för index 4.
Nästa sond ska följa asset entry 4 från `0x802f3170` till första
texture-set-lookup/upload/draw, inte återgå till tidig hydration eller ändra
descriptorantalet två.

Verifieringsartefakter:

- `/tmp/eutherdrive-gauntlet-probe/index4-lifecycle-f300-f700-20260719.log`
- `/tmp/eutherdrive-gauntlet-probe/index4-lifecycle-f700-f1100-20260719.log`
- `/tmp/eutherdrive-gauntlet-probe/index4-owner-mask10-f700-f1100-20260719.log`
- `/tmp/eutherdrive-gauntlet-probe/gauntdl-index4-lifecycle-f1100-20260719.warm`

#### `kjh` stannar i asset-tabellen efter parsern

En reproducerbar owner-publicerad f1100-snapshot bekräftar att renderloopen i
den aktuella scenen bara gör texture-set-lookup för set 0, record 0 och 1
(`0x802e2158` och `0x802e21a8`). Read-watch på hela asset entry 4 och de
första `0x800` byten från `kjh`-bodyn ger noll träffar både under f1100--f1120
och, för bodyn, under hela f700--f1100-QIO/parsercykeln. Parsern publicerar
alltså pekaren men konsumerar inte body-payloaden.

En RAM-vid pointer scan vid f1100 hittar `0x802f3170` exakt en gång: i
`0x8024fa60`, asset entry 4 själv. Pekaren har inte kopierats till någon
render-, owner- eller queue-struktur. Nästa kausala mål är därför guestkoden
som slår upp/aktiverar poster i `0x8024f9a0`-tabellen. Följ index- eller
namnlookupen för entry 4 fram till första pointer-load; ändra inte QIO,
`kjh`-payloaden eller Voodoo-uppladdningen innan en sådan consumer finns.

Verifieringsartefakter:

- `/tmp/eutherdrive-gauntlet-probe/gauntdl-index4-owner-f1100-20260719.warm`
- `/tmp/eutherdrive-gauntlet-probe/index4-body-consumer-f700-f1100-20260719.log`
- `/tmp/eutherdrive-gauntlet-probe/index4-asset-read-f1100-f1120-20260719.log`
- `/tmp/eutherdrive-gauntlet-probe/index4-body-read-f1100-f1120-20260719.log`

#### Asset-callbacken aktiverar inte `kjh`

En statisk scan av alla direkta referenser till `0x8024f9a0` hittade tolv
guest-kodställen. Gettern vid `0x800ab410` har inga direkta `jal`-callers i
den laddade koden. Den äldre slutsatsen om validatorn vid `0x800b2830` ska
däremot inte användas: dess PC-filter var inte kanoniskt och den grenen är
därför fortfarande öppen.

Callbacken vid `0x800aa898` är en riktig asset-table-consumer. Den läser
`assetEntry[index]+8` vid `0x800aaa14` och använder nollvärdet för att hoppa
över hjälparen vid `0x800ab0ec`. En ny default-off trace kan filtrera detta
ställe per index:

```text
EUTHERDRIVE_GAUNTDL_TRACE_BGLOADMODEL_ASSET_CONSUMER=1
EUTHERDRIVE_GAUNTDL_TRACE_BGLOADMODEL_ASSET_CONSUMER_INDEX=4
EUTHERDRIVE_GAUNTDL_TRACE_BGLOADMODEL_ASSET_CONSUMER_LIMIT=128
```

Den filtrerade f700--f1100-körningen ger inga index-4-träffar. En kanonisk
CPU-trace visar varför: den observerade callback-begäran löser index 0 och
stannar där; den itererar inte vidare till entry 4. `kjh` är därför
förladdad assetmetadata för ett annat objekt, inte den aktiva scenens saknade
texture-upload. Att fabricera word8 för entry 4 saknar kausalt stöd och ska
inte provas.

En exporterad f1100-frame med owner-publiceringen har fortsatt
`frameHash=0xb86ea0ec` och `197112` färgade pixlar, men bilden är visuellt
kraftigt korrupt: flerfärgat brus, horisontella band och en stor vit
bottenyta. Nästa visuella mål går därför tillbaka till den aktiva set-0,
record-0-vägen (`0x802e2158`) och dess felstora `0x40e5/0x60da`-descriptor,
inte till fler index-4-metadataexperiment.

Verifieringsartefakter:

- `/tmp/eutherdrive-gauntlet-probe/index4-asset-callback-canonical-f700-f1100-20260719.log`
- `/tmp/eutherdrive-gauntlet-probe/index4-asset-consumer-filtered-f700-f1100-20260719.log`
- `/tmp/eutherdrive-gauntlet-probe/asset-table-static-code-20260719.log`
- `/tmp/eutherdrive-gauntlet-probe/gauntdl-index4-owner-f1100-20260719.png`

### Formatterarens frame-cursor öppnar den riktiga static_lr-livscykeln

Formatter-wrappern `0x8011f3c0` bygger sin interna frame vid `sp+0x10` och
anropar `0x80120204`. Disassemblyn och wrapperns terminator visar att aktuell
output-cursor ligger i `frame+0x00`, inte i `frame+0x54`. De tidigare
format-fastpatharna skrev därför till fel adress och lämnade den riktiga
pathbufferten tom.

Den korrigerade, guardade wrapperacceleratorn läser destinationen från
`frame+0x00`, stöder literal, `%%` och `%s`, tar 32-bitars varargspekare från
`a2`-listan, uppdaterar frame-cursorn och returnerar producerad längd. De
granulära formatteracceleratorerna och den osäkra in-flight-fallbacken kan nu
isoleras med:

```text
EUTHERDRIVE_GAUNTDL_FASTPATH_FORMAT_ACCELERATORS
EUTHERDRIVE_GAUNTDL_FASTPATH_FORMAT_BUFFER_INFLIGHT
```

Med baselinepreseten aktiv materialiseras därefter bland annat:

```text
/d0/hstable/hstable_e.rom
/d0/hstable/hstable_j.rom
/d0/audio/aud_data.rom
/d0/static_lr/objects.rom
/d0/static_lr/textures.rom
```

Den guestproducerade `objects.rom`-pathen ger nu en exakt QIO-guard. Slot 0:s
första `0x2000` byte mappas därför till den redan FSYS-bevisade payload-LBA:n
`0x7d970`, med logical offset noll. F132 verifierar utan path-owner- eller
size-owner-experiment:

```text
source+0x40 = 0xf00b0001
source+0x5c = 0x00067a98
source+0x60 = 0x00000149
source+0x64 = 0x0000002e
```

Det flyttar den aktiva selectorn från de felaktiga texture-bytarna vid
`0x802e2158` till objekttabellen vid `0x802ecb6c`. Tabellen ligger dock vid
`source+0xb454`, utanför den färdigmarkerade `0x2000`-requesten, och innehåller
därför ännu stale RAM i standardvägen. F300 avslutar rent med
`frameHash=0xf29eb67c`; detta är framsteg i fil- och parserproveniensen, inte
ännu en bildfix.

En kontrollerad bulk-body-sond laddade hela den verifierade `0x67b4c`-extenten.
Då gav alla 46 poster rimliga mipstorlekar (`0x50`, `0x150`, `0xaa0` och sista
`0x2a8`), sista end-offset blev exakt `0x11de4`, och f150 ändrades till
`frameHash=0x784f3e66`. När den bevisade companionextenten vid `0x0fb95e00`
också publicerades blev texturorden icke-noll men bilden återgick till svarta
`0xf29eb67c`. Bulk-body/companion-sonden är därför inte promoterad.

Nästa gräns är exakt: bevara requestägd destination och completion för den
guestformaterade `/d0/static_lr/textures.rom`-requesten. Standardvägen får
inte markera den återanvända slot-0-QIO:n klar med tomma fält eller fortsätta
använda objectbasen `0x802e1718` som texture-source efter att recordtabellen
har skannats.

Verifieringsartefakter:

- `/tmp/eutherdrive-gauntlet-probe/format-frame-destination-f100-f132-20260719.log`
- `/tmp/eutherdrive-gauntlet-probe/static-objects-path-map-f100-f132-20260719.log`
- `/tmp/eutherdrive-gauntlet-probe/static-objects-path-map-f100-f300-20260719.log`
- `/tmp/eutherdrive-gauntlet-probe/static-objects-record-selection-f100-f300-20260719.log`
- `/tmp/eutherdrive-gauntlet-probe/static-objects-qio-lifecycle-f100-f150-20260719.log`
- `/tmp/eutherdrive-gauntlet-probe/static-objects-full-body-f100-f150-20260719.log`
- `/tmp/eutherdrive-gauntlet-probe/static-objects-real-path-companion-f100-f150-20260719.log`

### Worker-kön dräneras och nästa passport-jobb publiceras

Den tidigare scheduleranalysen använde fel adress för interruptnivån.
Instruktionen `lui 0x8023; lw ...,0x8160` använder en signerad immediate och
läser därför `0x80228160`, inte `0x80238160`. En korrigerad write-watch
visar att värdet inte sitter fast: initieringen skriver `-1`,
`0x800de46c` skriver `0` före varje callback och `0x800de494` återställer
det tidigare `-1` efter callbacken.

Det förklarar också varför `signal(0)` inte sätter CP0 Cause `0x0200` för
just den observerade worker-enqueuen. Signalen sker inne i den redan aktiva
schedulersektionen. Det är inte i sig ett tappat jobb: schedulerloopen vid
`0x800de420` plockar callback och context ur noden och gör `jalr s1` direkt.

Med den inbyggda, aktuella preseten
`EUTHERDRIVE_GAUNTDL_BRINGUP_BASELINE=1` och dess interrupt bridge aktiv
träffar CPU:n `0x800f10e0` vid instruktion `#11861244` och igen vid
`#11861760`. Det andra anropet dispatchar objektstate 1 till `0x800f126c`.
Handlern publicerar state 2 i `object+0xe8` och anropar `0x800efb7c` för att
fortsätta jobbet. Senare, vid `#12556104`, materialiserar guestkoden
callbackadressen `0x800f087c` och skriver den i nästa jobbnode. De tidigare
slutsatserna att `0x800f10e0` aldrig körs och att köhuvudet aldrig konsumeras
är därför avfärdade.

Presetskillnaden är kausal. Med full baseline men explicit
`EUTHERDRIVE_GAUNTDL_FIX_RUNTIME_INTERRUPT_BRIDGE=0` stannar f100--f105 efter
initieringsskrivningen och når inte schedulerdräneringen. Med bridgen aktiv
når en ren f100--f115-replay riktig `"Loading Game."`,
`frameHash=0x30e41dc5`, 38 415 LFB-writes och 64 texture-map-writes. Den exakta
entry-tracen för `0x800f087c` gav ännu ingen träff senast f115, så dess
konsumtion är nästa avgränsade gräns. Ett f130-försök avslutades innan
scoreboard och skapade ingen snapshot; det ska inte användas som resultat.

En ny default-off trace
`EUTHERDRIVE_GAUNTDL_TRACE_RUNTIME_WORKER_SIGNAL=1` rapporterar bara när CP0:s
software-pending-bit och worker-kön samtidigt är aktiva, samt när emulatorn
faktiskt går in i interruptvektorn. Den läser inte worker-köns guestminne när
tracen är avstängd.

Nästa steg:

1. Spara en reproducerbar f115-snapshot med den inbyggda baseline-preseten.
2. Fortsätt från f115 och tracea exakt `0x800f087c` tills callbacken anropas.
3. Dumpa första stabila frame efter `"Loading Game."` och separera
   schedulerframsteg från de kvarvarande null-body/texturproblemen.

Verifieringsartefakter:

- `/tmp/eutherdrive-gauntlet-probe/scheduler-level-owner-f100-f105-20260719.log`
- `/tmp/eutherdrive-gauntlet-probe/worker-entry-full-baseline-bridge-f100-f105-20260719.log`
- `/tmp/eutherdrive-gauntlet-probe/worker-state-machine-f100-f105-20260719.log`
- `/tmp/eutherdrive-gauntlet-probe/worker-progress-f100-f110-20260719.log`
- `/tmp/eutherdrive-gauntlet-probe/passport-worker-entry-f100-f115-20260719.log`

### Passport-QIO:n konsumeras synkront efter status `0x080c`

Den saknade exakta entryn för `0x800f087c` är inte längre den primära
schedulergränsen. En per-instruktion transition-watch visar att QIO-nodens
länkfält `0x802956a4` alltid skrivs till `0x8021e980` av en lyckad enqueue vid
`0x800ed590`, men därefter nollas av den accelererade guest-loopen vid
`0x800d1470`. Den requeststorleksberoende loopen börjar vid `0x802956a0`,
nollställer bland annat båda callback-qworden och återgår till QIO-koden med
`ra=0x800ebd28`; därför syntes nollningen inte i den tidigare store-baserade
write-watch-tracen.

Statusproveniensen förklarar ordningen. Guestkoden vid `0x800f1de4` skriver
själv `0x080c` till `0x80295684`, eftersom objektet som hämtas via
`0x800ec184` har state 2 i `0x8021e990`. Den befintliga
`TryRepairKnownRuntimeMountQioStatus` maskerar sedan `0x080c` till `0x0800` vid
`0x800f5b44`. Waitern fortsätter synkront och återinitialiserar QIO-objektet,
vilket konsumerar/nollställer den inbäddade callbacknoden innan schedulerloopen
kan göra en separat `0x800f087c`-entry. Ett försök att blockera host-completion
medan noden var länkad ändrade inte förloppet och har återtagits: statusen
produceras av guestkoden före hostmaskningen.

State 2 har en separat verifierad proveniens: `0x800f124c` flyttar
`0x8021e990` från 0 till 1 och `0x800f1544` flyttar den från 1 till 2 under den
redan observerade `0x800f10e0`-workerprogressen. Nästa felsökning ska därför
inte forcera `0x800f087c` eller återvinna descriptorpoolen. Den ska avgöra om
`0x080c` är den förväntade "redan monterad"-status som synkronwaitern
konsumerar, och därefter återgå till den kvarvarande texture/body-proveniensen.

Den default-off write-watch-tracen täcker nu även
`TryFastPathKnownRuntimeZeroQwordFillTail`, så framtida hostaccelererade
qword-nollningar rapporteras med kind `fast-zero-qword`.

Den tidigare ofullständiga f130-noteringen är också ersatt av den verifierade
fortsättningen från f115: `frameHash=0x308a2ac6`, 1 730 packet-3-draws och
113 344 texture-map-writes, men den valda framebufferexporten är fortfarande
nästan svart (`nonBlack=707`, bara fyra färgade pixlar utöver den kända vita
triangeln). Descriptorpoolens räknare når 64 och free-head blir noll, men detta
är ännu inte bevisat som orsak till den svarta bilden.

Verifieringsartefakter:

- `/tmp/eutherdrive-gauntlet-probe/passport-node-transition-owner-f100-f110-20260719.log`
- `/tmp/eutherdrive-gauntlet-probe/passport-status-transition-f100-f110-20260719.log`
- `/tmp/eutherdrive-gauntlet-probe/passport-status-producer-trace-f100-f110-20260719.log`
- `/tmp/eutherdrive-gauntlet-probe/passport-mount-state-transition-f100-f110-20260719.log`
- `/tmp/eutherdrive-gauntlet-probe/worker-progress-f115-f130-20260719.log`

### QIO-dispatchförsöket är avgränsat och återtaget

Den generiska runtime-formateraren hanterar nu även `%c`, vilket återställer
de faktiska gästnamnen `indexA.rom` och `/d0/passport/indexA.rom` i stället
för att lämna formatsekvensen oformaterad.

En instruktionsexakt stop vid första `0x800ecda4` från f106 visar att
`0x80295670+0x30` då är en giltigt länkad intrusiv nod: callback
`0x800f087c`, context `0x80295670` och `pprev=0x8021e980`. Close returnerar
`0x3007` medan länken fortfarande är aktiv.

Det är däremot fel att anropa `0x800de3fc` med list-headern `0x8021e97c`.
Den enda riktiga call siten vid `0x800de5d8` bygger en sexfälts
dispatcher-deskriptor på stacken; ett direkt köargument fastnar därför i
schedulerns interna loop. Ett smalare försök att avlänka noden och anropa
`0x800f087c` direkt nådde close-returen men gav `0x300b`, lämnade free-head
noll och ökade poolräknaren till 65 vid f109. Båda dispatchförsöken är
återtagna. Detta stämmer med den tidigare bevisade synkrona konsumtionen efter
`0x080c`: nästa steg ska följa den verkliga status-/close-livslängden, inte
forcera workern.

Verifieringsartefakter:

- `/tmp/gaunt-qio-scheduler-map-20260719.log`
- `/tmp/gaunt-open-worker-map-20260719.log`
- `/tmp/gaunt-qio-direct-dispatch-f106-f109-20260719.log`

### Prematur WaitForQio-completion äger descriptorläckan

En full write-watch över `0x80295670..0x802956ff` från f106 visar den
repeterade resource-size-livslängden. `0x800ec828` publicerar handle 5, 6, 7
och vidare, `0x800ed590` länkar callbacknoden, men hostreparationen
`TryCompleteKnownRuntimeMountWaitForQio` skriver omedelbart `0x0800` för
handle 5 och `0x3000` för alla handle över 5. `WaitForQio` lämnar därför
loopen medan noden fortfarande är länkad. Nästa operation vid `0x800ec304`
och close vid `0x800ecdc8` skriver båda `0x3007`; därefter nollställer
`0x800d1470` hela QIO-objektet och nästa försök allokerar en ny poolpost.

En strikt linked-node-spärr på den syntetiska completionen bekräftar kausalitet:
vid f107 är callbackkön dränerad, free-head `0x8021dd18` och poolräknaren 4;
vid f109 är free-head fortfarande giltig och räknaren 6 i stället för 64.
Spärren är ändå inte en komplett reparation. En senare request stannar i den
riktiga wait-loopen, eftersom ingen filesystem-service-signal genereras; till
f112 har swapräknaren nått 1576 utan att noden konsumerats.

Varken software-interrupt `0x0100` eller `0x0200` är rätt wakeup: båda ger en
okvitterad interruptstorm och lämnar target-noden länkad. Stackdumpen vid f112
visar dessutom att waitern inte är reentrant under `0x800de3fc` och att
scheduler-level `0x80228160` är `-1`. Köhuvudet `0x8021e97c` ägs av en tidigare
filesystem-service-post med callback `0x800f7060`; nästa gräns är att följa den
servicens normala pump/kvittering före `0x800f087c`, inte att forcera target-
workern eller syntetisera fler statusar. Alla beteendeexperiment är återtagna.

Verifieringsartefakter:

- `/tmp/gaunt-qio-object-lifetime-f106-f109-20260719.log`
- `/tmp/gaunt-qio-real-completion-f106-f109-20260719.log`
- `/tmp/gaunt-qio-real-completion-f109-f112-20260719.log`
- `/tmp/gaunt-qio-reentrant-stack-f112-20260719.log`
- `/tmp/gaunt-qio-targeted-wake-f109-f110-20260719.log`
- `/tmp/gaunt-qio-targeted-wake-sw0-f109-f110-20260719.log`

### Guestens timer- och schedulerpump är återställd som default-off experiment

> **Korrigering 2026-07-20:** experimentet nedan är inte säkert. Varje
> direktanrop får mutera guest-RAM, men efter 100 000 steg återställs alltid
> CPU/FPU/CP0-kontexten även om anropet inte har returnerat. En timeout kan
> därför avlänka en callback och samtidigt kasta bort dess fortsättning.
> Experimentet ska förbli avstängt och får inte användas för nya checkpoints.
> De äldre progressionerna nedan beskriver observerade sidoeffekter, inte en
> giltig context-switch-implementation.

Den tidigare stillastående filesystem-servicen berodde på två saknade led, inte
på ett felaktigt timerrecord. Ett kontextbevarande direktanrop till guestens
timerkö `0x800ccbb0` med `a0=1000` minskar delta-listans huvud exakt 1000 per
tick. Första noden `0x802954f0` löpte ut efter cirka 222 ms; därefter blev
filesystemtimern `0x8021f3f8` huvud och löpte också ut.

`0x800ccbb0` anropar inte timer-callbacken direkt. Vid expiry flyttar den noden
till ready-listan `0x80262ad0` via `0x800de2cc`. Den normala interruptvägen
dränerar listan med `0x800de30c`, med `a0=oldStatus` och
`a1=oldStatus | 1`. Filesystem-callbacken lägger i sin tur ett jobb på
scheduler-listan `0x80262ae0`, vars riktiga wrapper är `0x800de59c` med samma
statusargument. Experimentet
`EUTHERDRIVE_GAUNTDL_EXPERIMENT_VBLANK_GUEST_TIMER_TICK=1` kör nu alla tre
leden med sparad och återställd CPU/FPU/CP0-kontext. Det är fortfarande
default-off och ska inte promoveras före en ren cold-replay.

Från den länkade f112-checkpointen gav kedjan följande verifierade progression:

- f127: timerhuvudet bytte till `0x8021f3f8` och dess delta minskade.
- f138: filesystemtimern var utgången och låg på ready-listan.
- f139: timer-ready-listan tömdes och scheduler-jobbet publicerades.
- f140: scheduler-listan tömdes; riktiga interrupt/exception-returer kördes.
- f150: filesystemstatus nådde 4 och QIO/statusfälten ändrades utan syntetisk
  linked-node-completion.
- f300: den riktiga count-delayen hade passerats och Voodoo nådde 1032 swaps,
  8373 FIFO-ord och 8256 command-I/O-ord. Bilden var fortfarande den svarta
  `frameHash=0xf29eb67c` och target-QIO:n var fortsatt länkad med status
  `0x0805`.

En instruktionstrace bekräftar därefter riktig entry i filesystem-workern
`0x800f7060`. Den läser IDE-status `0x50` via `0xa4000400` och går genom de
verkliga servicehjälparna. Nästa gräns är därför statusproveniensen för
`0x0805` inne i `0x800f7060`-flödet: avgör om den är retry, mediafel eller en
saknad IDE-transfer/completion. Ändra inte QIO-statusen syntetiskt och avlänka
inte target-workern manuellt.

GauntletProbe accepterar nu warm-formatversion 1--8 med en explicit
intervallkontroll; den tidigare pattern-kontrollen avvisade i praktiken en
giltig v8-checkpoint.

Verifieringsartefakter:

- `/tmp/gaunt-context-queue-1khz-f124-f127-20260720.log`
- `/tmp/gaunt-context-queue-1khz-f127-f138-20260720.log`
- `/tmp/gaunt-context-timer-dispatch-f138-f139-20260720.log`
- `/tmp/gaunt-context-full-dispatch-f139-f140-20260720.log`
- `/tmp/gaunt-context-full-dispatch-f140-f150-20260720.log`
- `/tmp/gaunt-context-full-dispatch-f150-f300-20260720.log`
- `/tmp/gaunt-filesystem-worker-full-dispatch-f139-f140-20260720.log`
- `/tmp/eutherdrive-gauntlet-probe/gauntdl-context-full-dispatch-f300.warm`

### IDE DMA-taskfilen slutförs och diskinitieringen passerar

Den riktiga filesystem-workern nådde IDE DMA, och PRD:n vid `0x0021f410`
pekade korrekt ut bounce-bufferten `0x8029e550`. Enheten skrev rätt sektor,
inklusive `0xfeedf00d`, men gästens slutbuffert förblev tom och QIO:n fick
`0x3409`. Statusen skrevs av state 12 efter fyra retries; state 4 översatte
sedan felet till ägarstatus `0x0805` och target-QIO `0x1c07`.

Orsaken var ATA-taskfilen efter en slutförd DMA-läsning. Modellen lämnade
sector count på det begärda värdet `1`. Gästens completion-handler läser
taskfilen och såg därför fortfarande en återstående sektor. MAME:s
`ata_mass_storage_device_base::fill_buffer()` minskar däremot sector count
efter att DMA-bufferten konsumerats; dess `finished_read()` har dessutom en
uttrycklig Gauntlet: Dark Legacy-regel som lämnar adressen på den sista
sektorn. `IdeDiskDevice` gör nu samma sak: sector count blir noll och
flersektorsläsningar lämnar taskfilens adress på den sist överförda sektorn.

En A/B-replay från den rena f139-checkpointen verifierar kausaliteten. Utan
ändringen skrivs `0x3409` upprepade gånger. Med ändringen försvinner alla
`0x3409`-träffar till f150, completion-handlern läser sector count `0`, och
gästen fortsätter med lyckade DMA-läsningar från bland annat LBA 1, 2, 52,
688980, 53, 104, 106, 115 och 116. Vid f141 är den tidigare tomma
slutbufferten `0x802a0578` befolkad och den underliggande QIO-statusen har
gått vidare till `0x3500`. Bilden är ännu oförändrad
(`frameHash=0xf29eb67c`, packet 3 = 0), så nästa gräns är att fortsätta den
nu fungerande disk/filesystem-kedjan och hitta första nya renderprogressen.

Den längre fortsättningen bekräftar att detta inte bara skjuter upp IDE-felet.
Från den reparerade f139-state:n är status fortfarande `0x3500` vid f520,
IDE-controllern ligger i state 10 i stället för felstate 12, och både
timer-ready- och scheduler-listan är tomma. Den hämtade katalogdatan i
`0x802a0578` innehåller bland annat `vmunix`, så bounce-to-final-kedjan är
verkligen konsumerad av gästen. Reproducerbara snapshots finns nu vid f200,
f300 och f520.

Den kvarvarande svarta bilden är därför en separat render/producer-loop. Vid
f520 har Voodoo 20 661 FIFO-ord, 10 312 packets, 2 568 swaps och 12 288
command-I/O-ord, men packets består fortfarande av type 1 och de 18 tidiga
type 4-paketen; packet 3 är fortsatt noll. Nästa spårning ska börja från den
reparerade f520-snapshoten och följa producenten av type 1/swap-loopen eller
villkoret som ska publicera första packet 3, inte återöppna IDE/QIO-felet.

Avfärdade och återtagna experiment före taskfile-fixen:

- en, två eller åtta artificiellt latched BSY-läsningar;
- CPU-instruktioner mellan syntetiska 1 ms-ticks;
- avbruten tickbatch när IDE-IRQ blev aktiv.

Verifieringsartefakter:

- `/tmp/gaunt-disk-init-signature-f139-f150-20260720.log`
- `/tmp/gaunt-ide-worker-3409-path-f139-f141-20260720.log`
- `/tmp/gaunt-dma-bounce-f140.log`
- `/tmp/gaunt-ide-taskfile-complete2-f139-f141-20260720.log`
- `/tmp/gaunt-ide-taskfile-complete-f139-f150-positive-20260720.log`
- `/tmp/gaunt-ide-taskfile-buffers-f141-20260720.log`
- `/tmp/gaunt-ide-taskfile-default-f139-f200-20260720.log`
- `/tmp/gaunt-ide-taskfile-default-f200-f300-20260720.log`
- `/tmp/gaunt-ide-taskfile-default-f300-f520-20260720.log`
- `/tmp/gaunt-ide-taskfile-fixed-state-f520-20260720.log`
- `/tmp/eutherdrive-gauntlet-probe/gauntdl-ide-taskfile-fixed-f200.warm`
- `/tmp/eutherdrive-gauntlet-probe/gauntdl-ide-taskfile-fixed-f300.warm`
- `/tmp/eutherdrive-gauntlet-probe/gauntdl-ide-taskfile-fixed-f520.warm`

### Native QIO-fortsättning och static-container-regression

Den reparerade IDE-linjen behöver inte någon syntetisk `signal()` eller den
osäkra context-preserving timerpumpen efter f700. En ren replay från
`gauntdl-ide-taskfile-fixed-f700.warm`, med
`EUTHERDRIVE_GAUNTDL_EXPERIMENT_VBLANK_GUEST_TIMER_TICK=0`, går under nästa
frame in i den riktiga QIO-callbacken kring `0x800f0c00`. Target-noden
konsumeras av guestkoden och runtime fortsätter genom `Restoring Passwords...`
till `Loading Game.`. Flera riktiga interrupt/exception/ERET-returer syns på
vägen. Att slå av respektive på `RUNTIME_HIGH_TIMER_FASTPATH` ger samma
post-QIO-gräns, så den fastpathen äger inte denna fortsättning.

Den nästa låsningen var i stället en asset-proveniensregression. Med
`EUTHERDRIVE_GAUNTDL_FIX_RUNTIME_BGLOADMODEL_STATIC_PATH_LIFECYCLE=1` matas
hela den råa `static_lr/textures.rom`-containern till en väg som förväntar sig
publicerade runtimeposter. Guestens outer count blir då det floatlika råordet
`0x431c8000`. Loopen `0x800abf00..0x800abf3c` försöker följaktligen skanna
över en miljard 0x50-byteposter, och storlekshjälparen `0x800a64a0` ser
`0xffff`-dimensioner i omappat minne. Detta är inte en lång legitim load utan
fel datalager vid konsumentgränsen.

Baseline-preseten och probe-scriptet sätter nu explicit
`EUTHERDRIVE_GAUNTDL_FIX_RUNTIME_BGLOADMODEL_STATIC_PATH_LIFECYCLE=0`. Den
separata indexed texture body-read-fixen kan vara aktiv. En A/B-replay från
samma f700-state når då world-tabellen (`castle`), bygger render-records och
har vid f750 producerat 2 774 531 texture-writes och 43 352 type-5-paket. Vid
f850 är motsvarande värden kvar och runtime arbetar i render-recordflödet i
stället för den falska stream-scannen. Packet 3 är fortfarande noll och den
exporterade bilden är svart, så nästa gräns är render-recordens null-body/
packet-3-publicering, inte timer-, IDE- eller QIO-livslängden.

En signaturkontrollerad fastpath för den rena storlekshjälparen
`0x800a64a0` räknar nu samma 32-bitars mip-extent utan upp till 65 536 tolkade
loopvarv. Den ändrar inte recorddata eller scanbeslut; den gjorde det möjligt
att bevisa den ogiltiga `0x431c8000`-proveniensen snabbt. Den normala
static-lifecycle-off-linjen behöver inte förlita sig på ett syntetiskt count.

Verifieringsartefakter:

- `/tmp/gaunt-timer-timeout-proof-f700-f701-20260720.log`
- `/tmp/eutherdrive-gauntlet-probe/gauntdl-native-callback-f710.warm`
- `/tmp/eutherdrive-gauntlet-probe/gauntdl-native-callback-f800.warm`
- `/tmp/eutherdrive-gauntlet-probe/gauntdl-native-callback-f1000.warm`
- `/tmp/eutherdrive-gauntlet-probe/gauntdl-no-static-lifecycle-f850.warm`

### Static-object-bodyn återställer giltiga texture descriptors

Den första riktiga Type-3-publiceringen efter diagnostic-menyn innehöll
`S=NaN`. Guestens producent vid `0x800b0834` dividerade descriptorbredd med
descriptorhöjd, men set 0 record 0 vid `0x802ecb6c` innehöll `0/0`.
Write-watch stängde orsaken: `0x800ac42c` publicerar korrekt
`objects.rom + 0xb454`, medan QIO-reparationen bara hade hydrerat requestens
första `0x2000` byte. Recordtabellen låg alltså utanför den levererade delen
av den verifierade `0x67b4c`-bytefilen.

`EUTHERDRIVE_GAUNTDL_FIX_RUNTIME_BGLOADMODEL_STATIC_OBJECT_BODY_READ` laddar
nu resten av just `/d0/static_lr/objects.rom` efter att headern har verifierat
signatur `0xf00b0001`, body-offset `0x67a98`, tabellindex `0x149`, 46 records
och tabelloffset `0xb454`. Samma fix aktiverar den tidigare verifierade
filstorleksägaren så guestens egen allocator reserverar exakt `0x67b4c` och
senare arenaobjekt inte skriver över bodyn. Den felaktiga full-container-vägen
för `static_lr/textures.rom` förblir avstängd.

Ren replay från f700 med baseline-scriptet gav:

```text
f750 frameHash=0x33f33edc
f800 frameHash=0xf244b244
Type3=1160
rasteriserade pixlar=42276
nonfinite rejects=0
```

FIRE 3 från f800 gav vid f850 `Type3=1872`, 5 184 718 täckta pixlar och
167 036 färgade framebufferpixlar (`frameHash=0xa14f6659`). Bilden består
fortfarande av noise och horisontella stripes. Nästa gräns är därför den
separata `0x11de4`-byte texture-companionens request/allokering och bindning,
inte Type-3-layouten, NaN-maskering eller display-buffer-val.

Verifieringsartefakter:

- `/tmp/gaunt-current-texture-table-writes-f700-f750.log`
- `/tmp/gaunt-static-object-body-promoted-f700-f750.log`
- `/tmp/gaunt-static-object-body-promoted-f750-f800.log`
- `/tmp/eutherdrive-gauntlet-probe/gauntdl-static-object-body-promoted-f800.warm`
- `/tmp/eutherdrive-gauntlet-probe/gauntdl-static-object-body-size-fire3-f850.png`

### Static texture-companion når verkliga TMU0-uppladdningar

Guestens nästa riktiga QIO-request öppnar `/d0/static_lr/textures.rom` med
`s0=0x2000`, `s1=0x11de4` och `s2=0x2e`. Readern vid `0x800ab3b0` konsumerar
hela extenten direkt från `0x80349268`; en första-chunk-hydrering lämnar därför
resten av källan tom. Råfilens verifierade extent ligger vid byteoffset
`0x0fb95e00` (LBA `0x7dcaf`) och är exakt `0x11de4` byte.

`EUTHERDRIVE_GAUNTDL_FIX_RUNTIME_BGLOADMODEL_STATIC_TEXTURE_STREAM` hydrerar
hela extenten vid den första exakta requesten, reserverar den bara när
allocatorns cursor matchar companion-basen och låter QIO metadata rapportera
den verkliga requeststorleken `0x2000`. Vakterna kräver rätt path, QIO-slot,
status, recordantal och kvarvarande extent; den äldre felaktiga
full-container-vägen förblir avstängd.

Korrelation från f800 genom FIRE 3 bevisar hela efterföljande kedjan. Guestens
bearbetade buffer `0x8037a4f8...` producerar Type-5 `0xc0000205`-paket från
PC `0x800fe5d4`, och samma writer-poster äger ord som de synliga TMU0-quadarna
faktiskt samplar. Type-5-adressformeln för `seq_8_downld`, inklusive `tt`,
`ts` och 32-bitars alignment, matchar MAMEs `internal_texture_w`/`write_ptr`.
Detta gör companion-hydreringen till en verifierad loaderfix, även om den
nuvarande förenklade rasteriseringen fortfarande visar RGB332-noise/stripes.

Verifierad effekt från den nya companion-linjen:

```text
f800 frameHash=0xe04045fd, Type3=1740, Type5=4627, texture writes=40381
FIRE 3 f850 frameHash=0xc02a9233, Type3=2320, Type5=11055
FIRE 3 f850 textured pixels=4186934, colored framebuffer pixels=167037
```

Två kontroller är fortsatt negativa och ska inte promoveras: sample-bias `0`
ger `frameHash=0x5a1b6aea` med samma stripefamilj, och prefer-TMU0-S/T är exakt
neutral (`0xc02a9233`). Tidiga `0x0180a8cb`-paket innehåller redan giltiga
S0/T0-koordinater `0..256`; nästa rendergräns ska därför isoleras från de sena
helskärmsquadarnas writer/layout-state i stället för att flytta Type-3-fält.

Verifieringsartefakter:

- `/tmp/gaunt-static-texture-record-source-f700-f750.log`
- `/tmp/gaunt-f800-f850-fire3-writer-correlation.log`
- `/tmp/gaunt-static-texture-full-fire3-bias0-f800-f850.log`
- `/tmp/gaunt-static-texture-full-fire3-tmu0st-f800-f850.log`
- `/tmp/gaunt-static-texture-full-fire3-type3-fields-f800-f801.log`
- `/tmp/eutherdrive-gauntlet-probe/gauntdl-static-texture-full-f800.warm`
- `/tmp/eutherdrive-gauntlet-probe/gauntdl-static-texture-full-fire3-f850.warm`

### Den sena helskärmsquaden ägs av en misslyckad movie3-open

F800--f820 med FIRE 3 isolerar den sista icke-rasteriserande producentgränsen.
De 52 nya Type-3-paketen använder samma `0x0180a8cb`-layout. Två av dem bildar
den synliga 512x384-quaden och slår upp `set 10, record 0`. Vid f800 är set-10-
slotten fortfarande noll. Gästkoden publicerar den först senare i delay-slotten
vid `0x800aae64`:

```text
source 0x8039a4e8
record 0x8039a550 (= source + 0x68)
asset  movies/movie3
lookup set 10, record 0 -> 0x8039a550
```

Varken source-objektet eller dess 0x50-bytepost skrivs före lookupen. Båda är
helt nollade, vilket gör att producenten senare beräknar `S=NaN`; rastervägens
fallback till screen-X skapar då de horisontella ränderna. Bufferdumpen visar
samtidigt att buffer 1 verkligen är den synliga ytan, så detta är inte ett nytt
display-bufferfel.

Den generiska resource-open-tracen visar den exakta orsaken. Movievägen försöker
öppna båda riktiga gästfilerna:

```text
/d0/movies/movie3/objects.rom
/d0/movies/movie3/textures.rom
```

Path lookup lyckas, men request-allocatorn `0x800ebed4` returnerar noll. Det är
samma redan verifierade 64-posters QIO-pool som tömdes av den äldre upprepade
`/d0/passport/`-livslängden. File-stat lämnar därför inget callbackresultat i
stackfältet som `0x800c893c` läser, storleken blir noll och movie-containern
allokeras utan innehåll.

En smal kontroll som kopierade det kvarvarande `v1=0x17d0` till stat-resultatet
är avvisad och borttagen. Värdet återkommer för flera orelaterade sökvägar och
är inte verifierad metadata. För movie3 gjorde kontrollen recordet icke-noll
och ändrade f820 från `0xc02a9233` till `0x8327e706`, men bilden blev endast mer
noise/stripes. Det bekräftar kausalitet utan att ge en giltig filstorlek eller
payload.

Den aktuella f800-snapshoten är alltså användbar som negativ renderer-orakel,
men kan inte ensam ge korrekt movie3-data: request-poolens tidigare historik är
redan förlorad. Nästa korrekta bringup-gräns är en ren tidig QIO-livslängd som
låter `/d0/passport/` slutföras utan descriptorläckan och sedan bygger en ny
f800-snapshot. Syntetisera inte movie3-storlekar, diskoffsetar eller records och
ändra inte Type-3-fältlayouten utifrån denna kontaminerade warm state.

Verifieringsartefakter:

- `/tmp/gaunt-current-set10-source-state-f800-f820.log`
- `/tmp/gaunt-current-set10-slot-writes-f800-f820.log`
- `/tmp/gaunt-current-movie-parser-qio.log`
- `/tmp/gaunt-current-movie3-stat-v1-f820.log`
- `/tmp/gaunt-current-movie3-stat-v1-f820.png`

### 2026-07-20: den sena QIO-poolkollapsen börjar i f700 -> f701

Den rena IDE-taskfile-snapshoten vid f700 är den sista verifierat friska sena
utgångspunkten. QIO-poolens free-head är `0x8021dda8` och allokeringsräknaren
är `7`. Redan efter en enda frame, mitt under `Restoring Passwords...`, har
räknaren stigit till `48` och free-head flyttats till `0x8021e558`. Vid f710
är räknaren exakt `64` och free-head noll. Den tidigare f800-snapshotens
poolkollaps sker alltså inte gradvis under movie-loaden; den orsakas av
passport-retryn omedelbart efter f700.

`EUTHERDRIVE_GAUNTDL_EXPERIMENT_PRESERVE_LINKED_MOUNT_QIO=1` ger den rena
A/B-kontrollen. Från samma f700-state är free-head fortfarande
`0x8021dda8` och räknaren fortfarande `7` vid f710, men gästen stannar i
`WaitForQIO: Timeout`. Den syntetiska completionen medan callbacknoden är
länkad är därför både nödvändig för nuvarande falska progression och direkt
orsak till poolkollapsen.

Den sena kön har samtidigt en annan ägare än den tidigare state-12-timern.
Vid f700 är timer-ready-listan `0x80262ad0`, scheduler-ready-listan
`0x80262ae0` och filesystem-timerrecordets länkar alla tomma. Ett längre test
till f800 med den contextbevarande timerproben dränerar den vanliga timerkön,
men ändrar varken filesystem-recordet, service-state 10 eller den blockerade
QIO-kön. Den proben är dessutom fortfarande ogiltig för checkpoints eftersom
en timeout kan behålla RAM-sideffekter och kasta bort CPU-continuationen.

Köhuvudet `0x8021e97c -> 0x8021e8d8` kan nu identifieras exakt. Dess owner är
`0x8021e8a8`, owner-status är redan lyckad `0x3500`, och nodens worker är
`0x800f10e0`. Passport-jobben hamnar bakom denna äldre, färdigbehandlade men
fortfarande länkade nod. Nästa korrekta gräns är därmed den normala
scheduler-återarmningen som ska köra `0x800f10e0` via dispatch-returen
`0x800de480`, så att guestkoden själv avslutar och avlänkar head-jobbet. Varken
manuell avlänkning, descriptor-recycling, syntetisk QIO-status eller direkt
worker-anrop ska användas.

Verifieringsartefakter:

- `/tmp/gaunt-current-f700-f710-pool.log`
- `/tmp/gaunt-pool-f700.log`
- `/tmp/gaunt-worker-timer-links-f700-20260720.log`
- `/tmp/eutherdrive-gauntlet-probe/gauntdl-linked-timer-f800.warm` (endast
  negativt experiment; använd inte som continuation-checkpoint)

### 2026-07-21: scheduler-producenten och Nile-timerkällorna är separerade

En write-watch från den friska f138-staten visar nu scheduleroperationen
instruktion för instruktion. Noden `0x8021f3e8` har redan callback
`0x800f7060` och context `0x8021e8a8` innan den publiceras. List-hjälparen
skriver rotpekaren med `pc=0x800de234` och nästa länk med
`pc=0x800de238`. Dispatchern nollställer därefter bara länkarna vid
`0x800de458` och `0x800de460`; den skriver inte callback/context. Returen
`0x800de480` är alltså fortsatt konsumentretur, inte producent.

Detta avslöjade också ett provenancefel i de äldre sena snapshotarna. Den
reparerade f200-snapshoten har frisk QIO-pool (`free=0x8021dda8`, count 7)
och callbackordet `0x800f7060` kvar. Den sparade f300-snapshoten har däremot
callbackordet noll. En ny f200 -> f300-replay utan den osäkra context-timer-
proben behåller callbackordet, och en write-watch över ordet ser ingen guest-
store som nollställer det. Nollan i den äldre f300/f700-kedjan får därför inte
längre tolkas som en normal guest-avregistrering; den kedjan bär state från den
tidigare contextbevarande direktpumpen och duger endast som negativ oracle.

Nile-modellen hade samtidigt en konkret avvikelse från MAME:s VRC5074. Endast
timer 2 och 3 genererar interrupts: timer 2 mappar till GPT bit 6 och timer 3
till watchdog bit 5. Timer 0/1 är SDRAM-/busstimers och ska inte sätta IRQ.
Modellen använder nu samma mapping. Runtime-bridgens befintliga suppression
av den kortperiodiska Nile-watchdogen är tills vidare kvar; att släppa igenom
den ger fortfarande den kända, korrekt kvitterade men omedelbart återassertade
`cause=0x0800`-stormen. CP0 Compare-spåret är också fortsatt negativt och ska
inte användas som ersättning för tickkällan.

Ändringen bygger rent och f700 -> f701-oraklet är oförändrat:
`frameHash=0xf29eb67c`, pool-count 48 och free-head `0x8021e558`. Nästa
korrekta implementation ska därför ge VBlank/SIO-vägen en resumable guest-
interrupt-context som kan köra `0x800de06c -> 0x800ccbb0` och återuppta en
blockerad scheduler-callback över flera frame-budgetar. Den får inte återställa
CPU-kontекст efter timeout medan RAM-sideffekter behålls, och den får inte
direktanropa `0x800f7000` eller `0x800f10e0`.

### 2026-07-21: en riktig VBlank-utlöst guest-IRQ når timerkön

Det nya default-off-experimentet
`EUTHERDRIVE_GAUNTDL_EXPERIMENT_VBLANK_GUEST_TIMER_IRQ=1` injicerar watchdog-
bit 5 som en vanlig Nile-IRQ i stället för att anropa guestfunktioner direkt.
Gästen går genom den normala exceptionvektorn, kvitterar biten med INTCLR,
kör `0x800de06c -> 0x800ccbb0` och återvänder med `eret`. Emulatorn håller
bara den vanliga timer-3-källan spärrad medan den injicerade IRQ:n är aktiv;
efter `eret` återgår den till runtime-bridgens befintliga suppression. Ingen
CPU-, FPU- eller CP0-kontext återställs av hosten.

Från den rena f200-staten, med den obligatoriska baseline-preseten och
`EUTHERDRIVE_GAUNTDL_FIX_RUNTIME_HIGH_TIMER_FASTPATH=0`, gav en enda frame:

```text
PC före/efter IRQ   0x800c86b8 -> 0x800c8714
Status efter eret   0x34007f01
Cause efter eret    0x00000000
timer deadline      0x00071903 -> 0x0016cc29
timer-ready root    0x80262ad0 -> 0x8021f3f8
frameHash           0xf29eb67c
```

Det viktiga resultatet är att guestkoden själv flyttar filesystem-noden till
timer-ready-listan och återvänder till den avbrutna `WaitForQIO`-loopen. Med
baseline-preseten aktiv finns ingen interruptstorm, och samma test över tio
frames slutar fortsatt i normal runtimekod med IE satt. Testkommandon som bara
laddar warm-staten men utelämnar `EUTHERDRIVE_GAUNTDL_BRINGUP_BASELINE=1` är
ogiltiga: runtime-flaggorna ingår inte i snapshoten och då är interrupt-
bridgens suppression avstängd.

Nästa led är nu avgränsat till timer-ready-dispatchen. `0x800de30c` har exakt
en direkt JAL-anropare vid `0x800dea2c`; den ska normalt dränera
`0x80262ad0`, anropa `0x800f7060` och därefter publicera scheduler-jobbet.
Den nuvarande watchdog-IRQ:n når ännu inte anroparen, så QIO-headen
`0x8021e97c -> 0x8021e8d8` ligger kvar. Ett A/B-försök med GPT bit 6 är
negativt och återtaget: gästen har den källan uroutad i Nile control, så den
ger ingen CPU-pin. Nästa steg är att hitta den riktiga guestvägen till
`0x800dea2c`; varken bit-6-routing, direkt dispatch eller software-IRQ ska
syntetiseras.

### 2026-07-21: guest-IRQ:n kör hela filesystem- och worker-kedjan

Den tidigare slutsatsen att watchdog-IRQ:n inte nådde `0x800dea2c` berodde på
att den första korta tracen slutade innan den indirekta exception-callbacken
var färdig. Från den rena länkade f112-staten går bit 5 genom den riktiga
exceptionvektorn till `0x800dec10`, med returadress `0x800114ec`. Den fortsätter
genom `0x800de82c`, dränerar timer-ready-listan och anropar därefter
filesystem-servicen från schedulerreturen `0x800de480`:

```text
#13388996 pc=0xffffffff800f7060
a0=0xffffffff8021e8a8
ra=0xffffffff800de480
Status=0x34007f01 Cause=0
```

Samma obrutna guestflöde når sedan den tidigare blockerade workern utan
direktanrop eller syntetisk software-IRQ:

```text
#13392060 pc=0xffffffff800f10e0
a0=0xffffffff8021e8a8
a1=0xffffffff8021e8d8
a3=0xffffffff800efb7c
ra=0xffffffff800de480
```

Den kvarvarande interruptstormen hade en separat orsak. Efter att gästen
kvitterat den uttryckligt injicerade bit-5-pulsen satte Nile-modellens vanliga
periodiska timer 3 omedelbart watchdogbiten igen. Om SIO samtidigt var aktiv
återställde dessutom `SuppressOnlyNileTimerInterrupts` den borttagna
timerbiten för att bevara det andra avbrottet. Resultatet blev blandade
`Cause=0x2800`-entries och nya bit-5-assertions inne i handlern.

Runtime-interrupt-bridgen spärrar nu den vanliga timer-3-källan vid själva
producenten. `RequestRuntimeTimerInterrupt` kan fortfarande uttryckligen sätta
bit 5, och bridgefiltreringen tar nu bort timerbitar även när en riktig
device-IRQ återstår. Device-IRQ:n bevaras och går vidare till gästen.

Med en enda injicerad puls (`IRQ_INTERVAL=100`) går f112 -> f120 nu rent ut ur
exceptionkoden:

```text
PC                 0xffffffff800ebaf8
Status/Cause       0x34007f01 / 0
Nile state/pins    0x0100 / 0
timer pulse        0 / 0
timer-ready        empty
scheduler-ready    empty
frameHash          0xf29eb67c
```

Utan den injicerade pulsen är f112 -> f114-oraklet oförändrat: CPU:n ligger
kvar i `0x800c86f0`, service-state är 12, båda ready-listorna är tomma och
frameHash är `0xf29eb67c`. En reload-verifierad ren continuation finns i
`/tmp/eutherdrive-gauntlet-probe/gauntdl-guest-irq-worker-clean-f120-200k.warm`.
Den tidigare f114-filen som sparades mitt i handlern har tagits bort.

Nästa gräns är nu guestens QIO-state efter workern, inte dispatchen. Från den
rena f120-staten ska nästa replay följa `0x800efb7c` och owner/QIO-statusen från
det observerade `0x3500`-läget tills den länkade noden antingen slutförs och
återgår till free-listan eller producerar ett verkligt IDE-fel. Den fysiska
watchdogen ska inte återaktiveras som ersättning för den explicita pulsen.

### 2026-07-21: native mount-QIO konsumeras efter en enda guest-IRQ

Den rena f120-fortsättningen behöver inga fler injicerade avbrott. Workern
`0x800f10e0` driver först filesystem-servicens state 10/2-cykel och de riktiga
IDE-completionerna går genom exceptionvektorn och `eret`. Den länkade
mount-QIO:n ligger kvar medan detta arbete pågår, men descriptorpoolen är
oförändrad med free-head `0x8021dd78` och count 6 genom f170.

Vid progress f200 når samma obrutna guestflöde slutligen den riktiga callbacken:

```text
#35992850 pc=0xffffffff800f087c
a0=0xffffffff80295670
a1=0xffffffff802954e0
ra=0xffffffff800de480
Status=0x34007f01
```

Callbacken körs därefter igen för verkliga `/d0/.../index%c.rom`-jobb. Ingen
direktdispatch, software-IRQ, syntetisk QIO-status eller descriptor-recycling
används. Vid f300 är filesystem-köhuvudet `0x8021e97c` noll, target-QIO:n är
avlänkad, dess handle är `-1` och status har lämnat `0x0800` för `0x0500`.
Runtime har samtidigt nått texten `Initializing Audio...`.

Poolens allocationsräknare är 38 och free-head fortsatt giltig vid både f300
och reload-verifierade f301. En separat f300 -> f310-kontroll lämnar count
exakt 38, så den gamla passport-retryns rusning till 64 är borta efter att
target-jobbet konsumerats. CPU:n slutar i normal runtimekod med IE satt,
IDE/Nile kvitterade och båda ready-listorna tomma. CP0 Compare-latchen ligger
kvar som maskerad IP7 (`Cause=0x8000`); den är inte en aktiv CPU-IRQ eftersom
Status maskerar IP7 och ska inte blandas ihop med den tidigare Nile-watchdog-
stormen.

Ny reload-verifierad continuation:

```text
/tmp/eutherdrive-gauntlet-probe/gauntdl-native-guest-irq-f300-200k.warm
```

Nästa gräns är inte längre QIO/scheduler. Fortsätt från den rena f300-staten
tills audio-/gameinit lämnar den tidiga svarta `frameHash=0xf29eb67c`, och
kontrollera att pool-count förblir under 64 när de senare static/movie-
resurserna öppnas. Först därefter ska den engångspuls som byggde f120-linjen
promoveras från experiment till normal bringup-semantik.

### 2026-07-21: audio-init lämnar count-delay och når `Loading Game.`

Den native QIO-linjen fortsätter reproducerbart genom f520 med pool-count 38,
men CPU:n fastnade därefter i audio-init-hjälparen `0x800457f8`. Anroparen
`0x80046040` skickar `a0=1`; hjälparen kör sin LED/callback, inkrementerar
globalen `0x8016bbe8` och väntar tills det signerade jämförelseresultatet mot
`0x02faf080` blir falskt. Detta är ett rent emuleringsdyrt count-delay, inte en
ny scheduler- eller QIO-blockering.

Ett första försök att prima globalen vid hjälparens entry förkastades eftersom
gästens normala store omedelbart skrev över värdet. Den aktiva fixen
`EUTHERDRIVE_GAUNTDL_FIX_AUDIO_INIT_COUNT_DELAY=1` ingriper därför först vid
den exakta `slt`-instruktionen `0x80045844`, efter att hjälparens riktiga
side-effects redan körts. Den verifierar helper-signaturen, `s0=1`, enable-
globalen `0x80227c80=1`, limit-operanden och sparad returadress `0x80046048`.
Endast jämförelseoperanden `v1` sätts till `limit + 1`; gästen kör själv sin
jämförelse, branch, delay-slot-store, cleanup och return.

A/B från samma rena f520-snapshot gav följande:

```text
fix=0  f521 pc=0x80045838, helpern ~4652 varv, pool-count 38
fix=1  f521 pc=0x80045354/0x800cc650, pool-count 40
```

Den accelererade linjen når `Loading Game.` vid f525 och fortsätter att mata
Voodoo. Vid f540 är räknarna `fifoWords=190209`, `packets=8983`,
`texWrites=113453`, `swaps=14` och `type5=2461`. Poolens kumulativa count har
stigit till 294, men free-head `0x8021ddd8` är fortsatt giltig och descriptors
återanvänds; detta är därför inte ännu evidens för den gamla passport-deadlocken.
Displayen är fortfarande svart (`frameHash=0xf29eb67c`) och inga type-3/draw-
paket har nåtts.

Vegas SIO-reset har samtidigt synkats med hårdvarureferensen: CS2 reset-control
bit 0 driver nu både IOASIC-reset och DCS reset line. Det är korrekt Midway-
Vegas-koppling, men det var inte orsaken till count-loopen; hjälparens write på
CS5 är system-LED.

Probeverktyget skrev tidigare en begärd final snapshot två gånger. Den tidiga
duplicerade skrivningen är borttagen, så en körning producerar nu exakt en
`finalSnapshotSaved`. Den nya continuation-filen är:

```text
/tmp/eutherdrive-gauntlet-probe/gauntdl-audio-init-f540-200k.warm
```

Reload f540 -> f541 är verifierad: CPU:n fortsätter till `0x800de5dc` och de
sparade Voodoo-räknarna är exakt bevarade (`swaps=14`, `texWrites=113453`).

Nästa mätbara gräns är första type-3/draw-paketet eller annan icke-svart
displayövergång. Fortsätt samtidigt följa pool-count/free-list och verkliga
IDE-interrupt så att nästa static/game-resursfel kan skiljas från normal
resursallokering; lägg inte in en ny syntetisk completion utan en observerad
guest-blockering.

Fortsatt körning till f560 visade först en mycket het traversering i
`0x800ef8e8..0x800ef900`: gästen hashar ett 24-bitars asset-ID och följer en
`node->next`-kedja. Noderna runt `0x802b8990` är konsistenta, framåtlänkade
assetposter och f560 -> f561 lämnar loopen normalt; hot-count sjunker från cirka
179k till 4,3k och PC fortsätter till `0x8011f3fc`. Detta ska alltså inte
behandlas som en cyklisk listkorruption. Voodoo-räknarna är däremot oförändrade
genom f561, så nästa probe ska följa asset-initens caller/state efter lookupen.
En ny diagnostisk continuation finns i
`/tmp/eutherdrive-gauntlet-probe/gauntdl-loading-game-f560-200k.warm`.

### 2026-07-21: global FIFO packet-state släpper riktig geometri

Den första f580-körningen rapporterade 12 Type3/draw-paket, men field-trace
visade bytepackad assetpayload som denormaliserade floatkoordinater. Packet-
mapen var nycklad per exakt store-PC trots att Glide-writern använder flera
store-instruktioner för samma sekventiella header/body-ström. Ett första exakt
specialfall för `0x800fe7a0/ac/c4/cc` flyttade felet till den parallella
`0x800fe5d4`-familjen och gav ett tunt färgbrus-band; det förkastades.

`EUTHERDRIVE_GAUNTDL_EXPERIMENT_VOODOO_STANDARD_FIFO_GLOBAL_PACKET_STATE=1`
låter i stället packet ownership följa sammanhängande logisk write-ordning
inom generationen, oberoende av vilken store-instruktion som skrev nästa ord.
Från samma f560-state försvinner alla falska Type3-paket vid f580:

```text
per-PC state   p3=12, rejected=7, frameHash=0xf29eb67c
global state   p3=0,  rejected=0, frameHash=0xf29eb67c
```

Detta parkerar inte riktig geometri. Vid f600 producerar `0x800c4e5c`
kompletta 19-ords `0x0180a8cb`-paket med plausibla floatkoordinater och exakt
depth 19. Resultatet är:

```text
draw/type3     554 / 554
covered        548
rejected       6 (clip)
raster pixels  215460
texture zero   203250
frameHash      0x6127a45e
```

Den globala modellen är därför promoterad till bringup-baseline. f600-bilden
är nu en vit fullskärm med svarta/blockiga fragment, inte längre falskt FIFO-
brus. Den nya reload-checkpointen är
`/tmp/eutherdrive-gauntlet-probe/gauntdl-global-packet-state-f600-200k.warm`.
Nästa gräns är texture ownership/sampling för de riktiga `0x0180a8cb`-
trianglarna: cirka 94 procent av deras prover blir noll. Ändra inte vertex-
decode eller clip för detta; koordinater, packet words och coverage är nu
verifierade.

### 2026-07-21: A8-texturer behandlas som alfamask

Ett fokuserat TMU-registerspår från f600 visar att `textureMode=0x8c2412cf`,
`tLOD=0x00302104` och `texBaseAddr=0xfffff800` kommer från ett komplett
gästpacket skrivet av `0x800bd18c..0x800bd19c`. Basvärdet är alltså inte en
läckt payload. Samma frame växlar avsiktligt mellan bas `0` och `0xfffff800`.
En A/B med rå, oskiftad bas gav fler nollprover (`66004` mot `63932`) och
förkastades; 4 MiB-bankwrap var neutral.

De riktiga trianglarna använder texture format 2, Voodoo A8. Bringup-renderaren
tolkade tidigare alfabyten som grå RGB och skrev därför svarta/grå block runt
glypherna. `EUTHERDRIVE_GAUNTDL_EXPERIMENT_VOODOO_TEXTURE_ALPHA8_MASK=1`
använder i stället den filtrerade A8-intensiteten som täckning för triangelns
itererade färg och lämnar destinationen orörd vid alfa noll. Från exakt samma
f600-state, med oförändrade 278 trianglar och 75180 textureprover:

```text
grå RGB       frameHash=0x80fc291a colored=3558 white=303436
A8-mask       frameHash=0x5a9d0a4b colored=2728 white=304472
```

A8-vägen tar bort de stora svarta alfaytorna och är promoterad till baseline.
Kvarvarande bild är fortfarande fragmenterad: nästa mätbara blockerare är
texture-layout/adresscoverage för glyph-atlasen, inte FIFO packet ownership,
vertexdecode, clip eller format-2-färgsemantik.

### 2026-07-21: gäststyrd triangel-LOD träffar uppladdad atlasdata

Efter att `/tmp` rensades regenererades en ny kall f600-state med Release-
proben och exakt 200000 CPU-steg/frame. Den ligger repo-lokalt i den redan
Git-ignorerade artifact-katalogen:

```text
artifacts/gauntlet-probe/gauntdl-alpha8-global-f600-200k.warm
sha256 c6b9820c966c44d5389d462cfda29dfde216f71284048e85520815e39c1e9a0e
frameHash 0x8e00966c
```

Snapshoten reload-verifieras till samma frame/hash. Dess texture-set-tabell
visar också riktig data vid de levande slotarnas fysiska baser, bland annat
`0x02f620`, `0x057488`, `0x07f4e8` och `0x0a7350`; det är alltså inte en tom
texture-RAM.

Det fokuserade LOD-spåret för de första riktiga f600 -> f601-trianglarna visar
att `tLOD=0x00302104` har både min och max `8.8 LOD = 256`, derivatan ger
`base8p8=256`, och MAME-beräkningen väljer exakt `targetLod=1`. Den gamla
baseline-vägen ignorerade allt detta och samplade alltid LOD 0. Samma snapshot
ger följande rena A/B:

```text
forcerad LOD 0   frameHash=0x3f068f9b zero=4813/11304 colored=8479
triangel-LOD 1   frameHash=0xd7808d00 zero=2083/11304 colored=10967
```

`EUTHERDRIVE_GAUNTDL_EXPERIMENT_VOODOO_TEXTURE_MAME_TRIANGLE_LOD=1` är därför
promoterad till baseline. Den gör att mip-offseten wrappar den höga signerade
basen in i faktiskt uppladdad atlasdata och mer än halverar nollproverna utan
att ändra packet-, vertex- eller coverage-räknarna. Bilden har tydligt mer
glyphinnehåll men är ännu inte korrekt; nästa gräns är återstående atlaslayout
och färg/alpha-kombination för LOD 1.

### MAME-setup-gradienter isolerar nästa förbättring

En direkt fetch-jämförelse på f600 -> f601 visar att vår vanliga layout och
MAME-layouten redan väljer samma LOD 1-bas, storlek, clamp och byteadress för
de första texturproverna. Den kvarvarande skillnaden sitter därför inte i
`GetMameTextureFetchLayout`. Ett separat försök att skala float-koordinaterna
med mipnivån avvisades också: det flyttade den första glyphtriangeln från
uppladdad data vid `0x0064c0` till nollor kring `0x005ba8`.

MAME:s setup-gradienter kan däremot slås på isolerat utan fixed-point-fetch.
Samma snapshot ger en deterministisk förbättring:

```text
triangel-LOD, barycentrisk float       frameHash=0xd7808d00 zero=2083/11304 colored=10967
triangel-LOD, MAME setup-gradienter    frameHash=0x5e9405c3 zero=1228/11304 colored=11688
```

`EUTHERDRIVE_GAUNTDL_EXPERIMENT_VOODOO_TEXTURE_MAME_SETUP_GRADIENTS=1` är
därför promoterad till baseline. Frame-dumpen finns repo-lokalt som
`artifacts/gauntlet-probe/gauntdl-mame-setup-f601.ppm` med SHA-256
`2aa816c6a0ff7126873871e3ef846f1379cbaacd199b3f0ffaaee5c3f6fe698d`.

### Fixed-point-fetch visar glyphkonturerna

Den första numeriska tolkningen av fixed-fetch var missvisande: fler nollprover
såg ut som en regression, men för en A8-glyph är nollorna transparent bakgrund.
Frame-dumpen visar att LOD-skiftet bryter upp de stora blå blocken till smala,
tydligt glyph-liknande konturer.

Källjämförelsen mot MAME hittade samtidigt två riktiga luckor i vår experimentväg:

1. `SampleTextureRgb565MameFixed` ignorerade triangelns beräknade LOD och föll
   tillbaka till den gamla globala force-LOD-inställningen.
2. Perspektivläget använde inte itererad W för `S/W` och `T/W`. Bilinearvikten
   dividerades dessutom med 255 i stället för att använda en 8-bitars fraktion
   med steg om 1/256.

Efter rättningen tar fixed-fetch emot triangel-LOD direkt, gör perspektivdivision
och använder 1/256-fraktioner. Ingen `TEXTURE_FORCE_LOD` eller separat
MAME-fetch-addressflagga behövs. Ren f600 -> f601-baseline ger nu:

```text
frameHash=0xbe038b7b
textured=314 triangles, 11304 pixels, 5821 zero texels
colored=7808
PPM sha256=9c036e676079ed4f6cfe3a9282f23e93a9a63d0374a30a8d22a58a5a3beeb6f3
```

`EUTHERDRIVE_GAUNTDL_EXPERIMENT_VOODOO_MAME_TEXTURE_FIXED_FETCH=1` är därför
promoterad till baseline. Referensbilden ligger repo-lokalt i
`artifacts/gauntlet-probe/gauntdl-mame-fixed-perspective-f601.ppm`. Nästa gräns
är inte längre själva LOD-koordinatskiftet, utan färgkombinationen och de
kvarvarande felplacerade glyphgrupperna.

### Riktig color-path ersätter den syntetiska A8-masken

En isolerad fyrvägs-A/B på samma f601-state jämförde rå A8, A8-mask,
`fbzColorPath` utan mask och båda samtidigt. De aktiva registren är
`fbzColorPath=0x0c482435`, `alphaMode=0x00040400` och `fbzMode=0x00000460`.
Registerbitarna visar att RGB-write är på medan alpha test, alpha blending och
alpha-mask är av. För den här drawen väljer color-path texturen som `other`,
lokal färg som multiplikator och producerar därför textur gånger lokal färg.

Den tidigare `TEXTURE_ALPHA8_MASK`-vägen gjorde nolltexlar transparenta och
blandade lokalfärgen en extra gång. Det gav vit bakgrund och en användbar
bringupbild men stämde inte med registren. Kombinationen mask plus color-path
blev dessutom tydligt urtvättad. Baseline använder nu i stället:

```text
EUTHERDRIVE_GAUNTDL_EXPERIMENT_VOODOO_TEXTURE_ALPHA8_MASK=0
EUTHERDRIVE_GAUNTDL_EXPERIMENT_VOODOO_FBZ_COLORPATH_RGB_COMBINE=1
```

På f600 -> f601 ger den hårdvarutrogna kombinationen
`frameHash=0x83db79e1`, `colored=7808` och samma 314 texturtrianglar/11304
covered pixels som före färgsteget. Nolltexlarna skrivs nu svart enligt den
avstängda alpha/blend-konfigurationen. Nästa checkpoint sparas som en separat
repo-lokal f601-snapshot så att färg- och placeringsarbetet kan fortsätta utan
att skriva över det äldre f600-oraklet.

Snapshot-checkpointen är nu skapad och reload-verifierad:

```text
artifacts/gauntlet-probe/gauntdl-fixed-colorpath-f601-200k.warm
size 77 MB
sha256 55c9f8d0d53cbd1a63d1be1eaebb992cf035e6f528e062b7aba1d07cec23ff29
reload frameCounter=601 ranFrames=0 frameHash=0x83db79e1 colored=7808
```

Den tillhörande framebufferdumpen är
`artifacts/gauntlet-probe/gauntdl-fixed-colorpath-f601.ppm`, SHA-256
`34749f1a1fa0a95d6676268c8f368a8fb0a966b626dad834ce057db4fd666d2c`.

### Setup-gradienternas determinanttecken återställer glyphrektanglarna

Ett fokuserat spår av det första 8x9-tecknets två trianglar visade att
skärmgeometrin och atlasrutans hörn redan var konsekventa: skärmrektangeln
`(200,310)-(208,319)` motsvarar S/T-rektangeln `(48,90)-(64,108)`, vilken vid
LOD 1 ska bli 8x9 texlar. De två triangelhalvorna läste ändå helt olika
adressområden (`0x006027..0x0063a7` respektive `0x0056b0..0x005a30`).

Orsaken var ett rent teckenfel. `Edge()` använder motsatt determinantorientering
mot gradienternas numerator, men setup använde `1 / area`. Därför blev
`dS/dX=-2` och `dT/dY=+2` när hörnen kräver `+2` respektive `-2`. Med
`setupDivisor=-1 / area` läser samma två halvor de överlappande intervallen
`0x005ba7..0x005f27` och `0x005c2e..0x005fae`.

Den visuella skillnaden är stor: diagonalt/repetitivt atlasbrus ersätts av
separata 8x9-glypher och tunna, delvis läsbara textlinjer. Ren f600 -> f601
ger nu:

```text
frameHash=0x0131d2c9
textured=314 triangles, 11304 pixels, 6339 zero texels
colored=7300
PPM sha256=88a0e787cbb85207b272a2407c2375141e9697ec1c20bc46678e969b3ec37368
```

En ny post-fix checkpoint är skapad utan att skriva över tidigare snapshots:

```text
artifacts/gauntlet-probe/gauntdl-gradient-sign-f601-200k.warm
size 77 MB
sha256 89b8b3c45d07c4e9361d74259be94ed2e3f14c0aad780cf348a9464be81f7987
reload frameCounter=601 ranFrames=0 frameHash=0x0131d2c9 colored=7300
```

Texten är ännu inte helt läsbar och scenen är fortfarande ofullständig, men
gradienttecknet är den första ändringen som återställer sammanhängande
glyphformer snarare än bara ändrar färg eller sampletäthet. Nästa gräns är de
kvarvarande atlasrutorna/färgvalen och varför flera förväntade scenelement
inte ritas vid denna checkpoint.

### Den historiska `+0x510`-biasen dolde gradientfelet

Sample-basbiasen `0x510` infördes långt innan LOD, fixed-fetch och
setup-gradienter var korrekta. Efter determinantfixen kördes därför en ny ren
f601-matris med bias `0`, `0x100`, `0x400` och `0x510`.

```text
bias 0x000  frameHash=0x7a22d82d zero=5106/11304 colored=8686
bias 0x100  frameHash=0xf845630e zero=5012/11304 colored=8407
bias 0x400  frameHash=0xc12f2660 zero=6768/11304 colored=6993
bias 0x510  frameHash=0x0131d2c9 zero=6339/11304 colored=7300
```

Bildjämförelsen är entydig: bias 0 ger sammanhängande tecken och flera
urskiljbara ord/rader, medan `0x400` och `0x510` hugger sönder samma glypher.
Den gamla offseten kompenserade delvis för de inverterade gradienterna men är
fel när koordinatkedjan är korrekt. Baseline sätter nu sample-basbias till
noll. Den visuella referensen är
`artifacts/gauntlet-probe/gauntdl-gradient-bias0-f601.png`; den byte-stabila
PPM-källan har SHA-256
`c4c100124cb5515ca91400e74ad1063954bc464f043cbd296be1c277511ead04`.

Den nya bias-0-checkpointen är också sparad och reload-verifierad:

```text
artifacts/gauntlet-probe/gauntdl-gradient-bias0-f601-200k.warm
sha256 c81352d0d17e2a0a7542e815f153e3aa4d025077b3b6fc1a86ab0c387fb94450
reload frameCounter=601 ranFrames=0 frameHash=0x7a22d82d colored=8686
```

Den 22 juli hittades en senare runner-drift: adapter-defaulten var fortfarande
`0`, men `run-gauntdl-baseline.sh` hade återgått till `0x510`. En isolerad
f771 -> f780-matris reproducerade felet. `0x510` gav hash `0x94f513a3` och
22 945 färgpixlar, men merparten av skillnaden bestod av falska horisontella
linjer. Bias `0` gav hash `0xb11fe479`, 11 948 färgpixlar och tog bort dessa
linjer utan att ta bort de underliggande UI-/face-drawsen. Den kanoniska
runnern sätter nu åter default `0`.

### T-origin och 8-bitars lanes gör texten läsbar

Den bias-0-bilden visade teckenformer men de såg fortfarande upp-och-ner och
spegelvända ut. Vertexspåret för den första glyphen bekräftade att skärmens Y
ökar medan T minskar. `TEXTURE_T_ORIGIN_FLIP=1` vänder därför texelraden till
rätt skärmorientering. Den återstående speglingen låg inte över hela atlasen
utan i varje 32-bitars A8-ord; fyra byte lästes i motsatt lane-ordning.

Den rena kombinationen är:

```text
EUTHERDRIVE_GAUNTDL_FIX_VOODOO_TEXTURE_T_ORIGIN_FLIP=1
EUTHERDRIVE_GAUNTDL_EXPERIMENT_VOODOO_8BIT_TEXTURE_SAMPLE_REVERSE_LANES=1
```

På f600 -> f601 ger den `frameHash=0xe23a380e`, `zero=3719/11304` och
`colored=9571`. Viktigare än räknarna är att framebufferdumpen nu innehåller
läsbara strängar som `DIAGNOSTIC MODE`, `NORMAL`, `ALPHA`, `ATTACHED OBJECTS`
och `NODE`. Det verifierar både vertikal orientering och byte-laneordning med
faktiskt glyphinnehåll. Båda inställningarna är promoterade till baseline.

Den visuella filen och den reload-verifierade checkpointen är:

```text
artifacts/gauntlet-probe/gauntdl-readable-text-f601.png
artifacts/gauntlet-probe/gauntdl-readable-text-f601.ppm
PPM sha256 20907f3b550170cab9fdfd55ad4e8bba2815ac37be94cd5e6dc2146941296efc

artifacts/gauntlet-probe/gauntdl-readable-text-f601-200k.warm
snapshot sha256 0d2a650ce3223682efa5f9ff5249d0ede2ad8ac053aa153bd9fedd90ca9f100d
reload frameCounter=601 ranFrames=0 frameHash=0xe23a380e colored=9571
```

### FIRE 3 startar nya uploads och exponerar nästa formatfel

Den läsbara f601-checkpointen kördes vidare i repo-lokala tiobildrutesteg. Vid
f700 stod diagnostikmenyn kvar (`frameHash=0x3f727c21`, 404 swaps och 578027
texturord). Korta pulser på TEST, START och COIN gav identiska förlopp.
MAME:s Vegas-inputtabell visar däremot att FIRE 3 är `BUTTON3`/Magic, inte
Turbo. En hållen MAGIC/B-puls f700 -> f710 gav omedelbart ett nytt förlopp:
swaps ökade 404 -> 468, 131196 nya Type 5-texturord skrevs och PC flyttade
till `0x800a725c`. Gästloggen bekräftar samtidigt strängen
`Exit menu (FIRE 3)`.

Den fortsatta kedjan är sparad som PNG/PPM och `.warm`-filer. Vid f740 syntes
en ny, till synes igenkännbar scen med 738 texturtrianglar, men stora delar
var fortfarande regnbågsrandiga. Senare TMU-banktest visar att scenintrycket
byggde på cross-TMU-aliasering och alltså inte är ett korrekt slutresultat.
f740-checkpointen utan nya formatprober är:

```text
artifacts/gauntlet-probe/gauntdl-post-diagnostic-f740.png
artifacts/gauntlet-probe/gauntdl-post-diagnostic-f740-200k.warm
frameHash=0x643d4f30
snapshot sha256 f6d325e3e0c9a9c0b6703eb9e623ef4bf12a1164116dbe7fe0263fb5aa1eef9e
PPM sha256 9c7ddb49458acf0c21b7ef79273cc93b24cc67770e03714c806705dbb856669c
```

Type 3-spåret f730 -> f740 delar renderingen i två tydliga grupper: 732
A8-glyphtrianglar med `mode/lod/base=8C2412CF/00302104/FFFFF800`, och sex
helskärmstrianglar med `80000009/FF802000/00000000`. De senare täcker
512x384 och använder S/T-området 0..256. Samma råa bas-0-textur blir en
sammanhängande mörklila 3D-scen när dess byte tolkas som RGB565 i stället för
det RGB332-format som textureMode-fältet för närvarande anger.

Format-override-proben gick tidigare bara genom float-samplern. Den går nu
genom en gemensam `ResolveTextureSampleFormat` även för den aktiva
MAME-fixed-samplern och dess spårsammanfattningar. En ny, default-avstängd
probe begränsar RGB565-tolkningen till exakt helskärmssignaturen ovan:

```text
EUTHERDRIVE_GAUNTDL_EXPERIMENT_VOODOO_RGB332_FULLRECT_AS_RGB565=1
```

Ren f730 -> f740 A/B verifierar både förbättringen och att baseline är orörd:

```text
flag off  frameHash=0x643d4f30
flag on   frameHash=0x793e695c

artifacts/gauntlet-probe/gauntdl-post-diagnostic-f740-rgb332-as-rgb565.png
PPM sha256 6eaa35f96590c84c7fa6cae5405585d24a218c7f89aa0bef93fe22b623a77951

artifacts/gauntlet-probe/gauntdl-post-diagnostic-f740-rgb332-as-rgb565-200k.warm
snapshot sha256 d4b091b1632e89e2d17a266cf2e9db77585362f1a200268f15e2ff1b5f409a9f
reload frameCounter=740 ranFrames=0 frameHash=0x793e695c
```

Proben håller även f740 -> f750: den sammanhängande övre 3D-scenen ligger
kvar samtidigt som ytterligare glyphgeometri ritas. Nästa isolerade fel är
den vita/brusiga nederhalvan, inte längre hela scenens texelformat:

```text
artifacts/gauntlet-probe/gauntdl-post-diagnostic-f750-rgb332-as-rgb565.png
PPM sha256 c3054b6533c4580cd6b5afc0ebb860d9858188c8a321d7ff4d44e84a7aa1968e

artifacts/gauntlet-probe/gauntdl-post-diagnostic-f750-rgb332-as-rgb565-200k.warm
snapshot sha256 83774c741a14ee12006f248bfee09daf433c76b0b509ce831601d42c86793433
```

Regeln är avsiktligt inte promoterad till baseline ännu. Nästa steg är att
spåra varför gästens helskärmspaket bär format 0 trots att bas-0-innehållet är
RGB565, och separat klassificera drawsen som producerar den korrupta
nederhalvan.

### Separata 4 MiB-TMU-banker bevarar fontatlasen

Rå textur-RAM vid f700 och f740 gav den avgörande jämförelsen. Vid f700 är
området runt `0x4500` en binär A8-atlas med huvudsakligen `00`/`ff`. Efter
FIRE3-uploaden innehåller samma fysiska adresser RGB565-liknande ord som
`59ae 49ce 736d ...`. A8-fontens bas ligger nära slutet och wrappar till detta
område, vilket förklarar att menytexten började läsa scenbytes som glypher.

MAME:s Vegas-konfiguration anger Voodoo 2 med två TMU:er och 4 MiB RAM per
TMU. En ren omkörning från den läsbara f700-checkpointen använde därför båda
de redan befintliga proberna genom hela FIRE3-kedjan:

```text
EUTHERDRIVE_GAUNTDL_EXPERIMENT_VOODOO_TEXTURE_UPLOAD_TMU_BANKS=1
EUTHERDRIVE_GAUNTDL_EXPERIMENT_VOODOO_SEPARATE_TMU_TEXTURE_MEMORY=1
```

Vid f740 är resultatet en sammanhängande diagnostiksida i stället för en
korrupt RGB/A8-blandning. Strängar som `DIAGNOSTIC MODE`, `VERSION`, `NORMAL`
och flera tabellrader går åter att urskilja. Den andra MAGIC-pulsen f740 ->
f750 lämnar sidan kvar, så den första pulsen startade grafikuppladdningar men
stängde inte menyn; den mörklila RGB565-tolkningen var ett aliaseringsartefakt.

```text
artifacts/gauntlet-probe/gauntdl-separated-tmu-f740.png
frameHash=0x4ee29f95
PPM sha256 e7de2dcf6bff7aa0e30c16e1ef9950fc53292a94552e653f55eba8c391320ac3

artifacts/gauntlet-probe/gauntdl-separated-tmu-f740-200k.warm
snapshot sha256 e05d15b764730585f0a50500b87d36861abd2e8c0212abeac2dfbdc7a3bb4061

artifacts/gauntlet-probe/gauntdl-separated-tmu-f741-both.png
frameHash=0x072e3349
PPM sha256 cefc0f4f3e7be2ccad96a493885895a3568d844316ae0baff8f976b8285dc4ab
```

Bankreglerna lämnar den äldre läsbarhetsreferensen f600 -> f601 exakt
oförändrad: `frameHash=0xe23a380e`, 314 täckta texturtrianglar och 3719
nolltexlar. Eftersom de både matchar den verkliga hårdvarutopologin, bevarar
den gamla referensen och tar bort ett uppmätt cross-TMU-fel är de nu
promoterade till baseline. RGB332-as-RGB565-regeln förblir default-off.

### FIRE 3 lämnar diagnostikmenyn via dess gästlatch

Den tidigare slutsatsen att en MAGIC- eller TURBO-puls i sig startade nästa
renderförlopp var fel: en ny kontrollkörning utan input gav exakt samma f710-
hash `0x2e22fdd3`, 404 swaps och 578027 texturwrites. Den verkliga
inputkedjan verifierades i stället genom en repo-lokal 32 MiB RAM-dump och
MIPS-disassemblering. Proben kan nu skriva dumpen med
`EUTHERDRIVE_GAUNTDL_DUMP_MAIN_RAM=/path/to/mainram.bin`.

Gästfunktionen vid `0x80019b9c` producerar de normaliserade current-, latch-
och edge-fälten. En sen FIRE 3-puls gav `0x0800` i alla tre aggregaten, men
diagnostikrenderaren lämnade ändå inte state `0x8000`. CPU-tracen genom
`0x8008458c..0x80084a68` visade orsaken: menyobjektet som normalt förmedlar
knappen saknas (`s6=0`) och kontrollen vid `0x80084a50` returnerar noll.
Det stämmer med de redan loggade null-render-recorden och är inte ett fel i
den råa IO- eller normaliseringskedjan.

Baseline innehåller därför nu
`EUTHERDRIVE_GAUNTDL_FIX_RUNTIME_DIAGNOSTIC_EXIT_BRIDGE=1`. Fixen är strikt
begränsad till huvudstate `0x8000`; när den normaliserade FIRE 3-biten syns
sätter den spelets eget latch `0x80227ec8`. Gästkoden utför därefter sin
ordinarie transition. Den tillfälliga proben som vände spelarposternas
ordning togs bort.

En ren baseline-körning från f700 verifierar övergången utan extra flaggor:

```text
input press/release: f708/f711
f712 main state: 0x8007
f712 frameHash: 0xc2b9fd15
f712 swaps: 444

artifacts/gauntlet-probe/gauntdl-baseline-diagnostic-exit-f712.ppm
PPM sha256 6814a3ad39084678abe98e1fdd3744fcff7e249c586c8038d8086a95eb4f6bfa
artifacts/gauntlet-probe/gauntdl-baseline-diagnostic-exit-f712-200k.warm
snapshot sha256 c0a61238fa7b52d4603459efb7408b621d9b809ee196749e790f5b5ac9912fca
```

Vid f720 visar PNG:n nästa `DIAGNOSTIC MODE`-sida. Vid f740 är bilden
oförändrad men gästförloppet fortsätter: 856 swaps, 933767 texturwrites och
nya spelarassets når `BGLoadModel`. Nästa isolerade blockerare är att denna
caller-kedja anländer med `key=<empty>` trots att assettabellen innehåller
bland annat `players/sel_lr/yel` och `players/dwf/sfxyel`.

```text
artifacts/gauntlet-probe/gauntdl-diagnostic-exit-bridge-f720.png
artifacts/gauntlet-probe/gauntdl-diagnostic-exit-bridge-f720-200k.warm
snapshot sha256 c15e5385c8d33064340ab99f4c522e3dd9f5b5eae12d1f8bdb2ab8d17fe9b676

artifacts/gauntlet-probe/gauntdl-post-diagnostic-exit-f740.png
artifacts/gauntlet-probe/gauntdl-post-diagnostic-exit-f740-200k.warm
snapshot sha256 df94868c6d0f9fae0484fe52ab00e13e7339027f70154b70fbd0d5e8f2ca5121
```

### Asset-sökningen når den laddade high-score-tabellen

`key=<empty>` vid f740 var inte nästa blockerare. En fortsatt baseline-körning
till f760 laddar fler records och producerar riktiga lookup-nycklar, bland
annat `WAR_FACE_HS` och `VAL_FACE_HS`. En RAM-dump vid f759 visar samtidigt
att båda namnen redan finns i den hydrerade texturtabellen vid `0x805b1d10`.

Två fel i den lokala known-missing-fastpathen dolde tabellen. Efter den första
lookup-missen hoppade fastpathen över även återstående descriptors med
icke-noll count. Dessutom låg `hiscore/legends` färdigladdad i slot 16 med 26
texturer medan gästens sökhögvatten fortfarande var `count=16`, så den
ordinarie `< count`-loopen slutade precis före rätt slot.

Fastpathen hoppar nu bara över en faktiskt tom återstod. För en icke-tom
nyckel utökas sökhögvattnet med en slot endast när slot `count` bevisligen har
en giltig RAM-pekare, icke-noll texture count och ett namn. Den rena
f759 -> f760-körningen loggar därefter:

```text
bgloadmodel-asset-search-count-extend key=WAR_FACE_HS count=16->17 asset=hiscore/legends
frameHash=0xb3d8eb1a
```

Vid f762 är ändringen stabil och den nedre menydelen innehåller åter läsbara
rader som `DIAGNOSTIC MODE` och `CREATE TEXTURES`. Den kvarvarande stora
atlasmattan kommer från separata format-2 font/panel-draws; face-recordets
32x32-textur använder redan korrekt LOD3-offset. En 16-bitars byte-swap-probe
gav identisk framebufferhash och togs bort.

```text
artifacts/gauntlet-probe/gauntdl-f762-asset-count-extend.png
frameHash=0x03ff9d41
PPM sha256 cb46a0d36b44d15c028d0d5fc33b3748aa508194530c68aae06ddfdfc40cd77a
artifacts/gauntlet-probe/gauntdl-f762-asset-count-extend-200k.warm
snapshot sha256 848269e1dce4762d3235f7d295d03873f36b82e29ba536e424097e3c752d0300
```

### Standard-FIFO:n väntar nu på kroppen till ett publicerat huvud

Den kvarvarande 128x128-atlasmattan var inte ett descriptor-, lookup- eller
gäst-builderfel. Descriptor `0x802592b0` slog upp en giltig record 0 vid
`0x802e26d4`, och Type-4-buildern vid `0x800bd100` skrev korrekt
`mode/lod/base=8c2412cf/00002104/ffffe000` efter headern `00059604`.

Felet låg i standard command-FIFO-readiness. När headern vid logisk adress
`0x015ceb0c` hade publicerats men payloadorden fortfarande skrevs kunde
`IsCommandFifoPacketReady()` resynka framåt till ett senare komplett paket.
Det övergav record-0-staten innan dess kropp blev giltig. Read-head väntar nu
i stället när den aktuella slotten är en giltig header i rätt logiska
generation; resync behålls för slots som inte kan vara generationens huvud.

Den rena f759 -> f760-körningen verifierar både orsak och effekt:

```text
frameHash=0xcdaed1a0
swaps=936
packet-3=143446
framebuffer colored=14656

glyph packet 0x015ce870: base fffff800 når TMU0
record-0 packet 0x015ceb0c: base ffffe000 når TMU0
```

Den tidigare helskärms-atlasmattan försvinner visuellt och den läsbara
text-/spritevägen överlever. TMU-spåret visar dessutom upprepade kompletta
`00059604`-paket med `ffffe000`, medan glyphpaketen fortfarande publicerar
`fffff800`.

Körningen fortsätter utan FIFO-stopp genom f762 och f770:

```text
f762 frameHash=0x9aca0a83 swaps=944 textured=682
f770 frameHash=0x9aca0a83 swaps=952 textured=1356
f770 textureMap writes=189932 touched=46681
```

f762 och f770 har identisk framebuffer-SHA-256
`43d2d7982c5c4478aec3593c4445c1c90883a0c90c63e4a8d1b910044fd8c8d6`,
trots fortsatt gäst-, FIFO- och texturuploadprogress. FIFO-fixen är därmed en
separat stabil checkpoint; nästa blockerare är den kvarvarande vita
bakgrunden och de små korrupta panel-/glyphdrawsen, inte längre den stora
record-0-atlasmattan.

```text
artifacts/gauntlet-probe/gauntdl-f760-fifo-head-wait.png
artifacts/gauntlet-probe/gauntdl-f762-fifo-head-wait.png
artifacts/gauntlet-probe/gauntdl-f770-fifo-head-wait.png

artifacts/gauntlet-probe/gauntdl-f762-fifo-head-wait-200k.warm
snapshot sha256 164100e7cedd4d9f7410abf0c61546ad4c42e5e802966304bd85d2e96f636213
artifacts/gauntlet-probe/gauntdl-f770-fifo-head-wait-200k.warm
snapshot sha256 9853a1afcb3553a15b1e96b41ac54f68a7e2b04c9673b99d0796e5774a52eebf
```

Två default-avstängda kontrollprober avgränsar nästa fel ytterligare. Format
2-drawsen är A8 och record-0-familjen samplar nästan bara nollor (typiskt
`8250/8256` pixlar per triangel). A8-maskningen tar därför bort de falska
horisontella linjerna och håller genom f770, men den exponerar ingen saknad
scen:

```text
EUTHERDRIVE_GAUNTDL_EXPERIMENT_VOODOO_TEXTURE_ALPHA8_MASK=1
f762/f770 frameHash=0xa4b14168
f770 swaps=952
```

En andra probe undertryckte den enda vita fast-fillen efter raster. Den tog
inte fram spelgrafik utan blottade den gamla atlasmattan under det färgade
A8-UI:t:

```text
EUTHERDRIVE_GAUNTDL_EXPERIMENT_VOODOO_SUPPRESS_WHITE_FASTFILL_AFTER_RASTER=1
f762 frameHash=0xa7b05b48
white pixels=120036
colored pixels=167125
```

Den vita fillen är alltså en slutlig rensning av en backbuffer som ännu inte
har ersatts av en scen, inte orsaken till att en redan ritad scen försvinner.
Varken A8-maskningen eller fill-undertryckningen promoterades. Nästa probe ska
i stället binda de nya Type-5-uploadsen mellan f762 och f770 till den första
efterföljande scen-Type-3-drawen, eller bevisa att gästen aldrig publicerar en
sådan draw.
