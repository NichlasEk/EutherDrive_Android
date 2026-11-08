# Mario 64 / N64 — paus 2026-11-08

## Uppdrag och pausläge

Användaren har uttryckligen pausat: ”vi pausar där skriv en bra handoff”.
Starta inga nya experiment förrän användaren återupptar arbetet.

Målet är Mario 64 i full realtid även i tunga lägen, med marginal för musiken.
**Målet är inte uppnått.** Spelet har blivit mer spelbart enligt användaren,
men korta mätfönster går fortfarande under 100 %. Medelvärden och probe-FPS
bevisar inte jämn bildtakt eller felfritt hörbart ljud.

Arbetet gäller Linux i /home/nichlas/EutherDrive_Android, inte Android.
Användaren har tidigare bett om commit/push mellan verifierade framsteg.
Ingen agentdelegering är begärd. Bevara ROM, saves och orelaterad dirty tree.

## Git och levererad version

- Branch: main.
- Origin: https://github.com/NichlasEk/EutherDrive_Android.git
- HEAD före denna handoff: bfd870ff90beffcf29ad0d5f039fda4cbf1cd01e.
  Matchade origin/main vid livekontroll inför pausen.
- Senast accepterade runtimeändring:
  054a996cd1882ab3c752cd42a24505939147082a,
  ”Inline native CPU operand guards without expanding the interpreter”.
- bfd870ff tillför tester, försöksrapport och arkiverade patchar; ingen ny
  runtimeoptimering. N64Adapter.cs har återställts till accepterad version.
- Denna handoff commitas/pushas separat. Kontrollera git log/status vid återstart.

**Befintlig spelstart:**

```sh
cd /home/nichlas/EutherDrive_Android
.build-tmp/n64-mario-2026-11-07/play/run.sh
```

Starta bara när användaren vill spela; inget synligt spelfönster startades i
senaste omgången. Ingen matchande N64-probe/test/spelprocess var igång vid
pauskontrollen. Inga egna benchmarkjobb väntar på fortsatt pollning.

Launchern pekar på:
`.build-tmp/n64-mario-2026-11-08/desktop-operand-native/EutherDrive.UI.dll`
och samma katalogs libeuther_n64_gpu.so, med .NET **10.0.14**.
Rollback: `.build-tmp/n64-mario-2026-11-07/play/run-before-operand-native.sh`
(pekar på föregående desktop-count-wrap).

Manifest:
`.build-tmp/n64-mario-2026-11-08/delivery-operand-native-manifest.json`.
Det innehåller acceptedCommit, buildSourceBase, corePatchSha256 och filhashar.
Bygg inte över den exakta verifierade desktopkatalogen. Desktopbygget innehåller
redan befintliga lokala UI-ändringar, vilka inte har absorberats i våra commits.

Originalsave, oförändrad vid pauskontrollen:
`/home/nichlas/roms/N64/Super_Mario_64__USA_-.n64_f3b0fc9f.euthstate`
SHA256:
`e0b139d1eb43ea3a36d331d927bdbfefb1567f09539c30a9927038e99cfe194c`.

## Accepterad CPU-optimering och dess bevis

Läs först `docs/n64-operand-inline-2026-11-08.md`.

R4300.Blocks.cs behåller CanAccessCpuBlockOperand som NoInlining-wrapper.
Den delar implementation med CanAccessCpuJitOperand (AggressiveInlining),
som används av genererade nativeblocks JitValidate. Operandreglerna är
oförändrade. Brett inlinande även i interpretern var en regression och avvisades.

Native-only ABBA:
- guest 70–90: +1,927 % wall throughput, +1,661 % CPU throughput.
- guest 95–120: +4,229 % wall, +5,572 % CPU.
- AudioEngine/adapter ABBA: +1,281 %, upprepning +1,662 % wall.
  Genomsnittliga underruns minskade i båda serierna men inte i varje par.

CPU/JIT: 2370 CPU-fall, 2902 JIT-fall (2469 compiled); kontrollspel, 58 GPU-fall,
portabelt bygge och exakt desktop save/load/resume passerade. Se rapporten
för detaljer och begränsningar. Dessa tester gjordes för den accepterade
runtimeändringen, inte om igen under sista frontendomgången.

MULTU-profil: 849481 outer-loop-anrop tog 72,821 ms guest 90–120.
Mätningen exkluderar block-exit/omgivande outer-loopkostnad. Det är därför
inte ett tak på nyttan av framtida fler-cykel-JIT, men motiverar inte att
bara optimera själva multiplikationen. Instruktionens fem cykler måste bevaras.

