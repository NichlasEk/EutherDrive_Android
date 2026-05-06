# XainSleena MCS Perf Handoff - 2026-05-06

## Current known-good base

- Goal: make `xsleena` run well through the MCS/MAME-backed arcade adapter in EutherDrive, with correct video/audio/savestates and acceptable UI FPS.
- ROM used for testing: `/home/nichlas/roms/MAME/XainSleena/xsleena.zip`
- Current better savestate: `/home/nichlas/roms/MAME/XainSleena/xsleena_496d7eaf.euthstate`
- Last committed fix before perf work: `ac3e144 Fix MCS Xain savestate restore`
- User confirmed savestates are effectively working after the pause-state confusion.

## Verified this pass

Build:

```sh
dotnet build EutherDrive.Headless/EutherDrive.Headless.csproj -c Release --no-restore
```

Result: build succeeded, 0 errors, 416 existing warnings.

Headless baseline command:

```sh
EUTHERDRIVE_HEADLESS_CORE=mcs \
EUTHERDRIVE_SAVESTATE_SLOT=1 \
EUTHERDRIVE_MCS_PROFILE=1 \
EUTHERDRIVE_HEADLESS_DUMP_DIR=/tmp/xsleena_perf \
dotnet run --project EutherDrive.Headless/EutherDrive.Headless.csproj -c Release --no-build -- \
  --load-savestate /home/nichlas/roms/MAME/XainSleena/xsleena.zip \
  /home/nichlas/roms/MAME/XainSleena/xsleena_496d7eaf.euthstate \
  600
```

Baseline from the better slot is still roughly mid/high 30 FPS when warm:

- Typical warmed FPS before failed spin experiment: about 35-39 FPS.
- `draw_ms` roughly 155-170 ms/s.
- `update_ms` roughly 175-195 ms/s.
- `audio_ms` negligible in the top-level MCS profile.
- UI/Vulkan is not the main bottleneck; the MCS core and CPU interpreter are.

## CPU profile findings

Command:

```sh
EUTHERDRIVE_HEADLESS_CORE=mcs \
EUTHERDRIVE_SAVESTATE_SLOT=1 \
EUTHERDRIVE_MCS_CPU_PROFILE=1 \
EUTHERDRIVE_MCS_CPU_PC_PROFILE=1 \
EUTHERDRIVE_HEADLESS_DUMP_DIR=/tmp/xsleena_perf \
dotnet run --project EutherDrive.Headless/EutherDrive.Headless.csproj -c Release --no-build -- \
  --load-savestate /home/nichlas/roms/MAME/XainSleena/xsleena.zip \
  /home/nichlas/roms/MAME/XainSleena/xsleena_496d7eaf.euthstate \
  300
```

Important hotspots:

- `:sub` spends a huge amount of time at PC `8014`.
- Local disassembly from `/home/nichlas/mame`/ROM shows:

```asm
8012: ANDCC #$EF
8014: JMP $8014
```

So the sub CPU is idling in a self-jump while waiting for IRQ.

- `:maincpu` hotspots around `8749-8752`:

```asm
8749: STA $3A09
874c: LDA <$3D
874e: ANDA <$3E
8750: ANDA #$02
8752: BEQ $8749
```

This looks like a handshake/busy wait, not a simple safe self-jump.

- `:audiocpu` hotspots include the small YM delay loop around `8810/8811`:

```asm
880e: LDA #$08
8810: DECA
8811: BNE $8810
```

Audio callback/mixing is cheap, but the audio CPU interpreter itself is expensive.

## Uncommitted changes currently in this perf branch

Only these two files are mine from this perf pass:

- `EutherDrive.Core/Arcade/McsArcadeAdapter.cs`
- `Third_party/MCS/mcs/src/src/devices/cpu/m6809/m6809.cs`

Changes:

- Added a raw unsafe palette16 framebuffer copy helper in `McsArcadeAdapter`.
- Added env-gated PC hotspot profiler in `m6809.cs`:
  - `EUTHERDRIVE_MCS_CPU_PC_PROFILE=1`
  - Only allocates/counts when enabled.
- Tried a generic/self-jump spin skip in `m6809.cs`, but it made XainSleena slower at about 30-32 FPS. That experiment has been removed again.

Current build after removing the bad spin experiment succeeds.

Other dirty files existed before and should not be touched/staged as part of this:

- `EutherDrive.Core/Arcade/Cps1/Cps1Ym2151.cs`
- `EutherDrive.Core/MdTracerAdapter.cs`
- `EutherDrive.Core/Sega32X/Sega32XScaffoldCore.cs`
- `EutherDrive.Core/Sega32X/Sega32XSh2Cpu.cs`
- `EutherDrive.Core/SegaCd/SegaCdMemory.cs`
- `EutherDrive.UI/MainWindow.axaml.cs`
- `MYSTWARR_HANDOFF.md`
- `notes/32x_perf_flags_2026-05-02.md`
- `notes/32x_perf_next_dragon_2026-05-01.md`

## What did not work

Generic per-instruction detection of `JMP current_pc` is too expensive in this interpreter shape. Even restricted to `:sub`, the measured run dropped to about 30-32 FPS. Do not reintroduce that exact approach.

Palette-cache-per-frame was also tested earlier and was worse than the direct palette lookup because cache setup cost/jitter ate the gain. It was reverted.

## Best next perf direction

Need a bigger semantic win, not just faster copying.

