# Gauntlet Dark Legacy Vegas Handoff

Date: 2026-05-06

Update: 2026-05-07

Update: 2026-05-09

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
- later commits continue NILE/VRC5074, Voodoo PCI/status, FIFO, and runtime wait bring-up

There are unrelated dirty files in the worktree. Do not revert them unless explicitly asked.

## Current Verified State

Core builds:

```sh
dotnet build EutherDrive.Core/EutherDrive.Core.csproj --no-restore /clp:ErrorsOnly
```

## 2026-05-14 Loaded Runtime Fastpath Pass

This pass focused on moving the loaded Gauntlet runtime forward after the UI-visible diagnostic framebuffer. The current target is still real Voodoo draw traffic; the UI image is visible, but it is not game graphics yet.

Verified ROM/disk inputs:

```text
/home/nichlas/roms/MAME/Midway/Vegas/gauntd/gauntdl24.7z
/home/nichlas/roms/MAME/Midway/Vegas/gauntd/gauntd24.raw
```

New verified fastpaths:

- `TryFastPathKnownRuntimeBitfieldUpdate()` for the loaded runtime helper at `0xffffffff800eafdc`, including the observed mid-body stop at `0xffffffff800eb020`.
- `TryFastPathKnownRuntimeDwordCopyTail()` for the 64-bit copy tail at `0xffffffff800d1380`, including the observed mid-store stop at `0xffffffff800d138c`.

Probe command used from the clean verifier worktree `/tmp/eutherdrive-gauntlet-verify`:

```sh
dotnet build tools/GauntletProbe/GauntletProbe.csproj -c Release --no-restore /clp:ErrorsOnly

env EUTHERDRIVE_GAUNTDL_BRINGUP_FAST=1 \
    EUTHERDRIVE_GAUNTDL_PROGRESS_INTERVAL=100 \
    EUTHERDRIVE_GAUNTDL_RAW_DISK=/home/nichlas/roms/MAME/Midway/Vegas/gauntd/gauntd24.raw \
    EUTHERDRIVE_GAUNTDL_DUMP_GPRS=1 \
    dotnet run --project tools/GauntletProbe/GauntletProbe.csproj -c Release --no-build -- \
      /home/nichlas/roms/MAME/Midway/Vegas/gauntd/gauntdl24.7z 300 2000000
```

Progression from this pass:

```text
before bitfield fastpath: pc=0xffffffff800eb020
after bitfield fastpath:  pc=0xffffffff800d138c
after dword copy tail:    pc=0xffffffff800eb1a0
```

Latest verified result:

```text
frame=300
pc=0xffffffff800eb1a0
ra=0xffffffff800e1358
voodoo regs=3095589 fifoWords=6168964 fifoPackets=3082268
drawPackets=0 directTriangles=0 setupTriangles=0
packetTypes=0:0,1:3080873,2:0,3:0,4:1395,5:0,6:0,7:0
framebuffer=640x480 stride=2560 nonBlack=151456 colored=21408
```

Interpretation:

- The runtime now advances beyond the bitfield helper and the `0x800d1380` dword copy loop.
- Voodoo traffic increased, but remains type-1 state packets plus type-4 clear/fill packets. No setup or triangle packets yet.
- The current repeated stop is around `0xffffffff800eb1a0`, a branch-delay point in an input/status polling path reading `A4205001/5003/5005/5007`.
- A narrow delay-slot fastpath for `0x800eb1a0` was tested and removed because it did not move endpoint or Voodoo stats.

## 2026-05-13 UI/ROM + A420 Bring-Up Pass

The UI can now launch Gauntlet from the real ROM archive instead of a temporary file path.

Use this ROM in the UI:

```text
/home/nichlas/roms/MAME/Midway/Vegas/gauntd/gauntdl24.7z
```

The same directory now also has the raw CHD sidecar:

```text
/home/nichlas/roms/MAME/Midway/Vegas/gauntd/gauntd24.raw
```

The adapter still accepts the directory path, but the UI should be pointed at `gauntdl24.7z` for normal ROM selection. `DiskImageFactory.ResolveRawSidecar()` also has a `/tmp/{name}.raw` fallback for development, but the preferred path is the sibling `.raw` beside the archive.

UI/default bring-up changes:

- Desktop and Android Gauntlet creation set `EUTHERDRIVE_GAUNTDL_BRINGUP_FAST=1` if it is not already set.
- The individual bring-up fix flags now fall back to that master flag.
- Desktop UI accepts a Gauntlet ROM directory as well as the archive.
- Gauntlet is included in the force-opaque/safe-RGBA/post-frame presentation paths, which is why the current diagnostic bars are visible in UI.

Important: the current UI image is not real game graphics yet. It is still the diagnostic/bring-up framebuffer plus Voodoo fast fills/swaps. Current Voodoo status still shows:

```text
drawPackets=0 directTriangles=0 setupTriangles=0
```

New boot fixes from this pass:

- Added a loaded boot A420 handshake fastpath for `0x80010d54..0x80010d98`.
- The actual helper entry is `0x80010d54`; `0x80010d50` is the caller-side prelude.
- The caller treats non-zero `v0` as the failure path, so the fastpath returns `v0=0` for bring-up.
- The helper is now matched at both `0x80010d54` and `0x80010d58`, plus the loop PCs.
- Corrected the loaded cache-loop variant around `0xa00cc2bc..0xa00cc2cc`; the old match started four words too early at the preamble.

Current preferred headless command:

```sh
dotnet build tools/GauntletProbe/GauntletProbe.csproj -c Release --no-restore /clp:ErrorsOnly

env EUTHERDRIVE_GAUNTDL_BRINGUP_FAST=1 \
    EUTHERDRIVE_GAUNTDL_SKIP_FRAME_RENDER=1 \
    EUTHERDRIVE_GAUNTDL_PROGRESS_INTERVAL=500 \
    EUTHERDRIVE_GAUNTDL_EXTRA_SERIES=1000,10000,50000,200000,1000000 \
    EUTHERDRIVE_GAUNTDL_DUMP_GPRS=1 \
    dotnet run --project tools/GauntletProbe/GauntletProbe.csproj -c Release --no-build -- \
      /home/nichlas/roms/MAME/Midway/Vegas/gauntd/gauntdl24.7z 2500 200000
```

Last verified result:

```text
Build succeeded.
331-332 warnings, 0 errors.

frame=2500
pc=0xffffffffa00ccae4
ra=0xffffffff80011868
voodoo regs=14049 fifoWords=13323 fifoPackets=4448
voodoo drawPackets=0 directTriangles=0 setupTriangles=0
framebuffer=640x480 nonBlack=147730 colored=17682
```

Interpretation:

- `/rd0` boot-file loading is still good.
- The old A420 loop no longer blocks; trace confirms:

```text
[GAUNTDL:BOOT] boot-a420-handshake pc=ffffffff80010d88 return=ffffffff80010210
```

- The current blocker is still loaded boot serial/reset flow around `0x8001068c` and caller `0x80011868`.
- The recurring state has `s0/s1` around `0x8001013c..0x80010237`, `s2=0xa4800000`, and no new Voodoo draw commands.
- Next likely target: identify which loaded boot condition routes to the serial/reset block, rather than blindly skipping `0x8001068c` since that routine eventually branches back to `0x80010000`.

Useful dumps:

```text
mem[0xffffffff800101e0]:
  +0x030: 10020009 00000000 24040007 24050000
  +0x040: 24060000 24070001 0411011a 00000000
```

`10020009` branches past the reset/serial block only when `v0 == 0`, which is why the A420 fastpath must return zero.

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

2026-05-09 pass result:

- `dotnet build /tmp/eutherdrive-gauntlet-probe/GauntletProbe.csproj /clp:ErrorsOnly` succeeds.
- Added a narrow RAM qword-fill fastpath for the runtime loop at `0xffffffff80005b18`:
  - signature: `addiu t0,-1; sd a1,0(a0); bgtz t0,-3; addiu a0,8`
  - verified by CPU trace with `t0=0x62`, aligned `a0`, and zero `a1`
  - constrained to main RAM ranges and exact instruction words
- Added `EUTHERDRIVE_GAUNTDL_TRACE_MEM_TARGET` so memory traces can be filtered by target name such as `CS6`, `PCI`, or `NILE`.
- Added a minimal Voodoo 2 PCI function at device 3:
  - vendor/device `121a:0002`
  - class/revision `0x03800002`
  - BAR0 defaults to `0xff000000`, 16 MiB, prefetchable memory
  - register/LFB/texture writes route to the existing Voodoo facade/trace backend
  - status reads return a ready FIFO-style value
- Probe with `EUTHERDRIVE_GAUNTDL_TRACE_VOODOO=1` now proves the guest can see Voodoo on PCI:

```text
[GAUNTDL:VOODOO-PCI] pci cfg read off=00 value=121a0002
[GAUNTDL:VOODOO-PCI] pci cfg read off=00 value=121a0002
```

Latest traced run after exposing Voodoo:

```text
frame=1800
pc=0xffffffff80040bc8
lastOp=0x8c622ed8
cp0 status=0x000000003400ff01 cause=0x0000000000000000 epc=0x00000000800147bc errorepc=0x0000000000000000
attached=True
```

The guest reads the Voodoo ID but has not yet issued BAR/config writes or Voodoo register writes in the 1800-frame probe. The active code path is a scheduler/callback loop around `0xffffffff80040bb4..0xffffffff80040c30`; callback `0xffffffff800043d4` is a small wrapper around a queue/allocation routine at `0xffffffff80042db0` and returns list nodes around `0xffffffff800f08xx`. This does not look like an unsupported-op halt.

Later 2026-05-09 pass result:

- Main build succeeds again:

```text
dotnet build EutherDrive.Core/EutherDrive.Core.csproj --no-restore /clp:ErrorsOnly
Build succeeded.
377 Warning(s)
0 Error(s)
```

- Probe build succeeds:

```text
dotnet build /tmp/eutherdrive-gauntlet-probe/GauntletProbe.csproj /clp:ErrorsOnly
Build succeeded.
377 Warning(s)
0 Error(s)
```

- Fixed the first real Voodoo wait blockers:
  - Voodoo status bit 9 is now clear when idle (`0x0ffff03f` base status), matching MAME's "overall busy" meaning.
  - Voodoo status bit 6 now toggles on status reads so the guest can observe a vblank edge.
  - Voodoo register `0x204` now returns a changing low-11-bit vRetrace counter.
- Added lightweight VRC5074/NILE timer countdown for the timer block at `0x1c0..0x1f8`.
  - This is driven from the R5000 CP0 count advance.
  - It gets past the `0x80017040` divide-by-zero guard caused by timer counter `0xbfa001e8` staying at `0xffffffff`.
- Verified forward progress with the current probe:

```text
frame=1000
pc=0xffffffff80016e74
lastOp=0xafb10014
voodoo regs=3829 fifoWords=63 fifoPackets=27 drawPackets=0 lfbWrites=0 texWrites=1
```

- A higher-budget run no longer halts, and reaches the callback/state code around `0x80016e88`:

```text
frame=1500
pc=0xffffffff80016e88
lastOp=0x00000000
voodoo regs=3829 fifoWords=63 fifoPackets=27 drawPackets=0 lfbWrites=0 texWrites=1
```

- Focused trace of `0x80016e64..0x80016e94` shows this is not a hard wait. It is a small callback loop that loads a function pointer from `0x800b2e2c`, calls `0x8003b614`, decrements `s0` from 10, then returns through `0x80016e90`.

Latest 2026-05-09 pass result:

- Main build succeeds:

```text
dotnet build EutherDrive.Core/EutherDrive.Core.csproj --no-restore /clp:ErrorsOnly
Build succeeded.
377 Warning(s)
0 Error(s)
```

- Probe build succeeds:

```text
dotnet build /tmp/eutherdrive-gauntlet-probe/GauntletProbe.csproj /clp:ErrorsOnly
Build succeeded.
377 Warning(s)
0 Error(s)
```

- Fixed the next deterministic runtime wait at `0xffffffff80017310`.
  - The guest saves `0x800b2ed8`, repeatedly calls `0x80016e64`, then waits until `*(0x800b2ed8) - saved >= 0xb4`.
  - Memory trace showed `0x800b2ed8` is initialized and then read repeatedly, but no emulated source advanced it.
  - The fastpath is exact-opcode guarded and only fires when `s1 + 0x2ed8` resolves to main RAM `0x800b2ed8`.

- Verified forward progress past the old blocker:

```text
frame=1500
pc=0xffffffff80052d04
lastOp=0x001118c0
voodoo regs=10205001 fifoWords=11772114 fifoPackets=1572285 drawPackets=0 lfbWrites=0 texWrites=1
peek 0x800b2ed0: 00000000,00000094,000000b4,00000000,807efde8,800e6748,800e6a60,00000000
```

- This is a new phase: the game is now heavily feeding the Voodoo FIFO, but the bring-up decoder still reports `drawPackets=0`. Next work should focus on FIFO packet framing/decoding or the active producer loop around `0xffffffff80052d04`.

Follow-up same pass:

- Limited FIFO trace proved the first heavy stream is mostly type-1/type-4 Voodoo register packets, not type-3 triangle packets.
- The repeated packet sequence writes `fastfillCMD` at register `0x124` and `swapbufferCMD` at `0x128`; the backend previously only stored these registers.
- Added a bring-up fastfill path:
  - `fastfillCMD` fills the current LFB clip rectangle from `clipLeftRight`/`clipLowYHighY`.
  - The fill color comes from `color1`, falling back to `color0` and then `zaColor`.
  - `swapbufferCMD` is counted for overlay/debug state.

Short verification after fastfill:

```text
frame=300
pc=0xffffffff80052ee4
voodoo regs=140583 fifoWords=159324 fifoPackets=23913 drawPackets=0 lfbWrites=43315200 texWrites=1
framebuffer width=640 height=480 nonblack=307200 first=(0,0)
```

Next best step:

- Open/render the Gauntlet adapter and check whether the fastfilled LFB now produces the first visible graphics surface.
- Then trace the active FIFO producer around `0xffffffff80052c80..0xffffffff80052d30` and continue toward type-3 triangle or setup-packet decoding.
- Keep short CPU windows plus `EUTHERDRIVE_GAUNTDL_DUMP_VOODOO=1`; full memory trace is too noisy unless filtered by target and address.

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

## 2026-05-09 Interpreter/IDE Progress

The current workspace rebuilds the isolated probe again:

```text
dotnet build /tmp/eutherdrive-gauntlet-probe/GauntletProbe.csproj /clp:ErrorsOnly
Build succeeded.
```

This pass fixed several real R5000 interpreter gaps hit by RAM code after the NILE timer-delay fastpath:

- REGIMM branch-likely forms: `BLTZL`, `BGEZL`, `BLTZALL`, `BGEZALL`.
- 32-bit signed `ADD`/`SUB` while leaving the previously working unsigned fastpath behavior intact.
- FPU register load/store spills: `LWC1`, `LDC1`, `SWC1`, `SDC1`.
- Conditional moves: `MOVZ`, `MOVN`.
- Little-endian unaligned word access: `LWL`, `LWR`, `SWL`, `SWR`.

Important correction: the first LWL/LWR implementation accidentally used the local Ryu64/N64 big-endian behavior. The correct formulas were copied from MAME `mips3_device::*_le`. Before that fix, a copy from `0x8008d5f4` produced a corrupt prefix in the stack string. After the fix it copies `??? error...` correctly.

The first real IDE/PCI config access is now visible:

```text
[GAUNTDL:IDEPCI] pci cfg read off=00 value=06461095
```

Current endpoint with a high step budget:

```text
EUTHERDRIVE_GAUNTDL_TRACE_IDE=1 EUTHERDRIVE_GAUNTDL_CPU_STEPS_PER_FRAME=200000 \
dotnet run --project /tmp/eutherdrive-gauntlet-probe/GauntletProbe.csproj --no-build -- /home/nichlas/roms/MAME/Midway/Vegas/gauntd 400

frame=400
pc=0xffffffff8004dacc
lastOp=0x080136b2
cp0 status=0x000000003400ff01
```

`0x8004dac8/0x8004dacc` is not an interpreter halt. It is the guest's own infinite halt loop after its stdio error formatter emits:

```text
??? error, Unknown status of 0x00000000
```

Nearby string table context includes `/tty0`, `Error reopening stdin`, `Error opening /tty0 for input`, and `Error queing first read on /tty0`. The next useful target is not more generic CPU opcodes; it is the `/tty0`/stdio open path and the device/status value that becomes zero before the error formatter. Trace around `0xffffffff8004d840..0xffffffff8004da20` is noisy because it mostly captures formatter loops. A better next trace is earlier in the call path that attempts to open or queue reads for `/tty0`, with targeted device/memory tracing for CS2/SIO and CS5 CPU I/O.

## 2026-05-09 Vegas Device/Interrupt Progress

