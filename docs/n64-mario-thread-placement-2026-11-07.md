# Mario thread placement and resumable gameplay measurements

With the accepted DMA, fetch and RSP signature changes, a cold-boot ABBA
compared all emulator threads sharing cores 6/7/8/9 against the dominant
emulation thread fixed to core 8 and the other existing threads on 6/7/9.
Both used .NET 10.0.14, two GPU workers and the same native library. Placement
occurred after guest second 20, before the 70–90 timing window.

| Run | Wall seconds | Audio realtime |
| --- | ---: | ---: |
| Shared A1 | 21.9418765 | 89.84% |
| Separate B1 | 20.2019118 | 97.58% |
| Separate B2 | 20.1408747 | 97.87% |
| Shared A2 | 22.6456279 | 87.05% |

Mean throughput improved 10.52%, with both pairs positive. All 18 checkpoints,
images, audio/input, cycles, task counts and final RAM match. The heavier
70–80 section remains at 90.76–93.65%; this is not sustained full realtime.
This comparison is against four-core process affinity, not unrestricted
desktop scheduling. No global CPU governor or application default is changed.

The native thread names did not expose the managed `R4300` name. The first
name-based attempt stopped its own candidate before timing. The successful
probe instead sampled thread CPU time after startup, required one thread to
use at least half a CPU and twice the next thread's CPU time, and recorded
the chosen TID and actual affinities. In B1 the selected thread used 94 ticks,
versus 10 for the runner-up. IDs are rediscovered on every run.

`bench-sm64.py` now supports `--candidate-cpu-core 8
--candidate-helper-cores 6,7,9`, checks that placement precedes the timing
window and stays inside the process affinity, and records the selection.
Failed setup terminates only that benchmark child. It remains an opt-in
measurement feature, with host-specific CPU IDs.

## GPU state fixture and full final-state checks

`N64_PROBE_SM64_SAVE_STATE=1` exports a separate `core-state.bin` after a
completed Mario run. `bench-sm64.py --state PATH` resumes both variants from
that exact fixture and records its hash. The input script and checkpoints
continue using absolute guest cycles. Resumed runs additionally export and
compare complete final CPU/device/GPU state, beyond the cold-boot harness's
existing image/RAM/audio/input checks.

The local fixture was captured at guest second 60 with core state SHA-256
`b97983b53b46da8f9e88ea82b97fa2134c2c78493212a662851ebeeed52b525f`.
Resuming it reproduces all six 65–90 second checkpoints' cycles, RAM, Mario
action/position and task counts, all six images, and final RAM from the
uninterrupted cold boot. Four resumed runs also match complete final state:
`a245aeb3e15d5751bd9a6abb7515ce9b8d178e7b9d245f17983e1001764474f8`.
The original user's savestate container is not used or modified.

## Process-only utilization hint: rejected

A resumed ABBA kept the separate CPU/helper placement in both variants and
tested `uclampset -m 1024` only on the candidate process. Wall times were
20.1351606, 20.9857323, 20.9192124 and 21.4113123 seconds. Mean throughput
changed −0.86%, with opposite pair signs. The hint is not enabled. All six
checkpoints/images and complete final states match. No system-wide setting
was changed; the hint ended with each child process.

Artifacts under `.build-tmp/n64-mario-2026-11-07/`:
`thread-placement-v2-abba/`, `bench-thread-placement.py`, `state-probe/`,
`mario-state60/`, `state-continuation-proof.json`, `warm-hint-abba/`, and
`bench-warm-hint.py`. ROMs, GPU states and captures stay local.

## Unrestricted desktop-affinity control

A further resumed ABBA compared the actual unrestricted scheduling scope
(all 28 logical CPUs) with the fixed core-8/helper-6/7/9 placement. Wall times
were 21.1813867, 21.2560449, 21.4961992 and 21.7829897 seconds. The mean gain
is only +0.50%, and pair signs differ. All six checkpoints/images and complete
final states match. Artifact directory: `unrestricted-vs-pin-abba/`.

Therefore the earlier +10.52% does not establish an improvement over the
ordinary desktop launcher. Fixed thread placement remains a diagnostic
option; the delivered launcher keeps unrestricted scheduling. Further core
timings should avoid confusing a restrictive benchmark affinity with the
user's actual launch configuration.
