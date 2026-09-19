# Darius Gaiden: billigare diagnostik på Linux

Utgångspunkt `3a3a2600`. Användaren upplever spelet som nästan spelbart.
Den gamla F3-handoffens uppgift om fastnad boot beskriver inte aktuell kod:
coin/start-prov når synlig gameplay, verifierat i den sparade bilden.

## Orsak och ändring

CPU-sampling under 1800 rutor med adaptiv rendering avstängd visade cirka
36 % exklusiv stackvikt i huvudbussens `TryWriteByte` och 18 % i
`Environment.GetEnvironmentVariableCore`. Profilen avgränsades till
`DariusGaidenAdapter.RunFrame`; uppstart ingår inte i dessa andelar.
Artefakt: `.build-tmp/darius-profile.speedscope.json`.

Två diagnostikkostnader minskas utan att ändra emulerade cykler:

1. `TrackTaskFrameWrite` sökte bland observerade stackar och läste 32
   stackpekare vid varje work-RAM-skrivning. Båda sökvägarna accepterar bara
   stackar i `[0x402000,0x4066b4)`, vars bevakade ram är byte stack+60..67.
   Efter kanonisering av RAM-spegeln kan adresser utanför
   `[0x40203c,0x4066f7)` därför avvisas direkt. Sökordning och diagnostik
   inom intervallet är oförändrade; ojusterade stackvärden tillåts fortsatt.
2. Gemensamma M68000-kärnans `TRACE_STACK` och `TRACE_JUMP` läses en gång
   vid typinitialisering, liksom dess övriga spårningsflaggor. Tidigare
   gjordes miljöuppslag vid varje push/pop/jump även med spårning avstängd.
   Sätt miljövariablerna före CPU-start; dynamisk ändring mitt i processen
   är inte längre stödd för just dessa två flaggor.

Ljud-CPU, ljudmixning, renderer, CPU-budgetar och adaptiv pacing ändras inte.

## Reproducerbart gameplay-prov

Ny `tools/DariusProbe` kör från kallstart, coin på ruta 600–604, start på
650–654 och skjutning från 700. Standardlängd 1800 rutor. Tid mäts enbart
runt `RunFrame`; hashing ligger utanför. Spelpartiets mått avser 900–1799.
Hela bildföljden och allt ljud SHA256-hashas, liksom hela sparade slutstate.
Ingen byte maskeras. Sparformatet är befintligt format 13, inte en dump av
alla privata/härledda fält.

```sh
dotnet build tools/DariusProbe/DariusProbe.csproj -c Release
env EUTHERDRIVE_DARIUSG_ADAPTIVE_RENDER=0 \
 dotnet tools/DariusProbe/bin/Release/net8.0/DariusProbe.dll \
 /home/nichlas/roms/MAME/TAITO/dariusg.zip 1800
```

Normal .NET-tiering, adaptiv rendering av för exakt lika antal ritade rutor.
Inga samtidiga egna byggen/profilerare under mätning. Ordning gammal/ny/
ny/gammal/gammal/ny/ny/gammal. Fryst originalbuild:
`.build-tmp/darius-frozen-baseline/`.

| Par | Original, spelparti ms | Fix, spelparti ms | Original över budget | Fix över budget |
|---|---:|---:|---:|---:|
| 1 | 14477,624 | 11324,932 | 251 | 0 |
| 2 | 14348,697 | 11586,910 | 205 | 6 |
| 3 | 14346,880 | 11375,564 | 232 | 0 |
| 4 | 14450,002 | 11433,053 | 218 | 6 |

Spelpartiets medel: **16,006→12,700 ms/ruta**, **20,66 % kortare tid**.
Alla fyra par snabbare. Budgeten är 16,965 ms (58,944 Hz); totalt
**906→12 rutor över budget av 3600**. Hela körningens medel inklusive
boot/övergång: 19723,938→15753,662 ms.

Alla åtta körningar matchar samtliga tre kontrollsummor:

- Bildföljd: `80B3EE26F379CB6FECEFB204A5D4A9A93C0DD7D6397ADD239008340179FD72EE`.
- Ljud: `AABEA9CB5665865280DB9363C352CD24EFCC13A4BCB3AE83E700958F910585B5`.
- Slutstate: `617E2E66357B1AD6A6056BAF068D7A605622DECC65AA62C6A01DA3C55DCE0EAD`.

Lokala loggar: `.build-tmp/darius-ab.log`, `darius-ab-{1..8}-{old,new}.log`.
Den automatiska sekvensen spelar bara ett begränsat parti; detta bevisar
inte att alla banor, grafikdetaljer, ljud eller interaktiv UI-pacing är korrekta.

## Regressionstester

```sh
dotnet tools/DariusProbe/bin/Release/net8.0/DariusProbe.dll --check-task-frame-filter
env EUTHERDRIVE_M68K_TRACE_STACK=1 EUTHERDRIVE_M68K_TRACE_JUMP=1 \
 dotnet tools/DariusProbe/bin/Release/net8.0/DariusProbe.dll --check-m68k-trace
```

RAM-testet kontrollerar 72832 adresser/scenarier mot den gamla sökningen,
inklusive båda RAM-speglarna, intervallgränser, ogiltiga/ojusterade stackar,
observerade stackars prioritet och samtliga åtta diagnostikfält.
Trace-testet kör JMP/JSR/LINK/UNLK/RTS i gemensamma M68000-kärnan och
kontrollerar retur-PC, stack, register och spårutskrifter med flaggor av/på.
RTS har en redan befintlig direkt fast path, så UNLK används för pop-spåret.

Alla fyra kombinationer av trace-flaggorna passerar, liksom RAM-filtret.
Probe, Headless och Linux UI bygger i Release utan fel; UI har 33 befintliga
varningar. `git diff --check` passerar. Befintliga Gauntlet/Lua-ändringar
och lokala snapshots/artefakter lämnas orörda och utanför commit.

## Slutprov med vanliga inställningar

Uppdaterad Headless, adaptiv rendering med sitt vanliga standardvärde,
samma inputscript och 1800 rutor: gameplay nås, ingen CPU-fault rapporteras.
Hela körningen inklusive boot har 9,230 ms/ruta i medel och 61/1800 rutor
över budget, varav fyra över 25 ms. En uppstartsspik på ruta 5 är 76,201 ms.
Detta är ett separat smoke-prov, inte samma mätfönster som A/B-tabellens
spelparti. Lokal logg: `.build-tmp/darius-final-default.log`; bild:
`.build-tmp/darius-final-default/headless_output.ppm`.

Starta det byggda Linux-UI:t från reporoten för praktiskt test:

```sh
dotnet run --project EutherDrive.UI -c Release --no-build -- /home/nichlas/roms/MAME/TAITO/dariusg.zip
```

Ingen interaktiv UI-/högtalarlyssning har verifierats i detta pass. Det
återstår att bekräfta användarens upplevda flyt och eventuella separata
grafik-/ljudfel. Ändringen kräver ingen ny spelinställning eller frameskip.
