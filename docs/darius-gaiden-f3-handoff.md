# Darius Gaiden / Taito F3 Handoff

Date: 2026-05-16

## Current State

`dariusg.zip` now has a dedicated Taito F3 bringup adapter at:

- `EutherDrive.Core/Arcade/Taito/DariusGaidenAdapter.cs`

The adapter executes the Darius Gaiden main program far enough to enter the game/test-mode task scheduler path, populate palette/sprite/text/playfield RAM, and render through the UI framebuffer path. It is not playable yet. The visible UI still sticks around the disk/status/test/menu area rather than reaching stable title/gameplay.

The current approach is intentionally small and direct:

- reuse the existing EutherDrive 68000 CPU as a 68EC020 bringup core
- patch missing 020 instructions locally as they are encountered
- keep Taito F3 devices minimal and behavior-driven
- prefer ugly first pixels over a complete architecture rewrite

## Implemented

- Darius Gaiden ROM loading and F3 memory map skeleton.
- Main 68EC020 reset/vector path through the existing M68000 core.
- Work RAM, palette RAM, sprite RAM, playfield RAM, text RAM, char RAM, line RAM, pivot RAM, and dual-port RAM mapping.
- Basic TC0640FIO-style active-low input mirrors for system/test/coin/start/buttons.
- Minimal EEPROM 93C46 line protocol and read response.
- Basic watchdog/timer/control register latches.
- F3 trap/task scheduler shim sufficient to reach scene/task code.
- 020 probe support for common missing instructions, including broader `MUL.L` effective-address forms.
- UI framebuffer handoff and volume plumbing for the bringup adapter.
- Rough text, playfield, pivot/pixel, and sprite renderers.
- MAME-aligned fixes for:
  - 64x64 text layer shape
  - Darius-style pixel layer textram attribute mapping
  - sprite zoom scale as `0x100 - zoom`
  - sprite Y origin
  - simple Darius sprite lag/latching

## Verification

Build:

```sh
dotnet build EutherDrive.sln
```

Result: passed, with existing repo warnings.

Headless smoke:

```sh
dotnet run --project EutherDrive.Headless/EutherDrive.Headless.csproj --no-build /home/nichlas/roms/MAME/TAITO/dariusg.zip 1600
```

Observed final state:

- framebuffer has content
- no unmapped reads/writes reported in the sampled final debug line
- task scheduler is active
- scene entry/init/mainwait counters advance
- still no correct Darius title/gameplay progression
- performance is under target in this path:
  - `capacity_fps=46.557`
  - `target_fps=58.944`

Representative final debug signature:

```text
pc=0x005892 op=0x51C8 tasks=0 q=-
taskEnq=1332 taskRun=826
lastTrap=0x010334
scene=entry:79/init:79,79,79,0,79/mainwait:79/cont:158,79,79
listPtr=0x600310 sprNZ=3097
unmappedR=0 unmappedW=0
```

During the stuck phase it repeatedly visits:

```text
pc=0x0011EA op=0x4269
q=0038B2,0B840A or q=01048E,0038B2,0B840A
btst=0x4022AD:0xFF/b2
```

That means the previous TC0640FIO/EEPROM test latch issue is no longer the obvious blocker. The game is now progressing through scene tasks but not completing the expected boot sequence.

## Likely Next Work

The next missing semantics are probably not broad video architecture. They are more likely one of:

- remaining 68EC020 instruction semantics in the scene/task path
- inaccurate F3 trap scheduler/task return semantics
- timer/interrupt cadence mismatch around the main wait gate
- control/status bits around `0x406BB4/0x406BB5`
- sprite-list interpretation still too rough for final display, after boot is unblocked

Good next step:

1. Compare MAME behavior for the task/trap path around `0x010322`, `0x010334`, `0x0038B2`, and `0x0B840A`.
2. Fill concrete missing 020/F3 semantics in code.
3. Re-run the same 1600-frame headless smoke and check whether `pc=0x0011EA` stops dominating.
4. Only then revisit priorities/blending and full sprite/framebuffer fidelity.

## Known Missing Devices

The adapter still reports these as intentionally incomplete:

```text
real M68EC020 core
full F3 trap scheduler
TC0630FDP priorities/blending
full F3 sprite generator
persistent EEPROM/NVRAM
watchdog
ES5505/ES5510 sound
```

## Reference

MAME reference files used for behavior checks:

- `src/mame/taito/taito_f3.cpp`
- `src/mame/taito/taito_f3_v.cpp`

