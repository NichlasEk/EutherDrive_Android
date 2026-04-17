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

- corrupted live VRAM contents already present in the bad countdown frame
- produced by normal CPU `VWR` traffic
- with late graphics-building writes happening later in boot/countdown setup

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

This part is still true, but the older BAT-specific reading was too strong.

Follow-up comparison against a working gameplay snapshot showed that the previously suspicious BAT words also appear during normal gameplay. So values like:

```text
008A 0029 0F00 0AC9 05B0 ...
```

are not, by themselves, proof that the BAT is corrupted.

The actually bad countdown frame that matches the visible corrupted screen instead uses a very different, highly structured BAT, for example:

```text
0100 0100 0100 ...
1300 1300 1300 ...
0101 0101 ... 0110 0111 0112 ...
```

Implication:

- the visible corruption is still upstream of final rendering
- but the old “BAT looks like code/data words” theory is not reliable enough to guide the next pass
- the bad frame needs to be analyzed as a broader VRAM/pattern problem, not only as a BAT problem

### 3. Gameplay and countdown differ in VRAM state

Observed snapshots:

- slot 1 / cold-boot countdown: structured BAT pointing at a different graphics set
- slot 2 gameplay: different BAT and pattern regions, with only a few active sprites

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
- the old “countdown BAT looks obviously invalid while gameplay BAT looks sane” contrast was overstated

### 4. Raw VRAM from the bad frame reproduces the bad image

Rendering the bad countdown frame directly from dumped `vram.bin` using the current BAT/tile/sprite layout rules reproduces the noisy screen shape closely enough that the visible corruption is explained by live VRAM contents.

The active bad-frame visual data is spread across more than BAT:

- BG tiles referenced around pattern regions `0x1000-0x13FF` and `0x3000-0x33FF`
- large sprite patterns around `0x6000-0x7FFF`
- SAT that is coherent enough to produce a `4x4` grid of large `32x64` sprites plus smaller overlay sprites

Implication:

- this is not primarily a final blit/UI issue
- BAT-only theories are insufficient
- background and sprite graphics-building paths both matter for this bug

### 5. The late BAT writes are CPU `VWR`, not VDC DMA

Focused VRAM tracing on BAT address range `0x0000-0x03FF` showed:

- countdown BAT writes were coming from normal CPU `VWR`
- not from VDC VRAM DMA

Observed progression for affected BAT rows:

- frame `0`: BAT initialized to `0x0200`
- frame `22/23/28/29`: rows overwritten to `0x01FF`
- frame `39`: rows overwritten again with the later structured values that had originally been misread as obviously invalid

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

### 6. The final corruption happens late, not at first BAT clear

The first pass writes sane uniform data:

- `0x0200` across the BAT region

Later passes replace it with:

- `0x01FF`
- then the later structured values used by the bad countdown frame

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

The countdown problem is present in raw VRAM contents, so UI adjustments are not the right fix path.

### Zero-page pointer tracing

Additional CPU-side tracing was added for:

- `LDA ($27),Y` effective-address reads
- zero-page writes in the `$20-$2F` range

Important implementation note:

- zero-page writes originally bypassed `Write8()` and therefore bypassed the CPU write tracer
- tracing `Poke_ZP8()` directly was necessary to see the real `$27/$28` activity

What this showed:

- `$27` is not obviously becoming random or corrupted
- the pointer moves deterministically through the countdown pack routine
- observed `($27),Y` base addresses included `0xE6D8`, `0xE714`, `0xE757`, and `0xE798`

Implication:

- the simple “bad zero-page pointer corruption” theory is weaker than before
- the game appears to be intentionally stepping through a source stream, even though the resulting BAT data is still wrong on screen

### The late BAT overwrite comes from a small pack loop, not a stray write

Disassembly of the executing RAM around the bad write PCs shows a compact routine around `0x4D80-0x4E00` that:

- updates `$27/$28`
- shifts a bitmask in `$22`
- uses `carry` from `ASL $22`
- conditionally either:
  - writes zero to VDC `VWR`, or
  - loads `LDA ($27),Y` and writes that byte pair to VDC `VWR`

Representative sequence:

```text
A4 24       LDY $24
82          CLX
06 22       ASL $22
B0 05       BCS +5
9C 02 00    STZ $0002
80 06       BRA +6
B1 27       LDA ($27),Y
8D 02 00    STA $0002
C8          INY
06 22       ASL $22
B0 05       BCS +5
9C 03 00    STZ $0003
80 06       BRA +6
B1 27       LDA ($27),Y
8D 03 00    STA $0003
```

