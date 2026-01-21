# Z80 BUSREQ Problem - Sonic 2 Specifik Analys

## Sammanfattning av Fynd

### 1. Z80 Startar och Kör!
**Viktigaste upptäckten**: Z80 startar faktiskt och kör kod!
- Frame 14: Z80 reset släpps, Z80 blir aktiv (`g_active=True`)
- Z80 kör boot-sekvens: `DI`, `LD SP, 0x1B80`, `JP 0x0167`
- Z80 hoppar till ljuddriver vid `0x0167`
- Z80 kör ~100 instruktioner (trace-loggar visar detta)
- Z80 initierar YM2612 (YM tracing visar skrivningar till `0x4000`/`0x4001`)

### 2. BUSREQ Problem - Sonic 2 Specifikt
**Problem**: Sonic 2 växlar BUSREQ extremt snabbt:
```
Frame 3:  BUSREQ aktiverat (0x0100) → Z80 stoppas
Frame 8:  BUSREQ inaktiverat (0x0000)
Frame 14: BUSREQ aktiverat (0x0100) → Z80 stoppas precis efter start!
Frame 14: BUSREQ inaktiverat (0x0000) → Z80 startar igen
Frame 15-20: BUSREQ växlar snabbt 0x0100 ↔ 0x0000 (flera gånger per frame!)
```

### 3. Timing Fix Fungerar
**God nyhet**: Z80 timing-synkronisering fungerar!
- Z80 ökar SystemCycles korrekt: `(Z80_cycles * 15L) / 7L`
- YM2612 busy-timing bör fungera (ingen `[YMBUSY-DEBUG]` logg)
- SystemCycles ökar när Z80 kör

### 4. Andra Spel Beter Sig Annorlunda
- **Seaquest**: Skriver till BUSREQ på frame 0, sedan normalt
- **Shinobi III**: Släpper inte reset alls efter 10 frames
- **Sonic 2**: Unikt med snabb BUSREQ-växling

## Rotorsaksanalys

### Varför Växlar Sonic 2 BUSREQ?
Möjliga orsaker:

#### 1. Synkroniseringsmekanism
Sonic 2 kanske använder BUSREQ för att:
- Synkronisera M68K med Z80
- Vänta på att Z80 ska slutföra en uppgift
- Kontrollera när Z80 kan komma åt delad resurs

#### 2. Z80 Driver Bug
Sonic 2s Z80-driver kanske:
- Förväntar sig specifik timing
- Har en bugg i sin väntloop
- Kräver att M68K växlar BUSREQ för att trigga något

#### 3. Emulator Timing Problem
Emulatorn kanske:
- Kör för snabbt/sakta jämfört med riktig hårdvara
- Har felaktig BUSREQ propagation delay
- Missar någon hårdvarudetalj

#### 4. Z80 Interrupt Problem
Sonic 2 kanske:
- Väntar på Z80 interrupt
- Z80 skickar aldrig interrupt
- M68K växlar BUSREQ för att "väcka" Z80

## Teknisk Analys av Z80 Körning

### Z80 Boot Sekvens (Frame 14)
```
[Z80-TRACE-1]  pc=0x0000->0x0001 op=0xF3  = DI (Disable Interrupts)
[Z80-TRACE-2]  pc=0x0001->0x0004 op=0x31  = LD SP, 0x1B80  
[Z80-TRACE-3]  pc=0x0004->0x0167 op=0xC3  = JP 0x0167
[Z80-TRACE-4]  pc=0x0167->0x0169 op=0xED  = Liknande LD I,A eller annan ED-prefix
[Z80-TRACE-5]  pc=0x0169->0x0B52 op=0xCD  = CALL 0x0B52
```

### Z80 YM2612 Access
```
[Z80YM] read addr=0x4000 -> 0x00        ; Läser YM2612 status (inte busy)
[Z80YM] write pc=0x001B addr=0x4000 val=0x00  ; Skriver till YM2612
[Z80YM] read addr=0x4000 -> 0x00        ; Läser status igen
[Z80YM] write pc=0x0021 addr=0x4001 val=0x00  ; Skriver till YM2612 data
```

Z80 initierar YM2612 korrekt!

### Z80 Loop (Efter Frame 20)
```
Frame 20: PC=0x857E
Frame 30: PC=0xC61F (ökar med ~0x39 per steg)
```

Z80 verkar köra i någon slags loop. Kanske en **väntloop** som väntar på:
- YM2612 busy flag
- Interrupt från VDP
- Kommando från M68K

## Rekommenderade Nästa Steg

### 1. Analysera Sonic 2 Z80 Driver
- Disassemblera Z80-koden vid `0x0167`
- Förstå vad drivern gör
- Identifiera väntloopen

### 2. Debugga BUSREQ Användning
- Logga M68K-kod som skriver till BUSREQ
- Förstå varför M68K växlar BUSREQ
- Kolla om det finns ett mönster

### 3. Testa Med YM2612 Busy Disabled
```bash
EUTHERDRIVE_YM_BUSY_Z80_CYCLES=0 ./EutherDrive.Headless ~/roms/sonic2.md 30
```
Om YM2612 aldrig är busy, kanske Z80 inte fastnar?

### 4. Testa Med BUSREQ Ignorerad
- Temporärt ignorera BUSREQ writes
- Låt Z80 köra kontinuerligt
- Se om ljud fungerar då

### 5. Analysera Z80-M68K Kommunikation
- Kolla mailbox (`0xA1xxxx`) access
- Z80 kanske skriver till M68K
- M68K kanske väntar på Z80 respons

## Status
**Positivt**:
- Z80 startar och kör kod ✓
- Timing-synkronisering fungerar ✓  
- YM2612 initieras ✓
- Safe boot är avstängt ✓

**Problem**:
- Sonic 2 växlar BUSREQ för snabbt
- Z80 stoppas kontinuerligt
- Ljud fungerar troligen inte

**Mysterium**:
- Varför växlar Sonic 2 BUSREQ?
- Väntar den på något från Z80?
- Är det en bugg eller avsiktligt?