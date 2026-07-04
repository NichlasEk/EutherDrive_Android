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

### LOD0 Bucket Write Trace

The existing bucket write trace was then aimed at the dominant sampled bucket:

```text
EUTHERDRIVE_GAUNTDL_TRACE_VOODOO_TEXTURE_WRITE_BUCKETS=2f
EUTHERDRIVE_GAUNTDL_TRACE_VOODOO_TEXTURE_WRITE_BUCKETS_LIMIT=40
```

The f420 result stayed on the same visual oracle:

```text
frameHash=0x035dcece
frameSha256=2f8a78d7a651de1a13fd98c2f9ab4275006b04a99857d1930b2f46db724ef41a
textureMap=16754480:8367795:8386685:599296:0x000000:0x7fe444
```

Bucket `0x02F000` is actively written late in the f420 run by the type-5
service at `pc=0xffffffff800fe5d4`:

```text
[GAUNTDL:VOODOO-TEXWRITE]
n=1 bucket=0x02F000 word=0x002180 addr=0x02F000 value=0x00008042 nzb=2
lod=0 ts=0x00 tt=0x43 bpp=1 seq8=1
mode=0x00000000 tlod=0x00000800 tbase=0x000055A0 pc=0xffffffff800fe5d4

[GAUNTDL:VOODOO-TEXWRITE]
n=4 bucket=0x02F000 word=0x002183 addr=0x02F00C value=0xBE8DCD3E nzb=4
lod=0 ts=0x0C tt=0x43 bpp=1 seq8=1
mode=0x00000000 tlod=0x00000800 tbase=0x000055A0 pc=0xffffffff800fe5d4
```

The first 40 traced writes continue sequentially from `0x02F000` through
`0x02F09C`, all with `seq8=1`, `lod=0`, `tlod=0x00000800`, and
`tbase=0x000055A0`. The values are a mix of sparse zeros and
float-looking words such as `0xBE8DCD3E`, `0x04EFEBBE`, `0xDC6122BD`,
`0xE19950BD`, and `0xB123F5BE`.

This is an important split:

- Fetch at `pc=0xffffffff800c4e5c` samples the same bucket through
  `mode=0x8C24100F`, `lod=0x000020C6`, `regbase=0x00005D82`,
  `base=0x02F120`.
- Upload at `pc=0xffffffff800fe5d4` writes the bucket with
  `mode=0x00000000`, `tlod=0x00000800`, and `tbase=0x000055A0`.

Conclusion: the hot LOD0 bucket is not missing, but the data being uploaded
there looks suspicious for 8-bit texture content. The next focused probe should
correlate these `0x02F000` writes with the type-5 upload source pointer/run
metadata and decide whether this bucket is receiving real texture bytes,
geometry/float data, or correctly uploaded data that the sampler interprets
through the wrong texture mode.

Follow-up code now makes that correlation directly in the diagnostic stream:

```text
EUTHERDRIVE_GAUNTDL_TRACE_VOODOO_TYPE5_PAYLOAD_TARGET_WORDS=2180
EUTHERDRIVE_GAUNTDL_TRACE_VOODOO_TYPE5_PAYLOAD_TARGET_LIMIT=256
EUTHERDRIVE_GAUNTDL_TRACE_VOODOO_TEXTURE_WRITE_BUCKETS=2f
EUTHERDRIVE_GAUNTDL_TRACE_VOODOO_TEXTURE_WRITE_BUCKETS_LIMIT=12
```

The target-focused f420 run stayed byte-for-byte on the same visual oracle:

```text
frameHash=0x035dcece
frameSha256=2f8a78d7a651de1a13fd98c2f9ab4275006b04a99857d1930b2f46db724ef41a
direct/setup=6028/3002 drawPackets=21375 texWrites=4296625
textureMap=16754480:8367795:8386685:599296:0x000000:0x7fe444
```

The exact Type5 packet that starts the traced bucket write is:

```text
[GAUNTDL:VOODOO-TYPE5-TARGET]
cmd=0xc0000205 space=3 targetWord=0x00002180 count=64 nz=61
first=0x42800000/dec=0x00008042 second=0x43000000/dec=0x00000043
last=0xbcdf0d41/dec=0x410ddfbc
packet=0x00024388 rd=0x00024388 depth=12474 holes=0 pc=0xffffffff800fe5d4
```

The first texture writes from that same packet are:

```text
[GAUNTDL:VOODOO-TEXWRITE]
n=1 bucket=0x02F000 word=0x002180 addr=0x02F000 value=0x00008042
type5=cmd=0xC0000205:space=3:targetStart=0x002180:target=0x002180:i=0/64:packet=0x00024388:rd=0x00024388:stream=0

[GAUNTDL:VOODOO-TEXWRITE]
n=4 bucket=0x02F000 word=0x002183 addr=0x02F00C value=0xBE8DCD3E
type5=cmd=0xC0000205:space=3:targetStart=0x002180:target=0x002183:i=3/64:packet=0x00024388:rd=0x00024388:stream=0
```

That changes the working theory: the hot bucket is receiving Type5 texture-space
data, but the raw FIFO payload starts with float-looking words
`0x42800000/0x43000000` that only become the sparse 8-bit-looking texel words
after the current Type5 texture endian conversion. The nearby upload payload
trace still repeats `DWF_GEILOIN_F01` metadata, but that alone does not prove
the source span for `packet=0x00024388`.

Follow-up word dumping now confirms the packet-to-bucket transform exactly:

```text
log=/tmp/gauntdl-e27b-f420-type5target2180-words64-bucket2f.log

packet=0x00024388 targetWord=0x00002180 count=64
rawWords=0x42800000/0x43000000/0x00000000/0x3ecd8dbe/...
decWords=0x00008042/0x00000043/0x00000000/0xbe8dcd3e/...

bucket writes:
0x002180 -> 0x00008042
0x002181 -> 0x00000043
0x002182 -> 0x00000000
0x002183 -> 0xBE8DCD3E
```

So the Type5 texture endian conversion is what turns the float-looking raw
stream into the active 8-bit texture-space bucket values. The remaining unknown
is earlier: which CPU/FIFO source write span produces command-FIFO packet
`0x00024388`, and whether that span is supposed to be texture bytes or command
payload. Do not change texture sampling yet; first decide whether the bad bucket
is caused by wrong source selection, wrong FIFO target/fifo-base math, or Type5
texture endian policy.

The focused FIFO-packet upload trace now resolves that earlier source:

```text
log=/tmp/gauntdl-e27b-f420-fifopacket24388-type5target.log

[GAUNTDL:TEXUPLOAD-FIFO-TARGET]
packet=67 index=67/255 fifo=0xa82a4388 fifoLow=0x024388
packetSource=0x00008600 sourceBase=0x00000000 source=0xffffffff802e6f68 words=64
bgsrc=1:gei+0x3850(body=0xa0d0/-0x6880 len=0xa13c hdr60=0x00000020 hdr64=0x00000016 hdr=ok)
first=0x42800000/0x43000000/0x00000000/0x3ecd8dbe text=""

[GAUNTDL:VOODOO-TYPE5-TARGET]
packet=0x00024388 targetWord=0x00002180
rawWords=0x42800000/0x43000000/0x00000000/0x3ecd8dbe/...
```

So `packet=0x00024388` is not a DWF metadata upload. It is sourced from the
`gei` indexed payload at `source+0x3850`, and the FIFO target word comes from
`packetSource=0x00008600` -> `0x00002180`. The next causal question is whether
that `gei+0x3850` span is the correct texture stream for this slot, or whether
the indexed-source body/header stride is selecting a geometry/float payload as
texture input.

Disk comparison on the same focused FIFO packet:

```text
log=/tmp/gauntdl-e27b-f420-fifopacket24388-diskcmp.log

gei@0x3850=42800000;mem=42800000
gei@0x3854=43000000;mem=43000000
gei@0x3858=00000000;mem=00000000
gei@0x385c=3ecd8dbe;mem=3ecd8dbe
```

The overlapping `snm` interpretation also appears in the diagnostic string, but
its header is already marked bad and its disk words do not match RAM. The
relevant conclusion is that the `gei+0x3850` RAM source is not corrupted; it
matches the disk bytes exactly.

Run-source tracing adds the cursor context for that same packet:

```text
log=/tmp/gauntdl-e27b-f420-fifopacket24388-runsource.log

packet=67 index=67/255 source=0xffffffff802e6f68
runSource=0xffffffff802e2c68 runDelta=0x4300 runStart=0
bgsrc=1:gei+0x3850(body=0xa0d0/-0x6880 len=0xa13c hdr=ok)
frameHash=0x035dcece
frameSha256=2f8a78d7a651de1a13fd98c2f9ab4275006b04a99857d1930b2f46db724ef41a
```

So the `0x24388` FIFO packet is not a separate one-off source decision. It is
packet index 67 inside a longer zero-base upload run that starts at
`0xffffffff802e2c68`, and the suspect `gei+0x3850` span appears after advancing
`0x4300` bytes through that run. The next target is therefore the run start and
per-packet cursor math for zero-base indexed uploads, not a local corruption of
the packet payload.

The follow-up `runSpan` trace makes that boundary explicit:

```text
log=/tmp/gauntdl-e27b-f420-fifopacket24388-runspan.log

runSource=0xffffffff802e2c68 runDelta=0x4300 runStart=0
runFirst=0x0001e69c/0x00001188/0x0000000b/0x00000000
runSpan=none+0x0..+0xab0,
        1:gei@0x0=00000000;mem=00000000+0xab0..+0x2ab0,
        1:gei@0x2000=bc891a33|2:snm@0x0=00000000;mem=bc891a33+0x2ab0..+0x4400
```

So the upload run begins `0xab0` bytes before the `gei` payload base, then
crosses the `gei` and overlapping `snm` windows. That `none` prefix looks like
a descriptor/control span, not disk-backed texture payload.

MAME's Voodoo 2 CMDFIFO path byte-swizzles direct FIFO writes when mapped
address bit 18 is set, so the narrow direct-endian control was re-run without
the broad MAME FIFO model:

```text
EUTHERDRIVE_GAUNTDL_EXPERIMENT_VOODOO_MAME_FIFO_DIRECT_ENDIAN=1
log=/tmp/gauntdl-e27b-f420-directendian-fifopacket24388.log
dump=/tmp/gauntdl-e27b-f420-directendian.ppm

packet=67 index=67/255 source=0xffffffff802e6f68
runSource=0xffffffff802e2c68 runDelta=0x4300
rawWords=0x42800000/0x43000000/0x00000000/0x3ecd8dbe/...
decWords=0x00008042/0x00000043/0x00000000/0xbe8dcd3e/...

frameHash=0x69438b6a
frameSha256=0c2f1e7475a197de12a50f71e82b23e34158231da189d5b57c78a61ed4d570ea
colors=3
AE vs baseline selected PPM = 307200
direct/setup=6973/3451 drawPackets=16985 texWrites=3318385
```

