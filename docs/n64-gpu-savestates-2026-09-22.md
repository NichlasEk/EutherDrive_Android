# N64 GPU savestates and Castlevania checkpoint

Work is paused at the user's request after committing and pushing this slice.
Resume from this document and the live checkout. The target remains Linux.

## What changed

The UI Save/Load commands were connected, but the live GPU core rejected saves
because it could not restore the renderer. New GPU saves now retain GPU
rendering on load. The slot panel also shows success and failure beside its
buttons. Existing software saves still load using the software renderer.

GPU saves stop/join the CPU, finish staged writes and commands, and reconcile
RDRAM/hidden bytes before writing the state. The native checkpoint contains
the processor and renderer registers, all eight tiles including bounds changed
by texture loads, constants, TMEM, and the primitive counter used by noise and
dithering. Loading constructs a fresh GPU context with saved memory and imports
these values. The live layer restores pending targets, completed frame copies,
and the held VI image without publishing unfinished drawing.

Core version 2 and memory version 8 identify GPU saves. Software saves retain
core version 1/memory version 7. A normal build rejects a GPU save with a message
to use the GPU launcher. Older native libraries are checked before replacing
the running renderer. Saving before the CPU has started is rejected, and a
failed file save preserves the previous file. No user save container was edited.

The native ABI remains 1 with optional checkpoint exports. Its checkpoint
revision is tied to paraLLEl-RDP `1cecd042b2619bc505c12bfdc713808386f2b54d`,
native resolution and little-endian Linux. The build prepares a local source
copy with two friend declarations; checkpoint implementation stays in this
repository. Future backend changes must audit/bump the checkpoint format.

## Validation

Artifacts: `.build-tmp/n64-gpu-savestates-2026-09-22/`.

- Six targeted save/resume cases compare complete memory/device state, hidden
  memory, TMEM and published images: before the first command, a queued frame
  with a partial CPU store, resident RGBA texture, resident CI/TLUT/LoadBlock,
  noise/dithering, and an incomplete XBUS command. Vulkan validation reports
  zero errors.
- Castlevania: ten guest seconds from boot, save, then five guest seconds of
  uninterrupted execution versus loading and repeating with neutral input.
  All five sampled images, audio, input, and the complete CPU/device/GPU state
  match. Final cycles: `1406841492`; audio frames: `213872`.
  CPU-state SHA-256:
  `C4F4F6BF6E5F9B6BEF4318FD23E60F8D89C3551DD5B66F994F20F2FFC2C803C0`.
- Mario uses the same comparison: final cycles `1410358022`, audio frames
  `158944`, all five images and complete state match. CPU-state SHA-256:
  `EE58C7E04DC2B326BBBEA11D26CBBAFA8FCCD786E0B7EE7C792C22E73ADA79AB`.
- Managed native-boundary checks pass: disposal races, finalization, recreation
  and memory roundtrip. Lifecycle checks cover save/load, stop/reset, old-save
  fallback and preserving an existing file when saving fails.
- The actual UI command/service/adapter check passes, including preserving
  slot 2 when saving slot 1 and keeping the container intact on save failure.
- Normal-core replays of 50 million instructions each match the existing full
  state hashes for Mario, Duke, Rampage, Gauntlet and Perfect Dark (the latter
  completes 50,000,001 instructions at its instruction-group boundary).

The game checks cover boot/intro scenes. They do not establish compatibility
with every game or claim a GPU speed increase. `GpuGameplayStateChecks` keeps
the uninterrupted reference on the original device; only the comparison branch
loads the checkpoint.

`tools/N64UiStateChecks` exercises the actual view-model commands, N64 adapter,
and slot service with a disposable ROM copy. It verifies GPU save/load,
frame-counter restoration, resume, preservation of the other slot, and visible
failure messages without touching user slots.

## Castlevania software optimization

The old slot-1 software scene exposed generic color-combiner overhead. A
source-pattern check now recognizes two-cycle texture × shade × primitive
modulation. It preserves both separate rounding steps and alpha, including
ONE=255. This is selected by RDP state, not a game name or ROM checksum.

Two interleaved ABBA series measured guest seconds 5–15 from the same old state,
with neutral input. Across eight runs, reference mean wall time was
`41.994931375 s`, candidate `35.403877375 s`: **18.62% more throughput**.
Individual series gained 14.68% and 22.85%, so host timing noise is visible.
All three checkpoints, images, audio/input hashes, RAM, and final complete CPU
state match in all eight runs. This is a software saved-scene result, not a
full-game or GPU speed claim.

Renderer checks pass: 160,960 combiner cases, 9,216 triangle modes, 4,096
rectangles, 432 flat-shade cases, sprite/alpha/depth cases, render/interrupt
checks, and completed-frame snapshots.

Artifacts: `.build-tmp/n64-castlevania-2026-09-22/`, including frozen reference
source/binaries, profile, both ABBA series, and `pooled-results.json`.
Base revision: `ee68388a45c4504982275fc7e55dc56dce77b235`.

The tested ROM was **Castlevania - Legacy of Darkness (Europe) (En,Fr,De)**,
SHA-256 `e78c172c1d554d1c94865969cec9b87a2149c92ec69ebc52b73a46648b5b2395`.
The original container was the older 2026-05-14 slot 1, SHA-256
`c1f0d6e54e57ff8e04ce55bbc9dc1e92f8a9157dc78ef2e9665cbbfe5f36fff7`.
Its extracted raw core payload is
`.build-tmp/n64-castlevania-2026-09-22/old-slot1/core-state.bin`.

## Try it and resume work

Restart through `scripts/run-n64-gpu-desktop.sh`; it rebuilds the desktop and
native checkpoint support. Start Castlevania and save a useful gameplay scene
in a UI slot. Loading that new save keeps **N64 GPU** active. Loading the old
software slot still selects software until ROM reset.

Next optimization pass: take a read-only copy of the user's new Castlevania
GPU slot, profile the actual gameplay scene, and compare complete state,
audio and images during interleaved measurements. Full GPU readback still
copies 8 MiB RDRAM, 4 MiB hidden memory and TMEM per synchronization. Range
readback/ownership work remains a separate future optimization; no such
change was mixed into savestate support.

Generated outputs, ROM copies, traces and user saves must remain untracked.