Implication:

- the final bad BAT write is not random bus noise
- it is produced by a structured CPU-side data pack/unpack routine
- a wrong decision in that routine, or in the flags/state feeding it, could explain the bad BAT words
- earlier interpretation that this point called a helper subroutine was incorrect; this is a direct `ASL $22` / branch-driven bitpack path

Additional concrete detail from later RAM dumps:

- the outer helper around `0x4D7D-0x4D9A` uses `LDA (ZP)`, `PHP`, pointer increment, `PLP`, and `BNE`
- if the loaded control byte is zero, it zero-fills a `0x40`-word block through VDC `VWR`
- otherwise it falls through into the `$20D0-$20DF` staging / `ASL $22` pack logic

That makes these opcodes especially relevant when comparing CPU behavior against Geargrafx:

- `0xB2` `LDA (ZP)`
- `0x08` / `0x28` `PHP` / `PLP`
- `0x82` `CLX`
- `0xC2` `CLY`

### Background CG-mode experiment

One additional renderer-side hypothesis was tested:

- our background path had a special `bgCgMode` branch in `PPU.cs`
- Geargrafx background rendering does not use a matching special-case branch

That branch was removed locally and the slot-1 countdown repro was rerun.

Result:

- the final countdown image was still corrupted

### HuC6280 `T` / stack-status follow-up

The next concrete CPU theory was HuC6280 transfer-flag semantics:

- Geargrafx `PHP` pushes `P | B` without clearing `T` first
- our core had been clearing `T` before `PHP`

Applying only that narrow `PHP` fix did **not** solve the countdown corruption, but it did change the resulting VRAM contents substantially while leaving the visible countdown still broken.

Implication:

- CPU status / transfer semantics are affecting the graphics build path
- `PHP` is not sufficient on its own

A larger follow-up experiment then tried the more complete Geargrafx-style model where:

- `T` is latched for the current instruction
- visible status `T` is cleared at the start of the next instruction
- `PLP` / `RTI` can therefore restore `T` for the following instruction

That change caused a major behavioral shift:

- the savestate no longer reached the bad countdown at all
- instead it stayed on the BIOS "JUST A MOMENT..." screen through the same frame window

Implication:

- the `T` flag path is almost certainly relevant
- but our HuC6280 still does not match Geargrafx closely enough for a full transfer-latch conversion to be dropped in blindly
- likely remaining differences include exact timing and/or additional status-flow details around the instructions that interact with `T`

Practical outcome:

- the broad transfer-latch experiment was backed out so the core does not stay in a regressed boot state
- the small `PHP` correction remains a plausible local correctness fix, but it is not the full answer

Implication:

- this specific background CG-mode interpretation was not the fix
- the bug still looks more likely to be in CPU/VDC data generation than in final background pixel fetch

### Cold-boot frame sync and source window

More recent cold-boot tracing pinned down the first real countdown-helper pass:

- the helper around `0x4D7D-0x4E00` first runs on cold boot at `frame 119`
- the immediately preceding source-stream producer is still the BIOS CD path at `0xEA9C`
- the decisive writes into `0xD0E0..0xD1D0` land on `frame 118`, not on `119/120`

Concrete bytes written by that `frame 118` producer include:

- `0xD0EE = 0x7F`
- `0xD0EF = 0xC0`
- `0xD0FF = 0xFF`
- `0xD104 = 0xC0`
- `0xD114 = 0xFF`
- `0xD169 = 0xA8`
- `0xD179 = 0x01`
- `0xD194 = 0xAA`
- `0xD1A4 = 0x01`
- `0xD1CE = 0x80`

Implication:

- for the earliest cold-boot countdown build, the helper is consuming a fully prepared source window from the prior frame
- there is no extra `0xD0E0..0xD1D0` rewrite on `frame 119` or `120` that explains the first visible corruption by itself

### Early visual result at `130` frames

A short cold-boot repro was rerun with frame dumps centered around the first helper pass.

Observed output at `130` frames:

- the top and bottom corruption bands are already present
- the central countdown numeral has not appeared yet

Implication:

