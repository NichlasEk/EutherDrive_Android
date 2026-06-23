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

Continue indexed-source hydration debugging. The current evidence points at the
Gauntlet BGLoadModel source-window population that feeds the type-5 Voodoo
upload loop, not at a broad Voodoo sampler fix, a missing FIFO delivery path,
or a simple indexed-loop limit correction.

Immediate targets:

1. Inspect `TryHydrateKnownRuntimeBgLoadModelIndexedTextureSource` and
   `HydrateKnownRuntimeBgLoadModelRemainingIndexedTextureSources` for how much
   of each promoted `0x8000` indexed source window is populated after the
   initial request.
2. Trace or repair the `requestedBytes` /
   `_runtimeBgLoadModelIndexedSourcePayloadBytesOverride` path so later
   `geb -> nin -> stg` upload spans do not read zero-filled body regions when
   the real source model should contain texture payload.
3. Keep the pointer-start correction (`0xffffffff80312998` ->
   `0xffffffff803129a4`) as the current baseline. Do not clamp the upload span
   to `geb`'s nominal payload; the evidence shows the 0x10000 upload crosses
   indexed source windows by design.
4. Every candidate still needs a visual dump, not only frame hashes.

Useful trace command:

   ```sh
   EUTHERDRIVE_GAUNTDL_TRACE_TEXTURE_UPLOAD_PAYLOAD=1
   EUTHERDRIVE_GAUNTDL_TRACE_TEXTURE_UPLOAD_PAYLOAD_LIMIT=8
   ```

The old removed Voodoo experiment set remains useful as a regression matrix:

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

## 2026-06-22 Texture Payload Provenance

Added opt-in payload tracing:

```text
EUTHERDRIVE_GAUNTDL_TRACE_TEXTURE_UPLOAD_PAYLOAD=1
EUTHERDRIVE_GAUNTDL_TRACE_TEXTURE_UPLOAD_PAYLOAD_LIMIT=<n>
```

With `TRACE_TEXTURE_UPLOAD_PAYLOAD_LIMIT=8`, the ASCII-trigger captured the hot
metadata source that later appears as Voodoo texture writes:

```text
packet=7 index=7/31 packetSource=0x00060e00 source=0xffffffff802ed500 words=8 text="DWF_GEIBEARD2"
packet=10 index=10/31 packetSource=0x00061400 source=0xffffffff802ed560 words=8 text="DWF_GEIBRADE3"
packet=13 index=13/31 packetSource=0x00061a00 source=0xffffffff802ed5c0 words=8 text="DWF_GEILEFTHEEL"
packet=28 index=28/31 packetSource=0x00063800 source=0xffffffff802ed7a0 words=8 text="DWF_GEIUPPERTOR"
```

The matching texture-write bucket showed the same data being written through
the type-5 path into the hot `0x00C000` texture bucket:

```text
bucket=0x00C000 addr=0x00C000 value=0x4457465F pc=0xffffffff800fe5d4
```

This makes the next bug narrower: a `gei` indexed source/body window is feeding
metadata names into a type-5 texture-space upload. The source is approximately
`0x9de8` bytes into `0xffffffff802e3718`; the seeded header reports body offset
`0xa0d0`, so the parser/loop may be starting before the intended body window or
using the wrong cursor/limit.

Rejected experiment:

```text
EUTHERDRIVE_GAUNTDL_EXPERIMENT_SUPPRESS_KNOWN_METADATA_TEXTURE_UPLOADS=1
```

Suppressing those packets preserved f260 framebuffer coverage but was not a
real fix:

```text
f260 frameHash=0xe806de53 framebuffer=157608/157586
f420 frameHash=0x7f170d59 framebuffer=307200/307200 direct/setup=1196/577
```

The f420 PNG regressed from the green baseline with diagonal artifact to an
almost empty blue screen, and setup-triangle activity collapsed from `49045` to
`577`. Do not promote this drop/suppress approach.

## 2026-06-22 Payload Cursor Follow-Up

Negative probes after commit `b84a50a1`:

```text
EUTHERDRIVE_GAUNTDL_EXPERIMENT_RUNTIME_BGLOADMODEL_INDEXED_TEXTURE_QIO_BODY_READ=1
  Did not trigger the body-read path in the f260 window. DWF metadata uploads
  remained present and f260 stayed baseline-like:
  frameHash=0xe806de53 direct/setup=710/337 framebuffer=157608/157586

EUTHERDRIVE_GAUNTDL_EXPERIMENT_RUNTIME_BGLOADMODEL_INDEXED_SOURCE_PAYLOAD_BYTES=0x120
  Fresh warm removed the early DWF payload trace, but stalled on "Loading Game."
  with frameHash=0x89c6d1c2 and framebuffer=307200/81701. Header-only source
  hydration is not enough.

EUTHERDRIVE_GAUNTDL_EXPERIMENT_RUNTIME_BGLOADMODEL_INDEXED_SOURCE_STRIDE=0x20000
  The first run used this literal before the positive-int parser accepted hex,
  so it silently fell back to the default `0x2000` stride and was not a valid
  control.

EUTHERDRIVE_GAUNTDL_EXPERIMENT_RUNTIME_BGLOADMODEL_INDEXED_SOURCE_STRIDE=131072
  This is the real `0x20000` stride control. It moved indexed source windows to
  non-overlapping addresses:
  index=1 code=gei dest=ffffffff80301718
  index=2 code=snm dest=ffffffff80321718
  index=3 code=stk dest=ffffffff80341718

  The f220 output changed substantially:
  frameHash=0xf138aaf3 direct/setup=167/67
  framebuffer=307200/164736
  textureMap.touched=66260

  This proves the compact `0x2000` stride affects runtime state, but the large
  stride is not yet a promotable default because triangle activity collapses
  versus the f220 baseline:
  baseline f220 frameHash=0xe806de53 direct/setup=424/194
  baseline f220 framebuffer=157608/157586

  After this result the positive-int env parser was updated to accept both
  decimal and `0x` hex values, so future stride probes can use the same notation
  as the rest of the Gauntlet bring-up flags.

Stride sweep after the parser fix:

EUTHERDRIVE_GAUNTDL_EXPERIMENT_RUNTIME_BGLOADMODEL_INDEXED_SOURCE_STRIDE=0x4000
  f220 frameHash=0x5c7e44ef direct/setup=345/156
  framebuffer=179549/179530 textureMap.touched=23104
  Better than baseline coverage but still partial.

EUTHERDRIVE_GAUNTDL_EXPERIMENT_RUNTIME_BGLOADMODEL_INDEXED_SOURCE_STRIDE=0x8000
  f220 frameHash=0x21c0914a direct/setup=1834/901
  framebuffer=307200/307200 textureMap.touched=55200
  Payload trace no longer showed `DWF_` metadata packets in the f220 type-5
  upload path.

  f260 frameHash=0x21c0914a direct/setup=6761/3371
  framebuffer stayed fully covered.

  f420 frameHash=0x44d3a578 direct/setup=12520/6255
  framebuffer=307200/307200 textureMap.touched=309364
  frameDump=/tmp/gauntdl-stride-0x8000-f420.ppm

  This preserves the f420 visual hash and coverage while fixing the early
  f220/f260 partial-output window, so `0x8000` was promoted as the default
  indexed-source stride.

  Default-path verification without the stride env override matched the
  candidate:
  f260 frameHash=0x21c0914a direct/setup=6761/3371
  f420 frameHash=0x44d3a578 direct/setup=12520/6255
  framebuffer=307200/307200
  frameDump=/tmp/gauntdl-default-0x8000-f420.ppm

EUTHERDRIVE_GAUNTDL_EXPERIMENT_RUNTIME_BGLOADMODEL_INDEXED_SOURCE_STRIDE=0x10000
  f220 frameHash=0xd0412930 direct/setup=167/67
  framebuffer=31560/31560
  This reproduces the known partial-output regression. Keep it as a rejected
  control.

Additional negative control after the `0x8000` default:

EUTHERDRIVE_GAUNTDL_EXPERIMENT_SKIP_ZERO_BASE_TEXTURE_PAYLOAD_RUNS=1
  This skips type-5 payload runs whose stack source base is zero while still
  advancing the emulated caller state. It hit the repeated f220 run:
  source=0xffffffff80312998 sourceBase=0x00000000 index=0/255 words=64
  packets=256.

  Result:
  f220 frameHash=0x1c5e37c9 direct/setup=169/68
  framebuffer=300325/248699 textureMap.touched=48926

  This rejects treating those zero-base runs as disposable upload noise. They
  are suspicious because of the caller state, but the writes still feed visible
  output and should be traced upstream rather than suppressed.

Additional stride controls between the promoted `0x8000` and rejected
`0x10000`:

EUTHERDRIVE_GAUNTDL_EXPERIMENT_RUNTIME_BGLOADMODEL_INDEXED_SOURCE_STRIDE=0xa000
  f220 frameHash=0x31ae53b0 direct/setup=429/198
  framebuffer=307200/191854 textureMap.touched=58570
  Keeps full nonBlack coverage but loses too much colored coverage.

EUTHERDRIVE_GAUNTDL_EXPERIMENT_RUNTIME_BGLOADMODEL_INDEXED_SOURCE_STRIDE=0xc000
  f220 frameHash=0x74c81599 direct/setup=169/68
  framebuffer=307200/292706 textureMap.touched=71468
  Keeps near-full colored coverage but collapses triangle activity.

EUTHERDRIVE_GAUNTDL_EXPERIMENT_RUNTIME_BGLOADMODEL_INDEXED_SOURCE_STRIDE=0xe000
  f220 frameHash=0x8d14d76b direct/setup=291/130
  framebuffer=198161/197956 textureMap.touched=91316
  Partial coverage again.

Conclusion: keep `0x8000` as the best current default. Larger strides reduce
some address overlap but degrade the early frame more than they help.
```

