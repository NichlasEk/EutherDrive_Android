# Mystic Warriors Audio Plan

Goal: implement real Mystic Warriors audio in EutherDrive, as close to MAME semantics as practical, instead of reusing the older TMNT/TMNT2 sound paths.

## Current Situation

Mystic Warriors now renders well enough that audio is the next major missing subsystem. The current `TmntAdapter` sound path is still shaped around the earlier Konami boards:

- TMNT-style path: Z80 + YM2151 + K007232 + UPD7759-style speech.
- TMNT2/SSRiders path: Z80 + YM2151 + K053260 PCM.
- Mystic Warriors currently falls through the wrong family of assumptions for sound. It has the Z80 ROM loaded, but the real board does not use K053260 or K007232 for sample playback.

MAME says Mystic Warriors uses:

- Main CPU: M68000 at 16 MHz.
- Sound CPU: Z80 at 8 MHz.
- Sound communication: K054321.
- PCM devices: two K054539 chips at 18.432 MHz.
- K054539 stream rate: chip clock / 384 = 48 kHz.
- Stereo routing is inverted per chip route:
  - K054539 output 0 routes to right.
  - K054539 output 1 routes to left.
- Sound CPU banking:
  - `0x0000-0x7fff`: fixed ROM.
  - `0x8000-0xbfff`: banked ROM, 16 banks of 0x4000.
  - initial bank is 2.
- Sound RAM:
  - `0xc000-0xdfff`: Z80 RAM.
- K054539 maps:
  - chip 1: `0xe000-0xe22f`.
  - chip 2: `0xe400-0xe62f`.
- K054321 sound interface:
  - `0xf000-0xf003`.
- Sound control:
  - `0xf800`: bank select and NMI enable/clear.
  - low nibble selects Z80 bank.
  - bit 4 gates K054539 timer NMI.
  - when bit 4 is clear, Z80 NMI is cleared.
- Main CPU sound IRQ:
  - main write at `0x49a000` triggers Z80 IRQ0 hold.
- ROMs:
  - sound program: `168a05.7c`, 0x20000, reloaded to 0x40000.
  - K054539 sample ROM: `168a06.1c` at 0x000000, `168a07.1e` at 0x200000.

MAME reset-time gains matter for Mystic Warriors:

- Chip 1 channels 0-3: gain 0.8.
- Chip 1 channels 4-7: gain 2.0.
- Chip 2 channels 0-7: gain 0.5.

Those are not optional polish. MAME comments say the two chips are badly out of balance without game-specific gain.

## Architecture

Build a dedicated Mystic sound backend inside the Konami adapter, not another patch on the existing TMNT sound branch.

Recommended shape:

- Keep `TmntSound` as the outer sound coordinator for now.
- Add a sound variant branch:
  - `TmntSoundVariant.TmntClassic`
  - `TmntSoundVariant.K053260`
  - `TmntSoundVariant.MystwarrK054539`
- Add a dedicated `K054539Pcm` class.
- Add a small `K054321SoundInterface` class or struct.
- Keep audio scheduling owned by `TmntSound`, so UI integration, volume scaling, savestates, and audio buffer delivery remain unchanged.

The important boundary:

- `TmntSound` handles Z80 clocking, bus mapping, IRQ/NMI lines, stream timing, and final mix into `short[]`.
- `K054539Pcm` handles only the chip register model, sample decoding, local RAM/reverb, timer callback, and stereo render.
- `K054321SoundInterface` handles latches/status/reset-ish communication semantics between main and sound CPUs.

## Phase 1: Correct Mystic Sound ROM Loading

Implement Mystic-specific sound ROM loading in `TmntRomSet.Load`.

Required:

- Load `168a05.7c` into the sound CPU region at `0x00000`.
- Mirror/reload it into `0x20000`.
- Load `168a06.1c` and `168a07.1e` into a 0x400000 K054539 sample ROM region.

Validation:

- Debug summary should report Mystic sound variant and ROM sizes.
- Z80 reset PC should point into valid program bytes, not empty/fallback ROM.
- Bank 2 should initially map to `soundcpu + 0x8000`.

## Phase 2: Mystic Z80 Memory Map

Implement the exact Z80 program map for Mystic:

```text
0000-7fff  fixed sound ROM
8000-bfff  banked ROM, bank = sound_ctrl & 0x0f
c000-dfff  RAM
e000-e22f  K054539 #1
e230-e3ff  RAM/ignored scratch
e400-e62f  K054539 #2
e630-e7ff  RAM/ignored scratch
f000-f003  K054321 sound map
f800       sound_ctrl_w
fff0-fff3  ignored writes
```

Implementation notes:

- Do not apply TMNT2 memory wait hacks to Mystic until a measured need appears.
- Keep reads from unmapped holes stable and boring, preferably `0xff`.
- Writes to unknown holes should be counted for diagnostics but not spam logs by default.
- `sound_ctrl_w` must:
  - set bank from low nibble.
  - clear Z80 NMI when bit 4 is low.
  - store the previous bit 4 state for timer-gated NMI behavior.

