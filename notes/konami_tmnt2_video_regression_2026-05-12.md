# Konami TMNT2 video regression notes - 2026-05-12

Status: fixed for the reproduced Sunset Riders sprite regression and visually
confirmed fixed for the current TMNT2/Turtles states on 2026-05-12. Rampage /
presenter issues were a separate regression; these fixes are in the Konami
adapter path. The latest code isolates several K053245 behaviors per legacy
game instead of letting later GX/Moo Mesa/Metamorphic work leak into TMNT2 and
SSR.

## Scope

This investigation is for the native Konami adapter path in:

- `EutherDrive.Core/Arcade/Konami/TmntAdapter.cs`
- `tmnt2.zip` / Turtles in Time
- `ssriders.zip` / Sunset Riders smoke checks
- K052109 tile layers + K053245 sprites + K053251 priority/color control

This is not currently believed to be a UI presenter issue, Vulkan/OpenGL UI
presenter issue, or savestate-specific issue. Savestates only make the broken
scenes easy to reproduce.

## Local MAME references

Compared against the local MAME checkout under `~/mame`:

- `/home/nichlas/mame/src/mame/konami/tmnt2.cpp`
- `/home/nichlas/mame/src/mame/konami/k053244_k053245.cpp`
- `/home/nichlas/mame/src/mame/konami/k052109.cpp`
- `/home/nichlas/mame/src/emu/tilemap.cpp`

Important MAME behavior:

- TMNT2 draws three K052109 layers sorted by K053251 priority.
- It clears the screen priority bitmap before drawing.
- The layers are drawn with priority codes `1`, `2`, and `4`.
- K053245 sprite drawing then masks pixels against that priority bitmap.
- MAME's default tilemap priority mask updates priority as
  `(*pri & 0xff) | priority`, so this path accumulates the layer bitmask for
  opaque pixels.
- Legacy K053245 draws 128 sprites and skips non-zero offsets whose z/priority
  code equals `m_z_rejection`.
- TMNT2 maps sprite RAM as raw CPU RAM for reads plus scattered write-side
  effects; Sunset Riders maps the same region with scattered read/write.

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

Reasoning: slot 2 showed the Winners text but missed the expected large round
emblem. Tracing active K053245 sprites showed a likely emblem candidate as an
`8x8` object with high raw Y, but the computed bounds were outside the 240-line
raw render target. Wrapping that high raw-Y phase by the full 512-pixel sprite
span improved one headless check, but later UI checks still showed the expected
emblem/mask was not fully correct.

Regression risk:

- This is limited to `Tmnt2CoordinateMode`.
- It affects large/high-phase K053245 sprites in TMNT2 attract/news/legal
  scenes and may also affect other TMNT2 scenes using raw Y `0x180..0x1ff`.
- It should be checked against gameplay scenes because the older mid-phase
  `-128` correction is still used for `0x100..0x17f`.

### 4. K052109 priority buffer accumulates layer bitmasks

Current code change:

```csharp
priorityBuffer[sy * FrameWidth + sx] =
    (byte)(priorityBuffer[sy * FrameWidth + sx] | priorityCode);
```

This matches MAME's bitmask priority scheme for up to four layers. A temporary
assignment-only change made some objects look better, but the later Shredder,
Krang, and hole-mask checks showed that sprites were then being tested against
the wrong per-pixel layer mask.

Temporary behavior that was reverted:

```csharp
priorityBuffer[sy * FrameWidth + sx] = (byte)priorityCode;
```

Reasoning: TMNT2 sprite masks use `0xf0`, `0xf0 | 0xcc`, and
`0xf0 | 0xcc | 0xaa`, i.e. MAME's bitmask scheme. The priority bitmap should
therefore contain a mask of which tile layers are opaque at a pixel, not just a
single top-layer code.

### 5. K052109 scroll control row/column lookup

Current code change:

- The row count now decodes `scrollctrl & 0x03`; the column-scroll bit
  `0x04` no longer accidentally turns rows into 256.
- Colscroll now selects the Y-scroll entry from the tilemap column after
  X-scroll, matching MAME's `set_scrolly((offs + xscroll / 8) & 0x3f, ...)`
  layout.
