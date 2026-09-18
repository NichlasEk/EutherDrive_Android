# Gauntlet DL: korrekt timeråterställning och billigare Nile-klocksteg

## Snapshotfelet

Aktivitetsmasken `_nileActiveTimerMask` infördes i `2a5f3724` för att hoppa
över avstängda timers. Probes snapshotladdare kopierade `_nileRegisters`
direkt men återskapade aldrig denna härledda mask. En ny minnesinstans
behöll därmed mask 0 även när snapshotens kontrollregister slog på timers.
Desktop använder samma loader genom reflection och berörs också.

Den befintliga f6750-snapshoten har timer 0 och 3 aktiva, alltså mask 9:

| Timer | Reload | Kontroll | Räknare |
|---|---:|---:|---:|
| 0 | 390 | 1 | 333 |
| 1 | 0 | 0 | 0 |
| 2 | 100100 | 0 | 100100 |
| 3 | 3 | 1 | 2 |

`RestoreNileTimerMaskAfterSnapshotLoad` bygger nu om masken omedelbart efter
registerimporten. Inga registervärden eller IRQ-bitar skrivs av denna hook.
Snapshotformatet ändras inte och originalfilen lämnas orörd.

Ett första långt prov med enbart återställningsfixen tog 13103,0 ms.
Jämfört med den gamla slutdumpen ändrades exakt två byte, timer 0:s och
timer 3:s räknare. Bildhash, slut-PC och 41 swap-kommandon var oförändrade.
Det gamla 32f8-oraklet är alltså en historisk referens med bortkopplat
timerarbete och ska inte användas för aktuell kod.

## Optimering med timers faktiskt aktiva

Två ändringar minskar arbete i varje aktivt timersteg:

- `reload` är uint, så perioden `(ulong)reload + 1` är alltid positiv.
  När antalet ticks är mindre än perioden är resten redan antalet ticks;
  modulo behövs bara annars.
- Interna skrivningar till timer-räknarna använder samma endian-korrekta
  32-bitarslagring direkt. Räknarregistren ligger skilda från kontrollbitarna,
  så dessa skrivningar behöver inte skanna om alla fyra kontroller för att
  uppdatera aktivitetsmasken. Vanliga registerskrivningar behåller sin hook.

Avrundning, timer-expiry, IRQ-bitar och watchdog-villkor är oförändrade.
Klocksteg aggregeras inte. Två anrop med 1537 ticks ger två Nile-ticks;
ett anrop med 3074 ger tre. Detta ligger nu som explicit regressionstest.

## Långmätning 6750→7950

Referens och kandidat har båda korrekt återställd mask och COP1-checkpoint
`dd68e135`. Referensen har den ursprungliga timeraritmetiken och
registerskrivaren; endast kandidaten har de två optimeringarna ovan.
Fryst referensbuild: `.build-tmp/nile-restored-baseline/`.

Linux Release, `DOTNET_TieredCompilation=0`, 90000 steg, cache4096 och
FIFO-medlemskap på. NCC/GPU av. Ordning gammal/ny/ny/gammal/gammal/ny/ny/gammal.
Inga egna samtidiga byggen eller profilerare under mätningarna.

| Par | Korrekt referens, ms | Optimerad, ms |
|---|---:|---:|
| 1 | 13072,0 | 11064,9 |
| 2 | 13298,6 | 10768,6 |
| 3 | 13252,6 | 10872,3 |
| 4 | 13203,1 | 10838,1 |
| Medel | 13206,575 | 10885,975 |
| Median | 13227,85 | 10855,2 |

Alla fyra par vanns: **17,57 % kortare medeltid, 17,94 % kortare median**.
Alla åtta fullständiga slutdumpar är identiska. Det är en förbättring mot
den korrekta basen, inte mot tidigare 9–10-sekunderstider där timers stod
stilla. 41 swap-kommandon på omkring 10,9 sekunder är fortfarande bara
cirka 3,8 swap-kommandon/s och inte spelbart tempo.

Nytt dekomprimerat slutdump-SHA256 för 6750→7950:
`fe8b7adabe915e51785414d2c1f6e3a5ef836b9159d14062bbf7f8e2dd1fd7ce`.
Bildhash `0xe87b12da`, PC `0xffffffff80079e18`.

Originalsnapshot: `.build-tmp/gaunt-k2-clean2-f6750.warm.gz`, komprimerat
SHA256 `312ef133ae70d40e2c437772ea79f96884d0eee687daabd4776ce9c166494df2`.

## Fortsättningsfönster 7950→9150

Start från den korrekt återställda referensens f7950-dump,
`.build-tmp/nile-restored-first.warm.gz`. Samma parametrar, ordning
gammal/ny/ny/gammal:

