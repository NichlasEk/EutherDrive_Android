# Madou Debugging Analysis - TOCODEX

## Problem
Madou (madou.md) visar korrupt grafik. Root symptom: CRAM DMA använder källadress `0xFF94F8`, men palettdata skrivs till `0xFF95F8` (offset +0x100), så CRAM får nollor.

## Analys från loggar

### DMA Flöde
1. DMA trigger: `srcWord=0x7FCA7C` → `srcByte=0xFF94F8` (register 21=0x7C, 22=0xCA, 23=0x7F)
2. DMA längd: `0x0040` ord (64 ord = 128 bytes)
3. Target: CRAM
4. Data vid `0xFF94F8`: alla nollor

### Palett Buffer
- Palettdata skrivs till `0xFF95F8` vid:
  - `pc=0x013A0A`: `move.l d0,$FF95F8` (värdet `0x001F001F`)
  - `pc=0x013C50`: `move.w d0,$FF95F8` (uppdateringar)
- Ingen data vid `0xFF94F8`

### Lookup-tabell Population
Kod vid `0x013A46` (anropas från `0x000629BC` med `D0=0x00070000`):
```
0x013A46: 7203           moveq #3,d1
0x013A48: 49F9 00FF 9478 lea $FF9478,a4
0x013A4E: E198           rol.l #8,d0
0x013A50: 48E7 8000      movem.l d0,-(a7)
0x013A54: 0240 00FF      andi.w #$00FF,d0
0x013A58: EB40           asl.w #5,d0        ; NOT as.w #1,d0!
0x013A5A: 47F9 0007 8A8C lea $078A8C,a3
0x013A60: D6C0           adda.w d0,a3
0x013A62: 28DB           move.l (a3)+,(a4)+ ; 8 gånger
...
0x013A72: 4CDF 0001      movem.l (a7)+,d0
0x013A76: 51C9 FFD6      dbf d1,0x013A4E
0x013A7A: 4E75           rts
```

### Problem med koden
1. `D0=0x00070000` → `rol.l #8,d0` → `0x07000000`
2. `andi.w #$00FF,d0` → på 68000: de höga 16 bitarna oförändrade → `0x07000000`
3. `asl.w #5,d0` → shiftar bara låga 16 bitar: `0x0000 << 5 = 0x0000`
4. Resultat: offset 0 → kopierar från `0x078A8C` (som har 32 bytes nollor)

### ROM Tabell
- `0x078A8C`: första 0x20 bytes = nollor
- `0x078AAC`: börjar med data (`0x0000 0002 0024 0226...`)
- `0x078B6C`: lookup data (`0x0000 739C 7300 7280...`)

### Konverteringsloop
Kod vid `0x013C60`:
- `A2=0xFF95F8` (palett bytes)
- `A3=0xFF9478` (lookup tabell)
- `A4=0xFF94F8` (packat output för DMA)
- Loop: läser från `A2`, använder `A3` som lookup, skriver till `A4`

## Identifierade Buggar i Emulatorn

### 1. `andi.w` Implementering Fel
På 68000: `andi.w #$00FF,d0` med `d0=0x07000000` borde ge `d0=0x07000000` (höga 16 bitar oförändrade).

Vår emulator gav `d0=0x00000007` (FEL).

**Fix**: I `md_m68k_addressing.cs`, `adressing_func_write` för dataregister:
```csharp
case 0: // Dataregister
    switch (in_size)
    {
        case 0: // Byte
            g_reg_data[in_reg].l = (g_reg_data[in_reg].l & 0xFFFFFF00) | (in_val & 0x000000FF);
            break;
        case 1: // Word
            g_reg_data[in_reg].l = (g_reg_data[in_reg].l & 0xFFFF0000) | (in_val & 0x0000FFFF);
            break;
        default: // Long
            g_reg_data[in_reg].l = in_val;
            break;
    }
    break;
```

Samma för `write_g_reg_data` i `md_m68k_sub.cs`.

### 2. `asl.w` Disassembly Fel
`EB40` = `asl.w #5,d0`, inte `as.w #1,d0` som disassemblyn visade.

### 3. Adressregister Write
Samma fix behövs för adressregister i `adressing_func_write`.

## Konsekvenser
Med korrekt `andi.w`:
- `d0=0x07000000` efter `andi.w`
- `asl.w #5,d0` → fortfarande `0x07000000` (offset 0)
- Första lookup-block får nollor → palett blir korrupt

## Möjliga Lösningar

### Alternativ 1: Patcha ROM-koden
- Ändra `andi.w #$00FF,d0` till `andi.b #$FF,d0` vid `0x013A54`
- Eller ändra `rol.l #8,d0` till `ror.w #8,d0` eller `swap d0`
- Eller ändra `D0`-värdet vid `0x000629BE` från `0x00070000` till `0x000700E0`

### Alternativ 2: Patcha ROM-data
- Fyll `0x078A8C` med data (t.ex. kopiera från `0x078B6C`)

### Alternativ 3: Hitta fler emulatorbuggar
- Testa `rol.l`, `asl.w` implementeringar
- Kolla `movem.l` instruktionen
- Verifiera `adda.w d0,a3` (sign-extend av `d0`?)

## Test Resultat
- Fixade `andi.w` buggen: fortfarande korrupt grafik
- Patchade `D0` till `0x00070020`: fortfarande korrupt
- Patchade `andi.w` till `andi.b`: inte testat fullt ut

## Nästa Steg
1. Skapa test för `rol.l #8,d0` → `andi.w #$00FF,d0` → `asl.w #5,d0` sekvens
2. Verifiera att clownmdemu-core ger samma resultat
3. Om ROM-koden är korrekt, måste emulatorn ha fler buggar
4. Annars, patcha ROM permanent för att få spelet att fungera

## Viktiga Filer
- `EutherDrive.Core/MdTracerCore/md_m68k_addressing.cs` - `adressing_func_write`
- `EutherDrive.Core/MdTracerCore/md_m68k_sub.cs` - `write_g_reg_data`
- `EutherDrive.Core/MdTracerCore/opc/md_m68k_opeANDI.cs` - `andi` implementering
- Loggar: `headless_madou_trace25/madou_step17.log`, `headless_madou_trace28/madou_step18.log`

## Status
Problem identifierat men inte helt löst. Emulatorn har minst en bugg (`andi.w`), men även efter fix fungerar inte spelet korrekt. Ytterligare analys behövs.