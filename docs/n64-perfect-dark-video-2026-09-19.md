# Perfect Dark video readback correction — 2026-09-19

Investigated the user's 576x237, cropped Perfect Dark image using slot 1 from
Perfect_Dark__USA___Rev_1_.z64_4e51142a.euthstate. The original file was only read;
SHA-256 remains 8d16ea33d2e16723c0948f0910bdb9b9209771df62b4af95a9c2a636cfc36d97.

## Cause and changes

Saved core VI registers: STATUS=00013056, WIDTH=00000240 (576),
V_START=002301fd, Y_SCALE=00000800. The active vertical interval is 237
field lines, but Y_SCALE is 2.0, so the source buffer has 474 rows.
The old readback ignored Y_SCALE and copied only 237 rows. Both the saved
adapter image and a frozen-state read reproduced the missing lower half.

The shared source-height calculation now uses the low 12 bits of Y_SCALE
as a 10-fractional-bit coefficient. Core readback and RDP framebuffer metadata
use the same calculation. Zero/uninitialized scale retains the previous
bring-up fallback. Valid source heights extend to 576 for PAL high resolution.

The UI presents N64 source buffers with a 4:3 display aspect (3:4 when rotated),
rather than treating all source pixels as square. This addresses this game's
576x474 source; it does not implement the full VI filter, field reconstruction,
horizontal crop/scale/offset, borders or game-selectable widescreen presentation.
The frozen state has H_START=0, so no new horizontal cropping is inferred.

Previously, both ordinary and cached readback called a translated memory-byte
reader once for every framebuffer byte. Both paths already validate the entire
range as RDRAM, so they now use Buffer.BlockCopy on that byte-ordered array.
Consumer notifications, events, snapshot fallback and cache invalidation remain.

## Evidence

- Frozen-state readback changes from 576x237 to 576x474; the complete Rare logo
  is visible in the RAM-derived image.
- A 12-second resumed run produces a complete, centered Nintendo logo at
  576x474. The RSP graphics counter advances; no unsupported opcode is reported.
- Equal-work frozen readback benchmark (Y_SCALE forced to 1.0 on both versions):
  100 cached reads of 576x237 took 3945.348 ms before and 40.514 ms after.
  Both output hashes are
  6B148A7E7C2F6FAB801425ABD061AD9263883ACA6B06D3CB008326B4EB63FC9E.
  This measures CPU-side image extraction, not whole-game FPS or GPU throughput.
- 81 video checks pass: scaled/unscaled/half-height/PAL/wrapped/uninitialized VI,
  16/32-bit pixels, 320/576 pitches, cached and ordinary reads, exact RAM bytes,
  and rejection of a framebuffer extending beyond RDRAM.
- 6,845 renderer/interrupt checks and snapshot coalescing checks pass.
- Probe and Linux UI Release builds pass; UI has 383 warnings and zero errors.
  UI Ryu64Core.dll and Ryu64.MIPS.dll match the tested probe binaries.

Hardware register semantics were cross-checked against the local reference
implementation /home/nichlas/cen64/vi/controller.c: VI source height derives
from the active half-line interval and the Y coefficient. No source was copied.

## Artifacts and checks

Untracked artifacts are in .build-tmp/perfect-dark-vi-2026-09-19/:
original saved-frame.png, raw-474.png, reference/candidate binaries and readback
logs, extracted core-state.bin/cpu-state.bin, live/frame-0010.png, execution log,
build/check logs and SHA256SUMS. The extracted files are copies, not replacements
for the original savestate.

The extracted core-state.bin includes the Ryu64Core header and is the correct
input for N64Probe's optional state argument. cpu-state.bin is only for direct
R4300.LoadState diagnostics.

Run:

```sh
dotnet tools/N64Probe/bin/Release/net8.0/N64Probe.dll --check-video
dotnet tools/N64Probe/bin/Release/net8.0/N64Probe.dll --check-render
EUTHERDRIVE_N64_PERF=1 dotnet tools/N64Probe/bin/Release/net8.0/N64Probe.dll --check-snapshots
```

The remaining CPU/RSP/rendering cost can still make Perfect Dark slow. The
7 gfx/s display counts completed graphics tasks, not unique presented frames.
No new whole-game performance percentage is claimed from this readback test.
