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

#### 2026-07-04 render-state/direct-payload visual breakthrough

Added several default-off diagnostics around the f420 plateau. The important
new flags are:

```text
EUTHERDRIVE_GAUNTDL_EXPERIMENT_VOODOO_IGNORE_IMPLAUSIBLE_RENDER_STATE_WRITES=1
EUTHERDRIVE_GAUNTDL_EXPERIMENT_VOODOO_SUPPRESS_IMPLAUSIBLE_BULK_DIRECT_TRIANGLES=1
EUTHERDRIVE_GAUNTDL_EXPERIMENT_VOODOO_DISABLE_TRIANGLE_WIRE_EDGES=1
EUTHERDRIVE_GAUNTDL_EXPERIMENT_VOODOO_SUPPRESS_IMPLAUSIBLE_SETUP_TRIANGLES=1
EUTHERDRIVE_GAUNTDL_EXPERIMENT_VOODOO_SETUP_MAME_AUX_DEPTH=1
EUTHERDRIVE_GAUNTDL_TRACE_VOODOO_LFB_DETAIL=1
```

`IGNORE_IMPLAUSIBLE_RENDER_STATE_WRITES` is the strongest non-destructive state
fix so far. It ignores render/TMU state writes only when the current Type1
packet is already implausible by the command FIFO model. This prevents payload
words like `0x437f0000` from clobbering `fbzMode`, `lfbMode`, texture state, and
global color state while preserving the draw count:

```text
/tmp/gauntdl-ignore-globalstate-f420.log
frameHash=0x0f55e72a
drawPackets=21375 directTriangles=6025 setupTriangles=3001
fbz=0x00000460 lfbm=0x03880000
```

It is still not the final picture: the selected buffer remains a false
cyan/yellow/noisy wedge. Buffer dumps show the selected frame is effectively
buffer 1; buffer 0 is the stripe surface and buffer 2 is a cyan/red fill.

Negative/neutral controls:

```text
EUTHERDRIVE_GAUNTDL_EXPERIMENT_VOODOO_SETUP_MAME_AUX_DEPTH=1
frameHash=0x0f55e72a

EUTHERDRIVE_GAUNTDL_EXPERIMENT_VOODOO_SUPPRESS_IMPLAUSIBLE_SETUP_TRIANGLES=1
frameHash=0x0f55e72a
```

The setup suppressor correctly catches the huge `pc=800c4e5c`,
`cmd=0x0180A8CB` setup triangles, but the selected f420 frame is unchanged.
Likewise, the MAME-style setup aux/depth experiment changes raster accounting
but not the selected image. So the visible f420 wedge is not dominated by those
setup texture triangles.

Solid-triangle profiling found the visible buffer-1 culprit: huge direct
`itri` fills are decoded from implausible Type1 payloads, mostly from
`pc=800fe5d4` and `pc=80106a74`:

```text
[GAUNTDL:VOODOO-LARGE-ITRI]
pc=0xffffffff800fe5d4 cmd=0x3E959C11 words=16022 trigger=bulk-end
box=0-640x0-480 fbz=0x00000000 lfb=0x03880000

[GAUNTDL:VOODOO-LARGE-ITRI]
pc=0xffffffff80106a74 cmd=0x3E9EC751 words=16031 trigger=write
box=0-640x41-645 fbz=0x00000460 lfb=0x3F800000
```

The existing `SUPPRESS_IMPLAUSIBLE_BULK_DIRECT_TRIANGLES` gate only handled the
`bulk-end` shape. It now also suppresses large `itri/ftri` raster when
`IsImplausibleCommandFifoPacket()` is true, regardless of the decode trigger.
That is the first test that produced a real visible movement on the current
f420 plateau:

```text
EUTHERDRIVE_GAUNTDL_EXPERIMENT_VOODOO_IGNORE_IMPLAUSIBLE_RENDER_STATE_WRITES=1
EUTHERDRIVE_GAUNTDL_EXPERIMENT_VOODOO_SUPPRESS_IMPLAUSIBLE_BULK_DIRECT_TRIANGLES=1

/tmp/gauntdl-directsuppress-renderstate-f420.png
frameHash=0x96f9e8f3
frameSha256=e971945b43864db3e4e5714965af301ce9dac51b1a5e43d24eb7d3d565f0fed6
lfbWrites=231912849
rast=9641199/102203530/0/12854/1512/11342/49/10315/971/44526238
rb=59055433/52789296/0
framebuffer=640x480:305614:166673
```

Visually this removes the old static full-screen false wedge and exposes a much
more informative, still incorrect scene: white/fill surfaces, texture/noise band,
and remaining false color polygons. This is progress toward real visible
graphics, but not yet gameplay-correct output.

`DISABLE_TRIANGLE_WIRE_EDGES` removes diagnostic edge overlay from
`DrawTriangleWire()` and makes the same result easier to read:

```text
/tmp/gauntdl-directsuppress-noedges-renderstate-f420.png
frameHash=0x971ff26b
framebuffer=640x480:305614:143532
```

This is a display/debug aid, not a core fix. A stricter experiment that
suppressed all direct `itri/ftri` from implausible Type1 payloads over-suppressed
the frame and caused selection to fall back to the buffer-0 stripe surface:

```text
/tmp/gauntdl-directsuppress-all-noedges-renderstate-f420.png
frameHash=0xa3750074
solidRaster=161696
```

That stricter variant was reverted. Keep the large-triangle gate as the useful
default-off diagnostic.

LFB detail tracing also showed no direct LFB-aperture writes after loading the
current f180 warm state in the early f220 window, so the large `lfbWrites`
counters here are renderer/raster accounting, not fresh guest LFB writes.

Build verification:

```text
dotnet build tools/GauntletProbe/GauntletProbe.csproj -c Release --no-restore /clp:ErrorsOnly
Build succeeded, 343 warnings, 0 errors
```

Next continuation point:

1. Keep the useful visual stack as an opt-in oracle:
   `IGNORE_IMPLAUSIBLE_RENDER_STATE_WRITES=1` +
   `SUPPRESS_IMPLAUSIBLE_BULK_DIRECT_TRIANGLES=1`, with optional
   `DISABLE_TRIANGLE_WIRE_EDGES=1` for screenshot readability.
2. Do not promote any of these flags into baseline yet. They are isolating the
   payload corruption, not fixing FIFO ownership.
3. Next narrow blocker is texture/setup correctness on buffer 1. At f420 the
   useful visual stack still has `textured=12854 covered=1512 rejected=11342`
   and `zero=44526238`, so the remaining image is dominated by bad texture
   fetch/source data and rejected setup triangles.
4. Continue by tracing buffer-1 texture samples after the direct-payload
   suppressor, especially the path from `pc=800fe5d4` Type5 writes into the
   sampled buckets, before changing color combine or display-buffer selection.

#### 2026-07-04 post-commit texture follow-up

After commit `a01c2994`, a texture-summary run using the useful visual stack:

```text
EUTHERDRIVE_GAUNTDL_EXPERIMENT_VOODOO_IGNORE_IMPLAUSIBLE_RENDER_STATE_WRITES=1
EUTHERDRIVE_GAUNTDL_EXPERIMENT_VOODOO_SUPPRESS_IMPLAUSIBLE_BULK_DIRECT_TRIANGLES=1
EUTHERDRIVE_GAUNTDL_EXPERIMENT_VOODOO_DISABLE_TRIANGLE_WIRE_EDGES=1
EUTHERDRIVE_GAUNTDL_TRACE_VOODOO_TEXTURE_TRIANGLE_SAMPLE_SUMMARY=1
```

reproduced the readable image:

```text
/tmp/gauntdl-directsuppress-texsummary-f420.log
frameHash=0x971ff26b
frameSha256=f32f2de2dcaf3a27ca1f90d19b5906506ac89873ed955f1c3b78ac6eb424b055
```

The first 60 texture summaries are still the known huge setup triangles from
`pc=800c4e5c`, `cmd=0x0180A8CB`, sampling bucket `0x02F000` with
`fbz=0x437F0000`. However, stacking setup suppression on top of the useful
visual stack was visually neutral:

```text
EUTHERDRIVE_GAUNTDL_EXPERIMENT_VOODOO_SUPPRESS_IMPLAUSIBLE_SETUP_TRIANGLES=1
/tmp/gauntdl-direct-setup-suppress-noedges-f420.ppm
frameHash=0x971ff26b
lfbWrites=173915477
textured=907:covered:693:rejected:214
rb=2432226/52789296/0
```

So the hot setup triangles are still real bad work, but after direct-payload
suppress they do not determine the selected buffer-1 image. The next trace needs
to include the destination buffer index in `VOODOO-TEXSUMMARY` or otherwise
filter texture summaries to the selected/rendered buffer. Without that, the
trace keeps reporting buffer-0 setup noise while the visible f420 frame is
selected from buffer 1.

#### 2026-07-04 buffer-filtered texture summary checkpoint

`VOODOO-TEXSUMMARY` now reports the destination color buffer and can be filtered
with:

```text
EUTHERDRIVE_GAUNTDL_TRACE_VOODOO_TEXTURE_TRIANGLE_SAMPLE_SUMMARY_BUFFERS=1
```

The buffer-1 filtered run used the current useful visual stack:

```text
EUTHERDRIVE_GAUNTDL_EXPERIMENT_VOODOO_IGNORE_IMPLAUSIBLE_RENDER_STATE_WRITES=1
EUTHERDRIVE_GAUNTDL_EXPERIMENT_VOODOO_SUPPRESS_IMPLAUSIBLE_BULK_DIRECT_TRIANGLES=1
EUTHERDRIVE_GAUNTDL_EXPERIMENT_VOODOO_DISABLE_TRIANGLE_WIRE_EDGES=1
```

and stayed on the same readable visual family:

```text
/tmp/gauntdl-buf1-texsummary-f420.log
frameHash=0x971ff26b
frameSha256=f32f2de2dcaf3a27ca1f90d19b5906506ac89873ed955f1c3b78ac6eb424b055
framebuffer=640x480:305614:143532
rb=59055433/52789296/0 rlast=1
```

The first buffer-1 summaries are not the earlier huge buffer-0 coordinates.
They are repeated full-ish quads on the active rendered buffer:

```text
pc=0xffffffff800c4e5c cmd=0x0180A8CB:19
buf=1 front=1 back=0 rbuf=1
bbox=(0,0)-(512,383) pixels=98303 zero=28317
mode=0x8C24100F lod=0x00002000 targetLod=0 fmt=0 b16=0
regbase=0x00000000 base=0x000510 addrs=0x000510-0x010310
raw=0x0000:38203,0x003E:10060,0x00BE:8250,0x0042:5530
rgb=0x0000:28317,0x27F5:2550,0x0005:1569,0x0002:1289
```

The opposite half of the same quad samples the high end of that same 64 KiB
span and is also buffer 1:

```text
pc=0xffffffff800c4e5c cmd=0x0180A8CB:19
buf=1 front=1 back=0 rbuf=1
bbox=(0,0)-(512,383) pixels=98001
raw=0x003E:27671,0x00BE:22637,0x00BF:12318,0x0000:11520
addr=0x00F000:27137,0x00E000:24269,0x00D000:21401,0x00C000:18533
```

One later buffer-1 triangle still points at the previously suspicious payload
producer:

```text
pc=0xffffffff800fe5d4 cmd=0x3DB1FAD1:15794
buf=1 mode=0x00000000 lod=0x00000800 pixels=861
raw=0x00E6:861 rgb=0xF935:861
```

So the selected f420 buffer is no longer hidden behind buffer-0 setup noise.
It is dominated by buffer-1 texture work that samples low texture memory as
RGB332 from `pc=800c4e5c`, plus a smaller amount of payload-looking work from
`pc=800fe5d4`. The next branch should compare the active texture
mode/lod/base/TMU source against MAME and rerun the old sample-base/layout
controls under this useful visual stack before changing color combine or buffer
selection again.

#### 2026-07-04 texture layout and TMU-source controls

MAME reference points used for this slice:

- `src/devices/video/voodoo.cpp`: `internal_texture_w()` selects the TMU from
  texture-space address bits, reads `textureMode/tLOD` from that TMU, applies
  `tdata_swizzle/tdata_swap`, then writes little-endian bytes.
- `src/devices/video/voodoo_render.cpp`: `rasterizer_texture::recompute()`
  derives `m_lodoffset[]` from `texBaseAddr`, `tLOD`, LOD split, aspect, bpp,
  and optional multibase registers; `write_ptr()` aligns `scale * offs` to a
  32-bit texture write.
- `src/devices/video/voodoo_regs.h`: `textureMode` bits 0..7 drive
  perspective/filter/clamp/format flags, while `tLOD` bits 0..31 provide
  `lod_min`, `lod_max`, bias, split/aspect, multibase, swizzle, swap, and magic.

Primary source links:

```text
https://github.com/mamedev/mame/blob/master/src/devices/video/voodoo.cpp
https://github.com/mamedev/mame/blob/master/src/devices/video/voodoo_render.cpp
https://github.com/mamedev/mame/blob/master/src/devices/video/voodoo_regs.h
```

The visible-stack layout controls were rerun at f420:

```text
common:
EUTHERDRIVE_GAUNTDL_EXPERIMENT_VOODOO_IGNORE_IMPLAUSIBLE_RENDER_STATE_WRITES=1
EUTHERDRIVE_GAUNTDL_EXPERIMENT_VOODOO_SUPPRESS_IMPLAUSIBLE_BULK_DIRECT_TRIANGLES=1
EUTHERDRIVE_GAUNTDL_EXPERIMENT_VOODOO_DISABLE_TRIANGLE_WIRE_EDGES=1
```

Results:

```text
baseline useful stack
frameHash=0x971ff26b
framebuffer=640x480:305614:143532

EUTHERDRIVE_GAUNTDL_FIX_VOODOO_TEXTURE_SAMPLE_BASE_BIAS=0
frameHash=0x308f14bc
framebuffer=640x480:305614:143532

EUTHERDRIVE_GAUNTDL_EXPERIMENT_VOODOO_MAME_TEXTURE_FETCH_ADDRESSING=1
frameHash=0x564c312a
framebuffer=640x480:307200:173936

MAME_TEXTURE_FETCH_ADDRESSING=1 + TEXTURE_SAMPLE_BASE_BIAS=0
frameHash=0x12496e15
framebuffer=640x480:307200:175980

EUTHERDRIVE_GAUNTDL_FIX_VOODOO_TMU_REG_BANKS=1
EUTHERDRIVE_GAUNTDL_EXPERIMENT_VOODOO_TEXTURE_UPLOAD_TMU_BANKS=1
frameHash=0xb5925ef8
framebuffer=640x480:305614:143532

EUTHERDRIVE_GAUNTDL_FIX_VOODOO_TMU_REG_BANKS=1
EUTHERDRIVE_GAUNTDL_EXPERIMENT_VOODOO_TEXTURE_SAMPLE_TMU=1
frameHash=0xb5925ef8
framebuffer=640x480:305614:143532
```

Visual result:

- `TEXTURE_SAMPLE_BASE_BIAS=0` changes the active buffer-1 texture fetch, but
  still produces the same false large colored polygon family.
- MAME fetch-layout, with or without the historical `0x510` sample bias,
  collapses into horizontal texture bands rather than a scene.
- TMU bank tracking and forced sample TMU1 also stay in the false polygon/stripe
  family.

The code now adds a diagnostic-only sample-source override:

```text
EUTHERDRIVE_GAUNTDL_EXPERIMENT_VOODOO_TEXTURE_SAMPLE_TMU=0
EUTHERDRIVE_GAUNTDL_EXPERIMENT_VOODOO_TEXTURE_SAMPLE_TMU=1
```

and `VOODOO-TEXSUMMARY` prints `tsrc=...` so future buffer-filtered traces show
whether the sample came from global state, TMU0, or TMU1. This is not a promoted
rendering fix.

Conclusion: the current visible failure is not primarily caused by MAME LOD/base
layout, sample-bias, or simple TMU source selection. The productive next branch
is back upstream: why the command FIFO/payload path still produces the
buffer-1 full-ish quads from `pc=800c4e5c` and the smaller payload-like triangle
from `pc=800fe5d4`.

#### 2026-07-04 solid-triangle profile after texture controls

`EUTHERDRIVE_GAUNTDL_PROFILE_VOODOO_SOLID_TRIANGLES=1` on the useful visual
stack preserved the readable visual hash:

```text
/tmp/gauntdl-directsuppress-noedges-solidprofile-f420.ppm
frameHash=0x971ff26b
solidRaster=9641199
framebuffer=640x480:305614:143532
```

The largest solid triangle candidates are still all `itri` from
`pc=0xffffffff800fe5d4` into buffer 1, with `fbz=0x00000460` and
`fbzcp=0x0C482435`. Representative top buckets:

```text
pc=800fe5d4 itri color=F800 count=98 sumBox=22299312 maxBox=307200 box=0-640x0-480
pc=800fe5d4 itri color=001F count=91 sumBox=19157460 maxBox=307200 box=0-640x0-480
pc=800fe5d4 itri color=FFE0 count=84 sumBox=18141998 maxBox=307200 box=0-640x0-480
pc=800fe5d4 itri color=FFFF count=91 sumBox=16419361 maxBox=307200 box=0-640x0-480
```

However, the broad existing solid suppression control was visually neutral:

```text
EUTHERDRIVE_GAUNTDL_EXPERIMENT_VOODOO_SUPPRESS_LARGE_SOLID_TRIANGLES=1
/tmp/gauntdl-directsuppress-noedges-nosolidlarge-f420.ppm
frameHash=0x971ff26b
solidRaster=9641199
framebuffer=640x480:305614:143532
```

That means the profile is counting large candidates before the current
implausible-direct suppressor, or the remaining visible false surfaces are not
removed by a simple `boxPixels >= 640*480` solid gate. Do not promote the broad
large-solid suppressor. The next useful probe should distinguish drawn vs.
pre-suppressed solid triangle stats and should correlate the surviving
buffer-1 visible errors with the same `pc=800fe5d4` command-FIFO payload
ownership path.

#### 2026-07-04 drawn-solid and offscreen-direct checkpoint

The solid-triangle profiler now separates candidate, suppressed, and actually
drawn solid triangles:

```text
solidtri=.../drawN/dpPIXELS/dboxBOX/dmaxMAX/simpN/slrgN/soffN/...
solidtriDraw=...
```

This fixed the ambiguity from the previous profile: the useful visual stack
still hashes to `0x971ff26b`, but the surviving drawn solids are also dominated
by `pc=0xffffffff800fe5d4` into buffer 1. The largest drawn buckets are mixed
`itri` and `ftri`:

```text
/tmp/gauntdl-directsuppress-noedges-solidprofile-drawn-f420.ppm
frameHash=0x971ff26b
solidRaster=9641199
framebuffer=640x480:305614:143532

solidtriDraw top:
pc=800fe5d4 itri color=001F draw=21 dp=501249 dbox=2058308
pc=800fe5d4 itri color=F860 draw=14 dp=398951 dbox=2007040
pc=800fe5d4 ftri color=F800 draw=35 dp=410550 dbox=1691676
```

Added a default-off geometry suppressor for the next direct-payload probe:

```text
EUTHERDRIVE_GAUNTDL_EXPERIMENT_VOODOO_SUPPRESS_OFFSCREEN_DIRECT_TRIANGLES=1
```

It only applies while decoding Type1 command-FIFO `itri/ftri` work. It catches
triangles that cover a substantial clip area while at least one vertex is far
outside the visible frame, and records those hits as `soff`.

With the previous useful stack plus the new flag:

```text
/tmp/gauntdl-offscreen-directsuppress-noedges-f420.ppm
frameHash=0xdf78e30d
frameSha256=00e76d30147d90c9b5198aa2175bcbd48a03c5de4bf765ae4f8ffdbae16dc008
solidRaster=8015229
framebuffer=640x480:305614:115648
```

Visual result: the frame moves again and loses some green/blue false solid
surfaces, but it is still not real gameplay graphics. Large cyan/white/red
direct-solid shapes remain. This is useful evidence and a readable diagnostic,
not a promoted fix.

Also added a default-off stale write-trigger gate:

```text
EUTHERDRIVE_GAUNTDL_EXPERIMENT_VOODOO_FIFO_WRITE_GATE_STALE_READ=1
```

