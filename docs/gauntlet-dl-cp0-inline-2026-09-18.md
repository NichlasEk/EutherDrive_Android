# Gauntlet DL: CP0-inlining utifrån genererad maskinkod

Linux CPU-försök ovanpå `cbc43991`. Ingen Android-körning.

## Maskinkod före och efter

Samla x64-maskinkoden för `TryRunRuntimeSafeInstructionBatch` med
`DOTNET_TieredCompilation=0`,
`COMPlus_JitDisasm=EutherDrive.Core.Arcade.Vegas.MipsR5000Core:TryRunRuntimeSafeInstructionBatch`
och `COMPlus_JitStdOutFile=<ny lokal asm-fil>` över kort warm replay
6750→6760, 90000 steg. Cache4096 och FIFO-medlemskap på som i tidigare
rapporter. Gör inte disassembly-körning samtidigt med benchmark.

Referensen har ett separat `AdvanceCp0Count`-anrop per instruktion i
safe-block-loopen. Den lilla hjälparen anropar i sin tur `AdvanceNileClock`.
Kandidaten sätter `AggressiveInlining` på CP0-hjälparen utan att ändra
dess innehåll eller slå ihop klocksteg. Genererad kod bekräftar att
CP0-anropet försvinner och ersätts av direkt Nile-anrop plus CP0-logiken.
Hela batchmetoden växer från 1675 till 1805 maskinkodsbytes. Det finns
alltså en kodstorlekskostnad som måste vägas mot färre anrop.

Lokala maskinkodsfiler: `.build-tmp/dispatch-jit-baseline.asm` och
`.build-tmp/dispatch-jit-cp0-inline.asm`.
Fryst referensbuild från `cbc43991`: `.build-tmp/cp0-inline-baseline/`.

## Korrekthet

Nytt ROM-fritt test: `EUTHERDRIVE_GAUNTDL_TEST_CP0_CLOCK=1`.
900 kombinationer av Count/Compare, tidssteg från 0 till ulong-max,
fryst klocka och redan väntande timeravbrott. Hela CP0-registeruppsättningen,
pending-flaggan, Nile-register och Nile-IRQ jämförs med den ursprungliga
räknaralgoritmen och en separat Nile-instans. Alla fyra timers är aktiva.
Testet ersätter inte end-to-end-provet av de inlinade anroparna.

900 CP0-fall och 30740 Nile-fall passerar på kandidaten.
Loggar: `.build-tmp/cp0-inline-checks.log` och `cp0-inline-nile-checks.log`.

## Replay 6750→7950

90000 steg, cache4096/FIFO på, tiering/NCC/GPU av. Växlad ordning
gammal/ny/ny/gammal/gammal/ny/ny/gammal, inga egna samtidiga byggen eller
profilerare.

| Par | Referens, ms | Inlining, ms |
|---|---:|---:|
| 1 | 10650,1 | 10719,5 |
| 2 | 10883,6 | 10496,8 |
| 3 | 10773,4 | 10747,1 |
| 4 | 10766,9 | 10626,9 |

Tre vunna par av fyra. Alla åtta fullständiga slutdumpar matchar exakt
SHA256 `fe8b7adabe915e51785414d2c1f6e3a5ef836b9159d14062bbf7f8e2dd1fd7ce`.
Lokala artefakter: `.build-tmp/cp0-inline-bench.log` och
`.build-tmp/cp0-inline-{1..8}-{old,new}.{log,warm.gz}`.

Medel 10768,500→10647,575 ms, 1,12 % kortare. Median
10770,15→10673,20 ms, 0,90 % kortare.

Nästa fönster 7950→9150, från `.build-tmp/nile-restored-first.warm.gz`:

| Par | Referens, ms | Enbart inlining, ms |
|---|---:|---:|
| 1 | 10996,7 | 10607,6 |
| 2 | 10792,6 | 11296,5 |

Medel 10894,65→10952,05 ms, 0,53 % längre. En vinst och en förlust;
detta ger inte en stabil vinst över båda fönstren. Alla fyra slutdumpar
matchar `469865d377c99469b1133a9067435593754cde7dfa2f00e4c3770efd99ecba5a`.
Loggar: `.build-tmp/cp0-inline-next-bench.log` och numrerade artefakter.

## Kompakt CP0-variant

Maskinkoden visar ytterligare en kostnad i den inlinade hjälparen:
avståndet från 32-bitars Count till Compare beräknas via villkor och
64-bitars omslagsaritmetik. `unchecked(compare - oldCount)` ger samma
avstånd modulo 2^32, även när räknaren slår runt. Resultatet breddas till
ulong innan jämförelsen med delta, så stora tidssteg förlorar inga bitar.
Avstånd noll behåller sitt tidigare beteende. Testets referens behåller
den ursprungliga förgrenade aritmetiken.

