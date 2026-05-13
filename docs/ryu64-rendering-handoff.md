# Ryu64 Rendering Handoff

Date: 2026-05-13

## Current State

Ryu64 now reaches real N64 framebuffer output in Zelda, Mario, Perfect Dark, and the boot logo path, but rendering is still approximate. The latest work focused on making the output more stable and closer to RDP semantics rather than only faster.

Recent relevant commits:

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

## Verification

Build used:

```sh
dotnet build Ryu64/Ryu64Core/Ryu64Core.csproj -c Release /m:1
```

This builds successfully. Full headless project build is currently blocked by unrelated untracked Taito code in `EutherDrive.Core/Arcade/Taito/`.

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

## Known Issues

- Mario boot/logo is still visually incomplete: text and pieces show, but geometry/textures are not fully correct.
- Zelda terrain is recognizable but still has warping, missing/incorrect texture details, and likely TMEM address issues.
- Some UI/browser testing uses copied Ryu64 DLLs in `EutherDrive.Headless/bin/Release/net8.0/` because full project build is blocked by unrelated Taito code.
- Performance varies run-to-run; use headless savestates for comparisons.

## Next Best Targets

1. Implement MAME-like TMEM row XOR/address swizzling for texture loads and texture fetches.
   MAME reference: `/home/nichlas/mame/src/mame/nintendo/rdptpipe.cpp` and `/home/nichlas/mame/src/mame/nintendo/n64_v.cpp`.

2. Improve texture load semantics:
   - `LoadTile`
   - `LoadBlock`
   - 4-bit/8-bit CI and IA formats
   - TLUT addressing

3. Continue depth/coverage work:
   - coverage hidden bits
   - image-read blending
   - Z mode edge cases

4. Use Zelda slot 1/2 and Mario boot as reference smoke tests after each change.

## Working Tree Note

At the time of this handoff, there are unrelated dirty/staged files outside Ryu64. Do not include them in Ryu64 commits unless explicitly requested.
