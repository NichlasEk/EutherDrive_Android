# Gauntlet DL Progress Plan - 2026-06-30

## 2026-06-30 e27b Warm-State Visual Bisect

The alternate visual oracle is now pinned to a narrow and reproducible commit
boundary. All runs used the current warm snapshot:

```text
/tmp/eutherdrive-gauntlet-probe/gauntdl-gauntdl24-fast-raw-f180-s200000-e27b9a6b6d3d.warm
```

Common command shape:

```text
EUTHERDRIVE_GAUNTDL_BRINGUP_BASELINE=1
EUTHERDRIVE_GAUNTDL_WARMUP_STATE=/tmp/eutherdrive-gauntlet-probe/gauntdl-gauntdl24-fast-raw-f180-s200000-e27b9a6b6d3d.warm
EUTHERDRIVE_GAUNTDL_WARMUP_FRAMES=180
EUTHERDRIVE_GAUNTDL_FRAME_CHECKPOINTS=420
dotnet run --project tools/GauntletProbe/GauntletProbe.csproj -c Release --no-build -- /home/nichlas/roms/MAME/Midway/Vegas/gauntd 420 200000
```

Bisect result:

```text
bad   329971c3 Promote Gauntlet full indexed payloads
good  76160800 Improve Gauntlet Voodoo buffer diagnostics
first good visual boundary: 76160800
```

Representative e27b/f420 results:

```text
73c41842  bad  frameHash=0x96f6b24f colors=2    AE vs 7d6841f7/329971c3 = 0
7d6841f7  bad  frameHash=0x96f6b24f colors=2    AE vs 73c41842 = 0
329971c3  bad  frameHash=0x96f6b24f colors=2    AE vs 73c41842 = 0

76160800  good frameHash=0x035dcece colors=2183 AE vs current = 0
94095bb5  good frameHash=0x035dcece colors=2183 AE vs current = 0
b556f733  good frameHash=0x035dcece colors=2183 AE vs current = 0
c46d2981  good frameHash=0x035dcece colors=2183 AE vs current = 0
64a54861  good frameHash=0x035dcece colors=2183 AE vs current = 0
```

The good PNG signature for the current non-flat family is:

```text
size=640x480 colors=2183 mean=13248.2
signature=34d0c6517992a5d074a603b21fa0586abb23e19a8a3e7e39b1ff615ed38fa47b
```

The important detail is that the draw/texture workload did not suddenly appear
at `76160800`. The bad and good sides both report the same major work counters:

```text
drawPackets=22410 directTriangles=4831 setupTriangles=2404 texWrites=4795569
textureMap.touched=588032
```

The commit message says "buffer diagnostics", but the behavior-changing hunk is
in `ChooseRenderBufferIndex()`: it adds visible unique-color counting, detects
low-detail full-screen fills, and prefers a higher-detail color buffer over the
front buffer when the front buffer is mostly a clear/fill surface. So this
bisect proves the older two-color purple image was primarily a presented-buffer
selection problem, not absence of Voodoo command/raster work.

Next technical focus:

1. Keep `76160800` as the exact display-buffer selection boundary and do not
   chase the older two-color output as a renderer regression.
2. Use `EUTHERDRIVE_GAUNTDL_DUMP_VOODOO_BUFFERS_PREFIX` on current HEAD at
   e27b/f420 to inspect `buf0`, `buf1`, and `buf2` directly.
3. Treat the current `0x035dcece` image as the real plateau: rendering is
   non-flat and selected correctly, but the image is still a diagonal/noisy band
   rather than scene geometry.
4. Continue from current HEAD by comparing texture sample/TMU state and setup
   triangle interpretation against MAME Voodoo behavior, not by revisiting the
   pre-`76160800` front-buffer output.

### Current Buffer Dump Follow-up

`GauntletProbe` now writes RAM/Voodoo RGB565 dumps with the same bit-replicated
RGB888 expansion used by the emulator frame path. Before this, buffer-dump PNGs
looked visually right but produced false AE differences against `DumpFrame`
because the probe used simple high-bit shifts.

Current HEAD e27b/f420 with:

```text
EUTHERDRIVE_GAUNTDL_DUMP_FRAME=/tmp/gauntdl-current-e27b-f420-selected-fixedbuf.ppm
EUTHERDRIVE_GAUNTDL_DUMP_VOODOO_BUFFERS_PREFIX=/tmp/gauntdl-current-e27b-f420-fixed
```

confirms the selected frame is exactly Voodoo color buffer 0:

```text
selected frame:
  frameHash=0x035dcece colors=2183
  signature=34d0c6517992a5d074a603b21fa0586abb23e19a8a3e7e39b1ff615ed38fa47b

buf0:
  colors=2183
  signature=34d0c6517992a5d074a603b21fa0586abb23e19a8a3e7e39b1ff615ed38fa47b
  AE vs selected = 0

buf1:
  colors=4
  AE vs selected = 307200

buf2:
  colors=2
  AE vs selected = 307200
```

Runtime status for the same run reports:

```text
buf=2/0/3 rlast=1
voodoo buffers=0:nz=310569:white=190:colored=310569
               1:nz=655360:white=0:colored=655360
               2:nz=330880:white=0:colored=330880
```

So the display selector is doing the useful thing: it ignores the old purple
front buffer and the green low-detail fill buffer, then presents `buf0`. The
remaining graphics bug is inside the selected high-detail buffer itself: a large
flat triangle/fill with a noisy horizontal texture band, not an incorrect final
buffer choice.

### Texture/Setup Trace Follow-up

A focused texture trace without the broken min-render-frame gate shows the
dominant early textured setup pattern:

```text
[GAUNTDL:VOODOO-TEXCOVER]
pc=0xffffffff800c4e5c cmd=0x0180A8CB:19
mode=0x8C24100F lod=0x000020C6 regbase=0x00005D82 base=0x02F120
xy=(0.000,-16231.000)/(49076.000,382.000)/(0.000,382.000)
stq=(0.000,0.172,0.001000)/(0.507,0.000,0.001000)/(0.000,0.000,0.001000)
bbox=(0,41)-(640,382) pixels=218240 zero=59522
```

The paired rejects are mostly clipped or empty-raster triangles from the same
PC/command family, with raw setup values such as:

```text
rawxy=0x432B87D1/0x473FB400 setup=0x0006002A fbz=0x437F0000
```

Texture sample debug on current HEAD at e27b/f420:

```text
texsamp=114141590/0x000510/0x781410
raw0x0000:68424466,0x0054:5754633,0x00C6:5753609,0x000D:4846839
rgb0x0000:36703663,0x0001:2437899,0x0002:1896688,0x0003:1353689
addr0x02F000:89207680,0x00C000:1569312,0x00D000:1569312,0x00B000:1569236
```

