# Compiled CPU block experiment: not enabled (2026-09-20)

Baseline: `e4b307a1`. This experiment was removed from production because it was
slower in the actual Duke gameplay replay. The verified interpreter blocks and
RAM-word improvements remain enabled.

Artifacts are in `.build-tmp/duke-compiled-blocks-2026-09-20/`, including the
prototype source, integration/test patch, reference/candidate builds and logs.
No new package dependencies were introduced. The retained source artifacts are
experiments, not part of the app build.

## Approach

Compile hot straight-line blocks and a final branch/delay slot with .NET
expression trees, calling existing instruction handlers with constant opcode
descriptors. The surrounding interpreter retains its event/Count guards and
continues across block boundaries. Every cached code word is checked live before
execution. Stores overlapping compiled code fall back before mutation. RAM
operands and delay slots are validated before side effects. MFC0 reads retain
precise intermediate Count/RANDOM values and instruction history excludes delay
slots as before.

The cache used 4,096 slots, a 32-hit threshold and at most 256 compilations per
memory instance. Unsupported dynamic-code runtimes fell back to interpretation.
The feature was opt-in during all measurements.

## Correctness

Forced compilation passed 611 full-state differential cases, including live-code
changes after compilation, executing 6,873 compiled instructions in that suite.
One million opcode samples still matched the original decoder/cycle table.

Duke's 20-million-instruction replay retained full-state hash
`6BF947D1D8777DE7F4BCCD98501A4210207F7F87ADEBADF5340784B577C9FA6F`.
The compiled path executed 81,427,754 instructions across the eight replay runs.
Perfect Dark also retained
`6DF70AF2557F2C750643A18E9D8876C365A3CBF8F3E761BC06A95B657C29CF74`.

The actual CPU-thread fixture preserved both its complete state and all 512
history entries. It improved synthetically from 371.804 to 306.429 ms, which did
not translate to this game.

## Performance decision

Pinned CPU 6; three warmups and five samples per replay process:

| Variant | Reference medians (ms) | Candidate medians (ms) |
| --- | --- | --- |
| Compile at eligible sub-block boundaries | 1017.633, 1011.246 | 1667.885, 1251.655 |
| Compile only at outer block entry, minimum four instructions | 998.608, 993.074 | 1028.632, 1123.160 |

The narrower version reduced cache-probe overhead but still lost to the existing
block interpreter. Its compiled path executed 27,960,643 instructions across
eight replay runs. Both versions were removed rather than enabled based on the
synthetic benchmark. A future compiler needs a cheaper validity/dispatch design;
it must still handle self-modifying code and every RAM writer correctly.
