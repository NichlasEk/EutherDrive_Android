# Gauntlet: första genererade kompaktblocket

## Resultat och gräns

En opt-in IL-generator ersätter nu den handskrivna sjuinstruktionsloopen vid
`0xffffffff800c9c98`. Den emitterar registerindex, omedelbara operander,
branchmål och delay slot från MIPS-orden med `DynamicMethod`, utan den gamla
expression-backendens registerprolog/epilog. Körning återgår efter ett block;
detta är ännu inte en kedjad loop-JIT eller registerallokering över block.

Fyra ordningsbalanserade långpar gav i medel 11 328,4 ms för handskriven
referens och 11 304,4 ms för genererad kod. Skillnaden, 0,21 procent, är för
liten och ojämn för att räknas som hastighetsvinst: två par vanns av vardera
varianten. Resultatet är en fungerande generator med ungefär referensens fart,
inte ett nytt spelbarhets- eller prestandagenombrott. Två separata safe-batch-
kontroller gav 11 016,8 och 11 534,8 ms; de visar också mätvariation och stödjer
inte något påstående om förbättring mot tolken i denna serie.

Prototypen behålls som avstängd arkitekturreferens för nästa steg. Befintliga
startskript och standardval är oförändrade. Inga commits eller pushar gjordes.

## Korrekthet

- Kort replay: hash `0x40bd6aae`, PC `0xffffffff80119038`, FIFO
  `25628590/2486968`, draw `439076`, swaps `3847`.
- Alla tolv långprov: hash `0xe87b12da`, PC `0xffffffff80079e18`, FIFO
  `27113239/2660774`, draw `474871`, swaps `3877`.
- Riktade referensjämförelser omfattar 80 kombinationer av signed/wrap-gränser,
  råa FPR-bitmönster och båda kontrollflödesvägarna. Sidoutgången provas genom
  att `swc1` faktiskt ändrar ordet för RuntimeMainState. GPR, FPR, PC, senaste
  instruktion, lagrat ord och exekverat instruktionsantal jämförs.
- Runtime-wrappen behåller CP0-räkning och probe-debt. De långa replayproven
  kontrollerar observerbart slutresultat; ingen fullständig RAM/state-dump-
  jämförelse eller verifiering av Android, ljud och interaktivt spel ingår.

Slutkontrollen efter probe-utskriftsändringen passerade åter alla 80 riktade
fall. Ett ytterligare långprov gav samma orakel, 11 314,2 ms och
`generatedRuns:186538` över `1305766` instruktioner. Release-byggen av Core,
GauntletProbe och UI passerade med noll fel (varningar finns kvar).

Admission kräver fortfarande den exakta referenssignaturen på den kända
adressen. Generatorn avkodar den begränsade formen `lwc1/addiu/addiu/slti/
swc1/bne/addiu`; den är inte godkänd för godtycklig kod. Som den handskrivna
referensen antar den att de validerade kodorden förblir oförändrade under
denna replay. Detta är inte generell hantering av självmodifierande kod.
På runtime utan stöd för dynamisk kod används ordinarie fallback.

## Reproduktion

Bas: commit `5d1ca6a1` plus denna arbetskopia. Warm snapshot:
`.build-tmp/gaunt-k2-clean2-f6750.warm.gz`, SHA-256:
`312ef133ae70d40e2c437772ea79f96884d0eee687daabd4776ce9c166494df2`.

```sh
dotnet build tools/GauntletProbe/GauntletProbe.csproj -c Release --no-restore -m:1 /clp:ErrorsOnly

EUTHERDRIVE_GAUNTDL_TEST_GENERATED_CONDITIONAL_BLOCK=1 \
scripts/run-gauntdl-probe-warm.sh /home/nichlas/roms/MAME/Midway/Vegas/gauntd \
6750 90000 .build-tmp/gaunt-k2-clean2-f6750.warm.gz 6750

EUTHERDRIVE_GAUNTDL_EXPERIMENT_RUNTIME_GENERATED_CONDITIONAL_BLOCK=1 \
EUTHERDRIVE_GAUNTDL_EXPERIMENT_RUNTIME_COMPACT_CONDITIONAL_BLOCK=0 \
scripts/run-gauntdl-probe-warm.sh /home/nichlas/roms/MAME/Midway/Vegas/gauntd \
7950 90000 .build-tmp/gaunt-k2-clean2-f6750.warm.gz 6750
```