That points at vertex/setup interpretation and texture-address concentration:
most samples collapse into the `0x02F000` texture bucket and a very large share
of samples are raw zero / black.

Negative control: enabling only
`EUTHERDRIVE_GAUNTDL_FIX_VOODOO_SETUP_VERTEX_COORDINATE_WRAP=1` changes the work
profile but not the image:

```text
default:
  frameHash=0x035dcece texturedPixels=114141590 zero=36703663
  textured tri=12850 covered=1330 rejected=11520 clip=10327 empty=1137

setup coordinate wrap:
  frameHash=0x035dcece texturedPixels=36520452 zero=13662302
  textured tri=12850 covered=1323 rejected=11527 clip=11201 empty=269
  output signature=34d0c6517992a5d074a603b21fa0586abb23e19a8a3e7e39b1ff615ed38fa47b
  AE vs default selected frame = 0
```

So wrapping setup XY is useful as a workload reducer and confirms the huge
coordinates are being interpreted by the raster path, but it is not the visual
fix. Next probe should trace raw Type3 packet words for `0x0180A8CB` and compare
the per-vertex field decode against MAME/Voodoo setup expectations.

### Type3 Packet Trace Follow-up

A short f220 Type3 packet trace from the same e27b warm state confirms that the
dominant setup family is raw packet `0x0180A8CB`, emitted from
`pc=0xffffffff800c4e5c`:

```text
[GAUNTDL:VOODOO-TYPE3]
cmd=0x0180a8cb words=19 count=3 code=1 flags=0x602a rd=0x0003b440 mame=0 depth=19 holes=0
packet=0x0180a8cb/00000000/c681cc00/437f0000/3f800000/00000000/432b87d1/473fb400/00000000/437f0000/3f800000/43fd5b56/bc292a85/00000000/00000000/437f0000/3f800000/00000000/bc292a85
pc=0xffffffff800c4e5c

[GAUNTDL:VOODOO-TYPE3]
cmd=0x0180a8cb words=19 count=3 code=1 flags=0x602a rd=0x0003b48c mame=0 depth=19 holes=0
packet=0x0180a8cb/473fb400/00000000/437f0000/3f800000/43fd5b56/bc292a85/00000000/c681cc00/437f0000/3f800000/00000000/432b87d1/473fb400/c681cc00/437f0000/3f800000/43fd5b56/432b87d1
pc=0xffffffff800c4e5c
```

The run ended on the current bad f220 family:

```text
frameHash=0xd1549bb3
drawPackets=12791 directTriangles=301 setupTriangles=134 texWrites=108005
packetTypes=0:17,1:15669,2:0,3:12791,4:46035,5:5712,6:1,7:0
cmdstop=invalid-standard-window/0xBC292A85/337234/27020/0x14/0x14/v1/lg0xC6014/vw64/0x0180A8CB/0x473FB400/pc=0xFFFFFFFF800C4E5C/...
voodoo textured=tri:4048:covered:540:rejected:3508:pixels:58890240:zero:21497265:rejects:nf:0:deg:0:clip:2968:empty:540
textureMap=writes=0:nz=0:zero=0:touched=0
framebuffer=640x480 stride=2560 nonBlack=307200 colored=297042
```

Current command decode for `0x0180A8CB` derives:

```text
count=(cmd >> 6) & 0xf = 3
code=(cmd >> 3) & 7 = 1
flags=((cmd >> 10) & 0xff) | (((cmd >> 22) & 0xf) << 16) = 0x0006002a
```

A raw-packet-only hand read initially made the field order look suspicious. The
trace-only field decoder below rejects that candidate by printing the exact
word index consumed by the current decoder.

Follow-up trace-only code now adds
`EUTHERDRIVE_GAUNTDL_TRACE_VOODOO_TYPE3_FIELDS=1`. With a packet trace limit of
1, the first packet decodes as:

```text
[GAUNTDL:VOODOO-TYPE3-FIELDS] cmd=0x0180a8cb words=19 count=3 code=1 setup=0x0006002a decode=current pc=0xffffffff800c4e5c
[GAUNTDL:VOODOO-TYPE3-FIELDS] v0 w1:x=0x00000000(0) w2:y=0xc681cc00(-16614) w3:alpha11=0x437f0000(255) w4:wb13=0x3f800000(1) w5:s0_15=0x00000000(0) w6:t0_15=0x432b87d1(171.530533)
[GAUNTDL:VOODOO-TYPE3-FIELDS] v1 w7:x=0x473fb400(49076) w8:y=0x00000000(0) w9:alpha11=0x437f0000(255) w10:wb13=0x3f800000(1) w11:s0_15=0x43fd5b56(506.713562) w12:t0_15=0xbc292a85(-0.0103250789)
[GAUNTDL:VOODOO-TYPE3-FIELDS] v2 w13:x=0x00000000(0) w14:y=0x00000000(0) w15:alpha11=0x437f0000(255) w16:wb13=0x3f800000(1) w17:s0_15=0x00000000(0) w18:t0_15=0xbc292a85(-0.0103250789)
```

For `0x0180A8CB`, command bit 10 is not set, while bits 11, 13, and 15 are set.
That means the current consumer reads:

```text
x, y, alpha, Wb, S0, T0
```

The MAME `voodoo_2.cpp` packet-3 implementation uses the same order: X/Y are
always read, bit 11 is unpacked alpha, bit 13 sets Wb/W0/W1, and bit 15 sets
S0/T0. Reference:
<https://github.com/mamedev/mame/blob/master/src/devices/video/voodoo_2.cpp>.
The packet field order is therefore not the immediate visual bug.

The stronger next target is how this bring-up renderer uses the decoded setup
state:

1. We currently consume alpha but do not store it in `SetupVertex` or set alpha
   gradients.
2. We store only one texture `Q` field where MAME keeps Wb, W0, and W1 separate.
3. The default texture path linearly samples S/T and only uses W/Q under
   experiment/fix flags; MAME writes W and S/T setup gradients before drawing.

Next runtime tests should compare `EUTHERDRIVE_GAUNTDL_FIX_VOODOO_TEXTURE_PERSPECTIVE_DIVIDE`,
`EUTHERDRIVE_GAUNTDL_EXPERIMENT_VOODOO_TEXTURE_PERSPECTIVE_INTERPOLATE`, and
the MAME-style setup-gradient/fixed-fetch flags against the same f220/f420
oracles.

### W/Texture Setup Experiment Matrix

All runs used the same e27b warm state and f220 checkpoint. Baseline for this
oracle remains:

```text
frameHash=0xd1549bb3
directTriangles=301 setupTriangles=134 texWrites=108005
textured=tri:4048:covered:540:rejected:3508:pixels:58890240:zero:21497265
```