Most promising paths:

1. Add a scheduler-aware idle path for known M6809 wait states, not a per-instruction generic spin detector.
   - For `:sub` PC `8014`, the CPU is in a pure idle loop after `ANDCC #$EF`.
   - A correct optimization should be event/scheduler based: when `:sub` is exactly at `8014` and no pending interrupt is present, consume remaining cycles without decoding/checking every instruction.
   - Avoid reading opcodes every instruction. Use fixed tag+PC checks or a cached idle state.

2. Investigate whether MCS already has MAME idle/yield semantics that are missing in the C# port.
   - Search in MAME/MCS for `spin_until`, `eat_remaining`, `suspend`, `perfect_quantum`, `abort_timeslice`, `INPUT_LINE`, and driver-specific idle paths.
   - If the original MAME path would yield on this hardware but the port just burns instructions, port that semantic instead of inventing a hack.

3. Look at M6809 generated interpreter overhead.
   - Hot CPUs execute around millions of tiny state-machine steps/sec.
   - The generated m6809 state machine may be doing unnecessary preemption/state dispatch for normal straight-line op execution.
   - Previous removal of some generated preemption states helped correctness/boot; there may be more low-risk cleanup, but it needs profiling.

4. Audio CPU delay loop optimization is possible but secondary.
   - The `LDA #8; DECA; BNE` delay loop is a tight fixed loop.
   - If optimized, it must preserve cycles, A value, flags, and interrupt timing closely enough.
   - Do this after the sub CPU idle issue unless CPU-PC profile says audio dominates the current savestate.

## Useful commands for next pass

Clean headless perf:

```sh
EUTHERDRIVE_HEADLESS_CORE=mcs EUTHERDRIVE_SAVESTATE_SLOT=1 EUTHERDRIVE_MCS_PROFILE=1 EUTHERDRIVE_HEADLESS_DUMP_DIR=/tmp/xsleena_perf dotnet run --project EutherDrive.Headless/EutherDrive.Headless.csproj -c Release --no-build -- --load-savestate /home/nichlas/roms/MAME/XainSleena/xsleena.zip /home/nichlas/roms/MAME/XainSleena/xsleena_496d7eaf.euthstate 600
```

CPU + PC hotspot profile:

```sh
EUTHERDRIVE_HEADLESS_CORE=mcs EUTHERDRIVE_SAVESTATE_SLOT=1 EUTHERDRIVE_MCS_CPU_PROFILE=1 EUTHERDRIVE_MCS_CPU_PC_PROFILE=1 EUTHERDRIVE_HEADLESS_DUMP_DIR=/tmp/xsleena_perf dotnet run --project EutherDrive.Headless/EutherDrive.Headless.csproj -c Release --no-build -- --load-savestate /home/nichlas/roms/MAME/XainSleena/xsleena.zip /home/nichlas/roms/MAME/XainSleena/xsleena_496d7eaf.euthstate 300
```

Build:

```sh
dotnet build EutherDrive.Headless/EutherDrive.Headless.csproj -c Release --no-restore
```

## Update: next pass results

Added two useful runtime changes after the original handoff:

- `device_execute_interface.spin_until_interrupt()` was exposed and used for the known `xsleena/:sub` idle loop at PC `8014`.
- `xain` now skips `config.set_perfect_quantum(m_maincpu)` by default in EutherDrive. Set `EUTHERDRIVE_MCS_XAIN_PERFECT_QUANTUM=1` to restore the original MAME-style perfect quantum if a regression appears.

The failed generic self-jump detector is still removed. A guessed audiocpu delay shortcut was also removed after PC profiling showed the hot `8810/8811` addresses were not the intended tiny delay loop in the active map.

Final default savestate run, no env overrides except profiling:

```sh
EUTHERDRIVE_HEADLESS_CORE=mcs EUTHERDRIVE_SAVESTATE_SLOT=1 EUTHERDRIVE_MCS_PROFILE=1 EUTHERDRIVE_HEADLESS_DUMP_DIR=/tmp/xsleena_perf dotnet run --project EutherDrive.Headless/EutherDrive.Headless.csproj -c Release --no-build -- --load-savestate /home/nichlas/roms/MAME/XainSleena/xsleena.zip /home/nichlas/roms/MAME/XainSleena/xsleena_496d7eaf.euthstate 600
```

Result:

- Warm savestate run is roughly `45-48 fps` instead of the earlier `35-38 fps`.
- `frameCounter` advanced from `5` to `605`.
- Final framebuffer remained valid: `fb_has_content=True`, `nonzero_pixels=50384`.

Cold boot with the faster default also advanced and rendered visible frames:

```sh
EUTHERDRIVE_MCS_XAIN_PERFECT_QUANTUM=0 EUTHERDRIVE_HEADLESS_CORE=mcs EUTHERDRIVE_MCS_PROFILE=1 EUTHERDRIVE_HEADLESS_DUMP_DIR=/tmp/xsleena_cold_perf dotnet run --project EutherDrive.Headless/EutherDrive.Headless.csproj -c Release --no-build -- /home/nichlas/roms/MAME/XainSleena/xsleena.zip 600
```

This reached visible boot/title content and warmed around `51-55 fps` in headless. UI may still be lower due presentation overhead.

Build after these changes:

```sh
dotnet build EutherDrive.Headless/EutherDrive.Headless.csproj -c Release --no-restore
```

Result: `0 errors`, existing warnings only.
