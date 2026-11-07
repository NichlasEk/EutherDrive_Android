# Mario follow-up: RSP trials and safe GPU play setup

The [runtime improvement](n64-mario-runtime-2026-11-07.md) is retained. None
of this pass's RSP production changes demonstrated a repeatable gameplay win;
`RspInterpreter.cs` and `RspBlockJit.cs` have been restored to their starting
contents. No new RSP flag is active in the delivered desktop.

## Interleaved comparison on .NET 10.0.14

The same original-USA Mario cold boot, controller-read input, guest seconds
70–90, GPU library, cores 6/7 and two GPU workers were used throughout.
All six completed runs match 18 sampled frames/checkpoints and final RAM,
including audio, input, task counts and player position/action.

| Order | Variant | Wall seconds |
| --- | --- | ---: |
| A1 | Reference | 22.2988086 |
| B1 | Local slice counter | 22.3017141 |
| C1 | Counter plus bounded short loops | 22.1672685 |
| C2 | Counter plus bounded short loops | 22.7982033 |
| B2 | Local slice counter | 22.2522455 |
| A2 | Reference | 22.8315020 |

The local counter's mean gain is 1.29%, but its first comparison is flat.
The combined short-loop candidate gains 0.37% over the reference and loses
0.92% against the counter-only candidate. These are not convincing wins.

The counter trial only moved `ExecuteSlice`'s out-parameter updates to its
normal exits. It preserved BREAK's excluded instruction, watchdog counts,
budgets and final state. The short-loop trial repeated an admitted direct
backedge inside one compiled delegate, capped at 256 instructions per call.
Each original block still published its complete progress/history state.

## Wider loop trial

A second loop candidate kept ordinary blocks at 16 instructions, but admitted
straight-line loops up to 64 instructions when they ended in a direct branch
back to the entry. CP0, pending DMA/events and IMEM writes remain excluded.
All entry code bytes are validated; reentry after returning to the dispatcher
validates them again. The in-delegate backedge remains bounded by the original
slice/watchdog budgets and the 256-instruction cap. No recursive delegates or
game-specific addresses are involved. The local-counter change was removed.

An initial complete run took 21.6204219 seconds in the moving window, or
91.17% realtime, with all checkpoints/frames/RAM identical. A fresh controlled
pair then took 21.8195219 seconds for the reference and 22.8754814 seconds
for the candidate: **4.62% lower throughput**. The series was stopped during
its third run once the apparent benefit failed to repeat. That unfinished
run is not a timing or correctness sample. The candidate is not retained.

The source-only experiment is archived in
[the bounded-loop patch](experiments/n64-rsp-bounded-loops-2026-11-07.patch).
It is not a recommended player setting. Raw runs, frozen binaries, initial
diff/status, manifests, copied source and logs remain under
`.build-tmp/n64-mario-2026-11-07/`.

## Retained regression coverage

`--check-rsp-blocks REFERENCE_CORE_DLL` adds 126 complete RSP/memory/device
comparisons for zero/tiny budgets, BREAK, unsupported instructions, absolute
caps, repeated-PC and progress watchdog exits. Existing coverage remains 96
block programs and 768 CPU-write/DMA/slice checkpoints.

`N64_PROBE_LONG_RSP_LOOPS=1` selects a separate 4096-instruction test cap and
checks 240 full state checkpoints for 3/17/32/64-instruction loops, budget
boundaries, branch-delay resumption and interior code changes between calls.
The ordinary suite keeps its original 61-instruction cap. The experimental
compiler passed 128 direct dispatch/guard cases; the restored production core
uses 32 cases checking its original single-block behavior. Both modes reject
modified interior code before executing the unchanged first instruction.

## Current saves and delivery

Read-only inventory of the user's existing Mario container found three valid,
occupied slots, all **core format 1: software rendering**. Loading them restores
software rendering even after a GPU-enabled boot. Changing a format marker
cannot supply the missing native RDP state. Original slots remain untouched.

The local play copy is `.build-tmp/n64-mario-2026-11-07/play/Mario64-GPU.n64`.
Its cartridge bytes match the original. It has a separate ROM-adjacent save
container, so new GPU slots cannot overwrite the three original slots.
`play/run.sh` starts the frozen Release desktop with .NET 10.0.14 and the
measured GPU settings/library. Its working directory and copied settings are
isolated from the normal desktop settings. No ROM, savestate or binary is
included in this commit.

Separate ten-guest-second Mega Man 64 and Resident Evil 2 comparisons between
.NET 8.0.33 and .NET 10.0.14 matched both checkpoints, frames, audio/input,
final RAM and complete serialized CPU/device/GPU state. These are correctness
checks, not new speed claims. GPU save/load via the actual UI commands also
passed; see the runtime report. The final Release desktop also started Mario
under .NET 10 in an isolated Xvfb display and produced the startup image using
the RTX 4090 N64 backend. Xvfb's frontend used bitmap fallback, so this is an
application-startup check, not a physical-display pacing measurement. The
temporary desktop was stopped and the copied play settings restored afterward.
The original three-slot container's hash was rechecked unchanged; the frozen
desktop/native library/ROM-copy/launcher hashes are in `delivery-manifest.json`.
Human controller/play-feel verification and
sustained full-speed gameplay remain outstanding.
