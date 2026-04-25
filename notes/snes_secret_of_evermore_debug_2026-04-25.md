# Secret of Evermore SNES Debug (2026-04-25)

## Repro

Use the new savestate next to the ROM:

```sh
EUTHERDRIVE_TRACE_VERBOSE=1 EUTHERDRIVE_LOG_VERBOSE=1 \
EUTHERDRIVE_HEADLESS_CORE=snes EUTHERDRIVE_SAVESTATE_SLOT=1 \
dotnet EutherDrive.Headless/bin/Release/net8.0/EutherDrive.Headless.dll \
  --load-savestate \
  "/run/media/nichlas/Atlas/SNES/sd2snes/1 S-Z - US/Secret of Evermore (USA).sfc" \
  "/run/media/nichlas/Atlas/SNES/sd2snes/1 S-Z - US/Secret_of_Evermore__USA_.sfc_17c864a7.euthstate" \
  3
```

The state has only slot 1 and loads cleanly in the current tree.

## Findings

- `EUTHERDRIVE_TRACE_VERBOSE=1` is now included in all SoE repro commands.
- The active-display VRAM write hypothesis did not move this repro:
  - `EUTHERDRIVE_SNES_ALLOW_ACTIVE_VRAM_WRITES=1` produced byte-identical frame output.
  - `$2115-$2119` writes from the new state occur in vblank, not active display.
- Layer isolation narrows the visible problem to BG1/BG2:
  - BG1 only: blue/window-like fragments remain.
  - BG2 only: dark tilemap structure remains.
  - BG3 only and OBJ only are effectively empty for this state.
- Color math is not the cause:
  - `EUTHERDRIVE_SNES_DISABLE_COLOR_MATH=1` is byte-identical.
- Tile-pattern cache is not the cause:
  - `EUTHERDRIVE_SNES_BYPASS_TILE_CACHE=1` is byte-identical for full, BG1-only, and BG2-only renders.
- Global BG tilemap byte swapping is not the fix:
  - `EUTHERDRIVE_SNES_SWAP_BG_TILEMAP_BYTES=1` changes output but makes the image obviously worse.

## Strongest Current Signal

The useful probe is the simple-path probe added inside `RenderLineSimpleMainOnly`, because the old `TraceProbeAtPoint()` path uses the complex `GetColor()` setup and was misleading on this simple-render path.

Representative probes:

```text
[PPU-SIMPLE-PROBE] x=32 y=42 selectedLayer=2 selectedPrio=0 pixel=0x4D color=0x0000
[PPU-SIMPLE-PROBE] x=100 y=100 selectedLayer=1 selectedPrio=1 pixel=0x26 color=0x2D04
[PPU-SIMPLE-PROBE] x=175 y=68 selectedLayer=2 selectedPrio=0 pixel=0x4C color=0x0000
```

Snapshot CGRAM around the BG2 palette:

```text
cgram[40..4F]=[7C1F 0044 0023 0023 0022 0022 0001 0001 0000 0000 0000 0000 0000 0000 0000 0067]
cgram[50..5F]=[7C00 0C85 0864 0863 0443 0442 0421 0021 0000 0000 0000 0000 0000 0000 0000 0000]
```

So the dark BG2 blocks are real selected nonzero BG pixels landing on black CGRAM entries such as `0x4C/0x4D`, not blank tiles or color math.

## Next Move

The next high-value comparison is against a known-good/reference render of this exact scene, or a pre-corruption capture that includes CGRAM. If those BG2 palette entries should be non-black, chase the producer of the palette/tilemap state before this savestate. If they are legitimately black on hardware, the remaining visible bug is probably wrong BG tilemap/tile selection rather than palette application.

## Cold Boot Pass

Cold boot reproduces without the savestate:

- First visible content starts at frame 69: Squaresoft logo, looks coherent.
- The logo fades out and reaches black at frame 321.
- The next scene appears at frame 482 and is already corrupted.
- Dumps:
  - `/tmp/soe_cold_scan/headless_frame120.png` - logo.
  - `/tmp/soe_cold_scan/headless_frame540.png` - corrupted scene.
  - `/tmp/soe_cold_scan/headless_frame600.png` - same corrupted scene moving/fading.

Useful logs:

```text
/tmp/soe_cold_scan_verbose.log
/tmp/soe_cold_window_ppubus_verbose.log
/tmp/soe_cold_ppu_frame481_verbose.log
/tmp/soe_cold_raw482/snes_ppu_frame482_vram.bin
/tmp/soe_cold_raw482/snes_ppu_frame482_meta.txt
```

Important cold boot observations:

- The frame 482 CGRAM snapshot matches the savestate's suspicious BG palette windows.
- The VRAM/tilemap data is already sparse/corrupt-looking at frame 482; this is not caused by a later render-only path.
- Internal PPU trace for frame 481 shows no `VMDATA*-REJECT` entries. Writes with `vblank=False` are still accepted there because the PPU is forced blank during the write period.
- `EUTHERDRIVE_SNES_ALLOW_ACTIVE_VRAM_WRITES=1` is byte-identical for the cold boot frame 540/600 dumps.
- `EUTHERDRIVE_FORCE_LEGACY_TIMING=1` is also byte-identical for cold boot frame 540/600.

Added debug-only DMA pacing flag:

```text
EUTHERDRIVE_SNES_GPDMA_BYTE_CYCLES=<n>
```

Default remains `4`, preserving current behavior. Running with `EUTHERDRIVE_SNES_GPDMA_BYTE_CYCLES=8` shifts the cold boot sequence by roughly 4 frames and changes same-numbered frame hashes, but it does not obviously fix the corrupted scene. This keeps DMA pacing/timing on the suspect list, but not as a one-line fix.
