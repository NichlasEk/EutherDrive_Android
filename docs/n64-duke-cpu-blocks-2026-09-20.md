# Duke Nukem 64: event-bounded CPU blocks (2026-09-20)

Continuation from `224cc5a9`, using the same latest playable slot 1. The slot file
is read-only throughout this work:

- `/home/nichlas/roms/N64/Duke_Nukem_64__Europe_.z64_8aaa9cce.euthstate`
- SHA-256: `1c18b38b54947268807704d028f281c9c889ce295bac12640a84248677d2730a`
- ROM: `Duke Nukem 64 (Europe).z64`
- Raw CPU snapshot: `.build-tmp/duke-playable-2026-09-20/cpu-state.bin`
- Artifacts: `.build-tmp/duke-cpu-blocks-2026-09-20/`

## Measured problem

A new 20-second sampling profile of `224cc5a9` found 85.46% of CPU-thread time
outside RSP task execution. Leaf samples attributed 33.22% to `InterpretOpcode`,
22.18% to the CPU outer loop, 7.58% to `WriteUInt32`, and 4.97% to `ReadOpcode`.
Inlined work is attributed to its visible caller. These are sampled thread-time
shares, not instruction counts or hardware performance counters.

## Change

`R4300.Blocks.cs` executes up to 32 one-cycle guest instructions during a quiet
device interval. It supports non-trapping integer arithmetic, aligned direct-RAM
loads/stores, ordinary J/JAL/JR/JALR/BEQ/BNE and their supported delay slots,
MFC0 reads, and MTC0 STATUS. It can follow jumps inside the bounded interval.

This is an interpreter optimization, without a decoded-code cache. Every next
instruction is fetched from live RAM after prior stores and branches. Address
validation precedes any execution that could fault. A delay-slot RAM address is
validated using the link register's *new* value when JAL/JALR changes its base.
All other CP0 writes, FPU/TLB instructions, trapping arithmetic, MMIO, mapped
memory and unsupported delay slots return to the ordinary instruction path.

The block stops before every possible existing boot/runtime loop shortcut.
Boot low RAM is excluded. The multiply leaf shortcut still takes precedence.
Tracing, debugging, stepping, pending IP/RCP interrupts, pending delay-slot
exceptions, Count wrap/mismatch and Compare crossings disable batching.

`GetQuietCpuCycles` bounds each block before VI line/interrupt changes and all
SP/RSP/DP/PI/SI/AI lifecycle completions. The block therefore aggregates only
counter updates that cannot trigger an event. It does not skip guest cycles or
change the emulated CPU rate. MFC0 sees the exact intermediate Count/RANDOM,
including the interpreter's delay-before-branch timing order. MTC0 STATUS cannot
create a pending interrupt inside such an interval; its original handler runs.

Instruction history still records every outer instruction, excluding delay
slots as before. A self-branch ends the block to preserve watchdog accounting.
The normal instruction path remains the fallback at every unsupported boundary.

## Performance

Release builds, pinned to CPU 6, no profiling or builds during timed runs. The
fixed-work test restores the snapshot, runs 20 million instructions, then hashes
the entire serialized CPU/device/RAM state outside the timed section. Each
process uses three warmups and five measured samples. Process order was
reference/candidate/candidate/reference.

| Build | Process medians (ms) | Mean of medians (ms) |
| --- | --- | --- |
| `224cc5a9` | 1242.384, 1225.847 | 1234.116 |
| CPU blocks | 1057.576, 1059.285 | 1058.431 |

That is **16.6% more instruction throughput**, or **14.2% less host time** for
this fixed workload. Every run ended with the same full-state SHA-256:

`6BF947D1D8777DE7F4BCCD98501A4210207F7F87ADEBADF5340784B577C9FA6F`

A separate 60-second real CPU-thread run from the same adapter save used the
reverse order (candidate, reference), with the first ten seconds excluded:

| Build | Emulated cycles/s | Graphics tasks/s |
| --- | ---: | ---: |
| `224cc5a9` | 13,853,143 | 4.378 |
| CPU blocks | 17,940,819 | 5.672 |

