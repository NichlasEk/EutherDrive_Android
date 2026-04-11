# PCE CD BIOS HLE TODO

Last updated: `2026-04-02`

## Purpose

This file is the working backlog for the PC Engine CD BIOS HLE effort.
It should stay short, explicit, and execution-focused:

- what is already working
- what is blocking progress now
- what to implement next
- how to validate each step

## Current state

The HLE subsystem now has:

- a separate dispatcher, context, trace, runtime state, and call catalog
- selectable BIOS modes: `rom`, `auto`, `hle`
- a sidecar harness for repeatable BIOS-less boot testing
- a direct boot profile for `Golden Axe (JP)`
- working startup request handling for the currently observed `E009` RAM/VDC READ(6) branches
- working startup launch offset handling for `E033`
- staged late `E036` VDC streaming from the decoded `E033` READ(6) packet

Current best known BIOS-less boot result for `Golden Axe (JP)`:

- first non-BIOS PC: `frame 0`, `pc=0x441F`
- first visible frame: `frame 18`
- corrected startup launch packet: `08 00 10 24 04 00`
- corrected late stream source: `LBA 4132..4135`
- first mismatch versus prior HLE run after the `E033` offset fix: `frame 422`

## What is blocked now

The main startup data path is no longer pointing at the wrong sectors.
The current blocker is after the corrected late `E033 -> E036` transfer.

Observed current bottleneck:

- startup now reaches visible output
- `E036` consumes the corrected staged queue
- divergence still happens after the late stream is consumed
- remaining BIOS-internal status/timing behavior after READ(6) is still incomplete

This means the next high-value work is no longer "find the right LBA".
It is "model the post-command status surface closely enough that the game continues".

## Priority order

### P0: Post-READ(6) status/timing chain

Target area:

- `E9C5`
- `EA79`
- `EB5E`

Why this is first:

- it is now the first meaningful blocker after the corrected launch/data path
- it likely controls readiness, completion, and CD-visible status transitions
- it affects both RAM and VDC transfer flows

Tasks:

- instrument ROM-backed runs around the late `E033 -> E036` sequence
- log RAM writes, zero-page changes, and CD register changes around the post-command path
- identify which status bytes transition, in what order, and under what timing
- implement the smallest HLE state machine that reproduces those transitions

Done when:

- the HLE run progresses past the current post-`E036` divergence
- the next mismatch moves materially later than frame `422`

Validation:

- run `tools/PceCdBiosHarness`
- compare against `artifacts/pcecd_bios/ga_hle_e033_offsets_20260402`
- check `pce_bios_trace.log` for post-`E036` status transitions

### P1: Finish `E009` branch coverage

Target area:

- `ECBB`
- `ED03`
- `FF=0xFE`
- `FF>=0x02`

Why this matters:

- current `Golden Axe` startup only needs the implemented `FF=0x01` and `FF=0xFF` branches
- broader compatibility will require the remaining `E009` request types
- these branches probably feed other discs' startup and file-loading behavior

Tasks:

- decode the ROM-visible packet-building behavior for the remaining `E009` branches
- capture observed inputs and outputs from BIOS-backed traces
- implement branch-specific HLE behavior instead of returning success stubs

Done when:

- no observed `E009` startup request falls back to `branch_unimplemented`

Validation:

- harness trace contains no `e009 branch_unimplemented`
- call catalog still shows expected callers and stable boot progress

### P1: Tighten `E012` busy/status behavior

Target area:

- `E012`
- follow-up polling after the `D8` packet

Why this matters:

- current implementation matches the packet shape but not the full busy/status surface
- later startup code may depend on exact readiness cadence

Tasks:

- trace BIOS-backed polling around the observed `D8 00 02 15 22 00 00 00 00 40` request
- identify when the BIOS exposes `0x80`, when it clears it, and what memory bytes change
- replace the current one-shot approximation with explicit status progression

Done when:

- `E012` no longer behaves like a static packet launcher
- later code sees the same readiness shape as BIOS-backed boot

Validation:

- compare HLE and ROM-backed traces around the `0x6674 -> E012` path

### P2: Expand `E05A`

Target area:

- non-`A=0x20` cases
- flag side effects
- possible table-driven behavior

Why this matters:

- current implementation only covers the single observed `Golden Axe` case
- this is enough for one path, not enough for general compatibility

Tasks:

- capture all observed callers and register patterns
- map ROM data reads from `FEC2/FEC3` and any related tables
- implement additional cases only when traces show they are real

Done when:

- `E05A` is no longer effectively game-specific

### P2: Replace generic success stubs

Target area:

- `E069`
- `E06C`
- `E06F`
- `E099`

Why this matters:

- current stubs are acceptable for boot probing but are not trustworthy long-term
- later game code may depend on exact side effects

Tasks:

- identify semantic purpose from callers and traces
- replace stubs with tiny explicit handlers where safe
- keep traces verbose enough to document uncertainty

Done when:

- each stubbed call has either a documented safe no-op reason or a real behavior model

## Trace tasks to keep doing

For every newly touched BIOS-facing routine:

1. Record entrypoint, callers, and transfer type.
2. Record input registers and key zero-page state.
3. Record memory and CD-register side effects.
4. Implement the smallest reasonable handler.
5. rerun the harness on at least one real title.
6. update `docs/pcecd_hle_bios.md` and this file.

## Recommended near-term run list

### Golden Axe (JP)

Use as the primary iteration target until post-`E036` divergence moves later.

Expected checks:

- first visible frame should still happen
- `E009` should keep producing `0x001012`, `0x001013`, `0x001023`
- `E033` should keep producing `0x001024`
- no startup READ(6) should land in audio tracks

## Definition of good progress

Any of these count as real progress:

- first mismatch moves materially later
- the game reaches a new stable screen or new code region
- a previously stubbed BIOS call becomes explicit and traceable
- a guessed behavior is replaced with a ROM-backed explanation

## Rules

- do not add or reconstruct BIOS ROM contents
- keep behavior explicit and traceable
- prefer small handlers over hidden hacks
- preserve BIOS-backed mode for A/B comparison
- if uncertain, trace first and mark the handler as partial
