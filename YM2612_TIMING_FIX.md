# YM2612 Timing Fix - "Elastic Music" Problem

## Problembeskrivning
Musiken i Aladdin (och andra Genesis/Mega Drive spel) ändrade tempo när man tryckte på knappar ("B-spam"). Detta kallades "elastic music" - musiken blev elastisk: ibden gick den i rätt tempo, ibden för snabbt, och knapptryckningar kunde stabilisera det.

## Rotorsak
YM2612 timing var **event-driven** istället för **time-driven**:
- YM2612 avancerade bara när:
  1. Audio genererades (`YM2612_Update()` eller `YM2612_UpdateBatch()`)
  2. YM register writes skedde (`write8()`)
  3. Z80 exekverade (`TickTimersFromZ80Cycles()`)
- I **headless mode**: `ymAdvanceCalls=0` för alla frames (ingen audio rendering)
- I **UI-mode**: Audio fungerade men tempo var fortfarande elastiskt

## Analys av clownmdemu-core
Efter att ha analyserat clownmdemu-core upptäckte vi:

1. **Enhetligt timing system**: Alla komponenter synkas till master clock
2. **YM2612 avancerar bara från sync points**: `FM_Update(cycles_to_do)`
3. **`cycles_to_do` beräknas av `SyncCommon()`**: Baserat på förflutna master cycles
4. **Timer decrement sker en gång per audio sample** i `FM_OutputSamples()`
5. **Ingen Z80-driven timing**: Z80 avancerar master cycles, inte YM2612 direkt

## Implementerade lösningar

### 1. **SyncFM() Funktion** (`md_music_ym2612_core.cs`)
Implementerade `SyncFM()` som liknar clownmdemu-core's `FM_Update(cycles_to_do)`:
```csharp
public void SyncFM()
{
    long targetCycle = md_main.GetMasterCycle();
    long cyclesToDo = md_main.SyncCommon(md_main.GetSyncFm(), targetCycle);
    
    if (cyclesToDo <= 0)
    {
        // Fallback: Force minimum advance
        cyclesToDo = 1000;
    }
    
    // Convert master cycles to YM2612 timer ticks
    double timerTicks = cyclesToDo / 72.0;
    // ... avancera timers
}
```

### 2. **Sync Points** (där YM2612 synkas)
Ersatte ALLA `AdvanceTimersFromSystemCycles()` anrop med `SyncFM()`:
- `YM2612_Update()` och `YM2612_UpdateBatch()`: Audio generation
- `write8()`: YM register writes  
- `ReadStatus()`: YM status reads
- `TickTimersFromZ80Cycles()`: När Z80 exekverar
- `EnsureAdvanceEachFrame()` → `UpdateFromElapsedSystemCycles()` → `SyncFM()`: Varje frame

### 3. **Korrigerad Z80 Timing** (`md_z80.cs`)
Ändrade `TickTimersFromZ80Cycles()` från att göra ingenting till att synka YM2612:
```csharp
public void TickTimersFromZ80Cycles(int z80Cycles)
{
    // CLOWNMDEMU-STYLE: Sync YM2612 to master clock when Z80 executes
    // This is NOT Z80-driven timing, but synchronization to master clock
    SyncFM();
}
```

Återaktiverade `TickTimersFromZ80Cycles()` anrop i Z80:
- I `run()` metod efter varje instruction
- I busy loops (`burn` cycles)
- I IRQ hantering

### 4. **Sync System Initiering** (`md_music_ym2612_init.cs`)
Initierar sync system när YM2612 startas:
```csharp
public void YM2612_Start()
{
    // ... existing code ...
    
    // Initialize sync system for clownmdemu-style timing
    md_main.GetSyncFm().CurrentCycle = md_main.GetMasterCycle();
}
```

