# Duke Nukem 64: low VI origin selected the wrong framebuffer

Baseline `875997ba`. The user's replacement slot 1 is preserved unchanged:
`/home/nichlas/roms/N64/Duke_Nukem_64__Europe_.z64_8aaa9cce.euthstate`,
SHA-256 `b06039b34897a205089846fef229ec47ca5f7e904d080c4e5e4f8d6d931b566d`.
Extraction verifies the embedded slot hash. Artifacts are under
`.build-tmp/duke-corruption-2026-09-19/`.

## Evidence

The CPU continues through the saved scene with no unknown opcodes. Duke uses
RGBA16 color buffers at `0x400` and `0x25c00`. VI origins `0x680` and `0x25e80`
start one 640-byte row into those buffers. The depth target is `0x3da800`.

The renderer already writes a recognizable game scene into low RAM. After
25 seconds from this state, raw RAM at `0x680` shows the level and weapon
(`ram-680.png`). The presenter, however, labels all VI addresses below `0x1000`
suspicious and substitutes another buffer. RDP snapshot publication also
rejects these addresses. The old path switches between color-image-base and
VI-origin readbacks, introducing a one-row jump, and can display a different
render target or a stale frame. A captured baseline log also identifies the
depth target as the most recent color image during its clear.

The actual saved adapter frame is a dark level image, not the bright rainbow
screenshot. This investigation establishes the incorrect buffer selection;
it does not claim to reproduce every transient screenshot pixel exactly.

## Change

- RDP producer metadata and completed snapshots accept any in-bounds RAM
  address, including zero. The arbitrary heuristic floor remains for unproven
  framebuffer guesses, but no longer rejects explicit RDP producers.
- When VI is below `0x1000`, first look for a completed RDP snapshot covering
  that exact VI range, width and pixel format. Apply its row offset and return
  it before any cached candidate or recovery heuristic can override it.
- Remove the four yield/sleep retries that could change a valid low VI pointer
  into the other buffer's pointer while polling.
- Restore a serialized snapshot at address zero when its data and epoch prove
  it exists. The save format is unchanged and still stores only the latest
  snapshot, not the entire runtime cache.
- The headless probe now captures low VI addresses too and prints the selected
  framebuffer status. Its old `>= 0x1000` capture gate hid this evidence.

No CPU timing, game instruction, texture, or triangle rasterization changes.
Low pointers without a matching completed snapshot retain existing recovery
behavior. Uniform low-address fill clears still do not publish by themselves.

## Validation

- `--check-low-vi`: 73 cases, including RGBA16/RGBA32, address zero/0x400/0x800,
  row offsets, alternating high/low buffers, save/load, and newer unrelated
  snapshots. RAM is overwritten after publication to ensure the completed
  image is returned. An unproven low pointer remains rejected. Pattern fills
  explicitly enqueue publication as textured/triangle draws do before FullSync.
  The baseline fails this regression because no low snapshot is published.
- Existing `--check-video`: 81 cases; `--check-render`: 6,845 cases.
- `EUTHERDRIVE_N64_PERF=1 --check-snapshots`: unchanged pixels with 240 -> 1
  coalesced snapshot captures.
- Replay of the 1,126-list captured RDP frame matches the old framebuffer and
  the entire 8 MiB RAM dump exactly. RAM SHA-256:
  `82ba62d15c34df7a6ab734379c069fbec301420b7fbfb9c3cd67d9e39128f085`.
- A 35-second resumed run displays the level and weapon with progressing
  graphics tasks and no unknown opcode. `fixed/frame-0035.png` and `fixed.log`
  show the RDP-backed VI read at `0x680`, 320x213. This verifies the saved scene,
  not every level, all visual effects, or real-time game speed.

```sh
dotnet tools/N64Probe/bin/Release/net8.0/N64Probe.dll --check-low-vi
dotnet tools/N64Probe/bin/Release/net8.0/N64Probe.dll --check-video
```

Linux UI Release builds with 383 warnings and zero errors. Tested probe and UI
match for both assemblies:
- MIPS: `ed629472015c5c6279508ffb3d03108e6c6ab7163ca2257b9e9b0adff3502845`
- Core: `d52519ef2ace47386428721c8ca082bed4faad15c47408f9e699aedcbb53e7d7`

The user slot hash is unchanged. Restart the application, then load slot 1.
