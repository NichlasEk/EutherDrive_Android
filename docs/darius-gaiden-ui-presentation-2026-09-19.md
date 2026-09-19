# Darius Gaiden: Linux UI presentation handoff

2026-09-19, following `d3d44e9b`. The user reports missing/frozen graphics
despite improved core timings and correct HUD colors. They also report that
waiting and retrying helps. That is compatible with warmup/JIT contributing,
but it does not establish warmup as the sole cause.

## Presentation defects addressed

The prior headless replay tests did not exercise these UI paths:

1. Darius presentation tried to acquire `_coreAudioLock` for only 3 ms.
   If emulation/audio held it longer, the UI abandoned the picture. A frame
   can therefore finish quickly enough on average while the UI still misses it.
2. `PresentPendingFrame` released the queued flag before rendering. A producer
   arriving during rendering could post a callback, then the consumer posted
   another callback for the same pending work. Reading then clearing the
   pending core also raced with producer publication.
3. Darius used an additional delayed presentation clock and background-priority
   dispatch on top of the already speed-locked emulation loop. Duplicate
   callbacks could advance that delayed clock further into the future.

The emulator now publishes a copied Darius framebuffer while it already owns
the core lock. The UI copies from that published buffer under a separate,
short copy-only lock, with dimensions/frame ID/core identity kept together.
It does not reacquire the running core's lock. Paused redraws refresh the
published image directly while the core is stopped.

A tested `LatestPresentationRequest<T>` keeps callback ownership until the
consumer finishes and coalesces new work into one follow-up. The shared posted
presenters use this queue; only Darius's framebuffer handoff changes. Darius's
extra delayed clock is removed, and presentation uses render priority like
the other posted presenters. CPU execution, audio generation, adaptive core
rendering and game state serialization are unchanged.

## Verification

```sh
dotnet run --project tools/DariusPresentationChecks -c Release
dotnet build EutherDrive.UI/EutherDrive.UI.csproj -c Release --no-restore -m:1
```

The production helper files are linked into the standalone test project:

- Deterministic producer-during-render test: no duplicate post; latest owner wins.
- 10,000 concurrent request publications: latest request delivered and queue drained.
- Core-lock contention: old 3 ms acquisition fails; published image is still readable.
- 1,000 concurrent framebuffer publications: copied pixel values match the frame ID,
  no tearing, latest frame retained; stale owner rejected and clear invalidates it.
- Five repeated full test runs passed. Release UI build passed with existing warnings.

A bounded Xvfb UI cold-start test ran the actual window and reached the animated
intro (screenshot `.build-tmp/darius-ui-display-check/frame.png`). It **fell back
from OpenGL to Bitmap**, so this is not a hardware-OpenGL validation. It used
isolated settings and did not interact with the user's running window.

A second actual-UI test explicitly used Bitmap, loaded a **copied slot 1** via
F8, and captured two different gameplay images two seconds apart. After the
intentional load/warmup pause, seven consecutive intervals recorded 59–60
successful surface submissions each and maximum submission gaps of
20.4–23.2 ms. The pause itself produced a 1417.5 ms gap; it is not hidden or
claimed fixed. This is an actual UI-path smoke test, not a controlled old/new
GPU benchmark. Artifacts: `.build-tmp/darius-ui-display-check/presentation.log`,
`slot1-a.png`, and `slot1-b.png`. The original savestate SHA-256 remains
`3213841b754a6e4005dca61de9b358378bef4546eb3f1f157c59ca2f4b21d37f`.

With `EUTHERDRIVE_LOG_VERBOSE=1 EUTHERDRIVE_UI_PROFILE=1`, the UI now additionally
reports `present_submit` and `present_gap_max_ms` per profiling interval. These
count successful framebuffer submissions to the rendering surface, not physical
monitor scanout. `EUTHERDRIVE_TRACE_GL=1` can separately show OpenGL render/upload
progress. Do not equate core FPS alone with smooth presentation again.

## User retest

The Release UI binary is rebuilt. The existing command remains valid:

```sh
dotnet run --project EutherDrive.UI -c Release --no-build -- /home/nichlas/roms/MAME/TAITO/dariusg.zip
```

Close the old process and restart so it loads the new UI assembly. Check both
cold start and saved gameplay on the user's real OpenGL backend. Complete
stutter removal and original-hardware graphics accuracy are not certified.