The trace now annotates type-5 upload sources with BGLoadModel payload matches.
Baseline f260 run:

```sh
EUTHERDRIVE_GAUNTDL_BRINGUP_BASELINE=1 \
EUTHERDRIVE_GAUNTDL_WARMUP_STATE=/tmp/eutherdrive-gauntlet-probe/gauntdl-gauntdl24-fast-raw-f180-s200000-87341a65baec.warm \
EUTHERDRIVE_GAUNTDL_WARMUP_FRAMES=180 \
EUTHERDRIVE_GAUNTDL_FRAME_CHECKPOINTS=220,260 \
EUTHERDRIVE_GAUNTDL_DEBUG_VOODOO_TEXTURE_ZERO_BUCKETS=1 \
EUTHERDRIVE_GAUNTDL_TRACE_TEXTURE_UPLOAD_PAYLOAD=1 \
EUTHERDRIVE_GAUNTDL_TRACE_TEXTURE_UPLOAD_PAYLOAD_LIMIT=8 \
dotnet tools/GauntletProbe/bin/Release/net8.0/GauntletProbe.dll \
  /home/nichlas/roms/MAME/Midway/Vegas/gauntd 260 200000 0 \
  > /tmp/gauntdl-payload-provenance-f260.log 2>&1
```

Key result:

```text
packet=7 index=7/31 packetSource=0x00060e00 sourceBase=0x00060000
source=0xffffffff802ed500 words=8
bgsrc=1:gei+0x9de8(body=0xa0d0/-0x2e8 len=0xa13c hdr60=0x00000020 hdr64=0x00000016)
text="DWF_GEIBEARD2"

packet=28 index=28/31 packetSource=0x00063800 sourceBase=0x00060000
source=0xffffffff802ed7a0 words=8
bgsrc=1:gei+0xa088(body=0xa0d0/-0x48 len=0xa13c hdr60=0x00000020 hdr64=0x00000016)
text="DWF_GEIUPPERTOR"

packet=1 index=1/15 packetSource=0x00080200 sourceBase=0x00080000
source=0xffffffff802ed830 words=4
bgsrc=1:gei+0xa118(body=0xa0d0/+0x48 len=0xa13c hdr60=0x00000020 hdr64=0x00000016)
text="DWF_NAME"
```

The important correction is that "start at body offset" is too blunt: the bad
ASCII run crosses the header body boundary, and `DWF_NAME` appears just after
it. The next target is the caller/source-cursor setup for the type-5 loop,
especially who seeds `_gpr[22]` before `sourceBase=0x60000` and why that source
range is being treated as texture data.

Next concrete trace:

1. Add an opt-in trace around the caller path that enters
   `TryFastPathKnownGlideFifoOuterPayloadLoopTail`, recording `_gpr[22]`,
   `_gpr[17]`, `_gpr[18]`, `_gpr[20]`, `sp+0x1c`, `sp+0x74`, and `ra` before
   the fast path consumes the packet run.
2. Correlate the first transition into `sourceBase=0x60000` against
   BGLoadModel asset-table updates and QIO stream-limit repairs.
3. Only after the real cursor source is known, try a narrowly gated cursor
   repair. Do not reintroduce metadata suppress/drop as a fix.

## 2026-06-22 Cursor And Body-View Probe

The payload trace now emits a run header before the fast path consumes a
type-5 packet run. The DWF upload is not an isolated single packet; it appears
inside a longer run entered from the same caller:

```text
[GAUNTDL:TEXUPLOAD-RUN] pc=0xffffffff800fe5d4 ra=0xffffffff800fe338
source=0xffffffff802ed420
bgsrc=1:gei+0x9d08(body=0xa0d0/-0x3c8 len=0xa13c hdr60=0x00000020 hdr64=0x00000016)
sourceBase=0x00060000/sp1c=0x00060000 packet=0x00060000
index=0/31/sp74=31 words=8
s1=0x0000000000060000 s2=0x0000000000000000
s4=0x0000000000000008 s6=0xffffffff802ed420
```

The same run later reaches the known bad metadata packets:

```text
packet=7 index=7/31 packetSource=0x00060e00
source=0xffffffff802ed500 text="DWF_GEIBEARD2"
```

This shifts the investigation from a single text packet to the caller/cursor
that selects `source=0xffffffff802ed420` for a `sourceBase=0x60000`,
`words=8`, `limit=31` upload run.

Rejected experiment:

```text
EUTHERDRIVE_GAUNTDL_EXPERIMENT_RUNTIME_BGLOADMODEL_INDEXED_SOURCE_PAYLOAD_FROM_BODY=1
```

This hydrates indexed source windows from the payload header body offset rather
than from the start of the file. It removes the early DWF payload trace, but it
is not a fix:

```text
f260 frameHash=0x378e1d3a direct/setup=235/101
framebuffer=307200/307199
textureMap.touched=492288
```

The run also starts producing broad `sourceBase=0x00000000` and
`sourceBase=0x00200000` type-5 uploads with `words=64`, which is a clear
state/cursor regression compared with the baseline f260:

```text
f260 frameHash=0xe806de53 direct/setup=710/337
framebuffer=157608/157586
```

Keep `INDEXED_SOURCE_PAYLOAD_FROM_BODY` as an opt-in diagnostic only. The next
useful target is narrower: trace or repair the preparation path around
`ra=0xffffffff800fe338` / `pc=0xffffffff800fe5d4` so the source cursor for the
`0x60000` packet run is understood before changing payload layout again.

Additional control:

```text
EUTHERDRIVE_GAUNTDL_EXPERIMENT_DISABLE_OUTER_PAYLOAD_FASTPATH=1
```

