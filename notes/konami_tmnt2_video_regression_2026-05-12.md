# Konami TMNT2 video regression notes - 2026-05-12

Status: improved, still needs live UI smoke testing. The current fixes restore
the large Winners Don't Use Drugs emblem in the slot 2 headless check and
improve the large attract/news sprites that were being clipped out of the frame.
The UI Release project has been rebuilt so `--no-build` runs with the updated
`EutherDrive.Core.dll`. Keep checking other Konami titles before treating this
as final.

## Scope

This investigation is for the native Konami adapter path in:

- `EutherDrive.Core/Arcade/Konami/TmntAdapter.cs`
- `tmnt2.zip` / Turtles in Time
- K052109 tile layers + K053245 sprites + K053251 priority/color control

This is not currently believed to be a UI presenter issue, Vulkan/OpenGL UI
presenter issue, or savestate-specific issue. Savestates only make the broken
scenes easy to reproduce.

## Local MAME references

Compared against the local MAME checkout under `~/mame`:

- `/home/nichlas/mame/src/mame/konami/tmnt2.cpp`
- `/home/nichlas/mame/src/emu/tilemap.cpp`

Important MAME behavior:

- TMNT2 draws three K052109 layers sorted by K053251 priority.
- It clears the screen priority bitmap before drawing.
- The layers are drawn with priority codes `1`, `2`, and `4`.
- K053245 sprite drawing then masks pixels against that priority bitmap.
- MAME tilemap priority writes the current tilemap priority code for the
  visible top tile pixel when using the default priority mask. It does not keep
  an accumulated OR of every layer that was previously drawn at that pixel.

## Changes made so far

### 1. Removed savestate framebuffer workaround

Earlier workaround commit:

- `463f413 Refresh TMNT framebuffer after savestate load`

That workaround refreshed a loaded framebuffer after savestate load. It was the
wrong direction for this regression because the broken TMNT2 scenes are caused
by video composition state, not by stale host framebuffer presentation.

This was removed by:

- `8dd51aa Fix TMNT2 sprite Y wrap`

### 2. K053245 TMNT2 sprite Y wrap

Committed fix:

- `8dd51aa Fix TMNT2 sprite Y wrap`

The TMNT2 coordinate mode originally had a bad negative-Y wrap correction:

- old behavior: add `384`
- new behavior: add `512`

This only applies when:

- `Tmnt2CoordinateMode` is active
- raw sprite Y is in `0x0100..0x01ff`
- computed Y is below `-128`

Reasoning: the previous `384` wrap could place sprites/layer-relative objects
too high after restore or during scenes with wrapped coordinates. This matched
some of the "Leo floating" / split-object symptoms but did not solve all layer
composition issues.

Regression risk:

- Should affect only TMNT2-coordinate-mode K053245 rendering.
- Check any future adapter title that reuses `Tmnt2CoordinateMode`.

### 3. K053245 high Y phase wrap

Current code change:

```csharp
if (rawY is >= 0x0180 and < 0x0200 && oy >= 384)
    oy -= 512;
else if (rawY is >= 0x0100 and < 0x0200 && oy >= Tmnt2RawFrameHeight)
    oy -= 128;
```

Reasoning: slot 2 showed the Winners text but missed the large round emblem.
Tracing the active K053245 sprites showed the emblem sprite as an `8x8` object
with `rawY=0x19f`, but the computed bounds were `y=410..538`, outside the
240-line raw render target. Wrapping that high raw-Y phase by the full
512-pixel sprite span moves it to `y=26..154`, which restores the emblem while
leaving the text sprites at `y=154..210`.

Regression risk:

- This is limited to `Tmnt2CoordinateMode`.
- It affects large/high-phase K053245 sprites in TMNT2 attract/news/legal
  scenes and may also affect other TMNT2 scenes using raw Y `0x180..0x1ff`.
- It should be checked against gameplay scenes because the older mid-phase
  `-128` correction is still used for `0x100..0x17f`.

### 4. K052109 priority buffer should not OR layer codes

Current code change:

```csharp
priorityBuffer[sy * FrameWidth + sx] = (byte)priorityCode;
```

Previous behavior:

```csharp
priorityBuffer[sy * FrameWidth + sx] =
    (byte)(priorityBuffer[sy * FrameWidth + sx] | priorityCode);
```