Validation:

- Add a short env-gated trace for the first few frames:
  - Z80 PC.
  - sound control writes.
  - bank switches.
  - IRQ pulses.
  - K054321 latch reads/writes.
  - K054539 key-on writes.

This trace should be off by default.

## Phase 3: K054321 Semantics

Replace the current stubby main-side K054321 behavior with real enough semantics.

Needed behavior:

- Main map: `0x498000-0x49801f`, low byte lane.
- Sound map: `0xf000-0xf003`.
- Main writes command/status through K054321.
- Sound CPU reads command and returns status/latch data.
- Preserve busy/ready behavior well enough that the game does not miss commands.

Implementation plan:

1. Port MAME's `k054321_device` behavior directly where small.
2. If MAME's implementation is tiny, keep it as a direct semantic port.
3. If it depends on scheduler internals, implement the observable registers first:
   - main-to-sound latch.
   - sound-to-main latch.
   - busy/status bits.
   - clear/ack transitions.
4. Use trace comparison to confirm command sequence during:
   - boot.
   - coin/start.
   - character select.
   - first gameplay attack.

Acceptance:

- Z80 receives changing sound commands.
- Main CPU no longer sees fake idle-only K054321 values.
- No command gets permanently stuck busy.

## Phase 4: K054539 Register Model

Implement the K054539 chip as a faithful local class.

Register surface from MAME:

- `0x000-0x0ff`: 0x20 bytes per channel, 8 channels.
- Per-channel fields include:
  - pitch.
  - volume.
  - reverb volume.
  - pan.
  - reverb delay.
  - loop address.
  - start/current address.
- `0x200-0x20f`: per-channel mode/loop/reverb control.
- `0x214`: key on.
- `0x215`: key off.
- `0x22c`: channel active status.
- `0x22d`: ROM/RAM readback.
- `0x22e`: ROM read address/data helper.
- `0x22f`: global control.

Important MAME behavior to preserve:

- Stream updates before register writes.
- Key-on starts channels using the latched start/current address rules.
- Some current position writes are ignored while a channel is active.
- ROM/RAM readback at `0x22d` is used by games and must not be stubbed.
- Timer state toggles and can drive NMI.
- Reverb RAM is 0x8000 bytes.
- Reverb position advances at render rate.
- Chip stream rate is 48 kHz for Mystic.

Implement this in two passes:

1. Functional pass:
   - register read/write.
   - key on/off.
   - channel active bits.
   - ROM readback.
   - 8-bit PCM.
   - 16-bit PCM.
   - 4-bit DPCM.
   - loop handling.
2. Fidelity pass:
   - reverb buffer.
   - exact pan table.
   - exact volume table.
   - channel gains.
   - timer/NMI period.
   - edge cases for position writes and global control.

## Phase 5: PCM Decoding and Mixing

K054539 supports at least these modes:

- 8-bit PCM.
- 16-bit little-endian PCM.
- 4-bit DPCM with the MAME delta table.

Mixing rules:

- Decode each active channel at chip sample rate.
- Apply per-channel volume table.
- Apply pan using constant-power pan table.
- Apply per-channel gain override.
- Apply reverb send.
- Mix to stereo float or 32-bit int accumulator.
- Clamp/convert once at final output to `short`.

Do not mix directly into `short` per channel. That causes clipping and makes gain debugging miserable.

Recommended internal format:

- Use `float` or `double` accumulators for development.
- Once stable, optimize to fixed-point if profiling says it matters.
- Keep a compile-time/simple env trace option for per-chip peak and active channels.

Mystic route:

- Chip output 0 goes to right.
- Chip output 1 goes to left.
- Chip 1 gains:
  - channels 0-3: 0.8.
  - channels 4-7: 2.0.
- Chip 2 gains:
  - channels 0-7: 0.5.

## Phase 6: Timing and Interrupts

This is where "real audio" either works or slowly drifts.

Targets:

- Sound Z80: 8 MHz.
- K054539: 18.432 MHz each.
- K054539 stream: 48 kHz.
- Main/sound scheduler should not batch too coarsely.
- MAME maximum quantum is 1920 Hz; we should use that as a scheduling hint.

Implementation:

- Extend `RunMainCpuCycles` ratio handling for Mystic:
  - sound cycles per frame from 8 MHz / video FPS.
  - do not reuse 8 MHz only by accident; explicitly name it Mystic.
- Run Z80 in small enough chunks that:
  - sound IRQ pulses are observed promptly.
  - K054539 timer NMI edges are not delayed an entire video frame.
  - audio stream can flush before register writes.
- On K054539 timer edge:
  - chip 1 timer callback toggles state.
  - if `sound_ctrl & 0x10` and rising edge, assert Z80 NMI.
  - if sound control bit 4 is cleared, clear NMI.