This continuation supersedes the `/tty0` endpoint above.

Implemented in `EutherDrive.Core/Arcade/Vegas/GauntletDarkLegacyAdapter.cs`:

- NILE/VRC5074 chip-select window decode using the CS2..CS8 config registers. This lets CPU physical windows like `0xa1000000`, `0xa1600000`, `0xa1800000`, and `0xa1a00000` reach mapped Vegas devices.
- Minimal CS5 CPU-I/O / FPGA config model, matching the MAME-observed CPU I/O register behavior closely enough for the guest to leave the old `/tty0` failure path.
- Extracted `/tmp/gauntd24.raw` from `gauntd24.chd` with `chdman extractraw`, so IDE reads now use a real raw sidecar instead of the CHD metadata-only fallback.
- RAM CP0 count-delay fastpath for the guest routine at `0xffffffff80010fec`, guarded by RAM return addresses and sane delay arguments.
- ATA `DSC` status bit support. Idle status is now `0x50` instead of `0x40`.
- ATA `SET FEATURES` command `0xef` as a successful no-op. The guest sends feature `0x03`, value `0x08` after IDENTIFY.
- Minimal CP0 interrupt exception entry plus `eret`. This is enough for guest software interrupt `Cause=0x200` to vector through the OS handler and leave the wait loop at `0x80022aa8`.

Verification:

```text
dotnet build /tmp/eutherdrive-gauntlet-probe/GauntletProbe.csproj /clp:ErrorsOnly
Build succeeded.
367/368 Warning(s)
0 Error(s)
```

Short IDE trace now reaches IDENTIFY and SET FEATURES:

```text
[GAUNTDL:IDE] read r7=50
[GAUNTDL:IDE] write r7=ec
[GAUNTDL:IDE] identify
[GAUNTDL:IDE] read r7=58
[GAUNTDL:IDE] read r7=50
[GAUNTDL:IDE] write r2=08
[GAUNTDL:IDE] write r1=03
[GAUNTDL:IDE] write r7=ef
[GAUNTDL:IDE] set features feature=03 value=08
[GAUNTDL:IDE] read r7=50
```

Current long probe with the raw disk sidecar:

```text
EUTHERDRIVE_GAUNTDL_RAW_DISK=/tmp/gauntd24.raw \
EUTHERDRIVE_GAUNTDL_CPU_STEPS_PER_FRAME=200000 \
dotnet run --project /tmp/eutherdrive-gauntlet-probe/GauntletProbe.csproj --no-build -- /home/nichlas/roms/MAME/Midway/Vegas/gauntd 2000

frame=2000
pc=0xffffffff80040bd4
lastOp=0x1440fff2
cp0 status=0x000000003400ff01 cause=0x0000000000000000 epc=0x00000000800147bc errorepc=0x0000000000000000
attached=True
```

`0x80040bb4..0x80040c10` appears to be an active dispatcher/callback path, not a hard halt. Targeted trace showed repeated calls through `0xffffffff800043d4` with no pending cause. Next useful bring-up targets are:

1. Trace the next device writes after SET FEATURES with `EUTHERDRIVE_GAUNTDL_TRACE_MEM=1`, filtered externally if possible; raw unfiltered IDE trace can produce millions of `r7` lines.
2. Start modeling enough of CS6/CS7 IOASIC/DCS and video-side registers for the first graphics path.
3. Add a proper interrupt/device pending model instead of only CP0 software interrupt entry.

## 2026-05-09 Late Bringup: IOASIC/PIC to First Voodoo/Glide

This continuation moved the endpoint from the dispatcher/stdio/IOASIC waits into the first real Voodoo/Glide startup failure.

Build checks completed during this pass:

```text
dotnet build EutherDrive.Core/EutherDrive.Core.csproj --no-restore /clp:ErrorsOnly
Build succeeded.
0 Error(s)

dotnet build /tmp/eutherdrive-gauntlet-probe/GauntletProbe.csproj /clp:ErrorsOnly
Build succeeded.
0 Error(s)
```

Main implementation changes in `EutherDrive.Core/Arcade/Vegas/GauntletDarkLegacyAdapter.cs`:

- CP0 Count/Compare timer pending:
  - Count advances through `AdvanceCp0Count(...)`.
  - Compare crossing sets Cause IP7 (`0x8000`).
  - Compare writes clear timer pending.
- SIO/NILE interrupt path:
  - SIO IRQ line now feeds NILE PCI INT C.
  - NILE INTCTRL/INTSTAT minimal decode updates CPU pending bits.
  - IOASIC interrupt model sets SIO IRQ bit `0x04`.
- CS6 IOASIC packed register model:
  - proper packed offset -> 16-bit IOASIC register mapping.
  - reg 0 returns `0x2001`.
  - reg 10 returns `0x0048`.
  - reg 11 returns `0x000a` for the current sound/PIC ack poll.
  - reg 13 returns `0x0100`.
  - reg 14 is computed INTSTAT.
  - reg 15 is INTCTL and asserts SIO IOASIC IRQ when enabled.
- Tightly signature-gated bringup fastpaths:
  - stdio/TTY init error loop at `0x8004dac8` / `0xffffffff8004dac8`.
  - IOASIC PIC bit-test waits at `0x80040f2c` and `0x80040f8c`.
- Voodoo PCI:
  - fixed Voodoo2 PCI vendor/device dword from wrong `0x121a0002` to correct `0x0002121a`.
  - status ready value changed from `0x0fffff3f` to `0x0fffff7f`.
  - PCI config `initEnable` default at offset `0x40` set to `0x00000003`.
- Trace filter:
  - `EUTHERDRIVE_GAUNTDL_TRACE_MEM_TARGET=PCI` no longer matches unrelated `PCI_ID_NILE:rom`.

Current useful Voodoo trace:

```text
[GAUNTDL:VOODOO-PCI] pci cfg read off=00 value=0002121a
[GAUNTDL:VOODOO-PCI] pci cfg write off=10 value=ffffffff
[GAUNTDL:MEM] read32 00000000a9000010 ff000008 PCI
[GAUNTDL:VOODOO-PCI] pci cfg write off=10 value=08000000
[GAUNTDL:MEM] read32 00000000a9000010 08000008 PCI
[GAUNTDL:VOODOO-PCI] pci cfg read off=40 value=00000003
[GAUNTDL:VOODOO-PCI] pci cfg write off=04 value=00000002
[GAUNTDL:VOODOO-PCI] mem read off=000214 value=00000000
[GAUNTDL:VOODOO] reg[00000214]=00001000
[GAUNTDL:VOODOO-PCI] mem read off=000000 value=0fffff7f
[GAUNTDL:VOODOO-PCI] mem read off=000244 value=00000000
[GAUNTDL:VOODOO] reg[00000244]=00000000
[GAUNTDL:VOODOO-PCI] mem read off=000000 value=0fffff7f
[GAUNTDL:CPU] halt pc=ffffffff80016eec op=0000000d reason=special 0d
```

Latest endpoint:

```text
frame=1000
pc=0xffffffff80016ef0
lastOp=0x0000000d
cp0 status=0x0000000034006f01 cause=0x0000000000009000 epc=0x000000008001128c errorepc=0x0000000000000000
attached=True
```

Meaning:

- We are past the earlier BIOS/stdio/IOASIC/PIC blockers.
- Before the PCI ID fix, the visible panic string was `main: grSstQueryHardware failed!`.
- After the PCI ID fix, the guest finds/configures Voodoo, maps BAR0 to `0x08000000`, reads status, and writes Voodoo registers.
- It still reaches the same generic `break` panic routine at `0xffffffff80016eec`.
- Adjacent string table around `0x80089940` contains:

```text
main: grSstQueryHardware failed!
SST_RESOLUTION
SST_REFRESH_RATE
SST_COLOR_FORMAT
SST_ORIGIN
SST_COLOR_BUFF_CNT
SST_AUX_BUFF_CNT
main: grSstWinOpen failed!
Unable to get LfbLock
Unable to LfbUnlock
```

Next recommended step:

1. Confirm the current panic string after the PCI ID/initEnable/status changes by dumping `a0` at the `0x80016eec` halt.
2. Add stored readback for Voodoo registers instead of returning zero for most MMIO reads. The immediate suspects are `0x214`, `0x244` (`fbiInit5`), and any LFB lock/status path used after `grSstWinOpen`.
3. Continue Voodoo/Glide startup until `grSstWinOpen` succeeds and command writes become frame/content related.

Temp probe note:

- `/tmp/eutherdrive-gauntlet-probe/Program.cs` was extended with:
  - `EUTHERDRIVE_GAUNTDL_DUMP_GPRS=1`
  - longer `EUTHERDRIVE_GAUNTDL_PEEK` first-word dumps
- This probe is outside the repo.

## 2026-05-09 WinOpen/FPU Continuation

This pass moved the endpoint deeper into `grSstWinOpen`; it no longer fails immediately after the first Voodoo status reads.

New implementation work:

- Added a narrow guest config hook at `0x80016774` for `SST_RESOLUTION`.
  - Default behavior now preserves the guest's default argument.
  - Host `SST_RESOLUTION` can override it for probes.
- Corrected the `grSstQueryHardware` fastpath shape enough to expose the mapped Voodoo base.
- Added signature-gated Glide/Voodoo bring-up hooks:
  - `grSstSelect` at `0x80064cd0`
  - board map at `0x8005aacc`
  - post-init checks at `0x80053f64` and `0x80054064`
  - board-state fill for the `0xa8000001` mapped-base path
- Added missing COP1 interpreter coverage used after `grSstWinOpen` gets deeper:
  - `cvt.s.w`, `cvt.d.w`
  - S/D format add/sub/mul/div, abs/mov/neg
  - S/D format `cvt.s`, `cvt.d`, `cvt.w`
  - S/D format round/trunc/ceil/floor to word
  - COP1 FCC0 comparisons `c.eq`, `c.lt`, `c.le`
  - `bc1f`, `bc1t`, `bc1fl`, `bc1tl`

Observed forward progress:

```text
[GAUNTDL:VOODOO-PCI] pci cfg write off=40 value=00000001
[GAUNTDL:VOODOO-PCI] pci cfg write off=44 value=00000000
[GAUNTDL:VOODOO-PCI] pci cfg write off=48 value=00000000
[GAUNTDL:VOODOO] reg[0000021c]=00110040
[GAUNTDL:VOODOO] reg[00000214]=00001100
[GAUNTDL:VOODOO] reg[00000210]=00000006
[GAUNTDL:VOODOO] reg[00000210]=00000002
[GAUNTDL:VOODOO] reg[00000210]=00000000
[GAUNTDL:VOODOO] reg[00000210]=00001c10
[GAUNTDL:VOODOO] reg[00000214]=00201102
[GAUNTDL:VOODOO] reg[00000218]=80000040
[GAUNTDL:VOODOO] reg[00000244]=00408000
[GAUNTDL:VOODOO] reg[0000024c]=08080000
```

Latest probe still reaches the generic break routine with `a0=0x800899ec`, which is the `main: grSstWinOpen failed!` string:

```text
EUTHERDRIVE_GAUNTDL_RAW_DISK=/tmp/gauntd24.raw \
EUTHERDRIVE_GAUNTDL_CPU_STEPS_PER_FRAME=200000 \
EUTHERDRIVE_GAUNTDL_DUMP_GPRS=1 \
dotnet run --project /tmp/eutherdrive-gauntlet-probe/GauntletProbe.csproj --no-build -- /home/nichlas/roms/MAME/Midway/Vegas/gauntd 4000

frame=4000
pc=0xffffffff80016ef0
lastOp=0x0000000d
a0=0x00000000800899ec
```

Current most useful next trace window:

```text
EUTHERDRIVE_GAUNTDL_TRACE_CPU=1
EUTHERDRIVE_GAUNTDL_TRACE_CPU_PC_MIN=ffffffff800541b0
EUTHERDRIVE_GAUNTDL_TRACE_CPU_PC_MAX=ffffffff80054890
```

The next blocker is still inside the later `grSstWinOpen` tail, after the `0xa8000001` mapped-base path. Keep tracing the branch to `0x80054870` and fill the missing board/global state only where a signature proves the expected field.

## 2026-05-09 WinOpen Cleared / First Voodoo Activity

This continuation moved the current endpoint past the `main: grSstWinOpen failed!` panic.

Additional signature-gated WinOpen tail hooks added:

- `0x80054230`: post-aux status check after the `0x80060d70` call.
- `0x800543f0`: post-LFB/status check after the `0x80057eb8` call, including the delay-slot `a1=1` effect.
- `0x80054424`: post-swap/status check after the `0x8005ee08` call, including the delay-slot `a2=0` effect.

Current normal probe:

```text
EUTHERDRIVE_GAUNTDL_RAW_DISK=/tmp/gauntd24.raw \
EUTHERDRIVE_GAUNTDL_CPU_STEPS_PER_FRAME=200000 \
EUTHERDRIVE_GAUNTDL_DUMP_GPRS=1 \
dotnet run --project /tmp/eutherdrive-gauntlet-probe/GauntletProbe.csproj --no-build -- /home/nichlas/roms/MAME/Midway/Vegas/gauntd 5000

frame=5000
pc=0xffffffff800654b4
lastOp=0x01455021
s4=0x0000000000000001
s5=0x0000000000000001
s6=0x00000000000001e0
s7=0x0000000000000280
```

The old panic is gone in this run. The guest is now in the `0x800654xx` Glide/Voodoo path with 640x480 state.

Voodoo trace after WinOpen shows real post-open video setup and repeated render-side register traffic. Useful examples:

```text
[GAUNTDL:VOODOO] reg[00000220]=02c00060
[GAUNTDL:VOODOO] reg[00000224]=020b0002
[GAUNTDL:VOODOO] reg[00000208]=00190026
[GAUNTDL:VOODOO] reg[0000020c]=01e0027f
[GAUNTDL:VOODOO] reg[00000218]=8004b040
[GAUNTDL:VOODOO] reg[00000214]=2241e1a2
[GAUNTDL:VOODOO] reg[00000230]=00080408
[GAUNTDL:VOODOO] reg[00000b1c]=0000dead
[GAUNTDL:VOODOO] reg[00001320]=00186ead
```

Frame presentation change:

- `EutherFrameTarget` now carries the adapter BGRA framebuffer.
- The default Voodoo backend records register writes and renders a simple register-driven bringup frame once Voodoo activity starts.
- The trace backend still logs register writes and also inherits that bringup frame.
- This is a visible bringup visualization, not a real Voodoo rasterizer yet.

Latest build checks:

```text
dotnet build EutherDrive.Core/EutherDrive.Core.csproj --no-restore /clp:ErrorsOnly
Build succeeded.
0 Warning(s)
0 Error(s)

dotnet build /tmp/eutherdrive-gauntlet-probe/GauntletProbe.csproj /clp:ErrorsOnly
Build succeeded.
0 Error(s)
```

Short verifier after the framebuffer wiring:

```text
frame=1000
pc=0xffffffff80065434
s4=0x0000000000000001
s5=0x0000000000000001
s6=0x00000000000001e0
s7=0x0000000000000280
```

Next useful steps:

1. Replace the register-driven bringup frame with a minimal Voodoo front-buffer/LFB model.
2. Track writes outside the register aperture (`lfb write` / `tex write`) without flooding trace output.
3. Decode the repeated `0x800654xx` path to decide whether it is buffer swap, FIFO wait, or draw dispatch.
4. Start mapping the high-value Voodoo registers currently being hit: `0x200`, `0x208`, `0x20c`, `0x210`, `0x214`, `0x218`, `0x21c`, `0x220`, `0x224`, `0x22c`, `0x230`, `0x244`, and TMU ranges around `0xb1c`/`0x1320`.

## 2026-05-09 FIFO/LFB Continuation

This continuation made the post-WinOpen Voodoo path more explicit:

- `EutherFrameTarget` presentation now has a minimal LFB path:
  - Voodoo LFB writes are stored as RGB565 pixels.
  - LFB reads return the stored 32-bit pair.
  - If no non-zero LFB pixels exist yet, the register-driven bringup visualization remains the fallback.
- Added focused trace flags:
  - `EUTHERDRIVE_GAUNTDL_TRACE_VOODOO_FIFO=1`
  - `EUTHERDRIVE_GAUNTDL_TRACE_VOODOO_FIFO_LIMIT=N`
  - `EUTHERDRIVE_GAUNTDL_TRACE_VOODOO_LFB=1`
  - `EUTHERDRIVE_GAUNTDL_TRACE_VOODOO_TEX=1`
- `0x800654xx` was traced and identified as a swap/FIFO-style wait/fill path.
- Voodoo register `0x1e8` is now modeled as a monotonic swap/status counter. This moves the loop forward from the earlier `0x800654a0` sample point.
- The board-state FIFO pointer was still being restored to bare offset `0x00200000`, so the loop was writing command words into RAM. A narrow `0x80065410..0x80065504` state normalizer now maps those FIFO pointer fields to `0xa8200000`.
- BAR offset `0x200000..0x3fffff` is routed as Voodoo command FIFO instead of ordinary register writes.

