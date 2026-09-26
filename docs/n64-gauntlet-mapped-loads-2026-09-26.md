# Gauntlet: mapped integer loads and the CACHE loop

Current defaults, after the subsequent instruction-page-cache pass: mapped
JIT, CACHE support and CACHE-entry-only admission are enabled unless explicitly
set to 0. Mapped loads remain off. The broad experiments below describe their
historical opt-in builds. See [current defaults and results](n64-gauntlet-instruction-page-cache-2026-09-26.md).

Continuation of [mapped instruction JIT](n64-gauntlet-mapped-jit-2026-09-26.md).
The prior prototype was correct but slower; its warmed profile showed 2.77
instructions per successful execution. This pass inspects the actual hot code
before extending the admitted instruction set. Linux desktop only.

## Evidence and changes

The new read-only probe command `--inspect-cpu-state ROM CPU_STATE PC...` loads a
raw CPU snapshot with the matching core/GPU infrastructure and prints opcode,
mnemonic, physical address and operands. It does not run or overwrite the game.
`hot-blocks.txt` and `hot-loop.txt` under
`.build-tmp/n64-mapped-operands-2026-09-26/` record inspection of the preceding
`profile-10/cpu-state.bin`.

- `e0019660`: `LUI r2,e013`, followed by `LW r2,-1108(r2)`. The second instruction
  reads mapped virtual address `e012fbac`; the old native data guard rejected it.
- `e001952c`: `LUI r5,e00b`, followed by `LW r5,-3648(r5)`, reading `e00af1c0`.
- The high-frequency block `e00996a4` follows `CACHE` at `e00996a0`. Its BNE
  branches back to that unsupported instruction, breaking native loop execution.

The first change (`EUTHERDRIVE_N64_CPU_JIT_MAPPED_LOADS=1`, together with mapped
JIT) admits aligned LB/LBU/LH/LHU/LW/LWU/LD operands in mapped instruction blocks.
A generated guard resolves the current TLB mapping to populated RDRAM, then
uses the existing signed/unsigned load and GPU ownership-read paths at that
physical address. Failure exits at the original virtual instruction before
register mutation. No MMIO, unaligned accesses or TLB-miss fallback is compiled.
An aligned operand of at most eight bytes cannot cross a 4 KiB subpage. Data
addresses are resolved at execution time, not captured as constants. Stores and
mapped loads in branch delay slots retain their previous fallback behavior.

The second, separately selectable change is
`EUTHERDRIVE_N64_CPU_JIT_MAPPED_CACHE=1`. It admits CACHE only in mapped blocks
and preserves `InstInterp.CACHE`'s **existing PC-only stub**. It does not implement
N64 cache behavior or remove any currently emulated cache operation. A native
backedge can now include that instruction and process the loop within the
existing quiet-cycle budget. CACHE delay slots remain interpreted. Direct-code
admission is unchanged; the earlier unsuccessful Mario-wide CACHE experiment is
not re-enabled globally.

Both new switches require `EUTHERDRIVE_N64_CPU_JIT_MAPPED=1`. All remain opt-in
pending whole-game acceptance. The prior mapped admission optimization remains
active unless `EUTHERDRIVE_N64_CPU_JIT_MAPPED_ADMISSION=0` is supplied.

## Correctness evidence

- The previous 125 mapped-code cases still pass with the new options off.
- The mapped-load suite passes 194 full serialized CPU/device/RAM comparisons
  and virtual-history checks. Added coverage includes all seven integer loads,
  destination/base aliases and r0, odd/subpage boundaries, unaligned/missing/
  out-of-RAM rejection and mapped MMIO rejection without side effects.
- With the CACHE option, 231 cases pass, including all 32 CACHE operation fields
  with a deliberately invalid operand address (matching the current stub),
  bounded backedges with budgets 4/13/64/512, and CACHE delay-slot rejection.
- `--check-mapped-gpu-loads LIBRARY` passes seven generated mapped integer loads
  against newer GPU-owned RAM. Each returns the correct signed/unsigned value
  and causes exactly one required readback; Vulkan validation reports zero
  errors. This diagnostic needs `N64RdpJournalCapture=true`, small-types shader
  mode and validation enabled. See `gpu-load-tests.log`.

The initial four-run `abba/` series matched all checkpoints and final CPU/RAM
snapshots, but **its timings are not acceptance evidence**. A GPU-backed snapshot
inspection ran concurrently, and later builds/validation work were allowed once
the series was marked correctness-only (`TIMING_INVALID.txt`). A fresh series
is required after all such work finishes.

## Reproduction and measurements

The frozen `final-probe/` uses Release, `N64LiveGpu=true`,
`N64PerformanceProbe=true`, `N64RdpJournalCapture=false`. The native renderer,
ROM and frozen Gauntlet slot 1 are unchanged from the preceding report.

`bench-three.py` in the artifact directory is a small experimental copy of
`tools/N64Probe/bench-state.py`: it retains all existing checkpoint/frame/audio/
input/final-state comparisons and adds the third mode and ABC-CBA ordering.
`reference` means mapped JIT off; `loads` means mapped JIT plus mapped loads;
`candidate` means both plus CACHE. All use the same frozen binary and library.
The measured interval is guest seconds 5–10, CPU affinity 26/27, GPU overlap,
direct RDP commands, two workers and synchronous shader compilation. Profiling
and validation are off. No builds, GPU tests or inspectors run during this
fresh timed series. Other existing host workloads are not stopped.