Acceptance:

- Music starts without needing input hacks.
- Sound effects trigger on coin/start/attack.
- No long silence after sound command writes.
- No runaway NMI storm.
- Z80 PC keeps moving and does not stall in a busy loop waiting for a missing chip bit.

## Phase 7: Savestates

Savestate must include the complete Mystic audio state, not just CPU RAM.

Required state:

- Z80 CPU state.
- Z80 RAM.
- sound control byte.
- bank index.
- IRQ/NMI line state.
- K054321 latches/status.
- both K054539 register arrays.
- both K054539 reverb RAM buffers.
- both K054539 current positions and latched positions.
- active channel bits.
- ROM readback address/current pointer.
- timer state and timer phase.
- output sample accumulators.

Acceptance:

- Save during music, load immediately: music continues from the same phrase, no stuck drone.
- Save during active sound effect, load: effect continues or decays naturally.
- Cold boot then load: sound state restores without needing to restart UI.

## Phase 8: UI Integration

Keep UI behavior simple:

- Existing master volume should scale the final mixed arcade output.
- Existing audio on/off should mute all Mystic audio without stopping emulation.
- Do not add a user-facing chip mixer until the backend is correct.

Developer-only env controls are useful:

```text
EUTHERDRIVE_MYSTWARR_MUTE_K054539_1=1
EUTHERDRIVE_MYSTWARR_MUTE_K054539_2=1
EUTHERDRIVE_MYSTWARR_MUTE_CHIP1_MASK=...
EUTHERDRIVE_MYSTWARR_MUTE_CHIP2_MASK=...
EUTHERDRIVE_MYSTWARR_AUDIO_TRACE=1
EUTHERDRIVE_MYSTWARR_AUDIO_PROBE_DIR=/tmp/mystwarr-audio
```

Probe output should include:

- mixed stereo raw.
- chip 1 stereo raw.
- chip 2 stereo raw.
- per-frame active channel mask.
- peak levels.
- key-on/key-off events.
- bank switches.
- K054321 commands.

## Phase 9: MAME Comparison

Use local MAME as the reference, not memory or YouTube audio.

Reference captures:

1. Boot attract audio.
2. Coin insert.
3. Start press.
4. Character select.
5. First stage walk/attack.
6. A loud hit/explosion.

For each capture:

- Record EutherDrive raw audio.
- Record MAME WAV with same ROM set.
- Compare:
  - song start timing.
  - rough RMS per channel.
  - stereo orientation.
  - channel balance.
  - obvious missing voices/percussion.
  - no DC offset.
  - no clipping.

Trace comparison:

- Log Z80 writes to K054539 registers in EutherDrive.
- Add equivalent temporary log in local MAME if needed.
- Compare command order, bank switching, key-on addresses, modes, volumes, and pans.

The fastest way to find bugs will be register-write diffing, not listening by ear.

## Phase 10: Performance Plan

K054539 can be expensive if implemented naively: 16 channels at 48 kHz with reverb and interpolation can add up.

Start correct, then optimize:

- Render only active channels.
- Precompute volume table and pan table.
- Keep ROM reads bounds-safe but cheap.
- Use per-chip active mask.
- Batch render between register writes, not every Z80 instruction.
- Avoid allocations in the audio frame path.
- Use float accumulators first; switch to fixed-point only if profiler says so.

Perf acceptance:

- Mystic with audio should not lose more than a small percentage of FPS versus muted audio.
- No per-frame allocations in steady state.
- No audio underruns when UI master volume is at 100%.

## Order of Work

1. Add Mystic-specific sound variant and ROM loading.
2. Add Mystic Z80 memory map with K054321 stub upgraded enough to run commands.
3. Port K054539 register shell, key-on/off, status, and ROM readback.
4. Implement 8-bit PCM, then 16-bit PCM, then DPCM.
5. Add chip timer callback and NMI edge behavior.
6. Add pan/volume/gain tables and stereo routing.
7. Add reverb.
8. Add savestate coverage.
9. Add trace/probe tools.
10. Compare against MAME captures and tune only after semantics match.

## First Milestone

The first meaningful milestone is not "sounds nice". It is:

- Z80 executes Mystic sound program using the right map.
- Main sound IRQ wakes it.
- K054321 commands are visible.
- K054539 key-on writes occur.
- At least one K054539 channel produces decoded PCM at the correct pitch.

Once that milestone is reached, the rest becomes normal chip fidelity work.

## Definition of Done

Mystic Warriors audio is done when:

- Music and sound effects are present from cold boot.
- Coin/start/gameplay sounds trigger reliably.
- Both K054539 chips contribute.
- Stereo orientation matches MAME.
- Channel balance uses MAME's Mystic gain overrides.
- Savestates preserve active audio.
- The UI volume/mute path works exactly like other cores.
- No debug env vars are required for normal sound.
- Performance remains playable with audio enabled.
