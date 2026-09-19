# N64 RSP checkpoint — 2026-09-19

Continues [the renderer checkpoint](n64-render-performance-2026-09-19.md).
The baseline is commit 772530f3, already pushed to origin/main.

## Retained change

Cached RSP block dispatch now keeps its selected delegate in a local and moves
compilation plus PlatformNotSupportedException handling to GetOrCompileBlock.
Ordinary cached dispatch no longer contains an exception region. First-word
validation, complete compiled-block guards, lifecycle checks, instruction budgets,
stagnation limits, invalidation and the interpreter fallback remain in place.

The replay harness now uses 20 warm-ups and 20 measured executions. Block
differential checks also alternate equivalent first instructions and explicitly
exercise accumulator operations with every element selector and register aliasing.

## Measurements and limits

Linux x64, logical CPU 6, separate processes, default runtime settings, identical
harnesses. Each process restores the same real task and checks complete CPU,
memory and private RSP state on every replay. Restoration/hashing are outside the
timer. Process order: reference/candidate/candidate/reference, then the reverse.

| Task | Reference process medians (ms) | Candidate process medians (ms) |
| --- | --- | --- |
| Graphics, 794,278 instructions | 56.871, 57.216, 65.328, 57.144 | 56.279, 57.318, 56.113, 58.900 |
| Audio, 18,824 instructions | 3.858, 3.863, 3.941, 3.948 | 3.807, 3.803, 3.912, 3.836 |

The median of process medians changes from 57.180 to 56.799 ms for graphics
(about 0.7%) and 3.902 to 3.822 ms for audio (about 2.1%). These are small,
indicative differences on an active workstation, with visible noise and an
outlying reference graphics run. They are not a demonstrated whole-game FPS
gain, and should not be added to previous CPU or renderer percentages.

All final task states match exactly:

- Graphics: BB124B1561F17BF41433FA6A9B0BF0DB97626A17CEB754844AD20C12FD296F75
- Audio: 466995C0B809ADE2DC1C0668F1CA0EB4AF85E449EB25B83F0F9B971BC4F7ED81

## Profiling and rejected trials

A 20-second CPU sample after 15 seconds of warm-up in the restored Mario scene
found these leaf costs: textured triangles 23.44%, CPU opcode interpreter 17.06%,
RSP vector interpreter 9.92%, CPU loop 7.61%, RSP block dispatch 5.33%, vector
accumulate 4.89%. Inclusive RSP task time includes rendering; do not count it
again as pure RSP execution.

Trials for predecoded VMUD multiplication and pinned accumulator buffers matched
state but did not show a stable benefit and were removed. Removing the duplicate
first-word read also matched state, but created 216 compiled graphics blocks
instead of 195 and slowed the graphics replay to 60.920–63.608 ms. It was removed;
the first-word check is retained. The fallback on a changed first word shifted
block starting points, so fewer checks did not mean less total work.

## Final verification

- 10,368 vector/transfer cases match the reference state digest.
- 2,048 vector-copy and 32,768 shuffled-copy cases pass.
- 96 expanded compiled-block cases match the reference, including changed
  first instructions, interior instructions, DMA lifecycle, wraparound and budgets.
- Block digest: 466A663F1EB6C0A84FDF6ED37B70E5932D4FE270A85522BF55C0FD7B0953A9E4.
- Probe and Linux UI Release builds pass. UI: 500 warnings, zero errors.
- The UI core DLL is byte-identical to the tested candidate core.

## Artifacts and reproduction

Final reference/candidate binaries and logs:
.build-tmp/n64-rsp-dispatch-2026-09-19/

Rejected experiments:
.build-tmp/n64-rsp-multiply-2026-09-19/
.build-tmp/n64-rsp-accumulate-2026-09-19/
.build-tmp/n64-rsp-dispatch2-2026-09-19/

The accumulator directory also contains the warmed profile and Mario scene log.
All artifact directories are untracked. Original captures remain under
.build-tmp/sm64-20260919/rsp-task-real/ and rsp-task-audio/.

Run one timing process at a time:

```sh
taskset -c 6 dotnet .build-tmp/n64-rsp-dispatch-2026-09-19/reference/N64Probe.dll --bench-rsp-task .build-tmp/sm64-20260919/rsp-task-real
taskset -c 6 dotnet .build-tmp/n64-rsp-dispatch-2026-09-19/candidate/N64Probe.dll --bench-rsp-task .build-tmp/sm64-20260919/rsp-task-real
dotnet tools/N64Probe/bin/Release/net8.0/N64Probe.dll --check-rsp .build-tmp/n64-rsp-dispatch-2026-09-19/reference/Ryu64.MIPS.dll
dotnet tools/N64Probe/bin/Release/net8.0/N64Probe.dll --check-rsp-blocks .build-tmp/n64-rsp-dispatch-2026-09-19/reference/Ryu64.MIPS.dll
```

Repeat with rsp-task-audio and reverse process order. Next optimization should
target a substantial measured cost, especially textured triangle rasterization
or vector operation bodies, with exact-state replays retained. Android and
playable whole-game speed remain unverified.

Follow-up: [rasterization checkpoint](n64-raster-performance-2026-09-19.md).
