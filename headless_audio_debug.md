# Headless audio debug (YM timing)

## Last run (Aladdin + slot 1 autoload)

Command:
```bash
EUTHERDRIVE_LOAD_SLOT1_ON_BOOT=1 \
EUTHERDRIVE_HEADLESS_AUDIO=1 \
EUTHERDRIVE_YM=1 \
EUTHERDRIVE_TRACE_YM_TIMING=1 \
EUTHERDRIVE_TRACE_YM_WRITE_TIMING=1 \
EUTHERDRIVE_TRACE_AUDIO_STATS=1 \
dotnet run --project /home/nichlas/EutherDrive/EutherDrive.Headless -- /home/nichlas/roms/Aladdin.md 600
```

Expected:
- Auto-load savestate slot 1 via SavestateService
- Run 60 warm-up frames
- Collect YM timing + write timing + audio stats

Observed:
- Savestate load failed: `md_vdp` payload corrupt (array rank unreasonable for `g_game_cmap`).
- Headless then booted normally and later crashed in Z80.

## Notes
- Slot-1 autoload flag: `EUTHERDRIVE_LOAD_SLOT1_ON_BOOT=1`
- If UI slot-1 works but headless fails, resave slot-1 with the current build to refresh the savestate format.

## Last run (Quackshot, headless audio timing check)

Command:
```bash
EUTHERDRIVE_HEADLESS_AUDIO=1 \
EUTHERDRIVE_TRACE_AUDIO_CYCLES=1 \
dotnet run --project /home/nichlas/EutherDrive/EutherDrive.Headless -- /home/nichlas/roms/quackshot.md 300
```

Observed:
- Headless ran 300 frames and produced audio.
- Audio cycle trace still logs `ratio=2.000` because the trace prints raw SystemCycles, but audio generation now applies `EUTHERDRIVE_SYSTEM_CYCLES_SCALE=0.5` by default to compensate.

## Latest run (Quackshot, after SystemCycles fix)

Command:
```bash
EUTHERDRIVE_HEADLESS_AUDIO=1 \
EUTHERDRIVE_TRACE_AUDIO_CYCLES=1 \
dotnet run --project /home/nichlas/EutherDrive/EutherDrive.Headless -- /home/nichlas/roms/quackshot.md 300
```

Observed:
- `[AUDIO-CYCLES] deltaCycles=127856 expectedPerFrame=127840.9 ratio=1.000`
- Confirms SystemCycles now match M68K timebase without scaling.

## UI symptom: B-spam (sword) affects music tempo

- Observed: Spamming B (sword) causes large tempo shifts in music.
- Likely cause: YM timing still influenced by event-driven paths or uneven audio production.

### Trace focus to confirm
Enable these to correlate YM timing with input-driven register writes:
```bash
EUTHERDRIVE_TRACE_YM_TIMING=1
EUTHERDRIVE_TRACE_YM_WRITE_TIMING=1
EUTHERDRIVE_TRACE_AUDIO_STATS=1
```

What to check in logs:
- Do YM register writes cluster during B-spam? (write bursts)
- Does YM timing advance stay smooth when audio production is steady?
- Does audio buffer level oscillate strongly during B-spam?
