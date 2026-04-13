# Super Castlevania IV: Bridge Underside Line Debug Notes

## Scope

This note summarizes the current findings for the SNES rendering bug in `Super Castlevania IV (USA)` where an unwanted horizontal line becomes visible under the bridge / drawbridge area.

The user-provided repro is a savestate placed next to the ROM:

- ROM: `/home/nichlas/roms/Super Castlevania IV (USA).sfc`
- Savestate: `/home/nichlas/roms/Super_Castlevania_IV__USA_.sfc_0ef6f4cc.euthstate`

The visible symptom is a thin line under the bridge that should not be visible.

## Repro

Headless repro works directly from the savestate:

```bash
EUTHERDRIVE_HEADLESS_CORE=snes \
dotnet run --no-build --no-restore \
  --project EutherDrive.Headless/EutherDrive.Headless.csproj -c Release \
  -- --load-savestate \
  '/home/nichlas/roms/Super Castlevania IV (USA).sfc' \
  '/home/nichlas/roms/Super_Castlevania_IV__USA_.sfc_0ef6f4cc.euthstate' \
  1
```

Important conclusion:

- the line is present in headless framebuffer output too
- this is not a desktop UI / Vulkan / presentation-only issue

## Current Conclusion

This currently does **not** look like:

- a Vulkan/UI composition bug
- a viewport bug
- an OBJ-only problem
- a mode 7 gameplay scene

This currently does look like:

- a real SNES core rendering issue
- tied to `BG2`
- isolated to a single scanline transition around `screenY=192`
- likely related to `HDMA`-driven `BG2HOFS` and/or nearby color-math state

## What Was Confirmed

### 1. The line is real core output

The raw headless frame contains the line.

Implication:

- do not chase UI-side cropping, scaling, or Vulkan-specific presentation first

### 2. `BG2` is the layer that matters

Layer isolation showed:

- disabling `BG2` removes the unwanted line
- disabling `OBJ` does **not** remove the line
- `OBJ` is the bridge, but the stray line is not drawn by the bridge sprite layer itself

Implication:

- the bug is in background composition, not in the bridge object art

### 3. The scene is actually `mode 1`, not active gameplay `mode 7`

Targeted PPU tracing showed:

```text
[PPU] BGMODE=0x09 mode=1 l3prio=True
```

The earlier “mode 7” suspicion turned out to be misleading for this scene. A later trace also shows:

```text
[PPU] BGMODE=0x07 mode=7 l3prio=False
```

but that happens during the end-of-frame / next-frame setup, not as the active gameplay mode for the visible bridge scene.

Implication:

- the bridge bug should be debugged as a mode 1 / HDMA / BG2 issue
- mode 7 math is not the primary suspect for the visible line

### 4. The bad output is confined to a single scanline

PPU probe results around the problematic area at `x=150`:

#### `screenY=191`

```text
main=(layer=5,pixel=0x00,color=0x1002)
sub =(layer=5,pixel=0x00,color=0x0000)
bg2 visM=1 visS=0 pix=0x00 rawPix=0x3F prio=0
```

Interpretation:

- no visible BG2 win on this scanline
- final main result is just backdrop

#### `screenY=192`

```text
main=(layer=1,pixel=0x3F,color=0x050F)
sub =(layer=5,pixel=0x00,color=0x0000)
bg2 visM=1 visS=0 pix=0x3F rawPix=0x00 prio=0
```

Interpretation:

- this is the one scanline where `BG2` actually becomes the visible main-screen winner
- this matches the unwanted line

#### `screenY=193`

```text
main=(layer=5,pixel=0x00,color=0x1002)
sub =(layer=5,pixel=0x00,color=0x0000)
bg2 visM=1 visS=0 pix=0x00 rawPix=0x00 prio=1
```

Interpretation:

- the line disappears immediately again
- this is not a broad region corruption, but a very localized transition bug

Implication:

- the highest-value next target is whatever changes right before `screenY=192`

### 5. `HDMA` changes land exactly at the suspicious boundary

The important traced writes around the transition are:

```text
[PPU] CGWSEL=0x02 clip=0 prevent=0 addSub=True directColor=False xy=(1104,192)
[PPU] BG2HOFS write=0xF0 value=0xF000 xy=(1104,192)
[PPU] BG2HOFS write=0x01 value=0x01F0 xy=(1104,192)
```

Earlier in the frame, `BG2HOFS` is different:

```text
[PPU] BG2HOFS write=0xC6 value=0x00C6 xy=(1104,96)
```

Implication:

- there is a deliberate HDMA split at this boundary
- `BG2HOFS` changes from `0x00C6` to `0x01F0`
- the one bad line appears exactly where that split begins

### 6. The active HDMA channels at the transition are now known

DMA tracing at `xy=(1104,192)` shows:

```text
[HDMA-STATE] ch=4 do=1 rep=0x01 mode=0 bbus=0x30 ...
[HDMA-STATE] ch=5 do=1 rep=0x01 mode=2 bbus=0x0F ...
[HDMA-STATE] ch=7 do=0 rep=0x01 mode=0 bbus=0x05 ...
```

Interpretation:

- channel `4` is writing `CGWSEL` (`$2130`)
- channel `5` is writing starting at `BBUS=0x0F`, which matches `BG2HOFS` / adjacent scroll register pair semantics
- channel `7` is writing `BBUS=0x05`, unrelated to the confirmed bad line so far

Implication:

- the glitch is most likely in one of:
  - HDMA timing application for channel 5 scroll writes
  - `BGxHOFS` write-pair semantics / latch behavior
  - the interaction between the `CGWSEL` split and the new `BG2HOFS` value

## Strongest Working Hypothesis

The current best hypothesis is:

- the bridge artifact is caused by the `BG2` HDMA split that begins at `Y=192`
- the emulator makes that split visible on exactly one scanline where hardware likely would not

The most plausible sub-causes are:

1. `BG2HOFS` pair-write semantics are slightly wrong for this HDMA case
2. the HDMA write is applied at the wrong effective scanline boundary
3. `CGWSEL` and `BG2HOFS` are both correct individually, but their relative order/effective-latch point is wrong

## Things Already Ruled Down

### Not a general `mode 7` gameplay bug

The visible bridge scene traces as `mode 1`.

### Not the bridge sprite layer itself

The offending line remains conceptually a background problem even when `OBJ` is not the primary contributor.

### Not a wide corruption region

The visible issue is a one-line transition, not a large broken band.

## Suggested Next Step

The next useful debugging step is:

1. instrument the effective latch point for HDMA-applied `BG2HOFS`
2. compare “write during HBlank” vs “visible line that first consumes that write”
3. test whether delaying or re-latching the `BG2HOFS=0x01F0` split by one visible line removes only the bridge artifact
4. if that works, convert it into a principled fix rather than a scene-specific hack

If timing looks correct, the next suspect after that is:

- the `BGxHOFS` pair-write implementation itself, especially low/high-byte combination during HDMA mode `2`

## Local Debugging Notes

Uncommitted local debug work currently exists in:

- `SuperNintendoEmulator/KSNES/PictureProcessing/PPU.cs`

That local debug work includes:

- layer-disable support usable for SNES isolation
- richer PPU snapshot output
- probe logging for a chosen `x/y`
- extra trace logging for `BGxHOFS/BGxVOFS`

Those changes are currently investigation support, not final bugfix logic.
