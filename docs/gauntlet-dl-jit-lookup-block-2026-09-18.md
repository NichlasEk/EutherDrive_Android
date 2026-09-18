# Gauntlet DL: genererat lookup-/branchblock

## Resultat och beslut

Tolv växlade körningar gav **0,96 % kortare median** och **0,51 % kortare
medeltid**; fem av sex närliggande par vanns. Det är en liten positiv signal
med överlappande tider, inte ett tydligt större fartlyft. Varianten arkiveras
som lokal prototyp men behålls inte i runtime. Standardkod och tidigare
FIFO-/cachevinster är oförändrade; inga nya flaggor finns i slutbygget.

| Par | Safe batch, ms | Genererad runner, ms |
|---|---:|---:|
| 1 | 10064,2 | 10031,8 |
| 2 | 10246,0 | 10330,0 |
| 3 | 10257,5 | 10212,7 |
| 4 | 10198,9 | 10085,9 |
| 5 | 10169,2 | 10122,1 |
| 6 | 10205,1 | 10046,6 |
| Median | 10202,00 | 10104,00 |
| Medel | 10190,15 | 10138,18 |

Alla tolv slutdumpar och profilkörningen matchar hela den dekomprimerade
referensen: `32f8e9c49dcc5498c044bb602156ef6e82fe5dec64f27c99749f3a6fe298053a`.
Bildhash `0xe87b12da`, 41 swap-kommandon. Det är en lokal replayverifiering,
inte generell JIT-korrekthet eller test av Android/ljud/interaktiv spelbarhet.

## Ny profil och val av kandidat

Efter FIFO-PC-filtervinsten kördes safe-block- och övergångsprofilerna med
snabbvägar aktiva. De visar 57308389 safe-block-instruktioner. De största
blocken `801069f4` och `80106b1c` är redan prövade i det förkastade
[stora block-försöket](gauntlet-dl-large-block-experiment-2026-09-10.md).
Den tidigare korta float-copy-loopen återanvändes inte heller.

Ny kandidat: `0xffffffff80103e78`, sju instruktioner (`lui/lw/addiu/sll/addu/lw/lw`)
följt av `beq` och `nop` som delay slot. Profilen gav 67889 blockentryn;
475223 safe-instruktioner, cirka 0,83 % av de profilerade safe-instruktionerna.
Detta är frekvens/instruktionsandel, inte uppmätt CPU-tid för blocket.
Blocket har inga stores och testar därför kodgenerering utan nya store-side-exits.

Viktig avgränsning: referensens safe-batch-väg sammanfogar redan branchparet.
En niostegsrunner här tar bort opcode-dispatch inne i blocket, **inte fler
återgångar till Step**. Den är inte den regions-JIT som behövs för att minska
dispatcherfrekvensen över flera block. Liten täckning begränsar möjlig totalvinst.

## Prototyp

En separat `DynamicMethod` emitterade registeroperander, 32-bitars wrap/sign-
extension, tre läsningar via samma minneshelper samt båda hoppmålen direkt
till IL. Ingen opcode-switch körs under exekveringen. Klock-/Nile-/instruktions-
bokföring behölls **per instruktion**, inklusive branch och delay slot.
GPR0, PC, LastFetchedInstruction, branchräknare och probe-debt behölls.

Admission krävde minst nio återstående steg, samma safe-batch-/branchflaggor,
avstängd tracing och stöd för dynamisk kod. Första admission validerade hela
signaturen. Entry-, branch- och delay-orden kontrollerades vid återanvändning.
Inre sekventiell kod hade samma immutable-antagande som befintlig blockcache;
detta var inte generell hantering av självmodifierande kod. Reset rensade runnern.

## Riktade tester

130 jämförelser mot safe-batch-vägen passerade. De jämförde GPR/FPR/CP0,
PC/senaste instruktion, instruktionsantal, probe-debt, branchräknare,
timer-pending och Nile-register/IRQ-state:

- Båda hoppvägarna och signerade lastvärden.
- Index som prövar wrap i `sll`/`addu` och sign-extension.
- CP0-wrap och Compare-passering.
- Aktiv Nile-timer 2, inklusive expiry, med CP0-steg 1537. Den kvoten
  avslöjar fel om individuellt avrundade klocksteg felaktigt slås ihop.
- Budget 0/1/2/7/8/9/10 och fallback vid ändrat entry-/branch-/delay-ord.
- Ändrade kodord efter cachad kompilering samt reset av runnern.

## Tidsmetod

Normal Release, samma binär med experimentflagga av/på, inga profilerare.
`DOTNET_TieredCompilation=0`, replay 6750→7950, 90000 steg. Den större
4096-posterscachen och FIFO-medlemskapsindexet är på i båda lägena; senaste
LINQ-förbättringen ingår. Inga samtidiga egna byggen eller tunga kontroller.
Andra värdjobb lämnas orörda.

Ordning: av/på/på/av/av/på/på/av, sedan på/av/av/på.
Runnern exekverade **67878 block / 610902 instruktioner** per JIT-replay.
Resterande entryn föll tillbaka, exempelvis vid kort återstående budget.

Artefakter: `.build-tmp/jit-next-profile.log`, `jit-next-code.log`,
`jit-lookup-{trial}-{mode}.log` och respektive `-final.warm.gz`.
ROM-härledda filer checkas inte in.

## Arkiv och nästa steg

Före borttagning sparades egen kandidatkod och testharness lokalt:

- `.build-tmp/jit-lookup-candidate.cs`
- `.build-tmp/jit-lookup-candidate-checks.cs`

För att återkoppla prototypen behövs partial-filen, dispatch vid
`GeneratedLookupEntry` efter ordinarie safe-batch-admission, reset av delegate
och run-räknare samt probe-test-/statuskoppling. Den tidigare testflaggan var
`EUTHERDRIVE_GAUNTDL_TEST_GENERATED_LOOKUP_BLOCK`; experimentflaggan var
`EUTHERDRIVE_GAUNTDL_EXPERIMENT_RUNTIME_GENERATED_LOOKUP_BLOCK`.
Filerna är inte en aktiverad backend eller ett färdigt generellt JIT-API.

För att faktiskt minska `Step`-återgångarna måste nästa kandidat korsa en
gräns som dagens safe-batch inte redan slår ihop. I samma funktion följer
bland annat branch-likely, vilket kräver korrekt annullerad delay slot och
kontroller för interrupt, special-PC-service och återstående budget vid
övergången. Det måste jämföras mot riktig `Step`/`RunProbeSteps`, inte bara
mot en safe-batch-funktion. Implementera inte ännu en generell C#-kedjeloop;
tidigare sådana försök var långsammare. Ingen flervarvs-/flerblocksrunner
implementerades i denna omgång.

## Slutkontroll efter återställning

Core och probe-entrypoint har ingen kvarvarande diff mot `b968c6cb`.
Release-byggen för GauntletProbe och EutherDrive.UI passerade med noll fel
(352 respektive 33 varningar). FIFO-PC-filtertestet passerade 2035 fall;
100000 syntetiska anrop gav 12000000 byte i den gamla jämförelsevägen och
noll i den nya. Även de verkliga type3/type4-anroparna gav noll allokeringar.

En sista replay med återställd runtime matchade samma fullständiga SHA-256
ovan: totalt 14 exakta slutdumpar i denna omgång. Logg och dump finns i
`.build-tmp/jit-lookup-restored.log` respektive
`.build-tmp/jit-lookup-restored-final.warm.gz`.
