# Ryu64 Rendering Handoff

Date: 2026-05-14

## Current State

Ryu64 now reaches real N64 framebuffer output in Zelda, Mario, Perfect Dark, and the boot logo path, but rendering is still approximate. The latest work focused on making the output more stable and closer to RDP semantics rather than only faster.

Recent relevant commits:

- local WIP 2026-05-14: MAME-like DXT row stepping for `LoadBlock`
- local WIP 2026-05-14: MAME-style TMEM row-swap for `LoadTile` and texture fetches
- `6a9322b Broaden Ryu64 boot zero-loop fast path`
- `0808d5e Improve Ryu64 boot region and fast paths`
- `f2a7486 Ryu64: apply depth to shaded triangles`
- `9645e12 Ryu64: avoid solid fill for failed texrects`
- `deff9de Ryu64: reuse framebuffer buffers`
- `b394bdf Ryu64: match RDP shade component scale`
- `7b702c5 Ryu64: keep visible RDP snapshots during buffer flips`

## What Changed

`9645e12` removes the default solid-color fallback when `TextureRectangle` sampling fails. That fallback was useful during bring-up, but it is not MAME-like RDP behavior and can produce bogus large solid blocks, such as the brown/flat rectangles seen in Zelda scenes. The fallback is still available behind:

```sh
EUTHERDRIVE_N64_TEXRECT_SOLID_FALLBACK=1
```

`f2a7486` applies RDP depth test/update to non-textured `TriangleZ` and `TriangleShadeZ` paths. Before this, depth was only honored in textured triangles, so shaded/solid Z triangles could overwrite in draw order and cause priority errors.

`deff9de` reduces framebuffer overhead by reusing a scratch framebuffer buffer and copying visible RDP snapshots directly into it. It also avoids repeat visible-pixel scans within one framebuffer decision.

2026-05-14 local WIP adds MAME-like TMEM address swizzling for `LoadTile`, `LoadBlock`, and texture sampling. The old path copied loaded texture rows/blocks linearly into TMEM and sampled them linearly. MAME alternates the byte/word address XOR by row, and its fetchers use the same odd/even row swap. Ryu64 now mirrors that for 8-bit, 16-bit, and 32-bit tile loads and for RGBA, CI, IA, and I fetches. `LoadBlock` now writes through the same word xor for normal sequential blocks, with 32-bit textures split into the paired TMEM planes.

The follow-up DXT `LoadBlock` WIP makes block loading respect `sl`, `tl`, `sh`, and `dxt` instead of always copying from the start of the texture image. It copies in MAME-style 8-byte groups and flips word XOR when the DXT accumulator crosses row bit 11, adding the tile line stride on row transitions. This is still intentionally narrower than full MAME because Ryu64 does not decode YUV yet.

Performance note: this was kept in the hot path as fixed `uint` xor/mask helpers and row-level precomputed xor values in load loops. No per-pixel allocations, dictionaries, LINQ, or generalized format objects were added. The 4-bit `LoadTile` path remains on the previous linear copy fallback because MAME's `LoadTile` switch only covers 8/16/32-bit texture image sizes; CI4 fetch addressing now uses the MAME-style packed nibble address. DXT row stepping adds a branch in `LoadBlock`, not in the pixel fetch loop.

## Verification

Build used:

```sh
dotnet build Ryu64/Ryu64Core/Ryu64Core.csproj -c Release /m:1
```

This builds successfully. The current local run also built:

```sh
dotnet build Ryu64/Ryu64Core/Ryu64Core.csproj -c Release --no-restore
dotnet build EutherDrive.Headless/EutherDrive.Headless.csproj -c Release --no-restore
```

Both builds succeeded. Existing warning noise remains outside this RDP change.

Headless smoke tests used:

```sh
env EUTHERDRIVE_HEADLESS_CORE=n64 EUTHERDRIVE_N64_HEADLESS_PERF=1 EUTHERDRIVE_N64_PERF=1 EUTHERDRIVE_SAVESTATE_SLOT=2 EUTHERDRIVE_N64_HEADLESS_TRACE_FRAMES=1 EUTHERDRIVE_N64_HEADLESS_DUMP_FRAMES=100,180 EUTHERDRIVE_HEADLESS_DUMP_DIR=/tmp/n64_zelda_slot2_depth_all_tri dotnet run --project EutherDrive.Headless/EutherDrive.Headless.csproj -c Release --no-build -- --load-savestate "/home/nichlas/roms/N64/Legend of Zelda, The - Ocarina of Time (USA) (Rev 2).z64" /home/nichlas/roms/N64/Legend_of_Zelda__The_-_Ocarina_of_Time__USA___Rev_2_.z64_49acd388.euthstate 180
```

Zelda slot 2 stayed visible through 180 frames. Last run after depth changes:

- `avg_fps=17.214`
- stable visible framebuffer
- output inspected at `/tmp/n64_zelda_slot2_depth_all_tri/frame179.png`

Mario smoke:

```sh
env EUTHERDRIVE_HEADLESS_CORE=n64 EUTHERDRIVE_N64_HEADLESS_PERF=1 EUTHERDRIVE_N64_PERF=1 EUTHERDRIVE_N64_HEADLESS_TRACE_FRAMES=1 EUTHERDRIVE_N64_HEADLESS_DUMP_FRAMES=120,240,320 EUTHERDRIVE_HEADLESS_DUMP_DIR=/tmp/n64_mario_depth_all_tri dotnet run --project EutherDrive.Headless/EutherDrive.Headless.csproj -c Release --no-build -- /home/nichlas/roms/N64/Super_Mario_64_\(USA\)-.n64 320
```