- Rowscroll now indexes the X-scroll table by screen scanline, matching MAME's
  `(offs + yscroll) & 0xff` setup for the rowscroll table.

Reasoning: the remaining broken slot 1/2/3 scenes look like planes are present
but shifted or used as a wrong mask. MAME's K052109 uses separate row and column
scroll tables, and our previous implementation mixed the column-scroll control
bit into row count and used different row/column indices.

Why this still needs testing:

- UI and headless must both be checked after rebuilding the UI project, because
  `--no-build` can otherwise run an older `EutherDrive.Core.dll`.
- Other Konami K052109/K053245 titles should still be smoke-tested.

Regression risk:

- This K052109 priority-buffer path is currently used by TMNT2 rendering.
- It does not change the K056832/Moo Mesa priority code path, which still has
  its own `RenderLayerWithPriority` implementation.
- K052109 row/column scroll is shared by any future title using this nested
  `K052109` implementation. Check `ssriders`, `lgtnfght`, `punkshot`, and
  `thndrx2` if they are enabled through this adapter.

### 6. Removed TMNT2 partial-render experiment

Temporary behavior that was reverted:

- `RunFrame` now reports visible-cycle progress to the TMNT bus while the
  visible section is executing.
- TMNT2/K053245 hardware calls a partial render before each K052109 write,
  matching MAME's `m_screen->update_partial(m_screen->vpos() - 1)` in
  `tmnt2_base_state::k052109_word_w`.
- The partial renderer is scoped to the K052109/K053245 path and renders only
  the raw scanline range that has elapsed. Final frame rendering then completes
  the remaining raw lines.

Reasoning for trying it: the remaining live issues were split by vertical region: the
Shredder CRT overlay was correct in the upper part but not the lower part, and
slot 1/3 had planes or masks that looked like they came from the wrong moment
in the frame. MAME explicitly flushes the current screen before K052109 writes
because TMNT2 changes tile state mid-frame. A single end-of-frame render uses
only the final K052109 state and loses those per-scanline transitions.

Why it was removed:

- It did not improve the user-visible slot 1/2/3 failures.
- It made the headless path much slower, around 150 ms for single-frame checks.
- The current code renders TMNT2 once at end of frame again.

### 7. K053245 legacy vs GX split

Current code change:

- Legacy K053245 games (`tmnt2`, `ssriders`) use a 128-sprite sort window.
- GX-style users (`mystwarr`, `metamrph`, Moo Mesa normal-plane path) keep the
  256-entry path.
- `ZRejection` is enabled only for K053245 legacy hardware and set to `0`,
  matching MAME's `lgtnfght_state::video_start()`.

Reasoning: later work for Moo Mesa and Metamorphic Force expanded K053245 into
a more general object renderer. TMNT2/SSR need the older K053244/K053245 rules:
128 sorted slots, z-rejection, and buffered legacy sprite RAM.

Regression risk:

- This intentionally changes `tmnt2` and `ssriders`.
- It should not change K056832/Moo Mesa or Mystic Warriors GX ordering.

### 8. TMNT2 vs Sunset Riders sprite-RAM reads

Current code change:

- `tmnt2` reads `0x180000..0x183fff` from raw CPU sprite RAM.
- `ssriders` keeps scattered reads for the same range.
- Both still use scattered writes to update the K053245 hardware RAM.
- TMNT2 protection helper reads now use raw CPU sprite RAM too.

Reasoning: MAME's TMNT2 map is `.ram().w(k053245_scattered_word_w)`, while
Sunset Riders is `.rw(k053245_scattered_word_r, k053245_scattered_word_w)`.
Using scattered reads globally is a per-game mismatch and can feed protection
or gameplay code with the wrong alias when multiple CPU offsets target the same
K053245 hardware word.

Verification note:

- The existing TMNT2 savestate slots did not visibly change after this fix,
  which suggests those states already reached the problematic sprite output
  through other inputs. The map split is still kept because it matches MAME and
  prevents future cold-boot/protection divergence.

