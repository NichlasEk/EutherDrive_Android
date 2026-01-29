# Audio Investigation Report for Codex - UPDATED

## Executive Summary
Audio suffers from **critically slow production rate** (only 31% of target) causing potential quality issues. UI has stable buffer with minimal underruns, but headless has constant buffer starvation. The core issue is insufficient audio generation frequency.

## Critical Finding: Production Rate Mismatch
- **Target rate**: 44100 frames/second
- **UI actual rate**: ~13701 fps (31% of target)
- **Headless actual rate**: ~6574 fps (15% of target)
- **Expected per frame**: ~735 audio frames per video frame
- **Actual per frame**: ~228 audio frames per video frame (31% of expected)

## Test Results

### 1. UI Mode (Stable but Slow)
```
[AudioStats] t=6,2s rate=44100 ch=2 batch=256 buf=5036/8192 minBuf=259 maxBuf=5292 
prod=64172 cons=59136 drift=5036 drop=0 underruns=0/0 
prodFps=13701,2 consFps=13701,2 genMs=1027,19 genFrames=14080 
ticks=55 mode=timed fpt=256,00 fptMin=256 fptMax=256 clamp=0
```
- **Buffer**: Healthy at 5036/8192 (62% full)
- **Underruns**: Only 1 initial underrun, then stable
- **Production**: 13701 fps (31% of 44100 target)
- **Mode**: `timed` (audio engine driven by timer)

### 2. Headless Mode (Constant Starvation)
```
[AudioStats] t=1,0s rate=44100 ch=2 batch=1024 buf=0/8192 minBuf=0 maxBuf=479 
prod=6615 cons=6615 drift=0 drop=0 underruns=120/122880 
prodFps=6574,1 consFps=6574,1 genMs=0,00 genFrames=0 
ticks=26 mode=mixed fpt=0,00 fptMin=0 fptMax=0 clamp=0
```
- **Buffer**: Empty (0/8192)
- **Underruns**: Constant (120 events, 122880 frames)
- **Production**: 6574 fps (15% of target)
- **Mode**: `mixed` (different audio engine mode)

### 3. Audio Timing (Correct)
```
[AUDIO-CYCLES] deltaCycles=127856 expectedPerFrame=127840,9 ratio=1,000
```
- **Timing ratio**: 1.000 (perfect)
- **Cycles per frame**: 127856 (correct for NTSC)

### 4. PLL Stability (Good)
```
[AUDIO-QUEUE] buffered=5036 target=5292 rateScale=1,0000 acc=0,22
```
- **rateScale**: Stable at 1.0000
- **Buffer vs target**: Close (5036 vs 5292)

## Root Cause Analysis

### Primary Issue: Insufficient Audio Generation Calls
The audio generation formula is correct:
```
frames = deltaCycles * (AudioSampleRate / m68kClockHz)
```
With `deltaCycles=127856` and `m68kClockHz=7670454`:
- Expected: `127856 * (44100 / 7670454) ≈ 735 frames per video frame`
- At 60 fps: `735 * 60 = 44100 fps` (perfect)

But actual is only 13701 fps, suggesting:
1. **Audio not generated every frame**: Only ~18.6 generations/sec vs 60 frames/sec
2. **`GenerateAudioFromSystemCycles()` not called consistently**
3. **Possible frame skipping in audio generation path**

### Secondary Issues:
1. **Headless vs UI disparity**: Different audio engine modes (`mixed` vs `timed`)
2. **Headless throttle ineffective**: Still shows buffer starvation
3. **Production rate variability**: 8.3% → 15% → 31% in different tests

## Code Analysis

### Problematic Areas:
1. **`MainWindow.axaml.cs:SubmitAudio()`**: May not be called every frame
2. **`GenerateAudioFromSystemCycles()`**: Timing of calls inconsistent
3. **Headless audio generation**: Missing proper audio pipeline
4. **Audio engine modes**: `timed` vs `mixed` behavior differences

### Audio Generation Formula (Correct):
```csharp
// In GenerateAudioFromSystemCycles()
_audioFrameAccumulator += (deltaCycles * SystemCyclesScale) * 
                         AudioCyclesScale * rateScale * 
                         (AudioSampleRate / m68kClockHz);
int frames = (int)_audioFrameAccumulator;
```

## Recommendations

### Immediate Fix (Priority 1):
**Ensure audio generation every video frame:**
1. Audit `SubmitAudio()` call frequency in UI loop
2. Ensure `GenerateAudioFromSystemCycles()` called consistently
3. Fix headless to match UI audio generation pattern

### Short-term Fixes:
1. **Enable audio catchup by default**: `EUTHERDRIVE_AUDIO_CATCHUP=1`
2. **Increase audio generation batch size**: Reduce per-call overhead
3. **Fix headless throttle**: Make it actually prevent starvation

### Medium-term Improvements:
1. **Unify audio modes**: Make headless use `timed` mode like UI
2. **Add audio generation logging**: Track when/how audio is generated
3. **Optimize audio pipeline**: Reduce per-frame overhead

### Testing Strategy:
1. **Add frame counter to audio generation**: Log each `GenerateAudioFromSystemCycles()` call
2. **Monitor `deltaCycles` consistency**: Ensure stable cycle counts
3. **Test with simple audio test ROM**: Isolate emulation from game complexity

## Logging Gaps Identified
1. No log for `GenerateAudioFromSystemCycles()` calls
2. No frame-by-frame audio generation tracking
3. Inconsistent `EUTHERDRIVE_TRACE_AUDIO_QUEUE` output

## Next Steps
1. **Add diagnostic logging**: Track audio generation per frame
2. **Fix headless audio generation**: Port UI's `timed` mode
3. **Verify audio generation frequency**: Ensure 60 Hz minimum
4. **Test with audio-intensive games**: Validate fixes under load

---
*Report updated by DeepSeek with new production rate analysis on 2026-01-28*

**Key Insight**: The math is correct but execution frequency is wrong. Audio should be generated 60 times/sec (once per video frame) but appears to be generated only ~18.6 times/sec.