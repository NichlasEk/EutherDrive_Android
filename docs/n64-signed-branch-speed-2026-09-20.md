# N64 signed branch compilation

Baseline: `ece73662`. Linux desktop N64, continuing from the CPU block compiler.
Local evidence is in `.build-tmp/n64-jit-next-2026-09-20/`; its `signed/` binary
is the candidate, and `../n64-cpu-jit-2026-09-20/loops/` is the baseline.

## Bottleneck and change

A fresh Rampage slot 3 profile put 13.21% of CPU samples in
`TryAdvanceCpuBlock`, 7.78% in `Memory.Tick`, and 7.35% in `InterpretOpcode`.
The instrumented 20-million-instruction replay then identified 2,617,416
fallback executions of `BGEZ`. Two branch words, `0441fffd` and `0441fff9`,
accounted for over 2.61 million block exits. This repeatedly broke up otherwise
compilable loops.

The bounded interpreter and native compiler now accept `BLTZ`, `BGEZ`, `BLEZ`
and `BGTZ`. Generated branches compare the full signed 64-bit register value
against zero, capture the target before the delay slot, and preserve the
existing delay-slot and clock accounting. Eligible backward branches can repeat
inside the existing event-free native budget. Code-byte validation, memory
guards, background compilation, the compilation cap and timer limits are unchanged.

Likely/link REGIMM variants still use the ordinary interpreter. Invalid reserved
bits and nested branches in delay slots are rejected. Self-branches still return
to the outer loop to preserve the watchdog cadence. Native repeated loops still
exclude stores and CP0 operations.

## Validation

`--check-cpu-jit` passes 1,263 complete serialized-state cases against the ordinary
interpreter, with 1,105 cases actually executing native code. Coverage includes
signed extrema, values whose upper/lower words have different signs, r0,
delay-slot operand changes, precise COUNT reads, faulting delays, nested branch
rejection and repeated signed loops across different budgets. Existing event,
self-modification, worker-publication and reset-during-compilation checks pass.

Timing comparisons use serial fresh processes pinned to physical CPU cores 6
and 7, three warmups and five samples per fixed-work run. Each replay compares
the entire serialized machine state. Scene rates count graphics tasks, not
display FPS. Other user applications remain running.

## Fixed-work results

50 million guest instructions per sample (Perfect Dark executes the final atomic
branch/delay pair and ends at 50,000,001 in both builds). These rows compare
`ece73662` with this change, not with the earlier interpreter-only baseline.

| Game / pair | Baseline ms | Candidate ms | Baseline host instructions | Candidate host instructions |
|---|---:|---:|---:|---:|
| rampage 1 | 1974.447 | 1034.590 | 16,235,446,987 | 8,750,886,873 |
| rampage 2 | 1947.434 | 968.898 | 16,296,800,985 | 8,849,512,347 |
| mario 1 | 2545.726 | 2433.961 | 22,100,338,188 | 21,740,469,743 |
| mario 2 | 2587.231 | 2501.704 | 22,027,354,484 | 21,814,485,854 |
| duke 1 | 1895.346 | 1854.352 | 14,691,402,671 | 14,453,513,767 |
| perfect-dark 1 | 3549.735 | 3777.950 | 29,284,858,825 | 30,217,182,065 |
| perfect-dark 2 | 3569.653 | 3534.332 | 30,001,128,945 | 30,020,039,044 |

Rampage takes 48–50% less elapsed CPU-replay time and uses about 46% fewer
host instructions. Mario improves 3–4% in these pairs; Duke's fixed replay
improves about 2%. Perfect Dark compiles zero blocks in this mapped-code
snapshot and has no reliable timing improvement: its first pair is 6.4% slower,
its repeat is 1.0% faster. Do not report that as a gain or conceal the variation.

All runs match the complete baseline state exactly:

| Game | SHA-256 |
|---|---|
| rampage | `CC87243FFF6860A0A94AA0CBAD900F6CE32FFD44012E21AB8A8EE82E2184D344` |
| mario | `D14DBC4767958E314135861F394F54430A90953B2B635AC64C56D7DFB1944ECF` |
| duke | `8890CFE3EB743929C06D5ADEFA2C278723991B4ED64404CB1F00819EE0EA5D4A` |
| perfect-dark | `1D07FB647F81FCD55AA308BF1DB09DA28978EDDE3E66A983BBF9D15701C38CE5` |

## Actual scene runs

Fresh 35-second processes load the existing core snapshots. Rates use telemetry
from approximately second 1 through 35, including the initial compilation period.
All runs use the same CPU affinity as the fixed-work tests.

| Scene / pair | Baseline graphics tasks/s | Candidate graphics tasks/s |
|---|---:|---:|
| rampage 1 | 13.407 | 25.611 |
| mario 1 | 29.921 | 30.742 |
| duke 1 | 8.131 | 7.839 |
| duke 2 | 8.172 | 8.607 |

Rampage improves about **91%** in its saved scene; the last 15 seconds also improve
from 13.40 to 25.10 tasks/s. Mario improves about 2.7% over its full interval.
Duke changes from -3.6% to +5.3% when the order is reversed, so its scene result
is inconclusive. These counters are not display FPS and do not establish full
speed for every game. No frame skipping or altered guest timing is introduced.

Evidence: `verified-bench.json`, `verified-scenes.json`, `repeat-pd.json`,
`repeat-duke-scenes.json` and their adjacent per-run logs. The original Rampage
save is only read; benchmarks and scene outputs live under `.build-tmp/`.

## Release checkpoint

- With `EUTHERDRIVE_N64_CPU_JIT=0`, all 1,181 bounded-interpreter cases pass.
- RSP scheduling checks pass (32 slice boundaries, save/restore, CPU publication
  and DP synchronization).
- Gauntlet's one-million-instruction replay matches the baseline full-state hash
  `C104C3D713939220FA6DD82B59695F7521BBBB97EDEDDE09644928A2C8686C0A`.
- The Release UI builds with zero errors. Its `Ryu64.MIPS.dll` exactly matches
  the tested candidate: SHA-256
  `6214aa0da81d844493d41d752aa5eac817734577ff978c2162a36d96a302818a`.
- Original Rampage save-container SHA-256 is still
  `c38698d2677d99c509aaa26e106cbc4066f6bf129f7c174659159640fde1b580`.

Restart the Release app and load the same Rampage save (slot 3 in the measured
snapshot). No settings or save-format changes are needed.