## Senaste omgången: tre avvisade frontendförsök

Läs `docs/n64-frontend-trials-2026-11-08.md`.

En faktisk adapter/AudioEngine-profil visade under en sekund före sampling:
R4300 93 CPU-ticks, MainThread 43, AudioEngine 25.
Ptrace-IP-sampling gav MainThread PullFrame 33,72 %, WaitForCpuProgress 5,69 %.
R4300-sampling: TryAdvanceCpuBlock 17,91 %, Rsp.ExecuteSlice 10,32 %,
ExecuteVectorSimdOperation 5,17 %, ComputeProgressSignature 4,54 %,
ExecuteCpuBlockInstruction 4,39 %.
Sampling inkluderar native runtime och väntan; procentsiffrorna är inte
direkt tidsattribution. Profilen är intrusiv, inte en hastighetsbenchmark.

Mario-bildformatet var 320x237 RGBA5551. Tre separata ABBA-serier jämförde
exakt samma accepterade probe med endast EutherDrive.Core.dll utbytt:

| Försök | Wall-throughput | Underruns referens | Underruns kandidat |
| --- | ---: | --- | --- |
| 128 KiB färgtabell | -3,161 % | 222 / 209 | 328 / 316 |
| SIMD-färgkonvertering | -1,099 % | 303 / 166 | 353 / 152 |
| Sleep(1) i stället för Sleep(0) efter första ms | +0,100 % | 180 / 203 | 200 / 200 |

Alla sex guest cycles/RAM/generated-audio-checkpoints matchade i varje serie.
Inga submission skips eller engine drops. Ingen gav stabil förbättring av hela
flödet. Samtliga runtimeändringar togs bort; patchar sparades under
`docs/experiments/n64-frame-{lut,simd,wait}-2026-11-08.patch`.
Alla tre passerar git apply --check mot accepterad N64Adapter.
Patcharnas tomma context-rader kan flaggas av git diff --check som whitespace;
det är patchformat, inte trailing whitespace i produktionskoden.

SIMD var ungefär sju gånger snabbare isolerat, men gav ingen pålitlig spelvinst.
Exakt skalning för fem bitar: (v * 1053) >> 7 == floor(v * 255 / 31).
Vanlig bitreplicering är inte identisk med tidigare färger.
10694 kontrollfall täcker alla 65536 värden, båda byteordningarna, osynkade
adresser, korta/udda buffertar, canaries, oförändrad källa och nollad destinationstail.
SIMD klarade även AVX2 av och alla intrinsics av. Accepterad Core klarade samma
nya tester. Testerna finns i tools/N64AudioPlaybackProbe/FrameConversionChecks.cs:

```sh
dotnet --fx-version 10.0.14 \
  .build-tmp/n64-mario-2026-11-08/frame-check-final/N64AudioPlaybackProbe.dll \
  --check-frame-conversion
```

Audio-proben använder riktig adapter och tidsstyrd AudioEngine men **null sink**.
Den bevisar inte hörbar kvalitet via ljudkortet. Ingen fysisk lyssningskontroll
gjordes under sista omgången.

## Exakta lokala fixtures och verktyg

Alla paths nedan är relativa repo om inget annat anges.

- ROM: /home/nichlas/roms/N64/Super_Mario_64_(USA)-.n64
- Normaliserad input: .build-tmp/n64-mario-2026-11-07/mario-state60/input.z64
- State60: .build-tmp/n64-mario-2026-11-07/mario-state60/core-state.bin
- State90: .build-tmp/n64-mario-2026-11-08/mario-state90/core-state.bin
- Aktuell accepterad CPU-probe:
  .build-tmp/n64-mario-2026-11-08/operand-native/N64Probe.dll
- Aktuell accepterad audioprobe:
  .build-tmp/n64-mario-2026-11-08/audio-operand-native/N64AudioPlaybackProbe.dll
- decode-final och audio-count-wrap är äldre baselines; använd dem inte som
  aktuell referens för nästa optimering.
- Native GPU: .build-tmp/n64-native-profile-2026-09-24/libeuther_n64_gpu.so
  SHA256 2e912064505584e6809f6fc5258301475916596c9ed1c97781444816b106104a.
  Vid kopiering: bevara namnet .so, inte symlinkens upplösta .so.1.0.0-namn.

Under .build-tmp/n64-mario-2026-11-08/:
- profile-audio-current.py, profile-audio-current/ och .log:
  thread-usage.json, profile.json, perf map, native maps, IP-sampling och replay.
