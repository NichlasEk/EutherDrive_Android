# Mario 64 Linux: delivered progress, realtime still incomplete

The accepted Linux build contains four checked core changes: exact SP DMA
notification batching (`2b183619`), validated instruction-fetch loads
(`4924d15e`), Memory-owned RSP register-prefix hashing (`1838f669`), and
constant RSP history stores (`8f2a1871`). The individual reports describe
their correctness gates and cold/warm timing conditions. Do not add or
multiply their measured percentage gains.

## Final combined control

Same .NET 10.0.14 runtime, original GPU library, all 28 CPUs allowed, two GPU
workers, direct commands and GPU overlap. Both variants resume the exact
same GPU state at guest second 60 and measure seconds 70–90. The reference
combines the new benchmark harness with the original frozen MIPS/Core DLLs;
the candidate contains all four changes. Both are isolated artifact copies.
The original DLL lacks the new DMA, prefix and history implementations.

| Run | Wall seconds | Audio realtime |
| --- | ---: | ---: |
| Original A1 | 21.5061035 | 91.66% |
| Updated B1 | 21.2662765 | 92.69% |
| Updated B2 | 21.3706792 | 92.24% |
| Original A2 | 22.9628925 | 85.84% |

Combined throughput improves 4.30% in this control, both pairs positive.
Host variability remains material. Candidate heavy intervals are only
88.32–90.14% realtime. Separate accepted-build runs reached 98.65–99.12%
in the same overall window, still with slower heavy intervals. This is
not sustained full realtime, and no interactive-play claim is inferred.
All six checkpoints/images, input/audio, final RAM and complete serialized
CPU/device/GPU states match in the combined control.

The alternative VMADH path was retested under .NET 10 with inline history.
A1/B1/B2/A2 wall times were 19.8880477 / 21.3287133 / 20.7995391 /
19.9814069 seconds: -5.36% throughput, exact state, retained disabled.
DMA polling, forced thread placement and utilization hints also remain
unselected as documented in their separate reports.

## Local runnable artifact

Run from the repository:

```sh
sh .build-tmp/n64-mario-2026-11-07/play/run.sh
```

It launches `desktop-history/EutherDrive.UI.dll` with .NET 10.0.14 and the
verified native GPU library. There is no forced affinity or utilization hint.
`play/run-before-dma-fetch-prefix.sh` retains the earlier `desktop/` build
for rollback. The separate play ROM and its saves do not overwrite the user's
original container. Original save SHA-256 remains
`e0b139d1eb43ea3a36d331d927bdbfefb1567f09539c30a9927038e99cfe194c`.

`desktop-history-build.log` reports zero errors (existing warnings remain).
The exact desktop DLLs pass UI save/load, GPU resume, other-slot preservation,
empty-slot handling and failure preservation in `ui-history-proof.log`.
Metadata checks confirm the four accepted paths and no performance-probe,
RSP profiling or DMA-polling implementation in the delivered MIPS DLL.
This build includes pre-existing local UI changes and is not claimed to be
a pristine-HEAD artifact. No new physical or interactive-user play test was
performed in this pass.

`delivery-history-manifest.json` is the authoritative final file/hash manifest;
older manifests describe retained earlier artifacts. Timing evidence is in
`aggregate-abba/` and `vmadh10-abba/`; the exact orchestration is retained in
`run-final-comparisons.py`. All are under the local `.build-tmp/n64-mario-2026-11-07/`
directory and keep ROM/state data out of Git.

## Remaining work

The hard case remains guest seconds 70–80. The profile identifies CPU block
dispatch/interpreter fallback and RSP execution as major costs. Future work
must improve these paths and demonstrate >=100% audio/game throughput in
heavy intervals, then validate actual frontend gameplay. Keep the current
artifact as a measured rollback; do not trade guest work or clocks for a
higher displayed frame count.