| Par | Korrekt referens, ms | Optimerad, ms |
|---|---:|---:|
| 1 | 13316,1 | 11129,9 |
| 2 | 13269,8 | 10967,5 |
| Medel | 13292,95 | 11048,7 |

Båda par vanns: **16,88 % kortare medeltid**. Alla fyra slutdumpar matchar
detta fönsters eget SHA256:
`469865d377c99469b1133a9067435593754cde7dfa2f00e4c3770efd99ecba5a`.
Bildhash `0x7cb19642`, PC `0xffffffff800779a0`, ytterligare 41 swaps.
Lokala loggar/dumpar: `.build-tmp/nile-restored-next-{1..4}-{old,new}.*`.

## Fortsättning genom snapshot och testgräns

Obruten 6750→9150 jämfördes också med 6750→7950, sparning/laddning och
7950→9150. Timerregister, CPU, RAM och alla övriga sparade bytes matchar,
med ett uttryckligt undantag: Voodoos `_renderFrame` är 6814 mot 6815.
Det är en byte vid nollbaserad filoffset 65307213. En ny maskin har
`_lastPresentedSwapCount = -1` och gör därför en extra presentation efter
reload; den härledda presentationslatchen sparas inte i format 18.
Ett extra vanligt bilduttag vid f7950 ändrar inte detta, eftersom den
obrutna maskinens presentationscache redan är aktuell.

Detta är **inte full snapshot-identitet över olika sessionsindelning**.
Det är däremot exakt timerfortsättning. Samtliga 12 A/B-dumpar ovan är
helt identiska inom sina respektive fönster, utan att maskera någon byte.
Den obrutna dumpens SHA256 är
`d2b288cbfdcd2bc6ea7e3ccd58be8e17500f609ae89d980801a979dec57a5521`.
Artefakter: `.build-tmp/nile-continuous-9150.*` och
`.build-tmp/nile-continuous-checkpoint-9150.*`.

## Tidigare missvisande mätserie

Innan loaderfelet upptäcktes gav samma timeroptimering endast 0,57 % kortare
medeltid och två vunna par av fyra. Där användes mask 0 efter laddningen,
så serien prövade inte den avsedda aktiva timervägen. Den används inte som
evidens för fartvinsten. Lokala filer: `.build-tmp/nile-clock-{1..8}-{old,new}.*`.

## Slutverifiering

- 25620 timer-/snapshotkontroller passerar: alla aktiva masker, extrema
  reload-/counter-/tickvärden, IRQ/watchdog, per-call-avrundning, stale cache
  i båda riktningar samt verklig SaveMemoryMap/LoadMemoryMap-roundtrip och
  efterföljande klocksteg utan ROM.
- 2528 COP1-dispatchkontroller passerar även i slutbygget.
- Release Probe och UI bygger utan fel; UI har 33 befintliga varningar.
- `git diff --check` passerar. Tidigare Lua-/lokala artefaktändringar lämnas
  utanför denna checkpoint.

Testloggar: `.build-tmp/nile-final-checks.log`, `nile-final-cop1-checks.log`,
`nile-final-build.log` och `nile-final-ui-build.log`.
Nästa CPU/JIT-profil måste tas med denna återställda timerstate; äldre
stackandelar beskriver ett annat arbetsinnehåll.

## Reproduktion och artefakter

```sh
dotnet build tools/GauntletProbe/GauntletProbe.csproj -c Release --no-restore -m:1 /clp:ErrorsOnly
env EUTHERDRIVE_GAUNTDL_TEST_NILE_CLOCK=1 \
 dotnet tools/GauntletProbe/bin/Release/net8.0/GauntletProbe.dll

env DOTNET_TieredCompilation=0 \
 EUTHERDRIVE_GAUNTDL_EXPERIMENT_RUNTIME_BLOCK_FAST_CACHE=1 \
 EUTHERDRIVE_GAUNTDL_RUNTIME_BLOCK_FAST_CACHE_SIZE=4096 \
 EUTHERDRIVE_GAUNTDL_EXPERIMENT_FIFO_PACKET_MEMBERSHIP=1 \
 EUTHERDRIVE_GAUNTDL_SAVE_FINAL_STATE=.build-tmp/nile-manual-final.warm.gz \
 scripts/run-gauntdl-probe-warm.sh \
 /home/nichlas/roms/MAME/Midway/Vegas/gauntd 7950 90000 \
 .build-tmp/gaunt-k2-clean2-f6750.warm.gz 6750
```

Välj nytt artefaktnamn vid upprepning. För jämförelsen ovan väljs den frysta
referens-DLL:n med `EUTHERDRIVE_GAUNTDL_PROBE_DLL`.
Loggar och fulla slutdumpar: `.build-tmp/nile-restored-fast-{1..8}-{old,new}.*`.
Endast faktiskt körd variant finns för varje index. ROM/snapshots och
frysta byggen checkas inte in.