This is **29.5% higher sustained throughput** in this run. Graphics tasks are
not displayed FPS. The scene still needs substantially more speed to be
playable. `broad-frame.png` shows the level, weapon and HUD at 60 seconds.

These measurements use the `broad/` artifact, before private helper names were
cleaned up for the final source. The earlier linear-only, branch, Count and
chained variants are retained as development evidence, not additional gains to
sum together. Absolute timings vary between processes and host conditions.

## Final NOP refinement

The generic zero-loop recognizer only permits a NOP at offset `0x14` of its
second pattern. Runtime NOP entry therefore checks that one possible base with
the same complete live-code validation. A block can continue through other NOPs;
it yields when the preceding word is the zero-loop branch `0x1520fffb`. The
709-case old-versus-gated loop suite still passes, including modified code.
The outer CPU loop also avoids block calls altogether for mapped guest code.

The final `nop/` build has the same full-state Duke replay hash. Its two process
medians were 1100.903 and 1025.549 ms, against 1451.405 and 1232.805 ms. Host
variation is substantial; retain the individual results rather than interpreting
the averaged 26% throughput gain as a precise universal number.

A fresh 45-second actual CPU-thread comparison (candidate, reference; first ten
seconds excluded) measured:

| Build | Emulated cycles/s | Graphics tasks/s |
| --- | ---: | ---: |
| `224cc5a9` | 14,507,463 | 4.576 |
| Final blocks + NOP handling | 19,928,469 | 6.332 |

That is **37.4% more emulated cycles/s and 38.4% more graphics tasks/s** in this
Duke scene. This is the final gameplay result for this checkpoint, not an
additional percentage to add to the earlier broad-block result.

Perfect Dark's 20-million-instruction replay also matches exactly:
`6DF70AF2557F2C750643A18E9D8876C365A3CBF8F3E761BC06A95B657C29CF74`.
Reference medians were 1397.115/1411.675 ms and candidate 1378.601/1559.790 ms:
no consistent speed gain is established for that predominantly mapped workload,
and the candidate mean is 4.6% slower in this small sample. The replay harness
was extended to dispatch TLB and address exceptions through the same core
handlers as the CPU thread; the original harness stopped at a normal TLB miss
on the reference build as well.

## Validation and reproduction

The probe adds `--check-cpu-blocks` and `--bench-block-thread`. The latter runs the
actual CPU thread to an exact SYNC boundary, including Count/RANDOM reads and a
Count read in a branch delay slot. It checks both full state and all 512 history
entries against the reference assembly using the same probe harness.

The block checks compare complete serialized states against ordinary
`InterpretOpcode` execution, or verify zero mutation on rejection. Coverage
includes reserved encodings, zero registers, signed values, all supported
instruction groups, link-register aliasing, self-modifying code, both direct
segments, RAM/page boundaries, unsafe jump targets, instruction budgets,
RANDOM/WIRED, Count/Compare, every device-event boundary, interrupt-pending bits,
and trace/debug/step fallback. One million sampled encodings also check the
accepted subset against the existing opcode table's validity and cycle counts.

Completed checks: 609 full-state block cases; one million decode samples
(345,208 accepted); 709 existing-loop cases (176 accepted); 175 multiply-batch
cases; 368 COP1-usability cases; and the existing opcode-table suite. Explicit
watch-address-zero tracing and fast-idle-disabled settings both reject blocks
without changing state.

The updated real-thread fixture executes 11,000,001 instructions. Reference and
candidate have identical full-state hash
`96D35D3917651781BFF0C4CC30D90EF217B7BDA695EAF27A9BD5A6EC28BDAF77`
and history hash
`507E8C28AFAEBC3F055AED63FC2D3CA4D3A79BFE1F30EBFB801E8D5A6654AD2C`.
Its 597.864 → 326.291 ms result is synthetic and is not the gameplay claim.

The initial block UI Release build completed with zero errors (500 existing warnings).
That UI and its tested probe share `Ryu64.MIPS.dll` SHA-256
`a72426a0996cb94dc0cd2c8fe11c588f42c38b9e7a6d5a0286b96d7580958271`.

Example commands (from the repo root):