That confirms direct-endian is not a no-op, but it is not a candidate visual
fix. It keeps the same `gei+0x3850` -> Type5 -> `0x02F000` chain and collapses
the f420 image from the 2183-color non-flat baseline to a three-color frame.
Do not promote it; keep MAME's swizzle rule as a reference for FIFO tracing
only.

Skipping the unknown prefix to start the run at `gei@0` is also a negative
control:

```text
EUTHERDRIVE_GAUNTDL_EXPERIMENT_ZERO_BASE_UPLOAD_SKIP_UNKNOWN_PREFIX=1
log=/tmp/gauntdl-e27b-f420-skipunknownprefix-fifopacket24388.log
dump=/tmp/gauntdl-e27b-f420-skipunknownprefix.ppm

source=0xffffffff802e2c68->0xffffffff802e3718 prefix=0xab0 next=1:gei
packet=67 source=0xffffffff802e7a18 runSource=0xffffffff802e3718
bgsrc=1:gei+0x4300

frameHash=0x0e416805
frameSha256=46863cd6ab05f81ba15d8e9a804318d63d28f8b79178b39c80ea606d0ef0ca54
colors=2
AE vs baseline selected PPM = 307200
direct/setup=8181/2568 drawPackets=21369 texWrites=4248625
```

This proves the prefix matters, but blindly dropping it is not the fix: command
and texture work stay high while the visible frame collapses to a two-color
image. Keep the experiment as a bracket around source-cursor math; do not
promote it.

Broad body-offset hydration is a negative control:

```text
EUTHERDRIVE_GAUNTDL_EXPERIMENT_RUNTIME_BGLOADMODEL_INDEXED_SOURCE_PAYLOAD_FROM_BODY=1
log=/tmp/gauntdl-e27b-f420-bodypayload-fifopacket24388.log

frameHash=0x7631e7d3
frameSha256=8dcaa4271b58ff31d1954efd8d9edff0bbec9fc34625af30382fcc9cb51a5149
direct/setup=1153/341 drawPackets=19014 texWrites=6976235
framebuffer=640x480:67860:67860
```

That flag removes the exact `packet=0x00024388` chain and changes the command
stream too broadly. Treat it as diagnostic evidence that body-offset selection
matters, not as a candidate fix.

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
- zero-base upload unknown-prefix skip;
- MAME FIFO model toggles as one-shot preset swaps.

Promotion bar:

```text
The fix must either restore the older high-work f420 family or improve visible
frame dumps without reducing live command/texture activity. Counter-only gains
are not enough.
```

### 2026-06-30 Source Selector Checkpoint

Added a narrow trace-only selector probe:

```text
EUTHERDRIVE_GAUNTDL_TRACE_TEXTURE_UPLOAD_SOURCE_SELECTOR=1
EUTHERDRIVE_GAUNTDL_TRACE_TEXTURE_UPLOAD_RUN_SOURCE=ffffffff802e2c68
```

The probe logs only the upload wrapper source-select instruction:

```text
pc=0xffffffff800fe228 op=0x8fb3006c  # lw s3,0x6c(sp)
```

f420 with the `e27b9a6b6d3d` warm state stayed on the current non-flat
baseline:

```text
log=/tmp/gauntdl-e27b-f420-source-selector2c68.log
lines=186
frameHash=0x035dcece
frameSha256=2f8a78d7a651de1a13fd98c2f9ab4275006b04a99857d1930b2f46db724ef41a
draw/setup/direct=21375/3002/6028
texWrites=4296625
```

The selector row corrected the stack address and confirmed that the pointer is
already selected before the existing `TEXUPLOAD-CALLER` window:

```text
[GAUNTDL:TEXUPLOAD-SOURCE-SELECT]
pc=0xffffffff800fe228 op=0x8fb3006c ra=0xffffffff80109704
sp=0xffffffff807ffb48 slot=0xffffffff807ffbb4
selected=0xffffffff802e2c68
sp60=00000003 sp64=00000000 sp68=00000003 sp6c=802e2c68 sp70=00000000 sp74=000000ff
first=0001e69c/00001188/0000000b/00000000
```

The previous fixed-stack trace at `807ffbbc` was one word past this selector
slot in the relevant call frame, so treat it as stack-reuse noise unless a
future trace correlates it with `sp` and PC. Re-running the memory write trace
on the corrected selector slot confirmed the real writer:

```text
log=/tmp/gauntdl-e27b-f420-stack807ffbb4-writes.log
lines=4735
pc=ffffffff800ebf6c write32 ffffffff807ffbb4 800f0c44
pc=ffffffff801096f0 write32 ffffffff807ffbb4 802e2c68
```

A follow-up exact CPU trace on `pc=801096f0` with a higher limit reached the
target rows and explains the stack-frame offset:

```text
log=/tmp/gauntdl-e27b-f420-cputrace-801096f0-limit6000.log
lines=969
first target row line=785
pc=ffffffff801096f0 op=afaa001c  # sw t2,0x1c(sp)
t2=s0=ffffffff802e2c68
sp=ffffffff807ffb98
ra=ffffffff801095c8
t3=ffffffff8013af90
s3=ffffffff807ffc20 s4=ffffffff80157f34 s5=ffffffff80157f50 s7=0000000000000003
a1/s6 sweep=0x0..0x3f0000, a0/fp toggle=1/0
```

So `801096f0` writes `sp+0x1c == 807ffbb4` in its frame, and `800fe228`
later reads the same word as `sp+0x6c == 807ffbb4` in the wrapper frame.
The writer is therefore a selector store loop; the next causal trace must find
where `t2/s0` is populated with `802e2c68` before this PC.

That run also kept the same f420 baseline:

```text
frameHash=0x035dcece
frameSha256=2f8a78d7a651de1a13fd98c2f9ab4275006b04a99857d1930b2f46db724ef41a
drawPackets=21375 directTriangles=6028 setupTriangles=3002 texWrites=4296625
```

Conclusion: `800fe228` consumes the selected upload run from `sp+0x6c`,
and `801096f0` is the concrete writer for the `0xffffffff802e2c68`
selector value. `801096f0` is not the origin of that pointer; trace the
producer path that loads `t2/s0` before the store, not the old `807ffbbc`
stack address.

### 2026-06-30 Source Producer Checkpoint

Added a second trace-only selector probe to follow the selected upload run
pointer before `801096f0` stores it:

```text
EUTHERDRIVE_GAUNTDL_TRACE_TEXTURE_UPLOAD_SOURCE_PRODUCER=1
EUTHERDRIVE_GAUNTDL_TRACE_TEXTURE_UPLOAD_SOURCE_PRODUCER_LIMIT=160
EUTHERDRIVE_GAUNTDL_TRACE_TEXTURE_UPLOAD_SOURCE_PRODUCER_PC_MIN=ffffffff801095c0
EUTHERDRIVE_GAUNTDL_TRACE_TEXTURE_UPLOAD_SOURCE_PRODUCER_PC_MAX=ffffffff801096f0
EUTHERDRIVE_GAUNTDL_TRACE_TEXTURE_UPLOAD_RUN_SOURCE=ffffffff802e2c68
```

The broader generic CPU trace over `801095c8..801096f0` was too noisy and hit
its trace limit before the target source appeared:

```text
log=/tmp/gauntdl-e27b-f420-cputrace-801095c8-801096f0-ra801095c8.log
lines=20122
```

The new source-producer hook kept the same f420 e27b visual baseline stable:

```text
frameHash=0x035dcece
frameSha256=2f8a78d7a651de1a13fd98c2f9ab4275006b04a99857d1930b2f46db724ef41a
drawPackets=21375 directTriangles=6028 setupTriangles=3002 texWrites=4296625
```

The focused trace around `801095c0..801096f0` shows that `801096c0` and
`801096f0` are still downstream, not the origin:

```text
log=/tmp/gauntdl-e27b-f420-source-producer-2c68.log

801095c0 jal 0x801096ac:
  s0=802e2c68 and caller sp+0x1c already equal the target source.

801096c0 lw t2,0x4c(sp):
  after the callee prologue shifts sp, the same word is at sp+0x4c and is
  loaded into t2.

801096f0 sw t2,0x1c(sp):
  t2 is stored into the selector slot later consumed by 800fe228.
```

A second focused trace one call level earlier found the immediate producer for
`s0`:

```text
log=/tmp/gauntdl-e27b-f420-source-producer-2c68-precall.log

8010957c lw s0,0x10(s3)
s3=807ffc20
source word address=807ffc30
```

So the concrete downstream chain is now:

```text
807ffc30 -> 8010957c loads s0
801095c0 -> calls 801096ac with s0 and caller sp+0x1c already set
801096c0 -> loads t2 from callee sp+0x4c
801096f0 -> stores t2 to callee sp+0x1c
800fe228 -> consumes the same selector word as wrapper sp+0x6c
```

The write trace on `807ffc30` is noisy because the address is stack space, but
it gives the next exact producer PC:

```text
log=/tmp/gauntdl-e27b-f420-stack807ffc30-writes.log

pc=ffffffff80102a0c write32 ffffffff807ffc30 802e2c68
pc=ffffffff80102a0c write32 ffffffff807ffc30 802e2c68
pc=ffffffff800a7344 write32 ffffffff807ffc30 802e2c68
```

`800a7344` then repeats the same target write many times. That makes
`800a7344` the next high-value trace target: inspect the source register and
address state for the write that populates `807ffc30=802e2c68`. Keep
`8010957c` as the read point and `801096c0`/`801096f0`/`800fe228` as proven
downstream consumers.

### 2026-06-30 Source Argument Chain Checkpoint

The `800a7344` writer is now resolved one level earlier. The combined CPU/mem
trace used:

```text
log=/tmp/gauntdl-e27b-f420-pc800a7344-stack807ffc30.log
EUTHERDRIVE_GAUNTDL_TRACE_CPU_PC_MIN=ffffffff800a7344
EUTHERDRIVE_GAUNTDL_TRACE_CPU_PC_MAX=ffffffff800a7344
EUTHERDRIVE_GAUNTDL_TRACE_MEM_ADDRESS=ffffffff807ffc30:4
```

It kept the same f420 e27b baseline:

```text
frameHash=0x035dcece
frameSha256=2f8a78d7a651de1a13fd98c2f9ab4275006b04a99857d1930b2f46db724ef41a
drawPackets=21375 directTriangles=6028 setupTriangles=3002 texWrites=4296625
```

For the target write, `800a7344` is the delay-slot store:

```text
800a7344 op=afa20020  # sw v0,0x20(sp)
sp=807ffc10
v0=802e2c68
write address=807ffc30
```

The wider CPU trace around that block shows the local formula:

```text
log=/tmp/gauntdl-e27b-f420-cputrace-800a72f0-800a7344.log

800a7328 lw v0,0x20(sp)   -> v0=0
800a7330 lw t0,0x6c(sp)   -> t0=802e2c68
800a7334 lw t1,0x70(sp)   -> t1=0
800a7338 addu v0,t0,v0    -> v0=802e2c68
800a733c subu v0,v0,t1    -> v0=802e2c68
800a7340 jal 0x800a64fc
800a7344 sw v0,0x20(sp)   -> 807ffc30=802e2c68
```

