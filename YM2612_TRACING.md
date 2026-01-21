# YM2612 Status Read Tracing Implementation

## Problem
Det finns en motsägelse i YM2612 status läsningskoden:
- I `ReadStatus` metoden finns en rad som tvingar `BUSY=0` (`status &= 0x7F`)
- Men Z80 disassembly visar en loop vid PC=0x0008:
  ```
  LD A,(0x4000)
  BIT 7,A
  JR NZ,0x0008   ; loop while BUSY=1
  ```
- I praktiken verkar Z80 fastna i denna loop

## Implementerad Lösning

Jag har lagt till "truth serum" tracing för att bekräfta vilken status-byte Z80 faktiskt får.

### 1. Gated Tracing under `EUTHERDRIVE_TRACE_YM_PORTS=1`

Tracing är redan implementerat i `md_music_ym2612_regster.cs` men jag har förbättrat det:

**Status Reads (0x4000/0x4002):**
- Loggar: frame, Z80 PC, adress, rawStatus hex, maskedStatus hex, och bit decode (busy/timerA/timerB)
- Loggningen sker på den riktiga return-vägen (direkt innan return)
- Global cap: max 5000 status logs (`MAX_YM_PORT_LOGS = 5000`)

**Write Logging (0x4000..0x4003):**
- Per-port counters för:
  - `0x4000`: Port 0 address (addr0)
  - `0x4001`: Port 0 data (data0) 
  - `0x4002`: Port 1 address (addr1)
  - `0x4003`: Port 1 data (data1)
- Samma globala cap: max 5000 write logs

**Summary Logging:**
- Automatisk sammanfattning vid exit
- Visar totalsummor för alla port-access

### 2. Förändringar Jag Gjorde

1. **Uppdaterade write logging cap** från 50 till 5000 (samma som read logging)
2. **Lade till `DumpPortStats()` metod** i `md_ym2612` klassen
3. **Lade till `DumpYmPortStats()` metod** i `MdTracerAdapter` klassen
4. **Uppdaterade headless programmet** att automatiskt logga statistik vid avslut

### 3. Hur Man Kör

#### Steg 1: Sätt miljövariabel
```bash
export EUTHERDRIVE_TRACE_YM_PORTS=1
```

#### Steg 2: Kör headless med ROM
```bash
cd /home/nichlas/EutherDrive
dotnet run --project EutherDrive.Headless -- ~/roms/quackshot.md 120
```

#### Alternativt: Kör med fler frames
```bash
dotnet run --project EutherDrive.Headless -- ~/roms/quackshot.md 500
```

### 4. Förväntad Output

**Under körning (exempel):**
```
[YM-PORT-STATUS] frame=5 Z80 pc=0x0008 addr=0x4000 raw=0x80 masked=0x00 busy=1 timerA=0 timerB=0 type=status
[YM-PORT-STATUS] frame=5 Z80 pc=0x0008 addr=0x4000 raw=0x80 masked=0x00 busy=1 timerA=0 timerB=0 type=status
[YM-PORT] frame=5 Z80 pc=0x0010 addr=0x4000 val=0x28 (port0 addr)
[YM-PORT] frame=5 Z80 pc=0x0012 addr=0x4001 val=0x80 (port0 data) addr_reg=0x28
```

**Vid avslut:**
```
[HEADLESS] Completed 120 frames
[YM-PORT-SUMMARY] writes: addr0=15 data0=12 addr1=8 data1=6 totalWrites=41
[YM-PORT-SUMMARY] status reads: port0=250 port2=0 totalReads=250
```

### 5. Vad Vi Letar Efter

Tracing hjälper oss identifiera om:
- **(a)** `0x4001` kollapsar till `0x4000`
- **(b)** BUSY masken inte appliceras på den riktiga read-vägen  
- **(c)** open-bus returnerar `0xFF`
- **(d)** Z80 läser en annan port

### 6. Fildetaljer

**Uppdaterade filer:**
1. `/home/nichlas/EutherDrive/EutherDrive.Core/MdTracerCore/md_music_ym2612_regster.cs`
   - Uppdaterade write logging cap från 50 till 5000
   - Lade till `public void DumpPortStats()` metod

2. `/home/nichlas/EutherDrive/EutherDrive.Core/MdTracerAdapter.cs`
   - Lade till `public void DumpYmPortStats()` metod

3. `/home/nichlas/EutherDrive/EutherDrive.Headless/Program.cs`
   - Lade till `adapter.DumpYmPortStats()` anrop vid avslut

### 7. Nästa Steg

Efter att ha kört tracingen, analysera loggarna för att se:
1. Vad är `rawStatus` värdena? (innan `&= 0x7F`)
2. Vad är `maskedStatus` värdena? (efter `&= 0x7F`)
3. Hur många gånger läser Z80 från `0x4000` vs `0x4002`?
4. Stämmer BUSY biten (bit 7) med vad vi förväntar oss?

Detta kommer hjälpa oss lösa varför Z80 fastnar i busy-loopen trots att koden tvingar `BUSY=0`.