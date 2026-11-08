# CPU decode-cache trials, November 8

Neither runtime candidate is promoted. The accepted 4096-entry PC-indexed
cache and COUNT rollover fix remain in the desktop launcher. These trials
use the dense Joybus observer in both reference and candidate and inspect
short-window floors as well as average throughput.

## Hypothesis and profiles

The sampled CPU fallback loop includes instruction fetch and cached decode
on every step. A separate intrusive profile of guest seconds 90–120 records:

| Variant | Decode hits | Decode misses | Miss fraction |
| --- | ---: | ---: | ---: |
| Accepted 4096 entries | 168024407 | 36573382 | 17.88% |
| 16384 entries | 194090436 | 13187920 | 6.36% |
| 4096 entries, opcode hash | 186416198 | 21320519 | 10.26% |

NOP is excluded from these counters: it retains its live predecessor check.
Native compilation is asynchronous, so the total number of fallback lookups
varies between runs. Compare miss fractions, not just absolute counts. All
three profile replays match the established six checkpoints/images and full
final state. Intrusive profile timings are not performance evidence.

The larger cache increases its storage from 32 to 128 KiB. The hashed trial
keeps 32 KiB and selects a slot by multiplying the full instruction word by
0x9e3779b1 and taking the upper 12 bits. Identical words share entries across
addresses. Every hit still checks the complete freshly fetched word. NOP's
PC-dependent behavior bypasses this cache; aliases, modified code and state
loads retain their live-word semantics.

## Measured results

| Guest seconds 95–120, ABBA | Wall throughput | Thread CPU throughput |
| --- | ---: | ---: |
| 16384 entries, first series | +5.838% | +4.585% |
| 16384 entries, repeat | -6.454% | -5.900% |
| Opcode hash | -2.684% | -1.475% |

Other host workloads were active, including compiler/linker processes and
shadPS4 runs. These series are not quiet-host measurements. The initial
apparent gain did not survive repetition; no stable gain or lower bound is
established. Fewer decoder misses alone do not establish faster execution.

The one-second audio-production minima, excluding windows spanning the
probe's own checkpoint captures, are:

| Series | Reference runs | Candidate runs |
| --- | --- | --- |
| 16384 first | 80.11%, 77.19% | 78.92%, 74.51% |
| 16384 repeat | 76.42%, 86.20% | 71.07%, 78.96% |
| Opcode hash | 74.66%, 72.05% | 73.92%, 68.90% |

These are instrumented replay measurements under the observed host load,
not physical playback or display pacing. They give no evidence that either
candidate improves the sustained real-time floor. Full-speed gameplay with
reliable audio remains unfinished.

All twelve completed timing runs match six checkpoints, six images, complete
final CPU/device/GPU state and RAM. Their 880 dense cycle/audio-duration
samples also match exactly across all runs. The larger cache passes the
1,000,000 decoder and 4,000,012 next-decoder comparisons. The hash variant
passes those plus 1474 explicitly observed cache evictions. Both also pass
1,050,624 RANDOM batching checks in the common decoder-test prefix. Broader
CPU/JIT, other-game, audio-playback and desktop promotion gates were not run
for these rejected runtime candidates.

## Retained work and reproduction

Conditional dispatch diagnostics now count decode hits/misses and emit the
cumulative dispatch profile at each five-second correctness checkpoint.
The benchmark refuses timing samples carrying that intrusive profile.
Normal desktop builds have no added counters or checkpoint work.

Decoder tests derive the collision stride from the real cache size and
add eviction checks that locate the published entry independently of the
slot-selection algorithm. `N64_PROBE_CPU_DECODE_ONLY=1` runs this focused
prefix with either `--check-cpu-blocks` or `--check-cpu-jit`.

Source experiments against `9407b106` are retained in:

- `experiments/n64-cpu-decode16-2026-11-08.patch`
- `experiments/n64-cpu-decodehash-2026-11-08.patch`

They include conditional diagnostic/test changes; apply each to an isolated
checkout of that baseline, not on top of the retained diagnostics twice.
The 16K patch predates the algorithm-independent eviction checks.

Local evidence under `.build-tmp/n64-mario-2026-11-08/` includes `decode*`
binaries, profile replay proofs, build/test logs, all three ABBA directories,
per-run `timing-summary.json` and combined `decode-floor-results.json`.
Do not repeat cache enlargement or this hash based only on miss counts.
Further optimization needs to reduce execution cost in the heavy CPU path,
and demonstrate an improved floor under comparable host conditions.

The retained diagnostic-only source passes a fresh build, the focused
decoder checks with the accepted 4096-slot policy, and another exact full-state
Mario replay (`decode-final-profile-run/`). An end-to-end benchmark harness
check supplies that actual checkpoint fixture through a fake process and
confirms dispatch profiling is rejected before any timing result is accepted.
Both archived patches pass application checks against their stated baseline.

A concrete next lead is the 849,481 rejected SPECIAL 0x19 (MULTU) entries
in the same dispatch profile. They leave the single-cycle block path. Measure
their execution cost before attempting multi-cycle batching; instruction
counts and cycle budgets must remain distinct, including COUNT/RANDOM,
interrupt boundaries, delay slots and history. Frequency alone is not a
measured cost or proof that this optimization will help.
