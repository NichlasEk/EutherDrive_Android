# Kaze Kiri Ninja Action: Countdown Corruption Debug Notes

## Scope

This note summarizes the confirmed findings from debugging the corrupted countdown screen in `Kaze Kiri Ninja Action (Japan)` on the PCE CD core.

The issue discussed here is the countdown/cutscene corruption where the middle portion of the image is visible, while the top and bottom contain garbage.

## Current Conclusion

This does **not** currently look like:

- a UI cropping problem
- a layer priority problem
- a savestate-only problem
- a VDC VRAM DMA corruption problem

It **does** currently look like:

- corrupted BAT contents already present in VRAM
- produced by normal CPU `VWR` traffic
- with the final bad BAT overwrite happening later in boot/countdown setup

## What Was Confirmed

### 1. The corruption reproduces from cold boot

The issue is not tied to savestate load.

Cold boot reproduction using headless probing reaches the same corrupted countdown frames as slot 1:

- countdown `5`
- countdown `4`
- countdown `3`
- countdown `2`
- countdown `1`

All of them were already corrupted when they first appeared.

Implication:

- savestate loading is not the root cause
- slot 1 is useful for quick iteration, but cold boot is the authoritative repro

### 2. The corruption is already in VRAM

VRAM dumps show that the BAT itself is wrong.

Slot 1 / cold-boot countdown BAT rows contain values that look like code/data words rather than tilemap entries, for example:

```text
008A 0029 0F00 C960 8DF7 ...
```

This means the visible garbage is not just a renderer interpreting good data incorrectly.

Implication:

- the problem is upstream of final rendering
- focus should stay on VDC register semantics / VRAM write path

### 3. Gameplay and countdown differ in VRAM state

Observed snapshots:

- slot 1 / cold-boot countdown: corrupted BAT
- slot 2 gameplay: structurally sane BAT

The same core display registers were largely identical between states:

- `VSR`
- `VDW`
- `HDS`
- `HDE`
- `HDW`
- `BXR`
- `BYR`

Implication:

- this is not explained by a simple “everything is shifted” display-mode issue

### 4. The bad BAT writes are CPU `VWR`, not VDC DMA

Focused VRAM tracing on BAT address range `0x0000-0x03FF` showed:

- countdown BAT writes were coming from normal CPU `VWR`
- not from VDC VRAM DMA

Observed progression for affected BAT rows:

- frame `0`: BAT initialized to `0x0200`
- frame `22/23/28/29`: rows overwritten to `0x01FF`
- frame `39`: rows overwritten again with the final garbage/code-like values

Example:

```text
0000: f0:0200 -> f22:01FF -> f23:01FF -> f28:01FF -> f39:0000
0001: f0:0200 -> f22:01FF -> f23:01FF -> f28:01FF -> f39:008A
0011: f0:0200 -> f22:01FF -> f23:01FF -> f28:01FF -> f39:C960
003E: f0:0200 -> f22:01FF -> f23:01FF -> f28:01FF -> f39:8DF7
```

Implication:

- the next useful target is not DMA timing
- the next useful target is CPU-side VDC register programming around `MAWR/MARR/VWR`

### 5. The final corruption happens late, not at first BAT clear

The first pass writes sane uniform data:

- `0x0200` across the BAT region

Later passes replace it with:

- `0x01FF`
- then final garbage/code-like words

Implication:

- the initial clear/fill is not the root issue
- the root issue is a later register/value sequence that causes CPU `VWR` to source the wrong words

## Things Already Tried

### VRR/MARR read-buffer semantics

A Geargrafx-like `VRR` read-buffer implementation was added locally to `PPU.cs`:

- `MARR` MSB write prefetched VRAM into a read buffer
- `VRR` low read returned buffer low byte
- `VRR` high read returned high byte and prefetched/incremented

This made `VRR` behavior closer to Geargrafx, but it did **not** resolve the countdown corruption.

Implication:

- `VRR` semantics may still matter in general
- but they are not sufficient to fix this bug alone

### UI-side cropping / viewport ideas

These were ruled out for this issue.

The countdown problem is present in raw VRAM/BAT contents, so UI adjustments are not the right fix path.

## Likely Next Step

The most promising next debugging step is:

1. trace `MAWR`, `MARR`, and `VWR` register writes around frames `22-39`
2. correlate the late BAT overwrite with the exact VDC register sequence
3. determine why CPU `VWR` is sourcing code-like words instead of intended tilemap values

In short:

- stay in core/PPU/VDC logic
- do not chase UI masking/cropping for this issue
- focus on late CPU-driven BAT rewrite sequencing

## Short Summary

- The countdown corruption is real VRAM/BAT corruption.
- It reproduces from cold boot.
- It is not primarily a savestate issue.
- It is not primarily a UI/layer/cropping issue.
- It is not coming from VDC VRAM DMA.
- It is coming from later CPU `VWR` writes into BAT.
- The decisive bad overwrite happens around frame `39`.
