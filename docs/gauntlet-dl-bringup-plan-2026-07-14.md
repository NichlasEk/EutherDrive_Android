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

Nästa mätpunkt är nu smalare än den ursprungliga planen: för den request-ägda
state-7-descriptorn runt `0x802e2158..0x802e21a8`, följ post-read/parsern till
den texelkropp som borde materialiseras innan 256-paketsuploaden. Klassificera
de aktiva sample-sidorna separat som aldrig skrivna respektive skrivna av
`0x802e2c68`-familjen. Ändra inte Type5-adressering eller TMU-fetch innan den
saknade parser-/body-kedjan antingen är bevisad eller utesluten.