So `800a7344` is not the origin either. It copies an already-passed source
argument from `sp+0x6c` through a local offset calculation.

Tracing `sp+0x6c == 807ffc7c` found the source of that local argument slot:

```text
log=/tmp/gauntdl-e27b-f420-stack807ffc7c-writes.log

pc=ffffffff800a70cc write32 ffffffff807ffc7c 802e2c68
```

The matching CPU trace shows `800a70cc` is function prologue argument save:

```text
log=/tmp/gauntdl-e27b-f420-cputrace-800a70a0-800a70d0.log

800a70cc op=afa5006c  # sw a1,0x6c(sp)
sp=807ffc10
a1=802e2c68
```

The callsite for that frame is `ra=800ab3b8`. A narrow callsite trace shows the
argument is loaded from the caller stack frame:

```text
log=/tmp/gauntdl-e27b-f420-cputrace-800ab390-800ab3b8.log

800ab3a0 op=8fa50018  # lw a1,0x18(sp)
sp=807ffc78
source word address=807ffc90

800ab3b0 jal 0x800a7094
a0=813815a0 a1=802e2c68 a2=0 a3=1188
```

The currently proven source-selector chain is therefore:

```text
807ffc90 -> 800ab3a0 loads a1
800ab3b0 -> calls 800a7094 with a1=802e2c68
800a70cc -> stores a1 to callee sp+0x6c (807ffc7c)
800a7330 -> reloads t0 from callee sp+0x6c
800a7338/800a733c -> computes v0=t0+0-0
800a7344 -> stores v0 to callee sp+0x20 (807ffc30)
8010957c -> later reads 807ffc30 into s0
801096c0/801096f0/800fe228 -> downstream upload-source consumers
```

Next causal target is now `807ffc90`, not `800a7344`: trace who writes the
caller stack argument consumed by `800ab3a0`.

### 2026-06-30 Caller Argument Slot Checkpoint

The next stack argument slot was traced directly:

```text
log=/tmp/gauntdl-e27b-f420-stack807ffc90-writes.log
EUTHERDRIVE_GAUNTDL_TRACE_MEM_ADDRESS=ffffffff807ffc90:4
```

The run again stayed on the same f420 e27b baseline:

```text
frameHash=0x035dcece
frameSha256=2f8a78d7a651de1a13fd98c2f9ab4275006b04a99857d1930b2f46db724ef41a
drawPackets=21375 directTriangles=6028 setupTriangles=3002 texWrites=4296625
```

Most writes to `807ffc90` are stack reuse noise, but the target value has one
clear writer:

```text
pc=ffffffff800ab298 write32 ffffffff807ffc90 802e2c68
```

So the current upstream chain is now:

```text
800ab298 -> writes 807ffc90=802e2c68
800ab3a0 -> loads 807ffc90 into a1
800ab3b0 -> calls 800a7094 with a1=802e2c68
800a70cc -> stores a1 to callee sp+0x6c (807ffc7c)
800a7330 -> reloads t0 from callee sp+0x6c
800a7344 -> stores computed v0 to callee sp+0x20 (807ffc30)
8010957c -> later reads 807ffc30 into s0
```

Next trace should be a narrow CPU window around `800ab298` to identify which
register supplies `802e2c68` and whether that value is a direct table entry,
iterator cursor, or caller argument.

### 2026-07-01 Arena Cursor Producer Checkpoint

The `800ab298` writer is now resolved. The narrow CPU trace used:

```text
log=/tmp/gauntdl-e27b-f420-cputrace-800ab270-800ab2a0.log
EUTHERDRIVE_GAUNTDL_TRACE_CPU_PC_MIN=ffffffff800ab270
EUTHERDRIVE_GAUNTDL_TRACE_CPU_PC_MAX=ffffffff800ab2a0
```

It stayed on the same e27b f420 visual oracle:

```text
frameHash=0x035dcece
frameSha256=2f8a78d7a651de1a13fd98c2f9ab4275006b04a99857d1930b2f46db724ef41a
drawPackets=21375 directTriangles=6028 setupTriangles=3002 texWrites=4296625
```

The target write is a MIPS delay-slot store. `800ab298` stores the return value
from the previous call at `800ab28c`, not the call at `800ab294`:

```text
800ab28c jal 0x800c9088
800ab290 sw a0,0x50(sp)
800ab294 jal 0x800c910c
800ab298 sw v0,0x18(sp)   -> 807ffc90=802e2c68
```

At the target row:

```text
sp=807ffc78
v0=802e2c68
store address=807ffc90
```

So `800ab298` is not the owner. It saves the return value from `800c9088`.

The follow-up trace around `800c9088` used:

```text
log=/tmp/gauntdl-e27b-f420-cputrace-800c9088-800c9108.log
EUTHERDRIVE_GAUNTDL_TRACE_CPU_PC_MIN=ffffffff800c9088
EUTHERDRIVE_GAUNTDL_TRACE_CPU_PC_MAX=ffffffff800c9108
```

For the target call (`ra=800ab294`), `800c9088` is an arena-pointer helper:

```text
800c90bc lw v1,0x80fc(v1)  -> reads cursor 0x00007828 from 0x802280fc
800c90d0 sra v0,v0,2
800c90d8 sll v0,v1,2       -> keeps aligned offset 0x00007828
800c90e0 lw a0,0x8104(a0)  -> reads arena base 0x802db440 from 0x80228104
800c90e4 addu v1,v0,a0     -> v1=802e2c68
800c90e8 move v0,v1        -> returns v0=802e2c68
800c9104 jr ra
```

The source pointer is therefore:

```text
0x802db440 + 0x00007828 = 0x802e2c68
```

The corresponding writes-only memory trace used:

```text
log=/tmp/gauntdl-e27b-f420-mem-802280fc-80228104-writes.log
EUTHERDRIVE_GAUNTDL_TRACE_MEM_ADDRESS=ffffffff802280fc:4,ffffffff80228104:4
```

It found no warm-run writes to `0x80228104`, so the arena base is stable during
this window. The target cursor value is written by `800c9014`:

```text
pc=ffffffff800c9014 write32 ffffffff802280fc 00007828
```

The narrow CPU trace around that writer used:

```text
log=/tmp/gauntdl-e27b-f420-cputrace-800c8fe0-800c9018.log
EUTHERDRIVE_GAUNTDL_TRACE_CPU_PC_MIN=ffffffff800c8fe0
EUTHERDRIVE_GAUNTDL_TRACE_CPU_PC_MAX=ffffffff800c9018
```

For the target cursor update:

```text
800c9004 lw v0,0x80fc(v0)  -> old cursor 0x00007828
800c9008 lw v1,0x20(fp)    -> allocation size 0
800c900c addu v0,v0,v1     -> new cursor 0x00007828
800c9014 sw v0,0x80fc(at)  -> writes 0x802280fc=0x00007828
```

This resolves the current stack selector path as an arena allocation pointer,
not a semantic texture table selector. The proven upstream chain is now:

```text
800c9014 -> writes arena cursor 0x802280fc=0x00007828
800c9088 -> returns arena base 0x802db440 + cursor 0x7828 = 0x802e2c68
800ab298 -> delay-slot stores v0 to caller stack 807ffc90
800ab3a0 -> loads 807ffc90 into a1
800ab3b0 -> calls 800a7094 with a1=802e2c68
800a70cc -> stores a1 to callee sp+0x6c (807ffc7c)
800a7330 -> reloads t0 from callee sp+0x6c
800a7344 -> stores computed v0 to callee sp+0x20 (807ffc30)
8010957c -> later reads 807ffc30 into s0
801096c0/801096f0/800fe228 -> downstream upload-source consumers
```

Do not keep chasing `807ffc90` or `800ab298` as if they own the upload source.
If pointer provenance is still needed, the next upstream allocation callsite is
`ra=800c8fa4` with the allocation size in the helper frame at `fp+0x20`. The
higher-value graphics target is now the payload content or upload decode for
the arena bytes at `802e2c68`, not the already-proven stack handoff.

### 2026-07-01 Indexed Source Hydration Checkpoint

The next payload/content check explains why raw memory writes were not visible
for the source bytes in the warm f180->f420 window. These two writes-only
probes:

```text
log=/tmp/gauntdl-e27b-f420-mem-802e2c68-payload-writes.log
EUTHERDRIVE_GAUNTDL_TRACE_MEM_ADDRESS=ffffffff802e2c68:0xab0

log=/tmp/gauntdl-e27b-f420-mem-802e3718-head-writes.log
EUTHERDRIVE_GAUNTDL_TRACE_MEM_ADDRESS=ffffffff802e3718:0x100
```

both stayed on the same oracle:

```text
frameHash=0x035dcece
frameSha256=2f8a78d7a651de1a13fd98c2f9ab4275006b04a99857d1930b2f46db724ef41a
drawPackets=21375 directTriangles=6028 setupTriangles=3002 texWrites=4296625
```

Neither log emitted `[GAUNTDL:MEM]` rows for those watched ranges. The logs only
showed the existing BGLoadModel hooks, which means the bus-level memory trace
does not observe emulator-side `_memory.Write*` hydration. It is still useful
for CPU writes, but not for this runtime-repair payload seed path.

The existing hydration trace is the right source of truth for this path:

```text
log=/tmp/gauntdl-e27b-f420-bgloadmodel-indexed-source-hydration.log
EUTHERDRIVE_GAUNTDL_TRACE_BGLOADMODEL_INDEXED_SOURCE_HYDRATION=1
EUTHERDRIVE_GAUNTDL_TRACE_BGLOADMODEL_INDEXED_SOURCE_HYDRATION_LIMIT=240
```

For the relevant source, it reports:

```text
phase=distinct-source-hydrate
index=1
code=gei
dest=ffffffff802e3718
bytes=0000a13c
disk=14a6f600
first=00000000
overwrite=False
sourceWords=00=00000000,04=00000000,08=00000000,0c=00000000,
            40=f00b0001,5c=0000a0d0,60=00000020,64=00000016,68=00000000
```

`bgloadmodel-distinct-source` then publishes that hydrated source through the
source table:

```text
pc=ffffffff800aae98
index=1
slot=ffffffff802529a4:802e1718->802e3718
cloned=False
seededIndexedHeader=True
```

So the upload-source arena pointer chain and the BGLoadModel hydration path now
line up as:

```text
runSource=802e2c68
GEI source starts at runSource+0xab0 = 802e3718
GEI source is hydrated by runtime repair from disk offset 0x14a6f600
target Type5 source 802e6f68 is GEI+0x3850
```

This makes the remaining graphics target narrower. The source pointer is
plumbed correctly, and the GEI payload seed is intentional runtime-repair state.
The next likely defect is how the Type5 upload/decode path interprets the
hydrated GEI bytes, the `0xab0` prefix before the GEI header, or the GEI header
fields (`0x5c=0xa0d0`, `0x60=0x20`, `0x64=0x16`) when building Voodoo texture
memory.

