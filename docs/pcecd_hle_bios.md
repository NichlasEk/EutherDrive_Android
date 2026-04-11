# PCE CD HLE BIOS

## Legal boundary

This subsystem is a behavioral high-level emulation layer for PC Engine CD BIOS services. It must not embed, derive, reconstruct, or ship a copyrighted System Card ROM image.

## Current shape

The HLE work lives in `PCE_CD_Core` and is intentionally separate from the generic CPU and CD core paths:

- `PceCdBiosDispatcher`: entrypoint routing and handler table
- `PceCdBiosContext`: controlled access to CPU registers, memory, CD media, and trace helpers
- `PceCdBiosTrace`: structured BIOS-facing trace output
- `PceCdBiosCallCatalog`: observed call catalog with status and callers
- `PceCdBiosMode`: ROM vs AUTO vs HLE selection

`EutherDrive.Core/PceCdAdapter.cs` keeps the public mode switch and preserves BIOS-backed loading. `HLE` is opt-in, and `AUTO` falls back to HLE only when no BIOS path resolves.

## Boot modes

- `rom`: use the external BIOS file path if present
- `auto`: use BIOS if found, otherwise HLE
- `hle`: force BIOS-less boot path

The current selectable control is `EUTHERDRIVE_PCE_CD_BIOS_MODE=rom|auto|hle`.

## Separate harness

See also: `docs/pcecd_hle_bios_todo.md` for the execution backlog and current blockers.

Use the sidecar tool instead of the main frontend when iterating on BIOS behavior:

```bash
dotnet run --project tools/PceCdBiosHarness/PceCdBiosHarness.csproj -- \
  --rom "/path/to/game.cue" \
  --bios-mode hle \
  --seconds 4 \
  --snapshot-frames 0,60,120
```

Artifacts are written under `artifacts/pcecd_bios/...` and currently include:

- `pce_trace.log`: frame-by-frame determinism trace
- `pce_bios_trace.log`: BIOS entry/exit and HLE memory/load trace
- `pcecd_bios_calls.md`: observed BIOS call catalog
- `summary.json`
- `summary.md`
- `pcesnap_*`: debug snapshots

## Current supported calls

| Entry | Status | Notes |
| --- | --- | --- |
| `0xFFF0` | `implemented` | Synthetic HLE reset trap. Direct-load profile for `Golden Axe (JP)` currently loads sectors `4086..4093` to `0x4000` and transfers control to `0x4000`. The late `E036` stream is now staged from the observed startup path at `0xE033` instead of directly from reset. |
| `0xE009` | `partially implemented` | Observed startup loader request path. Current HLE models the ROM-visible `EC05` workspace prelude, applies the slot offset from `$2274..$2276` to `$FC-$FE`, and implements the two boot-relevant branches seen in `Golden Axe`: `FF=0x01` builds a READ(6) packet and streams the payload into the caller RAM buffer at `$FA/$FB`, while `FF=0xFF` builds a READ(6) packet and streams the payload to VDC via MAWR from `$FA/$FB`. The later `ECBB/ED03` branches remain unimplemented. |
| `0xE012` | `partially implemented` | Observed startup loader follow-up path. Current HLE models the ROM-visible `EE10` workspace prelude, mirrors `FB & 0xC0` into `$2255`, seeds `$224C` with `0xD8`, places `$F8-$FA` into the request slot chosen by `$FB`, and issues the traced command packet from the prepared workspace. The observed `Golden Axe` packet is `D8 00 02 15 22 00 00 00 00 40`, which leaves the BIOS status surface at `0x80`. |
| `0xE01E` | `partially implemented` | Observed status poll. Current HLE only implements the first known `Golden Axe` readiness promotion at `0x3E04 -> 0x03`. |
| `0xE02D` | `implemented` | Matches the public ROM entry `F379`: `A &= 0x0F`, `STA $180F`, `RTS`. |
| `0xE033` | `partially implemented` | Observed startup launch path. Current HLE models the ROM-visible `F393` busy rejection, resets `$2273`, mirrors the `FF==0` helper writes to `$1808/$1809/$180D`, applies the same `$2274..$2276` slot offset used by `F104`, builds the launch workspace, and issues a traced READ(6) packet directly from `$224C..$2250`. The late `E036` stream is now staged from the decoded packet fields instead of a fixed request signature. |
| `0xE05A` | `partially implemented` | Observed once in `Golden Axe` before `0xE036`. Current HLE only models the `A=0x20, X=0x00, Y=0x00` case and primes `X=0x03` from ROM trace. |
| `0xE069` | `stubbed` | Observed in loaded game code after direct boot. |
| `0xE06C` | `stubbed` | Observed in loaded game code after direct boot. |
| `0xE06F` | `stubbed` | Observed in loaded game code after direct boot. |
| `0xE099` | `stubbed` | Observed in loaded game code after direct boot. |
| `0xE05D` | `partially implemented` | Registers an IRQ1 handler when `A=1`. Current observed `Golden Axe` handler is `0x40E9`. |
| `0xE036` | `partially implemented` | Observed via `JMP` from `0x66DF`. HLE now models the `ZP $FF == 0xFF` VDC stream path and the `ZP $FF == 0x00` linear stream path, consuming staged HLE CD data before falling back to the live CD port. |

## Known observed calls

These are catalogued and traced, but not yet behaviorally implemented beyond basic tracing or stubs:

- `0xE00F`
- `0xE030`
- `0xE03C`
- `0xE036`
- `0xE05A`
- `0xE07B`
- `0xE08A`
- `0xE063`

## Per-game observations

### Golden Axe (JP)

BIOS-backed baseline boot shows early execution around BIOS/internal CD code in the `0xE8xx..0xFExx` region and disc traffic consistent with startup reads. Observed sector traffic during baseline startup includes:

- `3590..3591`
- `4078`
- `4079`
- `4080..4085`
- `4084`
- `4085`
- `4086..4093`

The current HLE proof-of-concept bypasses BIOS ROM reset and directly loads `4086..4093` into RAM with the same MPR layout observed in the BIOS-backed path:

- `FF,F8,80,81,82,83,84,00`

The late observed `E036` data stream from sectors:

- `4132..4135`

is still an explicit, traceable approximation for the currently skipped BIOS loader work, but it is no longer injected at reset or keyed by a fixed game profile. The staged stream is now armed from the decoded `E033` READ(6) packet itself:

- workspace bytes: `08 00 10 24 04`
- decoded packet: `08 00 10 24 04 00`
- decoded read: `LBA 0x001024`, `count 4`

That keeps the approximation inside the BIOS-facing startup surface instead of the reset trap and removes the previous `Golden Axe`-specific `4132..4135` signature table from the dispatcher.

Direct-loaded `Golden Axe` startup code also exposes the request tables that currently feed these calls:

- `E009/E033`: 7-byte records based at `0x53F1`, laid out as `FF, FC, FD, FE, F8, FA, FB`
- `E012`: 8-byte records based at `0x55C7`, laid out as `Control, FF, F8, F9, FA, FC, FD, FE`

Recent harness evidence:

- the corrected `E009` slot-offset handling now produces the observed startup READ(6) packets `08 00 10 12 01 00`, `08 00 10 13 08 00`, and `08 00 10 23 01 00` instead of falling into audio-track sectors
- the latest HLE harness run reaches the first visible frame at `frame 18`, which is the first BIOS-less run in this branch that produces video at all
- the corrected `E033` slot-offset handling now launches `08 00 10 24 04 00` and stages the late stream from `LBA 4132..4135` instead of landing in audio-track `LBA 542`
- the current startup-handler run diverges from the previous queue-only HLE baseline at frame `13`, which is expected because `E009/E033/E02D/E012` now perform ROM-visible workspace writes instead of returning through empty stubs
- the `E033` offset fix does not disturb early boot: compared against the prior HLE run, the first trace mismatch now moves out to frame `422`, where `0xE036` finally consumes the corrected `E033`-staged queue
- the focused HLE VDC trace now starts with `FA 07 FA 07 F9 07 FD 03 FC 03 FE 01`, matching the ROM-backed `F452` stream prefix instead of the earlier `00 00` data pattern
- startup-facing HLE traces now record the decoded `E009/E012/E033` request signatures explicitly and preserve the ROM-visible workspace setup in `$224C..$2273`, including the corrected `EC05/F393` copy of `$FC-$FE`, the `F104` slot-offset addition from `$2274..$2276`, and the `EE10` mode selection from `$FB`
- early visible output now exists, but matching the startup byte stream is still not sufficient for stable boot completion

That is enough to transfer execution into game code and reach the following BIOS-facing calls from loaded code:

- `0xE099`
- `0xE069`
- `0xE06F`
- `0xE06C`
- `0xE05D`

The HLE path now gets materially further than the initial direct-load proof-of-concept:

- synthetic IRQ traps keep execution out of `0xFFFF/0x0002` dead ends
- `0xE05D` captures the observed IRQ1 handler registration at `0x40E9`
- `0xE01E` no longer spins tens of thousands of times; the observed `0x3E04` readiness poll is collapsed to a single HLE hit
- ROM baseline and HLE now both show the late `0xE05A -> 0xE036` path, which confirms it is part of normal `Golden Axe` boot

Current limitation: boot still diverges after the `0xE05A -> 0xE036` path. HLE now feeds `E036` with the corrected staged sector payload from `0xE033`, but the remaining BIOS-internal state transitions after that stream are still incomplete. Late HLE runtime still remaps MPR4/MPR5 to `0x04/0x05`, changes VRAM content, and then settles into a repeating `0x57BD/0x57BF/0x57C4/0x57C6` IRQ-driven state after the initial visible output.

## Trace model

The BIOS trace is designed to keep guesses explicit:

- control transfers into BIOS/HLE entrypoints
- handler entry/exit register state
- sector reads initiated by HLE boot logic
- HLE memory writes caused by those reads
- per-run call catalog generation

If a behavior is uncertain, keep the handler small, trace it, and leave the catalog status at `traced`, `stubbed`, or `partially implemented`.

## Next priorities

1. Implement the remaining `0xE009` branches through `ECBB/ED03`, especially the `FF=0xFE` and `FF>=0x02` cases that still fall back to trace-only handling.
2. Replace the current traced `E012` AudioStartPos approximation with a fuller status/timing model so the `0x80` busy surface matches ROM-backed polling more closely.
3. Tighten `0xE009` post-command status behavior around `E9C5/EA79/EB5E` so the RAM/VDC transfer loops observe the same status cadence as ROM-backed boot.
4. Determine the broader semantics of `0xE05A`, especially non-`A=0x20` requests and any flag side effects.
5. Replace the remaining generic success stubs on load/poll/status services with traced behavior.