It reuses the existing stale Type5-payload read predicate, but only skips
`trigger=write` decode; it does not mutate the read pointer or drop packets.
This is a negative visual probe:

```text
/tmp/gauntdl-writegate-offscreen-directsuppress-f420.ppm
frameHash=0xdf78e30d
frameSha256=00e76d30147d90c9b5198aa2175bcbd48a03c5de4bf765ae4f8ffdbae16dc008
drawPackets=22993
direct/setup=6025/3001
texWrites=4541041
cmdstop=invalid-standard-window/0xbda7eca1/.../0x210/0x210/.../trig=write
```

It changes FIFO/textured workload but not the selected image or terminal stale
stop. So the visible false graphics are already drawn before the terminal
`0xbda7eca1 @ 0x210` symptom. Do not promote the write gate as a fix.

Next continuation point:

1. Keep the new drawn/suppressed solid profile and `soff` field as the visual
   oracle for direct-payload experiments.
2. The next real graphics target is earlier than the terminal stale stop:
   explain why `pc=800fe5d4` is still issuing valid-looking `ftri` and
   near-screen `itri` solid draws after the implausible/offscreen filters.
3. A narrow next probe should record max-drawn geometry separately from max
   candidate geometry, then trace the register-write packet sequence for the top
   remaining `solidtriDraw` buckets before adding more suppressors.

#### 2026-07-04 max-drawn geometry and 224-line offscreen probe

The solid profile now records max-drawn geometry separately from max candidate
geometry:

```text
dlastX0-X1xY0-Y1/dbN/dxyA:B:C/dareaN/drdN
```

This matters because candidate max geometry often came from already-suppressed
payload; `dlast` identifies the largest box that actually wrote pixels.

Rerunning the `0xdf78e30d` offscreen stack with the new fields preserved the
same selected image and exposed the top remaining `itri` offender:

```text
/tmp/gauntdl-offscreen-directsuppress-drawnmax-f420.ppm
frameHash=0xdf78e30d
solidRaster=8015229
framebuffer=640x480:305614:115648

top remaining itri:
pc=800fe5d4 color=F860 draw=14 dp=398951 dbox=2007040
dlast=0-640x0-224
dxy=0.0,-0.1:1604.4,223.8:1305.5,0.0
```

That shape sat just under the old `640*240` screen-fill threshold, so the
default-off offscreen-direct experiment now uses `640*224` for the
screen-fill-outside branch. The rerun:

```text
/tmp/gauntdl-offscreen224-directsuppress-f420.ppm
frameHash=0x2376d83f
frameSha256=1c0ea9d464e4f9075797c79151a308ecb36212d6d594a6df81c0cea2e766f646
solidRaster=7416358
framebuffer=640x480:305614:111993
```

Visual result: more `itri` false surface is removed, but this is still not real
graphics. The image shifts within the same false direct-solid family and leaves
large `ftri` half-screen blocks from `pc=800fe5d4`:

```text
solidtriDraw top after 224-line gate:
pc=800fe5d4 ftri color=F800 dlast=0-486x0-256
pc=800fe5d4 ftri color=FFE0 dlast=0-525x0-256
pc=800fe5d4 ftri color=0006 dlast=0-256x0-480
pc=800fe5d4 ftri color=27FF dlast=0-256x0-480
```

Do not promote the 224-line gate as correctness. Its value is diagnostic: it
proves the remaining visible f420 corruption has moved from near-screen `itri`
to valid-looking `ftri` register triangles from the same `pc=800fe5d4` payload
path. The next useful slice should trace the register packet sequence for those
`ftri` commands around read indices `0xAB85`, `0xA138`, `0x2E1D`, and `0x800B`.

#### 2026-07-04 focused direct-triangle read trace

Added a trace-only filter for direct `itri/ftri` draws at selected command-FIFO
read indexes:

```text
EUTHERDRIVE_GAUNTDL_TRACE_VOODOO_DIRECT_TRIANGLE_READS=0xAB85,0xA138,0x2E1D,0x800B
EUTHERDRIVE_GAUNTDL_TRACE_VOODOO_DIRECT_TRIANGLE_READS_LIMIT=...
```

The marker is:

```text
[GAUNTDL:VOODOO-DIRECT-TRI-READ]
```

Focused f420 run with the 224-line offscreen stack preserved the visual oracle:

```text
/tmp/gauntdl-offscreen224-directreadtrace-f420.ppm
frameHash=0x2376d83f
frameSha256=1c0ea9d464e4f9075797c79151a308ecb36212d6d594a6df81c0cea2e766f646
solidRaster=7416358
framebuffer=640x480:305614:111993
```

The trace proves the largest remaining `ftri` blocks are still stale/payload
Type1 decode from `pc=800fe5d4`, not plausible scene geometry:

```text
rd=0xAB85 cmd=0xBDA7ECA1 words=48552 packet=0x00000210 trigger=bulk-end
ftri color=F800 box=0-486x0-256
rawf=43F30000/43800000/0000FFFE/3E40912F/BEC5E593/3EB77445

rd=0xA138 cmd=0x3E1D9C71 words=15902 packet=0x00000044 trigger=bulk-end
ftri color=FFE0 box=0-525x0-256
rawf=44032000/43800000/0000FFFD/3E1EC504/3E914DC1/BE78072F

rd=0x2E1D cmd=0x3EDF8581 words=16096 packet=0x0000F8D0 trigger=bulk-end
ftri color=0006 box=0-256x0-480
rawf=BE3B233A/BE942B6B/3E59FE8F/43F30000/43800000/0000FFFE

rd=0x800B cmd=0x3EDF8581 words=16096 packet=0x0000F8D0 trigger=bulk-end
ftri color=27FF box=0-256x0-480
rawf=3E07F8D2/44032000/43800000/0000FFFE/3E1F6254/BE80031C
```

The same focused points also emit paired huge `itri` draws from the same packets,
confirming that one stale Type1 payload packet is polluting both integer and
float direct triangle register paths.

Next continuation point:

1. Do not add more geometry-only suppressors for these `ftri` blocks. They are
   symptoms of stale Type1 payload decode and will keep moving.
2. The next productive fix is upstream ownership: prevent `bulk-end` from
   executing oversized Type1 packets whose packet head is known payload storage,
   without reviving the broad implausible-Type1 stop that collapsed real
   direct/setup work.
3. A narrow candidate should combine `trigger=bulk-end`, `pc=800fe5d4`, oversized
   Type1 (`words > 1025`), and storage provenance (`last=fifo` inside the known
   payload-read family) before deciding to skip direct register draw effects.

#### 2026-07-05 payload direct-command suppression negative control

Added a default-off Type1 direct-command suppression probe:

```text
EUTHERDRIVE_GAUNTDL_EXPERIMENT_VOODOO_SUPPRESS_PAYLOAD_DIRECT_TRIANGLE_COMMANDS=1
EUTHERDRIVE_GAUNTDL_EXPERIMENT_VOODOO_SUPPRESS_PAYLOAD_DIRECT_TRIANGLE_COMMANDS_CMDS=...
EUTHERDRIVE_GAUNTDL_EXPERIMENT_VOODOO_SUPPRESS_PAYLOAD_DIRECT_TRIANGLE_COMMANDS_LIMIT=...
```

It suppresses only `triangleCommand` / `ftriangleCommand` register writes when:

```text
trigger=bulk-end
pc=800fe5d4
Type1 is oversized/implausible
packet-head storage was last written by FIFO at pc=800fe5d4
optional command filter matches
```

The trace marker is:

```text
[GAUNTDL:VOODOO-PAYLOAD-DIRECT-CMD-SUPPRESS]
```

This is useful as a negative control, but still not a visual fix.

Unfiltered source-gate run, without the 224-line offscreen suppressor:

```text
/tmp/gauntdl-payloaddirectcmd-f420.ppm
frameHash=0xa3750074
frameSha256=cb9f4fb20d9a476d33eb50a5016f5d14c01c0397e576b5c1a07f7c8beced125f
direct/setup=442/3001
solidRaster=308574
pdtc=5583
```

This reproduces the previously rejected over-suppressed visual family.

Filtered to the focused `ftri` commands from the prior trace, plus the 224-line
offscreen stack:

```text
CMDS=0xBDA7ECA1,0x3E1D9C71,0x3EDF8581
/tmp/gauntdl-offscreen224-payloaddirectcmd-filtered-f420.ppm
frameHash=0xa3750074
direct/setup=1650/3001
solidRaster=713081
pdtc=4375
```

Even the narrower filter still falls into the same over-suppressed family.

Filtered without `0xBDA7ECA1`, also with the 224-line stack:

```text
CMDS=0x3E1D9C71,0x3EDF8581
/tmp/gauntdl-offscreen224-payloaddirectcmd-nobda-f420.ppm
frameHash=0xa3750074
direct/setup=4303/3001
solidRaster=4494418
pdtc=1722
```

This proves the command-side suppression point is still too late or too coarse.
Suppressing selected payload direct commands reduces false solids, but it also
changes buffer selection/coverage into the old fully-covered fallback family
(`framebuffer=640x480:307200:144298`).

Next continuation point:

1. Keep the payload direct-command suppressor default-off as a diagnostic only.
2. Do not promote command-side direct suppression, even with command filters.
3. Move one level earlier: gate or classify the stale oversized Type1 packet
   before `DecodeFifoType1()` writes any register effects, using packet-head
   ownership/read-window evidence rather than draw-command side effects.
4. A useful next probe should emit a per-packet ownership summary for the first
   few `0xBDA7ECA1`, `0x3E1D9C71`, and `0x3EDF8581` Type1 packets: packet start,
   storage last writer, current bulk window relation, valid-window length, depth,
   holes, and whether it is inside Type5 payload data.

#### 2026-07-05 Type1 packet ownership trace

Added a default-off pre-decode ownership trace:

```text
EUTHERDRIVE_GAUNTDL_TRACE_VOODOO_TYPE1_PACKET_OWNERSHIP=1
EUTHERDRIVE_GAUNTDL_TRACE_VOODOO_TYPE1_PACKET_OWNERSHIP_COMMANDS=...
EUTHERDRIVE_GAUNTDL_TRACE_VOODOO_TYPE1_PACKET_OWNERSHIP_LIMIT=...
```

The trace marker is:

```text
[GAUNTDL:VOODOO-TYPE1-OWNERSHIP]
```

The f420 ownership run used the current visible stack, including the 224-line
offscreen suppressor, but did not enable the payload direct-command suppressor:

```text
frameHash=0x2376d83f
frameSha256=1c0ea9d464e4f9075797c79151a308ecb36212d6d594a6df81c0cea2e766f646
direct/setup=6025/3001
framebuffer=640x480:305614:111993
cmdstop=invalid-standard-window/0xbda7eca1/48552
pdtc=0
```

The focused Type1 packets all had the same important ownership shape:

```text
cmd=0xbda7eca1 words=48552 packet=0x00000210 validWindow=64/64
amin=0xfffffffc amax=0xfffffffc trigger=bulk-end
type5Data=0 bulk=scan=outside:rel29772/16896
w0 last=fifo/pc0xffffffff800fe5d4

cmd=0x3e1d9c71 words=15902 packet=0x00000044 validWindow=64/64
amin=0x0016cffc amax=0x0016cffc trigger=bulk-end
type5Data=0 bulk=scan=outside:rel59305/16896
w0 last=fifo/pc0xffffffff800fe5d4

cmd=0x3edf8581 words=16096 packet=0x0000f8d0 validWindow=64/64
amin=0x00000000 amax=0xbf4f5da9 trigger=bulk-end
type5Data=0 bulk=scan=outside:rel58308/16896
w0 last=fifo/pc0xffffffff800fe5d4
```

This narrows the cause again. The bad Type1 packets are not currently classified
as Type5 payload by the bulk scanner; they are valid old FIFO storage outside the
current bulk write window. The direct-command suppressor was too late, because
the stale packet has already installed thousands of bogus setup/register values
by then.

Next continuation point:

1. Add a default-off decode gate before `DecodeFifoType1()` for oversized Type1
   packets whose packet head is valid FIFO storage but `bulk=scan=outside` during
   `bulk-end`/`write` decode.
2. Prefer a stop/gate first, not header dropping, so the first behavior probe
   proves whether avoiding register effects improves the visible frame without
   consuming potentially real FIFO words.
3. If stop/gate stalls too often, try a separate default-off header-drop variant
   and compare f420 frame hash, `direct/setup`, and `cmdstop`.

#### 2026-07-05 outside-bulk Type1 stop-gate negative control

Added a default-off pre-Type1 stop gate:

```text
EUTHERDRIVE_GAUNTDL_EXPERIMENT_VOODOO_GATE_IMPLAUSIBLE_TYPE1_OUTSIDE_BULK_WINDOW=1
EUTHERDRIVE_GAUNTDL_EXPERIMENT_VOODOO_GATE_IMPLAUSIBLE_TYPE1_OUTSIDE_BULK_WINDOW_COMMANDS=...
EUTHERDRIVE_GAUNTDL_EXPERIMENT_VOODOO_GATE_IMPLAUSIBLE_TYPE1_OUTSIDE_BULK_WINDOW_LIMIT=...
```

It stops before `DecodeFifoType1()` when:

```text
Type1 is oversized/implausible
trigger is bulk-end or write
bulk scanner says scan=outside
packet-head storage is valid FIFO data
packet-head last-writer pc low32 is 800fe5d4
```

The first f420 behavior probe proved this is not the final fix:

```text
frameHash=0x6d791e91
frameSha256=1bbae73410456e3b595ce97970764a4bf1d2434f8f904ea72112c4031cf1a341
direct/setup=317/141
framebuffer=640x480:307200:307200
t1ob=46901
cmdstop=invalid-standard-window/0xbda7eca1/48552
```

The gate removed most stale direct/setup triangle work, but it over-gated real
progress and produced a fully covered frame. Early trace hits repeatedly stopped
on `cmd=0xf00b0001` at `packet=0x0000be5c`, which appears to be asset/model data
(`sourceWords` from the `gei` BGLoadModel path) being interpreted as Type1 after
the read pointer entered old storage outside the active bulk window.

Next continuation point:

1. Keep the stop-gate default-off as a negative control.
2. The next behavior probe should advance past invalid outside-bulk Type1 heads
   instead of repeatedly stopping on them.
3. Implement this as a separate default-off header-drop variant so it can be
   compared against the stop-gate and payload direct-command suppressor.

#### 2026-07-05 outside-bulk Type1 drop and stale-resync probes

Added a separate default-off header-drop variant:

```text
EUTHERDRIVE_GAUNTDL_EXPERIMENT_VOODOO_DROP_IMPLAUSIBLE_TYPE1_OUTSIDE_BULK_WINDOW=1
EUTHERDRIVE_GAUNTDL_EXPERIMENT_VOODOO_DROP_IMPLAUSIBLE_TYPE1_OUTSIDE_BULK_WINDOW_COMMANDS=...
EUTHERDRIVE_GAUNTDL_EXPERIMENT_VOODOO_DROP_IMPLAUSIBLE_TYPE1_OUTSIDE_BULK_WINDOW_LIMIT=...
```

It uses the same candidate as the stop-gate, but invalidates only the oversized
Type1 head and advances one word. This avoids getting stuck on `0xf00b0001`, but
it still over-corrects the frame:

```text
frameHash=0x6d791e91
frameSha256=1bbae73410456e3b595ce97970764a4bf1d2434f8f904ea72112c4031cf1a341
direct/setup=327/141
framebuffer=640x480:307200:307200
t1ob=0/69
```

The result matches the stop-gate visual family, so one-word header dropping is
not enough. It prevents the large bad Type1 register blocks, but it also loses
too much real setup/direct work and falls into a fully covered frame.

Also tested the pre-existing stale bulk resync:

```text
EUTHERDRIVE_GAUNTDL_EXPERIMENT_VOODOO_FIFO_BULK_RESYNC_STALE_PACKET=1
EUTHERDRIVE_GAUNTDL_TRACE_VOODOO_CMD_FIFO_BULK_END=1
```

This is structurally closer to the real fix: it repeatedly resyncs outside-bulk
stale reads to the current bulk start, where the packet head is usually
`0xc0000205` Type5 texture data:

```text
[GAUNTDL:VOODOO-CMDFIFO-BULK-RESYNC]
kind=stale-packet reason=oversized oldRd=0x00000000 newRd=0x00004d38
bulk=0x00004d38-0x00015534 words=16896
oldWord=0x3eb84c4d start=0xc0000205 scan=outside:rel60594/16896
```

The f420 result is better than stop/drop but still not real graphics:

```text
/tmp/gauntdl-resync-stale-f420.ppm
/tmp/gauntdl-resync-stale-f420.png
frameHash=0xa5684ec1
frameSha256=1cf30de84221cf95c96dc7da930a91b45a6022fa2cf69d7a8eedee6a42671385
direct/setup=321/141
framebuffer=640x480:307200:166664
texWrites=6629697
textureMap=26086768:13174989:12911779:812706
```

Visual inspection of the PNG shows a large blue triangle and horizontal stripe
bands. That is not correct scene output, but it is a more informative failure
than the fully covered fallback: texture upload and selected-buffer activity are
alive, while geometry packet boundaries are still wrong.

Next continuation point:

1. Keep the Type1 outside-bulk stop/drop probes default-off.
2. The stale-resync direction is more promising than stop/drop, but it still
   over-resyncs or decodes the wrong packet boundary.
3. Next probe should compare current read position to the decoded packet heads
   inside the active bulk window and resync to the nearest valid packet head,
   not always the bulk start.
4. The visual target for the next slice is to remove the giant blue triangle and
   stripe bands from `/tmp/gauntdl-resync-stale-f420.png` while preserving the
   increased texture activity from stale-resync.

#### 2026-07-05 payload Type1 packet skip and texture-cover filtering

Added a trace quality-of-life filter for covered textured triangles:

```text
EUTHERDRIVE_GAUNTDL_TRACE_VOODOO_TEXTURE_COVERED_MIN_PIXELS=...
```

With the stale-resync stack, a min-pixels trace confirmed that the blue/stripe
image is still dominated by repeated large Type3 setup work from
`pc=800c4e5c`, `cmd=0x0180A8CB`, with absurd coordinates such as
`y=-16231` and `x=49076`. Stacking the existing setup suppressor on the
stale-resync run catches some of these triangles, but the f420 image remains
unchanged:

```text
/tmp/gauntdl-resync-setup-suppress-f420.ppm
frameHash=0xa5684ec1
frameSha256=1cf30de84221cf95c96dc7da930a91b45a6022fa2cf69d7a8eedee6a42671385
direct/setup=321/141
framebuffer=640x480:307200:166664
```

That rules out the simple "filter the huge setup triangle" shape as the real
fix for the stale-resync visual family.

Added another default-off upstream probe:

```text
EUTHERDRIVE_GAUNTDL_EXPERIMENT_VOODOO_SKIP_PAYLOAD_TYPE1_PACKETS=1
EUTHERDRIVE_GAUNTDL_EXPERIMENT_VOODOO_SKIP_PAYLOAD_TYPE1_PACKETS_CMDS=...
EUTHERDRIVE_GAUNTDL_EXPERIMENT_VOODOO_SKIP_PAYLOAD_TYPE1_PACKETS_LIMIT=...
```

It skips whole oversized Type1 packets before their payload words touch Voodoo
register state, but only for the proven payload family:

```text
trigger=bulk-end
pc=800fe5d4
Type1 is implausible/oversized
packet-head storage was last written by FIFO at pc=800fe5d4
optional command filter matches
```

The broad run with the useful visual stack plus packet skip hit the intended
outside-bulk payload heads:

```text
[GAUNTDL:VOODOO-PAYLOAD-TYPE1-SKIP]
cmd=0x3e959c11 packet=0x00000020 bulk=scan=outside

[GAUNTDL:VOODOO-PAYLOAD-TYPE1-SKIP]
cmd=0xbda7eca1 packet=0x00000210 bulk=scan=outside
```

but it collapsed the frame into the same fully covered fallback family as the
earlier Type1 stop/drop probes:

```text
/tmp/gauntdl-payloadtype1skip-offscreen-f420.ppm
/tmp/gauntdl-payloadtype1skip-offscreen-f420.png
frameHash=0x6d791e91
frameSha256=1bbae73410456e3b595ce97970764a4bf1d2434f8f904ea72112c4031cf1a341
direct/setup=442/201
framebuffer=640x480:307200:307200
```

