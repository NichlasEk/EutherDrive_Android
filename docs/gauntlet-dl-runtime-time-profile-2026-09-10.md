# Gauntlet: uppmätt tidsfördelning

## Slutsats

Två separata fasprofiler och två EventPipe-stackprofiler visar att grafikvägen
är en stor del av det arbete som tidigare benämnts CPU-runtime. Raster/FIFO
exekveras synkront från gästens minnesskrivningar och ligger därför inuti
`RunProbeSteps`. Att CPU-fasen tar cirka 92 procent av tiden innebär inte att
92 procent är MIPS-tolkning.

Den uppmätta texturrastertiden ensam är 4,04–4,07 sekunder för sekvensens
41 gäst-swaps. Om den kostnaden består skulle även en hypotetisk eliminering
av allt annat arbete ge cirka 10 swaps/s. Målet 30 swaps/s kräver att hela
sekvensen ryms på ungefär 1,37 sekunder. Både CPU- och grafikvägen behöver
alltså större förbättringar. Det är en extrapolering med konstant rasterkostnad,
inte en uppmätt hastighet för en framtida JIT eller GPU-backend.

## Direkta fas- och rastertider

Samma f6750-snapshot, 1 200 anrop och 90 000 CPU-steg/anrop som tidigare.
Summerat över alla 1 200 anrop, inklusive återinträde och .NET-uppvärmning:

| Tid | Körning 1 | Körning 2 |
| --- | ---: | ---: |
| Hela replayen | 11 712,0 ms | 11 832,4 ms |
| Callbacks före huvudsaklig CPU-körning | 562,78 ms | 577,73 ms |
| CPU-fas **inklusive synkron grafik** | 10 816,22 ms | 10 925,79 ms |
| Enhetssteg/ljud efter CPU-fasen | 2,97 ms | 2,92 ms |
| Framebuffer/presentation i headless-proben | 228,29 ms | 227,22 ms |
| FIFO-avkodning, inklusive raster | 4 764,14 ms | 4 792,46 ms |
| Därav Type-3-push, inklusive raster | 4 270,55 ms | 4 298,50 ms |
| Texturrasterisering | 4 039,94 ms | 4 066,35 ms |

De tre sista raderna överlappar varandra och CPU/callbacks; summera dem inte.
`renderMs` är probens framebufferarbete, inte hela grafikemuleringen eller
kostnaden för desktop-UI:n. Avrundning per anrop och profilutskrifter förklarar
att fasraderna inte summerar exakt till hela replaytiden.

## Stackprofil: huvudtråden under just replay

`dotnet-trace` 8.0.547301 installerades lokalt i `.build-tmp/profiling-tools`.
Två cpu-sampling-spår konverterades till Speedscope. Analysen väljer bara
stackintervall under `Program.g__RunUntilFrame`; ROM-inläsning, snapshot-
dekomprimering och efterföljande dumpning ingår inte. Följande kategorier är
ömsesidigt uteslutande, med grafik före klockor före övriga minnesvägar före
resterande CPU-kod:

| Andel av huvudtrådens samplade replaytid | Spår 1 | Spår 2 |
| --- | ---: | ---: |
| Texturraster, inklusive väntan på rasterarbetare | 35,49 % | 34,91 % |
| Övrig Voodoo/FIFO/presentation | 21,35 % | 21,54 % |
| Återstående MIPS-kod, inklusive inlinade helpers | 38,19 % | 39,01 % |
| Separat synliga minnes-/enhetsvägar | 1,49 % | 1,16 % |
| Separat synlig CP0/Nile-klockkod | 0,64 % | 0,97 % |
| Övrig replay | 2,85 % | 2,42 % |

Spåren tog 13 014,9 respektive 14 921,0 ms, alltså längre än fasprofilerna.
Absoluta samplade tider ska därför inte behandlas som opåverkad körtid.
Fördelningen är däremot likartad mellan spåren och rasterandelen stämmer
väl med den oberoende fasprofilen.

Det här är **huvudtrådens samplade tidsandelar**, inte summerad CPU-tid över
alla processorkärnor. Verktygets ofiltrerade `topN` visar stora `Monitor.Wait`-
andelar eftersom rasterarbetarnas tomgång ingår. De får inte användas som
bevis för att huvudtråden väntar motsvarande andel eller att workerpoolen bör
tas bort. Speedscope-markörerna `CPU_TIME` och `UNMANAGED_CODE_TIME` är inte
applikationsmetoder och filtreras bort vid val av exklusivt blad.

