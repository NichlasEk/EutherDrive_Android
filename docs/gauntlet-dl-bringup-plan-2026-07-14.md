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