- the band corruption is an early result of the same graphics build path
- it is not secondary fallout from the later center-digit pass
- this makes the early `frame 119` VRAM/VDC build sequence especially valuable to compare against Geargrafx

### Geargrafx-guided CD register check

The local Geargrafx code was checked for CD register semantics.

Confirmed model difference:

- Geargrafx treats `0x1808` as a read of the currently latched SCSI byte followed by `AutoAck()`
- data advancement belongs to SCSI handshake phase logic, not directly to the `0x1808` register read

A matching local experiment was tried in `CDRom.cs` and then backed out.

Result:

- the relevant cold-boot source bytes for the countdown window did not change
- gameplay loading regressed

Implication:

- that was a real model mismatch
- but it was not the root cause of this countdown corruption
- the CD handshake path should not be treated as the current best lead

### Current best pause-state

At pause time, the highest-signal reading is:

- the first helper pass is real and synchronized (`frame 119`)
- the helper's source window is already populated on `frame 118`
- top/bottom corruption appears before the center numeral
- broad CD and renderer guesses have not moved the bug in a useful way

That leaves the best next step as a direct Geargrafx-side comparison of:

- the source CD-RAM window just before the first helper pass
- the corresponding VDC/VRAM state during that pass

### Additional correction from a later pass

The decode loop around `0x4D9F` was rechecked carefully.

Important correction:

- the sequence is **not** `CLX` followed by a helper call
- it is `CLX`, then `ASL $22`, then `BCS`

That makes the current leading CPU-side hypothesis narrower:

- the problematic decision point is more likely flag/bitstream state in `$22`
- not a missing helper call target around `0x05B0`

### Direct trace of the real `0x4D7D` helper

The countdown helper was then traced directly during a real savestate run with instruction/state, indirect-read, and stack tracing restricted to `0x4D7D-0x4E00`.

Observed sequence:

- `0x4D7E` `B2 $27` read from effective address `0xD0EE` and loaded `0x7F`
- `0x4D80` `PHP` pushed `0x10`
- `0x4D81` incremented `$27` from `0xEE` to `0xEF`
- `0x4D87` `PLP` popped the same `0x10`
- `0x4D88` `BNE` took the non-zero path into the pack logic
- `0x4D9B` `B1 $27,Y` then read bytes from `0xD0EF`, beginning with `0xC0 00 00 ...`

Later iterations in the same countdown path used a different base:

- `0x4DDE` / `0x4DED` read from `0xD56E + Y`
- those reads were a long run of `0xFF`

Implication:

- the `PHP` / `PLP` path is now confirmed by direct execution trace, not only by static disassembly
- in the traced path, stack traffic looked internally consistent: `PHP` pushed `P | B = 0x10` and `PLP` restored the same byte
- this specific helper invocation did not show an obviously broken `PHP` / `PLP` round-trip by itself

### The traced source stream already lives in CD RAM before the helper consumes it

Snapshots taken around the traced helper showed:

- CPU `mpr6 = 0x7E`
- the helper source addresses `0xD0EE` and `0xD56E` therefore resolve into mapped CD RAM, not zero page or ROM
- the corresponding snapshot bank (`ram_bank_31.bin`) contained the same bytes across consecutive frames

Concrete bytes from the traced source windows:

- around `0xD0EE`: `... 7F C0 00 00 00 C0 00 00 ...`
- around `0xD56E`: `... FF FF FF FF 00 00 00 00 FF FF FF FF ...`

Additional write tracing against `0xD560-0xD59F` during a `305`-frame savestate run showed:

- no CPU writes at all to that range after the savestate was loaded

Implication:

- for this savestate repro, the helper is consuming a source stream that is already present in CD RAM
- the late pack loop is not, by itself, generating that `0xD56E` `0xFF` run during the observed post-load window
- this makes the next useful question narrower: where did that CD-RAM source data come from earlier in boot/load, and is it already wrong before the helper starts unpacking it?

## Likely Next Step

The most promising next debugging step is:

1. trace the pack/build routine around `0x4D7D-0x4E00`, not only BAT-targeted `VWR` writes
2. verify how `$22` and the staged `$20D0-$20DF` buffer are populated before the `ASL`/branch sequence
3. compare `LDA (ZP)`, `PHP/PLP`, and the surrounding flag behavior in that path against Geargrafx
4. identify when the active visual regions `0x1000-0x13FF`, `0x3000-0x33FF`, and `0x6000-0x7FFF` are built, since those regions are what the bad frame actually uses
5. keep `MAWR/MARR/VWR` tracing available for correlation, but treat BAT-only evidence as insufficient

