# Gauntlet: instruction-page translation cache

Continuation of [the filtered mapped CACHE-loop experiment](n64-gauntlet-mapped-loads-2026-09-26.md).
Linux desktop only. Artifacts are under
`.build-tmp/n64-cache-filter-profile-2026-09-26/`.

**Current shipping defaults:** after the measurements below, the user requested
that measured wins become standard. CPU-owned instruction translation (mode 2)
and mapped CACHE-entry-only JIT are now on by default. Mode 1, broad mapped
admission and mapped loads remain experimental. Older passages below describe
the opt-in evaluation phase and its frozen binaries.

Normal desktop startup now needs only `sh scripts/run-n64-gpu-desktop.sh`.
Explicit environment overrides still take precedence. To restore the previous
CPU path for a comparison, set `EUTHERDRIVE_N64_INSTRUCTION_PAGE_CACHE=0` and
`EUTHERDRIVE_N64_CPU_JIT_MAPPED=0` before launch.

Default-mode validation: the rebuilt Release probe passes the 38-case mapped
CACHE-entry suite and the expanded 271,045-operation fetch differential with
the CPU switches unset. A complete 15-second Gauntlet replay also matches the
accepted mode-2 candidate at all three checkpoints, audio/input hashes, frames
and final serialized CPU/RAM state. This last replay is correctness-only:
desktop building and the direct-JIT suite ran concurrently. It supplies no
additional timing claim. The Linux desktop Release build passes.

The final default-mode direct-JIT regression also passes: 2,267 block cases
(2,004 compiled), full state, rejection/event boundaries, self-modification,
stale background compilation and reset during compilation. Its RANDOM and
decode checks pass as well. Log: `default-direct-jit-tests.log`.

## Profile with the CACHE-entry filter active

The frozen previous `review-probe/` was sampled with `dotnet-trace`, with mapped
JIT, CACHE and CACHE_ONLY enabled and mapped loads disabled. The same Gauntlet
Europe ROM, frozen slot 1, native renderer, affinity 26/27, GPU overlap, direct
RDP commands, two workers and small-types/synchronous shaders were used.
No validation or JIT counters were enabled. This is a diagnostic run, not a
performance comparison.

`filtered.nettrace`, `filtered.speedscope.json`, `managed-summary.json` and
`replay/` preserve the raw profile, derived attribution and replay. Its three
checkpoints, frame files, audio/input hashes, cycles/tasks and final serialized
CPU/RAM state exactly match the preceding filtered candidate.

Approximate guest seconds 5–15, taking the deepest managed method in each
sample stack (ignoring synthetic CPU_TIME/UNMANAGED_CODE_TIME leaf labels):

| Method | Sample weight, seconds |
| --- | ---: |
| InterpretOpcode | 11.854 |
| RspInterpreter.ExecuteSlice | 9.492 |
| CPU thread outer loop | 5.784 |
| TLB.TranslateAddress | 5.538 |
| N64GpuBackend.Submit | 3.995 |
| ReadOpcode | 2.613 |
| N64GpuBackend.ReadbackCore | 2.179 |

These are approximate sample weights, not exclusive hardware CPU durations.
Inlining/safepoints affect attribution and native waits are attributed to the
nearest managed caller. The window is aligned from CPU-thread start plus the
first checkpoint's wall time, not a precise trace marker. Nonetheless,
translation and fetch remain useful candidates after the CACHE-loop change.

## Isolated candidate

`EUTHERDRIVE_N64_INSTRUCTION_PAGE_CACHE=1` makes interpreted opcode fetches use
a thread-local last-instruction-page translation. It stores one 4 KiB virtual
subpage, ASID, physical subpage and TLB version. A hit must match all identity
fields and the current version. Misses call the existing strict translator;
exceptions are not cached. The existing low-address bring-up fallback remains
in ReadOpcode and cannot populate this cache as a successful TLB mapping.

TLB indexed/random writes, reset and state loading already advance the version.
ASID changes without a write are caught by the tag. Each thread owns its cache;
cross-thread version changes are observed through the existing volatile version
read. Large/overlapping mappings retain the existing translator's scan priority
and are cached only as 4 KiB subpages. Data translations are unchanged.

