# Arkanoid: managed C# Linux bring-up, 2026-09-19

## Run

```sh
dotnet run --project EutherDrive.UI -c Release --no-build -- /home/nichlas/roms/MAME/Arkanoid/arkanoidu.zip
```

Keep `arkanoid.zip` beside `arkanoidu.zip`: the USA clone obtains its shared
graphics and palette PROMs from the parent. Both archive names select the new
`ArkanoidAdapter`, not MCS or a native MAME process. Single-hyphen legacy ROM
names and double-underscore names are accepted. No ROMs are included in Git.

Default arcade mappings: **5** coin, **Enter** start, **left/right** paddle,
**Z/X/C** launch/fire. Existing customized arcade mappings also apply.
Savestate slots are connected through `ISavestateCapable`.

## Implementation

- Existing managed Z80, 6 MHz, RAM/I/O mirrors and zero reads above F000.
- New compact MC68705 interpreter executing the supplied MCU ROM at 750 kHz
  instruction-cycle clock (3 MHz oscillator / 4). Actual port A/C mailboxes,
  direction registers, host IRQ and semaphore edges; no protection response
  table and no Z80 ROM patches.
- Decoded three-plane graphics, PROM palette, tile and sprite banks, global
  flips, native crop and ROT90 output (224x256). UI uses portrait 3:4 physical
  aspect and snapshots the framebuffer under the emulator lock.
- YM2149 tone/noise/envelope generator at 1.5 MHz internal clock, mono duplicated
  to stereo. Sample production follows board cycles; default 44100 Hz, honoring
  `EUTHERDRIVE_AUDIO_OUTPUT_HZ`. Volume and mute use the existing UI control.
- Board cadence: 6,000,000 / (384 * 264) = approximately 59.1856 Hz.
- Versioned savestates include CPU, MCU, PSG, RAM, inputs and timing. ROM identity
  hashes the loaded CPU/MCU/graphics/PROM content. Host volume is not restored.
- Hardware references and BSD notices: `EutherDrive.Core/Arcade/Taito/ArkanoidNotice.md`.

## Verification

```sh
dotnet run --project tools/ArkanoidProbe -c Release -- --checks /home/nichlas/roms/MAME/Arkanoid/arkanoidu.zip
dotnet run --project tools/ArkanoidProbe -c Release --no-build -- --checks /home/nichlas/roms/MAME/Arkanoid/arkanoid.zip
dotnet run --project tools/ArkanoidProbe -c Release --no-build -- /home/nichlas/roms/MAME/Arkanoid/arkanoidu.zip 18000 /tmp/arkanoid-usa-final
```

Passed:

- Synthetic MCU ADC half-carry, mailbox direction/edges and reset checks.
- RAM mirroring and final-boss open-bus behavior.
- Deterministic cold boot and reset.
- Savestate restores current image; 180-frame input replay matches video,
  audio and complete serialized machine state exactly.
- Left/right inputs move the paddle in the correct directions via MCU firmware.
- Audio sample count matches board clock; master-volume mute produces zeroes.
- Checks on both supplied sets, plus USA at 192000 Hz output.
- USA 18000-frame run (~304 seconds emulated), no exception. With concurrent
  checks/build activity: 925.6 emulated fps, 1.080 ms/frame excluding capture I/O;
  890.2 fps including PPM/WAV capture. This is headless throughput, not display fps.
- Visually inspected intro and first-stage captures. Coin/start enters a game;
  the scripted paddle/fire sequence earns points.
- Linux UI Release build: zero errors (existing warnings remain).
- Isolated-working-directory Xvfb UI smoke, dummy audio: selected ArkanoidAdapter,
  loaded USA ROM and completed frames, no logged exception during 15 seconds.
  Timeout exit 124 is intentional. User settings and running instances untouched.

Local artifacts: `/tmp/arkanoid-usa-final/` (PPM captures and audio.wav),
`/tmp/arkanoid-longrun.log`, `/tmp/arkanoid-parent-checks.log`,
`/tmp/arkanoid-192k-checks.log`, `/tmp/arkanoid-ui-smoke.log`.

## Limits / next work

This is a first playable implementation, not a cycle-exact conformance claim.
Tested supplied legacy MCU images: USA `a75-20.ic14` CRC de518e47 and parent
`a75-06.ic14` CRC 515d77b6. Other clones/MCU revisions have not been validated.
The MCU timer/EPROM programming/bootstrap peripherals are not implemented;
these supplied firmware images boot and run without them. Graphics render once
per frame, not per scanline. PSG mixing uses an approximate logarithmic level
curve and DC filter, not a measured analogue model; audio has not been compared
sample-for-sample or by listening against a hardware/reference recording.

Only one player and keyboard/gamepad digital paddle input are connected;
mouse/relative spinner input would be the useful next UX improvement. No complete
playthrough or final boss has been tested. Savestates require the same output
sample rate. N64/SM64 behavior and unrelated Gauntlet work were not changed.

## Follow-up: red ball shadow / incorrect brick colors

The initial graphics decoder assigned IC64 to pen bit 2 and IC62 to bit 0.
MAME's layout lists planes most-significant first: its offsets `{0x10000,
0x8000, 0}` therefore require IC62 -> bit 2, IC63 -> bit 1, IC64 -> bit 0.
Corrected this common tile/sprite decoder, rather than special-casing the ball
or hiding a sprite. Added 64 bitplane/horizontal-position regression cases.
Old savestates remain compatible: decoded graphics are rebuilt from ROM and
are not part of the serialized state.
