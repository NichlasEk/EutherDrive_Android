# N64 OoT RDP/render handoff - 2026-04-25

## Read this first next session

This file is the current handoff for getting real Ocarina of Time boot graphics beyond the green fill. It supersedes the working-state details from `notes/n64_oot_framebuffer_handoff_2026-04-24.md`, but that older file is still useful for background.

Current goal: move from valid boot + VI/RDP framebuffer handoff + green clear to actual rendered OoT image.

## Current state

- OoT now boots far enough to get VI active and alternating framebuffers around `0x003b5280` and `0x003daa80`.
- UI/headless sees content, but the content is still a solid green fill: final PPM is one color `(0,32,0)`.
- Direct RDP/VI framebuffer selection now wins over old stale recent-buffer heuristics.
- Audio is not required for this boot/render path and should be skipped during N64 headless debug.
- The current bottleneck is not framebuffer handoff anymore. It is minimal RDP textured rendering, most likely texture coordinate/TMEM/loadblock semantics or later RDP command state.

## Important local changes

Changed files for the current N64 work:

- `Ryu64/Ryu64.MIPS/Memory.cs`
- `Ryu64/Ryu64Core/Ryu64Core.cs`
- `EutherDrive.Core/N64Adapter.cs`
- `EutherDrive.Headless/Program.cs`

There are unrelated dirty files in the repo. Do not revert them unless explicitly asked.

Known unrelated dirty files from `git status --short` included:

- `.gitignore`
- `EutherDrive.Android/MainView.axaml`
- `EutherDrive.UI/RomPickerDialog.cs`
- SNES/SA1/SuperFX files
- `eutherdrive_title_stats.toml`
- old notes and scripts

## Implemented this session

### RDP state and commands

`Memory.cs` now has a minimal RDP state machine:

- `SetColorImage`
- `SetTextureImage`
- `SetTile`
- `SetTileSize`
- `LoadBlock`
- `LoadTile`
- `LoadTLut`
- `FillRectangle`
- `TextureRectangle` / `TextureRectangleFlip`
- `SetPrimColor`, `SetEnvColor`, `SetBlendColor`
- command histogram trace via `EUTHERDRIVE_TRACE_N64_RDP_COMMANDS=1`
- texture/load trace via `EUTHERDRIVE_TRACE_N64_RDP_TEXRECT=1`

It also tracks the latest RDP-produced color image:

- `LastRdpColorImageAddress`
- `LastRdpColorImageWidth`
- `LastRdpColorImageBytesPerPixel`
- `LastRdpColorImageWriteEpoch`

`Ryu64Core.TryGetFramebuffer` uses this as a producer hint, so early bogus VI origins like `0x0000027f` no longer force stale RDRAM framebuffer scans.

### RDP texture path

Minimal texture drawing now exists:

- TMEM buffer: 4096 bytes.
- TLUT buffer: 256 `ushort` entries.
- Texture decoders: RGBA16, RGBA32, CI4/CI8 fallback, IA, I.
- Writes to 16-bit/32-bit color image via `WriteRdpRgbaPixel`.
- Fill path now marks latest RDP color image writes.

Important attempted fix:

- Added `TileSizeSet` to `RdpTileState`.
- If no explicit `SetTileSize` exists, sampler derives width from `line` and height from available TMEM instead of treating default `lrs/lrt=0` as a 1x1 texture.
- Result: still green fill, so this was not sufficient.

### Headless/debug ergonomics

`N64Adapter` now supports:

- `EUTHERDRIVE_N64_SKIP_AUDIO=1` to avoid `PullAudio()` cost/logging.

`EutherDrive.Headless` N64 path now defaults to setting:

- `EUTHERDRIVE_N64_SKIP_AUDIO=1` unless already set.

Added early-stop support:

- `EUTHERDRIVE_N64_HEADLESS_STOP_ON_FRAMEBUFFER=1`
- `EUTHERDRIVE_N64_HEADLESS_STOP_MIN_FRAME=<n>`
- `EUTHERDRIVE_N64_HEADLESS_STOP_STABLE_FRAMES=<n>`

This stops once `GetFrameStats(...).HasContent` is seen for the requested stable count. It is useful, but note that the current "content" is still green fill, so early-stop proves handoff, not real rendering.

## Verification performed

Build passed:

```sh
dotnet build EutherDrive.Headless/EutherDrive.Headless.csproj -c Release -nologo -v minimal -nr:false -m:1 -p:IncludeOptionalCores=true
```

