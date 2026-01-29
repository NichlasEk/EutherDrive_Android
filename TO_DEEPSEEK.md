# TO_DEEPSEEK — Audio Instrumentation & Next Steps

Du ska ta över felsökning av ljudet. Målet är att identifiera varför ljudet fortfarande kan låta sprucket/bursty/unclean trots PLL+buffer och YM‑resample. Följ detta som checklista och rapportera tydligt.

## 0) Översikt av nuvarande ljudmodell (kort)
- SystemCycles = M68K‑tid (2x‑buggen är fixad).
- Audio produceras per SystemCycles: frames = deltaCycles * (AudioSampleRate / M68kClockHz) + accumulator.
- YM core kör internt ~53.2 kHz och resamplas linjärt till 44.1 kHz innan mix.
- PSG är 44.1 kHz.
- Mix (YM+PSG) → mastervolume → (valfri LP) → AudioEngine ringbuffer → sink.
- Catch‑up är **av** som default (kan slås på med env). PLL finns som mjuk korrigering.

## 1) Alla befintliga instrumenteringar (env flags)

### Audio timing & buffer
- `EUTHERDRIVE_TRACE_AUDIO_CYCLES=1`
  - Loggar per sekund: deltaCycles, expectedPerFrame, ratio.
- `EUTHERDRIVE_TRACE_AUDIO_QUEUE=1`
  - Loggar per sekund: buffered, target, rateScale, acc.
- `EUTHERDRIVE_TRACE_AUDIO_STATS=1`
  - AudioEngine stats (underrun/drops/produce/consume) om aktiverad i AudioEngine.
- `EUTHERDRIVE_TRACE_AUDIO_BUFFER=1`
  - Loggar buffer/frames i adaptern (debug elastic audio).

### Audio levels
- `EUTHERDRIVE_TRACE_AUDLVL=1`
  - RMS + min/max, per sekund.
- `EUTHERDRIVE_TRACE_AUDIO_STATS=1` + `TraceAudioLevel`
  - PSG/YM peaks och non‑zero counts.

### YM timing
- `EUTHERDRIVE_TRACE_YM_TIMING=1`
  - Loggar YM2612_UpdateBatch timing.
- `EUTHERDRIVE_TRACE_YM_WRITE_TIMING=1`
  - Loggar YM register write timing (input‑driven bursts).

### Z80 timing
- `EUTHERDRIVE_TRACE_Z80_FRAME_CYCLES=1`
  - Loggar Z80 cycles per frame.

### Headless
- `EUTHERDRIVE_HEADLESS_AUDIO=1` (krävs för YM timing i headless)
- `EUTHERDRIVE_LOAD_SLOT1_ON_BOOT=1` (autoload slot 1 i headless)

## 2) Viktiga tuning‑env vars

### PLL / buffer
- `EUTHERDRIVE_AUDIO_PLL=1`
- `EUTHERDRIVE_AUDIO_PLL_MAX=0.003` (±0.3%)
- `EUTHERDRIVE_AUDIO_TARGET_MS=120`
- `EUTHERDRIVE_AUDIO_BUFFER_FRAMES=16384` (test större)
- `EUTHERDRIVE_AUDIO_BATCH_FRAMES=128` (mindre batch)
- `EUTHERDRIVE_AUDIO_CATCHUP=1` (normal default OFF)

### YM / mix
- `EUTHERDRIVE_FM_VOLUME_DIVISOR=48` / `64` (lower is louder)
- `EUTHERDRIVE_FM_MIX_GAIN=0.85`
- `EUTHERDRIVE_AUDIO_LP_HZ=8000` (low‑pass)

### Region & cycles
- CPU cycles/line ska vara 488 (NTSC). UI startar nu på 488.

## 3) Hur du kör loggar (UI)

### Baslogg (tempo + buffer + underruns)
```
EUTHERDRIVE_TRACE_AUDIO_CYCLES=1 \
EUTHERDRIVE_TRACE_AUDIO_QUEUE=1 \
EUTHERDRIVE_TRACE_AUDIO_STATS=1 \
EUTHERDRIVE_AUDIO_PLL=1 \
EUTHERDRIVE_AUDIO_TARGET_MS=120 \
EUTHERDRIVE_AUDIO_PLL_MAX=0.003 \
EUTHERDRIVE_AUDIO_LP_HZ=8000 \
dotnet run --project EutherDrive.UI
```