### 2026-07-01 Type5 Upload Link Checkpoint

`GauntletDarkLegacyAdapter` now has a trace-only
`[GAUNTDL:TEXUPLOAD-LINK]` line which connects the CPU upload loop to the
Voodoo Type5 decoder:

```text
packetSource -> packetOffset -> targetWord
source -> first payload words
fifo/fifoLow/fifoRingDelta -> later Type5 packet offset
```

This removes the previous manual guesswork between RAM source offsets and
command-FIFO packet offsets.

The focused f420 run used the e27b warm snapshot and:

```text
EUTHERDRIVE_GAUNTDL_TRACE_TEXTURE_UPLOAD_PACKET_SOURCE=00008600
EUTHERDRIVE_GAUNTDL_TRACE_VOODOO_TYPE5_PAYLOAD_TARGET_WORDS=2180
EUTHERDRIVE_GAUNTDL_TRACE_VOODOO_TYPE5_PAYLOAD_TARGET_LIMIT=256
```

The important matched pair is:

```text
[GAUNTDL:TEXUPLOAD-LINK]
packetSource=0x00008600 packetOffset=0x00008600 targetWord=0x00002180
source=0xffffffff80316ca4
fifo=0xa82a4238 fifoLow=0x024238
raw=0x00000000/0x01d90000/0x00000000/0x00000000
swap=0x00000000/0x0000d901/0x00000000/0x00000000

[GAUNTDL:VOODOO-TYPE5-TARGET]
targetWord=0x00002180 packet=0x00024238
rawWords=0x00000000/0x01d90000/...
decWords=0x00000000/0x0000d901/...
```

So the currently observed `0x2180` Type5 texture write is not lossy between the
CPU upload loop and the Type5 decoder. The raw payload word `0x01d90000` is
delivered to the decoder and byte-swapped there to `0x0000d901`.

This also clarifies two earlier confusing offsets:

```text
packetSource=0x00008600 -> targetWord=0x00002180
packetSource=0x00208600 -> targetWord=0x00082180
```

`fifoBase` is zero in this run, so the non-zero-base packet source does not map
to the same Type5 target. Separately, `802e6f68` is still a valid GEI-relative
upload source (`gei+0x3850`), but its first payload words are
`0x42800000/0x43000000/...`; it is not the source of the early `0x2180` payload
with `0x01d90000`.

The validation oracle stayed unchanged after the trace-only code:

```text
frameHash=0x035dcece
frameSha256=2f8a78d7a651de1a13fd98c2f9ab4275006b04a99857d1930b2f46db724ef41a
drawPackets=21375 directTriangles=6028 setupTriangles=3002 texWrites=4296625
```

### 2026-07-01 Texture Bucket Routing Checkpoint

The existing texture-write bucket trace can now be used with the Type5/upload
link trace. For the overused sample bucket `0x02f000`, the filter value is
`0x2f` because `TextureZeroSampleBucketShift == 12`:

```text
EUTHERDRIVE_GAUNTDL_TRACE_VOODOO_TEXTURE_WRITE_BUCKETS=2f
EUTHERDRIVE_GAUNTDL_TRACE_VOODOO_TEXTURE_WRITE_BUCKETS_LIMIT=96
EUTHERDRIVE_GAUNTDL_TRACE_VOODOO_TYPE5_PAYLOAD_TARGET_WORDS=2180
```

The f420 run proves that the packet paired in the upload-link checkpoint writes
directly into `0x02f000`:

```text
[GAUNTDL:VOODOO-TEXWRITE]
bucket=0x02F000 word=0x002180 addr=0x02F000 value=0x00008042
lod=0 ts=0x00 tt=0x43 bpp=1 seq8=1
mode=0x00000000 tlod=0x00000800 tbase=0x000055A0
type5=cmd=0xC0000205:space=3:targetStart=0x002180:target=0x002180:i=0/64:packet=0x00024388:rd=0x00024388

[GAUNTDL:VOODOO-TEXWRITE]
bucket=0x02F000 word=0x002181 addr=0x02F004 value=0x00000043
lod=0 ts=0x04 tt=0x43 bpp=1 seq8=1
mode=0x00000000 tlod=0x00000800 tbase=0x000055A0
type5=cmd=0xC0000205:space=3:targetStart=0x002180:target=0x002181:i=1/64:packet=0x00024388:rd=0x00024388
```

The next packet starts at target `0x2200` and remains in the same bucket:

```text
bucket=0x02F000 word=0x002200 addr=0x02F100
type5=targetStart=0x002200 packet=0x00024490
```

So the current dominant `0x02f000` texture bucket is not just a sample-side
artifact. It is being populated by Type5 texture writes whose target words
`0x2180..0x221f` map through the current upload addressing path as:

```text
lod=0
ts=(wordOffset << 2) & 0xff
tt=(wordOffset >> 7) & 0xff
mode=0x00000000
tlod=0x00000800
tbase=0x000055A0
bpp=1 seq8=1
byteOffset=0x02F000...
```

That makes the next likely bug either stale/default TMU state at upload time
(`mode=0`, `tlod=0x800`, `tbase=0x55a0`) or an addressing formula mismatch for
sequential 8-bit downloads, not a CPU-upload-to-Type5 transport loss.

### 2026-07-01 Texture Upload TMU-State Checkpoint

`TraceTextureWriteBucket()` now includes the selected global/TMU texture state
on each bucket hit. A focused f420 run used:

```text
EUTHERDRIVE_GAUNTDL_TRACE_VOODOO_TMU_WRITES=1
EUTHERDRIVE_GAUNTDL_TRACE_VOODOO_TEXTURE_WRITE_BUCKETS=2f
EUTHERDRIVE_GAUNTDL_TRACE_VOODOO_TEXTURE_WRITE_BUCKETS_LIMIT=24
EUTHERDRIVE_GAUNTDL_TRACE_VOODOO_TYPE5_PAYLOAD_TARGET_WORDS=2180
```

The first `0x2180` write now shows the selected upload state directly:

```text
[GAUNTDL:VOODOO-TEXWRITE]
bucket=0x02F000 word=0x002180 addr=0x02F000 value=0x00008042
lod=0 ts=0x00 tt=0x43 bpp=1 seq8=1
mode=0x00000000 tlod=0x00000800 tbase=0x000055A0
global=0x00000000/0x00000000/0x00000000
tmu0=00000000/00000800/000055A0/ncc7/7
tmu1=0C26100F/FF802000/00000000/ncc7/5
type5=cmd=0xC0000205:space=3:targetStart=0x002180:target=0x002180:i=0/64:packet=0x00024388:rd=0x00024388:stream=0
pc=0xffffffff800fe5d4
```

So the bad `0x02f000` mapping is not coming from global texture registers.
`WriteTexturePort32()` is selecting TMU bank 0 for `word=0x2180`, and bank 0 is
already carrying the default-looking `mode=0`, `tlod=0x800`, `tbase=0x55a0`
state at the moment the Type5 packet is consumed. TMU bank 1 simultaneously has
a different texture mode/LOD state, which means the next branch should focus on
either the last writer of TMU0's upload registers or whether the Type5 texture
download should be selecting another bank/state for this target range.

The TMU write trace now has target/register/value filters:

```text
EUTHERDRIVE_GAUNTDL_TRACE_VOODOO_TMU_WRITE_TARGETS=c3,2c3,4c3
EUTHERDRIVE_GAUNTDL_TRACE_VOODOO_TMU_WRITE_REGISTERS=c1,c3
EUTHERDRIVE_GAUNTDL_TRACE_VOODOO_TMU_WRITE_VALUES=800,55a0
EUTHERDRIVE_GAUNTDL_TRACE_VOODOO_TMU_WRITES_LIMIT=80
```

Using those filters closes the TMU0 writer question for the `0x2180` upload.
The bad/default-looking state is written immediately before the Type5 texture
payload by a Type4 register packet from the texture upload service:

```text
[GAUNTDL:VOODOO-TMU]
target=0x2c1 chip=0x2 reg=0xc1 value=0x00000800
cmd=0x00059604 pc=0xffffffff800fe428
before=tmu0=00000000/FF802000/00000000
after=tmu0=00000000/00000800/00000000

[GAUNTDL:VOODOO-TMU]
target=0x2c3 chip=0x2 reg=0xc3 value=0x000055a0
cmd=0x00059604 packet=0x0001fe60 rd=0x0001fe60
before=tmu0=00000000/00000800/00000000
after=tmu0=00000000/00000800/000055A0
pc=0xffffffff800fe428

[GAUNTDL:VOODOO-TEXWRITE]
bucket=0x02F000 word=0x002180 addr=0x02F000
tmu0=00000000/00000800/000055A0
type5=targetStart=0x002180:packet=0x00024388
pc=0xffffffff800fe5d4
```

So this is no longer just stale state. The upload service itself emits the
TMU0 LOD/base setup (`target=0x2c1/0x2c3`, chipmask `0x2`) immediately before
the Type5 download consumes target `0x2180`. The next likely fork is either
our Type4/chipmask/TMU-bank interpretation for `cmd=0x00059604`, or the upload
service is legitimately programming TMU0 for a different texture and the Type5
target/bank selection should not use TMU0 for this range.

The validation oracle stayed unchanged:

```text
frameHash=0x035dcece
frameSha256=2f8a78d7a651de1a13fd98c2f9ab4275006b04a99857d1930b2f46db724ef41a
drawPackets=21375 directTriangles=6028 setupTriangles=3002 texWrites=4296625
```

## Next Concrete Work Slice

### 2026-07-01 MAME Texture Upload Parity Check

Checked the current MAME Voodoo primary source against the traced upload path:

- register-space chipmask decode uses bits 8..11, with zero expanded to the
  active chip mask;
- `textureMode`, `tLOD`, and `texBaseAddr` are TREX registers, and chipmask bit
  1 writes TMU0 while bit 2 writes TMU1;
- texture memory offset bits 19..20 select the TMU, and `seq_8_downld` comes
  from TMU0;
- texture-memory writes use `t * width + s`, then align the scaled write offset
  with `& ~3`.

That means our observed `cmd=0x00059604` writes to `target=0x2c1/0x2c3`
legitimately program TMU0. The `target=0x2180` Type5 texture download also
legitimately selects TMU0. The `0x02f000` write is therefore not explained by a
wrong Type4 chipmask or wrong TMU select.

Promoted `EUTHERDRIVE_GAUNTDL_FIX_VOODOO_TEXTURE_DOWNLOAD_ALIGN32=1` into the
bringup baseline because MAME aligns texture downloads to the 32-bit write
boundary. The warm f420 oracle with that flag stayed pixel-identical:

```text
frameHash=0x035dcece
frameSha256=2f8a78d7a651de1a13fd98c2f9ab4275006b04a99857d1930b2f46db724ef41a
drawPackets=21375 directTriangles=6028 setupTriangles=3002 texWrites=4296625
textureMap baseline touched=608352
textureMap align32 touched=599296
```