Mario kept visible framebuffer content. Last run after depth changes:

- `avg_fps=11.626`
- framebuffer content recovered and stayed visible
- output inspected at `/tmp/n64_mario_depth_all_tri/frame240.png`

2026-05-14 smoke after TMEM row-swap WIP:

```sh
env EUTHERDRIVE_HEADLESS_CORE=n64 EUTHERDRIVE_N64_HEADLESS_PERF=1 EUTHERDRIVE_N64_SKIP_AUDIO=1 EUTHERDRIVE_N64_RUNFRAME_WAIT_MS=1 EUTHERDRIVE_N64_BRINGUP_RUNFRAME_WAIT_MS=1 dotnet run --no-build --project EutherDrive.Headless/EutherDrive.Headless.csproj -c Release -- "/home/nichlas/roms/N64/Earthworm Jim 3D (Europe) (En,Fr,De,Es,It).z64" 420
```

- PAL/region path still passes the unit check and reaches framebuffer at runFrame 67.
- Framebuffer stayed steady through runFrame 420.
- Final headless `fb_has_content` still reports false for this ROM despite steady VI logs; treat the steady logs as the useful signal until that final snapshot heuristic is fixed.

```sh
env EUTHERDRIVE_HEADLESS_CORE=n64 EUTHERDRIVE_N64_HEADLESS_PERF=1 EUTHERDRIVE_N64_PERF=1 EUTHERDRIVE_N64_SKIP_AUDIO=1 EUTHERDRIVE_N64_RUNFRAME_WAIT_MS=1 EUTHERDRIVE_N64_BRINGUP_RUNFRAME_WAIT_MS=1 dotnet run --no-build --project EutherDrive.Headless/EutherDrive.Headless.csproj -c Release -- --load-savestate "/home/nichlas/roms/N64/Legend of Zelda, The - Ocarina of Time (USA) (Rev 2).z64" "/home/nichlas/roms/N64/Legend_of_Zelda__The_-_Ocarina_of_Time__USA___Rev_2_.z64_49acd388.euthstate" 120
```

- Zelda slot 1 stayed visible through 120 frames.
- RDP after the run: `lists=9849`, `cmds=130117/130117`, `tri=14563`, `tex=3680`, `fill=714`.
- Initial 120-frame run after `LoadTile`/fetch swizzle: `rdpList=604/223.867ms avg=0.371ms`, `triTex=730/192.231ms avg=0.263ms`, `fbSnap=650/7.822ms avg=0.012ms`.
- Follow-up 60-frame run after adding matching sequential `LoadBlock` swizzle stayed visible: `rdpList=241/146.929ms avg=0.610ms`, `triTex=292/118.250ms avg=0.405ms`, `fbSnap=260/3.670ms avg=0.014ms`.
- Follow-up 60-frame run after DXT `LoadBlock` stayed visible: `rdpList=241/193.643ms avg=0.803ms`, `triTex=292/153.876ms avg=0.527ms`, `fbSnap=260/4.763ms avg=0.018ms`. This is slower in the same short smoke window, so keep watching perf while improving correctness.

Earthworm Jim Europe cold smoke after DXT:

- framebuffer recovered at runFrame 88
- steady at runFrame 180
- no region/unit regression

## Known Issues

- Mario boot/logo is still inconsistent in current headless cold boot runs. In this local run, both the Europe ROM and the older USA smoke ROM stalled before VI became active, so no RDP signal was available from Mario.
- Zelda terrain is recognizable but still has warping, missing/incorrect texture details, and likely TMEM address issues.
- Some UI/browser testing uses copied Ryu64 DLLs in `EutherDrive.Headless/bin/Release/net8.0/` because full project build is blocked by unrelated Taito code.
- Performance varies run-to-run; use headless savestates for comparisons.
- `LoadBlock` now has DXT-driven row stepping, but it is a pragmatic subset. YUV-specific packing is not implemented. Perf also needs more samples because the short Zelda smoke got slower after DXT.

## Next Best Targets

1. Audit and tune `LoadBlock` perf:
   - compare DXT and non-DXT command traces
   - skip DXT bookkeeping when `dxt == 0`
   - consider specialized loops per texture size if profiling confirms this path is hot
   MAME reference: `/home/nichlas/mame/src/mame/nintendo/n64_v.cpp`, especially `cmd_load_block`.

2. Improve texture load semantics:
   - `LoadBlock`
   - 4-bit `LoadTile` edge cases if real ROM traces show them
   - TLUT addressing

3. Continue texture fetch semantics:
   - bilinear filtering
   - copy-cycle texrect behavior
   - tile LOD / second texture tile

4. Continue depth/coverage work:
   - coverage hidden bits
   - image-read blending
   - Z mode edge cases

5. Use Zelda slot 1/2 and a PAL game like Earthworm Jim as smoke tests after each RDP change. Use Mario only after the cold boot regression is understood, because it currently does not reach VI in this local headless run.

## Working Tree Note

At the time of this handoff, there are unrelated dirty/staged files outside Ryu64. Do not include them in Ryu64 commits unless explicitly requested.