### Logg med buffer debug
```
EUTHERDRIVE_TRACE_AUDIO_BUFFER=1 \
EUTHERDRIVE_TRACE_AUDIO_QUEUE=1 \
EUTHERDRIVE_TRACE_AUDIO_STATS=1 \
dotnet run --project EutherDrive.UI
```

### Headless sanity
```
EUTHERDRIVE_HEADLESS_AUDIO=1 \
EUTHERDRIVE_TRACE_AUDIO_CYCLES=1 \
dotnet run --project EutherDrive.Headless -- /home/nichlas/roms/quackshot.md 300
```

## 4) Vad du ska titta efter i loggar

### A) Underruns/drops
- `[AudioEngine] dropped …` eller underrun counters ökar.
- `[AUDIO-QUEUE] buffered` nära 0 → starvation.

### B) PLL instabilitet
- `[AUDIO-QUEUE] rateScale` pendlar kraftigt (t.ex. 0.995 ↔ 1.005). Ska helst vara stabil.

### C) Timing mismatch
- `[AUDIO-CYCLES] ratio` ≠ 1.000 → fel i SystemCycles eller M68kClockHz.

### D) YM burst / input‑driven writes
- `[YM-WRITE-TIMING]` clustera vid inputs → kan ge tonal modulations.

## 5) Hypoteser att testa
1) **Buffer starvation**: öka bufferFrames + target ms och se om sprickor försvinner.
2) **PLL för aggressiv**: sänk `EUTHERDRIVE_AUDIO_PLL_MAX` till 0.001.
3) **FM clipping**: höj `FM_VOLUME_DIVISOR`, sänk `FM_MIX_GAIN`.
4) **LP cutoff**: 7–10 kHz, se om harshness minskar.

## 6) Om du hittar fel
- Skriv ner exakt env‑kombination + vilka logglinjer som visar problemet.
- Ange vilken komponent som verkar orsaka: AudioEngine ringbuffer, PLL, YM resampler, eller UI pacing.
- Föreslå minst 2 konkreta patch‑förslag.

## 7) Rapport
I slutet: **sammanfatta allt i `TO_CODEX.md`** (kort, tydligt, med logg‑utdrag och rekommendationer).

## 8) Overflow/underrun sanity (viktigt)
Om användaren skriver **"overflow"**:
- Det betyder oftast ringbuffer *överfylls* (producer snabbare än sink), vilket kan ge stutter/frys.
- Leta efter loggar som:
  - `[AudioEngine] dropped ... samples`
  - `PwCatAudioSink overflow count=...`

Åtgärder att testa (i ordning):
1) Minska produktion per pull:
   - `EUTHERDRIVE_AUDIO_PULL_MAX_FRAMES=1024`
2) Öka sink‑batch:
   - `EUTHERDRIVE_AUDIO_BATCH_FRAMES=512`
3) Minska ringbuffer‑storlek (mindre latency, mindre backlog):
   - `EUTHERDRIVE_AUDIO_BUFFER_FRAMES=8192`

Be om logg med:
```
EUTHERDRIVE_TRACE_AUDIO_STATS=1
EUTHERDRIVE_TRACE_AUDIO_QUEUE=1
```
Och be användaren återge exakt overflow‑rad.

## 9) Pull‑mode update (viktigt)
Rapporter som säger “audio genereras bara ~18.6 gånger/sec” är **irrelevanta i pull‑mode**. I pull‑mode är audio producerad på **demand** från AudioEngine, inte 1x per frame.

### Ny instrumentering att använda i pull‑mode
Lägg fokus på:
- Hur ofta producer (pull) kallas per sekund
- Hur många frames som faktiskt returneras per sekund
- Om producer returnerar partial buffers (under‑delivery)
- Om safety‑loop slåss/inte hinner generera

Exempel på loggar att be om:
- `[AUDIO-PULL]` logg per sekund med:
  - requestedFrames
  - returnedFrames
  - bufferedSamples (intern pull‑queue)
  - pullLoops (hur många RunFrame per call)

### Slutsats
Om pull‑producer returnerar mindre än request → underruns i sink.
Om pull‑producer returnerar mycket mer än request → overflow/ringbuffer‑drop.

**Be alltid om pull‑mode‑specifika loggar** innan du drar slutsatser om “för få calls per sekund.”
