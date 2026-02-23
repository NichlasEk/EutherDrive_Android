# N64 bring-up status (2026-02-22)

## Mål
Få första bildruta (VI aktiv + framebuffer-innehåll) i N64-kärnan via `EutherDrive.UI`/`EutherDrive.Headless`.

## Läget just nu
- Emulatorn bootar ROM och kör stabilt i tidig IPL3-loop.
- Vi ser fortfarande ingen bild:
  - `VI mode not active (status=0x00000000, viType=0)`
  - Ingen audio ännu.
- Senaste stabila kortkörning (SM64) stannar i området `pc=0x800001xx` och fortsätter loopa där.

## Senaste ändringar (detta pass)

### 1) Stramare low-address pass-through (dataadressöversättning)
- Fil: `Ryu64/Ryu64.MIPS/Memory.cs`
- Ändring:
  - Tidigare pass-through för `virtualAddress < 0x20000000` var för bred.
  - Nu tillåts den endast i tidiga boot-PC-fönster:
    - `0xA4000000..0xA4001FFF`
    - `0x80000000..0x80001FFF`
    - `0xBFC00000..0xBFC00FFF`
  - Samt endast för låg fysisk window `< 0x05000000`.
- Syfte:
  - Undvika att user/kuseg-kod bypassar TLB och börjar exekvera skräpdata som instruktioner.

### 2) Minimal PIF control-ack
- Fil: `Ryu64/Ryu64.MIPS/Memory.cs`
- Ändring:
  - I `ProcessPifJoybusCommands()` nollställs PIF control-byte (`PIFRAM[63]`) när den är satt.
- Syfte:
  - Förhindra polling-loopar där PIF control-bitar blir kvar.

## Tidigare viktiga ändringar (från föregående pass)
- Fil: `Ryu64/Ryu64.MIPS/Interpreter/InstInterpCOP0.cs`
  - Normalisering av CP0-skrivningar + write-wrapper för `MTC0/DMTC0/CTC0`.
  - `WIRED`-skrivning återställer `RANDOM` till `0x1F`.
- Fil: `Ryu64/Ryu64.MIPS/Interpreter/R4300.cs`
  - Korrigerad `RANDOM` decrement/wrap-logik.

## Verifiering som kördes
- Build:
  - `dotnet build EutherDrive.Headless -v minimal` (OK, warnings only)
- Kort headless-run:
  - `EUTHERDRIVE_HEADLESS_CORE=n64 EUTHERDRIVE_HEADLESS_TRACE_FRAMES=1 EUTHERDRIVE_TRACE_VERBOSE=1 dotnet /home/nichlas/EutherDrive/EutherDrive.Headless/bin/Debug/net8.0/EutherDrive.Headless.dll "/home/nichlas/roms/Super_Mario_64_(USA)-.n64" 1200`
- Observation:
  - Ingen unknown-opcode-storm i denna kortkörning.
  - Fortfarande ingen VI-init, loop kring `0x80000130..0x80000188`.

## IO-trace-fynd
- Med `EUTHERDRIVE_TRACE_N64_IO=1` i kort run:
  - Endast tydlig PI-DMA start sågs:
    - `[N64IO] PI_WR_LEN write len=0x0fffff cart=0x10001000 dram=0x00246000 pc=0x80000050`
  - Inga tydliga efterföljande PI_STATUS/MI_INTR_MASK-skrivningar i den loggen.

## Trolig nästa flaskhals
Bootkod verkar fastna i tidig PI/interrupt-pollingsekvens (kring `0x800001xx`) innan VI konfigureras.

## Rekommenderad nästa-pass plan
1. Lägg riktad trace för just `0x80000120..0x80000190`:
   - logga register som styr branch-villkor i loopen (t.ex. `t0/t1/t6/t7/t8/t9`, samt minnesläsningar som används i jämförelser).
2. Verifiera PI/SI/MI-statussemantik mot förväntad bootsekvens:
   - `PI_STATUS` busy/interrupt-clear-beteende
   - `MI_INTR`/`MI_INTR_MASK` interaktion
   - eventuella ack-write som idag missas.
3. Kontrollera att PI DMA-kopieringen matchar förväntad längd/offset exakt för IPL3-fortsättning.
4. Kör längre headless med aggressiva N64 pacing-env för snabbare cykelprogress och kontrollera om loop bryts.

## Kommando-snippets för nästa gång
- Build:
  - `dotnet build EutherDrive.Headless -v minimal`
- Standard headless:
  - `EUTHERDRIVE_HEADLESS_CORE=n64 dotnet /home/nichlas/EutherDrive/EutherDrive.Headless/bin/Debug/net8.0/EutherDrive.Headless.dll "/home/nichlas/roms/Super_Mario_64_(USA)-.n64" 1200`
- IO trace:
  - `EUTHERDRIVE_HEADLESS_CORE=n64 EUTHERDRIVE_TRACE_N64_IO=1 dotnet /home/nichlas/EutherDrive/EutherDrive.Headless/bin/Debug/net8.0/EutherDrive.Headless.dll "/home/nichlas/roms/Super_Mario_64_(USA)-.n64" 180 > /tmp/n64_io_trace.log 2>&1`
  - `rg -n "\\[N64IO\\]|PI_STATUS|MI_INTR_MASK|SI_STATUS|PI_(RD|WR)_LEN" /tmp/n64_io_trace.log`

## Git state notering
- Orelaterad lokal ändring fanns redan:
  - `PCE_CD_Core/PPU.cs` (inte rörd i N64-arbetet).
- N64-relaterad ändring i detta pass:
  - `Ryu64/Ryu64.MIPS/Memory.cs`