Result at f220:

```text
frameHash=0x2b9ec036 direct/setup=305/135
framebuffer=258395/258144
textureMap.touched=33984
```

Baseline f220 remains:

```text
frameHash=0xe806de53 direct/setup=424/194
framebuffer=157608/157586
```

So the outer-payload fast path changes output, but disabling it is not a
visual or behavioral fix. Keep this as a control only. The active line of work
is still to understand why the caller at `ra=0xffffffff800fe338` supplies the
`0x60000` run source cursor that crosses the `gei` DWF metadata region.

## 2026-06-22 Caller Prep Trace And Metadata-Skip Control

Added `TEXUPLOAD-CALLER`, gated by
`EUTHERDRIVE_GAUNTDL_TRACE_TEXTURE_UPLOAD_PAYLOAD=1`, to record the caller
prep window around `0x800fe2f0..0x800fe5d4`. The f220 run stayed on the
baseline image:

```text
frameHash=0xe806de53 direct/setup=424/194
framebuffer=157608/157586
```

The trace confirms the upload loop is prepared by normal caller state, not by
an isolated bad fast-path packet. In the first pass, `s6` starts at the
BGLoadModel destination base and `sp+0x1c` is later written by the caller:

```text
pc=0xffffffff800fe2f0 s6=0xffffffff802e1718 sp1c=8024f9d0 sp74=0000000f
pc=0xffffffff800fe384 op=0xafa8001c ... sp1c=00000000
pc=0xffffffff800fe388 ... sp1c=00020000
```

The run consumed by the fast path then uses the stack-fed packet base and the
current source cursor:

```text
[GAUNTDL:TEXUPLOAD-RUN] pc=0xffffffff800fe5d4 ra=0xffffffff800fe338
source=0xffffffff802ed420 bgsrc=1:gei+0x9d08(body=0xa0d0/-0x3c8 ...)
sourceBase=0x00060000/sp1c=0x00060000 packet=0x00060000
index=0/31/sp74=31 words=8
```

Added an opt-in negative control:

```text
EUTHERDRIVE_GAUNTDL_EXPERIMENT_SKIP_METADATA_TEXTURE_PAYLOADS=1
```

This skips obvious ASCII metadata payloads like `DWF_GEIBEARD2`,
`DWF_GEIUPPERTOR`, and `DWF_NAME` while still advancing the source cursor and
loop index. It is not a fix:

```text
skip-metadata f220 frameHash=0xe806de53 direct/setup=422/193
framebuffer=157608/157586
textureMap.writes=3256648 touched=34112
```

Conclusion: the visible failure is not solved by suppressing the ASCII DWF
packets after they reach type-5 upload. Larger indexed-source spacing also
changes real runtime state, so the next useful target is upstream but narrower:
trace the BGLoadModel indexed source construction and asset-table selection
until we can identify the smallest source-window layout change that preserves
triangle activity while avoiding the bad metadata-as-texture upload path.

## 2026-06-22 Upload Source Header Marker

Added a trace-only `hdr=ok|bad` marker to `bgsrc=` entries emitted by
`DescribeKnownRuntimeBgLoadModelUploadSource()`. The marker classifies the
candidate BGLoadModel upload header fields at `+0x5c/+0x60/+0x64`, so
overlapping source windows can be separated without changing runtime behavior.

Verified command shape:

```sh
EUTHERDRIVE_GAUNTDL_BRINGUP_BASELINE=1 \
EUTHERDRIVE_GAUNTDL_WARMUP_STATE=/tmp/eutherdrive-gauntlet-probe/gauntdl-gauntdl24-fast-raw-f180-s200000-87341a65baec.warm \
EUTHERDRIVE_GAUNTDL_WARMUP_FRAMES=180 \
EUTHERDRIVE_GAUNTDL_FRAME_CHECKPOINTS=220 \
EUTHERDRIVE_GAUNTDL_TRACE_TEXTURE_UPLOAD_PAYLOAD=1 \
EUTHERDRIVE_GAUNTDL_TRACE_TEXTURE_UPLOAD_PAYLOAD_LIMIT=8 \
dotnet tools/GauntletProbe/bin/Release/net8.0/GauntletProbe.dll \
  /home/nichlas/roms/MAME/Midway/Vegas/gauntd 220 200000 0
```

Key f220 trace:

```text
source=0xffffffff80312998
bgsrc=5:pnk+0x9280(body=0x3d682f9a/... hdr60=0xbd252696 hdr64=0x44000000 hdr=bad),
      6:geb+0x1280(body=0xb330/... hdr60=0x0000001f hdr64=0x00000017 hdr=ok)
sourceBase=0x00000000/sp1c=0x00000000 index=0/255 words=64
```

Verification stayed on the promoted `0x8000` stride result:

```text
f220 frameHash=0x21c0914a direct/setup=1834/901
framebuffer=307200/307200
textureMap.touched=55200
```

Conclusion: the repeated zero-base upload source overlaps both `pnk` and
`geb`, but only the `geb` candidate has a plausible upload header. This keeps
the next target upstream of the type-5 write loop: trace why the caller enters
the zero-base run with `s6=0xffffffff80312998` and `sp1c=0`, rather than
dropping the run after it is already prepared.

## 2026-06-22 Focused Zero-Base Caller Trace

The caller trace cap was extended with a narrow bypass for the pnk/geb
zero-base case: after the general 256-line caller cap, keep logging only when
`s6 >= 0xffffffff80300000`, the address is inside a known BGLoadModel upload
source window, and `sp+0x1c == 0`.

The focused f220 run captured the missing caller prep immediately before the
zero-base upload run:

```text
pc=0xffffffff800fe350 s3=0xffffffff80312998 s6=0xffffffff80312998
s4=0x40 s5=0x100 sp1c=00000000 sp74=000000ff
bgsrc=5:pnk(... hdr=bad),6:geb(... hdr=ok)
```

By `pc=0xffffffff800fe584`, the same caller state is still live:

```text
s6=0xffffffff80312998 sp1c=00000000 sp74=000000ff
```

The resulting run is the repeated zero-base upload:

```text
source=0xffffffff80312998 sourceBase=0x00000000 index=0/255 words=64
```

Verification stayed unchanged:

```text
f220 frameHash=0x21c0914a direct/setup=1834/901
framebuffer=307200/307200
```

Conclusion: the bad-looking zero-base packet setup is already present at the
start of the caller prep window, not introduced by the type-5 fast path. The
next useful target is one level earlier: trace the BGLoadModel asset/record
selection that leaves `s6=0xffffffff80312998`, `sp1c=0`, and `sp74=0xff` for
the pnk/geb candidate.

## 2026-06-22 Asset Parser Control

Ran the focused pnk/geb caller trace together with the existing asset-parser
trace:

```text
EUTHERDRIVE_GAUNTDL_TRACE_BGLOADMODEL_ASSET_PARSER=1
```

The run confirms the indexed `geb` payload is seeded as a normal BGLoadModel
source window before the zero-base upload:

```text
bgloadmodel-distinct-source-indexed-header index=6 code=geb
dest=ffffffff80311718 bytes=0000b130
sourceWords=... 5c=0000b330,60=0000001f,64=00000017
```

Later caller prep still enters the upload path with the offset source:

```text
pc=0xffffffff800fe350 s3=0xffffffff80312998 s6=0xffffffff80312998
sp1c=00000000 sp74=000000ff
bgsrc=5:pnk(... hdr=bad),6:geb(... hdr=ok)
```

The visual checkpoint remains unchanged:

```text
f220 frameHash=0x21c0914a direct/setup=1834/901
framebuffer=307200/307200
```