The focused run filtered the skip to the three previously proven largest
`ftri` offenders:

```text
EUTHERDRIVE_GAUNTDL_EXPERIMENT_VOODOO_SKIP_PAYLOAD_TYPE1_PACKETS_CMDS=0xbda7eca1,0x3e1d9c71,0x3edf8581
```

It still produced the same fullcover family:

```text
/tmp/gauntdl-payloadtype1skip-focused-offscreen-f420.ppm
/tmp/gauntdl-payloadtype1skip-focused-offscreen-f420.png
frameHash=0x6d791e91
frameSha256=1bbae73410456e3b595ce97970764a4bf1d2434f8f904ea72112c4031cf1a341
direct/setup=1650/810
framebuffer=640x480:307200:307200
```

The useful part is not the visual result. The trace shows the same outside-bulk
heads being replayed repeatedly at `bulk-end`:

```text
cmd=0xbda7eca1 packet=0x00000210 bulk=scan=outside:rel29772/16896
cmd=0x3e1d9c71 packet=0x00000044 bulk=scan=outside:rel59305/16896
cmd=0x3edf8581 packet=0x0000f8d0 bulk=scan=outside:rel58308/16896
```

So whole-packet skip is a negative control, not a candidate fix. It proves that
the next fix must stop the stale outside-bulk heads from being selected/replayed
in the first place, rather than consuming entire oversized packets after decode
has already selected the wrong read head.

Next continuation point:

1. Keep `SKIP_PAYLOAD_TYPE1_PACKETS` default-off as evidence only.
2. Do not add more geometry or packet-consumption suppressors; they collapse
   direct/setup work and keep the `0x6d791e91` fullcover family.
3. Trace why `bulk-end` repeatedly re-enters the same outside-bulk packet heads
   (`0x210`, `0x44`, `0xf8d0`) after the skip/advance path.
4. The likely productive fix is read-pointer provenance or generation validity:
   once a bulk-end read head is proven outside the current bulk window and
   last-written by stale FIFO payload, the decode should resync to a valid
   packet head in the active producer window or gate until the producer installs
   a new read head, not consume the stale packet body.

#### 2026-07-05 outside-stale-chain bulk-end gate

Added a separate default-off bulk-end decode gate:

```text
EUTHERDRIVE_GAUNTDL_EXPERIMENT_VOODOO_FIFO_BULK_GATE_OUTSIDE_STALE_CHAIN=1
```

It skips the automatic `bulk-end` decode when the current read head is outside
the active producer bulk window, the storage word was last written by FIFO, and
the last writer PC low32 is `800fe5d4`. This is narrower than packet dropping:
it does not consume the stale Type1 packet and does not move the read pointer.

The first f420 behavior probe used the same visual stack as the current
offscreen/direct diagnostic run:

```text
/tmp/gauntdl-outside-stalechain-gate-f420.ppm
/tmp/gauntdl-outside-stalechain-gate-f420.png
frameHash=0x2376d83f
frameSha256=1c0ea9d464e4f9075797c79151a308ecb36212d6d594a6df81c0cea2e766f646
direct/setup=6025/3001
drawPackets=21375
framebuffer=640x480:305614:111993
cmdstop=invalid-standard-window/0xbda7eca1/48552/.../pc=0xffffffff801066c8
```

The gate fires on the intended shape:

```text
[GAUNTDL:VOODOO-CMDFIFO-BULK-GATE]
reason=outside-stale-chain:scan=outside:rel60594/16896
rd=0x00000000 bulk=0x00004d38-0x00015534
word=0x3eb84c4d start=0xc0000205 pc=0xffffffff800fe5d4
```

Visual inspection is still negative. The selected frame remains dominated by
large cyan/white/red false surfaces and a horizontal noise band. The result
matches the `0x2376d83f` offscreen-224 family, so gating only the `800fe5d4`
outside-stale bulk-end entry does not remove the visible corruption.

The important clue is that the summary's top surviving solid buckets moved to
the follow-on command-FIFO service PC:

```text
solidtriDraw top: pc=800fe850 ftri ... rdAB85/rdA138/rd2E1D/rd800B
```

Next continuation point:

1. Keep the outside-stale-chain bulk-end gate default-off as diagnostic.
2. Do not promote it; it is visually neutral against the current false-surface
   family.
3. Run a focused direct-read/ownership trace on the same `0x2376d83f` stack to
   confirm whether the remaining `ftri` blocks are the same stale Type1 payload
   heads replayed from `pc=800fe850`.
4. If confirmed, the next candidate should classify stale payload ownership by
   packet storage provenance and active bulk window, not by the current CPU PC
   alone.

#### 2026-07-05 outside-stale write gate and resync probes

The focused direct-read/ownership trace confirmed why the bulk-end gate was
visually neutral. The remaining large direct blocks are not being emitted by
`bulk-end`; they are repeated `write`-trigger decodes from the follow-on command
FIFO service at `pc=800fe850`:

```text
cmd=0xbda7eca1 packet=0x00000210 rd0xAB85 trigger=write pc=800fe850
bulk=scan=outside:rel29772/16896
w0 last=fifo/pc0xffffffff800fe5d4

cmd=0x3e1d9c71 packet=0x00000044 rd0xA138 trigger=write pc=800fe850
bulk=scan=outside:rel59305/16896
w0 last=fifo/pc0xffffffff800fe5d4

cmd=0x3edf8581 packet=0x0000f8d0 rd0x2E1D/rd0x800B trigger=write pc=800fe850
bulk=scan=outside:rel58308/16896
w0 last=fifo/pc0xffffffff800fe5d4
```

So the stale payload ownership is real, but keying fixes to current CPU PC
`800fe5d4` is too narrow. The stale packet heads can be replayed by the next
service phase at `800fe850`.

Added a separate default-off write-trigger gate:

```text
EUTHERDRIVE_GAUNTDL_EXPERIMENT_VOODOO_FIFO_WRITE_GATE_OUTSIDE_STALE_CHAIN=1
```

It uses the same provenance predicate as the bulk-end gate, but applies before
`DecodeCommandFifoPacketsIfNotPending("write")`. This is negative:

```text
/tmp/gauntdl-outside-stalechain-writegate-f420.ppm
/tmp/gauntdl-outside-stalechain-writegate-f420.png
frameHash=0x6d791e91
frameSha256=1bbae73410456e3b595ce97970764a4bf1d2434f8f904ea72112c4031cf1a341
direct/setup=942/441
drawPackets=22273
framebuffer=640x480:307200:307200
```

The gate catches the intended trigger, but it leaves the stale read head in
place while depth/valid data accumulate. The selected frame falls into the
same fully covered cyan/brown family as earlier Type1 stop/drop controls.

Added a second default-off write-trigger resync:

```text
EUTHERDRIVE_GAUNTDL_EXPERIMENT_VOODOO_FIFO_WRITE_RESYNC_OUTSIDE_STALE_CHAIN=1
```

It moves the read head from the stale outside-bulk packet to the current bulk
start, but only when the bulk start is a Type5 texture packet head:

```text
[GAUNTDL:VOODOO-CMDFIFO-BULK-RESYNC]
kind=write-outside-stale
oldRd=0x00000210 newRd=0x000020a0
oldWord=0xbda7eca1 start=0xc0000205
reason=scan=outside:rel63580/16896
pc=0xffffffff800fe850
```

This is mechanically cleaner than the write gate, but the visual result is also
negative:

```text
/tmp/gauntdl-outside-stalechain-writeresync-f420.ppm
/tmp/gauntdl-outside-stalechain-writeresync-f420.png
frameHash=0x6d791e91
frameSha256=1bbae73410456e3b595ce97970764a4bf1d2434f8f904ea72112c4031cf1a341
direct/setup=317/141
drawPackets=24761
texWrites=6372017
textureMap=25056048:12682016:12374032:807808
framebuffer=640x480:307200:307200
```

The resync increases texture traffic and repeatedly anchors stale reads to
`0xc0000205` heads, but it still collapses real direct/setup work and produces
the same fullcover image. Bulk-start is therefore too coarse as a universal
resync target.

Next continuation point:

1. Keep `FIFO_WRITE_GATE_OUTSIDE_STALE_CHAIN` and
   `FIFO_WRITE_RESYNC_OUTSIDE_STALE_CHAIN` default-off as negative controls.
2. The productive trace direction is now narrower: for the repeated
   `0xbda7eca1 @ 0x210` and `0x3e1d9c71 @ 0x44` write-trigger loops, compare
   the stale read head to packet heads already decoded inside the current bulk
   window. Do not always jump to bulk start.
3. A candidate fix should select a valid next packet head or preserve the
   producer-installed read phase, not gate/stall or reset every outside-stale
   read to the Type5 header.

#### 2026-07-05 lower implausible direct-draw threshold probe

The remaining `0x2376d83f` visible corruption had moved to `ftri` blocks below
the existing implausible direct-draw suppressor threshold (`640*240`). Added an
env-controlled threshold while preserving the old default:

```text
EUTHERDRIVE_GAUNTDL_EXPERIMENT_VOODOO_SUPPRESS_IMPLAUSIBLE_BULK_DIRECT_TRIANGLES_MIN_BOX_PIXELS=...
```

The behavior probe used `32768` with the current visual stack:

```text
/tmp/gauntdl-impldirect-min32768-f420.ppm
/tmp/gauntdl-impldirect-min32768-f420.png
frameHash=0xa3750074
frameSha256=cb9f4fb20d9a476d33eb50a5016f5d14c01c0397e576b5c1a07f7c8beced125f
direct/setup=6025/3001
drawPackets=21375
framebuffer=640x480:307200:144298
```

This is also not correct graphics. Unlike the earlier packet/command-side
suppression controls, it preserves direct/setup counters, but visual inspection
shows a white/stripe frame instead of scene geometry. That means simply peeling
more stale direct-solid surfaces is not enough; the underlying selected buffer
is still dominated by bad texture/stripe output.

Next continuation point:

1. Keep the min-box threshold env defaulted to the old value as a diagnostic
   knob only.
2. The next visual target should move from direct-solid overlays to the
   selected-buffer texture/stripe source. The direct counters can remain high
   while the revealed frame is still wrong.
3. Compare buffer selection and the stripe-producing setup/textured triangles
   between `0x2376d83f` and `0xa3750074`; the direct overlay is not the only
   blocker to visible graphics.

#### 2026-07-05 pixel last-writer texture-stack probe

Added a default-off selected-pixel writer profiler:

```text
EUTHERDRIVE_GAUNTDL_PROFILE_VOODOO_PIXEL_LAST_WRITERS=1
```

It samples a fixed 20x15 grid across the visible 640x480 area for each color
buffer and reports the final writer buckets as `plw=...` in `DebugStatus`.
This avoids dumping every pixel while still answering which renderer path is
actually visible after all overdraw.

The `0xa3750074` stack with setup suppress enabled confirms that the selected
frame is buffer 0, not the heavier buffer-1 raster profile:

```text
rbuf=0
frameHash=0xa3750074
frameSha256=cb9f4fb20d9a476d33eb50a5016f5d14c01c0397e576b5c1a07f7c8beced125f
framebuffer=640x480:307200:144298
textured=tri:907:covered:693:rejected:214:pixels:57522088:zero:11941765
```

The buffer-0 sampled last writers are:

```text
b0:
171/300 pc=801027cc fill fastfill cFFFF cmd=0104824C
 75/300 pc=800c4e5c tex setup c07FF cmd=0180A8CB rdCF50
 50/300 pc=800c4e5c tex setup c07FF cmd=0180A8CB rdCF3D
  4/300 pc=800fe5d4 tex setup c07E0 cmd=0180A8CB rd9E2E
```

So the early giant `800c4e5c` setup triangles are only one layer. After those
are suppressed, later `800c4e5c` Type3 setup-texture draws with plausible
framebuffer state still write the visible stripe band. The white area is a real
late fastfill from `801027cc`.

The next behavior probe used the same stack plus the MAME-style texture setup
and fetch path:

```text
EUTHERDRIVE_GAUNTDL_EXPERIMENT_VOODOO_TEXTURE_MAME_SETUP_GRADIENTS=1
EUTHERDRIVE_GAUNTDL_EXPERIMENT_VOODOO_MAME_TEXTURE_FETCH_ADDRESSING=1
EUTHERDRIVE_GAUNTDL_EXPERIMENT_VOODOO_MAME_TEXTURE_FIXED_FETCH=1
```

This is a real but incomplete improvement:

```text
/tmp/gauntdl-mame-texture-path-f420.ppm
/tmp/gauntdl-mame-texture-path-f420.png
frameHash=0x3cce6946
frameSha256=75ad57a15b453bb7779f33840cf213c04df32c3e6db94695672ae8222a23bcc2
framebuffer=640x480:307200:175015
textured=tri:907:covered:693:rejected:214:pixels:57522088:zero:6681926
```

Visual inspection is still negative: the frame is darker/more textured but is
still horizontal bands, not scene graphics. However, the texture-zero count is
nearly halved and the selected frame hash changes, so this is the strongest
current renderer-side direction.

Adding TMU-bank-aware texture upload on top of the MAME-style path:

```text
EUTHERDRIVE_GAUNTDL_EXPERIMENT_VOODOO_TEXTURE_UPLOAD_TMU_BANKS=1
```

does not change the visible frame:

```text
/tmp/gauntdl-mame-texture-upload-tmubanks-f420.ppm
frameHash=0x3cce6946
frameSha256=75ad57a15b453bb7779f33840cf213c04df32c3e6db94695672ae8222a23bcc2
framebuffer=640x480:307200:175015
textureMap=16754480:8367795:8386685:569824
```

The follow-up buffer-0 texture summary on the MAME-style path shows the
surviving visible band source directly. The hot packets are repeated
`0x0180A8CB` setup-texture full-rect pairs from `pc=800c4e5c`, later replayed
from `pc=80106a74` and `pc=800fe5d4` as well:

```text
pc=800c4e5c cmd=0x0180A8CB
xy=(0,-1)/(512,383)/(0,383)
stq=(0,256,1)/(0,0,1)/(0,0,1)
bbox=(0,41)-(512,383)
pixels=97128 zero=105
mode=0x8C24100F lod=0x00002000 regbase=0x00000000 base=0x000510
raw=0x0000:9394,0x0023:2904,0x00FF:2898,0x0003:1920
addr=0x00F000:11648,0x00E000:10880,0x00D000:10112,0x00C000:9344

paired triangle:
xy=(512,383)/(0,-1)/(512,-1)
pixels=77976 zero=407
raw=0x0000:13134,0x00FE:2132,0x00D4:2097,0x0002:1851
addr=0x003000:9856,0x002000:9709,0x004000:9088,0x005000:8320
```

This means the remaining visual failure is no longer an arbitrary giant
coordinate triangle; it is a repeated screen-sized texture page with simple
`S/T` coordinates. The sampled texture data is still stripe-like.

The old `0x510` sample-base bias was also tested as an override:

```text
EUTHERDRIVE_GAUNTDL_FIX_VOODOO_TEXTURE_SAMPLE_BASE_BIAS=0
```

This changes the image but remains wrong:

```text
/tmp/gauntdl-mame-texture-nobias-f420.ppm
/tmp/gauntdl-mame-texture-nobias-f420.png
frameHash=0x62c2d545
frameSha256=6ec73e4e88b9104ce5e2d40d6990e688c122d9268657c31b278c9270957ce703
framebuffer=640x480:307200:175980
textured zero=6829521
```

Visual inspection is still horizontal bands. Removing the bias is therefore
not the fix, although it proves the stripe page is sensitive to the sampled
texture base.

Next continuation point:

1. Keep `PIXEL_LAST_WRITERS` as the primary visual oracle for the selected
   buffer. It shows the actual visible writers instead of total raster work.
2. Do not chase more broad setup/direct suppressors. The selected frame is now
   dominated by late `800c4e5c` setup-texture writes and `801027cc` fastfill.
3. Use the MAME-style setup/fixed-fetch path as the next diagnostic baseline,
   but do not promote it yet; it changes the frame and reduces zero samples,
   yet still renders stripes.
4. Next probe should compare the full-rect `800c4e5c`/`80106a74`/`800fe5d4`
   `0x0180A8CB` replays against command-FIFO packet ownership. The same
   screen-sized texture packet appears at many read offsets, so packet replay
   or stale source ownership is still suspicious even after direct-triangle
   overlays are peeled away.
5. In parallel, trace Type5 writes into the sampled buckets
   `0x002000..0x010000` under the MAME-style path. TMU bank selection alone is
   not the missing piece, and `baseBias=0` is only a different wrong stripe.

## 2026-07-05 - Texture Writer Ownership Trace

Two diagnostic-only trace improvements were added:

```text
EUTHERDRIVE_GAUNTDL_TRACE_VOODOO_TEXTURE_WRITE_BUCKETS_PER_BUCKET_LIMIT=...
EUTHERDRIVE_GAUNTDL_TRACE_VOODOO_TEXTURE_TRIANGLE_SAMPLE_WRITERS=1
```

The first prevents `VOODOO-TEXWRITE` from spending the whole trace budget on
the first sampled bucket. The second records optional texture-word last-writer
metadata and adds a compact `writers=` section to `VOODOO-TEXSUMMARY`.

The per-bucket Type5 trace under the same MAME-style texture path remained
visually unchanged:

```text
/tmp/gauntdl-mame-path-texwrite-perbucket-f420.ppm
/tmp/gauntdl-texwrite-perbucket-f420.log
frameHash=0x3cce6946
frameSha256=75ad57a15b453bb7779f33840cf213c04df32c3e6db94695672ae8222a23bcc2
textured zero=6681926
textureMap=16754480:8367795:8386685:558752
```

The sampled hot buckets now have concrete upload provenance:

```text
0x002000 pc=800fe7cc mode=0x00000B00 lod=0x00300804 base=0x1FFFE200 targetStart=0x008800
0x003000 pc=800fe7cc mode=0x00000B00 lod=0x00300804 base=0x1FFFE200 targetStart=0x009000
0x004000 pc=800fe7cc mode=0x00000B00 lod=0x00300804 base=0x1FFFE200 targetStart=0x009800
0x005000 pc=800fe7cc mode=0x00000B00 lod=0x00700804 base=0x00000200 targetStart=0x008000
0x00C000 pc=800fe5d4 mode=0x00000100 lod=0x0000080C base=0x1FFFEDA2 targetStart=0x018B80/0x018C00
0x00D000 pc=800fe614 mode=0x00000000 lod=0x00700800 base=0x000019A0 targetStart=0x000180
0x00E000 pc=800fe614 mode=0x00000000 lod=0x00700800 base=0x000019A0 targetStart=0x000980
0x00F000 pc=800fe614 mode=0x00000000 lod=0x00700800 base=0x00001DA0 targetStart=0x000180
0x010000 pc=800fe614 mode=0x00000000 lod=0x00700800 base=0x00001DA0 targetStart=0x000980
```

The writer-summary probe also stayed hash-identical:

```text
/tmp/gauntdl-mame-path-texsummary-writers-f420.ppm
/tmp/gauntdl-texsummary-writers-f420.log
frameHash=0x3cce6946
framebuffer=640x480:307200:175015
textured=tri:907:covered:693:rejected:214:pixels:57522088:zero:6681926
```

The repeated buffer-0 fullrect pair still samples `base=0x000510` from
`pc=800c4e5c`, but the sampled words now resolve to specific upload runs:

```text
triangle A addr=0x00F000/0x00E000/0x00D000/0x00C000
writers=pc=800fe614 mode=0x00000000 lod=0x00700800 base=0x00001DA0
        targetStart=0x000B80/0x000A80/0x000980/0x000880

triangle B addr=0x003000/0x002000/0x004000/0x005000
writers=pc=800fe7cc mode=0x00000B00 lod=0x00300804 base=0x1FFFE200
        targetStart=0x008900/0x008A00/0x008B00/0x008C00
```

Later repetitions of the same screen-sized packet shift the low-address half
to `pc=800fe5d4` zero-base upload runs, and the `pc=80106a74` replay eventually
overwrites one of the high buckets. This strongly points away from a generic
texture sampler bug and toward payload/source ownership or repeated Type5
upload source selection.

