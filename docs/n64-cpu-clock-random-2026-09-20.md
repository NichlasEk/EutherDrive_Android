# N64 CPU timing and RANDOM investigation

Baseline: `8fd2c226`, including the Duke sprite/occlusion fixes.
Artifacts: `.build-tmp/duke-speed-next-2026-09-20/`.

A new 20-second CPU sample from the playable Duke slot-1 snapshot attributes
28.1% exclusive samples to the outer CPU thread, 18.1% to the block interpreter,
6.0% to the multiply shortcut and 5.2% to word writes. Textured triangles account
for 4.4% exclusive samples. These are sampled attribution, including any inlined
helpers, not exact per-method costs. Nested inclusive costs must not be summed.

## Clock experiment (removed)

The existing block and multiply paths already call `GetQuietCpuCycles` before
execution. This excludes every device completion, VI line transition and VI
interrupt during the accepted interval. Both paths only access direct RAM and
cannot change device timing registers. Calling the complete `Tick` afterwards
rechecks event conditions and video scheduling that were already validated.

`TickQuietCpuCycles` performs just the corresponding countdown decrements and
VI line-cycle accumulation. It retains the VI interrupt-enable comparison: a
nonzero stale countdown must remain untouched when VI interrupts are disabled.
Only the guarded block and multiply paths call it. Normal instructions, mapped
idle loops and all actual event boundaries retain the existing full `Tick`.
No emulated cycles or instructions are skipped, and there is no timer cache.
A Debug assertion checks the method's interval precondition.

`--check-quiet-clock` compares complete serialized memory/device states against
individual ordinary ticks for all 128 combinations of seven active device timers,
and 60 VI sync/interrupt/budget configurations, including disabled interrupts
with stale nonzero countdowns. The existing full CPU block and multiply suites
also compare against ordinary instruction execution.

The clock-only variant passed 188 clock cases, 609 CPU block cases and 279
multiply cases. Its CPU-thread fixture median was 312.638 ms versus 342.445 ms
reference, with identical state and history. Duke fixed-work replay measured
1027.335 versus 1044.531 ms, retaining complete-state hash
`F123855881E70B914F7AACAE83BB43BB44B73B9AEDDCC510EF06A3FA3F76275C`.
This differs from pre-sprite-fix hashes because the baseline renderer changed.

Four 45-second actual Duke runs gave reference 6.652/6.570 and clock-only
candidate 6.367/6.426 graphics tasks/s. This did not establish a gameplay gain;
the isolated fixture improvement must not be presented as game FPS.

The quiet-clock implementation and its standalone test are preserved only in
the artifact folder (`combined.patch`, `QuietClockChecks.cs`). They are removed
from the final production source.

## RANDOM counter

A separately tested arithmetic simplification handles COP0 WIRED=0 as a
32-value wrapping down-counter: `(random - instructions) & 31`. Nonzero WIRED
retains the general modulo formula, and zero elapsed instructions retain the raw
unmasked RANDOM register. Duke's extracted states use WIRED=0; Perfect Dark's
state uses WIRED=2. An exhaustive comparison covers all 32 WIRED settings,
64 starting low-register values with unrelated high bits, and 0..128 instruction
counts (264,192 cases) against individual architectural decrements.

## Final comparison and retained change

All actual-game series use identical probe binaries with only the core DLL
exchanged, CPU 6 affinity and four 45-second runs in R/C/C/R order. The first
ten seconds are excluded. Rates count graphics tasks, not displayed FPS.

| Variant | Reference graphics tasks/s | Candidate graphics tasks/s |
| --- | ---: | ---: |
| Quiet clock only | 6.611 | 6.397 |
| Quiet clock plus RANDOM | 6.184 | 6.517 |
| RANDOM only | 6.369 | 6.361 |

The combined result is not a repeatable isolated clock gain: its controls range
from 5.630 to 6.739 graphics tasks/s, and the clock-only series regressed.
The larger clock change was removed. The final production change consists only
of the two-line WIRED=0 counter fast path. Its actual-game result is essentially
unchanged, so this turn establishes no additional Duke FPS improvement.

A final paired R/C/C/R CPU-thread fixture comparison, six measured samples per
process, gives reference medians 336.670/326.619 ms and candidate
334.556/314.125 ms. Averaging process medians gives 331.645 versus 324.341 ms
(2.20% less elapsed time, with run variation). This is a synthetic block workload,
not a gameplay claim. The narrow arithmetic simplification is retained for the
isolated reduction in work, with the exhaustive mathematical checks; no new
clock path, cache, game-specific instruction replacement or event skipping ships.

The final RANDOM-only build retains the CPU-thread state hash
`96D35D3917651781BFF0C4CC30D90EF217B7BDA695EAF27A9BD5A6EC28BDAF77`
and instruction-history hash
`507E8C28AFAEBC3F055AED63FC2D3CA4D3A79BFE1F30EBFB801E8D5A6654AD2C`.
Duke's complete replay hash remains the post-graphics-fix value above;
Perfect Dark remains
`6DF70AF2557F2C750643A18E9D8876C365A3CBF8F3E761BC06A95B657C29CF74`.
The combined build also passed all 609 CPU block cases. Every actual-game run
completed with zero unknown opcodes. No user processes or machine-wide runtime
settings were changed. The sprite fixes and normal device timing remain intact.

Future work should target more of the outer CPU dispatch cost rather than
assuming microbenchmark gains scale into gameplay. A fixed-work benchmark that
runs the complete CPU thread would also improve measurement of these small
changes; current instruction replay omits part of the outer thread machinery.

Final Release UI build: 0 errors, 383 warnings. The UI core DLL matches the
benchmarked RANDOM-only DLL, SHA-256
`8bf28c8b7400c3766db2f7278c4cd0a2bf19c91bd56ac200d2c9219c8b3a07b8`.
