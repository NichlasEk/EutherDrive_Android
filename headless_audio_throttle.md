# Headless audio throttle (för att undvika “emu rusar, ljud hinner inte med”)

## Problem
I headless kan emulationen gå så snabbt att ljud‑bufferten byggs upp för fort eller töms i ojämna klumpar. Det kan ge stutter, underruns, och “hackigt” ljud.

## Lösning
Headless kan nu **throttle:a** när audio‑bufferten är för full:
- Om `AudioEngine.BufferedFrames` > target → headless väntar kort tills bufferten går ner.
- Detta håller audio och emu i bättre takt.

## Env vars
- `EUTHERDRIVE_HEADLESS_AUDIO_THROTTLE=1`
  - Default: ON (stäng av med `0` om du vill max‑speed)
- `EUTHERDRIVE_HEADLESS_AUDIO_TARGET_MS=120`
  - Target buffer size i ms (default 100ms)

## Exempel
```
EUTHERDRIVE_HEADLESS_AUDIO=1 \
EUTHERDRIVE_HEADLESS_AUDIO_THROTTLE=1 \
EUTHERDRIVE_HEADLESS_AUDIO_TARGET_MS=120 \
dotnet run --project EutherDrive.Headless -- /home/nichlas/roms/quackshot.md 600
```

## Tips
- Om det fortfarande hackar: prova högre target (t.ex. 160ms).
- Om det känns segt: sänk target (80–100ms) eller stäng av throttle.
