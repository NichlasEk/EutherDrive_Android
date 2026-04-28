# Sega System 32 Port Plan

Goal: add a native C# Sega System 32 arcade core to EutherDrive, starting with Golden Axe: The Revenge of Death Adder (`ga2`, `ga2u`, `ga2j`).

## Licensing

The local MAME System 32 source files checked for this port are BSD-3-Clause:

- `/home/nichlas/mame/src/mame/sega/segas32.cpp`
- `/home/nichlas/mame/src/mame/sega/segas32.h`
- `/home/nichlas/mame/src/mame/sega/segas32_m.cpp`
- `/home/nichlas/mame/src/mame/sega/segas32_v.cpp`

Those files are marked `license:BSD-3-Clause` and `copyright-holders:Aaron Giles`. Any translated System 32 hardware logic in EutherDrive must keep that attribution in source comments and in the local notice file.

System 32 also needs a NEC V60 CPU core. The local MAME V60 source is marked BSD-3-Clause with copyright holders Farfetch'd and R. Belmont. If that code is translated, the V60 files need their own attribution in the same notice path.

No ROMs, generated NVRAM, EEPROM, save states, screenshots, logs, or runtime cache files should be committed.

## External Files Needed

For Golden Axe: The Revenge of Death Adder:

- `ga2.zip` for the world/parent set, or
- `ga2u.zip` for the US set, or
- `ga2j.zip` for the Japan set.

Current local status: `/home/nichlas/roms/MAME/System32/ga2.zip` is present and the loader accepts it. No additional external files are missing for this ROM set at the current boot stage; sound/sample/protection files are expected inside the same archive.

The archive must include the matching MAME ROM files for:

- V60 main program: `epr-*.ic17`, `epr-*.ic8`, `epr-*.ic18`, `epr-*.ic9`
- Z80/sound and sample data: `epr-14945.ic36`, `mpr-14944.ic35`, `mpr-14943.ic34`, `mpr-14942.ic24`
- protection MCU: `epr-14468-02.u3`
- tiles: `mpr-14948.ic14`, `mpr-14947.ic5`
- sprites: `mpr-14949.ic32`, `mpr-14951.ic30`, `mpr-14953.ic28`, `mpr-14955.ic26`, `mpr-14950.ic31`, `mpr-14952.ic29`, `mpr-14954.ic27`, `mpr-14956.ic25`

There is no separate BIOS file in the MAME `ga2/ga2u/ga2j` ROM definitions, but the protection MCU ROM is required.

## Milestones

1. Add a dedicated `System32Adapter`, ROM detection, ROM archive validation, and BSD-3 notice files. Done.
2. Port or implement the NEC V60 core in C# with instruction tests and a small trace harness against MAME behavior. Started: reset, 24-bit fetch, HALT/BRK/CLRTLBA/NOP, conditional/unconditional branch opcodes, LDPR, MOVB/H/W, MOVEA, PUSH, RETIS, DBcc/TB, and the boot addressing modes hit by GA2 so far are present.
3. Build the GA2 memory map: V60 ROM/RAM, palette RAM, tile RAM, sprite RAM, I/O, EEPROM, IRQs, and sound communication. Started: ROM, work RAM, video RAM, sprite RAM, palette/mixer RAM, shared RAM, comm RAM, and GA2 DPRAM are mapped.
4. Implement enough boot flow to reach the test/initialization screens from a cold start.
5. Add video in layers: palette decode, tilemaps, sprites, row/line scroll, and priority mixing.
6. Add sound: Z80, Sega MultiPCM, YM3438 path if needed by the selected System 32 machines, bank switching, and frame-synchronous audio output.
7. Add inputs: coins, start, service/test, player controls, dip/service behavior, and persistent NVRAM/EEPROM under the runtime save directory.
8. Add headless smoke tests for known ROMs and keep UI routing ahead of the generic MCS archive path.
9. Optimize after correctness: V60 dispatch, video dirty tracking, sprite batching, and audio mixing.

## Main Risks

- V60 is the largest dependency; System 32 is not a 68000 board.
- GA2 uses a protection MCU path (`segas32_v25_state` in MAME), so boot can stall even with a good V60 if the protection behavior is missing.
- System 32 video priority is more complex than the current CPS1/CPS2 paths and should be modeled from MAME before optimizing.
- Clones and regional sets may be split differently; the loader should report missing files clearly instead of falling through to the wrong arcade adapter.

## Current Headless Entry Point

Use this once a GA2 ROM archive is available:

```sh
EUTHERDRIVE_HEADLESS_CORE=system32 EUTHERDRIVE_SYSTEM32_TRACE_BOOT=1 dotnet run --project EutherDrive.Headless -- /path/to/ga2.zip 1
```

The trace stops at the first V60 opcode that has not been ported yet. If it does not stop, GA2 is currently executing a longer initialization loop and can be advanced with more frames or a higher temporary V60 instruction slice.

Last checked: `ga2.zip` gets past the reset vector, LDPR setup, stack setup, early memory/register copy loops, and RETIS trampoline. The frame buffer is still blank because video rendering and most System 32 devices are not implemented yet.
