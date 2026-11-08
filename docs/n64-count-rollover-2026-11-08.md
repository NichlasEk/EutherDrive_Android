# Keep CPU batching active after COUNT rollover

Mario's CPU block interpreter and JIT stopped executing after roughly 95
emulated seconds in the previous long replay. The late slowdown was largely
a COUNT guard bug, not simply increased scene complexity.

The fix improves throughput by 54.737% over the previously delivered
`ee8f2b23` core in the measured 95–120 second window. Candidate runs reach
101.867% and 103.887% realtime on this host. Some five-second sections still
reach only about 90%; these averages do not establish uninterrupted realtime
or audible music quality.

## Cause and change

The internal half-rate clock is 64 bits; the guest-visible CP0 COUNT is its
low 32 bits after division by two. An ordinary multi-cycle instruction can
skip the `ffffffff` value that triggers the interpreter's existing reset.
For example, DMULTU advances internal `1fffffffc` by eight cycles to
`200000004`, leaving guest COUNT at 2. The old batch consistency check
compared `100000002` against 2 and rejected all subsequent batches. Its
unmasked end-of-batch wrap guard independently rejected them too.

At guest second 120 the captured internal clock is `29ea2df97`, while the
visible COUNT is `4f516fcb`. Both the accepted baseline and the sampling run
retain that state. Existing checkpoint logs show JIT instruction and
compilation totals frozen after second 95.

The block, multiply-routine and idle-branch paths now compare the visible
32-bit value and calculate the next boundary from the low 33 internal
clock bits. They still reject genuine mismatches and defer matching or
wrapping instructions to ordinary interpretation. Internal high bits,
serialization, timer behavior and ordinary reset semantics are preserved.

## Regression and correctness

The targeted test first reproduces the retained carry using ordinary DMULTU,
with COMPARE away from the rollover. The previous delivered MIPS assembly
fails the corrected test at case zero: accepted 0, expected 32. The fixed
assembly passes all 49 cases, including repeated/high carries, odd/even
clock phases, MFC0 reads, COMPARE matches, near-wrap rejection and genuine
low-word mismatches. Full serialized state and instruction history match
ordinary stepping.

Validation completed:

- CPU blocks: 2370 cases; JIT: 2902 cases (2469 compiled), including worker
  publication/reset checks and existing branch-likely regressions.
- Multiply batching: 295 cases. Idle events: 929 cases, 506 accepted, plus
  six differential two-million-instruction event replays.
- Decoder: one million samples; next-decode: 4,000,012 checks; RANDOM batching:
  1,050,624 cases in each full CPU suite.
- GPU: 58 exact checks with validation. Portable netstandard2.0 build.
- Mega Man 64 and Resident Evil 2: identical full state, frames, PCM audio
  and input checkpoints against the accepted reference.
- Mario 60–120 replay: all twelve checkpoints/images and final RAM/full
  CPU/device/GPU state identical. After second 95 the fixed run executes
  75,210,108 JIT instructions, versus zero additional JIT work before the fix.
- The uninstrumented desktop's exact assemblies pass UI GPU save/load/resume,
  other-slot preservation, empty-slot handling and injected save failure.

## Performance method and results

A common GPU core state at second 90 was produced by the previous delivered
core. Its six earlier checkpoints and full CPU state at 90 match the prior
replay. Both variants resume this state, warm through second 95 and stop
at the same controller read at 120. The final CPU state also matches the
original replay from second 60.

Runtime is .NET 10.0.14, fixed native GPU library, command overlap enabled,
single-threaded command handling, two GPU workers, unrestricted host CPU
affinity. Builds and other verification jobs finished before timing;
unrelated host workloads were unchanged. Order: reference/candidate/
candidate/reference. Each run matches six checkpoints/images and complete
final state, including generated PCM and scripted input hashes.

| Metric | Reference mean | Candidate mean | Throughput gain |
| --- | ---: | ---: | ---: |
| Wall seconds, guest 95–120 | 37.0322932 | 23.9324779 | +54.737% |
| Emulation-thread CPU seconds | 34.7631373 | 21.6439603 | +60.614% |

The initial 60–120 correctness replay ran alongside other tests and is not
used for performance claims. No intrusive profiling build supplied these
timings.

## Local evidence

Under `.build-tmp/n64-mario-2026-11-08/`:

- `count-regression-old.log`: old-core failure using the corrected regression.
- `count-wrap-target.log`, `count-full-{blocks,jit,multiply,idle}.log`.
- `count-smoke-proof.json`, `count-cross-game-proof.json`, `count-gpu-check.log`.
- `mario-state90/`, `make-state90.log`, `count-abba/` including manifest,
  checkpoints, images, final states, results and summary.
- `desktop-count-wrap/`, `ui-count-checks/`, `ui-count-proof.log`.

Private ROMs, save states and generated profile artifacts remain local.
Set `N64_PROBE_CPU_COUNT_ONLY=1` with `--check-cpu-blocks` or `--check-cpu-jit`
for the targeted regression. The full multiply and idle checks also cover
retained COUNT carries.

## Playback diagnostic

The adapter/audio probe now accepts a state at second 90 and optional final
guest second, allowing the same post-rollover interval to be measured. Both
variants use the same newly built probe and dependencies; the baseline
replaces only Ryu64.MIPS.dll with the previously delivered core.

| ABBA run | Realtime | Sampled underrun events | Engine dropped frames | Submission skips (whole run) |
| --- | ---: | ---: | ---: | ---: |
| Reference 1 | 65.817% | 1061 | 0 | 0 |
| Candidate 2 | 102.337% | 39 | 0 | 0 |
| Candidate 3 | 104.586% | 20 | 0 | 3662 |
| Reference 4 | 66.681% | 1048 | 0 | 0 |

Mean throughput gain is 56.159%; all six guest checkpoints agree. Counts
come from approximately one-second host samples around the measured window,
not exact checkpoint deltas. The existing submission hysteresis remains;
one candidate run skips 3662 submission frames when buffered output is
high. These are real AudioEngine buffer events with a null sink, not
measured speaker gaps. Some underruns remain and no listening result is
claimed. Evidence: `audio-count-abba-results.json`, its per-run host/guest
JSON files, logs, and `count-artifacts.json`.

## Desktop delivery

The existing `.build-tmp/n64-mario-2026-11-07/play/run.sh` selects the
uninstrumented `desktop-count-wrap/` build on .NET 10.0.14. The immediately
previous launcher is preserved as `run-before-count-wrap.sh`.
`delivery-count-wrap-manifest.json` records exact artifact hashes and the
UI validation result. Existing local UI edits are included, as in prior
deliveries; this is not a clean-HEAD UI build. The original user save remains
unchanged. This delivery is ready for interactive play/listening, with no
claim that every scene now maintains realtime.
