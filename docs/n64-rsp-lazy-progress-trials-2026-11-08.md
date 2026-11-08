# RSP progress trials and hardware counters, November 8

Neither deferred-progress candidate was promoted. The playable core and
launcher remain the previously verified version. Lower instruction counts
alone are not evidence of smooth music or sustained full realtime.

## Candidates and correctness

The first candidate defers the exact progress hash inside uninterrupted
compiled execution after establishing a known baseline. When only one
tracked GPR changes, fixed-input XOR and multiplication by the odd FNV prime
are bijections, so the hash must change. Stagnation can be updated exactly
without immediately materializing the new hash. Materialization precedes
interpreter fallback, slice return and state observation.

The second candidate also accepts a baseline that was just computed on
fresh task entry or by an ordinary instruction. It does **not** assume an
ordinary NOP recomputed a retained signature. A discarded intermediate draft
made that assumption; it was corrected during review before validation.
The new four-case stale-signature regression exercises this distinction.

Both validated candidates pass the 96 RSP programs, 1024 sliced progress
checks, 126 exits, 192 lifecycle deadlines, 32 code-change checks and the
Mega Man/RE2 full-state replays. The corrected candidate also passes the
four targeted stale-signature/dirty-register cases against an isolated
interpreter. See `n64-rsp-progress-baseline-2026-11-08.md`.

## Results

All complete Mario series match six checkpoints/images, audio/input hashes,
final RAM and full serialized CPU/device/GPU state. Runtime is 10.0.14,
GPU commands/overlap are enabled, and all 28 host CPUs remain available.
Other user workloads were neither stopped nor reconfigured.

| Variant/measurement | Wall throughput | Thread CPU throughput | Instructions/work | Cycles/work |
| --- | ---: | ---: | ---: | ---: |
| First, ordinary ABBA | +8.458% | +8.571% | not collected | not collected |
| First, independent ordinary repeat | -3.156% | -2.373% | not collected | not collected |
| First, hardware-counter diagnostic | -2.061% | -0.529% | +0.281% | -0.743% |
| Fresh-baseline variant, hardware-counter diagnostic | -0.621% | +1.686% | +0.420% | +1.490% |

Instructions/work and cycles/work express throughput (reference count /
candidate count - 1), not percentage reduction in counts. The corrected
candidate's adjacent cycle pairs disagree. The original large wall-time
result is not reproducible, and the small measured reduction in instruction
work does not establish useful playback headroom. Instrumented wall numbers
are diagnostic and do not replace the normal performance acceptance gate.

## Reproducible hardware counter method

`tools/N64Probe/native/thread-counters.c` is a Linux-only diagnostic helper.
It opens a current-thread perf event group for user-space CPU cycles and
retired instructions, excludes kernel/hypervisor work, and does not inherit
other threads. Both counters run together. The diagnostic probe rejects
multiplexing unless time_running == time_enabled.

Build the helper with:

```sh
cc -O2 -Wall -Wextra -Werror -fPIC -shared \
  tools/N64Probe/native/thread-counters.c -o PROBE/libthread-counters.so
```

The optional patch `docs/experiments/n64-thread-counters-probe-2026-11-08.patch`
adds collection only at Joybus checkpoints: start at guest second 70, read
at the five-second checkpoints, disable/read/close at 90. Subtract the
70-point counters from the 90-point counters. The usual checkpoint hashes
and image/state comparisons remain active. This helper is not linked into
ordinary probes or the desktop build. The host already allowed per-thread
user-space perf events; no privilege, governor or security setting changed.

Local frozen `counter-probe-src/` builds the same copied benchmark against
accepted Ryu64Core/MIPS assemblies. Candidate directories replace only
Ryu64.MIPS.dll; all other probe/dependency files remain identical. The exact
CPU thread is therefore counted without sampling-rate or thread-selection
ambiguity. CPU-cycle counts still respond to cache/scheduler interference;
instruction counts also reflect asynchronous JIT publication timing. These
are diagnostics, not perfect isolation from a busy host.

## Evidence and next boundary

Under `.build-tmp/n64-mario-2026-11-08/`:

- `lazy-progress-abba/` and `lazy-progress-repeat-abba/`.
- `lazy-counter-abba/` and `lazy-seeded-counter-abba/`, including
  `counter-summary.json`, checkpoint counters and full-state replay evidence.
- `lazy-progress-rsp-check.log`, `lazy-progress-seeded-rsp-check.log`, both
  cross-game proof JSON files, `lazy-baseline-check.log`.
- `counter-probe-src/`, `counter-probe-reference/`, `counter-probe-lazy/`,
  `counter-probe-lazy-seeded/`: frozen diagnostic artifacts.

Validated source candidates are archived as
`n64-rsp-lazy-progress-2026-11-08.patch` and
`n64-rsp-lazy-progress-seeded-2026-11-08.patch` in `docs/experiments/`.
No candidate desktop or audio improvement was delivered. The next productive
investigation should identify guest blocks/instructions dominating CPU JIT
fallback, rather than repeating hash deferral or the rejected scanout cache.
