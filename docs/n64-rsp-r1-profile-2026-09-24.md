# RSP block profiling: RE2 and Mega Man gameplay

This pass follows [the realtime plan](n64-realtime-agent-plan.md), section R1.
It adds `-p:N64RspProfile=true` to diagnostic builds of `N64Probe`. The option
instruments block attempts, delegate execution, `EndBlock`, and progress checks;
normal builds contain none of its counters or timestamp calls. The raw logs and
copied benchmark outputs are under `.build-tmp/n64-rsp-r1-2026-09-24/`.

Both profiles used Linux, the same current GPU library, two GPU workers,
CPU affinity 8/9, neutral controller input, and copied core states. The run
ends at guest second 10. Timers cover seconds 0–10 and **include profiling
overhead**; they identify work, not uninstrumented gameplay speed. `delegateMs`
contains `EndBlock` and `UpdateBlockProgress`, so those columns must not be
added to it. `blockMs` contains dispatch and delegate work.

| Scene | Block attempts | Executed blocks | Block instructions | Lifecycle skips | Known non-blocks | Guard skips | Block / dispatch / delegate ms | End / progress ms |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| RE2 slot 1 | 53,049,268 | 24,697,033 | 212,188,273 | 12,524,184 | 15,755,794 | 72,257 | 13,314 / 3,625 / 8,396 | 1,070 / 1,080 |
| Mega Man later slot 1 | 49,007,886 | 23,587,478 | 202,094,580 | 11,026,171 | 14,316,370 | 77,867 | 11,636 / 3,141 / 7,293 | 957 / 1,060 |

The RE2 core-state SHA-256 is
`b12239503b4178a281b4954d0bbad50c77c0e78937f52dce9ce2045337e62461`;
Mega Man is
`5c5c9d812a74368d2f792d488b3679eb92143d0f94749df16c5badbff839dd93`.
The benchmark ROM and state files were read only. Both diagnostic runs reached
their target guest cycles with image, audio, RAM and input checkpoints.

Three isolated production candidates were compared against the frozen
1024-version CPU JIT build with the same GPU library. Each RE2 series used
reference/candidate/candidate/reference over guest seconds 5–10. The harness
compared two checkpoints, two image hashes, audio/input data, final RAM and
serialized CPU state before accepting a timing. Results:

| Candidate | Mean reference wall time | Mean candidate wall time | Throughput change | Decision |
| --- | ---: | ---: | ---: | --- |
| Skip the JIT call during pending lifecycle and skip the `NoBlock` delegate | 10.703 s | 11.078 s | −3.39% | Reverted |
| Skip only the `NoBlock` delegate | 10.691 s | 11.133 s | −3.97% | Reverted |
| Native 32-bit fetch for the block's first IMEM word | 10.619 s | 10.569 s | +0.47% | Reverted: opposite signs in the two pairs |

The many skipped attempts are not, by themselves, a speed opportunity:
moving their checks onto every loop iteration or adding a branch to every
valid block cost more than the avoided calls. The next RSP experiment should
measure work *inside* the executed delegate and the interpreter fallback
during lifecycle events, including the exact DMA/event boundaries. A bounded
region that does more safe work per return might help, but it needs an oracle
for mid-region code changes, progress detection and every slice boundary.
CPU block dispatch remains an independent candidate from section C1 of the
plan. This pass retains only the compile-time diagnostic profiler; it makes
no gameplay-speed claim or change to the normal RSP execution path.

The uninstrumented Release probe builds successfully. Its RSP differential
check against the frozen reference passed all 96 block programs and 768
sliced-progress cases. `git diff --check` also passed. No desktop speed or
ten-minute realtime result is claimed by this diagnostic pass.
