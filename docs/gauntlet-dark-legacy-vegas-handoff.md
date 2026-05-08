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
- `bda14ab` Advance Gauntlet Vegas FPGA bring-up
- pending/current pass: add minimal NILE/VRC5074 CPU-register window for `0x1fa00000..0x1fa003ff`

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

Current pass result:

- Added a little-endian NILE register bank mapped through physical `0x1fa00000..0x1fa003ff`, visible to BIOS as `0xbfa00000..0xbfa003ff`.
- This matches MAME's `vrc5074_device::device_start()`, which installs:
  - CPU registers at `0x1fa00000..0x1fa001ff`
  - PCI config alias at `0x1fa00200..0x1fa002ff`
  - serial registers at `0x1fa00300..0x1fa0033f`
- The main worktree build is currently blocked by unrelated dirty `TmntAdapter.cs` errors.
- The Vegas patch was build-tested in `/tmp/eutherdrive-gauntlet-nextpass` against a clean `bda14ab` worktree plus the local `Cps1Ym2151.cs` change needed by that baseline:

```text
Build succeeded.
322 Warning(s)
0 Error(s)
```

Probe against the test worktree reached:

```text
rom=gauntdl24
frame=600
pc=0x00000000bfc00924
lastOp=0x64420000
a0=0x0000000080042e64
a1=0x00000000bfa00000
v0=0x0000000080000000
v1=0x0000000026300000
s8=0x00000000bfc00000
```

This means the BIOS is now executing the NILE register POST path instead of reading `bfa00000` as unmapped. It then repeatedly enters the exception/POST handler around `0xbfc00880..0xbfc00970`.

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

After the NILE register bank, `bfa00000` is no longer suspected scratch RAM; it is the VRC5074/NILE register window. Focused trace showed repeated execution through:

```text
bfc00880..bfc008b8
bfc00900..bfc00970
```

The first read of the ROM text pointer at `0xbfc42e64` was the normal banner:

```text
EPROM Boot code. Version: Dec 14 1999 13:37:53
```

Older 120-frame probe before the CP0/NILE/UART pass ended at:

```text
pc=0x00000000bfc03968
lastOp=0x11200003
cp0 status=0x0000000034400000 cause=0x0000000000000000 epc=0x0000000000000000 errorepc=0x0000000000000000
a0=0x000000000000000d a1=0x00000000bfa00000 v0=0x00000000bfc009b0 v1=0x0000000080000000 s8=0x00000000bfc00000
romTableStart=0xbfa00000
romTableEnd=0x00000000
```

This means the current loop is not an emulated CPU exception: CP0 `Cause`, `EPC`, and `ErrorEPC` remain zero. The repeated path is BIOS POST/print/control flow around `0xbfc03940..0xbfc03990`, calling through `0xbfc009b0`.

Focused trace of the loop:

```text
bfc03940 nop
bfc03944 addiu t5,zero,0x10
bfc03948 daddu t6,zero,zero
bfc0394c daddu t7,zero,zero
bfc03950 lui at,0x8000
bfc03954 and t1,t0,at
bfc03958 bne t1,zero,...
bfc03960 andi t1,t0,0x0008
bfc03964 beq t1,zero,...
bfc0396c lui t6,0x0004
bfc03970 addiu t7,zero,0x20
bfc03974 jr ra
bfc03988 daddu ra,v0,zero
bfc0398c mfc0 v0,Status
bfc03990 lui v1,0x0001
```

That blocker moved after the current pass. The loop was caused by incomplete CP0 transfer behavior, not a memory-map exception.

## 2026-05-07 Next Pass Result

This pass added three bring-up fixes:

- CP0 `mfc0/mtc0` now use the 32-bit transfer path, while `dmfc0/dmtc0` keep the 64-bit path.
- CP0 `Status` writes now apply MAME's R4000/R5000 write mask: `data & ~0x01a80000`.
- CP0 `Cause` writes only preserve software interrupt bits, and `Compare` clears the timer interrupt bit.
- The BIOS NILE init table at `0xbfc01cc8` is fast-pathed through the loop at `0xbfc01f08`.
- The TLB invalid-entry helper at `0xbfc041b8` is fast-pathed as a no-op TLB write with IE cleared.
- The minimal NILE UART line-status read at `0xbfa00328` now returns `0x60` (`THRE | TEMT`), letting BIOS serial output proceed.

Relevant MAME references:

- `r4000_base_device::cp0_set()` masks CP0 `Status` writes with `~0x01a80000`.
- `vrc5074_device::serial_r()` delegates the `0x1fa00300..0x1fa0033f` window to an INS8250 UART.

Latest 120-frame probe after this pass:

```text
rom=gauntdl24
frame=120
pc=0x00000000bfc0085c
lastOp=0x40804800
cp0 status=0x0000000034400000 cause=0x0000000000000000 epc=0x0000000000000000 errorepc=0x0000000000000000
a0=0x0000000002ee0000 a1=0xffffffffffffff99 v0=0x000000005f300000 v1=0x000000000000007d s8=0x00000000bfc00000
```

This is forward progress from:

- `bfc03968` CP0 status helper
- `bfc01f10` NILE init table
- `bfc041d8` TLB invalid-entry helper
- `bfc02bbc` UART transmit-ready wait

The next suspected blocker is now around `0xbfc00850..0xbfc00870`. It writes CP0 `Count`/`Cause`, then jumps into the BIOS POST/exception-vector path. CP0 `Cause`, `EPC`, and `ErrorEPC` remain zero at the 120-frame endpoint, so do not assume this is a real emulated exception yet.

## Current Trace/Debug Additions

`MipsR5000Core` now exposes:

- `Cp0Status`
- `Cp0Cause`
- `Cp0Epc`
- `Cp0ErrorEpc`

CPU trace lines and unsupported-op halts now include those CP0 values. CPU trace also accepts:

```text
EUTHERDRIVE_GAUNTDL_TRACE_CPU_LIMIT=200
```

Use it with a narrow PC window. Without a limit, this BIOS loop is too noisy.

## Recommended Next Steps

1. Trace `0xbfc00850..0xbfc00890` with `EUTHERDRIVE_GAUNTDL_TRACE_CPU_LIMIT=200` and include GPRs beyond `a0/a1/v0/v1` if needed.
2. Decode whether `0xbfc00850` is just another delay/POST helper that can be fast-pathed, or whether it depends on CP0 `Count`, `Cause`, or `ErrorEPC` semantics.
3. Re-run the 120-frame probe first; only use a 600-frame probe once PC moves beyond `0xbfc0085c`.
4. Confirm whether `0xbfc80000` is a ROM mirror, RAM scratch, or device window once this POST path moves.
5. Once config/init stops re-entering, expect the next real blocker to be SIO/IDE/Voodoo self-test rather than CPU loops.

## 2026-05-07 Evening Pass Result

The `0xbfc00850` loop was caused by incomplete CP0 `Config` write semantics, not by a real exception or POST blink that should be fast-pathed.

Focused trace showed BIOS executing:

```text
bfc00944 mfc0 v0,Config
bfc00968 ori  v0,v0,0x0008
bfc0096c mtc0 v0,Config
```

Before this pass, `WriteCp0(Config)` only preserved bit 31. MAME `r4000_base_device::cp0_set()` preserves runtime-writable `CONFIG_WM = 0x0000003f`, so BIOS could never observe the low Config bits it set. The adapter now preserves the low six Config bits.

Build result:

```text
dotnet build EutherDrive.Core/EutherDrive.Core.csproj --no-restore /clp:ErrorsOnly
Build succeeded.
324 Warning(s)
0 Error(s)
```

Probe checkpoints after the Config fix:

```text
frame=5
pc=0x00000000bfc039ec
lastOp=0x008b2821
cp0 status=0x0000000034410000 cause=0x0000000000000000 epc=0x0000000000000000 errorepc=0x0000000000000000

frame=20
pc=0x000000009fc0113c
lastOp=0x04110222
cp0 status=0x0000000034400000 cause=0x0000000000000000 epc=0x0000000000000000 errorepc=0x0000000000000000

frame=120
pc=0x00000000bfc02ba0
lastOp=0x40034800
cp0 status=0x0000000034400000 cause=0x0000000000000000 epc=0x0000000000000000 errorepc=0x0000000000000000
```

The new 120-frame endpoint is BIOS serial output at `0xbfc02b88..0xbfc02bac`. It reads NILE UART line status from `0xbfa00328`; the existing `0x60` line-status stub satisfies the transmit-ready check (`0x20`), so this is not yet proven to be a hard blocker.

Follow-up in the same pass:

- Added a guarded BIOS serial char fastpath at routine entry `0xbfc02b88`.
- The fastpath only fires when `ra` returns into boot ROM and `a0` is a byte, then returns to `ra`.
- This skips only the serial side effect; UART line-status behavior remains unchanged.

