# Gauntlet Legends: narrow triangle write barriers

The September 24 Gauntlet Legends (Europe) slot 1 core state is
`.build-tmp/n64-gauntlet-legends-2026-09-24/slot1-core.bin`, SHA-256
`6fb857e9ae0ff0c2e5784c1fdfe897b62110b7f21684c0a328f02bcff4b09ade`.
The original UI state was not changed. The previous Gauntlet pass and its
ROM/state provenance are in `n64-gauntlet-legends-performance-2026-09-24.md`.

Native profiling showed approximately 72,000 GPU idles before RDP opcode
0x0f triangles in five guest seconds, costing about 22 seconds of native
submit time. Pending CPU patches were concentrated around RDRAM 2.0–2.2 MiB,
while current color targets began around 1.5–1.8 MiB. The old conservative
bound assumed every target could cover 1,024 rows. It therefore called the
CPU patches overlapping even though none touched the active scissor-height
color or depth rectangle. The diagnostic profile is
`.build-tmp/n64-gauntlet-legends-2026-09-24/triangle-target-profile-5.log`.

The new, optional `ED_N64_GPU_NARROW_TRIANGLE_WRITES` flag uses the pinned
renderer’s triangle bound: `update_deduced_height()` clips to the scissor’s
12-bit quarter-pixel `yhi`, so the color/depth rectangle covers at most
`ceil(yhi / 4)` rows. A CPU patch can remain pending before opcode 0x0f only
when every pending span is disjoint from both rectangles and the command queue
since the last GPU idle contains only triangles under unchanged color, depth,
and scissor targets plus state commands that do not read RDRAM. Texture loads,
other draws, unknown commands, VI updates, and target/scissor changes close
that safe interval. The existing `FULL_SYNC`, readback, save, and texture-load
barriers remain in place. State restore starts with unknown scissor bounds and
therefore retains the old wait until a new SetScissor command arrives.

With the same Release probe, neutral input, CPU affinity 26/27, Vulkan GPU,
64 KiB overlap batches, and four interleaved runs, the results were:

| Guest window | Previous mean | Narrow mean | Throughput gain |
| --- | ---: | ---: | ---: |
| 5–10 s | 67.151 s | 40.900 s | +64.18% |
| 10–15 s | 60.220 s | 39.133 s | +53.89% |

Every run matched guest cycles, input and audio hashes, all frame hashes,
RDRAM checkpoints, and final serialized CPU state. The 10–15-second series
matched three checkpoints and three frames. Raw ABBA output is under
`.build-tmp/n64-gauntlet-legends-2026-09-25/abba-narrow-triangles*/`.
The initial profiled five-second candidate reduced triangle barriers from
roughly 72,000 to 20,000 and all write barriers from about 79,000 to 29,000.

A separate Khronos Vulkan validation replay reached five guest seconds with
zero errors and the same checkpoint. The managed ABI checks passed all 13
cases, GPU savestates passed six exact round trips, and GPU readback passed 77
cases with zero validation errors. Those logs are in
`.build-tmp/n64-gauntlet-legends-2026-09-25/`.

The optimization is enabled by default only for the verified Gauntlet Legends
(Europe) ROM signature. Other games retain the previous behavior.
`EUTHERDRIVE_N64_GPU_NARROW_TRIANGLE_WRITES=0` disables it for a graphics
bisect; `=1` forces it for experiments. This is a large verified improvement
in this replay, but the measured segment still runs well below 100% real time.
The final Linux UI build succeeded. A replay through that final build, with no
narrow-write environment override, matched the candidate checkpoint and
reported about 29,000 write barriers. An RE2 slot 1 replay matched its
reference checkpoint and retained the 256 KiB/default GPU path.
The final GPU core also saved Gauntlet slot 1 after two guest seconds, then
compared five more seconds of uninterrupted play with a load-and-resume path.
CPU/devices/RAM/hidden bits/TMEM, audio, controller reads, and all five frames
matched exactly (`gauntlet-save-resume.log`).