Next continuation point:

1. Use `TEXTURE_TRIANGLE_SAMPLE_WRITERS=1` as the main oracle for the stripe
   page. It connects the visible fullrect texture samples to exact Type5
   upload packets.
2. Trace the Type5 payload/source chain for the target starts that dominate
   the visible fullrect: `0x000B80`, `0x000A80`, `0x000980`, `0x000880`,
   `0x008900..0x008C00`, and the later zero-base `0x001100..0x001400`.
3. Compare those packet source addresses against the BGLoadModel indexed
   source/body ownership. The next real fix is likely in upload payload
   provenance or source selection, not in another broad triangle suppressor.

## 2026-07-05 - Type5 Target Source and Stride Triage

Added a target-word filter to the upload-link trace:

```text
EUTHERDRIVE_GAUNTDL_TRACE_TEXTURE_UPLOAD_PACKET_TARGET_WORDS=b80,8900
EUTHERDRIVE_GAUNTDL_TRACE_TEXTURE_UPLOAD_PACKET_TARGET_LIMIT=96
```

`VOODOO-TYPE5-TARGET` now also prints `targetByte` and the last three FIFO
storage writers (`w0/w1/w2`) for focused Type5 target packets.

Baseline MAME-texture-path run stayed on the known wrong hash:

```text
/tmp/gauntdl-upload-link-targetwords-b80-8900-f420.log
frameHash=0x3cce6946
frameSha256=75ad57a15b453bb7779f33840cf213c04df32c3e6db94695672ae8222a23bcc2
framebuffer=640x480:307200:175015
textureMap=16754480:8367795:8386685:558752:0x000000:0x7fe444
textured=tri:907:covered:693:rejected:214:pixels:57522088:zero:6681926
```

The `0x00000b80` target is definitely the bad low texture page:

```text
TEXUPLOAD-LINK targetWord=0x00000b80 targetByte=0x00002e00
packetOffset=0x00002e00 source=0xffffffff803140a4
bgsrc=18:cel+0xe98c(...hdr=bad),21:rat+0x898c(...hdr=bad),
      22:ga2+0x698c(...hdr=bad),23:gam+0x498c(...hdr=bad),
      24:ged+0x298c(...hdr=bad),25:gep+0x98c(...hdr=bad)
raw=0x00a00000,0x00000000,0x00000000,0x00000000,...
```

The matching Type5 storage writers for that target are the same upload service
PCs already suspected in the visible texture page:

```text
w0/w1/w2 pc=800fe5e8/800fe5f8/800fe60c for the full payload
later sparse payloads from pc=800fe5d4 start with 0x00a00000 and zeros
```

`0x00008900` has Type5-target storage provenance but does not appear through
`TEXUPLOAD-LINK`, which means it is not produced by the same fast-path
`TryFastPathKnownGlideFifoOuterPayloadLoopTail` upload-link helper:

```text
VOODOO-TYPE5-TARGET targetWord=0x00008900 targetByte=0x00022400
w0/w1/w2 pc=800fe7a0/800fe7b0/800fe7c4
```

The indexed BGLoadModel source-stride probe is the strongest diagnostic change
from this pass:

```text
EUTHERDRIVE_GAUNTDL_EXPERIMENT_RUNTIME_BGLOADMODEL_INDEXED_SOURCE_STRIDE=0x20000
/tmp/gauntdl-stride20000-dump-f420.log
/tmp/gauntdl-stride20000-f420.png
frameHash=0x6d791e91
frameSha256=1bbae73410456e3b595ce97970764a4bf1d2434f8f904ea72112c4031cf1a341
framebuffer=640x480:307200:307200
textureMap=18297344:7949314:10348030:313172:0x000000:0x704284
textured=tri:981:covered:828:rejected:153:pixels:77609945:zero:11835230
```

Visual inspection is still negative: it is a fully colored cyan/brown
two-field artifact, not real scene graphics. The useful result is source
ownership, not presentation:

```text
TEXUPLOAD-LINK targetWord=0x00000b80 source=0xffffffff80314098 bgsrc=none
raw=0x00000000,0x00000000,0x00000000,0x00a00000,...
```

So `0x2000` indexed source stride was too small for the known payload sizes and
caused overlapping body windows. `0x20000` removes the bad overlap, materially
changes the frame, and should remain a diagnostic/foundation knob. Do not
promote it to a default until the visible image becomes scene-like.

Setup-wrap on top of the stride run did not improve presentation:

```text
/tmp/gauntdl-stride20000-setupwrap-f420.log
/tmp/gauntdl-stride20000-setupwrap-f420.png
EUTHERDRIVE_GAUNTDL_FIX_VOODOO_SETUP_VERTEX_COORDINATE_WRAP=1
frameHash=0x6d791e91
frameSha256=1bbae73410456e3b595ce97970764a4bf1d2434f8f904ea72112c4031cf1a341
framebuffer=640x480:307200:307200
textureMap=18297344:7949314:10348030:313172:0x000000:0x704284
textured=tri:14843:covered:1648:rejected:13195:pixels:89196507:zero:23421544
```

Triangle triage on the stride variant rules out the direct-solid overlay as the
main screen-filling artifact. The large `itri` packets mostly show up as
suppressed `simp` groups, and the actual visible field is dominated by setup
textured quads:

```text
pc=0xffffffff800c4e5c
cmd=0x0180A8CB
setup=0x0006002A
fbz=0x00000460 or later 0xC0000205
fbzcp=0x0C482435
mode=0x8C24100F lod=0x00002000 regbase=0 base=0x000510
xy=(0,-1)/(512,383)/(0,383) stq=(0,256)/(0,0)/(0,0)
xy=(512,383)/(0,-1)/(512,-1) stq=(0,0)/(0,256)/(0,256)
```

The sample writer summaries for these quads still point at Type5 upload data:

```text
pc=800fe614 target ranges around 0x000100..0x001c00
pc=800fe5d4 sparse target ranges around 0x001100/0x007f00
plus none buckets
```

Next continuation point:

1. Keep `0x20000` stride as a diagnostic run condition, but do not make it
   default yet.
2. Focus on the `pc=800c4e5c`, `cmd=0x0180A8CB`, `base=0x510` setup-textured
   quad path. It is the current visible artifact owner.
3. Trace or repair Type3 setup decode/texture-source mapping for that quad
   before adding more broad direct-triangle suppressors.
4. Keep `0x00000b80/0x00002e00` sparse `800fe5d4` writes and
   `0x00008900/0x00022400` direct `800fe7a0` writes as separate source-chain
   problems.

## 2026-07-05 - Type3 Fullrect Ownership Trace

Added default-off register and Type3 read filters:

```text
EUTHERDRIVE_GAUNTDL_TRACE_VOODOO_REGISTER_WRITE_TARGETS=a8,a9
EUTHERDRIVE_GAUNTDL_TRACE_VOODOO_REGISTER_WRITE_PCS=800c4e5c
EUTHERDRIVE_GAUNTDL_TRACE_VOODOO_TYPE3_READS=21034,21080,...
```

Register-write tracing rules out the first suspected direct-register path:

```text
/tmp/gauntdl-stride20000-regwrite-a8-f420.log
frameHash=0x6d791e91
```

The first 240 `0xa8/0xa9` writes are stale/payload-looking writes from
`pc=800fe5d4`, mostly while the decode context is `cmd=0xbda7eca1` or
`cmd=0xbfa88d14`. Filtering the same trace to `pc=800c4e5c` produces no
`VOODOO-REGWRITE` rows:

```text
/tmp/gauntdl-stride20000-regwrite-800c4e5c-f420.log
frameHash=0x6d791e91
```

That means the visible `800c4e5c` full-screen artifact is not coming from
ordinary `WriteRegister(0xa8/0xa9)` calls.

The Type3 read-filter trace resolves the ownership instead:

```text
/tmp/gauntdl-stride20000-type3reads-f420.log
frameHash=0x6d791e91
```

The visible fullrect packets are real Type3 packets decoded at `pc=800c4e5c`:

```text
rd=0x00021034 cmd=0x0180a8cb v0=(0,-1,s=0,t=256) v1=(512,383,s=0,t=0) v2=(0,383,s=0,t=0)
rd=0x00021080 cmd=0x0180a8cb v0=(512,383,s=0,t=0) v1=(0,-1,s=0,t=256) v2=(512,-1,s=0,t=256)
```

The same clean 512x383 setup-textured pair repeats at
`0x23d20/0x23d6c`, `0x3510c/0x35158`, `0x2989c/0x298e8`,
`0x00ae8/0x00b34`, and `0x223d8/0x22424`.

Current conclusion:

1. Stop chasing Type3 geometry for this visible plane. The fullrect is real,
   its screen coordinates are clean, and its S/T range is intentional-looking
   (`S=0`, `T=0..256`).
2. Keep the MAME texture fetch path and `0x20000` BGLoadModel source stride as
   diagnostic conditions for now, because they expose the current artifact
   without turning it into scene-like graphics.
3. Focus next on texture data ownership for the real fullrect sample buckets:
   `base=0x510`, sample addresses around `0x00f000..0x00c000`,
   `0x003000..0x005000`, and Type5 upload targets from `pc=800fe614` and
   `pc=800fe5d4`.

Next trace target:

```text
EUTHERDRIVE_GAUNTDL_TRACE_TEXTURE_UPLOAD_PACKET_TARGET_WORDS=100,200,300,d00,e00,f00,1100,1200,1300,1400,7d00,7e00,7f00,8900,8a00,8b00,8c00
EUTHERDRIVE_GAUNTDL_TRACE_TEXTURE_UPLOAD_PACKET_TARGET_LIMIT=200
```

## 2026-07-05 - Upload Source Gap Probe

The next f420 probe used the same stride/MAME texture stack plus the focused
upload target list above:

```text
/tmp/gauntdl-stride20000-upload-targets-f420.log
/tmp/gauntdl-stride20000-upload-targets-f420.ppm
/tmp/gauntdl-stride20000-upload-targets-f420.png
frameHash=0x68886c0f
frameSha256=befa0463ab2223ddb6d97ac4fd08121e9e8bcdd512253b3b3c876b7d3e002f3b
framebuffer=640x480:307200:173051
textureMap=18297344:7949314:10348030:313172:0x000000:0x704284
textured=tri:981:covered:828:rejected:153:pixels:77609945:zero:11835230
```

Visual inspection is still negative: horizontal colored stripes with a large
white fill area, not scene graphics.

The useful result is that the visible fullrect's low and high target buckets
are now separated:

```text
17x each: targetWord=0x00000100/0x00000200/0x00000300/0x00000d00/0x00000e00/0x00000f00
14x each: targetWord=0x00001100/0x00001200/0x00001300/0x00001400/0x00007d00/0x00007e00/0x00007f00
0x00008900..0x00008c00: no TEXUPLOAD-LINK hits in this helper path
```

The low pages are copied from the gap after index 1's currently declared
payload:

```text
targetWord=0x00000100 source=0xffffffff80312b98 bgsrc=none
targetWord=0x00001100 source=0xffffffff80314b98 bgsrc=none
```

With `EUTHERDRIVE_GAUNTDL_EXPERIMENT_RUNTIME_BGLOADMODEL_INDEXED_SOURCE_STRIDE=0x20000`,
those addresses are index 1 `gei` plus roughly `0x11280..0x13280`, while the
table currently declares `gei` as only `0xa13c` bytes. Raw disk inspection shows
there is dense texture-looking data at those offsets:

```text
gauntd24.raw @ 0x14a80880 = gei+0x11280
48688e8e 684f687e 7e7e8468 ...
```

The high pages do map to the next known payload, but their header metadata in
RAM is still bad:

```text
targetWord=0x00007d00 source=0xffffffff80322398 bgsrc=2:snm+0xc80(... hdr=bad)
targetWord=0x00007e00 source=0xffffffff80322598 bgsrc=2:snm+0xe80(... hdr=bad)
targetWord=0x00007f00 source=0xffffffff80322798 bgsrc=2:snm+0x1080(... hdr=bad)
```

Added a default-off length override for known BGLoadModel texture payloads:

```text
EUTHERDRIVE_GAUNTDL_EXPERIMENT_RUNTIME_BGLOADMODEL_TEXTURE_PAYLOAD_INDEX1_LENGTH=...
EUTHERDRIVE_GAUNTDL_EXPERIMENT_RUNTIME_BGLOADMODEL_TEXTURE_PAYLOAD_GEI_LENGTH=...
```

The first behavior probe combined:

```text
EUTHERDRIVE_GAUNTDL_EXPERIMENT_RUNTIME_BGLOADMODEL_TEXTURE_PAYLOAD_INDEX1_LENGTH=0x20000
EUTHERDRIVE_GAUNTDL_EXPERIMENT_ZERO_BASE_UPLOAD_DISK_WORDS=1
```

and was intentionally stopped after it failed to reach f420 in a useful time:

```text
/tmp/gauntdl-gei-extended-diskwords-f420.log
last progress: frame=300
no frame dump
exit=143 after manual stop
```

It proves disk data exists and can be substituted:

```text
zero-base-upload-disk-word addr=0xffffffff803129a4 1:gei@0x1128c mem=0x00000000->disk=0x687e6884
```

but it is a negative visual/fix direction. Raw disk bytes in that gap are being
fed back through command FIFO decode as texture packet words, producing massive
stale register noise:

```text
cmd=0xd6a46639 words=54949 packet=0x0000007c pc=800fe5d4
target=0xcc7 reg=0xc7 value=<texture-like bytes>
```

Current conclusion:

1. Do not promote a blind `gei` length extension or broad
   `ZERO_BASE_UPLOAD_DISK_WORDS` replacement.
2. The current zero-base upload run is crossing BGLoadModel payload boundaries:
   it starts in the gap after `gei`, then reaches `snm`, while the visible
   texture targets are written as one continuous Type5 run.
3. The next fix should be segment-aware: either split/stop zero-base upload
   runs at known BGLoadModel payload boundaries, or repair the source/index
   selector so the command FIFO receives Type5 payload structure and texture
   bytes in the correct roles.
4. Keep `TEXTURE_PAYLOAD_INDEX1_LENGTH` default-off as a diagnostic for mapping
   disk offsets, not as a renderer fix.

## 2026-07-05 - Zero-Base Prefix Packet Skip Probe

Added a default-off packet-level variant for the unknown-prefix experiment:

```text
EUTHERDRIVE_GAUNTDL_EXPERIMENT_ZERO_BASE_UPLOAD_SKIP_UNKNOWN_PREFIX_PACKETS=1
EUTHERDRIVE_GAUNTDL_EXPERIMENT_ZERO_BASE_UPLOAD_SKIP_UNKNOWN_PREFIX_PACKETS_MAX_BYTES=0x20000
```

The probe keeps baseline behavior unchanged unless the env is set. Instead of
only normalizing the starting source pointer, it skips whole fixed-size Type5
payload packets until the next known BGLoadModel payload is near the current
zero-base source.

f420 run:

```text
/tmp/gauntdl-prefix-packet-skip-f420.log
/tmp/gauntdl-prefix-packet-skip-f420.ppm
/tmp/gauntdl-prefix-packet-skip-f420.png
frameHash=0x8b4d205d
frameSha256=61d12e13c120936f1afff676ba78860edca00cabd1a97febb0675491d1dcd04c
framebuffer=640x480:307200:306847
textureMap=5932764:2588295:3344469:542400:0x000000:0x3e78dc
textured=tri:652:covered:375:rejected:277:pixels:32874387:zero:3322031
cmdstop=invalid-standard-window/0x00012609/.../pc=0xffffffff801066c4
```

Visual inspection is still negative for real Gauntlet graphics. It produces
large flat colored polygon fields and a few diagonal artifacts, not the scene.
The useful evidence is the skip position:

```text
source=0xffffffff80312998->0xffffffff80321798
next=0xffffffff80321718:2:snm
packets=238 bytes=0xee00 index=0->238/255 packet=0x00000000->0x0001dc00 words=64
```

That overshoots the known `snm` payload by `0x80` because the current fast path
can only move in whole 0x100-byte payload packets. The result confirms that the
current zero-base run is not just a harmless prefix problem: the source stream
and Type5 packet framing are out of phase across BGLoadModel payload boundaries.

Current conclusion:

1. Keep the packet skip default-off as a diagnostic only.
2. Do not promote whole-packet skipping to baseline; it improves raw coverage
   but still feeds stale/shifted texture bytes through FIFO command decode.
3. The next implementation candidate should be a segment-aware zero-base source
   splitter/remapper: stop or split a fast-path upload run when it reaches a
   known BGLoadModel payload boundary, then resume with that payload's native
   command/data alignment instead of forcing one continuous packet grid.
4. The verification target remains strict: a f420 frame must show recognizable
   model/scene graphics, not only non-black/colorful rasterization.

## 2026-07-05 - Zero-Base Stop At Known Boundary Probe

Added another default-off boundary diagnostic:

```text
EUTHERDRIVE_GAUNTDL_EXPERIMENT_ZERO_BASE_UPLOAD_STOP_AT_KNOWN_BOUNDARY=1
EUTHERDRIVE_GAUNTDL_EXPERIMENT_ZERO_BASE_UPLOAD_STOP_AT_KNOWN_BOUNDARY_MAX_BYTES=0x20000
```

This variant does not skip forward. It truncates the current zero-base fast-path
upload run before the next known BGLoadModel payload, so the run no longer
crosses from the unknown `gei` gap into `snm`.

f420 run:

```text
/tmp/gauntdl-boundary-stop-f420.log
/tmp/gauntdl-boundary-stop-f420.ppm
/tmp/gauntdl-boundary-stop-f420.png
frameHash=0xead9850e
frameSha256=582761465a2ddda3360ad074d8ba7a4fe9c36124a734b09bac55e9b300ce0729
framebuffer=640x480:307200:173051
textureMap=18297344:7263524:11033820:313172:0x000000:0x704284
textured=tri:981:covered:828:rejected:153:pixels:77609945:zero:12050628
cmdstop=invalid-standard-window/0x00012609/.../pc=0xffffffff801066c4
```

The boundary hit is exact:

```text
source=0xffffffff80312998
boundary=0xffffffff80321718:2:snm
packets=256->237 limit=255->236 bytes=0x10000->0xed00 dropped=0x1300
```

Visual inspection is negative: the frame is still horizontal colored stripes
with white fill, not real scene/model graphics. Truncating the run did not
repair the visible plane and did not remove the later invalid standard-window
stop.

Current conclusion:

1. Keep `STOP_AT_KNOWN_BOUNDARY` default-off as a traceable negative candidate.
2. The visible artifact does not come only from carrying too many packets into
   `snm`; it is already wrong before that boundary or the packet target/source
   pairing is wrong.
3. Next useful probe should compare the FIFO Type5 packet source word and
   payload memory side by side for the exact visible target buckets, especially
   whether the 0x200 packet-address stride is correct when `sourceBase==0`.

## 2026-07-05 - Zero-Base Packet Address Stride Probe

Added a default-off packet source address stride override for zero-base upload
runs:

```text
EUTHERDRIVE_GAUNTDL_EXPERIMENT_ZERO_BASE_UPLOAD_PACKET_ADDRESS_STRIDE=0x100
```

The experiment keeps the same payload bytes but changes the Type5 packet source
address calculation for `sourceBase==0` from `index * 0x200` to the requested
stride. This directly tests the observed mismatch where the payload cursor moves
by `0x100` bytes per 64-word packet while the Type5 target word advances as if
the packet source address moved by `0x200` bytes.

f420 run:

```text
/tmp/gauntdl-packetstride100-f420.log
/tmp/gauntdl-packetstride100-f420.ppm
/tmp/gauntdl-packetstride100-f420.png
frameHash=0x9bea8154
frameSha256=c159f41ac9bcae73246f7a1de86b03f955e7742858b666e962dec7da8257dfed
framebuffer=640x480:307200:175099
textureMap=18297344:7949314:10348030:175156:0x000000:0x704284
textured=tri:981:covered:828:rejected:153:pixels:77609945:zero:4150187
cmdstop=invalid-standard-window/0x00012609/.../pc=0xffffffff801066c4
```

The override hit both relevant zero-base runs:

```text
source=0xffffffff80312998 packetStride=0x100 index=0/31 words=64
source=0xffffffff802e2c68 packetStride=0x100 index=0/255 words=64
```