Focused FIFO trace now proves the guest is feeding the Voodoo FIFO path:

```text
EUTHERDRIVE_GAUNTDL_TRACE_VOODOO_FIFO=1 \
EUTHERDRIVE_GAUNTDL_TRACE_VOODOO_FIFO_LIMIT=48 \
dotnet run --project /tmp/eutherdrive-gauntlet-probe/GauntletProbe.csproj --no-build -- /home/nichlas/roms/MAME/Midway/Vegas/gauntd 1100

[GAUNTDL:VOODOO] fifo[000000]=00000018
[GAUNTDL:VOODOO] fifo[000001]=00000018
...
[GAUNTDL:VOODOO] fifo[00002f]=00000018
frame=1100
pc=0xffffffff800654e8
```

Focused LFB/texture trace through 2500 frames still shows no direct LFB or texture aperture writes. The immediate next target is therefore the repeated FIFO token `0x18` and the swap/FIFO routine around `0x800654c0..0x80065504`, not LFB upload yet.

Latest build checks after FIFO routing:

```text
dotnet build EutherDrive.Core/EutherDrive.Core.csproj --no-restore /clp:ErrorsOnly
Build succeeded.
0 Error(s)

dotnet build /tmp/eutherdrive-gauntlet-probe/GauntletProbe.csproj /clp:ErrorsOnly
Build succeeded.
0 Error(s)
```

## 2026-05-09 FIFO Room / COP1X Cleared

This continuation moved the bring-up into the next phase: the guest is now issuing varied Voodoo FIFO setup packets instead of sitting in the earlier swap/FIFO room path.

Key fixes:

- Corrected the signature for the known Glide FIFO-room helper at `0x800653d8`.
  - Actual entry starts with `3c02800b 8c464d2c 0080c82d 8cc20384`.
  - The hook now refreshes the board-state FIFO fields at `0x800b5174..0x800b5188` and returns `0x10000` bytes of apparent room.
- Added COP1X/R5000 MIPS IV interpreter support for:
  - `lwxc1`, `ldxc1`, `swxc1`, `sdxc1`, `prefx`
  - `madd.s/d`, `msub.s/d`, `nmadd.s/d`, `nmsub.s/d`
- This cleared the previous unsupported instruction:

```text
halt pc=ffffffff80072d70 op=4c002860 reason=opcode 13
```

Verification after the COP1X patch:

```text
dotnet build EutherDrive.Core/EutherDrive.Core.csproj --no-restore /clp:ErrorsOnly
Build succeeded.
328 Warning(s)
0 Error(s)

dotnet build /tmp/eutherdrive-gauntlet-probe/GauntletProbe.csproj /clp:ErrorsOnly
Build succeeded.
375 Warning(s)
0 Error(s)
```

Normal 2500-frame probe now runs past the old COP1X stop and remains live in Glide code:

```text
EUTHERDRIVE_GAUNTDL_RAW_DISK=/tmp/gauntd24.raw \
EUTHERDRIVE_GAUNTDL_CPU_STEPS_PER_FRAME=200000 \
EUTHERDRIVE_GAUNTDL_DUMP_GPRS=1 \
dotnet run --project /tmp/eutherdrive-gauntlet-probe/GauntletProbe.csproj --no-build -- /home/nichlas/roms/MAME/Midway/Vegas/gauntd 2500

frame=2500
pc=0xffffffff80054b64
lastOp=0x27bdffe8
v0=0x0000000000000001
ra=0xffffffff80017a14
attached=True
```

Focused FIFO trace now shows real setup packets around frame 300, including the expected 640x480 values:

```text
EUTHERDRIVE_GAUNTDL_TRACE_VOODOO_FIFO=1 \
EUTHERDRIVE_GAUNTDL_TRACE_VOODOO_FIFO_LIMIT=96 \
EUTHERDRIVE_GAUNTDL_TRACE_VOODOO_LFB=1 \
EUTHERDRIVE_GAUNTDL_TRACE_VOODOO_TEX=1 \
dotnet run --project /tmp/eutherdrive-gauntlet-probe/GauntletProbe.csproj --no-build -- /home/nichlas/roms/MAME/Midway/Vegas/gauntd 2600

[GAUNTDL:VOODOO] fifo[000000]=00010221
[GAUNTDL:VOODOO] fifo[000001]=00034001
[GAUNTDL:VOODOO] fifo[00000d]=00018234
[GAUNTDL:VOODOO] fifo[00000e]=00000280
[GAUNTDL:VOODOO] fifo[00000f]=000001e0
[GAUNTDL:VOODOO] fifo[000028]=00019604
[GAUNTDL:VOODOO] fifo[000029]=04221000
frame=2600
pc=0xffffffff80054be8
```

No direct LFB/texture aperture writes have shown up yet in this trace window. The next phase should treat FIFO packet decode as the main path to first recognizable graphics.

Next-phase plan:

1. Add a small Voodoo FIFO packet decoder for the packets now visible in trace. Start with register-write packets and update the existing register bank from FIFO, not just direct MMIO writes.
2. Use the decoded register writes to drive the bringup framebuffer: clip/window registers, buffer selection, color/depth mode, and swap status.
3. Keep tracing LFB/texture apertures, but do not expect first pixels there yet. The guest is currently using FIFO command streams for setup.
4. Once packet decode is stable, add minimal triangle/rect handling only for packets proven by trace. The immediate goal is a first recognizable clear/viewport/frame transition, not full Voodoo emulation.
5. Keep each fastpath signature-gated. The current stack has enough narrow hooks to reach graphics; the next quality step is replacing hooks with small hardware models where the trace proves the contract.

## 2026-05-09 FIFO Packet Decode

Implemented a small streaming Voodoo2 command FIFO decoder in the bringup backend:

- Packet type 1: `count/inc/register` register writes.
- Packet type 4: masked general register writes.
- Packet type 5: upload packets routed to LFB or texture storage based on space bits.
- Packet type 3 is consumed and counted as draw/setup traffic, but not rasterized yet.

The decoder uses MAME's `voodoo_2.cpp` packet word-count fields so the probe no longer needs long raw FIFO logs to prove state movement.

The bringup visualization now reads FIFO-updated Voodoo registers:

- Primary clip rectangle: `clipLeftRight` / `clipLowYHighY` at register numbers `0x46` / `0x47`.
- Fallback dimensions: `videoDimensions` at register number `0x83`.
- Register color bands include both the `0x100` drawing-state area and the `0x200` video-init area.

Short fast verifier:

```text
EUTHERDRIVE_GAUNTDL_RAW_DISK=/tmp/gauntd24.raw \
EUTHERDRIVE_GAUNTDL_CPU_STEPS_PER_FRAME=200000 \
EUTHERDRIVE_GAUNTDL_DUMP_VOODOO=1 \
dotnet run --project /tmp/eutherdrive-gauntlet-probe/GauntletProbe.csproj --no-build -- /home/nichlas/roms/MAME/Midway/Vegas/gauntd 350

frame=350
pc=0xffffffff80054b74
voodoo regs=3884 fifoWords=63 fifoPackets=27 drawPackets=0 lfbWrites=0 texWrites=0
voodoo reg[046]=0x00000280
voodoo reg[047]=0x000001e0
voodoo reg[083]=0x01e0027f
```

This confirms the first FIFO packet batch now updates the live Voodoo register bank by frame 350. Still no draw packets or LFB/texture uploads in this early window, so the next target is finding the first packet type 3/type 5 activity or the guest state transition that enables it.

## 2026-05-09 Reboot Recovery / Voodoo Triangle Prep

After the machine reboot, `/tmp/eutherdrive-gauntlet-probe` and `/tmp/gauntd24.raw` were gone. Recreated the temp probe and re-extracted the raw disk:

```text
chdman extractraw -i /home/nichlas/roms/MAME/Midway/Vegas/gauntd/gauntd24.chd -o /tmp/gauntd24.raw
```

Current local Voodoo work in `GauntletDarkLegacyAdapter.cs`:

- Type-3 FIFO packets now copy their setup bits into register `0x98` (`sSetupMode`) before consuming vertices.
- The bringup raster path now handles both `triangleCMD` (`0x20`) and `ftriangleCMD` (`0x40`) as wireframe triangles.
- The Voodoo2 setup path still handles `sDrawTriCMD` / `sBeginTriCMD` at `0xa8` / `0xa9`.
- Type-4 packet formatting was cleaned up against MAME `voodoo_2.cpp`.

Build verification:

```text
dotnet build EutherDrive.Core/EutherDrive.Core.csproj --no-restore /clp:ErrorsOnly
Build succeeded.
328 Warning(s)
0 Error(s)

dotnet build /tmp/eutherdrive-gauntlet-probe/GauntletProbe.csproj /clp:ErrorsOnly
Build succeeded.
377 Warning(s)
0 Error(s)
```

Short smoke after the patch:

```text
EUTHERDRIVE_GAUNTDL_RAW_DISK=/tmp/gauntd24.raw \
EUTHERDRIVE_GAUNTDL_CPU_STEPS_PER_FRAME=200000 \
EUTHERDRIVE_GAUNTDL_DUMP_FRAME=/tmp/gauntdl_frame_after_patch.ppm \
dotnet run --project /tmp/eutherdrive-gauntlet-probe/GauntletProbe.csproj --no-build -- /home/nichlas/roms/MAME/Midway/Vegas/gauntd 350

frame=350
pc=0xffffffff800194b8
voodoo regs=4967 fifoWords=1543 fifoPackets=523 drawPackets=0 directTriangles=0 setupTriangles=0
voodoo packetTypes=0:0,1:361,2:0,3:0,4:162,5:0,6:0,7:0
framebuffer=640x480 stride=2560 nonBlack=307200 colored=11332
```

Long 2600-frame check with the raw disk still shows visible Voodoo framebuffer activity but no guest draw packets yet:

```text
frame=2600
pc=0xffffffff80052f30
voodoo regs=12161969 fifoWords=14030154 fifoPackets=1873357 drawPackets=0 directTriangles=0 setupTriangles=0
voodoo packetTypes=0:0,1:3053,2:0,3:0,4:1870304,5:0,6:0,7:0
lfbWrites=43315200 fastFills=282 swaps=564
```

Focused CPU trace around `0x80052c80..0x80053020` shows the current hotspot is a command/list builder, not a Voodoo status poll. It builds register addresses such as `0xa80000a0`, `0xa80000a4`, `0xa80000a8`, and `0xa8000100` and returns a byte/word count around `0x28`. The next useful step is either to let a longer run reach the point where those lists are flushed as `ftriangleCMD`/setup commands, or to model/fast-path that command-list builder carefully enough to get to the actual flush sooner.

## 2026-05-09 Continued Boot Push / Glide Hotpaths

Added three narrowly signature-gated MIPS fastpaths in `GauntletDarkLegacyAdapter.cs`:

- `0x80052880`: unrolled Glide vertex/state copy loop, copies the remaining 16-byte blocks and resumes at `0x800528ac`.
- `0x80052bc0`: setup packet helper, writes `state+0x354/0x358/0x35c` directly and returns.
- `0x800526ac`: Glide state flush helper, writes the same two type-4 Voodoo FIFO register packets and updates `state+0x374/0x37c`.

Verification still builds clean:

```text
dotnet build EutherDrive.Core/EutherDrive.Core.csproj --no-restore /clp:ErrorsOnly
Build succeeded.
328 Warning(s)
0 Error(s)

dotnet build /tmp/eutherdrive-gauntlet-probe/GauntletProbe.csproj /clp:ErrorsOnly
Build succeeded.
377 Warning(s)
0 Error(s)
```

Current 450-frame smoke:

```text
EUTHERDRIVE_GAUNTDL_RAW_DISK=/tmp/gauntd24.raw \
EUTHERDRIVE_GAUNTDL_CPU_STEPS_PER_FRAME=200000 \
EUTHERDRIVE_GAUNTDL_DUMP_FRAME=/tmp/gauntdl_after_flush_fastpath2.ppm \
dotnet run --project /tmp/eutherdrive-gauntlet-probe/GauntletProbe.csproj --no-build -- /home/nichlas/roms/MAME/Midway/Vegas/gauntd 450

frame=450
pc=0xffffffff80052f08
voodoo regs=314939 fifoWords=360504 fifoPackets=50737 drawPackets=0 directTriangles=0 setupTriangles=0
voodoo packetTypes=0:0,1:3053,2:0,3:0,4:47684,5:0,6:0,7:0
lfbWrites=43315200 texWrites=1 fastFills=282 swaps=564
```

High-budget comparison:

```text
EUTHERDRIVE_GAUNTDL_RAW_DISK=/tmp/gauntd24.raw \
EUTHERDRIVE_GAUNTDL_CPU_STEPS_PER_FRAME=2000000 \
dotnet run --project /tmp/eutherdrive-gauntlet-probe/GauntletProbe.csproj --no-build -- /home/nichlas/roms/MAME/Midway/Vegas/gauntd 120

frame=120
pc=0xffffffff80052f00
voodoo regs=10110283 fifoWords=11662824 fifoPackets=1557713 drawPackets=0 directTriangles=0 setupTriangles=0
voodoo packetTypes=0:0,1:3053,2:0,3:0,4:1554660,5:0,6:0,7:0
```

The fastpaths move more FIFO/state work per run, but the game still only sends Voodoo type-4 state packets (`0x0e3f820c` etc.) in this phase. The type-3-like command words (`state+0x354 = 0x020014c3`, `state+0x358 = 0x02001403`) are present in RAM, but the copied state at `state+0x24c` currently looks like Glide/video state rather than model vertices. Do not synthesize draw packets from that block yet.

Focused caller trace around `0x800195c0..0x80019610` shows a repeating update path:

- `0x80019224` is called first and costs roughly 5.8k interpreted instructions in the sampled path.
- `0x800532f0` follows and returns a small status/value.
- `0x800527f0` / `0x800526ac` then pushes the Voodoo state packets.

Next best target is `0x80019224` or the broader update loop if it can be proven to be a wait/message helper. Otherwise keep tracing until the first FIFO type 3/type 5 or direct `0xa8000100` write appears.

## 2026-05-09 Continued Boot Push / Post-Reboot Hotspots

Added more narrowly signature-gated fastpaths in `GauntletDarkLegacyAdapter.cs`:

- `0x80019224`: caller-gated UI/message dispatch from the frame loop. Only fires for caller `0x800195d4` with the observed zero/small flags and returns `v0=0`.
- `0x8003ce94..0x8003cf40`: runtime copy helper covering byte/halfword/word/dword forward-copy loops, including branch-delay-slot resume. Restricted to main RAM ranges.
- `0x800511c8`: Glide two-word FIFO state packet tail. Writes `0x00010219` plus the computed state word to the signed Voodoo FIFO address, updates `state+0x374/0x37c`, restores `ra/s1/s0/sp`, and returns.

Build notes:

```text
dotnet build EutherDrive.Core/EutherDrive.Core.csproj --no-restore /p:BuildProjectReferences=false /clp:ErrorsOnly
Build succeeded.
328 Warning(s)
0 Error(s)

dotnet build /tmp/eutherdrive-gauntlet-probe/GauntletProbe.csproj /p:BuildProjectReferences=false /clp:ErrorsOnly
Build succeeded.
0 Warning(s)
0 Error(s)
```

A normal probe build that rebuilds all project references currently fails in unrelated `Third_party/MCS/mcs` code (`neogeo.cs` unsafe/overload errors). Use `BuildProjectReferences=false` for Gauntlet probe work until that side tree is fixed.

Key smoke results with `EUTHERDRIVE_GAUNTDL_CPU_STEPS_PER_FRAME=200000`:

```text
frame=450
pc=0xffffffff8003cee0 -> before runtime copy fastpath
voodoo fifoWords=194331 fifoPackets=64784 fastFills=4054 swaps=8108

frame=450
pc=0xffffffff80016d9c -> after runtime copy fastpath
voodoo fifoWords=200237 fifoPackets=66753 fastFills=4177 swaps=8354

frame=1800
pc=0xffffffff800511c8 -> before two-word state packet tail fastpath
voodoo regs=2035218 fifoWords=2635385 fifoPackets=878471 fastFills=54909 swaps=109818

frame=1800
pc=0xffffffff80053340 -> after two-word state packet tail fastpath
voodoo regs=2046731 fifoWords=2650319 fifoPackets=883449 fastFills=55220 swaps=110440
```

The current 1800-frame endpoint is now in the `0x800532f0..0x800533a0` Glide/state path. A focused trace shows it:

- validates the mapped Voodoo base (`state+0x004 == 0xa8000000`);
- updates state flags at `state+0x398`, `state+0x388`, and `state+0x38c`;
- calls `0x8005f9d0`;
- writes another two-word FIFO packet header `0x00010261` plus `state+0x280`;
- then updates FIFO room/pointer.

