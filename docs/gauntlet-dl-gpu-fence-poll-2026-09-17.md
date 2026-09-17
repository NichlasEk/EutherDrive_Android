# Gauntlet DL: kort pollning före ordinarie GPU-väntan

## Resultat på denna host

Fyra växlade par med diagnostisk färgnivå 2 ger 4,86 procent kortare replaytid
i medel. Alla fyra par vinner och alla åtta slutmaskiner är byte-exakta.
Detta gör inte GPU-vägen snabbare än normal CPU-körning. Pollningen är
avstängd som standard och endast tillgänglig i det diagnostiska nativebiblioteket.

| Par | Utan pollning, ms | Med pollning, ms |
|---|---:|---:|
| 1 | 14635,0 | 13854,8 |
| 2 | 14195,1 | 13999,7 |
| 3 | 14629,7 | 13555,2 |
| 4 | 14659,9 | 13884,6 |
| Medel | 14529,925 | 13823,575 |

Uppmätt draw-väntetid sjunker från 1329,18 till 1106,45 ms i medel.
Den skillnaden är mindre än skillnaden i total replaytid; hela totalvinsten
kan inte tillskrivas just denna deltimer. Scheduler-/hosteffekter och vanlig
körvariation återstår. Resultatet är från denna Linux/NVIDIA-host, inte Android.

## Implementation

`EUTHERDRIVE_GAUNTDL_GPU_FENCE_POLL=1` läses en gång när sessionen skapas.
Efter draw-submit frågar native om fencen är klar, fram till en deadline
50 mikrosekunder senare. Klar fence avbryter pollningen; annars går körningen
vidare till den ordinarie väntan. Vulkan-fel behandlas som fel, inte som timeout.

`vkWaitForFences` körs **alltid**, även när pollningen hittar en färdig fence.
Ingen synkronisering, minnesbarriär, readback eller räknarkontroll tas bort.
Inget nytt asynkront tillstånd införs. Pixel-readbacks ändras inte.
50 mikrosekunder är en kontrollerad loopdeadline, inte en hård tidsgräns för
ett enskilt drivrutinsanrop eller en tråd som schemaläggs bort.

`gpuFencePoll` visar completed/fallback. I tidsproven blir 3 851–4 634 av
9 012 draws klara under pollningen. Aktiv pollning kan öka CPU-/energikostnad;
energiförbrukning är inte mätt och flaggan blir inte en standardinställning.

## Reproduktion och bevis

Samma capture-build/native/shader i alla tidsprov, nivå 2,
resident/incremental/sparse/dirty/bbox, GPU-profile på, limit 65 536,
`DOTNET_TieredCompilation=0`. Warm-replay 6750→7950, 90 000 steg per anrop.
Vulkan-validering och dirty-kontrollskanning av. Inga samtidiga byggen/hashningar.
Ordning old1/new1/new2/old2/old3/new3/new4/old4; endast pollflaggan skiljer.

Alla åtta slutmaskiner: 98 901 914 byte, SHA-256
`32f8e9c49dcc5498c044bb602156ef6e82fe5dec64f27c99749f3a6fe298053a`.
Bildhash `0xe87b12da`, 41 swap-kommandon. Probe-fps är inte spel-FPS.

Artefakter: `.build-tmp/gaunt-poll-{old1,new1,new2,old2,old3,new3,new4,old4}.log`
med motsvarande `-final.warm.gz`. Slutlig kontrollkörning och native-tester
finns i `gaunt-poll-audit.log` respektive `gaunt-poll-native-tests.log`.

Slutkontrollen med pollning, Vulkan-validering och dirty-kontrollskanning
matchar samma full-state-orakel, har noll synkroniseringsfel och inga väntande
pixlar. Native resident-test passerar med pollning: äldre pixelorakel,
readback/reset-ordning, avvisning av okänd färgväg och bevarat djup när
utökad färgväg stänger av djupskrivning. Testet avvisar också det nya
fbz-läget om den gamla common-färgvägen anges.

Det större arkitekturarbetet kvarstår: färre submissions och uppskjutna,
ordnade per-draw-resultat. Kort pollning ersätter inte riktig batchning.
Nästa avgränsade steg är att ge befintlig batch-shadow separata statistikord
per draw och jämföra dem mot sparade CPU-deltan vid flush. Först när det
passerar bör replacement få uppskjutna räknare/returvärden; CPU-buffertläsningar,
fallback, presentation, snapshot och avstängning måste behålla sina synkgränser.