This is a correctness cleanup, not the visible fix: the current visible oracle
does not move. The next useful slice should leave Type4/TMU decode alone and
instead trace why the command FIFO reaches the `0x2180` texture upload source
when the frame still terminates at the same `invalid-standard-window`
`0xbda7eca1` command.

### 2026-07-01 Command FIFO Bulk-Window Checkpoint

The focused `bulk-end` trace confirms the latest blocker is a standard command
FIFO read-window phase issue, not the texture upload target selection. The run
used the current baseline and the warm f420 oracle:

```text
/tmp/gauntdl-e27b-f420-bulk-lowring.log
frameHash=0x035dcece
frameSha256=2f8a78d7a651de1a13fd98c2f9ab4275006b04a99857d1930b2f46db724ef41a
drawPackets=21375 directTriangles=6028 setupTriangles=3002 texWrites=4296625
textureMap=16754480:8367795:8386685:599296:0x000000:0x7fe444
cmdstop=invalid-standard-window/0xbda7eca1/48552/7914/0x210/0x210/0xbed9a6b6/0xc2280000/pc=0xffffffff801066c8
```

The last-writer debug now proves the final stop words were normal FIFO writes
from the texture service, not Type5-space0 backfill or anonymous stale data:

```text
w0=0x00210:v1/lg0x00000210/cur0xbda7eca1/last=fifo/seq133/lg0x00000210/addr0x00000210/val0xbda7eca1/pc0xffffffff800fe5d4
w1=0x00214:v1/lg0x00000214/cur0xbed9a6b6/last=fifo/seq134/lg0x00000214/addr0x00000214/val0xbed9a6b6/pc0xffffffff800fe5d4
```

The new bulk-window trace shows the failing phase directly. After a valid
texture batch starts at low storage, the read index remains on an older low
word while later `0xc0000205` texture batches begin far ahead:

```text
n=233 rd=0x00000020/0x00020 bulk=0x00000020-0x0001081c inside=1 word=0xc0000205
n=234 rd=0x00000000/0x00000 bulk=0x00031880-0x0000207c inside=1 word=0x000e0010
n=235 rd=0x00000210/0x00210 bulk=0x000020a0-0x0001289c inside=0 word=0xbda7eca1
n=236 rd=0x00000210/0x00210 bulk=0x000128c0-0x000230bc inside=0 word=0xbda7eca1
n=237 rd=0x00000210/0x00210 bulk=0x000230e0-0x000338dc inside=0 word=0xbda7eca1
```

That narrows the next repair attempt: preserve the standard FIFO path, but add
or probe a bulk-window/read-index rule that prevents decode from treating an
old low-ring word as the head of a fresh texture-service batch. The broad
`FIX_VOODOO_MAME_CMD_FIFO_MODEL` preset and its defer/yield variants remain
negative controls; the useful behavior is the narrow bulk/read ownership
transition around `pc=800fe5d4`.

Added an opt-in diagnostic/experiment for that exact shape:

```text
EUTHERDRIVE_GAUNTDL_EXPERIMENT_VOODOO_FIFO_BULK_RESYNC_STALE_PACKET=1
```

It only runs at command-FIFO bulk end, only when the read head is outside the
current bulk, and only when the current bulk starts with a Type5 texture packet
while the read-head packet looks stale: implausible, oversized for the bulk, or
short of a valid packet window. With
`EUTHERDRIVE_GAUNTDL_TRACE_VOODOO_CMD_FIFO_BULK_END=1`, it logs explicit
`[GAUNTDL:VOODOO-CMDFIFO-BULK-RESYNC]` lines. The f420 probe proves it catches
the target condition:

```text
oldRd=0x00000210/0x00210 newRd=0x000020a0/0x020a0
oldWord=0xbda7eca1 start=0xc0000205 last=0xbe91b046
```

But it is negative as a fix. It changes the frame and packet mix but still ends
on the same final command-FIFO stop:

```text
baseline frameHash=0x035dcece direct/setup=6028/3002 texWrites=4296625 textureMap.touched=599296
stale-resync frameHash=0x340b271c direct/setup=321/141 texWrites=6629697 textureMap.touched=812706
stale-resync cmdstop=invalid-standard-window/0xbda7eca1/.../0x210/0x210/0xbed9a6b6/...
```

So a simple "jump read head to Type5 bulk start" rule is too broad and still
does not clear the terminal `0x210` state. Keep the flag as a diagnostic and
use the resync log to find why the stale read head is recreated after the
texture-service batches, rather than promoting it.

Pause checkpoint after the next probe: the follow-up profiler showed the final
`0xbda7eca1` stop is recreated by the normal bulk-end decode at
`pc=800fe5d4`, walking from the low wrapped tail (`rd0`) to word index `0x84`.
It was not a direct register setter recreating the stale read head after the
stale-packet resync.

Added one more opt-in diagnostic to target that exact low-tail shape:

```text
EUTHERDRIVE_GAUNTDL_EXPERIMENT_VOODOO_FIFO_BULK_RESYNC_WRAP_TAIL=1
```

Build still passes for the probe project:

```text
dotnet build tools/GauntletProbe/GauntletProbe.csproj -c Release --no-restore
463 warnings, 0 errors
```

The f420 warm probe is strongly negative as a fix, but useful as a diagnostic:

```text
/tmp/gauntdl-e27b-f420-bulk-resync-wraptail.log
frameHash=0x6d791e91
frameSha256=1bbae73410456e3b595ce97970764a4bf1d2434f8f904ea72112c4031cf1a341
drawPackets=20163 directTriangles=317 setupTriangles=141 texWrites=2461873
textureMap=9415472:4329754:5085718:296960:0x000000:0x3ffffc
packetTypes=0:1017,1:26051,2:157,3:20163,4:73958,5:42946,6:48,7:103
framebuffer=640x480:307200:307200
cmdstop=invalid-standard-window/0x0005a604/4/3/0x31870/0x31870/0x00000000/0x00000800/pc=0xffffffff800fe420/last=0x0005a604:4:0x31870:0x31880/920972
cmdrd=0xC620 cmd=22862/0/8046/0xFFFFFFFC/0xFFFFFFFC cmdio=0/0/0
```

This removes the terminal `0xbda7eca1/0x210` symptom, but it over-resyncs:
texture coverage drops to `296960`, direct/setup triangles collapse to
`317/141`, and the run stops earlier/elsewhere on TMU Type4 `0x0005a604` with
only three valid words. The no-local-jump/no-command-IO tail also suggests this
prevents later control/render stream progress instead of repairing ordering.

Next work after the pause should refine the wrapped-Type5 case by
distinguishing packet head from payload tail inside the wrapped bulk. The likely
shape is tracking intra-bulk logical order or the Type5 packet byte count, not
the broad rule "read is in the low wrapped tail, so jump to the high bulk
start".

2026-07-02 payload-tail packet scan checkpoint: compared the local command FIFO
bulk-end path against current MAME `voodoo_2.cpp` command FIFO behavior. MAME's
FIFO dispatcher waits until enough words are present at the read head before
dispatching, and Type5 packet length is `2 + word_count`; the local issue is
therefore likely read-head/depth ownership around wrapped bulk writes, not the
Type5 texture copy itself.

Added trace-only packet classification for bulk-end/resync lines. With
`EUTHERDRIVE_GAUNTDL_TRACE_VOODOO_CMD_FIFO_BULK_END=1`, each line now reports
whether the current read index is outside the just-written bulk, at a packet
head, in a generic packet body, or inside Type5 data. Baseline behavior is
unchanged:

```text
/tmp/gauntdl-e27b-f420-bulk-scan.log
frameHash=0x035dcece
frameSha256=2f8a78d7a651de1a13fd98c2f9ab4275006b04a99857d1930b2f46db724ef41a
drawPackets=21375 direct/setup=6028/3002 texWrites=4296625
textureMap=16754480:8367795:8386685:599296:0x000000:0x7fe444
cmdstop=invalid-standard-window/0xbda7eca1/.../0x210/0x210/...
```

The new scan explains why the broad wrap-tail rule was destructive. Many of the
low-tail read heads are not packet heads; they are inside Type5 payload data:

```text
rd=0x000000c8 bulk=0x00000020-0x0001081c scan=type5-data:rel42/16896:pkt0:off42/66:cmd=0xc0000205:type=5
rd=0x00000000 bulk=0x00031880-0x0000207c scan=type5-data:rel14816/16896:pkt224:off32/66:cmd=0xc0000205:type=5
rd=0x00000000 bulk=0x00031910-0x0000210c scan=type5-data:rel14780/16896:pkt223:off62/66:cmd=0xc0000205:type=5
```

Two opt-in experiments confirm the shape without producing a shippable fix:

```text
EUTHERDRIVE_GAUNTDL_EXPERIMENT_VOODOO_FIFO_BULK_GATE_PAYLOAD_READ=1
frameHash=0x035dcece
drawPackets=20435 direct/setup=6044/3010 texWrites=3586225
textureMap=13912880:6842060:7070820:352640:0x000000:0x7fe444
cmdstop=invalid-standard-window/0xbda7eca1/.../0x210/0x210/...
```

Gating bulk-end decode when the read pointer is inside a Type5 payload preserves
the visible hash but drops texture throughput and does not clear the terminal
stale stop. It is useful only as a diagnostic.

```text
EUTHERDRIVE_GAUNTDL_EXPERIMENT_VOODOO_FIFO_BULK_RESYNC_PAYLOAD_TO_HEAD=1
frameHash=0x828a27b0
frameSha256=0b38c984ff8e3a9bcbf6c0d101e5ccb769b7ccc9e47720282bccf515079a6eb3
drawPackets=19950 direct/setup=939/448 texWrites=2914929
textureMap=11227696:5191649:6036047:316352:0x000000:0x48fffc
cmdstop=invalid-standard-window/0x0005a604/4/3/0x31870/0x31870/0x00000000/0x00000800/pc=0xffffffff800fe420/last=0x0005a604:4:0x31870:0x31880/785265
```

Payload-to-head resync moves the failure from the old `0xbda7eca1/0x210` stale
Type5 tail to the TMU Type4 `0x0005a604` stop, but it collapses direct/setup
throughput and texture coverage. Keep these resync/gate flags diagnostic
only. The next narrow step is a MAME-parity FIFO bookkeeping trace/fix: update
`read_index`, `depth`, `holes`, and `address_min/address_max` as one causal
state machine around wrapped writes, then dispatch only when the read head and
depth describe a complete packet. Do not promote packet dropping or payload-head
rewinding.

2026-07-02 stale-read gate checkpoint: added one more opt-in readiness probe:

```text
EUTHERDRIVE_GAUNTDL_EXPERIMENT_VOODOO_FIFO_BULK_GATE_STALE_READ=1
```

It uses the stale-packet predicate from the earlier resync experiment but does
not mutate `read_index` or `depth`; it only skips the current `bulk-end` decode
when the active bulk starts with Type5 and the current read head is outside that
bulk on an implausible/oversized/invalid packet. The f420 warm probe preserves
the baseline frame and throughput:

```text
/tmp/gauntdl-e27b-f420-bulk-gate-stale-read-trigger.log
frameHash=0x035dcece
frameSha256=2f8a78d7a651de1a13fd98c2f9ab4275006b04a99857d1930b2f46db724ef41a
drawPackets=21375 direct/setup=6028/3002 texWrites=4296625
textureMap=16754480:8367795:8386685:599296:0x000000:0x7fe444
```

The gate does catch the stale `0x210` family during `pc=800fe5d4` bulk service:

```text
reason=stale-read:implausible rd=0x00000210 bulk=0x000020a0-0x0001289c start=0xc0000205
```

But it is not a fix. The terminal stop is still the baseline stale packet, now
shown with the added decode-trigger field:

```text
cmdstop=invalid-standard-window/0xBDA7ECA1/48552/7914/0x210/0x210/.../pc=0xFFFFFFFF801066C8/trig=write/last=0xBED16E7E:1:0x20C:0x210/1359661
```

So `bulk-end` readiness is only an early symptom. The stale read pointer remains
live and the actual terminal stop is a later normal FIFO write-triggered decode.
Next slice should trace writes around `pc=801066c8` and the last decoded
`0x20c -> 0x210` transition, including last-writer metadata for storage
`0x20c/0x210`, rather than adding more broad bulk-end gates.

#### 2026-07-02 stop-on-unknown checkpoints

The first f420 warm probe used the existing unknown-packet guard:

```bash
EUTHERDRIVE_GAUNTDL_EXPERIMENT_VOODOO_MAME_FIFO_STOP_ON_UNKNOWN=1
EUTHERDRIVE_GAUNTDL_TRACE_VOODOO_CMD_FIFO_MODEL=1
EUTHERDRIVE_GAUNTDL_TRACE_VOODOO_CMD_FIFO_MODEL_COMMANDS=0xbed16e7e,0xbda7eca1
EUTHERDRIVE_GAUNTDL_TRACE_VOODOO_CMD_FIFO_MODEL_STORAGE=0x20c,0x210,0x214
EUTHERDRIVE_GAUNTDL_TRACE_VOODOO_ODD_FIFO=1
```

That result must be treated as a MAME-model no-op under the current baseline:
`EUTHERDRIVE_GAUNTDL_BRINGUP_BASELINE=1` does not enable
`EUTHERDRIVE_GAUNTDL_FIX_VOODOO_MAME_CMD_FIFO_MODEL`, and the `ODD-FIFO` trace
showed `mame=0`. It preserves the same baseline/non-flat sibling output:

```text
frameHash=0x035dcece
frameSha256=2f8a78d7a651de1a13fd98c2f9ab4275006b04a99857d1930b2f46db724ef41a
drawPackets=21375 direct/setup=6028/3002 texWrites=4296625
textureMap=16754480:8367795:8386685:599296:0x000000:0x7fe444
packetTypes=0:2973,1:27428,2:103,3:21375,4:78556,5:70997,6:81,7:102
```

And the terminal stop remains unchanged:

```text
cmdstop=invalid-standard-window/0xBDA7ECA1/48552/7914/0x210/0x210/.../pc=0xFFFFFFFF801066C8/trig=write/.../last=0xBED16E7E:1:0x20C:0x210/1359814
```

Added a separate default-off generic unknown-stop probe for the current
non-MAME command FIFO model:

```text
EUTHERDRIVE_GAUNTDL_EXPERIMENT_VOODOO_FIFO_STOP_ON_UNKNOWN=1
```

It is diagnostic only. The f420/e27b probe proves type6/type7 consumption is
phase-relevant but not itself the repair:

```text
/tmp/gauntdl-e27b-f420-generic-stop-unknown.log
frameHash=0xcabd6b3e
frameSha256=07dd051ebbe204a70311b29603d3cf07fb61a83a6ca6922c2030ffc87b8d3141
drawPackets=18997 direct/setup=1941/957 texWrites=3941041
textureMap=15332144:7572061:7760083:562752:0x000000:0x60fefc
packetTypes=0:2609,1:24819,2:55,3:18997,4:70141,5:65523,6:1,7:0
cmdstop=unknown/0xBED9A6B6/1/7919/0x1FC/0x1FC/.../pc=0xFFFFFFFF801066C8/trig=write/last=0xBF13EE93:106:0x54:0x1FC/1388873
```

So simply refusing to consume unknown packet types is not a fix either. It moves
the terminal read head away from `0x210`, but it reduces useful draw/setup work
and still parks on stale Type5-looking payload at a later word. The trace also
showed the same payload family being installed as Voodoo register values, for
example final registers `0x09a=0xbed16e7e`, `0x09b=0xbda7eca1`,
`0x09c=0xbed9a6b6`, and repeated self-register traces such as:

```text
reg-value target=0x02c reg=0x2c value=0xbed16e7e packet=0xb0000161 packetType=1 packetStart=0x0000f900 words=45057 rd=0x0000f900 pc=0xffffffff800fe5d4
```

Also re-ran the broad implausible-register stop:

```text
EUTHERDRIVE_GAUNTDL_EXPERIMENT_VOODOO_STOP_IMPLAUSIBLE_REGISTER_PACKETS=1
/tmp/gauntdl-e27b-f420-stop-implausible-register.log
frameHash=0xc6733b0d
drawPackets=20519 direct/setup=317/141 texWrites=3710513
textureMap=14410032:7193695:7216337:535680:0x000000:0x690144
cmdstop=implausible-packet/0x000E000B/1/8042/0x10/0x10/.../pc=0xFFFFFFFF801066C8/trig=write/last=0x0011000C:3:0x4:0x10/1292101
```

This is still a destructive diagnostic, not a fix. It catches earlier
implausible Type1-style packets such as `0x3c1f15f1` and prevents the terminal
`0xbda7eca1/0x210` symptom, but it collapses direct/setup work enough to rule
out a broad Type1 stop/drop policy.

Added a default-off implausible-packet trace, with optional type/command/PC
filters:

```text
EUTHERDRIVE_GAUNTDL_TRACE_VOODOO_CMD_FIFO_IMPLAUSIBLE_PACKETS=1
EUTHERDRIVE_GAUNTDL_TRACE_VOODOO_CMD_FIFO_IMPLAUSIBLE_TYPES=1
EUTHERDRIVE_GAUNTDL_TRACE_VOODOO_CMD_FIFO_IMPLAUSIBLE_COMMANDS=...
EUTHERDRIVE_GAUNTDL_TRACE_VOODOO_CMD_FIFO_IMPLAUSIBLE_PCS=...
```

The first all-types trace was visually neutral but got flooded by repeated
Type5 stale header `0xbc292a85` at `packetStart=0x14`. The filtered Type1 trace
preserves the exact f420/e27b baseline output:

```text
/tmp/gauntdl-e27b-f420-implausible-type1-trace.log
frameHash=0x035dcece
frameSha256=2f8a78d7a651de1a13fd98c2f9ab4275006b04a99857d1930b2f46db724ef41a
drawPackets=21375 direct/setup=6028/3002 texWrites=4296625
textureMap=16754480:8367795:8386685:599296:0x000000:0x7fe444
cmdstop=invalid-standard-window/0xbda7eca1/48552/7914/0x210/0x210/.../pc=0xffffffff801066c8/last=0xbed16e7e:1:0x20c:0x210/1359814
```

It gives two stronger causal handles than the generic stop/drop experiments:

```text
cmd=0xf00b0001 type=1 words=61452 target=0x000 count=61451 packetStart=0x0000be5c storage=0x0be5c lg0x000d1e5c pc0xffffffff800fe5d4
cmd=0x3e959c11 type=1 words=16022 target=0x382 count=16021 packetStart=0x00000020 storage=0x00020 amin=0xfffffffc amax=0xfffffffc pc0xffffffff800fe5d4
```

The `0xf00b0001` family lines up with BGLoadModel indexed-source metadata:
`sourceWords` shows offset `0x40=f00b0001` for the `gei` source. The later
`0x3e959c11` family appears after the command FIFO address window has collapsed
to `0xfffffffc/0xfffffffc`, with the first payload words
`0xbb7f72cf/0x04a300ac`. That points at raw model/texture payload being treated
as Type1 command headers, not at a missing generic unknown-packet rule.

Next slice should focus on write ownership for those exact Type1 families:
trace who installs `0xf00b0001`/`0x3e959c11` into command FIFO storage at
`pc=800fe5d4`, how `address_min/address_max` becomes `0xfffffffc`, and why the
later write-triggered decode still parks on `0x210`. A narrow candidate is to
gate only implausible Type1 headers that are outside the live command FIFO
address window or whose storage words come from BGLoadModel/source payload,
before considering any generic packet drop behavior.

#### 2026-07-03 implausible self-register write checkpoint

Added a default-off baseline/non-MAME experiment that keeps decoding
implausible Type1 packets but suppresses only command FIFO control-register
writes from packets that are both implausible and touch the self-register range:

```text
EUTHERDRIVE_GAUNTDL_EXPERIMENT_VOODOO_IGNORE_IMPLAUSIBLE_SELF_REG_WRITES=1
EUTHERDRIVE_GAUNTDL_EXPERIMENT_VOODOO_IGNORE_IMPLAUSIBLE_SELF_REG_WRITES_LIMIT=80
```

The previous `...SELF_REG_PACKETS` spelling is accepted as a compatibility
alias, but the current behavior is write-only. The trace marker is:

```text
[GAUNTDL:VOODOO-CMDFIFO-SELFREG-WRITE-IGNORE]
```

The first local version skipped the entire implausible self-register Type1
packet. That must not be revived as a fix: it reproduced the broad
implausible-stop collapse while still ending on the same terminal stale read:

```text
/tmp/gauntdl-e27b-f420-ignore-implausible-selfreg.log
frameHash=0x6d791e91
frameSha256=1bbae73410456e3b595ce97970764a4bf1d2434f8f904ea72112c4031cf1a341
drawPackets=21375 direct/setup=317/141 texWrites=4296625
textureMap=16754480:8367795:8386685:558752:0x000000:0x7fe444
cmdstop=invalid-standard-window/0xbda7eca1/48552/7914/0x210/0x210/.../pc=0xffffffff801066c8/last=0xbed16e7e:1:0x20c:0x210/1359814
```

The committed write-only version is neutral on the f420/e27b oracle. It proves
that command FIFO self-register writes from these implausible packets are real,
but not sufficient to explain the terminal `0xbda7eca1 @ 0x210` stop:

```text
/tmp/gauntdl-e27b-f420-ignore-implausible-selfreg-writes.log
frameHash=0x035dcece
frameSha256=2f8a78d7a651de1a13fd98c2f9ab4275006b04a99857d1930b2f46db724ef41a
drawPackets=21375 direct/setup=6028/3002 texWrites=4296625
textureMap=16754480:8367795:8386685:599296:0x000000:0x7fe444
cmdstop=invalid-standard-window/0xbda7eca1/48552/7914/0x210/0x210/.../pc=0xffffffff801066c8/last=0xbed16e7e:1:0x20c:0x210/1359814
```