- audio-frame-{lut,simd,wait}-abba.py/.log/-results.json,
  numrerade host-JSON, .guest.json och loggar.
- audio-frame-{lut,simd,wait}-manifest.json:
  visar att bara Core.dll skiljer sig från accepterad audio-baseline.
- frame-lut/, frame-simd/, frame-wait/: kandidatbyggen, inte levererad version.
- frame-check-final/: nya testprogrammet med accepterad Core.dll.
- operand-native*, audio-operand-native*, desktop-operand-native*:
  tidigare accepterade resultat och verifierade artefakter.

Benchmarkskripten skriver fasta outputnamn: kopiera till nya namn och ändra
outputprefix innan nästa körning; skriv inte över historisk evidens.

Audio-buildmall, ny separat outputkatalog:

```sh
dotnet build tools/N64AudioPlaybackProbe/N64AudioPlaybackProbe.csproj \
  -c Release -m:1 -p:N64LiveGpu=true -p:N64PerformanceProbe=true \
  -p:N64CpuDispatchProfile=false -p:N64RdpJournalCapture=false \
  -p:N64RspProfile=false -o .build-tmp/NY-KANDIDAT /clp:ErrorsOnly
```

GPU-miljö för replay:
EUTHERDRIVE_N64_GPU_LIBRARY=absolut path till .so,
EUTHERDRIVE_N64_GPU_VALIDATE=0, PARALLEL_RDP_SMALL_TYPES=1,
PARALLEL_RDP_FORCE_SYNC_SHADER=1, PARALLEL_RDP_SINGLE_THREADED_COMMAND=1,
GRANITE_NUM_WORKER_THREADS=2, GRANITE_VULKAN_NO_VALIDATION=1,
EUTHERDRIVE_N64_GPU_OVERLAP=1.
Audio ABBA sätter även EUTHERDRIVE_N64_RUNFRAME_WAIT_MS=12.

## Dirty tree som måste bevaras

Följande redan befintliga ändringar ligger utanför de senaste scoped commits:
Directory.Build.props; EutherDrive.UI/EutherDrive.UI.csproj;
EutherDrive.UI/Program.cs; EutherDrive.UI/Savestates/SavestateViewModel.cs;
MIT_License.txt; docs/n64-realtime-agent-plan.md;
tools/N64Probe/Program.cs; tools/N64Probe/StateGameplayBenchmark.cs;
tools/N64Probe/bench-state.py.

Ospårat inkluderar .build-tmp/, EutherDrive.UI/N64DesktopDefaults.cs,
console_history, diff/, snap/, äldre septemberpatchar/rapporter,
tools/N64Probe/CpuJitValidationChecks.cs och pycache-kataloger.
Använd aldrig git clean/reset över detta. Ingen ROM/save ska commitas.

## Nästa steg efter uttryckligt återupptagande

1. Läs denna fil och de två novemberrapporterna. Kontrollera branch, remote,
   dirty tree, leveransmanifest och aktiva processer. Bygg inte om på rutin.
2. Återvänd till R4300 blockdispatch/RSP-exekvering som nästa profileringsspår.
   Ovanstående procent är hypotesunderlag, inte bevis för en specifik fix.
   Läs även tidigare försöksrapporter innan en tidigare avvisad idé återtestas.
3. Välj en smal förändring och bevara instruktionstiming, exceptions, device
   boundaries och full savestate-semantik. Ingen klockfusk/frame skipping.
4. Kör relevanta riktighetstester och jämför samma replay mot operand-native.
   CPU-ändringar kräver CPU/JIT och full-state/frame/PCM/input-kontroller.
   Sex audio-checkpoints är inte ensamma ett full-state-bevis.
5. Mät tid med interleaved ABBA och både tidig/tung scen, följt av riktig
   adapter/AudioEngine. Kräv upprepning om marginalen är liten eller paren
   motsäger varandra. Inga egna byggen/testjobb samtidigt med timing.
6. Andra användarjobb (bl.a. shadPS4 och modellservrar) kan störa CPU/GPU.
   Stoppa dem inte. Kör egna Vulkan-jobb seriellt; VRAM-brist har tidigare
   gjort device creation omöjlig. Separera miljöfel från kodregression.
7. Först vid verifierad vinst: separat desktopbygge, exakt desktop
   save/load/resume-gate, originalsave-hash och artefaktmanifest.
   Bevara rollback, uppdatera befintlig launcher, scoped commit/push.
8. Rapportera ärligt kvarvarande korta dippar och separat fysisk ljudkontroll.
   100 % i alla lägen är fortsatt öppet, inte en uppnådd leverans.