`EUTHERDRIVE_GAUNTDL_FIX_VOODOO_TEXTURE_PERSPECTIVE_DIVIDE=1`:

```text
frameHash=0xd1549bb3
directTriangles=301 setupTriangles=134 texWrites=108005
textured=tri:4048:covered:540:rejected:3508:pixels:58890240:zero:21497265
```

`EUTHERDRIVE_GAUNTDL_EXPERIMENT_VOODOO_TEXTURE_PERSPECTIVE_INTERPOLATE=1`:

```text
frameHash=0xd1549bb3
directTriangles=301 setupTriangles=134 texWrites=108005
textured=tri:4048:covered:540:rejected:3508:pixels:58890240:zero:21497265
```

`EUTHERDRIVE_GAUNTDL_EXPERIMENT_VOODOO_TEXTURE_MAME_SETUP_GRADIENTS=1`:

```text
frameHash=0xd1549bb3
lfbWrites=114891308
textured=tri:4048:covered:540:rejected:3508:pixels:58890240:zero:58890240
```

`EUTHERDRIVE_GAUNTDL_EXPERIMENT_VOODOO_TEXTURE_MAME_SETUP_GRADIENTS=1`
with `EUTHERDRIVE_GAUNTDL_EXPERIMENT_VOODOO_MAME_TEXTURE_FIXED_FETCH=1`:

```text
frameHash=0xd1549bb3
lfbWrites=114891308
textured=tri:4048:covered:540:rejected:3508:pixels:58890240:zero:58890240
```

Conclusion: the existing W/Q experiment flags do not move this f220 visual
oracle. The MAME-gradient path is useful as a negative control because it
changes counters heavily, but it currently drives every sampled textured pixel
to zero. Do not promote those flags as the next fix.

### e27b f420 Texture/Q Control Matrix

The same W/Q controls were re-run against the better current visual oracle,
`e27b9a6b6d3d` warm state at f420, because the f220 oracle above is the broken
`0xd1549bb3` stride family. Baseline f420 remains:

```text
frameHash=0x035dcece
frameSha256=2f8a78d7a651de1a13fd98c2f9ab4275006b04a99857d1930b2f46db724ef41a
direct/setup=6028/3002 drawPackets=21375 texWrites=4296625
textureMap=16754480:8367795:8386685:599296:0x000000:0x7fe444
textured=tri:12850:covered:1330:rejected:11520:pixels:114141590:zero:36703663
cmdstop=invalid-standard-window/0xbda7eca1/48552/7914/0x210/0x210/...
```

`EUTHERDRIVE_GAUNTDL_TRACE_VOODOO_TEXTURE_COVERED=1` plus
`EUTHERDRIVE_GAUNTDL_TRACE_VOODOO_TEXTURE_SAMPLES=1` shows the first dominant
covered triangle repeatedly sampling the same narrow texture address band:

```text
[GAUNTDL:VOODOO-TEXSAMPLE]
st=(0.005,3.516) xy=(0,3) size=256x256
mode=0x8C24100F lod=0x000020C6 regbase=0x00005D82 base=0x02F120
addr=0x02F420 word=0x000000DA raw=0x00DA result=0xDEB5
pc=0xffffffff800c4e5c

[GAUNTDL:VOODOO-TEXSAMPLE]
st=(1.502,3.516) xy=(1,3) size=256x256
mode=0x8C24100F lod=0x000020C6 regbase=0x00005D82 base=0x02F120
addr=0x02F421 word=0x000000DA raw=0x0000 result=0x0000
near=+1:0x02F424=0x3CF34001
pc=0xffffffff800c4e5c
```

The final bucket summary for that run matches the earlier texture sample
profile:

```text
texsamp=114141590/0x000510/0x781410
raw0x0000:68424466,0x0054:5754633,0x00C6:5753609,0x000D:4846839
rgb0x0000:36703663,0x0001:2437899,0x0002:1896688,0x0003:1353689
addr0x02F000:89207680,0x00C000:1569312,0x00D000:1569312,0x00B000:1569236
```

That makes the problem more specific than "no texture uploads": texture memory
is populated, but the hot sampler path is spending most of its time in a tiny
address neighborhood under `base=0x02F120`.

Two f420 W/Q controls were then run without sample tracing:

```text
EUTHERDRIVE_GAUNTDL_FIX_VOODOO_TEXTURE_PERSPECTIVE_DIVIDE=1
  frameHash=0x035dcece
  frameSha256=2f8a78d7a651de1a13fd98c2f9ab4275006b04a99857d1930b2f46db724ef41a
  direct/setup=6028/3002 drawPackets=21375 texWrites=4296625
  textured zero=36703663

EUTHERDRIVE_GAUNTDL_EXPERIMENT_VOODOO_TEXTURE_PERSPECTIVE_INTERPOLATE=1
  frameHash=0x035dcece
  frameSha256=2f8a78d7a651de1a13fd98c2f9ab4275006b04a99857d1930b2f46db724ef41a
  direct/setup=6028/3002 drawPackets=21375 texWrites=4296625
  textured zero=36703603
```

The dumped PPMs for baseline sample trace, perspective-divide, and
perspective-interpolate are byte-identical:

```text
aac2b0bd684a02d7eece6ddcda9aca4781d9db0d7542f20ba062d03812afd24a
```

Conclusion: W/Q use is still worth comparing against MAME, but the current
flags are not a visual fix for either the f220 bad family or the f420 e27b
visual oracle. The stronger next probe is texture address/layout calculation:
for the same `0x0180A8CB` triangles, compare current resolved base/LOD/size and
sample byte addresses against a MAME-style fetch model without changing the
drawn image first.

### Texture Fetch Compare Trace

`EUTHERDRIVE_GAUNTDL_TRACE_VOODOO_TEXTURE_FETCH_COMPARE=1` now adds an
opt-in, non-mutating sampler comparison. It logs the current sampler layout and
a MAME-style layout for the same `s/t` input, including target LOD, format,
filter bit, clamp bits, resolved size/base, `x/y`, byte address, source word,
raw texel, and converted RGB565. It is limited by
`EUTHERDRIVE_GAUNTDL_TRACE_VOODOO_TEXTURE_FETCH_COMPARE_LIMIT` and respects the
existing `EUTHERDRIVE_GAUNTDL_TRACE_VOODOO_TEXTURE_MIN_RENDER_FRAME`.

The f420 e27b control with the trace enabled is byte-for-byte visual neutral:

```text
frameHash=0x035dcece
frameSha256=2f8a78d7a651de1a13fd98c2f9ab4275006b04a99857d1930b2f46db724ef41a
direct/setup=6028/3002 drawPackets=21375 texWrites=4296625
textured=tri:12850:covered:1330:rejected:11520:pixels:114141590:zero:36703663
```