This narrows the next slice: do not spend more time on broad Type1 packet drops
or self-register write suppression. The better target is packet/read ownership:
why payload words from `pc=800fe5d4` become eligible headers at `0x20`,
`0x20c`, and `0x210`, and why the standard FIFO read pointer keeps returning to
that payload window with `address_min/address_max=0xfffffffc`.

#### 2026-07-03 command FIFO read-index checkpoint

Added a default-off read-index transition trace for the command FIFO:

```text
EUTHERDRIVE_GAUNTDL_TRACE_VOODOO_CMD_FIFO_READ_INDEX=1
EUTHERDRIVE_GAUNTDL_TRACE_VOODOO_CMD_FIFO_READ_INDEX_STORAGE=...
EUTHERDRIVE_GAUNTDL_TRACE_VOODOO_CMD_FIFO_READ_INDEX_PCS=...
EUTHERDRIVE_GAUNTDL_TRACE_VOODOO_CMD_FIFO_READ_INDEX_LIMIT=...
```

The trace marker is:

```text
[GAUNTDL:VOODOO-CMDFIFO-READ]
```

A focused f420/e27b run filtered to `0x20`, `0x20c`, `0x210`, and `0x214`
is visually and behaviorally neutral:

```text
/tmp/gauntdl-e27b-f420-read-index-trace.log
frameHash=0x035dcece
frameSha256=2f8a78d7a651de1a13fd98c2f9ab4275006b04a99857d1930b2f46db724ef41a
drawPackets=21375 directTriangles=6028 setupTriangles=3002 texWrites=4296625
textureMap=16754480:8367795:8386685:599296:0x000000:0x7fe444
cmdstop=invalid-standard-window/0xbda7eca1/48552/7914/0x210/0x210/0xbed9a6b6/0xc2280000/pc=0xffffffff801066c8/last=0xbed16e7e:1:0x20c:0x210/1359814
```

The new evidence is that the terminal `0x210` stop is not caused by an explicit
resync to that address. During `bulk-end`, normal packet advancement consumes
through a chain of one-word payload-looking packets while
`address_min/address_max` are already collapsed:

```text
reason=packet-advance old=0x00000208/0x00208 new=0x0000020c/0x0020c oldWord=0x00000000 newWord=0xbed16e7e trigger=bulk-end command=0x00000000:1 pc=0xffffffff800fe5d4
reason=packet-advance old=0x0000020c/0x0020c new=0x00000210/0x00210 oldWord=0xbed16e7e newWord=0xbda7eca1 trigger=bulk-end command=0xbed16e7e:1 pc=0xffffffff800fe5d4
```

Immediately after landing on `0xbda7eca1`, Type1 handling writes the command
FIFO read pointer register and rewinds the local read index to zero:

```text
reason=reg-rdptr old=0x00000210/0x00210 new=0x00000000/0x00000 oldWord=0xbda7eca1 newWord=0x000e0010 trigger=bulk-end current=0xbda7eca1:48552:0x00000210 pc=0xffffffff800fe5d4
```

The same trace also confirms the final payload words were installed by the same
bulk writer:

```text
0x20c: last=fifo/seq132/lg0x0000020c/addr0x0000020c/val0xbed16e7e/pc0xffffffff800fe5d4
0x210: last=fifo/seq133/lg0x00000210/addr0x00000210/val0xbda7eca1/pc0xffffffff800fe5d4
```

This rules out self-register write suppression as the next primary fix target.
The better experiment is to stop bulk-end decode from treating payload-owned
words as new packet headers after the command FIFO address window collapses,
without applying a broad Type1 drop. A useful narrow next probe is a
default-off gate or trace that fires only when:

```text
trigger=bulk-end
address_min=address_max=0xfffffffc
packet start is in recently bulk-written payload storage
decoded wordsNeeded is one for payload-looking Type0/Type1 chains
```

The expected safe shape is to pause/stop that local bulk-end decode window
before it consumes into `0x20c/0x210`, then verify the f420/e27b visual oracle
and direct/setup counters do not collapse.

#### 2026-07-03 collapsed payload-chain gate checkpoint

Added a default-off experiment for the collapsed-address-window payload case:

```text
EUTHERDRIVE_GAUNTDL_EXPERIMENT_VOODOO_FIFO_BULK_GATE_COLLAPSED_PAYLOAD_CHAIN=1
```

The first form was intentionally narrow: only `bulk-end`, only
`address_min/address_max=0xfffffffc`, only current bulk-written FIFO storage,
and only one-word payload-looking chains. It is not enough to fix the terminal
stop, but it is useful evidence because it changes the command/texture workload
without changing the selected f420 image:

```text
/tmp/gauntdl-e27b-f420-collapsed-gate-on-clean.log
frameHash=0x035dcece
frameSha256=2f8a78d7a651de1a13fd98c2f9ab4275006b04a99857d1930b2f46db724ef41a
drawPackets=20869 directTriangles=6044 setupTriangles=3010 texWrites=4031217
textureMap=15692848:7839270:7853578:591232:0x000000:0x7fe444
cmdstop=invalid-standard-window/0xbda7eca1/48552/7914/0x210/0x210/.../pc=0xffffffff801066c8
```

The broader form also gates implausible oversized Type1 packets after the
address window has collapsed, including the later `write` decode path. That
does catch the terminal packet:

```text
/tmp/gauntdl-e27b-f420-collapsed-gate-write-type1-on.log
frameHash=0x828a27b0
frameSha256=0b38c984ff8e3a9bcbf6c0d101e5ccb769b7ccc9e47720282bccf515079a6eb3
drawPackets=19986 directTriangles=564 setupTriangles=262 texWrites=3456241
textureMap=13392944:6711424:6681520:567296:0x000000:0x7fe444
cmdstop=bulk-collapsed-payload-chain/0xbda7eca1/48552/7914/0x210/0x210/.../pc=0xffffffff801066c8
```

This broader form is also destructive. It changes the visual family and
collapses real direct/setup work, so it must stay an experiment and must not be
promoted as a fix. The useful conclusion is narrower: by the time the `write`
decode sees `0xbda7eca1 @ 0x210`, the emulator has already lost enough FIFO
ownership/order information that simply stopping implausible Type1 packets is
too late and too blunt.

Next work should move earlier than the `write` stop. The strongest next target
is the producer/consumer boundary around `pc=800fe5d4`: trace the Type5 packet
head, payload body, and read pointer as one unit and determine why the payload
tail remains eligible command storage after the bulk transfer. A safer fix is
likely to preserve packet body ownership or defer decode at the source boundary,
not to suppress the eventual oversized Type1 header after it has already become
the read pointer.

1. Continue from the Type5 upload-link truth source. For target `0x2180`, use
   `[GAUNTDL:TEXUPLOAD-LINK]` to pair `packetSource`, `fifoLow`, and Type5
   `packet` before reasoning about the source. The currently matched early
   `0x2180` payload comes from `80316ca4` with raw
   `0x00000000/0x01d90000/...`, not from the `802e6f68` GEI+0x3850 payload. The
   transport path into texture memory is now proven: `packet=0x24388` writes
   `targetStart=0x2180` into bucket `0x02f000` under `mode=0`, `tlod=0x800`,
   `tbase=0x55a0`, `bpp=1`, `seq8=1`. The expanded texture-write trace proves
   this comes from selected `tmu0=00000000/00000800/000055A0`, not from global
   texture registers, while `tmu1` carries a different texture state. The last
   writer is now known: `pc=800fe428` emits Type4 `cmd=0x00059604` to
   `target=0x2c1/0x2c3`, setting TMU0 to `lod=0x800`, `base=0x55a0` before
   `pc=800fe5d4` consumes the Type5 payload. Next work should compare our
   command FIFO source chain for `0x00059604` and the following Type5 upload,
   then trace why `rd=0x210` is recreated after the stale-packet resync catches
   it at `pc=800fe5d4`.
   Do not keep using raw `TRACE_MEM` for emulator-side BGLoadModel hydration;
   use `TRACE_BGLOADMODEL_INDEXED_SOURCE_HYDRATION` or explicit upload/decode
   traces.
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

#### 2026-07-04 source-chain trace checkpoint

Added a default-off bulk-end source-chain trace:

```text
EUTHERDRIVE_GAUNTDL_TRACE_VOODOO_CMD_FIFO_SOURCE_CHAIN=1
EUTHERDRIVE_GAUNTDL_TRACE_VOODOO_CMD_FIFO_SOURCE_CHAIN_PCS=...
EUTHERDRIVE_GAUNTDL_TRACE_VOODOO_CMD_FIFO_SOURCE_CHAIN_STORAGE=...
EUTHERDRIVE_GAUNTDL_TRACE_VOODOO_CMD_FIFO_SOURCE_CHAIN_LIMIT=...
EUTHERDRIVE_GAUNTDL_TRACE_VOODOO_CMD_FIFO_SOURCE_CHAIN_PACKETS=...
```

The trace marker is:

```text
[GAUNTDL:VOODOO-CMDFIFO-SOURCE]
```

It fires at `EndCommandFifoBulkWrite()` before the bulk-end decoder mutates the
read pointer. Each row records the current read word, bulk write window, packet
head scan, selected storage offsets, nearby read window, and last-writer chain.
This is trace-only and default-off.

Focused e27b/f420 baseline run:

```text
/tmp/gauntdl-e27b-f420-source-chain.log
frameHash=0x035dcece
frameSha256=2f8a78d7a651de1a13fd98c2f9ab4275006b04a99857d1930b2f46db724ef41a
drawPackets=21375 directTriangles=6028 setupTriangles=3002 texWrites=4296625
cmdstop=invalid-standard-window/0xbda7eca1/48552/7914/0x210/0x210/0xbed9a6b6/0xc2280000/pc=0xffffffff801066c8/last=0xbed16e7e:1:0x20c:0x210/1359814
```

Key source-chain evidence:

```text
n=137 rd=0x00000210/0x00210 word=0xbda7eca1 type=1 words=48552
bulk=0x000020a0-0x0001289c bulkWords=16896 inside=0
lastDecoded=0xbed16e7e:1:0x0000020c->0x00000210
position=scan=outside:rel63580/16896
heads=p0@0x000020a0/0x020a0:cmd=0xc0000205:t=5:w=66:v=8/8...
0x0020c=cur0xbed16e7e/last=fifo/seq132/lg0x0000020c/addr0x0000020c/pc0xffffffff800fe5d4
0x00210=cur0xbda7eca1/last=fifo/seq133/lg0x00000210/addr0x00000210/pc0xffffffff800fe5d4
```

The source-chain trace confirms that `0xbda7eca1 @ 0x210` is stale payload
storage from the same `pc=800fe5d4` bulk writer, not a new command head. It also
shows that the current bulk upload has valid Type5 heads starting at `0x20a0`,
while the read pointer remains outside that bulk on the old payload word.

Negative probe: existing stale-packet resync is too broad.