This stores **translations, not opcode bytes**. Every fetch continues through
the existing physical memory/GPU ownership read path, so a matching page does
not imply unchanged instruction contents. The new derived state is not saved.
No game address/signature or clock adjustment is involved. This first,
thread-local variant was opt-in during evaluation and remains so.

## Iteration: CPU-owned fetch cache

A second diagnostic profile with mode 1 (`page-cache.nettrace`,
`page-cache-managed-summary.json`, `page-cache-profile-replay/`) also matches
the exact replay. Approximate ReadOpcode sample weight rises from 2.613 to
5.674 seconds while TranslateAddress falls from 5.538 to 1.572 seconds. Some
work moved into the inlined fetch method. These samples are not proof of a
net speed improvement or an explanation of all timing variation.

`fetch-disasm.txt` shows that the optimized mode-1 cache hit still performs a
thread-local-storage lookup. The next candidate, environment value **2**, puts
the derived cache in the CPU's private fetch path instead. ReadOpcode has only
two production callers: the CPU main loop and its delay-slot execution. Both
already own the single static R4300 architectural state. UI diagnostics call
the general TLB API and do not access this CPU-owned cache. The shared cache
kernel still checks ASID, 4 KiB page and volatile TLB version; the general and
thread-local translation APIs remain available. A future multi-instance CPU
must move this cache with its architectural state, not share it across CPUs.

At evaluation time mode 0 was the default; mode 1 preserves the first experiment.
Mode 2 began as another opt-in, not an assumption that thread-local overhead caused
the earlier variability. It passes the same 271,045 expanded fetch operations
and 264,192 strict translation comparisons with 128 reader-thread updates.
The fetch differential also runs with tiering disabled to inspect optimized
code: `cpu-owned-fetch-disasm.txt` has no thread-static lookup in the new fetch
path. This diagnostic setting is **not** used for gameplay timing.

`cpu-owned-abba/` compares modes 0/2/2/0 with a frozen `cpu-owned-probe/`, normal
runtime tiering, the same native library and unchanged replay/timing settings.
No builds or diagnostic work overlap its measurements.

| CPU-owned experiment | Wall seconds, observations | Mean |
| --- | --- | ---: |
| Cache off | 70.2804, 68.3528 | 69.3166 |
| CPU-owned cache (mode 2) | 64.5912, 64.4079 | 64.4996 |

Mode 2 gives **+7.47% throughput** in this ABBA series, on top of the previous
CACHE-entry JIT experiment. Both candidate observations beat both controls.
All three checkpoints, frames, audio/input/cycle/task checks and final CPU/RAM
snapshots match. About 9.996 guest audio seconds take 64.500 wall seconds:
**15.50% real time** in this demanding sequence, still far from full speed.

Mode 2 is now the default at the user's request. This is evidence for this
scene and machine, not an
all-games result. Mode 1 stays available to investigate the first design;
its eight-run non-result must not be combined with mode 2's speed claim.

To reproduce this second variant, use the same benchmark command below with
both binary paths pointing at `cpu-owned-probe/N64Probe.dll`, candidate
`EUTHERDRIVE_N64_INSTRUCTION_PAGE_CACHE=2` and a fresh output directory.

To try mode 2 in the Linux desktop with the preceding filtered JIT experiment:

```sh
EUTHERDRIVE_N64_INSTRUCTION_PAGE_CACHE=2 \
EUTHERDRIVE_N64_CPU_JIT_MAPPED=1 \
EUTHERDRIVE_N64_CPU_JIT_MAPPED_CACHE=1 \
EUTHERDRIVE_N64_CPU_JIT_MAPPED_CACHE_ONLY=1 \
EUTHERDRIVE_N64_CPU_JIT_MAPPED_LOADS=0 \
sh scripts/run-n64-gpu-desktop.sh
```

Restart with `EUTHERDRIVE_N64_INSTRUCTION_PAGE_CACHE=0` to disable only this
pass's cache. Also set `EUTHERDRIVE_N64_CPU_JIT_MAPPED=0` to return to the normal
CPU path. These environment switches are sampled when the process starts.

Next: repeat mode 2 in a later Gauntlet sequence and another mapped-code game,
then profile with it enabled before widening mapped JIT admission. Preserve
ASID/version, virtual-PC and GPU ownership guards. The first broad JIT attempt
and first page-cache implementation show why a smaller lookup count alone is
not an acceptance criterion.

