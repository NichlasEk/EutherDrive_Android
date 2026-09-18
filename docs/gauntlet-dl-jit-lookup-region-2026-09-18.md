# Gauntlet DL: genererad JIT-region över blockgränser

## Beslut

Ingen av de två varianterna gav en tydlig totalvinst. Första variantens
median var 0,23 % kortare; den förenklade variantens median var 0,86 % längre.
Runner, testkopplingar och RAM-signaturhelper har därför tagits bort ur
runtime och arkiverats lokalt. Tidigare FIFO-/cachevinster är orörda.

## Vad som prövades

Uppföljning till [lookup-blockförsöket](gauntlet-dl-jit-lookup-block-2026-09-18.md).
En `DynamicMethod` kör regionen `0xffffffff80103e78` till `0xffffffff80103ec4`:
det första niostegsblocket plus tre branch-likely-par och mellanliggande
register-/load-instruktioner. Den längsta vägen är 19 instruktioner.
Ingen generell C#-kedjeloop, store-barriär eller länkat blockcache infördes.

Till skillnad från föregående försök minskar detta verkliga `Step`-anrop.
Ett riktat test av den längsta vägen gav **10 anrop i referensen mot 1 i JIT**,
med samma tillstånd. Det bevisar lokal dispatchreduktion, inte speltempo.

## Korrekthet och begränsningar

- Endast den befintliga steady-state-vägen, med samma safe-batch-admission.
- Minst 19 återstående instruktioner krävs; kortare budget faller tillbaka.
- Alla dataadresser begränsas till RAM; inga stores eller MMIO-operationer.
- Hela kodsignaturen kontrolleras vid varje entry, även inre instruktioner.
- CP0, Nile och instruktionsräknare uppdateras per instruktion, aldrig som
  ett avrundat samlat klocksteg.
- Branch-likely annullerar delay slot när hoppet inte tas. Även inaktiva
  `_pendingBranchTarget`/`_immediatePcOverride` och deras aktiva flaggor bevaras.
- Inga special-PC-services ligger i regionen. QIO-taskkontext som behöver
  separat service tillåts inte.

Viktig avgränsning: befintliga steady-state-`Step` går direkt till
`ExecuteInstruction` när safe-batch/branchpair inte kan köras, och passerar
då inte den långsamma vägens interrupt-entry. Regionen behåller den policyn;
den introducerar inte en ny generell avbrottsmodell. Test med pending timer
och aktiverade CP0-interrupt jämför mot just denna befintliga väg.

316 ROM-fria differentialfall passerade. Normala fall använder verkliga
`RunProbeSteps`; ett extra fall räknar verkliga `Step`-anrop med samma
budget/debt-konsumtion. Testerna omfattar alla hoppkombinationer, budget
0–25, maskvärden, signerade laddningar, index-wrap, CP0-wrap/Compare,
aktiv Nile-timer 2 med expiry, CP0-steg 1537 och ändring av vart och ett av
de 19 kodorden efter cachad kompilering. Reset tömmer runnern.

En tidig replay aktiverade inte runnern eftersom första RAM-guardversionen
var för snäv. Den räknas inte som en JIT-prestandamätning. Efter korrigering
körs regionen **67866 gånger**, av **67889 försök**; 23 fall avvisas vid
admission (bland annat kort återstående budget), inga vid RAM/kodkontroll.

## Mätmetod

Linux, normal Release, `DOTNET_TieredCompilation=0`, samma binär av/på per
serie. Replay 6750→7950, 90000 steg per probe-frame. Tidigare cache4096,
FIFO-medlemskapsindex och allokeringsfria FIFO-PC-filter ingår i båda lägena.
Ingen profilerare eller egna samtidiga byggen under tidmätningen.

Ordning per serie: av/på/på/av/av/på/på/av. Hela dekomprimerade slutdumpen
jämförs efter varje körning med referensen:
`32f8e9c49dcc5498c044bb602156ef6e82fe5dec64f27c99749f3a6fe298053a`.
Bildhash `0xe87b12da`, 41 swap-kommandon. Probe-fps är inte spelets fps;
Android, interaktiv input och ljud är inte verifierade av detta replaytest.