Visual inspection is still negative: horizontal colored stripes with white
fill, no recognizable Gauntlet scene/model graphics. The touched texture range
changed substantially (`313172 -> 175156` compared with the boundary-stop
run), and the zero-texture count improved, but the result remains the same
artifact family and still stops on invalid standard-window command decode.

Current conclusion:

1. Keep `ZERO_BASE_UPLOAD_PACKET_ADDRESS_STRIDE` default-off as a diagnostic.
2. The 0x200 packet-address stride is not the root visual blocker.
3. This reinforces the older zero-base payload conclusion: Type5 routing is
   faithfully consuming the data it is given, but the source payload itself is
   float/geometry/control-looking data rather than real texture bytes.
4. Next target should move upward to the source selector/hydration path that
   populates `0xffffffff80312998` and `0xffffffff802e2c68`, not further
   packet-address remapping.

## 2026-07-05 - Unknown Zero-Base Pointer-Start Probe

Added a default-off descriptor normalization probe:

```text
EUTHERDRIVE_GAUNTDL_EXPERIMENT_ZERO_BASE_UPLOAD_POINTER_START_UNKNOWN=1
```

Older pointer-start correction already handled known BGLoadModel sources whose
descriptor word at `source+0x08` points to `source+0x0c`. With the current
`EUTHERDRIVE_GAUNTDL_EXPERIMENT_RUNTIME_BGLOADMODEL_INDEXED_SOURCE_STRIDE=0x20000`
path, the critical `0xffffffff80312998` source is no longer classified as a
known source, so that correction was skipped. This probe allows the same exact
descriptor shape for unknown zero-base upload sources without changing the
default path.

Trace run proof:

```text
zero-base-upload-pointer-start-unknown source=0xffffffff80312998->0xffffffff803129a4 bgsrc=none bytes=0x2000 index=0/31 words=64 first=8012e528/00000000/803129a4/00000000
TEXUPLOAD-RUN ... source=0xffffffff803129a4 s6=0xffffffff80312998 sourceBase=0x00000000 index=0/255
```

f420 run:

```text
/tmp/gauntdl-pointerunknown-fast-f420.log
/tmp/gauntdl-pointerunknown-fast-f420.ppm
/tmp/gauntdl-pointerunknown-fast-f420.png
frameHash=0x6d791e91
frameSha256=1bbae73410456e3b595ce97970764a4bf1d2434f8f904ea72112c4031cf1a341
framebuffer=640x480:307200:307200
textureMap=17190656:7460189:9730467:323136:0x000000:0x704284
textured=tri:1013:covered:42:rejected:971:pixels:2711969:zero:38450
cmdstop=invalid-standard-window/0x00012609/.../pc=0xffffffff801066c4
```

Visual inspection is still negative: the frame is two large flat colored
polygons, not recognizable Gauntlet scene/model graphics. The probe is still
useful because it restores an older descriptor-start invariant under the newer
0x20000 source-stride experiment and produces a distinct frame hash instead of
the stripe artifact family.

Current conclusion:

1. Keep `ZERO_BASE_UPLOAD_POINTER_START_UNKNOWN` default-off until the upstream
   source hydration problem is understood.
2. The descriptor pointer word was a real local bug in the current experiment,
   but fixing it alone does not produce real graphics.
3. The next target is the payload content behind `0xffffffff803129a4` under the
   0x20000 stride path: current trace shows zero/stale-looking words after the
   descriptor, while older notes expected texture-looking words there.

## 2026-07-05 - Focused GEI Zero-Disk-Word Probe

Ran the existing zero-disk-word diagnostic against the newly normalized
`0xffffffff803129a4` upload run only:

```text
EUTHERDRIVE_GAUNTDL_EXPERIMENT_RUNTIME_BGLOADMODEL_INDEXED_SOURCE_STRIDE=0x20000
EUTHERDRIVE_GAUNTDL_EXPERIMENT_RUNTIME_BGLOADMODEL_TEXTURE_PAYLOAD_INDEX1_LENGTH=0x20000
EUTHERDRIVE_GAUNTDL_EXPERIMENT_ZERO_BASE_UPLOAD_POINTER_START_UNKNOWN=1
EUTHERDRIVE_GAUNTDL_EXPERIMENT_ZERO_BASE_UPLOAD_ZERO_DISK_WORD_INDEX_MASK=0x2
EUTHERDRIVE_GAUNTDL_EXPERIMENT_ZERO_BASE_UPLOAD_ZERO_DISK_WORD_MIN_OFFSET=0x1128c
EUTHERDRIVE_GAUNTDL_EXPERIMENT_ZERO_BASE_UPLOAD_ZERO_DISK_WORD_MAX_OFFSET=0x1ffff
EUTHERDRIVE_GAUNTDL_EXPERIMENT_ZERO_BASE_UPLOAD_ZERO_DISK_WORD_RUN_SOURCE=0xffffffff803129a4
```

This proves the post-descriptor zeros can be mapped to dense `gei` disk bytes:

```text
zero-base-upload-zero-disk-word addr=0xffffffff803129a4 1:gei@0x1128c mem=0x00000000->disk=0x687e6884 packet=0 index=0/31 word=0/64
```

f420 run:

```text
/tmp/gauntdl-pointerunknown-gei-zero-disk-f420.log
/tmp/gauntdl-pointerunknown-gei-zero-disk-f420.ppm
/tmp/gauntdl-pointerunknown-gei-zero-disk-f420.png
frameHash=0x6d791e91
frameSha256=1bbae73410456e3b595ce97970764a4bf1d2434f8f904ea72112c4031cf1a341
framebuffer=640x480:307200:307200
textureMap=10516480:6019596:4496884:325600:0x000000:0x70f3fc
textured=tri:961:covered:20:rejected:941:pixels:863832:zero:82537
cmdstop=invalid-standard-window/0x00012609/.../pc=0xffffffff801066c4
```

Visual inspection is negative and identical to the prior pointer-start run:
two large flat colored polygons, no recognizable scene/model graphics. The
frame hash and SHA are also identical even though draw/texture counters changed.
The log again shows command-FIFO register noise when raw disk texture-looking
bytes are substituted into the upload stream:

```text
cmd=0x57494639 words=22346 packet=0x00000004 pc=0xffffffff800fe5d4
```

Current conclusion:

1. Do not promote zero-disk-word replacement for the `803129a4` run.
2. The missing zeros are not the direct visible blocker; raw `gei` bytes at
   `0x1128c+` are not valid command/FIFO payload structure for this run.
3. Next target should identify the producer of the `80312998/803129a4`
   descriptor and its intended source/extent, rather than substituting disk
   bytes at upload-read time.

## 2026-07-05 - Asset Pointer Normalize Skip Probe

Added a default-off mask for the BGLoadModel asset pointer normalize repair:

```text
EUTHERDRIVE_GAUNTDL_EXPERIMENT_RUNTIME_BGLOADMODEL_ASSET_POINTER_NORMALIZE_SKIP_INDEX_MASK=...
```

The immediate target was asset-table index 9 (`0x200`), because the f420 byte
dump showed the hot `0xffffffff80312998` upload window is a descriptor/control
structure, not a texture stream:

```text
bytes[0xffffffff80312998]:
+0x000: 28 e5 12 80 00 00 00 00 a4 29 31 80 00 00 00 00
+0x010: 00 00 09 00 68 00 00 00 88 17 2e 80 00 00 6e 06
```

The cold f180 wide memory trace also exposed the relevant producer sequence.
Important note: `EUTHERDRIVE_GAUNTDL_TRACE_MEM_ADDRESS` parses the address as
hex, but the optional length is decimal, so use `80312998:320`, not
`80312998:0x140`.

```text
/tmp/gauntdl-producer-cold-f180-wide.log
pc=ffffffff8004c850 write32 ffffffff80312998 8012e528
pc=ffffffff8004c858 write32 ffffffff803129a0 803129a4
pc=ffffffff800af344 write32 ffffffff803129a0 00000000
pc=ffffffff800af34c write8  ffffffff803129a4 00000000
pc=ffffffff800af34c write8  ffffffff803129a5 00000000
pc=ffffffff800a6284 write32 ffffffff803129a8 00090000
pc=ffffffff800a6288 write32 ffffffff803129ac 00000068
```

That explains why the unknown pointer-start probe sees a valid descriptor shape
but still lands on zero/control data instead of the older texture-looking
`07e3fc01...` words.

f420 run with index 9 skipped:

```text
/tmp/gauntdl-skip-fontstory-normalize-f420.log
/tmp/gauntdl-skip-fontstory-normalize-f420.ppm
/tmp/gauntdl-skip-fontstory-normalize-f420.png
frameHash=0x6d791e91
frameSha256=1bbae73410456e3b595ce97970764a4bf1d2434f8f904ea72112c4031cf1a341
framebuffer=640x480:307200:307200
textureMap=17190656:7460189:9730467:323136:0x000000:0x704284
```

Visual inspection is negative and identical to the previous pointer-start
frame: two large flat colored polygons, no recognizable Gauntlet graphics.
Skipping index 9 normalization logs `credits`/`font_story` skips, but the final
hot `80312998` bytes, hash, and visible output do not move.

Current conclusion:

1. Keep the asset-pointer skip mask default-off as a diagnostic only.
2. Index 9 asset pointer normalization is not the direct cause of the current
   two-field visual artifact.
3. The stronger next target is the caller/source selector around
   `800af328..800a632c` and `800fe5d4`: the upload service is being handed a
   descriptor/control structure as if it were a texture payload run.

## 2026-07-05 - Broad Zero-Base Run Skip Recheck

Ran the pointer-start experiment again, this time with a deliberately broad
zero-base texture payload skip:

```text
EUTHERDRIVE_GAUNTDL_EXPERIMENT_RUNTIME_BGLOADMODEL_INDEXED_SOURCE_STRIDE=0x20000
EUTHERDRIVE_GAUNTDL_EXPERIMENT_ZERO_BASE_UPLOAD_POINTER_START_UNKNOWN=1
EUTHERDRIVE_GAUNTDL_EXPERIMENT_SKIP_ZERO_BASE_TEXTURE_PAYLOAD_RUNS=1
```

Artifacts:

```text
/tmp/gauntdl-skip-zero-base-runs-f420.log
/tmp/gauntdl-skip-zero-base-runs-f420.ppm
/tmp/gauntdl-skip-zero-base-runs-f420.png
```

The skipped runs all hit the same normalized source/control address observed
in the producer trace:

```text
skip-zero-base-texture-payload-run source=0xffffffff803129a4 bgsrc=none sourceBase=0x00000000 packet=0x00000000 index=0/31 words=64 packets=32
skip-zero-base-texture-payload-run source=0xffffffff803129a4 bgsrc=none sourceBase=0x00000000 packet=0x00000000 index=0/255 words=64 packets=256
```

f420 result:

```text
frameHash=0x86a50545
frameSha256=a8d81d4b4c363d24c010be1bc22254fa1d2bd65871dc0323e59eff2292e6ac12
framebuffer=640x480:33600:33600
drawPackets=23645 directTriangles=733 setupTriangles=346 texWrites=1573477
textureMap=5861888:2312555:3549333:93696:0x000000:0x7e7d9c
textured=tri:834:covered:817:rejected:17:pixels:76043864:zero:13415165
cmdstop=invalid-standard-window/0x00012609/.../pc=0xffffffff801066c4
```

Visual inspection is meaningfully different but still not correct. The old
full-screen two-field artifact is gone; the frame is mostly black with a red
vertical strip on the right edge. Non-black pixels drop from full-screen
`307200` to `33600`, so the zero-base runs are directly responsible for the
dominant visible artifact. However, skipping all such runs also removes nearly
all content.

Current conclusion:

1. Do not treat broad zero-base skipping as a fix.
2. The next useful experiment should split or classify the `803129a4`
   zero-base runs instead of blindly suppressing them all.
3. Start with packet-window filters, especially separating the `index=0/31`
   runs from the `index=0/255` runs, then compare frame hashes and visible
   output. If one class removes the full-screen artifact while preserving more
   geometry, use that class as the next producer-trace target.

## 2026-07-05 - Zero-Base Packet-Window Split Probe

Added a default-off packet-count filter to the broad zero-base skip:

```text
EUTHERDRIVE_GAUNTDL_EXPERIMENT_SKIP_ZERO_BASE_TEXTURE_PAYLOAD_RUN_PACKETS=...
```

When the existing
`EUTHERDRIVE_GAUNTDL_EXPERIMENT_SKIP_ZERO_BASE_TEXTURE_PAYLOAD_RUNS=1` flag is
enabled, the optional packet-count filter restricts the skip to runs with a
matching packet count. This keeps the broad skip available unchanged when the
filter is unset.

Common run setup:

```text
EUTHERDRIVE_GAUNTDL_BRINGUP_BASELINE=1
EUTHERDRIVE_GAUNTDL_WARMUP_STATE=/tmp/eutherdrive-gauntlet-probe/gauntdl-gauntdl24-fast-raw-f180-s200000-e27b9a6b6d3d.warm
EUTHERDRIVE_GAUNTDL_WARMUP_FRAMES=180
EUTHERDRIVE_GAUNTDL_FRAME_CHECKPOINTS=420
EUTHERDRIVE_GAUNTDL_SUMMARY=1
EUTHERDRIVE_GAUNTDL_EXPERIMENT_RUNTIME_BGLOADMODEL_INDEXED_SOURCE_STRIDE=0x20000
EUTHERDRIVE_GAUNTDL_EXPERIMENT_ZERO_BASE_UPLOAD_POINTER_START_UNKNOWN=1
EUTHERDRIVE_GAUNTDL_EXPERIMENT_SKIP_ZERO_BASE_TEXTURE_PAYLOAD_RUNS=1
```

256-packet-only skip:

```text
EUTHERDRIVE_GAUNTDL_EXPERIMENT_SKIP_ZERO_BASE_TEXTURE_PAYLOAD_RUN_PACKETS=256
/tmp/gauntdl-skip-zero-base-packets256-f420.log
/tmp/gauntdl-skip-zero-base-packets256-f420.ppm
/tmp/gauntdl-skip-zero-base-packets256-f420.png
skip lines=64
frameHash=0x51aaa3c7
frameSha256=9509b95ef9ef667701cf841a0172f66e9bd265dd906b1329bffe6cdaca8d1550
framebuffer=640x480:307200:306975
drawPackets=22725 directTriangles=886 setupTriangles=422 texWrites=1627045
textureMap=6076160:2311063:3765097:37184:0x000000:0x50fffc
textured=tri:14016:covered:1546:rejected:12470:pixels:158684996:zero:40763789
```

Visual inspection: still full-screen wrong graphics. It becomes a magenta field
with horizontal stripe texture on the left, not the mostly-black broad-skip
frame.

32-packet-only skip:

```text
EUTHERDRIVE_GAUNTDL_EXPERIMENT_SKIP_ZERO_BASE_TEXTURE_PAYLOAD_RUN_PACKETS=32
/tmp/gauntdl-skip-zero-base-packets32-f420.log
/tmp/gauntdl-skip-zero-base-packets32-f420.ppm
/tmp/gauntdl-skip-zero-base-packets32-f420.png
skip lines=45
frameHash=0xec1494b8
frameSha256=705b0448853aed3c3504a5d35e07ea9ac27283f81719f5d09e8a451667386e72
framebuffer=640x480:307200:307200
drawPackets=20845 directTriangles=4186 setupTriangles=2079 texWrites=4758181
textureMap=18600704:8019012:10581692:369472:0x000000:0x7af22c
textured=tri:12311:covered:1436:rejected:10875:pixels:136717806:zero:38854959
```

Visual inspection: still full-screen wrong graphics. It becomes the old
two-field diagonal yellow/magenta style.

Current conclusion:

1. Packet count alone is not the correct classifier.
2. Both `packets=32` and `packets=256` classes contribute bad visual state when
   the other class is allowed through.
3. The broad skip only improves the visible artifact when both classes are
   suppressed, which proves the `803129a4` source family is bad but does not
   identify a safe subset to drop.
4. Next selector should key on producer/caller or payload structure instead of
   packet count. The producer trace still points at the handoff around
   `800af328..800a632c` and the later FIFO write path at `800fe5d4`.

## 2026-07-05 - Descriptor Source Pointer Offset Probe

Added two default-off descriptor offset probes for unknown zero-base upload
runs:

```text
EUTHERDRIVE_GAUNTDL_EXPERIMENT_ZERO_BASE_UPLOAD_DESCRIPTOR_SOURCE_POINTER_OFFSET=...
EUTHERDRIVE_GAUNTDL_EXPERIMENT_ZERO_BASE_UPLOAD_DESCRIPTOR_PACKET_ADDRESS_OFFSET=...
```

These are diagnostic only. They apply after the caller has selected a zero-base
source that is not already one of the known indexed BGLoadModel upload regions.
The source pointer offset rewrites the upload source from a descriptor field;
the packet address offset optionally rewrites the current packet/target address
from another descriptor field.

Negative control: disabling the whole outer-payload fastpath is not useful.

```text
EUTHERDRIVE_GAUNTDL_EXPERIMENT_DISABLE_OUTER_PAYLOAD_FASTPATH=1
/tmp/gauntdl-disable-outer-payload-fastpath-f220.log
frame=220
frameHash=0xd1549bb3
frameSha256=fffa25c1da2cdbfc1c1c68503ef1524e30fc7a59a28597aea75a1863f95aac24
textureMap=0:0:0:0:0x000000:0x000000
cmdstop=invalid-standard-window/0xbc292a85/.../pc=0xffffffff800c4e5c
```

The interpreter fallback loses texture upload progress early, so do not pursue
blanket fastpath disable as a graphics fix.

Descriptor source pointer offset `0x18`:

```text
EUTHERDRIVE_GAUNTDL_EXPERIMENT_ZERO_BASE_UPLOAD_DESCRIPTOR_SOURCE_POINTER_OFFSET=0x18
/tmp/gauntdl-descriptor-source-offset18-f420.log
/tmp/gauntdl-descriptor-source-offset18-f420.ppm
/tmp/gauntdl-descriptor-source-offset18-f420.png

zero-base-upload-descriptor-source-pointer
source=0xffffffff80312998->0xffffffff802e1788
offset=0x18
descriptor=8012e528/00000000/803129a4/00000000/00090000/00000068/802e1788/00000000

frameHash=0xbbe7fc19
frameSha256=f8b8b29b9ffb37d65f38b45fa4b3552b42bc52e54561572d0883892696d1b8cc
framebuffer=640x480:307200:307200
drawPackets=23239 directTriangles=817 setupTriangles=393 texWrites=4930597
textureMap=19290368:8366331:10924037:520182:0x000000:0x3efffc
textured=tri:14544:covered:1137:rejected:13407:pixels:102135844:zero:34832345
```

Visual inspection is still wrong: mostly dark/olive full-screen coverage with a
magenta vertical strip on the right and a small dark-blue corner wedge. This is
not Gauntlet scene graphics. It is nevertheless a stronger signal than the
`803129a4` zero/control source because it restores millions of texture writes.

Descriptor source pointer offset `0x18` plus packet address offset `0x10`:

```text
EUTHERDRIVE_GAUNTDL_EXPERIMENT_ZERO_BASE_UPLOAD_DESCRIPTOR_SOURCE_POINTER_OFFSET=0x18
EUTHERDRIVE_GAUNTDL_EXPERIMENT_ZERO_BASE_UPLOAD_DESCRIPTOR_PACKET_ADDRESS_OFFSET=0x10
/tmp/gauntdl-descriptor-source18-packet10-f420.log
/tmp/gauntdl-descriptor-source18-packet10-f420.ppm
/tmp/gauntdl-descriptor-source18-packet10-f420.png

zero-base-upload-descriptor-packet-address
source=0xffffffff80312998->0xffffffff802e1788
offset=0x10
packet=0x00000000->0x00090000

frameHash=0xbbe7fc19
frameSha256=f8b8b29b9ffb37d65f38b45fa4b3552b42bc52e54561572d0883892696d1b8cc
framebuffer=640x480:307200:307200
drawPackets=23239 directTriangles=817 setupTriangles=393 texWrites=4930597
textureMap=19290368:8366331:10924037:520182:0x000000:0x3efffc
textured=tri:14544:covered:1137:rejected:13407:pixels:102135844:zero:33402761
```

