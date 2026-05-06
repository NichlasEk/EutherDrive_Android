# Gauntlet Dark Legacy Vegas Bring-Up Plan

Scope: build a focused EutherDrive adapter for Midway/Atari Vegas/Durango and `gauntdl`/`gauntdl24`, using local MAME at `/home/nichlas/mame` as the hardware map and test oracle. This is not a full C# MAME port. The first target is a Gauntlet Dark Legacy-compatible machine that can load the ROM/CHD set, boot far enough to produce useful traces, and then grow device accuracy only where the game demands it.

## Source Anchors

Primary MAME file:

- `/home/nichlas/mame/src/mame/midway/vegas.cpp`

Useful MAME device files:

- `/home/nichlas/mame/src/devices/video/voodoo_2.cpp`
- `/home/nichlas/mame/src/devices/video/voodoo.cpp`
- `/home/nichlas/mame/src/devices/video/voodoo_regs.h`
- `/home/nichlas/mame/src/mame/shared/dcs.cpp`
- `/home/nichlas/mame/src/mame/shared/dcs.h`

Local ROM set found during this pass:

- `/home/nichlas/roms/MAME/Midway/Vegas/gauntd/gauntdl.zip`
- `/home/nichlas/roms/MAME/Midway/Vegas/gauntd/gauntdl24.7z`
- `/home/nichlas/roms/MAME/Midway/Vegas/gauntd/gauntd24.chd`

MAME set facts from `vegas.cpp`:

- `gauntdl`: Gauntlet Dark Legacy version DL 2.52, disk image `gauntdl`
- `gauntdl24`: Gauntlet Dark Legacy version DL 2.4, disk image `gauntd24`
- Main ROM: `gauntdl.bin`, 0x80000 bytes, CRC `3d631518`
- Vegas SIO boot ROM: `vegassio.bin`, 0x8000 bytes loaded as 16-bit byte ROM
- Security PIC: `346_gauntlet-dl.u37`, 0x2000 bytes
- Machine config: `gauntdl()` calls `vegas250()`
- CPU: little-endian R5000 at `SYSTEM_CLOCK * 2.5`
- Base SDRAM in `vegascore`: 8 MB
- Video: Voodoo 2 PCI, 2 MB framebuffer, two TMUs with 4 MB each
- IDE: CMD/Silicon Image PCI0646-compatible IDE path
- Audio: DCS2 ADSP-2104, 4 MB DRAM, polling offset `0x0b5d`
- IOASIC: shuffle `SHUFFLE_GAUNTDL`, upper `346`, year offset `80`, auto-ack enabled

## Adapter Shape

The initial EutherDrive shape should stay small and explicit:

```text
GauntletDarkLegacyAdapter
  GauntletDarkLegacyMachine
    MipsR5000Core
    VegasMemoryMap
    IdeDiskDevice
    VegasSioDevice
    DcsAudioDevice
    VoodooFacade
    GauntletInputPanel
```

The adapter should expose the normal `IEmulatorCore` surface while keeping the Vegas-specific parts inspectable. Early frames may render only a diagnostic framebuffer; that is acceptable until CPU/device execution exists.

## Bring-Up Phases

### Phase 0: Scaffold

Status: started.

- Add `EutherDrive.Core/Arcade/Vegas/GauntletDarkLegacyAdapter.cs`.
- Detect `gauntdl`, `gauntdl24`, `gauntdl.zip`, `gauntdl24.7z`, and directories containing those files.
- Resolve the matching CHD beside the ROM archive when possible.
- Load and validate required ROM entries by name and size.
- Add Voodoo backend interfaces and null/trace backends.
- Add minimal input panel mapping from EutherDrive buttons to Gauntlet P1 controls first.
- Route UI core selection before the generic MCS arcade fallback.

### Phase 1: Vegas Memory Skeleton

Port the MAME chip-select maps as named ranges before attempting CPU execution:

- CS2: Vegas SIO, offsets `0x00000000..0x00007003`
- CS3: analog port, offsets `0x00000000..0x00000003`
- CS4: M48T37 timekeeper/watchdog, offsets `0x00000000..0x00007fff`
- CS5: CPU I/O, offsets `0x00000000..0x00000003`, plus unknown read window `0x00100000..0x001fffff`
- CS6: IOASIC/DCS, packed IOASIC `0x00000000..0x0000003f`, ASIC FIFO `0x1000`, DCS FIFO `0x3000`, DCS IDMA `0x5000/0x7000`
- CS7: Ethernet/DCS alternate path, Ethernet `0x1000..0x100f`, DCS IDMA `0x5000/0x7000`
- CS8: Denver-style DUART/parallel/MPS reset map; keep documented, but not first-class for Gauntlet DL.

The useful output of this phase is read/write logging that says which unimplemented device range the boot code hit.

### Phase 2: ROM, CHD, IDE

Disk access should be boring and strict:

- Implement a CHD/raw abstraction with explicit metadata and sector reads.
- Start with identify, status, command register, LBA sector reads, IRQ assertion, and busy/ready timing good enough for the boot path.
- Use the MAME set mapping to bind `gauntdl` to `gauntdl.chd` and `gauntdl24` to `gauntd24.chd`.
- Avoid GPU/CPU shortcuts in IDE until disk reads are stable.

### Phase 3: CPU Interpreter

Begin with correctness diagnostics, not speed:

- Add or wrap an R5000/RMIPS interpreter.
- Implement reset vector, TLB/cache behavior only to the extent the BIOS path requires.
- Add PC histogram and trace windows from the first run.
- Stop on unknown instructions with register dump, PC, and last device access.

Expected progression:

- A: simple interpreter
- B: basic block cache
- C: hot-loop JIT/dynarec
- D: optional unsafe fast paths

### Phase 4: SIO, IOASIC, Input

Only build the Gauntlet surface initially:

- P1-P4 joystick directions
- attack/magic/start
- coin slots
- service/test
- DIP/config stubs
- security PIC response path for game ID `346`

Keep IOASIC shuffle behavior visible in code because `gauntdl` uses its own shuffle constant in MAME.

### Phase 5: Voodoo Facade

Do not start with a full low-level Voodoo 2 rasterizer. Use a facade with multiple backends:

```csharp
public interface IVoodooBackend
{
    void WriteRegister(uint address, uint value);
    void WriteFifo(ReadOnlySpan<uint> words);
    void RenderFrame(EutherFrameTarget target);
}
```

Backends:

- `VoodooNullBackend`: answers register writes and presents black/diagnostic output.
- `VoodooTraceBackend`: logs register writes, FIFO bursts, texture uploads, triangle bursts, and state changes.
- `VoodooGpuBackend`: translates common Voodoo draw/state commands to EutherDrive GPU paths.
- `VoodooReferenceBackend`: later software reference path for correctness comparisons.

The first useful Voodoo milestone is not pixel-perfect output. It is a reliable command log from real Gauntlet frames.

### Phase 6: DCS Audio

Delay until video/input boot is alive:

- Stub DCS status and queues so the game can boot.
- Add command queue and sample playback.
- Port deeper ADSP-2104/DCS behavior only when the game needs it.

## Trace Switches To Add

Suggested environment variables:

- `EUTHERDRIVE_GAUNTDL_TRACE_LOAD=1`
- `EUTHERDRIVE_GAUNTDL_TRACE_MEM=1`
- `EUTHERDRIVE_GAUNTDL_TRACE_IDE=1`
- `EUTHERDRIVE_GAUNTDL_TRACE_SIO=1`
- `EUTHERDRIVE_GAUNTDL_TRACE_VOODOO=1`
- `EUTHERDRIVE_GAUNTDL_TRACE_CPU=1`

Trace format should include frame, CPU PC when known, access size, address, value, and device name.

## Current First Sketch

The first code sketch intentionally stops before pretending to emulate:

- It recognizes the local Gauntlet DL set.
- It loads ROM archive entries and records the sibling CHD path.
- It owns a `GauntletDarkLegacyMachine` with stub devices and Voodoo facade.
- It renders a diagnostic frame until CPU/video execution exists.
- It maps EutherDrive input into a Gauntlet input state.

Next useful task: implement `VegasMemoryMap` dispatch and attach a minimal CPU fetch/reset path that reads `gauntdl.bin` through the same boot ROM region MAME assigns to `PCI_ID_NILE:rom`.
