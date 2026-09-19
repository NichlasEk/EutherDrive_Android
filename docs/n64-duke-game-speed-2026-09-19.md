# Duke gameplay: precompute VI line periods

Baseline `492f7bb1`. Artifacts: `.build-tmp/duke-game-speed-2026-09-19/`.
The input is the user's gameplay slot extracted under
`.build-tmp/duke-corruption-2026-09-19/`, not the older menu benchmark.

A 15-second CPU sample attributes 85.86% of CPU-thread time outside RSP task
execution. The interpreter is still the main bottleneck. `Memory.Tick` runs
for every instruction and derives VI line cycles from a division by the
current VI line count.

Precompute the exact integer quotient for all 1,025 possible inputs (zero's
existing default plus the 10-bit VI_V_SYNC range + 1). The helper retains its
previous division fallback outside the hardware range. This is immutable
arithmetic data, not cached register state: writes and savestate loads cannot
leave it stale. The table uses 4,100 bytes. No emulated timing, frame skipping,
save format, or interrupt scheduling changes.

Validation uses the existing `--bench-cpu-state RAW_CPU_STATE ROM.z64` harness:
20 million fetched instructions, interrupt service, real ROM data, three warmup
runs and five measured runs per process, full serialized state hashing outside
the timed interval. Processes run sequentially pinned to CPU 6, in both orders.
This measures CPU replay, not displayed FPS; frontend overhead and CPU-thread
outer-loop shortcuts are excluded.

## Results

| Scene | Baseline process medians | Candidate process medians | Throughput gain |
| --- | --- | --- | ---: |
| User slot 1 | 1642.557 / 1584.870 ms | 1564.633 / 1558.992 ms | 3.3% |
| 35 seconds later | 1543.993 / 1561.047 ms | 1464.487 / 1444.980 ms | 6.7% |

Gains use the average of the two process medians. Host-time reductions are
3.2% and 6.3%, respectively. This is a modest CPU improvement, not a claim of
full-speed gameplay. The later scene ends on a branch/delay-slot boundary at
20,000,001 instructions in both builds.

Full-state hashes match across all runs in each scene:
- User slot: `7A84E5472C90541887A7AE33F6B25267F3950AD55C21BA1E7F567F6D6250CC52`
- Later scene: `822508CB053F27CC10DD71D698382B25AD49446FE0A72228BC4EAA12C019FFD7`

Validation passes: 6,845 render/interrupt cases, 81 VI cases, 73 low-VI
buffer-selection cases, and 270 idle-event cases (140 accepted batches).
The interrupt-serviced 2-million-instruction replay with and without idle
batching has identical state, including Count/Compare and device timers.

Input raw-state SHA-256 values:
- User slot: `9d6f494fe9cb3f4da601e4ab3b8e7257d5f6f139c61049245e8543004b7c81b6`
- Later scene: `ed91ffbf0b9267ee65eccd58a43071f5920ebfa64d149cb22d21fe08718d8f85`

```sh
dotnet tools/N64Probe/bin/Release/net8.0/N64Probe.dll --check-render
dotnet tools/N64Probe/bin/Release/net8.0/N64Probe.dll --check-video
dotnet tools/N64Probe/bin/Release/net8.0/N64Probe.dll --check-low-vi
dotnet tools/N64Probe/bin/Release/net8.0/N64Probe.dll --check-idle-events \
  .build-tmp/perfect-dark-vi-2026-09-19/cpu-state.bin
```

Linux UI Release builds successfully. Its MIPS core matches the tested
candidate: `8869db014724bf0977aeac274d360be35c3879a63d65a9e2789e898586eacb9b`.
Restart the running app to use the new build.
