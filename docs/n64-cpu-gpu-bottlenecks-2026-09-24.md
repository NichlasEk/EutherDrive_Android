# CPU dispatch and GPU submission: measured limits

This Linux pass follows C1/G1 of [the realtime plan](n64-realtime-agent-plan.md).
The diagnostic switches `-p:N64CpuDispatchProfile=true` and
`-p:N64GpuBatchProfile=true`, plus the native CMake option
`-DN64_GPU_PROFILE=ON`, add counters and timestamps only to explicitly
instrumented builds. Normal builds do not contain those measurements.
Artifacts are under `.build-tmp/n64-cpu-c1-2026-09-24/` and
`.build-tmp/n64-gpu-g1-2026-09-24/` and
`.build-tmp/n64-native-profile-2026-09-24/`; the copied RE2 slot 1 core has SHA-256
`b12239503b4178a281b4954d0bbad50c77c0e78937f52dce9ce2045337e62461`.

## Where RE2 spends work

Over ten guest seconds, the instrumented CPU dispatch ran 24.36 million outer
attempts. It accepted 8.93 million and executed 96.04 million instructions:
40.36 million via compiled JIT blocks and 55.68 million via the bounded
fallback. The dispatch timer was 9.31 seconds **including its children**;
JIT calls were 1.75 seconds and fallback instruction work 2.79 seconds.
The timer counts are perturbed by profiling and cannot be added to GPU timers
or treated as uninstrumented gameplay time.

Of the early rejects, 11.62 million started with an unsupported block opcode.
The largest SPECIAL functions were `MULT` (2.71 million), `DMULT` (2.01
million), `DADD` (1.16 million), `ADD` (0.55 million), and `DIV` (0.45
million). `MULT`/`DMULT` use 5/8 cycles and `ADD`/`DADD` can trap, so merely
adding them to the one-cycle block list would change guest timing or exception
behavior. The other frequent unsupported groups include unaligned load/store
instructions. About 2.72 million fallback continuations had an already ready
JIT cache entry at their next PC.

The separate GPU batch profile saw 1,208,364 commands and 1,004
synchronizations over the same guest duration. `Command` used 576 ms including
234 ms in its nested `Writes` calls. `Synchronize` used 5,562 ms, comprising
4,519 ms inside native `Submit`, 1,026 ms inside `ReadbackLive`, and about
15 ms in managed readback-completion bookkeeping. These are **inclusive
diagnostic timings**. A perfect rewrite of managed command serialization has
less than roughly 0.4 seconds of headroom in this ten-guest-second scene;
native submission is the larger target.

The native profile separated that submission: about 4.61 seconds in `Submit`,
including 2.78 seconds waiting at 989 `FULL_SYNC` barriers, 0.40 seconds at
227 TLUT-load barriers, and 0.76 seconds in `enqueue_command`. Batch
validation took 26 ms and framebuffer/texture tracking 29 ms. The barrier's
`begin_read_rdram` portion was below 1 ms in total, so the cost is GPU work
finishing at `idle`, not copying the CPU patches. With 1,004 managed syncs,
moving `FULL_SYNC` to the end of the same submit would normally just move the
wait. Native readback spent 37 ms waiting for its timeline, 252 ms copying
dirty RDRAM, 681 ms reading all hidden bits, and 81 ms starting frame
contexts. These are separate instrumented runs, so treat them as component
estimates, not additive whole-core timing.

## Retained improvement: partial hidden-bit readback

Live readback already marks all RDRAM pages that a draw or unknown command
*may* have written. Each 4 KiB RDRAM page maps to 2 KiB of RDP hidden bits.
After the mandatory first full readback, native readback now copies hidden
bits only for those marked pages. Unknown commands still mark every page;
full readback and save/restore still copy all hidden bits. The reported
`ReadbackBytes` now counts actual copied RAM and hidden bytes.

In the instrumented RE2 slot 1 run, hidden-bit readback fell from 681 to
90 ms per ten guest seconds. An uninstrumented 5–15 second ABBA run measured
0.91% whole-core throughput gain with three identical checkpoints and three
identical frames. RE2 slot 2's 5–10 second ABBA measured 1.36% with two
identical checkpoints and frames. These modest gains are close to host timing
noise; they do not make RE2 real-time. The GPU readback suite passed 77
full-memory/hidden-bit/TMEM comparisons and zero validation errors; the ABI
check and the regular Linux desktop build passed. Changing the GPU worker
count from two to four produced only 0.45% in a separate exact four-run test,
so the default remains two.

## Rejected exact candidates

All comparisons used the same GPU library, copied states, neutral input at
Joybus reads, CPU affinity 8/9 and two GPU workers. `bench-state.py` accepted
only identical image/audio/input/cycle/task checkpoints, final RAM and
serialized CPU state. No user ROM or original savestate was modified.

| Candidate | Measured whole-core result | Decision |
| --- | --- | --- |
| Treat interpreter-stubbed `CACHE` as a block no-op | RE2's 5–15 guest-second ABBA: +0.11%; Mega Man's 5–10 ABBA: −0.42%. Shorter RE2 series varied with host load. | Reverted: no stable speedup |
| Retry JIT after every fallback instruction with an already cached successor | RE2 5–15 ABBA: −6.49% | Reverted |
| Retry JIT only at fallback branch successors | RE2 5–15 ABBA: −0.26% | Reverted |

