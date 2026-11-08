# Mario audio headroom, November 8

The user tested the accepted desktop build in a visible Linux window and
reported substantially better movement/playability, but insufficient margin
for music. This is useful interactive gameplay feedback; uninterrupted
audio and sustained full realtime remain unconfirmed.

## Diagnose the whole playback path

`tools/N64AudioPlaybackProbe` models 60 Hz N64Adapter polling and the real
AudioEngine with a null sink. The saved GPU scene and controller sequence
cover guest seconds 60–90; timings use exact Joybus checkpoints at 70/90.
The null sink excludes the real audio driver and Avalonia. Engine underrun
events are not a direct measurement of audible gaps.

The exploratory run found no submission-policy skips and no ring overflow,
but repeated empty-buffer events. The hypothesis that the UI's submission
hysteresis was dropping this sequence's music was not supported.

Disabling the adapter's active CPU-progress wait was tested in ABBA order:

| Run | Policy | Wall seconds for 19.7122401 audio seconds | Realtime | Sampled warm underruns |
| --- | --- | ---: | ---: | ---: |
| A1 | current 12 ms wait limit | 23.5343862 | 83.76% | 401 |
| B1 | no wait | 24.7335547 | 79.70% | 399 |
| B2 | no wait | 22.5604343 | 87.38% | 301 |
| A2 | current 12 ms wait limit | 23.9533576 | 82.29% | 383 |

All six guest checkpoint cycle counts, RAM hashes and generated audio counts
match. Mean throughput gain is only 0.410%, with adjacent pairs disagreeing;
the wait setting is unchanged. These adapter-chain numbers are not directly
comparable to earlier core-only throughput or a listening test. Other host
workloads were left running. Underrun deltas are sampled at wall-second
boundaries, unlike the exact guest timing window.

The first diagnostic harness version stopped before the final Joybus read;
that incomplete result was rejected. The corrected harness waits until the
second-90 callback has actually recorded its checkpoint. Source was then
promoted with ROM/state/output validation; the project builds successfully.
The measured runs used frozen accepted desktop/CPU-clock assemblies with the
same loop, before adding the argument guards and formatting.

## Scanout crop candidate, not delivered

The GPU frontend previously allocated/copied a VI crop on every poll, even
when selecting the identical immutable completed frame. The candidate caches
that internal crop by completed-frame identity, offset and dimensions while
still cloning the returned pixels, so callers cannot mutate emulator state.
Loading a state or detaching the renderer invalidates the derived cache.

59 native GPU oracle/integration checks pass, including new repeated-poll
ownership/allocation and restored-crop checks, existing VI offset/held-frame
checks, GPU/CPU hazards, DMA and compiled CPU loads. In the 4096-byte crop
fixture, 100 repeated polls allocate 412000 bytes: one owned output per poll.
The candidate builds for netstandard2.0. No new throughput or audible gain
has been established.

Before its timing series could start, even the reference binary failed
Vulkan device creation. NVIDIA reported 24161/24564 MiB occupied, with
unrelated Python/voice/media processes running. They were not stopped. A
later reading was 22795 MiB; GPU availability/load is not a stable timing
baseline yet. There is no completed scanout-candidate ABBA result.

The production core and launcher were restored/preserved. Candidate source
and its dedicated tests are archived in
`docs/experiments/n64-scanout-cache-2026-11-08.patch`; frozen binaries remain
local. After GPU resources are available, rerun the prepared adapter-chain
ABBA in a fresh output directory, then complete full-state Mario/cross-game
replays and exact desktop save/load validation before delivery.

## Evidence

Local directory: `.build-tmp/n64-mario-2026-11-08/`.

- `audio-abba-v2.log`, `audio-abba-v2-*.json[.guest.json]`: completed wait trial.
- `audio-probe-src/`, `audio-probe-timed/`: frozen diagnostic implementation.
- `scanout-cache/`, `scanout-cache-gpu-checker/`, `scanout-cache-gpu-check.log`.
- `scanout-cache-portable.log`: portable build.
- `audio-crop-abba-1-reference.log`: Vulkan failure before candidate timing.
- `audio-crop-abba.py`: prepared driver; use fresh output names on retry.
- `profile-heavy/` and `profile-heavy-proof.json`: separate short sampling
  pass within the heavy guest interval, with exact six checkpoints/images,
  audio and full final state versus the accepted core. CPU block dispatch
  remains about 14.34%, RSP ExecuteSlice 10.79%, RSP progress hashing 4.50%.
  These are exclusive sample shares, not guaranteed obtainable speedups.

ROMs, saves and generated binaries remain local. No original save was edited.
