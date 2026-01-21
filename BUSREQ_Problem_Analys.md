# BUSREQ Problem Analys - Z80 Timing i EutherDrive

## Problemidentifiering
Z80 startar inte korrekt efter att Sonic 2 laddat ljuddrivern. Z80 blir kortvarigt aktiv men blir sedan inaktiv igen pga BUSREQ-problemen.

## Root Cause Analys

### 1. Safe Boot Problem Löst
**Tidigare problem**: Safe boot var aktivt trots `Z80SafeBootEnabled = false`
**Lösning**: Använde fel DLL-fil. Efter att ha uppdaterat till korrekt DLL:
- `Z80SafeBootEnabled = false` (verifierat)
- `StartZ80SafeBoot()` anropas INTE
- `_z80BusGranted = false` vid start (korrekt)

### 2. BUSREQ Problem Identifierat
**Ny upptäckt**: Sonic 2 skriver aktivt till BUSREQ-registret (`0xA11100`)!

#### Observerat Beteende:
```
Frame 3:  Sonic 2 skriver 0x0100 → BUSREQ aktiverat (Z80 stoppas)
Frame 8:  Sonic 2 skriver 0x0000 → BUSREQ inaktiverat
Frame 14: Sonic 2 skriver 0x0100 → BUSREQ aktiverat
Frame 14: Sonic 2 skriver 0x0000 → BUSREQ inaktiverat (Z80 blir aktiv!)
Frame 15-20: Sonic 2 växlar snabbt 0x0100 ↔ 0x0000
```

### 3. Z80 Status Under BUSREQ Växling
```
Frame 14: [Z80-ACTIVE] g_active=True busGranted=False reset=False  ← Z80 KAN köra!
Frame 14: [Z80-ACTIVE] g_active=False busGranted=True reset=False   ← Z80 STOPPAS!
Frame 15: [Z80-ACTIVE] g_active=False busGranted=True reset=False   ← Z80 stoppad
...
```

## Teknisk Analys

### BUSREQ-registret (0xA11100)
- Bit 0 = 1: BUSREQ aktiverat (Z80 stoppas, M68K tar över bussen)
- Bit 0 = 0: BUSREQ inaktiverat (Z80 kan köra)
- Sonic 2 skriver `0x0100` (bit 0 = 1) och `0x0000` (bit 0 = 0)

### Varför Växlar Sonic 2 BUSREQ?
Möjliga orsaker:

1. **Synkronisering**: Sonic 2 försöker synkronisera med Z80
2. **Race Condition**: Bug i Sonic 2s kod
3. **Z80 Respons**: Kanske väntar Sonic 2 på att Z80 ska göra något?
4. **Hardware Bug**: Emulatorbugg som gör att Sonic 2 beter sig konstigt

### Z80 Kan Inte Köra
När `busGranted=True`:
- Z80 är inaktiv (`g_active=False`)
- Z80 kan inte komma åt minne
- Z80 kan inte köra instruktioner

När `busGranted=False` och `reset=False`:
- Z80 är aktiv (`g_active=True`)
- Z80 KAN köra från PC=0x0000
- Men Sonic 2 aktiverar BUSREQ igen snabbt!

## Logganalys - Kritiska Frames

### Frame 14 - Z80 Startar!
```
[Z80-RESET-RELEASE] frame=14 reset released, setting g_active=True (busreq=False, reset=False)
[DEBUG-BUSREQ-DETAIL] frame=14 addr=0xA11100 raw=0x0100 regByte=0x01 next=True
[DEBUG-BUSREQ] frame=14 _z80BusGranted changed: False → True
[Z80-ACTIVE] frame=14 g_active=False busGranted=True reset=False
[DEBUG-BUSREQ-DETAIL] frame=14 addr=0xA11100 raw=0x0000 regByte=0x00 next=False  
[DEBUG-BUSREQ] frame=14 _z80BusGranted changed: True → False
[Z80-ACTIVE] frame=14 g_active=True busGranted=False reset=False
```

**Sekvens**:
1. Reset släpps → Z80 aktiv
2. Sonic 2 aktiverar BUSREQ → Z80 inaktiv
3. Sonic 2 inaktiverar BUSREQ → Z80 aktiv igen
4. Men Z80 hinner inte köra något!

### Frame 15-20 - Snabb Växling
```
Frame 15: BUSREQ True→False→True→False (4 ändringar!)
Frame 16: BUSREQ True→False→True→False
Frame 17: BUSREQ True→False→True→False  
...
```

Sonic 2 växlar BUSREQ flera gånger per frame! Z80 har ingen chans att köra.

## Möjliga Lösningar

### 1. Debugga Sonic 2s BUSREQ-användning
- Varför växlar Sonic 2 BUSREQ så snabbt?
- Väntar den på Z80-respons?
- Är det en timing-bugg?

### 2. Testa Andra Spel
- Testa Strider, Seaquest
- Gör de samma sak?
- Om ja: generellt problem
- Om nej: Sonic 2-specifikt problem

### 3. Z80 Instruction Tracing
- När Z80 är aktiv, vad gör den?
- Kör den några instruktioner?
- Kanske kör den och kraschar?

### 4. BUSREQ Timing Fix
- Kanske behöver vi lägga till delay?
- I riktig hårdvara tar BUSREQ tid att propagera
- Emulatorn kanske är för snabb?

### 5. Z80 Interrupts
- Kanske väntar Sonic 2 på Z80 interrupt?
- Z80 kanske behöver skicka interrupt till M68K?
- Interrupt-hanteringen kanske är buggig?

## Nästa Steg

### 1. Aktivera Z80 Instruction Tracing
```bash
EUTHERDRIVE_TRACE_Z80WIN=1 ./EutherDrive.Headless ~/roms/sonic2.md 20
```

### 2. Kolla Z80 RAM Innehåll
- Vad finns på adress 0x0000-0x0100?
- Är ljuddrivern korrekt laddad?
- Kör Z80 koden eller kraschar den?

### 3. Testa Med Mindre BUSREQ Växling
- Kanske behöver vi ignorera vissa BUSREQ-writes?
- Eller lägga till minimum delay mellan BUSREQ ändringar?

### 4. Analysera Sonic 2 Z80 Driver
- Hur ser Sonic 2s Z80-ljuddriver ut?
- Förväntar den sig specifikt timing?
- Använder den interrupts?

## Status
**Kritiskt Problem**: Sonic 2 växlar BUSREQ för snabbt, vilket förhindrar Z80 från att köra.

**Timing Fix Fungerar**: Z80 ökar SystemCycles korrekt för YM2612 busy-timing.

**Reset Hantering Fungerar**: Z80 startar i reset, släpps korrekt, men kan inte köra pga BUSREQ.

**Safe Boot Avstängt**: Korrekt - ingen safe boot interference.