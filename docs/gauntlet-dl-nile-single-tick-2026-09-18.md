# Gauntlet DL: profil med aktiva timers och single-tick-försök

Linux CPU-arbete ovanpå `a6bcd536`. Ingen Android-körning.

## Ny profil

CPU-sampling med korrekt timeråterställning, replay 6750→7950 och 90000
steg. Samma cache4096/FIFO-flaggor som i Nile-rapporten, tiering av,
NCC/GPU av. Huvudtrådens replay-stack, inklusive väntetid:

| Grupp | Andel |
|---|---:|
| MIPS exklusive synliga klock-/enhetsanrop | 39,83 % |
| Texturraster inklusive väntan | 31,06 % |
| Övrig Voodoo/FIFO/presentation | 14,11 % |
| Synlig CP0/Nile-klocka | 11,40 % |
| Övrigt | 3,60 % |

`AdvanceNileClock` har 10,98 % exklusiv stackvikt. Det motiverar ett
ytterligare klockförsök. Andelarna är inte summerade CPU-cykler över
rendertrådarna. Profileringens tid används inte som benchmark.
Slutdumpen matchar det korrigerade fe8b-oraklet exakt.

Lokala artefakter: `.build-tmp/cpu-after-nile-1.nettrace`,
`.build-tmp/cpu-after-nile-1.speedscope.json` och
`.build-tmp/cpu-after-nile-1-final.warm.gz`.

## Förkastad single-tick-specialisering

Specialisera `timerTicks == 1` utan division. Behåll period-1-fallet
(reload noll), räknare utanför reload-intervallet och normaliseringen
`next == period`. För övriga tickvärden behövs inte modulo vid wrap:
`decrement < period` och `counter < decrement` ger
`0 < decrement - counter < period`.

Ingen aggregering av CPU-klockan och ingen ändring av IRQ/watchdog.
Differentialtestet utökas med `counter = unchecked(reload + 2)`, som
träffar normaliseringen efter subtraktion även för ogiltiga räknarvärden.
30740 Nile-kontroller, 2528 COP1-kontroller och 45 cachekontroller passerar.

Fryst referens: `.build-tmp/nile-single-baseline/` från `a6bcd536`.
Mätningar körs separat från bygge/profilering och kräver exakt SHA256 för
hela den dekomprimerade slutdumpen; inga bytes maskeras.

| Fönster | Referens, ms | Single-tick, ms |
|---|---|---|
| 6750→7950 | 10976,5 / 10810,5 / 10954,2 / 10827,9 | 10789,0 / 10889,0 / 10740,9 / 10714,1 |
| 7950→9150 | 11106,7 / 10976,1 | 11224,2 / 11237,0 |

Första fönstret: 1,00 % kortare medeltid, 1,16 % kortare median, 3/4
vunna par. Nästa fönster: 1,71 % längre medeltid, 0/2 vunna par.
Alla 12 fullständiga slutdumpar matchar respektive korrigerat orakel.
Specialiseringen tas bort eftersom vinsten inte håller över båda fönstren.
Lokala loggar: `.build-tmp/nile-single-bench.log` och
`.build-tmp/nile-single-next-bench.log`; tillhörande numrerade loggar och
slutdumpar har samma prefix.

## Separat försök: enbart överflödig modulo

Återgå till samma kontrollflöde som `a6bcd536` och ta bara bort den andra
modulooperationen, med olikhetsbeviset ovan. Behåll det utökade testet.

| Fönster | Referens, ms | Enbart modulo borttagen, ms |
|---|---|---|
| 6750→7950 | 10956,7 / 11283,9 / 11024,7 / 10785,7 | 10903,2 / 11215,6 / 10871,5 / 10809,0 |
| 7950→9150 | 10976,9 / 10946,3 | 10925,3 / 10968,6 |

Första fönstret: medel 11012,750→10949,825 ms (**0,57 % kortare**),
median 10990,70→10887,35 ms (0,94 % kortare), 3/4 vunna par.
Nästa fönster: medel 10961,60→10946,95 ms (0,13 % kortare), 1/2 vunna
par, alltså i praktiken neutralt. Detta är inte bevis för en generell
prestandavinst. Den lilla algebraiska förenklingen behålls utan att införa
ytterligare förgreningar; single-tick-specialiseringen behålls inte.

Alla 12 slutdumpar matchar exakt:

- 6750→7950: `fe8b7adabe915e51785414d2c1f6e3a5ef836b9159d14062bbf7f8e2dd1fd7ce`.
- 7950→9150: `469865d377c99469b1133a9067435593754cde7dfa2f00e4c3770efd99ecba5a`.

Loggar: `.build-tmp/nile-remainder-bench.log`,
`.build-tmp/nile-remainder-next-bench.log` och tillhörande numrerade
loggar/slutdumpar. Första fönstrets ordning är gammal/ny/ny/gammal/
gammal/ny/ny/gammal; nästa är gammal/ny/ny/gammal. Samma flaggor och
indata som [Nile-rapportens reproduktion](gauntlet-dl-nile-clock-2026-09-18.md).
För andra fönstret används `.build-tmp/nile-restored-first.warm.gz`,
start 7950 och mål 9150. Referens-DLL väljs med
`EUTHERDRIVE_GAUNTDL_PROBE_DLL=.build-tmp/nile-single-baseline/GauntletProbe.dll`.

Slutkandidatens testloggar: `.build-tmp/nile-remainder-checks.log`,
`nile-remainder-cop1-checks.log`, `nile-remainder-cache-checks.log`.
30740 timer-/snapshotfall, 2528 COP1-fall och 45 cachefall passerar.
Release Probe och UI bygger utan fel. Byggloggar:
`.build-tmp/nile-remainder-build.log` och `nile-remainder-ui-build.log`.
`git diff --check` passerar. Befintliga Lua-ändringar och lokala artefakter
lämnas utanför checkpointen.

## Nästa riktning

Timerarbetet är nu korrekt aktiverat och enklare, men ytterligare små
timergrenar gav ingen stabil vinst. Prioritera den breda MIPS-vägen:
`Step`, safe-block-loopen och instruktionsexekveringen dominerar tillsammans
profilen. Behåll exakta klockanrop per instruktion och fulla replay-orakel.
41 swap-kommandon på ungefär 11 sekunder är fortfarande långt från spelbart.