Pröva detta tillsammans med inlining mot samma oförändrade `cbc43991`.

Maskinkoden för batchmetoden krymper till 1765 bytes; omslagsgrenen
ersätts av `sub eax, esi`. Artefakt:
`.build-tmp/dispatch-jit-cp0-compact.asm`.

## Kompakt variant, replay 6750→7950

| Par | Referens, ms | Kompakt CP0 + inlining, ms |
|---|---:|---:|
| 1 | 10927,0 | 10701,2 |
| 2 | 11007,3 | 10371,9 |
| 3 | 10742,8 | 10606,9 |
| 4 | 11030,9 | 10348,0 |
| Medel | 10927,0 | 10507,0 |
| Median | 10967,15 | 10489,40 |

Fyra vunna par: **3,84 % kortare medeltid, 4,36 % kortare median**.
Alla åtta fullständiga slutdumpar matchar fe8b-oraklet ovan, utan maskering.
Detta är resultatet för kombinationen, inte ett isolerat mått på enbart
subtraktionen. Samma parametrar/ordning som för första kandidaten.
Artefakter: `.build-tmp/cp0-compact-bench.log` och numrerade
`.build-tmp/cp0-compact-{1..8}-{old,new}.{log,warm.gz}`.

## Kompakt variant, replay 7950→9150

Samma startdump/parametrar som första kandidatens fortsättningsfönster,
ordning gammal/ny/ny/gammal:

| Par | Referens, ms | Kompakt CP0 + inlining, ms |
|---|---:|---:|
| 1 | 11048,7 | 10691,2 |
| 2 | 10742,4 | 10620,6 |
| Medel | 10895,55 | 10655,90 |

Båda par vunna: **2,20 % kortare medeltid**. Alla fyra slutdumpar matchar
det andra fönstrets 4698-orakel ovan. Lokala artefakter:
`.build-tmp/cp0-compact-next-bench.log` och numrerade loggar/slutdumpar.

Kombinationen behålls: sex av sex vunna par över två fönster och alla
tolv fullständiga slutdumpar identiska mot referensen. Detta är en lokal
Linux-vinst, inte bevis för universell fartvinst eller spelbart tempo.
41 swap-kommandon på cirka 10,5–10,7 sekunder är fortfarande långt från
spelbart. Probe-fps får inte användas som spelets bildfrekvens.

## Reproduktion och nästa spår

Slutverifiering: 900 CP0-, 30740 Nile-, 2528 COP1-, 45 cache- och 5120
runtime-statusfall passerar. Release Probe/UI bygger utan fel; UI har
33 befintliga varningar. `git diff --check` passerar. Loggar:
`.build-tmp/cp0-compact-checks.log`, `cp0-compact-{nile,cop1,cache,state}-checks.log`,
`cp0-compact-build.log` och `cp0-compact-ui-build.log`.
Befintliga Lua-ändringar och lokala artefakter lämnas utanför commit.

Bygg Probe i Release och kör testet:

```sh
dotnet build tools/GauntletProbe/GauntletProbe.csproj -c Release --no-restore -m:1 /clp:ErrorsOnly
env EUTHERDRIVE_GAUNTDL_TEST_CP0_CLOCK=1 \
 dotnet tools/GauntletProbe/bin/Release/net8.0/GauntletProbe.dll

env DOTNET_TieredCompilation=0 \
 EUTHERDRIVE_GAUNTDL_EXPERIMENT_RUNTIME_BLOCK_FAST_CACHE=1 \
 EUTHERDRIVE_GAUNTDL_RUNTIME_BLOCK_FAST_CACHE_SIZE=4096 \
 EUTHERDRIVE_GAUNTDL_EXPERIMENT_FIFO_PACKET_MEMBERSHIP=1 \
 EUTHERDRIVE_GAUNTDL_SAVE_FINAL_STATE=.build-tmp/cp0-manual-final.warm.gz \
 scripts/run-gauntdl-probe-warm.sh \
 /home/nichlas/roms/MAME/Midway/Vegas/gauntd 7950 90000 \
 .build-tmp/gaunt-k2-clean2-f6750.warm.gz 6750
```

Välj nya filnamn vid upprepning. Referens-DLL väljs med
`EUTHERDRIVE_GAUNTDL_PROBE_DLL=.build-tmp/cp0-inline-baseline/GauntletProbe.dll`.
För andra fönstret: startdump `.build-tmp/nile-restored-first.warm.gz`,
startframe 7950, mål 9150. Inga ROM/snapshots/binärer checkas in.

Nästa maskinkodsbaserade spår: batchmetoden anropar
`TryRunRuntimeCompiledBlock` när längdgränsen uppfylls, även när det
experimentet är avstängt. Mät att välja bort den vägen före hjälparanropet;
behåll den aktiverade vägens befintliga kontroller. Inte implementerat här.