## Validation

- 264,192 strict instruction-translation comparisons against the frozen prior
  assembly pass, including page masks, ASIDs, invalid entries, remaps, reset,
  save/load and exceptions. A reader thread also observes 128 mapping updates.
- The original 264,192 general-translation comparisons and 128 reader updates
  pass unchanged.
- Expanded opcode-fetch tests pass 271,045 operations with identical values,
  exceptions and final serialized state. They add repeated mapped fetches,
  physical code writes, ASID changes, remaps, reset and restored mappings to
  the existing direct-memory/boundary/framebuffer tests.
- Release probe build and `git diff --check` pass.

Logs: `translation-tests.log`, `tlb-control-tests.log`,
`fetch-expanded-tests.log`, `build-final.log`.

## Whole-emulator measurement

`page-cache-abba/` runs ordinary/translation-cache/translation-cache/ordinary,
all with the previous CACHE-entry filter enabled. Both modes use the same
frozen `candidate/` binary; only INSTRUCTION_PAGE_CACHE changes. Measure guest
seconds 5–15 using the existing exact-state/frame/audio/input oracle in
`tools/N64Probe/bench-state.py`. No builds, profiling or GPU diagnostics overlap
the timed series. Existing unrelated host workloads remain running.

The first series matched every replay oracle:

| Mode | Wall seconds, observations | Mean |
| --- | --- | ---: |
| Filtered JIT, page cache off | 69.1441, 69.1633 | 69.1537 |
| Filtered JIT, page cache on | 65.4834, 72.1684 | 68.8259 |

Mean throughput differs by only +0.48%, while the candidate varies by over
10%. The initial fast observation is **not** a stable speedup. Existing host
load included QEMU, Python and a compiler (`host-load-during-abba.txt`); this
does not prove the source of variation. No observation is discarded.

`page-cache-reverse/` repeats with on/off/off/on order, the same guest window,
inputs and binary. Its harness labels are reversed: `reference` is cache on
and `candidate` is cache off. Interpret environment values, not just labels.
The reverse series also matches all three checkpoints/frames and final state:

| Actual mode, reverse series | Wall seconds, observations | Mean |
| --- | --- | ---: |
| Cache on | 66.2463, 72.7467 | 69.4965 |
| Cache off | 67.9674, 70.0744 | 69.0209 |

Across all eight observations, off averages **69.0873 s**, on **69.1612 s**:
**−0.11% throughput**. No observation is discarded. The cache-on observations
vary substantially more than controls; host load alone does not establish a
cause. This is **not an accepted speed improvement**. Preserve the isolated
implementation and diagnostics, off by default, for code-generation/locality
investigation rather than reporting the initial +5.6% as a whole-game win.

Reproduction from the repository root (choose a fresh output directory):

```sh
root=.build-tmp/n64-cache-filter-profile-2026-09-26
gpu=.build-tmp/n64-live-gpu/native/libeuther_n64_gpu.so
env PARALLEL_RDP_SINGLE_THREADED_COMMAND=1 EUTHERDRIVE_N64_GPU_OVERLAP=1 \
  EUTHERDRIVE_N64_CPU_JIT_MAPPED=1 EUTHERDRIVE_N64_CPU_JIT_MAPPED_CACHE=1 \
  EUTHERDRIVE_N64_CPU_JIT_MAPPED_CACHE_ONLY=1 EUTHERDRIVE_N64_CPU_JIT_MAPPED_LOADS=0 \
  python tools/N64Probe/bench-state.py \
  --reference "$root/candidate/N64Probe.dll" --candidate "$root/candidate/N64Probe.dll" \
  --reference-env EUTHERDRIVE_N64_INSTRUCTION_PAGE_CACHE=0 \
  --candidate-env EUTHERDRIVE_N64_INSTRUCTION_PAGE_CACHE=1 \
  --rom .build-tmp/n64-gauntlet-legends-2026-09-25/rsp-slice-graphics-verified/input.z64 \
  --state .build-tmp/n64-gauntlet-legends-2026-09-24/slot1-core.bin \
  --output "$root/page-cache-abba-repeat" --cores 26,27 --start 5 --end 15 \
  --reference-gpu-library "$gpu" --candidate-gpu-library "$gpu"
```
