# Gauntlet: första GPU-provet för textursampling

## Beslut

GPU-spåret är värt ett större integrationsförsök. Ett fristående Vulkan-
computeprov på RTX 4090 ger exakt samma RGBA som C#-samplern för 558 872
samplingsanrop från 16 verkliga trianglar. Vinsten beror starkt på vad som
överförs. Detta är **inte** en komplett GPU-renderare och gör ännu inte
spelet snabbare i normal körning.

Nästa steg bör flytta en sammanhängande del av rasterkedjan till GPU:n med
kvarliggande texturer och färg-/djupbuffertar. Att skicka hela texturminnet
eller färdigräknade koordinater för varje pixel vid varje triangel är inte
slutarkitekturen. Börja med en inspelad draw-sekvens och kontrollera dess färg-
och djupbuffertar mot mjukvarurenderaren innan den kopplas till live-CPU:n.

## Uppmätt avgränsning

Två fönster med åtta trianglar vardera, interna render-frame 6732 och 6736.
Det andra fönstret hoppar över 120 stora kandidat-trianglar. Dessa två närliggande
fönster är ett begränsat urval, inte bevis för alla banor eller spelmoment.

558 872 samplingsanrop är inte lika många färdiga skärmpixlar: två TMU:er kan
samplas för samma pixel. Följande delar körs på GPU:n:

- Perspektivdivision från riktiga 64-bitars S/T/W-värden.
- Mjukvarans mättning/avrundning till 24.8-koordinater och negativ-W-regel.
- Redan vald LOD, clamp/wrap, bank- och byteadressering.
- Texturformatavkodning med samma NCC-tabeller och bilinjär heltalsfiltrering.

Triangle coverage, gradientinterpolering, själva LOD-valet, TMU-kombinering,
fog, depth, alpha, framebuffer och presentation ingår inte. Resultatet jämförs
vid samplerns RGBA-utgång, inte vid en färdig GPU-framebuffer.

## Tider

Summan av de 16 batchernas medianer, **inte** en sammanhängande spelmätning:

| Väg | Tid |
| --- | ---: |
| Verklig C#-sampler, seriell replay | 60,107 ms |
| Verklig C#-sampler, åtta workers | 20,394 ms |
| GPU med full uppladdning och återläsning | 29,933 ms |
| GPU med nya requests/NCC men kvarliggande texturer | 9,606 ms |
| GPU med alla indata redan uppladdade, inklusive requests | 1,414 ms |

I dessa prov är requests-uppladdning cirka 2,1 gånger snabbare än åtta CPU-
workers. Full uppladdning är däremot långsammare. Resident-fallet är optimistiskt:
även de per-pixel-anrop som GPU:n ska bearbeta ligger redan där.
Kernel-tidsstämplarna summerar till cirka 0,075–0,094 ms; de får inte användas
som ett mått på hela renderingskostnaden.

Alla vägar gör 12 repetitioner per batch, kastar tre uppvärmningar och väljer
medianen av nio. Host-GPU-tider omfattar memcpy till staging när tillämpligt,
uppladdning, submit, fence-väntan, resultatöverföring och CPU-kopia av resultat.
Shader/pipeline/device/buffer-setup tar separat ungefär 198–241 ms per process
i den mätta serien. Filer, inspelning, command-buffer-recording och NCC-
förberedelse ingår inte. CPU-replay omfattar requesttraversering och worker-
dispatch, inte exakt samma omgivande instruktioner som live-rasterloopen.

CPU-proven använder `DOTNET_TieredCompilation=0` för att undvika tierbyten
under korta batcher. Den första inspelningen med standard-tiering visade
instabila uppvärmningseffekter och används inte i tabellen. Andra jobb kördes
på datorn. Siffrorna är ett genomförbarhetsprov, inte en reproducerad total-
hastighetsvinst för emulatorn eller en garanti om 30 FPS.

## Korrekthet

- Alla 558 872 RGBA-värden är identiska i samtliga tre GPU-lägen och alla
  repetitioner; CPU-replay kontrollerar också varje resultat mot inspelningen.
- Åtta batcher passerar Khronos validering inklusive synchronization validation
  med noll rapporterade fel.
- Negativ kontroll ändrar en bit i facit och upptäcker exakt en avvikelse.
  Shadern läser aldrig facitordet.
- Inspelningen behåller kortoraklet `0x40bd6aae` och långoraklet `0xe87b12da`.
- Fullständig lång slutmaskin är fortfarande 98 901 914 byte med SHA-256
  `32f8e9c49dcc5498c044bb602156ef6e82fe5dec64f27c99749f3a6fe298053a`.

Faktisk täckning: format 10 och 9 har vardera 186 560 prover; format 1 och 0
vardera 92 876. 465 996 prover använder filtrering och perspektiv. LOD 1, 3,
4, 5 och 6 samt både clamp och wrap förekommer. Övriga implementerade format,
LOD-nivåer, extrema koordinater, W=0/negativt W och alla byteordningsvarianter
är inte därmed fullständigt validerade.

Av perspektivproverna använder 373 120 den reciproka W-faktor som den
befintliga CPU-kombineraren redan delar mellan TMU:erna. 92 876 gör själva
divisionen på GPU:n; återstående 92 876 prover är affina. Provet mäter alltså
inte GPU-beräkning av all förberedande perspektivmatematik. 208 174 prover har
negativ S eller T, men inget har W <= 0.

## Kod och artefakter

- `VoodooBringupBackend.GpuCapture.cs`: opt-in-inspelning och CPU-samplerreplay.
- `tools/GauntletGpuProbe/`: C++/Vulkan-runner, compute-shader och instruktioner.
- `.build-tmp/gpu-samples-optimized{.log,/}` och `gpu-samples-later{.log,/}`:
  mätta CPU-prover, GSC-filer och GPU-loggar.
- `.build-tmp/gpu-samples-first{.log,/}`: tidigt prov, inte tabellens tider.
- `.build-tmp/gpu-capture-final.warm.gz`: byte-exakt slutmaskin med inspelning.

Inspelningen kräver byggflaggan `-p:GauntletGpuCapture=true`. I normalbygget
kompileras anropsplatserna bort helt. Inga ROM-data eller inspelningar ska
checkas in och ingen GPU-väg slås på för vanlig spelkörning.

Normal Release för Core/probe och UI har återbyggts med noll fel. Ett långt
slutprov med capture-miljövariabeln satt men **utan** byggflaggan skapade ingen
inspelning och behöll samma fullständiga snapshot-hash. Artefakter:
`.build-tmp/gpu-normal-final.log` och `.build-tmp/gpu-normal-final.warm.gz`.
PCI-regressionstestet passerar fortsatt med noll allokeringar i den avstängda
loggvägen. SPIR-V-validering och shell-syntaxkontroll passerar också.

## Nästa avgränsade leverans

1. En replay av riktiga draw-kommandon med texturändringar och definierad
   ordning, inte en lista med redan interpolerade pixelanrop.
2. GPU-genererade koordinater och samma samplingskärna, därefter färg-/djup-
   kedjan för ett uttryckligen avgränsat vanligt render-state.
3. Behåll buffertar på GPU:n, batcha arbete och synkronisera vid verkliga
   läs-/skrivberoenden. Överlappande trianglars blend/depth-ordning måste bevaras.
4. Kräv korrekt färg- och djupresultat samt förbättrad komplett replaytid
   inklusive uploads/readbacks innan live-integrering. CPU-JIT återstår sedan
   fortfarande som en separat stor flaskhals.
