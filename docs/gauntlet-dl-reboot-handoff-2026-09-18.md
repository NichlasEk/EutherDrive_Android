# Gauntlet DL – återstartspunkt 2026-09-18

## Korrigerad arbetsinriktning efter återstart

Användaren har uttryckligen förtydligat att det pågående arbetet gäller
MIPS-JIT och prestanda här på Linux. Android-/ARM64-mätning är inte nästa
steg eller ett villkor för detta arbete. Fortsätt med CPU-exekveringsvägen,
fullständiga replay-slutdumpar och växlade Linux-mätningar. NCC-resultaten
nedan är föregående checkpoint, inte en beställning på enhetsprov.

Senaste fortsättning:
[Nile-timeråterställning och klockoptimering](gauntlet-dl-nile-clock-2026-09-18.md).
Snapshotladdaren måste återställa timeraktivitetsmasken från kontrollregistren.
Den gamla 32f8-slutdumpen hade bortkopplade timers; aktuellt orakel är fe8b,
se det fullständiga SHA256-värdet i reproduktionen nedan. Mot korrekt
timerreferens gav optimeringen 17,94 % kortare median och fyra vunna par.

Föregående CPU-checkpoint, pushad som `dd68e135`:
[COP1-dispatch i safe-block-vägen](gauntlet-dl-safe-cop1-dispatch-2026-09-18.md).
3,02 % kortare median i första Linux-fönstret; senare fönster neutralt.
Alla fullständiga slutdumpar och 2528 riktade CPU-fall matchar referensen.
Probe/UI bygger. Kod, test och rapport hör till samma COP1-checkpoint.
Ingen mätning eller byggprocess från detta pass behöver återupptas.

## Börja här efter omstart

Repo: `/home/nichlas/EutherDrive_Android`, branch `main`.
Historisk NCC-kodcheckpoint före återstart: **`ee918186`**.
Aktuell fortsättning och förändrat timerorakel beskrivs ovan.
Inget GauntletProbe-test eller dotnet-bygge körde när anteckningen skrevs.
Ingen bakgrundskörning behöver återupptas efter omstart.

Användaren vill fortsätta mot spelbart Gauntlet Dark Legacy, med uppmätta
förbättringar och commit/push av bra, verifierade ändringar.

## Tidigare NCC-resultat

Vi har implementerat **sammanslagen NCC-texelhämtning och bilinjär filtrering**
för format 1/9 i CPU-renderern. Det är inte en ny GPU-backend.

- Flagga: `EUTHERDRIVE_GAUNTDL_EXPERIMENT_FUSED_NCC=1`, **av som standard**.
- Förpackad, härledd NCC-tabell, högst 16 KiB extra; samma färger/avrundning.
- Originalvägen finns kvar för andra format och samplediagnostik.
- Cacheinvalidation följer NCC-registerskrivningar; probe-snapshotladdning
  och direkt TMU-registerimport tömmer båda NCC-cachetyperna.
- 151684 nya differential-/kontrollfall passerar, plus 49408 LUT-postkontroller.
- Befintliga filtertester (1156784), LOD-tester (1048576) och GPU-gränstester
  passerar. Release Core/Probe och UI bygger; UI: 33 varningar, 0 fel.
- Alla **24 fullständiga replay-slutdumpar** var exakt identiska.
- Slutvarianten mot separat oförändrat `599d05bd`-bygge: 3/4 par snabbare,
  **2,42 % kortare median**, **1,54 % kortare medeltid**.
- Samma binär av/på för slutvarianten: 4/4 par snabbare, 1,93 % kortare median.

Detta är en liten positiv signal på en Linux-replay, inte bevis för generell
vinst eller spelbart tempo. Probe-fps är **inte spelets fps**. Android/ARM64
och interaktiv spelbarhet har inte verifierats av dessa tester.

Detaljer: [NCC-rapport](gauntlet-dl-fused-ncc-filter-2026-09-18.md).

## Fortsätt härnäst

1. Fortsätt med den breda CPU-exekveringsvägen och JIT på Linux enligt
   användarens korrigerade inriktning. Behåll NCC-flaggan som opt-in.
2. Validera nya CPU-kandidater mot det befintliga fullständiga replay-oraklet
   och mät med växlande referens-/kandidatbyggen.
3. För större lyft: återvänd till bred CPU-exekveringsväg eller rendererarbete
   utifrån profilen, inte en ny godtycklig gäst-PC-region. Senaste CPU-profil
   före NCC visade cirka 41 % MIPS, 36 % texturraster och 17 % övrig Voodoo
   på huvudtrådens replay-stack; detta är stackvikt, inte additiva CPU-cykler
   över alla trådar. Profilen föregår timeråterställningen: samla en ny profil
   med aktiva timers innan nästa CPU/JIT-prioritering. GPU-offload är inte
   bevisat snabbare av dessa data.

