# Darius Gaiden: ES5505 bass, volume and interrupt corrections

Linux follow-up to `c90ad68c`. The user confirmed playable presentation, then
reported intro graphics artifacts, broken music, missing effects and especially
missing bass. This checkpoint addresses verified sound-chip defects; it does
not certify all effects by listening or resolve the reported intro graphics.

## Corrections

Compared against the local MAME primary implementation in
`/home/nichlas/mame/src/devices/sound/es5506.cpp` and `es5506.h`:

- Filter mode 2 (LP4 only) incorrectly used a highpass third pole. Both third
  and fourth poles must be lowpass using K2. The old implementation removed
  low frequencies. Filter coefficient arithmetic now also truncates signed
  division toward zero, matching the reference.
- ES5505 volume is a four-bit exponent plus four-bit mantissa, not a linear
  byte. Use the logarithmic gain table and retain native 20-bit mixer precision
  until converting the accumulated channels to PCM16. This corrects relative
  voice levels and substantial clipping, not just the master volume.
- A 68000 word read of the chip was performed as two device reads. The first
  acknowledged IRQV before the low byte was returned, losing the voice index.
  Aligned word reads are now one device transaction.
- Pending voice interrupts are serviced even when a voice has stopped, so a
  second pending voice is not abandoned while another IRQ is being acknowledged.

Savestate format is unchanged. Existing limitations remain: frame-batched audio,
16-bit filter history and incomplete ES5510 DSP emulation. This is not a claim
of complete hardware audio fidelity. Old states also contain old filter history;
test a fresh boot as well as restored gameplay.

## Regression checks

`tools/DariusProbe --check-es5505` checks 2,304 volume cases, 180 filter cases,
all 32 read-to-clear IRQ vectors, a queued stopped-voice interrupt, and a 60 Hz
synthetic bass tone. The corrected mode-2 filter retains 0.999734 of that tone's
RMS amplitude at the test cutoff (K1=K2=0x8000). This is a filter unit test,
not a measurement of bass in the actual music.

Replayed copies of all three new intro slots for 360 frames each and the older
gameplay slot 2 for 900 frames, with adaptive rendering disabled. Intro input
does not hold fire; gameplay does. Audio statistics are measured from generated
PCM, not the physical output device:

| Replay | PCM samples | Clipped before | Clipped after | RMS before | RMS after |
| --- | ---: | ---: | ---: | ---: | ---: |
| Intro 1 | 538682 | 45495 | 0 | 17331.657 | 3787.295 |
| Intro 2 | 538682 | 72361 | 0 | 19157.958 | 2056.813 |
| Intro 3 | 538682 | 92350 | 0 | 20263.237 | 2856.913 |
| Gameplay 2 | 1346706 | 287769 | 0 | 21341.690 | 4180.884 |

All four full video-sequence hashes are unchanged. Audio and serialized sound
state intentionally differ. These runs do not establish a performance gain.
Logs and frozen baseline binaries: `.build-tmp/darius-intro-20260919/`.
Copied intro-state SHA-256:
`382bb86c8f74203e6608aa017bbdf2e1fcad0414165755ad0e405b529b1181f2`.

The probe accepts `EUTHERDRIVE_DARIUS_PROBE_FIRE=0` for intro replays and
`EUTHERDRIVE_DARIUS_PROBE_AUDIO_STATS=1` for sample/peak/RMS/clipping statistics.

## Separate mixer correction / intro still open

The three final pixel-conversion paths incorrectly bypassed blend weights when
one contributor was absent. They now shortcut only a full-weight contributor;
partial weights and zero/zero correctly fade toward black. All 8,748 weighted
color checks pass against an independent reference calculation. However, this
does **not** change any video hashes in the four replays above, so it does not
explain the user's captured intro artifacts. Continue investigation from slots
1–3; do not label those graphical artifacts fixed by this checkpoint.

## User retest

Restart the emulator to load the rebuilt Release assemblies:

```sh
dotnet run --project EutherDrive.UI -c Release --no-build -- /home/nichlas/roms/MAME/TAITO/dariusg.zip
```

Listen for bass and shot/explosion effects from a fresh boot and saved gameplay.
Overall volume can be lower now that the incorrect voice gains and clipping
are removed. Listening on the user's actual output is still required.
