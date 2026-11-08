# N64 audio playback diagnostic

Build with `dotnet build tools/N64AudioPlaybackProbe -c Release -m:1
-p:N64LiveGpu=true -p:N64PerformanceProbe=true -p:N64RdpJournalCapture=false`.
Pass a normalized, original Super Mario 64 USA ROM, a GPU core state at guest
second 60, a new output JSON path, and `current`. Private fixtures stay local.
Run with the same GPU library/environment as the playable launcher.

This models the desktop's 60 Hz N64Adapter polling, 44.1 kHz stereo output,
256-frame AudioEngine batches, 16384-frame ring, 8820-frame target, and
4410/8379-frame submission hysteresis. `all` is an experimental submission
policy, not a production recommendation. The null sink excludes the real
sound driver, Avalonia rendering, and listening validation.

The host JSON records producer/consumer counts, submission skips, buffer
occupancy, engine underruns and drops. `.guest.json` records CPU-thread
checkpoints at 65, 70, 75, 80, 85 and 90 emulated seconds, including RAM hashes
and generated audio counts. Compare timings between **70 and 90**, excluding
JIT warmup, and require equal guest checkpoints before interpreting speed.
Host underrun counts are sampled approximately once per wall second and
are not exact checkpoint deltas. They are engine events, not measured
physical speaker gaps. PCM hashes and full device state are not checked by
this tool; use N64Probe's full-state replay for core changes.

For a wait-policy experiment, interleave separate processes with
`EUTHERDRIVE_N64_RUNFRAME_WAIT_MS=12` and `=0`. Never benchmark while builds,
other test jobs, or an interactive emulator session are running. Record
artifact hashes and do not infer audible success from this diagnostic.
