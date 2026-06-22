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

Isolate the removed Voodoo experiments. The first candidate set was:

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

## 2026-06-22 Probe Matrix

All runs used the f180 warm snapshot and the default baseline, with one removed
Voodoo experiment enabled at a time.

Baseline reference:

```text
f260 frameHash=0xe806de53 direct/setup=710/337
f420 frameHash=0x44d3a578 direct/setup=2908/49045
f420 framebuffer=307200/307200 zeroTexels=30913347 textureMap.touched=517376
```

Results:

```text
FIFO_BULK_RESET:
  f420 0x51841dc5 direct/setup=1051/506 framebuffer=307200/307200
  Keeps coverage but collapses setup-triangle activity. Keep opt-in.

FIFO_BULK_RESYNC_LOW_READ:
  f420 0x51841dc5 direct/setup=1051/506 framebuffer=307200/307200
  Same simplified scene shape as FIFO_BULK_RESET. Keep opt-in.

FIFO_BULK_DECODE_WINDOW:
  f420 0xf15a2439 direct/setup=939/450 framebuffer=307200/307178
  Nearly full coverage but simplified scene and changed hash. Keep opt-in.

FIFO_LOW_OFFSET_WRITES:
  f420 0x44d3a578 direct/setup=2269/48729 framebuffer=307200/307200
  Preserves the visual hash and coverage; textureMap.touched drops to 500992.
  Candidate for later targeted testing, but not needed in default.

SUPPRESS_NON_NEUTRAL_FASTFILL:
  f420 0x8b701bbb direct/setup=2908/49045 framebuffer=285925/285925
  Clear framebuffer regression. Keep opt-in only.

TEXTURE_MAME_SETUP_GRADIENTS:
  f420 0x44d3a578 direct/setup=2908/49045 framebuffer=307200/307200
  Preserves hash/coverage but raises zero texels from 30913347 to 81110463.
  Keep opt-in until the gradient path improves texture sampling.

MAME_TEXTURE_FIXED_FETCH:
  f420 0x44d3a578 direct/setup=2908/49045 framebuffer=307200/307200
  Neutral in this window. Candidate for later cleanup, not a default requirement.

TYPE3_NONFINITE_S_AS_X:
  f420 0x44d3a578 direct/setup=2908/49045 framebuffer=307200/307200
  Neutral in this window. Keep available as a probe.

SUPPRESS_IMPLAUSIBLE_BULK_DIRECT_TRIANGLES:
  f420 0x44d3a578 direct/setup=2908/49045 framebuffer=307200/307200
  Neutral in coverage/hash; lowers LFB writes only. Keep opt-in.
```

Conclusion: the failed default stack was a bad bundle, not a single mandatory
fix. None of the removed flags should return to `BRINGUP_BASELINE` now. The
next useful graphics work is texture-source/upload debugging, not broad FIFO
experiment promotion.