```sh
dotnet build tools/N64Probe/N64Probe.csproj -c Release --no-restore
dotnet tools/N64Probe/bin/Release/net8.0/N64Probe.dll --check-cpu-blocks
dotnet tools/N64Probe/bin/Release/net8.0/N64Probe.dll --bench-block-thread
taskset -c 6 dotnet tools/N64Probe/bin/Release/net8.0/N64Probe.dll \
  --bench-cpu-state .build-tmp/duke-playable-2026-09-20/cpu-state.bin \
  '/home/nichlas/roms/N64/Duke Nukem 64 (Europe).z64'
```

## Next work

Profile the completed block implementation before choosing the next change.
RAM word reads/writes remain candidates. Keep measuring the same snapshot and
preserve full-state equality; a faster synthetic loop alone is insufficient.

Final NOP-refined UI build: zero errors, 383 existing warnings. The UI and
`nop/` probe core both have SHA-256
`993f23788b385b7f6dd91baa043c257e8b9a8d341e30eb3e1247b8430ff7e3a4`.
The final thread fixture again matches both state/history hashes above.

## End-of-session profile and final build

The follow-up RAM-word change is recorded in
`n64-ram-word-access-2026-09-20.md` (`e4b307a1`). A managed compiled-block attempt
was measured and removed; see `n64-compiled-block-experiment-2026-09-20.md`.

A fresh 20-second profile of the restored final interpreter found 68.65% of
CPU-thread time outside RSP tasks and 31.35% inside RSP tasks (including RDP
rendering called from them). Leading leaf samples were the outer CPU loop
27.22%, CPU blocks 16.47%, multiply batches 5.44%, textured triangles 5.34%,
and word writes 4.95%. Inlining still attributes work to its visible caller.
Profile and final control-run artifacts: `.build-tmp/duke-final-2026-09-20/`.

The final rebuilt UI and probe share core SHA-256
`3cc33cbc953b2f55d61812977927b49886606a4d6e3abe5e2bfbf7bf01c98e60`.
Its assembly version stamp is `e4b307a1`; the different digest from the first
RAM build also reflects that rebuilt version metadata. No experimental compiled
block methods are present in this final binary.

The probe now supports `N64_PROBE_MOVE_INPUT=1` for a gameplay smoke run: move
forward at wall seconds 10–44, turn at 45–59, then release the controls. This is
headless test input and does not modify the application's input mappings or the
user's source savestate.

### Final combined control run

Four fresh 60-second runs used order `224cc5a9`, current, current, `224cc5a9`,
pinned CPU 6, with the first ten seconds of each excluded. No profiling or build
ran alongside them. Both versions started from the identical saved scene.

| Build | Individual graphics tasks/s | Combined graphics tasks/s | Combined cycles/s |
| --- | --- | ---: | ---: |
| Start of session (`224cc5a9`) | 3.941, 4.143 | 4.042 | 12,814,617 |
| Final (`e4b307a1`) | 5.703, 5.511 | 5.607 | 17,705,348 |

Weighted by each measured interval, the final result is **38.7% higher graphics
throughput and 38.2% higher emulated cycles/s**. All four runs reported zero
unknown opcodes. These are headless graphics-task rates, not displayed FPS, and
the game remains well below full speed. This combined comparison supersedes
adding together gains from the individual experiments. Raw samples and the
calculation are in `results.json` and `summarize.py` in the final artifact folder.

Final validation on the rebuilt binary:

- All 609 CPU-block cases passed, plus one million decoder samples (345,208
  accepted), including state, rejection, event boundaries and self-modification.
- Duke ran for 90 seconds with forward movement and camera turning. Captured
  images show movement toward the fence with the level, weapon and HUD intact.
  The run completed with zero unknown opcodes.
- Perfect Dark completed a 45-second smoke run with zero unknown opcodes; its
  final image shows the "Choose Your Reality" menu. This is a smoke check, not
  a claim of complete menu correctness or improved Perfect Dark speed.
- The user's Duke slot 1 remains unchanged, SHA-256
  `1c18b38b54947268807704d028f281c9c889ce295bac12640a84248677d2730a`.