Still no guest triangle packets yet:

```text
drawPackets=0 directTriangles=0 setupTriangles=0
voodoo packetTypes=0:0,1:607369,2:0,3:0,4:276080,5:0,6:0,7:0
```

Frame dumps at `/tmp/gauntdl_after_memcpy_fastpath_900.png` and `/tmp/gauntdl_after_statepacket_fastpath_1800.ppm` are still fill/debugbar-only. Next useful target is the `0x800533xx` path, but it needs a fuller code dump before fast-pathing because it has state conditionals and a call to `0x8005f9d0`.

## 2026-05-09 Post-Freeze Runtime/Event Wrapper Push

Added two more signature-gated runtime fastpaths in `GauntletDarkLegacyAdapter.cs`:

- `0x80053340`: Glide buffer-swap packet tail. It decrements `state+0x38c`; when the counter reaches zero it emits the observed FIFO packets `0x00010261`/`state+0x280`, `0x00010221`/`state+0x26c`, and optional `0x00010241`/`0`, then updates `state+0x374/0x37c` and restores the frame.
- `0x8005d230..0x8005d344`: runtime table lookup. It scans the `0x800b4c30` table with stride `0xec`, compares signed `record+4` against the argument, updates `0x800b2f34` and `0x800b2f2c` on match, and returns `v0=1/0`.
- `0x8005fab4` / `0x8005fac0` / `0x8005faf4`: runtime event-poll wrapper. Only the safe early-return cases are fastpathed. If `record+0x58 != 0` and `record+0x5c == 0`, it falls back to interpreted execution so the callback/work branch remains faithful.

Important correction: `0x8005fab4` is the true wrapper entry. `0x8005fac0` is post-prologue and must restore `ra/fp/sp` from the current stack frame if fastpathed. Do not treat `0x8005fac0` as a no-frame entry point.

Current smoke status with `/tmp/eutherdrive-gauntlet-probe`, `frames=1800`, `CPU_STEPS_PER_FRAME=200000`:

```text
extra=512, helper-drain enabled
pc=0xffffffff8005fa70
previous blocker 0xffffffff8005faf4 is now passed

extra=4096
pc=0xffffffff80052bb8
voodoo regs=2046762 fifoWords=2650361 fifoPackets=883463 fastFills=55221 swaps=110442

extra=16384, broader helper-drain enabled
pc=0xffffffff8005ec0c
voodoo regs=2046843 fifoWords=2650463 fifoPackets=883497 fastFills=55223 swaps=110446
```

Still no real triangle traffic:

```text
drawPackets=0 directTriangles=0 setupTriangles=0
voodoo packetTypes=0:0,1:607402,2:0,3:0,4:276095,5:0,6:0,7:0
```

The next runtime hotspot is `0x8005ec0c`, reached from the event/cleanup path after `0x8005f9d0` and the `0x8005fab4` wrapper. It is not yet proven safe to fastpath; it likely performs event/callback cleanup and should be traced or dumped before any Core-side shortcut.

## 2026-05-10 Runtime Event Status No-Callback Fastpath

Added a conservative Core fastpath for the observed no-callback path in `0x8005ec0c`.

Trace summary:

- `0x8005ec0c` receives an output pointer in `a0` and a status/value in `a1`.
- It computes an event offset from `a0 - record+4`.
- The hot path checks the current runtime record at `0x800b2f2c`.
- In observed boot/render-init traffic, `record+0xd8 == 0`, so there is no callback.
- That path sets a local success flag, writes `a1` to `*a0`, then returns.

The new fastpath is deliberately narrow:

- requires the exact `0x8005ec0c` signature;
- requires `record+0xd8 == 0`;
- performs the real `_memory.Write32(a0, a1)` so Voodoo/PCI writes such as `0xa8000210`, `0xa8000214`, and `0xa8000244` are preserved;
- returns with `v0 = a0`, `v1 = a1`, and falls back to interpreted execution if a callback exists.

Build and smoke:

```text
dotnet build EutherDrive.Core/EutherDrive.Core.csproj --no-restore /p:BuildProjectReferences=false /clp:ErrorsOnly
Build succeeded.
0 Warning(s)
0 Error(s)

dotnet build /tmp/eutherdrive-gauntlet-probe/GauntletProbe.csproj --no-restore /p:BuildProjectReferences=false /clp:ErrorsOnly
Build succeeded.
0 Warning(s)
0 Error(s)

dotnet /tmp/eutherdrive-gauntlet-probe/bin/Debug/net8.0/GauntletProbe.dll \
  /home/nichlas/roms/MAME/Midway/Vegas/gauntd 1800 200000 16384

extraCpuSteps=16384
drainedHelperSteps=238
pc=0xffffffff8005ed8c
voodoo regs=2046844 fifoWords=2650463 fifoPackets=883497
fastFills=55223 swaps=110446
```

Still no triangle traffic:

```text
drawPackets=0 directTriangles=0 setupTriangles=0
voodoo packetTypes=0:0,1:607402,2:0,3:0,4:276095,5:0,6:0,7:0
```

Note for next pass: the temp probe drain currently stops at `0x8005ed8c`, which is the epilogue area after the event-status helper. If continuing from this exact state, either widen the probe-only drain through the final epilogue or trace the caller around `ra=0xffffffff8005dfc8`. Do not synthesize draw packets yet; the guest still has not submitted FIFO type 3/type 5 or direct triangle commands.

## 2026-05-12 Runtime Read/Delay Helper Fastpath

Continued the post-event/runtime push toward first real Gauntlet graphics.

Added a narrow signature-gated Core fastpath in `GauntletDarkLegacyAdapter.cs` for:

- `0x8005e37c`: wrapper around the runtime read/delay helper.
- `0x8005eda4`: helper that reads `*(a0)`, calls a short delay routine, and returns the read value.

The fastpath deliberately preserves the actual `_memory.Read32(a0)` so MMIO/Voodoo status reads still get their existing side effects. It only skips the wrapper/prologue/delay overhead.

Verification:

```text
dotnet build EutherDrive.Core/EutherDrive.Core.csproj --no-restore /p:BuildProjectReferences=false /clp:ErrorsOnly
Build succeeded.
0 Warning(s)
0 Error(s)

dotnet build /tmp/eutherdrive-gauntlet-probe/GauntletProbe.csproj --no-restore /clp:ErrorsOnly
Build succeeded.
328 Warning(s)
0 Error(s)
```

Probe status with `/tmp/eutherdrive-gauntlet-probe`, raw disk `/tmp/gauntd24.raw`, `frames=1800`, `CPU_STEPS_PER_FRAME=200000`:

```text
extra=1048576 before the fastpath:
pc=0xffffffff8005e37c
voodoo regs=2054059 fifoWords=2659823 fifoPackets=886617 fastFills=55418 swaps=110836

extra=1049600 after the fastpath:
pc=0xffffffff8004cc24
voodoo regs=2054081 fifoWords=2659851 fifoPackets=886624 fastFills=55419 swaps=110838

extra=2097152 after the fastpath:
pc=0xffffffff80015280
voodoo regs=2061407 fifoWords=2669355 fifoPackets=889792 fastFills=55617 swaps=111234
```

Still no real triangle traffic:

```text
drawPackets=0 directTriangles=0 setupTriangles=0
voodoo packetTypes=0:0,1:611728,2:0,3:0,4:278064,5:0,6:0,7:0
```

The `0x8004cc24` stop was dumped and is not a small device helper. It is part of a larger formatter/dispatcher path beginning around `0x8004cbd0` with callbacks and stack arguments, so it was not fastpathed. The current next target is `0x80015280` (`lastOp=0x8e070004`) after a 2M-extra run. Dump or trace `0x80015200..0x80015300` before deciding whether it is safe to model.

## 2026-05-12 Probe Sweep Optimization

The repeated Gauntlet probe runs were spending most wall time re-running the same 1800-frame warmup. The temp probe at `/tmp/eutherdrive-gauntlet-probe/Program.cs` now supports:

```text
EUTHERDRIVE_GAUNTDL_EXTRA_SERIES=1048576,2097152,4194304,8388608
```

When set, it runs the frame warmup once, then advances cumulatively to each extra-step checkpoint in the same process and prints one compact `checkpoint` line per target. This is currently probe-only and does not affect Core.

Observed sweep with `frames=1800`, `CPU_STEPS_PER_FRAME=200000`:

```text
checkpoint extra=1048576 pc=0xffffffff8005e37c fifoPackets=886617 drawPackets=0
checkpoint extra=2097152 pc=0xffffffff8006cd80 fifoPackets=889792 drawPackets=0
checkpoint extra=4194304 pc=0xffffffff8003a080 fifoPackets=896128 drawPackets=0
checkpoint extra=8388608 pc=0xffffffff8005ed8c fifoPackets=908809 drawPackets=0
```

This cut the workflow cost from several full warmups to one full warmup plus incremental stepping. Still no type 3/type 5 or triangle traffic by 8M extra steps:

```text
voodoo packetTypes=0:0,1:624804,2:0,3:0,4:284005,5:0,6:0,7:0
```

## 2026-05-12 Probe Warmup Snapshot

Added a probe-only warmup snapshot path in `/tmp/eutherdrive-gauntlet-probe/Program.cs`. This caches the full state after the expensive Gauntlet warmup: adapter frame counter/buffer, R5000 CPU state, Vegas RAM/register state, IDE/SIO/PCI state, and the Voodoo bringup backend counters/buffers.

Use:

```text
EUTHERDRIVE_GAUNTDL_WARMUP_STATE=/tmp/gauntdl_warmup_1800_200k_v1.bin
```

If the file exists, the probe loads it immediately after `LoadRom()` and skips the 1800-frame warmup. If the file is missing, the probe runs the warmup once and saves it. Set `EUTHERDRIVE_GAUNTDL_SAVE_WARMUP=1` to force rewriting the snapshot, and `EUTHERDRIVE_GAUNTDL_LOAD_WARMUP=0` to ignore an existing snapshot.

Created and verified:

```text
/tmp/gauntdl_warmup_1800_200k_v1.bin
frames=1800
CPU_STEPS_PER_FRAME=200000
pc=0xffffffff80053340
voodoo regs=2046731 fifoWords=2650319 fifoPackets=883449
```

The immediate load path reproduces the same PC/counters and avoids the frame-progress warmup. A full 1M/2M/4M/8M extra-step sweep from the snapshot completed in seconds and reproduced the current checkpoints:

```text
checkpoint extra=1048576 drained=3 pc=0xffffffff8005e37c fifoPackets=886617 drawPackets=0
checkpoint extra=2097152 drained=0 pc=0xffffffff8006cd80 fifoPackets=889792 drawPackets=0
checkpoint extra=4194304 drained=0 pc=0xffffffff8003a080 fifoPackets=896128 drawPackets=0
checkpoint extra=8388608 drained=55 pc=0xffffffff8005ed8c fifoPackets=908809 drawPackets=0
```

This is still probe-only and intentionally not a general emulator save-state. It is enough to make divergent Gauntlet trace/dump passes cheap while bringup is still hunting the first type 3/type 5 draw traffic.

## 2026-05-12 Long Snapshot Sweep

Ran a larger extra-step sweep from `/tmp/gauntdl_warmup_1800_200k_v1.bin`:

```text
checkpoint extra=16777216 drained=0 pc=0xffffffff80051370 fifoPackets=934174 drawPackets=0
checkpoint extra=33554432 drained=0 pc=0xffffffff80018efc fifoPackets=984896 drawPackets=0
checkpoint extra=67108864 drained=105 pc=0xffffffff8005ed8c fifoPackets=1086345 drawPackets=0
checkpoint extra=134217728 drained=0 pc=0xffffffff8005eda4 fifoPackets=1289241 drawPackets=0
```

Still no real draw traffic even at 128M extra:

```text
directTriangles=0 setupTriangles=0
voodoo packetTypes=0:0,1:886351,2:0,3:0,4:402890,5:0,6:0,7:0
```

Added a probe-only narrow code-dump env var:

```text
EUTHERDRIVE_GAUNTDL_DUMP_CODE_RANGES=0xffffffff80018e80:48,0xffffffff80051320:48
```

Traced `0x80018e80..0x80018f40` at the 32M stop. It is a tight byte/text pack loop that reads bytes from `0x800a5cxx`, converts/combines character values, and writes halfwords through `s2`; it is not the missing Voodoo draw submit. The more useful current target is `0x80051320..0x800513d0`: it touches the command/state area at `0x800b4d20`, updates values around `+0x37c`, and was seen at the 16M checkpoint. Next pass should trace/dump that path and its callers rather than extending blind sweeps.

## 2026-05-12 IOASIC Shuffle/PIC Pass

Implemented a first Gauntlet-DL-specific IOASIC model in `GauntletDarkLegacyAdapter.cs`:

- Wired the loaded `346_gauntlet-dl.u37` security PIC payload into `VegasMemoryMap`.
- Added the MAME `SHUFFLE_GAUNTDL` register map and IOASIC unlock state.
- Added a deterministic MAME-style serial PIC2 simulator for serial number, RTC, and NVRAM commands.
- Updated the existing IOASIC/PIC bit-wait fastpath to mark the IOASIC unlocked, because that fastpath skips the hardware-side unlock path during boot.
- Updated the probe warmup snapshot format to v2 so it serializes the new IOASIC/PIC state.

The old snapshot was invalid for this pass because it had no IOASIC shuffle/PIC fields. Rebuilt:

```text
/tmp/gauntdl_warmup_1800_200k_v1.bin
frames=1800
CPU_STEPS_PER_FRAME=200000
pc=0xffffffff80015784
voodoo regs=14086 fifoWords=13371 fifoPackets=4464
```

This moved the failure mode. The previous repeated serial callback at `0x80015248` is no longer the active stop from the saved state. The new hard stop is a fatal loop at:

```text
pc=0xffffffff80015784
ra=0xffffffff80015784
s0=0x11
s1=0x300b
```

Tracing the caller showed it entered from `0x80015ed4` with the format/error path around `0x80015708`. The string table at `0x80089660` identifies the next blocker:

```text
"Unable to get home blocks:"
```

So the bringup has moved from IOASIC/PIC serial failure to disk/filesystem boot-volume discovery. Voodoo still only sees clear/swap/FIFO state packets:

```text
drawPackets=0 directTriangles=0 setupTriangles=0
packetTypes=0:0,1:3065,2:0,3:0,4:1399,5:0,6:0,7:0
```

Frame dump after the IOASIC unlock pass:

```text
/tmp/gauntdl_after_unlock_200k.png
```

It is still just clear/overlay output, not game graphics. Next pass should inspect IDE/raw disk reads and the boot filesystem home-block parser around `0x80015e80..0x80015f40`, not Voodoo draw submission.

## 2026-05-12 Disk/IDE Pass

Committed the IOASIC/PIC bring-up checkpoint as:

```text
a959eff gauntdl ioasic bringup
```

MAME does not model a high-level GUTS filesystem for Vegas. It exposes a CMD/Silicon Image IDE PCI controller and an `ide_hdd_device`; the game filesystem is read by the guest from disk sectors:

```text
IDE_PCI(config, PCI_ID_IDE, 0, 0x10950646, 0x05, 0x0)
ide.irq_handler().set(PCI_ID_NILE, FUNC(vrc5074_device::pci_intr_d))
DISK_REGION(PCI_ID_IDE ":ide:0:hdd")
```

This pass moved the adapter in that direction:

- Added ATA command handling for `READ SECTORS NO RETRY` (`0x21`), `READ MULTIPLE` (`0xc4`), `READ DMA` (`0xc8`), and `SET CONFIG` (`0x91`).
- Split disk addressing into LBA28 vs CHS decode so the guest can use either IDE mode.
- Added a device-control `nIEN` bit and IDE interrupt pending state.
- Wired IDE PCI interrupts into NILE PCI INTD (`bit 11`), matching MAME's `pci_intr_d` route.
- Kept SIO/DUART on NILE PCI INTC (`bit 10`).

Verified:

```text
dotnet build EutherDrive.Core/EutherDrive.Core.csproj --no-restore /p:BuildProjectReferences=false /clp:ErrorsOnly
Build succeeded. 326 Warning(s), 0 Error(s)

dotnet build /tmp/eutherdrive-gauntlet-probe/GauntletProbe.csproj --no-restore /clp:ErrorsOnly
Build succeeded. 326 Warning(s), 0 Error(s)
```

Probe after the ATA/IRQ work still reaches the same fatal loop:

```text
rom=gauntdl24
frame=1000
pc=0xffffffff80015784
attached=True
voodoo regs=14086 fifoWords=13371 fifoPackets=4464 drawPackets=0
```

The important trace result is that the guest still only performs IDE IDENTIFY and SET FEATURES:

```text
[GAUNTDL:IDE] write r7=ec
[GAUNTDL:IDE] identify
[GAUNTDL:IDE] write r7=ef
[GAUNTDL:IDE] set features feature=03 value=08
```

There are no `read sectors`, `READ DMA`, bus-master DMA, or unsupported IDE commands before the `/d0` failure. The raw disk sidecar is usable and contains plausible GUTS/home-block data (`0xfeedf00d` / `0xf00dface` near sectors 1 and 2), so the current blocker is not sector decoding yet.

Current conclusion:

- The adapter now has the lower ATA commands and MAME-style IDE INTD route needed for the next phase.
- The guest fails earlier: the filesystem open path returns `0x300b` before any sector I/O.
- RAM dumps show populated device/list nodes around `0x800b2ee0` and heap nodes such as `0x800e6748`, `0x800e6a60`, `0x800e6d78`, `0x800e7090`, but the `/d0` block device path still is not resolved.
- NILE tracing confirms the guest enables high interrupt control bits for INTC/INTD (`0x8000ba00`), so the next investigation should follow the IDE driver's registration path and device table names/ops, not add a high-level fake filesystem yet.

## 2026-05-12 CMD646 Control Follow-Up

Added one more MAME-aligned CMD646 compatibility fix in `GauntletDarkLegacyAdapter.cs`:

- The PCI0646U `0x0c40` BAR/control enable bits now reset at PCI config `0x50`, matching MAME's `ide_pci_device`, instead of `0x40`.
- Guest writes to config dword `0x08` now update the programming-interface byte at `0x09`, so the write from `0x01018a05` to `0x01018f05` reads back correctly.
- IDE interrupt assertion now mirrors bit `0x04` in the same `0x50` control/status dword and writing that bit clears it, matching MAME's `pcictrl_w` behavior.

Verified:

```text
dotnet build EutherDrive.Core/EutherDrive.Core.csproj --no-restore /p:BuildProjectReferences=false /clp:ErrorsOnly
Build succeeded. 326 Warning(s), 0 Error(s)

dotnet build /tmp/eutherdrive-gauntlet-probe/GauntletProbe.csproj --no-restore /p:BuildProjectReferences=false /clp:ErrorsOnly
Build succeeded. 1 Warning(s), 0 Error(s)
```

The corrected trace now shows the expected MAME-style readback:

```text
[GAUNTDL:IDEPCI] pci cfg read off=50 value=00000c40
```

This still does not reach sector I/O. The more precise blocker is now:

```text
qio_getioq @ 0x80014724 returns [0x800b2dd8]
```

By the `/rd0` open call at `0x80022a48`, `0x800b2dd8` is already zero, so the open object never gets a real underlying handle. `/d0` then fails because the object field at `+0x0c` is `-1`, and the mount/open wrapper returns `0x300b`.

Next useful implementation target:

1. Trace who consumes the QIO free list before `/rd0` and why those entries are not returned.
2. Inspect the completion paths around `0x800146c0..0x80014814` and the queue nodes rooted at `0x800b2dd8/0x800b2dcc`.
3. Only after `/rd0` gets a valid handle should disk-sector/home-block parsing be expected to run.

## 2026-05-12 QIO Trace Correction

Added faster probe/trace support:

- Memory trace lines now include the current CPU PC.
- `EUTHERDRIVE_GAUNTDL_TRACE_MEM_ADDRESS` accepts comma-separated addresses and `address:length` ranges.
- `GauntletProbe` can scan main RAM pointers with `EUTHERDRIVE_GAUNTDL_SCAN_POINTERS`.

Important correction to the previous QIO hypothesis:

- `0x800b2dd8` is not the QIO free list. It is the temporary "current QIO" global used while callback dispatch runs.
- Dispatch at `0x80014698..0x800146c8` loads the current-QIO field pointer from the event record, saves the old value, writes the event's current-QIO value, calls the callback, then restores the saved value.
- For the failing `/rd0` callback, event record `0x800e7840` has callback `0x80032504`, argument `0x800e7810`, but its current-QIO field is zero. Therefore `qio_getioq` correctly reports no current IOQ for that callback rather than showing a consumed freelist.

The failing path is now pinned down:

```text
0x800e7810 = /rd0 object
  +0x0c = 0xffffffff
  +0x14 = 0x0000300b
  +0x64 = 0x80089618 -> "/rd0"

0x80032504 callback:
  calls fd lookup with +0x0c == -1
  writes status 0x3500

0x80029470 path:
  reads +0x0c == -1
  writes final status 0x300b at pc=0x800294b4
```

Pointer scan result at frame 500:

```text
pointerScan needles=0x80089618,0x80089634,0x800a6da4,0x800e7810,0x800e7880,0xa40001f0
pointer 0xffffffff800e7864 -> 0x80089618
pointer 0xffffffff800a6da4 -> 0x800e7880
pointer 0xffffffff800a6df8 -> 0xa40001f0
pointerScan matches=44
```

No pointer to the literal `/d0` string (`0x80089634`) appears in RAM at the fatal state. IDE tracing from cold boot still shows only IDENTIFY and SET FEATURES, with no sector reads or DMA before the failure. The next target is the raw-disk/open plumbing that should give `/rd0` a valid lower handle before `0x80032504`, not ATA sector transfer yet.

## 2026-05-12 FD Slot Helper Finding

The actual bad write to the `/rd0` object's handle is now narrower:

```text
generic open 0x80021774..0x80021868
  0x800217bc writes object +0x0c = -1 as an initial value
  0x8002182c calls fd-slot allocator 0x80020f0c
  0x80021848 calls fd-slot-to-handle helper 0x80020b54
  0x80021854 stores helper return into object +0x0c
```

Trace with `EUTHERDRIVE_GAUNTDL_TRACE_CPU_RA=0xffffffff80021850` showed valid slot pointers such as `0xffffffff800a6170`, `0xffffffff800a61a0`, and `0xffffffff800a61d0` entering `0x80020b54`, but the helper returned `-1`. The helper builds its range constants as zero-extended `0x00000000800a6170..0x00000000800a6d70`, while the allocator returns sign-extended pointers, so its two `sltu` checks reject valid slots.

Global CPU changes were tested and rejected:

- Sign-extending `lui` broke cold boot in the boot ROM.
- Making all `slt/sltu/slti/sltiu` word-sized broke early runtime init at `0x80022f24`.
- Sign-extending `addi/addiu` results broke cold boot at `0xffffffff81000000`.

Current code therefore includes an experimental, signature-checked fast path for just `0x80020b54`, returning `(slot - 0x800a6170) / 0x30 | *(slot + 0x14)` for valid slots and `-1` outside the fd table. It is gated behind `EUTHERDRIVE_GAUNTDL_FIX_FD_SLOT_HANDLE=1`.

## 2026-05-12 FD Slot Validation and QIO Completion Blocker

The fd-slot helper fast path was narrowed to the `/rd0` generic-open call site:

```text
pc=0xffffffff80020b54
ra=0xffffffff80021850
s2=0xffffffff800e7810
slotSize=0x30
```

This keeps early init stable. With `EUTHERDRIVE_GAUNTDL_CPU_STEPS_PER_FRAME=200000`, a cold 500-frame run has Voodoo init alive both with and without the flag:

```text
default:
  pc=0xffffffff80015788
  voodoo regs=14086 fifoPackets=4464 fastFills=284 swaps=568
  /rd0 +0x0c = 0xffffffff
  /rd0 +0x14 = 0x0000300b

EUTHERDRIVE_GAUNTDL_FIX_FD_SLOT_HANDLE=1:
  pc=0xffffffff80015a30
  voodoo regs=14049 fifoPackets=4448 fastFills=283 swaps=566
  /rd0 +0x0c = 0x00000004
  /rd0 +0x14 = 0x00000000
```

With the raw sidecar enabled, the fd fix reaches real IDE DMA for the first time in this path:

```text
EUTHERDRIVE_GAUNTDL_FIX_FD_SLOT_HANDLE=1
EUTHERDRIVE_GAUNTDL_RAW_DISK=/tmp/gauntd24.raw
EUTHERDRIVE_GAUNTDL_TRACE_IDE=1

[GAUNTDL:IDE] write r7=c8
[GAUNTDL:IDE] read sectors lba=1 count=1
```

Without the raw sidecar the same command fails at the existing CHD limitation:

```text
command c8 failed: Compressed CHD sector reads are not ported yet.
```

The new blocker is QIO/asynchronous completion rather than ATA command issue. After the DMA, the guest waits at:

```text
0x80015a2c: lw a2,0x14(s6)
0x80015a30: beqz a2,0x80015a2c
s6=0xffffffff800e7810
```

Trace shows the status field being written and then cleared:

```text
pc=0xffffffff80032544 write32 0xffffffff800e7824 0x00003500
pc=0xffffffff8002953c write32 0xffffffff800e7824 0x00000000
```

`0x8002953c` builds a child QIO object and installs callback `0x80029230`; the child and follow-up objects remain pending:

```text
0x800e7880:
  +0x1c = 0x80029230
  +0x20 = 0x800e7810
  +0x24 = 0x00000002

0x800e7960:
  +0x1c = 0x80029230
  +0x20 = 0x800e7810
  +0x24 = 0x00000002
```

An experimental probe-only status override was added:

```text
EUTHERDRIVE_GAUNTDL_FORCE_RD0_OPEN_STATUS=0x3500
```

It only writes `/rd0 +0x14` at the known `/rd0` poll PCs (`0x80015a2c` and `0x80022b88`). This is not a real fix. It confirms the missing completion diagnosis:

```text
fd fix + raw disk:
  frame=600 pc=0xffffffff80015a2c

fd fix + raw disk + forced /rd0 status:
  frame=900 pc=0xffffffff80015788
  frame=1800 pc=0xffffffff80015788
```

The forced status gets past the two `/rd0` wait loops but does not rejoin the previous large FIFO path; Voodoo remains at the small init counters (`fifoPackets=4464`, `drawPackets=0`). Next useful target is therefore a real QIO completion model for the `0x80029230` / `0x800322c0` async chain, not another scalar status poke.

Also tested `EUTHERDRIVE_GAUNTDL_NILE_IRQ_SHIFT8=1`, which maps NILE vectors from CP0 IP bit 8 instead of bit 10. It changes pending Cause from `0xa000` to `0x8800` in this state but does not complete the QIO chain, so it remains an experiment and should not be enabled by default.

## 2026-05-12 BMDMA Byte Start and Callback Chain

The IDE blocker after the fd-slot fix was not ATA command issue; the guest programmed the bus-master command register one byte at a time:

```text
bmdma write8 off=00 value=08
bmdma write off=04 value=000a6e30
bmdma write8 off=00 value=09
write r7=c8
read sectors lba=1 count=1
```

`VegasIdePciDevice` now routes 8/16-bit PCI I/O writes through the bus-master register logic and attempts DMA both when the command byte starts and when ATA command `0xc8` creates DRQ. With:

```text
EUTHERDRIVE_GAUNTDL_FIX_FD_SLOT_HANDLE=1
EUTHERDRIVE_GAUNTDL_RAW_DISK=/tmp/gauntd24.raw
```

the first sector now copies into the guest buffer:

```text
[GAUNTDL:IDE] read sectors lba=1 count=1
[GAUNTDL:IDE] dma transfer bytes=512
[GAUNTDL:IDEPCI] bmdma primary read copied=512

bytes[0xffffffff800f41e0]:
  0d f0 ed fe 05 00 01 00 ...
```

A gated `EUTHERDRIVE_GAUNTDL_IDE_DMA_SWAP32=1` experiment proves the byte order can be changed to `fe ed f0 0d`, but that is not the correct path for the current emulated MIPS memory reads: it prevents the existing QIO completion probe from recognizing the sector. Leave it off.

The remaining blocker is the async completion path from IDE interrupt to QIO callbacks. Two gated probes document the missing chain:

```text
EUTHERDRIVE_GAUNTDL_FIX_RD0_DMA_QIO_COMPLETE=1
  after DMA, marks the first /rd0 child QIO complete enough to leave 0x80015a2c

EUTHERDRIVE_GAUNTDL_FIX_RD0_ASYNC_CALLBACK=1
  kicks the follow-up callbacks at 0x80029230 / 0x800325a0 under signature checks
```

With both probes enabled, `/rd0` reaches the same final state as the old scalar status poke, but through guest callbacks:

```text
frame=1800
pc=0xffffffff80015784
/rd0 +0x0c = 0xffffffff
/rd0 +0x14 = 0x00003500
voodoo fifoPackets=4464 drawPackets=0
```

This proves:

- BMDMA sector transfer is now real.
- The first and second `/rd0` wait loops can be crossed without `EUTHERDRIVE_GAUNTDL_FORCE_RD0_OPEN_STATUS`.
- Still no further IDE commands are issued after the home-sector open completes, and Voodoo remains in the init/fill-only state.

Re-testing `EUTHERDRIVE_GAUNTDL_NILE_IRQ_SHIFT=8`, `9`, and `11` after the BMDMA fix still leaves the guest at `0x80015a2c`; the IRQ bit position is not sufficient. The next real implementation target is a generalized IDE interrupt / event dispatch model that runs the queued QIO callback chain instead of the current `/rd0`-specific kicks.

## 2026-05-13 `/rd0` Callback Ordering Pass

The `/rd0` async probe was choosing the final callback (`0x800325a0`) before the active open/read callback (`0x80029230`). A new env-gated trace was added:

```text
EUTHERDRIVE_GAUNTDL_TRACE_RD0_HOME=1
```

It logs `/rd0` poll candidates, callback kicks, and the fatal print message at `0x80015708`.

The trace showed this candidate set at the second `/rd0` poll:

```text
qio+0e0 cb=800325a0 owner=800e7810 stage=0
qio+150 cb=80029230 owner=800e7810 stage=3 buf=80104008
```

The `0x80029230` signature offsets were corrected, and callback selection now runs that active stage-3 QIO first, then lets finalization run after it reaches stage 4. This removes the premature final-callback ordering bug.

Verified build:

```text
dotnet build tools/GauntletProbe/GauntletProbe.csproj --no-restore /clp:ErrorsOnly
Build succeeded. 383 Warning(s), 0 Error(s)
```

Current probe:

```text
EUTHERDRIVE_GAUNTDL_FIX_FD_SLOT_HANDLE=1
EUTHERDRIVE_GAUNTDL_FIX_RD0_DMA_QIO_COMPLETE=1
EUTHERDRIVE_GAUNTDL_FIX_RD0_ASYNC_CALLBACK=1
EUTHERDRIVE_GAUNTDL_TRACE_RD0_HOME=1
EUTHERDRIVE_GAUNTDL_RAW_DISK=/tmp/gauntd24.raw

frame=500
pc=0xffffffff80015784
msg="No boot file on volume"
```

This is forward progress in diagnosis: the home-block failure is gone, and the fatal text is now the next filesystem phase. IDE trace still shows only one physical read (`READ DMA`, LBA 1, count 1). The raw disk does contain game/boot file strings such as `worlds.rom`, so the next implementation target is still the real post-home-block QIO/IDE read dispatch, not Voodoo and not byte-swapping.

## 2026-05-13 Boot Slot / Stage-4 Probe

Added a `boot-slot-check` trace at `0x80015b38` and an optional IOASIC port-0 override:

```text
EUTHERDRIVE_GAUNTDL_IOASIC_PORT0=0xffef
EUTHERDRIVE_GAUNTDL_TRACE_IOASIC_INPUTS=1
```

The boot code computes the selected boot slot from `(((port0 >> 4) & 3) ^ 3)`. Testing slots 0..3 still ends at `No boot file on volume`, so DIP slot selection is not the current blocker.

At the slot check the parsed home/boot table remains zero:

```text
selected=ffffffff807ffd08:00000000
f00=00000000 f04=00000000 f40=00000000 f44=00000000 f64=00000000
slot0=00000000 slot1=00000000 slot2=00000000 slot3=00000000
```

Tracing callback dispatch showed stage 3 jumps to `0x800293e4`, stores stage 4, then calls `0x80020ed8` and `0x80020914`. Allowing stage 4 to be kicked repeatedly does not populate the parsed table; it loops idempotently and still reaches the same empty slot state. The useful next target is still event/IRQ-driven QIO completion or the parser/copy path that should turn the valid raw home sector at `0x800f41e0` into the stack table at `s0=0x807ffcb8`.

## 2026-05-13 Pause Handoff: Home Table Crossed, Stage-4 Read Wait Next

Paused intentionally at the user's request. No Gauntlet probe should be left running.

Current uncommitted Gauntlet-local changes are in `EutherDrive.Core/Arcade/Vegas/GauntletDarkLegacyAdapter.cs`. Unrelated dirty files still exist in the worktree and should not be reverted as part of Gauntlet work.

What changed in this pause window:

- Added `EUTHERDRIVE_GAUNTDL_FIX_RD0_HOME_TABLE`.
- Added a narrow `ApplyKnownRd0HomeTableParse()` hook.
- The hook runs at the boot-slot check (`0xffffffff80015b38`) after the home sector has been DMA-read to `0xffffffff800f41e0`.
- It verifies home-sector magics `0xfeedf00d` at `0x800f41e0` and `0xfe1dfaed` at `0x800f4218`.
- It copies the three boot candidates from home-sector offsets `0x48..0x50` into the runtime boot table: `0x00197901`, `0x0032f201`, `0x000000a6`.
- Added the new wait helper PCs to `TryGetKnownRuntimeQioPollObject()`: `0xffffffff80022f18`, `0xffffffff80022f20`, `0xffffffff80022f24`.

Verified build:

```text
dotnet build tools/GauntletProbe/GauntletProbe.csproj --no-restore /clp:ErrorsOnly
Build succeeded. 383 Warning(s), 0 Error(s)
```

Most useful reproduction command:

```sh
env EUTHERDRIVE_GAUNTDL_FIX_FD_SLOT_HANDLE=1 \
    EUTHERDRIVE_GAUNTDL_FIX_RD0_DMA_QIO_COMPLETE=1 \
    EUTHERDRIVE_GAUNTDL_FIX_RD0_ASYNC_CALLBACK=1 \
    EUTHERDRIVE_GAUNTDL_FIX_RD0_HOME_TABLE=1 \
    EUTHERDRIVE_GAUNTDL_TRACE_RD0_HOME=1 \
    EUTHERDRIVE_GAUNTDL_TRACE_IDE=1 \
    EUTHERDRIVE_GAUNTDL_RAW_DISK=/tmp/gauntd24.raw \
    dotnet run --project tools/GauntletProbe/GauntletProbe.csproj --no-build -- /home/nichlas/roms/MAME/Midway/Vegas/gauntd 1000 200000
```

Key trace from the good run:

```text
[GAUNTDL:RD0] home-table pc=ffffffff80015b38 table=ffffffff807ffcb8 bootCandidates=00197901,0032f201,000000a6
[GAUNTDL:RD0] panic-site boot-slot-check ... selected=ffffffff807ffd08:00197901 ... f04=00010002 f64=00000001 slot0=00197901 slot1=00197901 slot2=00197901 slot3=00197901
[GAUNTDL:RD0] kick pc=ffffffff80022f18 object=ffffffff800e7810 qio=ffffffff800e7880 cb=ffffffff80029230 stage=00000003 status=00000004 buf=80104008 arg=00000000
```

Current state after the hook:

```text
frame=800
pc=0xffffffff80022f18
lastOp=0x00a0102d
voodoo regs=14049 fifoWords=13323 fifoPackets=4448 drawPackets=0
voodoo fastFills=283 swaps=566 framebuffer nonBlack=147730 colored=17682
```

This is real progress: the old `No boot file on volume` fatal path is crossed, and Voodoo still has visible/fill activity. The new blocker is the wait helper at `0x80022f08..0x80022f2c`.

Decoded wait helper:

```text
0x80022f08: 14a00008 0000182d
0x80022f10: 8c850014 8c820018
0x80022f18: 10620002 00000000
0x80022f20: 8c830018 10a0fffa
0x80022f28: 00000000 03e00008
0x80022f30: 00a0102d
```

At the stop:

- `a0/s0 = 0xffffffff800e7810` (`/rd0` object)
- `object+0x14 = 0`
- `object+0x18 = 0`
- `object+0x2c = 0x80104048`
- child QIO at `object+0x70` has callback `0x80029230`, owner `0x800e7810`, stage `4`, buffer `0x80104008`

Important raw-sector facts:

```text
raw LBA 0x00197901: ce fa 0d f0 70 86 00 00 64 00 00 00 01 04 00 00
raw LBA 0x0032f201: ce fa 0d f0 70 86 00 00 64 00 00 00 01 04 00 00
raw LBA 0x000000a6: be ba ed c0 d4 5d 16 00 2f 0b 00 00 01 04 00 00
```

The next implementation target should not be graphics yet. The hook made the boot table valid, and the code now enters the boot-sector read path, but stage 4 does not complete the async read state. The most direct next step is a narrow env-gated completion for this exact stage-4 `/rd0` read:

- Recognize `/rd0` object at `0x800e7810`.
- Recognize child QIO at `0x800e7880`, callback `0x80029230`, owner `0x800e7810`, stage `4`.
- Confirm current PC is the wait helper (`0x80022f18`, `0x80022f20`, or `0x80022f24`).
- Confirm the boot table already contains candidates from the home-sector hook.
- Fill the expected read buffer from the raw disk candidate sector, or better route through the existing IDE DMA/QIO path if there is enough context.
- Set the completion field that makes `object+0x14` or `object+0x18` change so `0x80022f08` returns naturally.

Do not just keep kicking `0x80029230`: the trace already proved one kick moves stage 3 to stage 4, then repeated polls stay at stage 4 with `object+0x14 == 0` and `object+0x18 == 0`.

Useful dump command if resuming:

```sh
env EUTHERDRIVE_GAUNTDL_FIX_FD_SLOT_HANDLE=1 \
    EUTHERDRIVE_GAUNTDL_FIX_RD0_DMA_QIO_COMPLETE=1 \
    EUTHERDRIVE_GAUNTDL_FIX_RD0_ASYNC_CALLBACK=1 \
    EUTHERDRIVE_GAUNTDL_FIX_RD0_HOME_TABLE=1 \
    EUTHERDRIVE_GAUNTDL_RAW_DISK=/tmp/gauntd24.raw \
    EUTHERDRIVE_GAUNTDL_DUMP_CODE_RANGES=0xffffffff80029200:192,0xffffffff80022ee0:128,0xffffffff80023000:128 \
    EUTHERDRIVE_GAUNTDL_DUMP_BYTES_RANGES=0xffffffff800e7810:768,0xffffffff80104000:1024 \
dotnet run --project tools/GauntletProbe/GauntletProbe.csproj --no-build -- /home/nichlas/roms/MAME/Midway/Vegas/gauntd 800 200000
```

## 2026-05-13 Speed Pass: Release Probe + Render Skip

The slow debug loop was dominated by two avoidable costs:

- Running `tools/GauntletProbe` as a Debug build.
- Rendering the 640x480 Voodoo bring-up framebuffer every emulated frame even when the probe only needs CPU/device state.

Added:

- `EUTHERDRIVE_GAUNTDL_SKIP_FRAME_RENDER=1`
  - `GauntletDarkLegacyAdapter.RunFrame()` now runs CPU/SIO/Voodoo state without drawing each frame.
  - `GetFrameBuffer()` still forces one render when the probe asks for a final dump.
- `EUTHERDRIVE_GAUNTDL_STOP_PC`
  - `tools/GauntletProbe` can stop after a frame if the CPU is at a requested PC.
- `EUTHERDRIVE_GAUNTDL_PROGRESS_INTERVAL`
  - lets long probe runs reduce progress spam.
- Release probe path is now the preferred bring-up path.

Preferred fast build:

```sh
dotnet build tools/GauntletProbe/GauntletProbe.csproj -c Release --no-restore /clp:ErrorsOnly
```

Preferred fast run:

```sh
env EUTHERDRIVE_GAUNTDL_SKIP_FRAME_RENDER=1 \
    EUTHERDRIVE_GAUNTDL_FIX_FD_SLOT_HANDLE=1 \
    EUTHERDRIVE_GAUNTDL_FIX_RD0_DMA_QIO_COMPLETE=1 \
    EUTHERDRIVE_GAUNTDL_FIX_RD0_ASYNC_CALLBACK=1 \
    EUTHERDRIVE_GAUNTDL_FIX_RD0_HOME_TABLE=1 \
    EUTHERDRIVE_GAUNTDL_FIX_RD0_STAGE4_BOOT_READ=1 \
    EUTHERDRIVE_GAUNTDL_PROGRESS_INTERVAL=250 \
    EUTHERDRIVE_GAUNTDL_RAW_DISK=/tmp/gauntd24.raw \
    dotnet run --project tools/GauntletProbe/GauntletProbe.csproj -c Release --no-build -- \
      /home/nichlas/roms/MAME/Midway/Vegas/gauntd 1000 200000
```

Observed speed:

- Debug/noisy runs were taking several minutes and could flood output.
- Release + render-skip reached frame 1000 in about 2.5 minutes on this machine.
- A frame 850 run completed in about 1m48s.

Also fixed the stage-4 boot-sector hook:

- The first attempt read LBA from the child QIO buffer at `0x80104008`, which stays zero for this stage.
- The selected boot-sector descriptor is the `/rd0` object buffer at `object+0x2c == 0x80104048`.
- The boot LBA is at descriptor offset `+0x20`, matching the earlier dump value `0x00197901`.
- The hook now reads that sector directly from the raw disk image and copies it into the descriptor buffer.

Current fast-run state:

```text
frame=1000
pc=0xffffffff80022f2c
lastOp=0x00000000
voodoo regs=14049 fifoWords=13323 fifoPackets=4448 drawPackets=0
framebuffer=640x480 nonBlack=147730 colored=17682
```

With 20,000 extra CPU steps after frame 1000:

```text
pc=0xffffffff80022f30
lastOp=0x03e00008
```

So the stage-4 wait condition is crossed far enough to reach the helper return sequence, but execution is still not cleanly back into the caller. The next target is likely the R5000 branch-delay/return path around `jr ra` at `0x80022f2c` and its delay slot `0x80022f30`, or a drain helper that recognizes this return pair correctly.

Do not enable full `EUTHERDRIVE_GAUNTDL_TRACE_RD0_HOME=1` for long runs unless needed. Candidate logging is now capped, but it still adds noise and cost.

## 2026-05-13 Late Pass: `/rd0` Boot Header Progress

The previous speed-pass note is partially stale. The stage-4 wait no longer stops at the helper return pair.

Additional fixes added:

- `TryKickKnownRd0AsyncCallback()` now preserves the original `ra` when it trampolines through callback `0x80029230` and restores it at `0x80022f18`.
- `EUTHERDRIVE_GAUNTDL_FIX_RD0_STAGE4_BOOT_READ=1` now reads the selected boot-sector LBA from descriptor offset `+0x24`, not `+0x20`.
- `EUTHERDRIVE_GAUNTDL_FIX_RD0_BOOT_HEADER_READ=1` fast-paths the boot-header read call at `0x80022fb0` when the parser returns to `0x80015ba4`.
- The boot-header fastpath also normalizes the local `c0edbabe` compare value for this parser path only. Do not globally change `lui` sign-extension in this adapter yet; doing so regressed BIOS bring-up back to `0x9fc02464`.

Current fast command:

```sh
dotnet build tools/GauntletProbe/GauntletProbe.csproj -c Release --no-restore /clp:ErrorsOnly

env EUTHERDRIVE_GAUNTDL_SKIP_FRAME_RENDER=1 \
    EUTHERDRIVE_GAUNTDL_FIX_FD_SLOT_HANDLE=1 \
    EUTHERDRIVE_GAUNTDL_FIX_RD0_DMA_QIO_COMPLETE=1 \
    EUTHERDRIVE_GAUNTDL_FIX_RD0_ASYNC_CALLBACK=1 \
    EUTHERDRIVE_GAUNTDL_FIX_RD0_HOME_TABLE=1 \
    EUTHERDRIVE_GAUNTDL_FIX_RD0_STAGE4_BOOT_READ=1 \
    EUTHERDRIVE_GAUNTDL_FIX_RD0_BOOT_HEADER_READ=1 \
    EUTHERDRIVE_GAUNTDL_PROGRESS_INTERVAL=500 \
    EUTHERDRIVE_GAUNTDL_RAW_DISK=/tmp/gauntd24.raw \
    dotnet run --project tools/GauntletProbe/GauntletProbe.csproj -c Release --no-build -- \
      /home/nichlas/roms/MAME/Midway/Vegas/gauntd 2000 200000
```

Verified trace milestones:

```text
[GAUNTDL:RD0] stage4-boot-read pc=ffffffff80022f18 lba=00197901 dest=ffffffff80104048 first=f00dface
[GAUNTDL:RD0] boot-header-read pc=ffffffff80022fb0 lba=00197901 dest=ffffffff807ffa68 first=f00dface
[GAUNTDL:RD0] boot-header-read pc=ffffffff80022fb0 lba=0032f201 dest=ffffffff807ffa68 first=f00dface
[GAUNTDL:RD0] boot-header-read pc=ffffffff80022fb0 lba=000000a6 dest=ffffffff807ffa68 first=c0edbabe
[GAUNTDL:RD0] stage4-boot-read pc=ffffffff80022f18 lba=000000a7 dest=ffffffff80104048 first=464c457f
```

The old blocker `Found no valid boot file headers:` is cleared. The current blocker is:

```text
pc=0xffffffff80015784
msg="Unable to read the boot file"
```

Interpretation:

- The parser now finds a valid `c0edbabe` boot-file header at LBA `0x000000a6`.
- The selected boot file starts at LBA `0x000000a7`; the first sector is ELF (`0x464c457f`).
- The current stage-4 completion only copies one sector into the old descriptor buffer, then reports success.
- The next fix should implement the actual boot-file read length/destination from the QIO/read arguments instead of treating this as another one-sector descriptor read.

Last verified fast run:

```text
frame=2000
pc=0xffffffff80015784
voodoo regs=14086 fifoWords=13371 fifoPackets=4464
framebuffer=640x480 nonBlack=147738 colored=17690
```

Build status:

```text
dotnet build tools/GauntletProbe/GauntletProbe.csproj -c Release --no-restore /clp:ErrorsOnly
Build succeeded.
331 Warning(s)
0 Error(s)
```

## 2026-05-13 Later Pass: Full `/rd0` Boot File Read

The previous blocker `Unable to read the boot file` is now cleared.

Additional fixes added:

- `EUTHERDRIVE_GAUNTDL_FIX_RD0_BOOT_FILE_READ=1` fast-paths the boot-file read call at `0x80022fb0` when the parser returns to `0x80015cbc`.
- `VegasMemoryMap.TryReadDiskBytesToMemory(...)` can copy a multi-sector raw disk range directly into main RAM.
- This keeps bring-up fast by avoiding the incomplete guest IDE/QIO path for the large boot ELF transfer.

Current fast command:

```sh
dotnet build tools/GauntletProbe/GauntletProbe.csproj -c Release --no-restore /clp:ErrorsOnly

env EUTHERDRIVE_GAUNTDL_SKIP_FRAME_RENDER=1 \
    EUTHERDRIVE_GAUNTDL_FIX_FD_SLOT_HANDLE=1 \
    EUTHERDRIVE_GAUNTDL_FIX_RD0_DMA_QIO_COMPLETE=1 \
    EUTHERDRIVE_GAUNTDL_FIX_RD0_ASYNC_CALLBACK=1 \
    EUTHERDRIVE_GAUNTDL_FIX_RD0_HOME_TABLE=1 \
    EUTHERDRIVE_GAUNTDL_FIX_RD0_STAGE4_BOOT_READ=1 \
    EUTHERDRIVE_GAUNTDL_FIX_RD0_BOOT_HEADER_READ=1 \
    EUTHERDRIVE_GAUNTDL_FIX_RD0_BOOT_FILE_READ=1 \
    EUTHERDRIVE_GAUNTDL_PROGRESS_INTERVAL=500 \
    EUTHERDRIVE_GAUNTDL_RAW_DISK=/tmp/gauntd24.raw \
    dotnet run --project tools/GauntletProbe/GauntletProbe.csproj -c Release --no-build -- \
      /home/nichlas/roms/MAME/Midway/Vegas/gauntd 2500 200000
```

Verified trace milestone:

```text
[GAUNTDL:RD0] boot-file-read pc=ffffffff80022fb0 lba=000000a7 dest=ffffffff802e73b0 bytes=00165e00 first=464c457f
```

The loaded ELF buffer starts at `0xffffffff802e73b0`:

```text
bytes[0xffffffff802e73b0]:
  +0x000: 7f 45 4c 46 01 01 01 00 ...
  +0x020: 5c 5d 16 00 00 00 00 20 34 00 20 00 01 00 28 00
```

The loader copies/decompresses enough to place Atari boot content at `0xffffffff80000000`:

```text
bytes[0xffffffff80000000]:
  +0x000: e9 44 00 08 00 00 00 00 ...
  +0x040: 00 43 6f 70 79 72 69 67 68 74 20 28 63 29 20 31
```

Current blocker:

```text
pc=0xffffffff800162ac
op=080058ab   # j 0x800162ac
msg="File is not bootable"
```

Useful narrow repro for the new blocker:

```sh
env EUTHERDRIVE_GAUNTDL_SKIP_FRAME_RENDER=1 \
    EUTHERDRIVE_GAUNTDL_FIX_FD_SLOT_HANDLE=1 \
    EUTHERDRIVE_GAUNTDL_FIX_RD0_DMA_QIO_COMPLETE=1 \
    EUTHERDRIVE_GAUNTDL_FIX_RD0_ASYNC_CALLBACK=1 \
    EUTHERDRIVE_GAUNTDL_FIX_RD0_HOME_TABLE=1 \
    EUTHERDRIVE_GAUNTDL_FIX_RD0_STAGE4_BOOT_READ=1 \
    EUTHERDRIVE_GAUNTDL_FIX_RD0_BOOT_HEADER_READ=1 \
    EUTHERDRIVE_GAUNTDL_FIX_RD0_BOOT_FILE_READ=1 \
    EUTHERDRIVE_GAUNTDL_STOP_PC=0xffffffff800162ac \
    EUTHERDRIVE_GAUNTDL_RAW_DISK=/tmp/gauntd24.raw \
    EUTHERDRIVE_GAUNTDL_DUMP_BYTES_RANGES=0xffffffff800897c0:160,0xffffffff802e73b0:128,0xffffffff80000000:128 \
    EUTHERDRIVE_GAUNTDL_DUMP_CODE_RANGES=0xffffffff80016260:32 \
    dotnet run --project tools/GauntletProbe/GauntletProbe.csproj -c Release --no-build -- \
      /home/nichlas/roms/MAME/Midway/Vegas/gauntd 370 200000
```

Last verified fast run:

```text
frame=2500
pc=0xffffffff800162ac
voodoo regs=14138 fifoWords=13439 fifoPackets=4487 lfbWrites=18808832 fastFills=287 swaps=574
framebuffer=640x480 nonBlack=147752 colored=17704
```

Build status:

```text
dotnet build tools/GauntletProbe/GauntletProbe.csproj -c Release --no-restore /clp:ErrorsOnly
Build succeeded.
332 Warning(s)
0 Error(s)
```

## 2026-05-13 Night Pass: Loaded Boot Code Progress

The previous blocker `File is not bootable` is now cleared.

Additional fixes added:

- `EUTHERDRIVE_GAUNTDL_FIX_BOOTABLE_ADDRESS_CHECK=1`
  - Fast-paths the loaded-ELF bootability address probe at `0x80016188` for the `/rd0` ELF buffer.
- `EUTHERDRIVE_GAUNTDL_FIX_BOOT_LOADER_ADDRESS_BASE=1`
  - Normalizes the loader's local `s4=0xa0000000` compare base to `0xffffffffa0000000`.
  - This is intentionally narrow. Do not globally sign-extend all `lui 0xa000` cases; that regressed BIOS bring-up.
- `EUTHERDRIVE_GAUNTDL_FIX_BOOT_SERIAL_COPY_LOOP=1`
  - Skips the known serial/FPGA byte-copy loop at `0x80012140`.
  - Also returns success from the follow-up serial handshake at `0x800121c0..0x80012218`.
- `EUTHERDRIVE_GAUNTDL_FIX_BOOT_COUNT_DELAY=1`
  - Fast-paths the loaded boot CP0 Count delay helper at `0x80010f40`, including KSEG1 aliases.
- The existing cache-loop fastpath now also covers the loaded boot cache helper around `0xa00cc294..0xa00cc328`.
- Boot trace spam for the serial loop and count-delay helper is capped.

Current fast command:

```sh
dotnet build tools/GauntletProbe/GauntletProbe.csproj -c Release --no-restore /clp:ErrorsOnly

env EUTHERDRIVE_GAUNTDL_SKIP_FRAME_RENDER=1 \
    EUTHERDRIVE_GAUNTDL_FIX_FD_SLOT_HANDLE=1 \
    EUTHERDRIVE_GAUNTDL_FIX_RD0_DMA_QIO_COMPLETE=1 \
    EUTHERDRIVE_GAUNTDL_FIX_RD0_ASYNC_CALLBACK=1 \
    EUTHERDRIVE_GAUNTDL_FIX_RD0_HOME_TABLE=1 \
    EUTHERDRIVE_GAUNTDL_FIX_RD0_STAGE4_BOOT_READ=1 \
    EUTHERDRIVE_GAUNTDL_FIX_RD0_BOOT_HEADER_READ=1 \
    EUTHERDRIVE_GAUNTDL_FIX_RD0_BOOT_FILE_READ=1 \
    EUTHERDRIVE_GAUNTDL_FIX_BOOTABLE_ADDRESS_CHECK=1 \
    EUTHERDRIVE_GAUNTDL_FIX_BOOT_LOADER_ADDRESS_BASE=1 \
    EUTHERDRIVE_GAUNTDL_FIX_BOOT_SERIAL_COPY_LOOP=1 \
    EUTHERDRIVE_GAUNTDL_FIX_BOOT_COUNT_DELAY=1 \
    EUTHERDRIVE_GAUNTDL_PROGRESS_INTERVAL=500 \
    EUTHERDRIVE_GAUNTDL_RAW_DISK=/tmp/gauntd24.raw \
    dotnet run --project tools/GauntletProbe/GauntletProbe.csproj -c Release --no-build -- \
      /home/nichlas/roms/MAME/Midway/Vegas/gauntd 2500 200000
```

Verified milestones:

```text
[GAUNTDL:BOOT] bootable-address-check pc=ffffffff80016188 addr=ffffffff802e73e4 result=1
[GAUNTDL:BOOT] boot-loader-address-base pc=ffffffff8001665c s4=ffffffffa0000000
[GAUNTDL:BOOT] boot-serial-copy-loop pc=ffffffff80012140 from=000000008013d9e8 to=0000000080145869 bytes=7e81
```

Last verified fast run:

```text
frame=2500
pc=0xffffffffa00ccac8
ra=0xffffffff80011868
cp0 status=0x34400000
voodoo regs=14049 fifoWords=13323 fifoPackets=4448
framebuffer=640x480 nonBlack=147730 colored=17682
```

Interpretation:

- The game is now executing loaded boot code beyond the old fatal paths.
- The current stop is no longer `/rd0`, `File is not bootable`, CP0 delay, or the first serial handoff.
- The next target is the loaded boot helper around `0xffffffffa00ccac8`, called from `0xffffffff80011868`.

Useful narrow repro for the next blocker:

```sh
env EUTHERDRIVE_GAUNTDL_SKIP_FRAME_RENDER=1 \
    EUTHERDRIVE_GAUNTDL_FIX_FD_SLOT_HANDLE=1 \
    EUTHERDRIVE_GAUNTDL_FIX_RD0_DMA_QIO_COMPLETE=1 \
    EUTHERDRIVE_GAUNTDL_FIX_RD0_ASYNC_CALLBACK=1 \
    EUTHERDRIVE_GAUNTDL_FIX_RD0_HOME_TABLE=1 \
    EUTHERDRIVE_GAUNTDL_FIX_RD0_STAGE4_BOOT_READ=1 \
    EUTHERDRIVE_GAUNTDL_FIX_RD0_BOOT_HEADER_READ=1 \
    EUTHERDRIVE_GAUNTDL_FIX_RD0_BOOT_FILE_READ=1 \
    EUTHERDRIVE_GAUNTDL_FIX_BOOTABLE_ADDRESS_CHECK=1 \
    EUTHERDRIVE_GAUNTDL_FIX_BOOT_LOADER_ADDRESS_BASE=1 \
    EUTHERDRIVE_GAUNTDL_FIX_BOOT_SERIAL_COPY_LOOP=1 \
    EUTHERDRIVE_GAUNTDL_FIX_BOOT_COUNT_DELAY=1 \
    EUTHERDRIVE_GAUNTDL_STOP_PC=0xffffffffa00ccac8 \
    EUTHERDRIVE_GAUNTDL_RAW_DISK=/tmp/gauntd24.raw \
    EUTHERDRIVE_GAUNTDL_DUMP_GPRS=1 \
    EUTHERDRIVE_GAUNTDL_DUMP_CODE_RANGES=0xffffffffa00cca80:128,0xffffffff80011820:128 \
    dotnet run --project tools/GauntletProbe/GauntletProbe.csproj -c Release --no-build -- \
      /home/nichlas/roms/MAME/Midway/Vegas/gauntd 650 200000
```

## 2026-05-13 Late Pass: Glide Init Reached, Still No Draw Packets

This pass moves `gauntdl24` past the loaded-code serial/vector setup, runtime
timer waits, `grSstQueryHardware`, and the first `grSstWinOpen` failure path.

Use the real ROM archive from the UI/probe path:

```text
/home/nichlas/roms/MAME/Midway/Vegas/gauntd/gauntdl24.7z
```

Raw disk sidecar expected by this bring-up:

```text
/home/nichlas/roms/MAME/Midway/Vegas/gauntd/gauntd24.raw
```

New implementation work in `EutherDrive.Core/Arcade/Vegas/GauntletDarkLegacyAdapter.cs`:

- Added a loaded boot vector setup-loop fastpath at `0x80011830`.
- Added NILE timer IRQ state generation for active NILE timers.
- Added runtime delay/callback fastpaths for `0x800d03b8` and
  `0x800e1420`.
- Added a runtime tick wait fastpath for the `0x800e0be4..0x800e0c18`
  loop over counter `0x80228114`.
- Added a command-completion wait fastpath for the `0x800d78c0` command
  `0x8a` loop.
- Extended `grSstQueryHardware` fastpath to the currently loaded query
  routine at `0x80108e84`.
- Added a narrow skip for the `main: grSstWinOpen failed!` panic call from
  `0x800e1b70`.
- Extended FIFO make-room/state normalization to the relocated Glide state at
  `0x80262d64` and make-room routine at `0x801097c0`.

Current verified command:

```sh
dotnet build tools/GauntletProbe/GauntletProbe.csproj -c Release --no-restore /clp:ErrorsOnly

env EUTHERDRIVE_GAUNTDL_BRINGUP_FAST=1 \
    EUTHERDRIVE_GAUNTDL_PROGRESS_INTERVAL=1000 \
    EUTHERDRIVE_GAUNTDL_DUMP_FRAME=/tmp/gauntdl_2200.ppm \
    dotnet run --project tools/GauntletProbe/GauntletProbe.csproj -c Release --no-build -- \
      /home/nichlas/roms/MAME/Midway/Vegas/gauntd/gauntdl24.7z 2200 200000
```

Build status:

```text
Build succeeded.
331 Warning(s)
0 Error(s)
```

Latest probe endpoint:

```text
frame=2200
pc=0xffffffff80120164
voodoo regs=343080 fifoWords=605572 fifoPackets=300572
drawPackets=0 directTriangles=0 setupTriangles=0
fastFills=283 swaps=66374
packetTypes=0:0,1:299177,2:0,3:0,4:1395,5:0,6:0,7:0
framebuffer=640x480 nonBlack=152480 colored=21408
frameDump=/tmp/gauntdl_2200.ppm
```

Image status:

- The UI/probe still shows diagnostic bars, not real Gauntlet graphics.
- The big progress is that Glide init now runs much deeper and Voodoo traffic
  jumps from roughly `14k` register writes to roughly `343k`.
- Still missing: triangle/setup packet production or correct decoding of the
  guest's render packet stream. `drawPackets` remains `0`.

Next recommended target:

1. Inspect the hot path around `0xffffffff8011f6e4`,
   `0xffffffff80120164`, and `0xffffffff801209f8`.
2. Determine whether the guest is still in front-end/font/LFB code or whether
   packet type `1` register traffic should be interpreted into setup/triangle
   state by the Voodoo parser.
3. Keep probes headless with `EUTHERDRIVE_GAUNTDL_SKIP_FRAME_RENDER=1` while
   debugging; dump a PPM only after packet stats move.

## 2026-05-14 Pass: Relocated Select + Glide Log Sink

Committed the previous bring-up state as:

```text
e052ace Advance Gauntlet Glide bringup
```

Additional work after that commit:

- Extended the `grSstSelect` fastpath for the relocated loaded routine at
  `0xffffffff8010a528`.
- Added a narrow Glide log/output callback sink at `0xffffffff8011ce40`.
- Kept the ROM path real/UI-loadable:
  `/home/nichlas/roms/MAME/Midway/Vegas/gauntd/gauntdl24.7z`.
- Kept the raw CHD sidecar:
  `/home/nichlas/roms/MAME/Midway/Vegas/gauntd/gauntd24.raw`.

Build status:

```text
dotnet build tools/GauntletProbe/GauntletProbe.csproj -c Release --no-restore /clp:ErrorsOnly
Build succeeded.
332 Warning(s)
0 Error(s)
```

Note: a later full build in the current dirty worktree is blocked by unrelated
DataEast Boogwing work:
`EutherDrive.Core/Arcade/DataEast/Boogwing/BoogwingAdapter.cs(831,12): error CS0103: The name 'Bitswap32' does not exist in the current context`.

Latest 5000-frame probe endpoint:

```text
frame=5000
pc=0xffffffff8011d6a8
lastOp=0x30620001
voodoo regs=951380 fifoWords=1700512 fifoPackets=848042
drawPackets=0 directTriangles=0 setupTriangles=0
fastFills=283 swaps=188034
packetTypes=0:0,1:846647,2:0,3:0,4:1395,5:0,6:0,7:0
framebuffer=640x480 stride=2560 nonBlack=151456 colored=21408
frameDump=/tmp/gauntdl_5000.ppm
```

Image status:

- Still diagnostic bars only, not real Gauntlet graphics.
- The log sink moved execution from the earlier formatter branch at
  `0xffffffff80120164` to `0xffffffff8011d6a8` and increased Voodoo traffic,
  but `drawPackets` is still `0`.
- FIFO traffic is still almost entirely type `1` register packets plus type
  `4`; no type `3` or type `5` triangle/setup stream is appearing yet.

Current next target:

1. Inspect `0xffffffff8011d6a8` and its caller/return context from
   `0xffffffff8011ce80`; this still looks like log/stdio/GD error machinery.
2. Determine whether the stale stack text
   `gd error (glide): grSstSelect: non-existent SST` is still being emitted
   through another path or only left in memory.
3. Do not spend the next pass on the FIFO parser until the guest produces
   non-clear/non-swap render packets; current stats say it is not there yet.

## 2026-05-14 Follow-up: Active Glide Error Reporter

After `eaeb6f1`, the stack text was confirmed active, not stale. At 1200
frames the CPU is in the formatter path:

```text
frame=1200
pc=0xffffffff801202e0
ra=0xffffffff80120848
a0=0xffffffff80159228
a1=0xffffffff807ff7f1
s0=0xffffffff80158474
s1=0x5
```

Stack bytes at `0xffffffff807ff9c0` contain:

```text
gd error (glide): grSstSelect:  non-existent SST
```

A narrow CPU trace over `0xffffffff8010a520..0xffffffff8010a6c0` showed the
hot path is not the `grSstSelect` entry fastpath. It repeatedly enters the
loaded Glide error reporter at `0xffffffff8010a640` with `a1=1`; observed
callers include `0xffffffff80115238` and `0xffffffff80109028`.

New follow-up code in progress:

- `TryFastPathKnownGlideSelect` no longer rejects nonzero SST indices before
  normalizing the selected board state.
- Added `TryFastPathKnownGlideErrorReport` for the exact function signature at
  `0xffffffff8010a640`; when the reporter is active (`a1 != 0`) it returns to
  `ra` instead of spending frames in the formatter/log path.

Verification note:

- `git diff --check` passes for the Gauntlet adapter patch.
- A full `dotnet build tools/GauntletProbe/GauntletProbe.csproj -c Release`
  is currently blocked by unrelated worktree compile errors outside Gauntlet
  (`BoogwingBus.SetInput2` and, with ad-hoc excludes, wider project duplicate
  assembly attribute errors). Re-run the 1200-frame probe after those unrelated
  build blockers are gone.

## 2026-05-14 Follow-up: Loaded Glide State Helpers

Committed previous state before this pass:

```text
0c8b403 Skip Gauntlet Glide error reporter
```

This pass focused on speeding up the loaded Glide state/register spam after the
active `grSstSelect` error reporter was skipped.

New code:

- Added `TryFastPathKnownGauntletGlideTwoWordStatePacket` for the loaded
  two-word state packet helper around `0xffffffff8010251c`.
  - Runtime trace showed the actual hot PCs are `0xffffffff80102520` after the
    stack adjust and `0xffffffff8010253c` after the prologue.
  - The fastpath is constrained to the exact function signature and Gauntlet's
    loaded Glide state pointer `0xffffffff80262d64`.
  - It normalizes the loaded FIFO state, writes the same `0x00010219` type-1
    register packet, advances FIFO room/pointer state, restores the stack
    frame when entered after the prologue, and returns to `ra`.
- Extended `TryFastPathKnownGlideSetupPacketHelper` to also match the loaded
  helper at `0xffffffff80103f70`.
  - This is the relocated equivalent of the existing `0xffffffff80052bc0`
    helper, but it loads state from `0xffffffff80262c8c` instead of
    `0xffffffff800b4d2c`.