## Första variant: individuella kod-/PC-kontroller

| Par | Referens, ms | Region, ms |
|---|---:|---:|
| 1 | 10161,9 | 10194,6 |
| 2 | 10319,1 | 10238,7 |
| 3 | 10129,6 | 10445,4 |
| 4 | 10569,0 | 10185,5 |
| Median | 10240,50 | 10216,65 |
| Medel | 10294,90 | 10266,05 |

Median 0,23 % kortare, medel 0,28 % kortare; två av fyra par vanns.
Alla åtta slutdumpar var exakta. Ingen tydlig vinst.

## Andra variant: sammanhängande signaturkontroll

Oföränderliga special-PC-kontroller flyttades till kompileringen.
Alla 19 instruktionsord kontrolleras fortfarande live, nu med `SequenceEqual`
på RAM-bytespan (och ordvis fallback för big-endian-värdar). Samma 316
differentialfall passerade även denna variant.

| Par | Referens, ms | Region, ms |
|---|---:|---:|
| 1 | 10221,8 | 10429,4 |
| 2 | 10216,8 | 10234,6 |
| 3 | 10541,9 | 10379,4 |
| 4 | 10024,5 | 10172,5 |
| Median | 10219,30 | 10307,00 |
| Medel | 10251,25 | 10303,975 |

Median 0,86 % längre, medel 0,51 % längre; ett av fyra par vanns.
Även dessa åtta slutdumpar matchade hela referensen. Detta bevisar inte att
span-kontrollen i sig är långsammare än individuella kontroller: de två
varianterna mättes i separata serier med mätvariation. Det visar däremot
ingen användbar totalvinst för denna region i någon av serierna.

## Arkiv och fortsättning

Lokalt arkiv (inte checkat in):

- `.build-tmp/lookup-region-candidate.cs`
- `.build-tmp/lookup-region-candidate-checks.cs`
- `.build-tmp/lookup-region-candidate-hooks.patch`

Patchen innehåller CPU-/probe-kopplingar och signaturhelper. De två
källfilerna hör till den sista span-varianten. Tidigare flaggor var
`EUTHERDRIVE_GAUNTDL_EXPERIMENT_LOOKUP_REGION` och
`EUTHERDRIVE_GAUNTDL_TEST_LOOKUP_REGION`; de finns inte i slutbygget.

Loggar/dumpar: `.build-tmp/lookup-region-{trial}-{mode}.*` och
`.build-tmp/lookup-region-span-{trial}-{mode}.*`, trial 1–8, mode 0/1.
ROM-härledda artefakter checkas inte in.

Nästa kandidat bör väljas från en **ny tidsprofil efter FIFO-PC-filterfixen**.
Färre dispatch-anrop är nu verifierat lokalt, men den regionen gav ingen
tydlig end-to-end-vinst. Tidigare safe-block-profiler mäter frekvens, inte
tidskostnaden per region. Undvik att bara bygga fler små regioner eller
att slå ihop Nile-klocksteg för att få en snygg mikrobenchmark.

## Slutverifiering

Efter borttagningen har Core och probe-entrypoint ingen diff mot
`e926652a`. Release-byggen för GauntletProbe och UI passerade med noll fel
(352 respektive 33 varningar). FIFO-PC-filtertestet passerade 2035 fall,
och de verkliga FIFO-anroparna behöll noll uppmätta allokeringar.

Den återställda runtime-koden kördes ännu en gång: `runMs=10156.3`, samma
bildhash och samma fullständiga SHA-256 ovan. Logg och dump:
`.build-tmp/lookup-region-restored.log` och
`.build-tmp/lookup-region-restored-final.warm.gz`. Totalt matchade 18
fullständiga slutdumpar under arbetet: 16 mätkörningar, den tidiga
fallback-körningen och slutkontrollen. Ingen ny prestandaflagga är kvar.
