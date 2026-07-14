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
