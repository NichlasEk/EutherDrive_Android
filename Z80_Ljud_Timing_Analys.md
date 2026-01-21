# Z80 Ljud Timing Analys - EutherDrive

## Problemöversikt
Z80-ljudsystemet fungerar inte korrekt i EutherDrive-emulatorn. Z80-processorn startar inte efter att Sonic 2 laddat ljuddrivern.

## Vad vi har åtgärdat hittills

### 1. Safe Boot Avstängt
- `Z80SafeBootEnabled = false` (hardcoded i md_bus.cs:142)
- Spel kontrollerar Z80 reset/BUSREQ själva
- Ingen automatisk RAM-tömning eller reset-hantering

### 2. Z80 Timing-synkronisering Fixad
- Z80 ökar nu SystemCycles när den kör (md_z80.cs:run())
- Konverterar Z80-cykler till M68K-cykler: `(z80Cycles * 15L) / 7L`
- YM2612 busy-timing synkroniseras korrekt med M68K/master-timing

### 3. Z80 Startar i Reset-tillstånd
- `Z80ResetAssertOnBoot = true` (hardcoded)
- Riktig hårdvarubeteende: Z80 är i reset vid power-on
- Spel måste släppa reset explicit

### 4. Z80 Reset-logik Fixad
- När reset släpps: `md_main.g_md_z80.reset()` (sätter PC=0)
- Z80 startar från PC=0 när reset släpps (som riktig hårdvara)

## Nuvarande Problem - Sonic 2 Test

### Observerat Beteende
1. **Frame 0-9**: Z80 är i reset (`reset=True`), PC=0x0000
2. **Frame 9**: `[Z80SAFE] reset released frame=9` - reset släpps!
3. **Frame 10**: `reset=False` - Z80 är inte längre i reset
4. **Men Z80 kör fortfarande inte!** `canRun=False` och `busreq=True`

### Rotorsaken
Z80 har BUSREQ (bus request) aktiverat (`busreq=True`), vilket betyder att M68K har tagit över Z80-bussen. Z80 kan inte köra när BUSREQ är aktivt!

### Logganalys
```
[Z80-RESET-RELEASE] frame=9 reset released, setting g_active=False (busreq=True, reset=False)
[Z80-ACTIVE] frame=10 g_active=False busGranted=True reset=False
```

Z80 blir aktiv endast om båda villkoren är uppfyllda:
```csharp
bool newActive = !_z80BusGranted && !_z80Reset;  // md_bus.cs:2153
```

### BUSREQ Problem
- `_z80BusGranted = true` (BUSREQ aktivt)
- Sonic 2 laddar Z80-driver (4866 bytes under "safe boot")
- Men spelet släpper aldrig BUSREQ!
- Efter 98 frames är BUSREQ fortfarande aktivt

## Teknisk Analys

### Safe Boot Loggar Trots Avstängning
```
[Z80SAFE] busreq asserted frame=0
[Z80SAFE] busreq granted frame=0  
[Z80SAFE] reset asserted frame=0
[Z80SAFE] ram cleared frame=0
```

Dessa loggar kommer från `StartZ80SafeBoot()` som bara anropas om `Z80SafeBootEnabled = true`. Men `Z80SafeBootEnabled = false`!

**Möjlig bugg**: Kanske anropas `StartZ80SafeBoot()` ändå?

### BUSREQ Initiering
- `_z80BusGranted` initieras till `false` (rad 47)
- Sätts till `false` i `ResetState()` (rad 704)
- Sätts till `true` i `StartZ80SafeBoot()` (rad 853) - men detta borde inte anropas!

### Timing-synkronisering Korrekt
Z80 ökar SystemCycles korrekt när den kör:
```csharp
// md_z80.cs:850-854
long m68kCyclesEquivalent = (g_clock * 15L) / 7L;
if (m68kCyclesEquivalent > 0)
{
    md_main.AdvanceSystemCycles(m68kCyclesEquivalent);
}
```

Detta behövs för YM2612 busy-timing:
- YM2612 använder master cycles (53.693175 MHz)
- Z80: master / 15 = ~3.58 MHz
- M68K: master / 7 = ~7.67 MHz
- När Z80 skriver till YM2612: busy för `z80Cycles * 15L` master cycles
- För att busy ska kunna rensas måste SystemCycles (M68K cycles) öka

## Nästa Steg

### 1. Debugga BUSREQ Problem
- Varför är `_z80BusGranted = true`?
- Kommer det från `StartZ80SafeBoot()` trots `Z80SafeBootEnabled = false`?
- Eller skriver Sonic 2 till BUSREQ-registret och släpper det aldrig?

### 2. Testa med BUSREQ Logging
```bash
EUTHERDRIVE_Z80_BUSREQ_LOG=1 ./EutherDrive.Headless ~/roms/sonic2.md
```

### 3. Kolla BUSREQ Write Loggar
- Ser vi `[Z80BUSREQ]` loggar?
- Skriver Sonic 2 1 till BUSREQ-registret?
- Skriver Sonic 2 någonsin 0 till BUSREQ-registret?

### 4. Testa Andra Spel
- Testa Strider, Seaquest
- Se om samma mönster gäller

### 5. YM2612 Initiering
- Kolla om YM2612 behöver initieras när Z80 startar
- Testa YM2612 register access från Z80

## Filer Modifierade

1. **md_bus.cs**:
   - `Z80SafeBootEnabled = false` (rad 142)
   - `Z80ResetAssertOnBoot = true` (rad 147)
   - Rensat safe boot-logik från `HandleZ80ResetWrite()` (rad 2136-2170)

2. **md_z80.cs**:
   - Lagt till `md_main.AdvanceSystemCycles(m68kCyclesEquivalent)` i `run()` metod
   - Timing-synkronisering mellan Z80 och YM2612

## Rekommendationer

1. **Återställ BUSREQ till false**: Om `Z80SafeBootEnabled = false`, se till att `_z80BusGranted = false` efter reset.

2. **Debugga BUSREQ writes**: Logga alla BUSREQ register writes för att se vad Sonic 2 gör.

3. **Testa utan safe boot loggar**: Ta bort `[Z80SAFE]` loggarna som förvirrar.

4. **Verifiera Z80 kan köra**: När reset släpps och BUSREQ släpps, ska Z80 kunna köra koden vid PC=0x0000.

## Status
**Kritiskt Problem**: Z80 startar inte eftersom BUSREQ är aktivt. Sonic 2 släpper aldrig BUSREQ efter att ha laddat ljuddrivern.

**Timing Fix**: Z80 ökar SystemCycles korrekt för YM2612 busy-timing.

**Reset Hantering**: Z80 startar i reset, släpps korrekt, men kan inte köra pga BUSREQ.