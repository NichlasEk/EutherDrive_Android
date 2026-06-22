# Gauntlet DL Graphics Plan - 2026-06-22

## Current Baseline

The latest clean visual baseline is the Gauntlet Dark Legacy bring-up preset
without the extra Voodoo FIFO/setup experiments that were briefly added to the
default stack.

Verified default command:

```sh
EUTHERDRIVE_GAUNTDL_BRINGUP_BASELINE=1 \
EUTHERDRIVE_GAUNTDL_WARMUP_STATE=/tmp/eutherdrive-gauntlet-probe/gauntdl-gauntdl24-fast-raw-f180-s200000-87341a65baec.warm \
EUTHERDRIVE_GAUNTDL_WARMUP_FRAMES=180 \
EUTHERDRIVE_GAUNTDL_FRAME_CHECKPOINTS=260,420 \
EUTHERDRIVE_GAUNTDL_DUMP_FRAME=/tmp/gauntdl-fixed-f420.ppm \
dotnet tools/GauntletProbe/bin/Release/net8.0/GauntletProbe.dll \
  /home/nichlas/roms/MAME/Midway/Vegas/gauntd 420 200000 0
```

Verified f420 result:

```text
frameHash=0x44d3a578
directTriangles=2908
setupTriangles=49045
framebuffer=640x480 nonBlack=307200 colored=307200
textureMap.touched=517376
```

The regressed preset result, with the extra Voodoo experiments enabled by
default, was:

```text
frameHash=0xd0412930
directTriangles=300
setupTriangles=134
framebuffer=640x480 nonBlack=31560 colored=31560
```

## Plan

1. Commit the narrow preset fix that keeps the risky Voodoo experiments opt-in.
2. Convert and inspect the f420 framebuffer dump.
3. Re-test the removed Voodoo experiments one at a time from the warm snapshot.
4. Keep any experiment that improves the f420 visual baseline; leave regressions
   opt-in only.
5. After the preset is stable, continue with texture-source/upload debugging
   around the type-5 upload path and hot sparse texture buckets.

## Acceptance Checks

Every promoted graphics change must beat or preserve:

- f420 `nonBlack=307200` and `colored=307200`
- f420 setup-triangle activity around the current `49045` range
- No return to the partial-output `0xd0412930` style regression
- A visually inspectable `/tmp/gauntdl-*.png` dump

## Current Next Target

Isolate the removed Voodoo experiments. The first candidate set is:

```text
EUTHERDRIVE_GAUNTDL_EXPERIMENT_VOODOO_FIFO_BULK_RESET
EUTHERDRIVE_GAUNTDL_EXPERIMENT_VOODOO_FIFO_BULK_RESYNC_LOW_READ
EUTHERDRIVE_GAUNTDL_EXPERIMENT_VOODOO_FIFO_BULK_DECODE_WINDOW
EUTHERDRIVE_GAUNTDL_EXPERIMENT_VOODOO_FIFO_LOW_OFFSET_WRITES
EUTHERDRIVE_GAUNTDL_EXPERIMENT_VOODOO_SUPPRESS_NON_NEUTRAL_FASTFILL
EUTHERDRIVE_GAUNTDL_EXPERIMENT_VOODOO_TEXTURE_MAME_SETUP_GRADIENTS
EUTHERDRIVE_GAUNTDL_EXPERIMENT_VOODOO_MAME_TEXTURE_FIXED_FETCH
EUTHERDRIVE_GAUNTDL_EXPERIMENT_VOODOO_TYPE3_NONFINITE_S_AS_X
EUTHERDRIVE_GAUNTDL_EXPERIMENT_VOODOO_SUPPRESS_IMPLAUSIBLE_BULK_DIRECT_TRIANGLES
```
