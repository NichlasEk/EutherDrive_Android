# N64 RAM writes and framebuffer page coverage

Starting baseline: `f610a1bd`. Artifacts and experiment scripts:
`.build-tmp/n64-more-speed-2026-09-20/`.

## Changes

Validated CPU-block SW instructions and the multiply leaf's four stack stores
use an aligned RAM-word writer. Their callers already validate the full range
and alignment and reject tracing. An aligned word cannot cross a 4 KiB page,
so the writer updates that page directly. **Every store still increments the
RAM epoch once and performs its framebuffer notification**, even when several
stores hit the same page. No guest instruction counts or device timing change.

Framebuffer tracking now keeps a derived 2 KiB page map of potentially overlapping
RAM pages. A write contained within one untracked page can skip scanning all
tracked framebuffers. Writes spanning pages, and writes to a tracked page, use
the original overlap logic. RAM epoch updates happen before this filter.

The page map is rebuilt when framebuffer bounds change and when framebuffer
metadata is loaded from a savestate. Re-registering unchanged bounds avoids a
rebuild. It is not serialized and does not change the save format. Malformed
saved bounds whose end wraps around uint disable the filter and retain the old
scan behavior. The existing empty-first-framebuffer rule also remains intact.

The multiply leaf reuses the block interpreter's RANDOM update helper, including
its existing WIRED=0 shortcut. The operation count remains twelve instructions
and nineteen cycles. This is a small cleanup with a modest synthetic result,
not an independently established gameplay improvement.

The main CPU block also skips its operand-validator call for non-memory
instructions. Only accepted primary load/store kinds 32 through 63 need that
call; branch delay-slot validation retains the original path. This differs from
the earlier rejected wrapper experiment by placing the gate only at the main
block call site. Native IP sampling motivated revisiting it with identical probe
binaries. Its isolated Duke replay reduced host instructions 1.29% and elapsed
time 1.92%; all 705 block cases passed. Both gameplay pairs improved.

## Validation

- 705 CPU block cases compare full serialized state with ordinary interpretation,
  including 12 new store cases at page/RAM edges, overlapping framebuffers,
  repeated stores, delay slots and RAM-epoch wraparound.
- 285 multiply cases compare full state and rejection behavior, including six
  new stack/page/framebuffer/epoch cases.
- `--check-framebuffer-writes BASELINE_DLL` runs 48 complete-state phases across
  every RAM page, all ordinary store widths, changing/evicted framebuffer bounds,
  dirty-page consumption, old-state restoration and malformed saved bounds.
- Word-access checks cover 131,278 operations. Fetch checks now also exercise
  the internal physical RAM reader, including unaligned reads, aliases, invalid
  ranges and the last valid bytes: 262,405 total operations.
- Rendering/interrupt checks cover 6,845 cases. Sprite checks cover 1,092
  intensity-alpha cases, 960 rectangle-depth cases and 24 blender cases.

Twenty-million-instruction replay hashes remain unchanged:

- Duke: `F123855881E70B914F7AACAE83BB43BB44B73B9AEDDCC510EF06A3FA3F76275C`.
- Perfect Dark: `6DF70AF2557F2C750643A18E9D8876C365A3CBF8F3E761BC06A95B657C29CF74`.

The framebuffer differential digest is
`9BFD5BCBA23DE2481F8AC1C8D98705E430B193BE7E216DA54C85F3E8B37D3037`.

## Intermediate measurements

All timed probes run sequentially pinned to CPU 6 on the local Xeon E5-2697 v3.
No builds or profilers overlap measurements. The machine is not otherwise idle,
so elapsed-time variation is substantial. Fixed replays use three warmups and
five measured samples; tables average the two process medians. Native counters
exclude state loading/hashing and reject multiplexed measurements.

Relative to the aligned-store variant, framebuffer page coverage reduces Duke's
fixed-work time from 1020.978 to 893.824 ms (-12.45%), measured host instructions
by 8.29%, and host cycles by 6.91%. Its four actual 45-second game runs give
7.028 to 7.984 graphics jobs/s (+13.60%), excluding the first ten seconds, with
both pairs improving. These are graphics jobs, not displayed FPS.

Perfect Dark's fixed replay executes 3.21% fewer host instructions but is 6.53%
slower in that noisy series. Separate actual game runs give 9.459 to 9.724
jobs/s (+2.80%), with both pairs improving. Do not turn either result into a
universal game-speed claim. Raw intermediate results are retained in
`intermediate-metrics.json`, `pages-gameplay-results.json` and
`pages-pd-gameplay-results.json`.