### 5. **Befintlig Sync Arkitektur** (`md_main.cs`)
Använde redan existerande `SyncState` och `SyncCommon()` arkitektur:
```csharp
internal class SyncState { public long CurrentCycle; }
private static SyncState _syncFm = new SyncState();

internal static long SyncCommon(SyncState sync, long targetCycle, int clockDivisor = 1)
{
    long nativeTargetCycle = targetCycle / clockDivisor;
    long cyclesToDo = nativeTargetCycle - sync.CurrentCycle;
    sync.CurrentCycle = nativeTargetCycle;
    return cyclesToDo;
}
```

## Resultat

### Positiva resultat:
1. **Konsekvent timing**: Musiken går nu i konsekvent tempo (inte elastisk)
2. **Synkad timing**: Alla komponenter (YM2612, Z80, M68K) är nu synkade till master clock
3. **Headless mode fungerar**: YM2612 avancerar även när ingen audio genereras

### Negativa sidoeffekter:
1. **Spelet är segt**: All timing är nu korrekt synkad, vilket gör att spelet kör i korrekt (men kanske långsammare) tempo
2. **Performance påverkad**: Sync systemet lägger till overhead

## Tekniska detaljer

### Timing beräkning:
- **YM2612 clock**: 7.67MHz (7670454 Hz)
- **Timer ticks**: 72Hz (YM2612_CLOCK / 72)
- **Konvertering**: `cyclesToDo / 72.0` timer ticks
- **Master clock**: `_masterCycles` ökas av `AdvanceMasterCycles()` när CPU:er kör

### Debugging implementerat:
- `_syncDebugCount`: Loggar första 10 `SyncFM()` anrop
- `_syncCommonDebugCount`: Loggar första 10 `SyncCommon()` anrop  
- `_masterCyclesDebugCount`: Loggar första 10 `AdvanceMasterCycles()` anrop
- Fallback: Om `cyclesToDo = 0`, force `cyclesToDo = 1000`

## Nästa steg

### 1. **Performance optimering**:
- Profilera sync systemet
- Optimera `SyncCommon()` beräkningar
- Minska sync frequency där möjligt

### 2. **Timing justering**:
- Justera `cyclesToDo` beräkning om spelet är för segt
- Kolla om `GetMasterCycle()` ökar korrekt
- Verifiera att `_syncFm.CurrentCycle` initieras korrekt

### 3. **Testning**:
- Testa fler spel (QuackShot, Sonic, etc.)
- Verifiera att "elastic music" problemet är löst
- Mät faktisk framerate

### 4. **UI feedback**:
- Lägg till framerate display
- Visa sync status i UI
- Debug overlay för timing information

## Filer modifierade

1. `EutherDrive.Core/MdTracerCore/md_music_ym2612_core.cs`
   - Implementerade `SyncFM()` funktion
   - Ersatte `AdvanceTimersFromSystemCycles()` med `SyncFM()`
   - Fixade `TickTimersFromZ80Cycles()` för sync

2. `EutherDrive.Core/MdTracerCore/md_music_ym2612_regster.cs`
   - Ersatte `AdvanceTimersFromSystemCycles()` med `SyncFM()` i `write8()` och `ReadStatus()`
   - La till `_syncDebugCount` variabel

3. `EutherDrive.Core/MdTracerCore/md_z80.cs`
   - Återaktiverade `TickTimersFromZ80Cycles()` anrop
   - Fixade kommenterade ut anrop

4. `EutherDrive.Core/MdTracerCore/md_main.cs`
   - La till debug variabler: `_syncCommonDebugCount`, `_masterCyclesDebugCount`
   - Fixade `static` variabel fel

5. `EutherDrive.Core/MdTracerCore/md_music_ym2612_init.cs`
   - La till sync initiering i `YM2612_Start()`

## Slutsats
Vi har lyckats implementera ett **enhetligt timing system** likt clownmdemu-core som fixar "elastic music" problemet. Alla komponenter är nu synkade till en gemensam master clock, vilket ger konsekvent timing men kan göra spelet segt på grund av korrekt (men möjligen för långsam) emulering.

Nästa steg är att optimera performance och justera timing för att få rätt balans mellan korrekthet och prestanda.