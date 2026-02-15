# YM2612 audio fix (SystemCycles-driven)

**Summary**
The key fix was to drive YM2612 ticking from `SystemCycles` increments instead of frame/audio callbacks. This eliminated large bursts of YM samples and the resulting time‑stretching (“kraaaaaa”) artifacts, and restored correct timing for GEMS/DAC effects.

**What changed**
- `md_main.AdvanceSystemCycles(long cycles)` now forwards each increment to YM via `g_md_music?.YmAdvanceSystemCycles(cycles)`.
- `md_music` exposes `YmAdvanceSystemCycles(long cycles)` and routes it to the YM core.
- `JgYm2612` converts `SystemCycles` to YM ticks (`cycles / 6`) and feeds the ring buffer immediately.
- `EnsureAdvanceEachFrame()` is no longer used for YM timing (prevents double‑ticking).

**Files touched**
- `EutherDrive.Core/MdTracerCore/md_main.cs`
- `EutherDrive.Core/MdTracerCore/md_music.cs`
- `EutherDrive.Core/MdTracerCore/md_music_ym2612_jgenesis.cs`

**Why it matters**
YM2612 timing must be driven by CPU time, not by audio callback timing. When it is only advanced per frame or per resampler batch, the chip outputs samples in bursts, which stretches or smears transient sounds. Ticking on each `SystemCycles` increment keeps FM/DAC behavior aligned with the rest of the system.