Conclusion: the existing asset-parser trace proves `geb` is hydrated, but it
does not yet expose the exact source-table or record transition that turns the
`geb` base `0xffffffff80311718` into caller source `0xffffffff80312998` with a
zero packet base. Next trace should focus on the source-table record slot
selected immediately after the `index=6 code=geb` hydration and before the
`0x800fe350` caller prep.

## 2026-06-22 Focused Geb Source Slot Trace

Added a narrow `bgloadmodel-geb-source` trace gated by
`EUTHERDRIVE_GAUNTDL_TRACE_BGLOADMODEL_ASSET_PARSER=1`. It snapshots source
table slot 6, side table slot 6, the hydrated `geb` header window, and the
registers at the asset-parser handoff points.

The focused f220 run confirms hydration and slot rewrite:

```text
bgloadmodel-distinct-source-indexed-header index=6 code=geb
dest=ffffffff80311718 bytes=0000b130
sourceWords=... 5c=0000b330,60=0000001f,64=00000017

bgloadmodel-geb-source phase=after-indexed-header
slot=ffffffff802529b8:802e1718
header=ffffffff80311718 bodyOffset=0000b330 body=ffffffff8031ca48

bgloadmodel-distinct-source pc=ffffffff800aae98 index=6
slot=ffffffff802529b8:802e1718->80311718
```

The later caller still enters the texture upload loop with:

```text
source=0xffffffff80312998
bgsrc=6:geb+0x1280(body=0xb330/-0xa0b0 ... hdr=ok)
sourceBase=0x00000000/sp1c=0x00000000
index=0/255 words=64
```

Verification stayed unchanged:

```text
f220 frameHash=0x21c0914a direct/setup=1834/901
framebuffer=307200/307200
```

Conclusion: `0xffffffff80312998` is not the hydrated `geb` body start; it is
`geb+0x1280`, while the header-reported body offset is `0xb330`. The source
table repair and header hydration are therefore doing their current job. The
next target is the later caller source/packet-base setup that converts the
selected `geb` source window into `s6=80312998`, `sp1c=0`, and `sp74=0xff`.

## 2026-06-22 Type-5 Zero-Base Target Control

Ran the f220 repro with:

```text
EUTHERDRIVE_GAUNTDL_TRACE_VOODOO_TYPE5_PAYLOADS=1
EUTHERDRIVE_GAUNTDL_TRACE_TEXTURE_UPLOAD_PAYLOAD=1
```

The zero packet base is decoded as a normal texture-space type-5 stream:

```text
cmd=0xc0000205 space=3 targetWord=0x00000000 count=64
cmd=0xc0000205 space=3 targetWord=0x00000080 count=64
cmd=0xc0000205 space=3 targetWord=0x00000100 count=64
...
source=0xffffffff80312998 sourceBase=0x00000000/sp1c=0x00000000
index=0/255 words=64
```

Verification stayed unchanged:

```text
f220 frameHash=0x21c0914a direct/setup=1834/901
framebuffer=307200/307200
```

Conclusion: `sp1c=0` is likely the intended texture target base for this
upload batch, not by itself a bad pointer. The remaining suspicious part is the
source cursor choice (`geb+0x1280`) and overlap with the bad `pnk` window; next
work should compare the selected source cursor against the hydrated `geb`
record/header fields rather than forcing a non-zero packet base.

## 2026-06-22 Indexed Upload Limit Clamp Control

Added an opt-in clamp experiment:

```text
EUTHERDRIVE_GAUNTDL_EXPERIMENT_CLAMP_INDEXED_TEXTURE_UPLOAD_LIMIT=1
```

The experiment only applies when `sourceBase/sp1c == 0` and the upload source
matches a plausible BGLoadModel header. For the repeated `geb` case it clamps
the caller limit from `0xff` to the header count word:

```text
clamp-indexed-texture-upload-limit
source=0xffffffff80312998 code=geb header=0xffffffff80311718
sourceOffset=0x1280 limit=255->31 stride=00000017
bytes=10000->2000 len=b130
```

This is not a fix. It sharply reduces render output:

```text
clamp f220 frameHash=0x3f3146a7 direct/setup=197/81
framebuffer=269450/205980
textureMap.touched=48928
```

Control remains:

```text
default f220 frameHash=0x21c0914a direct/setup=1834/901
framebuffer=307200/307200
textureMap.touched=55200
```

Conclusion: even though the `geb` header count is plausible, it is not the
runtime upload loop limit for this caller path. The `0xff` batch is doing real
render work and clamping it is destructive. Keep this experiment as a negative
control only; the next target should move away from simple limit correction and
look at how the source window should be populated beyond `geb`'s nominal disk
payload length.

## 2026-06-22 Bucket C/D/E Control On Current Baseline

Re-ran the older current-target bucket trace on the promoted `0x8000` stride
baseline:

```text
EUTHERDRIVE_GAUNTDL_TRACE_VOODOO_TEXTURE_WRITE_BUCKETS=c,d,e
EUTHERDRIVE_GAUNTDL_TRACE_TEXTURE_UPLOAD_PAYLOAD=1
```

The trace shows the current default writes real texture data into bucket
`0x00c000` from the type-5 service at `0xffffffff800fe5d4`:

```text
bucket=0x00C000 word=0x018000 addr=0x00C000 value=0xFDFF0000
mode=0x00000100 tlod=0x00000808 tbase=0x1FFFEE00 pc=0xffffffff800fe5d4
```

The repeated `geb+0x1280` upload remains present:

```text
source=0xffffffff80312998 sourceBase=0x00000000 index=0/255 words=64
```

Verification stays on the current baseline:

```text
f220 frameHash=0x21c0914a direct/setup=1834/901
framebuffer=307200/307200
textureMap.touched=55200
```

Conclusion: the bucket trace no longer points at a missing broad texture write
path for f220. The texture upload is active and productive; next work should
target correctness of the source bytes/window composition rather than command
FIFO delivery or packet-base mechanics.

## 2026-06-22 Zero-Base Upload Span Composition

Added `[GAUNTDL:TEXUPLOAD-SPAN]`, gated by the existing
`EUTHERDRIVE_GAUNTDL_TRACE_TEXTURE_UPLOAD_PAYLOAD=1` trace, to summarize
zero-base indexed upload source spans across known BGLoadModel source windows.

The focused f220 trace shows the repeated suspicious upload is a real
cross-window copy:

```text
source=0xffffffff80312998 bytes=0x10000 packets=256 words=64 index=0/255
segments=5:pnk@0x9280|6:geb@0x1280+0x0..+0xc00,
         6:geb@0x1e80+0xc00..+0x6d80,
         6:geb@0x8000|7:nin@0x0+0x6d80..+0x9eb0,
         7:nin@0x3130+0x9eb0..+0xed80,
         7:nin@0x8000|8:stg@0x0+0xed80..+0x10000
```

The corresponding run still reports the overlapping source descriptions:

```text
bgsrc=5:pnk+0x9280(... hdr=bad),6:geb+0x1280(... hdr=ok)
sourceBase=0x00000000/sp1c=0x00000000 index=0/255 words=64
```

Verification remains unchanged on baseline:

```text
f220 frameHash=0x21c0914a direct/setup=1834/901
```

Conclusion: the `0xff` limit is not simply wrong; the current default makes this
upload span intentionally cross the promoted `0x8000` indexed-source windows.
The next fix candidate should target the source-window population/overlap
semantics around `pnk`/`geb`/`nin`/`stg`, not the Voodoo FIFO path, zero-base
packet address, or header count clamp.

## 2026-06-22 Zero-Base Span Disk-Word Control

Extended the span trace to include the first RAM word at each segment and the
first disk word for every overlapping candidate, without writing disk data into
RAM.

Focused f220 result:

```text
segments=5:pnk@0x9280=bc754d4b|6:geb@0x1280=07f00c05;mem=8012e528+0x0..+0xc00,
         6:geb@0x1e80=00000000;mem=00000000+0xc00..+0x6d80,
         6:geb@0x8000=3dfe8d84|7:nin@0x0=00000000;mem=3dfe8d84+0x6d80..+0x9eb0,
         7:nin@0x3130=43490000;mem=00000000+0x9eb0..+0xed80,
         7:nin@0x8000=be276d7c|8:stg@0x0=00000000;mem=00000000+0xed80..+0x10000
```

Verification stayed stable:

```text
f220 frameHash=0x21c0914a direct/setup=1834/901
```

Conclusion: source-window overlap is not behaving like a simple final disk
payload copy. The start of the repeated upload contains neither the `pnk` nor
`geb` disk word, the `geb|nin` overlap preserves the `geb` word, and later
`nin`/`stg` regions are zero where candidate disk words can be nonzero. The next
candidate should trace or repair who writes those source bytes after hydration,
especially around `0xffffffff80312998`, before trying another stride or upload
limit experiment.

## 2026-06-22 Zero-Base Source Writer Trace

Used the existing filtered memory trace:

```text
EUTHERDRIVE_GAUNTDL_TRACE_MEM=1
EUTHERDRIVE_GAUNTDL_TRACE_MEM_WRITES_ONLY=1
EUTHERDRIVE_GAUNTDL_TRACE_MEM_ADDRESS=0xffffffff80312998:256
```

The first upload word is overwritten by runtime writes, not by the disk
hydration path:

```text
pc=ffffffff8004c850 write32 ffffffff80312998 8012e588
...
pc=ffffffff8004c850 write32 ffffffff80312998 8012e528
pc=ffffffff8004c858 write32 ffffffff803129a0 803129a4
```

Other nearby writes also treat this range as live runtime structure data:

```text
pc=ffffffff800a7710 write32 ffffffff803129d0 000157e8
pc=ffffffff800a773c write32 ffffffff803129c0 00002000
pc=ffffffff800a7768 write32 ffffffff803129c0 00000afd
pc=ffffffff800a780c write32 ffffffff803129c4 00000bcf
```

Verification stayed stable:

```text
f220 frameHash=0x21c0914a direct/setup=1834/901
```

Conclusion: the promoted source-window addresses collide with runtime-owned
state by the time the zero-base texture upload consumes them. A broad stride
move was already a negative control, so the next narrow experiment should happen
at upload read time: for known zero-base indexed texture uploads, substitute
candidate disk words for source bytes without changing the persistent runtime
RAM layout.

Added an opt-in upload-read control:

```text
EUTHERDRIVE_GAUNTDL_EXPERIMENT_ZERO_BASE_UPLOAD_DISK_WORDS=1
```

At f220 it did substitute candidate disk words from the overlapping known source
windows:

```text
zero-base-upload-disk-word addr=0xffffffff80312998 6:geb@0x1280 mem=0x8012e528->disk=0x07f00c05
zero-base-upload-disk-word addr=0xffffffff803129d4 5:pnk@0x92bc mem=0x00000000->disk=0x0000fffd
```

But the rendered checkpoint stayed on the same hash and lost some draw setup
activity:

```text
f220 frameHash=0x21c0914a direct/setup=1685/827 framebuffer=307200/307200
```

Conclusion: direct disk-word substitution is another negative control. The
failure is likely not that the FIFO upload should simply read the original asset
bytes from the overlapping hydrated source windows; the remaining suspicion is
that the runtime structures feeding the zero-base repeated upload are being
assembled with the wrong pointers, extents, or sequence before the fast FIFO
copy sees them.

## 2026-06-22 Zero-Base Caller State Words

Extended the existing `TEXUPLOAD-CALLER` trace with temporary registers and
state/stack words used by the caller setup path. The f220 trace shows that
`sp+0x1c` is not randomly corrupted: the caller writes the current Glide state
base word from `state+0x08`, and that word is zero in this mode.

Normal earlier passage:

```text
pc=0xffffffff800fe344 op=0x8e080008 t0=0 state08=00000000 sp1c=8024f9d0
pc=0xffffffff800fe34c op=0xafa8001c t0=0 state08=00000000 sp1c=8024f9d0
pc=0xffffffff800fe350 ... sp1c=00000000 sp74=0000001f
```

Repeated pnk/geb passage:

```text
pc=0xffffffff800fe418 op=0xac5e0000 s6=0xffffffff80312998 state374=a8235a5c state37c=0000ffe4 sp1c=00000000 sp74=000000ff
pc=0xffffffff800fe448 op=0x8fa80074 t0=0xff state374=a8235a6c state37c=0000ffd4 sp1c=00000000 sp74=000000ff
pc=0xffffffff800fe5d4 source=0xffffffff80312998 sourceBase=0x00000000 packet=0x00000000 index=0/255 words=64
```

Verification stayed stable:

```text
f220 frameHash=0x21c0914a direct/setup=1834/901
```

Conclusion: zero `sourceBase` is probably intended Glide texture-base state,
not the root cause by itself. The next useful target is lower in the Voodoo
path: verify whether the command FIFO consumer interprets these repeated
zero-base uploads with the right texture address, format, and memory layout.

## 2026-06-22 Focused Voodoo Type5 Zero-Target Trace

Extended the Voodoo Type5 payload trace so it keeps logging focused texture
writes after the broad trace cap when the packet is:

```text
space=3 targetWord=0 count=64
```

The f220 repro now captures the late zero-base sequence that previously landed
after the general Type5 trace limit. Normal earlier packets still look like
small texture header/data uploads:

```text
[GAUNTDL:VOODOO-TYPE5-FOCUS] pc=0xffffffff800fe614 targetWord=0 count=64 nz=40 first=0x02000000/dec=0x00000002
```

The suspected repeated pnk/geb upload is different. It reaches the Voodoo Type5
consumer as a texture-space write to word zero, but the first payload word is the
runtime pointer-like word already seen in main RAM:

```text
[GAUNTDL:TEXUPLOAD-RUN] pc=0xffffffff800fe5d4 source=0xffffffff80312998 sourceBase=0x00000000 packet=0x00000000 index=0/255 words=64
[GAUNTDL:VOODOO-TYPE5-FOCUS] pc=0xffffffff800fe5d4 space=3 targetWord=0x00000000 count=64 nz=49 first=0x8012e528/dec=0x28e51280 second=0x07f3fc00/dec=0x00fcf307 last=0x00982fc5/dec=0xc52f9800 depth=16896
```

Verification stayed stable:

```text
f220 frameHash=0x21c0914a direct/setup=1834/901
```

Conclusion: the command FIFO consumer is not mis-routing these packets; it is
faithfully writing the repeated zero-target texture packets into TMU texture
memory. The bad-looking first word is already in the source payload before
Voodoo consumes it. The next target should move upward again: identify why the
upload source window at `0xffffffff80312998` contains a runtime pointer and
mixed control-looking words when the BG source overlap says it is near the known
`geb` indexed texture body.

## 2026-06-22 Zero-Base Upload Pointer Probe

Added a focused `TEXUPLOAD-PTR` trace for zero-base BGLoadModel upload windows.
It only reports aligned KSEG-like words that also resolve to main RAM, keeping
the trace centered on plausible runtime pointers rather than low-value texture
data.

The f220 repro shows two stable pointer-looking words at the start of the
suspected `0xffffffff80312998` upload window:

```text
[GAUNTDL:TEXUPLOAD-PTR] source=0xffffffff80312998 packet=0 index=0/255 word=0 ptr=0xffffffff8012e528 ptrWords=656d616e/746e6f66/00000000/726f6373
[GAUNTDL:TEXUPLOAD-PTR] source=0xffffffff803129a0 packet=0 index=0/255 word=2 ptr=0xffffffff803129a4 ptrWords=07e3fc01/07fffdfc/07ec0c0a/07fc0c03
```