In short:

- stay in core CPU/PPU/VDC logic
- do not chase UI masking/cropping for this issue
- focus on the late CPU-driven graphics build logic and the state feeding it

## Short Summary

- The countdown corruption is real live VRAM corruption.
- It reproduces from cold boot.
- It is not primarily a savestate issue.
- It is not primarily a UI/layer/cropping issue.
- It is not coming from VDC VRAM DMA.
- The older “BAT looks like code/data garbage” reading was too strong and is no longer the best summary of the bug.
- The bad frame uses both BG pattern data and large sprite patterns, not only BAT.
- The decisive bad overwrite happens around frame `39`.
- The late overwrite/build logic is produced by a structured CPU-side routine around `0x4D7D-0x4E00`.
- `$27/$28` no longer look like the primary bug by themselves; they move deterministically in that routine.

### Geargrafx `state1` compare: same wait loop, different `$20`

A local Geargrafx MCP session was used against the user's `state1` save.

Important practical note:

- an earlier GUI "error loading rom" popup was caused by a bad Geargrafx CLI invocation during MCP startup, not by the ROM itself
- the corrected launch (`geargrafx --mcp-http`, then `load_media`) loaded the same `.cue` successfully

After `load_state` the embedded state screenshot was still unreliable, but stepping `3` frames produced a clean countdown `5` image in Geargrafx.

Direct compare at that point:

- Geargrafx CPU sat at `PC=0x40D5`, then `0x40D3`/`0x40D5` in the same wait loop around:
  - `0x40D1` `LDA $0B`
  - `0x40D3` `CMP $0B`
  - `0x40D5` `BEQ $40D3`
- our EutherDrive slot-1 savestate after `3` frames also sat at `PC=0x40D3`
- the code bytes around `0x40C6-0x40EE` matched

The strongest CPU-state delta in that matched wait loop was:

- Geargrafx `ZP[0x20..0x2F] = 01 01 9F 01 02 10 00 76 A4 ...`
- EutherDrive `ZP[0x20..0x2F] = 02 01 9F 01 02 10 00 76 A4 ...`

Implication:

- this specific bad state is not explained by different code at the active loop
- it is also not explained by a different source pointer in `$27/$28` at that moment
- the best current differential is an off-by-one in `$20` before control returns to the `0x40D3/0x40D5` wait loop

### `$20` writer chain in the corrupted slot state

Focused CPU-write tracing on our slot-1 savestate (`$20-$28` only) identified the active writers:

- `0xA14E` `STA $20`
- `0xA153` `STZ $20`
- `0xA1BD` `STA $20`
- `0xA1F0` `DEC $20`

Relevant surrounding routine from local ROM/CDRAM disassembly:

- `0xA147` loads `$20`, increments A, and either:
  - stores it back via `0xA14E`, then returns, or
  - zeroes it via `0xA153` and enters the builder path
- `0xA171-0xA17D` advances `$21`
- `0xA17E-0xA192` selects the source pointer in `$27/$28`
- `0xA1BA` begins the data fetch/build helper:
  - `LDA ($27),Y`
  - `STA $20`
  - ...
  - `DEC $20`
  - `BNE $A1C0`

Observed trace pattern in the bad EutherDrive state:

- frame `120`: `A153 -> $20=0`, then `A1BD -> $20=1`, later `A1BD -> $20=2`, then `A1F0` decrements back down
- later frames repeat the same family of writes as `$21` advances (`2`, `3`, `4`, `5`, ...)

Implication:

- the live countdown state is being shaped by the `0xA147-0xA1F0` producer chain before the `0x40D3/0x40D5` wait loop
- since Geargrafx and EutherDrive share the same pointer bytes `$27/$28 = 0x76/0xA4` at the compared clean/bad wait point, the current lead is no longer "wrong table selected"
- the more likely issue is that our core reaches the wait loop one builder-step earlier/later, or decrements `$20` a different number of times before the wait

Current narrowed hypothesis:

- the remaining bug is more likely timing/synchronization around the `0x40C6 -> 0x40D8 -> 0x412B` control path than a bad final renderer interpretation
- especially suspicious are the status/polling interactions that decide when this builder path advances before returning to the `0x40D3/0x40D5` wait loop
