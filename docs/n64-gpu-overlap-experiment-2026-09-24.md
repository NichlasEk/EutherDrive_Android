# Early RDP submission on Linux

The live GPU now has an opt-in experiment, `EUTHERDRIVE_N64_GPU_OVERLAP=1`.
When the staged RDP stream reaches 256 KiB at a command boundary, the emulator
copies that immutable batch and submits it on a background worker while the
CPU/RSP thread prepares the next batch. Only one native submission can be in
flight. The existing `FULL_SYNC`, CPU read hazard and save path still wait for
the worker and perform the same readback before publishing a framebuffer or
raising DP. CPU writes retain their original byte snapshots and command order.
The default remains off because the benefit depends strongly on the scene.

The batch threshold can be varied for experiments with
`EUTHERDRIVE_N64_GPU_OVERLAP_BATCH_KIB=64..4096`; invalid values use 256 KiB.
In RE2 slot 1, 64 KiB caused 2,631 partial submissions and 256 KiB caused
500 over ten guest seconds; 1024 KiB caused none. The 256 KiB setting is the
tested starting point, not a universal optimum.

All timings below use Linux, the direct RDP command path, the same unprofiled
GPU library in both modes, two GPU workers, CPU affinity 8/9, neutral input,
and copied user states. The benchmark runs reference/candidate/candidate/
reference, comparing image/audio/input/guest-cycle/RAM checkpoints, final
RDRAM and serialized CPU state before accepting a timing. Raw output is under
`.build-tmp/n64-overlap-2026-09-24/`.

| Scene and guest window | Reference wall time | Overlap wall time | Whole-core throughput | Result |
| --- | ---: | ---: | ---: | --- |
| Mega Man 64 later slot 1, 5–10 s | 11.508 s | 8.597 s | +33.85% | Exact two checkpoints, frames and final state |
| RE2 slot 1, 5–15 s | 20.358 s | 20.222 s | +0.68% | Exact three checkpoints, frames and final state; too small to claim stable speedup |
| RE2 movie slot 2, 5–10 s | 6.069 s | 6.130 s | −0.98% | Exact two checkpoints, frames and final state |

The attempted Rampage comparison was a software-backend state and did not
exercise the GPU. Mario's older UI slot state has an incompatible CPU-state
version. Neither result is evidence for this GPU option. A single Mega Man
Khronos-validation run reached five guest seconds with zero Vulkan/RDP
validation errors. Separate RE2 and Mega Man GPU save/load checks resumed
with exact audio, input, frames, CPU/devices, RAM, hidden bits and TMEM.
The normal Linux GPU desktop build passed. The benchmark and validation
runs did not modify the ROMs or original savestates.

Early submission changes when host work begins, not guest-visible completion.
It does not solve the GPU framebuffer ownership contract needed for G3's
larger asynchronous readback. Before enabling it by default, compare a newer
GPU-native Mario state and a verified GPU Rampage state, check sustained
desktop audio/UI behavior, and repeat a broad ABBA series without host load.

## Follow-up: worker and buffer costs

A long-lived dedicated submit thread was tested against the existing
`Task.Run` partial-submit path in Mega Man's 5–10-second window. Its first
smoke exposed a job-handoff race; after correcting that, the run matched the
reference's images, audio, input, final RAM and CPU state. The reverse-order
ABBA averaged 8.620 s for the dedicated thread versus 8.490 s for
`Task.Run`, **1.51% lower throughput**. It was removed.

Reusing rented byte arrays while keeping `Task.Run` also failed to improve
whole-core time. Mega Man's ABBA averaged 8.562 s with pooling versus
8.558 s without (−0.05% throughput); RE2 slot 1 averaged 9.935 s versus
9.886 s (−0.50%). All benchmark checkpoints and final states matched. The
pooling variant was removed, leaving the earlier opt-in path unchanged.
Results are under `.build-tmp/n64-dedicated-submit-2026-09-24/` and
`.build-tmp/n64-buffer-pool-2026-09-24/`. Any future worker rewrite must
justify its scheduling cost with a whole-core test, not just fewer allocations.