The aligned-store-only series was mixed in elapsed time: Duke's fixed replay
improved, Perfect Dark's slowed, and the two Duke gameplay pairs disagreed.
Its combined Duke result was +3.86%. Final combined measurements against the
starting core are therefore more useful than adding these intermediate gains.

## Profiling and rejected alternatives

The initial EventPipe sampling profile is `profile-summary.json`. A subsequent
native instruction-pointer profile of the page-coverage variant is in
`native-summary.json`, with raw JIT maps and disassembly. It sampled 28,306 IPs,
of which 27,493 mapped to managed code. Its largest leaf was TryAdvanceCpuBlock
(27.02%), followed by ExecuteCpuBlockInstruction (6.32%) and operand validation
(5.49%). These sampling methods attribute time differently; the native sample
was used to locate specific instruction costs, not as a benchmark.

- `cold/`: extracting watchdog/PIF logging from the outer CPU loop retained
  synthetic state/history hashes but lost 1.35% in its gameplay series. Removed.
- `fetch/`: an explicitly range-checked unaligned internal RAM read passed all
  262,405 comparisons and reduced Duke host instructions 1.23%, but did not
  improve elapsed time. Removed; the ordinary BinaryPrimitives implementation
  remains. The extra physical-reader regression cases are retained.
- `random/`: shared RANDOM update passed all 285 multiply cases and preserved
  the actual CPU-thread state/history hashes. Its synthetic timing varied;
  measured host instruction savings were small. No separate FPS claim.

Prototype snapshots and binaries remain under the artifact directory. The
probe supports `N64_PROBE_REPLAY_INSTRUCTIONS` for longer fixed-work runs;
its default remains 20 million instructions.

## Final combined fixed-work comparison

Compared directly against `f610a1bd`, using identical final probe binaries with
only the core DLL swapped. Each replay requests 50 million instructions and
finishes at 50,000,001 because the ordinary last branch executes its delay slot.
Reference/final/final/reference order, three warmups and five samples per process.

| Workload | Reference ms | Final ms | Less time | Fewer host instructions |
| --- | ---: | ---: | ---: | ---: |
| Duke | 2534.185 | 2299.833 | 9.25% | 10.55% |
| Perfect Dark | 3936.310 | 3374.932 | 14.26% | 2.06% |

Host cycle reductions were 9.87% and 13.39%, respectively. The much smaller
instruction reduction in Perfect Dark and the substantial variation between
processes limit how strongly its elapsed-time gain should be interpreted.
`final-replay-results.json` contains the raw means and reductions.

All baseline/candidate runs agree on the complete serialized state:

- Duke 50M: `F797D4FD883F49BBD77C29FDA510810CC23BD36C5609863DCFDF0B3B80B4E593`.
- Perfect Dark 50M: `5DF61F64B5D6511D96B8E468620DB8D48B3D9819028889FF72E4B407E62BE19A`.

## Final actual game series

Starting core versus the final combination: six 60-second Duke runs in
reference/final/final/reference/reference/final order, then four 45-second
Perfect Dark runs in reference/final/final/reference order. First ten seconds
excluded from every run. Unknown opcodes stayed zero. All paired comparisons
improved, with substantial host variation in Duke.

| Game | Reference jobs/s | Final jobs/s | Increase |
| --- | ---: | ---: | ---: |
| Duke | 6.663 | 7.797 | 17.02% |
| Perfect Dark | 9.575 | 9.906 | 3.46% |

These rates count completed RSP graphics jobs, not displayed FPS. They describe
these snapshots on this host and do not promise the same percentage everywhere.
Raw per-run rows are in the two JSON files named above. The final core is in
`final/` (same core as `guard/`); `reference/` contains the starting core with the
same final probe binaries/dependencies.

Final baseline differential checks for framebuffer writes, word accesses and
instruction fetch pass. Rendering and sprite checks pass, as do both batch
rejection checks with `EUTHERDRIVE_TRACE_WATCH_ADDRESS=0`. The block suite also
passes 264,192 RANDOM cases and its million-word decoder sample.

Release UI build: 0 errors, 500 warnings. Its core exactly matches the
measured/tested final DLL, SHA-256
`f42fa45ec6f302028f17ce46dd9ff1453e7d56af17dce0ea40fd2478ff8c1a9c`.
