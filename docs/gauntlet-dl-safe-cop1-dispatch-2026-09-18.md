# Gauntlet DL: återanvänd COP1-avkodningen i CPU-blockvägen

## Resultat

Den befintliga safe-block-vägen återanvänder nu sina avkodade COP1-operander.
I Linux-replay 6750→7950 vann slutvarianten tre av fyra växlade par:
**3,02 % kortare median och 3,78 % kortare medeltid** mot oförändrad
`c117b73e`. Ett senare fönster, 7950→9150, var neutralt: 0,14 % kortare
medeltid och ett vunnet par av två. Förbättringen är därför belagd lokalt i
första fönstret, inte som en generell procentsats för hela spelet.

Senare samma dag upptäcktes att warm-loadern inte återställde Nile-timrarnas
aktivitetsmask. Mätningarna i denna rapport gäller den dåvarande basen med
bortkopplat timerarbete. Använd den nya referensen i
[Nile-rapporten](gauntlet-dl-nile-clock-2026-09-18.md) för fortsatt arbete.

Detta optimerar den avkodade mellannivån i CPU/JIT-arbetet. Ingen ny
gästkodgenerator, PC-specifik region, tidsaggregering eller GPU-väg införs.
Det första fönstret ger ungefär 4,28 swap-kommandon/s med kandidaten;
det är fortfarande långt från spelbart tempo.

## Ändring och korrekthet

`ExecuteRuntimeSafeInstruction` skickade tidigare COP1/COP1X tillbaka till
`Execute`, som avkodade instruktionen igen innan den nådde COP1-hjälparna.
Nu återanvänds blockets registerindex, format och funktionsfält:

- COP1-registerflyttar använder samma GPR/FPR/FCR-operationer direkt.
- Single-format anropar samma `ExecuteCop1SingleFormat` med avkodade fält.
- Övriga COP1-format och COP1X använder befintliga helpers direkt.
- DADDI/DADDIU utför samma 64-bitars addition direkt i safe-vägen.
- COP1 med opcodeprofilering använder fortsatt originaldispatchen, så
  profileringsräknarna behåller sina tidigare uppdateringar.

Inga instruktioner läggs till i safe-block-admission. PC, delay slots,
instruktionsbudget, klocksteg, stores och side exits hanteras fortsatt av
befintliga anropare. Fallbacksemantik, inklusive unsupported/halt, bevaras.

2528 riktade differentialfall jämför den nya vägen med den generella
`Execute`-tolken. De jämför primitiva CPU-fält och register-/räknararrayer,
med registeröverlapp, register noll, extrema heltals-/flyttalsbitmönster,
varierade FCR-värden, branchvillkor, COP1X-aritmetik, immediates och
COP1-profilering av/på. Unsupported single-format ingår som halt-kontroll.
Det är CPU-dispatchtester, inte ett generellt bevis för all emulering.

```sh
env EUTHERDRIVE_GAUNTDL_TEST_SAFE_COP1_DISPATCH=1 \
 dotnet tools/GauntletProbe/bin/Release/net8.0/GauntletProbe.dll
```

## Mätningar

Release, `DOTNET_TieredCompilation=0`, 90000 steg per probe-anrop,
cache4096 och FIFO-medlemskapsindex på. NCC-experimentet och GPU-vägarna
är av. Samma warm-runner används för båda byggena. Referensen är fryst i
`.build-tmp/step-split-baseline/`, från oförändrad runtime vid `c117b73e`.
Inga egna samtidiga byggen eller profilerare under tidsserierna.

6750→7950, ordning gammal/ny/ny/gammal/gammal/ny/ny/gammal:

| Par | Referens, ms | Kandidat, ms |
|---|---:|---:|
| 1 | 10466,6 | 9465,8 |
| 2 | 9617,7 | 9707,8 |
| 3 | 9974,9 | 9536,4 |
| 4 | 9742,7 | 9586,6 |
| Medel | 9950,475 | 9574,15 |
| Median | 9858,8 | 9561,5 |

Första referenskörningen är märkbart långsammare än övriga, vilket bidrar
till den större medelvinsten. Den är kvar i tabellen; resultatet ska inte
tolkas som en statistiskt fastställd förbättring för andra värdlastlägen.

Alla åtta fullständiga dekomprimerade slutdumpar matchar
`32f8e9c49dcc5498c044bb602156ef6e82fe5dec64f27c99749f3a6fe298053a`.
Bildhash `0xe87b12da`, PC `0xffffffff80079e18`, 41 swap-kommandon.

7950→9150, ordning gammal/ny/ny/gammal:

| Par | Referens, ms | Kandidat, ms |
|---|---:|---:|
| 1 | 9742,9 | 9897,9 |
| 2 | 10025,5 | 9842,7 |
| Medel | 9884,2 | 9870,3 |

Detta fönster startar från första seriens referensdump vid 7950, inte från
ett nytt spel/annan bana. Dess eget referens-SHA256 är
`d4517e6a8fa515842bb2a081f87f77f128ccc3bd190d6b3506eb6d7e7a868982`.
Alla fyra slutdumpar matchar. Resultatet är prestandamässigt neutralt.

## Försök som inte behölls

Två varianter delade upp stora `Step()` i kall servicekod och
enkelinstruktionskörning. Första varianten gav 0,03 % kortare medeltid men
sämre median. Explicit inlining gav 0,24 % kortare medeltid. Båda vann
endast ett av fyra par och togs bort. En första COP1-variant som bara hoppade
över den yttre dispatchen gav 0,81 % kortare medeltid och två vunna par;
den ersattes av den fullständigare avkodningsvarianten ovan.

Samtliga 36 replay-slutdumpar över försök och slutvariant var exakta mot
respektive referens. Endast COP1-ändringen ovan finns kvar i runtime.

Slutkontroll: Release Probe och UI bygger utan fel (UI: 33 befintliga
varningar). De 2528 differentialfallen passerar även efter varierad FCR-
initialisering; blockcache-regressionen passerar 45 kontroller.
`git diff --check` är ren. Runtime, differentialtester och rapport hör till
samma COP1-checkpoint.

## Artefakter och fortsättning

Lokala loggar/dumpar: `.build-tmp/step-split-{1..8}-{old,new}.*`,
`step-inline-{1..8}-{old,new}.*`, `safe-cop1-{1..8}-{old,new}.*`,
`safe-cop1-decoded-{1..8}-{old,new}.*` och
`safe-cop1-next-{1..4}-{old,new}.*`. Endast den faktiskt körda varianten
finns för varje index. ROM-/snapshotdata checkas inte in.

Nästa större CPU/JIT-arbete bör mäta kvarvarande dispatch-/klock-/helperkostnad
från denna bas och välja bred kodtäckning. Denna begränsade förbättring ger
inte stöd för att återinföra de redan förkastade PC-specifika JIT-regionerna.
Arbetet gäller Linux enligt användarens uttryckliga korrigering.
