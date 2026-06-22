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

Continue texture-source/upload debugging. The current evidence points at the
Gauntlet BGLoadModel/indexed-source window that feeds the type-5 Voodoo upload
loop, not at a broad Voodoo sampler fix, a missing FIFO delivery path, or a
simple indexed-loop limit correction.

Immediate targets:

1. Trace complete upload-source span composition for the repeated
   `source=0xffffffff80312998` / `geb+0x1280` run. The current suspicious case
   copies `0x10000` bytes from a source window whose nominal `geb` payload is
   only `0xb130` bytes, so the next question is whether the run intentionally
   crosses into later indexed windows or reads an unmodelled gap.
2. Compare span segments against the promoted `0x8000` indexed-source stride
   and the known negative controls:
   larger strides, zero-base skip, metadata suppression, and header-limit clamp.
3. If the span proves the runtime expects cross-window data, test a narrow source
   population fix; keep it opt-in until it preserves f420 coverage and improves
   the visual dump.
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