Visual inspection is identical to source-offset-only, and the frame hash/SHA
are identical. The packet-address offset changes texture sampling/accounting
slightly but does not change the selected frame.

Current conclusion:

1. The descriptor layout is now partially confirmed:
   `+0x08 -> 803129a4`, `+0x10 -> 00090000`, `+0x18 -> 802e1788`.
2. `+0x18` is a real source candidate and is closer to the BGLoadModel payload
   family, but it is not sufficient by itself.
3. `+0x10` is not enough as a packet-address repair for the selected frame.
4. Next useful probe should inspect the source words at `802e1788` and nearby
   descriptor fields (`+0x14`, `+0x1c`, and later floats) to derive the correct
   run extent/stride rather than forcing the whole `index=0/255` run through a
   single body pointer.

Follow-up byte dumps from the f180 warm state:

```text
/tmp/gauntdl-descriptor-source18-bytes-f180.log
/tmp/gauntdl-descriptor-nested-candidates-bytes-f180.log

bytes[0xffffffff802e1788]:
+0x000: 01 00 00 00 04 00 00 00 ...
+0x01c: 68 27 31 80
+0x020: f8 17 2e 80
+0x090: 68 18 2e 80

bytes[0xffffffff802e17f8]:
+0x000: 01 00 00 00 00 00 00 00 ...
+0x020: 68 18 2e 80
+0x090: b8 19 2e 80

bytes[0xffffffff80312768]:
+0x000: 01 00 00 00 00 00 00 00 ...
+0x018: 88 17 2e 80
+0x020: d8 27 31 80
+0x088: 88 17 2e 80

bytes[0xffffffff802e1868]:
+0x000: 04 00 00 00 04 00 00 00 ...
+0x020: b8 19 2e 80
+0x090: 48 19 2e 80
```

This makes the descriptor-source result clearer: `802e1788` is not raw texture
payload. It is a model/scenegraph-style node with child pointers and matrix-like
float blocks. Blindly dereferencing one more node is unlikely to produce stable
graphics; the next target should find the node-to-material/texture payload link
or the real upload run extent generated from this node tree.

## 2026-07-05 - Descriptor Node and Source-Table Probes

Added two default-off descriptor source probes:

```text
EUTHERDRIVE_GAUNTDL_EXPERIMENT_ZERO_BASE_UPLOAD_DESCRIPTOR_SOURCE_NODE_POINTER_OFFSET=...
EUTHERDRIVE_GAUNTDL_EXPERIMENT_ZERO_BASE_UPLOAD_DESCRIPTOR_SOURCE_TABLE_DERIVED_SOURCE=1
```

The node-pointer probe applies after:

```text
EUTHERDRIVE_GAUNTDL_EXPERIMENT_ZERO_BASE_UPLOAD_DESCRIPTOR_SOURCE_POINTER_OFFSET=0x18
```

and lets the descriptor source `80312998 + 0x18 -> 802e1788` optionally follow a
field inside that node. The source-table probe decodes descriptor word
`+0x10` as `assetIndex:localIndex` and mirrors the producer code at
`800a6240..800a6290`:

```text
source = sourceTable[assetIndex] + localIndex * 0x8c + 0x68
```

Evidence from the focused producer trace:

```text
/tmp/gauntdl-descriptor-producer-regs-f420.log
pc=800a6254 reads sourceTable[9] from 802529c4
s1=0x000948ef local=0x48ef sourceTable[9]=80312998
computed source=80312998 + 0x27e31c = 80590cb4

final sourceTable bytes at f420:
802529c4 = 802e2c68
hot descriptor:
80312998 +0x10 = 00090000
80312998 +0x14 = 00000068
80312998 +0x18 = 802e1788
```

Screened node pointer offsets from the f180 warm state to f420:

```text
node+0x1c:
/tmp/gauntdl-descriptor-node1c-f420-fast.log
/tmp/gauntdl-descriptor-node1c-f420.ppm
/tmp/gauntdl-descriptor-node1c-f420.png
frameHash=0x53011381
frameSha256=699c017a0b826bb2852c4434f883f1952c4b0ada558573fe706532913a271c53
framebuffer=640x480:307200:292693
drawPackets=22645 directTriangles=1285 setupTriangles=605 texWrites=4558693
textureMap=17802752:8231317:9571435:539960:0x000000:0x78fffc
colors=5

node+0x20:
/tmp/gauntdl-descriptor-node20-f420.log
/tmp/gauntdl-descriptor-node20-f420.ppm
/tmp/gauntdl-descriptor-node20-f420.png
frameHash=0xfc5e919c
frameSha256=b6548a051f9315e4deaf5e520d532197a4576bc6d9a0933eed502674d64688c4
framebuffer=640x480:246400:72704
drawPackets=23969 directTriangles=335 setupTriangles=136 texWrites=5345381
textureMap=20949504:9071196:11878308:515768:0x000000:0x3efffc
colors=114

node+0x90:
/tmp/gauntdl-descriptor-node90-f420.log
/tmp/gauntdl-descriptor-node90-f420.ppm
/tmp/gauntdl-descriptor-node90-f420.png
frameHash=0xf6255035
frameSha256=e7d0ba6972297ac9209a184dfacdc22ce48afd740b713fe6cb7a4286e62cd5b0
framebuffer=640x480:307200:307200
drawPackets=23627 directTriangles=1803 setupTriangles=888 texWrites=5575653
textureMap=21870592:7850291:14020301:701760:0x000000:0x52a164
colors=3
```

The source-table-derived candidate was also negative:

```text
EUTHERDRIVE_GAUNTDL_EXPERIMENT_ZERO_BASE_UPLOAD_DESCRIPTOR_SOURCE_TABLE_DERIVED_SOURCE=1
EUTHERDRIVE_GAUNTDL_EXPERIMENT_ZERO_BASE_UPLOAD_DESCRIPTOR_PACKET_ADDRESS_OFFSET=0x10
/tmp/gauntdl-descriptor-table-source-f420.log
/tmp/gauntdl-descriptor-table-source-f420.ppm
/tmp/gauntdl-descriptor-table-source-f420.png
frameHash=0xa796a405
frameSha256=87d2bc6bcdc34b9c20b9b8f53bc8c61895a79e0812fb205324ae351300850092
framebuffer=640x480:307200:307200
drawPackets=23819 directTriangles=4085 setupTriangles=2003 texWrites=5272101
textureMap=20656384:9102081:11554303:550464:0x000000:0x3dfffc
colors=2
```

Visual inspection:

1. `node+0x1c` is a flat dark/olive field with magenta strip.
2. `node+0x20` has more colors but only horizontal stripe artifacts.
3. `node+0x90` is a flat green field.
4. source-table-derived source is a flat pale/pink field.

Current conclusion:

1. The node fields are real scenegraph child/sibling links. They are not raw
   texture payloads.
2. The constructor/source-table formula is real, but applying it at upload time
   still produces a flat artifact; it is not the final source repair.
3. Keep both probes default-off as useful diagnostics.
4. The next target should trace the descriptor consumer's intended run extent
   and source selection before `800fe5d4`, especially why the upload service
   repeats `index=0/255` zero-base runs from the same descriptor instead of a
   bounded material/texture-body span.

## 2026-07-05 - Descriptor Selector Setup Trace

Added a default-off trace for the caller/setup window before the upload helper
consumes `sp+0x6c`:

```text
EUTHERDRIVE_GAUNTDL_TRACE_TEXTURE_UPLOAD_SOURCE_SELECTOR_SETUP=1
EUTHERDRIVE_GAUNTDL_TRACE_TEXTURE_UPLOAD_SOURCE_SELECTOR_SETUP_PC_MIN=ffffffff800fe180
EUTHERDRIVE_GAUNTDL_TRACE_TEXTURE_UPLOAD_SOURCE_SELECTOR_SETUP_PC_MAX=ffffffff800fe228
```

The trace marker is:

```text
[GAUNTDL:TEXUPLOAD-SOURCE-SETUP]
```

The pointer-start unknown recheck stayed negative:

```text
/tmp/gauntdl-pointerstartunknown-f420.log
/tmp/gauntdl-pointerstartunknown-f420.png
frameHash=0x5ad95612
framebuffer=640x480:307200:307200
drawPackets=22813 directTriangles=3923 setupTriangles=1957 texWrites=4405669
textureMap=17190656:7460189:9730467:320416:0x000000:0x704284
colors=2
```

Visual inspection is the same two-field yellow/magenta artifact. Starting the
unknown descriptor at `803129a4` is therefore not a visible fix.

The new setup trace answers the previous selector question: the source and
extent are already present when `800fe1fc` enters the helper. The prologue
switches from the caller stack to a wrapper frame whose selector slots are
already populated:

```text
pc=800fe1fc addiu sp,-0x50
post sp68=00000003 sp6c=80312998 sp70=00000000 sp74=0000001f
post sp60=00000003 sp64=00000000 sp68=00000003 sp6c=80312998 sp74=000000ff
```

The caller trace around `801095c0..80109708` resolves the immediate producer:

```text
/tmp/gauntdl-caller801095c8-source80312998-f300.log

801095c0 jal 801096ac
  caller sp+0x1c already equals 80312998

801096c0 lw t2,0x4c(sp)
  loads the source pointer from the shifted caller argument slot

801096f0 sw t2,0x1c(sp)
  stores the source slot later consumed as wrapper sp+0x6c

801096dc lw v0,0x04(v0)
801096f4 addiu v0,-1
80109700 sw v0,0x24(sp)
  stores the limit later consumed as wrapper sp+0x74
```

The `0x1f` and `0xff` extents are table-driven, not a local stack corruption.
For the `0xff` case, `801096ac` computes a table address around `80158128`,
loads `0x100`, subtracts one, and stores `0xff`. The hot `80312998` source also
appears immediately after `bgloadmodel-asset-pointer-normalize ... index=9 ...
name=font_story`.

Current conclusion:

1. `800fe1fc` and `800fe5d4` are downstream consumers for this descriptor run.
2. The repeated `index=0/31` and `index=0/255` limits are intentional-looking
   table selections, so packet count alone cannot classify the bad runs.
3. The next target should move one level earlier than the helper call: trace
   the source/limit table selection around `801096ac` and the asset-table
   producer path for `font_story`, then connect that to the zero-base Type5
   upload source ownership.

## 2026-07-05 - Indexed Source State Trace and Descriptor Offset Negatives

Added a default-off indexed BGLoadModel source trace so we can inspect the real
source-table slot instead of the old hardcoded `geb`/index-6 view:

```text
EUTHERDRIVE_GAUNTDL_TRACE_BGLOADMODEL_INDEXED_SOURCE_STATE=1
EUTHERDRIVE_GAUNTDL_TRACE_BGLOADMODEL_INDEXED_SOURCE_STATE_INDEX=9
EUTHERDRIVE_GAUNTDL_TRACE_BGLOADMODEL_INDEXED_SOURCE_STATE_LIMIT=96
```

The trace marker is:

```text
[GAUNTDL:TRACE] bgloadmodel-indexed-source-state
```

The filtered f300 run:

```text
/tmp/gauntdl-index9-source-state-filtered-f300.log
frameHash=0x578ddca1
drawPackets=18674 directTriangles=308 setupTriangles=139
textureMap=8487424:3859486:4627938:47168:0x000000:0x1900cc
framebuffer=640x480:249131:248011
```

Key source-chain result:

```text
802529c4:80312a08 -> 80312a08
  assetEntry=8024fb50:80312a08/0002006f/00000000/"font_story"

after-path-lookup / size-alloc:
802529c4:80312998 -> 80312998
  sourceWords=8012e528/00000000/803129a4/00000000

after parser/normalize:
assetEntry=8024fb50:80312998/00000000/00000000/"font_story"
```

So the hot upload source is not born inside `800fe5d4`; the source-table slot
for index 9 is changed from the earlier `80312a08` body/source to the
`80312998` descriptor before the upload helper consumes it. The asset-table
entry also loses the previous `0002006f` length after the parser pass.

Added a second default-off zero-base upload experiment to test descriptor-local
source offsets without changing default behavior:

```text
EUTHERDRIVE_GAUNTDL_EXPERIMENT_ZERO_BASE_UPLOAD_DESCRIPTOR_SOURCE_ADD_OFFSET=0xNN
```

Two visible f420 controls are negative:

```text
/tmp/gauntdl-descriptor-add70-f420.log
/tmp/gauntdl-descriptor-add70-f420.png
SOURCE_ADD_OFFSET=0x70
frameHash=0x44238545
drawPackets=23868 directTriangles=3406 setupTriangles=1687
textureMap=19717632:8653069:11064563:314934:0x000000:0x60fffc
framebuffer=640x480:16447:16393
colors=6
visual=mostly black with magenta/cyan artifact

/tmp/gauntdl-descriptor-add68-f420.log
/tmp/gauntdl-descriptor-add68-f420.png
SOURCE_ADD_OFFSET=0x68
frameHash=0xd6f942b3
drawPackets=23797 directTriangles=6614 setupTriangles=3271
textureMap=17629952:7666944:9963008:175104:0x000000:0x38fffc
framebuffer=640x480:307200:307200
colors=2
visual=full-screen blue/green artifact
```

The `0x70` trace confirms the redirect itself works:

```text
zero-base-upload-descriptor-source-add-offset
source=80312998->80312a08 offset=0x70
descriptor=8012e528/00000000/803129a4/00000000/00090000/00000068/802e1788/00000000
first=00000001/00000004/00000000/00000000
```

Current conclusion:

1. The old `geb` trace was misleading for this question because it read slot 6
   while the active caller index was 9. Use
   `TRACE_BGLOADMODEL_INDEXED_SOURCE_STATE_INDEX=9` for future `font_story`
   work.
2. Blindly using descriptor-local offsets `+0x68` or `+0x70` is not a visible
   fix. The payload head still looks like metadata, and the output remains a
   low-color artifact.
3. The next useful target is the helper path that changes index-9 source state
   before `800aae60`/`800b72fc`: preserve or reconstruct the asset table's
   `80312a08/0002006f` ownership through the parser, rather than changing the
   Voodoo upload read cursor after the descriptor has already been selected.

## 2026-07-05 - Index-9 Asset Source Preserve Negative

Added a default-off source ownership probe before the parser caller consumes
index 9:

```text
EUTHERDRIVE_GAUNTDL_EXPERIMENT_RUNTIME_BGLOADMODEL_PRESERVE_ASSET_SOURCE_INDEX_MASK=0x200
```

The probe fires at `800aae60`/`800aae98` and writes the current asset-table
source back into `802529a0 + index * 4`, also updating `s2`. The f420 run proves
that preserving `80312a08/0002006f` through this caller point is not a visible
fix:

```text
/tmp/gauntdl-preserve-asset-source9-f420.log
/tmp/gauntdl-preserve-asset-source9-f420.png

bgloadmodel-preserve-asset-source
pc=800aae60 index=9 slot=802529c4:80312998->80312a08
asset=8024fb50:80312a08/0002006f/00000000/"font_story"
sourceWords=00000001/00000004/00000000/00000000...

frameHash=0x5ad95612
drawPackets=23431 directTriangles=2961 setupTriangles=1469
textureMap=18297344:7949314:10348030:299060:0x000000:0x704284
framebuffer=640x480:307200:307200
colors=2
visual=full-screen yellow/magenta artifact
```

Current conclusion:

1. Merely preserving the `80312a08` asset source into the parser path is as
   negative as moving the upload cursor to `+0x70`.
2. The `80312a08` region is still a metadata/header-like structure
   (`00000001/00000004/...`), not the texture payload the Voodoo upload should
   consume.
3. The next target should move one layer deeper into the asset object's internal
   body/payload pointer fields or QIO body completion for index 9, instead of
   preserving either `80312998` or `80312a08` as the direct upload source.

## 2026-07-05 - Zero-Base Upload Run Classifier

Rechecked the existing QIO body-read experiment because it was present in code
but had no current progress-plan result:

```text
EUTHERDRIVE_GAUNTDL_EXPERIMENT_RUNTIME_BGLOADMODEL_INDEXED_TEXTURE_QIO_BODY_READ=1
EUTHERDRIVE_GAUNTDL_TRACE_BGLOADMODEL_INDEXED_QIO_OBJECT_STATE=1
EUTHERDRIVE_GAUNTDL_TRACE_TEXTURE_UPLOAD_PAYLOAD=1

/tmp/gauntdl-bodyread-f420.log
/tmp/gauntdl-bodyread-f420.ppm
/tmp/gauntdl-bodyread-f420.png
```

The run did not emit `bgloadmodel-indexed-texture-qio-body-read`; it only hit
the existing stream-limit/object-metadata paths. The visible result stayed in
the same artifact family:

```text
frameHash=0x5ad95612
drawPackets=23431 directTriangles=2961 setupTriangles=1469
textureMap=18297344:7949314:10348030:299060:0x000000:0x704284
framebuffer=640x480:307200:307200
colors=2
```

The payload trace is still useful because it proves the upload stream is
consuming descriptor/scenegraph content, not texture bytes:

```text
packet=0 source=80312998 first=8012e528/00000000/803129a4/00000000
packet=9 source=80313298 first=5f53454a/494b4e50/5349564e/00003223 text="JES_PNKINVIS#2"
```

Added a default-off zero-base upload classifier:

```text
EUTHERDRIVE_GAUNTDL_TRACE_TEXTURE_UPLOAD_ZERO_BASE_RUN_CLASSIFIER=1
EUTHERDRIVE_GAUNTDL_TRACE_TEXTURE_UPLOAD_ZERO_BASE_RUN_CLASSIFIER_LIMIT=80
```

The trace marker is:

```text
[GAUNTDL:TEXUPLOAD-ZEROBASE-CLASS]
```

It deduplicates by source/packet-address/packet-count/payload-word-count and
prints descriptor words, the first source words, basic sample statistics, and
the next known BGLoadModel source boundary.

Focused f300 run:

```text
/tmp/gauntdl-zerobase-classifier-dedup-f300.log
frameHash=0x578ddca1
drawPackets=18674 directTriangles=308 setupTriangles=139
textureMap=8487424:3859486:4627938:47168:0x000000:0x1900cc
framebuffer=640x480:249131:248011
```

The deduplicated run list is decisive:

```text
class=descriptor source=80312998 packet=0 index=0/31 packets=32 words=64 bytes=0x2000
  sampleWords=512 unique=104 zero=209 ptr=62 float=209 asciiBytes=246
  nextKnown=none
  descriptor=8012e528/00000000/803129a4/00000000/00090000/00000068/802e1788/00000000

class=descriptor source=80312998 packet=0 index=0/255 packets=256 words=64 bytes=0x10000
  sampleWords=512 unique=104 zero=209 ptr=62 float=209 asciiBytes=246
  nextKnown=80321718:2:snm/+0xed80
  descriptor=8012e528/00000000/803129a4/00000000/00090000/00000068/802e1788/00000000
```

Current conclusion:

1. The bad zero-base uploads are not mixed texture/control subsets in this
   window. The only distinct runs are the same `80312998` descriptor, with
   32-packet and 256-packet extents.
2. The descriptor is being sent directly as Type5 texture payload. Downstream
   offset/pointer fixes and preserving `80312a08` only change which control or
   scenegraph bytes get uploaded; they do not produce real graphics.
3. The next target should move one call level before `800fe1fc/800fe5d4` and
   repair or trace the source/limit table selection around `801096ac`: why the
   caller chooses `80312998` as a texture payload source instead of deriving a
   bounded material/texture body span from the descriptor.

## 2026-07-05 - Source Limit Table Trace

Added a default-off focused trace for the caller/limit window around
`801095b0..80109720`:

```text
EUTHERDRIVE_GAUNTDL_TRACE_TEXTURE_UPLOAD_SOURCE_LIMIT_TABLE=1
EUTHERDRIVE_GAUNTDL_TRACE_TEXTURE_UPLOAD_SOURCE_LIMIT_TABLE_LIMIT=64
EUTHERDRIVE_GAUNTDL_TRACE_TEXTURE_UPLOAD_SOURCE_LIMIT_TABLE_PC_MIN=801095b0
EUTHERDRIVE_GAUNTDL_TRACE_TEXTURE_UPLOAD_SOURCE_LIMIT_TABLE_PC_MAX=80109720
```

