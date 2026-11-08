# Additional Mario Linux trials, November 8

The accepted core remains the November 7 DMA/fetch/prefix/inline-history build.
Five further trials did not establish useful performance gains and were
restored. All use .NET 10.0.14, the same saved GPU state at guest second 60,
all 28 CPUs allowed, direct GPU commands, overlap and two GPU workers.
ABBA measures guest seconds 70–90 (19.7122401 audio seconds). Every completed
trial matches six checkpoints/images, audio/input, final RAM and complete
serialized CPU/device/GPU state. Other desktop workloads remained active;
none were stopped or reconfigured to improve the results.

| Trial | Mean reference wall seconds | Mean candidate wall seconds | Throughput |
| --- | ---: | ---: | ---: |
| Fold two RSP hash multiplications into the existing prefix | 21.7893700 | 21.6286613 | +0.743% |
| Inline RSP progress helpers | 22.1960003 | 22.0114116 | +0.839% |
| Existing CPU register cache under .NET 10 | 22.7577671 | 22.7610636 | -0.014% |
| Validate RSP block interiors once per uninterrupted slice period | 22.5561781 | 22.4133908 | +0.637% |
| Also reuse the validated first instruction | 21.4343204 | 21.6315664 | -0.912% |

The sub-one-percent positive differences do not justify new production
complexity. The final slice-entry trial loses both adjacent comparison pairs.
The code-cache candidates pass the full RSP suite and the 32 new interior
edit/within-slice CP0 IMEM rewrite/queued DMA/epoch-overflow checkpoints.
All source candidates also match complete Mega Man 64 and RE2 GPU states;
the register-mode experiment was a Mario timing rerun of existing code.
The fold candidate additionally builds for netstandard2.0.

The four source experiments are archived in `docs/experiments/n64-{fold,
inline-progress,slice-code,slice-entry}-2026-11-08.patch`. These contain only
core changes, so they apply independently of the new committed regression
checks. None are enabled by the playable launcher.

## Fresh accepted-core profile

A separate sampling pass at guest seconds 70–90 matches the same six Mario
checkpoints/images and complete final state. It is diagnostic, not a timing
sample. Managed samples grouped by method names are approximately 29.80%
R4300 CPU, 42.70% RSP/interpreter/dynamic lambdas, and 22.16% other managed
work. Dynamic-lambda attribution assumes the RSP-generated lambda naming.
The largest individual methods are TryAdvanceCpuBlock (14.84%), RSP
ExecuteSlice (10.66%), ComputeProgressSignature (4.59%), CPU fallback
instruction execution (3.55%) and TryRunCpuJit (3.23%). Sample shares are not
independently achievable speedups.

The next bounded trial is a larger CPU interpreter fallback batch, still
restricted by the existing quiet-device/event budget. No result for that
trial is claimed here yet.

Local evidence: `.build-tmp/n64-mario-2026-11-08/`, the five ABBA directories,
`trial-options.json`, `profile-current/`, `profile-current-proof.json`, the
RSP logs and per-candidate cross-game proof JSON files. Private ROMs and
states remain local. The established `../n64-mario-2026-11-07/play/run.sh`
launcher remains the delivery/rollback point until another change qualifies.