Reasoning: TMNT2 sprite masks are based on priority values `0`, `1`, `2`, `4`,
and the sprite callback builds masks like MAME. OR-ing multiple tile layers can
produce combined values such as `3`, `5`, or `7`, which are not the priority
codes the sprite mask table expects. That can hide or reveal sprite pixels
incorrectly, for example Leo's head disappearing behind the wrong layer.

Why this still needs testing:

- UI and headless must both be checked after rebuilding the UI project, because
  `--no-build` can otherwise run an older `EutherDrive.Core.dll`.
- Other Konami K052109/K053245 titles should still be smoke-tested.

Regression risk:

- This K052109 priority-buffer path is currently used by TMNT2 rendering.
- It does not change the K056832/Moo Mesa priority code path, which still has
  its own `RenderLayerWithPriority` implementation.
- If another K052109 Konami title later starts passing a priority buffer into
  `K052109.RenderLayer`, this assignment behavior should be closer to MAME than
  OR accumulation, but it still needs game-specific visual checks.

## Repro / verification commands

Build checks used during this pass:

```bash
MSBUILDDISABLENODEREUSE=1 dotnet build EutherDrive.Headless/EutherDrive.Headless.csproj -c Release --no-restore -v:minimal -m:1 -nr:false

MSBUILDDISABLENODEREUSE=1 dotnet build EutherDrive.UI/EutherDrive.UI.csproj -c Release --no-restore -v:minimal -m:1 -nr:false
```

Headless checks, using `--no-build` as requested:

```bash
EUTHERDRIVE_HEADLESS_CORE=tmnt2 EUTHERDRIVE_SAVESTATE_SLOT=1 EUTHERDRIVE_HEADLESS_DUMP_DIR=/tmp/tmnt2_pri_s1 dotnet run --no-build --project EutherDrive.Headless/EutherDrive.Headless.csproj -c Release -- --load-savestate /home/nichlas/roms/MAME/turtles2/tmnt2.zip /home/nichlas/roms/MAME/turtles2/tmnt2.zip_b3bd9b1c.euthstate 1

EUTHERDRIVE_HEADLESS_CORE=tmnt2 EUTHERDRIVE_SAVESTATE_SLOT=2 EUTHERDRIVE_HEADLESS_DUMP_DIR=/tmp/tmnt2_pri_s2 dotnet run --no-build --project EutherDrive.Headless/EutherDrive.Headless.csproj -c Release -- --load-savestate /home/nichlas/roms/MAME/turtles2/tmnt2.zip /home/nichlas/roms/MAME/turtles2/tmnt2.zip_b3bd9b1c.euthstate 2

EUTHERDRIVE_HEADLESS_CORE=tmnt2 EUTHERDRIVE_SAVESTATE_SLOT=3 EUTHERDRIVE_HEADLESS_DUMP_DIR=/tmp/tmnt2_pri_s3 dotnet run --no-build --project EutherDrive.Headless/EutherDrive.Headless.csproj -c Release -- --load-savestate /home/nichlas/roms/MAME/turtles2/tmnt2.zip /home/nichlas/roms/MAME/turtles2/tmnt2.zip_b3bd9b1c.euthstate 3
```

## Scenes to keep checking

TMNT2:

- Slot 1 bridge scene: Leo's head/body should not intermittently disappear
  behind the wrong foreground object.
- Slot 2/3 attract/legal scenes: Winners Don't Use Drugs text and the large
  round FBI/emblem symbol should appear when MAME shows them.
- April/news scene: April, the background, foreground text, and any Krang/news
  elements should match MAME without split or missing pieces.
- Startup/coin scene: text layer and sprites should still have correct priority.

Other Konami titles to regression-check if they are wired to this adapter later:

- `ssriders`
- `lgtnfght`
- `punkshot`
- `thndrx2`

Moo Mesa note:

- `moomesa` uses the K056832 path in this adapter, not the K052109 path changed
  above. It is still worth a smoke test because it shares K053245 sprite masking,
  but the K052109 priority-buffer assignment should not directly change it.

## Current hypothesis

If more TMNT2 issues remain, they are probably still in layer/priority
composition or sprite placement rather than savestates or host presentation.
Highest-value next checks:

1. Compare the exact K053251 priority values and color bases for the bad April
   and bridge frames against MAME.
2. Compare `SpritePriorityMask` against MAME's TMNT2 sprite callback for the
   same raw sprite colors in the failing scenes.
3. Verify whether K052109 transparent pen handling and layer draw order match
   MAME for the bad frames.
4. If those match, inspect K053245 sprite tile banking/zoom/wrap for the
   remaining split or missing objects.
