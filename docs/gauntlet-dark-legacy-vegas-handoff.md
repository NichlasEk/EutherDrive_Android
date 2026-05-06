# Gauntlet Dark Legacy Vegas Handoff

Date: 2026-05-06

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

There are unrelated dirty files in the worktree. Do not revert them unless explicitly asked.

## Current Verified State

Core builds:

```sh
dotnet build EutherDrive.Core/EutherDrive.Core.csproj --no-restore /clp:ErrorsOnly
```

Last known result:

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

This is forward progress from earlier cache/text stalls, but not yet past BIOS/FPGA/SIO bring-up.

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

## What Was Tried And Reverted

A minimal FPGA config stub was tried for:

- `0xa1600000..0xa1600003`
- `0xa1000000`

The idea was to make `0xa1600002` report `CFG_DONE`. Memory trace confirmed the stub was hit:

```text
write8 00000000a1600001 00000001 FPGA config
write8 00000000a1600001 000000fe FPGA config
read8  00000000a1600002 00000001 FPGA config
write8 00000000a1600000 ... FPGA config
```

But BIOS still reset/re-entered the same boot sequence and still ended around `0x9fc00b70`. The stub was removed and not committed.

## Current Suspected Blocker

The current failure path is around:

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

The next pass should trace CPU through `0x1fc027a0..0x1fc02ab0` with registers, not only memory. We need to know exactly which branch sets `v0=2`.

## Recommended Next Steps

1. Add a focused probe mode that stops when PC enters `0x9fc027a0..0x9fc02ab0` and dumps registers every branch.
2. Confirm whether the `0xa1600002` status model needs bit 0, bit 1, or state transitions rather than a fixed value.
3. Inspect MAME Vegas/NILE mapping for the `0xa1000000` and `0xa1600000` boot FPGA/status area. It may not be plain CPU RAM or normal CS range.
4. Once FPGA/config passes, expect the next blocker to be SIO/IDE/Voodoo self-test rather than CPU loops.
5. Avoid committing any FPGA/status stub until the probe shows that it changes the final PC past the current failure route.

## Gotchas

- Do not use the abandoned middle-of-text-loop fastpath. It repeats the boot string and breaks return flow.
- Do not assume `a1600002 = 0x01` is sufficient. Trace showed that it was read, but boot still failed.
- The probe output can be slow because each frame runs `200000` CPU steps. Use progress every 100 frames or targeted trace windows.
- `EUTHERDRIVE_GAUNTDL_TRACE_MEM=1` is very noisy because ROM fetches are traced too.
- There are unrelated dirty files in the repo, including CPS1/TMNT/32X/SegaCD/UI/README work. Keep Gauntlet edits isolated.
