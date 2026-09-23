# Mega Man 64: fewer texture-upload barriers

Linux continuation after the [idle-jump pass](n64-megaman-idle-speed-2026-09-23.md).
The reference already includes that pass and the Castlevania CPU-cache/TLUT
changes. The common committed base is still
`f5771bcf6ccf2f61dd487847e09b59b2853e436c`; this pass does not change CPU code.

## Input and diagnosis

The same frozen Mega Man 64 (USA) slot 1 supplies the sea/airship introduction.
ROM, container and extracted state hashes are recorded in the preceding note.
The source save is untouched. This measures the introduction, not combat or
desktop presentation overhead.

Artifacts are in `.build-tmp/n64-megaman-more-2026-09-23/`. `reference/` is a
copy of the preceding pass's final timing probe, and `reference-native.so` is
the previously shipped GPU library. `initial.diff` and `provenance.json`
preserve the working-tree starting point. Both sides use the same managed
probe; only the native GPU library differs.

A new CPU-thread profile collected 19,462 instruction-pointer samples. Block
dispatch accounts for 20.01%, JIT entry lookup for 4.92%, RSP execution for
4.29%, and the native library for 10.89%. These are exclusive CPU sample shares,
not wall-time percentages. The earlier idle-dominated profile is obsolete.

Separate native instrumentation found 8,396 LoadTile barriers in 15 guest
seconds, plus 1,350 palette barriers and 67 FULL_SYNC barriers. The sampled
LoadTile layout is a single-row CI8 source/tile with a 128-byte row. Its source
is disjoint from the pending CPU writes, but LoadTile previously always flushed.
Diagnostic builds are separate from timed and shipping builds.

## Source bounds and ordering

`LoadBlockRange::disjoint_tile` now proves a narrow class of LoadTile reads:

- Matching 8/16-bit texture-image and tile sizes, defined non-YUV formats.
- One row, non-reversed quarter-pixel S coordinates, and an aligned source
  span entirely inside the installed 8 MiB of RAM.
- The source span rounds to eight bytes and includes source-word padding.
  Tile stride and wrapping at the TMEM destination cannot expand it.

The proof follows the pinned paraLLEl-RDP commit
`1cecd042b2619bc505c12bfdc713808386f2b54d`:
`CommandProcessor::op_load_tile`, `Renderer::load_tile_iteration` and
`update_tmem_16` in `tmem_update.comp`. With one row, the shader clamps
`upload_y` to zero. Matching source/tile sizes bound `upload_x` by the rounded
width; its final halfword selection remains within the same eight-byte group.
The accepted row also fits a single upload iteration, even at maximum tile
stride. Odd addresses, multiple rows, mismatched sizes, YUV, 32-bit layouts,
wrapped S and unproven reads keep their barriers.

The cheap enclosing interval remains the first pending-write check. For
texture loads only, at most 128 pending patches can additionally be checked
individually with the same existing source proofs. All patches must be disjoint;
larger lists or any overlap retain the wait. Synthetic tests exercise the false
hazard where unrelated writes straddle a texture, the scan limit, and real
overlaps. This fallback produces no additional barrier reduction in the
measured Mega Man sequence compared with the LoadTile change alone.

Deferred writes still flush at FULL_SYNC, submission boundaries, and any
unproven read or draw. The flush waits for preceding GPU work before applying
the patches, so a later CPU store cannot change an earlier texture read.
CPU-byte ownership, GPU readback, frame publication and guest timing are
unchanged. There are no game identifiers or game addresses in the optimization.
The ABI, flags and native/core savestate formats are unchanged.

## Validation and timing

The texture suite passes **666 cases**, comparing all 8 MiB of RAM, hidden
memory and TMEM against an independently ordered, unbatched GPU context.
New coverage includes source sizes, all tile slots, formats, fractional S/T,
stride, destination wrapping, rounded source tails, same-value writes, source
changes, word padding, installed-RAM end, fallback layouts and sparse patches.
Vulkan synchronization validation is enabled. The deliberately reversed-S
case reaches the pinned renderer's existing unsupported-upload diagnostic on
both contexts; it does not produce a Vulkan validation error.

The four-run ABBA comparison measures guest seconds 5–30 using neutral input
at identical Joybus reads, CPU cores 6/7, two GPU workers and synchronous shader
compilation. Profiling, validation and builds run outside this series. Other
user applications remain running.

| Run | Wall seconds | Audio / wall, real time |
| --- | ---: | ---: |
| 1 / reference | 32.3668 | 77.27% |
| 2 / candidate | 26.1676 | 95.58% |
| 3 / candidate | 25.9106 | 96.53% |
| 4 / reference | 32.4385 | 77.10% |

Mean elapsed time falls from **32.4026 to 26.0391 seconds**, giving **24.44%
more throughput**. Generated audio divided by mean elapsed time rises from
approximately **77.2% to 96.1% of real time** in this headless intro test.
All six sampled frames and audio/input/RAM/task/cycle checkpoints match in
every run, as does complete final CPU/device/GPU state:
`a68f97f21ae4a1f79b9ea1c7b448cbbd8bbfe2b9f7ef3a7c8013caffa8d533bf`.

Write barriers drop from **29,851 to 3,601** (87.94% fewer). Each run retains
2,198,945 commands, 901 synchronizations, zero read hazards, 1,162 frames and
99,827,216 CPU-written bytes, with zero validation errors reported. The native
work changes how those writes are submitted, not how much guest work executes.

Mario's separate boot-and-moving-gameplay ABBA series matches all 18 sampled
frames and audio/input/RAM/task/position checkpoints, plus final RAM. Guest
seconds 70–90 average 28.1757 seconds before and 28.2035 after (-0.10%
throughput), effectively unchanged within run-to-run variation. This harness
does not export complete final CPU/device state.

Both Castlevania GPU saves (slots 2 and 3) match all three sampled frames and
checkpoints after 15 guest seconds, plus complete final CPU/device/GPU state,
against the previously accepted continuations. `cross-game-proof.json`
records the hashes.

The 13 managed and 68 native ABI checks, six GPU savestate round trips, and
58 live GPU replay checks against the accepted Mario reference also pass with
validation enabled.

Normal and GPU desktop Release builds complete with zero errors (505 warnings
each). The actual native library rebuilt by the desktop launcher separately
passes the 666 texture cases and all six GPU savestate round trips.
With synchronization validation and CPU-write auditing enabled, that library
also matches the reference's three images/checkpoints and complete final
CPU/device/GPU state after 15 guest seconds. Both desktop builds exclude
performance, JIT-profile and journal hooks; the normal CPU assembly remains
byte-identical to the preceding validated probe. The native library contains
no diagnostic instrumentation. `shipping-audit.json` records these checks
and binary hashes. The original save container remains byte-identical to
the input copy (`save-preservation.json`).

Evidence includes `profile`, `diagnostic*.log`, `tile-smoke`, `smoke-proof.json`,
`combined-textures.log`, `combined-abba`, `gpu-work-proof.json`, `mario-abba`,
`cross-game-proof.json`, `validation-driver.log`, `shipping-*`, and
`source-manifest.json`. ROM-derived captures remain under `.build-tmp/`.

## Continuing

Use `scripts/run-n64-gpu-desktop.sh`, select Mega Man 64 and load slot 1.
The measured percentage excludes UI overhead and does not promise real time
throughout the game. A later gameplay save would provide another workload.

Reprofile this version before the next pass. Active CPU block dispatch is
still prominent, while multirow/other texture layouts retain conservative
waits. Keep any further relaxation tied to the pinned shader's actual reads
and compare full memory, TMEM, audio, images and final state again.