`0x8012e528` decodes as ASCII-looking metadata (`name`, `font`, then `scor` in
little-endian word view), not indexed texture payload. The second pointer points
back into the same upload window, where the words match the color/index-looking
run that Voodoo later receives after endian correction.

The matching Type5 consumer trace still shows the first payload word being sent
to texture word zero:

```text
[GAUNTDL:VOODOO-TYPE5-FOCUS] pc=0xffffffff800fe5d4 space=3 targetWord=0x00000000 count=64 first=0x8012e528/dec=0x28e51280
```

Verification stayed stable:

```text
f220 frameHash=0x21c0914a direct/setup=1834/901
```

Conclusion: this is unlikely to be a simple dereference fix. Word 2 points at
plausible payload bytes, while word 0 points at unrelated text/metadata. The
next narrow target is the source-start calculation or descriptor interpretation
for this upload run: why the fastpath starts at the descriptor/pointer pair
instead of the indexed data at or after `0xffffffff803129a4`.

## 2026-06-22 Zero-Base Pointer-Start Fix

Promoted the narrow source-start correction for known zero-base BGLoadModel
texture upload windows. When the upload source is a known runtime BGLoadModel
candidate, `sourceBase == 0`, and word 2 points exactly at `source+0x0c`, the
fastpath now starts the payload at that pointed-to data word instead of sending
the descriptor/pointer trio as texture data.

The focused f220 repro changed from sending a metadata pointer as the first
Type5 texture payload word:

```text
old first=0x8012e528/dec=0x28e51280
```

to sending the plausible indexed payload data:

```text
[GAUNTDL:TEXUPLOAD-PTRSTART] source=0xffffffff80312998->ffffffff803129a4 first=8012e528/07f3fc00/803129a4/07e3fc01
[GAUNTDL:VOODOO-TYPE5-FOCUS] first=0x07e3fc01/dec=0x01fce307 second=0x07fffdfc/dec=0xfcfdff07
```

Verification:

```text
f220 default path frameHash=0x3a5175a3 direct/setup=1851/906
f420 default path frameHash=0x44d3a578 direct/setup=12514/6237
f420 framebuffer=307200/307200 textureMap.touched=327796
visual dump=/tmp/gauntdl-pointer-start-default-f420.png
```

This preserves the current f420 full-frame green baseline and fixes the hot
zero-base upload's descriptor-as-texture-data error. It is still not final game
graphics; the next target is why the f420 scene remains a flat green frame with
the diagonal artifact despite the corrected Type5 payload start.

## 2026-06-22 Bulk-End FIFO Follow-Up

After the pointer-start fix, f220/f420 still land on the same green full-frame
surface with a diagonal artifact. Fastfill/swap profiling shows the dominant
fill path is not a normal clear-only sequence:

```text
ffpc=0xffffffff800fe5d4:859/w12/k270/o577/s664
swpc=0xffffffff800fe5d4:656/c111/d0
```

The suspicious fills are emitted while decoding command FIFO at `bulk-end`.
The focused bulk trace shows several bulk writes begin with a valid Type5
header, but the read pointer is outside the just-written bulk window and points
at stale/float-looking data:

```text
bulk=0x0003aa1c-0x0000b218 words=16896 inside=1 start=0xc0000205
stop reason=invalid-standard-window cmd=0xbed49fb1 type=1 words=48853 rd=0x00000008 next=0x3f371306/0x3e29fd26

bulk=0x0000ab4c-0x0001b348 words=16896 inside=0 word=0xbfa59869 start=0xc0000205
bulk=0x0001bf38-0x0002c734 words=16896 inside=0 word=0xbfa59869 start=0xc0000205
```

Negative controls:

```text
EUTHERDRIVE_GAUNTDL_EXPERIMENT_VOODOO_STOP_IMPLAUSIBLE_REGISTER_PACKETS=1
  f220 frameHash=0x224aafbc direct/setup=223/92 framebuffer=31673/31673

EUTHERDRIVE_GAUNTDL_EXPERIMENT_VOODOO_DROP_IMPLAUSIBLE_REGISTER_PACKETS=1
  f220 frameHash=0x224aafbc direct/setup=223/92 framebuffer=31673/31673

EUTHERDRIVE_GAUNTDL_EXPERIMENT_VOODOO_FIFO_BULK_SKIP_OUTSIDE_READ=1
  Local opt-in guard tested and removed. It was neutral:
  f220 frameHash=0x3a5175a3 direct/setup=1851/906 framebuffer=306327/306319
```

Conclusion: do not promote broad implausible-packet stop/drop. They remove the
green fill symptom but also remove most scene work. The next useful target is
not suppressing bad packets, but correcting command FIFO read/depth/window state
so `bulk-end` does not repeatedly decode stale low offsets like `rd=0x8` after
a Type5 bulk upload.

MAME command-FIFO controls were also re-tested from the same f180 warm snapshot:

```text
EUTHERDRIVE_GAUNTDL_FIX_VOODOO_MAME_CMD_FIFO_MODEL=1
  f220 frameHash=0x365c1baf direct/setup=301/134 framebuffer=307200/307200
  texWrites=60125 texture mapped writes=0

MAME_CMD_FIFO_MODEL + REQUIRE_VALID_PACKET_WINDOW + READY_VALID_PACKET_WINDOW
  f220 frameHash=0x365c1baf direct/setup=301/134 framebuffer=307200/307200

MAME_CMD_FIFO_MODEL + REQUIRE_READ_IN_ADDRESS_WINDOW + ACCUMULATE_ADDRESS_WINDOW
  f220 frameHash=0x365c1baf direct/setup=301/134 framebuffer=307200/307200

MAME_CMD_FIFO_MODEL + WRAP_READ_TO_WINDOW + REQUIRE_PACKET_IN_ADDRESS_WINDOW
  f220 frameHash=0xe0d35bbf direct/setup=166/67 framebuffer=300325/145279
```

Additional non-MAME self-register guard control:

```text
EUTHERDRIVE_GAUNTDL_EXPERIMENT_VOODOO_IGNORE_CMD_FIFO_SELF_REGISTER_WRITES=1
  Local opt-in guard tested and removed. It did not emit any
  "ignore cmdfifo self-register" events and was neutral:
  f220 frameHash=0x3a5175a3 direct/setup=1851/906 framebuffer=306327/306319
  cmdstop=invalid-standard-window/0x3C0D2F2D/.../rd=0x2F0
```

Conclusion: the observed command-FIFO register pollution is not fixed by
ignoring decoded FIFO self-register writes at `WriteCmdFifoRegister`; that path
does not appear to receive the offending writes in this run. The next narrower
trace should capture Type1 packets whose register range targets
`cmdFifoBaseAddr`, `cmdFifoRdPtr`, `cmdFifoAMin/AMax`, `cmdFifoDepth`, or
`cmdFifoHoles`, including packet start/read index and caller PC, so legitimate
FIFO control packets can be separated from stale float-data packets.

That trace was added as:

```text
EUTHERDRIVE_GAUNTDL_TRACE_VOODOO_CMD_FIFO_SELF_REG_PACKETS=1
```

The trace shows the Type1 self-register-looking writes are downstream of stale
reads, not the first cause. The first focused f220 sequence starts with repeated
invalid Type5 stops at `rd=0x13404`:

```text
stop reason=invalid-standard-window cmd=0xbc292a85 type=5 words=337234
  rd=0x00013404 depth=22948.. valid=1.. pc=0xffffffff800c4e5c
```

Only after later bulk-end stale reads do the float-looking Type1 packets target
the command-FIFO control register range:

```text
CMDFIFO-SELFREG cmd=0xbed49fb1 target=0x3f6 count=48852
  packetStart=0x00000008 trigger=bulk-end values=0x3f371306/0x3e29fd26/...

CMDFIFO-SELFREG cmd=0xbfa59869 target=0x30d count=49061
  packetStart=0x00000008 trigger=bulk-end values=0x3dc1bf80/0x42080000/...
```

This makes the next useful experiment narrower than the previous broad
implausible-packet controls: drop only implausible Type5 headers so the read
pointer is not pinned on the first stale huge texture packet.

```text
EUTHERDRIVE_GAUNTDL_EXPERIMENT_VOODOO_DROP_IMPLAUSIBLE_TYPE5_PACKETS=1
  f220 frameHash=0x3a5175a3 direct/setup=1986/973 framebuffer=306327/306319
  default f220 was direct/setup=1851/906 at the same hash/framebuffer.

  f420 frameHash=0x44d3a578 direct/setup=13019/6488 framebuffer=307200/307200
  default f420 was direct/setup=12514/6237 at the same hash/framebuffer.
  cmdstop moved to invalid-standard-window/0x00012609/2 at rd=0x2dfb0.
```

Conclusion: the Type5-only drop is not a final graphics fix because the visible
frame hash remains unchanged, but it is a useful default-off control. It proves
the earliest absurd Type5 stale read is pinning command-FIFO progress and that
advancing past it exposes the next, smaller invalid-window problem without
collapsing the scene like the broad implausible-packet stop/drop controls did.

Visual dump follow-up:

```text
Default f420:
  EUTHERDRIVE_GAUNTDL_DUMP_FRAME=/tmp/gauntdl-default-f420.ppm
  EUTHERDRIVE_GAUNTDL_DUMP_VOODOO_BUFFERS_PREFIX=/tmp/gauntdl-default-f420
  frameHash=0x44d3a578 direct/setup=12514/6237

Type5-only f420:
  EUTHERDRIVE_GAUNTDL_EXPERIMENT_VOODOO_DROP_IMPLAUSIBLE_TYPE5_PACKETS=1
  EUTHERDRIVE_GAUNTDL_DUMP_FRAME=/tmp/gauntdl-type5-f420.ppm
  EUTHERDRIVE_GAUNTDL_DUMP_VOODOO_BUFFERS_PREFIX=/tmp/gauntdl-type5-f420
  frameHash=0x44d3a578 direct/setup=13019/6488
```

The PPM hashes are byte-identical for the final frame and all three dumped
Voodoo color buffers:

```text
final frame sha256=f13e47a9dbf3c7a0eecd0fb311fc895e1290187acef3859f6772fab4c7bd8171
buf0 sha256=4becbb4364725216079d02693ce542f10f1988faf735151f010bf9ad82b2940f
buf1 sha256=ec0d75f3c4d9478eb5a1e9676af1ce873cf3876f660b077bdb72ed0254fed530
buf2 sha256=6cd3891d427cd38a782780d13b16488058ab73bcf84bf6c339b671631d07e5b1
```

Conclusion: the extra Type5-only draw/setup accounting is not visible and does
not alter any dumped color buffer. Treat this as diagnostic only; future fixes
should target the data feeding the visible buffers, not promote Type5 packet
dropping on the basis of higher counters.

Negative follow-up:

```text
EUTHERDRIVE_GAUNTDL_EXPERIMENT_VOODOO_DROP_IMPLAUSIBLE_TYPE5_PACKETS=1
EUTHERDRIVE_GAUNTDL_EXPERIMENT_VOODOO_DROP_INVALID_STANDARD_TYPE1_PACKETS=1
  Local opt-in guard tested and removed.
  f220 frameHash=0x854372de direct/setup=477/218 framebuffer=213937/193910
```

Conclusion: do not drop the smaller invalid Type1 standard-window packet. It
changes the frame hash, but by collapsing most triangle work and corrupting the
framebuffer path rather than advancing toward correct graphics.

Another negative combo:

```text
EUTHERDRIVE_GAUNTDL_EXPERIMENT_VOODOO_DROP_IMPLAUSIBLE_TYPE5_PACKETS=1
EUTHERDRIVE_GAUNTDL_EXPERIMENT_VOODOO_FIFO_BULK_RESYNC_LOW_READ=1
  f220 frameHash=0xf66139ba direct/setup=358/159 framebuffer=281671/229273
```

Conclusion: the old low-read bulk resync remains a collapse even after the
pointer-start fix and Type5-only drop. It should stay opt-in and is not a path
to promote.

These are not promotion candidates. The current non-MAME default still carries
more render work (`1851/906`) and preserves the corrected Type5 texture upload
path. Keep MAME-FIFO work as a separate model repair, not as a quick preset
toggle for the current graphics bring-up.

## 2026-06-23 Type5 Parity and Upload Span Recheck

After proving the Type5-only stale-packet drop changes counters but not pixels,
reran the texture-upload span trace on the current default f420 path:

```sh
EUTHERDRIVE_GAUNTDL_BRINGUP_BASELINE=1
EUTHERDRIVE_GAUNTDL_WARMUP_STATE=/tmp/eutherdrive-gauntlet-probe/gauntdl-gauntdl24-fast-raw-f180-s200000-87341a65baec.warm
EUTHERDRIVE_GAUNTDL_WARMUP_FRAMES=180
EUTHERDRIVE_GAUNTDL_FRAME_CHECKPOINTS=220,260,420
EUTHERDRIVE_GAUNTDL_TRACE_TEXTURE_UPLOAD_PAYLOAD=1
EUTHERDRIVE_GAUNTDL_TRACE_TEXTURE_UPLOAD_PAYLOAD_LIMIT=24
```

Verification stayed on the current visual baseline:

```text
f220 frameHash=0x3a5175a3 direct/setup=1851/906
f260 frameHash=0x3a5175a3 direct/setup=6765/3363
f420 frameHash=0x44d3a578 direct/setup=12514/6237
f420 textureMap.touched=327796
```

The hot repeated upload still starts at the runtime descriptor pointer and is
normalized by the pointer-start fix before Voodoo consumes payload words:

```text
TEXUPLOAD-PTRSTART source=0xffffffff80312998->ffffffff803129a4
  bgsrc=5:pnk+0x9280(... hdr=bad),6:geb+0x1280(... hdr=ok)
  bytes=0x10000 index=0/255 words=64
  first=8012e528/07f3fc00/803129a4/07e3fc01

TEXUPLOAD-RUN source=0xffffffff803129a4
  bgsrc=5:pnk+0x928c(... hdr=bad),6:geb+0x128c(... hdr=ok)
  sourceBase=0x00000000/sp1c=0x00000000 index=0/255 words=64
```

The full span confirms the upload crosses the promoted `0x8000` indexed-source
windows:

```text
segments=
  5:pnk@0x928c=0000ffff|6:geb@0x128c=07e3fc01;mem=07e3fc01+0x0..+0xbf4,
  6:geb@0x1e80=00000000;mem=00000000+0xbf4..+0x6d74,
  6:geb@0x8000=3dfe8d84|7:nin@0x0=00000000;mem=3dfe8d84+0x6d74..+0x9ea4,
  7:nin@0x3130=43490000;mem=00000000+0x9ea4..+0xed74,
  7:nin@0x8000=be276d7c|8:stg@0x0=00000000;mem=00000000+0xed74..+0x10000
```

Conclusion: the 0x10000 upload is not a single `geb` payload and should not be
clamped to `geb`'s nominal body length. The pointer-start correction is still
right: it skips descriptor/pointer words and starts at the actual payload
(`07e3fc01`). The remaining visual problem is more likely in indexed source
hydration/population semantics: later `nin`/`stg` body portions are still zero
when the cross-window upload reaches them, while the overlap boundary preserves
the previous `geb` word. The next code target is
`TryHydrateKnownRuntimeBgLoadModelIndexedTextureSource` /
`HydrateKnownRuntimeBgLoadModelRemainingIndexedTextureSources`, especially the
`requestedBytes` and indexed-source payload-byte override paths.

