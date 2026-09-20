# N64 CPU: compact instruction operands

Baseline `3c60c9a9`. Artifacts: `.build-tmp/n64-cpu-decode-2026-09-20/`.

`OpcodeDesc` previously expanded each instruction word into six operand fields
before calling its handler, passing a 16-byte struct through dispatch. It now
holds only the 4-byte instruction word. Read-only properties extract register
indices, immediate and jump target where the handler uses them; the small
getters are eligible for normal JIT inlining. This removes upfront work for
unused fields and reduces the argument passed to every handler.

All repository consumers read the descriptor rather than modifying it. The
instruction handlers, opcode matching, cycle counts, interrupts, delay slots,
and save format are unchanged. The core, probe and UI are rebuilt together
because public operand fields have become properties.

## Measurements

The Duke gameplay input is
`.build-tmp/duke-game-speed-2026-09-19/later-cpu-state.bin` with SHA-256
`ed91ffbf0b9267ee65eccd58a43071f5920ebfa64d149cb22d21fe08718d8f85`.
ROM: `/home/nichlas/roms/N64/Duke Nukem 64 (Europe).z64`.

The fixed-work replay runs 20,000,001 instructions with real cartridge data
and interrupt service. Each process warms up three times and measures five
runs. Baseline/candidate/candidate/baseline processes run sequentially on CPU 6.

| Duke replay | First process median | Second process median |
| --- | ---: | ---: |
| Baseline | 1434.145 ms | 1456.832 ms |
| Candidate | 1392.018 ms | 1379.893 ms |

Averaging process medians gives **4.3% higher instruction throughput**, or
4.1% less host time. All runs have full-state SHA-256
`822508CB053F27CC10DD71D698382B25AD49446FE0A72228BC4EAA12C019FFD7`.

The mixed real-CPU-thread benchmark (10,000,001 instructions; three warmups,
six measured runs) goes from 740.359 to 714.922 ms median, about 3.6% higher
throughput. Both builds retain full-state hash
`41CBC873ED5B0FE3C41D49EBDC5166F7D6197F0A35B5EDA74670ABBCD86B0617`.

These are CPU workload measurements, not a claim of equal displayed FPS gains
or real-time game speed. The Duke replay excludes CPU-thread outer-loop
bookkeeping; the mixed benchmark includes it.

## Validation

- 852,358 opcode cases agree with the ordered instruction table (16,617
  rejected encodings).
- 368 COP1 usability cases pass, including lazy-FPU retry and delay-slot faults.
- 709 full-state CPU-loop comparisons pass (176 accepted loops).
- 270 idle-event comparisons pass (140 accepted batches), plus matching
  2-million-instruction interrupt-serviced replays with batching on/off.
- Trace/debug output and unsupported-instruction behavior match the baseline
  assembly, with full-state hash
  `39184AD36017CA8FF42E07A884A6FB6814DFBF6FC5A36345973E0BF65AE1F40A`.

```sh
dotnet tools/N64Probe/bin/Release/net8.0/N64Probe.dll --check-cop1-usability
dotnet tools/N64Probe/bin/Release/net8.0/N64Probe.dll --check-cpu-loops
EUTHERDRIVE_TRACE_N64_SM64_DISPATCH_WINDOW=1 \
  dotnet tools/N64Probe/bin/Release/net8.0/N64Probe.dll --check-cpu-diagnostics \
  .build-tmp/n64-cpu-decode-2026-09-20/reference/Ryu64.MIPS.dll
```

Linux UI Release builds successfully. The rebuilt UI and tested probe use
MIPS core SHA-256
`56ae6fb56509317abc72198a60b83a5ce7aa32e968947ad1aa10b2b4764ba632`.
Restart the application to use the new core.