The first hot samples show no base/size/address difference between the current
LOD0 path and the existing MAME-style layout helper:

```text
[GAUNTDL:VOODOO-TEXFETCH]
st=(0.005,3.516) mode=0x8C24100F lod=0x000020C6 targetLod=0 fmt=0 b16=0 filt=1
cur=size=256x256:base=0x02F120:clamp=1/1:xy=0,3:addr=0x02F420:word=0x000000DA:raw=0x00DA:rgb=0xDED5
mame=size=256x256:base=0x02F120:clamp=0/0:xy=0,3:addr=0x02F420:word=0x000000DA:raw=0x00DA:rgb=0xDED5
deltaBase=0 deltaAddr=0 pc=0xffffffff800c4e5c
```

The only early difference is clamp policy: current clamps by default and the
MAME-style side uses mode bits, but these first positive coordinates still land
on the same byte address.

`EUTHERDRIVE_GAUNTDL_EXPERIMENT_VOODOO_TEXTURE_USE_LOD_MIN=1` was also tested
against the f420 oracle because `lod=0x000020C6` implies a low-six-bit LOD of
6. It is not a visual fix:

```text
frameHash=0x035dcece
frameSha256=2f8a78d7a651de1a13fd98c2f9ab4275006b04a99857d1930b2f46db724ef41a
lfbWrites=344147690
textured=tri:12850:covered:1330:rejected:11520:pixels:114141590:zero:93326876
```

A one-frame trace with `TEXTURE_USE_LOD_MIN=1` shows why it gets worse for the
dominant setup family:

```text
targetLod=6
cur=size=4x4:base=0x044660:clamp=1/1:xy=0,3:addr=0x04466C:word=0x00000000:raw=0x0000:rgb=0x0000
mame=size=4x4:base=0x044660:clamp=0/0:xy=0,3:addr=0x04466C:word=0x00000000:raw=0x0000:rgb=0x0000
```

Conclusion: do not promote LOD-min. For the current hot LOD0 path, the resolved
base/size/address calculation matches the MAME-style helper for the first
samples. The next useful trace should move one level earlier or later: either
explain why setup-generated `s/t` only walks `x=0..1` across huge screen
triangles, or inspect the upload/source that populates the active LOD0
neighborhood around `0x02F420`.

### FIFO Alias Control

A focused non-MAME command FIFO trace for the f220 stop word `0xbc292a85`
showed that the value is not a mysterious Type3 field-order bug. It is a real
FIFO payload word written at logical `0x000c6014` and later read through storage
alias `0x14` as if it were a command:

```text
stop reason=invalid-standard-window cmd=0xbc292a85 type=5 words=337234
rd=0x00000014 storage=0x00014 readValid=1 storedLogical=0x000c6014
validWindow=1..64/64 next=0x0180a8cb/0x473fb400
w0=0x00014:v1/lg0x000c6014/cur0xbc292a85/last=fifo/seq6/lg0x000c6014/addr0x000c6014/val0xbc292a85/pc0xffffffff800c4e5c
w1=0x00018:v1/lg0x000c6018/cur0x0180a8cb/last=fifo/seq7/lg0x000c6018/addr0x000c6018/val0x0180a8cb/pc0xffffffff800c4e5c
```

The broad MAME command-FIFO preset remains a negative control, not a fix. At
f220 it removes the final `cmdstop`, but also drops all textured triangle work
and keeps the same bad visible hash:

```text
EUTHERDRIVE_GAUNTDL_FIX_VOODOO_MAME_CMD_FIFO_MODEL=1
frameHash=0xd1549bb3
direct/setup=301/134 drawPackets=8743 texWrites=108005
textured=tri:0:covered:0:rejected:0:pixels:0:zero:0
packetTypes=0:11,1:11213,2:0,3:8743,4:31720,5:5712,6:1,7:0
```

Conclusion: keep FIFO ownership diagnostics, but do not promote
`FIX_VOODOO_MAME_CMD_FIFO_MODEL` and do not treat the latest
`invalid-standard-window` alone as the main repair target. The trace proves a
read-window/storage alias problem exists; the visual oracle still says the next
high-value work is texture layout/sampling around the active e27b f420 draw
family.

## 2026-06-30 Execution Update

Implemented the first work slice and used it to isolate the current plateau.

Code changes made in this slice:

- `GauntletProbe` now supports `EUTHERDRIVE_GAUNTDL_SUMMARY=1`.
- Each checkpoint emits one stable `summary gauntdl ...` line with module id,
  snapshot path, warmup state, PC, frame hash, visible framebuffer SHA-256,
  framebuffer non-black/colored counts, draw/setup counters, texture-map
  counters, cmdstop, and packet type counts.
- `EUTHERDRIVE_GAUNTDL_EXPERIMENT_RUNTIME_BGLOADMODEL_INDEXED_SOURCE_STRIDE`
  default was restored from `0x8000` to `0x2000`. The env override remains
  available for diagnostics.

Bisect result:

```text
good  cfdbf4c6 Accept hex Gauntlet probe integer flags
bad   8fbf62ec Promote Gauntlet indexed source stride
cause default stride changed 0x2000 -> 0x8000
```

Representative cold f180-to-f220 oracle results:

```text
cfdbf4c6 default 0x2000:
  f220 frameHash=0xe806de53 direct/setup=804/385 drawPackets=11338 texWrites=852913
  cmdstop=invalid-standard-window/0x0001a604/3/2/...

8fbf62ec default 0x8000:
  f220 frameHash=0xd1549bb3 direct/setup=301/134 drawPackets=12791 texWrites=108005
  cmdstop=invalid-standard-window/0xbc292a85/337234/25849/...
```

Current HEAD verification before the code change:

```text
default 0x8000:
  f220 frameHash=0xd1549bb3 direct/setup=301/134 texWrites=108005
  f260 frameHash=0xd1549bb3 direct/setup=301/134 texWrites=201999

env override 0x2000:
  f220 frameHash=0xe806de53 direct/setup=804/385 drawPackets=11380 texWrites=869297
  frameSha256=4da37be991dca35de45a2fc3be264c6791fb31a0c7d8e8cadd6c54e91da30bbe
```

Current HEAD verification after restoring the default to `0x2000`:

```text
snapshot=/tmp/eutherdrive-gauntlet-probe/head-default-after-stridefix-f180.warm
module=afddb84c200b

f220 frameHash=0xe806de53 direct/setup=804/385 drawPackets=11380 texWrites=869297
     framebuffer=640x480:157608:157586
     frameSha256=4da37be991dca35de45a2fc3be264c6791fb31a0c7d8e8cadd6c54e91da30bbe
     cmdstop=invalid-standard-window/0x0001a604/3/2/0x1bef8/...

f260 frameHash=0xe806de53 direct/setup=1090/528 drawPackets=15450 texWrites=3801393
     framebuffer=640x480:157608:157586
     frameSha256=4da37be991dca35de45a2fc3be264c6791fb31a0c7d8e8cadd6c54e91da30bbe
     cmdstop=invalid-standard-window/0x0001828c/3/2/0xfad0/...

f420 frameHash=0x44d3a578 direct/setup=3288/49236 drawPackets=20459 texWrites=6903947
     framebuffer=640x480:307200:307200
     frameSha256=df2d3c5b979cfaa956134fd7e3cd7ab4c891e04e96bb85443299cf354eb52dee
     cmdstop=invalid-standard-window/0x00012609/2/1/0x3e108/...
```

Conclusion: the worst current `0xd1549bb3` plateau was caused by promoting the
indexed source stride to `0x8000`. Restoring `0x2000` recovers the documented
`0x44d3a578` high-work f420 family. The next work should continue from this
restored f420 baseline, not from the broken `0xd1549bb3` family.

## Initial Verified State Before Execution

Starting checkout: `56b0ead3 Trace Gauntlet texture upload caller transitions`.
The worktree was clean before this plan was written.

The current auto warm snapshot is module-specific and newer than the documented
June 22/23 baseline:

```text
/tmp/eutherdrive-gauntlet-probe/gauntdl-gauntdl24-fast-raw-f180-s200000-e27b9a6b6d3d.warm
```

Fresh f180-to-f420 verification on current HEAD no longer matches the older
`0x44d3a578` or `0x772ab040` documented graphics baselines. It now plateaus
early:

```text
f220 frameHash=0xd1549bb3 direct/setup=301/134 framebuffer=307200/297042
f260 frameHash=0xd1549bb3 direct/setup=301/134 framebuffer=307200/297042
f420 frameHash=0xd1549bb3 direct/setup=301/134 framebuffer=307200/297042
cmdstop=invalid-standard-window/0xBC292A85/337234/.../pc=0xFFFFFFFF800C4E5C
textureMap.writes=0 at f220
```

The dumped f260/f420 image is not real scene graphics. It is a stable colored
band/block output, and f260/f420 PNG signatures match.

`EUTHERDRIVE_GAUNTDL_EXPERIMENT_RUNTIME_BGLOADMODEL_FULL_INDEXED_SOURCE_PAYLOADS=0`
does not change this result when starting from the current f180 snapshot. That
means the bad state is either already present by f180, or this flag is not the
active control after f180.

The Type5 stale-packet control is still diagnostic-only. Re-running
`EUTHERDRIVE_GAUNTDL_EXPERIMENT_VOODOO_DROP_IMPLAUSIBLE_TYPE5_PACKETS=1` from
the current f180 snapshot got past the first f220 plateau and reached:

```text
f260 frameHash=0xd1549bb3 direct/setup=301/134 -> 301/134 visible hash unchanged
f260 packetTypes default: 0:17,1:15669,2:0,3:12791,4:46035,5:5712,6:1,7:0
f260 packetTypes type5drop: 0:28,1:21735,2:0,3:16819,4:60848,5:10189,6:1,7:2
```

The rerun then failed to reach f420 in a reasonable time and was interrupted.
This is useful evidence: dropping the first absurd Type5 packet is not a fix,
but it proves the current blocker is command-FIFO state/read-window ownership
before the older `state+0x08` texture-source investigation can continue.

## External References Worth Using

Use modern MAME as the primary reference for Midway Vegas machine behavior and
Voodoo command FIFO semantics:

- https://github.com/mamedev/mame/blob/master/src/mame/williams/vegas.cpp
- https://github.com/mamedev/mame/blob/master/src/devices/video/voodoo.cpp
- https://github.com/mamedev/mame/blob/master/src/devices/video/voodoo_2.cpp

The old `src/mame/midway/vegas.cpp` path is stale. GitHub tree lookup on
2026-06-30 shows the current Vegas driver at `src/mame/williams/vegas.cpp`.
The useful MAME comparison points for this slice are:

- `vegas.cpp`: Gauntlet DL is documented as Vegas/Durango + Vegas SIO +
  Voodoo 2 with 2 TMUs at 4 MB each.
- `voodoo_2.cpp`: CMDFIFO write handling owns `depth`, `holes`,
  `address_min/address_max`, and `read_index` as one model.
- `voodoo_2.cpp`: CMDFIFO only executes when `depth >= words_needed(command)`;
  the packet word counts come from the command low-three-bit packet type.
- `voodoo_2.cpp`: mapped register writes become CMDFIFO writes when
  `cmdfifo_enable()` is set, with bit-18 byte swizzling applied before the
  direct FIFO write.

Use MAME 2003-Plus only as a secondary readability reference for older Voodoo
raster/texture code. It does not appear to carry the useful Vegas/Gauntlet DL
driver path, but the smaller Voodoo implementation is easier to compare against
when modern MAME is too broad:

- https://github.com/libretro/mame2003-plus-libretro/blob/master/src/vidhrdw/voodoo_vidhrdw.c
- https://github.com/libretro/mame2003-plus-libretro/blob/master/src/vidhrdw/voodblit.c
- https://github.com/libretro/mame2003-plus-libretro/blob/master/src/vidhrdw/voodoo.h

## Revised Priority Order

### 1. Freeze and Compare Repro Baselines - Done

Two comparable runs were preserved before changing the default:

- current HEAD + current f180 snapshot, f220/f260/f420 checkpoints;
- current HEAD + a cold regenerated f180 with the same current flags, then
  f220/f260/f420 checkpoints.

The goal is to separate three possibilities:

- the current f180 snapshot is poisoned;
- the current default preset changed since the older `0x44d3a578` baseline;
- a recent committed fix changed module ID and behavior, and the old snapshot
  cannot be compared directly.

The f420 run was interrupted after f260 during the first pass, but the f220 and
f260 summaries were enough to prove the same `0xd1549bb3` family from current
warm and cold snapshots. The stabilized summary mode now covers the success
criteria:

```text
One table with snapshot path, module id, frame hashes, direct/setup counters,
cmdstop reason, textureMap.writes, and dump hash for f220/f260/f420.
```

### 2. Bisect the Regression Family - Done

The first bad commit for the current `0xd1549bb3` family is:

```text
8fbf62ec Promote Gauntlet indexed source stride
```

The parent `cfdbf4c6` is good in this narrower oracle. The only code change is:

```text
ParsePositiveInt(... INDEXED_SOURCE_STRIDE, 0x2000)
-> ParsePositiveInt(... INDEXED_SOURCE_STRIDE, 0x8000)
```

### 3. Re-establish the Restored Baseline at f420 - Done

The fixed default was run from the saved f180 snapshot through f420:

```text
EUTHERDRIVE_GAUNTDL_BRINGUP_BASELINE=1
EUTHERDRIVE_GAUNTDL_SUMMARY=1
EUTHERDRIVE_GAUNTDL_WARMUP_STATE=/tmp/eutherdrive-gauntlet-probe/head-default-after-stridefix-f180.warm
EUTHERDRIVE_GAUNTDL_WARMUP_FRAMES=180
EUTHERDRIVE_GAUNTDL_FRAME_CHECKPOINTS=220,260,420
```

Result:

```text
f220 0xe806de53 direct/setup=804/385
f260 0xe806de53 direct/setup=1090/528
f420 0x44d3a578 direct/setup=3288/49236
```

### 4. Resume `state+0x08` Upload-Source Tracing From f420 - Done

The June 23 trace now matters again because render/FIFO progress is back in the
older family. The previous texture-source conclusion was:

```text
The upload caller copies zero from Glide state+0x08 into sp+0x1c at 800fe34c.
The descriptor at 80312998/803129a4 is stable.
```

Continue upstream ownership of that Glide state word from the restored f420
baseline. Do not use the broken `0xd1549bb3` / `0xBC292A85` path for this work.

Fresh restored-baseline traces:

```text
TEXUPLOAD-CALLER-CHANGE:
  s0=0xffffffff80262d64
  state08=00000000->00000000
  s6=0xffffffff80312998

TRACE_MEM writes-only 0xffffffff80262d6c:4 from warm f180 to f420:
  no writes

TRACE_MEM writes-only 0xffffffff80262d64:0x20 from cold to f180:
  pc=ffffffff800103a4 write32 ffffffff80262d64 00000000 mainram
  no writes to state+0x08
```

Conclusion: `state+0x08` is not a late-owned field in this path. It stays at
RAM/default zero from cold boot through the restored f420 baseline, so the
zero copied at `800fe34c` is probably intended Glide state for this upload
mode, not the next causal blocker.

### 5. Instrument Command-FIFO Ownership If f420 Still Stalls Visibly - Active

The restored f420 baseline still ends with:

```text
cmdstop=invalid-standard-window/0x00012609/2/1/0x3e108/...
```

The first restored f420 command-FIFO profile kept the same frame hash and showed
that the dominant path is still `800fe5d4` bulk texture upload work, not a new
BGLoadModel stride regression:

```text
f420 frameHash=0x44d3a578 frameSha256=df2d3c5b979cfaa956134fd7e3cd7ab4c891e04e96bb85443299cf354eb52dee
cmdpc 0xffffffff800fe5d4: 114009 packets / 7678870 words / 109350 type5
cmdcall bulk-end@0xffffffff800fe5d4: 113994 packets, mostly type5-only
```

The new stop diagnostic adds read-valid/generation/window data to `cmdstop`.
It does not change emulation behavior. A focused run with:

```text
EUTHERDRIVE_GAUNTDL_TRACE_VOODOO_CMD_FIFO_MODEL=1
EUTHERDRIVE_GAUNTDL_TRACE_VOODOO_CMD_FIFO_MODEL_COMMANDS=0x00012609
```

showed repeated stop rows like:

```text
stop reason=invalid-standard-window cmd=0x00012609 type=1 words=2
rd=0x00000174 storage=0x00174 readValid=1 storedLogical=0x000c6174
validWindow=1/2 next=0x0001828c/0xffffffff depth=1 valid=1
```

and the final debug status:

```text
cmdstop=invalid-standard-window/0x00012609/2/1/0x3E108/0x3E108/v1/lg0x3E108/vw1/0xBEE5888D/0x3F1140CD/pc=0xFFFFFFFF801066C4
```

Conclusion: the current restored f420 stop is a valid first word with a missing
or stale second word (`validWindow=1/2`), not an invalid read pointer by itself.
The next useful trace is write ownership for the packet word after each
`0x00012609` header, especially the storage offset paired with the final
`rd=0x3e108` stop.

The storage trace was made available in the non-MAME baseline by reusing
`EUTHERDRIVE_GAUNTDL_TRACE_VOODOO_CMD_FIFO_MODEL_STORAGE` for all command FIFO
writes when a storage offset filter is present. A first fixed-offset probe:

```text
EUTHERDRIVE_GAUNTDL_TRACE_VOODOO_CMD_FIFO_MODEL_STORAGE=0x3e108,0x3e10c
```

kept the baseline stable:

```text
f420 frameHash=0x44d3a578 frameSha256=df2d3c5b979cfaa956134fd7e3cd7ab4c891e04e96bb85443299cf354eb52dee
```

but showed that the final storage offsets are reused across unrelated FIFO
generations:

```text
storage=0x3e108 value=0x437f0000 pc=0xffffffff800c4e5c
storage=0x3e10c value=0x3a83126f pc=0xffffffff800c4e5c
storage=0x3e108 value=0x00010209 pc=0xffffffff8010302c
storage=0x3e10c value=0x0c482435 pc=0xffffffff80103030
storage=0x3e108 value=0x00000000 pc=0xffffffff800fe614
storage=0x3e10c value=0x0d000000 pc=0xffffffff800fe60c
storage=0x3e108 value=0x00000000 pc=0xffffffff800fe5d4
storage=0x3e10c value=0x00000000 pc=0xffffffff800fe5d4
```

This is another useful negative: fixed storage offsets do not identify the
final `0x00012609` producer because the command FIFO storage is recycled. The
next trace should be dynamic: when decode stops on `0x00012609`, report the
last write metadata for the stop storage and stop storage+1.

Dynamic last-writer tracing is now available in the stop rows and final debug
status. It keeps the restored baseline stable:

```text
checkpoint frame=420 frameHash=0x44d3a578
frameSha256=df2d3c5b979cfaa956134fd7e3cd7ab4c891e04e96bb85443299cf354eb52dee
direct/setup=3288/49236
```

The final restored f420 stop now carries the actual stop-window ownership:

```text
cmdstop=invalid-standard-window/0x00012609/2/1/0x3E108/0x3E108/v1/lg0x3E108/vw1
w0=0x3e108:v1/lg0x0003e108/cur0x00012609
   /last=fifo/seq63555/lg0x0003e108/addr0x0003e108
   /val0x00012609/pc0xffffffff801066c4
w1=0x3e10c:v0/lg0x0003e10c/cur0xbee5888d/last=none
```