Negative cap control:

```text
EUTHERDRIVE_GAUNTDL_EXPERIMENT_RUNTIME_BGLOADMODEL_INDEXED_SOURCE_PAYLOAD_BYTES=0x8000
  f220 frameHash=0x21c0914a direct/setup=941/454
  f260 frameHash=0x21c0914a direct/setup=5855/2911
  f420 frameHash=0xace2e494 direct/setup=11863/5915
  f420 framebuffer=304122/244205 textureMap.touched=597810
```

This cap makes the indexed headers for `pnk`, `geb`, `nin`, and `stg`
plausible and prevents the full `geb` payload from overwriting the start of the
`nin` source window, but it regresses the frame hash and color coverage. Do not
promote a plain `0x8000` payload cap. The next fix needs to preserve the
runtime layout that keeps the baseline alive while targeting the specific
zero-filled body regions seen by the cross-window upload.

Negative disk-word substitution control:

```text
EUTHERDRIVE_GAUNTDL_EXPERIMENT_ZERO_BASE_UPLOAD_DISK_WORDS=1
  f220 frameHash=0x21c0914a direct/setup=1798/882
  f260 frameHash=0x21c0914a direct/setup=6712/3339
  f420 frameHash=0xd11222dc direct/setup=13280/6626
  f420 framebuffer=307200/307200 textureMap.touched=323252
```

This replaces too much live runtime data. The trace shows substitutions such as
`geb@0x129c mem=0x0c0b0101->disk=0x040b0101` and
`geb@0x12a8 mem=0x00000afd->disk=0xffff1348`, so it is not just filling
zero-body holes. Keep `ZERO_BASE_UPLOAD_DISK_WORDS` diagnostic-only. A future
variant would need a much narrower guard, for example only late zero-filled
body ranges that are not part of the descriptor/runtime fields.

Added a non-mutating hydration trace:

```text
EUTHERDRIVE_GAUNTDL_TRACE_BGLOADMODEL_INDEXED_SOURCE_HYDRATION=1
EUTHERDRIVE_GAUNTDL_TRACE_BGLOADMODEL_INDEXED_SOURCE_HYDRATION_LIMIT=80
  f220 frameHash=0x3a5175a3 direct/setup=1851/906
  f260 frameHash=0x3a5175a3 direct/setup=6765/3363
```

The trace confirms the overlap blocker directly:

```text
phase=distinct-source-hydrate index=6 dest=ffffffff80311718 bytes=0000b130 code=geb
phase=distinct-source-skip index=7 dest=ffffffff80319718 bytes=0000b130
  mask=True seedable=False partial=True
  sourceWords=00=3dfe8d84,04=3dc1d0a4,08=bdc0e993,0c=c3814000,...
phase=distinct-source-hydrate index=8 dest=ffffffff80321718 bytes=0000accc code=stg
```

`nin` is skipped because its first words have already been populated by the
overflowing `geb` payload. That makes the next candidate narrower than a global
payload cap: handle overlap seedability for index 7 without changing the live
runtime descriptor words that the broad disk-word substitution damaged.

Added a default-off overwrite probe:

```text
EUTHERDRIVE_GAUNTDL_EXPERIMENT_RUNTIME_BGLOADMODEL_OVERWRITE_INDEXED_SOURCE_MASK=0x80
EUTHERDRIVE_GAUNTDL_TRACE_BGLOADMODEL_INDEXED_SOURCE_HYDRATION=1
  f220 frameHash=0x21c0914a direct/setup=1066/514
  f260 frameHash=0x21c0914a direct/setup=6358/3160
  f420 frameHash=0x44d3a578 direct/setup=12233/6096
  f420 framebuffer=307200/307200 textureMap.touched=539424
```

The probe confirms the chain reaction:

```text
index=7 code=nin overwrite=True
index=8 seedable=False sourceWords=00=be276d7c,04=3e0483fb,...
```

Forcing only `nin` to seed fixes that one header but makes `stg` the next
overlapped non-seedable window. Because f220/f260 regress, keep
`OVERWRITE_INDEXED_SOURCE_MASK` as a diagnostic only. The next useful
experiment should either model chained overlap deliberately or trace the game
reader's expected window ownership before writing more payload bytes.

Added a default-off overlap zero-fill probe:

```text
EUTHERDRIVE_GAUNTDL_EXPERIMENT_RUNTIME_BGLOADMODEL_OVERLAP_ZERO_FILL_INDEXED_SOURCE_MASK
EUTHERDRIVE_GAUNTDL_EXPERIMENT_RUNTIME_BGLOADMODEL_OVERLAP_ZERO_FILL_INDEXED_SOURCE_MIN_OFFSET
```

The broad index-7 run confirms that preserving existing nonzero words is not
enough when the write happens during BGLoadModel source hydration:

```text
OVERLAP_ZERO_FILL_INDEXED_SOURCE_MASK=0x80
  index=7 code=nin filledBytes=0x79ac firstFilledOffset=0x44
  f220 frameHash=0x1c5e37c9 direct/setup=169/68
  f260 frameHash=0x1c5e37c9 direct/setup=169/68
  f420 frameHash=0x8a1fd828 direct/setup=2215/1091
  f420 framebuffer=246825/246825
```

Restricting the same BGLoadModel write to the upload-span hole still collapses
early rendering:

```text
OVERLAP_ZERO_FILL_INDEXED_SOURCE_MASK=0x80
OVERLAP_ZERO_FILL_INDEXED_SOURCE_MIN_OFFSET=0x3130
  index=7 code=nin filledBytes=0x7828 firstFilledOffset=0x3130
  f220 frameHash=0x1c5e37c9 direct/setup=169/68
  f260 frameHash=0x1c5e37c9 direct/setup=169/68
```

Conclusion: do not hydrate `nin`'s missing body bytes into the live source
window during BGLoadModel. The game/runtime still observes that window before
the later Type5 upload path, so filling the hole early changes control flow or
record interpretation.

Added a separate upload-time zero-word substitution probe:

```text
EUTHERDRIVE_GAUNTDL_EXPERIMENT_ZERO_BASE_UPLOAD_ZERO_DISK_WORD_INDEX_MASK
EUTHERDRIVE_GAUNTDL_EXPERIMENT_ZERO_BASE_UPLOAD_ZERO_DISK_WORD_MIN_OFFSET
EUTHERDRIVE_GAUNTDL_EXPERIMENT_ZERO_BASE_UPLOAD_ZERO_DISK_WORD_MAX_OFFSET
```

Narrow range test:

```text
ZERO_BASE_UPLOAD_ZERO_DISK_WORD_INDEX_MASK=0x80
ZERO_BASE_UPLOAD_ZERO_DISK_WORD_MIN_OFFSET=0x3130
ZERO_BASE_UPLOAD_ZERO_DISK_WORD_MAX_OFFSET=0x7fff
  f220 frameHash=0x21c0914a direct/setup=1680/823
  f260 frameHash=0x21c0914a direct/setup=6594/3280
  f420 frameHash=0x44d3a578 direct/setup=12343/6154
  f420 framebuffer=307200/307200 textureMap.touched=303092
```

This is better than mutating the BGLoadModel source window and preserves the
f420 hash/coverage, but it still regresses the earlier checkpoints to the same
family as the `0x8000` payload cap. Keep it diagnostic-only. The next target
should narrow by upload call/site or by the later hot repeated
`0xffffffff803129a4` run, not merely by indexed source offset.
