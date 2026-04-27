# Sega 32X Port Handoff

## Status

- Work paused here.
- No long-running command sessions are left open.
- Build passed:
  - `dotnet build EutherDrive.Headless/EutherDrive.Headless.csproj -c Release --no-restore`
- Current modified files:
  - `EutherDrive.Core/MdTracerAdapter.cs`
  - `EutherDrive.Core/Sega32X/Sega32XScaffoldCore.cs`
  - `EutherDrive.Core/Sega32X/Sega32XSh2Bus.cs`
  - `EutherDrive.Core/Sega32X/Sega32XVdp.cs`
- Existing untracked path:
  - `sh2dis/`

## User Request Context

- Local reference emulator: `/home/nichlas/jgenesis`
- Main focus: real 32X rendering and timing problems, not savestates.
- User specifically asked to remove loop skipping entirely.
- Reported problems:
  - Virtua Fighter fighters still do not render.
  - Knuckles' Chaotix lost graphics after the logo.

## Completed Changes

### Loop Skipping Removed

Loop skipping has been removed from the active 32X code.

Removed or disabled from active code:

- SH2 empty dispatch loop skipping
- SH2 tight delay loop skipping
- stable register poll skipping
- 32X comm-poll loop skip model
- M68K 32X comm-poll loop skipping

Old loop-skip symbols such as `TryExecuteEmptyDispatchLoop`, `TryExecuteTightDelayLoop`, and `SkipToCycleLimit` only remain in `.orig` backup files.

### 32X Timing / Interleave

`MdTracerAdapter` now defaults to M68K-time-driven 32X interleaving instead of the old fixed per-frame SH2 budget path.

Key details:

- Default interleave slice env:
  - `EUTHERDRIVE_S32X_M68K_INTERLEAVE_SLICE`
  - default: `16`
- Legacy timing is still available only with:
  - `EUTHERDRIVE_S32X_LEGACY_FRAME_BUDGET=1`
- M68K-side 32X peripheral accesses now sync the 32X side by default.

### Comm-Port Handling

The recent comm-write FIFO behavior is now opt-in instead of default.

Env:

- `EUTHERDRIVE_S32X_COMM_FIFO_WINDOW`
- default: `0`

Reason: jgenesis exposes the communication ports directly as shared state; the previous recent-write window was not the root issue and was less jgenesis-like.

### VDP / Bus Diagnostics

Added or improved diagnostics:

- `EUTHERDRIVE_S32X_TRACE_SH2_VDP_WRITES=1`
- `EUTHERDRIVE_S32X_TRACE_SH2_FB_WRITES=1`
- `EUTHERDRIVE_S32X_TRACE_SH2_VDP_WRITES_MAX`
- `EUTHERDRIVE_S32X_TRACE_VDP_REG_WRITES_MAX`

The VDP register trace no longer filters out autofill registers.

## Verification

### Build

Command:

```sh
dotnet build EutherDrive.Headless/EutherDrive.Headless.csproj -c Release --no-restore
```

Result:

- Build succeeded.
- 0 warnings.
- 0 errors.

### Knuckles' Chaotix

ROM:

```text
/run/media/nichlas/Atlas/roms/Genesis/32x/Knuckles' Chaotix (32X) (E) [!].32x
```

Command:

```sh
EUTHERDRIVE_HEADLESS_CORE=32x EUTHERDRIVE_HEADLESS_DUMP_DIR=/tmp/euther_chaotix_nocommfifo700 EUTHERDRIVE_HEADLESS_DUMP_32X_LAYER=1 EUTHERDRIVE_HEADLESS_DUMP_32X_RAW=1 dotnet EutherDrive.Headless/bin/Release/net8.0/EutherDrive.Headless.dll "/run/media/nichlas/Atlas/roms/Genesis/32x/Knuckles' Chaotix (32X) (E) [!].32x" 700
```

Result:

- Frame 59: `fb_has_content=True`, `nonzero_pixels=71680`
- Frame 119: `fb_has_content=True`, `nonzero_pixels=39498`
- Final frame 700:
  - `fb_has_content=True`
  - `nonzero_pixels=76800`
  - dump: `/tmp/euther_chaotix_nocommfifo700`

Conclusion:

- Chaotix no longer stays blank after the logo in this headless pass.

### Virtua Fighter

ROM:

```text
/run/media/nichlas/Atlas/roms/Genesis/32x/Virtua Fighter (32X) (E) [!].32x
```

Command:

```sh
EUTHERDRIVE_S32X_SH2_SLAVE_FIRST=1 EUTHERDRIVE_HEADLESS_CORE=32x EUTHERDRIVE_HEADLESS_DUMP_DIR=/tmp/euther_vf_slavefirst960 EUTHERDRIVE_HEADLESS_DUMP_32X_LAYER=1 EUTHERDRIVE_HEADLESS_DUMP_32X_RAW=1 dotnet EutherDrive.Headless/bin/Release/net8.0/EutherDrive.Headless.dll "/run/media/nichlas/Atlas/roms/Genesis/32x/Virtua Fighter (32X) (E) [!].32x" 960
```

Result:

- Final frame 960:
  - `fb_has_content=True`
  - `nonzero_pixels=76092`
  - fingerprint: `0x4ECFB7FB1A70BDB9`
  - dump: `/tmp/euther_vf_slavefirst960`
- Fighters are still not visible.
- `EUTHERDRIVE_S32X_SH2_SLAVE_FIRST=1` did not fix the VF issue.

## Important Virtua Fighter Findings