Conclusion: `0x00012609` is a Type1 packet with `count=1`, so it requires one
payload word after the header. The header word at `rd=0x3e108` is newly written
by `801066c4`, but `rd+4` is not written in the f180-to-f420 window and remains
invalid with a stale storage value. This points at host packet assembly around
`801066c4`, not a valid-bit clear after a complete packet.

The command-FIFO assembly trace is now available with:

```text
EUTHERDRIVE_GAUNTDL_TRACE_VOODOO_CMD_FIFO_ASSEMBLY=1
EUTHERDRIVE_GAUNTDL_TRACE_VOODOO_CMD_FIFO_ASSEMBLY_PCS=ffffffff801066c4
EUTHERDRIVE_GAUNTDL_TRACE_VOODOO_CMD_FIFO_ASSEMBLY_LIMIT=460
```

It also keeps the restored f420 baseline stable:

```text
checkpoint frame=420 frameHash=0x44d3a578
frameSha256=df2d3c5b979cfaa956134fd7e3cd7ab4c891e04e96bb85443299cf354eb52dee
cmdstop=invalid-standard-window/0x00012609/2/1/0x3e108/...
```

The corrected storage-index debug confirms the repeated writer layout:

```text
0x00012609 row:
  prev=.../val0xff802000/pc0xffffffff801066c8
  w0=.../val0x00012609/pc0xffffffff801066c4
  w1=.../v0/.../cur0x00000000 or stale
```

Representative final rows show `depth=1` at the read position after each
`0x00012609` write, with the apparent payload immediately before the header,
not after it. That means `801066c4` is not simply omitting payload data. The
decoder is either starting one word late for this paired write pattern, or the
FIFO accounting is making a header-style word visible before the preceding
payload/header pair is consumed as intended.

Follow-up paired tracing with:

```text
EUTHERDRIVE_GAUNTDL_TRACE_VOODOO_CMD_FIFO_ASSEMBLY_PCS=ffffffff801066c4,ffffffff801066c8,ffffffff801031a8
EUTHERDRIVE_GAUNTDL_TRACE_VOODOO_CMD_FIFO_ASSEMBLY_COMMANDS=0x00011609,0x00012609,0xff802000,0xffffffff
```

shows the actual pattern:

```text
0xffffffff
0x00011609
0xff802000
0x00012609
0xff802000
```

The assembly trace runs before the per-write decode call. In the non-MAME
baseline path, `0x00011609 + 0xff802000` becomes a complete Type1 packet as
soon as the payload word arrives, then decode consumes both words and advances
`rd` to the next header. The following `0x00012609` is therefore briefly visible
with `depth=1` until its own payload arrives. The final `cmdstop` can simply be
the last transient partial-packet state observed at a frame boundary, not the
root visual blocker.

Next success criteria:

```text
Stop treating the latest cmdstop row as the primary blocker unless it is shown
to persist across payload arrival. Use frame dumps, visible hashes, and targeted
decode/write traces as the oracle.
```

### 6. Current Visual State: Restored Counters, Flat Output - Active

Fresh restored-baseline dump:

```text
/tmp/gauntdl-restored-f420-20260630.ppm
/tmp/gauntdl-restored-f420-20260630.png

frameHash=0x44d3a578
frameSha256=df2d3c5b979cfaa956134fd7e3cd7ab4c891e04e96bb85443299cf354eb52dee
direct/setup=3288/49236 drawPackets=20459 texWrites=6903947
framebuffer=640x480:307200:307200
```

The image is not a useful scene. It is almost entirely flat `#52EB9C`, with a
thin `#FF4100` diagonal:

```text
colors=4
306877 x #52EB9C
321    x #FF4100
1      x #31106B
1      x #42FFF7
```

A cold f180-to-f420 control with:

```text
EUTHERDRIVE_GAUNTDL_EXPERIMENT_RUNTIME_BGLOADMODEL_FULL_INDEXED_SOURCE_PAYLOADS=0
EUTHERDRIVE_GAUNTDL_WARMUP_STATE=/tmp/eutherdrive-gauntlet-probe/full-off-after-stridefix-f180.warm
```

still produced the same visible hash and SHA:

```text
frameHash=0x44d3a578
frameSha256=df2d3c5b979cfaa956134fd7e3cd7ab4c891e04e96bb85443299cf354eb52dee
```

The counters changed (`direct/setup=2805/1383`, `drawPackets=24621`,
`texWrites=6244835`), but the final framebuffer histogram was identical. So the
flat-output regression is not caused only by the full indexed-source payload
flag. It is a broader drift from the older `0x772ab040` visual scene family.

First historical comparison:

```text
worktree=/tmp/eutherdrive-gauntlet-73c41842
commit=73c41842 Advance Gauntlet graphics baseline
snapshot=/tmp/eutherdrive-gauntlet-probe/73c41842-f180.warm
dump=/tmp/gauntdl-73c41842-f420.png

f420 frameHash=0x2ed3dbdd
direct/setup=2805/1383 drawPackets=24621 texWrites=6244835
framebuffer=640x480:307200:307199
```

This did not reproduce `0x772ab040` from a freshly generated f180 state. The
image is still the same flat `#52EB9C` field with the thin `#FF4100` diagonal.
The old documented visual-scene family may therefore depend on the missing
`446392c984c8` warm snapshot/state lineage, or on a narrower runtime condition
than commit selection alone.

Missing-snapshot search and alternate lineage check:

```text
missing:
  /tmp/eutherdrive-gauntlet-probe/gauntdl-gauntdl24-fast-raw-f180-s200000-446392c984c8.warm

searched:
  /tmp
  /home/nichlas
  /run/media/nichlas

available sibling:
  /tmp/eutherdrive-gauntlet-probe/gauntdl-gauntdl24-fast-raw-f180-s200000-e27b9a6b6d3d.warm
```

Current HEAD from the available `e27b9a6b6d3d` f180 warm state produces a
different f420 visual family:

```text
dump=/tmp/gauntdl-current-e27b-f420.png
log=/tmp/gauntdl-current-e27b-f420.log

f420 frameHash=0x035dcece
frameSha256=2f8a78d7a651de1a13fd98c2f9ab4275006b04a99857d1930b2f46db724ef41a
direct/setup=6028/3002 drawPackets=21375 texWrites=4296625
framebuffer=640x480:305614:305508
colors=2183
dominant=#000C00
cmdstop=invalid-standard-window/0xbda7eca1/48552/7914/0x210/0x210/...
```

The saved PNG is still not a real scene, but it is no longer the four-color
flat field. It shows a large diagonal overdraw shape with a noisy horizontal
band. This makes `e27b9a6b6d3d` the best currently reproducible visual-state
oracle until the missing `446392c984c8` snapshot is found.