The trace marker is:

```text
[GAUNTDL:TEXUPLOAD-SOURCE-LIMIT]
```

This slice also fixed the texture-upload source filter so a bare 32-bit
`EUTHERDRIVE_GAUNTDL_TRACE_TEXTURE_UPLOAD_RUN_SOURCE=80312998` is canonicalized
to `0xffffffff80312998`. Without that, focused source traces could silently miss
the signed runtime address.

Focused f300 run:

```text
/tmp/gauntdl-source-limit-table-focused-f300.log
frameHash=0x578ddca1
drawPackets=18674 directTriangles=308 setupTriangles=139
textureMap=8487424:3859486:4627938:47168:0x000000:0x1900cc
framebuffer=640x480:249131:248011
```

Key evidence:

```text
801095b4 sw s0,0x1c(sp)
  post: s0=80312998 sp1c=80312998

801095c0 jal 801096ac
  called with s0=80312998 and caller sp+0x1c=80312998

801096ac prologue
  caller sp+0x1c shifts to callee sp+0x4c

801096c0 lw t2,0x4c(sp)
  post: t2=80312998 sp4c=80312998

801096d4/801096dc/801096f4/80109700
  table=00000100/00000020/00000080/00000010
  first bad run uses v0=0x20 -> sp24=0x1f
  later bad run uses v0=0x100 -> sp24=0xff
```

The subsequent upload/classifier lines line up with the same descriptor:

```text
[GAUNTDL:TEXUPLOAD-RUN] source=0xffffffff80312998 sourceBase=0 packet=0 index=0/31 words=64
[GAUNTDL:TEXUPLOAD-ZEROBASE-CLASS] class=descriptor packets=32

[GAUNTDL:TEXUPLOAD-RUN] source=0xffffffff80312998 sourceBase=0 packet=0 index=0/255 words=64
[GAUNTDL:TEXUPLOAD-ZEROBASE-CLASS] class=descriptor packets=256
```

Current conclusion:

1. `801096ac` is downstream of the bad source choice. It receives `80312998`,
   selects the extent (`0x20 -> 0x1f`, later `0x100 -> 0xff`), and forwards the
   descriptor into the Type5 upload path.
2. The active bug is not a Voodoo packet offset issue. It is an asset/parser
   ownership issue where index 9 has already become the descriptor before this
   helper runs.
3. Next work should trace or repair the index-9 asset object/body selection
   around `800aae60`/`800aae98`/`800aacb4`/`800b72fc`, including the point where
   asset entry `80312a08/0002006f` becomes `80312998/00000000`.

## 2026-07-05 - Index-9 Overwrite Source Bracket

The default-off indexed source overwrite mask now also permits replacing a
nonzero source slot when the selected index bit is set:

```text
EUTHERDRIVE_GAUNTDL_EXPERIMENT_RUNTIME_BGLOADMODEL_OVERWRITE_INDEXED_SOURCE_MASK=0x200
```

Before this change, the mask could only permit destination overwrite after the
source-slot gate had already rejected index 9. That made the experiment a no-op
once slot `802529c4` contained descriptor `80312998`.

Before-fix focused f300 run:

```text
/tmp/gauntdl-overwrite-index9-f300.log
/tmp/gauntdl-overwrite-index9-f300.png
frameHash=0x578ddca1
```

The result matched the source-limit baseline because index 9 was not actually
overwritten.

After-fix focused f300 run:

```text
/tmp/gauntdl-overwrite-index9-afterpatch-f300.log
/tmp/gauntdl-overwrite-index9-afterpatch-f300.png
frameHash=0xb38e8ea2
directTriangles=819 setupTriangles=395
textureMap=8605704:3887129:4718575:37438:0x000000:0x1900cc
framebuffer=640x480:216812:216684
```

Key evidence:

```text
[GAUNTDL:TRACE] bgloadmodel-indexed-source-hydration phase=distinct-source-hydrate
  index=9 dest=80401718 bytes=0000bca4 code=wtr disk=158b0600
  sourceWords=40=f00b0001,5c=0000bc38,60=0000001f,64=00000015

[GAUNTDL:FIX] bgloadmodel-distinct-source
  pc=800aae98 index=9 slot=802529c4:80312998->80401718
  cloned=False seededIndexedHeader=True
```

The source-state trace then shows the index-9 asset path using the hydrated
runtime source:

```text
800aae98: s2=80401718 slot=80401718
800aacb4: asset entry becomes 8040d350/0002006f/.../"font_story"
800b72fc: asset entry becomes 8040d350/00000003/.../"font_story"
```

This is a real causal bracket: the frame hash, triangle counts, texture-map
touches, and visible artifact all change. It is not yet correct visible game
graphics. The bad descriptor upload is still alive:

```text
[GAUNTDL:TEXUPLOAD-ZEROBASE-CLASS] class=descriptor source=80312998 index=0/31 packets=32
[GAUNTDL:TEXUPLOAD-ZEROBASE-CLASS] class=descriptor source=80312998 index=0/255 packets=256
```

CPU traces show why this did not fix the upload by itself:

```text
/tmp/gauntdl-overwrite-index9-cputrace-800ab100-800ab140-f300.log
800ab100 receives a3=80312998 and moves it into s1 before the asset update path.

/tmp/gauntdl-overwrite-index9-cputrace-80054750-80054790-f300.log
80054784 calls the 800ab100 helper with a3=80312998; a3 is already the
descriptor before this traced range.
```

Next continuation:

1. Trace earlier in the same caller, starting around `800546f0..80054764`, to
   identify the producer of `a3=80312998`.
2. If that proves the call should receive the hydrated index-9 source/body,
   test a narrow default-off argument remap at the call site. Candidate targets
   are the seeded header `80401718` and the body pointer `8040d350`; choose only
   after the producer trace explains which object the original code expects.

## 2026-07-05 - Texture Source Global Remap and Payload Bracket

Added a default-off global source remap at `80054900`, the point proven by
memtrace to write descriptor `80312998` into global `8019d1f0`:

```text
EUTHERDRIVE_GAUNTDL_EXPERIMENT_RUNTIME_BGLOADMODEL_TEXTURE_SOURCE_GLOBAL_REMAP_INDEX_MASK=0x200
EUTHERDRIVE_GAUNTDL_EXPERIMENT_RUNTIME_BGLOADMODEL_TEXTURE_SOURCE_GLOBAL_REMAP_TARGET=body|header
EUTHERDRIVE_GAUNTDL_EXPERIMENT_RUNTIME_BGLOADMODEL_TEXTURE_SOURCE_GLOBAL_REMAP_BODY_OFFSET=0x...
```

The same slice also broadens known-source classification to the configured
indexed source stride, so `body + offset` candidates remain classed as the same
hydrated source slot when using:

```text
EUTHERDRIVE_GAUNTDL_EXPERIMENT_RUNTIME_BGLOADMODEL_INDEXED_SOURCE_STRIDE=0x20000
```

Evidence that `80054900` is the producer:

```text
/tmp/gauntdl-overwrite-index9-memtrace-8019d1f0-f300.log
[GAUNTDL:MEM] pc=ffffffff80054900 write32 ffffffff8019d1f0 80312998

/tmp/gauntdl-overwrite-index9-cputrace-ffff800548d0-80054910-f300.log
800548f4 after allocator return: v0=80312998
80054900 delay slot writes v0 to 8019d1f0
```

Remap results:

```text
/tmp/gauntdl-source-global-remap-body-index9-f300.log
/tmp/gauntdl-source-global-remap-body-index9-f300.png
80312998 -> 8040d350
frameHash=0x40395adc
textureMap=11084552:661714:10422838:32768:0x000000:0x01fffc

/tmp/gauntdl-source-global-remap-header-index9-f300.log
/tmp/gauntdl-source-global-remap-header-index9-f300.png
80312998 -> 80401718
frameHash=0x5950fe0d
textureMap=5961480:3119981:2841499:31886:0x000000:0x7b24ac
```

Body remap is causally correct but still starts at metadata:

```text
[GAUNTDL:TEXUPLOAD-ZEROBASE-CLASS] class=known-bg source=8040d350
  bgsrc=9:wtr+0xbc38(body=0xbc38/+0x0)
  text="BK_RED"
```

The raw disk confirms the source relationship:

```text
0x158b0600 + 0xbc38 = 0x158bc238
0x158bc238 contains BK_RED / BTMBK_RED / KNI_NAME
```

Offset and payload tests:

```text
/tmp/gauntdl-source-global-remap-body-clamp-index9-f300.log
/tmp/gauntdl-source-global-remap-body-clamp-index9-f300.png
clamp 255->31 works for source 8040d350
frameHash=0x0851d9bc
textureMap=5171464:125892:5045572:22910:0x000000:0x01660c

/tmp/gauntdl-source-global-remap-body-plus70-clamp2-index9-f300.log
/tmp/gauntdl-source-global-remap-body-plus70-clamp2-index9-f300.png
source=8040d3c0, clamp 255->31 still works after known-span broadening
frameHash=0x10aac8bd

/tmp/gauntdl-source-global-remap-body-plus3c8-clamp-index9-f300.log
/tmp/gauntdl-source-global-remap-body-plus3c8-clamp-index9-f300.png
source=8040d718, clamp 255->31
frameHash=0x6ec1140c

/tmp/gauntdl-source-global-remap-body-plus3c8-diskwords-clamp-index9-f300.log
/tmp/gauntdl-source-global-remap-body-plus3c8-diskwords-clamp-index9-f300.png
disk words replace sparse runtime RAM at wtr@0xc000
frameHash=0x5ef40570
textureMap=5171464:581292:4590172:22910:0x000000:0x01660c

/tmp/gauntdl-plus3c8-diskwords-clamp-mamefetch-f300.log
/tmp/gauntdl-plus3c8-diskwords-clamp-mamefetch-f300.png
MAME fetch addressing did not move sampled addresses for the traced triangle
frameHash=0x8ad291bb

/tmp/gauntdl-plus3c8-diskwords-clamp-no-type5endian-f300.log
/tmp/gauntdl-plus3c8-diskwords-clamp-no-type5endian-f300.png
disabling Type5 texture endian changed output but kept the same stripe failure
frameHash=0xec0597e1
```

Current conclusion:

1. The source remap is now real: the descriptor global can be replaced with
   hydrated index-9 header/body/offset candidates.
2. The bad 256-packet repeat is independently controllable with
   `EUTHERDRIVE_GAUNTDL_EXPERIMENT_CLAMP_INDEXED_TEXTURE_UPLOAD_LIMIT=1`.
3. Disk-backed payload at `wtr@0xc000` increases real texture memory content
   substantially, but the rendered frame is still striped. The next blocker is
   likely Type5 texture download layout, TMU state, or the raw WTR payload's
   internal swizzle/tiling, not just descriptor ownership.

Next continuation:

1. Trace texture writes for bucket `0x02f000`/sample address `0x02f420` while
   using `body+0x3c8 + diskwords + clamp`.
2. Compare `type5 target`, `textureMode`, `textureLod`, `textureBase`, and the
   first bytes written at sampled addresses against raw WTR bytes.
3. If the sampled address receives the right bytes but still displays stripes,
   bracket 8-bit texture download addressing (`seq8`, align32, linear, TMU
   banks) before adding any permanent decode logic.

## 2026-07-05 - Texture Writer Summary and S/T Coordinate Bracket

Added two default-off trace controls for narrowing whether visible textured
triangles are sampling uploaded texture memory or default/unwritten memory:

```text
EUTHERDRIVE_GAUNTDL_TRACE_VOODOO_TEXTURE_TRIANGLE_SAMPLE_SUMMARY_REQUIRE_WRITER=1
EUTHERDRIVE_GAUNTDL_TRACE_VOODOO_TEXTURE_TRIANGLE_SAMPLE_GRADIENTS=1
```

Important reproducibility note: the current `body+0x3c8 + diskwords + clamp`
case must be run from the e27b warm snapshot with `WARMUP_FRAMES=180` and an
explicit indexed-header mask that includes bit 9:

```text
EUTHERDRIVE_GAUNTDL_WARMUP_STATE=/tmp/eutherdrive-gauntlet-probe/gauntdl-gauntdl24-fast-raw-f180-s200000-e27b9a6b6d3d.warm
EUTHERDRIVE_GAUNTDL_WARMUP_FRAMES=180
EUTHERDRIVE_GAUNTDL_FIX_RUNTIME_BGLOADMODEL_DISTINCT_SOURCE_INDEXED_HEADER_MASK=0x3fe
EUTHERDRIVE_GAUNTDL_EXPERIMENT_RUNTIME_BGLOADMODEL_TEXTURE_SOURCE_GLOBAL_REMAP_BODY_OFFSET=0x3c8
```

Without the explicit `0x3fe` mask, the default `0x1fe` misses index 9 and the
remap path falls back toward the old descriptor/gei source state.

Focused writer-backed run:

```text
/tmp/gauntdl-plus3c8-diskwords-clamp-requirewriter-warm-f300.log
/tmp/gauntdl-plus3c8-diskwords-clamp-requirewriter-warm-f300.ppm
/tmp/gauntdl-plus3c8-diskwords-clamp-requirewriter-warm-f300.png
frameHash=0x5ef40570
textureMap=5171464:581292:4590172:22910:0x000000:0x01660c
```

The remap and clamp are active in the same run:

```text
[GAUNTDL:FIX] bgloadmodel-texture-source-global-remap pc=80054900 index=9
  80312998->8040d718 bodyOffsetAdd=0x3c8

[GAUNTDL:FIX] texture-upload-indexed-clamp source=8040d718 index=9 limit=255->31
```

The first large writer-backed visible quads sample real uploaded texture
memory, but their setup vertices carry constant S:

```text
base=0x000510 samples=0x000510-0x00E810 buckets=0x001000..0x00D000
stq=(0.000,256.000,1.000000)/(0.000,0.000,1.000000)/(0.000,0.000,1.000000)
stq=(0.000,0.000,1.000000)/(0.000,256.000,1.000000)/(0.000,256.000,1.000000)
```

The writer buckets point at Type5 texture uploads, for example:

```text
pc=800fe614/m=0x00000000/lod=0x00700800/base=0x00000000/l=0/bpp=1/seq=1/t5=0xC0000205@0x000280/...
```

Gradient summary run:

```text
/tmp/gauntdl-plus3c8-diskwords-clamp-gradsummary-warm-f300.log
frameHash=0x5ef40570
textureMap=5171464:581292:4590172:22910:0x000000:0x01660c
```

The appended gradient register dump is currently diagnostic only. It produced
alias-looking/stale values such as:

```text
gradRaw=0x0005A604/0x0C24100F/0xFF802000/0x0C24100F/0xFF802000/0xC681CC00/0x437F0000
gradF=(0.000000,0.000000,NaN)/(0.000000,NaN)/(-16614.000000,255.000000)
```

So the reliable evidence is still the captured setup vertices in `TEXSUMMARY`:
the big visible quads are textured, writer-backed, and sampling valid texture
memory addresses, but they are sampling a near-single texture column.

Texture layout A/B matrix from the same warm baseline:

```text
FIX_VOODOO_TEXTURE_DOWNLOAD_ALIGN32=0
  /tmp/gauntdl-plus3c8-diskwords-clamp-noalign-warm-f300.log
  frameHash=0x5ef40570, unchanged

FIX_VOODOO_SEQ8_TEXTURE_DOWNLOAD=0
  /tmp/gauntdl-plus3c8-diskwords-clamp-noseq8-warm-f300.log
  frameHash=0xa56712ad, still large horizontal stripe blocks

FIX_VOODOO_LINEAR_TEXTURE_DOWNLOAD_ADDRESSING=1
  /tmp/gauntdl-plus3c8-diskwords-clamp-linear-warm-f300.log
  frameHash=0x9a17a88e, more large blocks but still stripe failure

FIX_VOODOO_TEXTURE_SAMPLE_BASE_BIAS=0
  /tmp/gauntdl-plus3c8-diskwords-clamp-nobias-warm-f300.log
  frameHash=0x41a3aaf0, same texture map and still striped

EXPERIMENT_VOODOO_TEXTURE_UPLOAD_TMU_BANKS=1
  /tmp/gauntdl-plus3c8-diskwords-clamp-tmubanks-warm-f300.log
  frameHash=0x8e39ab70, same texture map and still striped

EXPERIMENT_VOODOO_TEXTURE_MAME_SETUP_GRADIENTS=1
  /tmp/gauntdl-plus3c8-diskwords-clamp-mamesetup-warm-f300.log
  frameHash=0x4189261f, horizontal stripes disappear but a huge wrong
  diagonal/solid area remains

EXPERIMENT_VOODOO_TEXTURE_MAME_SETUP_GRADIENTS=1
EXPERIMENT_VOODOO_TEXTURE_MAME_FIXED_FETCH=1
  /tmp/gauntdl-plus3c8-diskwords-clamp-mamesetup-fixedfetch-warm-f300.log
  frameHash=0x4189261f, no additional change
```

Local MAME source check:

```text
/home/nichlas/mame/src/devices/video/voodoo.cpp
  texture format 0 maps to rgb332.

/home/nichlas/mame/src/devices/video/voodoo.cpp
  Type5 texture download uses bytes_per_texel=(format < 8 ? 1 : 2),
  seq_8_downld from TMU0 mode, lod=offset[18:15], tt=offset[14:7],
  ts=(offset << (seq8 ? 2 : 1)) & 0xff.

/home/nichlas/mame/src/devices/video/voodoo_render.h
  write_ptr aligns ((scale * offs) & ~3), matching the current align32
  behavior; the no-align A/B result is therefore expected.
```

Updated conclusion:

1. Descriptor/source remap, clamp, and disk-backed `wtr@0xc000` payload are all
   necessary and are active in the current best stripe frame.
2. Format 0 is RGB332, so this is not a simple NCC/palette decode problem.
3. Type5 write-layout toggles move hashes but do not produce correct geometry.
4. The strongest current blocker is Type3/setup texture coordinate decode or
   latch timing: `ReadCurrentSetupVertex` is feeding constant S into large
   visible textured triangles.

Next continuation:

1. Inspect command FIFO Type3 decode and the setup vertex capture path around
   `ReadCurrentSetupVertex`, `SetupFloatOrFallback`, `RegFstartS/T/W`, and the
   setup packet register aliases.
2. Add a narrow trace that logs Type3 packet words/register writes immediately
   before the first writer-backed `TEXSUMMARY` rows with constant S.
3. Compare captured S/T with hardware gradient registers and MAME setup
   semantics before making another default-on rendering change.

## 2026-07-05 - Type3 Constant-S Confirmation and Trace Oracle Fix

Rechecked the current best `body+0x3c8 + diskwords + clamp` f300 stripe frame
with the existing Type3 TMU selector experiment:

```text
EUTHERDRIVE_GAUNTDL_EXPERIMENT_VOODOO_TYPE3_PREFER_TMU0_ST=1
/tmp/gauntdl-plus3c8-diskwords-clamp-prefertmu0-warm-f300.log
/tmp/gauntdl-plus3c8-diskwords-clamp-prefertmu0-warm-f300.ppm
frameHash=0x5ef40570
textureMap=5171464:581292:4590172:22910:0x000000:0x01660c
drawPackets=17111 directTriangles=647 setupTriangles=304
```

Result: no frame/hash/texture-map movement. The visible stripe artifact is not
caused by picking TMU1 S/T over TMU0 S/T in the simplified Type3 path.

Focused Type3 read trace for the first writer-backed fullrect pair:

```text
/tmp/gauntdl-plus3c8-type3-fields-rd2c90c-rd2c958-warm-f300.log
frameHash=0x5ef40570
textureMap=5171464:581292:4590172:22910:0x000000:0x01660c
```

The raw packet words confirm the visible clean fullrect pair has constant S in
the Type3 payload itself:

```text
rd=0x0002c90c cmd=0x0180a8cb
v0 x=0 y=-1   s0=0 t0=256
v1 x=512 y=383 s0=0 t0=0
v2 x=0 y=383 s0=0 t0=0

rd=0x0002c958 cmd=0x0180a8cb
v0 x=512 y=383 s0=0 t0=0
v1 x=0 y=-1   s0=0 t0=256
v2 x=512 y=-1 s0=0 t0=256
```

