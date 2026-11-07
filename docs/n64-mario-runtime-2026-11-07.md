# Mario 64: installed .NET 10 runtime comparison

The Linux GPU launcher now accepts `EUTHERDRIVE_DOTNET_RUNTIME`, passed to
`dotnet --fx-version`. The default target/runtime remains unchanged. On this
host, selecting installed .NET 10.0.14 improved Mario throughput versus
.NET 8.0.33. This does **not** establish sustained full-speed gameplay.

```sh
EUTHERDRIVE_DOTNET_RUNTIME=10.0.14 sh scripts/run-n64-gpu-desktop.sh /path/to/mario-rom
```

Omit the variable to return to the normal runtime. No runtime is downloaded,
and other applications' runtime selection is unaffected.

## Measurement

Base HEAD: `7771ae7f`; existing desktop/probe changes were preserved. Frozen
probes, initial diff/status, raw logs, sampled CPU profile and checkpoints:
`.build-tmp/n64-mario-2026-11-07/`. The two probes have identical assemblies;
only the copied runtimeconfig's framework version differs. Subsequent RSP
experiments are not part of this comparison.

Hardware: Xeon E5-2697 v3 and RTX 4090. Both variants use cores 6/7, two GPU
workers, synchronous shader compilation, direct command processing and GPU
overlap. GPU library: `.build-tmp/n64-native-profile-2026-09-24/libeuther_n64_gpu.so`.
Other applications remained running; host load visibly varied.

Four complete cold boots reach guest second 90 with deterministic Joybus
input. The timed window is seconds 70–90, containing 19.7122401126 seconds of
audio. The speed percentage is audio duration/wall time, **not displayed FPS**.

| Order | Runtime | Wall seconds | Realtime |
| --- | --- | ---: | ---: |
| A1 | 8.0.33 | 28.8753641 | 68.27% |
| B1 | 10.0.14 | 22.6675439 | 86.96% |
| B2 | 10.0.14 | 22.5764598 | 87.31% |
| A2 | 8.0.33 | 23.9326325 | 82.37% |

Mean throughput improves **16.72%**. Both adjacent comparisons favor .NET 10,
but the large reference variation prevents treating 16.72% as a stable general
gain. Candidate five-second intervals range from 80.3% to 94.3% realtime.
All four runs match all 18 sampled images and checkpoints, including RAM,
audio, input, cycle and task counts, Mario position/action, and final RAM.
This harness does not compare complete final CPU/device state.

`bench-sm64.py` now accepts `--reference-runtime` and `--candidate-runtime`,
records assembly/configuration and GPU-library hashes, runtime inventory and
selected settings, and reports both cycle-based speed and five-second audio
intervals. It retains the exact-state/frame rejection gate. This allows future
comparisons without editing copied runtimeconfig files.

## Validation

On .NET 10.0.14, the frozen reference passed:

- 2,267 CPU block/JIT cases, including 2,004 compiled cases, stale code,
  reset during compilation, event boundaries and self-modification.
- 1,050,624 RANDOM checks, one million decoder samples and 4,000,012
  continuation decoder checks.
- JIT cache lifecycle and 32 RSP slice/save/restore/producer-consumer checks.
- Audio FIFO, snapshots, state and resampling; 81 video and 96 TV timing checks;
  controller port checks.

The Release UI integration build passed with existing warnings. The actual
Save/Load commands passed on .NET 10 with Vulkan validation: GPU restoration,
resume, frame-counter restoration, preserving other slots, and preserving the
container after a deliberately failed save. It used a disposable ROM copy and
new slots, not the user's saved game. That build includes the separately
tested local RSP counter candidate, with the later loop experiment disabled.

The UI test had a stale exact status-string assertion: the current UI appends
`N64 GPU` to its successful load message. The assertion now accepts that suffix
while still checking the actual GPU backend and exact restored frame counter.

Shell syntax, Python CLI parsing and `git diff --check` passed. Desktop play
feel and a longer sustained-speed run remain to be checked. This commit adds
an explicit measured runtime choice; it does not change global desktop defaults.