Conclusion: snapshot lineage is now proven to matter independently of commit
selection. The next repair loop should preserve both visual families:

- `head-default-after-stridefix-f180.warm` -> flat `0x44d3a578`/four-color
  family;
- `gauntdl-gauntdl24-fast-raw-f180-s200000-e27b9a6b6d3d.warm` ->
  non-flat `0x035dcece`/2183-color family.

MAME-CMDFIFO model control from the same `e27b9a6b6d3d` f180 warm state:

```text
EUTHERDRIVE_GAUNTDL_FIX_VOODOO_MAME_CMD_FIFO_MODEL=1

dump=/tmp/gauntdl-current-e27b-mamefifo-f420.png
log=/tmp/gauntdl-current-e27b-mamefifo-f420.log

f420 frameHash=0x67bfcd31
frameSha256=5f271c6522ef8eeb1e3ffa4c658ab76302359675ed4dd99d0021b4f625bef0d1
direct/setup=470/205 drawPackets=9589 texWrites=225317
framebuffer=640x480:307200:277093
colors=4
dominant=#52CB10
cmdstop=depth/0xbc3dc2dc/15/5/0xbe57e498/0x3e498/...
```

Visual inspection: this is another flat/geometry-test style image, not an
improvement over the default `e27b9a6b6d3d` 2183-color output. MAME-CMDFIFO
comparison remains useful for targeted tracing, but the existing broad
`FIX_VOODOO_MAME_CMD_FIFO_MODEL` preset should not be promoted as a visual fix.

Full-indexed-source-payload control from the same `e27b9a6b6d3d` f180 warm
state:

```text
EUTHERDRIVE_GAUNTDL_EXPERIMENT_RUNTIME_BGLOADMODEL_FULL_INDEXED_SOURCE_PAYLOADS=0

dump=/tmp/gauntdl-current-e27b-fulloff-f420.png
log=/tmp/gauntdl-current-e27b-fulloff-f420.log

f420 frameHash=0x47258dc9
frameSha256=059abfa3719496db4085da7f433255af364ed2fd6d3736f060ab0aed17fe6ca6
direct/setup=6028/3002 drawPackets=21375 texWrites=4296625
framebuffer=640x480:305622:305511
colors=2178
dominant=#000C00
cmdstop=invalid-standard-window/0xbda7eca1/48552/7914/0x210/0x210/...
```

This changes the `gei` indexed-header byte count in the log from `0x0000a13c`
to `0x00009f60`, so the flag is active after the warm state. However, the f420
PNG is visually the same diagonal/noisy-band family as default `e27b9a6b6d3d`.
Pixel diff against `/tmp/gauntdl-current-e27b-f420.png`:

```text
AE=227 / 307200 pixels
RMSE=907.807 (0.0138523 normalized)
```

Conclusion: full indexed-source payloads are not the primary cause of the
current non-flat e27b visual family either.

Historical-code comparison using the same `e27b9a6b6d3d` f180 warm state:

```text
worktree=/tmp/eutherdrive-gauntlet-73c41842
commit=73c41842 Advance Gauntlet graphics baseline
snapshot=/tmp/eutherdrive-gauntlet-probe/gauntdl-gauntdl24-fast-raw-f180-s200000-e27b9a6b6d3d.warm
dump=/tmp/gauntdl-73c41842-e27b-f420.png
log=/tmp/gauntdl-73c41842-e27b-f420.log

f420 frameHash=0x96f6b24f
direct/setup=4831/2404 drawPackets=22410 texWrites=4795569
framebuffer=640x480:307200:307200
colors=2
dominant=#31106B
```

Visual inspection: this is a near-solid purple screen, not the current
`e27b9a6b6d3d` non-flat diagonal/noisy-band family. So the available e27b warm
state is not sufficient by itself to reproduce the current non-flat output in
the older `73c41842` code. The 2183-color family requires both the e27b lineage
and later code changes after that historical checkpoint.

### 7. Promote Only Visible or Causal Fixes

Keep these diagnostic-only unless they become part of a proven causal repair:

- broad implausible packet drops;
- Type5-only stale packet drop;
- zero-word disk substitution;
- broad BGLoadModel source mutation;
- MAME FIFO model toggles as one-shot preset swaps.

Promotion bar:

```text
The fix must either restore the older high-work f420 family or improve visible
frame dumps without reducing live command/texture activity. Counter-only gains
are not enough.
```

## Next Concrete Work Slice

1. Use the new non-mutating texture fetch comparison trace to move one step
   earlier or later from the hot `0x0180A8CB` samples: either explain why
   setup-generated `s/t` only walks `x=0..1` across huge screen triangles, or
   inspect the upload/source that populates the active LOD0 neighborhood around
   `0x02F420`. The trace already proves LOD0 current-vs-MAME layout is identical
   for the first samples and LOD-min is visually negative.
2. Reproduce or bracket the older `0x772ab040` visual scene family. The original
   warm snapshot `/tmp/eutherdrive-gauntlet-probe/gauntdl-gauntdl24-fast-raw-f180-s200000-446392c984c8.warm`
   is no longer present under `/tmp`, `/home/nichlas`, or `/run/media/nichlas`.
   A cold f180 rerun at `73c41842` is still flat-fill. The available sibling
   `e27b9a6b6d3d` snapshot does produce a distinct non-flat f420 image, so use
   it as the current best alternate visual oracle while continuing to watch for
   the missing `446392c984c8` state.
3. Use a visual oracle, not just counters:
   `frameHash`, SHA, histogram, and a saved PNG for f420. `0x44d3a578` with the
   four-color `#52EB9C` histogram is the current flat family; `0x035dcece` with
   2183 colors is the current non-flat sibling family; `0x772ab040` with
   `292034/291360` was the older scene family.
4. Keep the stride regression guard from this pass:
   `0xd1549bb3`, `0xBC292A85`, and `direct/setup=301/134` mean the bad
   `0x8000` indexed-source stride family is back.
5. Compare our CMDFIFO model against current MAME `voodoo_2.cpp` semantics:
   mapped write swizzling, `address_min/address_max`, `holes`, `depth`, and
   `read_index` should be treated as one causal unit. Do this as tracing or a
   narrow model fix, not as packet dropping or the broad existing
   `FIX_VOODOO_MAME_CMD_FIFO_MODEL` preset.
6. Treat `FULL_INDEXED_SOURCE_PAYLOADS=0` as non-causal for the current f420
   visual state unless a narrower earlier-frame oracle proves otherwise.
7. Do not use `73c41842` as a direct oracle for the current e27b family. It can
   load the snapshot, but it collapses to a two-color screen.
8. Preserve the current assembly/last-writer tracing as diagnostics, but stop
   treating transient `invalid-standard-window` as the main repair target.