This resolves the local Type3 question: the decoder field order is still
consistent with MAME, and the fullrect's `S=0,T=0..256` shape is
intentional-looking payload data, not a field-shift bug. The strongest next
target remains upload/source ownership for the texture data consumed by the
`base=0x510` fullrect, especially why the source selector keeps feeding
descriptor or metadata streams into Type5 texture uploads.

Also fixed a trace-oracle bug introduced in the writer-summary slice:
`EUTHERDRIVE_GAUNTDL_TRACE_VOODOO_TEXTURE_TRIANGLE_SAMPLE_SUMMARY_REQUIRE_WRITER=1`
now collects writer buckets even when the more verbose
`EUTHERDRIVE_GAUNTDL_TRACE_VOODOO_TEXTURE_TRIANGLE_SAMPLE_WRITERS=1` flag is not
set. Before this fix, `REQUIRE_WRITER=1` could silently filter out all summaries
because the write context, last-writer map, and summary buckets were only active
under the verbose `SAMPLE_WRITERS` flag.

Verified fixed oracle:

```text
/tmp/gauntdl-requirewriter-autobuckets-fixed-warm-f300.log
frameHash=0x5ef40570
textureMap=5171464:581292:4590172:22910:0x000000:0x01660c
drawPackets=17111 directTriangles=647 setupTriangles=304
```

The first writer-backed fullrect summaries now point from visible pixels back to
specific Type5 texture uploads:

```text
n=1 rd0x0002c90c base=0x000510 addrs=0x000510-0x00e810
writers=none:1574,
  pc=800fe614/lod=0x00700800/base=0/t5=0xC0000205@0x000280/pkt=0x00007334:1021,
  pc=800fe614/lod=0x00700800/base=0/t5=0xC0000205@0x000300/pkt=0x0000743C:1016,
  pc=800fe614/lod=0x00700800/base=0/t5=0xC0000205@0x000400/pkt=0x0000764C:1008

n=2 rd0x0002c958 base=0x000510 addrs=0x000510-0x00e810
writers=none:3034,
  pc=800fe614/lod=0x00700800/base=0x1c00/t5=0xC0000205@0x000300/pkt=0x0001D1FC:904,
  pc=800fe614/lod=0x00700800/base=0x1c00/t5=0xC0000205@0x000200/pkt=0x0001CFEC:896,
  pc=800fe614/lod=0x00700800/base=0x1c00/t5=0xC0000205@0x000100/pkt=0x0001CDDC:888
```

Next continuation:

1. Keep the Type3 fullrect as real geometry and stop spending time on S/T field
   decode unless a new packet family appears.
2. Use the fixed `REQUIRE_WRITER` trace as the oracle for upload/source
   ownership, starting from Type5 packets `0x00007334`, `0x0000743C`,
   `0x0000764C`, `0x0001CFEC`, `0x0001D1FC`, and `0x0001CDDC`.
3. Move back up the source-selector chain around the index-9 `font_story`/`wtr`
   asset body and the path that hands descriptor-like data to Type5 upload
   runs.

## 2026-07-05 - Constant-S Fullrect Negative and Type3 Ownership Trace

Added a default-off diagnostic gate for the dominant full-screen stripe quads:

```text
EUTHERDRIVE_GAUNTDL_EXPERIMENT_VOODOO_SUPPRESS_CONSTANT_S_FULLRECT_TEXTURE_TRIANGLES=1
EUTHERDRIVE_GAUNTDL_EXPERIMENT_VOODOO_SUPPRESS_CONSTANT_S_FULLRECT_TEXTURE_TRIANGLES_LIMIT=12
```

The gate is intentionally narrow: it only suppresses large textured Type3
triangles with `cmd=0x0180A8CB`, constant S, and a large T span. It still
materializes pending clears before skipping the raster write, so the resulting
image is useful as a diagnostic for what sits behind the stripe quads.

Suppress result:

```text
/tmp/gauntdl-suppress-constant-s-fullrect-warm-f300.log
/tmp/gauntdl-suppress-constant-s-fullrect-warm-f300.ppm
/tmp/gauntdl-suppress-constant-s-fullrect-warm-f300.png
frameHash=0x6d791e91
textureMap=5171464:581292:4590172:22910:0x000000:0x01660c
drawPackets=17111 directTriangles=647 setupTriangles=304
```

Visual result: the horizontal stripe quads disappear, but the frame becomes a
large cyan/brown diagonal field, not real scene graphics. Therefore the stripe
quad is not merely hiding a correct scene behind it; suppressing it is only a
negative diagnostic, not a fix.

Also extended `EUTHERDRIVE_GAUNTDL_TRACE_VOODOO_TYPE3_PACKETS=1` so Type3 trace
rows now include packet start, storage/read storage, valid-window count, bulk
position, and `w0/w1/w2` last-writer metadata. This avoids relying on later
storage-only source-chain snapshots after the FIFO ring has been reused by
Type5 payloads.

Focused ownership trace:

```text
/tmp/gauntdl-type3-ownership-rd2c90c-rd2c958-warm-f300.log
frameHash=0x5ef40570
```

The visible fullrect pair is valid at decode time:

```text
rd=0x0002c90c cmd=0x0180a8cb validWindow=19/19 bulk=scan=outside:rel910/32
w0 last=fifo/pc=800c4e5c value=0180a8cb
w1 last=fifo/pc=800c4e5c value=00000000
w2 last=fifo/pc=800c4e5c value=bf800000

rd=0x0002c958 cmd=0x0180a8cb validWindow=19/19 bulk=scan=outside:rel929/32
w0 last=fifo/pc=800c4e5c value=0180a8cb
w1 last=fifo/pc=800c4e5c value=44000000
w2 last=fifo/pc=800c4e5c value=43bf8000
```

Current conclusion:

1. The constant-S fullrects are not an obvious stale Type5 payload misdecode at
   the moment they are consumed. They are complete, valid Type3 packets written
   by `pc=800c4e5c`.
2. Storage-only source-chain traces are still useful, but they can be
   misleading for this exact question because the same storage offsets are later
   reused by Type5 bulk payloads.
3. The next useful target is the state that makes `800c4e5c` emit the
   full-screen texture pair with `S=0`, or the upstream runtime/FIFO state that
   causes this draw path to dominate instead of real model/scene geometry.

## 2026-07-05 - Vertex FIFO Source Closure and Texture-Path Negatives

Added a positive default-off trace for the runtime vertex FIFO fast path:

```text
EUTHERDRIVE_GAUNTDL_TRACE_VERTEX_FIFO_FASTPATH=1
EUTHERDRIVE_GAUNTDL_TRACE_VERTEX_FIFO_FASTPATH_LIMIT=...
EUTHERDRIVE_GAUNTDL_TRACE_VERTEX_FIFO_FASTPATH_DESTINATIONS=...
```

The destination filter now accepts full, low32, low29, low24, and low20
addresses. The low20 comparison is required for focused Type3 reads such as
`0x2c90c/0x2c958`, because the fast-path destination is
`0xffffffffa822c90c/0xffffffffa822c958`.

Focused f300 trace:

```text
/tmp/gauntdl-vertexdest-low20-type3-rd2c90c-rd2c958-warm-f300.log
frameHash=0x5ef40570
drawPackets=17111 directTriangles=647 setupTriangles=304
textureMap=5171464:581292:4590172:22910:0x000000:0x01660c
```

The visible fullrect packet pair is emitted directly by
`TryFastPathKnownRuntimeVertexFifoEmit` from the source structs:

```text
rd=0x0002c90c
src0=802e1a78: x=0 y=-1   s=0 t=256
src1=802e1a50: x=512 y=383 s=0 t=0
src2=802e1a28: x=0 y=383   s=0 t=0

rd=0x0002c958
src0=802e1a50: x=512 y=383 s=0 t=0
src1=802e1a78: x=0 y=-1    s=0 t=256
src2=802e1aa0: x=512 y=-1  s=0 t=256
```

Source-struct write tracing also shows those structs are freshly built before
the emit by `800b0a38..800b0ba8`, so this is not stale FIFO storage. The
constant-S fullrect is a real draw path.

Three texture-path experiments were rechecked from the current best
`body+0x3c8 + diskwords + clamp` f300 stack:

```text
MAME setup/fetch:
  /tmp/gauntdl-current-mamesetup-fetch-warm-f300.log
  /tmp/gauntdl-current-mamesetup-fetch-warm-f300.png
  frameHash=0xe5aa97e0
  visual=still horizontal stripes plus large solid fields

ZERO_BASE_UPLOAD_STOP_AT_KNOWN_BOUNDARY=1:
  /tmp/gauntdl-boundary-stop-warm-f300.log
  /tmp/gauntdl-boundary-stop-warm-f300.png
  frameHash=0x5ef40570
  visual=unchanged

ZERO_BASE_UPLOAD_SKIP_UNKNOWN_PREFIX_PACKETS=1:
  /tmp/gauntdl-prefix-skip-packets-warm-f300.log
  frameHash=0x5ef40570
  visual=unchanged

EXPERIMENT_VOODOO_FBZ_COLORPATH_RGB_COMBINE=1:
  /tmp/gauntdl-fbzcp-combine-warm-f300.log
  frameHash=0x5ef40570
  visual=unchanged
```

Disk inspection confirms why `body+0x3c8` was a plausible source target:

```text
wtr disk base 0x158b0600
wtr header bodyOffset=0xbc38 len=0xbca4
0x158bc238 contains metadata/text labels: BK_RED, BTMBK_RED, KNI_NAME
0x158bc600 contains dense texture-looking bytes: 8191a1a0 a0a1a0b1 ...
```

Current conclusion:

1. The vertex FIFO/source-struct ownership question is closed for this fullrect.
2. The current visual blocker is still the texture upload/layout/sample path for
   the real `pc=800c4e5c`, `cmd=0x0180A8CB`, `base=0x510` fullrect.
3. Boundary-stop and prefix-skip did not fire because the best stack has already
   normalized the source into a known `wtr` window; the next trace should focus
   on Type5 target/address layout and writer buckets for the `wtr@0xc000`
   texture-looking region, not generic unknown-prefix handling.

## 2026-07-05 - Constant-S Fullrect Remap and Disk-Backed Type5 Layout

Added a default-off raster experiment:

```text
EUTHERDRIVE_GAUNTDL_EXPERIMENT_VOODOO_CONSTANT_S_FULLRECT_USE_X_AS_S=1
```

It only applies to the large `cmd=0x0180A8CB` fullrect pair whose S values are
constant and whose T span is large. It leaves normal triangles and the MAME
fixed-fetch branch alone. The experiment reconstructs S from screen X across a
256-wide texture span and logs the first 12 remapped fullrects.

Best diagnostic result from the current warm f300 stack:

```text
/tmp/gauntdl-constant-s-x-as-s-warm-f300.log
/tmp/gauntdl-constant-s-x-as-s-warm-f300.png
frameHash=0x26448ab8
textureMap=writes=5171464:nz=581292:zero=4590172:touched=22910:first=0x000000:last=0x01660c
drawPackets=17111 directTriangles=647 setupTriangles=304
textured=tri:8376:covered:1023:rejected:7353:pixels:108873600:zero:34469210
```

This is the first run that breaks the pure horizontal-stripe failure into a
real 2D texture-looking surface. It is not correct graphics yet, but it proves
the `wtr@0xc000` upload region contains visible image data and that the previous
frame was dominated by sampling a near-single texture column.

Negative comparisons from the same X-as-S stack:

```text
sample base bias disabled:
  /tmp/gauntdl-constant-s-x-as-s-nobias-warm-f300.log
  /tmp/gauntdl-constant-s-x-as-s-nobias-warm-f300.png
  frameHash=0x3c757efd
  zero=39579145
  visual=more noisy; worse than biased X-as-S

8-bit sample lanes reversed:
  /tmp/gauntdl-constant-s-x-as-s-revlane-warm-f300.log
  /tmp/gauntdl-constant-s-x-as-s-revlane-warm-f300.png
  frameHash=0xbc9c9c62
  zero=44248635
  visual=worse

T origin flipped:
  /tmp/gauntdl-constant-s-x-as-s-tflip-warm-f300.log
  /tmp/gauntdl-constant-s-x-as-s-tflip-warm-f300.png
  frameHash=0x85acba3c
  zero=91100649
  visual=clearly worse

linear texture download addressing:
  /tmp/gauntdl-constant-s-x-as-s-linear-warm-f300.log
  /tmp/gauntdl-constant-s-x-as-s-linear-warm-f300.png
  frameHash=0xc64c12d8
  zero=38964686
  visual=larger colored blocks, not more correct

sequential 8-bit texture download disabled:
  /tmp/gauntdl-constant-s-x-as-s-noseq8-warm-f300.log
  /tmp/gauntdl-constant-s-x-as-s-noseq8-warm-f300.png
  frameHash=0x4cdb237e
  zero=37744493
  visual=not better

align32 disabled:
  /tmp/gauntdl-constant-s-x-as-s-noalign-warm-f300.log
  /tmp/gauntdl-constant-s-x-as-s-noalign-warm-f300.png
  frameHash=0x26448ab8
  zero=34469210
  visual=unchanged from best X-as-S

Y-as-T span 32:
  /tmp/gauntdl-constant-s-x-as-s-tspan32-warm-f300.log
  /tmp/gauntdl-constant-s-x-as-s-tspan32-warm-f300.png
  frameHash=0xe2796df3
  zero=33233299
  visual=more filled but vertically smeared; not a correct image

Y-as-T span 64:
  /tmp/gauntdl-constant-s-x-as-s-tspan64-warm-f300.log
  /tmp/gauntdl-constant-s-x-as-s-tspan64-warm-f300.png
  frameHash=0x8ff63a4d
  zero=34883541
  visual=worse than best X-as-S and still smeared
```

Also extended `TEXUPLOAD-LINK` rows with a `disk=` column using the same
disk-compare oracle as the focused FIFO-packet trace. Target-focused run:

```text
/tmp/gauntdl-type5-target-layout-diskcompare-warm-f300.log
frameHash=0x5ef40570
targetWords=100,200,280,300,400,d00,e00,f00
```

The focused Type5 target layout is stable: each target occurs 25 times and maps
to the same `wtr` spans:

```text
target=0x100 -> source=8040d918 -> wtr@0xc200
target=0x200 -> source=8040db18 -> wtr@0xc400
target=0x280 -> source=8040dc18 -> wtr@0xc500
target=0x300 -> source=8040dd18 -> wtr@0xc600
target=0x400 -> source=8040df18 -> wtr@0xc800
target=0xd00 -> source=8040f118 -> wtr@0xda00
target=0xe00 -> source=8040f318 -> wtr@0xdc00
target=0xf00 -> source=8040f518 -> wtr@0xde00
```

RAM is still mostly zero at these sources, while the disk oracle shows dense
`wtr` texture data. The current best visible result therefore depends on
`EUTHERDRIVE_GAUNTDL_EXPERIMENT_ZERO_BASE_UPLOAD_DISK_WORDS=1`, and the next
useful target is texture upload/write layout for those disk words, not another
RAM-source search.

Follow-up write-bucket trace:

```text
/tmp/gauntdl-texwrite-sample-buckets-diskwords-warm-f300.log
frameHash=0x5ef40570
```

It targeted the sample buckets reported by the first fullrect summaries
(`0x001000..0x004000` and `0x00a000..0x00d000`). The first writes in those
buckets show `WriteTexturePort32` using upload `tlod=0x00700800`, while the
fullrect sample summaries use draw/sample `lod=0x00002000`. In the current
decode, `0x00700800` is a `256x32` upload layout and `0x00002000` is sampled as
`256x256`.

That LOD/aspect mismatch is a useful lead, but the T-span experiments show it
is not solved by simply compressing the fullrect T coordinate:

```text
T_SPAN=32 reduces zero pixels, but smears the visible image.
T_SPAN=64 is numerically and visually worse.
```

Current conclusion:

1. `S=0` is real payload in the fresh source structs, but X-as-S proves the
   texture data becomes visible when the fullrect samples across the X axis.
2. Base bias stays enabled; no-bias, reverse lanes, T flip, linear addressing,
   no-seq8, no-align, and T-span remaps did not improve the visible image.
3. Next graphics slice should either replace the diagnostic raster remap with a
   source-accurate way to recover the fullrect's horizontal coordinate, or
   instrument `WriteTexturePort32`/writer buckets for the `wtr@0xc200..0xde00`
   disk-backed Type5 writes to resolve the upload-vs-sample LOD/aspect mismatch.

## 2026-07-05 - Vertex-FIFO Fullrect S-from-X Checkpoint

The latest source-ownership trace closes the narrow `S=0` question one level
earlier than the raster hack. Dumping and tracing `800b0760..800b07d0` shows
that `800b0770` is a small float interpolator:

```text
800b0770: lwc1  f0, 0(a1)
800b0774: lwc1  f1, 0(a0)
800b0778: sub.s f0, f0, f1
800b077c: mtc1  a2, f2
800b0780: madd.s-style op
800b0784: swc1  f0, 0(a0)
...
800b07ac: swc1  f0, 0x0c(a0)  ; S
800b07c4: swc1  f0, 0x10(a0)  ; T
```

Focused CPU trace around the same range reached the visible fullrect builders:

```text
pc=800b0770 a0=802e1a28 a1=802e1a78 a2=b87c78a0 ra=800b0c50
pc=800b0770 a0=802e1a50 a1=802e1aa0 a2=b87c78a0 ra=800b0c64
pc=800b0770 a0=802e1a50 a1=802e1a28 a2=bf7ab6ac ra=800b0ca0
pc=800b0770 a0=802e1aa0 a1=802e1a78 a2=bf7ab6ac ra=800b0cb4
```

That means the constant-S fullrect is not a stale FIFO artifact and not a
Type3 field-order bug. The game code is clipping/interpolating source structs
whose S endpoints are already all zero.

Added a default-off source/FIFO-level diagnostic:

```text
EUTHERDRIVE_GAUNTDL_EXPERIMENT_RUNTIME_VERTEX_FIFO_FULLRECT_S_FROM_X=1
EUTHERDRIVE_GAUNTDL_EXPERIMENT_RUNTIME_VERTEX_FIFO_FULLRECT_S_FROM_X_LIMIT=16
```

It matches only the large `0x0180A8CB` runtime vertex FIFO fullrects with
constant S and a large T span. Instead of changing raster sampling, it patches
the emitted FIFO packet's S words from screen X:

```text
dst=0xffffffffa822c90c bbox=(0,-1)-(512,383) scale=0.5
s=0/0/0 -> 0/256/0
t=256/0/0

dst=0xffffffffa822c958 bbox=(0,-1)-(512,383) scale=0.5
s=0/0/0 -> 256/0/256
t=0/256/256
```

Test run:

```text
/tmp/gauntdl-vertexfifo-s-from-x-warm-f300.log
/tmp/gauntdl-vertexfifo-s-from-x-warm-f300.png
frameHash=0x38bc79b5
textureMap=writes=5171464:nz=581292:zero=4590172:touched=22910:first=0x000000:last=0x01660c
drawPackets=17111 directTriangles=647 setupTriangles=304
textured=tri:8376:covered:1023:rejected:7353:pixels:108873600:zero:34469210
colors=29689 signature=bb697b4fb2c3b20940ac93fc7c711ef1c410cef6232b4d39ecdf79fd43723177
```

Comparison:

```text
AE vs raster X-as-S diagnostic: 1403 pixels
AE vs current stripe baseline: 195287 pixels
```

The source/FIFO experiment is visually the same useful signal as the raster
X-as-S run: a real 2D texture-looking surface appears, but the output is still
not correct scene graphics. The important improvement is diagnostic placement:
we can now treat the old raster remap as a control and continue from FIFO/source
packet evidence.

Current next plan:

1. Keep both S-from-X experiments default-off. Do not promote either as a real
   fix yet.
2. Use the source/FIFO experiment as the preferred visual diagnostic because it
   proves the packet-level STQ path can carry the recovered horizontal span.
3. Continue on the remaining blocker: the texture upload/sample layout mismatch
   for the `wtr@0xc200..0xde00` disk-backed Type5 writes. The MAME upload
   pointer trace found real LOD-tail address mismatches, but enabling it did not
   change the f300 image, so the next useful trace is a focused writer-bucket
   comparison for the visible fullrect sample buckets under source/FIFO
   S-from-X.