```text
/tmp/gauntdl-e27b-f420-stale-resync.log
EUTHERDRIVE_GAUNTDL_EXPERIMENT_VOODOO_FIFO_BULK_RESYNC_STALE_PACKET=1
frameHash=0x340b271c
drawPackets=25034 directTriangles=321 setupTriangles=141 texWrites=6629697
cmdstop=invalid-standard-window/0xbda7eca1/48552/7914/0x210/0x210/...
```

It catches the exact terminal symptom several times:

```text
kind=stale-packet reason=implausible oldRd=0x00000210/0x00210 newRd=0x000020a0/0x020a0
oldWord=0xbda7eca1 start=0xc0000205
```

But it also over-resyncs many earlier oversized/invalid payload-looking reads,
collapses direct/setup work, changes the visual family, and still terminates at
the same `0xbda7eca1` stop. Keep it as a negative oracle.

Current MAME `src/devices/video/voodoo_2.cpp` / `voodoo_2.h` comparison point:
their `command_fifo::write()` updates `address_min`, `address_max`, `depth`, and
`holes` as one unit; `execute_if_ready()` only checks `depth >=
words_needed(peek_next())`; `read_next()` simply consumes from linear
`m_read_index`. There is no separate per-word valid/invalidation model like our
diagnostic standard path. The next useful fix should therefore move toward
packet body ownership/depth semantics at the producer boundary, not toward more
packet dropping after stale payload has become the read pointer.

Next continuation point:

1. Add or refine tracing around the exact `pc=800fe5d4` Type5 upload boundary so
   one source-chain row can show old read pointer, new Type5 head, and
   depth/holes/address-window changes before and after the first payload word.
2. Avoid broad resync/gate experiments unless the f420 oracle stays near
   `frameHash=0x035dcece` and `direct/setup ~= 6028/3002`.
3. Compare a narrow MAME-style depth/hole update against the standard path for
   the `0x20c -> 0x210` transition before promoting any behavior flag.

#### 2026-07-04 producer-boundary checkpoint

Added a default-off producer-boundary trace:

```text
EUTHERDRIVE_GAUNTDL_TRACE_VOODOO_CMD_FIFO_PRODUCER_BOUNDARY=1
EUTHERDRIVE_GAUNTDL_TRACE_VOODOO_CMD_FIFO_PRODUCER_BOUNDARY_PCS=...
EUTHERDRIVE_GAUNTDL_TRACE_VOODOO_CMD_FIFO_PRODUCER_BOUNDARY_STORAGE=...
EUTHERDRIVE_GAUNTDL_TRACE_VOODOO_CMD_FIFO_PRODUCER_BOUNDARY_LIMIT=...
EUTHERDRIVE_GAUNTDL_TRACE_VOODOO_CMD_FIFO_PRODUCER_BOUNDARY_WORDS=...
```

The marker is:

```text
[GAUNTDL:VOODOO-CMDFIFO-PRODUCER]
```

It records the begin/end read pointer, depth, holes, valid count, address
window, current bulk packet heads, selected storage offsets, and the first words
written by the producer before the bulk-end decoder gets another chance to move
the read pointer. The trace is default-off and preserves the current f420 oracle.

Focused trace-only run:

```text
/tmp/gauntdl-e27b-f420-producer-boundary.log
frameHash=0x035dcece
frameSha256=2f8a78d7a651de1a13fd98c2f9ab4275006b04a99857d1930b2f46db724ef41a
drawPackets=21375 directTriangles=6028 setupTriangles=3002 texWrites=4296625
textureMap=16754480:8367795:8386685:599296:0x000000:0x7fe444
cmdstop=invalid-standard-window/0xbda7eca1/48552/7914/0x210/0x210/.../pc=0xffffffff801066c8/last=0xbed16e7e:1:0x20c:0x210/1359814
```

Key evidence:

```text
n=112 bulk=0x00031880-0x0000207c rd=0x00000000
position=type5-data:rel14816/16896:pkt224:off32/66
0x0020c=cur0xbed16e7e/last=fifo/.../pc0xffffffff800fe5d4
0x00210=cur0xbda7eca1/last=fifo/.../pc0xffffffff800fe5d4

n=113 beginRd=0x00000210 beginWord=0xbda7eca1
bulk=0x000020a0-0x0001289c inside=0
```

This proves the terminal `0xbda7eca1 @ 0x210` state is already stale before the
next Type5 producer writes its valid heads at `0x20a0`. The bug is not the next
producer head; it is the prior consumer/decode path that advances from
`0x20c -> 0x210` into Type5 payload storage and leaves that payload eligible as
the next command.

Two default-off behavior probes were added and rejected as negative controls:

```text
EUTHERDRIVE_GAUNTDL_EXPERIMENT_VOODOO_FIFO_BULK_ADVANCE_PAYLOAD_TO_NEXT_HEAD=1
/tmp/gauntdl-e27b-f420-payload-next-head.log
frameHash=0x6d791e91
drawPackets=26134 directTriangles=317 setupTriangles=141 texWrites=7048753
cmdstop=invalid-standard-window/0x00012609/.../0x7db0/...

EUTHERDRIVE_GAUNTDL_EXPERIMENT_VOODOO_FIFO_BULK_INVALIDATE_PAYLOAD_REMAINDER=1
/tmp/gauntdl-e27b-f420-payload-invalidate.log
frameHash=0x6d791e91
drawPackets=18405 directTriangles=317 setupTriangles=141 texWrites=2126321
cmdstop=invalid-standard-window/0x0005a604/.../0x0/...
```

Both probes trigger on the target wrapped Type5 payload window, but both crush
real direct/setup work and switch to the same wrong visual family. Do not promote
either behavior. They are useful only as proof that moving or invalidating the
read pointer after it is already inside payload is still the wrong boundary.

Next continuation point:

1. Keep the producer-boundary trace as the main truth source for the
   `pc=800fe5d4` / `0x20c -> 0x210` transition.
2. Stop iterating on payload-head jumps or payload invalidation unless an oracle
   run stays near `frameHash=0x035dcece` and `direct/setup ~= 6028/3002`.
3. Move to a narrow MAME-style FIFO ownership fix: update or trace
   `address_min`, `address_max`, `depth`, `holes`, and linear `read_index` as a
   single causal unit across the wrapped Type5 bulk, so the read side never
   starts decoding inside the previous payload body.

#### 2026-07-04 texture-triangle sample summary checkpoint

Added a default-off per-textured-triangle sample summary trace:

```text
EUTHERDRIVE_GAUNTDL_TRACE_VOODOO_TEXTURE_TRIANGLE_SAMPLE_SUMMARY=1
EUTHERDRIVE_GAUNTDL_TRACE_VOODOO_TEXTURE_TRIANGLE_SAMPLE_SUMMARY_LIMIT=...
```

The marker is:

```text
[GAUNTDL:VOODOO-TEXSUMMARY]
```

The trace records the triangle bbox, covered/zero sample counts, texture mode,
LOD, target size, resolved texture base, sampled byte-address range, dominant
raw texel words, dominant RGB565 results, address buckets, setup/fbz state,
current FIFO command, and CPU PC. It is default-off and preserves the current
f420 oracle.

Focused trace-only run:

```text
/tmp/gauntdl-texsummary-f420.log
frameHash=0x035dcece
frameSha256=2f8a78d7a651de1a13fd98c2f9ab4275006b04a99857d1930b2f46db724ef41a
drawPackets=21375 directTriangles=6028 setupTriangles=3002 texWrites=4296625
```

The first large green surface is a setup triangle, not direct stale overdraw:

```text
cmd=0x0180A8CB pc=0xffffffff800c4e5c setup=0x0006002A
mode=0x8C24100F lod=0x000020C6 fmt=0 b16=0 size=256x256
regbase=0x00005D82 base=0x02F120
xy=(0,-16231)/(49076,382)/(0,382)
addr=0x02F000:218240 addrs=0x02F120-0x02F426
raw=0x0000:136741,0x000D:14065,0x0054:14065,0x00C6:14065
rgb=0x0000:59522,0x0001:7136,0x0002:4873,0x0003:3621
```

Other early rows repeat the same pattern for neighboring huge setup triangles.
This makes the visible green plane a concrete texture-source problem: the
setup rasterizer samples bucket `0x02F000`, with a resolved base around
`0x02F120`, and interprets the contents as RGB332.

Type3 packet tracing confirms the huge coordinates are in the packet body, not
an obvious field-order parse bug:

```text
/tmp/gauntdl-type3-hot-f420.log
frameHash=0x035dcece
cmd=0x0180a8cb words=19 count=3 code=1 flags=0x602a pc=800c4e5c
x=0 y=0xc67d9c00(-16231) x=0x473fb400(49076) y=0x43bf0000(382)
```

Texture-write bucket tracing then links the hot texture bytes to Type5 uploads
from `pc=800fe5d4`:

```text
/tmp/gauntdl-texwrite-02f000-f420.log
bucket=0x02F000 word=0x002180 addr=0x02F000 value=0x00008042
cmd=0xC0000205 packet=0x24388 rd=0x24388 pc=0xffffffff800fe5d4
mode=0x00000000 tlod=0x00000800 tbase=0x000055A0 bpp=1 seq8=1
```

The written values are byte-swapped float-like words such as `0x42800000`,
`0x43000000`, and `0x3ECD8DBE` landing in 8-bit texture lanes. That explains
why the first real setup triangles become a flat green/noisy plane: model or
geometry-looking payload is currently being accepted as texture data for the
bucket those triangles sample.

Negative visual control:

```text
EUTHERDRIVE_GAUNTDL_FIX_VOODOO_TEXTURE_SAMPLE_BASE_BIAS=0
/tmp/gauntdl-bias0-f420.log
frameHash=0x035dcece
```

Removing the `0x510` sample-base bias is neutral or worse. It keeps the same
hash family and increases zero textured samples, so it is not the visible
graphics fix.

Available warm-state check:

```text
/tmp/eutherdrive-gauntlet-probe/73c41842-f180.warm
/tmp/gauntdl-current-73cwarm-f420.log
frameHash=0x44d3a578
frameSha256=df2d3c5b979cfaa956134fd7e3cd7ab4c891e04e96bb85443299cf354eb52dee
drawPackets=24639 directTriangles=5471 setupTriangles=1198 texWrites=6254771
```

The rendered frame is still a full green plane with only a thin line near the
top, so the available `73c41842` warm snapshot is not a usable replacement for
the older missing scene-like `446392c984c8` oracle.

Next continuation point:

1. Treat the current visible failure as a texture-upload/source ownership bug,
   not a color-combine-only issue and not a PPM dump issue.
2. Trace the final producer/source path for the hot `0x02F000` writes from
   `pc=800fe5d4`, including the origin of the float-like payload before it is
   committed to texture RAM.
3. Keep the earlier FIFO producer-boundary evidence in scope, because the same
   Type5 path has already shown stale-payload command decoding at the
   `0x20c -> 0x210` boundary.
4. Revisit triangle depth/alpha only after the texture-source path is either
   fixed or proven correct; MAME has real depth/alpha/stipple handling, while
   the current bringup rasterizer still only applies a simplified color path.