Build status:

```text
dotnet build tools/GauntletProbe/GauntletProbe.csproj -c Release --no-restore /clp:ErrorsOnly
Build succeeded.
450 Warning(s)
0 Error(s)
```

Verification:

```text
env EUTHERDRIVE_GAUNTDL_BRINGUP_FAST=1 \
    EUTHERDRIVE_GAUNTDL_PROGRESS_INTERVAL=100 \
    EUTHERDRIVE_GAUNTDL_RAW_DISK=/home/nichlas/roms/MAME/Midway/Vegas/gauntd/gauntd24.raw \
    EUTHERDRIVE_GAUNTDL_DUMP_GPRS=1 \
    dotnet run --project tools/GauntletProbe/GauntletProbe.csproj -c Release --no-build -- \
      /home/nichlas/roms/MAME/Midway/Vegas/gauntd/gauntdl24.7z 300 2000000
```

Current endpoint:

```text
frame=300
pc=0xffffffff800e0d08
lastOp=0x8c620018
ra=0xffffffff800e13c8
sp=0xffffffff807ffdf0
voodoo regs=2265577 fifoWords=4066068 fifoPackets=2030820
drawPackets=0 directTriangles=0 setupTriangles=0
lfbWrites=18546688 texWrites=1 fastFills=283 swaps=450872
packetTypes=0:0,1:2029425,2:0,3:0,4:1395,5:0,6:0,7:0
framebuffer=640x480 stride=2560 nonBlack=151456 colored=21408
```

Interpretation:

- This moves the 300-frame high-budget endpoint from the loaded Glide
  registersetter at `0xffffffff8010253c` to runtime code at
  `0xffffffff800e0d08`.
- The visible framebuffer is still the diagnostic/clear-bars image; no real
  Gauntlet geometry is being emitted yet.
- Voodoo traffic is still state/clear/swap dominated. `drawPackets`,
  `directTriangles`, and `setupTriangles` remain `0`.

New hot-code dump around the endpoint:

```text
mem[0xffffffff800e0cd0]:
  +0x020: 00000000 27bdffe8 3c028022 2443af10
  +0x030: afbf0010 8c620018 04400006 00000000
  +0x040: 8c620020 24420001 ac620020 0c040a81
  +0x050: 8c640018 8fbf0010 03e00008 27bd0018
```

Next recommended target:

1. Inspect `0xffffffff800e0d08` and caller `0xffffffff800e13c8`; the endpoint
   looks like a small runtime counter/dispatch helper rather than Voodoo draw.
2. Keep chasing the first non-state render producer. Do not spend more time on
   packet type 2 parsing until the guest emits setup/triangle-looking traffic.
3. A useful next probe is a CPU trace around
   `0xffffffff800e0cd0..0xffffffff800e0d60` plus stack dump at
   `0xffffffff807ffdd0`.

## 2026-05-14 Follow-up: Runtime Frame-State Callback

This pass added one verified fastpath after
`5bc2a38 Fast path loaded Gauntlet Glide state helpers`.

New code:

- Added `TryFastPathKnownRuntimeFrameStateCallback` for the wrapper at
  `0xffffffff800e0cf4`.
  - The wrapper reads status from `0xffffffff8021af28`, increments
    `0xffffffff8021af30`, and calls `0xffffffff80102a04`.
  - `0xffffffff80102a04` was dumped and still emits Glide type-1 state packet
    traffic, including `0x00030251`; it is not producing triangle/setup
    packets.
  - The fastpath matches both function entry `0xffffffff800e0cf4` and the
    post-status-load budget endpoint `0xffffffff800e0d08`.

Clean verification was done in `/tmp/eutherdrive-gauntlet-verify` because the
main worktree build is currently blocked by unrelated dirty Boogwing errors.

Clean build:

```text
dotnet build tools/GauntletProbe/GauntletProbe.csproj -c Release --no-restore /clp:ErrorsOnly
Build succeeded.
332 Warning(s)
0 Error(s)
```

Probe:

```text
env EUTHERDRIVE_GAUNTDL_BRINGUP_FAST=1 \
    EUTHERDRIVE_GAUNTDL_PROGRESS_INTERVAL=100 \
    EUTHERDRIVE_GAUNTDL_RAW_DISK=/home/nichlas/roms/MAME/Midway/Vegas/gauntd/gauntd24.raw \
    EUTHERDRIVE_GAUNTDL_DUMP_GPRS=1 \
    dotnet run --project tools/GauntletProbe/GauntletProbe.csproj -c Release --no-build -- \
      /home/nichlas/roms/MAME/Midway/Vegas/gauntd/gauntdl24.7z 300 2000000
```

New endpoint:

```text
frame=300
pc=0xffffffff80104068
lastOp=0x30e20002
ra=0xffffffff801090bc
sp=0xffffffff807ffdc0
voodoo regs=1904456 fifoWords=3794132 fifoPackets=1894852
drawPackets=0 directTriangles=0 setupTriangles=0
lfbWrites=18546688 texWrites=1 fastFills=283 swaps=566
packetTypes=0:0,1:1893457,2:0,3:0,4:1395,5:0,6:0,7:0
framebuffer=640x480 stride=2560 nonBlack=151456 colored=21408
```

Interpretation:

- Endpoint moved from `0xffffffff800e0d08` to `0xffffffff80104068`.
- Swap spam dropped sharply from `450872` to `566`.
- The framebuffer is still diagnostic/clear-bars only.
- No real render packets yet: `drawPackets`, `directTriangles`, and
  `setupTriangles` remain `0`.

Attempted but not kept:

- A speculative fastpath for the larger loaded state emitter around
  `0xffffffff80103fc8` / `0xffffffff80104068` built cleanly but did not move
  the endpoint or stats, so it was removed before commit.

Next target:

1. Continue from `0xffffffff80104068` inside the loaded Glide state-emitter
   body.
2. Dump/trace around `0xffffffff80103fc8..0xffffffff80104140` with stack
   around `0xffffffff807ffdc0`.
3. Keep treating type-1-only traffic as bring-up/state noise until the guest
   emits setup/triangle packet types.

## 2026-05-14 Follow-up: Loaded State Emitter

This pass added a verified fastpath for the loaded Glide state-emitter body.

New code:

- Added `TryFastPathKnownGauntletGlideStateEmit`.
  - The correct function entry is `0xffffffff80103fcc`; the earlier attempted
    `0xffffffff80103fc8` address was the preceding delay-slot/nop.
  - The endpoint inside the mask body is `0xffffffff80104068`.
  - The function emits more loaded Glide state packets from
    `0xffffffff80262d64`; it is still type-1 state traffic, not geometry.
  - The fastpath normalizes the loaded FIFO state and returns to the caller
    for both entry and mask-body budget stops.

Clean verification was done in `/tmp/eutherdrive-gauntlet-verify` because the
main worktree still has unrelated dirty build blockers.

Clean build:

```text
dotnet build tools/GauntletProbe/GauntletProbe.csproj -c Release --no-restore /clp:ErrorsOnly
Build succeeded.
332 Warning(s)
0 Error(s)
```

Probe:

```text
env EUTHERDRIVE_GAUNTDL_BRINGUP_FAST=1 \
    EUTHERDRIVE_GAUNTDL_PROGRESS_INTERVAL=100 \
    EUTHERDRIVE_GAUNTDL_RAW_DISK=/home/nichlas/roms/MAME/Midway/Vegas/gauntd/gauntd24.raw \
    EUTHERDRIVE_GAUNTDL_DUMP_GPRS=1 \
    dotnet run --project tools/GauntletProbe/GauntletProbe.csproj -c Release --no-build -- \
      /home/nichlas/roms/MAME/Midway/Vegas/gauntd/gauntdl24.7z 300 2000000
```

New endpoint:

```text
frame=300
pc=0xffffffff800eb020
lastOp=0xacc30004
ra=0xffffffff800eb768
sp=0xffffffff807ffda8
a2=0x0000000080262bc8
voodoo regs=2382893 fifoWords=4743572 fifoPackets=2369572
drawPackets=0 directTriangles=0 setupTriangles=0
lfbWrites=18546688 texWrites=1 fastFills=283 swaps=566
packetTypes=0:0,1:2368177,2:0,3:0,4:1395,5:0,6:0,7:0
framebuffer=640x480 stride=2560 nonBlack=151456 colored=21408
```

Interpretation:

- Endpoint moved from `0xffffffff80104068` to `0xffffffff800eb020`.
- This is forward progress through another loaded state emitter, but Voodoo
  traffic is still only type-1 state plus type-4 clear/fill.
- The framebuffer is still diagnostic/clear-bars only.

Attempted but not kept:

- A fastpath for the bitfield/update helper around `0xffffffff800eafdc` was
  tried, including corrected trace-derived entry/signature. It still did not
  move the endpoint or stats, so it was removed before commit.

Next target:

1. Continue at `0xffffffff800eb020`; trace showed the helper entry is
   `0xffffffff800eafdc`.
2. Current record pointer at the endpoint is `0x0000000080262bc8`.
3. If retrying that helper, derive the exact branch/body semantics from the
   trace rather than the byte dump; the byte-offset alignment was easy to get
   wrong.

## 2026-05-14 Follow-up: Faster Warmup Iteration and State-init Tail

This pass switched Gauntlet probing to the warmup-snapshot loop for faster
bringup/debug iteration, then added a small fastpath for the loaded Glide
runtime state-init tail.

Fast iteration command:

```text
env EUTHERDRIVE_GAUNTDL_BRINGUP_FAST=1 \
    EUTHERDRIVE_GAUNTDL_WARMUP_STATE=/tmp/eutherdrive-gauntlet-probe/gauntdl-gauntdl24-f300-s2000000-bc88fcdd60ae.warm \
    EUTHERDRIVE_GAUNTDL_EXTRA_SERIES=1000000,2000000,5000000,10000000 \
    EUTHERDRIVE_GAUNTDL_RAW_DISK=/home/nichlas/roms/MAME/Midway/Vegas/gauntd/gauntd24.raw \
    dotnet run --project tools/GauntletProbe/GauntletProbe.csproj -c Release --no-build -- \
      /home/nichlas/roms/MAME/Midway/Vegas/gauntd/gauntdl24.7z 300 2000000
```

Warmup snapshot in use:

```text
/tmp/eutherdrive-gauntlet-probe/gauntdl-gauntdl24-f300-s2000000-bc88fcdd60ae.warm
```

New code:

- Added `TryFastPathKnownGauntletGlideRuntimeStateInitTail`.
  - It catches the loaded runtime routine at `0xffffffff80109074`, after the
    stack/local state pointer has already been written.
  - It writes the computed `0xffffffff80262c90` value, normalizes the loaded
    Glide FIFO state at `0xffffffff80262d64`, restores the stack frame, and
    returns to the caller.
  - The guard is intentionally anchored to the exact tail bytes from
    `0xffffffff80109074` onward; earlier attempts used offsets that were too
    broad and did not fire.

Clean verification in `/tmp/eutherdrive-gauntlet-verify`:

```text
dotnet build tools/GauntletProbe/GauntletProbe.csproj -c Release --no-restore /clp:ErrorsOnly
Build succeeded.
332 Warning(s)
0 Error(s)
```

Before this fastpath, the warmup run stopped at the state-init tail:

```text
checkpoint extra=1000000 pc=0xffffffff80109074 regs=3102268 fifoWords=6182322 fifoPackets=3088947
drawPackets=0 directTriangles=0 setupTriangles=0
packetTypes=0:0,1:3087552,2:0,3:0,4:1395,5:0,6:0,7:0
```

After the fastpath:

```text
checkpoint extra=1000000 pc=0xffffffff8010378c regs=3102547 fifoWords=6182880 fifoPackets=3089226
checkpoint extra=2000000 pc=0xffffffff800eb764 regs=3109501 fifoWords=6196788 fifoPackets=3096180
checkpoint extra=5000000 pc=0xffffffff800e2c0c regs=3130373 fifoWords=6238532 fifoPackets=3117052
checkpoint extra=10000000 pc=0xffffffff800e0cf4 regs=3165156 fifoWords=6308098 fifoPackets=3151835
drawPackets=0 directTriangles=0 setupTriangles=0
packetTypes=0:0,1:3150440,2:0,3:0,4:1395,5:0,6:0,7:0
```

Interpretation:

- The new fastpath is valid and moves the 1M-extra endpoint past the repeated
  `0xffffffff80109074` state-init tail.
- We are still only seeing Voodoo type-1 state traffic plus type-4 clear/fill.
  No setup/triangle packets yet, so this is still not real game graphics.
- The fastest next workflow is to keep the warmup snapshot and run focused
  `EUTHERDRIVE_GAUNTDL_EXTRA_SERIES` probes instead of repeating cold frame
  bringup.

Next target:

1. Investigate the repeated loaded-runtime path around `0xffffffff8010378c`,
   `0xffffffff800eb764`, and `0xffffffff800e2c0c`.
2. Keep looking for the first transition from type-1 state packets to Voodoo
   setup/triangle packets; that is the next meaningful "real graphics" gate.

## 2026-05-14 Follow-up: Runtime Two-word State Update

This pass added one more verified fastpath in the loaded Glide runtime state
path.

New code:

- Added `TryFastPathKnownGauntletGlideRuntimeTwoWordStateUpdate`.
  - It catches the repeated leaf at `0xffffffff801036a0`.
  - The routine updates loaded state word `0xffffffff80262d64+0x264`,
    writes type-1 packet `0x00010211`, then flushes the loaded FIFO.
  - The first guard attempt used packet-tail offsets that were 0x24 bytes too
    early; the kept version is anchored to the actual `0xffffffff8010374c`
    packet write sequence.

Clean verification in `/tmp/eutherdrive-gauntlet-verify`:

```text
dotnet build tools/GauntletProbe/GauntletProbe.csproj -c Release --no-restore /clp:ErrorsOnly
Build succeeded.
332 Warning(s)
0 Error(s)
```

Warmup-series before this fastpath:

```text
checkpoint extra=1000000 pc=0xffffffff8010378c regs=3102547 fifoWords=6182880 fifoPackets=3089226
checkpoint extra=10000000 pc=0xffffffff800e0cf4 regs=3165156 fifoWords=6308098 fifoPackets=3151835
drawPackets=0 directTriangles=0 setupTriangles=0
```

Warmup-series after this fastpath:

```text
checkpoint extra=1000000 pc=0xffffffff800eb640 regs=3103093 fifoWords=6183972 fifoPackets=3089772
checkpoint extra=2000000 pc=0xffffffff800eb76c regs=3110597 fifoWords=6198980 fifoPackets=3097276
checkpoint extra=5000000 pc=0xffffffff8010331c regs=3133112 fifoWords=6244010 fifoPackets=3119791
checkpoint extra=10000000 pc=0xffffffff800ce5f0 regs=3170637 fifoWords=6319060 fifoPackets=3157316
drawPackets=0 directTriangles=0 setupTriangles=0
packetTypes=0:0,1:3155921,2:0,3:0,4:1395,5:0,6:0,7:0
```

Longer warmup-series after this fastpath:

```text
checkpoint extra=25000000 pc=0xffffffff800eb768 regs=3283205 fifoWords=6544196 fifoPackets=3269884
checkpoint extra=50000000 pc=0xffffffff801021b0 regs=3470821 fifoWords=6919428 fifoPackets=3457500
checkpoint extra=100000000 pc=0xffffffff800e13e0 regs=3846060 fifoWords=7669906 fifoPackets=3832739
drawPackets=0 directTriangles=0 setupTriangles=0
packetTypes=0:0,1:3831344,2:0,3:0,4:1395,5:0,6:0,7:0
```

Interpretation:

- The fastpath is effective: the 1M endpoint moves from the return at
  `0xffffffff8010378c` to `0xffffffff800eb640`, and longer budgets move past
  the previous `0xffffffff800e0cf4` endpoint.
- Even after 100M extra steps from the 300-frame warmup snapshot, Voodoo still
  only sees type-1 state packets plus type-4 clear/fill. No geometry yet.
- `0xffffffff800ce5f0` is a small runtime callback wrapper, not a direct Voodoo
  state-packet writer. Do not skip it blindly; trace its callee/effect first if
  it remains hot.

Next target:

1. Trace around `0xffffffff800e13e0` and the surrounding caller path. That is
   the 100M endpoint after the latest fastpath.
2. If `0xffffffff800ce5f0` remains hot, trace its call to the runtime helper
   before adding a wrapper fastpath.
3. Keep using `EUTHERDRIVE_GAUNTDL_WARMUP_STATE=/tmp/eutherdrive-gauntlet-probe/gauntdl-gauntdl24-f300-s2000000-bc88fcdd60ae.warm`
   and focused `EUTHERDRIVE_GAUNTDL_EXTRA_SERIES`; cold 300-frame probes are no
   longer the fastest workflow.
