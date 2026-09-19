# Darius Gaiden: stale intro sprites and video history

Linux follow-up to `f910c701`. The user confirmed that the ES5505 changes fixed
the sound, then clarified the intro defect: some objects appear to get stuck.
Sound code and UI presentation are unchanged in this checkpoint.

## Causes and fixes

1. **Stale software sprite-list fallback.** When the active hardware sprite
   list was empty, `BuildSpriteList` tried three game work-RAM pointers and
   rendered whatever old list remained there. This resurrected ships from a
   previous intro scene over the hangar/people scene, and old silhouettes during
   transitions. An empty hardware list is valid. Removed the bringup fallback;
   only the hardware-selected list is now used.
2. **Sprite history stopped on skipped frames.** Adaptive or explicit frame
   skipping also skipped sprite RAM latching and the two-stage sprite buffer.
   Advance that history every emulated frame, even when the final picture is
   skipped. The normal two-frame sprite lag and hardware trail mode remain.
3. **Background-only frames were discarded.** The old path substituted palette
   zero for scanline backgrounds and could retain the previous presented frame
   when no layer drew a pixel. Always mix and publish a rendered background-only
   frame, including deliberate scene clears.
4. **Y advancement used cropped coordinates.** The reference skips vertical
   accumulator advancement after hardware scanline zero, not after the first
   visible scanline (24). Correct the hidden-line prefix and visible-line loop.

## Evidence

The original three-slot state is untouched, SHA-256:
`382bb86c8f74203e6608aa017bbdf2e1fcad0414165755ad0e405b529b1181f2`.
All replays use its copy in `.build-tmp/darius-intro-20260919/`.

- Before the timing correction, explicit divisor 2 matched full-rate rendering
  on only **92/178** sampled slot-1 frames. After correction it matched **178/178**.
- Divisors 2 and 3 are checked across all three slots, including sprite-buffer
  hashes and identical CPU/audio state. The first four reconstruction frames
  are excluded from picture comparisons; audio/state checks cover the full run.
  All **890** sampled presented frames pass in the final build.
- Unit checks cover three background-only transitions (222,720 pixels) and a
  terminated hardware sprite list with a valid-looking stale software pointer.
- Running the unoptimized ROM video/object loops produced exactly the same
  900-frame video and audio hashes as the optimized loops in slot 1. The temporary
  bypass was removed: disabling those optimizations did not solve this defect.
- Final 900-frame replays of each intro slot and the older gameplay slot 2 retain
  identical audio-sequence and serialized-state hashes versus `f910c701`.
  Video changes intentionally. Release probe, UI and Headless builds pass, as do
  the existing 98,308 graphics-plane, 72,832 task-frame, 8,748 mixer, ES5505 and
  trace-switch checks. Existing compiler warnings remain.

### Independent renderer oracle

Built the local primary MAME F3 implementation at `/home/nichlas/mame`:

```sh
make SUBTARGET=darius SOURCES=src/mame/taito/taito_f3.cpp -j4 NOWERROR=1 PRECOMPILE=0 REGENIE=1
```

Existing precompiled headers were incompatible with the installed compiler;
regenerating the makefiles without PCH resolved that. No MAME source edits.
The focused build reports an unrelated missing `spacedx` parent for `spcinvdj`,
but runs `dariusg` and the capture script successfully.

Exported all video RAM and control registers from one frame in each user slot.
Injected the same bytes through both emulators' bus handlers, stopped the main
CPU, and allowed the sprite pipeline to settle. All **three** final 320x232
images now match MAME exactly: ImageMagick `compare -metric AE` reports **0**.
Before correcting Y advancement, small differences remained in every scene.
An additional frozen gameplay-slot-2 snapshot also matches exactly (0 differing
pixels), using `game-memory/reference64.png` and `game-oracle/frame0005.ppm`.

This validates rendering of those supplied video-memory snapshots, not all CPU
behavior or every frame in the intro. Live cold-boot MAME captures also show the
hangar scene without the incorrectly resurrected fleet.

Artifacts under `.build-tmp/darius-intro-20260919/`:

- `capture1`–`capture3`: original 900-frame sequences and sprite-history traces.
- `final1`–`final3`: stale-list removal before the final Y correction.
- `ram1`, `fixed2`, `fixed3`: input memory and MAME `reference64.png` images.
- `y-oracle-*`: final same-memory EutherDrive images (`frame0005.ppm`).
- `pacing-complete.log`: final frame-skipping integration checks.
- `complete1.log`–`complete3.log`: final 900-frame intro regressions.

## Repeatable checks

```sh
dotnet build tools/DariusProbe/DariusProbe.csproj -c Release --no-restore -m:1
dotnet tools/DariusProbe/bin/Release/net8.0/DariusProbe.dll --check-video-history
python tools/DariusProbe/check-video-pacing.py \
  tools/DariusProbe/bin/Release/net8.0/DariusProbe.dll \
  /home/nichlas/roms/MAME/TAITO/dariusg.zip .build-tmp/darius-intro-20260919
```

Set `EUTHERDRIVE_DARIUS_PROBE_CAPTURE_DIR` during an ordinary probe replay to
write per-frame hashes, periodic PPMs and the first frame's video memory.
These diagnostics add overhead and must not be used for timing claims.

Replay a capture's video memory without running the main CPU:

```sh
dotnet tools/DariusProbe/bin/Release/net8.0/DariusProbe.dll --render-video-memory \
  /home/nichlas/roms/MAME/TAITO/dariusg.zip INPUT_CAPTURE_DIR SEPARATE_OUTPUT_DIR
```

For MAME, set `DARIUS_REFERENCE_DIR` to the absolute capture directory and use
`-autoboot_delay 0 -autoboot_script tools/DariusProbe/mame-video-reference.lua`.
Use separate cfg/nvram directories and `-video none -sound none -nothrottle`;
on headless Linux also set `SDL_VIDEODRIVER=dummy SDL_AUDIODRIVER=dummy`.
The script exits after its snapshots; it does not overwrite any savestate.

Restart the rebuilt Linux UI before retesting. User playback of all transitions
is still the final check; the earlier audio correction is preserved.
