# Mario real-time floor, November 8

The target is sustained full speed in demanding gameplay, with enough margin
for audio and presentation. An average above 100% does not meet that target.
A measured replay also cannot establish performance in every level or under
arbitrary host contention.

## Short-window observation

`N64_PROBE_SM64_TIMING=1` enables a probe-only sample at each existing Joybus
controller read: guest cycles, wall clock, emulation-thread CPU time and
produced audio duration. Samples stay in memory until the CPU stops, then
are written to `timing.json`. No additional input, framebuffer reads, hashes
or file writes occur during the run. The normal desktop has no such observer.

Run the existing interleaved benchmark with this environment variable and
both probes built from the same measurement code. The flag is captured in
its environment manifest. Then inspect each run with:

```sh
python3 tools/N64Probe/report-sm64-timing.py RUN/timing.json --start 95 --end 120
```

The report uses overlapping windows of at least 0.25, 1 and 5 guest seconds,
with endpoints at actual controller reads. It reports cycle and audio
production rates separately, minimum, fifth percentile, median and counts
below 100%. These counts overlap; they are not a fraction of playback time.
No scheduling delay or GPU wait is subtracted. This measures core throughput,
not physical display frame times or audible playback.

Existing five-second correctness checkpoints take hashes and frame captures.
Their work remains inside raw wall timings. A separate group excludes windows
spanning those captures, identified by the adjacent `checkpoints.json` file;
this avoids mistaking the test's capture work for an ordinary gameplay dip.
No complete five-second window in this replay avoids all captures.

## Validation and present limitations

The new probe passes an interleaved comparison against the accepted COUNT
rollover core: six checkpoints, six images, generated audio/input and complete
final RAM/CPU/device/GPU state match exactly. The two dense captures also
match all 880 guest-cycle/audio-duration sample pairs.

The summary calculation was checked against a constant 100% trace, an injected
half-second host stall, exact checkpoint boundaries, interval filtering and
rejection of a range too short to contain a complete window. Python syntax
checks, Release build and whitespace checks pass.

Local evidence: `.build-tmp/n64-mario-2026-11-08/timing-probe/` and
`timing-abba/`, including artifact manifests and each candidate's
`timing-summary.json`.

This run had concurrent host workloads, including shadPS4. The old reference
itself averaged only 89.85–90.42% audio production speed, compared with
104.84–105.15% in the earlier run. The observer variant averaged 2.67% lower
wall throughput and 1.67% lower thread-CPU throughput than this reference.
This is not evidence of a speedup or a clean observer-overhead estimate;
host activity and probe layout differ. Do not compare these absolute floors
to earlier measurements as if only the emulator changed.

Under this observed load, one-second windows free of checkpoint capture
bottom out at 68.92–74.25% cycle speed and 68.10–73.37% audio production.
Both runs put the worst one-second window at guest 117.65–118.68. Shorter
capture-free windows reach 57.56–60.75% cycle speed. These diagnostic values
confirm that five-second averages conceal dips; they are not new desktop
performance claims. The accepted desktop launcher remains unchanged.

## Acceptance for further optimizations

Compare the same short-window observer in both variants, with identical
state/input/graphics/audio checks and recorded host conditions. Target the
repeatable 117.65–118.68 interval, then broaden coverage to other demanding
scenes and transitions. Retain raw and capture-free minima, not just means.
A candidate must also pass actual audio-playback underrun checks and visible
play testing. Full-speed gameplay with reliable audio remains unfinished.