Växla flaggorna till `0/1` för handskriven referens och `0/0` för safe batches.
Testläget är en separat engångsprocess: det ändrar register och scratch-RAM,
skriver ingen snapshot och avslutar innan vanligt replay.

Loggar: `.build-tmp/generated-jit-20260910-*.log`.
Långseriens ordning var referens/genererad/safe/referens/genererad/genererad/
referens/referens/genererad/genererad/referens/safe. De första två körningarna
var uppvärmningskontroller; de fyra paren är körning 4–11. Dessa kördes med
samma Core-DLL. Probe-utskriften utökades därefter så att även enbart den nya
flaggan visar `generatedRuns`; det förändrar inte den mätta Core-koden.

## Efterföljande flervarvsförsök: korrekt men ingen stabil vinst

På användarens nästa begäran prövades en IL-genererad bakåtkant med GPR/FPR
i lokala variabler över flera varv. En separat loopflagga gav admission bara
i vanlig steady-state-context (`0x400c/0x400d/0x400f`), utanför hostägda
game-task/phase-five-contexts. Före varje varv kontrollerades RAM-intervall;
MMIO, diagnostikskrivningar och skrivningar som överlappar blockets kod gav
side exit. Register skrevs tillbaka vid utgång, inklusive budgetgränser.

En viktig korrekthetsdetalj är att `AdvanceNileClock` avrundar varje anrop
separat. Därför kördes CP0/Nile-bokföring fortfarande per 7-instruktionsvarv,
eller 5 instruktioner vid store-side-exit. Att summera hela loopens tid till
ett enda klockanrop skulle inte bevara samma timersemantik.

Resultat:

- 80 befintliga blocktester, 108 flervarvstester och 9 guardtester passerade.
  Flervarvstesterna jämförde även CP0, timer-pending, Nile-register och IRQ-state
  och täckte bland annat budget 0/6/7/13/14/20/21/28/35 och CP0-wrap.
- Kortprovet och alla tio långprov matchade tidigare bildhash, PC, FIFO,
  draw- och swapräknare exakt.
- Samma 1 305 766 instruktioner kördes i 93 288 genererade anrop i stället
  för 186 538: nästan exakt två varv per inträde.
- Efter två inledande kontrollkörningar gav fyra ordningsbalanserade par
  11 136,1 ms i medel för ett varv och 11 100,5 ms för flera varv, en skillnad
  på 0,32 procent. Medianen var däremot sämre: 11 066,95 mot 11 108,15 ms.
  Två par vanns och två förlorades. Detta räknas inte som stabil vinst.

Flervarvskoden, loopflaggan och de tillhörande testerna är därför borttagna
ur de aktiva projekten. Föregående genererade enblocksreferens är kvar.
Prototypen sparades före borttagning för reproduktion:

- `.build-tmp/generated-loop-20260910-candidate.cs`
- `.build-tmp/generated-loop-20260910-candidate-checks.cs`
- `.build-tmp/generated-loop-20260910-checks.log` och `*-guards.log`
- `.build-tmp/generated-loop-20260910-short.log`
- `.build-tmp/generated-loop-20260910-long-{1..10}-{generated|loop}.log`

Långserien kördes generated/loop/generated/loop/loop/generated/generated/
loop/loop/generated. Core-DLL under mätningen hade SHA-256
`af1868caca528bb3204482ef415d2201a6a9e82baff135bc2b8cd6b6402ecfc0`.
Prototypfilerna är lokala experimentartefakter, inte en aktiverad backend.
Efter borttagningen passerade Release-byggen av Core/probe/UI, de 80
ursprungliga blocktesterna och det korta replay-oraklet på nytt.

## Nästa beslut

Nästa försök med två större block är nu genomfört och förkastat efter 1,93
procent regression. Det gav däremot en byte-exakt jämförelse av hela det
sparade maskintillståndet. Se
[större block-experimentet](gauntlet-dl-large-block-experiment-2026-09-10.md).

Den här korta loopens halverade antal återgångar gav inte stabil nettovinst.
Profilera därför större sammanhängande CPU-arbete och den uppskattade
tidsbesparingen innan fler block antas i generatorn. Nya guards och
timerbokföring måste räknas in i kostnaden; återuppliva inte samma
tvåvarvsvariant oförändrad. Bredare admission kräver ett uttryckligt
kodinvalideringskontrakt; replayens immutable-antagande får inte göras till
generell standard.