Probe checkpoints after the serial char fastpath:

```text
frame=20
pc=0x000000009fc02c00
lastOp=0x0082082a
cp0 status=0x0000000034400000 cause=0x0000000000000000 epc=0x0000000000000000 errorepc=0x0000000000000000

frame=120
pc=0x000000009fc01140
lastOp=0x00000000
cp0 status=0x0000000034400000 cause=0x0000000000000000 epc=0x0000000000000000 errorepc=0x0000000000000000

frame=240
pc=0x000000009fc019e0
lastOp=0x38630027
cp0 status=0x0000000034400000 cause=0x0000000000000000 epc=0x0000000000000000 errorepc=0x0000000000000000
```

Follow-up trace of `0x9fc019c0..0x9fc01a20` showed this is the earlier CP0 PRId/Config-based delay helper starting at `0x1fc019c4`, called repeatedly with different `a0` delay values. The existing count-delay fastpath now also covers `0x1fc019c4..0x1fc019ec`.

Latest checkpoint after that delay helper extension:

```text
frame=120
pc=0x000000009fc01148
lastOp=0x0211082b
cp0 status=0x0000000034400000 cause=0x0000000000000000 epc=0x0000000000000000 errorepc=0x0000000000000000
```

Next useful step:

1. Trace around `0x9fc01130..0x9fc01170` if the next pass still ends near `0x9fc01148`.
2. Run a longer non-trace probe only after confirming whether that endpoint is a hot loop or just a transient call site.
3. If it exits into `0x9fc02c00`/text output again, keep serial/text fastpaths at routine entries only.

Additional follow-up in the same bringup pass:

- Added a guarded FPGA serial-stream fastpath at `0x1fc01118`.
- The routine bit-bangs boot ROM bytes to `0xa1600000`, then jumps back through `0x1fc00800`; the fastpath marks FPGA config done and preserves the expected loop-end registers.
- Rechecked the later `fpgaload()` CFG_DONE poll at `0x1fc02a30..0x1fc02a38`: `bit0 != 0` branches to the success path at `0x1fc02a90`, while timeout falls through to the embedded `fpgaload(): timed out waiting for CFG_DONE` text and returns code 4.
- Added a guarded entry fastpath for the BIOS cache helper at `0x1fc03980`, plus inner-loop coverage for the next cache helper at `0x1fc03a88..0x1fc03ae4`.

Verification after these changes:

```text
dotnet build EutherDrive.Core/EutherDrive.Core.csproj --no-restore /clp:ErrorsOnly
Build succeeded.
324 Warning(s)
0 Error(s)

dotnet build /tmp/eutherdrive-gauntlet-probe/GauntletProbe.csproj /clp:ErrorsOnly
Build succeeded.
365 Warning(s)
0 Error(s)

EUTHERDRIVE_GAUNTDL_CPU_STEPS_PER_FRAME=5000 frame=120
pc=0x000000009fc01cb4
lastOp=0x0296082a
cp0 status=0x0000000034400000 cause=0x0000000000000000 epc=0x0000000000000000 errorepc=0x0000000000000000
```

The new endpoint is a BIOS loop around `0x1fc01ca4..0x1fc01cb8` that repeatedly calls a function pointer from a computed table until `s4 == 0x20`. This should be the next trace target. A full `200000` steps/frame probe is still too heavy after the reboot/freeze recovery, so use the 5000-step checkpoint first and only scale back up after the `0x1fc01cb4` loop is understood.

## 2026-05-07 Late Pass Result

The `0x1fc01ca4..0x1fc01cb8` loop is a 32-entry TLB clear loop. It computes the helper pointer `0x9fc041b4`, calls it with `a0 = 0..31`, and returns through `s0`. Added a guarded TLB-clear-loop fastpath that preserves the final CP0/TLB-visible state used by the existing single-entry helper.

The next BIOS stop was `0x1fc02b18..0x1fc02b48`, a small UART/NILE register init table using address/value pairs at `0x1fc02ac0`. Added a guarded UART init table fastpath that writes the table through `VegasMemoryMap.Write32()` until the zero terminator.

The later stop around `0x1fc027e0..0x1fc028b0` is the `fpgaload()` preamble. It pulses `a1600001`, checks `a1600002` low/high status, then sets up `a1/a2` for the existing `0x1fc02918` block-load fastpath. Added a guarded preamble fastpath that preserves the source/end registers and jumps into the existing block fastpath.

Verification after these changes:

