# N64 Linux performance handoff — 2026-09-24

## Current checkpoint

Work is paused after profiling and two RSP optimization experiments. The
checkout is `/home/nichlas/EutherDrive_Android`, branch `main`, HEAD
`90945e6e` at handoff. The working tree contains many earlier uncommitted
changes and untracked `.build-tmp/` measurements. **Inspect the live tree
before editing; do not reset, clean, or overwrite these artifacts.** Nothing
in this pass was committed or pushed. The target is the Linux emulator/JIT,
not an Android build.

There is **no verified new default-on gameplay speedup** from these RSP passes.
Two exact-state experiments remain available, both off by default:

- `EUTHERDRIVE_N64_RSP_FAST_PROGRESS=1` selects an alternative RSP block
  progress helper in `RspBlockJit.cs`. Longer Mega Man replay was −1.40%; RE2
  was −1.10%. See [progress experiment](n64-rsp-progress-experiment-2026-09-24.md).
- `EUTHERDRIVE_N64_RSP_FAST_VMADH=1` selects a dedicated SIMD `VMADH` method
  when compiling RSP blocks. Same-binary off/on ABBA was −0.77% in RE2 and
  −4.92% in Mega Man. See [instruction mix and VMADH experiment](n64-rsp-instruction-mix-2026-09-24.md).

Both flags are for further investigation, not recommended player settings.
The pre-existing `EUTHERDRIVE_N64_GPU_OVERLAP=1` experiment is independent;
it too remains off by default. See
[GPU overlap experiment](n64-gpu-overlap-experiment-2026-09-24.md).

## What the RSP measurements established

The [initial RSP profile](n64-rsp-r1-profile-2026-09-24.md) recorded roughly
24 million executed RSP blocks and 200 million block instructions per ten
guest seconds in both RE2 and Mega Man. A diagnostic-only instruction-mix
counter then found `VMADH` executed 14.2 million times in Mega Man and 16.1
million times in RE2. `VMADN` was next at about 12 million in both. Short
vector-memory operations are also frequent: SSV (store, subop 1) occurred
9.25/10.48 million times, and SDV (store, subop 3) 7.64/8.43 million times.
The instruction counter is compiled only with `-p:N64RspProfile=true`; its
timings are perturbed and are not gameplay-speed measurements.

The simple progress shortcut failed to improve whole-game time. A generic
`VMADH` carry simplification passed exact state but produced inconsistent
whole-game timings: RE2 +1.14%, Mega Man +6.99% in one longer run, then +0.23%
in a repeat. The separate opt-in `VMADH` method was slower in both same-binary
ABBA runs. All completed ABBA series matched CPU/RAM, audio, input, and frame
checkpoints. RSP differential checks passed 359,848 SIMD cases, 96 compiled
block programs, and 768 sliced-progress cases.

Whole-game timing was noisy. During part of this pass another emulator process
was using the host CPU/GPU; do not treat any single positive percentage as a
stable win. A fresh Mega Man graphics-task capture with 8,088 RSP instructions
is in `.build-tmp/n64-rsp-mix-2026-09-24/megaman-capture-long/`. The existing
isolated task harness cannot replay its GPU savestate exactly: a temporary
GPU attachment let it load, but full serialized CPU/memory state differed at
byte 5. That harness change was removed; its exact-state check was not relaxed.

## Resume here

1. Check `git status --short` and the linked reports. Preserve `.build-tmp/`
   and the user's savestates. The relevant sources are `RspBlockJit.cs`,
   `RspVectorSimd.cs`, and `RspInterpreter.cs` under `Ryu64/Ryu64.MIPS/`.
2. Pick one general RSP bottleneck with a defensible oracle. The best next
   candidate is the frequent SSV/SDV path in `TransferVectorBytes`: inspect
   actual alignment, descriptor-overlap, and fallback rates before changing
   transfers. Alternatively build a fixed-work RSP replay that can compare
   GPU-backed task captures exactly, including saved GPU state.
3. Freeze a Release reference **before** editing. Change one path at a time.
   Use the existing Linux differential checks and interleaved ABBA harness;
   compare at least RE2 and Mega Man with exact state, audio, and frames.
   Reject or leave opt-in any candidate whose gain changes sign across pairs.
4. Once a candidate is stable, do a longer gameplay interval and a normal
   desktop playability check. Probe percentages alone do not establish 100%
   realtime play.

Useful unchanged inputs:

- Mega Man ROM: `/home/nichlas/roms/N64/Mega Man 64 (USA).z64`; state:
  `.build-tmp/n64-megaman-later-2026-09-23/slot1/core-state.bin` (SHA-256
  `5c5c9d812a74368d2f792d488b3679eb92143d0f94749df16c5badbff839dd93`).
- RE2 ROM: `/home/nichlas/roms/N64/Resident Evil 2 (Europe) (En,Fr).z64`;
  state: `.build-tmp/n64-re2-speed-2026-09-23/slot1-core.bin` (SHA-256
  `b12239503b4178a281b4954d0bbad50c77c0e78937f52dce9ce2045337e62461`).
- Native GPU library used for the A/B runs:
  `.build-tmp/n64-native-profile-2026-09-24/libeuther_n64_gpu.so`.
- Frozen probe binaries and raw ABBA `summary.json`/logs:
  `.build-tmp/n64-rsp-progress-2026-09-24/` and
  `.build-tmp/n64-rsp-mix-2026-09-24/`.

For a new Release probe, use `dotnet build tools/N64Probe/N64Probe.csproj -c
Release -m:1 -p:N64LiveGpu=true -p:N64PerformanceProbe=true
-p:N64RdpJournalCapture=false -o <fresh-output-dir> /clp:ErrorsOnly`.
`tools/N64Probe/bench-state.py --help` lists the A/B arguments. The established
run used `--cores 26,27`, guest seconds 5–10 or 5–15, the same native GPU
library on both sides, and `PARALLEL_RDP_SINGLE_THREADED_COMMAND=1` in both
reference and candidate environments. Its four-run order is
reference/candidate/candidate/reference and it rejects mismatched checkpoints.

At handoff, the final Release probe built, `git diff --check` passed, the
flagged VMADH block differential passed, and the temporary task-harness edit
was absent from the working tree. Recheck these if the checkout changes.