Profil: [CPU-profil efter FIFO-fix](gauntlet-dl-cpu-profile-after-fifo-filter-2026-09-18.md).
Återimplementera inte de redan prövade försöken utan nytt underlag:
`599d05bd` dokumenterar specialiserad TMU-kod med osäker vinst; lookup-block
och lookup-region-JIT har också provats och förkastats i dagens rapporter.

## Reproduktion efter omstart

Kör från reporoten. Bygg klart innan tidtagning; inga samtidiga egna byggen
eller profilerare under benchmark. Välj nya artefaktnamn vid upprepning.

```sh
dotnet build tools/GauntletProbe/GauntletProbe.csproj -c Release -m:1 /clp:ErrorsOnly
env EUTHERDRIVE_GAUNTDL_TEST_NILE_CLOCK=1 \
 dotnet tools/GauntletProbe/bin/Release/net8.0/GauntletProbe.dll

env DOTNET_TieredCompilation=0 \
 EUTHERDRIVE_GAUNTDL_EXPERIMENT_RUNTIME_BLOCK_FAST_CACHE=1 \
 EUTHERDRIVE_GAUNTDL_RUNTIME_BLOCK_FAST_CACHE_SIZE=4096 \
 EUTHERDRIVE_GAUNTDL_EXPERIMENT_FIFO_PACKET_MEMBERSHIP=1 \
 EUTHERDRIVE_GAUNTDL_SAVE_FINAL_STATE=.build-tmp/reboot-nile-1-final.warm.gz \
 scripts/run-gauntdl-probe-warm.sh \
 /home/nichlas/roms/MAME/Midway/Vegas/gauntd 7950 90000 \
 .build-tmp/gaunt-k2-clean2-f6750.warm.gz 6750 \
 > .build-tmp/reboot-nile-1.log 2>&1

gzip -dc .build-tmp/reboot-nile-1-final.warm.gz | sha256sum
```

Förväntad **dekomprimerad slutdump-SHA256**, replay 6750→7950:
`fe8b7adabe915e51785414d2c1f6e3a5ef836b9159d14062bbf7f8e2dd1fd7ce`.
Bildhash `0xe87b12da`, 41 swap-kommandon.

Indatasnapshot finns lokalt (7,1 MiB):
`.build-tmp/gaunt-k2-clean2-f6750.warm.gz`.
Dess **komprimerade fil-SHA256**, kontrollerad inför omstart:
`312ef133ae70d40e2c437772ea79f96884d0eee687daabd4776ce9c166494df2`.

Historisk NCC-jämförelse (före timerfixen): samma binär, ändra bara FUSED_NCC till 0/1, kör minst
fyra växlande par och jämför fulla sluttillstånd. Separat original finns
också i `.build-tmp/fused-ncc-reference-head` (detached `599d05bd`). Dess
Probe-DLL kan väljas via `EUTHERDRIVE_GAUNTDL_PROBE_DLL`; den variabeln
ska inte ligga kvar av misstag när kandidatens DLL ska testas.

## Filer och lokalt arbete att bevara

Kod: `EutherDrive.Core/Arcade/Vegas/VoodooBringupBackend.FusedNcc.cs`,
sampler/cache-hookar i `GauntletDarkLegacyAdapter.cs`,
`tools/GauntletProbe/FusedNccChecks.cs` och `Program.cs`.

Mätartefakter ligger under `.build-tmp/fused-ncc-*` (första variant),
`fused-ncc-inline-*` (slutvariant av/på) och `fused-ncc-head-*`
(oberoende originalbygge), med loggar och fulla slutdumpar.
`.build-tmp/fused-ncc-sampler-asm.log` visar att filter/hämtningshjälparna
inlinades på denna värd. Alla dessa filer är lokala, **inte Git-backupade**.
Behåll `.build-tmp/` över omstart; rensa inte bort snapshots eller worktree.

Följande orelaterade arbetskatalogändringar lämnades orörda och är inte
med i NCC-commiten:

- Ändrad `tools/GauntletProbe/mame-gauntdl-phase5-oracle.lua`.
- Ospårade `tools/GauntletProbe/mame-gauntdl-mainram.lua`,
  `tools/GauntletProbe/__pycache__/`, `console_history`, `diff/`, `snap/`
  och `.build-tmp/`.

Använd inte `git add .`, reset/clean eller bred rensning. ROM, snapshots
och råa profiler ska inte checkas in. Omstarten beställs av användaren;
ingen reboot har initierats av assistenten.