The `CACHE` candidate passed 1,737 bounded-block and 2,269 JIT cases,
including exact state, instruction history, self-modifying code and event
boundaries. Those passes establish correctness for that candidate, not a
reason to keep an unmeasured speed claim. One first RE2 timing series had a
25-second reference outlier while unrelated large compiles ran on the host;
its apparent +67.7% gain is invalid and excluded above.

The other retained changes are opt-in diagnostic instrumentation. Normal CPU
and RSP execution logic remains as at the starting checkpoint. The largest
remaining graphics cost is waiting for completed GPU work at frequent
`FULL_SYNC` submissions; further gains there require a dependency-preserving
pipeline rather than a simple barrier deletion.

## Follow-up: RDP command processing

The pinned paraLLEl-RDP processor has a supported
`PARALLEL_RDP_SINGLE_THREADED_COMMAND=1` mode. In RE2 slot 1 it removes the
command-ring handoff without changing the renderer or its GPU workers. Two
interleaved, reverse-order 5–15 guest-second comparisons measured **+4.83%**
and **+3.21%** whole-core throughput. Both had identical three checkpoints,
three frames, final RAM and serialized CPU state. Slot 2's 5–10 second
comparison measured +2.39% with identical checkpoints and frames, but its
reference runs varied more. Rampage's recovered state had exact frames,
audio, RAM and CPU state in both modes; its scene was already near the
real-time limiter, so its wall time is not a useful speed result. With direct
processing, the native readback suite again passed 77 cases, plus ABI and
live-texture checks.

The Linux desktop launcher now defaults to direct command processing. Set
`PARALLEL_RDP_SINGLE_THREADED_COMMAND=0` when comparing the prior command-ring
path. The main remaining RE2 cost is still GPU completion at `FULL_SYNC`,
which this setting does not eliminate.

The follow-up also tested CPU-block admission for `LWL/LWR/SWL/SWR` and direct
dispatch for `MULT/DMULT`. Both maintained exact state in their targeted
checks. The four-opcode block candidate measured +2.28% in one RE2 slot 1
ABBA but −0.35% when the order was reversed, and −2.22% in slot 2. Load-only,
store-only, and direct-store variants gave −0.61%, −0.87%, and +0.05% in slot
1. Direct multiply dispatch gave +1.72% and +0.20% in slot 1's two orders,
but its first form cost 4.08% in slot 2. Restricting it to the SPECIAL switch
removed that regression but then measured −1.80% in slot 1. All CPU variants
were reverted. The comparison artifacts are under
`.build-tmp/n64-unaligned-cpu-2026-09-24/` and
`.build-tmp/n64-direct-multiply-2026-09-24/`.

## Direct-command FULL_SYNC follow-up

Reprofiling the same RE2 slot 1 for ten guest seconds with direct command
processing left 989 `FULL_SYNC` barriers and about 2.81 seconds waiting at
them. Native command enqueue fell from about 0.77 to 0.33 seconds, while
TLUT barriers still used about 0.39 seconds. Thus the direct mode removes
command-ring overhead but does not address GPU completion. The new diagnostic
run retained identical 5- and 10-second image, audio, input, cycle and RAM
checkpoints, as well as identical final RAM and CPU state. Its logs are under
`.build-tmp/n64-native-profile-2026-09-24/profile-re2-direct*`.

Two isolated renderer settings did not help: allowing asynchronous shader
compilation was within the timing noise (5–10 guest seconds: 10.48 seconds
versus 10.21 seconds for the direct-mode reference); forcing the ubershader
made the same window 10.42 seconds versus 10.21 seconds, and increased the
`FULL_SYNC` idle sum from about 2.81 to 5.76 seconds over the full ten-guest-
second run. The profiler's total time includes initialization and varies with
shader/cache state, so neither setting is retained. Both produced identical
checkpoints and final state in this sample, but neither had a repeatable
whole-core ABBA speedup.

Disabling the renderer's small-integer shader path initially looked slightly
faster in one run, but the same-library, direct-mode 5–10 guest-second ABBA
gave 10.334 seconds for wide types versus 10.156 seconds for the default:
**−1.72% throughput**. Both checkpoints, frames, audio/input and final
RAM/CPU state matched. The default remains `PARALLEL_RDP_SMALL_TYPES=1`.
The reusable benchmark harness now accepts per-mode `--reference-env` and
`--candidate-env` overrides; the four-run raw results are under
`.build-tmp/n64-native-profile-2026-09-24/abba-small-types/`.
A single one-worker smoke run matched the default's image and state but did
not separate from timing noise; the two-worker default remains unchanged.

An extra profile-only counter classifies each `FULL_SYNC` wait by whether all
pending CPU patches are outside the conservative color/depth framebuffer
ranges. In this scene all 989 waits, covering 1,134,562 patches and about
2.78 seconds of idle time, met that narrow test. This is **only an upper
bound** for safe overlap: it does not prove that an earlier texture upload or
unknown GPU command did not read a patch, nor may it delay the guest-visible
DP interrupt or completed framebuffer. The result justifies investigating
region ownership and timeline generations as G3 describes; it does not
justify removing the barrier or claiming a speed gain. The diagnostic log is
`.build-tmp/n64-native-profile-2026-09-24/profile-re2-disjoint.log`.
