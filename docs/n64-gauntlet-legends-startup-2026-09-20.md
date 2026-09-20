# Gauntlet Legends (Europe): startup deadlock

The N64 game hung on a black 416x219 framebuffer. This is unrelated to the
arcade Gauntlet Dark Legacy core.

## Reproduction and cause

Artifacts: `.build-tmp/n64-gauntlet-2026-09-20/`.
Original slot 1 container SHA-256:
`fe6ca019e0dee56778bd2018db3b23efa37df991a19da8451d37f1311122b64d`.
The original container and ROM were not modified.

Loading slot 1 reproduces the idle CPU at `e0002afc`, with graphics/audio task
counts fixed at 3/5. Cold boot reveals that graphics task 3 loops until the
500-million-instruction RSP limit. The ordinary interpreter reproduces the
same failure, so this is not an RSP block JIT regression.

The display-list producer writes `DE010000 E02126B8` at physical `2126b8`:
a branch back to itself. The CPU later replaces it with zeroes as it publishes
the following commands. Previously synchronous RSP execution prevented the
CPU from making that publication. Increasing the instruction limit cannot fix
this dependency.

Cooperative execution alone passed that point but stalled after 5 graphics
jobs. DPC_END incorrectly raised DP interrupts for every submitted buffer,
before the display list's FULL_SYNC. Interleaving made those premature
interrupts observable to the CPU scheduler. Moving the interrupt to FULL_SYNC
lets the game continue through logos and character/name selection.

## Implementation

- Valid OSTasks execute in 16,384-instruction slices. CPU device scheduling
  resumes a pending slice after 16,384 CPU cycles; this is a coarse cooperative
  model, not a claim of cycle-accurate CPU/RSP overlap.
- A slice retains registers, vector accumulator/flags, reciprocal state, PC,
  pending branch and delay-slot state. It does not assert BREAK, task-done, or
  SP completion interrupts, and does not count as a new graphics/audio job.
- CPU HALT suspends continuation; an explicit SP_PC write discards the previous
  continuation's control flow. Raw IPL/helper execution retains its old path.
- DP interrupts come from FULL_SYNC rather than DPC_END buffer submission.
- Memory savestate v5 appends the RSP execution state and pending-slice flag.
  Versions 1–4 remain readable. New states require the updated emulator.
- Probe capture can skip matching tasks with `N64_PROBE_CAPTURE_RSP_SKIP`.

## Validation

- `--check-rsp-scheduling`: 32 slice budgets including branch/delay-slot cuts,
  full memory/RSP save/load into a new instance, CPU publication after dispatch,
  pending-task restore, HALT/resume, explicit PC replacement, and DP interrupt
  timing. Passes with both RSP block JIT and ordinary interpreter.
- `--check-render`: 6,845 checks passed.
- `--check-sprites`: 1,092 intensity-alpha, 960 rectangle-depth and 24 blender
  checks passed.
- `--check-rsp`: 10,368 instruction cases, 2,048 vector-copy and 32,768 shuffle
  cases passed.
- Gauntlet cold boot: 206 graphics jobs after 60 seconds instead of stopping at
  3. Saved/restored this new state and continued for another 240 seconds through
  character/name entry into a rendered gameplay scene: 1,045 graphics jobs,
  155,205 triangles, zero unknown CPU opcodes, no RSP failures.
- Final ordinary-interpreter cold boot: 56 graphics jobs after 25 seconds.
- Duke and Perfect Dark: 30-second runs from the established gameplay snapshots
  continue producing graphics without RSP failures or unknown CPU opcodes.
  These are functional smoke tests, not performance comparisons.
- Release UI builds successfully. Probe and UI core SHA-256 both:
  `c95f22d69f298c6e873ebd0e249cd63a0d5aa62c60bdec97ef40d22335993f82`.

## User testing

Restart the application and start Gauntlet from the ROM's beginning. The old
slot 1 records the already-failed task and does not contain its RSP registers;
loading it cannot reconstruct that lost execution. Leave it intact for diagnosis
and make a fresh save after startup. Playability and all later levels remain to
be tested; this change fixes the reproduced startup deadlock.
