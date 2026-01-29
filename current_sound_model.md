# Current YM2612 Sound Model in EutherDrive

## Overview
This document describes the current timing and sound generation model for the YM2612 FM synthesis chip in the EutherDrive emulator, after fixing the "elastic music" issue.

## Core Timing Principle
YM2612 timing is now **decoupled from audio generation** and instead synchronized with the **master SystemCycles (M68K clock)**. This ensures consistent music tempo regardless of when audio buffers are filled.

## Timing Components

### 1. Master Clock
- **SystemCycles**: M68K master clock (≈7.67MHz)
- All YM2612 timing is derived from SystemCycles
- **72 SystemCycles ≈ 1 YM2612 timer tick** (at YM2612_CLOCK/72 rate)

### 2. Timer A/B System
Timer A and B are the **heartbeat of music timing**:
- Advance based on elapsed SystemCycles
- Generate interrupts when they overflow
- Music drivers use these interrupts for timing

```csharp
// Timer advancement logic
public void AdvanceTimersFromSystemCycles()
{
    long currentSystemCycles = md_main.SystemCycles;
    long elapsedCycles = currentSystemCycles - _lastSyncSystemCycles;
    
    // Convert to timer ticks: 72 cycles = 1 tick
    _timerTickFrac += elapsedCycles;
    int ticks = (int)(_timerTickFrac / 72);
    
    if (ticks > 0) {
        StepTimers(ticks);  // Advance Timer A/B
        _timerTickFrac %= 72;
    }
    
    _lastSyncSystemCycles = currentSystemCycles;
}
```

### 3. Operator Timing
Operators generate sound and have their own timing:
- **Phase generators**: Control pitch/frequency
- **Envelope generators**: Control volume over time (ADSR)
- **LFO**: Modulation effects

Operators advance **ONLY when audio is generated**:
```csharp
public (int out1, int out2) YM2612_Update()
{
    // 1. Advance timers based on SystemCycles
    AdvanceTimersFromSystemCycles();
    
    // 2. Advance operators by ONE audio sample
    lfo_calc();
    for (int w_ch = 0; w_ch < NUM_CHANNELS; w_ch++)
    {
        register_change(w_ch);     // Update parameters
        phase_generator(w_ch);     // Advance phase
        envelop_generator(w_ch);   // Advance envelope
        operator_update(w_ch);     // Generate audio
    }
    
    return (left_output, right_output);
}
```

## Timing Synchronization Points

### When AUDIO is generated (sound playing):
1. **`YM2612_Update()`** - Single sample
   - Called by audio system when buffer needs filling
   - Advances: Timers + Operators (1 sample)

2. **`YM2612_UpdateBatch()`** - Multiple samples
   - Called for batch processing
   - Advances: Timers + Operators (N samples)

### When SILENT (no audio generated):
1. **`EnsureAdvanceEachFrame()`** - Each frame (~60Hz)
   - Called from main loop every frame
   - Advances: Timers ONLY

2. **Register read/write operations**
   - When game reads/writes YM2612 registers
   - Advances: Timers ONLY

3. **`TickTimersFromZ80Cycles()`** - Z80 execution
   - Accumulates Z80 cycles
   - **Currently DISABLED** to avoid double-counting

## The "Elastic Music" Fix

### Problem (BEFORE):
- Operators advanced ONLY when `YM2612_Update()` was called
- Timer A/B advanced ONLY when `YM2612_Update()` was called
- Music tempo depended on **when audio buffers were filled**
- B-spam → more audio calls → faster tempo

### Solution (AFTER):
- Timer A/B advance based on **SystemCycles elapsed**
- Timer advancement happens from **multiple sources**
- Music tempo is **independent of audio generation rate**

## Current Timing Flow

```
┌─────────────────┐
│  SystemCycles   │ ← M68K master clock (7.67MHz)
└────────┬────────┘
         │
         ▼
┌─────────────────────────────────────┐
│  AdvanceTimersFromSystemCycles()    │
│  - Calculates elapsed cycles        │
│  - Converts to timer ticks (72:1)   │
│  - Advances Timer A/B               │
└────────┬────────────────────────────┘
         │
         ▼
┌─────────────────┐    ┌─────────────────┐
│   Timer A/B     │───▶│  YM2612 IRQ     │
│   (72Hz ticks)  │    │  (to Z80)       │
└─────────────────┘    └────────┬────────┘
         │                      │
         │                      ▼
         │            ┌─────────────────┐
         │            │  Z80 Music      │
         │            │  Driver         │
         │            └────────┬────────┘
         │                      │
         ▼                      ▼
┌─────────────────┐    ┌─────────────────┐
│  Operator       │    │  YM2612         │
│  Advancement    │◀───│  Register Writes│
│  (when audio    │    │  (note on/off,  │
│   generated)    │    │   parameters)   │
└────────┬────────┘    └─────────────────┘
         │
         ▼
┌─────────────────┐
│  Audio Output   │ ← What we hear
│  (44.1kHz)      │
└─────────────────┘
```

## Key Variables

### Timer State:
- `_lastSyncSystemCycles`: Last SystemCycles when timers were advanced
- `_timerTickFrac`: Fractional accumulator for timer ticks (72 cycles = 1 tick)
- `_timerACount`, `_timerBCount`: Current timer values
- `_timerAReload`, `_timerBReload`: Reload values

### Operator State:
- `g_slot_phase_inc`: Phase increment per sample (frequency)
- `g_slot_env_*`: Envelope rates (attack, decay, sustain, release)
- `g_slot_freq_cnt`: Current phase accumulator
- `g_slot_env_cnt`: Current envelope position

## Calibration Notes

### Original Timing (BEFORE calibration):
- YM2612 sample rate: 53,267 Hz (YM2612_CLOCK / 144)
- Audio output rate: 44,100 Hz
- Ratio: 53,267 / 44,100 ≈ 1.2079

### Current Timing (AFTER fixing elastic music):
- **ALL timing scaling removed** (`EMU_CORRECTION = 1.0`)
- Operators advance at **audio rate** (44.1kHz)
- Timer A/B advance at **SystemCycles rate** (72 cycles/tick)
- This may cause music to be **~21% slower** than real hardware

## Testing Results

### Working:
- Timer A/B interrupts generated correctly
- Z80 writes to YM2612 registers
- Audio generation (hear "weak pling")
- No "mad cricket" extreme fast audio

### Issues:
- Music may be slightly slow (21% timing error)
- Volume may be low (FM_VOLUME_DIVISOR = 64)
- Some games may not initialize YM2612 immediately

## Next Steps

1. **Verify timing accuracy**: Compare with real hardware
2. **Test with games** that have immediate audio
3. **Consider re-adding 21% scaling** if music is too slow
4. **Monitor for "elastic music" regression** with B-spam tests