```text
dotnet build EutherDrive.Core/EutherDrive.Core.csproj --no-restore /clp:ErrorsOnly
Build succeeded.
324 Warning(s)
0 Error(s)

dotnet build /tmp/eutherdrive-gauntlet-probe/GauntletProbe.csproj /clp:ErrorsOnly
Build succeeded.
0 Warning(s)
0 Error(s)

EUTHERDRIVE_GAUNTDL_CPU_STEPS_PER_FRAME=5000 frame=400
pc=0x00000000007a0e98
lastOp=0x00000000
cp0 status=0x0000000034400000 cause=0x0000000000000000 epc=0x0000000000000000 errorepc=0x0000000000000000

EUTHERDRIVE_GAUNTDL_CPU_STEPS_PER_FRAME=5000 frame=1000
pc=0x0000000001312998
lastOp=0x00000000
cp0 status=0x0000000034400000 cause=0x0000000000000000 epc=0x0000000000000000 errorepc=0x0000000000000000
```

This is the first checkpoint in this bringup where PC leaves BIOS ROM and runs in low RAM. The bad news is `lastOp=0`, and PC keeps advancing through zero-filled RAM, so the next blocker is likely missing game-code/data population before the BIOS jumps out of ROM.

Next useful step:

1. Trace the handoff from BIOS to RAM around `0x9fc00b00..0x9fc00c80` and the jump target setup, with register fields that include `t5/t6/t7/t8/gp/fp`.
2. Add a probe-side memory peek for the RAM entry window before and after `fpgaload()` to confirm whether disk/ROM data is copied into `0x007a0000`.
3. If RAM is still zero after fpgaload, shift focus to disk/IDE DMA or the raw CHD read path rather than adding more BIOS fastpaths.

Follow-up check against `docs/gauntlet-dark-legacy-vegas-plan.md`:

- The bring-up is now squarely in Phase 2: ROM, CHD, IDE.
- A temp probe-side `EUTHERDRIVE_GAUNTDL_PEEK` hook was added under `/tmp/eutherdrive-gauntlet-probe` only.
- RAM peeks at frames 300, 400, and 1000 show the tested RAM entry/load windows are still zero:

```text
frame=300 pc=0x00000000005b8a18 lastOp=0x00000000
peek 0x007a0000 nonzeroWords=0
peek 0x00989000 nonzeroWords=0
peek 0x01312000 nonzeroWords=0

frame=400 pc=0x00000000007a0e98 lastOp=0x00000000
peek 0x007a0000 nonzeroWords=0
peek 0x00989000 nonzeroWords=0
peek 0x01312000 nonzeroWords=0

frame=1000 pc=0x0000000001312998 lastOp=0x00000000
peek 0x007a0000 nonzeroWords=0
peek 0x00989000 nonzeroWords=0
peek 0x01312000 nonzeroWords=0
```

The current adapter has an `IdeDiskDevice`, raw sidecar support, identify, and PIO read-sector behavior, but it is not yet connected to the Vegas memory/PCI/SIO path the BIOS/game code would use for real program loading. Do not add more BIOS fastpaths until the IDE register/DMA path is wired and traced.

## 2026-05-07 PCI/IDE Bring-Up Follow-Up

Implemented the first minimal Vegas PCI/IDE path in `VegasMemoryMap`:

- NILE PCI master windows now decode `PCIW0/1` and `PCIINIT0/1`.
- PCI type 2 routes to a small CMD646-compatible IDE I/O wrapper.
- PCI type 6 has a main-RAM target path for bus-master DMA.
- PCI type 0 config access can expose the IDE device at dev 5 with BAR0-4 state.
- The IDE wrapper exposes primary/secondary command and control ports plus a minimal bus-master register block.

This did not immediately produce IDE traffic because the previous BIOS fastpaths were still corrupting control flow before the real loader path:

- `fpgaload()` fastpath skipped the BIOS prologue that preserves `ra` through `k1`; the epilogue then returned to `0`. Fixed by preserving the caller return in `k1` when the fastpath enters the BIOS epilogue.
- The A100/A160 ready-poll fastpath returned through the BIOS `jr a3` delay slot, which forced `v0=1` and sent the caller down the retry/failure path. Fixed by returning directly to `a3` with `v0=0`.
- Added guarded RAM POST fastpaths for the 32-bit and 64-bit walking-bit RAM tests at `0x1fc02468` and `0x1fc01f80`, for both `0x80000000` and `0xa0000000` segments.

Verification:

```text
dotnet build /tmp/eutherdrive-gauntlet-probe/GauntletProbe.csproj /clp:ErrorsOnly
Build succeeded.

EUTHERDRIVE_GAUNTDL_CPU_STEPS_PER_FRAME=5000 frame=1200
pc=0x0000000080004698
lastOp=0x1040fffd
```

The earlier bad endpoint was `pc=0x00000000007a0e98` executing zero-filled RAM. The current endpoint is real RAM code; a probe peek confirms code at the active RAM page:

```text
peek addr=0x00004600 nonzeroWords=27
firstWords=03e00008,27bd0018,40028000,03e00008,...
```

No `[GAUNTDL:IDE]` or `[GAUNTDL:IDEPCI]` traffic has appeared yet. The next useful target is the RAM-loader wait loop at `0x80004698` before adding more IDE behavior.

Note: a normal `dotnet build EutherDrive.Core/EutherDrive.Core.csproj --no-restore /clp:ErrorsOnly` is currently blocked by an unrelated, untracked `EutherDrive.Core/Arcade/DataEast/Deco32/Deco32Adapter.cs` compile error. The isolated Gauntlet probe still builds.

## 2026-05-07 Pause Point

Latest trace target was the RAM-loader wait loop at `0x8000468c..0x80004698`. It is not IDE yet. The code initializes NILE timer 2 at `BFA001E0/BFA001E4`, then polls the timer-2 counter at `BFA001E8` until it drops below `0x65`:

```text
80004640 lui   s1,0xbfa0
80004644 ori   s1,s1,0x01e0
8000464c lui   s0,0x0001
80004650 ori   s0,s0,0x8704
80004660 sw    zero,4(s1)
80004664 sw    s0,0(s1)
80004668 sw    s0,8(s1)
8000466c sw    v0,4(s1)
8000468c lw    s0,8(s1)
80004690 sltiu v0,s0,0x65
80004694 beq   v0,zero,0x8000468c
80004698 nop
```

The current `VegasMemoryMap` stores NILE timer registers as plain bytes, so the counter never counts down. I added a narrow bring-up fastpath, `TryFastPathKnownRamNileTimerDelay()`, matching only `pc == 0x8000468c` with `s1 == 0xbfa001e0`, then jumping to `0x8000469c`. This is a temporary deterministic replacement for NILE timer behavior, not a real timer implementation.

Verification state:

```text
EUTHERDRIVE_GAUNTDL_CPU_STEPS_PER_FRAME=5000 frame=1500
pc=0x0000000080004698
lastOp=0x1040fffd
```

After adding the timer-delay fastpath, probe rebuild was attempted but is currently blocked by unrelated untracked DataEast/Deco32 code:

```text
EutherDrive.Core/Arcade/DataEast/Deco32/Deco32Adapter.cs(245,9): error CS0103: The name 'Deco32GfxDecryptor' does not exist in the current context
EutherDrive.Core/Arcade/DataEast/Deco32/Deco32Adapter.cs(246,9): error CS0103: The name 'Deco32GfxDecryptor' does not exist in the current context
```

That compile failure is outside the Gauntlet file. `Deco32GfxDecryptor` appears later in the same untracked `Deco32Adapter.cs`, so the next session can either fix that local DataEast compile issue first or isolate the Gauntlet probe from the full Core project reference before continuing.

Next useful steps after resuming:

1. Clear or isolate the unrelated DataEast/Deco32 build blocker.
2. Rebuild `/tmp/eutherdrive-gauntlet-probe/GauntletProbe.csproj`.
3. Run with `EUTHERDRIVE_GAUNTDL_TRACE_IDE=1` and the raw disk sidecar to see what PC reaches after the timer-delay fastpath.
4. If still no `[GAUNTDL:IDE]` or `[GAUNTDL:IDEPCI]` traffic, trace the next RAM PC window rather than adding broad BIOS fastpaths.

## Gotchas

- Do not use the abandoned middle-of-text-loop fastpath. It repeats the boot string and breaks return flow.
- Do not use `a1600002 = 0x01`; trace proved the done bit needed by this BIOS path is `0x02`.
- The probe output can be slow because each frame runs `200000` CPU steps. Use progress every 100 frames or targeted trace windows.
- `EUTHERDRIVE_GAUNTDL_TRACE_MEM=1` is very noisy because ROM fetches are traced too.
- There are unrelated dirty files in the repo, including CPS1/TMNT/32X/SegaCD/UI/README work. Keep Gauntlet edits isolated.
