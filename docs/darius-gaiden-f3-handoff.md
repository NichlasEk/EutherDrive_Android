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

Post-crash continuation on 2026-05-16:

```sh
dotnet build EutherDrive.sln
dotnet run --project EutherDrive.Headless/EutherDrive.Headless.csproj --no-build /home/nichlas/roms/MAME/TAITO/dariusg.zip 1600
```

Result with the current working tree:

- build still passes, with existing repo warnings
- framebuffer still has content and no unmapped reads/writes
- current stable blocker is now the idle/task-dispatch loop at `pc=0x002326`, not `pc=0x0011EA`
- final task queue is `010322,0038B2,0043AE`
- `0x0043AE` is a frame/tick wait path that tests `0x406BB6`
- forcing `0x406BB6` from the frame latch did not change the final signature because the ROM clears that byte later
- MAME confirms IRQ2 vblank plus delayed IRQ3 after 10000 main CPU cycles in `src/mame/taito/taito_f3.cpp`; the local adapter already mirrors that broad cadence
- sprite list is populated (`listPtr=0x600310`, `sprNZ=3101`) but the current renderer still reports no visible sprite pixels from that list (`sprVis=0`)

Representative final debug signature after the post-crash run:

```text
pc=0x002326 op=0x60F8 tasks=3 q=010322,0038B2,0043AE
taskEnq=2632 taskRun=1982
lastTrap=0x0043AC
scene=entry:190/init:190,190,190,0/mainwait:189/cont:190,190,0
gateEbb4=0x01 gateEbb5=0x00 gateEbb6=0x00
listPtr=0x600310 sprNZ=3101 sprVis=0
unmappedR=0 unmappedW=0
```

Pause handoff on 2026-05-16 after commit `5dcfd93`:

The current local target is past the old `COIN ERROR` blocker. The game now boots into the Zone A transition, showing the `ZONE A` label at the top of an otherwise black frame. It does not progress into visible gameplay during a 1800-frame headless run.

Changes in the current pass:

- added a sprite-bank fallback when building the F3 sprite list, so an empty selected bank can retry the opposite bank without permanently corrupting the command-selected bank
- added playfield render diagnostics to the debug line:
  - `pfCand`
  - `pfPix`
- confirmed playfield rendering is seeing and decoding a lot of candidate pixels internally

Current 1800-frame headless command:

```sh
dotnet run --project EutherDrive.Headless/EutherDrive.Headless.csproj --no-build /home/nichlas/roms/MAME/TAITO/dariusg.zip 1800
```

Observed final state:

- final framebuffer fingerprint: `0x8E81D3B5E8907A53`
- the framebuffer content is stable from roughly frame 660 onward
- visible output remains the `ZONE A` transition frame, not gameplay
- `pc=0x00569C`, `op=0x322E`
- task scheduler is active and repeatedly running tasks
- queue remains `0038B2,0C63F2:1,01048E:1,010508:1,004154`
- `taskEnq=89005`, `taskRun=88998`
- scene counters are stuck in wait/mainwait growth:
  - `scene=entry:1/init:1,1,1,1`
  - `wait=14829`
  - `mainwait=14829`
- playfield diagnostics show the renderer is not empty:
  - `pfNZ=1386`
  - `pfCand=100224`
  - `pfPix=86759`
- sprite list data exists but current visible sprites are effectively gone by final frame:
  - `listPtr=0x608310`
  - `sprNZ=3120`
  - `sprCand=1`
  - `sprVis=0`
  - `sprPix=0`
- earlier around frame 660, sprite fallback did produce visible sprite pixels:
  - `sprCand=17`
  - `sprVis=16`
  - `sprPix=3164`

Important interpretation:

`pfCand/pfPix` proves the playfield pass is decoding nonzero tiles and pens. The all-black area under `ZONE A` is therefore probably not a simple "no playfield data" problem. Next pass should start with one of these:

1. Scene wait / scheduler semantics around the current wait loop, especially why `pc=0x00569C` keeps returning to the same task set and never advances the Zone A transition.
2. F3 mixer initialization/composition. The local `_mixSrcPriority` starts at zero, while MAME's `pri_mode` starts as zeroed but accepts priority-zero source pixels through the `color` path only when the source comparison wins. If Darius is using priority-zero playfields at this point, local source/destination selection may be hiding valid playfield pixels.
3. Palette/mix output verification. Since `pfPix` is high, sample `_mixSrcPalette`, `_mixSrcBlend`, `_mixDstPalette`, and final RGB for a few mid-screen playfield pixels before changing tile decode again.

Useful files/places:

- `EutherDrive.Core/Arcade/Taito/DariusGaidenAdapter.cs`
- `BuildSpriteList()` and `BuildSpriteListFrom(...)`
- `RenderPlayfieldLayer(...)`
- `WritePalettePixel(...)`
- `RenderMameMixBufferToFrame()`
- MAME reference: `/home/nichlas/mame/src/mame/taito/taito_f3_v.cpp`, especially `mix_line()` and `scanline_draw()`

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
