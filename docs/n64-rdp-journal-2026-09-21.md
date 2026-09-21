# N64 ordered RDP journal — 2026-09-21

This checkpoint implements the command/write-recording part of stage 1 in
[the GPU plan](n64-gpu-backend-plan-2026-09-21.md). It adds a reproducible input
boundary for a future GPU backend. **It does not change installed gameplay
speed or enable live GPU rendering.** The Linux software renderer remains the
runtime default. Android is outside this work.

## What is now proved

The opt-in `N64RdpJournalCapture=true` build observes complete commands after
the existing FIFO assembler. It copies command words immediately, including
commands completed from recycled buffers or a different DMA source. Ordered
external writes carry exact byte offsets and payloads, including stores that
leave the same value in software memory. Software RDP writes are excluded.

A full-memory shadow audit before each command rejects changed bytes that
were not reported by a write hook. Synthetic checks found two real capture
issues during implementation:

- `WriteUInt64`, byte-indexer and bulk/mirrored writes bypass the usual
  framebuffer notification. The capture build now also observes the actual
  backing-array offset in that path. DMA hooks cover mirrored destinations.
- A command that has just finished assembling is temporarily a *complete*
  FIFO, which the normal partial-command savestate reader correctly rejects.
  Diagnostic snapshotting clears that FIFO while writing `start.bin` and
  restores it immediately. The next journal record contains the full command.
  Normal savestate format and command execution are unchanged.

Repeated writes between commands coalesce. A separate byte bitset preserves
unchanged-value stores, which a memory diff cannot discover. Patches never
round up to pages or words: untouched GPU output in neighbouring bytes survives.

The software replayer restores `start.bin`, applies writes and VI observations,
and feeds the complete commands through scratch DMEM. At every FULL_SYNC it
requires identical SHA-256 hashes for all RDRAM, hidden bits, TMEM and TLUT,
as well as image registers and the DP completion bit.

## Measured captures and checks

Artifacts are under `.build-tmp/n64-journal-2026-09-21/`; ROM-derived data,
reference sources and binaries are not committed. These counts describe
captured input, **not gameplay throughput**. Warm captures may begin partway
through the first frame. The unchanged-byte column counts touched bytes whose
final coalesced value equals the pre-write audit baseline, not individual store
instructions.

| Capture | FULL_SYNC checkpoints | Commands | Exact write spans | Payload bytes | Bytes written with unchanged values |
| --- | ---: | ---: | ---: | ---: | ---: |
| Mario castle, warm | 3 | 3,444 | 10,645 | 383,228 | 182,220 |
| Mario swimming, warm | 3 | 2,690 | 8,331 | 256,539 | 151,816 |
| Rampage gameplay, warm | 3 | 5,609 | 8,624 | 120,473 | 73,509 |
| Mario from reset, extended | 20 | 15,090 | 31,852 | 3,347,529 | 1,744,829 |

All checkpoints in all four captures replay exactly in software. The byte
counts exclude the initial 8 MiB RDRAM + 4 MiB hidden snapshot, record headers,
commands and state file. Their small size is not a measured bus-transfer saving;
the current native replay still synchronizes and flushes much larger memories.

The 20-checkpoint reset capture also passes paraLLEl-RDP versus Angrylion at
every checkpoint: zero differing bytes across 8 MiB RDRAM, 4 MiB hidden memory
and 4 KiB TMEM, with Khronos and synchronization validation enabled and **zero
validation errors**. One renderer instance retains state across the sequence;
there is no per-frame reset or guessed register primer. The final image is
the Super Mario 64 startup logo, not gameplay. Earlier five-checkpoint and
synthetic two-checkpoint comparisons also passed.

The synthetic GPU fixture additionally exercises unchanged CPU stores into
rendered pixels, unaligned byte patches, mirrored/wrapping RAM, and texture
contents changing between loads. The negative control flips one expected
framebuffer bit after each successful comparison and detects exactly one
differing byte. Software replay separately rejects a corrupted external write.

Other checks:

- 45 managed journal checks: exact spans, CPU/JIT/SP/PI write paths,
  unchanged stores, page edges, source switches, recycled commands,
  save/load with a pending command, missing-hook audit and malformed input.
- 21 native parser checks, including bounds, command length, sequence,
  missing end/checkpoint, truncation and trailing bytes.
- Native warm-state replay rejects the input with an explicit missing-state-
  import error. It never pretends an approximate primer restores warm state.
- Existing 197 RDP streaming and 102 legacy export checks pass.
- Normal Release build passes. Compared with the accepted `19ba64c1` MIPS
  DLL: all 382 Memory fields and 453 methods match, with **108,179 identical
  IL bytes** and zero observer members. Exception clauses and locals match.
  Thus the diagnostic hooks add no normal-build calls, branches or fields.

Main evidence:

```text
checks-final-v2.log
checks-final-v2/{synthetic,split,warm-pending}/
native-parser-final/results.json
{mario-warm,swim-warm,rampage-warm}/journal/manifest.json
{mario-warm,swim-warm,rampage-warm}-replay.log
mario-reset-20/journal/{start.bin,journal.bin,manifest.json}
mario-reset-20-replay.log
mario-reset-20-gpu.log
mario-reset-20-gpu/frame-20-gpu.png
synthetic-gpu-final.log
verification-results.json
production-build.log
```

## What remains before live integration

This is an RDP-boundary journal, not the complete stage-1 event/timing trace.
VI values are observed at command boundaries; individual VI writes, scanout,
DPC submission timing and CPU/DMA reads are not recorded. Software replay
checks the DP bit at FULL_SYNC, not the exact interrupt cycle. Capture must run
on the emulator thread; loading a state during capture is unsupported.

Software exactness proves reproduction of the recorded input. GPU/Angrylion
exactness proves the two hardware-oriented renderers agree on that input.
Neither proves how CPU execution changes when it starts consuming GPU output.
Warm gameplay captures have **not** been validated through the GPU: that needs
raw hardware-state import or longer reset-to-gameplay journals.

The native implementation waits and invalidates before each external-write
batch, patches only recorded bytes, then flushes caches. Mario's extended
capture requires 15,089 such batches for 15,090 commands, largely because
command and ordinary CPU data are also in RDRAM. This deliberately conservative
path is unsuitable for a live speed claim. Optimizing it requires knowing which
ranges queued GPU work actually reads or writes, not dropping "unchanged" data
or delaying arbitrary CPU stores.

Next bounded work:

1. Add CPU/RSP/PI/SI read-dependency and DPC/VI event observations where needed
   to test the ownership/barrier design. Include JIT reads and presentation
   reads that currently access the managed array directly.
2. Add a versioned permissive-only native library boundary; reuse this journal
   for managed/native batch submission and explicit byte-order staging tests.
   Angrylion and `rdp-utils` remain test-only because of their separate license.
3. Measure submission, necessary transfers, waits and readback together.
   Avoid a full-memory flush for CPU ranges that no queued GPU work can touch,
   with conservative fallback whenever that cannot be proved.
4. Only then run a headless live game with CPU-read barriers and correct
   FULL_SYNC completion, followed by VI/presentation and savestate gates in
   the original plan. Keep the existing software fallback throughout.

Reproduction commands and the versioned binary format are in
[the probe README](../tools/N64GpuProbe/README.md#ordered-multi-frame-journals).
