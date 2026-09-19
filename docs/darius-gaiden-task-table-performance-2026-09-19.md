# Darius Gaiden: measured task-table overhead reduction

Linux, 2026-09-19. Baseline is the HUD-corrected `65caeb35` runtime.
The user requested another measurement-driven attempt at residual stutter.

## Finding and change

A sampled CPU profile of 1,800 frames from user slot 2, excluding the
`WarmUpRuntimeFromCurrentState` call path, attributed 34.17% of sampled time
to `TaitoF3MainBus.TryWriteByte` (including inlined helpers). The expensive
path includes diagnostic task-frame detection: each candidate RAM write
may search 32 live scheduler entries, previously using four general-purpose
byte-bus lookups per entry.

The accepted change reads each big-endian 32-bit entry directly from the
fixed work-RAM slice instead. No lookup is cached. Partially written pointers,
slot order, observed-stack precedence, mirrors and diagnostic output remain
unchanged. The table at `0x4066b8..0x406737` is ordinary RAM; `PeekLong`
had no device/read-counter side effects there. No CPU cycles, audio timing,
frame-skip policy or UI presentation policy were changed.

The existing exhaustive diagnostic test passes all **72,832 cases**.
The HUD decoder test still passes all **98,308 cases**.
All four independent M68000 stack/jump trace-switch combinations pass.
Release builds of the probe and Linux UI pass (existing warnings remain).

## Controlled replay comparison

Both copied user slots replay for 900 frames with fire held. Normal runtime
warmup and .NET tiering remain enabled. Adaptive rendering is disabled for
this comparison so every frame is rendered and full video hashes can be
compared. Each slot runs baseline/new/new/baseline in separate processes;
no concurrent build or profiler. Wall-time noise from the desktop is not
eliminated. These are `RunFrame` times, not monitor-presentation intervals.

| Slot | Variant | Total ms | p99 ms | Max ms | Frames over ~16.97 ms |
| --- | --- | ---: | ---: | ---: | ---: |
| 1 | Baseline | 11360.730 | 16.202 | 17.578 | 2 |
| 1 | New | 8809.139 | 17.065 | 18.775 | 10 |
| 1 | New | 8871.731 | 11.433 | 17.959 | 3 |
| 1 | Baseline | 11425.778 | 16.608 | 22.946 | 7 |
| 2 | Baseline | 13393.218 | 17.328 | 23.307 | 16 |
| 2 | New | 8538.191 | 10.721 | 14.631 | 0 |
| 2 | New | 8591.526 | 10.745 | 11.085 | 0 |
| 2 | Baseline | 13028.902 | 19.245 | 25.606 | 29 |

Across the two runs of each variant:

- Slot 1 mean: **12.659 -> 9.823 ms**, about **22.4% shorter**.
- Slot 2 mean: **14.679 -> 9.517 ms**, about **35.2% shorter**.
- Slot 2 over-budget frames: **45 -> 0 out of 1,800**.
- Slot 1 over-budget frames: **9 -> 13 out of 1,800**. Its noisy tail did not
  improve consistently despite the repeatable mean-time reduction. Do not
  describe this as eliminating every stutter.

Removing the first 60 replay frames retains the improvement: slot 1 new means
9.724/9.790 ms versus 12.424/12.488 ms; slot 2 new means 9.506/9.568 ms versus
14.901/14.461 ms. This is not just a loading/warmup improvement.

All video/audio sequence hashes and final serialized state hashes matched
within each slot across all four runs:

| Slot | Stream | SHA-256 |
| --- | --- | --- |
| 1 | Video | `1504CE8EC167B1C5B7FF1E7A419A937467908CC41BE85B00887C20EF35742D3A` |
| 1 | Audio | `0189C5E250E3C38FD9B19F41C1E6E4A1E85A399BF2D88D0E3F6AA289DDF936BF` |
| 1 | State | `12BC4DDC8669B029873E4852FC249FF79024B23AA7381B994CE69588F93155A4` |
| 2 | Video | `07DCC3247F812A62C05DAC85011303D104F0D7F317DFABAC1BF360B1B7AF56AB` |
| 2 | Audio | `ECD63C121EBB57A2771994E0A0D24B054FBBE2145B49E7FE5C48220236E7F6E6` |
| 2 | State | `0A604A8D97B9F6CAAF5B1AA2C0ECF2CE8F746B88918E6F625A1AEE5472DD9959` |

The final rebuilt numeric-buffering probe replayed both slots again: all three
hashes still matched, and each CSV contained 900 sequential, valid timing rows.

## Measurement details and continuation

An additional slot-2 baseline/new/new/baseline comparison used default adaptive
rendering. Across 1,800 frames per variant, baseline skipped **160** renders
(7/153 per run), new skipped **1** (0/1). Over-budget frames were **346 -> 4**.
The baseline's large run-to-run variation is important; do not treat this as
a guaranteed fixed percentage improvement. Audio and final state hashes
matched all four runs; video hashes are intentionally not compared when
wall-time-dependent frame skipping is enabled. Logs:
`.build-tmp/darius-task-table-default-ab.log` and its per-run CSV files.

`EUTHERDRIVE_DARIUS_PROBE_TIMING_CSV=/path/to/output.csv` enables per-frame
`frame,totalMs,cpuMs,renderMs` output from `tools/DariusProbe`. The final tool
buffers numeric records and formats/writes them after replay. The balanced
runs above used the same earlier string-buffering probe in both variants;
neither variant wrote the CSV during replay. CPU/render columns are zero
when the core's timing is disabled (e.g. adaptive rendering off and phase
tracing off); total frame timing is always external and valid.

Do not enable core phase/render tracing for a normal-warmup latency claim:
those diagnostic flags deliberately disable runtime warmup. The earlier
scanline-OSR investigation exercised a colder path than normal playback.

Local artifacts, not committed: `.build-tmp/darius-task-table-ab.log`,
per-run logs/CSVs with prefix `.build-tmp/darius-task-table-slot`, and
`.build-tmp/darius-slot2-pacing-profile.speedscope.json`.

Original user savestates are read-only; use the preserved copies described
in [the HUD report](darius-gaiden-hud-stutter-2026-09-19.md). Interactive UI
pacing still needs user confirmation; no claim of complete stutter removal.
