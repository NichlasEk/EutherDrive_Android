# Rampage partial RDP commands, flicker and graphics deadlock

Baseline: `b768651c`. Local evidence: `.build-tmp/rampage-freeze-2026-09-20/`.
This is a graphics-correctness fix; CPU JIT optimizations remain enabled.

## Reproduction and cause

The user's new Rampage slot 1 (outer frame 12898) was already stuck. Graphics
jobs remained at 5224 while audio jobs and the CPU continued. Current, previous
CPU-JIT and CPU-JIT-disabled builds all reproduced that saved deadlock. This
comparison alone does not date when corruption originally occurred.

DPC CURRENT was `0x18d160`, END `0x18d170`. The remaining words were:

```
08600000 fc000400 e9000000 00000000
```

The first pair was a texture-rectangle payload, whose header had been lost.
Interpreting its high byte as a triangle opcode makes the parser wait for 32
bytes, hiding the intact trailing FULL_SYNC. The game waits for DP completion
at `0x8005ca8c` and keeps servicing audio interrupts.

A trace from the earlier working snapshot confirmed the producing condition:
Rampage's command ring wraps from `0x18f160` to `0x18d160` while the previous
rectangle is only half submitted. The old parser leaves CURRENT at `0x18f158`
and retains no private copy of that header. The next START redirects CURRENT,
losing the prefix. Depending on the payload, drawing is lost, payload words
are misinterpreted, or the command stream blocks entirely.

Reference behavior was checked against the original
[Angrylion command ingestion implementation](https://github.com/ata4/angrylion-rdp-plus/blob/master/src/core/n64video.c):
it retains incomplete commands independently of DMA addresses and advances
CURRENT for fetched words. Our implementation is independent, retaining its
existing direct path for complete commands.

## Implementation

- Keep at most one incomplete command (44 words, the maximum triangle length).
- Consume and copy submitted fragments immediately; CURRENT reports bytes read.
- Preserve pending words across new START addresses and RDRAM/DMEM source changes.
- Execute only when the entire command is present. The existing renderer reads
  the retained words for a reassembled command, including triangle coefficients.
- FULL_SYNC remains responsible for DP completion; no timeout or synthetic
  completion interrupt is added to normal emulation.
- Memory savestate v6 appends 184 bytes for the partial command. Versions 1–5
  remain readable and clear absent pending state. Existing corrupted v5 saves
  cannot reconstruct a command header they never stored.

## Captured slot recovery

Original save container SHA-256:
`ab095f52d957a26f49d2f4abb9f373752f50b9333b752dc30c2e5899b5c66f76`.
The untouched original is retained as `original.euthstate` in the evidence
folder. The one-off `recover/` helper checks the exact captured addresses and
all four words before discarding the orphaned payload and executing the intact
FULL_SYNC in a separate copy. This is not automatic game-specific recovery in
the emulator. The next frame redraws the scene.

The reconstructed container keeps slots 2 and 3 byte-for-byte intact. Its hash:
`b811259c48d2c4e79c7995a921550f52333bc4ef49a131d9e42aaaa82fd31a5f`.

Recovered slot 1 ran for 121 seconds: graphics jobs advanced from 5226 to 8378
and audio jobs from 6843 to 9995, about 26.2 graphics tasks/s. No unknown CPU
instructions were reported. Frame captures show continuing character/helicopter
animation. This validates recovery and continued output; it is not a claim
that every unrelated rendering issue has been eliminated.

## Checks

`--check-rdp-streaming` covers 197 cases: every 8-byte split point of all eight
triangle variants and both texture-rectangle commands, recycled input storage,
source changes, mid-command save/restore, legacy-state reset and trailing DP
interrupts. Pixels match unsplit execution. A dedicated case reproduces the
exact Rampage ring boundary and the misleading `0x08600000` payload.

The snapshot test now initializes the CPU memory reference before FULL_SYNC,
matching the normal core setup; its old standalone harness omitted that setup.

The exact ring-wrap oracle gives CURRENT=`18d160`, END=`18d170`, DP=false with
the baseline DLL; the fixed DLL gives CURRENT=END=`18d170`, DP=true. This isolates
the graphics defect from CPU execution and timing.

Additional checks pass: 6,845 render/interrupt cases; 1,092 intensity-alpha,
960 rectangle-depth and 24 blender cases; 81 video cases; framebuffer snapshot
equivalence; RSP scheduling; and 1,263 CPU JIT full-state comparisons.
The recovered run was saved and reloaded for a further 20 seconds, advancing
graphics jobs from 8382 to 8820. Mario, Duke and Gauntlet also continued producing
graphics/audio in 20-second saved-scene checks, with no unknown instructions.

The Release UI builds with zero errors. Its tested MIPS DLL SHA-256 is
`29dff6dd72a8b7ec4ca5a132b3e6139d777a81c886467025e5988a06f6fa0dab`.

## Installed recovery

After validation, the reconstructed container was installed atomically only
after confirming the user's file had not changed. Original backup:

```
/home/nichlas/roms/N64/Rampage_2_-_Universal_Tour__Europe_.z64_ef59e77d.euthstate.before-rdp-stream-fix-20260920.bak
```

Restart the new Release app before loading slot 1: the recovered slot uses the
new memory v6 format. Slots 2 and 3 retain their original payloads. To undo only
the save repair, restore the backup container; it contains the original frozen
slot, so this does not undo the graphics deadlock by itself.