Expected warnings remain from other projects. No errors.

Headless outputs tested:

```sh
timeout 150s env \
  EUTHERDRIVE_HEADLESS_DUMP_DIR=/tmp/oot_rdp_force \
  EUTHERDRIVE_TRACE_N64_RDP_TEXRECT=1 \
  EUTHERDRIVE_N64_RSP_INTERPRETER=1 \
  EUTHERDRIVE_N64_RSP_GRAPHICS_HLE_FALLBACK=0 \
  EUTHERDRIVE_N64_RSP_TASK_MAX_INSTRUCTIONS=20000000 \
  EUTHERDRIVE_N64_RSP_TASK_NO_PROGRESS_LIMIT=2000000 \
  EUTHERDRIVE_N64_RUNFRAME_WAIT_MS=5 \
  EUTHERDRIVE_N64_BRINGUP_RUNFRAME_WAIT_MS=5 \
  EUTHERDRIVE_N64_BRINGUP_FRAME_LIMIT=400 \
  dotnet EutherDrive.Headless/bin/Release/net8.0/EutherDrive.Headless.dll \
  "/home/nichlas/roms/N64/Legend of Zelda, The - Ocarina of Time (USA) (Rev 2).z64" 5400
```

Result before the latest headless ergonomics:

- Framebuffer recovered around `runFrame=4803`.
- Status:
  - `RDP color framebuffer used (vi=0x0000027f -> fb=0x003b5000, color=0x003b5000, width=320, writeEpoch=..., visualScore=-2147483648)`
  - then `RDP vi framebuffer used` alternating `0x003b5280` and `0x003daa80`.
- Final PPM: `320x237`, 75840 pixels, exactly one color `(0,32,0)`.

After the `TileSizeSet` change, a 7000-frame run still produced:

- `headless_output.ppm`
- `320x237`
- exactly one color `(0,32,0)`

So framebuffer selection is working, but texture/primitive rendering is not yet producing visible non-clear pixels.

## RDP trace facts from OoT

The first visible-looking OoT RDP path is a series of I8 texture rectangles.

Representative trace:

```text
[N64RDP] loadblock tile=7 ti=0x002c61d0/4:2x1 tmem=0x0 line=0 bytes=384 first=00000000 w0=0xf3000000 w1=0x070bf056
[N64RDP] texrect ci=0x003b5000 size=2 width=320 rect=(97,94)-(289,96) tile=0 ti=0x002c61d0/4:2x1 tmem=0x0 line=24 fmt=4:1 w0=0xe4484180 w1=0x00184178 w2=0x00000000 w3=0x04000400
```

Repeated bands:

- `ci=0x003b5000` and `ci=0x003da800`
- color image size 2, width 320
- rects around `(97,94)-(289,126)`
- `tile=0`
- `fmt=4:1` which is I8
- `line=24`, implying 192 bytes/row for I8
- `w2=0x00000000`
- `w3=0x04000400`, so current code interprets `dsdx=1.0`, `dtdy=1.0`

Load path often loads `tile=7` but render uses `tile=0`. In traces, tile 0 has matching `tmem=0`, `line=24`, `fmt=4:1`, so current assumption was that this should sample the same TMEM. That may still be wrong if RDP tile/tmem addressing or loadblock swizzle differs.

## Main suspicion now

The output remains solid green because either:

- `TextureRectangle` samples zeros or transparent-equivalent data from TMEM.
- `LoadBlock` copy layout is wrong. Real RDP `LoadBlock` is not a plain linear copy in all cases.
- Tile 7 to tile 0 relationship is being modeled incorrectly.
- The texture rectangles write visible bands but are later cleared again before final handoff.
- The RDP combiner/blender state is effectively selecting fill/prim/env instead of texture, and current code always writes raw sampled texture in a way that does not match OoT.

Evidence against pure framebuffer-selection bug:

- Handoff status is now explicitly `RDP vi framebuffer used`.
- VI origins are plausible after recovery.
- The dump size/height are sane.
- The one-color output matches the clear color, not random stale RDRAM.

## Efficient next-session debug plan

Do not use long fixed `9000` frame runs with `waitMs=5` unless necessary. They waste minutes.

Use early-stop for handoff checks:

```sh
mkdir -p /tmp/oot_rdp_fast
timeout 90s env \
  EUTHERDRIVE_HEADLESS_DUMP_DIR=/tmp/oot_rdp_fast \
  EUTHERDRIVE_N64_HEADLESS_STOP_ON_FRAMEBUFFER=1 \
  EUTHERDRIVE_N64_HEADLESS_STOP_STABLE_FRAMES=1 \
  EUTHERDRIVE_N64_RSP_INTERPRETER=1 \
  EUTHERDRIVE_N64_RSP_GRAPHICS_HLE_FALLBACK=0 \
  EUTHERDRIVE_N64_RSP_TASK_MAX_INSTRUCTIONS=20000000 \
  EUTHERDRIVE_N64_RSP_TASK_NO_PROGRESS_LIMIT=2000000 \
  EUTHERDRIVE_N64_RUNFRAME_WAIT_MS=5 \
  EUTHERDRIVE_N64_BRINGUP_RUNFRAME_WAIT_MS=5 \
  EUTHERDRIVE_N64_BRINGUP_FRAME_LIMIT=400 \
  dotnet EutherDrive.Headless/bin/Release/net8.0/EutherDrive.Headless.dll \
  "/home/nichlas/roms/N64/Legend of Zelda, The - Ocarina of Time (USA) (Rev 2).z64" 6000 \
  > /tmp/oot_rdp_fast.log 2>&1
```

Analyze PPM colors:

```sh
python - <<'PY'
from pathlib import Path
from collections import Counter
p = Path('/tmp/oot_rdp_fast/headless_output.ppm')
print('exists', p.exists(), 'size', p.stat().st_size if p.exists() else 0)
if p.exists():
    data = p.read_bytes()
    pos = 0
    toks = []
    while len(toks) < 4:
        nl = data.find(b'\n', pos)
        line = data[pos:nl].strip()
        pos = nl + 1
        if line and not line.startswith(b'#'):
            toks.extend(line.split())
    pix = data[pos:]
    colors = Counter(tuple(pix[i:i+3]) for i in range(0, len(pix), 3))
    print('header', toks[:4], 'pixels', len(pix)//3, 'colors', len(colors), 'top', colors.most_common(30))
PY
```

If the goal is real rendering, early-stop on any content is insufficient because green fill counts as content. Add a stronger stop condition next:

- stop only when final frame has more than one color, or
- stop only when `GetFrameStats` exposes a color diversity count, or
- write a small N64-specific headless mode that runs until `colors > 1` or timeout.

## Next implementation block

Recommended next code work:

1. Add a cheap RDP texture-rect write diagnostic that counts sampled non-zero/non-green pixels per rectangle, not full logs.
2. Add an env-gated diagnostic summary like:
   - rect count
   - sampled count
   - failed sample count
   - first 8 sampled intensity values
   - written color diversity for each rect
3. Compare `LoadBlock` semantics against Mupen/Angrylion style RDP code, specifically:
   - `LoadBlock` dxt behavior
   - TMEM address units
   - line stride units
   - tile descriptor reuse between tile 7 load tile and tile 0 render tile
4. If sampled data is all zero, fix loadblock/TMEM layout.
5. If sampled data is non-zero but final PPM is green, fix write ordering or final framebuffer selection.

Avoid adding huge logs. Use summarized counters and first few samples only.

## Potential quick code improvements

- Add `EUTHERDRIVE_N64_HEADLESS_STOP_ON_COLOR_DIVERSITY=1`.
- Add `N64Adapter` or headless access to color diversity so we can stop when output is no longer solid fill.
- Implement raw N64 savestate if practical. Current `Ryu64Core.SaveState/LoadState` are stubs, so `--load-raw-state` cannot currently accelerate N64 iteration.
- If adding savestate is too big, add a temporary "run until RDP first texrect, then dump RDRAM/TMEM summary" mode instead.

## Commands to inspect current work

```sh
git status --short
git diff -- Ryu64/Ryu64.MIPS/Memory.cs Ryu64/Ryu64Core/Ryu64Core.cs EutherDrive.Core/N64Adapter.cs EutherDrive.Headless/Program.cs
```

Build:

```sh
dotnet build EutherDrive.Headless/EutherDrive.Headless.csproj -c Release -nologo -v minimal -nr:false -m:1 -p:IncludeOptionalCores=true
```

## Last known conclusion

We are past "no framebuffer" and past "wrong stale framebuffer" for OoT. The next blocker is RDP texture rendering. The current minimal RDP path clears the correct buffer but does not yet reproduce texture rectangles, so the user sees green fill in UI/headless.
