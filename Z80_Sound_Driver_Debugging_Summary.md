# Z80 Sound Driver Debugging Summary - Sonic 1

## Overview
Attempted to get the Z80 processor to run a custom sound driver program that would play audio through the YM2612 DAC in Sonic 1, but ultimately had to revert changes because they broke Sonic 1's normal sound.

## Root Problems Identified
1. **Z80 safe boot is disabled** (`Z80SafeBootEnabled = false` in `md_bus.cs`) - When disabled, the 68K doesn't upload the Z80 program
2. **Z80 executes NOPs forever** - Without safe boot, Z80 RAM starts cleared to 0x00, so Z80 executes infinite NOPs at address 0x0000
3. **Our hack broke Sonic 1's sound** - By overwriting Z80 RAM with our program, we destroyed Sonic 1's actual sound driver

## Files Modified and Reverted
### Modified Files (now reverted via `git stash`)
1. **`EutherDrive.Core/MdTracerCore/md_z80_memory.cs`**
   - Added `LoadSonic1Z80Program()` method that loaded a custom Z80 program at address 0x0167
   - Added hack in `read8()` to read environment variable `EUTHERDRIVE_Z80_TRIGGER_BEEP` at address 0x1FF0
   - Modified `ClearZ80Ram()` to call our hack program

2. **`EutherDrive.Core/MdTracerCore/md_z80.cs`**
   - Modified `reset()` to set `g_reg_PC = 0x0167` instead of `0x0000` (bypassing boot vector)

3. **`EutherDrive.Core/MdTracerCore/md_bus.cs`**
   - Changed `Z80SafeBootEnabled = true` (then back to `false`)

4. **`EutherDrive.UI/MainWindow.axaml.cs`**
   - Modified `UpdateDacTest()` to set `EUTHERDRIVE_Z80_TRIGGER_BEEP=1` when DAC test button is pressed
   - Added `_lastDacTestState` field for edge detection

## What Worked
✅ **Z80 can execute code** - Our reset hack made Z80 start at 0x0167  
✅ **Z80 can write to YM2612** - We saw Z80 write `0x2B` (DAC enable register) and `0x80` (enable bit) to YM2612 address port 0x4040  
✅ **Z80 can write to DAC** - We saw Z80 write `0xC0` and `0x00` values to DAC data port 0x4041 (square wave)  
✅ **Z80 I/O port mapping works** - OUT (0x40), A writes to memory address 0xA04000 (YM2612 port 0)

## What Didn't Work
❌ **No sound was heard** - Despite Z80 writing to DAC, no audio was produced  
❌ **Z80 was held in reset with safe boot enabled** - `g_active=False, reset=True`  
❌ **Our hack destroyed Sonic 1's sound** - User reported Sonic 1 had no sound at all with our changes

## Current State (After Revert)
- All changes have been stashed with `git stash`
- Code is back to commit `0ca3de6` "audio(z80): improve scheduling for steadier sound"
- Sonic 1 should have normal sound again
- Z80 safe boot is disabled (`Z80SafeBootEnabled = false`)
- Z80 likely executes NOPs forever (0x00 bytes in RAM)

## Key Technical Decisions Made
1. **Bypassed boot vector** - Set PC directly to 0x0167 instead of fixing JP at 0x0000 → 0x0167
2. **Environment variable trigger** - Used `EUTHERDRIVE_Z80_TRIGGER_BEEP` for UI to trigger Z80 beep
3. **Memory-mapped hack** - Made address 0x1FF0 return value from environment variable
4. **Had to revert everything** - Because it broke Sonic 1's actual sound

## Critical Files for Next Session
1. `md_z80_memory.cs` - Z80 RAM and I/O handling
2. `md_z80.cs` - Z80 CPU core and reset logic  
3. `md_bus.cs` - Z80 bus arbitration and safe boot logic (`Z80SafeBootEnabled = false`)
4. `md_music_ym2612_regster.cs` - YM2612 register handling (DAC enable at register 0x2B)
5. `md_music_ym2612_core.cs` - YM2612 audio generation (DAC test tone code)

## What Needs to Be Done Next
The fundamental issue is **Z80 safe boot is disabled**. When enabled, the 68K should upload the Z80 program before releasing reset. But we disabled it, so Z80 executes garbage (NOPs).

### Options for next session:
1. **Enable safe boot properly** - Fix the timing so 68K uploads Z80 program, then Z80 runs
2. **Debug why DAC output isn't heard** - Even when Z80 writes to DAC, no sound comes out
3. **Test with actual Sonic 1 Z80 program** - Instead of our hack, let 68K load real program
4. **Fix the "SEEGA!" sample playback** - Original goal was to get Sonic 1's startup sample playing

## Environment Variables That Matter
- `EUTHERDRIVE_TRACE_Z80=1` - Enable Z80 execution tracing
- `EUTHERDRIVE_TRACE_DAC=1` - Enable DAC write logging  
- `EUTHERDRIVE_TRACE_YM=1` - Enable YM2612 register logging
- `EUTHERDRIVE_DAC_TEST_TONE=1` - Enable YM2612 direct test tone (bypasses Z80)
- `EUTHERDRIVE_Z80_SAFE_BOOT=1` - Should enable safe boot (currently hardcoded to `false`)

## Build and Run Commands
```bash
cd /home/nichlas/EutherDrive
dotnet build --configuration Debug
EUTHERDRIVE_TRACE_Z80=1 ./EutherDrive.UI/bin/Debug/net8.0/EutherDrive.UI ~/roms/sonic1.md
# OR for headless testing:
dotnet run --project EutherDrive.Headless/EutherDrive.Headless.csproj ~/roms/sonic1.md
```

## Next Steps
To continue debugging, we need to:
1. Investigate why `Z80SafeBootEnabled = false` is hardcoded
2. Understand the proper timing for 68K to upload Z80 program
3. Test with actual Sonic 1 Z80 program instead of our custom hack
4. Ensure DAC audio generation is working correctly in the YM2612 core