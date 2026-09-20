# N64 endian word access and measured CPU work

Baseline: `d1dcc2f8`, including Duke's sprite/depth fixes and the RANDOM shortcut.
Final artifacts: `.build-tmp/duke-register-words-2026-09-20/`.

## Retained implementation

Use `BinaryPrimitives` for direct RDRAM 32-bit reads/writes, 64-bit reads,
physical instruction-word reads, and the internal 32-bit device-register buffer
helpers. This replaces individual byte loads/stores and shifts with the runtime's
endian primitives. The existing address ranges, RAM-end checks, null/short-buffer
behavior, write epochs, tracing fallback and device access order remain intact.
No new instruction cache, instruction skipping, timing approximation or save
format is involved. `System.Memory` 4.6.3 supplies these APIs while retaining the
core's `netstandard2.0` target.

## Fixed-work evidence

The same 20-million-instruction Duke replay was run with the original core,
RAM-word-only change, and final RAM/register-word change in B/W/C/C/W/B order.
Each process had three warmups and five measured samples, CPU 6 affinity. The
probe harness and runtime dependencies were identical, with only the core DLL
exchanged. Table entries average the two process medians.

| Variant | Elapsed ms | Host CPU cycles | Retired host instructions |
| --- | ---: | ---: | ---: |
| Baseline | 1241.831 | 3,532,659,387 | 8,070,769,891 |
| RAM words only | 1208.844 | 3,410,036,266 | 7,589,179,984 |
| RAM and register words | 1202.052 | 3,416,370,640 | 7,533,855,019 |

The retained version used **3.20% less elapsed time, 3.29% fewer host CPU cycles,
and 6.65% fewer host instructions** in this replay. These are measurements of
the probe's CPU workload, not displayed game FPS. The register helper addition
has only a small effect relative to the RAM-only variant; the data does not
establish a separate wall-time win for it.

The 11-million-instruction CPU-thread fixture also retained identical state and
history. Its RAM-only variant reduced retired host instructions by about 8%.
Wall-clock samples fluctuate considerably; the replay's complete machine state
is used to check equivalent work instead of accepting a shorter run alone.

## Actual game measurement

The final four 45-second Duke slot-1 runs used B/C/C/B order and excluded the
first ten seconds. All completed with zero unknown opcodes.

| Version | First run, graphics tasks/s | Second run | Combined rate |
| --- | ---: | ---: | ---: |
| Baseline | 5.513 | 6.065 | 5.789 |
| Final | 5.994 | 5.770 | 5.882 |

The combined rate is **1.62% higher**, but the two pairs disagree in direction.
This is not a reliable general FPS increase. Graphics tasks/s is the probe's
throughput measure, not a count of displayed frames. Earlier RAM-only series
also varied: 5.873 to 6.001, then 6.166 to 6.106. Keep the fixed-work CPU saving
separate from claims about playability.

## Validation

- 131,278 word-access and 131,281 opcode-fetch operations match the old core,
  including values, exception details, framebuffer write epochs and full state.
- 36,512 doubleword reads cover endianness, unaligned accesses and boundaries.
- 6,845 rendering/interrupt checks pass.
- The previous graphics fixes pass 1,092 intensity-alpha, 960 rectangle-depth
  and 24 blender cases.
- 108 DMA cases match the old core, including serialized state.
- Word accesses also match with a trace watch address of zero enabled.
- Duke replay state: `F123855881E70B914F7AACAE83BB43BB44B73B9AEDDCC510EF06A3FA3F76275C`.
- Perfect Dark replay state: `6DF70AF2557F2C750643A18E9D8876C365A3CBF8F3E761BC06A95B657C29CF74`.
- CPU-thread state: `96D35D3917651781BFF0C4CC30D90EF217B7BDA695EAF27A9BD5A6EC28BDAF77`.
- CPU-thread history: `507E8C28AFAEBC3F055AED63FC2D3CA4D3A79BFE1F30EBFB801E8D5A6654AD2C`.

## Counter support for future work

`N64_PROBE_HOST_COUNTERS=1` optionally enables Linux x64 `perf_event_open`
counters in `--bench-cpu-state` and `--bench-block-thread`. The counted interval
excludes state loading, hashing and serialization. CPU-thread counters include
newly created child threads. No machine-wide permissions, frequency governor or
runtime settings are changed. The default probe and emulator do not open counters.
Unsupported platforms or unavailable counters report an error only when opted in.
Multiplexed counters are rejected rather than comparing unscaled partial totals.

The helper differences cumulative readings: resetting a parent event alone does
not clear inherited child totals. The early `thread-*` counter logs in the final
artifact directory were from that rejected prototype and must not be used.
Use `valid-thread-*` and `duke-counters-*`, which contain interval differences
and enabled/running times. The table above uses `duke-counters-*`. Counter medians
for even sample counts select the upper middle sample; elapsed thread medians
average the two middle samples.

Example, with a previously extracted raw CPU snapshot:

```sh
N64_PROBE_HOST_COUNTERS=1 taskset -c 6 dotnet tools/N64Probe/bin/Release/net8.0/N64Probe.dll \
  --bench-cpu-state .build-tmp/duke-playable-2026-09-20/cpu-state.bin \
  '/home/nichlas/roms/N64/Duke Nukem 64 (Europe).z64'
```

## Rejected experiments

- Reusing the runtime-loop recognizer's fetched RAM opcode passed 709 full-state
  loop comparisons, but actual Duke throughput fell from 6.260 to 5.861 tasks/s.
  Removed; patch and logs in `.build-tmp/duke-fetch-2026-09-20/`.
- Direct byte/halfword RAM wrappers passed 262,556 differential operations with
  tracing off and on, but added no sustained game benefit. Removed; prototype
  and logs in `.build-tmp/duke-small-access-2026-09-20/`. Its `reference` DLL is
  the RAM-word-only version, while `baseline` is the original core.
- `.build-tmp/duke-word-load-2026-09-20/` holds the RAM-word-only investigation,
  including early whole-process counters. Those counters include unrelated
  startup/validation work and are not the basis of the retained measurements.

Release UI build completed with 0 errors and 501 warnings. The UI core DLL
matches the measured and validated candidate, SHA-256
`747d86240c2807e98fb1b56f2890ef88299c97fcc9e3c8696bbc7497da439d38`.