EutherDrive and jgenesis match for a while, then diverge.

Known comparison:

- EutherDrive frame 840 raw framebuffer and CRAM matched jgenesis frame 900 exactly.
- After that, EutherDrive diverges.
- jgenesis frame 960 already shows the fighters.
- EutherDrive frame 900/960 remains on the wrong visual output.

jgenesis frame 960 state from prior raw dump:

- fighters visible
- CRAM hash matches the later Euther CRAM hash
- system state includes FM / VDP access on SH2 side

EutherDrive around frame 900/960:

- CRAM appears to have reached the expected later palette state.
- The actual framebuffer content is still wrong.
- Trace showed Euther alternating around FM / comm state and not landing in the same final state as jgenesis.

## Failed Experiment To Avoid

Do not simply make SH2 bus wait states advance the scheduler counter.

This was tried by making direct bus latency update scheduler timing as well as `CycleCounter`.

Result:

- Virtua Fighter boot regressed badly.
- M68K got stuck around `0x880874`.
- No useful content appeared through 1200 frames.

That experiment was reverted.

Current expected state:

- `IncrementCycleCounter` updates `CycleCounter` and `_schedulerCycleCounter`.
- Direct bus wait additions remain only on `CycleCounter`.
- `LoadState` uses `_schedulerCycleCounter = CurrentCpu.CycleCounter`.
- `rg -n "AdvanceCycleCounter"` should return nothing.

## Next Debugging Targets

### 1. Add Euther `.sys.txt` Raw Dump

jgenesis dump helper writes:

- `.fb0.bin`
- `.fb1.bin`
- `.cram.bin`
- `.vdp.txt`
- `.sys.txt`

EutherDrive currently dumps raw VDP state, but not an equivalent system-register `.sys.txt`.

Adding this would make it easier to compare:

- adapter enabled
- SH2 reset
- FM / VDP access
- comm ports
- interrupt pending/enabled bits
- DMA state

Likely place:

- `EutherDrive.Core/MdTracerAdapter.cs`
- `Dump32XRawVdpState(string prefix)`

Likely backing data:

- `EutherDrive.Core/Sega32X/Sega32XRegisters.cs`
- `Sega32XSystemRegisters`

### 2. Compare SH2 Cache Semantics

Likely source of the Virtua Fighter divergence.

Compare EutherDrive:

- `EutherDrive.Core/Sega32X/Sega32XSh2Bus.cs`
- cache data array functions
- cache address array functions
- `TryReplaceCache`
- `WriteThroughCache*`
- associative purge

Against jgenesis:

- `/home/nichlas/jgenesis/cpu/sh2-emu/src/cache.rs`
- `/home/nichlas/jgenesis/backend/s32x-core/src/bus.rs`

Virtua Fighter reaches PCs in the `0xC00000xx` cache-data-array area in Euther traces, so exact cache-array behavior may matter.

### 3. Compare 32X Bus Timing And Access Gating

Compare:

- `/home/nichlas/jgenesis/backend/s32x-core/src/bus.rs`
- `EutherDrive.Core/Sega32X/Sega32XSh2Bus.cs`
- M68K 32X mappings in EutherDrive host bridge

Focus on:

- SH2 VDP register access when FM is M68K vs SH2
- frame buffer writes when FM is wrong
- CRAM reads/writes
- byte writes as read-modify-write
- M68K frame buffer / VDP / CRAM gating

### 4. Region / Timing Difference

EutherDrive detects the VF ROM as EU from raw header `A`.

jgenesis region detection logic treats hex region char `A` as Europe because bit 3 is set, so this likely matches.

Still worth checking if any forced timing or frame count mismatch affects the 840/900 alignment.

Relevant jgenesis files:

- `/home/nichlas/jgenesis/backend/genesis-core/src/cartridge.rs`
- `/home/nichlas/jgenesis/backend/s32x-core/src/core.rs`
- `/tmp/jg32xdump/src/main.rs`

## Useful Commands

Build:

```sh
dotnet build EutherDrive.Headless/EutherDrive.Headless.csproj -c Release --no-restore
```

Chaotix check:

```sh
EUTHERDRIVE_HEADLESS_CORE=32x EUTHERDRIVE_HEADLESS_DUMP_DIR=/tmp/euther_chaotix_nocommfifo700 EUTHERDRIVE_HEADLESS_DUMP_32X_LAYER=1 EUTHERDRIVE_HEADLESS_DUMP_32X_RAW=1 dotnet EutherDrive.Headless/bin/Release/net8.0/EutherDrive.Headless.dll "/run/media/nichlas/Atlas/roms/Genesis/32x/Knuckles' Chaotix (32X) (E) [!].32x" 700
```

Virtua Fighter check:

```sh
EUTHERDRIVE_HEADLESS_CORE=32x EUTHERDRIVE_HEADLESS_DUMP_DIR=/tmp/euther_vf_next960 EUTHERDRIVE_HEADLESS_DUMP_32X_LAYER=1 EUTHERDRIVE_HEADLESS_DUMP_32X_RAW=1 dotnet EutherDrive.Headless/bin/Release/net8.0/EutherDrive.Headless.dll "/run/media/nichlas/Atlas/roms/Genesis/32x/Virtua Fighter (32X) (E) [!].32x" 960
```

jgenesis dump:

```sh
cargo run --release --manifest-path /tmp/jg32xdump/Cargo.toml -- "/run/media/nichlas/Atlas/roms/Genesis/32x/Virtua Fighter (32X) (E) [!].32x" 960 /tmp/jg32x_vf_960.ppm
```
