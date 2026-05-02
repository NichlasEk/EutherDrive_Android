# Ryu64 N64 graphics handoff - 2026-05-02

## Goal

Continue the Ryu64/N64 bringup toward real graphics. The current pass removed a false positive green framebuffer artifact and added tighter diagnostics around RDP textured rectangles.

## Current state

- Ocarina of Time reaches RDP textured rectangle execution and writes into the color image.
- The visible first OoT texrect band samples successfully and writes pixels, but the sampled color is currently black/opaque:
  - `hits=579 misses=0`
  - `firstRgba=0x000000ff`
  - `firstStored=0x0001`
- The previous all-green output was not real graphics. It was an adapter byte-order artifact: RGBA5551 black/opaque (`0x0001`, bytes `00 01`) could be auto-detected as swapped (`0x0100`), which displays as green.
- `N64Adapter` now avoids scoring swapped output as valid color when the normal value is plain black/opaque. That removes the green false positive.

## Changed files

- `EutherDrive.Core/N64Adapter.cs`
  - Tightened RGBA5551 byte-order detection so black/opaque `0x0001` does not falsely boost the swapped score.
- `Ryu64/Ryu64.MIPS/Memory.cs`
  - Added `EUTHERDRIVE_TRACE_N64_RDP_TEXRECT=1` gated `texrect-write` logging.
  - The trace records sample hit/miss counts, first framebuffer address, first sampled RGBA, first stored RGBA5551 value, and tile metadata.

## Verification

Build:

```bash
dotnet build EutherDrive.Headless/EutherDrive.Headless.csproj -c Release -nologo -v minimal -nr:false -p:IncludeOptionalCores=true
```

Result: build succeeded with existing warnings, 0 errors.

Useful OoT trace command:

```bash
timeout 120s env EUTHERDRIVE_HEADLESS_DUMP_DIR=/tmp/ryu64_oot_probe \
  EUTHERDRIVE_TRACE_N64_RDP_TEXRECT=1 \
  EUTHERDRIVE_N64_HEADLESS_STOP_ON_FRAMEBUFFER=1 \
  EUTHERDRIVE_N64_HEADLESS_STOP_STABLE_FRAMES=1 \
  EUTHERDRIVE_N64_RSP_INTERPRETER=1 \
  EUTHERDRIVE_N64_RSP_INTERPRETER_GRAPHICS_ONLY=1 \
  EUTHERDRIVE_N64_RSP_GRAPHICS_HLE_FALLBACK=0 \
  EUTHERDRIVE_N64_RSP_TASK_MAX_INSTRUCTIONS=20000000 \
  EUTHERDRIVE_N64_RSP_TASK_NO_PROGRESS_LIMIT=2000000 \
  EUTHERDRIVE_N64_RUNFRAME_WAIT_MS=5 \
  EUTHERDRIVE_N64_BRINGUP_RUNFRAME_WAIT_MS=5 \
  EUTHERDRIVE_N64_BRINGUP_FRAME_LIMIT=400 \
  dotnet EutherDrive.Headless/bin/Release/net8.0/EutherDrive.Headless.dll \
    "/home/nichlas/roms/N64/Legend of Zelda, The - Ocarina of Time (USA) (Rev 2).z64" 6500 \
  > /tmp/ryu64_oot_probe.log 2>&1
```

Observed before the adapter fix:

```text
[N64RDP] texrect-write ci=0x003b5000 rect=(97,94)-(289,96) tile=0 hits=579 misses=0 firstAddr=0x003c3bc2 firstRgba=0x000000ff firstStored=0x0001 tileSizeSet=True tile=fmt4:sz1:line24:tmem0x0:uls0:ult0:lrs764:lrt4
```

The produced PPM was one color, `(0, 32, 0)`, due to the byte-order artifact.

Observed after the adapter fix:

```text
[N64Adapter] RGBA5551 byte-order auto-detect: swap=False scoreNormal=0 scoreSwapped=0
```

The 120s OoT run timed out before producing a final `headless_output.ppm`, but it reached framebuffer recovery/steady state. Since black/opaque has zero RGB, the existing headless content detector may no longer treat this as a completed visible frame.

## Next work

- Continue implementing real RDP texture semantics rather than treating framebuffer presence as image success.
- Investigate why the OoT I8 texture path samples black in the visible texrect band:
  - `LoadBlock` source addressing and byte count.
  - TMEM line/stride and swizzle behavior.
  - tile format/size selection for `fmt=4 sz=1`.
  - combiner mode and shade/texture contribution.
- Adjust headless framebuffer stop/content scoring so black/opaque framebuffer writes can still be captured as valid write activity, while visual success remains measured separately.
- Separate the remaining RSP HLE dispatcher warning from graphics progress; it still appears even with graphics fallback disabled, likely due non-graphics task handling.

