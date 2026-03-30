# PSX gameplay sync governor

Date: 2026-03-30

## Mål

Få ut mer faktisk gameplay-prestanda i PSX-spel som liknar `Tekken 3` utan att göra en stor omskrivning av GPU eller timingkärna.

Det här passet lägger därför inte optimeringen i Android-renderingen, utan i PSX-kärnans sync-beteende mellan CPU och devices.

## Problemet

PSX-kärnan kör idag med två sync-lägen:

- ett tight läge för BIOS, polling och annan timingkänslig aktivitet
- ett relaxed läge när kärnan bedömer att tät sync inte behövs

Det relaxed läget har hittills haft en fast batchstorlek. Det är säkert, men ganska konservativt.

I spel som `Tekken 3` finns ofta långa perioder där workloaden är:

- mycket GPU-kommandon och geometri
- ganska lite MMIO-läsande polling
- inga stora VRAM-copy-operationer
- ingen tydlig load/streaming-karaktär

I just de lägena kostar täta `bus.tick()`-flushar och interrupt-checks mer än de hjälper.

## Idén

Lägg ett adaptivt lager ovanpå relaxed sync:

- om föregående frame ser ut som stabil 3D-gameplay höjs relaxed batch successivt
- om workloaden ser polling-, copy- eller irq-tung ut faller governorn tillbaka direkt
- den påverkar bara relaxed mode, aldrig det tighta skyddsnätet

Praktiskt betyder det att governorn försöker flytta workload från:

- många små `bus.tick()`-steg

till:

- färre, större steg när spelet tydligt tål det

## Hur heuristiken fungerar

Implementationen finns i [ProjectPSX.cs](/home/nichlas/EutherDrive_Android/ProjectPSX/Core/ProjectPSX.cs).

Per frame tar den perf-snapshots från:

- CPU
- BUS
- GPU

Sedan räknar den ut om framen liknar "3D gameplay" eller inte.

### Signal som talar för gameplay

- många trianglar eller hög samlad GPU-score
- geometri dominerar över rektangel/fill-rate-UI
- MMIO-läsningar är låga
- MMIO-skrivningar är fortfarande aktiva

Det här fångar scenarion där CPU:n mest matar GPU:n med draw commands, men inte busy-loopar på statusregister.

### Signal som talar emot gameplay

- många MMIO-läsningar eller många `load32` från MMIO
- CPU->VRAM eller VRAM->CPU copies
- mycket fill-rect / tung 2D-UI-profil
- tydliga IRQ-spikar
- BIOS har inte lämnat boot än

Det här är typiska tecken på laddning, streaming, polling eller timingkänsliga sekvenser där grovare batching är farligare.

## Batchnivåer

Default-batcherna är fortfarande:

- tight: `24`
- relaxed baseline: `96`

Governorn kan i `auto` höja relaxed-batch till:

- level 0: `96`
- level 1: `192`
- level 2: `384`

I `aggressive` får den ett högre tak:

- level 0: `192`
- level 1: `384`
- level 2: `768`
- level 3: `1536` eller `EUTHERDRIVE_PSX_SYNC_GOVERNOR_MAX_BATCH_CYCLES`, beroende på vilket som är lägre

Auto är tänkt som default. Aggressive är för snabbare experiment när man vill se om ett spel har mer marginal.

## Säkerhetsräcken

Governorn är medvetet byggd för att backa snabbare än den går upp.

Den gör följande:

- om polling/copy/irq blir tung: nivå `0` direkt och cooldown
- om scenen slutar se ut som geometri-dominerad gameplay: en nivå ned
- om tight sync ändå krävs av `BUS.RequiresFrequentSync`: vanliga tight-pathen tar över som tidigare

Det sista är viktigt. Governorn ersätter inte existerande tight sync-regler, den justerar bara relaxed-batchen när de reglerna inte redan har slagit till.

## Styrning via env vars

- `EUTHERDRIVE_PSX_SYNC_GOVERNOR=off|auto|aggressive`
- `EUTHERDRIVE_PSX_SYNC_GOVERNOR_MAX_BATCH_CYCLES=<heltal>`

Tolkning:

- `off`: helt avstängd, relaxed-batch stannar på basnivån
- `auto`: adaptiv men konservativ
- `aggressive`: går upp snabbare och högre

## Hur man verifierar att den jobbar

Perf-summaryn har nu en extra rad som visar governorns läge, nivå och nästa relaxed-batch.

Exempel på format:

```text
PSX sync mode:auto batch:192->384 lvl:2 stable:0 cool:0 poll:r4/l32:1 w:88 gpu:220/180/24 score:1086 cand:1
```

Tolkning:

- `batch:192->384`: den här framen kördes med `192`, nästa relaxed frame går upp till `384`
- `lvl:2`: governorn är på andra nivån
- `poll:r4/l32:1`: låg MMIO-polling, bra tecken
- `w:88`: aktiv MMIO-write workload, typiskt GPU-command feeding
- `cand:1`: framen kvalade som gameplay-kandidat

Om man i ett Tekken-liknande spel ser att:

- `cand` ofta blir `1`
- nivå går upp till `1` eller `2`
- FPS samtidigt stiger

då jobbar governorn ungefär som tänkt.

## Risker

Det här är fortfarande en heuristisk optimering, inte en exakt timingmodell.

Troliga riskområden:

- spel med ovanligt känslig GPU/TIMER-polling mitt i gameplay
- hybridscener med mycket 2D-overlay ovanpå 3D
- FMV- eller loadövergångar som kort ser "lätta" ut innan polling drar igång

Om ett spel regresserar är första steget att sätta:

```text
EUTHERDRIVE_PSX_SYNC_GOVERNOR=off
```

och därefter jämföra mot `auto`.

## Varför den här vägen är intressant

Den försöker köpa FPS där overheaden faktiskt ligger:

- färre device flushar
- färre onödiga interrupt-check-rundor
- bättre användning av relaxed mode just under gameplay

Det ger chans till märkbar vinst i 3D-spel utan att behöva börja med en mycket större rasterizer- eller renderer-omskrivning.

## Uppföljning: MMIO-polling och GPUSTAT

Efter första testen visade Tekken-liknande gameplay fortfarande mycket hög MMIO-polling även när governorn låg kvar på level `0`.

Det ledde till en andra probe:

- `BUS` samlar nu toppadresser för MMIO-läsningar per perf-fönster
- perf-summaryn visar en extra `PSX poll ...`-rad med hotspots
- `GPUSTAT` kan läsas via en relaxed path i vanligt command-läge utan att varje läsning automatiskt driver tight sync

Den nya raden kan till exempel se ut så här:

```text
PSX poll gpu.statx552 t0.valx12 gstRelax:540 sh:539
```

Tolkning:

- `gpu.statx552`: `GPUSTAT` är den hetaste poll-adressen
- `gstRelax:540`: så många `GPUSTAT`-läsningar gick genom relaxed pathen
- `sh:539`: så många av dem var rena shadow-hits inom samma device-epoch

Om det här orsakar regression kan den nya vägen stängas av med:

```text
EUTHERDRIVE_PSX_RELAX_GPUSTAT_POLLING=0
```

## Uppföljning 2: JOY_STAT som faktisk hotspot

Nästa körning visade att den största poll-adressen inte var `GPUSTAT`, utan `JOY_STAT` på `0x1F801044`.

Det gav en ännu bättre target:

- idle-`JOY_STAT` kan shadowas mellan device-ticks
- shadowing tillåts bara när joypad-länken verkligen är stilla
- om transfer, ack, interrupt eller RX-data finns pending faller läsningen tillbaka till normal MMIO-path

Perf-summaryn kan nu visa till exempel:

```text
PSX poll 0x1f801044x225 t2.valx93 t2.modex79 jstRelax:210 sh:209
```

Tolkning:

- `0x1f801044x225`: `JOY_STAT` dominerar polling-last
- `jstRelax:210`: så många `JOY_STAT`-läsningar tog relaxed pathen
- `sh:209`: nästan alla av dem blev shadow-hits inom samma device-epoch

Om just den här lättnaden behöver stängas av:

```text
EUTHERDRIVE_PSX_RELAX_JOYSTAT_POLLING=0
```
