# RSP progress bookkeeping experiment

The previous RSP profile found about 24 million executed blocks and roughly
one second of instrumented `UpdateBlockProgress` work per ten guest seconds in
both RE2 and Mega Man. The profile included instrumentation overhead, so this
was a hypothesis, not an expected gameplay gain. `perf` was unavailable on
this Linux host (`perf_event_paranoid=2`).

I froze a Release `N64Probe` reference before editing the RSP path. All runs
used the same copied savestates, native GPU library, cores 8/9, and interleaved
reference/candidate/candidate/reference order. The harness checked guest
cycles, audio and input hashes, RAM and CPU state, and rendered frames. Raw
outputs and frozen binaries are in `.build-tmp/n64-rsp-progress-2026-09-24/`.

| Implementation | Scene and interval | Mean reference / candidate wall time | Throughput | Decision |
| --- | --- | ---: | ---: | --- |
| Generated branch and direct add for unmodified tail | Mega Man, 5–10 s | 11.388 / 11.776 s | −3.30% | Removed; first reference was disturbed by a simultaneous differential check |
| Small `AdvanceBlockProgress` helper | Mega Man, 5–10 s | 11.761 / 11.400 s | +3.17% | Inconsistent pairs; remeasure |
| Small helper | RE2, 5–10 s | 10.229 / 10.342 s | −1.10% | Leave disabled |
| Small helper, same binary with flag toggled | Mega Man, 5–15 s | 22.483 / 22.802 s | −1.40% | Earlier positive result did not repeat |
| Early return inside existing `UpdateBlockProgress` | RE2, 5–10 s | 10.235 / 10.217 s | +0.18% | Removed from default path; noise-sized |
| Early return inside existing routine | Mega Man, 5–10 s | 11.454 / 11.421 s | +0.29% | Removed from default path; noise-sized |

Every completed ABBA comparison had identical checkpoints and frames. The
helper also passed the RSP differential oracle: 96 block programs and 768
sliced-progress cases. The first differential invocation used `N64Probe.dll`
as its reference argument and failed before testing; rerunning with
`Ryu64.MIPS.dll` passed.

The helper remains opt-in with `EUTHERDRIVE_N64_RSP_FAST_PROGRESS=1`. With the
flag unset, generated blocks call the original progress method. This retains
the slower but exact experiment for iteration without changing normal play.
The next useful step is to measure the instruction mix and sampled cost inside
executed blocks; the skipped progress calculation was already cheap and did
not account for the larger RSP delegate cost.