## Konkreta heta vägar

Exklusiv tid på synliga metodramar, ungefärligt intervall mellan spåren:

- Rasterraden `FillTexturedTriangle.RasterRow`: 13,7–14,7 procent.
- Två-TMU-sampling/kombinering `SampleAndCombineTwoTmusMameFixed`: 8,8–10,1 procent.
- `SampleTextureMameFixedForTmu`: 3,1–3,3 procent.
- `WriteFifo`: 6,0–6,5 procent, utöver dess anropade arbete.
- `MipsR5000Core.Step`: 11,0–11,4 procent.
- `RunProbeSteps`: 5,5–5,7 procent.
- `ExecuteRuntimeSafeInstruction`: 5,2–5,6 procent.
- `TryRunRuntimeSafeInstructionBatch`: 4,5–4,6 procent.
- Dictionary-uppslag: cirka 3 procent; stackarna pekar främst på
  `GetRuntimeSafeInstructionBlock`.
- `SortedSet.DoRemove`: cirka 2 procent; stackarna pekar på FIFO-bokföring.

Inlining och sampling gör att dessa siffror inte separerar varje operation
exakt. Framför allt kan inlinad RAM- och klockkod bokföras på CPU-metoden som
anropar den. De låga separat synliga clock/RAM-andelarna är inte ett bevis för
att all sådan kostnad är under en procent. Små blad som `Stopwatch.GetTimestamp`
kan också vara känsliga för sampling/attribution; optimera inte utifrån ett
sådant blad utan en riktad kontroll.

## Rekommenderad prioritering

1. Nästa större grafikförsök bör angripa textursampling/kombinering och den
   heta rasterraden. En GPU-väg är ett möjligt större arkitektursteg men måste
   bedömas mot exakt bildreferens, MMIO-/FIFO-ordning och synkronisering.
2. Profilera FIFO-bokföringens `WriteFifo`/`SortedSet` separat inför ett
   avgränsat försök som minskar arbetet per inskrivet ord/paket. Detta är en
   konkret kostnad även utanför själva pixelarbetet.
3. Fortsatt CPU-JIT måste täcka en stor del av dispatchen och dess
   register-/minnesarbete. Ett fåtal långa block eller ytterligare en kort
   loop träffar för lite av denna tid. Prioritera inte klockändringar enbart
   på antagandet att instruktionstidsbokföringen är huvudkostnaden.

Ingen emulatoroptimering eller ändring av standardbeteende gjordes i denna
undersökning. Detta är en mätning och prioritering, inte en ny spelbar version.

## Verifiering och artefakter

Alla fyra körningar behöll hash `0xe87b12da`, PC `0xffffffff80079e18`, FIFO
`27113239/2660774`, draw `474871` och swaps `3877`. Start-snapshoten har
swaps `3836`. Samma warm-snapshot-SHA-256 som tidigare:
`312ef133ae70d40e2c437772ea79f96884d0eee687daabd4776ce9c166494df2`.

Artefakter under `.build-tmp/`:

- `gauntdl-time-profile-20260910-{phases,repeat-phases}.log`
- `gauntdl-time-profile-20260910{,-repeat}.nettrace`
- `gauntdl-time-profile-20260910{,-repeat}.speedscope.json`
- `gauntdl-time-profile-20260910-{trace,repeat-trace}.log`
- `gauntdl-time-profile-20260910-{analysis,repeat-analysis}.txt`
- `analyze-gauntdl-time-profile.py`

Fasprofilen körs med `EUTHERDRIVE_GAUNTDL_PROFILE_FRAME_PHASES=1` framför
det vanliga warm-probe-kommandot. Stackprofilen startades med:

```sh
.build-tmp/profiling-tools/dotnet-trace collect --profile cpu-sampling \
  --format Speedscope --output .build-tmp/gauntdl-time-profile-20260910.nettrace \
  --show-child-io -- scripts/run-gauntdl-probe-warm.sh \
  /home/nichlas/roms/MAME/Midway/Vegas/gauntd 7950 90000 \
  .build-tmp/gaunt-k2-clean2-f6750.warm.gz 6750
```

Spårningen krävde körning utanför sandboxen eftersom den lokala
diagnostiksocketen blockerades där. Inga globala dotnet-tools installerades.