The clean `abccba/` series passed all exact-state checks:

| Mode | Wall seconds, observations | Mean | Throughput vs ordinary path |
| --- | --- | ---: | ---: |
| Ordinary path | 35.8375, 35.4154 | 35.6265 | baseline |
| Mapped JIT + loads | 35.6034, 34.9324 | 35.2679 | +1.02% |
| Mapped JIT + loads + CACHE | 34.2319, 34.3640 | 34.2979 | +3.87% |

Both checkpoints and frame files, audio/input hashes, task counts, cycles,
RDRAM and final serialized CPU/RAM snapshots match in all six runs. The loads-
only effect is small relative to host variation; the combined candidate needs
a longer confirmation before accepting the performance result.

`confirm-reverse-5-15/` reverses order to combined/ordinary/ordinary/combined
and measures guest seconds 5–15. Its harness labels are reversed: `reference`
means combined and `candidate` means ordinary. Interpret the environment and
the timing ratio accordingly. No source/build/GPU work overlaps this series.

| Longer window, actual mode | Observations | Mean |
| --- | --- | ---: |
| Combined | 69.6374, 71.5168 | 70.5771 |
| Ordinary | 70.2775, 71.2464 | 70.7620 |

All three checkpoints and final snapshots matched, but the **+0.26%** mean
throughput difference is smaller than run-to-run variation. The earlier +3.87%
does not establish a stable win for the broad mapped path. Keep it opt-in as
an exact experimental implementation, not a new production speed claim.

## Restrict mapped admission to CACHE entries

The next isolated variant adds `EUTHERDRIVE_N64_CPU_JIT_MAPPED_CACHE_ONLY=1`.
The main CPU loop checks the primary opcode before calling the mapped JIT
dispatcher. Non-CACHE mapped instructions retain ordinary interpretation,
avoiding per-instruction warming/lookup/setup overhead. CACHE entries can use
the existing guarded native block machinery; direct-segment admission is
unchanged. No game-specific PC or ROM signature is used.

This trial uses mapped mode and CACHE enabled, **mapped loads disabled**. It
isolates the profitable-looking loop without assuming broad mapped dispatch
pays for itself. The separately developed load support stays available for
later iteration. The 38-case CACHE-entry suite checks rejection of other
mapped entries, all 32 CACHE operation fields, bounded loops, history and a
not-taken loop exit. It passes complete state comparison. The broad 231-case
suite also passes again with the filter disabled.

`cache-entry-abba-5-15/` compares ordinary/filtered/filtered/ordinary over guest
seconds 5–15 with the same frozen `cache-entry-probe/`, native library, inputs,
affinity and graphics settings. No builds or GPU diagnostics overlap timing.

| Mode | Wall seconds, observations | Mean |
| --- | --- | ---: |
| Ordinary | 71.6854, 70.8451 | 71.2652 |
| CACHE entries only | 69.1962, 68.9017 | 69.0489 |

The filtered variant gives **+3.21% throughput** in this ABBA series. Both
candidate runs beat both controls. Three checkpoints/frames and final CPU/RAM
snapshots match, together with the harness's audio/input/cycle/task checks.
This is a useful local result, not whole-game acceptance: the roughly ten
emulated seconds still take 69 real seconds (about 14.48% real time), and other
host workloads remain a source of variation. All mapped experiments stay off
by default; no game-specific selection has been introduced.

## Try the filtered variant

From the repository root, launch the Linux desktop with:

```sh
EUTHERDRIVE_N64_CPU_JIT_MAPPED=1 \
EUTHERDRIVE_N64_CPU_JIT_MAPPED_CACHE=1 \
EUTHERDRIVE_N64_CPU_JIT_MAPPED_CACHE_ONLY=1 \
EUTHERDRIVE_N64_CPU_JIT_MAPPED_LOADS=0 \
sh scripts/run-n64-gpu-desktop.sh
```

Load the same Gauntlet save. To return to the ordinary CPU path, restart with
`EUTHERDRIVE_N64_CPU_JIT_MAPPED=0 sh scripts/run-n64-gpu-desktop.sh`.
Environment switches are read at process startup.

The final probe and Linux desktop Release builds passed. The inspector was
smoke-tested after adding GPU ownership synchronization before reading opcode
bytes. Build logs, inspection output and SHA-256 hashes of the frozen probes,
renderer, save and rebuilt desktop are in the artifact directory. The desktop
was built, but no interactive whole-game playthrough was performed in this pass.

## Continuation

Preserve the broad load implementation for iteration, but do not enable it
based on the short-window gain: its longer confirmation was inconclusive.
First confirm the narrower CACHE-entry result in another sequence and a longer
desktop session. Then profile the remaining interpreter/translation cost with
the filter active in a separate diagnostic run. Choose the next admission
rule from actual useful work per entry; broad mapped dispatch already showed
that more compiled instructions can lose to lookup and guard overhead.
Do not relax ASID/translation/opcode/GPU ownership checks to improve timing.
