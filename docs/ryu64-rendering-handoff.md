# Ryu64 Rendering Handoff

Date: 2026-05-14

## Current State

Ryu64 now reaches real N64 framebuffer output in Zelda, Mario, Perfect Dark, and the boot logo path, but rendering is still approximate. The latest work focused on making the output more stable and closer to RDP semantics rather than only faster.

Recent relevant commits:

- local WIP 2026-05-14: optimize textured triangle hot path
- local WIP 2026-05-14: broaden TLUT fetch semantics for RGBA32/IA/I
- local WIP 2026-05-14: specialized fast `LoadBlock` loops while preserving DXT row stepping
- `61e5c7a Add Ryu64 LoadBlock row stepping`
- `8425791 Improve Ryu64 TMEM texture layout`
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

2026-05-14 local WIP then optimized `LoadBlock` without removing that behavior. The hot path is split into sequential 16-bit, sequential 32-bit, DXT 16-bit, and DXT 32-bit loops. Sequential blocks now skip DXT bookkeeping entirely, 32-bit blocks avoid a per-group size branch, and the per-word TMEM stores are local direct byte copies over cached `RDRAM`/TMEM references. The DXT paths still keep the MAME-like accumulator, XOR flip, tile-line row advance, and 32-bit high/low TMEM plane split.

Performance note: preserve semantics first, then specialize the loop. Do not "optimize" by reverting DXT, source coordinate handling, row XOR, or 32-bit plane splitting. This pass moved cost out of the common path instead. No per-pixel allocations, dictionaries, LINQ, or generalized format objects were added. The 4-bit `LoadTile` path remains on the previous linear copy fallback because MAME's `LoadTile` switch only covers 8/16/32-bit texture image sizes; CI4 fetch addressing now uses the MAME-style packed nibble address. DXT row stepping adds a branch in `LoadBlock`, not in the pixel fetch loop.

The next TLUT fetch WIP extends palette lookup beyond CI/RGBA16. MAME's fetch table applies TLUT to RGBA32, IA4/8/16, and I4/8 as well. Ryu64 now routes those modes through the existing TLUT conversion when `other_modes.tlut_en` is set:

- RGBA32 uses the first TMEM byte as the palette index, matching MAME's `c >> 24`.
- IA4/I4 use `tile.palette << 4 | nibble`.
- IA8/I8 use the fetched byte.
- IA16 uses the high byte of the fetched word.

This is intentionally a narrow semantic expansion. It keeps the raw fast path unchanged when TLUT is disabled.

The latest perf WIP reduces per-pixel work in textured triangles and rectangles without changing visible RDP behavior:

- Texture sampler dimensions/origin are prepared once per draw call instead of recomputed for every sampled pixel.
- Textured triangle spans now use incremental S/T/W, shade, and depth values across the row instead of doing `Math.Round` and repeated `xStep * delta` multiplications per pixel.
- A trial direct 16-bit framebuffer writer was rejected because the Zelda smoke got slower; keep measuring before committing inner-loop rewrites.

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
- Follow-up 60-frame run after specialized `LoadBlock` loops stayed visible: `rdpList=329/172.833ms avg=0.525ms`, `triTex=427/143.844ms avg=0.337ms`, `fbSnap=390/5.662ms avg=0.015ms`. The command mix changed in this short savestate window, but average RDP/texture cost is back below the previous DXT regression while keeping DXT semantics.
- Follow-up 60-frame run after broadening TLUT fetches stayed visible: `rdpList=241/154.354ms avg=0.640ms`, `triTex=292/125.235ms avg=0.429ms`, `fbSnap=260/3.897ms avg=0.015ms`. The raw non-TLUT path remains close to the specialized `LoadBlock` baseline.
- Follow-up 60-frame run after textured triangle hot-path optimization stayed visible: best run `rdpList=241/140.065ms avg=0.581ms`, `triTex=292/113.615ms avg=0.389ms`, `fbSnap=260/3.598ms avg=0.014ms`; confirm run after removing the rejected direct writer stayed visible at `rdpList=241/142.911ms avg=0.593ms`, `triTex=292/117.584ms avg=0.403ms`.

Earthworm Jim Europe cold smoke after DXT:

- framebuffer recovered at runFrame 88
- steady at runFrame 180
- no region/unit regression

Earthworm Jim Europe cold smoke after specialized `LoadBlock` loops:

- framebuffer recovered at runFrame 65
- steady at runFrame 180
- no region/unit regression

Earthworm Jim Europe cold smoke after broader TLUT fetches:

- framebuffer recovered at runFrame 72
- steady at runFrame 180
- no region/unit regression

Earthworm Jim Europe cold smoke after textured triangle hot-path optimization:

- framebuffer recovered at runFrame 62
- steady at runFrame 180
- no region/unit regression

## Known Issues

- Mario boot/logo is still inconsistent in current headless cold boot runs. In this local run, both the Europe ROM and the older USA smoke ROM stalled before VI became active, so no RDP signal was available from Mario.
- Zelda terrain is recognizable but still has warping, missing/incorrect texture details, and likely TMEM address issues.
- Some UI/browser testing uses copied Ryu64 DLLs in `EutherDrive.Headless/bin/Release/net8.0/` because full project build is blocked by unrelated Taito code.
- Performance varies run-to-run; use headless savestates for comparisons.
- `LoadBlock` now has DXT-driven row stepping, but it is a pragmatic subset. YUV-specific packing is not implemented. Perf recovered after loop specialization, but still needs more samples because RDP command mix varies across short savestate windows.

## Next Best Targets

1. Continue RDP semantic work with performance-aware implementation:
   - keep specialized loops for hot `LoadBlock`/fetch paths
   - compare DXT and non-DXT command traces when touching texture loads
   - prefer local cached arrays, fixed-size loops, and early branch splitting over removing correctness behavior
   MAME reference: `/home/nichlas/mame/src/mame/nintendo/n64_v.cpp`, especially `cmd_load_block`.

2. Improve texture load semantics:
   - `LoadBlock`
   - 4-bit `LoadTile` edge cases if real ROM traces show them
   - TLUT load edge cases and palette-cache invalidation if traces expose stale colors

3. Continue texture fetch semantics:
   - bilinear filtering
   - copy-cycle texrect behavior
   - tile LOD / second texture tile

4. Continue performance work with measurements:
   - avoid per-pixel `Math.*` and repeated invariant calculations in triangle/texrect paths
   - specialize only when a before/after smoke shows a win
   - use Zelda savestate `triTex` and `rdpList` as the quick regression guard

5. Continue depth/coverage work:
   - coverage hidden bits
   - image-read blending
   - Z mode edge cases

6. Use Zelda slot 1/2 and a PAL game like Earthworm Jim as smoke tests after each RDP change. Use Mario only after the cold boot regression is understood, because it currently does not reach VI in this local headless run.

## Working Tree Note

At the time of this handoff, there are unrelated dirty/staged files outside Ryu64. Do not include them in Ryu64 commits unless explicitly requested.
