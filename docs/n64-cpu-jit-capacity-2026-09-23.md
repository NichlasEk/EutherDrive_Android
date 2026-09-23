# CPU-JIT: 1024 bevarade kodversioner

Fortsättning på [realtidsplanens C1-spår](n64-realtime-agent-plan.md) efter
[RE2:s GPU-väntoptimering](n64-re2-texture-barrier-speed-2026-09-23.md).
Detta är en generell cachegräns, utan spel- eller PC-adressvillkor.

På RE2 slot 1 noterade ett separat diagnostikbygge 17,21 miljoner försök att
köra JIT-block men bara 10,37 miljoner utförda block under 15 gästsekunder.
Flera heta adresser nådde kodversionsgränsen upprepade gånger. Hypotesen var
att större retention skulle minska återkommande kompilering/avslag. Enda
källändringen är `CpuJitMaximumVersions` från 512 till 1024.

Samma kopierade RE2-state, native-GPU-bibliotek, CPU-affinitet `8,9`, neutral
input och miljö som i GPU-rapporten användes i en ABBA-serie. För gästsekund
5–15 (10,002 faktiska gästsekunder) blev medeltiden 22,557 s med 512 och
21,983 s med 1024: **2,61 % högre genomströmning**, motsvarande cirka
44,34 respektive 45,50 % realtid. Samtliga tre kontrollpunkter, bildrutor,
ljud, RAM och slutligt CPU-state matchade exakt. Rådata finns lokalt i
`.build-tmp/n64-re2-speed-2026-09-23/abba-cpu-cache1024/`.

Mario 64 USA kördes med samma native-bibliotek till gästsekund 90. I ett
matchat par tog gångsegmentet 70–90 26,249 s med 512 och 25,985 s med 1024
(76,20 respektive 76,98 % realtid). Alla kontrollpunkter, bilder, ljud,
position och slutligt RAM matchade; den lilla tidsskillnaden är inte en
statistiskt säker Mario-vinst. Resultaten är i `mario-cpu-baseline-90/` och
`mario-cpu-cache1024-90/` under samma lokala arbetskatalog.

`--check-cpu-jit-cache` klarade retention, kall kodersättning, återställning
och adressgräns med `nativeCap=1024`. Det större `--check-cpu-jit` hann
passera över fem miljoner avkodnings-/cykelfall men avbröts efter flera
minuter innan hela differentialsviten var klar. Det räknas inte som ett
godkänt fulltest. De deterministiska RE2- och Mario-körningarna samt det
riktade cachetestet är bevisen för denna avgränsade ändring.

Den större cachen kan förbruka mer värdminne. Fortsätt med C1:s mätning av
exklusiv dispatchkostnad och R1:s RSP-slicearbete; denna gränsökning ensam
är långt ifrån 100 % realtid. Kör längre sekvenser och ytterligare spel innan
eventuell ytterligare ökning av cachegränsen.
