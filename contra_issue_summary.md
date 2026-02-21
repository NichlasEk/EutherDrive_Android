# Mega Drive-problem - sammanställning

Datum: 2026-02-21

## Kort nuläge
- Vi har ett kvarstående kärnfel i MD-emuleringen.
- Symtomen syns i flera spel:
1. `contra.md`: svart direkt eller fastnar tidigt.
2. `sonic3.md`: låser sig efter cirka 350 frames / loopar.
3. `sonic2.md`: bildskärmar går inte sönder korrekt, ringar tappas inte vid träff, död/physics blir fel.
- Slutsats: ett grundläggande CPU/memory-path-fel, inte en ren blit/render-fråga.

## Vad som är verifierat

### 1) Contra når illegal-path
- Logg visar:
1. `missing opcode handler op=0x124D pc=0x00BD98`
2. `exception ILLEGAL vec=0x0010 start=0x0005A4 pc=0x00BD98`
- Efter detta hamnar CPU i exception-loop (`0x60FE`) och spelet avancerar inte.

### 2) Vägen in i felet är reproducerad
- Senaste op-sekvens före illegal:
1. `0x005D60 op=0x303B`
2. `0x005D64 op=0x4EBB`
3. hopp till `0x00BD8E`
4. vidare till `0x00BD90`, `0x00BD92`
5. illegal vid `0x00BD98 op=0x124D`

### 3) MOVEM är inte huvudspåret
- Problemprofilen kvarstår utan att peka på MOVEM som rotorsak.
- Vi dokumenterar uttryckligen att MOVEM-spåret inte förklarar Contra/Sonic-felen.

### 4) Specialfall ska vara kvar
- Specialfallen för `0x33FC` och `0x33D8` är återställda enligt beslut och ska inte tas bort i fortsatt arbete.

## Arbetsdiagnos
- Trolig rotorsak ligger i 68k exekverings-/adresseringsflöde som ger fel kontrollflöde.
- Primära misstankar:
1. PC-relativ indexerad adressering (brief extension) ger fel adress.
2. Fel data in till dispatch (exempelvis värde som används av `0x303B`/`0x4EBB`-vägen).
3. Fel i ROM-normalisering/deinterleave som ger fel byteinnehåll i kod/dataområde.

## Instrumentering som finns
- Tillfällig trace för opcode `0x303B` i `EutherDrive.Core/MdTracerCore/opc/md_m68k_opeMOVE.cs`.
- Syfte: få exakt source-adress + läst värde precis före felhopp.

## Nästa steg när vi återupptar
1. Köra Contra-trace med `[OP303B]` aktiv.
2. Jämföra exakt adressberäkning och läst data mot `/home/nichlas/jgenesis/backend/genesis-core/`.
3. Göra minsta möjliga fix i CPU-adressering eller ROM-normalisering beroende på utfall.
4. Regressionstesta mot `contra.md`, `sonic3.md`, `Sonicnknuckles3.gen`, och Sonic 2-symtomen.