### 9. Sunset Riders K053244 `noA1` control register mapping

Current code change:

- TMNT2/SSR reads and word writes to `0x5a0000..0x5a001f` now pass a 68k
  word-offset into `ReadControlWordNoA1` / `WriteControlWordNoA1`.
- Byte writes to the same range now mirror A1 at the word-offset level before
  selecting the high or low byte lane.

Reasoning: MAME's `k053244_word_noA1_r/w` handler receives a word offset and
then applies `offset &= ~1`. The previous EutherDrive code passed a byte offset
from the 68k address. That allowed K053244 control bytes to land in the wrong
internal registers.

Observed failure before the fix:

- Sunset Riders slot 1 had active K053245 entries and protection ran every
  frame, but all objects were offscreen.
- The debug register summary showed `regs=03/A3/00/00/F9/F4/00/00`, putting
  `F9` into register 5 and enabling the wrong global sprite control state.

Observed behavior after the fix:

- A cold boot reaches visible Sunset Riders sprites.
- The K053244 register summary becomes `regs=03/A3/02/F9/00/00/00/00`, matching
  the expected offset/control layout for this path.
- User-created fresh Sunset Riders slot 1 state shows sprites again.
- User also confirmed the current TMNT2/Turtles states render correctly after
  this pass.

Regression risk:

- This deliberately changes only the legacy TMNT2/SSR `0x5a0000` K053244 path.
- GX-style object paths for Mystic Warriors, Metamorphic Force, and Moo Mesa use
  different control-register mappings and are not changed by this item.

## Repro / verification commands

Build checks used during this pass:

```bash
MSBUILDDISABLENODEREUSE=1 dotnet build EutherDrive.Headless/EutherDrive.Headless.csproj -c Release --no-restore -v:minimal -m:1 -nr:false

MSBUILDDISABLENODEREUSE=1 dotnet build EutherDrive.UI/EutherDrive.UI.csproj -c Release --no-restore -v:minimal -m:1 -nr:false
```

Note: the latest UI build hit an Avalonia PDB file lock while the app was
running, but `EutherDrive.Core.dll` was successfully rebuilt and copied into
`EutherDrive.UI/bin/Release/net8.0/`, which is what the `--no-build` run needs
after restarting the UI process.

Latest verification after the per-game K053245 pass:

```bash
dotnet build EutherDrive.Headless/EutherDrive.Headless.csproj -c Release --no-restore -v:minimal -m:1 -nr:false

EUTHERDRIVE_HEADLESS_CORE=tmnt2 EUTHERDRIVE_SAVESTATE_SLOT=1 dotnet run --no-build --project EutherDrive.Headless/EutherDrive.Headless.csproj -c Release -- --load-savestate /home/nichlas/roms/MAME/turtles2/tmnt2.zip /home/nichlas/roms/MAME/turtles2/tmnt2.zip_b3bd9b1c.euthstate 1
```

Sunset Riders cold-boot verification that showed visible sprites after the
`noA1` mapping fix:

```bash
EUTHERDRIVE_HEADLESS_CORE=tmnt2 EUTHERDRIVE_HEADLESS_DUMP_DIR=/tmp/ssr_cold2400 EUTHERDRIVE_TMNT_SPRITE_TRACE=1 dotnet run --no-build --project EutherDrive.Headless/EutherDrive.Headless.csproj -c Release -- /home/nichlas/roms/MAME/sunsetriders/ssriders.zip 2400
```

The headless build completed with existing warnings and no errors. The UI
Release `EutherDrive.Core.dll` was copied from the Release build output so a
UI `--no-build` run picks up the adapter change after restarting the app.

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
3. Compare K053245 sprite bounds against MAME for the bad raw Y ranges, notably
   `0x17f..0x1cf` in the slot 1/2 attract scenes.
4. Verify K052109 mixed row+column scroll against MAME's `tile_blitter` path if
   planes are still shifted.
5. Verify whether K052109 transparent pen handling and layer draw order match
   MAME for the bad frames.
6. If those match, inspect K053245 sprite tile banking/zoom/wrap for the
   remaining split or missing objects.
