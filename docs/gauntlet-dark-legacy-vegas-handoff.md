# Gauntlet Dark Legacy Vegas Handoff

Date: 2026-05-06

Update: 2026-05-07

## Scope

This pass continued the Gauntlet Dark Legacy / Midway Vegas bring-up in `EutherDrive.Core/Arcade/Vegas/GauntletDarkLegacyAdapter.cs`.

The working strategy is still:

- Build a Gauntlet-compatible Vegas machine adapter, not a full MAME port.
- Use MAME `vegas.cpp` as the hardware map and expected behavior reference.
- Fast-path expensive BIOS loops where they are deterministic and only affect bring-up speed.
- Keep risky hardware guesses uncommitted unless they are proven by probe output.

## Relevant Local Paths

- Repo: `/home/nichlas/EutherDrive_Android`
- Adapter: `EutherDrive.Core/Arcade/Vegas/GauntletDarkLegacyAdapter.cs`
- Plan doc: `docs/gauntlet-dark-legacy-vegas-plan.md`
- ROM directory: `/home/nichlas/roms/MAME/Midway/Vegas/gauntd`
- MAME source: `/home/nichlas/mame/src/mame/midway/vegas.cpp`
- Probe project: `/tmp/eutherdrive-gauntlet-probe/GauntletProbe.csproj`
- Raw CHD sidecar used by probe: `/tmp/gauntd24.raw`

## Commits From This Bring-Up

- `32446b3` Add Gauntlet Dark Legacy Vegas bring-up scaffold
- `1be84de` Fast path Gauntlet BIOS checksum loop
- `f2dea3c` Skip Gauntlet BIOS cache flush loops
- `3a10669` Skip Gauntlet BIOS secondary cache loop
- `533c99e` Fast path Gauntlet BIOS text output
- `3160c40` Initialize Gauntlet R5000 CP0 reset state
- pending/current pass: Model FPGA config done transition, add `slti/sltiu`, and fast-path deterministic FPGA/delay loops

There are unrelated dirty files in the worktree. Do not revert them unless explicitly asked.

## Current Verified State

Core builds:

```sh
dotnet build EutherDrive.Core/EutherDrive.Core.csproj --no-restore /clp:ErrorsOnly
```

Last known committed result before the 2026-05-07 pass:

- Build succeeded.
- Warnings remain existing project warnings.
- `GauntletDarkLegacyAdapter.cs` was clean after commit `3160c40`.

The 600-frame probe runs and reaches:

```text
rom=gauntdl24
frame=600
pc=0x000000009fc00b70
lastOp=0x00b02821
a0=0x0000000000000004
a1=0x000000009fc01f3a
v0=0x0000000000000002
v1=0x0000000080000000
s8=0x000000009fc00000
geometry=DiskGeometry { Cylinders = 34367, Heads = 5, SectorsPerTrack = 26, BytesPerSector = 512, TotalSectors = 4467710 }
identifyStatus=0x48
identifyWord0=0x0040
readStatus=0x48
lba0Words=0x0000,0x0000
```

This was forward progress from earlier cache/text stalls, but not yet past BIOS/FPGA/SIO bring-up.

2026-05-07 pass result:

- `dotnet build EutherDrive.Core/EutherDrive.Core.csproj --no-restore /clp:ErrorsOnly` succeeds.
- The fixed `a1600002` status model gets past the old `v0=2` fail path.
- Missing CPU opcodes `slti/sltiu` were implemented after BIOS halted on `opcode 0x0a`.
- The BIOS FPGA bit-bang loop at `0x1fc02918` is fast-pathed per block by advancing `a1` to `a2` and returning at `0x1fc02a04`.
- The CP0 count delay loop at `0x1fc01a20..0x1fc01a30` is fast-pathed back to `ra`.

Latest 600-frame probe after the current pass:

```text
rom=gauntdl24
frame=600
pc=0x000000009fc028a8
lastOp=0x34840002
a0=0x00000000a1600002
a1=0x0000000000007e81
v0=0x000000008e300000
v1=0x000000000000007d
s8=0x000000009fc00000
geometry=DiskGeometry { Cylinders = 34367, Heads = 5, SectorsPerTrack = 26, BytesPerSector = 512, TotalSectors = 4467710 }
identifyStatus=0x48
identifyWord0=0x0040
readStatus=0x48
lba0Words=0x0000,0x0000
```

This is not attract-mode progress yet, but it does move beyond the earlier fixed failure return and into repeated BIOS config/exception/cache init paths.

## Probe Setup

The temporary probe sets:

```csharp
EUTHERDRIVE_GAUNTDL_RAW_DISK=/tmp/gauntd24.raw
EUTHERDRIVE_GAUNTDL_TRACE_CPU=0
EUTHERDRIVE_GAUNTDL_TRACE_CPU_PC_MIN=ffffffffbfc01f00
EUTHERDRIVE_GAUNTDL_TRACE_CPU_PC_MAX=ffffffffbfc01f30
EUTHERDRIVE_GAUNTDL_CP0_COUNT_STEP=1048576
EUTHERDRIVE_GAUNTDL_CPU_STEPS_PER_FRAME=200000
```

Run:

```sh
dotnet run --project /tmp/eutherdrive-gauntlet-probe/GauntletProbe.csproj --no-restore
```

Useful trace variants:

```sh
EUTHERDRIVE_GAUNTDL_TRACE_MEM=1 dotnet run --project /tmp/eutherdrive-gauntlet-probe/GauntletProbe.csproj --no-build
EUTHERDRIVE_GAUNTDL_TRACE_CPU=1 EUTHERDRIVE_GAUNTDL_TRACE_CPU_PC_MIN=000000009fc027a0 EUTHERDRIVE_GAUNTDL_TRACE_CPU_PC_MAX=000000009fc02ab0 dotnet run --project /tmp/eutherdrive-gauntlet-probe/GauntletProbe.csproj --no-build
```

## What Was Implemented

### BIOS Checksum Fastpath

The known checksum loop at physical `0x1fc038c4` is fast-pathed. It iterates ROM words, updates the two accumulators, and jumps to the loop exit.

### BIOS Cache Fastpaths

Known deterministic cache flush loops now skip:

- `0x1fc039c8/39d0/39d4 -> 0x1fc039dc`
- `0x1fc039f0/39f8 -> 0x1fc03a04`
- `0x1fc03a18/3a20 -> 0x1fc03a2c`
- `0x1fc03a40/3a50/3a54 -> 0x1fc03a5c`

### BIOS Text Fastpaths

Two BIOS text routines are fast-pathed:

- Inline text routine at `0x1fc02c28`
- Pointer text routine at `0x1fc02c5c`

Important detail: the first attempted text-loop fastpath was wrong because it jumped out from the middle of the loop and repeated the boot string. The committed version only fast-paths routine entry points.

### R5000 CP0 Reset State

Reset now initializes the core CP0 registers using MAME-compatible R5000 values:

- `Count = 0`
- `Compare = 0xffffffff`
- `Status = 0x00400004` (`BEV | ERL`)
- `PRId = 0x00002300`
- `Config = 0x00026030`

This did not change the final 600-frame PC, but it is correct baseline state and did not regress the probe.

## FPGA Config Status Model

A minimal FPGA config model is now committed for:

- `0xa1600000..0xa1600003`

Focused CPU trace showed the earlier fixed return `v0=2` came from the second status check after BIOS toggles `CONF` through `0xa1600001`.

Important behavior:

- First read of `0xa1600002` after writing `0xfe` to `0xa1600001` must have bit 1 clear.
- After BIOS writes bit 0 high again (`0xff`) to `0xa1600001`, `0xa1600002 & 0x02` must become non-zero.
- Returning `0x01` was wrong; the meaningful done bit for this path is `0x02`.

The current implementation models only this proven state transition. It does not yet model the `0xa1000000` data sink except by skipping the deterministic BIOS bit-bang loop.

## Current Suspected Blocker

The old fixed failure path was around:

```text
0x1fc00ae8..0x1fc00b88
0x1fc027a0..0x1fc02ab0
```

At `0x1fc00b24`, BIOS calls `0x1fc027a0`. It later branches to a fail path if `v0 != 0`.

Observed at 600 frames:

```text
pc=0x9fc00b70
v0=0x2
```

Disassembly notes:

- `0x1fc027a0` appears to perform FPGA/config loading.
- It touches `0xbfa00010`, `0xbfa00110`, `0xa1600001`, `0xa1600002`, and writes a bitstream-ish sequence through `0xa1600000`.
- It calls delay/timer routine `0x1fc019c4` repeatedly.
- Error code `2` appears in the path around `0x1fc028b8..0x1fc02900`, which corresponds to status low during/after config pulse.

The current suspected blocker is no longer the `v0=2` branch itself. The next pass should trace why BIOS returns/re-enters `0x1fc027a0..0x1fc02ab0` after later exception/cache/init activity, and whether `bfa00000`/`bfc80000` scratch/exception-vector behavior needs a real writable mapping.

## Recommended Next Steps

1. Add a focused probe mode for `0xbfc01e00..0xbfc04260` and `0x9fc027a0..0x9fc02ab0`, with `ra`, `sp`, `a0-a3`, `t5-t8`, and CP0 Cause/EPC.
2. Inspect whether writes to `0xbfa00000..0xbfa00200` should land in a writable boot scratch/exception-vector area instead of being treated as unmapped.
3. Confirm whether `0xbfc80000` reads are a ROM mirror, RAM scratch, or device window in MAME's Vegas/NILE map.
4. Once config/init stops re-entering, expect the next real blocker to be SIO/IDE/Voodoo self-test rather than CPU loops.

## Gotchas

- Do not use the abandoned middle-of-text-loop fastpath. It repeats the boot string and breaks return flow.
- Do not use `a1600002 = 0x01`; trace proved the done bit needed by this BIOS path is `0x02`.
- The probe output can be slow because each frame runs `200000` CPU steps. Use progress every 100 frames or targeted trace windows.
- `EUTHERDRIVE_GAUNTDL_TRACE_MEM=1` is very noisy because ROM fetches are traced too.
- There are unrelated dirty files in the repo, including CPS1/TMNT/32X/SegaCD/UI/README work. Keep Gauntlet edits isolated.
