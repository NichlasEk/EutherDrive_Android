# COP1 branch trials, November 8

Neither candidate is promoted. The COUNT rollover fix (`fd3bb3fd`) remains
in the desktop launcher. These trials follow the cached-successor experiments
in `n64-cpu-successor-trials-2026-11-08.md`.

The intrusive profile records 1,174,493 rejected COP1 entry instructions in
the guest-second 90–120 replay. BC1F, BC1T, BC1FL and BC1TL were added to the
bounded interpreter and native compiler, retaining live CU1 checks, likely
annulment, code validation, event budgets, history and register state.
Condition-writing delay slots remain on the ordinary path, preserving its
existing condition-read order. Register caching is disabled for these blocks.

The second variant also retries native dispatch after a fallback COP1 branch.
This restores an opportunity to train/execute its successor that previously
occurred in the outer interpreter, without retrying after every instruction.

| Guest seconds 95–120, ABBA | Wall throughput | Thread CPU throughput |
| --- | ---: | ---: |
| COP1 branches | +0.799% | +0.459% |
| COP1 branches with successor retry | -1.806% | -2.009% |

The first result is too small to establish useful audio headroom. The second
regresses. All completed series match six guest checkpoints, six images and
the complete final CPU/device/GPU state and RAM. The final reference runs
reach 104.84–105.15% average audio production speed but only 89.75–90.51%
in the slowest five-second interval: averages do not establish sustained
real time. That interval needs approximately 10–11.5% more throughput just
to keep pace, before additional margin for presentation and audio playback.

The base candidate passes 400 targeted branch cases, including condition,
CU1, annulment, prefix writes, delay operands, deadlines and self branches;
2770 full block cases; 3302 full JIT cases (2695 compiled); 58 GPU checks;
portable compilation; and exact Mega Man 64 / Resident Evil 2 control replays.
The 400 targeted cases also pass with register caching requested. A subsequent
native-execution assertion passes with a byte-identical MIPS binary.

The successor refinement passes 416 targeted JIT cases, including 16
warm-successor cases, and its complete Mario ABBA. Broad suites were not
repeated for this rejected refinement. No early-window or audio-playback
result is claimed for either candidate; neither reached those promotion gates.

Source patches against `fd3bb3fd` (including the conditional diagnostic
instrumentation in Blocks.cs) are retained as:

- `experiments/n64-cpu-cop1-branches-2026-11-08.patch`
- `experiments/n64-cpu-cop1-resume-2026-11-08.patch`

Apply in an isolated checkout of that baseline; the diagnostic additions
must not be applied twice on a newer checkout. These are experiments, not
runtime defaults. Local frozen binaries, logs and manifests remain under
`.build-tmp/n64-mario-2026-11-08/`: `cop1-branch/`, `cop1-native-check/`,
`cop1-resume/`, `cop1-late-abba/`, `cop1-resume-abba/`, and `cop1-*.log`.

Only the conditional dispatch diagnostics are retained in production source.
Next investigation should reduce interpreter-loop overhead using the sampled
hot offsets, rather than assuming additional native dispatch improves speed.
