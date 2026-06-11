# Gauntlet Dark Legacy Vegas Handoff

Date: 2026-05-06

Update: 2026-05-07

Update: 2026-05-09

Update: 2026-05-15

Update: 2026-05-25

Update: 2026-05-31

Update: 2026-06-05

Update: 2026-06-10

Update: 2026-06-11

## Scope

This pass continued the Gauntlet Dark Legacy / Midway Vegas bring-up in `EutherDrive.Core/Arcade/Vegas/GauntletDarkLegacyAdapter.cs`.

The working strategy is still:

- Build a Gauntlet-compatible Vegas machine adapter, not a full MAME port.
- Use MAME `vegas.cpp` as the hardware map and expected behavior reference.
- Fast-path expensive BIOS loops where they are deterministic and only affect bring-up speed.
- Keep risky hardware guesses uncommitted unless they are proven by probe output.

## 2026-06-11 Checkpoint Probe: Indexed Header Default Regression

`tools/GauntletProbe/Program.cs` now supports compact per-frame checkpoints:

```text
EUTHERDRIVE_GAUNTDL_FRAME_CHECKPOINTS=190,200,210,220
```

Each checkpoint prints frame, PC, framebuffer hash, draw/triangle counts,
LFB/texture/fill/swap counters, and FIFO packet-type counts. This is intended
for comparing BGLoadModel/Voodoo divergences without enabling the full FIFO
trace.

The important result from the castle f180 warm snapshot is that broad indexed
source header repair is not safe as a `BRINGUP_FAST` default. With
`EUTHERDRIVE_GAUNTDL_FIX_RUNTIME_BGLOADMODEL_INDEXED_SOURCE_HEADERS=0`, the
420-frame baseline is:

```text
frame=420 pc=800b1b38 frameHash=0x4f22cd71
drawPackets=11250 directTriangles=1361 setupTriangles=656
texWrites=8659569 lfbWrites=216898724
framebuffer nonBlack=307200 colored=305702
packetTypes=0:3842,1:42543,2:0,3:11250,4:124525,5:139698,6:0,7:3
```

With the broad indexed-header repair implicitly enabled by `BRINGUP_FAST`, the
same run regresses coverage and changes the render signature:

```text
frame=420 pc=80120264 frameHash=0xc155c141
drawPackets=10846 directTriangles=1839 setupTriangles=893
texWrites=10835075 lfbWrites=281953990
framebuffer nonBlack=281698 colored=279140
packetTypes=0:6210,1:42101,2:30,3:10846,4:122398,5:169312,6:12,7:23
```

The repair was changed back to explicit opt-in:

```text
EUTHERDRIVE_GAUNTDL_FIX_RUNTIME_BGLOADMODEL_INDEXED_SOURCE_HEADERS=1
```

The narrower slot-2 early-header probe (`mask=0x4`) is useful but still
diverges from baseline. It keeps final coverage high but changes the hash and
introduces type-2/type-6/type-7 FIFO packets early:

```text
mask=0x4 frame=220:
pc=800b0c64 frameHash=0x94623c72
drawPackets=3507 directTriangles=313 setupTriangles=141
texWrites=2751573 lfbWrites=117026084
packetTypes=0:2689,1:27736,2:24,3:3507,4:83705,5:44906,6:14,7:15

baseline frame=220:
pc=800b1ff0 frameHash=0x8004b9d5
drawPackets=3616 directTriangles=503 setupTriangles=235
texWrites=2653525 lfbWrites=105873089
packetTypes=0:1152,1:29185,2:0,3:3616,4:84128,5:43349,6:0,7:3
```

The divergence starts before frame 220, after the alternative indexed QIO
sequence. At frame 190 the slot-2 run already has packet types 2/6/7 and much
lower triangle counts:

```text
baseline f190: hash=0x18c7a740 direct/setup=283/125 packetTypes 2/6/7=0/0/3
mask=0x4 f190: hash=0x07f3bc2f direct/setup=93/31 packetTypes 2/6/7=24/14/15
```

`EUTHERDRIVE_GAUNTDL_TRACE_VOODOO_ODD_FIFO=1` mapped those extra packet classes
back to the Glide FIFO outer payload fastpath at `0xffffffff800fe5d4`, not to a
new CPU renderer. The suspicious commands were payload words being consumed as
packet headers around synthetic type-5 texture uploads (`0xc0000205`). Added:

```text
EUTHERDRIVE_GAUNTDL_TRACE_VERTEX_FIFO_FASTPATH=1
EUTHERDRIVE_GAUNTDL_TRACE_VOODOO_ODD_FIFO=1
```

The producer trace confirms the fastpath emits type-5 packets with
`payloadWords=64` and arbitrary payload words whose low bits can look like FIFO
types 2/6/7. A default decode bulk section now brackets the fastpath writes so
the command FIFO decoder only runs after the synthetic batch is complete. This
keeps the known baseline unchanged:

```text
baseline f220 after bulk bracket:
pc=800b1ff0 frameHash=0x8004b9d5
drawPackets=3418 direct/setup=503/235
packetTypes=0:1638,1:28960,2:0,3:3418,4:83372,5:39562,6:0,7:3
framebuffer nonBlack=13628 colored=11630
```

There is also an opt-in diagnostic reset for stale command FIFO valid slots:

```text
EUTHERDRIVE_GAUNTDL_EXPERIMENT_VOODOO_FIFO_BULK_RESET=1
EUTHERDRIVE_GAUNTDL_EXPERIMENT_VOODOO_FIFO_BULK_REWIND=1
EUTHERDRIVE_GAUNTDL_EXPERIMENT_VOODOO_FIFO_BULK_DECODE_WINDOW=1
```

With slot-2 early headers (`mask=0x4`) this reduced f220 odd packet counts from
`2/6/7=24/14/15` to `6/0/5` and cleared the `reg[0c0]=0xc0000205` style TMU
leak, but it also changes baseline rendering if promoted. Keep it as a probe
only.

Follow-up probes:

```text
mask=0x4 + FIFO_BULK_REWIND f220:
frameHash=0x37636bf4
drawPackets=2191 direct/setup=312/141
packetTypes=2/6/7=8/0/9

mask=0x4 + FIFO_BULK_DECODE_WINDOW f220:
frameHash=0xb99c9697
drawPackets=3152 direct/setup=312/141
packetTypes=2/6/7=0/0/4

baseline + FIFO_BULK_DECODE_WINDOW f220:
frameHash=0xf48be87e
drawPackets=3154 direct/setup=368/168
packetTypes=2/6/7=0/0/3

baseline default sanity f220:
frameHash=0x8004b9d5
drawPackets=3418 direct/setup=503/235
packetTypes=2/6/7=0/0/3
```

Added an opt-in closer MAME-style command FIFO model:

```text
EUTHERDRIVE_GAUNTDL_FIX_VOODOO_MAME_CMD_FIFO_MODEL=1
EUTHERDRIVE_GAUNTDL_TRACE_VOODOO_CMD_FIFO_MODEL=1
```

This tracks `cmdFifoBaseAddr`, `cmdFifoAddressMin`, `cmdFifoAddressMax`,
`cmdFifoDepth`, and `cmdFifoHoles` instead of using only the local `valid[]`
ring. The warm f180 snapshot does not restore meaningful `address_min/max`
state; the first observed FIFO write can start at `0x13ecc`, so the model has a
warm-state guard that seeds min/max from the first post-restore write rather
than creating a huge artificial hole range. The old `storageIndex == 0`
wrap-clear heuristic is also gated off while this MAME model is active.

Current f220 probe results:

```text
baseline + MAME_CMD_FIFO_MODEL:
frameHash=0x8004b9d5
drawPackets=550 direct/setup=368/168
packetTypes=0:1645,1:25702,2:0,3:550,4:72412,5:6161,6:0,7:3
framebuffer nonBlack=13628 colored=11630

mask=0x4 + MAME_CMD_FIFO_MODEL:
frameHash=0x07a00a21
drawPackets=206 direct/setup=312/141
packetTypes=0:1623,1:24000,2:0,3:206,4:71135,5:5935,6:0,7:3
framebuffer nonBlack=105085 colored=101438
```

Further MAME-model probes:

```text
baseline + MAME_CMD_FIFO_MODEL after unmasked read-index experiment:
frameHash=0x8004b9d5
drawPackets=550 direct/setup=368/168
packetTypes=0:1645,1:25702,2:0,3:550,4:72412,5:6161,6:0,7:3

cold start to f220 + MAME_CMD_FIFO_MODEL:
pc=0xffffffff80108048
frameHash=0x30e41dc5
drawPackets=19249 direct/setup=33/0
packetTypes=0:53730,1:22825,2:6,3:19249,4:68388,5:4490,6:8,7:1
framebuffer nonBlack=0 colored=0

cold start to f220 + MAME_CMD_FIFO_MODEL after type5-space0 local-RAM fix:
pc=0xffffffff80108048
frameHash=0x30e41dc5
drawPackets=19238 direct/setup=33/0
packetTypes=0:53732,1:22810,2:6,3:19238,4:68338,5:4492,6:9,7:1
framebuffer nonBlack=0 colored=0

baseline + MAME_CMD_FIFO_MODEL + MAME_CMD_FIFO_YIELD_ON_WORK:
frameHash=0x8004b9d5
drawPackets=506 direct/setup=368/168
packetTypes=0:1645,1:25652,2:0,3:506,4:72244,5:5620,6:0,7:3

baseline + MAME_CMD_FIFO_MODEL + strict command-FIFO enable:
frameHash=0x6934c1b5
drawPackets=0 direct/setup=32/0
packetTypes=0:8,1:23286,2:0,3:0,4:69743,5:0,6:0,7:3

baseline + MAME_CMD_FIFO_MODEL + framebuffer-sized command-FIFO RAM mask:
frameHash=0x8004b9d5
drawPackets=295 direct/setup=368/168
packetTypes=0:196318,1:25412,2:0,3:295,4:71446,5:3345,6:0,7:3

cold start to f220 + MAME_CMD_FIFO_MODEL + framebuffer-sized command-FIFO RAM mask:
pc=0xffffffff8010805c
frameHash=0x30e41dc5
drawPackets=0 direct/setup=33/0
packetTypes=0:770422,1:1,2:0,3:0,4:1,5:1384,6:0,7:0
framebuffer nonBlack=0 colored=0

baseline + MAME_CMD_FIFO_MODEL after runtime FIFO-ring wrap in the vertex FIFO fastpath:
frameHash=0x08bc7d55
drawPackets=2926 direct/setup=39/0
packetTypes=0:2896,1:28402,2:0,3:2926,4:81484,5:40855,6:0,7:3
type5-space3=40855 packets / 2493906 words

same at f260:
frameHash=0xafbe6460
drawPackets=6424 direct/setup=45/0
packetTypes=0:6796,1:32375,2:0,3:6424,4:94839,5:88451,6:0,7:3
type5-space3=88451 packets / 5540050 words
buffers=0:nz=307200:white=295259:colored=307200 1:nz=13985:white=1998:colored=13985

same after white-dominated front-buffer avoidance:
f220 frameHash=0xdbcda991 framebuffer nonBlack=13732 colored=11734 rbuf=1
f260 frameHash=0xafbe6460 framebuffer nonBlack=13985 colored=11987 rbuf=1
f260 counters: ffb=50/0/0 ffw=50/0/0 ffk=0/0/0 ffs=271/0/0 swc=0

same + MAME_CMD_FIFO_YIELD_ON_WORK f220:
frameHash=0xa4fafa4d
drawPackets=2926 direct/setup=39/0
packetTypes=0:2676,1:28402,2:0,3:2926,4:81544,5:40855,6:0,7:3
framebuffer nonBlack=307200 colored=11746
```

The cold-start result proves the MAME path can consume a large amount of FIFO
traffic, but it is still not semantically correct: odd packet classes return
and the run is in a different load-state than the f180 warm continuation. The
`TRACE_VOODOO_CMD_FIFO_MODEL` stop trace mostly shows normal `depth < words`
pauses while the game writes a multi-word packet one word at a time; that is not
by itself the stall. MAME-correcting type-5 space 0 to write local command
RAM/framebuffer RAM instead of immediate LFB does not materially change f220.
The `MAME_CMD_FIFO_YIELD_ON_WORK` timing experiment is also negative; it
under-executes warm f220 further. Two more controls should also stay diagnostic
only: strict FIFO enable routes current synthetic fastpath payload words into
register writes and kills draws, and making the command FIFO backing mask match
the full framebuffer RAM size causes NOP/type-0 dominance. The active MAME probe
therefore remains on the 64K-word command FIFO storage mask for now.

The next concrete MAME-FIFO issue was found at warm f220: without wrapping the
synthetic vertex FIFO fastpath's write pointer, the MAME model stops on
`peek=0xc0000205` (`66` words needed) with only `62` words of depth near the
runtime FIFO ring end. Wrapping the fastpath writes inside the runtime
`state+0x378/state+0x380` FIFO ring is gated to `FIX_VOODOO_MAME_CMD_FIFO_MODEL`
and restores texture-packet throughput (`40855` type-5 packets vs baseline
`39562`). It is still not correct: the frame hash changes and setup/direct draw
work collapses, so the next target is register/setup sequencing after wrapped
texture FIFO replay, not more type-5 payload depth.

Follow-up draw traces show the "direct/setup collapse" is not the best primary
signal. Baseline also executes some clearly synthetic/stale register-draw noise
around `pc=0xffffffff800fe5d4`, while MAME-wrap mainly replays type-3 setup
triangles from `pc=0xffffffff800c4e5c`. By f260 MAME-wrap has more texture
traffic than baseline but fewer swaps/fills and leaves color buffer 0 almost
entirely white while buffer 1 remains plausible. `MAME_CMD_FIFO_YIELD_ON_WORK`
does not fix ordering; it makes the white buffer become the selected framebuffer.
The next useful target is therefore swap/fastfill/buffer selection ordering
under the wrapped MAME FIFO replay.

MAME's Voodoo `swapbufferCMD` bit 9 (`dont_swap`) was added under the MAME FIFO
path, but current probes show `dswap=0`, so it is correct cleanup rather than
the visible issue. The visible whiteout was the display-buffer chooser accepting
a front buffer that was almost entirely white because it still had enough
non-white active pixels. The chooser now treats a visible front buffer with more
than ~240k white pixels and fewer than 32k active non-white pixels as
white-clear-dominated, allowing a comparable back/alternate buffer to win. This
leaves default f220 unchanged and makes MAME-wrap f220/f260 render from buffer 1
instead of the white front buffer.

Fastfill/swap counters narrow the remaining ordering issue:

```text
default f260:
ffb=34/12/0 ffw=23/0/0 ffk=11/12/0 ffo=0/0/0
ffs=359/304/32 swc=1
framebuffer nonBlack=13628 colored=11630

MAME-wrap f260:
ffb=50/0/0 ffw=50/0/0 ffk=0/0/0 ffo=0/0/0
ffs=271/0/0 swc=0
framebuffer nonBlack=13985 colored=11987

MAME-wrap f260 with FASTFILL_COLOR_MASK disabled:
ffb=208/113/0 ffw=208/113/0 ffk=0/0/0 ffo=0/0/0
ffs=0/0/0
framebuffer nonBlack=307200 colored=11941
```

So the remaining delta is not fixed by allowing masked fastfills through; that
just makes both visible buffers white-dominated. MAME-wrap currently does not
see the black fastfills/swap-clear pattern that the baseline path sees. The
next target should be FIFO/timing/order before fastfill emission, especially
why baseline reaches `ffk` and `swc` events that MAME-wrap does not.

Added an opt-in compact PC profile for this exact issue:

```text
EUTHERDRIVE_GAUNTDL_PROFILE_VOODOO_FASTFILL_SWAP_PCS=1
```

It adds `ffpc=` and `swpc=` to the Voodoo debug line, grouped by CPU PC, with
white/black/other/suppressed counts plus the latest `color0/color1/za/fbz` and
command-FIFO read index seen by each fastfill PC.

Profiled f260 comparison:

```text
default:
ffpc=0xffffffff800fe5d4:379/w25/k322/o32/s356/b11-12-0/last0000:1:-1:fbz00000000:c00000000-00000000:za00000000:rd0,
     0xffffffff801027cc:362/w357/k5/o0/s339/b23-0-0/lastFFFF:1:-1:fbz00000000:cFFFFFFFF-00000000:za00FFFFFF:rd58EE
swpc=0xffffffff800fe5d4:375/c1/d0/last0x00000000,
     0xffffffff80102a80:212/c0/d0/last0x00000000,
     0xffffffff80102ab4:212/c0/d0/last0x00000000

MAME-wrap:
ffpc=0xffffffff800fe5d4:107/w107/k0/o0/s106/b1-0-0/lastFFFF:1:-1:fbz00000000:cFFFFFFFF-00000000:za00FFFFFF:rd5ABFA2,
     0xffffffff800fe5fc:99/w99/k0/o0/s85/b14-0-0/lastFFFF:1:-1:fbz00000000:cFFFFFFFF-FFFFFFFF:zaFFFFFFFF:rd57FFF2,
     0xffffffff801027cc:113/w113/k0/o0/s78/b35-0-0/lastFFFF:1:-1:fbz00000000:cFFFFFFFF-00000000:za00FFFFFF:rd5B3FEB
swpc=0xffffffff80102a80:212/c0/d0/last0x00000000,
     0xffffffff80102ab4:212/c0/d0/last0x00000000,
     0xffffffff800fe5fc:7/c0/d0/last0x00000000
```

This is the strongest current lead. Both paths reach `pc=0xffffffff800fe5d4`,
but baseline sees the expected black clear state there (`last0000`,
`color0/color1/za=0`) and one clear-swap, while MAME-wrap sees white
under suppressed color writes (`lastFFFF`, `color0=FFFFFFFF`, `za=00FFFFFF`)
and no clear-swap from that PC. Continue by tracing the register packet order
around the `800fe5d4` fastfill sequence, especially `color0/color1/za`,
`fbzMode`, and `swapbufferCMD`, rather than changing the fastfill mask.

Added a narrower order trace for that sequence:

```text
EUTHERDRIVE_GAUNTDL_TRACE_VOODOO_FASTFILL_SWAP_ORDER=1
EUTHERDRIVE_GAUNTDL_TRACE_VOODOO_FASTFILL_SWAP_ORDER_PCS=ffffffff800fe5d4,ffffffff800fe5fc,ffffffff801027cc
```

The PC list is optional; without it the trace is too noisy because the early
`801031a8` color-state loop fills the trace budget before the useful sequence.

Rechecked f260 after adding the trace:

```text
baseline f260:
frameHash=0x8004b9d5
drawPackets=7466 direct/setup=789/378
fastFills=1393 swaps=1187 ffb=34/12/0 ffw=23/0/0 ffk=11/12/0 swc=1

MAME-wrap f260:
frameHash=0xafbe6460
drawPackets=6424 direct/setup=45/0
fastFills=973 swaps=815 ffb=50/0/0 ffw=50/0/0 ffk=0/0/0 swc=0
```

The trace sharpens the failure mode. In baseline, the `800fe5d4` sequence drains
to local command-FIFO state `rd=0/depth=0`, applies black state
(`c0/c1/za/fbz=0`), emits repeated suppressed black fastfills, and executes
swap commands from the same PC. One trace window also shows the stale
`c1=0xc0000205` word being overwritten back to zero before the stable black
sequence continues:

```text
baseline around 800fe5d4:
kind=fastfill-suppressed value=0x00000000 c0=0 c1=0 za=0 fbz=0 rd=0 depth=0
kind=swap value=0x00000000 c0=0 c1=0 za=0 fbz=0 rd=0 depth=0
```

In the MAME-wrap path, the comparable `800fe5d4/800fe5fc` region is still
decoding from a live command FIFO window with thousands of words remaining
(`depth` roughly `4.5k -> 4.2k` in the sampled window), and it mostly applies
white `color0/color1` state. The only fastfills captured at the filtered PCs are
white:

```text
MAME-wrap around 800fe5fc:
kind=fastfill value=0x0000ffff c0=ffffffff c1=00000000 za=00ffffff fbz=00000460 depth=4
...
kind=reg reg=0x051/0x052 value=ffffffff c0=ffffffff c1=ffffffff za=ffffffff fbz=00000460
```

So the next target should not be display-buffer choice or fastfill color masks.
It is the command-FIFO read/drain model around the transition from the
texture-heavy replay back to the `800fe5d4` clear/swap service. Baseline's old
valid-slot model effectively reaches an empty local FIFO at that point; the
MAME-depth model is still consuming a backlog and never reaches the black
clear/swap state before f260.

Two narrow MAME-FIFO controls were tested after that:

```text
EUTHERDRIVE_GAUNTDL_EXPERIMENT_VOODOO_MAME_FIFO_WRAP_CLEAR=1
EUTHERDRIVE_GAUNTDL_EXPERIMENT_VOODOO_MAME_FIFO_MASK_READ_INDEX=1
```

`MAME_FIFO_WRAP_CLEAR` reintroduced the old `storageIndex == 0` clear behavior
under the MAME path. It is not safe: it finds some black/swap state, but clears
too early and destroys legitimate texture/draw traffic.

```text
MAME-wrap + WRAP_CLEAR f220:
frameHash=0x7418c5eb
drawPackets=14 direct/setup=174/67
texWrites=122645 fastFills=747 swaps=879

MAME-wrap + WRAP_CLEAR f260:
frameHash=0x4f665194
drawPackets=26 direct/setup=180/67
texWrites=251477 fastFills=752 swaps=882
framebuffer colored=0
```

`MAME_FIFO_MASK_READ_INDEX` masks read-pointer register writes, type-0 jumps,
and packet advancement to the local 64K-word storage ring. It is neutral: f220
and f260 match the unmasked MAME-wrap results exactly except for the displayed
`cmdrd` formatting.

```text
MAME-wrap + MASK_READ_INDEX f260:
frameHash=0xafbe6460
drawPackets=6424 direct/setup=45/0
texWrites=5540053 fastFills=973 swaps=815
ffk=0/0/0 swc=0
```

Conclusion from these controls: the missing black/swap sequence is not fixed by
blindly restoring the old wrap clear, and it is not caused by the unbounded
read-index display/advance alone. The next useful probe should inspect
MAME-style `cmdFifoDepth/cmdFifoHoles/cmdFifoAMin/cmdFifoAMax` updates against
the real producer writes around the ring end, likely with a targeted trace that
captures only late FIFO address-window transitions instead of the early startup
traffic.

Additional MAME-FIFO probes on 2026-06-11:

```text
EUTHERDRIVE_GAUNTDL_EXPERIMENT_VOODOO_MAME_FIFO_RESYNC_AMIN_ON_PARTIAL_TYPE5=1
EUTHERDRIVE_GAUNTDL_EXPERIMENT_VOODOO_MAME_CMD_FIFO_YIELD_ON_WORK=1
EUTHERDRIVE_GAUNTDL_EXPERIMENT_VOODOO_MAME_FIFO_SPACE0_ENDIAN=1
EUTHERDRIVE_GAUNTDL_EXPERIMENT_VOODOO_MAME_FIFO_STOP_ON_UNKNOWN=1
```

`RESYNC_AMIN_ON_PARTIAL_TYPE5` was a bad control. It did not restore black
clears and pushed the final image toward a full white framebuffer:

```text
MAME-wrap + RESYNC_AMIN_ON_PARTIAL_TYPE5 f260:
frameHash=0xf20bcaf3
drawPackets=7848 direct/setup=45/0
texWrites=3274310 fastFills=1097 swaps=881
ffk=0/0/0 swc=0
framebuffer nonBlack=307200 colored=892
```

`MAME_CMD_FIFO_YIELD_ON_WORK` matches MAME's stop-after-positive-cycles shape
more closely, but it does not fix the clear state:

```text
MAME-wrap + YIELD_ON_WORK f260:
frameHash=0x77e2a3d8
drawPackets=6424 direct/setup=45/0
texWrites=5540053 fastFills=1021 swaps=815
ffk=0/0/0 swc=0
```

`MAME_FIFO_SPACE0_ENDIAN` was neutral at f220/f260 and matched the unmodified
MAME-wrap result. Removing the internal `0xffff` clamp from MAME-style
`_cmdFifoDepth/_cmdFifoHoles` was also neutral at f220/f260, but is closer to
MAME's internal `u32` counters; register reads remain clamped.

The useful new instrumentation is:

```text
EUTHERDRIVE_GAUNTDL_TRACE_VOODOO_CMD_FIFO_MODEL_PCS=...
EUTHERDRIVE_GAUNTDL_TRACE_VOODOO_CMD_FIFO_MODEL_COMMANDS=...
```

The PC filter now applies to decode-stop traces as well, so startup stalls no
longer consume the whole trace budget. A targeted trace for command
`0xc0000205` showed that it is not false header data: it is a real 66-word
type-5 texture packet produced at `800fe7a0..800fe7cc`. The MAME-depth model
waits correctly while depth rises from partial to complete, then consumes it.
By f400, however, the model is out of packet phase again:

Fastfill color-mask control on 2026-06-11:

`EUTHERDRIVE_GAUNTDL_FIX_VOODOO_FASTFILL_COLOR_MASK` was removed from the
implicit `BRINGUP_FAST` set and is now explicit opt-in only. On the current
warm snapshot, default `BRINGUP_FAST` suppressed the black clear sequence when
`fbzMode` color writes were disabled and left the visible framebuffer dominated
by white:

```text
BRINGUP_FAST f260:
frameHash=0xbd71006f
ffb=36/0/0 ffw=36/0/0 ffk=0/0/0
ffs=388/1171/91
framebuffer nonBlack=307200 colored=443
```

With `EUTHERDRIVE_GAUNTDL_FIX_VOODOO_FASTFILL_COLOR_MASK=0`, those clears are
applied again:

```text
BRINGUP_FAST + FASTFILL_COLOR_MASK=0 f260:
frameHash=0x8e14c17e
ffb=1044/642/0 ffw=405/19/0 ffk=636/535/0
ffs=0/0/0
buffer1 nonBlack=307200 white=1 colored=307200
```

The color-mask behavior still may be useful as a later accuracy fix, but it is
too broad for bringup because Gauntlet's current state frequently issues
important clear commands while the emulated color-write bit is not set.

Fresh f400 comparison after the fastfill color-mask change:

```text
BRINGUP_FAST f400:
frameHash=0x8e14c17e
drawPackets=1024 direct/setup=2818/1394
texWrites=5431619 fastFills=3399 swaps=2774
ffk=692/581/0 ffs=0/0/0
packetTypes=0:2422,1:42639,2:0,3:1024,4:125877,5:84869,6:0,7:6
framebuffer colored=307199

BRINGUP_FAST + MAME_CMD_FIFO_MODEL f400:
frameHash=0x9ac85dc5
drawPackets=1046 direct/setup=46/0
texWrites=6555139 fastFills=1762 swaps=1382
ffk=0/0/0 ffs=0/0/0
packetTypes=0:9943,1:42142,2:0,3:1046,4:122690,5:102424,6:0,7:3
framebuffer colored=0
cmdrd=0x6B735C cmd=3/0/0x2CD78/0x2CD78
peek=0x00059604:4
```

The MAME-FIFO path no longer shows the old type-6/huge-type-5 phase failure at
f400, and it no longer depends on fastfill suppression, but it still misses the
black clear/swap progression. The current failure is a late partial type-4
packet. `DebugStatus` now includes the most recent command FIFO decode stop:

```text
cmdstop=depth/0x00059604/4/3/0x1ADCD70/pc=0xFFFFFFFF801031A8/2087871
```

A targeted trace for `0x00059604` showed the same packet is normally produced
as four consecutive words and the depth stalls at 1/2/3 clear once the next
word arrives. The final f400 state is different: only three words have arrived
for the packet at `cmdrd=0x6B735C`. Next useful target is the producer around
`80103190..801031a8` and the FIFO room/state that makes it stop after the third
word in the MAME-depth path.

`EUTHERDRIVE_GAUNTDL_EXPERIMENT_VOODOO_MAME_FIFO_MASK_READ_INDEX=1` remains
neutral at f400 after the fastfill-mask change. It only changes displayed
`cmdrd` from `0x6B735C` to `0x735C`; the hash, white framebuffer, and
`cmdstop=depth/0x00059604/4/3/...` remain unchanged.

`EUTHERDRIVE_GAUNTDL_EXPERIMENT_VOODOO_MAME_FIFO_TRUNCATE_PARTIAL_TYPE4=1` is
a negative control. It proves that simply consuming the partial packet is not a
valid repair:

```text
MAME_CMD_FIFO_MODEL + TRUNCATE_PARTIAL_TYPE4 f400:
frameHash=0x9ac85dc5
drawPackets=1046 direct/setup=46/0
texWrites=6555139 fastFills=1761 swaps=1186
packetTypes=0:57921,1:40547,2:0,3:1046,4:122631,5:102424,6:0,7:6169
framebuffer colored=0
cmdstop=partial-type4-truncate/0x00059604/4/2/0x1ADCD70/pc=0xFFFFFFFF8010319C/2071355
```

The experiment removes the final depth stop but causes thousands of type-7
packets and no visual progress, so the correct next step is not decoder
tolerance. Keep the investigation on why the MAME-depth model reaches a partial
`0x00059604` packet in the first place.

```text
MAME-wrap f400:
frameHash=0xcff92b39
drawPackets=10504 direct/setup=49/0
texWrites=7339121 fastFills=1144 swaps=1215
ffk=0/0/0 swc=0
cmdrd=0x90EC5C cmd=76456/0/0x25C0C/0x25C0C
peek=0x40E28DD5:283068
reg[026]=0xc0000205

MAME-wrap + STOP_ON_UNKNOWN f400:
frameHash=0xafbe6460
drawPackets=10381 direct/setup=49/0
texWrites=7246641 fastFills=3465 swaps=2002
ffk=0/0/0 swc=0
cmdrd=0x9131F2 cmd=58642/0/0x25C0C/0x25C0C
peek=0x3B4C55B5:101048
reg[026]=0xc0000205
```

`STOP_ON_UNKNOWN` removes type-6 consumption but still ends with a data word
misread as a huge type-5 header, so the remaining bug is still read-pointer /
packet-boundary phase, not a specific type-6/7 fallback.

`tools/GauntletProbe` now writes warmup snapshot version 3 and includes the
MAME command FIFO address fields:

```text
_cmdFifoRamBase
_cmdFifoRamEnd
_cmdFifoAddressMin
_cmdFifoAddressMax
```

Loading existing v1/v2 warm snapshots remains supported. This does not make the
old f180 snapshot a valid MAME-FIFO state, but future snapshots created while
probing this model will no longer silently drop the new address-window state.

The important conclusion is that the odd packet classes are a symptom of the
simplified `valid[]` command FIFO model, not a type-5 length bug: MAME's
`voodoo_2.cpp` confirms type 5 is `2 + N` words. The decode-window experiment
removes type-2/type-6 and most type-7 noise, but it suppresses legitimate draw
work in both baseline and slot-2 runs. The MAME-style model also removes the
odd packet classes, but it currently under-executes legitimate type-5 and draw
work. The next target is to find why its `depth/holes/address_min/address_max`
accounting stalls relative to the old baseline, likely around warm snapshot
register state, read-pointer jumps, or bulk fastpath write boundaries.

Do not promote broad indexed headers, `SHORT_READ_FILL_REMAINING`, all-index
hydration, FIFO bulk reset, FIFO bulk rewind, or FIFO bulk decode-window until
that packet-class change is explained. Do not promote
`FIX_VOODOO_MAME_CMD_FIFO_MODEL` yet either; keep it as a probe/trace path.

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

Probe builds:

```sh
dotnet build tools/GauntletProbe/GauntletProbe.csproj -c Release --no-restore /clp:ErrorsOnly
```

Last verified in this pass: core build succeeded with 343 warnings and 0 errors;
probe release build succeeded with 344 warnings and 0 errors.

## 2026-06-10 Handoff At Break: Minimum Safe Indexed Payload

Current safest payload stack is:

```text
EUTHERDRIVE_GAUNTDL_BRINGUP_FAST=1
EUTHERDRIVE_GAUNTDL_FIX_RUNTIME_BGLOADMODEL_PARTIAL_INDEXED_SOURCE_PAYLOADS=1
EUTHERDRIVE_GAUNTDL_FIX_RUNTIME_BGLOADMODEL_ASSET_NAMES=1
```

This is intentionally more conservative than the earlier all-index source
header fix. `BRINGUP_FAST` by itself does not enable the partial indexed
payload repair. The new repair hydrates only BGLoadModel index 1 (`gei`) by
default (`mask=2`) with `0x9f60` bytes. This preserves the known-good frame
hash and avoids the wider all-index texture churn until we know which later
assets are actually needed.

Warm snapshot used for the latest probes:

```text
/tmp/eutherdrive-gauntlet-probe/gauntdl-gauntdl24-indexed-src-f600-s200000.warm
```

Run it with `EUTHERDRIVE_GAUNTDL_WARMUP_FRAMES=180`. The saved snapshot's
internal frame counter is 600, but `tools/GauntletProbe/Program.cs` now saves
future final snapshots with the requested target frame count instead of the
warmup-frame count.

Known-good f620 result with the minimum safe stack:

```text
frame=620
pc=0xffffffff800b1ae0
frameHash=0x27001f2f
texWrites=9257493
textureMap=writes=105032:nz=58084:zero=46948:touched=9573
fastFills=3080 swaps=111498 colored=9125
voodoo reg[02a]=0xc0000205
voodoo reg[09a]=0x00000000
```

Known-good f900 result with the same stack:

```text
frame=900
pc=0xffffffff8004cb0c
frameHash=0x27001f2f
texWrites=9257493
textureMap=writes=105032:nz=58084:zero=46948:touched=9573
fastFills=5252 swaps=111498 colored=9125
voodoo reg[09a]=0x00000000
```

Plain `BRINGUP_FAST` remains unchanged and is still useful as a control:

```text
frame=620 pc=0xffffffff800af764 frameHash=0x27001f2f
texWrites=9231235 textureMap=writes=0 fastFills=3088 colored=9125
```

Wider masks were tested but should stay experimental:

- Mask `40` (`geb` only) regressed the visible hash at f620
  (`frameHash=0x2f5d8900`, `colored=8709`).
- Mask `42` (`gei+geb`) matched the all-index behavior at f900: good hash,
  but higher texture work (`texWrites=9273039`, `textureMap=writes=167216`).
- All-index `0x9f60` held the good hash through f1500 in earlier probes, but
  it creates more texture writes than the minimum stack and is not the safest
  default.
- Runtime diagnostic menu scan experiments are not safe here. They regressed
  f620 to `frameHash=0x2f5d8900` even with the same `gei` texture writes.

`GauntletProbe` now has `EUTHERDRIVE_GAUNTDL_DUMP_RENDER_RECORDS=1`. The dump
prints render-list counts, slot distribution, first records, non-slot-0
records, and allocator body histograms. This confirmed that the current
render-list entries are still loading/UI text records, not real model-body
records:

```text
f620 minimum stack:
renderRecords count=43 flag40=30 nullBody=43 nonZeroToken=0
renderRecords slots=0:30,6:1,7:9,8:3

f660 minimum stack:
renderRecords count=53 flag40=30 nullBody=53 nonZeroToken=0
renderRecords slots=0:30,1:2,6:1,7:15,8:5

f900 minimum stack:
renderRecords count=37 flag40=30 nullBody=37 nonZeroToken=0
renderRecords slots=0:30,6:1,7:5,8:1

f1200 minimum stack:
frameHash=0x27001f2f pc=0xffffffff800b1a6c
renderRecords count=41 flag40=30 nullBody=41 nonZeroToken=0
renderRecords slots=0:30,6:1,7:8,8:2
```

The f620 difference versus plain `BRINGUP_FAST` was only timing: by f660 the
minimum stack reaches the same 53-record loading/UI list shape as plain f620.
All sampled `s2` pointers are sequential bytes in the `8020f268..8020f29c`
text/layout buffer. A byte dump around `8020f260` showed NULs followed by
space padding, so `TryFastPathKnownRuntimeRenderRecordNullBody()` is still
handling the intended null-text-record case. `807ffc58`, the repeated source
for the string-copy fast path, points into a structure-like area; the copied
record-token bytes are zero because the render-list is still drawing/loading
text placeholders.

Important interpretation at break:

- The minimum `gei` payload is safe and gives real texture-map writes.
- It does not yet unlock real model-body render records (`nonZeroToken` stays
  0 through f1200).
- `800b1dc4` remains a hot helper on the render path, but it is not the next
  correctness bug; the body token data is still intentionally null/empty.
- The next target should be load progression and asset/source-table state,
  not another render null-body fast path.

Suggested next probe:

1. Dump BGLoadModel asset/source tables at f900 and compare plain
   `BRINGUP_FAST` vs the minimum stack, especially entries 1..8 and any
   selected model/material globals near the `800aa958/800aacb4/800aae98`
   path.
2. Trace why later assets are still normalized back to `802e1718` after `gei`
   is hydrated; current logs show names repaired, then later pointer-normalize
   calls still collapse slots 2..8 to static source.
3. Only after identifying the next required source index, raise the mask
   narrowly. Do not jump back to all-index by default.

## 2026-06-10 Continuation: Promote Indexed BGLoadModel Source Headers

The previously experimental combination for BGLoadModel source slots 1..8 is
now a normal bring-up fast fix:

```text
EUTHERDRIVE_GAUNTDL_FIX_RUNTIME_BGLOADMODEL_INDEXED_SOURCE_HEADERS=1
```

It is enabled by `EUTHERDRIVE_GAUNTDL_BRINGUP_FAST=1`. The repair runs at the
same caller/parser points as the older distinct-source experiment, but only
writes `802529a0 + index * 4` when the per-index source header was actually
hydrated from the known indexed texture payload table. This avoids the earlier
failed state where the table pointed at empty per-index buffers.

300-frame verification from the castle warm snapshot:

```sh
env EUTHERDRIVE_GAUNTDL_BRINGUP_FAST=1 \
    EUTHERDRIVE_GAUNTDL_WARMUP_STATE=/tmp/eutherdrive-gauntlet-probe/gauntdl-gauntdl24-castle-f180-s200000.warm \
    EUTHERDRIVE_GAUNTDL_WARMUP_FRAMES=180 \
    EUTHERDRIVE_GAUNTDL_CPU_STEPS_PER_FRAME=200000 \
    dotnet run --project tools/GauntletProbe/GauntletProbe.csproj -c Release --no-build -- \
      /home/nichlas/roms/MAME/Midway/Vegas/gauntd 300 200000 0
```

Result:

```text
frame=300
pc=0xffffffff800c8138
frameHash=0x27001f2f
drawPackets=1080 directTriangles=3605 setupTriangles=1784
texWrites=9231235 fastFills=2973 swaps=7681
framebuffer=640x480 nonBlack=28727 colored=9125
```

Baseline immediately before this fix, with the same castle snapshot and only
the older fast fixes:

```text
frameHash=0xbd71006f
drawPackets=1024 directTriangles=2818 setupTriangles=1394
texWrites=6203075 framebuffer colored=443
```

The source table now has distinct hydrated headers:

```text
802529a0: 802e1718 802e3718 802e5718 802e7718
802529b0: 802e9718 802eb718 802ed718 802ef718
802529c0: 802f1718 80312998 80332998 ...
```

The asset table entries for slots 1..8 now contain non-static pointers/counts
instead of repeated `802e1718/0` empty descriptors. The missing-texture loop
still sees empty names, but it is no longer parsing a completely collapsed
source list. Next target: identify whether the empty names are harmless runtime
labels or still blocking later material/model lookup, then trace from the new
PC plateau around `800c8138`.

600-frame sanity with the same settings remained stable:

```text
frame=600
pc=0xffffffff8004ed68
frameHash=0x2f5d8900
drawPackets=1080 directTriangles=3605 setupTriangles=1784
texWrites=9231235 fastFills=2973 swaps=110922
framebuffer=640x480 nonBlack=307200 colored=8709
```

The Voodoo draw/texture counters stop growing after the new source-header
repair has done its work, while swap/status traffic continues. The next blocker
therefore appears to be a later runtime wait/state decision rather than another
early BGLoadModel source-table collapse.

Follow-up probes after the source-header fix:

- Pressing player FIRE 3 / TURBO for frames 220..280 did not leave the visible
  diagnostic/menu pump. It reproduced the same `frameHash=0x2f5d8900`,
  `pc=0xffffffff8004ed68`, and draw/texture counters.
- `EUTHERDRIVE_GAUNTDL_EXPERIMENT_RUNTIME_DIAGNOSTIC_OVERLAY_SUPPRESS=1` plus
  `EUTHERDRIVE_GAUNTDL_EXPERIMENT_RUNTIME_DIAGNOSTIC_TEXT_PUMP_SKIP=1` changed
  the sampled PC to `0xffffffff801034c8` and increased type-1/swap traffic, but
  did not increase draw packets, triangle counts, texture writes, or the final
  frame hash. Keep those experiments off the best stack.
- A saved f600 continuation snapshot now exists at:

```text
/tmp/eutherdrive-gauntlet-probe/gauntdl-gauntdl24-indexed-src-f600-s200000.warm
```

Because the probe harness used warmup-frame metadata for final snapshots, this
specific file currently loads with `EUTHERDRIVE_GAUNTDL_WARMUP_FRAMES=180`
despite containing `frameCounter=600`. `tools/GauntletProbe/Program.cs` has
been corrected so future `EUTHERDRIVE_GAUNTDL_SAVE_FINAL_STATE` files store the
actual target frame count.

Continuing from that f600 snapshot to frame 900 is stable but confirms the same
plateau:

```text
frame=900
pc=0xffffffff800c7b94
frameHash=0x2f5d8900
drawPackets=1080 directTriangles=3605 setupTriangles=1784
texWrites=9231235 fastFills=2973 swaps=214164
framebuffer=640x480 nonBlack=307200 colored=8709
hotpcs=800e3378,800b1dc4,80121670,800c7b68..800c7b90,800c81a8..
```

The current best next target is the `800c7b68..800c8210` progress/text pump and
the loaded Glide state at `80262d64`, especially the fields around
`+0x250..+0x37c`. At f600, the state includes `fbzMode=00000460`, FIFO room
tracking around `+0x374/+0x37c`, and queued type-1/state words, but no further
geometry or texture upload work is being generated.

### Castle Worlddata Static Link

The f600 plateau still had `8016c13c=802e1000`, so the selected runtime world
was castle, but the matching static world entry at `8015bef4` had no worlddata
link:

```text
8015bef4+0x10 = 00000000
```

Raw FSYS inspection of `gauntd24.raw` verified the `/worlddata` headers:

```text
/worlddata directory header  byte=0x0f0f8a00 lba=0x787c5
/worlddata directory payload byte=0x0f0f8c00 lba=0x787c6
castle.wad id=0x326
castle.wad header  byte=0x0f0ffe00 lba=0x787ff size=0x0a04
castle.wad payload byte=0x0f100000 lba=0x78800 first=0x000009a4 count=8
```

`ApplyKnownRuntimeWorldStaticDataLinkRepair()` now links selected castle static
data at the existing `8004f29c` scan point, hydrating `81000000` from
`/worlddata/castle.wad`, storing it in `8015bef4+0x10`, storing count `8` in
`+0x14`, and setting `v0` so the already-loaded branch condition sees the new
pointer immediately:

```text
[GAUNTDL:FIX] world-selected-static-data-link pc=ffffffff8004f29c index=0 entry=ffffffff8015bef4 data=ffffffff81000000 offset=f100000 bytes=a04 first=000009a4 count=00000008
```

Short f600 -> f601 verification:

```text
pc=0xffffffff800c7bd0
frameHash=0x2f5d8900
8015bef4+0x10=81000000 +0x14=00000008
81000000: a4 09 00 00 08 00 00 00 ...
```

This is not a new-geometry fix by itself, but it does unlock additional runtime
work after f600. The previous f600 -> f900 run stayed in the `800c7b94` progress
pump with no BGLoadModel activity. With the castle worlddata link, the run emits
new BGLoadModel and render-record traces after frame 600:

```text
bgloadmodel-known-missing-texture-caller-loop key=<empty> pc=ffffffff800aa958 ...
render-record-null-body pc=ffffffff800b1e7c record=ffffffff80210d18 ...
```

f600 -> f900 with the castle link now ends at a different plateau:

```text
frame=900
pc=0xffffffff80013a3c
frameHash=0x27001f2f
drawPackets=1080 directTriangles=3605 setupTriangles=1784
texWrites=9231235 fastFills=5253 swaps=111498
lfbWrites=461061927
framebuffer=640x480 nonBlack=28727 colored=9125
hotpcs=800b1dc4,80121670,8011fab8,8011f7ac,80120204..80120230
```

Important delta versus the pre-castle-link f600 -> f900 plateau:

```text
old pc=0xffffffff800c7b94
old frameHash=0x2f5d8900
old fastFills=2973 swaps=214164 lfbWrites=110853927
new pc=0xffffffff80013a3c
new frameHash=0x27001f2f
new fastFills=5253 swaps=111498 lfbWrites=461061927
```

Next target moved from the old `800c7b68..800c8210` progress pump to the new
`80013a3c` service path and its hot text/string helpers:
`8011f7ac`, `8011fab8`, and `80120204..80120230`. The recurring
`bgloadmodel-known-missing-texture-caller-loop` still shows empty asset names
for entries 1..8, so the next useful split is whether the new `80013a3c`
plateau is a text/progress service delay or still caused by missing BGLoadModel
asset names/material descriptors.

### Runtime Record Scan/Allocate Fastpath

The new hot loop after the castle static-data link was the small record
allocator at `800b1264..800b1300`. It reads the record count through signed
offset `0x8088(0x80230000)`, so the real count global is `80228088`, not
`80238088`. The table is `80255f20`, stride `0x50`, max `0x17f`; records with
signed byte `record+4 == 2` are reused, otherwise the count increments and the
next slot is returned.

`TryFastPathKnownRuntimeRecordScanAllocate()` now handles both the function
entry and the in-flight scan loop `800b128c..800b12a4`, preserving the count
write and normal return stack adjustment. Verification from the f600 snapshot:

```text
record-scan-allocate pc=ffffffff800b1264 start=0 count=0 selected=0 result=ffffffff80255f20
...
record-scan-allocate pc=ffffffff800b1264 start=0 count=7 selected=7 result=ffffffff80256150
```

f600 -> f620 after this fastpath:

```text
frame=620
pc=0xffffffff800af764
frameHash=0x27001f2f
drawPackets=1080 directTriangles=3605 setupTriangles=1784
texWrites=9231235 fastFills=3088 swaps=111498
lfbWrites=128517927
hotpcs=800b1dc4,80121670,8011fab8,8011f7ac,80120204..80120230
```

The previous `800b128c..800b12a4` hot group is gone. f600 -> f900 remains on
the same framebuffer hash and moves the sampled PC from the previous
`80013a3c` plateau into the render-record/service path:

```text
frame=900
pc=0xffffffff800b1ba0
frameHash=0x27001f2f
drawPackets=1080 directTriangles=3605 setupTriangles=1784
texWrites=9231235 fastFills=5263 swaps=111498
lfbWrites=462597927
hotpcs=800b1dc4,80121670,8011fab8,8011f7ac,80120204..80120230
```

The next target is now `800b1dc4` / nearby render-record traversal, with the
diagnostic format/string-copy family still a major secondary cost.

### Partial Indexed Source Header Probe

Default f600 -> f620/f900 was rechecked after the record-scan fastpath and still
matches the previous best visual baseline:

```text
f620 default:
pc=0xffffffff800af764
frameHash=0x27001f2f
drawPackets=1080 directTriangles=3605 setupTriangles=1784
texWrites=9231235 fastFills=3088 swaps=111498
framebuffer colored=9125

f900 default:
pc=0xffffffff800b1ba0
frameHash=0x27001f2f
drawPackets=1080 directTriangles=3605 setupTriangles=1784
texWrites=9231235 fastFills=5263 swaps=111498
```

The f620 dumps showed why the promoted indexed-source repair does not fire
after the f600 continuation point: source-table slots 1..8 collapse back to
`802e1718`, while the per-index windows have a zero header prefix but nonzero
loader metadata beginning around `+0x40`. The old guard required the whole
`0x80` window to be zero before hydrating the known indexed payload header.

Added an explicit opt-in experiment:

```text
EUTHERDRIVE_GAUNTDL_EXPERIMENT_RUNTIME_BGLOADMODEL_PARTIAL_INDEXED_SOURCE_HEADERS=1
```

With that enabled, `TrySeedKnownRuntimeBgLoadModelDistinctSourceIndexedHeader()`
allows hydration when only the first `0x40` bytes are clear. This restores the
distinct source table after f600:

```text
802529a0: 802e1718 802e3718 802e5718 802e7718
802529b0: 802e9718 802eb718 802ed718 802ef718
802529c0: 802f1718 ...
```

It is real runtime progress, but not a visual win yet:

```text
f620 partial-indexed-header experiment:
pc=0xffffffff800b1c78
frameHash=0x9ac85dc5
drawPackets=1174 directTriangles=4439 setupTriangles=2197
texWrites=12253059 textureMapWrites=12087296
framebuffer colored=0

f900 partial-indexed-header experiment:
pc=0xffffffff800b22a0
frameHash=0x9ac85dc5
drawPackets=1174 directTriangles=4439 setupTriangles=2197
texWrites=12253059 fastFills=5671 swaps=111915
framebuffer colored=0
```

Negative controls:

- `EUTHERDRIVE_GAUNTDL_PRESERVE_NONZERO_TEXTURE_BYTES=1` did not change the
  white-frame result.
- `EUTHERDRIVE_GAUNTDL_FIX_RUNTIME_BGLOADMODEL_ASSET_NAMES=1` also did not
  change it when it only repaired empty names. A later correction made this
  repair map asset-table body pointers back to the per-index source window via
  `sourceBase + header[0x5c]`, so `802f1abc` now correctly resolves to `stk`
  instead of being misidentified by address range. This keeps labels such as
  `gei/snm/stk/kjh/pnk` stable, but still does not by itself fix the white
  frame.

Follow-up mask probes used
`EUTHERDRIVE_GAUNTDL_EXPERIMENT_RUNTIME_BGLOADMODEL_DISTINCT_SOURCE_INDEXED_HEADER_MASK`
with the new partial-header experiment. The mask parser is hexadecimal, so
single-index masks are `2`, `4`, `8`, `10`, `20`, `40`, `80`, and `100` for
indexes 1..8.

```text
f620 single-index partial headers:
index 1 mask=2   frameHash=0xc00c5e9f pc=0xffffffff80019a3c colored=8810
index 2 mask=4   frameHash=0x0246527a pc=0xffffffff800b2a3c colored=9105
index 3 mask=8   frameHash=0x5dd6db19 pc=0xffffffff8012169c colored=9101
index 4 mask=10  frameHash=0x27001f2f pc=0xffffffff800c7a70 colored=9125
index 5 mask=20  frameHash=0x17db0316 pc=0xffffffff80106b58 colored=8812
index 6 mask=40  frameHash=0x7647fcce pc=0xffffffff800b1bd8 colored=9091
index 7 mask=80  frameHash=0x27001f2f pc=0xffffffff8004c950 colored=9125
index 8 mask=100 frameHash=0xf932733a pc=0xffffffff8012023c colored=9604
```

At f900, index 1 alone remains colored (`frameHash=0xc00c5e9f`,
`colored=8810`), while index 2 alone and the `1+2+3` combination both collapse
to white (`frameHash=0x9ac85dc5`, `colored=0`). The `1+2+3` probe also leaves
`0xc0000205` in a setup/vertex register, matching the earlier Voodoo trace where
type-5 texture command words leaked into register/fastfill interpretation.

Keep the partial indexed-header probe off `BRINGUP_FAST` for now. It proves the
late per-index source windows can be hydrated and pushes the CPU/Voodoo counters
forward, but all-index hydration is too broad. The next concrete target is the
BGLoadModel body/name side: find why the later asset table still shows
`<empty>` names for the indexed records and why multiple partial headers cause
type-5 command words such as `0xc0000205` to be consumed as render/setup state.

Follow-up asset-parser traces showed the first actionable split: `mask=e`
(`1+2+3`) parses `stk` correctly into asset entry `802f1abc/4`, but the
header-only source then exposes a side selector from the same index:

```text
index=3 source=802e7718
selector=802f17ec/0000001e/802f2ea0/0000001e
asset=3:802f1abc/00000004/.../stk
caller-after-path-lookup v0=802e87e8 v1=0000001e
```

That was the missing piece: `0x120` bytes is enough to rebuild the asset-table
entry, but not enough to make all side/list data referenced by the indexed
source safe. Added another explicit experiment:

```text
EUTHERDRIVE_GAUNTDL_EXPERIMENT_RUNTIME_BGLOADMODEL_FULL_INDEXED_SOURCE_PAYLOADS=1
```

When this is enabled, the indexed source seeding uses the known payload length
from `TryGetKnownRuntimeBgLoadModelTexturePayload()` instead of only `0x120`
bytes. It is still opt-in and not part of `BRINGUP_FAST`.

Results with partial headers + full payloads + asset names:

```text
mask=e f620:
pc=0xffffffff800b1ae0
frameHash=0x27001f2f
drawPackets=1080 directTriangles=3605 setupTriangles=1784
texWrites=9257493 fastFills=3080 swaps=111498
framebuffer colored=9125
reg[09a]=00000000

mask=e f900:
pc=0xffffffff8004cb0c
frameHash=0x27001f2f
drawPackets=1080 directTriangles=3605 setupTriangles=1784
texWrites=9257493 fastFills=5252 swaps=111498
framebuffer colored=9125
reg[09a]=00000000

all-index f620:
pc=0xffffffff800d51c4
frameHash=0x27001f2f
drawPackets=1080 directTriangles=3605 setupTriangles=1784
texWrites=9275037 fastFills=3074 swaps=111498
framebuffer colored=9125
reg[09a]=00000000
```

This confirms the white-out was caused by incomplete indexed source payloads,
not empty asset names. The current best next target is to decide whether full
payload seeding can be narrowed/promoted safely, because it restores correct
visual output but also changes texture-upload volume and PC plateaus versus the
default f600 continuation.

Minimum-payload sweep:

```text
mask=e f620:
0x2000 -> frameHash=0x2f5d8900 colored=8709
0x8000 -> frameHash=0x2f5d8900 colored=8709
0x9000 -> frameHash=0x2f5d8900 colored=8709
0x9f00 -> frameHash=0x2f5d8900 colored=8709
0x9f40 -> frameHash=0x2f5d8900 colored=8709
0x9f60 -> frameHash=0x27001f2f colored=9125 reg[09a]=00000000
0x9f80 -> frameHash=0x27001f2f colored=9125 reg[09a]=00000000
0xa000 -> frameHash=0x27001f2f colored=9125 reg[09a]=00000000
0xa0d0 -> frameHash=0x27001f2f colored=9125 reg[09a]=00000000
```

`0x9f60` is therefore the current smallest verified practical extent. It is
still a contiguous arena-style extent from the first indexed source, not an
isolated per-slot payload, because these source records deliberately reference
data beyond the nominal `0x2000` stride. Added:

```text
EUTHERDRIVE_GAUNTDL_EXPERIMENT_RUNTIME_BGLOADMODEL_MIN_INDEXED_SOURCE_PAYLOAD=1
```

This selects `0x9f60` without needing the generic
`EUTHERDRIVE_GAUNTDL_EXPERIMENT_RUNTIME_BGLOADMODEL_INDEXED_SOURCE_PAYLOAD_BYTES`
override.

The partial-header path now defaults to this `0x9f60` minimum extent whenever
`EUTHERDRIVE_GAUNTDL_EXPERIMENT_RUNTIME_BGLOADMODEL_PARTIAL_INDEXED_SOURCE_HEADERS=1`
is active. The generic byte override still wins first, and the full-payload
experiment still wins when no explicit byte override is present.

Verification:

```text
mask=e f900 with min payload:
pc=0xffffffff8004cb0c
frameHash=0x27001f2f
drawPackets=1080 directTriangles=3605 setupTriangles=1784
texWrites=9257493 fastFills=5252 swaps=111498
framebuffer colored=9125
reg[09a]=00000000

all-index f620 with min payload:
pc=0xffffffff80106a34
frameHash=0x27001f2f
drawPackets=1080 directTriangles=3605 setupTriangles=1784
texWrites=9273039 fastFills=3077 swaps=111498
framebuffer colored=9125
reg[09a]=00000000
```

Regression after making `0x9f60` the partial-header default:

```text
default BRINGUP_FAST f620:
pc=0xffffffff800af764
frameHash=0x27001f2f
texWrites=9231235 fastFills=3088 swaps=111498
framebuffer colored=9125

default BRINGUP_FAST f900:
pc=0xffffffff800b1ba0
frameHash=0x27001f2f
texWrites=9231235 fastFills=5263 swaps=111498
framebuffer colored=9125

partial mask=e f620:
pc=0xffffffff800b1ae0
bytes=00009f60
frameHash=0x27001f2f
texWrites=9257493 fastFills=3080 swaps=111498
framebuffer colored=9125

partial mask=e f900:
pc=0xffffffff8004cb0c
bytes=00009f60
frameHash=0x27001f2f
texWrites=9257493 fastFills=5252 swaps=111498
framebuffer colored=9125

partial all-index f620:
pc=0xffffffff80106a34
bytes=00009f60
frameHash=0x27001f2f
texWrites=9273039 fastFills=3077 swaps=111498
framebuffer colored=9125

partial all-index f900:
pc=0xffffffff800a9298
bytes=00009f60
frameHash=0x27001f2f
texWrites=9273039 fastFills=5249 swaps=111498
framebuffer colored=9125
```

Default `BRINGUP_FAST` remains unchanged because the partial-header seedability
path is still explicit. Since all-index f900 now holds, the next promotion
candidate is to decide whether `EUTHERDRIVE_GAUNTDL_FIX_RUNTIME_BGLOADMODEL_INDEXED_SOURCE_HEADERS`
should use the partial seedability + `0x9f60` extent by default after f600.

Longer all-index soak with the `0x9f60` extent:

```text
all-index f1200:
pc=0xffffffff80106a3c
frameHash=0x27001f2f
texWrites=9273039 fastFills=7576 swaps=111498
framebuffer colored=9125
reg[02a]=c0000205 reg[09a]=00000000

all-index f1500:
pc=0xffffffff800b1ffc
frameHash=0x27001f2f
texWrites=9273039 fastFills=9903 swaps=111498
framebuffer colored=9125
reg[02a]=c0000205 reg[09a]=00000000
```

`reg[02a]=c0000205` also appears in good minimum/full-payload runs; the bad
white-out signature was `reg[09a]=c0000205`, which stays clear here.

Added a narrower opt-in repair flag:

```text
EUTHERDRIVE_GAUNTDL_FIX_RUNTIME_BGLOADMODEL_PARTIAL_INDEXED_SOURCE_PAYLOADS=1
```

This flag enables the partial seedability check and the `0x9f60` source extent
without requiring the experiment flags. It intentionally uses truthy parsing
rather than `IsBringupFixEnabled`, so plain `EUTHERDRIVE_GAUNTDL_BRINGUP_FAST=1`
does not enable it yet. The repair path also keeps the source-table write gated
on successful indexed-source hydration, so it cannot silently fall back to the
old clone/static-source behavior if the payload seed fails.

Follow-up comparison showed why this should stay narrower than all-index by
default:

```text
plain BRINGUP_FAST f620:
frameHash=0x27001f2f
texWrites=9231235 textureMapWrites=0
hotpcs=800b1dc4,80121670,8011fab8,8011f7ac,80120204..

all-index partial payload f620:
frameHash=0x27001f2f
texWrites=9273039 textureMapWrites=167216
hotpcs=800fe7bc..800fe7e0,800b1dc4,80121670..

mask=2/index-1 gei f620:
frameHash=0x27001f2f
texWrites=9257493 textureMapWrites=105032
hotpcs=800b1dc4,800fe7bc..800fe7e0,80121670..

mask=40/index-6 geb f620:
frameHash=0x2f5d8900
texWrites=9246781 textureMapWrites=62184
framebuffer colored=8709
```

So `gei`/index 1 is the smallest currently verified partial-payload promotion
point. The opt-in repair now defaults to mask `0x2` when no explicit
`EUTHERDRIVE_GAUNTDL_EXPERIMENT_RUNTIME_BGLOADMODEL_DISTINCT_SOURCE_INDEXED_HEADER_MASK`
is set. Explicit masks still work for broader experiments such as `0x42` or
all-index.

Verification after adding the opt-in repair:

```text
BRINGUP_FAST + PARTIAL_INDEXED_SOURCE_PAYLOADS f900:
bytes=00009f60
mask=00000002
pc=0xffffffff8004cb0c
frameHash=0x27001f2f
texWrites=9257493 textureMapWrites=105032 fastFills=5252 swaps=111498
framebuffer colored=9125
reg[09a]=00000000

plain BRINGUP_FAST f620:
pc=0xffffffff800af764
frameHash=0x27001f2f
texWrites=9231235 fastFills=3088 swaps=111498
framebuffer colored=9125
reg[09a]=00000000

BRINGUP_FAST + PARTIAL_INDEXED_SOURCE_PAYLOADS f620 after the stricter guard:
bytes=00009f60
mask=00000002
pc=0xffffffff800b1ae0
frameHash=0x27001f2f
texWrites=9257493 textureMapWrites=105032 fastFills=3080 swaps=111498
framebuffer colored=9125
reg[09a]=00000000
```

So the current safe next boot stack is plain `BRINGUP_FAST` plus the explicit
partial indexed source payload repair, with its default index-1 mask. Keep it
explicit until we have a cleaner explanation for the remaining extra
texture-download work and PC plateau versus plain default.

## 2026-06-05 Continuation: Keep Runtime World Selection on Castle

The runtime world-selection bring-up no longer forces the fallback `test`
world at index 12 when the selected-world global is still empty. The normal
runtime selected-pointer repair now fills `8016c13c` from fallback world entry
index 0:

```text
[GAUNTDL:FIX] world-selected-pointer pc=ffffffff800e3dec selected=ffffffff802e1000 id=00000001 name=castle
```

The later `8004f29c` world-selection branch repair was aligned to the same
index, and the narrow test-world static-data linker is now guarded so it only
runs when the current selected entry is empty or already points at the test
fallback entry. This prevents the old test-WAD static-data repair from
re-entering after castle has been selected.

Added a separate opt-in trace,
`EUTHERDRIVE_GAUNTDL_TRACE_WORLD_VALIDITY=1`, for the later validity helper at
`800ceb28..800ceb78`. This keeps it separate from the older
`TRACE_WORLD_DATA_FLAGS` descriptor scan.

Build verification:

```text
dotnet build tools/GauntletProbe/GauntletProbe.csproj -c Release --no-restore /clp:ErrorsOnly
Build succeeded.
340 Warning(s)
0 Error(s)
```

Fresh castle warm snapshot:

```text
/tmp/eutherdrive-gauntlet-probe/gauntdl-gauntdl24-castle-f180-s200000.warm
```

Probe results from that snapshot:

```text
frame=300
pc=0xffffffff8004eb08
frameHash=0xbd71006f
drawPackets=1024 directTriangles=2818 setupTriangles=1394
texWrites=6203075 fastFills=2580 swaps=9926
framebuffer=640x480 nonBlack=307200 colored=443
```

The old post-300 log line is gone:

```text
[GAUNTDL:FIX] world-static-data-link ...
```

Longer 600-frame sanity remains stable but reaches a new plateau:

```text
frame=600
pc=0xffffffff800ceb50
frameHash=0x65d7fd5d
drawPackets=1024 directTriangles=2818 setupTriangles=1394
texWrites=6203075 fastFills=2580 swaps=110485
framebuffer=640x480 nonBlack=480 colored=0
```

Interpretation:

- The selected world is now consistently `castle`, not `test`.
- The accidental test static-data relink was a real blocker and is now stopped.
- Rendering does advance past the earlier all-white/test fallback state, but
  draw and texture counters stop growing after the 300-frame region.
- A 360-frame validity trace shows the later helper is repeatedly called with
  `selected=802e1000`, `valid0=ffffffff`, `valid88=ffffffff`, and
  `mark38=01fffc06`, so the previously empty validity bitsets are not the
  current hard blocker.
- The next target is back on BGLoadModel source population: the descriptor
  parser still reports asset entries based on `static_lr` and empty names after
  index 0, even on the castle-selected path.

Follow-up instrumentation extended
`EUTHERDRIVE_GAUNTDL_TRACE_BGLOADMODEL_ASSET_PARSER=1` with candidate
source-writer points around `800aac10`, `800aac18`, and `800aac24`. A fresh
180-frame trace showed those writer labels are not the producer for the early
repeated source values in this path. Instead, the parser reaches `800aac48`
directly with caller state already set:

```text
slot 1..8: s2=802e1718 source=802e1718 asset=<empty>
slot 9:    s2=80312a08 source=80312a08 asset=credits
```

So the next source-side target moved one caller level earlier than
`800aac48`: find why the BGLoadModel caller chooses `802e1718` for slots 1..8
instead of distinct hydrated asset descriptors.

Follow-up trace extended the same opt-in parser trace with caller-side source
selection points around `800aad40..800aae98`. A fresh 180-frame run confirms the
collapse happens before the parser consumes `802529a0`:

```text
caller-source-table-loaded index=1 slot=802529a4:00000000 s2=8015a92c s5=1
caller-source-selected     index=1 slot=802529a4:00000000 s2=8015a92c
caller-after-path-lookup   index=1 v0=802e1780 s2=802e1718 s5=1

caller-source-table-loaded index=7 slot=802529bc:802e1718 s2=8015a938 s5=0
caller-source-table-store  index=7 slot=802529bc:802e1718 s2=8015a938
caller-after-path-lookup   index=7 v0=802e1780 s2=802e1718 s5=0

caller-source-table-loaded index=8 slot=802529c0:00000000 s2=8015a938 s5=1
caller-after-path-lookup   index=8 v0=802e1780 s2=802e1718
```

Slot 9 is the first one that does not collapse to the `static_lr` payload base:

```text
caller-after-path-lookup index=9 s2=80312a08
parser source=80312a08 asset=credits
```

Interpretation:

- `800aac48` is still only the parser/consumer.
- The earlier caller seeds from static path/name pointers such as `8015a92c`
  and `8015a938`, then a lookup returns through `v0=802e1780` while final `s2`
  becomes `802e1718` for slots 1..8.
- The next useful target is the helper chain called from
  `800aad40..800aae60`, especially the calls to `800b72fc` and `800c9088`, not
  another mask at the parser entry.

Follow-up helper trace added `bgloadmodel-lookup-helper` logging under the same
`EUTHERDRIVE_GAUNTDL_TRACE_BGLOADMODEL_ASSET_PARSER=1` gate. The important new
data is that slots 1..8 are built from empty path slots before the lookup:

```text
runtime-string-copy dst=8024f9b0 src=80129c50 len=9  -> "static_lr"
runtime-string-copy dst=8024f9e0 src=80166370 len=0
runtime-string-copy dst=8024fa10 src=80166370 len=0
...

8024f9b0: "static_lr" ... ptr=802e1718
8024f9e0: ""          ... ptr=802e1718
8024fa10: ""          ... ptr=802e1718
...
8024fb50: "credits"   ... ptr=80312a08
```

The static table around `8015a92c` points into runtime strings such as
`GRE/RED/BLU/YEL`, `LEFTHAND`, and inventory words, not the missing texture or
model descriptor names. A gated experiment,
`EUTHERDRIVE_GAUNTDL_FIX_RUNTIME_BGLOADMODEL_ASSET_NAMES=1`, fills empty
asset-table names from the existing indexed texture payload table
(`gei/snm/stk/kjh/pnk/...`). It keeps the names visible after each parser pass,
but a 300-frame check still reaches the same castle baseline:

```text
pc=0xffffffff8004eb08
frameHash=0xbd71006f
drawPackets=1024 directTriangles=2818 setupTriangles=1394
framebuffer colored=443
```

Keep that experiment off by default for now. The next concrete target remains
the real path/name source that should replace the empty `80166370` copy source,
or the caller branch that decides to reuse `802e1718` for slots 1..8.

The filtered helper trace now catches the exact `800aaddc -> 800c9088` call.
For the empty model slots:

```text
slot 1:
  lookup-entry a0=8024f9e0("") a1=80166371("") s2=8015a92c
  after-path-lookup v0=802e1780 s2=802e1718

slot 8:
  lookup-entry a0=8024fb30("") a1=80166371("") s2=8015a938
  after-path-lookup v0=802e1780 s2=802e1718
```

For the first healthy non-static slot:

```text
slot 9:
  lookup-entry a0=8024fb60("credits") a1=8013588c("credits_font")
  after-path-lookup v0=80312a70 s2=80312a08
```

So the strongest next target is no longer generic lookup behavior: it is the
caller-side population of the name argument (`a1`) and path slot (`a0`) before
`800c9088`. Healthy slot 9 has both strings populated; slots 1..8 pass empty
strings and then legitimately fall back to `802e1718`.

Address-load scans at frame 180 found the key references:

```text
80166370 refs: 8001db68, 8001dc74, 8001dcf0, 8001de4c,
               8001e140..8001e5d8
8013588c refs: 80083d38, 80083d50
```

The `8001db40` cluster is the more relevant source path. It seeds `s0` with
`80166370`, indexes the `8016a92c` pointer table by a caller-provided index,
loads a second pointer from the `8016aa80`/`80227b2c` path, and calls the string
format/copy helper at `8011f3c0`. Later branches around `8001e540` use
`8016a93c` and still fall back to `80166370` when the expected table entry is
empty. The `80083d00` cluster is the explicit healthy credits setup path using
`8013588c("credits_font")`.

Next useful trace/fix target: instrument the `8001db40..8001e5d8` cluster with
the same slot index and output path/name registers. This should identify why the
slot 1..8 table lookup falls back to `80166370` while slot 9 reaches the
credits-specific setup.

A follow-up CPU range trace over `8001db40..8001e650` did not hit during the
active 180-frame castle path, so the address-load refs are static xrefs rather
than the live source of the current empty slots.

Added another gated experiment:

```text
EUTHERDRIVE_GAUNTDL_FIX_RUNTIME_BGLOADMODEL_DISTINCT_SOURCES=1
```

It rewrites BGLoadModel source-table slots 1..8 from repeated `802e1718` to the
same per-index destination layout used by the QIO metadata/hydration experiments:

```text
slot 1 -> 802e3718
slot 2 -> 802e5718
...
slot 8 -> 802f1718
```

This successfully changes `802529a0`, but a 300-frame dump shows all sampled
per-index buffers remain zero:

```text
802e3718: 00 00 00 ...
802e5718: 00 00 00 ...
802e7718: 00 00 00 ...
802f1718: 00 00 00 ...

pc=0xffffffff8004eb08
frameHash=0xbd71006f
drawPackets=1024 directTriangles=2818 setupTriangles=1394
framebuffer colored=443
```

Keep this experiment off by default. The next target should be the hydration or
file-read path for those per-index destinations; merely pointing the source
table at the expected stride is not enough because the backing buffers are
never populated in the current run.

## 2026-06-05 Continuation: BGLoadModel Asset Pointer Bias

Added a narrow `ApplyKnownRuntimeBgLoadModelAssetPointerNormalize()` repair in
`GauntletDarkLegacyAdapter.cs`, gated by
`EUTHERDRIVE_GAUNTDL_FIX_RUNTIME_BGLOADMODEL_ASSET_POINTER_NORMALIZE` and
therefore included in `EUTHERDRIVE_GAUNTDL_BRINGUP_FAST=1`.

Observed problem:

```text
8024f9a0 asset table entry 0 before repair:
  word0 = bfae1718, name = static_lr

bfae1718 - 3f800000 = 802e1718
```

That is exactly the hydrated static asset payload base plus the `1.0f` bit
pattern. The repair only runs at the observed BGLoadModel parser point
`800aacb4`, scans the `8024f9a0 + index * 0x30` asset descriptor table, and
normalizes a biased pointer only when `raw - 0x3f800000` lands in main RAM.

Verification:

```text
dotnet build tools/GauntletProbe/GauntletProbe.csproj -c Release --no-restore /clp:ErrorsOnly
Build succeeded.
339 Warning(s)
0 Error(s)
```

420-frame table dump from the standard 180-frame warm snapshot now ends with:

```text
bytes[0xffffffff8024f9a0]:
  +0x000: 18 17 2e 80 ... static_lr
  +0x030: 18 17 2e 80 ...
  +0x060: 18 17 2e 80 ...
```

600-frame sanity is stable but not a visual breakthrough:

```text
frame=600
pc=0xffffffff800c7a40
frameHash=0x9ac85dc5
drawPackets=0 directTriangles=1873 setupTriangles=897
lfbWrites=567045313 texWrites=6322435
framebuffer=640x480 nonBlack=307200 colored=0
```

Interpretation:

- The `0x3f800000` asset-pointer bias is real and now repaired.
- The game still reaches the same late diagnostic/progress helper and still
  renders a solid white frame.
- The next concrete target is filling the empty BGLoadModel asset descriptors:
  the missing-texture caller still reports `key=<empty>` and asset slots after
  `static_lr` still carry empty names/metadata even after the pointer is
  normalized.

Follow-up code dump at 260 frames narrowed the parser source:

```text
800aac90..800aacb4:
  assetEntry = 8024f9a0 + index * 0x30
  source = a1
  assetEntry[0] = source + lw(source + 0x5c)
```

For the observed first descriptor, `source == 802e1718` and
`lw(source + 0x5c) == 3f800000`, so the biased `bfae1718` is produced by the
guest parser itself. The value at `source+0x5c` is part of the hydrated payload's
float/identity data, not a plausible table offset. The next useful fix is
therefore to identify the correct descriptor base or record stride feeding
`800aac90`, rather than adding more pointer-bias masks.

Continuation on the same trail added opt-in parser instrumentation:

- `EUTHERDRIVE_GAUNTDL_TRACE_BGLOADMODEL_ASSET_PARSER=1`
- Logs `800aac48`, `800aac80`, `800aac90`, `800aacb4`, `800aacd0`,
  and `800aaea8`.
- Includes caller registers, source words at descriptor offsets, the selector
  entry around `v1`, and the current `8024f9a0` asset table slice.

Key 240-frame trace result from
`gauntdl-gauntdl24-fast-raw-f180-s200000-679903a27884.warm`:

```text
index 0 selector=802e1718/00000000/802e1718/00000000 source=802e1718
index 1 selector=802e1718/00000000/802e1718/00000000 source=802e1718
...
index 8 selector=802e1718/00000000/80312a08/00000000 source=802e1718
index 9 selector=80312998/00000000/00000000/00000000 source=80312998
index 10 selector=80332998/00000000/00000000/00000000 source=80332998
```

Follow-up code dump corrected one detail: `802551d0` is parser output, not the
source list. `800aac48` reads `source = *(802529a0 + index * 4)`, derives the
two selector words from `source + 0x58/0x50/0x60`, and writes them to
`802551d0 + index * 8`.

At frame 181 the relevant data tables are already:

```text
802529a0: 802e1718 802e1718 802e1718 802e1718
802529b0: 802e1718 802e1718 802e1718 802e1718
802529c0: 802e1718 80312a08 00000000 00000000

802551d0: 802e1718 00000000 802e1718 00000000 ...
80255210: 802e1718 00000000 80312a08 00000000
```

So `800aac48` is faithfully parsing the `802529a0` source table. If the empty
asset slots are wrong, the next source-side target is the writer around
`800aabfc..800aac20`, where the game stores `s2` into `802529a0 + index * 4`
and `s0` into `802549a0 + index * 4`. The visual state remains
pre-boot-gameplay: at 240 frames the final buffer has `nonBlack=17944`,
`colored=0`, with `directTriangles=1873`, `setupTriangles=897`, and no type-3
FIFO draw packets.

## 2026-05-25 Gauntlet Dark Legacy Bring-Up Pass

This pass moved the Gauntlet path past the early cold-boot terminal loops and into the loaded game runtime's `Loading Game.` screen.

Changed files from this pass:

- `EutherDrive.Core/Arcade/Vegas/GauntletDarkLegacyAdapter.cs`
- `docs/gauntlet-dark-legacy-vegas-handoff.md`

Implemented/verified:

- Added a neutral CS5/A170 window profile for the checksum probe at `0xffffffff80015d9c`; the first `0x30` bytes now read as zero instead of unmapped `0xff`, avoiding the early checksum error path.
- Added a narrow fastpath for the post-return `break` at `0xffffffff80016eec`. This helper is immediately after a `jr ra`/`sb zero,a1007000` sequence; if execution lands on the alignment/trap word, it now verifies the signature and resumes at `ra`.
- Fixed the boot-ELF read fastpath at `0xffffffff8001594c` for the observed `gauntdl24` prologue and enabled it under `EUTHERDRIVE_GAUNTDL_BRINGUP_FAST`.
- Removed the over-strict unsigned argument rejection in that boot-ELF fastpath. The MIPS runtime can pass sign-extended 32-bit values; the fastpath now casts the observed value instead of rejecting it before disk read.

Build command used:

```sh
dotnet build tools/GauntletProbe/GauntletProbe.csproj -c Release --no-restore /clp:ErrorsOnly
```

Result:

```text
Build succeeded.
337 warnings, 0 errors.
```

Key progression:

- Before the CS5 fix, cold boot hit repeated `special 0d` halts at `0xffffffff80016eec`.
- After the `80016eec` fastpath, cold boot reached `0xffffffff80015ebc`, but was stuck in a boot-open error loop.
- After enabling/fixing the `8001594c` boot-ELF fastpath, probe logs confirmed:

```text
[GAUNTDL:RD0] boot-elf-read pc=ffffffff8001594c lba=000000a7 dest=ffffffff802e73b0 bytes=00165d5c
```

Latest useful probe:

```sh
EUTHERDRIVE_GAUNTDL_BRINGUP_FAST=1 \
EUTHERDRIVE_GAUNTDL_CONTINUE_AFTER_UNSUPPORTED=1 \
EUTHERDRIVE_GAUNTDL_LOAD_WARMUP=0 \
EUTHERDRIVE_GAUNTDL_SAVE_WARMUP=1 \
EUTHERDRIVE_GAUNTDL_WARMUP_FRAMES=180 \
EUTHERDRIVE_GAUNTDL_WARMUP_STATE=/tmp/eutherdrive-gauntlet-probe/gauntdl-current-cs5-elf3-clean-f180.warm \
EUTHERDRIVE_GAUNTDL_CPU_STEPS_PER_FRAME=1000000 \
EUTHERDRIVE_GAUNTDL_PROFILE_HOT_PCS=1 \
dotnet run --project tools/GauntletProbe/GauntletProbe.csproj -c Release --no-build -- \
  /home/nichlas/roms/MAME/Midway/Vegas/gauntd 360
```

Latest observed status:

```text
frame=360
pc=0xffffffff801202dc
rtxt=16@0xffffffff800e30a0/ra=0xffffffff800e33e4 "Loading Game."
voodoo active, fifoWords=326850, fifoPackets=110887, fastFills=674, swaps=4762
drawPackets=0, directTriangles=30, setupTriangles=0
packetTypes=0:3,1:40893,2:0,3:0,4:69991,5:0,6:0,7:0
framebuffer=640x480 nonBlack=307200 colored=0
dcs boot=128w host=10 fifo=512/0 xfer=0 state=0/0 type=0000 left=0 lc=0400 out=000a
adsp pc=0079 ppc=0079 irq2=1/1
```

Interpretation:

- The adapter now reaches the loaded game runtime and displays/maintains the `Loading Game.` path.
- Voodoo is active and swapping; output is still fill/LFB-heavy and monochrome, with no setup/triangle packets yet.
- The active next blocker is likely the loaded runtime's asset/sound-load dependency. DCS remains in boot idle (`xfer=0`), so no real ADSP program upload has started.
- A traced cold run showed transient RD0 home-block repair warnings after the ELF load, but the clean 360-frame run continues through them to `Loading Game.`.

## 2026-05-25 Follow-up: Loading Game Runtime Hotspots

Follow-up work continued from the clean 180-frame warm snapshot:

```text
/tmp/eutherdrive-gauntlet-probe/gauntdl-current-cs5-elf3-clean-f180.warm
```

Implemented in `EutherDrive.Core/Arcade/Vegas/GauntletDarkLegacyAdapter.cs`:

- Added `TryFastPathKnownRuntimeStackRecordCopy` for the hot record-copy loop at
  `0xffffffff800b1edc..800b1f04`.
  - Signature-gated to the observed loop body.
  - Limited to small aligned main-RAM ranges.
  - This removed the previous `800b1e*` block-copy cluster from the top hot-PC list.
- Added `TryFastPathKnownRuntimeByteMove` for the small byte move helper at
  `0xffffffff8011df40`.
  - Handles the entry case and the observed forward pre-loop state.
  - Signature-gated and bounded to main RAM.
- Added prologue/epilogue fastpaths for the `MBOX_BGLoadModel` dispatcher at
  `0xffffffff8011d590` and `0xffffffff8011d9f4`.
  - These only collapse register save/restore sequences; callback/body side
    effects are still executed normally.

Verification:

```text
dotnet build tools/GauntletProbe/GauntletProbe.csproj -c Release --no-restore /clp:ErrorsOnly
Build succeeded.
338 Warning(s)
0 Error(s)
```

Latest warm-snapshot profile, 180 -> 240 frames:

```text
frame=240
pc=0xffffffff8011ce7c
voodoo active fifoWords=295266 fifoPackets=95095 fastFills=674 swaps=814
drawPackets=0 directTriangles=30 setupTriangles=0
packetTypes=0:3,1:25101,2:0,3:0,4:69991,5:0,6:0,7:0
framebuffer=640x480 nonBlack=307200 colored=0
dcs boot=128w host=0 fifo=0/0 xfer=0 state=0/0 type=0000 left=0 lc=0c00 out=000a
```

Longer warm-snapshot profile, 180 -> 300 frames:

```text
frame=300
pc=0xffffffff800ac4f0
voodoo active fifoWords=314194 fifoPackets=104559 fastFills=674 swaps=3180
drawPackets=0 directTriangles=30 setupTriangles=0
packetTypes=0:3,1:34565,2:0,3:0,4:69991,5:0,6:0,7:0
framebuffer=640x480 nonBlack=307200 colored=0
```

Interpretation:

- The loaded runtime is progressing through `MBOX_BGLoadModel`, not sitting in a
  pure idle wait.
- The remaining top hot PCs are dominated by the `8011d590` dispatcher body and
  its first status branches. `EUTHERDRIVE_GAUNTDL_PROFILE_HOT_PCS=1` counts PCs
  before fastpaths run, so entry/epilogue PCs can still appear even when their
  save/restore bodies are collapsed.
- Output is still fill/LFB-heavy; no type-3/type-5 geometry packets or setup
  triangles have appeared yet.

Next target:

1. Inspect the `8011ce7c -> 8011d590 -> MBOX_BGLoadModel` caller path and the
   message records on the stack around `807ffc00..807ffe00`.
2. Avoid skipping the `MBOX_BGLoadModel` dispatcher wholesale; it is doing real
   asset-load work and advancing Voodoo FIFO state.
3. If optimizing further, target narrow helpers/callbacks called from the
   dispatcher rather than the dispatcher body itself.

Additional runtime/FSYS inspection from the same warm snapshot:

- Frame 240 stops at `0xffffffff8011ce7c`, the wrapper immediately before
  calling the `MBOX_BGLoadModel` dispatcher with a stack message record at
  `807ffc00`.
- The record contains `ResetModels Timeout  Timeout, state=5`; heap copies also
  include `MBOX_BGLoadModelDone Timeout, state=5`.
- FSYS/QIO objects around `802954b0` include active state-4 records with zero
  status, plus mount status `0x300b` on `80295670`.
- A trial status-only queue completion for those state-4 records did not change
  the 180 -> 240 or 180 -> 360 signatures, so it was not kept. The remaining
  blocker is likely missing model/FSYS read data or the real completion callback,
  not just an unmasked QIO status.
- The timeout text itself is copied through a runtime log-ring path. Address-load
  scans for `802171b8` hit `800c80fc`, `800c8118`, and `800c8404`; that code
  maintains the log ring at `802171b8`, not the root model-load state.
- The adjacent `size:2147483637 max:110` string has a static copy at
  `8013d8fb` and a heap/log copy at `802171e0`. Direct address-load scans for
  the static copy did not find a simple lui/addiu reference, so this likely needs
  callback/dataflow tracing rather than a direct string xref.
- Relevant FSYS callbacks seen in active records:
  `800d2bcc`, `800d3b64`, and the larger state machine at `800f1ad4`.

Longer warm-snapshot check, 180 -> 360 frames:

```text
frame=360
pc=0xffffffff8011ce5c
voodoo active fifoWords=346338 fifoPackets=120631 fastFills=674 swaps=7198
drawPackets=0 directTriangles=30 setupTriangles=0
packetTypes=0:4,1:50636,2:0,3:0,4:69991,5:0,6:0,7:0
framebuffer=640x480 nonBlack=307200 colored=0
```

## 2026-06-05 Follow-up: BGLoadModel QIO Poll Progression

This pass promoted the narrow BGLoadModel QIO poll helper at
`0xffffffff800abaa0..800abbc0` from the broad experimental skip bucket to a
normal bring-up fix:

- Added `EUTHERDRIVE_GAUNTDL_FIX_RUNTIME_BGLOADMODEL_QIO_POLL`.
- It is enabled by `EUTHERDRIVE_GAUNTDL_BRINGUP_FAST=1`.
- The broader `EUTHERDRIVE_GAUNTDL_FASTPATH_RUNTIME_BGLOADMODEL_EXPERIMENTAL`
  remains opt-in for unrelated risky skips.

Why:

- Baseline 180 -> 240 was burning almost all CPU in `800abaa0..800abd28` and
  still reported the diagnostic text buffer as `LoadLightMaps: Timeout`.
- Enabling only the QIO poll behavior moved execution into world-data and
  render setup work, producing real Voodoo geometry activity.

Verification:

```sh
dotnet build tools/GauntletProbe/GauntletProbe.csproj -c Release --no-restore /clp:ErrorsOnly
```

Result:

```text
Build succeeded.
340 warnings, 0 errors.
```

New warm snapshot:

```text
/tmp/eutherdrive-gauntlet-probe/gauntdl-gauntdl24-fast-raw-f180-s200000-08bdce9c387f.warm
```

Useful 300-frame cold check with only `BRINGUP_FAST`:

```text
frame=300
pc=0xffffffff800f0c24
rtxt=16@0xffffffff800e30a0/ra=0xffffffff800e33e4 "Loading Game."
voodoo active, fifoWords=6893391, fifoPackets=251474
drawPackets=1024, directTriangles=2818, setupTriangles=1394
packetTypes=0:2370,1:38376,2:0,3:1024,4:112778,5:96923,6:0,7:3
texture writes=6203075
framebuffer=640x480 nonBlack=307200 colored=443
```

Useful 180 -> 360 warm check:

```text
frame=360
pc=0xffffffff800b1bdc
voodoo active, fifoWords=6921929, fifoPackets=260931
drawPackets=1024, directTriangles=2818, setupTriangles=1394
packetTypes=0:2370,1:41417,2:0,3:1024,4:119194,5:96923,6:0,7:3
textured triangles=1049 covered=1024 rejected=25
texture writes=6203075
framebuffer=640x480 nonBlack=307200 colored=0
```

Current interpretation:

- The old `LoadLightMaps: Timeout` QIO-poll stall is bypassed under the normal
  bring-up flag.
- The runtime now loads/hydrates the fallback world table and reaches Voodoo
  setup/draw packets instead of fill-only output.
- The next blocker appears around `800b119c..800b11dc`, `800b1bdc`, and
  `800b1dc4`. Dumps show the fallback world table at `802e1000` contains
  `castle`, `mount`, `desert`, `forest`, `temple`, `hell`, and `town`, while
  the runtime is also pumping menu/object diagnostic text through `802944c0`.
- DCS is still boot-idle (`xfer=0`), so audio upload remains a later blocker.

Next target:

1. Disassemble/trace the `800b119c..800b11dc` loop and the branch at
   `800b1bdc`; it may be a per-object/world scan or state wait.
2. Use the warm snapshot above and dump `802e1000`, `802e1718`, `80262b80`,
   and `807ffc00` when comparing future changes.
3. Keep the QIO poll fastpath narrow; do not enable the whole experimental
   BGLoadModel skip set by default.

Same-day continuation:

- Promoted the world-selection repair behind
  `EUTHERDRIVE_GAUNTDL_FIX_RUNTIME_WORLD_SELECTION` into the normal
  `EUTHERDRIVE_GAUNTDL_BRINGUP_FAST=1` set.
- With only `BRINGUP_FAST`, the runtime now logs:

```text
[GAUNTDL:FIX] world-selected-static-data-link pc=ffffffff800e3dec entry=ffffffff8015c104 data=ffffffff81000000 bytes=358 first=000002f8 count=00000008
[GAUNTDL:FIX] world-selected-pointer pc=ffffffff800e3dec selected=ffffffff802e1600 id=0000000d name=test
```

Verification build:

```text
Build succeeded.
458 warnings, 0 errors.
```

New warm snapshot from this promoted set:

```text
/tmp/eutherdrive-gauntlet-probe/gauntdl-gauntdl24-fast-raw-f180-s200000-679903a27884.warm
```

Useful cold 420-frame check with only `BRINGUP_FAST`:

```text
frame=420
pc=0xffffffff8004cb0c
rtxt=16@0xffffffff800e30a0/ra=0xffffffff800e33e4 "Loading Game."
voodoo active, fifoWords=6932591, fifoPackets=233969
drawPackets=0, directTriangles=1873, setupTriangles=897
packetTypes=0:5433,1:32352,2:0,3:0,4:97393,5:98789,6:0,7:2
lfbWrites=280888513
texture writes=6322435
framebuffer=640x480 nonBlack=307200 colored=0
```

Useful 180 -> 600 warm profile from the new snapshot:

```text
frame=600
pc=0xffffffff8004c954
voodoo active, fifoWords=7016105, fifoPackets=259573
drawPackets=0, directTriangles=1873, setupTriangles=897
packetTypes=0:5442,1:36393,2:0,3:0,4:118940,5:98789,6:0,7:9
lfbWrites=487787713
texture writes=6322435
hotpcs=0xffffffff800b1dc4:251600,0xffffffff80019924..80019960:160576 each
```

Current interpretation:

- `800b1dc4` is the existing global `8022808c` exchange helper counted before
  its fastpath returns; it is not the next stall.
- The new warm hotspot is the diagnostic/menu bit-scan loop at
  `80019924..80019a14`, entered from `ra=8001a270`.
- Trace shows the loop walks 13 small entries, building pointers from
  `8012a12c + t4`, reading status at `+0x10`, and advancing
  `t0/t3/t4` by `1/8/0xc8`.
- The table at `80165fc0` is small `0/1/2` state data, and the visible
  stopped state near frame 238 has `t0=12`, `t3=80165fe8`, `t4=0x960`,
  `s2=80165fe0`.
- This is not a pure wait/no-op: later paths can write per-entry counters at
  `s2+0` and `s2+4`, so any fastpath should emulate those writes or move up to
  a higher-level diagnostic overlay suppression with clear evidence.
- Testing the existing opt-in
  `EUTHERDRIVE_GAUNTDL_EXPERIMENT_RUNTIME_DIAGNOSTIC_OVERLAY_SUPPRESS=1` and
  `EUTHERDRIVE_GAUNTDL_EXPERIMENT_RUNTIME_DIAGNOSTIC_TEXT_PUMP_SKIP=1` did not
  remove this hotspot. The 180 -> 600 run ended at `pc=80019d34`, but
  `80019924..80019960` still dominated at `162656` hits each, with similar
  Voodoo counters (`fifoWords=7018483`, `fifoPackets=260271`, `tri=1873+897`,
  `lfbWrites=493778113`, `texWrites=6322435`).
- Added an opt-in exact-ish loop emulator under
  `EUTHERDRIVE_GAUNTDL_EXPERIMENT_RUNTIME_DIAGNOSTIC_MENU_SCAN=1`. It
  signaturizes `80019924..80019abc`, uses a local write overlay while walking
  the 13 entries, commits only when the scan reaches the normal `t1 == 0`
  epilogue, and bails out for the helper-call path.
- 180 -> 240 warm smoke with the experiment enabled logged hits such as
  `diagnostic-menu-scan ... writes=18 skipped=920 t0=13`, removed
  `80019924` from the hot list, and moved the PC to `800b36c8`. Counters rose
  to `fifoWords=6774225`, `fifoPackets=206364`, `tri=1799+861`,
  `lfbWrites=104092781`, `texWrites=6249603`.
- 180 -> 600 warm profile with the experiment enabled ended at `pc=800c7b20`.
  `80019924..80019960` was gone from top hot PCs. New top entries were
  `800b1dc4:280015`, `8011fab8:177322`, `800b1e54:176973`,
  `80121670/80121674:175464`, and `8011f7ac..8011f7e0:173818`. Voodoo counters:
  `fifoWords=7037367`, `fifoPackets=266088`, `tri=1873+897`,
  `lfbWrites=540472513`, `texWrites=6322435`.
- The diagnostic format-line wrapper at `80121670` now also fastpaths from the
  exact entry PC instead of requiring the first stack-adjust instruction to run.
  A 180 -> 600 warm profile with the menu-scan experiment ended at
  `pc=80102710`; `80121674` dropped out of the hot list, while `80121670`
  remains a frequent fastpath-entry at `175668` hits. Voodoo counters stayed in
  the same band: `fifoWords=7037557`, `fifoPackets=266147`, `tri=1873+897`,
  `lfbWrites=540933313`, `texWrites=6322435`.
- Added a second opt-in diagnostic experiment,
  `EUTHERDRIVE_GAUNTDL_EXPERIMENT_RUNTIME_DIAGNOSTIC_STATE_ZERO_MASK=1`, for
  the follow-on `80019d20..80019db8` bit scan only when the live mask at `t6`
  is zero. It signaturizes the loop, requires `a3 == t0 * 4`, writes zeroes to
  the remaining `t3+a3` and `s7+a3` slots, and falls back to normal CPU for any
  nonzero mask. With both diagnostic experiments enabled, 180 -> 240 warm smoke
  logged `diagnostic-state-zero-mask ... remaining=12` hits and kept Voodoo work
  healthy: `fifoWords=6832777`, `fifoPackets=207390`, `tri=1873+897`,
  `lfbWrites=104094913`, `texWrites=6306051`. The 180 -> 600 profile ended at
  `pc=800bd3b0`, `fifoWords=7039070`, `fifoPackets=266610`, `tri=1873+897`,
  `lfbWrites=544773313`, `texWrites=6322435`, with framebuffer `nonBlack=307200`.
- Traced `8011f7ac..8011f7e0` and identified it as a small NUL-terminated
  string-copy routine, not format parsing. Added `TryFastPathKnownRuntimeStringCopy`
  at `8011f7ac`; it copies through the terminating NUL, returns the original
  destination in `v0`, advances `a1`/`v1`, and accepts RAM or KSEG0/ROM sources
  with a 64K cap. With both diagnostic experiments enabled, 180 -> 240 warm
  smoke logged repeated `runtime-string-copy ... len=0` hits and stayed healthy:
  `fifoWords=6850301`, `fifoPackets=207838`, `tri=1873+897`,
  `lfbWrites=104094913`, `texWrites=6322435`. The 180 -> 600 profile ended at
  `pc=800a67bc`, `fifoWords=7042780`, `fifoPackets=267747`, `tri=1873+897`,
  `lfbWrites=553989313`, `texWrites=6322435`, with framebuffer `nonBlack=307200`.
- Traced the remaining `800b1e54/800b1e7c` render-record path and avoided the
  earlier broad active-record skip regression. Added
  `TryFastPathKnownRuntimeRenderRecordNullBody()` only for the proven
  `800b1e7c..800b1ea8` early-exit case where `lb 0(s2)` is zero; it emulates
  the prologue loads and jumps to the routine's own tail at `800b1fec`.
  180 -> 240 warm smoke reached `pc=800b24cc` with healthy counters:
  `fifoWords=6851045`, `fifoPackets=208066`, `tri=1873+897`,
  `lfbWrites=104094913`, `texWrites=6322435`. The 180 -> 600 profile improved
  to `pc=800c7a40`, `fps=2.39`, `fifoWords=7048093`, `fifoPackets=269375`,
  `tri=1873+897`, `lfbWrites=567045313`, `texWrites=6322435`, framebuffer
  `nonBlack=307200`.

Next target:

1. Decide whether the `80019924` menu-scan emulator is stable enough to promote
   into `BRINGUP_FAST`; for now it remains explicitly opt-in. The
   `80019d20` zero-mask helper should stay experiment-gated until a nonzero
   mask trace is understood.
2. Re-profile top hot PCs after the render-record null-body helper; likely next
   candidate is remaining diagnostic/text traffic around `80121670` or whatever
   replaces the `800b1e` path in the 600-frame hot list.
3. DCS remains boot-idle (`xfer=0`); keep audio upload as a later blocker.

## 2026-05-15 Native DCS Audio Bring-Up Pass

This pass started the real/native DCS audio path for Gauntlet Dark Legacy. The important outcome is that the adapter now has an actual audio buffer path and a MAME-shaped DCS/ADSP boot skeleton, but the game still has not reached the real DCS program upload.

Changed files from this pass:

- `EutherDrive.Core/Arcade/Vegas/GauntletDarkLegacyAdapter.cs`
- `tools/GauntletProbe/Program.cs`

Implemented/verified:

- `GauntletDarkLegacyAdapter.GetAudioBuffer()` now returns the machine DCS frame buffer instead of `ReadOnlySpan<short>.Empty`.
- `GauntletDarkLegacyMachine` loads `vegassio.bin` into the DCS audio device and runs the audio device each frame.
- `DcsAudioDevice` now decodes the boot ROM into ADSP program words.
- Added a first-pass native ADSP-2104 core with boot execution, IRQ2 latch/state, IMASK/ICNTL status, status stack, RTI handling, program/data memory read/write, DAG access, and basic ALU/shift support.
- DCS latch/status behavior now follows the relevant MAME shape more closely:
  - host input full reports `0x80`
  - output empty reports `0x40`
  - FIFO status contributes the expected `0x08/0x10/0x20` bits
- DCS stage-1 HLE transfer type `0` is now written as 24-bit ADSP program RAM, not incorrectly as 16-bit DRAM.
- DCS output latch now has a tiny queue so checksum followed by `000a` does not overwrite the checksum before the host reads it.
- `DebugStatus` now includes DCS transfer state, transfer type, words left, output queue depth, ADSP PC/PPC/ASTAT/MSTAT/IMASK/ICNTL/IRQ2, and step count.
- `tools/GauntletProbe` prints adapter debug status at the end of a run.

Build command used:

```sh
dotnet build tools/GauntletProbe/GauntletProbe.csproj -c Release --no-restore /p:AllowUnsafeBlocks=true
```

Result:

```text
Build succeeded.
334 warnings, 0 errors.
```

Latest useful probe:

```sh
env EUTHERDRIVE_GAUNTDL_BRINGUP_FAST=1 \
    EUTHERDRIVE_GAUNTDL_FASTPATH_DIAGNOSTIC_RUNTIME=1 \
    EUTHERDRIVE_GAUNTDL_CPU_STEPS_PER_FRAME=12000 \
    dotnet run --project tools/GauntletProbe/GauntletProbe.csproj -c Release --no-build -- \
      /home/nichlas/roms/MAME/Midway/Vegas/gauntd 12000
```

Observed status:

```text
frame=12000
pc=0xffffffff80042a70
dcs boot=128w host=10 fifo=3584/0 xfer=0 state=0/0 type=0000 left=0 lc=0c00 out=000a oq=0
adsp pc=0079 ppc=0079 astat=0002 mstat=0030 imask=0000 icntl=0000 irq2=0/0
```

Interpretation:

- The DCS native path is wired up and ready to receive the real downloaded sound program.
- ADSP `pc=0079` is the decoded boot ROM idle loop. This is expected until the host sends the DCS program transfer.
- `xfer=0` means no real DCS program/data transfer has happened yet.
- FIFO writes keep increasing in 512-word blocks, which appears to be the host repeating the DCS FIFO self-test/boot-control path rather than starting `001a/002a` program upload.
- DCS trace confirms only early host writes and sequential FIFO self-test traffic so far.

MAME references used:

- `/home/nichlas/mame/src/mame/shared/dcs.cpp`
- `/home/nichlas/mame/src/mame/shared/dcs.h`
- `/home/nichlas/mame/src/mame/midway/vegas.cpp`
- `/home/nichlas/mame/src/devices/cpu/adsp2100/adsp2100.cpp`
- `/home/nichlas/mame/src/devices/cpu/adsp2100/2100ops.hxx`
- `/home/nichlas/mame/src/devices/cpu/adsp2100/2100dasm.cpp`

Current blocker:

- Host/runtime is looping around the DCS/FIFO boot-control area, with PCs around `0xffffffff800428dc` and `0xffffffff80042a70`.
- IOASIC trace shows the early DCS register traffic and then repeated writes through the shuffled register path. No real stage-1 DCS program transfer is seen.
- Next work should focus on the host-side DCS boot/FIFO test condition and IOASIC sound status semantics, not on adding more ADSP opcodes yet.

Useful trace commands:

```sh
env EUTHERDRIVE_GAUNTDL_BRINGUP_FAST=1 \
    EUTHERDRIVE_GAUNTDL_FASTPATH_DIAGNOSTIC_RUNTIME=1 \
    EUTHERDRIVE_GAUNTDL_CPU_STEPS_PER_FRAME=100000 \
    EUTHERDRIVE_GAUNTDL_TRACE_IOASIC=1 \
    EUTHERDRIVE_GAUNTDL_TRACE_IOASIC_LIMIT=220 \
    dotnet run --project tools/GauntletProbe/GauntletProbe.csproj -c Release --no-build -- \
      /home/nichlas/roms/MAME/Midway/Vegas/gauntd 800
```

```sh
env EUTHERDRIVE_GAUNTDL_BRINGUP_FAST=1 \
    EUTHERDRIVE_GAUNTDL_FASTPATH_DIAGNOSTIC_RUNTIME=1 \
    EUTHERDRIVE_GAUNTDL_CPU_STEPS_PER_FRAME=12000 \
    EUTHERDRIVE_GAUNTDL_TRACE_DCS=1 \
    EUTHERDRIVE_GAUNTDL_TRACE_DCS_LIMIT=500 \
    dotnet run --project tools/GauntletProbe/GauntletProbe.csproj -c Release --no-build -- \
      /home/nichlas/roms/MAME/Midway/Vegas/gauntd 5000
```

Do not lose these details:

- The stage-1 DCS type-0 transfer fix is required for native sound. Without it, real downloaded ADSP code will be corrupted even if host-side boot proceeds.
- The output latch queue is required so MAME-style delayed checksum/`000a` behavior is not collapsed into a single visible word.
- The current ADSP core is still incomplete, but it is not the active blocker while `xfer=0`.

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

## 2026-05-14 Follow-up: Runtime State-init Entry

This pass extended the loaded Glide state-init fastpath to catch the function
entry at `0xffffffff80108fe0` when it is called with `a0=0`.

Context:

- The earlier fastpath only caught the same routine at tail
  `0xffffffff80109074`.
- Tracing from the 100M warmup endpoint showed the frame/event path repeatedly
  reaches `0xffffffff80108fe0` before the tail, so taking the entry is cheaper
  and still uses the same loaded state at `0xffffffff80262d64`.
- The entry fastpath is deliberately limited to `a0=0`; other selectors are
  not skipped until their state base and side effects are traced.

Clean verification in `/tmp/eutherdrive-gauntlet-verify`:

```text
dotnet build tools/GauntletProbe/GauntletProbe.csproj -c Release --no-restore /clp:ErrorsOnly
Build succeeded.
332 Warning(s)
0 Error(s)
```

Warmup-series after the entry fastpath:

```text
checkpoint extra=1000000 pc=0xffffffff800eb668 regs=3103669 fifoWords=6185124 fifoPackets=3090348
checkpoint extra=10000000 pc=0xffffffff800eb50c regs=3176397 fifoWords=6330580 fifoPackets=3163076
checkpoint extra=100000000 pc=0xffffffff800eb668 regs=3903669 fifoWords=7785124 fifoPackets=3890348
checkpoint extra=150000000 pc=0xffffffff800eb764 regs=4307709 fifoWords=8593204 fifoPackets=4294388
drawPackets=0 directTriangles=0 setupTriangles=0
packetTypes=0:0,1:4292993,2:0,3:0,4:1395,5:0,6:0,7:0
```

Interpretation:

- The fastpath fires and moves the long-run endpoints away from the previous
  `0xffffffff800e13e0` frame/event callback area.
- It still does not reach real geometry. The run remains type-1 state traffic
  plus type-4 clear/fill.
- The 150M endpoint at `0xffffffff800eb764` is the callsite into the bitfield
  helper at `0xffffffff800eafdc`; that helper already has an entry fastpath, so
  this endpoint is partly a checkpoint-budget artifact.

Next target:

1. Run slightly past `0xffffffff800eb764` or add a narrow callsite fastpath only
   if the budget artifact keeps dominating measurements.
2. More importantly, identify why the runtime remains in state/update traffic
   and never emits Voodoo setup/triangle packets.

## 2026-05-14 Follow-up: Runtime Copy and Mid-packet Fastpaths

This pass added two narrow fastpaths in
`EutherDrive.Core/Arcade/Vegas/GauntletDarkLegacyAdapter.cs` to speed warm
bringup probes from the existing 300-frame snapshot.

New code:

- Added `TryFastPathKnownRuntimeAlignedQwordCopy`.
  - Catches the aligned qword copy path at `0xffffffff800d1370`.
  - Signature-gated against the exact helper body.
  - Only fires when source, destination, and byte count are 8-byte aligned, the
    copy is in main RAM, and `a3` still matches the original destination.
- Extended `TryFastPathKnownGauntletGlideTwoWordStatePacket`.
  - Now also catches the mid-body state-word point at `0xffffffff80102554`.
  - This avoids repeatedly stopping inside the same two-word type-1 Glide
    packet writer after the state word has already been computed.

Clean verification:

```text
dotnet build tools/GauntletProbe/GauntletProbe.csproj -c Release --no-restore /clp:ErrorsOnly
Build succeeded.
333 Warning(s)
0 Error(s)
```

Warmup-series after these fastpaths:

```text
checkpoint extra=151000010 pc=0xffffffff800e3e4c regs=4326988 fifoWords=8631762 fifoPackets=4313667
checkpoint extra=151000100 pc=0xffffffff80102704 regs=4326988 fifoWords=8631762 fifoPackets=4313667
checkpoint extra=151001000 pc=0xffffffff80104358 regs=4326996 fifoWords=8631778 fifoPackets=4313675
checkpoint extra=152000000 pc=0xffffffff800eb090 regs=4335141 fifoWords=8648068 fifoPackets=4321820
checkpoint extra=160000000 pc=0xffffffff800111d4 regs=4400381 fifoWords=8778548 fifoPackets=4387060
checkpoint extra=200000000 pc=0xffffffff80104388 regs=4726580 fifoWords=9430946 fifoPackets=4713259
drawPackets=0 directTriangles=0 setupTriangles=0
packetTypes=0:0,1:4711864,2:0,3:0,4:1395,5:0,6:0,7:0
framebuffer=640x480 stride=2560 nonBlack=151456 colored=21408
```

Interpretation:

- The previous warm endpoints around `0xffffffff800d1370` and
  `0xffffffff80102554` are no longer dominating the focused series.
- The renderer still receives only Voodoo type-1 state packets plus type-4
  clear/fill packets. There are still no type-3/type-5 draw/setup packets, so
  the UI image is still not real game geometry.
- `nonBlack`/`colored` confirms the placeholder/clear path still produces
  pixels, but the missing geometry is upstream of rasterization.

Next target:

1. Trace the now-hot loaded-runtime area around `0xffffffff80104358` /
   `0xffffffff80104388`.
2. Keep using the warmup snapshot and `EUTHERDRIVE_GAUNTDL_EXTRA_SERIES`; it is
   the fastest route to the no-geometry blocker.
3. Do not spend time on Voodoo triangle decode until a probe first shows
   non-zero setup/draw packet counters.

## 2026-05-14 Follow-up: Snapshot Mode Split and State Snapshot Copy

This pass fixed one probe workflow issue and added one narrow loaded-runtime
fastpath.

Probe change:

- `tools/GauntletProbe` auto warmup snapshots now include `fast/base` and
  `raw/chd` in the filename.
- This prevents accidentally loading a base/CHD 300-frame snapshot for the
  `EUTHERDRIVE_GAUNTDL_BRINGUP_FAST=1` + raw-disk workflow. The bad snapshot
  lands in idle/interrupt code around `0xffffffff80015784` with loaded runtime
  code ranges still zeroed.

New fastpath:

- Added `TryFastPathKnownGauntletGlideRuntimeStateSnapshotCopy`.
  - Catches the loaded runtime wrapper at `0xffffffff80108298` and budget-stop
    epilogue points through `0xffffffff801082f8`.
  - Signature-gated against the exact wrapper body.
  - Copies `0x108` bytes from loaded Glide state
    `0xffffffff80262d64+0x24c` to the caller-provided destination, matching the
    wrapper's call to the generic copy helper.

Verification:

```text
dotnet build tools/GauntletProbe/GauntletProbe.csproj -c Release --no-restore /clp:ErrorsOnly
Build succeeded.
332 Warning(s)
0 Error(s)
```

Correct warmup snapshot recreated with:

```text
EUTHERDRIVE_GAUNTDL_BRINGUP_FAST=1
EUTHERDRIVE_GAUNTDL_RAW_DISK=/home/nichlas/roms/MAME/Midway/Vegas/gauntd/gauntd24.raw
EUTHERDRIVE_GAUNTDL_WARMUP_STATE=auto
EUTHERDRIVE_GAUNTDL_WARMUP_FRAMES=300
```

Snapshot path:

```text
/tmp/eutherdrive-gauntlet-probe/gauntdl-gauntdl24-fast-raw-f300-s2000000-40cf7be0343c.warm
```

Warmup-series after the fastpath:

```text
checkpoint extra=1000000 pc=0xffffffff8010253c regs=3784835 fifoWords=7547456 fifoPackets=3771514
checkpoint extra=5000000 pc=0xffffffff800eb528 regs=3818517 fifoWords=7614820 fifoPackets=3805196
checkpoint extra=10000000 pc=0xffffffff800eb7ac regs=3860621 fifoWords=7699028 fifoPackets=3847300
checkpoint extra=25000000 pc=0xffffffff800e1464 regs=3986940 fifoWords=7951666 fifoPackets=3973619
drawPackets=0 directTriangles=0 setupTriangles=0
packetTypes=0:0,1:3972224,2:0,3:0,4:1395,5:0,6:0,7:0
```

Interpretation:

- The new fastpath is valid after correcting the entry-case stack behavior.
  The first attempt adjusted `sp` as if the prologue had already run and was
  rejected after probe verification hit invalid code/data PCs.
- The former 1M endpoint at `0xffffffff801082ec` no longer dominates; the
  25M endpoint is now `0xffffffff800e1464`.
- Voodoo still only sees type-1 state packets and type-4 clear/fill packets.
  No real geometry yet.

Next target:

1. Trace/dump around `0xffffffff800e1464` and caller `0xffffffff800e1460`.
2. Keep using the fast/raw warmup snapshot path above.
3. Continue looking for the first transition to type-3/type-5 draw/setup
   packets before spending time in triangle decode.

## 2026-05-14 Pause Handoff: Runtime Loop Progress, Still No Geometry

This pass kept the same fast/raw 300-frame warmup snapshot:

```text
/tmp/eutherdrive-gauntlet-probe/gauntdl-gauntdl24-fast-raw-f300-s2000000-40cf7be0343c.warm
```

Probe/build status before pausing:

```text
dotnet build tools/GauntletProbe/GauntletProbe.csproj -c Release --no-restore /clp:ErrorsOnly
Build succeeded.
333 Warning(s)
0 Error(s)
```

Implemented bringup changes:

- Corrected `TryFastPathKnownRuntimeDelayCallbackLoop`:
  - Real entry is `0xffffffff800e1414`, not `0xffffffff800e1420`.
  - Signature offsets were adjusted to the full prologue/body/epilogue.
  - Mid-loop stops now restore `s2/s1/s0/ra/sp`.
  - Callback is accepted as either zero or `0x800d03b8`.
  - This moved the hot stop from `0xffffffff800e1464` to the inline tick wait
    around `0xffffffff800e18ec`.
- Added `TryFastPathKnownRuntimeInlineTickWaitLoop`:
  - Covers `0xffffffff800e18c0..0xffffffff800e18f0`.
  - Advances counter `0xffffffff80228114` by `0xb4` and resumes at
    `0xffffffff800e18f8`.
  - This moved the hot stop into the QIO/error status tail around
    `0xffffffff800d0e40`.
- Added `TryFastPathKnownRuntimeQioErrorPollTail`:
  - Narrow catch at `0xffffffff800d0e40` when the pending branch returns to
    `0xffffffff800d0964`, with `s1=8` and `s5=0`.
  - Signature-gated around `0xffffffff800d0964/0968` and
    `0xffffffff800d0e34..0e48`.
  - Writes the expected `0xa1` status path and clears `s1`.
  - This moved the run forward into loaded-runtime text/state code around
    `0xffffffff800e38xx` / `0xffffffff800e3ffc`.
- Added `TryFastPathKnownRuntimeTextStateBlitBody`:
  - Current accepted range is `0xffffffff800e3500..0xffffffff800e388c`.
  - Restores saved `s0..s7/fp/ra`, advances `sp` by `0xa8`, and returns via
    the saved `ra`.
  - A broader attempted range starting at `0xffffffff800e3370` regressed the
    long probe back to `0xffffffff800d0968` with lower FIFO counts, so it was
    rejected and reverted.
- Added state emit/copy handling:
  - `TryFastPathKnownGauntletGlideRuntimeStateSnapshotCopy` copies the
    `0x108`-byte loaded Glide state snapshot.
  - `TryFastPathKnownGauntletGlideStateEmitCallerEpilogue` handles the caller
    epilogue at `0xffffffff80103f64`; single-step probing showed this point
    continues normally through `0xffffffff80103f68`, then to
    `0xffffffff801036a0` and `0xffffffff800eb870`.
- Diagnostic trace output now includes `s3..s7`, which was useful for the QIO
  and runtime state-loop probes.

Best long-run checkpoints from the accepted range still show no real draw
traffic:

```text
checkpoint extra=5000001 pc=0xffffffff800eb864 regs=3787119 fifoWords=7552024 fifoPackets=3773798
checkpoint extra=25000000 pc=0xffffffff800e30c4 regs=3820223 fifoWords=7618232 fifoPackets=3806902
checkpoint extra=100000000 pc=0xffffffff800d0ffc regs=3944383 fifoWords=7866552 fifoPackets=3931062
checkpoint extra=400000000 pc=0xffffffff80103f64 regs=4441005 fifoWords=8859796 fifoPackets=4427684
checkpoint extra=800000000 pc=0xffffffff800d0f5c regs=5103151 fifoWords=10184088 fifoPackets=5089830
drawPackets=0 directTriangles=0 setupTriangles=0
packetTypes=0:0,1:5088435,2:0,3:0,4:1395,5:0,6:0,7:0
```

Interpretation:

- The bringup made real progress through several loaded-runtime wait/status
  loops, but the Voodoo FIFO still only receives type-1 state packets and the
  same 1395 type-4 clear/fill packets.
- There are still no type-3 or type-5 packets, so the missing image is still
  upstream of Voodoo triangle decode.
- The current useful stop points for the next session are
  `0xffffffff800d0f5c`, `0xffffffff800e3fd8`, and `0xffffffff800e345c`.

Next target:

1. Trace the runtime state/text/IO loops around those three PCs.
2. Keep the text-state fastpath range at `0xffffffff800e3500..388c` unless a
   new trace proves an earlier entry is safe.
3. Continue looking for the first non-zero type-3/type-5 packet before doing
   any Voodoo raster work.

## 2026-05-24 Resume After Crash: Direct Triangles Are Still Self-Test Geometry

The repo was resumed after a machine crash with unrelated dirty files present.
Gauntlet-specific files were clean at start except for this new diagnostic
change.

Implemented:

- `EUTHERDRIVE_GAUNTDL_TRACE_VOODOO_DRAW=1` draw trace lines now include the
  current CPU PC from `CpuPcProvider`.

Verification:

```text
dotnet build tools/GauntletProbe/GauntletProbe.csproj -c Release --no-restore /clp:ErrorsOnly
Build succeeded.
337 Warning(s)
0 Error(s)
```

Warmup snapshot regenerated after the diagnostic build:

```text
/tmp/eutherdrive-gauntlet-probe/gauntdl-gauntdl24-fast-raw-f300-s0-945e4914bd0d.warm
```

Key probe result:

```text
EUTHERDRIVE_GAUNTDL_BRINGUP_FAST=1
EUTHERDRIVE_GAUNTDL_RAW_DISK=/home/nichlas/roms/MAME/Midway/Vegas/gauntd/gauntd24.raw
EUTHERDRIVE_GAUNTDL_WARMUP_STATE=auto
EUTHERDRIVE_GAUNTDL_WARMUP_FRAMES=300
EUTHERDRIVE_GAUNTDL_TRACE_VOODOO_DRAW=1
EUTHERDRIVE_GAUNTDL_TRACE_VOODOO_DRAW_LIMIT=90
```

All currently observed direct triangles come from one PC:

```text
[GAUNTDL:VOODOO-DRAW] fastfill ... pc=0xffffffff8005ed54
[GAUNTDL:VOODOO-DRAW] itri color=0xFFFF xy=(0.0,0.0)/(36.0,0.0)/(0.0,36.0) ... pc=0xffffffff8005ed54
[GAUNTDL:VOODOO-DRAW] itri color=0xF800 xy=(0.0,0.0)/(128.0,0.0)/(128.0,128.0) ... pc=0xffffffff8005ed54
```

Interpretation:

- The existing `directTriangles=68` are boot/self-test style diagnostic
  geometry from `0xffffffff8005ed54`, not real loaded-runtime game geometry.
- The warm 300-frame state still has no FIFO type-3/type-5 draw/setup packets:

```text
voodoo regs=64373 fifoWords=124741 fifoPackets=62369 drawPackets=0 directTriangles=68 setupTriangles=0 lfbWrites=440464 texWrites=16 fastFills=2 swaps=62352
voodoo packetTypes=0:1,1:62365,2:0,3:0,4:3,5:0,6:0,7:0
framebuffer=640x480 stride=2560 nonBlack=16641 colored=16641
```

Extra-step probing from the warm snapshot is dominated by a swap/status pump,
not by new draw emission:

```text
checkpoint extra=5000001 pc=0xffffffff8005171c regs=85121 fifoWords=166235 fifoPackets=83116 drawPackets=0 directTriangles=68 setupTriangles=0 fastFills=2 swaps=83098 packetTypes=0:2,1:83111,2:0,3:0,4:3,5:0,6:0,7:0
hotpcs=0xffffffff80054af0:31120,0xffffffff80054af4:31120,0xffffffff80054af8:31120,0xffffffff80054afc:31120,0xffffffff80054b00:31120,0xffffffff80054b04:31120,0xffffffff8001fa20:20748,...
```

Useful code windows dumped:

```text
mem[0xffffffff800516a0] includes the repeated swapbuffer FIFO write:
  ae710128 ... 8e020374 34630251 ac430000 ac510004 ... ae020374 ae03037c

mem[0xffffffff80054ae0] is a tiny status helper returning through
0xffffffff80054af0..54b04.

mem[0xffffffff80018370] is a vblank/status wait loop around the same status
bit path.
```

Next target:

1. Treat `directTriangles=68` as already-explained diagnostic geometry.
2. Investigate why loaded runtime repeatedly emits only swapbuffer type-1 FIFO
   packets from the `0xffffffff800516a0` path.
3. The next meaningful rendering milestone is still the first real runtime
   draw/setup packet or a new direct triangle PC other than
   `0xffffffff8005ed54`.

## 2026-05-24 Follow-up: Collapse Low Runtime Swap/Vblank Waits

Implemented in `EutherDrive.Core/Arcade/Vegas/GauntletDarkLegacyAdapter.cs`:

- Added `TryFastPathKnownRuntimeSwapbufferPump`.
  - Catches the low runtime swapbuffer pump body at
    `0xffffffff800516a0..0xffffffff80051744`.
  - Signature-gated against the exact helper body at
    `0xffffffff80051654`.
  - Restores `s0..s3/ra/sp` and returns to the caller without repeatedly
    emitting the same `swapbufferCMD` type-1 FIFO packet.
- Added `TryFastPathKnownRuntimeLowVblankStatusWait`.
  - Catches the low runtime vblank/status wait body at
    `0xffffffff80018370..0xffffffff80018420`.
  - Signature-gated against the exact helper body at
    `0xffffffff80018350`.
  - Restores `s0..s3/ra/sp` and returns the same no-counter-change result seen
    in the trace.

Verification:

```text
dotnet build tools/GauntletProbe/GauntletProbe.csproj -c Release --no-restore /clp:ErrorsOnly
Build succeeded.
337 Warning(s)
0 Error(s)
```

Probe command:

```text
EUTHERDRIVE_GAUNTDL_BRINGUP_FAST=1 \
EUTHERDRIVE_GAUNTDL_RAW_DISK=/home/nichlas/roms/MAME/Midway/Vegas/gauntd/gauntd24.raw \
EUTHERDRIVE_GAUNTDL_WARMUP_STATE=auto \
EUTHERDRIVE_GAUNTDL_WARMUP_FRAMES=300 \
EUTHERDRIVE_GAUNTDL_EXTRA_SERIES=1000000,5000000,25000000 \
EUTHERDRIVE_GAUNTDL_PROFILE_HOT_PCS=1 \
dotnet run --project tools/GauntletProbe/GauntletProbe.csproj -c Release --no-build -- \
  /home/nichlas/roms/MAME/Midway/Vegas/gauntd 300
```

New warmup snapshot:

```text
/tmp/eutherdrive-gauntlet-probe/gauntdl-gauntdl24-fast-raw-f300-s0-928173a0982a.warm
```

Progression after the two fastpaths:

```text
checkpoint extra=1000000 pc=0xffffffff8003aabc regs=2021 fifoWords=85067 fifoPackets=42532 drawPackets=0 directTriangles=68 setupTriangles=0 fastFills=2 swaps=0 packetTypes=0:1,1:42528,2:0,3:0,4:3,5:0,6:0,7:0
checkpoint extra=5000000 pc=0xffffffff80011280 regs=2021 fifoWords=106287 fifoPackets=53142 drawPackets=0 directTriangles=68 setupTriangles=0 fastFills=2 swaps=0 packetTypes=0:1,1:53138,2:0,3:0,4:3,5:0,6:0,7:0
checkpoint extra=25000000 pc=0xffffffff80016df4 regs=2021 fifoWords=212387 fifoPackets=106192 drawPackets=0 directTriangles=68 setupTriangles=0 fastFills=2 swaps=0 packetTypes=0:3,1:106186,2:0,3:0,4:3,5:0,6:0,7:0
```

Interpretation:

- The old `0xffffffff80051718` swapbuffer endpoint is gone.
- The old `0xffffffff8001839c` vblank/status wait endpoint is gone.
- `swaps` no longer grows during the extra-step series; the repeated
  swapbuffer packets were bringup wait noise.
- There is still no real runtime geometry: `drawPackets=0`,
  `setupTriangles=0`, and `directTriangles=68` remains the earlier
  self-test geometry from `0xffffffff8005ed54`.

Current blocker:

```text
pc=0xffffffff80016df4
lastOp=0x0c006556
```

Useful dump around the current blocker:

```text
mem[0xffffffff80016dc0]:
  +0x000: 3c11800a 3c12800a 0c019334 0200202d
  +0x010: 8e22371c 30420001 10400009 00000000
  +0x020: 0c00fb2b 00000000 14400005 00000000
  +0x030: 0c006556 0000202d 0c00fbe8 00000000
  +0x040: 8e22371c 30420002 10400003 00000000
  +0x050: 0c005a03 00000000 8e22371c 30420004
  +0x060: 50400006 26100001 8e443724 0000282d
  +0x070: 0c0144a7 3406ffff 26100001 1a00ffe2
  +0x080: 00000000 0c019334 0000202d 8fbf001c
```

Next target:

1. Inspect the scheduler/status loop at `0xffffffff80016dc0..80016e44`.
2. The immediate call at the endpoint is `0xffffffff80019558`
   (`0x0c006556`), with the next call at `0xffffffff8003efa0`
   (`0x0c00fbe8`).
3. Continue treating type-1-only FIFO growth as state/wait traffic until a
   new direct triangle PC or a type-3/type-5 packet appears.

## 2026-05-24 Follow-up: Remove Remaining Low Runtime FIFO/Status Noise

Implemented in `EutherDrive.Core/Arcade/Vegas/GauntletDarkLegacyAdapter.cs`:

- Added `TryFastPathKnownRuntimeDiagnosticDrawCallsite`.
  - Catches the `0xffffffff80016df0/80016df4` callsite for
    `0xffffffff80019558`.
  - Signature-gated against the caller at `0xffffffff80016dc0` and callee at
    `0xffffffff80019558`.
  - Important correction: caller epilog signature is at `+0x8c..+0xa0`, not
    `+0x90..+0xa4`.
- Added `TryFastPathKnownRuntimeLowVoodooStatusPump`.
  - Catches entry `0xffffffff80054b60`.
  - Skips the repeated FIFO type-1 status-register packet
    `cmd=0x00010241 target=0x048 value=0`.
  - Returns `v0=0` and writes the status latch at `0xffffffff800b97f4` to
    zero, matching the observed backend status bit.
  - Important correction: this is an entry fastpath, so it must use the
    current `$ra`; the prolog has not saved `ra`/`s0` to the stack yet.

Verification:

```text
dotnet build tools/GauntletProbe/GauntletProbe.csproj -c Release --no-restore /clp:ErrorsOnly
Build succeeded.
337 Warning(s)
0 Error(s)
```

Probe command:

```text
EUTHERDRIVE_GAUNTDL_BRINGUP_FAST=1 \
EUTHERDRIVE_GAUNTDL_RAW_DISK=/home/nichlas/roms/MAME/Midway/Vegas/gauntd/gauntd24.raw \
EUTHERDRIVE_GAUNTDL_WARMUP_STATE=auto \
EUTHERDRIVE_GAUNTDL_WARMUP_FRAMES=300 \
EUTHERDRIVE_GAUNTDL_EXTRA_SERIES=1000000,5000000,25000000,100000000 \
EUTHERDRIVE_GAUNTDL_PROFILE_HOT_PCS=1 \
EUTHERDRIVE_GAUNTDL_RECORD_VOODOO_EVENTS=1 \
EUTHERDRIVE_GAUNTDL_DUMP_VOODOO_EVENTS=1 \
dotnet run --project tools/GauntletProbe/GauntletProbe.csproj -c Release --no-build -- \
  /home/nichlas/roms/MAME/Midway/Vegas/gauntd 300
```

New warmup snapshot:

```text
/tmp/eutherdrive-gauntlet-probe/gauntdl-gauntdl24-fast-raw-f300-s0-b7d96bcb91d9.warm
```

Progression after these fastpaths:

```text
checkpoint extra=1000000 pc=0xffffffff800512c8 regs=2021 fifoWords=43 fifoPackets=20 drawPackets=0 directTriangles=68 setupTriangles=0 fastFills=2 swaps=0 packetTypes=0:0,1:17,2:0,3:0,4:3,5:0,6:0,7:0
checkpoint extra=5000000 pc=0xffffffff80051690 regs=2021 fifoWords=43 fifoPackets=20 drawPackets=0 directTriangles=68 setupTriangles=0 fastFills=2 swaps=0 packetTypes=0:0,1:17,2:0,3:0,4:3,5:0,6:0,7:0
checkpoint extra=25000000 pc=0xffffffff8001683c regs=2021 fifoWords=43 fifoPackets=20 drawPackets=0 directTriangles=68 setupTriangles=0 fastFills=2 swaps=0 packetTypes=0:0,1:17,2:0,3:0,4:3,5:0,6:0,7:0
checkpoint extra=100000000 pc=0xffffffff800179c0 regs=2021 fifoWords=43 fifoPackets=20 drawPackets=0 directTriangles=68 setupTriangles=0 fastFills=2 swaps=0 packetTypes=0:0,1:17,2:0,3:0,4:3,5:0,6:0,7:0
```

Interpretation:

- The old `0xffffffff80016df4` diagnostic draw callsite blocker is gone.
- The repeated low-runtime `0x00010241` type-1 FIFO/status pump is gone.
- `fifoWords` is now stable across the whole extra-step series (`43`), instead
  of climbing into hundreds of thousands.
- There is still no real runtime geometry: `drawPackets=0`,
  `setupTriangles=0`, and `directTriangles=68` is still the self-test geometry
  from `0xffffffff8005ed54`.

Current endpoint:

```text
pc=0xffffffff800179c0
lastOp=0x0c019334
hotpcs=0xffffffff80001a24:4884,0xffffffff80000fd8:1148,0xffffffff80041880:828,...
```

Useful dump around the endpoint:

```text
mem[0xffffffff80017980]:
  +0x000: 0000202d afbf001c afb20018 afb10014
  +0x010: 0c0065ce afb00010 0c0065ce 0040202d
  +0x020: 0c005e38 0000882d 3c048009 248499c4
  +0x030: 0c0059dd 24050002 0040902d 0c019334
  +0x040: 0220202d 1a40000a 0000802d 0000202d
  +0x050: 0080282d 0c0144a7 0080302d 0c005a03
  +0x060: 26100001 0212102a 1440fff9 0000202d
  +0x070: 26310001 1a20fff1 00000000 0c019334
  +0x080: 0000202d 0c00fc0c 00000000 0c0152d8
  +0x090: 00000000 1440fffd 00000000 8fbf001c
  +0x0a0: 8fb20018 8fb10014 8fb00010 03e00008
  +0x0b0: 27bd0020
```

At this stop:

```text
ra=0xffffffff800179c4
a0=0xffffffff808101ac
s1=0xffffffff808101ad
s2=0xffffffff800b0000
s3=0x50
```

Recent Voodoo events after the fastpaths are only warmup/setup residue:

```text
fifo type1 cmd=0x00010221 target=0x044 ... pc=0xffffffff80054cb4
fifo type1 cmd=0x00010221 target=0x044 ... pc=0xffffffff800517f8
fifo type4 cmd=0x0001828c target=0x051 ... pc=0xffffffff80051df8
fifo type4 cmd=0x00018234 target=0x046 ... pc=0xffffffff8005199c
```

Next target:

1. Inspect the scheduler path around `0xffffffff800179c0`.
2. Dump the callers/hot helpers around `0xffffffff80001a24`,
   `0xffffffff80000fd8`, and `0xffffffff80041880` if this endpoint repeats.
3. Do not chase Voodoo rasterization yet; the next milestone is still the
   first new runtime draw/setup packet or a direct triangle PC other than
   `0xffffffff8005ed54`.

## 2026-05-24 Follow-up: Make Frame Runs Continue Past Soft CPU Halts

Implemented in `EutherDrive.Core/Arcade/Vegas/GauntletDarkLegacyAdapter.cs`:

- Added `EUTHERDRIVE_GAUNTDL_CONTINUE_AFTER_UNSUPPORTED`, enabled by
  `EUTHERDRIVE_GAUNTDL_BRINGUP_FAST`.
  - `HaltUnsupported` still logs the bad PC/op/reason.
  - In bringup mode it no longer leaves `_halted=true`, so `RunFrame()` keeps
    stepping like the direct `Step()` probe path already did.
- Added `TryFastPathKnownRuntimeFrameSwapTick`.
  - Catches `0xffffffff8001680c`.
  - Preserves the `800a36c0+0x1c` counter increment.
  - Skips the already-known swapbuffer/status noise path.
- Added `TryFastPathKnownGauntletGlideBootStateEmitAltEntry`.
  - Catches the alternate entry `0xffffffff8005129c`, which skips the normal
    `sp -= 0x38` prolog but still returns through an epilog with `sp += 0x38`.
  - Normalizes the Glide FIFO state and reproduces the stack unwind.
- Added `TryFastPathKnownRuntimeIdleRenderLoop`.
  - Catches the `0xffffffff80017980` render/scheduler function when `s2 <= 0`,
    which means the inner state-emission loop is skipped by guest code anyway.
  - Restores `s0..s2/ra/sp` and returns to the caller.

Verification:

```text
dotnet build tools/GauntletProbe/GauntletProbe.csproj -c Release --no-restore /clp:ErrorsOnly
Build succeeded.
338 Warning(s)
0 Error(s)
```

Before `CONTINUE_AFTER_UNSUPPORTED`, loading a 300-frame warm snapshot and
running one more frame left the CPU halted in low ASCII/data memory:

```text
frame=301
pc=0x0000000000000044
lastOp=0x706f4300
```

After the soft-halt change:

```text
frame=301
pc=0xffffffff80051674
fifoWords=43 fifoPackets=20 drawPackets=0 directTriangles=68 setupTriangles=0
```

After the frame-swap and idle-render fastpaths, a high-budget post-warmup run
moved the endpoint forward:

```text
EUTHERDRIVE_GAUNTDL_CPU_STEPS_PER_FRAME=1000000
frame=302
pc=0xffffffff8001fad8
lastOp=0x3c03a180
fifoWords=43 fifoPackets=20 drawPackets=0 directTriangles=68 setupTriangles=0
```

Current endpoint dump:

```text
mem[0xffffffff8001fa90]:
  +0x020: 8cc20004 03e00008 00000000 27bdffe8
  +0x030: afb00010 3c10800b 8e022e50 1440001a
  +0x040: afbf0014 3c03a180 3463000e 3c02a180
  +0x050: 34420008 3c04800b 94630000 94450000
  +0x060: 24843020 00052c00 0c007e88 00a32827
  +0x070: 3c03a180 3463000c 3c02a180 3442000a
  +0x080: 3c04800b 94630000 94450000 24843040
  +0x090: 00052c00 0c007e88 00a32827 3c03800b
  +0x0a0: 08007edb ac622e4c 0040f809 24040001
  +0x0b0: 3c04800b 24843020 0c007e88 0040282d
  +0x0c0: 8e022e50 0040f809 0000202d 3c04800b
  +0x0d0: 24843040 0c007e88 0040282d 8fbf0014
  +0x0e0: 8fb00010 03e00008 27bd0018
```

Register state at the endpoint:

```text
ra=0xffffffff80016d90
sp=0xffffffff807ffea8
s0=0xffffffff800b0000
s1=0xffffffff800a0000
s2=0xffffffff800b0000
a0=0x3400ff01
a1=0xffffffff800a74b8
a2=0xffff
```

Interpretation:

- Frame-based probing now makes forward CPU progress again instead of freezing
  on `_halted`.
- The 300-frame visual output is still unchanged: no new Voodoo draw/setup
  packets and only the known self-test triangles from `0xffffffff8005ed54`.
- Next target is the `0xffffffff8001fad8` helper. It reads `a1800008/0c/0e/0a`
  style hardware windows and calls `0xffffffff8001fa20`; likely an analog/input
  or small board-status helper, not Voodoo rendering.

## 2026-05-24 Follow-up: Collapse Low Runtime IOASIC Input Poll

Added `TryFastPathKnownRuntimeLowIoasicInputPoll` for the low runtime helper at
`0xffffffff8001fabc`.

Observed behavior:

```text
pc=0xffffffff8001fad8
read16 ffffffffa180000e -> ffff  CS6 IOASIC packed
read16 ffffffffa1800008 -> 7fff  CS6 IOASIC packed
read16 ffffffffa180000c -> ffff  CS6 IOASIC packed
read16 ffffffffa180000a -> ffff  CS6 IOASIC packed
```

The helper folds those neutral IOASIC/analog values through
`0xffffffff8001fa20` into the two local debounce records:

```text
mem[0xffffffff800b3020]:
  +0x000: 80000000 80000000 00000000 00000000
mem[0xffffffff800b3040]:
  +0x000: 00000000 00000000 00000000 00000000
mem[0xffffffff800b2e4c]:
  +0x000: 00000000
```

Implementation notes:

- The fastpath is signature-gated against the `8001fabc` helper and its epilog.
- It avoids re-reading `a1800008/0a/0c/0e` while stopped mid-helper, because
  doing so perturbs the IOASIC shuffle sequence.
- It instead requires the neutral debounce-record state above, reapplies the
  same bitfield update, writes `800b2e4c=0`, restores `s0/ra/sp`, and returns.

Verification:

```text
dotnet build tools/GauntletProbe/GauntletProbe.csproj -c Release --no-restore /clp:ErrorsOnly
Build succeeded.
337 Warning(s)
0 Error(s)
```

After this change, the same 300-frame warm snapshot no longer parks at
`8001fad8`; longer probes advance through the scheduler:

```text
frame=303 pc=0xffffffff8003aaac
frame=306 pc=0xffffffff800144b0
frame=320 pc=0xffffffff80016e5c
```

Render status remains unchanged through frame 320:

```text
frameHash=0x35297462
fifoWords=43 fifoPackets=20 drawPackets=0 directTriangles=68 setupTriangles=0
packetTypes=0:0,1:17,2:0,3:0,4:3,5:0,6:0,7:0
nonBlack=16641 colored=16641
```

Next target:

- `0xffffffff80016e5c` is still scheduler/epilog churn, not new Voodoo work.
- Need identify why the runtime frame loop keeps returning through
  `80016dxx/80016exx` without reaching real game geometry submission.

## 2026-05-24 Follow-up: Collapse More Neutral Scheduler Churn

Implemented in `EutherDrive.Core/Arcade/Vegas/GauntletDarkLegacyAdapter.cs`:

- Extended `TryFastPathKnownRuntimeBitfieldUpdate` coverage with a separate
  low-runtime bitfield updater for `0xffffffff8001fa20`.
  - This low helper is not byte-identical to the `0xffffffff800eafdc` runtime
    helper; it uses local debounce records at `800b3020/3040` with countdown
    fields at `+0x10/+0x12/+0x14`.
  - After the change, hot-PC output only shows the `8001fa20` entry, not the
    whole body.
- Added `TryFastPathKnownRuntimePlainStatusMaskUpdate` for
  `0xffffffff80011270..80011290`.
  - This is the compact CP0 `Status` mask helper used by the low scheduler lock
    path.
- Added `TryFastPathKnownRuntimeSchedulerCallbackEnqueue` for
  `0xffffffff8003aaac`.
  - It preserves the observed scheduler callback node invariant:
    `800a74b8+0x08 = 8003aa04`, list head `800b2f60 -> 800a74b8`.
- Extended `TryFastPathKnownRuntimeLowVblankStatusWait` to catch the prolog
  stop at `0xffffffff80018368`, before `ra/s2/s3` have been saved.
- Added `TryFastPathKnownRuntimeLowVblankStatusWaitCallsite` for the scheduler
  callsite at `0xffffffff80016d70/80016d74`.

Verification:

```text
dotnet build tools/GauntletProbe/GauntletProbe.csproj -c Release --no-restore /clp:ErrorsOnly
Build succeeded.
337 Warning(s)
0 Error(s)
```

Extra-step progression from the 300-frame warm snapshot:

```text
before callsite fix:
checkpoint extra=1000000 pc=0xffffffff80016d74
checkpoint extra=5000000 pc=0xffffffff80016d74

after callsite fix:
checkpoint extra=1000000 pc=0xffffffff80016dec
```

The old hot bodies are reduced to entry hits:

```text
hotpcs=0xffffffff8001fa20:13246,0xffffffff80064cd0:13245,
       0xffffffff80016d88:6623,0xffffffff80016d8c:6623,...
```

Render status is still unchanged:

```text
fifoWords=43 fifoPackets=20 drawPackets=0 directTriangles=68 setupTriangles=0
packetTypes=0:0,1:17,2:0,3:0,4:3,5:0,6:0,7:0
frameHash=0x35297462
```

Current endpoint:

```text
pc=0xffffffff80016dec
lastOp=0x14400005
```

Interpretation:

- We are now deeper in the active `80016d30` scheduler flag loop.
- The next candidate is the `0xffffffff8003ecac` branch at
  `80016de0..80016dec`; it likely decides whether to skip the diagnostic draw
  calls at `80016df0/80016df8`.
- Still no evidence of real game geometry. The next rendering milestone remains
  first type-3/type-5 packet or a new direct-triangle PC other than
  `0xffffffff8005ed54`.

## 2026-05-24 Follow-up: Diagnostic Predicate/Queue and Frame-Service Churn

Implemented in `EutherDrive.Core/Arcade/Vegas/GauntletDarkLegacyAdapter.cs`:

- Added `TryFastPathKnownRuntimeDiagnosticPredicateCallsite` for
  `0xffffffff80016de0/80016de4/80016de8`.
  - The callee at `0xffffffff8003ecac` is a pure
    `Read32(800a778c) & 1` predicate.
  - The fastpath computes the same branch target directly:
    `80016e00` when set, otherwise `80016df0`.
- Added `TryFastPathKnownRuntimeDiagnosticQueueDispatchCallsite` and entry/range
  support for `0xffffffff8003efa0`.
  - This helper drains a four-slot diagnostic queue from
    `800a7798..800a77d8` through `8003f3ac`.
  - In bringup-fast mode the fastpath treats it as diagnostic-only and advances
    `800a77cc` to `800a77c8`.
  - Important correction: the helper epilog is at `+0x78..+0x8c`.
- Extended `TryFastPathKnownRuntimeLowIoasicInputPoll` to cover entry
  `0xffffffff8001fabc`.
  - Important correction: its epilog signature is at `+0xb0..+0xbc`.
- Added an experimental `TryFastPathKnownRuntimeIdleFrameServiceEntry` for the
  `0xffffffff80016d30` service-frame function.
  - It models the frame-service counter increment and scheduler callback node.
  - Current warm snapshots can still land in partial prolog/body states, so this
    has not yet eliminated the `80016d30` hot loop.
- Added `TryFastPathKnownRuntimeLowFrameStateCallback` for
  `0xffffffff80064cd0`.
  - Signature offsets are entry-relative to `80064cd0`, not the dump base
    `80064cc0`.

Verification:

```text
dotnet build tools/GauntletProbe/GauntletProbe.csproj -c Release --no-restore /clp:ErrorsOnly
Build succeeded.
337 Warning(s)
0 Error(s)
```

Progress from the 300-frame warm snapshot
`/tmp/eutherdrive-gauntlet-probe/gauntdl-gauntdl24-fast-raw-f300-s0-918179e261f3.warm`:

```text
after low IOASIC entry correction:
checkpoint extra=25000000 pc=0xffffffff8003f024
fifoWords=43 fifoPackets=20 drawPackets=0 directTriangles=68 setupTriangles=0

after diagnostic queue range support:
checkpoint extra=30000000 pc=0xffffffff800512d8
checkpoint extra=50000000 pc=0xffffffff80016d44
```

Latest 50M profile after the frame-service experiments:

```text
pc=0xffffffff80016d44
hotpcs=0xffffffff80064cd0:925926,
       0xffffffff8001680c:462963,
       0xffffffff80016d30:462963,
       0xffffffff80016d34:462963,
       0xffffffff80016d38:462963,
       0xffffffff80016d3c:462963,
       0xffffffff80016d40:462963,
       0xffffffff80016d88:462963,...
fifoWords=43 fifoPackets=20 drawPackets=0 directTriangles=68 setupTriangles=0
packetTypes=0:0,1:17,2:0,3:0,4:3,5:0,6:0,7:0
frameHash=0x35297462
```

Interpretation:

- The previous `8003f024` diagnostic queue epilog checkpoint is no longer a
  hard endpoint once one more step is allowed.
- We are still cycling in the low runtime service/diagnostic frame path, not
  emitting new Voodoo geometry.
- `directTriangles=68` is still only the known self-test geometry from
  `0xffffffff8005ed54`.

Next target:

1. Fix or replace the `80016d30` service-frame fastpath so it reliably catches
   partial prolog/body states from the warm snapshot.
2. If that remains messy, target the specific `80016d88..80016dc4` loop and
   `80064cd0` callback callsites rather than the full function unwind.
3. Keep using `EUTHERDRIVE_GAUNTDL_PROFILE_HOT_PCS=1`; the current top hot PCs
   are more useful than the endpoint alone because many checkpoints land on
   ordinary instructions rather than halts.

## 2026-05-24 Follow-up: Break Self-Returning Service/Dispatcher Loops

Implemented in `EutherDrive.Core/Arcade/Vegas/GauntletDarkLegacyAdapter.cs`:

- Corrected `TryFastPathKnownRuntimeLowFrameStateCallback` signature offsets.
  - The previous version still mixed offsets from dump base `80064cc0` with
    entry `80064cd0`.
- Corrected `TryFastPathKnownRuntimeIdleFrameServiceEntry` signature offsets
  around the low vblank/input section.
  - The fastpath now catches partial prolog states such as `80016d48`.
  - When the saved return address is the service entry itself
    (`ra=80016d30`), bringup-fast mode redirects to the next dispatcher
    function at `80016e64` instead of re-entering the same service frame.
- Added `TryFastPathKnownRuntimeSelfDispatcherEntry` for
  `80016e64..80016ebc`.
  - It handles the same self-return pattern (`ra=80016e64`) by redirecting to
    the next local function at `80016ebc`.
- Added `TryFastPathKnownRuntimeMainIdleLoop` for the terminal
  `80015784/80015788` idle spin.
  - It routes the idle loop back through the dispatcher call at `8001577c`
    instead of burning every probe step on `nop; j self`.

Verification:

```text
dotnet build tools/GauntletProbe/GauntletProbe.csproj -c Release --no-restore /clp:ErrorsOnly
Build succeeded.
337 Warning(s)
0 Error(s)
```

Progression from the same 300-frame warm snapshot:

```text
before service offset fix:
checkpoint extra=50000001 pc=0xffffffff80016d48

after service offset fix:
checkpoint extra=50000001 pc=0xffffffff80016e7c
checkpoint extra=100000000 pc=0xffffffff80016ea8

after dispatcher/self-idle handling:
checkpoint extra=50000001 pc=0xffffffff8001577c
checkpoint extra=150000000 pc=0xffffffff80015784
```

Latest hot-PC profile:

```text
hotpcs=0xffffffff80016e64:37489739,
       0xffffffff8001577c:37489738,
       0xffffffff80015780:37489738,
       0xffffffff80015784:37489737,
       0xffffffff8003b554:994,...
fifoWords=43 fifoPackets=20 drawPackets=0 directTriangles=68 setupTriangles=0
packetTypes=0:0,1:17,2:0,3:0,4:3,5:0,6:0,7:0
frameHash=0x35297462
```

Interpretation:

- The service-frame and self-dispatcher loops are now understood and can be
  bypassed.
- The runtime still reaches a terminal main idle pump with an empty/neutral
  dispatcher state; repeatedly pumping it does not create new Voodoo work.
- Rendering remains unchanged: no type-3/type-5 FIFO packets and no direct
  triangle source other than the known self-test PC.

Next target:

1. Inspect why the dispatcher/list state around `800b2e2c`, `800b2f60`, and
   related callback nodes is empty or only loops neutral work after `8001577c`.
2. The next useful dumps are around `8003b554..8003b5b0` and
   `8003aa5c..8003aaa0`, which are now the nontrivial hot helpers after the
   idle pump.
3. Do not spend more time optimizing `80015784` itself; it is now just the
   outer idle sentinel.

## 2026-05-25 Follow-up: Raw-Fed Probe and Current Model-Load Blocker

The local `/tmp/eutherdrive-gauntlet-probe` and `/tmp/gauntd24.raw` artifacts
were missing, while the actual raw sidecar is present beside the ROM set:

```text
/home/nichlas/roms/MAME/Midway/Vegas/gauntd/gauntd24.raw
```

`tools/GauntletProbe` now auto-selects `gauntd24.raw` or `gauntdl.raw` from the
ROM directory when `EUTHERDRIVE_GAUNTDL_RAW_DISK` is unset. This keeps the warm
snapshot key's `raw/chd` component aligned with what the IDE device actually
uses.

New raw-fed warm snapshot:

```text
/tmp/eutherdrive-gauntlet-probe/gauntdl-gauntdl24-fast-raw-f300-s0-89de87071a67.warm
```

Implemented in `EutherDrive.Core/Arcade/Vegas/GauntletDarkLegacyAdapter.cs`:

- Added `TryFastPathKnownRuntimeCp0CountRead()` for the pure helper at
  `0xffffffff80010fbc` (`mfc0 v0,Count; jr ra`). This removes the helper body
  from the active model-load path, though hot-PC accounting still records the
  entry before fastpaths run.

Verification:

```text
dotnet build tools/GauntletProbe/GauntletProbe.csproj -c Release --no-restore /clp:ErrorsOnly
Build succeeded.
338 warnings, 0 errors.
```

Baseline raw-fed cold run to frame 320:

```text
frame=320
pc=0xffffffff80010fc8
rtxt=16@0xffffffff800e30a0/ra=0xffffffff800e33e4 "Loading Game."
voodoo active, fifoWords=309330, fifoPackets=102127, fastFills=674, swaps=2572
drawPackets=0, directTriangles=30, setupTriangles=0
packetTypes=0:3,1:32133,2:0,3:0,4:69991,5:0,6:0,7:0
framebuffer=640x480 nonBlack=307200 colored=0
```

From the new frame-300 warm snapshot, 50M extra CPU steps with default count
step now lands in the model/FSYS state-machine instead of the earlier
`MBOX_BGLoadModel` dispatcher stop:

```text
pc=0xffffffff800abaac
hotpcs=0xffffffff80010fbc:820670,
       0xffffffff800abaa0:410336,
       0xffffffff800ac330:410336,
       0xffffffff800ac49c:410336,...
voodoo fifoWords=313938 fifoPackets=104431 drawPackets=0 directTriangles=30 setupTriangles=0
packetTypes=0:3,1:34437,2:0,3:0,4:69991,5:0,6:0,7:0
```

With `EUTHERDRIVE_GAUNTDL_CP0_COUNT_STEP=8192`, the same snapshot reaches a
later `MBOX_BGLoadModel` state after 100M extra CPU steps:

```text
pc=0xffffffff8011d5e0
voodoo fifoWords=332290 fifoPackets=113607 drawPackets=0 directTriangles=30 setupTriangles=0
packetTypes=0:4,1:43612,2:0,3:0,4:69991,5:0,6:0,7:0
```

Current interpretation:

- The adapter is still in the loaded runtime's `Loading Game.` path.
- Voodoo continues to receive status/swap/type-1 traffic, but no real
  type-3/type-5 geometry appears.
- The active blocker is not the outer idle sentinel. It is the model-load/FSYS
  state-machine around `800abaa0`, `800ac2b4`, `800ac330`, and
  `MBOX_BGLoadModel`.
- Runtime data windows checked at `80238040`, `80217a00`, and `8021f150` are
  still zero in the failing state, while the stack/log strings still show
  `MBOX_BGLoadModelDone Timeout, state=5` and `ResetModels Timeout`.

Next target:

1. Trace the state transition through `800ac2b4` and its jump table at
   `800ac330`; identify which state leaves `80238060/80238080` and the
   `80252da0` records empty.
2. Avoid faking a successful `MBOX_BGLoadModel` completion until the missing
   model/FSYS data source or completion callback is identified.
3. Keep using the raw-fed frame-300 warm snapshot above; it is the fastest
   current repro.

## 2026-05-25 Follow-up: BGLoadModel QIO Status Repair

Implemented in `EutherDrive.Core/Arcade/Vegas/GauntletDarkLegacyAdapter.cs`:

- Added a narrow `ApplyKnownRuntimeBgLoadModelQioCompletion()` repair at
  `0xffffffff800abaa0`. It detects the current model record at `80252da0 +
  index * 0x28`, follows record `+0x08` to the runtime QIO object, and marks
  QIO `+0x14` complete when QIO `+0x18` still contains a `/d0/` file path.
- The first confirmed hit is for `/d0/static_lr/textures.rom`:

```text
[GAUNTDL:FIX] bgloadmodel qio-complete index=0 record=ffffffff80252da0 qio=ffffffff80217c58
```

Effect from the raw-fed frame-300 snapshot:

```text
record 80252da0 after 50M extra, CP0_COUNT_STEP=8192:
+0x000: 00000000 00000000 00000000 00000006
+0x010: 00000001 07606dfe ...
+0x020: 80217d70 ...

qio 80217c58:
+0x010: 00000000 ffffffff "/d0/static_lr/textures.rom"

next qio candidate 80217d70:
"/d0/players/war/yel00/textures.rom"
```

This confirms the previous state-5 timeout was a missing runtime QIO completion
signal. It does not yet make the port render geometry. After the repair the
runtime progresses into later model/texture handling, then spends time in the
formatting/log path around `8011fff0..80120190` and still reports timeout-like
behavior. Voodoo remains fill/swap-heavy with no type-3/type-5 draw packets.

Current next target:

1. Identify the actual FSYS file payload path for the QIO object, not only its
   status field. The QIO object carries destination-like words before the path:
   `80295670`, `800ab4e4`, `802e1718`, `00002000`.
2. Either wire the runtime QIO path to raw-disk file reads, or identify the
   callback that should fill the buffer and update model metadata before state 6.
3. Consider a fastpath for the late runtime formatting loop only as a speed aid;
   it is not the functional blocker.

## 2026-05-25 Follow-up: BGLoadModel QIO Table Scan

The first BGLoadModel QIO repair was internally inconsistent and too narrow:

- The QIO callback at `800ab4e4` reads QIO `+0x10` as the actual byte count.
  The repair now writes QIO `+0x10 = qio+0x0c` before setting status `+0x14 = 2`.
- Disassembly of `800abaa0` showed the active table stride is `index * 0x18`,
  not `index * 0x28`.
- The current-index latch does not always point at the pending QIO. The repair
  now scans the first 64 BGLoadModel records at `80252da0 + index * 0x18` and
  completes pending `/d0/` QIOs.

Confirmed hits from the frame-300 warm snapshot:

```text
[GAUNTDL:FIX] bgloadmodel qio-complete index=0 record=ffffffff80252da0 qio=ffffffff80217c58 bytes=00002000
[GAUNTDL:FIX] bgloadmodel qio-complete index=1 record=ffffffff80252db8 qio=ffffffff80217d70 bytes=00002000
[GAUNTDL:FIX] bgloadmodel qio-complete index=2 record=ffffffff80252dd0 qio=ffffffff80217e88 bytes=00002000
...
```

This materially changes bringup:

```text
50M extra:
pc=0xffffffff8011d608
fifoWords=2829696 fifoPackets=156340
drawPackets=308 directTriangles=980 setupTriangles=475
packetTypes=0:980,1:34829,2:0,3:308,4:82925,5:37298,6:0,7:0

200M extra:
pc=0xffffffff800c7bd4
fifoWords=6934582 fifoPackets=248633
drawPackets=870 directTriangles=2772 setupTriangles=1371
packetTypes=0:2421,1:41857,2:0,3:870,4:106472,5:97013,6:0,7:0
```

The port is no longer blocked at the original BGLoadModel state-5/status-0
stall. Voodoo now receives real type-3/type-5 draw traffic and texture writes.
The probe framebuffer classifier still reports `colored=0`, so the next blocker
is probably downstream in render state, framebuffer interpretation, or a later
runtime wait around `800c7bd4`/the hot message dispatch path `8011d5xx`.

## 2026-05-25 Follow-up: Runtime FSYS Status and World-Data Blocker

Further inspection showed the `800c7bd4` endpoint is not a hard halt. It is in a
runtime text/log helper, while the scratch log buffer reports:

```text
No world data: test
Too many open files: 8
StartFileRead: QIO error, file not open
No valid worlds
```

The visible blocker is therefore still runtime asset/world loading, not the
first Voodoo draw submission. The BGLoadModel QIOs at `80217c58`, `80217d70`,
etc. point at FSYS objects starting at `80295670`, and each object carried
status `0x300b` when the QIO completion repair fired.

Implemented in `EutherDrive.Core/Arcade/Vegas/GauntletDarkLegacyAdapter.cs`:

- Extended `ApplyKnownRuntimeBgLoadModelQioCompletion()` so that, for the same
  verified `/d0/` QIOs, it also clears the low error byte on the associated FSYS
  object status (`0x300b -> 0x3000`) before marking QIO `+0x14 = 2`.
- The repair is still bounded by the existing QIO signature checks: main-RAM
  QIO pointer, `/d0/` path signature, nonzero byte count <= `0x20000`, and
  pending QIO status.

Verification:

```text
dotnet build tools/GauntletProbe/GauntletProbe.csproj -c Release --no-restore /clp:ErrorsOnly
Build succeeded.
337 Warning(s)
0 Error(s)
```

Cold, raw-fed 300-frame run plus 50M extra CPU steps:

```text
pc=0xffffffff801201a8
rtxt=16@0xffffffff800e30a0/ra=0xffffffff800e33e4 "Loading Game."
fifoWords=6905143 fifoPackets=256856
drawPackets=1024 directTriangles=2770 setupTriangles=1370
packetTypes=0:4877,1:41177,2:0,3:1024,4:112760,5:97017,6:0,7:1
framebuffer=640x480 nonBlack=307200 colored=0
```

This is forward progress compared with the older warm-snapshot checks, and it
confirms that all eight initial BGLoadModel objects hit the `0x300b` repair.
However, `worlds.rom` is still only present as static/runtime strings and not as
an active QIO path in the dumped pool; `80227c94` is still zero and
`80227cb8` remains `0xffffffff`. The next target is the world-loader open path
around `8004d45c`/`8004d658`, not another generic Voodoo fastpath.

## 2026-05-31 Follow-up: World Data Repair, Text Fastpaths, and IRQ Bridge Status

Current local context:

- Repo: `/home/nichlas/EutherDrive_Android`
- Local MAME source/reference: `/home/nichlas/mame`
- Adapter: `EutherDrive.Core/Arcade/Vegas/GauntletDarkLegacyAdapter.cs`
- Probe: `tools/GauntletProbe/Program.cs`
- ROM path used: `/home/nichlas/roms/MAME/Midway/Vegas/gauntd`
- Useful warm snapshot: `/tmp/eutherdrive-gauntlet-probe/gauntdl-gauntdl24-worldfail-38m.warm`

Build command used throughout:

```sh
dotnet build tools/GauntletProbe/GauntletProbe.csproj -c Release --no-restore /clp:ErrorsOnly
```

Latest build result:

```text
Build succeeded.
337 Warning(s)
0 Error(s)
```

Important implementation state:

- `ApplyKnownRuntimeWorldDataTableRepair()` now triggers at `pc=0xffffffff8004ecac`.
- It writes a diagnostic world-data table:
  - global `0xffffffff8016c130 + 0x18 = 13`
  - global `0xffffffff8016c130 + 0x1c = 0x802e1000`
  - fallback table entries at `0xffffffff802e1000`
- Trace when active:

```text
[GAUNTDL:FIX] world-data-table pc=ffffffff8004ecac global=ffffffff8016c130 table=ffffffff802e1000 count=13
```

Runtime fastpaths added/fixed in this pass:

- linked-list append tail around `800b13d4..800b13e0`
- formatter entry fixed from wrong `80120230` to actual `80120234`
- format-buffer entry/in-flight fastpaths
- global `8022808c` exchange entry fixed to `800b1dc4`
- guarded diagnostic format-line wrapper around `80121670`
- `strncmp` fastpath at `8011fb40`
- fixed-length compare wrapper at `800aa898`

The `800aa898` wrapper was the major old hot loop. After the wrapper fix, it no
longer dominates. It returns to `8011b68c` with `v0=0` for the observed case.

Do not re-enable runtime interrupt bridge by default right now:

- `EUTHERDRIVE_GAUNTDL_FIX_RUNTIME_INTERRUPT_BRIDGE` should not be on by default yet.
- `BRINGUP_FAST=1` previously enabled it implicitly through `IsBringupFixEnabled`.
- That caused execution to enter the `a0011078`/`a00cc3xx` exception/cache path and eventually run string/data regions as code.
- The default was changed so `_enableRuntimeInterruptBridge` only checks the explicit env var:

```csharp
private readonly bool _enableRuntimeInterruptBridge =
    GauntletDarkLegacyAdapter.IsTruthy(Environment.GetEnvironmentVariable("EUTHERDRIVE_GAUNTDL_FIX_RUNTIME_INTERRUPT_BRIDGE"));
```

Known-bad experiment that was reverted:

- A narrow repair at `a0011078..a0011080` tried to resume from CP0 EPC when `a2 == 0`.
- It moved past the local loop but was wrong: it produced unsupported opcode spam in string/data ranges such as `8012b080`, `8013be4c`, and `8015295c`.
- That patch was reverted. Do not resurrect it without first fixing the actual interrupt/exception entry cause.

Current recommended run command:

```sh
EUTHERDRIVE_GAUNTDL_BRINGUP_FAST=1 \
EUTHERDRIVE_GAUNTDL_FASTPATH_DIAGNOSTIC_RUNTIME=1 \
EUTHERDRIVE_GAUNTDL_CONTINUE_AFTER_UNSUPPORTED=1 \
EUTHERDRIVE_GAUNTDL_PROFILE_HOT_PCS=1 \
EUTHERDRIVE_GAUNTDL_WARMUP_STATE=/tmp/eutherdrive-gauntlet-probe/gauntdl-gauntdl24-worldfail-38m.warm \
EUTHERDRIVE_GAUNTDL_WARMUP_FRAMES=300 \
EUTHERDRIVE_GAUNTDL_EXTRA_SERIES=5000000 \
EUTHERDRIVE_GAUNTDL_DUMP_FRAME=/tmp/gauntdl-default-no-irq-bridge-5m.ppm \
dotnet run --project tools/GauntletProbe/GauntletProbe.csproj -c Release --no-build -- \
  /home/nichlas/roms/MAME/Midway/Vegas/gauntd 300
```

Verified default `BRINGUP_FAST=1` result after disabling implicit runtime IRQ bridge:

```text
checkpoint extra=5000000 drained=0 pc=0xffffffff800de134 lastOp=0x8c628190
frameHash=0x663d858b
fifoWords=328306 fifoPackets=95009 drawPackets=497 directTriangles=30 setupTriangles=0
fastFills=674 swaps=44
packetTypes=0:9,1:22517,2:4,3:497,4:71627,5:351,6:0,7:4
hotpcs=0xffffffff800de910:80838,0xffffffff800de914:80838,0xffffffff800de928:80838,
       0xffffffff800de9ac:80837,0xffffffff800de9b0:80837,0xffffffff800de9b4:80837,
       0xffffffff800de9b8:80837,0xffffffff800de92c:71361,
       0xffffffff80010fbc:28430,0xffffffff800111c8:19193
```

Longer 50M run with bridge off/default:

```text
checkpoint extra=50000000 drained=0 pc=0xffffffff80013584 lastOp=0x3c028023
frameHash=0x663d858b
fifoWords=328306 fifoPackets=95009 drawPackets=497 directTriangles=30 setupTriangles=0
fastFills=674 swaps=44
hotpcs=0xffffffff800de910:807786,0xffffffff800de914:807786,0xffffffff800de928:807786,
       0xffffffff800de9ac:807785,0xffffffff800de9b0:807785,0xffffffff800de9b4:807785,
       0xffffffff800de9b8:807785,0xffffffff800de92c:712359
```

Interpretation:

- The port is past the original first world-data failure and past the `800aa898` compare-wrapper hot loop.
- Voodoo is still active but no new useful visual progress happened in this pass; framebuffer hash remains `0x663d858b`.
- Current real blocker is IRQ/status polling around `800de910`/`800de9ac`, with endpoint samples at `800de134` and `80013584`.
- The next session should inspect or fastpath the runtime IRQ/status dispatch around `800de8e0..800deafc` and related globals under `80228144`, `80228150`, `80228160`, `80228170`, `80228190`, rather than enabling the broad runtime interrupt bridge.

Useful dump command for the next pass:

```sh
EUTHERDRIVE_GAUNTDL_BRINGUP_FAST=1 \
EUTHERDRIVE_GAUNTDL_FASTPATH_DIAGNOSTIC_RUNTIME=1 \
EUTHERDRIVE_GAUNTDL_CONTINUE_AFTER_UNSUPPORTED=1 \
EUTHERDRIVE_GAUNTDL_DUMP_GPRS=1 \
EUTHERDRIVE_GAUNTDL_DUMP_CODE_RANGES=0xffffffff800de0f0:128,0xffffffff800de8e0:256,0xffffffff800d4d10:96,0xffffffff80013560:96 \
EUTHERDRIVE_GAUNTDL_DUMP_BYTES_RANGES=0xffffffff80228000:512,0xffffffff807ffc80:192 \
EUTHERDRIVE_GAUNTDL_WARMUP_STATE=/tmp/eutherdrive-gauntlet-probe/gauntdl-gauntdl24-worldfail-38m.warm \
EUTHERDRIVE_GAUNTDL_WARMUP_FRAMES=300 \
EUTHERDRIVE_GAUNTDL_EXTRA_SERIES=5000000 \
dotnet run --project tools/GauntletProbe/GauntletProbe.csproj -c Release --no-build -- \
  /home/nichlas/roms/MAME/Midway/Vegas/gauntd 300
```

## 2026-05-31 Continuation: Exception FPU Context and IRQ Status Dump

The previous `/tmp/eutherdrive-gauntlet-probe/gauntdl-gauntdl24-worldfail-38m.warm`
snapshot was not available as a late runtime state in this session. A replacement
early snapshot was regenerated at:

```text
/tmp/eutherdrive-gauntlet-probe/gauntdl-current-regenerated-f300-1m.warm
```

Regeneration command:

```sh
EUTHERDRIVE_GAUNTDL_BRINGUP_FAST=1 \
EUTHERDRIVE_GAUNTDL_FASTPATH_DIAGNOSTIC_RUNTIME=1 \
EUTHERDRIVE_GAUNTDL_CONTINUE_AFTER_UNSUPPORTED=1 \
EUTHERDRIVE_GAUNTDL_CPU_STEPS_PER_FRAME=1000000 \
EUTHERDRIVE_GAUNTDL_SAVE_WARMUP=1 \
EUTHERDRIVE_GAUNTDL_WARMUP_STATE=/tmp/eutherdrive-gauntlet-probe/gauntdl-current-regenerated-f300-1m.warm \
EUTHERDRIVE_GAUNTDL_WARMUP_FRAMES=300 \
dotnet run --project tools/GauntletProbe/GauntletProbe.csproj -c Release --no-build -- \
  /home/nichlas/roms/MAME/Midway/Vegas/gauntd 300
```

Result:

```text
pc=0xffffffff80014610
voodoo idle
frameHash=0xa9a65ac5
```

Implemented:

- Added `TryFastPathKnownExceptionFpuContextBlock()` for the exception handler
  COP1 context save/restore blocks at `800114e0..80011690`.
- The fastpath verifies sequential `sdc1`/`ldc1` signatures and performs the
  same main-RAM FPR stores/loads in bulk. It does not change interrupt policy.

Build:

```text
dotnet build tools/GauntletProbe/GauntletProbe.csproj -c Release --no-restore /clp:ErrorsOnly
Build succeeded.
338 Warning(s)
0 Error(s)
```

With the runtime interrupt bridge still off/default, extra stepping from the
regenerated snapshot remains in the early exception/timer path:

```text
checkpoint extra=50000000 pc=0xffffffff800114f0 lastOp=0xf7a30148
voodoo idle
hotpcs=80014998...,80011068...,8001116c...
```

With `EUTHERDRIVE_GAUNTDL_FIX_RUNTIME_INTERRUPT_BRIDGE=1`, the run reaches the
known runtime IRQ/status area quickly, but also reproduces the known-bad symptom
where text/data are executed as code before recovery:

```text
halt pc=000000000000005c op=47206972 reason=cop1 rs=19
halt pc=0000000000000060 op=73656d61 reason=opcode 1c
checkpoint extra=25000000 pc=0xffffffff800de9fc lastOp=0x1462000f
voodoo active fifoWords=5 fifoPackets=2 directTriangles=20 fastFills=2
frameHash=0xf29eb67c
```

Relevant state at `800de9fc`:

```text
v0=1 v1=1 a0=0000fd60 a1=80260000 a2=0 a3=20
s1=00010000 s2=2 s3=8 s4=80165d10 s5=0000fd60
cp0 status=34007f00 cause=8000 epc=01000035
```

Relevant globals around `80228100`:

```text
80228114 = 0x00004084
80228118 = 0x000004a5
8022811c = 0x00000000
80228120 = 0x807fdef8
80228124 = 0x802947d8
80228128 = 0x80294af0
80228150 = 0x00000001
80228158 = 0x000040d7
80228160 = 0xffffffff
80228164 = 0x000040d7
80228168 = 0x01000035
8022817c = 0x00004a58
```

Next target:

1. Do not make `EUTHERDRIVE_GAUNTDL_FIX_RUNTIME_INTERRUPT_BRIDGE` default.
2. Inspect the `800de8e0..800deb04` dispatcher and the callback pointers in
   `80228144`, `80228150`, `80228160`, `80228170`, and `80228190`.
3. Add a narrower repair for the timer/status path that avoids entering text/data
   as code, then rerun from the regenerated snapshot.

## 2026-05-31 Continuation: FPU Context Bounds Fixed, IRQ Dispatcher Still Hot

The original late snapshot is present again:

```text
/tmp/eutherdrive-gauntlet-probe/gauntdl-gauntdl24-worldfail-38m.warm
```

Implemented in `EutherDrive.Core/Arcade/Vegas/GauntletDarkLegacyAdapter.cs`:

- Corrected `TryFastPathKnownExceptionFpuContextBlock()` block boundaries.
  The actual exception routine layout is:
  - save all: `800114e0..80011560`, with `f31` in the branch delay slot
  - save even: `80011564..800115a0`
  - load all: `800115e8..80011668`, with `f31` in the branch delay slot
  - load even: `8001166c..800116a4`
- Corrected FPU context load base from `sp` to `a0`. Stores still use `sp`.
- Added a narrow `0x04400000..0x04500000` uncached RAM-code window to
  `IsRuntimeCodeAddress()` for the observed `a044d178` copy stub.
- Added an experimental timer-only interrupt-dispatch suppress fastpath for
  the `800de8e0` dispatcher. As of this note it does not yet alter the
  endpoint, so treat it as incomplete/investigative.

Build:

```text
dotnet build tools/GauntletProbe/GauntletProbe.csproj -c Release --no-restore /clp:ErrorsOnly
Build succeeded.
337 Warning(s)
0 Error(s)
```

Current repro command:

```sh
EUTHERDRIVE_GAUNTDL_BRINGUP_FAST=1 \
EUTHERDRIVE_GAUNTDL_FASTPATH_DIAGNOSTIC_RUNTIME=1 \
EUTHERDRIVE_GAUNTDL_CONTINUE_AFTER_UNSUPPORTED=1 \
EUTHERDRIVE_GAUNTDL_PROFILE_HOT_PCS=1 \
EUTHERDRIVE_GAUNTDL_WARMUP_STATE=/tmp/eutherdrive-gauntlet-probe/gauntdl-gauntdl24-worldfail-38m.warm \
EUTHERDRIVE_GAUNTDL_WARMUP_FRAMES=300 \
EUTHERDRIVE_GAUNTDL_EXTRA_SERIES=5000000,50000000 \
dotnet run --project tools/GauntletProbe/GauntletProbe.csproj -c Release --no-build -- \
  /home/nichlas/roms/MAME/Midway/Vegas/gauntd 300
```

Current result:

```text
checkpoint extra=5000000 pc=0xffffffffa044d178
checkpoint extra=50000000 pc=0xffffffff800de914
voodoo active fifoWords=5 fifoPackets=2 drawPackets=0 directTriangles=20
hotpcs=800110c4..800110d0, 80011620, 8004d9a8...
```

State at `800de914`:

```text
v0=1 a0=34007f00 a1=80260000 a2=03e80000
s1=00008000 s3=7 s4=80165d0c s5=0000fd60 s6=0 s7=1 s8=0800
ra=800de9a8 sp=0000fcd8
cp0 status=34007f00 cause=8000 epc=01000315
80228150=1 80228160=ffffffff 80228168=01000315
```

Interpretation:

- The FPU context fastpath was genuinely wrong before and is now aligned with
  the live exception routine.
- The persistent blocker is still the runtime interrupt/status dispatcher around
  `800de8e0..800deb04`. It is handling a CP0 timer bit with an invalid-looking
  EPC and then continues through the same text/data-as-code recovery symptom.
- The next useful action is to instrument why the timer-only dispatcher fastpath
  is not matching, or replace it with a trace-first version that reports which
  guard fails before changing control flow.

## 2026-05-31 Continuation: FPU Bases Corrected, IRQ Signature Diagnosed

Follow-up implementation in `EutherDrive.Core/Arcade/Vegas/GauntletDarkLegacyAdapter.cs`:

- Corrected the remaining FPU context base-register mistakes:
  - all/even stores use `sp` (`sdc1` opcodes `f7a...`)
  - all/even loads use `a0` (`ldc1` opcodes `d48...`)
- Corrected the `800de8e0` dispatcher signature guards to match the live copied
  code. The old guards were offset incorrectly around `+0x20/+0x30` and also
  expected the wrong word at `+0xf8`.
- Added a limited diagnostic reject trace for the dispatcher suppress fastpath
  under `EUTHERDRIVE_GAUNTDL_FASTPATH_DIAGNOSTIC_RUNTIME=1`.

Verification:

```text
dotnet build tools/GauntletProbe/GauntletProbe.csproj -c Release --no-restore /clp:ErrorsOnly
Build succeeded.
337 Warning(s)
0 Error(s)
```

The signature fix changes the reject reason from `signature` to the actual state:

```text
[GAUNTDL:IRQ] suppress-reject reason=pending pc=ffffffff800de8f0
pending=3000 status=34007f00 cause=3000 epc=ffffffff800479d8
sp=000000000000dbb8 ra=ffffffff800de8b8
```

A broad experiment that allowed any pending interrupt when EPC was runtime code
was tested and reverted. It increased Voodoo type-1/swap traffic but corrupted
control flow and stack state:

```text
pc=0xffffffffffff9ecb lastOp=0xffffffff
earlier repeated: pc=0000000000000007 op=edface34
ra=ffffffffffffffff sp=fffffffffffffc58 epc=ffffffffffff1fa8
fifoWords=4805 fifoPackets=2402 swaps=600
```

Current safe sanity check after reverting that broad pending rule:

```text
checkpoint extra=5000000 pc=0xffffffffa044d178
fifoWords=5 fifoPackets=2 directTriangles=10 fastFills=1 swaps=0
```

Interpretation:

- The dispatcher fastpath now reaches meaningful guards; the old "not matching"
  problem was partly just a bad signature.
- `pending=0x3000` cannot be blindly skipped from inside the dispatcher. The
  handler has already built state on the stack, and forcing the epilogue return
  from that point can jump into stack/data.
- Next target is a narrower model of the `0x3000` dispatcher path: either
  emulate just enough of the bit loop/callback choice to land on the normal
  epilogue, or identify which device pending bit is spurious before the runtime
  enters `800de8e0`.

## 2026-05-31 Continuation: IRQ C/D Rejected, Early FPU Save Advanced

Follow-up implementation in `EutherDrive.Core/Arcade/Vegas/GauntletDarkLegacyAdapter.cs`:

- Added NILE interrupt diagnostics to the dispatcher reject trace. The live
  `pending=0x3000` case decodes as NILE PCI C + PCI D:

```text
nileState=0c00 nilePins=0c nileCtl=00900000/8000ba00
nileStatus=00000000/08000400 sio=1 ide=1
savedRa=ffffffff800ded54 savedS0=000000000000dc40
```

- Tested suppressing PCI C/D and returning through the dispatcher epilogue. This
  is unsafe and was reverted; it jumps into stack/text-data-like low addresses
  (`pc=0000000000000007`, then low ASCII-looking opcodes).
- Tested clearing C/D in place without taking the epilogue. Also not sufficient:
  the run still falls into the same exception-context failure path.
- Removed the C/D clearing side effect from the dispatcher fastpath. The current
  behavior is diagnostic reject only for the non-timer `0x3000` case.
- Added a narrow early exception FPU-store fastpath for
  `800113ec..80011468`. This path stores the actually encoded `sdc1` register
  range using `sp` as the context base, then exits at `8001146c`.
- Added gated diagnostic trace for that early FPU context path under
  `EUTHERDRIVE_GAUNTDL_FASTPATH_DIAGNOSTIC_RUNTIME=1`.

Verification:

```text
dotnet build tools/GauntletProbe/GauntletProbe.csproj -c Release --no-restore /clp:ErrorsOnly
Build succeeded.
338 Warning(s)
0 Error(s)
```

Probe from the late warm state:

```text
EUTHERDRIVE_GAUNTDL_EXTRA_SERIES=50000000
warmupSnapshotLoaded=/tmp/eutherdrive-gauntlet-probe/gauntdl-gauntdl24-worldfail-38m.warm
[GAUNTDL:FPUCTX] store pc=ffffffff800113ec op=f7a10138 expected=f7a10138
fpr=1 sp=000000000000fd60 opcodeOk=True contextOk=True
checkpoint extra=50000000 pc=0xffffffff800114bc lastOp=0x03a8e821
```

Interpretation:

- The earlier `80011440` stop (`sdc1 f21,0x1d8(sp)`) was a real missing FPU
  context fastpath. The new loose path gets past it.
- The current endpoint is now `800114bc` (`addu sp,sp,t0`), so the next blocker
  is later in the exception context save/stack transition, not the FPU store
  itself.
- Hot PCs remain dominated by `800110c4..800110d0` CP0 cause access and
  `80011620`, so the next useful target is the exception context save/restore
  around `8001146c..80011620`, especially how `sp/k0/k1` are being transformed
  before the return path.

## 2026-05-31 Continuation: Early Even Save and Restore Boundary

Additional implementation in `EutherDrive.Core/Arcade/Vegas/GauntletDarkLegacyAdapter.cs`:

- Added an early even-FPU save fastpath for `8001146c..800114a8`, exiting at
  `800114ac`. This covers the `sdc1 f0,f2,...,f30` block that immediately
  follows the earlier loose save-all path.
- Added a loose FPU-load helper that only traces/acts on actual `ldc1`
  candidates. This avoids treating the nearby GPR restore prelude as FPU loads.

Verification:

```text
dotnet build tools/GauntletProbe/GauntletProbe.csproj -c Release --no-restore /clp:ErrorsOnly
Build succeeded.
338 Warning(s)
0 Error(s)
```

Safe short probe:

```text
EUTHERDRIVE_GAUNTDL_EXTRA_SERIES=5000000
[GAUNTDL:FPUCTX] load pc=ffffffff80011620 op=d48e01a0 expected=d48e01a0
fpr=14 a0=ffffffff807ffc20 opcodeOk=True contextOk=True
checkpoint extra=5000000 pc=0xffffffffa044d178
fifoWords=5 fifoPackets=2 directTriangles=10 fastFills=1 swaps=0
```

Longer diagnostic result after the early even-save path:

```text
EUTHERDRIVE_GAUNTDL_EXTRA_SERIES=50000000
checkpoint extra=50000000 pc=0xffffffff800dec24 lastOp=0xafb1001c
hotpcs includes 80011620, but with roughly half the previous count after the
restore-side experiment.
```

Rejected experiment:

- A narrow GPR context restore fastpath for `80011610..80011688` was tested and
  reverted. It skipped the hot `ld r1..r31,offset(k0)` block, but immediately
  exposed a tight `80011690` syscall/low-vector loop:

```text
halt pc=ffffffff80011690 op=0000000c reason=special 0c
status=34007f03 epc=00000000010000b9
then repeated low-vector/text-data PCs such as 00000040, 0000005c, 00000060
```

Interpretation:

- The FPU save/load fastpaths are real and useful, but the next blocker is not
  just the GPR restore cost. The restore tail must be understood semantically:
  `80011690` is reached with EXL still set and a low/invalid EPC, so blindly
  fast-forwarding the GPR restore only accelerates the bad exception return.
- Next target should be the restore tail around `80011688..80011694` and how
  CP0 Status/EPC are supposed to be repaired before leaving the exception
  context.

## 2026-05-31 Continuation: Restore Tail Instrumentation

Additional implementation in `EutherDrive.Core/Arcade/Vegas/GauntletDarkLegacyAdapter.cs`:

- Added passive `[GAUNTDL:EXCRET]` diagnostics for the exception restore tail.
  The trace does not alter execution. It logs CP0 status/cause/EPC, `sp/ra/k0/k1`,
  and the restore context fields that the runtime actually uses:
  `ctxStatus=+0x100`, `ctxHi=+0x108`, `ctxLo=+0x110`, `ctxEpc=+0x298`,
  and `ctxRa=+0x0f8`.
- The diagnostic covers both observed restore-tail layouts:
  `80011728..80011738` for the normal runtime context restore from the warm
  snapshot, and `80011680..80011688` for the later low-PC exception loop.

Verification:

```text
dotnet build tools/GauntletProbe/GauntletProbe.csproj -c Release --no-restore /clp:ErrorsOnly
Build succeeded.
337 Warning(s)
0 Error(s)
```

First restore from the warm snapshot is valid:

```text
EUTHERDRIVE_GAUNTDL_EXTRA_STOP_PC=ffffffff80011738
EUTHERDRIVE_GAUNTDL_EXTRA_SERIES=100000
[GAUNTDL:FPUCTX] load pc=ffffffff80011620 op=d48e01a0 expected=d48e01a0
fpr=14 a0=ffffffff807ffc20 opcodeOk=True contextOk=True
[GAUNTDL:EXCRET] pc=ffffffff80011738 op=0000000f
status=34007f03 cause=8800 epc=ffffffff800479d8
k0=ffffffff807ffc20 ctxStatus=34007f03 ctxEpc=ffffffff800479d8
ctxRa=ffffffff800479d8
checkpoint extra=37 pc=0xffffffff80011738
```

Letting that same run continue reaches normal runtime:

```text
EUTHERDRIVE_GAUNTDL_EXTRA_SERIES=100000
checkpoint extra=100000 pc=0xffffffff8004d9ac lastOp=0x3c03800b
```

The 5M sanity remains stable:

```text
EUTHERDRIVE_GAUNTDL_EXTRA_SERIES=5000000
checkpoint extra=5000000 pc=0xffffffffa044d178 lastOp=0x24840004
fifoWords=5 fifoPackets=2 directTriangles=10 fastFills=1 swaps=0
```

The 50M run still reaches the known IRQ-dispatch boundary:

```text
EUTHERDRIVE_GAUNTDL_EXTRA_SERIES=50000000
checkpoint extra=50000000 pc=0xffffffff800dec24 lastOp=0xafb1001c
fifoWords=5 fifoPackets=2 directTriangles=20 fastFills=2 swaps=0
hotpcs=800110c4..800110d0 and 80011620
```

Important interpretation:

- The initial FPU load/restore tail is not corrupt. It restores `EPC=800479d8`
  and returns to runtime correctly.
- The later bad loop begins after the real pending IRQ path rejects the
  timer-only suppress fastpath at `800de8f0..800de90c` with `pending=3000`
  (`sio=1 ide=1`), then execution falls through unsupported low-memory/text-data
  PCs under `EUTHERDRIVE_GAUNTDL_CONTINUE_AFTER_UNSUPPORTED=1`.
- Those continued low-PC instructions generate fresh exception contexts with
  low/increasing `ctxEpc` values:

```text
[GAUNTDL:EXCRET] pc=ffffffff80011688 op=42000018
status=34007f03 cause=0000 epc=00000000010000b9
k0=000000000000fd60 ctxStatus=34007f03 ctxHi=00000050
ctxLo=ffffffb0 ctxEpc=00000000010000b9 ctxRa=0000000000000000
```

- Therefore the next target is not a generic `syscall` implementation or a GPR
  restore skip. The next real target is the pending SIO/IDE IRQ dispatch around
  `800de8e0..800dec24`, and specifically why it eventually returns/branches to
  low memory before `CONTINUE_AFTER_UNSUPPORTED` starts manufacturing bogus
  low-EPC exception frames.

## 2026-06-01 Continuation: Format Fastpath Prologue Guard

Additional implementation in `EutherDrive.Core/Arcade/Vegas/GauntletDarkLegacyAdapter.cs`:

- Added passive `[GAUNTDL:LOWPC]` diagnostics for suspicious low-PC transitions.
- Added passive `[GAUNTDL:S8DEAD]` / `[GAUNTDL:S8CHANGE]` diagnostics to track
  the copied runtime frame pointer around the low-PC failure.
- Fixed `TryFastPathKnownRuntimeFormatBufferInFlight()` so it cannot trigger
  before the `80120234` format-buffer prologue has saved all callee-saved
  registers. The old guard allowed entry at `80120258`, before `afbe00e0` and
  related saves had executed, so the fastpath restored `s8/fp` and `s7` from
  uninitialized stack values (`0xdead`) and later returned through `ra=0`.
- Added `TryFastPathKnownRuntimeMountQioWaitLoop()` for the runtime mount wait
  at `800f5b1c..800f5b38`. The guard verifies the live code signature, the
  fixed mount object `80295670`, `+0x14 == 0`, and `+0x7c == 5` before writing
  completion status `0x0800` and continuing at `800f5b44`.

Verification:

```text
dotnet build tools/GauntletProbe/GauntletProbe.csproj -c Release --no-restore /clp:ErrorsOnly
Build succeeded.
337 Warning(s)
0 Error(s)
```

Warm-state probe after the fix:

```text
EUTHERDRIVE_GAUNTDL_EXTRA_SERIES=20000000,50000000
checkpoint extra=20000000 pc=0xffffffff800f5b1c lastOp=0x00000000
fifoWords=117 fifoPackets=42 directTriangles=30 fastFills=12 swaps=0

checkpoint extra=50000000 pc=0xffffffff800f5b1c lastOp=0x00000000
fifoWords=117 fifoPackets=42 directTriangles=30 fastFills=12 swaps=0
debug rtxt="Initializing Disk..."
```

Interpretation:

- The first real low return at `801152c8/801152cc` is gone after fixing the
  format-buffer fastpath prologue timing.
- Voodoo activity advances materially from the prior `fifoWords=5` /
  `fifoPackets=2` / `fastFills=2` state to `fifoWords=117` /
  `fifoPackets=42` / `fastFills=12`.
- Current blocker is now the runtime disk initialization wait around
  `800f5b10..800f5b1c`, sampling text `"Initializing Disk..."`.
- The mount object at `80295670` has status `+0x14 == 0` and state `+0x7c == 5`
  at the endpoint; completing that loop with status `0x0800` is enough to leave
  `"Initializing Disk..."`.

After adding the mount wait fastpath, the same warm-state probe reaches
`"Loading Game."` instead of the disk-init wait:

```text
EUTHERDRIVE_GAUNTDL_EXTRA_SERIES=50000000
checkpoint extra=50000000 pc=0xffffffff80102468 lastOp=0xae03037c
debug rtxt="Loading Game."
fifoWords=1960585 fifoPackets=157609 drawPackets=7792
directTriangles=372 setupTriangles=170 fastFills=930 swaps=8179
framebuffer=640x480 nonBlack=307200 colored=24296
```

A longer non-RD0-trace run confirms that this is not just a one-off advance:

```text
EUTHERDRIVE_GAUNTDL_EXTRA_SERIES=100000000
[GAUNTDL:FIX] bgloadmodel qio-complete index=0 record=ffffffff80252da0
checkpoint extra=100000000 pc=0xffffffff80103300 lastOp=0x8e110280
debug rtxt="Loading Game."
fifoWords=7958145 fifoPackets=502731 drawPackets=20598
directTriangles=1464 setupTriangles=716 fastFills=1902 swaps=56158
frameHash=0x1a2235fc
```

Endpoint note:

- `80103300` is not a new tight wait. It is inside a runtime state-bit setter
  around `801032e0`, with `s0=80262d64` and a store back to `s0+0x280`. The
  100M sample stopped there simply because the extra-step budget expired.

Remaining risk / next targets:

- The game now repeatedly cycles through the RD0 home/open path while loading:
  `first-open-error`, `first-getioq-error`, `first-no-valid-home-blocks`, and
  `second-open-error` diagnostics repeat.
- Runtime also reaches `bgloadmodel qio-complete`, so the next useful probe is a
  longer runtime/visual check or a stop-on-repeat detector for whichever loader
  PC becomes genuinely hot after 100M. The old low-PC return and
  `"Initializing Disk..."` wait are no longer active blockers.

### 200M Loading Snapshot

A reusable late-loading snapshot was saved at:

```text
/tmp/eutherdrive-gauntlet-probe/gauntdl-gauntdl24-loading-200m.warm
```

Command shape:

```sh
EUTHERDRIVE_GAUNTDL_BRINGUP_FAST=1 \
EUTHERDRIVE_GAUNTDL_FASTPATH_DIAGNOSTIC_RUNTIME=1 \
EUTHERDRIVE_GAUNTDL_CONTINUE_AFTER_UNSUPPORTED=1 \
EUTHERDRIVE_GAUNTDL_SAVE_FINAL_STATE=/tmp/eutherdrive-gauntlet-probe/gauntdl-gauntdl24-loading-200m.warm \
EUTHERDRIVE_GAUNTDL_WARMUP_STATE=/tmp/eutherdrive-gauntlet-probe/gauntdl-gauntdl24-worldfail-38m.warm \
EUTHERDRIVE_GAUNTDL_WARMUP_FRAMES=300 \
EUTHERDRIVE_GAUNTDL_EXTRA_SERIES=200000000 \
dotnet run --project tools/GauntletProbe/GauntletProbe.csproj -c Release --no-build -- \
  /home/nichlas/roms/MAME/Midway/Vegas/gauntd 300
```

Checkpoint:

```text
checkpoint extra=200000000 pc=0xffffffff800c7c04 lastOp=0x001028c0
debug rtxt="Loading Game."
fifoWords=9258107 fifoPackets=1152712 drawPackets=20598
directTriangles=1464 setupTriangles=716 fastFills=1902 swaps=218654
frameHash=0x1a2235fc
```

From that snapshot, a 20M extra-step probe and a real 60-frame run both show the
same late behavior:

```text
20M extra:
pc=0xffffffff80102524
fifoWords=9518093 fifoPackets=1282705 drawPackets=20598
directTriangles=1464 setupTriangles=716 fastFills=1902 swaps=251152
hotpcs=800b1dc4,800e3378,80121670..80121698,800b1ba0..

frames 300->360:
pc=0xffffffff8010349c
fifoWords=9304899 fifoPackets=1176108 drawPackets=20598
directTriangles=1464 setupTriangles=716 fastFills=1902 swaps=224502
```

Interpretation:

- The port is no longer stuck in BGLoadModel QIO completion; `80252da0` has
  record 0 in state 6 and later records mostly point at an uninitialized
  `80217d70` QIO shell.
- After 200M, no new draw packets, triangle counts, texture writes, or frame hash
  changes appear. Only type-1/swap traffic continues.
- `800c7c04` / `80102524` / `8010349c` are sample PCs inside active runtime
  render/message helpers, not a single obvious tight wait.
- The next useful target is the late render/message pump: `800b1dc4`,
  `800e3378`, and the callback/object path around `80121670..80121698`, rather
  than adding more BGLoadModel QIO completions.

Late render/message trace from the 200M snapshot showed that this phase is
mostly a text/progress render pump:

- `800b1dc4..800b1dd0` swaps a global color/value at `8022808c`.
- `800b1ba0..800b1c20` walks a small render-list/scratch buffer around
  `807ffcc0`.
- `800e3378..800e33f0` submits runtime text, but the observed string pointers
  were blank/empty (`80217bf8`, `802172b8`, `80217338`).
- `80121670..801216bc` formats diagnostic/progress lines from caller
  `800c7bc8`, with `802171a4` incrementing through the observed calls.

The 200M snapshot also exposed a presentation mismatch in the bring-up Voodoo
backend. The exported framebuffer was nearly empty because the renderer always
copied `_frontBufferIndex == 0`, while buffer 1 contained the visible image:

```text
buf=0/1/2
voodoo buffers=0:nz=1:white=1:colored=1 1:nz=491520:white=467224:colored=491520 2:nz=0:white=0:colored=0
framebuffer=640x480 stride=2560 nonBlack=1 colored=0
```

Added a gated bring-up renderer fallback:

```text
EUTHERDRIVE_GAUNTDL_FIX_VOODOO_DISPLAY_BUFFER=1
```

When enabled, the renderer keeps the normal front buffer if it contains useful
pixels, otherwise it picks the best non-pending color buffer with more than 1024
non-zero pixels. This is a visualization aid for the current bring-up state, not
a final Voodoo swap-semantics fix.

Verification from the same 200M snapshot:

```text
EUTHERDRIVE_GAUNTDL_FIX_VOODOO_DISPLAY_BUFFER=1
EUTHERDRIVE_GAUNTDL_DEBUG_BUFFER_COUNTS=1
pc=0xffffffff800c7c04
voodoo buffers=0:nz=1:white=1:colored=1 1:nz=491520:white=467224:colored=491520 2:nz=0:white=0:colored=0
frameHash=0x838a144e
framebuffer=640x480 stride=2560 nonBlack=307200 colored=24296
```

Next target after this display fix: inspect why the late runtime keeps rendering
blank progress text and only advances type-1/swap traffic after the first real
draw batch, instead of treating the empty front buffer as proof that geometry
never rendered.

### 2026-06-01 Continuation: DCS Auto-Ack and Voodoo TMU Banks

Additional implementation in `EutherDrive.Core/Arcade/Vegas/GauntletDarkLegacyAdapter.cs`:

- Matched MAME's Gauntlet Dark Legacy IOASIC sound behavior by auto-acking DCS
  sound input reads. The 38M warm snapshot confirms this now emits `ack` after
  `data-r`, but the current loading blocker is not DCS program transfer:

```text
[GAUNTDL:DCS] data-r value=000a last=0004 reset=False lc=0400
[GAUNTDL:DCS] ack
dcs boot=128w host=5 fifo=512/0 xfer=0 state=0/0 type=0000 left=0 lc=0c00 out=000a
```

- Added passive Voodoo TMU register tracing for FIFO targets `0x2c0/0x2c1/0x2c3`
  and `0x4c0/0x4c1/0x4c3`.
- Added gated TMU register-bank separation:

```text
EUTHERDRIVE_GAUNTDL_FIX_VOODOO_TMU_REG_BANKS=1
```

`BRINGUP_FAST=1` also enables this flag. FIFO writes with target bit `0x200`
populate TMU0, and writes with target bit `0x400` populate TMU1. The current
sampler reads TMU0 first, then TMU1, then the legacy collapsed register values.
This avoids TMU1 state overwriting TMU0 state in the bring-up backend while
preserving compatibility with old warm snapshots.

Verification:

```text
dotnet build tools/GauntletProbe/GauntletProbe.csproj -c Release --no-restore /clp:ErrorsOnly
Build succeeded.
338 Warning(s)
0 Error(s)
```

Probe from the 38M warm snapshot to 50M extra steps:

```text
EUTHERDRIVE_GAUNTDL_BRINGUP_FAST=1
EUTHERDRIVE_GAUNTDL_FIX_VOODOO_DISPLAY_BUFFER=1
EUTHERDRIVE_GAUNTDL_DEBUG_BUFFER_COUNTS=1
EUTHERDRIVE_GAUNTDL_WARMUP_STATE=/tmp/eutherdrive-gauntlet-probe/gauntdl-gauntdl24-worldfail-38m.warm
EUTHERDRIVE_GAUNTDL_EXTRA_SERIES=50000000
```

Result:

```text
checkpoint extra=50000000 pc=0xffffffff80102468
debug rtxt="Loading Game."
tmu0=8C24100F/00002000/00000000
tmu1=0C24100F/FF802000/00000000
voodoo textured=tri:7794:covered:914:rejected:6880:pixels:65453119:zero:57436214
voodoo textureMap=writes=5377000:nz=24966:zero=5352034:touched=16385:first=0x000000:last=0x015554
voodoo buffers=0:nz=1:white=1:colored=1 1:nz=491520:white=362730:colored=491520 2:nz=0:white=0:colored=0
frameHash=0x5c2396c6
framebuffer=640x480 stride=2560 nonBlack=307200 colored=89238
```

Interpretation:

- The guest really writes different state into the two TMU target ranges; the
  old low-byte-only register collapse hid that distinction.
- Separating the banks changes visible output (`colored=89238` in this 50M
  check with display fallback), so this is a real Voodoo bring-up correction.
- The game is still in the `Loading Game.` phase. The next blocker remains the
  late loader/render-message path: after the first draw batch, the runtime keeps
  emitting type-1/swap traffic while model/QIO records after the first entry
  mostly point at the blank `80217d70` shell.

### 2026-06-01 Continuation: Late Loading Hotpath Reduction

Additional implementation:

- Extended the Gauntlet Glide state emit fastpath to accept the observed
  pre-mask entry at `80103f38`. The prior helper started at `80103f44`, missing
  the live endpoint after the `200M` loading snapshot.
- Added `TryFastPathKnownGauntletGlideRuntimeZeroStatePacketTail()` for the
  small zero-payload packet epilogue at `80102254..80102264`.
- Extended `TryFastPathKnownRuntimeDiagnosticFormatLineWrapper()` to handle the
  early prologue point at `80121674`, using live `ra` before the wrapper has
  saved it to the stack.

Verification:

```text
dotnet build tools/GauntletProbe/GauntletProbe.csproj -c Release --no-restore /clp:ErrorsOnly
Build succeeded.
337 Warning(s)
0 Error(s)
```

Progression from the 200M snapshot:

```text
Before:
20M extra -> pc=0xffffffff80102524, statusPcs=80105ea0:32498

After zero-packet tail:
20M extra -> pc=0xffffffff80121674

After early diagnostic-format wrapper:
20M extra -> pc=0xffffffff8004ede0
fifoWords=9527851 fifoPackets=1287584 swaps=252372
drawPackets=20598 directTriangles=1464 setupTriangles=716
statusPcs=80105ea0:33718
```

Current semantic blocker:

- The endpoint has moved back into the runtime world-data scan around
  `8004ed80..8004ede0`, not the render formatter.
- `8016c130` reports `count=13` and table `802e1000`, but that table is only
  the bring-up stub data generated by `ApplyKnownRuntimeWorldDataTableRepair()`:
  each entry has `id=1..13`, `+0x04=1`, and the rest zero.
- The fallback buffers used by the allocation repair (`81000000`, `81400000`,
  `81800000`) are also effectively empty at the 200M snapshot. There is no
  already-loaded real world table to wire up.
- Model records are still consistent with this: record 0 is complete, and later
  records point at the blank `80217d70` QIO shell.

Next target:

The next useful bring-up work is not another render fastpath. It is to replace
the current synthetic world-table stubs with real data, or trace the disk/read
path that should populate the world data before `ApplyKnownRuntimeWorldDataTableRepair()`
falls back to `802e1000`.

### 2026-06-01 Continuation: FSYS Worlddata Indexing and Table Stride

Additional raw-disk inspection:

- The `c0edbabe` FSYS directory payload format is `id24, type8, nameLen8,
  name\0`.
- Root directory payload at byte `0x0000d200` includes:
  `worlddata id=0x324`, `levels id=0x1dc`, `static_lr id=0x5d`.
- `/worlddata` is the directory with `.` id `0x324`, header/payload at
  `LBA 0x787c5/0x787c6` (`byte 0x0f0f8a00/0x0f0f8c00`).
- `/worlddata` entries include:
  `mount.wad id=0x325`, `castle.wad id=0x326`, `desert.wad id=0x327`,
  `forest.wad id=0x328`, `temple.wad id=0x329`, `hell.wad id=0x32a`,
  `town.wad id=0x32b`, `battle.wad id=0x32c`, `ice.wad id=0x32d`,
  `dream.wad id=0x32e`, `sky.wad id=0x32f`, `secret.wad id=0x330`,
  and `test.wad id=0x331`.
- Assuming sequential file headers after the `/worlddata` directory, `test.wad`
  payload is at `LBA 0x7883f` (`byte 0x0f107e00`) with size `0x358`.
  Its first word is `0x000002f8`.

Implementation updates:

- `ApplyKnownRuntimeWorldDataReadBufferRepair()` now hydrates the fallback
  buffer `81000000` with `/worlddata/test.wad` bytes when that repair path is
  reached.
- `ApplyKnownRuntimeWorldDataTableRepair()` now uses the table stride the
  runtime lookup actually uses: `index << 7`, i.e. `0x80` bytes per entry. The
  old stub used `0x40`.
- Stub entry `+0x04` is now `0xffffffff` instead of `1`, because the runtime
  world lookup masks that word with a caller-provided filter before accepting an
  entry.
- `EUTHERDRIVE_GAUNTDL_TRACE_WORLD_DATA_TABLE=1` was capped on the trace path;
  before this, already-installed stubs could flood logs because the trace count
  only advanced on the repair branch.

Disassembly notes:

```text
8004ee20 lookup:
  global = *(8016c130)
  start = lh(global+0x16) + 1
  count = lh(global+0x18)
  table = *(global+0x1c)
  entry = table + index * 0x80
  accept if (*(entry+0x04) & filter) != 0
  store selected entry to 8016c13c
```

Verification:

```text
dotnet build tools/GauntletProbe/GauntletProbe.csproj -c Release --no-restore /clp:ErrorsOnly
Build succeeded.
337 Warning(s)
0 Error(s)
```

12M extra from `gauntdl-gauntdl24-worldfail-38m.warm` confirms the corrected
stub layout:

```text
world-data-table pc=ffffffff8004ecc0 global=ffffffff8016c130 table=ffffffff802e1000 count=13
802e1000: id=1 mask=ffffffff
802e1080: id=2 mask=ffffffff
802e1100: id=3 mask=ffffffff
```

50M extra from the same snapshot remains visually/runtime-equivalent:

```text
pc=0xffffffff80102edc
rtxt="Loading Game."
fifoWords=2086242 fifoPackets=160741 drawPackets=8010
directTriangles=372 setupTriangles=170
frameHash=0x5c2396c6
framebuffer=640x480 stride=2560 nonBlack=307200 colored=89238
```

Current endpoint:

- `80102edc` is in a runtime render/state jump-table path, not a simple empty
  epilogue. The local code around `80102d80..80102f70` prepares render flags,
  stores state at offsets such as `+0x300/+0x304`, then dispatches through a
  table at `80157aa0`.
- `80252da0` still shows only record 0 complete; later records continue pointing
  at the blank `80217d70` shell.
- The stride/mask fix is real correctness work, but it does not yet advance out
  of `Loading Game.`. The remaining blocker is still real world/model metadata
  hydration after the synthetic world table lets the loader progress.

### 2026-06-01 Continuation: Worlddata Loader State Trace

Additional implementation:

- Added passive `EUTHERDRIVE_GAUNTDL_TRACE_WORLD_DATA_LOADER=1` instrumentation
  for the worlddata loader state around `8004d430..8004dc80`.
- The trace is gated by live-code signatures and capped at 48 lines. It logs the
  relevant globals `80227c90..80227cb8` plus `80236ae0/80236ae4`, and does not
  change execution.

Verification:

```text
dotnet build tools/GauntletProbe/GauntletProbe.csproj -c Release --no-restore /clp:ErrorsOnly
Build succeeded.
338 Warning(s)
0 Error(s)
```

Longer progression after the corrected table stride/mask still stays in the
same loading phase:

```text
100M extra:
pc=0xffffffff80102244
rtxt="Loading Game."
drawPackets=20598 directTriangles=1464 setupTriangles=716
swaps=58770 frameHash=0x5c2396c6

200M extra:
pc=0xffffffff80102564
rtxt="Loading Game."
drawPackets=20598 directTriangles=1464 setupTriangles=716
swaps=227360 frameHash=0x5c2396c6
statusPcs=80105ea0:219867,80105eac:4688,801005b8:838,8005eda4:420
```

Static scans from the 12M checkpoint:

```text
addrLoadScan 80252da0:
  80050f68, 80051364, 800514ec,
  800abad4, 800abc84, 800abdb4,
  800ac074, 800ac1f8, 800ac20c,
  800ac220, 800ac2e8

memRefScan 80227cb8:
  8001ecdc, 8001ecec, 8001ecf8, 8001ed10,
  8004d448, 8004d4f8, 8004d6c4, 8004da04,
  8004da10, 8004da58, 8004dc44, 8004dc4c,
  8004dc54, 80050a54, 80050a6c
```

Important disassembly observations:

- `8004d4f8` stores a runtime allocation/read result into `80227cb8`.
- `8004d6c4..8004d710` is the branch that consumes `80227cb8` and posts the
  command with callback `8005d630`.
- `8004dc44..8004dc54` clears `80227ca8`, `80227c98`, `80227cb8`, and
  `80227cac`.
- `80050a70..80050ac0` computes a total from `80227cb8` and `80227cac` by
  reading pointed objects' `+0x50/+0x58` fields.

Runtime probe with `EUTHERDRIVE_GAUNTDL_TRACE_WORLD_DATA_LOADER=1` from
`gauntdl-gauntdl24-worldfail-38m.warm` to 12M emitted no loader trace lines.
The loader path either already ran before the warm snapshot point or is bypassed
after the fallback world table is installed.

50M byte snapshot notes:

- `80252da0` record 0 is complete (`+0x0c = 6`, `+0x10 = 1`).
- Subsequent model records still carry only the blank QIO-shell pointer
  `80217d70`.
- The only hydrated BGLoadModel QIO remains record 0:
  `qio=80217c58`, `object=80295750`, `callback=800ab4e4`,
  `dest=802e1718`, `bytes=0x2000`.
- `81000000` begins with the synthetic stub word `0x00000001`, not the
  `/worlddata/test.wad` first word. That confirms the `test.wad` buffer
  hydration path has not fired in this warm-snapshot route.

Next target:

- Do not spend more time on `80102edc`/`80102564`; they are sample points in
  active render/state helpers.
- The useful next probe is earlier than the current warm snapshot: capture or
  trace the `8004d4f8 -> 80227cb8` allocation/read path before
  `ApplyKnownRuntimeWorldDataTableRepair()` installs synthetic stubs, then use
  that to replace the stub-only table with real world/model metadata.

### 2026-06-01 Continuation: Static World Descriptor Copy

Pre-repair stop at the actual fail-path PC from
`gauntdl-current-regenerated-f300-1m.warm`:

```text
EUTHERDRIVE_GAUNTDL_EXTRA_STOP_PC=ffffffff8004ecc0
checkpoint extra=8243389 pc=0xffffffff8004ecc0
s0=8015bf24 s2=8015bef4 ra=8004efb0
8016c130: all zero before the repair
80227cb8: zero before the repair
```

The static list at `8015bef4` is real world metadata, not code or scratch:

```text
8015bef4 +0x00: id=1  name="castle"  tag='A' secondary="powerups"
8015bef4 +0x2c: id=2  name="mount"   tag='B' secondary="powerups"
8015bef4 +0x58: id=3  name="desert"  tag='C' secondary="powerups"
...
```

Disassembly around the world helpers confirms that runtime-selected entries are
not just id/mask pairs:

- `8004ee20` still indexes the runtime table with `index << 7` and tests
  `entry+0x04` as a filter mask.
- `8004f450..8004f4a0` compares selected-entry bytes at `+0x08..+0x0b`
  against caller strings.
- `8004f0c0..8004f340` checks runtime table flag bits and also updates fields
  in the static `8015bef4` descriptors.

Implementation update:

- `ApplyKnownRuntimeWorldDataTableRepair()` still builds the fallback runtime
  table at `802e1000` with 0x80-byte entries, but now seeds each entry from the
  static descriptor list:
  - `entry+0x00 = static id`
  - `entry+0x04 = 0xffffffff`
  - `entry+0x08..+0x2f = static +0x04..+0x2b`, including both the world name and
    secondary name such as `powerups`/`powerups2`.

Verification:

```text
dotnet build tools/GauntletProbe/GauntletProbe.csproj -c Release --no-restore /clp:ErrorsOnly
Build succeeded.
337 Warning(s)
0 Error(s)
```

Table layout check:

```text
802e1000:
  +0x000 id=1 mask=ffffffff name="castle" secondary="powerups"
  +0x080 id=2 mask=ffffffff name="mount"
```

Progression is still equivalent at the known late cap:

```text
50M extra from worldfail-38m:
pc=0xffffffff80102edc
drawPackets=8010 directTriangles=372 setupTriangles=170

100M extra from worldfail-38m:
pc=0xffffffff80102244
drawPackets=20598 directTriangles=1464 setupTriangles=716
frameHash=0x5c2396c6
```

Interpretation:

- The fallback runtime world table is no longer an id-only stub; selected-entry
  name comparisons now have meaningful data.
- This is correctness work, but not the current late-loading blocker. The next
  target remains the model/load path after the first BGLoadModel QIO: only model
  record 0 hydrates, while later records point at the blank `80217d70` shell.

### 2026-06-01 Continuation: BGLoadModel Trace and Hot-PC Recheck

Additional passive instrumentation in
`EutherDrive.Core/Arcade/Vegas/GauntletDarkLegacyAdapter.cs`:

- Added `EUTHERDRIVE_GAUNTDL_TRACE_BGLOADMODEL_RECORDS=1`.
- The trace covers the static BGLoadModel/QIO helper windows around
  `800abaa0`, `800abc60`, `800ac040..800ac4f0`, and
  `800c92f0..800c9820`.
- Each line reports the core BGLoadModel globals
  `80238060/80238068`, `8021f154..8021f184`, the first 8 model records at
  `80252da0`, and the first 4 expected QIO shells at
  `80217c58 + index * 0x118`.
- This is trace-only; it does not modify execution.

Verification:

```text
dotnet build tools/GauntletProbe/GauntletProbe.csproj -c Release --no-restore /clp:ErrorsOnly
Build succeeded.
337 Warning(s)
0 Error(s)
```

Runtime checks:

```text
EUTHERDRIVE_GAUNTDL_TRACE_BGLOADMODEL_RECORDS=1
EUTHERDRIVE_GAUNTDL_EXTRA_SERIES=12000000
EUTHERDRIVE_GAUNTDL_WARMUP_STATE=/tmp/eutherdrive-gauntlet-probe/gauntdl-current-regenerated-f300-1m.warm

Result:
pc=0xffffffff800bd10c
drawPackets=526
directTriangles=30 setupTriangles=0
```

No BGLoadModel-record trace lines were emitted in this route. A cold run to
frame 300 with the same trace also emitted no BGLoadModel-record lines:

```text
frame=300
pc=0xffffffff800b0af4
drawPackets=1368
directTriangles=30 setupTriangles=0
```

Hot-PC profiling from the 1M warm snapshot to 30M extra steps initially pointed
at `8004d9a8..8004d9c0` and `8004e08c..8004e09c`:

```text
hotpcs=
  80011068..80011074:29108
  8004d9a8..8004d9c0:29065
  8004e08c..8004e09c:29065
```

That looked like a worlddata loader loop, so `TRACE_WORLD_DATA_LOADER` was
extended to cover those exact PCs and the relevant `80236a60` object fields.
The follow-up stop-PC check showed this was a false lead for the current
semantic blocker:

```text
EUTHERDRIVE_GAUNTDL_EXTRA_STOP_PC=ffffffff8004d9a8
checkpoint extra=13577 pc=0xffffffff8004d9a8 lastOp=0x00000000
ra=0xffffffff8004e094
80227c90..80227ccf: all zero
80236a60..80236aff: all zero
801acbd8: zero
```

Interpretation:

- The early `8004d9a8` hits are not the populated worlddata loop; they occur
  while that address still reads as zero code and before any of the relevant
  globals are installed.
- The old hot-PC output can be polluted by this early zero-code path. Do not use
  `8004d9a8` as the next target unless the probe also confirms a non-zero opcode
  and populated `80236a60` state at the same stop.
- The model/QIO issue is still open. The useful next probe is a state-delta
  check on `80252da0`/`80217c58` across a save point, or a broader write-source
  trace for those RAM ranges, rather than chasing the `8004d9a8` hot-PC sample.

### 2026-06-02 Continuation: BGLoadModel State Delta and QIO Alias

Added a sampled passive state-delta trace:

```text
EUTHERDRIVE_GAUNTDL_TRACE_BGLOADMODEL_STATE_DELTA=1
```

It reports changes to the BGLoadModel globals, the first model records at
`80252da0`, and the expected QIO shells at `80217c58 + index * 0x118`. The
regular step path samples every 4096 instructions; the post-QIO-completion path
still checks immediately so repair-side changes are visible.

Verification:

```text
dotnet build tools/GauntletProbe/GauntletProbe.csproj -c Release --no-restore /clp:ErrorsOnly
Build succeeded.
337 Warning(s)
0 Error(s)
```

The state-delta probe from
`/tmp/eutherdrive-gauntlet-probe/gauntdl-current-regenerated-f300-1m.warm` to
30M extra steps exposed the exact aliasing sequence:

```text
pc=800c9300: qio shells are initialized at 0x118 stride
pc=800ac3c4/800ac4dc: record 0 moves through states 1..6
pc=800abbc0: later records receive qio pointer 80217c58
pc=800abb10: record state remains, qio0 status is reset/toggled
```

For later records, `s1` advanced through the model table:

```text
s1=80252db8 -> record 1
s1=80252dd0 -> record 2
s1=80252de8 -> record 3
...
```

but each record received the same QIO pointer:

```text
record 1 +0x08 = 80217c58
record 2 +0x08 = 80217c58
record 3 +0x08 = 80217c58
```

Implementation update:

- Added a narrow `ApplyKnownRuntimeBgLoadModelQioAliasRepair()` at the observed
  post-alias PC `800abb10`.
- If the live record is inside `80252da0 + index * 0x18`, `index > 0`, and
  `record+0x08 == 80217c58`, the repair rewrites `record+0x08` to
  `80217c58 + index * 0x118`.
- The repair is gated by the existing BGLoadModel bring-up flag and only touches
  records that already show the alias value.

Verification:

```text
dotnet build tools/GauntletProbe/GauntletProbe.csproj -c Release --no-restore /clp:ErrorsOnly
Build succeeded.
337 Warning(s)
0 Error(s)
```

30M probe from the same 1M warm snapshot now logs:

```text
[GAUNTDL:FIX] bgloadmodel-qio-alias index=1 record=80252db8 old=80217c58 new=80217d70
[GAUNTDL:FIX] bgloadmodel-qio-alias index=2 record=80252dd0 old=80217c58 new=80217e88
[GAUNTDL:FIX] bgloadmodel-qio-alias index=3 record=80252de8 old=80217c58 new=80217fa0
...
```

Final 30M table dump confirms the alias repair sticks:

```text
80252da0:
  record 0: state=6, done=1
  record 1 +0x08 = 80217d70
  record 2 +0x08 = 80217e88
  record 3 +0x08 = 80217fa0
  record 4 +0x08 = 802180b8
  record 5 +0x08 = 802181d0
  record 6 +0x08 = 802182e8
  record 7 +0x08 = 80218400
```

Progression at 30M is still unchanged:

```text
pc=0xffffffff800b1c38
rtxt="Loading Game."
drawPackets=3119 directTriangles=30 setupTriangles=0
frameHash=0x9ac85dc5
```

100M sanity from the same warm snapshot confirms the fix does not yet advance
past the known late-loading plateau:

```text
pc=0xffffffff800c7bec
rtxt="Loading Game."
drawPackets=20598 directTriangles=1464 setupTriangles=716
texWrites=6366778
frameHash=0x5c2396c6
framebuffer colored=89238
```

Interpretation:

- The record-pointer alias is now fixed, but the backing QIO shells after
  `80217c58` remain blank except for their init status words.
- The next semantic blocker is QIO-shell request hydration/population for
  `80217d70`, `80217e88`, etc., not record-table pointer selection.

### 2026-06-02 Continuation: BGLoadModel QIO Request Trace

Added a passive request-side trace:

```text
EUTHERDRIVE_GAUNTDL_TRACE_BGLOADMODEL_QIO_REQUESTS=1
```

It is intentionally narrow and reports the key BGLoadModel/QIO sites:
`800ac350`, `800c9678`, `800c9944`, `800abbc0`, and `800abb10`.

The trace confirms the root of the alias one step earlier:

```text
idx=1 record=80252db8 expectedQio=80217d70 pc=800c9944 v0=80217c58
idx=2 record=80252dd0 expectedQio=80217e88 pc=800c9944 v0=80217c58
idx=3 record=80252de8 expectedQio=80217fa0 pc=800c9944 v0=80217c58
```

A trial repair was added behind an explicit experiment flag only:

```text
EUTHERDRIVE_GAUNTDL_FIX_RUNTIME_BGLOADMODEL_QIO_CREATE_ALIAS=1
```

That experiment rewrites the `800c9944` return slot (`s8+0x20`) from
`80217c58` to `80217c58 + index * 0x118` before `lw v0, 0x20(s8)`.
It is not enabled by `BRINGUP_FAST`.

The experiment is informative but currently regresses progression:

```text
[GAUNTDL:FIX] bgloadmodel-qio-create-alias index=1 record=80252db8 slot=807ffd40 old=80217c58 new=80217d70
30M result with experiment:
  pc=0xffffffff800abaa4
  drawPackets=0
  record 1 +0x08 = 80217d70
  qio 80217d70 still blank except status ffffffff
```

Baseline without the experiment is restored:

```text
30M result without experiment:
  pc=0xffffffff800b1c38
  drawPackets=3119
  directTriangles=30 setupTriangles=0
  frameHash=0x9ac85dc5
```

Interpretation:

- The creator's returned pointer is wrong for records after index 0, but simply
  returning the separate shell is too early/incomplete: the shell metadata is
  still not populated.
- The next useful repair is not just pointer selection. It must also populate or
  replay the QIO request metadata for `80217d70`, `80217e88`, etc. (object,
  callback, destination, byte count, and path/read source), then let the existing
  completion/hydration path consume those shells.

### 2026-06-02 Continuation: BGLoadModel QIO Metadata Replay

Added an isolated metadata-replay experiment:

```text
EUTHERDRIVE_GAUNTDL_FIX_RUNTIME_BGLOADMODEL_QIO_REQUEST_METADATA=1
```

At `800c9944`, before the caller reloads the aliased QIO return slot, the
repair fills the expected per-index QIO shell:

```text
qio = 80217c58 + index * 0x118
object = live a0, observed 80295750
callback = 800ab4e4
dest = 802e1718 + index * 0x2000
bytes = 0x2000
status = 0
object + 0x14 low bits = 0x300b
```

This leaves the original return value alone; the existing `800abb10` alias
repair still redirects the model record to the per-index shell after the guest
stores the aliased pointer.

30M probe from `gauntdl-current-regenerated-f300-1m.warm` confirms the important
change:

```text
[GAUNTDL:FIX] bgloadmodel-qio-request-metadata index=1 qio=80217d70 object=80295750 dest=802e3718 bytes=00002000
[GAUNTDL:FIX] bgloadmodel qio-complete index=1 record=80252db8 qio=80217d70 object=80295750 objectStatus=0000300b bytes=00002000 data=hydrated:@001b0830/static-lr-bgmodel-callback/base=0007d000/mapped/first=00000012
[GAUNTDL:FIX] bgloadmodel qio-complete index=2 record=80252dd0 qio=80217e88 ...
...
```

So the per-index QIO shells are no longer inert: completion now fires for
records after index 0.

30M progression remains equivalent:

```text
pc=0xffffffff800b1c38
drawPackets=3119 directTriangles=30 setupTriangles=0
frameHash=0x9ac85dc5
```

100M progression changes materially compared with the previous plateau:

```text
Before metadata replay:
  pc=0xffffffff800c7bec
  drawPackets=20598 directTriangles=1464 setupTriangles=716
  frameHash=0x5c2396c6

With metadata replay:
  pc=0xffffffff8004f2a0
  drawPackets=20620 directTriangles=1470 setupTriangles=720
  frameHash=0xec4ad078
  framebuffer colored=69805
```

The new endpoint is in the world-data/static-descriptor flag path, not the QIO
poller. A stop-PC probe at `8004f2a0` with
`EUTHERDRIVE_GAUNTDL_TRACE_WORLD_DATA_FLAGS=1` shows:

```text
global18=0000000d global1c=802e1000 selected=00000000
pc=8004f240 scans s1=0..0xc over static descriptors at 8015bef4 + s1*0x2c
pc=8004f29c v0=0 v1=0xc s2=8015bef4 selected=0
```

Interpretation:

- Metadata replay is a useful correctness repair and should be kept available,
  but it is still marked experimental because it currently uses synthetic
  destination stride and the static-lr fallback read offset for all records.
- The next semantic blocker has moved back to world selection/descriptor flags:
  the fallback world table is present at `802e1000`, but `8016c13c` remains zero
  through the scan ending at `8004f2a0`.
- The next useful probe is either a real-path/LBA mapping for the per-index QIO
  paths, or a targeted disassembly/trace of `8004f240..8004f2a0` to determine
  why the static descriptor scan never selects a world entry.

### 2026-06-02 Continuation: QIO Record Alias Scan and FSYS Offset Probes

The 200M metadata-replay snapshot was saved at:

```text
/tmp/eutherdrive-gauntlet-probe/gauntdl-qio-metadata-200m.warm
```

From that snapshot, compact state-delta tracing showed a new alias failure after
the per-index QIO shells had completed:

```text
records=0:.../state=6;1:.../80218518/...;2:.../80218518/...;...
qio=1:80295750/800ab4e4/802e3718/00002000/00002000/00000002
```

`80218518` is QIO slot 8 (`80217c58 + 8 * 0x118`), so records 1-7 were no
longer pointing at their completed slots. The alias repair now scans records
1-15 and canonicalizes any `record+0x08` pointer that is already inside the
known QIO-shell array to `80217c58 + index * 0x118`. This fixes both the old
base-pointer alias and the later slot-8 collapse:

```text
[GAUNTDL:FIX] bgloadmodel-qio-alias pc=ffffffff800c7c10 index=1 old=ffffffff80218518 new=ffffffff80217d70
...
records=1:.../80217d70/...;2:.../80217e88/...;3:.../80217fa0/...
```

A 20M resume from the 200M snapshot with the record repair still stays on the
late text/progress pump:

```text
pc=0xffffffff80103308
drawPackets=20620 directTriangles=1470 setupTriangles=720
frameHash=0xec4ad078
hotpcs=800b1dc4,800e3378,80121670,800c7c10...
```

So the record alias repair is real correctness work, but not the remaining
semantic blocker.

Raw FSYS inspection found the relevant directory chain:

```text
/players id=1128
/players/war id=1129
/players/war/yel00 id=1232
/players/war/yel00/textures.rom id=1234
```

The nearby `yel00` directory payload is at `0x12b9ee00`, with adjacent candidate
file payloads around `0x12ba5400` and `0x12baf200`. Added an isolated direct
hydration probe:

```text
EUTHERDRIVE_GAUNTDL_FIX_RUNTIME_BGLOADMODEL_QIO_HYDRATE_DIRECT_BASE=0x...
```

For blank replayed BGLoadModel QIO paths, it reads:

```text
directBase + (qioIndex - 1) * 0x2000
```

Both tested candidates are wrong for the current replay:

```text
directBase=0x12baf200 -> pc=0xffffffffa044d178, drawPackets=0, frameHash=0xf29eb67c
directBase=0x12ba5400 -> same low-PC/FPU exception regression
```

Keep the direct-base probe off by default. The next useful path is either
mapping file id `0x4d2` through the real FSYS index table, or returning to the
world-selection scan at `8004f240..8004f2a0`.

### 2026-06-02 Continuation: Snapshot Baseline and World Trace Gate

After the commit `5298dccb`, a rerun from the regenerated f300 snapshot:

```text
/tmp/eutherdrive-gauntlet-probe/gauntdl-current-regenerated-f300-1m.warm
```

regressed within 5M extra CPU steps, both with and without the world-selection
experiment:

```text
pc=0xffffffffa044d178
drawPackets=0 directTriangles=10 setupTriangles=0
frameHash=0xf29eb67c
```

Do not use that snapshot as the current continuation baseline.

The previously saved 200M metadata snapshot remains the good baseline. A 5M
resume from it with metadata replay and the QIO alias scan stays on the known
plateau:

```text
snapshot=/tmp/eutherdrive-gauntlet-probe/gauntdl-qio-metadata-200m.warm
pc=0xffffffff800e3378
drawPackets=20620 directTriangles=1470 setupTriangles=720
frameHash=0xec4ad078
framebuffer colored=69805
```

`TraceKnownRuntimeWorldDataFlags` now skips the early all-zero `global18`,
`global1c`, and `selected` cases and allows up to 256 logged entries. This is
diagnostic only; it prevents the trace budget from being consumed before the
world-selection globals become meaningful.

The existing world-selection experiment was also corrected to hook the actual
branch instruction at `8004f29c`, not the following delay-slot/nop at
`8004f2a0`. With:

```text
EUTHERDRIVE_GAUNTDL_FIX_RUNTIME_WORLD_SELECTION=1
```

the 200M snapshot now logs:

```text
[GAUNTDL:FIX] world-selection pc=ffffffff8004f29c selected=ffffffff802e1000 id=00000001 mask=ffffffff name=castle
```

A 5M resume with that experiment active remains stable but does not move past
the current render plateau:

```text
pc=0xffffffff801035a0
drawPackets=20620 directTriangles=1470 setupTriangles=720
frameHash=0xec4ad078
```

Interpretation: world selection being zero was a real local defect, but not the
remaining blocker by itself. Continue with the real FSYS/file-id mapping or the
late text/progress pump around `800e3378` / `80102b20` / `801035a0`.

### 2026-06-05 Continuation: BGLoadModel QIO Record Register Probe

`TraceKnownRuntimeBgLoadModelQioRequests` now reports which callee-saved
register provided the record pointer. It first checks `s1` and falls back to
`s0`, matching the active `800ac350`/`800c9944` path where the BGLoadModel
record is often still in `s0`.

The same register fallback is used by the opt-in QIO metadata and create-alias
experiments, so future probes will not silently miss valid nonzero records if
the guest keeps them in `s0`.

240-frame verification with metadata replay:

```text
EUTHERDRIVE_GAUNTDL_FIX_RUNTIME_BGLOADMODEL_QIO_REQUEST_METADATA=1
EUTHERDRIVE_GAUNTDL_TRACE_BGLOADMODEL_QIO_REQUESTS=1
```

Key observations:

```text
idx=0 record=s0:80252da0 at 800ac350/800c9678/800c9944
bgloadmodel-qio-request-metadata-slot0 ... qio=80217c58 dest=802e1718
later textures.rom create has s0=00002000, s1=188d2303, so it is not a model record
```

The run changed rendering/progression but still did not populate per-index
BGLoadModel records or QIO shells:

```text
frame=240 pc=0xffffffff800a92b0 frameHash=0xdd724f6b
drawPackets=18344 directTriangles=660 setupTriangles=313
record table: only record 0 has state; records 1+ remain zero
qio 80217d70+ remain blank/status-only
802e3718 remains all zero
framebuffer colored=31878
```

Interpretation: the earlier `idx=-1` trace was partly a trace blind spot, but
not the active blocker. The current short castle path still only drives
BGLoadModel record 0 through QIO create. The next useful target is the
record-builder/asset-loop handoff before `800ac350`, specifically why asset
slots 1..8 are parsed but never become live model records with distinct QIO
requests.

### 2026-06-05 Continuation: Combined BGLoadModel Experiments

Combined these opt-in experiments:

```text
EUTHERDRIVE_GAUNTDL_FIX_RUNTIME_BGLOADMODEL_ASSET_NAMES=1
EUTHERDRIVE_GAUNTDL_FIX_RUNTIME_BGLOADMODEL_DISTINCT_SOURCES=1
EUTHERDRIVE_GAUNTDL_FIX_RUNTIME_BGLOADMODEL_QIO_REQUEST_METADATA=1
```

This is the best visual/progression signature from the current short castle
path, but it is still not boot:

```text
frame=300 pc=0xffffffff8004ecd0 frameHash=0x5335d5be
drawPackets=27461 directTriangles=941 setupTriangles=453
texWrites=6424515 framebuffer colored=286179
```

The 420-frame continuation plateaus with the same frame hash and unchanged
geometry/texture counts:

```text
frame=420 pc=0xffffffff800c80f0 frameHash=0x5335d5be
drawPackets=27461 directTriangles=941 setupTriangles=453
texWrites=6424515 swaps=40386 framebuffer colored=286179
```

Memory dumps confirm the remaining BGLoadModel gap:

```text
record table 80252da0: only record 0 has state; records 1+ remain zero
qio shells 80217d70+ remain blank/status-only
802e3718 remains all zero
asset table slots 1..8 point to 802e3718/802e5718/... but names remain empty
```

Stop-PC tracing at `800c80f0` with CPU range `800c80e0..800c8120` shows this is
the runtime log-ring helper, returning the log buffer at `802171b8`, not the
root model-load state:

```text
pc=800c80f0 op=90c371b2 lbu from 802171b2
pc=800c8118/811c builds v0=802171b8
ra=800c8364
```

It is hit from several callers. The most relevant late asset/source case has:

```text
s0=802e3718 s1=8013d968 s2=802e3718 s3=8013b07c a3=3
```

Interpretation: the combined experiments materially improve the rendered
signature, but the plateau is a log/progress pump over still-empty per-index
asset buffers. Continue by tracing the caller around `ra=800c8364` together
with the asset-source hydration path for `802e3718+`, not by patching
`800c80f0` itself.

### 2026-06-05 Continuation: Distinct Source Asset Names

The `800c8364` caller trace confirms the `800c80f0` stop is a runtime
formatter/log-ring path. It repeatedly formats diagnostic text via
`80021670`; relevant strings include allocator/QIO messages at `8013d900`
and `_LoadModel: > max %d models` / `textures.rom` near `8013b060`. This
does not identify a new wait loop.

Fixed the opt-in BGLoadModel asset-name repair so it accepts the distinct
per-index source pointers produced by
`EUTHERDRIVE_GAUNTDL_FIX_RUNTIME_BGLOADMODEL_DISTINCT_SOURCES`, not only the
old repeated `802e1718` pointer. With asset names + distinct sources + QIO
metadata enabled, the asset table now records:

```text
0: static_lr -> 802f0e70
1: gei       -> 802e3718
2: snm       -> 802e5718
3: stk       -> 802e7718
4: kjh       -> 802e9718
5: pnk       -> 802eb718
6: geb       -> 802ed718
7: nin       -> 802ef718
8: stg       -> 802f1718
9: font_story
```

Verification still plateaus at the same render/progress signature:

```text
frame=420 pc=0xffffffff800c80f0 frameHash=0x5335d5be
drawPackets=27461 directTriangles=941 setupTriangles=453
texWrites=6424515 swaps=40386 framebuffer colored=286179
```

Interpretation: the previous "names remain empty" symptom was an experiment
interaction, now fixed. It is not the boot blocker. The blocker remains that
the per-index source buffers `802e3718..802f1718` are still zero-filled, so
the next target is source-buffer hydration or the record/QIO creation path
that should populate those buffers.

### 2026-06-05 Continuation: Distinct Source Clone Probe

Added a separate opt-in probe:

```text
EUTHERDRIVE_GAUNTDL_EXPERIMENT_RUNTIME_BGLOADMODEL_CLONE_DISTINCT_SOURCES=1
```

When enabled with distinct sources, the runtime copies the hydrated slot0
source buffer at `802e1718` into the per-index buffers before the asset parser
uses them. This is intentionally *not* part of the default distinct-source
experiment, because it changes progression and currently regresses the final
visual surface.

With clone enabled, source words for indexes 1..8 become non-zero:

```text
index=1 slot=802529a4:802e1718->802e3718 cloned=True
sourceWords=00=00000012,04=00000002,0c=0000000a,5c=0000f758
```

The run no longer lands on the old `800c80f0`/`0x5335d5be` plateau:

```text
frame=420 pc=0xffffffff800c81fc frameHash=0x81a461d7
frame=600 pc=0xffffffff800c7b78 frameHash=0x81a461d7
drawPackets=22956 directTriangles=924 setupTriangles=445
texWrites=6521475 swaps=103691 framebuffer colored=31854
```

Compared with the non-clone combined run, this is real behavioral movement
and higher texture activity, but it is not boot and it is visually worse
(`colored=31854` vs `286179`). The asset parser also rewrites entries 1..8
to `802f2e70/802f4e70/...` with empty names, so the cloned source is probably
a useful probe but not the correct payload for those slots.

No-clone sanity after adding the flag still reproduces the previous best
combined signature:

```text
frame=420 pc=0xffffffff800c80f0 frameHash=0x5335d5be
drawPackets=27461 directTriangles=941 setupTriangles=453
texWrites=6424515 framebuffer colored=286179
```

Next target: trace the clone-enabled path around `800aa974` /
`800aa98c` and the asset parser rewrite that replaces named entries with
`802f*` empty entries. That should reveal what per-index payload or count/key
state is missing, instead of blindly cloning slot0.

Follow-up: clone mode now disables the local
`bgloadmodel-known-missing-texture-caller-loop` fastpath when the key is empty,
so the clone probe does not immediately skip eight non-zero source entries as
known-missing textures. This removes the `key=<empty>` skip trace, but it does
not fix the plateau:

```text
frame=420 pc=0xffffffff800c81c4 frameHash=0x81a461d7
drawPackets=22956 directTriangles=924 setupTriangles=445
texWrites=6521475 framebuffer colored=31854
```

The asset table is still rewritten to `802f2e70/802f4e70/...` with empty names.
So the skip fastpath was masking part of the behavior, but the underlying issue
is that cloned `static_lr` source data is not the correct per-index payload.
Trace the real parser rewrite path next, likely around `800aac48..800aaee0`
with clone enabled and the empty-key skip disabled.

Parser trace with clone enabled confirms the rewrite mechanics:

```text
caller-before-parser-call index=1 s2=802e3718
entry pc=800aac48 a1=80150000 a2=1 a3=1
pre-index source=802e3718 sourceWords=00=12,04=2,0c=0a
after-source-pointer-store asset[1]=802f2e70/00000000/00000000/<empty>
caller-return asset[1]=802f2e70/00000002/00000000/<empty>
```

The source table still contains the distinct `802e*` pointers, but the parser
emits internal `802f*` payload pointers into the asset table and leaves the
names empty. Indexes 2..8 follow the same pattern (`802f4e70`,
`802f6e70`, etc.). This means the clone probe is not failing because the
distinct-source table write is lost; it is failing because the cloned
`static_lr` source stream describes unnamed/generated entries for every slot.
The next useful probe is to identify or synthesize the correct per-index
source stream contents, not to repair asset names after this parser pass.

### 2026-06-05 Continuation: Indexed Texture QIO Probe

Added an opt-in probe:

```text
EUTHERDRIVE_GAUNTDL_EXPERIMENT_RUNTIME_BGLOADMODEL_INDEXED_TEXTURE_QIO=1
```

The trace before this change showed that `QIO_REQUEST_METADATA` only hydrated
slot0 (`802e1718`) from `static_lr`; the later `textures.rom` QIO create calls
are not BGLoadModel records (`s0=0x2000`, `s1=0x188d2303`, `idx=-1`) and reuse
the same `80217c58` return slot, so the record-indexed metadata path never
fills `802e3718`, `802e5718`, etc.

The new probe catches those blank-path texture QIO returns and hydrates the
first empty distinct source slot from the existing indexed payload table. At
420/600 frames it currently hydrates only:

```text
index=1 code=gei dest=802e3718 disk=14a6f600
index=2 code=snm dest=802e5718 disk=14a54800
```

This is not boot yet, but it is a real new signature:

```text
frame=420 pc=0xffffffff800c7b88 frameHash=0x8ef9e361
drawPackets=24640 directTriangles=1265 setupTriangles=615
texWrites=6472734 framebuffer colored=307200

frame=600 pc=0xffffffff800c7c08 frameHash=0x8ef9e361
drawPackets=24640 directTriangles=1265 setupTriangles=615
texWrites=6472734 framebuffer colored=307200
```

The no-indexed sanity path is unchanged at the previous best 420-frame
signature:

```text
pc=0xffffffff800c80f0 frameHash=0x5335d5be
drawPackets=27461 directTriangles=941 setupTriangles=453
texWrites=6424515 framebuffer colored=286179
```

Next target: find why only two blank-path texture QIO creates are observed by
600 frames, or synthesize the remaining indexed texture source streams as a
controlled probe. The important improvement over the clone probe is that the
per-index source words now match real texture payload headers (`f00b0001`,
payload-local extents) instead of cloned `static_lr` model data.

Follow-up added a second, more aggressive probe:

```text
EUTHERDRIVE_GAUNTDL_EXPERIMENT_RUNTIME_BGLOADMODEL_INDEXED_TEXTURE_QIO_FILL_ALL=1
```

This fills every still-empty known indexed source slot 1..8 as soon as the
first blank-path indexed texture QIO is observed. It is a negative control, not
a fix. At 420 frames it hydrates `fillAll=7`, but regresses hard:

```text
pc=0xffffffff800aace4 frameHash=0xf29eb67c
drawPackets=0 directTriangles=30 setupTriangles=0
texWrites=106189 framebuffer colored=0
```

So the missing slots cannot simply be bulk-filled at first sight. The useful
next target is the sequencing around the two real blank-path QIO creates: why
only `gei` and `snm` are requested by 600 frames, and what condition should
allow the remaining per-index streams to be requested without breaking the
parser path.

Follow-up added a configurable QIO request trace cap:

```text
EUTHERDRIVE_GAUNTDL_TRACE_BGLOADMODEL_QIO_REQUEST_LIMIT=512
```

With the indexed texture QIO probe enabled and a 512-event request trace cap,
the 600-frame run only emitted 82 `bgloadmodel-qio-request` lines. That proves
the old default cap of 96 was not hiding later texture requests. The same run
still showed only the two real blank-path `textures.rom` QIO-create pairs:

```text
index=1 code=gei dest=802e3718 disk=14a6f600
index=2 code=snm dest=802e5718 disk=14a54800
```

and plateaued at the same indexed signature:

```text
frame=600 pc=0xffffffff800c7c08 frameHash=0x8ef9e361
drawPackets=24640 directTriangles=1265 setupTriangles=615
texWrites=6472734 framebuffer colored=307200
```

CPU tracing the plateau PC `800c7b88` showed it is in the same runtime
log/status helper family (`ra=800c81f0`, `a0=0x1000`, `a2=9/10`,
`a3=8013d85c`) and increments `802171a4` before calling `80021670`; it is not
a simple new wait loop. The next concrete target is the caller around the two
observed `textures.rom` QIO creates (`ra=800ac014` into `800c9678`) to find
why the stream advances only through `gei/snm`.

### 2026-06-05 Continuation: Indexed Texture Stream Limit Probe

Added a second opt-in indexed texture QIO probe:

```text
EUTHERDRIVE_GAUNTDL_EXPERIMENT_RUNTIME_BGLOADMODEL_INDEXED_TEXTURE_QIO_STREAM_LIMIT=9
```

This is only active with
`EUTHERDRIVE_GAUNTDL_EXPERIMENT_RUNTIME_BGLOADMODEL_INDEXED_TEXTURE_QIO=1`.
It targets the BGLoadModel texture state-machine comparison at `800abe78`,
where the traced path had `s4=2`. After `gei/snm`, `$v1 == 2`, so the stock
comparison stops the stream path instead of continuing toward more indexed
texture requests. The probe raises the register limit only when that exact
state-machine path is about to terminate and the already-loaded source window
for the current index is non-empty.

The first broad version of the probe fired too early (`streamIndex=0`) and
regressed the render path:

```text
frame=420 pc=0xffffffff80102ad8 frameHash=0x4a85cbfa
drawPackets=25248 directTriangles=1646 setupTriangles=805
texWrites=6488094 framebuffer colored=34248
```

The tightened version only fires once, after `snm`:

```text
bgloadmodel-indexed-texture-qio-stream-limit pc=ffffffff800abe78
streamIndex=2 sourceCursor=0 limit=2->9 loadedSource=ffffffff802e5718
```

It does not produce additional `textures.rom` QIO requests by 600 frames. It
does change the plateau PC, while preserving the indexed render statistics:

```text
frame=420 pc=0xffffffff80121684 frameHash=0x742b775c
drawPackets=24640 directTriangles=1265 setupTriangles=615
texWrites=6472734 framebuffer colored=307200

frame=600 pc=0xffffffff800ce4c4 frameHash=0x742b775c
drawPackets=24640 directTriangles=1265 setupTriangles=615
texWrites=6472734 framebuffer colored=307200
```

Conclusion: the `s4=2` limit is a real branch point, but raising it alone does
not advance to index 3 QIO. The next target is the code reached after the
tightened probe (`80121684` at 420, `800ce4c4` at 600) or the state that
should set a non-zero source cursor before the `800abe78` comparison.

### 2026-06-05 Continuation: Indexed Texture Short Read Probe

Added a third opt-in indexed texture QIO probe:

```text
EUTHERDRIVE_GAUNTDL_EXPERIMENT_RUNTIME_BGLOADMODEL_INDEXED_TEXTURE_QIO_SHORT_READ=1
```

This is only active with
`EUTHERDRIVE_GAUNTDL_EXPERIMENT_RUNTIME_BGLOADMODEL_INDEXED_TEXTURE_QIO=1`.
It targets the small `textures.rom` read that appears only after the tightened
stream-limit probe:

```text
pc=800c9678 a1=8013b07c(textures.rom) a2=00018e00 a3=00000120
s0=00000120 s1=00000120 s2=00000003 retSlot=807ffc98->80217c58
```

At the `800c9944` return-slot reload, the shared QIO object is otherwise empty
except for status `2`. The probe hydrates only this exact short-read signature,
uses `s2` as the indexed texture source id, copies the first `0x120` bytes of
the known payload into the corresponding `802e1718 + index * 0x2000` source
window, and repairs the QIO metadata as a completed `800ab4e4` callback.

The probe fires for `stk`:

```text
bgloadmodel-indexed-texture-qio-short-read pc=ffffffff800c9944
index=3 code=stk qio=ffffffff80217c58 object=ffffffff80295750
dest=ffffffff802e7718 bytes=00000120 disk=15117a00
```

This is a real forward step. The stream path now reaches `streamIndex=3`, and
the index 3 source window becomes non-zero:

```text
bgloadmodel-indexed-texture-qio-stream-limit pc=ffffffff800abe78
streamIndex=3 sourceCursor=0 limit=2->9 loadedSource=ffffffff802e7718

index=3 slot=ffffffff802529ac:802e1718->802e7718
sourceWords=00=00000000,04=00000000,08=00000000,0c=00000000,
40=f00b0001,5c=0000a3a4,60=0000001e,64=00000009,68=00000000
```

With indexed QIO + stream-limit + short-read enabled, the 420-frame signature
is:

```text
frame=420 pc=0xffffffff801034b8 frameHash=0x08862a9a
rtxt="Loading Game."
drawPackets=25545 directTriangles=303 setupTriangles=134
texWrites=6835614 framebuffer colored=271547
```

The 600-frame signature is:

```text
frame=600 pc=0xffffffff80102a88 frameHash=0x37fd72d4
rtxt="Loading Game."
drawPackets=25545 directTriangles=303 setupTriangles=134
texWrites=6835614 framebuffer colored=307200
```

It still does not boot through the loading phase. A focused 260-frame QIO trace
shows the next unhydrated request immediately after the `stk` short read:

```text
pc=800c9678 a1=8013b07c(textures.rom) a2=00000000 a3=00002000
s0=00002000 s1=000214c0 s2=00000007 retSlot=807ffc98->80217c58

pc=800c9944 s0=00002000 s1=000214c0 s2=00000007
retSlot=807ffc98->80217c58:00000000/00000000/00000000/00000000/00000000/00000002
```

That `0x2000`/`s1=0x214c0`/`s2=7` request is the next concrete target. Do not
treat `s2=7` as a proven texture source index yet; it may be a later state or
stream counter. The next probe should first establish the intended destination
and offset semantics for this second class of indexed texture stream request.

### 2026-06-05 Continuation: Indexed Texture Body Read Negative Probe

Added a fourth opt-in indexed texture QIO probe:

```text
EUTHERDRIVE_GAUNTDL_EXPERIMENT_RUNTIME_BGLOADMODEL_INDEXED_TEXTURE_QIO_BODY_READ=1
```

This is only active with indexed QIO enabled. It captures the destination from
the `800c9678` object-create point for the post-`stk` request, before the QIO
object is cleared, then hydrates the same source window at the `800c9944`
return-slot reload. The capture deliberately does not use `s2=7` as the
source index; it infers the source index from the previous QIO destination:

```text
bgloadmodel-indexed-texture-qio-body-read pc=ffffffff800c9944
index=3 code=stk key=000214c0 qio=ffffffff80217c58 object=ffffffff80295750
dest=ffffffff802e7718 bytes=00002000 disk=15117a00
```

This is a useful negative result, not a fix. It hydrates the suspected body
read and moves the PC, but the visual/render state regresses badly:

```text
frame=420 pc=0xffffffff8004ed14 frameHash=0xf262f878
rtxt="Loading Game."
drawPackets=25523 directTriangles=931 setupTriangles=449
texWrites=6505438 framebuffer colored=25058

frame=600 pc=0xffffffff8004f29c frameHash=0xf262f878
rtxt="Loading Game."
drawPackets=25523 directTriangles=931 setupTriangles=449
texWrites=6505438 framebuffer colored=25058
```

The body-read probe also leaves many Voodoo registers at zero compared with
the short-read-only run, and `lfbm` becomes `0x00000000`. Conclusion: the
`0x2000`/`0x214c0` request is real, but blindly full-hydrating the previous
`stk` source window is wrong or incomplete. The stronger current candidate
remains short-read without body-read:

```text
EUTHERDRIVE_GAUNTDL_EXPERIMENT_RUNTIME_BGLOADMODEL_INDEXED_TEXTURE_QIO=1
EUTHERDRIVE_GAUNTDL_EXPERIMENT_RUNTIME_BGLOADMODEL_INDEXED_TEXTURE_QIO_STREAM_LIMIT=9
EUTHERDRIVE_GAUNTDL_EXPERIMENT_RUNTIME_BGLOADMODEL_INDEXED_TEXTURE_QIO_SHORT_READ=1
```

Next target: trace how the game uses the `0x214c0` request result after
`800c9944`, especially whether the requested bytes should land in another
scratch buffer, an offset inside `802e7718`, or a table distinct from the
indexed texture payload body.

### 2026-06-05 Continuation: Short-Read Caller Trace

With the stronger short-read-only flag set, a focused CPU trace over
`800ac000..800ac520` to 260 frames (`/tmp/gauntdl-shortread-cpu-800ac000-260.log`)
confirms the next interesting path is not the full-body destination itself.
After the `stk` short read, the caller returns through `800ac43c` into the
`800ac04c` helper with `a0=-1`:

```text
pc=800ac43c jal 800ac04c a0=6c5c7a80 a1=802e1718 a2=8024f9a0 a3=0
pc=800ac440 addiu a0,-1
pc=800ac04c ... a0=ffffffffffffffff a1=802e1718 ...
```

That helper then clears/resets BGLoadModel globals and record fields:

```text
pc=800ac068 sw zero,8021f178
pc=800ac070 sw s4,8021f184   ; s4 is -1 on this path
pc=800ac08c sw zero,record+4
pc=800ac0a8 sw zero,80254da8
pc=800ac0bc sw -1,record+0
```

The body-read negative probe proves that simply full-hydrating the previous
`stk` source window before this path is wrong. The next higher-confidence target
is the branch/data that decides to call `800ac43c` with `a0=-1` after the
`0x214c0` request. In the short-read-only trace, the route just before that is:

```text
pc=800ac330 lw v1,record+0x0c
pc=800ac334 sltiu v0,v1,7     ; v1=2 on the captured path
pc=800ac338 beq v0,zero,...
pc=800ac350 jr v0             ; dispatches to 800ac43c
```

Follow-up disassembly corrected that interpretation. `record+0x0c == 2` is a
normal dispatch state, not a stuck state:

```text
mem[0xffffffff8013b208]:
 +0x000: 800ac358 800ac3d0 800ac43c 800ac460
 +0x010: 800ac478 800ac49c 800ac4c0 20746f4e
```

The same short-read-only trace shows `800ac43c` calling `800ac04c`; when the
helper returns non-zero, the delay-slot path stores state `3`, and the record
then proceeds through states `3`, `4`, `5`, and `6`. So the next probe should
not repair the state value feeding `record+0x0c == 2`. The better target remains
the semantics of the following `0x2000`/`s1=0x214c0` request and how its result
is consumed after `800c9944`.

### 2026-06-06 Continuation: Short-Read Header Fill Negative Probe

Added another opt-in indexed texture QIO probe:

```text
EUTHERDRIVE_GAUNTDL_EXPERIMENT_RUNTIME_BGLOADMODEL_INDEXED_TEXTURE_QIO_SHORT_READ_FILL_REMAINING=1
```

This is only active with indexed QIO short-read enabled. Unlike the earlier
full `0x2000` fill-all probe, it only fills the remaining empty indexed texture
source windows with the first `0x120` bytes of each known payload. The intent
was to test whether slots `4..8` only needed the same header fragment as the
observed `stk` short read.

The corrected probe fires and fills five remaining headers:

```text
bgloadmodel-indexed-texture-qio-short-read pc=ffffffff800c9944
index=3 code=stk dest=ffffffff802e7718 bytes=00000120
fillRemaining=5
```

This is also a negative result. At 420 frames it regresses similarly to the
body-read probe:

```text
frame=420 pc=0xffffffff800aacf0 frameHash=0x4796dd5b
rtxt="Loading Game."
drawPackets=14319 directTriangles=586 setupTriangles=275
texWrites=914846 framebuffer colored=631 lfbm=0x00000000
```

Conclusion: the later indexed source windows cannot be bulk-seeded, even with
only their short headers. The current best run remains indexed QIO +
stream-limit + `stk` short-read only, without body-read and without short-read
fill-remaining. The next target should stay focused on the single
`0x2000`/`s1=0x214c0` request and its caller/consumer semantics, not on
pre-seeding later source windows.

### 2026-06-06 Continuation: QIO File-State Trace

Expanded `EUTHERDRIVE_GAUNTDL_TRACE_BGLOADMODEL_QIO_REQUESTS=1` output with
`file=`, `retFile=`, and `argFile=` summaries. These decode the QIO object or
`a0` object pointer into the backing file-state pointer plus words around
`+0x114/+0x118`.

The focused short-read-only trace confirms that the `0x214c0` request is not
explained by the file-state current offset:

```text
pc=800c9944 s0=00002000 s1=000214c0 s2=00000007
retSlot=807ffc98->80217c58:00000000/00000000/00000000/00000000/00000000/00000002
argFile=ffffffff8021e88c:off=00000000/lba=00000000/w110=00000000/w11c=00000000
```

At `800c9678`, the same request still has the previous `stk` short-read QIO
metadata in the return slot, but by `800c9944` the QIO object is empty except
for status `2`. The object passed in `a0` is valid, but its file-state offset
and LBA are zero. That makes `s1=0x214c0` more likely to be caller/source-data
derived state than a recoverable file current-offset value.

Follow-up trace also added `globals=` for the BGLoadModel QIO globals
`8021f154/f178/f17c/f180/f184`. They are all zero on the `0x214c0` path:

```text
pc=800c9678 a1=textures.rom a2=00000000 a3=00002000
s0=00002000 s1=000214c0 s2=00000007
globals=f154=00000000/f178=00000000/f17c=00000000/f180=00000000/f184=00000000

pc=800c9944 s0=00002000 s1=000214c0 s2=00000007
argFile=ffffffff8021e88c:off=00000000/lba=00000000/w110=00000000/w11c=00000000
globals=f154=00000000/f178=00000000/f17c=00000000/f180=00000000/f184=00000000
```

So the next useful trace is lower-level: follow the caller/register path that
loads `s1=0x214c0`, probably around the `800abf80..800ac030` QIO submission
wrapper and the `80102a60` caller loop, rather than adding another synthetic
QIO hydration from global state.

### 2026-06-06 Continuation: QIO Stack Trace

Expanded `EUTHERDRIVE_GAUNTDL_TRACE_BGLOADMODEL_QIO_REQUESTS=1` again with a
compact `stack=` summary for selected words from `sp` and `s8/fp`. This maps
the create helper arguments more clearly:

```text
sp+00 = object
sp+78 = source/file offset copy
sp+7c = byte count copy
sp+80 = destination argument
sp+84 = callback
sp/fp+20 = return QIO slot
```

The `stk` short-read path is coherent: it submits `textures.rom` with
`a2=0x18e00`, `a3=0x120`, `sp+78=0x18e00`, `sp+7c=0x120`, and the repair
hydrates destination `802e7718`:

```text
pc=800c9678 a1=8013b07c(textures.rom) a2=00018e00 a3=00000120
s0=00000120 s1=00000120 s2=00000003
stack=.../78=00018e00/7c=00000120/80=802e1718/84=800ab4e4
retSlot=807ffc98->80217c58:00000000/800ab4e4/802e5718/00002000/00002000/ffffffff

bgloadmodel-indexed-texture-qio-short-read ... index=3 code=stk
dest=802e7718 bytes=00000120 disk=15117a00
```

The following `0x214c0` request is a different shape and should not be treated
as “the rest of `stk`”. At submit time it has `s2=7`, a zero file offset, and a
stale return-slot QIO still pointing at the previous `stk` short-read:

```text
pc=800c9678 a1=8013b07c(textures.rom) a2=00000000 a3=00002000
s0=00002000 s1=000214c0 s2=00000007
stack=.../78=00000000/7c=00002000/80=802e1718/84=800ab4e4
retSlot=807ffc98->80217c58:00000000/800ab4e4/802e7718/00000120/00000120/ffffffff
```

By return, the qio record is empty except status `2`:

```text
pc=800c9944 s0=00002000 s1=000214c0 s2=00000007
retSlot=807ffc98->80217c58:00000000/00000000/00000000/00000000/00000000/00000002
```

This explains the earlier negative body-read probe: it captured the stale
`802e7718` destination from the old qio record and full-hydrated the wrong
conceptual request. The opt-in body-read capture now requires the submit-stack
destination (`sp+0x80`) to match the qio-record destination before it records a
pending body read, so the known-bad `0x214c0` case no longer fires from stale
metadata.

Build and stack-trace sanity:

```text
dotnet build tools/GauntletProbe/GauntletProbe.csproj -c Release --no-restore /clp:ErrorsOnly
Build succeeded.
343 Warning(s)
0 Error(s)

frame=260
pc=0xffffffff800b39c0
frameHash=0x37fd72d4
drawPackets=21475 directTriangles=303 setupTriangles=134
texWrites=5624478 framebuffer colored=307200
```

### 2026-06-06 Continuation: QIO Stream Cursor Trace

The next CPU trace bracketed the submit wrapper before the `800c9678` create
call. The wrapper does not invent the `0x214c0` request from the QIO file-state
record; `s1=0x214c0` is already live before the submit helper:

```text
#56105612 pc=ffffffff800ac00c jal
a0=8024f9b0 a1=8013b07c a2=00000000 a3=00002000
s0=00002000 s1=000214c0 s2=00000007 s3=802e2158 s4=00000009
```

In that wrapper:

```text
800abff8 loads a2 from 8021f180, which is zero for this request.
800ac000 loads the destination base from 8021f154, which becomes 802e1718.
s4=9, s3=802e2158, s2=7, s1=214c0 are already established.
```

A wider trace over `800abe00..800abfc0` shows where the cursor comes from. The
loop at `800abf08..800abf38` scans source entries, calls helper `800a64a0`,
builds candidate end offsets, and keeps the maximum candidate below global
`8021f15c` (`0x30200`). The key candidate is:

```text
entry s0=802e22e8
entry+8=00017fe4
global 8021f180=00000000
helper 800a64a0 return=000094dc
candidate end=00017fe4 + 000094dc = 000214c0
```

The loop then stores that cursor into `8021f158` and reloads it for the submit
path:

```text
pc=800abf54 sw ... s0=ffffffff80210000 s1=000214c0 s2=00000007
pc=800abf70 lw ... s0=ffffffff80210000 s1=000214c0
pc=800abf74 lw ... s0=000214c0 s1=000214c0 a0=ffffffff
pc=800abf84 lui ... s0=00002000 s1=000214c0
```

So `0x214c0` is a stream cursor/end offset derived from the parsed source-entry
table, not a stale QIO record field and not the remainder of the prior `stk`
short-read. The next bringup target is the `802e2158` source-entry table and
the helper at `800a64a0`: decode why entry `802e22e8` reports base `0x17fe4`
and length `0x94dc`, then decide whether the following `0x2000` read should be
hydrated into the stream destination at `802e1718` or represented by a more
precise state update.

The RAM/code dump at the same 260-frame baseline verifies the source-entry
bytes and preserves the helper body for later decoding:

```text
dotnet run --project tools/GauntletProbe/GauntletProbe.csproj -c Release --no-build -- \
  /home/nichlas/roms/MAME/Midway/Vegas/gauntd 260 200000 0

EUTHERDRIVE_GAUNTDL_DUMP_CODE_RANGES=0xffffffff800a64a0:64
EUTHERDRIVE_GAUNTDL_DUMP_BYTES_RANGES=0xffffffff802e2158:768

frame=260
pc=0xffffffff800b39c0
frameHash=0x37fd72d4
```

Helper `800a64a0` starts with:

```text
800a64a0: 94850004 90820002 94860006 2c420008
800a64b0: 14400002 0000382d 00052840 90830001
800a64c0: 90820000 00621023 0440000a 0000202d
800a64d0: 0040182d 00a60018 00052843 00063043
800a64e0: 24840001 0064102a 00004012 1040fff9
800a64f0: 00e83821 03e00008 00e0102d 2c820007
```

Entry `802e22e8` is dump base `802e2158 + 0x190`:

```text
802e22e8: 10 90 01 00 dc 94 01 00 e4 7f 01 00 12 0e 00 00
802e22f8: 0b 00 00 00 00 00 00 00 00 00 e1 40 01 00 00 00
```

The CPU trace confirms this exact entry is the accepted max candidate:

```text
pc=800abf00 jal helper, s0=802e22e8
pc=800abf08 helper returned v0=000094dc
pc=800abf0c a0=00017fe4 from lw 8(s0)
pc=800abf1c v1=000214c0 after a0 + v0
pc=800abf30 s1=000214c0 after movn
```

Adjacent entries explain why `0x214c0` wins: `802e2338` later produces
`0x0e6e + 0x7f98`, and later entries overshoot the `0x30200` cap instead of
replacing the cursor. The next practical probe should trace consumers after the
`0x2000` submit with `a2=0x214c0` and stream destination `802e1718`, not retry
full body hydration of `802e7718`.

Code follow-up: `ComputeKnownRuntimeBgLoadModelLateStreamExtent()` now mirrors
the helper loop instead of using `width * height * (span + 1)`. The runtime
helper adds the current `width * height` once per level and shifts both
dimensions right after each add. That is required for the `802e22e8` case to
return `0x94dc` rather than a huge overestimate.

Verification after that code fix:

```text
dotnet build tools/GauntletProbe/GauntletProbe.csproj -c Release --no-restore /clp:ErrorsOnly
Build succeeded. 462 Warning(s), 0 Error(s)

frame=260 pc=0xffffffff800b39c0 frameHash=0x37fd72d4
drawPackets=21475 directTriangles=303 setupTriangles=134
texWrites=5624478 framebuffer colored=307200
```

### 2026-06-06 Continuation: Loading Hotloop After 600 Frames

Revalidated the best-known flag stack after the stream-helper fix:

```text
frame=600
pc=0xffffffff80102a88
frameHash=0x37fd72d4
rtxt="Loading Game."
drawPackets=25545 directTriangles=303 setupTriangles=134
texWrites=6835614 framebuffer colored=307200
```

A focused CPU trace over `80102000..80103650` to frame 620 shows that the
post-600 time is dominated by runtime Voodoo/display-list emission, not by the
BGLoadModel QIO submit path. The tail repeatedly runs:

```text
80103574 -> 80103588 jal 80102174
80102174..801021d8 status/mode check against 80262d64 + 0x268
80102b40..80102bc0 writes command pairs through a82005c0/a82005c8
```

Representative end-state from the trace:

```text
pc=80102b40 ra=801035a0
s0=00000000 s1=807ffc10 s2=802e3718 s3=8013b07c
s4=ffffffff s5=00000001 s6=fffffffd
80102b5c loads 80262d64+0x26c = 0x00000460
80102b74 stores 0x00000460 & -3 back to 80262d64+0x26c
80102b78 loads remaining/cursor word from 80262d64+0x37c
80102ba4/80102ba8 write command words to a82005c0
80102bbc/80102bc0 advance 80262d64+0x374/+0x37c by eight bytes
```

The trace reached its CPU-line cap before the loop fully unwound, but frame 620
still ended at:

```text
pc=0xffffffff80102ad8
frameHash=0x37fd72d4
rtxt="Loading Game."
drawPackets=25545 setupTriangles=134 texWrites=6835614 framebuffer colored=307200
```

Next target is this display-list emitter, especially the ring/cursor state at
`80262d64+0x268/+0x26c/+0x374/+0x37c` and the `a82005c0` command writes. That
path looks like the current post-load hot loop; BGLoadModel stream cursor work
is no longer the immediate blocker at 600 frames.

### 2026-06-06 Follow-up: Loaded FBZ Mode Packet Hotpath

Added `TryFastPathKnownGauntletGlideRuntimeFbzModeClearPacket()` for the
observed `80102b40` entry variant. It is intentionally narrow:

- Matches the full `80102b40..80102bd4` packet function signature.
- Accepts only the live `a0=0` path observed from `80103598`, which clears bit
  `0x02` in `80262d64+0x26c`.
- Emits the same type-1 Voodoo register packet:
  `0x00010221, fbzMode`, then advances `80262d64+0x374/+0x37c` by eight bytes.
- Falls back to normal emulation if FIFO room is below eight bytes or the
  unknown `a0=1` path is reached.

Verification:

```text
dotnet build tools/GauntletProbe/GauntletProbe.csproj -c Release --no-restore /clp:ErrorsOnly
Build succeeded. 462 Warning(s), 0 Error(s)

frame=620
pc=0xffffffff800c7c08
frameHash=0x37fd72d4
rtxt="Loading Game."
drawPackets=25545 directTriangles=303 setupTriangles=134
texWrites=6835614 framebuffer colored=307200
packetTypes=0:3908,1:480839,2:0,3:25545,4:93814,5:109640,6:1,7:4
```

This moves the 620-frame endpoint away from the previous `80102ad8` display-list
tail without changing the stable visible hash. The next target is now the
`800c7c08` runtime loading/dispatch path reached after the FBZ-mode packet spam
is reduced.

Follow-up trace over `800c7b80..800c7c50` confirms this is the runtime progress
/ diagnostic-format pump, not a BGLoadModel QIO wait. Repeated calls:

```text
800c7b80..800c7bb8 saves line state and increments 802171a4
800c7bc0 jal 80121670 with a0=sp+0x10, a1=8013d85c, a2=sp+0x78
800c7bc8..800c7bd4 checks overlay flag 80163b0c before the overlay path
```

An overlay-suppress sanity run hit the existing experiment once:

```text
EUTHERDRIVE_GAUNTDL_EXPERIMENT_RUNTIME_DIAGNOSTIC_OVERLAY_SUPPRESS=1
[GAUNTDL:EXPERIMENT] diagnostic-overlay-suppress pc=ffffffff800c7c10 flags=00000001/00000001/00000001/00000001->00000000/...
```

but it is not a clear fix:

```text
frame=620
pc=0xffffffff800c7b90
frameHash=0x08862a9a
drawPackets=25545 directTriangles=303 setupTriangles=134
packetTypes=0:3908,1:616839,2:0,3:25545,4:93814,5:109640,6:1,7:4
framebuffer colored=271547
```

Compared with the non-overlay-suppress run, it increases type-1/swap traffic and
changes the visible hash, but does not produce new draw/setup work. Keep it as a
diagnostic lever, not part of the best boot stack. The next useful target is a
proper fastpath or semantic fix for the `800c7b80` progress-format caller, or
the condition that keeps scheduling these progress lines while `Loading Game.`
never completes.

Text-pump skip was retested against the same post-FBZ best stack:

```text
EUTHERDRIVE_GAUNTDL_EXPERIMENT_RUNTIME_DIAGNOSTIC_TEXT_PUMP_SKIP=1
[GAUNTDL:EXPERIMENT] diagnostic-text-pump-loop-skip pc=ffffffff800c81ec epilogue=ffffffff800c8210

frame=620
pc=0xffffffff80103438
frameHash=0x08862a9a
drawPackets=25545 directTriangles=303 setupTriangles=134
packetTypes=0:3908,1:781579,2:0,3:25545,4:93814,5:109640,6:1,7:4
framebuffer colored=271547
```

This also is not a boot fix. It skips the local `800c812c` string pump and moves
time into the same runtime state/display-list family around `801034xx`, with
even more type-1 packet traffic and no new geometry. Keep
`DIAGNOSTIC_TEXT_PUMP_SKIP` as a profiling lever only; the stable comparison
stack should leave it off.

Follow-up returned to the stronger BGLoadModel texture path instead of the
cosmetic progress/text pump. A 620-frame hot-PC profile with the current best
stack stayed on the same stable endpoint:

```text
frame=620
pc=0xffffffff800c7c08
frameHash=0x37fd72d4
hotpcs=0xffffffff80001a24:4884,0xffffffff80000fd8:1148,0xffffffff80041880:828,...
```

The hot-PC list is dominated by low-level helper paths after the fastpaths and
does not identify a new runtime wait. The visible/debug signature still points
back to loader scheduling, not to the local progress text work.

Retested the old full indexed texture fill-all control briefly. It still fires
too early and is not part of the best stack:

```text
EUTHERDRIVE_GAUNTDL_EXPERIMENT_RUNTIME_BGLOADMODEL_INDEXED_TEXTURE_QIO_FILL_ALL=1
bgloadmodel-indexed-texture-qio pc=ffffffff800c9944 index=1 code=gei ... fillAll=7
```

The live log again showed later slots being bulk-seeded before the parser has
requested them; slot data diverges from the clean `f00b0001` header pattern.
This confirms the earlier negative result: do not bulk-fill slots `4..8`, even
though their payload offsets are known.

Focused QIO request trace at 260 frames:

```text
/tmp/gauntdl-qio-214c0-260.log

pc=800c9678 a1=8013b07c(textures.rom) a2=00000000 a3=00002000
s0=00002000 s1=000214c0 s2=00000007 retSlot=807ffc98->80217c58

pc=800c9944 s0=00002000 s1=000214c0 s2=00000007
retSlot=807ffc98->80217c58:00000000/00000000/00000000/00000000/00000000/00000002
```

Then CPU-traced the QIO helper with a full 64-bit PC range. Use the full
`ffffffff...` addresses; passing `800c9600` alone traces the wrong low range.

```text
/tmp/gauntdl-qio-create-cpu-fullpc-260.log
trace range: ffffffff800c9600..ffffffff800c9950

800c96f4 sw v0,sp+0x20        ; ret slot = 80217c58
800c9704 jal 800d13a0         ; clear/setup QIO struct
800c9734 jal 8011f3c0         ; path/log helper with a3=textures.rom
...
800c97d8 jal 800c8684         ; object/status helper
800c97e0..800c97f4 checks object/status result
800c97f4 beq v0,zero,800c9918 ; taken for the 0x214c0 request
800c9918 jal 800edda4
800c9940 sw 2,qio+0x14        ; complete status
800c9948 sw zero,qio+0x00     ; clear object pointer
```

That explains the body-read negative result: the `0x2000/s1=0x214c0/s2=7`
request is not currently a normal completed QIO body copy into the previous
`stk` source window. The helper takes an error/empty-complete path, marks the
shared QIO complete, and clears the object pointer. Next target is the branch
condition feeding `800c97f4` or the object/status helper result, not another
payload bulk-fill.

Follow-up RA-filtered the status helper to avoid exhausting the trace budget on
other callers:

```text
/tmp/gauntdl-qio-status-helper-ra97e0-260.log
EUTHERDRIVE_GAUNTDL_TRACE_CPU_RA=ffffffff800c97e0
trace range: ffffffff800c8640..ffffffff800c86d0

pc=800c8684 a0=80295750 a1=0000300b a2=0000007f a3=807ffca0
s0=00002000 s1=000214c0 s2=00000007 s4=00000009 ra=800c97e0
pc=800c86a0 lw v1,object+0x20 -> 00000000
pc=800c86a8 jal 80010fbc
```

So the failing `0x214c0` path reaches the helper with the expected stream
registers, but the QIO object metadata slot read at `object+0x20` is empty. The
next concrete patch target is to trace or repair the producer of
`80295750+0x20`, then retest whether `800c97f4` stops taking the empty-complete
path. Keep the current best flags unchanged until that branch result changes.

## 2026-06-06 Indexed QIO Object Metadata Repair

Traced writes to the failing metadata slot with:

```text
EUTHERDRIVE_GAUNTDL_TRACE_MEM=1
EUTHERDRIVE_GAUNTDL_TRACE_MEM_WRITES_ONLY=1
EUTHERDRIVE_GAUNTDL_TRACE_MEM_ADDRESS=ffffffff80295770:4
```

The slot is not permanently uninitialized. It is repeatedly cleared by
`0x800d1470`, and the normal writer paths at `0x800c88d8`,
`0x800c8b98`, and `0x800c8ebc` restore `80218518`. Around the fabricated
indexed texture QIO path, however, the clear happens just before the repair
creates the synthetic completion:

```text
write64 ffffffff80295770 00000000 pc=ffffffff800d1470
bgloadmodel-indexed-texture-qio pc=ffffffff800c9944 index=...
```

Added a narrow repair to the indexed texture QIO metadata paths so the synthetic
QIO completions also restore `qioObject+0x20` to `80218518` when that slot is
empty. It refuses to overwrite any other nonzero value. New trace lines:

```text
bgloadmodel-indexed-texture-qio-object-metadata pc=ffffffff800c9944 phase=indexed object=ffffffff80295750 obj20=00000000->80218518
bgloadmodel-indexed-texture-qio-object-metadata pc=ffffffff800c9944 phase=short-read object=ffffffff80295750 obj20=00000000->80218518
```

Verified build:

```text
dotnet build tools/GauntletProbe/GauntletProbe.csproj -c Release --no-restore /clp:ErrorsOnly
Build succeeded: 462 warnings, 0 errors
```

Verified 260-frame probe with the current best flags:

```text
frame=260
pc=0xffffffff800af7dc
frameHash=0x37fd72d4
drawPackets=21475 directTriangles=303 setupTriangles=134
```

Verified 620-frame probe stayed on the previous stable profile:

```text
frame=620
pc=0xffffffff800c7c08
frameHash=0x37fd72d4
drawPackets=25545 directTriangles=303 setupTriangles=134
texWrites=6835614 framebuffer colored=307200
packetTypes=0:3908,1:480839,2:0,3:25545,4:93814,5:109640,6:1,7:4
```

This patch fixes the local metadata inconsistency, but does not by itself move
the 620-frame PC beyond the prior stable point. Next target is to re-run the
RA-filtered helper trace with the metadata repair active and check whether
`800c86a0` now reads `80218518`, and whether the branch at `800c97f4` still
takes the empty-complete path.

## 2026-06-06 Indexed QIO Pre-Status Metadata Repair

The post-return metadata repair was too late for the branch under investigation:
the status helper at `0x800c8684` had already loaded `object+0x20` as zero. Added
a pre-status repair at the callsite `0xffffffff800c97d8`, guarded to the known
indexed texture request signatures:

```text
s0=00002000 s1=188d2303 s2=00000002
s0=00000120 s1=00000120 s2=00000003..00000008
s0=00002000 s1=000214c0 s2=00000007
```

The repair writes `80218518` to `qioObject+0x20` only when the slot is empty or
already has that value. RA-filtered helper trace now confirms the helper reads
the repaired metadata before the `80010fbc` call:

```text
bgloadmodel-indexed-texture-qio-object-metadata pc=ffffffff800c97d8 phase=pre-status object=ffffffff80295750 obj20=00000000->80218518

pc=800c86a0 lw v1,object+0x20
pc=800c86a4 ... v1=ffffffff80218518
```

The same trace showed the `short-read` path and the `s1=0x214c0/s2=7` path both
getting `v1=ffffffff80218518` at `0x800c86a4`.

Verified build:

```text
dotnet build tools/GauntletProbe/GauntletProbe.csproj -c Release --no-restore /clp:ErrorsOnly
Build succeeded: 462 warnings, 0 errors
```

Verified 260-frame RA-filtered probe remained stable:

```text
frame=260
pc=0xffffffff800af7dc
frameHash=0x37fd72d4
drawPackets=21475 directTriangles=303 setupTriangles=134
```

Verified 620-frame probe remained on the current stable profile:

```text
frame=620
pc=0xffffffff800c7c08
frameHash=0x37fd72d4
drawPackets=25545 directTriangles=303 setupTriangles=134
texWrites=6835614 framebuffer colored=307200
packetTypes=0:3908,1:480839,2:0,3:25545,4:93814,5:109640,6:1,7:4
```

The metadata input to the helper is now fixed. Since the 620-frame PC did not
advance, the next concrete target is the helper continuation after `80010fbc`
and the branch at `800c97f4`: trace `0x800c86b0..0x800c872c` without the
`ra=800c97e0` filter, or add a narrower return-site trace for `ra=800c86b0`, to
see why the helper still returns the value that drives the empty-complete path.

Follow-up continuation trace used:

```text
EUTHERDRIVE_GAUNTDL_TRACE_CPU_RA=ffffffff800c86b0
trace range: ffffffff800c86b0..ffffffff800c872c
```

The trace is too broad for regular use, but it pinned down the next failure
surface. For indexed signatures, the helper continuation sees the repaired
metadata pointer and still normalizes the helper return into status values:

```text
s0=00002000 s1=188d2303 s2=00000002
pc=800c86b0 ... v1=ffffffff80218518
pc=800c872c ... v0=00003000 v1=00003000

s0=00000120 s1=00000120 s2=00000003
pc=800c86b0 ... v1=ffffffff80218518
pc=800c872c ... v0=00003000 v1=00003000

s0=00002000 s1=000214c0 s2=00000007
pc=800c86b0 ... v1=ffffffff80218518
pc=800c872c ... v0=00003000 v1=00003000
```

The alternate status-check subpath still produces `0x1c00` for `a1=0x1c01`;
the load path we care about produces `0x3000` for `a1=0x300b`. The 260-frame
continuation trace remained stable at:

```text
frame=260
pc=0xffffffff800af7dc
frameHash=0x37fd72d4
```

Do not re-run the broad `ra=800c86b0` trace unless needed; it emits thousands of
unrelated helper returns. Next useful implementation target is a targeted
`bgloadmodel-indexed-status-helper-continuation` trace gated on the three indexed
signatures above, including caller `ra`, `sp+0x14/0x24`, and the post-helper
branch operands at `0x800c97e0..0x800c97f4`.

Added the narrow trace as:

```text
EUTHERDRIVE_GAUNTDL_TRACE_BGLOADMODEL_INDEXED_STATUS_HELPER=1
EUTHERDRIVE_GAUNTDL_TRACE_BGLOADMODEL_INDEXED_STATUS_HELPER_LIMIT=80
```

It is gated on the three known indexed request signatures and emits only the
helper return/status-load and caller branch PCs. A 260-frame probe with the
current stable flags remained stable:

```text
frame=260
pc=0xffffffff800af7dc
frameHash=0x37fd72d4
drawPackets=21475 directTriangles=303 setupTriangles=134
```

The trace confirms the pre-status repair is effective, but the next branch is
driven by caller stack fields rather than `object+0x20`:

```text
helper-return a1=300b v1=ffffffff80218518 obj20=80218518
helper-status-load v0=00003000 v1=00003000 obj14=00003000 obj20=80218518
caller-after-helper pc=800c97e0 v0=00003000 v1=00003000 fp24=802171b8
caller-empty-branch pc=800c97f4 v0=00000000 v1=00000000 obj20=80218518
```

A focused PC trace of `0x800c97e0..0x800c97f8` shows the exact caller sequence:

```text
800c97e0 8fc20038  lw v0,0x38(fp)
800c97e4 afc2001c  sw v0,0x1c(fp)
800c97e8 8fc20078  lw v0,0x78(fp)
800c97ec 8fc3001c  lw v1,0x1c(fp)
800c97f0 0043102a  slt v0,v0,v1
800c97f4 10400048  beq v0,zero,...
```

For the indexed request, both compared values are zero, so `slt` returns zero
and the branch is taken. Next concrete target: trace writes to the caller stack
slots `fp+0x38` and `fp+0x78` for indexed calls, then repair the missing count or
limit metadata at the producer rather than forcing the branch directly.

Added an explicit negative experiment:

```text
EUTHERDRIVE_GAUNTDL_EXPERIMENT_RUNTIME_BGLOADMODEL_INDEXED_TEXTURE_QIO_STATUS_STACK_LIMIT=1
```

At `0x800c97e0`, for the known indexed signatures only, this fills an empty
caller compare limit slot `fp+0x38` with the request size from `s0` when both
`fp+0x38` and `fp+0x78` are zero. The goal was to let the native `slt`/`beq`
fall through without directly patching the branch.

The experiment does flip the branch:

```text
bgloadmodel-indexed-status-stack-limit pc=ffffffff800c97e0 ... limit=...:00000000->00002000 cursor=...:00000000
caller-empty-branch pc=ffffffff800c97f4 ... v0=00000001 v1=00002000 fp38=00002000 fp78=00000000
```

But it is not a valid boot path. A 260-frame probe regressed to a sparse
diagnostic path:

```text
frame=260
pc=0xffffffff800c806c
frameHash=0xf29eb67c
drawPackets=0 directTriangles=30 setupTriangles=0
framebuffer nonBlack=703 colored=0
```

Keep this flag off. A follow-up 260-frame probe without the stack-limit
experiment, but with the expanded trace, returned to the stable baseline:

```text
frame=260
pc=0xffffffff800af7dc
frameHash=0x37fd72d4
drawPackets=21475 directTriangles=303 setupTriangles=134
framebuffer colored=307200
```

The expanded trace also shows that the status-check helper frames may have
non-zero `fp+0x78` values (`0x800c83b4`, `0x2`, `0x18e00`), while the caller
compare at `0x800c97e0` still consistently has `fp+0x38=0`. The branch itself is
therefore a poor repair point; the next target remains the producer of the
caller compare limit / argument block.

Follow-up caller trace:

```text
trace range: ffffffff800abfd0..ffffffff800ac020
```

For the first indexed request (`s0=0x2000`, `s1=0x188d2303`, `s2=2`), the caller
builds the call to `0x800c95e8` as:

```text
800abff8 8c46f180  lw a2,0xfffff180(v0)   ; a2=00000000 from 8021f180
800ac000 8c63f154  lw v1,0xfffff154(v1)   ; v1=802e1718 destination
800ac008 afa20014  sw v0,0x14(sp)         ; callback 800ab4e4
800ac00c 0c03257a  jal 800c95e8
800ac010 afa30010  sw v1,0x10(sp)         ; stack arg destination
```

At the callee prologue:

```text
800c95e8 27bdff90  addiu sp,-0x70
800c95f8 afc40070  sw a0,0x70(fp)
800c95fc afc50074  sw a1,0x74(fp)
800c9600 afc60078  sw a2,0x78(fp)
800c9604 afc7007c  sw a3,0x7c(fp)
```

No traced instruction in `0x800c95e8..0x800c9800` writes `fp+0x38`; the only
`0x38` hits are register values or the final `lw v0,0x38(fp)`. This makes
`fp+0x38` look like a local/output value expected from an earlier helper path,
not an argument directly supplied by `800ac00c`. The next narrow target is the
source of `8021f180` and the helper path before `800c97e0` that should populate
the local compare limit.

Memory watchpoint on the argument/global block:

```text
EUTHERDRIVE_GAUNTDL_TRACE_MEM=1
EUTHERDRIVE_GAUNTDL_TRACE_MEM_WRITES_ONLY=1
EUTHERDRIVE_GAUNTDL_TRACE_MEM_ADDRESS=ffffffff8021f140:96
```

Result: the whole `8021f140..8021f19c` window is only zero-initialized early:

```text
pc=80005b18 write64 8021f140..8021f198 = 0
pc=800103a4 write32 8021f140..8021f19c = 0
```

No runtime write to `8021f180` occurs before the indexed caller reads it as
`a2=0`. This makes `8021f180` a bad direct repair target. The remaining narrow
target is the helper path between `800c979c` and the return at
`800c97a4..800c97cc`, which should explain why local `fp+0x38` stays zero before
the compare at `800c97e0`.

Added a narrow opt-in prepare-helper trace:

```text
EUTHERDRIVE_GAUNTDL_TRACE_BGLOADMODEL_INDEXED_PREPARE_HELPER=1
```

It is gated on the same indexed request signatures and traces the call chain
around `0x800c979c..0x800c97e0`. The important result is that the caller does
have non-zero source/output words before the detail helper:

```text
prepare-detail-call pc=800c97c4 ... fp28=802171b8 fp38=802171b8
prepare-detail-return pc=800c97cc ... v0=300b fp28=00000000 fp38=00000000 obj14=300b
before-compare pc=800c97e0 ... v0=3000 fp28=00000000 fp38=00000000 fp78=00000000
```

The target of `jal 0c03b09a` is `0xffffffff800ec268` (not `800ce268`). An
RA-filtered trace for `ra=0xffffffff800c97cc` shows why the output is cleared:

```text
800ec268 entry: a0=80295750 a1=807ffdb0 ra=800c97cc
800ec288 lw v0,0x34(s0) -> 0
800ec294 sw zero,0x18(s0)
800ec2ac jal 800d13a0 with a0=output, a1=0 ; clears output structure
800ec2b4 lw a0,0x0c(s0) -> ffffffff
800ec2b8 jal 800ebacc
800ec2c0 beql v0,zero,...
800ec2c4 v0=300b
800ec304 sw v0,0x14(s0)
```

So the indexed failure is not because `fp+0x38` lacks any producer. It is
produced earlier, then the detail helper intentionally clears the output because
the synthetic object/file state still looks empty or closed (`obj+0x0c` reads as
`0xffffffff`, and `obj+0x34` reads as zero on entry).

The previous `STATUS_STACK_LIMIT` experiment remains a confirmed negative
control. It forces the branch past `0x800c97f4`, but diverts into a bad sparse
diagnostic path and must stay off for the baseline. Next implementation target:
repair the indexed QIO object's native file-state metadata before
`0x800ec268`, or emulate the `800ec268` detail helper narrowly enough to preserve
the existing `fp+0x28/fp+0x38` output only for the known indexed request
signatures.

Added the opt-in detail-helper preserve experiment:

```text
EUTHERDRIVE_GAUNTDL_EXPERIMENT_RUNTIME_BGLOADMODEL_INDEXED_PREPARE_DETAIL_PRESERVE=1
```

This intercepts the native `0x800c97c4 -> 0x800ec268` call only for known
indexed request signatures, only when `fp+0x28/fp+0x38` are already non-zero. It
returns `0x300b` to `0x800c97cc` and preserves the caller's existing output pair
instead of letting `800ec268` clear it because `obj+0x0c == 0xffffffff` and
`obj+0x34 == 0`.

Observed first hit:

```text
bgloadmodel-indexed-prepare-detail-preserve pc=ffffffff800c97c4
object=ffffffff80295750 output=ffffffff807ffdb0
cursor=802171b8 limit=802171b8 obj0c=ffffffff obj34=00000000
objectStatus=00001c00->0000300b
```

This is not yet a visible boot breakthrough, but it is non-regressive and moves
the terminal PC:

```text
260 baseline off: frameHash=0x37fd72d4 pc=800af7dc drawPackets=21475 setupTriangles=134 colored=307200
260 preserve on:  frameHash=0x37fd72d4 pc=800a50e4 drawPackets=21475 setupTriangles=134 colored=307200
620 baseline off: frameHash=0x37fd72d4 pc=800c7c08 drawPackets=25545 setupTriangles=134 colored=307200
620 preserve on:  frameHash=0x37fd72d4 pc=800b1bb4 drawPackets=25545 setupTriangles=134 colored=307200
```

Keep this flag off for the stable baseline, but use it for the next PC-focused
trace. It avoids the bad sparse diagnostic path caused by `STATUS_STACK_LIMIT`
while proving that preserving the native output pair changes later control flow.

Follow-up timing/progress checks with the preserve experiment:

```text
620 preserve, 400k steps/frame:
frameHash=0x37fd72d4 pc=800c7ba4 fps=1.03
fifoWords=10806547 fifoPackets=1567223 drawPackets=25545 setupTriangles=134

620 preserve + diagnostic-overlay-suppress + diagnostic-text-pump-skip, 200k steps/frame:
frameHash=0x37fd72d4 pc=80102afc fps=2.01
fifoWords=9701091 fifoPackets=1014495 drawPackets=25545 setupTriangles=134
```

The `800b1bb4` endpoint from the 200k preserve run was not a hard stop; it was
inside the `800b1ba0` render-list builder. More budget naturally reaches later
render/status code. The overlay/text-pump skip profile is now the better local
iteration profile because it reaches comparable loaded/render state at 200k
steps/frame in about half the 400k runtime.

A focused trace of the new `80102afc` endpoint shows it is the epilogue of a
Glide/Voodoo status updater around `80102ac0..80102b18`: it reads status via
`80105ea0`, updates the counter at `80262c80+0x80`, and returns to `800c83b4`.
Do not fast-path this blindly yet. The useful next target is the caller state
after `800c83b4`/`Loading Game.`: determine whether the game is waiting for a
specific Voodoo swap/status transition, a model-load completion bit, or just
spending budget pumping the same loading-screen render path.

Trace around `800c8380..800c8460` shows a better immediate target than the
status-return epilogue. The path at `800c843c` calls into a clear helper at
`800c8400`; that helper walks backward by `0x40` bytes from `80217b78` to the
`802171b8` area and writes zero bytes, then writes zero to `802171b0..802171b2`
and stores `8` at `802380dc`. This is adjacent to the indexed cursor/output
area that the preserve experiment keeps alive, so the next investigation should
check whether this reset is intentionally per-frame loading-screen state or is
erasing indexed loader completion metadata that should persist past
`800c8450`.

Added a narrow trace for that helper:

```text
EUTHERDRIVE_GAUNTDL_TRACE_RUNTIME_LOADING_RESET_HELPER=1
EUTHERDRIVE_GAUNTDL_TRACE_RUNTIME_LOADING_RESET_HELPER_LIMIT=12
```

It signature-checks `800c8400/800c843c` and logs `802171b0..b2`, cursor words
at `802171b8/80217338/80217738/80217b78`, and `802380dc`. A 120-frame check
confirmed it hits without full instruction trace noise. Early calls see the
cursor words already zero; a later pre-call sample had `b171b1=02 b171b2=02`
while the cursor words were still zero. This points more toward byte-sized
loading/reset state near `802171b0..b2` than the word cursor values as the next
thing to compare before and after the indexed preserve path.

Follow-up comparison at 120 frames:

```text
preserve off + loading-reset trace:
pre-reset b171b0=00 b171b1=02 b171b2=02
post-byte0-write b171b0=00 b171b1=00 b171b2=02
pc=801093c8 frameHash=0x9ac85dc5

preserve on, clean indexed preserve log:
bgloadmodel-indexed-prepare-detail-preserve ... b171b0=00 b171b1=01 b171b2=01 g380dc=00000000
pc=80106b04 frameHash=0x9ac85dc5
```

This rules out the `800c8400` clear helper as the direct preserve-vs-baseline
split: both paths see the same early reset behavior. The bytes at
`802171b1..b2` still look meaningful, but by the time the indexed preserve
fastpath fires they have advanced to `01/01`, while an earlier reset phase had
`02/02`. The preserve trace now logs those bytes directly so future indexed
runs can compare phase state without enabling the reset-helper trace.

A focused write trace on `802171b0..b2` confirms those bytes are phase/progress
state, not the indexed asset completion metadata:

```text
pc=80005b18 write64 802171b0 00000000
pc=800103a4 write32 802171b0 00000000
pc=800c8424 write8  802171b0 0
pc=800c842c write8  802171b1 0
pc=800c8438 write8  802171b2 0
pc=800c80d8 write8  802171b1 1..8
pc=800c80e8 write8  802171b1 0
pc=800c8108 write8  802171b2 1..8
pc=800c8110 write8  802171b0 1..8
```

The preserve run still ends at the same 120-frame render signature
(`frameHash=0x9ac85dc5`, `drawPackets=9581`, `setupTriangles=0`) while the
terminal PC moves from `801093c8` to `80106b04`. The bytes are useful as a
loading-screen phase marker, but the real indexed split remains the synthetic
QIO object's native file state: before `800ec268`, the caller has an output
pair, while object fields still say `obj+0x0c=ffffffff` and `obj+0x34=0`.

Added a narrow object-state trace for the next run:

```text
EUTHERDRIVE_GAUNTDL_TRACE_BGLOADMODEL_INDEXED_QIO_OBJECT_STATE=1
EUTHERDRIVE_GAUNTDL_TRACE_BGLOADMODEL_INDEXED_QIO_OBJECT_STATE_LIMIT=80
```

It is gated on the known indexed signatures and logs `80295750` fields
`0x00/0x0c/0x14/0x18/0x20/0x34`, the `80217c58` QIO record, derived file-state
summary, stack output slots, and the `802171b0..b2` phase bytes at
`800c9678`, `800c97b0`, `800c97b8`, `800c97c4`, `800c97cc`, `800c97d8`,
`800c97e0`, `800abe78`, and `800c9944`. Use this before adding another repair:
the question is now whether to set native file-state metadata earlier than
`800ec268`, or to keep the preserve helper as a narrow emulation of that call.

First 120-frame object-state trace with preserve enabled:

```text
request-create-object:
obj00=8021e88c obj0c=ffffffff obj14=0000300b obj18=00000000 obj20=00000000 obj34=00000000
qio=80217c58:00000000/800ab4e4/802e1718/00002000/00002000/ffffffff
fp20=80102a60 fp28=802171b8 fp38=802171b8

status-check-call:
obj00=8021e88c obj0c=ffffffff obj14=00001c01 obj18=00000000 obj20=00000000 obj34=00000000
qio=80217c58:00000000/00000000/00000000/00000000/00000000/00000000
fp20=80217c58 fp28=802171b8 fp38=802171b8

prepare-detail-call:
obj00=8021e88c obj0c=ffffffff obj14=00001c00 obj18=00000000 obj20=00000000 obj34=00000000
qio=80217c58:00000000/00000000/00000000/00000000/00000000/00000000
fp20=80217c58 fp28=802171b8 fp38=802171b8

prepare-detail-return after preserve:
obj00=8021e88c obj0c=ffffffff obj14=0000300b obj18=00000000 obj20=00000000 obj34=00000000
fp20=80217c58 fp28=802171b8 fp38=802171b8

pre-status-call after obj20 repair:
obj00=8021e88c obj0c=ffffffff obj14=0000300b obj18=00000000 obj20=80218518 obj34=00000000

before-compare:
obj00=8021e88c obj0c=ffffffff obj14=00003000 obj18=00000000 obj20=80218518 obj34=00000000

qio-callback:
obj00=8021e88c obj0c=ffffffff obj14=00003000 obj18=00000000 obj20=80218518 obj34=00000000
qio=80217c58:00000000/00000000/00000000/00000000/00000000/00000002
```

The corresponding 120-frame result remains unchanged:

```text
pc=80106b04 frameHash=0x9ac85dc5 drawPackets=9581 directTriangles=31 setupTriangles=0
```

Follow-up memory traces:

```text
80295750+0x0c:
pc=800ebd34 write32 ffffffff
pc=800ec790 write32 ffffffff
pc=800ec828 write32 00000006, 00000086, 00000106, ... increasing transient handles
pc=800f0c4c write32 ffffffff

80295750+0x34:
no writes observed before the 120-frame indexed preserve window
```

Conclusion: `obj+0x0c` is a transient native handle that has already been closed
back to `ffffffff` when the indexed detail helper runs, and `obj+0x34` is never
populated for this synthetic indexed path. A blind metadata fill before
`800ec268` would invent native state that the game has not established. The
current preserve helper is therefore the safer narrow emulation surface: it
keeps the already-produced caller output pair and returns the expected low
status without pretending the native file handle is still open.

A 420-frame object-state run shows this applies beyond the first indexed
request, but also exposes a second class:

```text
s0=2000 s1=188d2303 s2=2:
fp28=80217338 fp38=80217338 -> preserve fires

s0=0120 s1=0120 s2=3:
fp28=80217338 fp38=00000000 fp78=00018e00
native helper clears fp28/fp38 and output

s0=2000 s1=000214c0 s2=7:
fp28=802e1838 fp38=00000000 fp78=00000000
native helper clears fp28/fp38 and output
```

The run still reaches the loaded/rendering plateau:

```text
420 preserve + object-state trace:
pc=800d13dc frameHash=0x37fd72d4 drawPackets=25545 directTriangles=303 setupTriangles=134 colored=307200
```

Added a follow-up experiment for the short/partial indexed detail class:

```text
EUTHERDRIVE_GAUNTDL_EXPERIMENT_RUNTIME_BGLOADMODEL_INDEXED_PREPARE_DETAIL_PRESERVE_PARTIAL=1
```

This only applies when the main preserve experiment is already enabled and
`fp+0x28` is non-zero while `fp+0x38` is zero. It writes `fp+0x38 = fp+0x28`
before returning `0x300b`, preserving pointer-pair semantics without inventing
an open `obj+0x0c` handle or a non-zero `obj+0x34`.

Verification with partial preserve enabled:

```text
220 preserve + partial:
partial hits:
  cursor=80217338 limit=80217338 partialLimit=True
  cursor=802e1838 limit=802e1838 partialLimit=True
pc=800b0c54 frameHash=0x37fd72d4
drawPackets=17039 directTriangles=303 setupTriangles=134 colored=307200

420 preserve + partial:
pc=80103360 frameHash=0x37fd72d4
drawPackets=25545 directTriangles=303 setupTriangles=134 colored=307200
fifoWords=8787867 fifoPackets=557883
```

Compared with the 420 preserve-only object-state run:

```text
420 preserve only:     pc=800d13dc frameHash=0x37fd72d4 drawPackets=25545 directTriangles=303 setupTriangles=134
420 preserve+partial:  pc=80103360 frameHash=0x37fd72d4 drawPackets=25545 directTriangles=303 setupTriangles=134
```

So partial preserve does not change the loaded/rendering plateau by 420 frames,
but it does move later control flow. Keep it as an experiment for the next
620-frame comparison and inspect the new `80103360` endpoint before promoting
it into the default indexed preserve profile.

620-frame comparison:

```text
620 preserve + overlay/text skip:
pc=80102afc frameHash=0x37fd72d4
drawPackets=25545 directTriangles=31 setupTriangles=134
fifoWords=9701091 fifoPackets=1014495

620 preserve + partial + overlay/text skip:
pc=8012027c frameHash=0x37fd72d4
drawPackets=25545 directTriangles=303 setupTriangles=134
fifoWords=9701091 fifoPackets=1014495
colored=307200
```

This is the best indexed detail result so far: same loaded framebuffer and FIFO
plateau, but later control flow and more direct triangles than the preserve-only
620 profile. Next target is the new `8012027c` endpoint and the remaining
empty indexed source slots 4..8; partial preserve fixes the short/partial detail
output clearing, but it does not synthesize the still-empty later source
headers.

Follow-up on remaining indexed source slots:

```text
EUTHERDRIVE_GAUNTDL_EXPERIMENT_RUNTIME_BGLOADMODEL_INDEXED_TEXTURE_QIO_SHORT_READ_FILL_REMAINING=1
```

Before fixing the payload table, bulk filling the remaining 0x120-byte source
headers showed that slots 4/5 contained the expected `f00b0001` marker, but slot
6 contained float-like data. A raw disk scan for little-endian `f00b0001` found
the actual slot-6 header at `0x15781640`, meaning the slot-6 payload base should
be `0x15781600` instead of `0x15783200`.

With the corrected `geb` base, a 420-frame partial+fill run produced coherent
headers for all remaining source slots:

```text
slot4: first=f00b0001 len=00009a58 w60=0000001e w64=0000000d
slot5: first=f00b0001 len=00009df0 w60=0000001e w64=0000000d
slot6: first=f00b0001 len=0000b330 w60=0000001f w64=00000017
slot7: first=f00b0001 len=0000b0c4 w60=00000020 w64=00000013
slot8: first=f00b0001 len=0000ac60 w60=00000020 w64=00000010
```

Verification after the offset fix:

```text
420 preserve + partial + fillRemaining:
pc=80104690 frameHash=0x939a9769
drawPackets=25631 directTriangles=728 setupTriangles=345
fifoWords=10692571 fifoPackets=586086
texWrites=8577630 textureMapTouched=159552
colored=123720 nonBlack=307200

620 preserve + partial + fillRemaining:
pc=80102584 frameHash=0x939a9769
drawPackets=25631 directTriangles=728 setupTriangles=345
fifoWords=11605801 fifoPackets=1042701
texWrites=8577630 textureMapTouched=159552
colored=123720 nonBlack=307200
```

This confirms the slot-6 table entry was wrong and that all slots 1..8 can now
hydrate sane indexed headers. Do not promote `SHORT_READ_FILL_REMAINING` into
the stable profile yet: it increases geometry and texture upload activity, but
it changes the framebuffer hash and lowers the fully colored-pixel count
compared with the current best partial-only profile. The next useful experiment
is earlier/selective seeding of the empty indexed source headers at the
distinct-source repair point, instead of late bulk hydration after a short-read
request has already progressed.

Added that selective early-seeding experiment:

```text
EUTHERDRIVE_GAUNTDL_EXPERIMENT_RUNTIME_BGLOADMODEL_DISTINCT_SOURCE_INDEXED_HEADER=1
```

It runs inside the existing distinct-source repair, only for indexed slots 1..8,
only when the destination source window is still empty, and hydrates only the
0x120-byte indexed header. This avoids the late `SHORT_READ_FILL_REMAINING`
bulk path while making the parser see real headers for slots 2..8 before the
stream-limit helper needs them. Slot 1 is normally already filled by the indexed
QIO read and is not rewritten.

Verification:

```text
420 preserve + partial + early distinct-source indexed headers:
pc=801035a8 frameHash=0x5da1211e
drawPackets=28166 directTriangles=634 setupTriangles=299
fifoWords=13394500 fifoPackets=641818
texWrites=11100775 textureMapTouched=145408
colored=115420 nonBlack=307200

620 preserve + partial + early distinct-source indexed headers:
pc=80102558 frameHash=0x5da1211e
drawPackets=28166 directTriangles=634 setupTriangles=299
fifoWords=14307726 fifoPackets=1098431
texWrites=11100775 textureMapTouched=145408
colored=115420 nonBlack=307200
```

This is not better than the current best partial-only profile for visible boot
progress because it still changes the framebuffer hash and keeps fewer colored
pixels. It is, however, a cleaner diagnostic than bulk fill: all indexed headers
are present early and deterministically, the slot-6 `geb` header is correct, and
the increased geometry/texture traffic proves the remaining parser path can see
those slots. The next target should be why real indexed source headers push the
render path into this alternate framebuffer signature instead of preserving the
`0x37fd72d4` loaded-screen plateau.

Added a mask for bisecting that experiment without recompiling:

```text
EUTHERDRIVE_GAUNTDL_EXPERIMENT_RUNTIME_BGLOADMODEL_DISTINCT_SOURCE_INDEXED_HEADER_MASK=0x...
```

The mask uses bit positions matching slot numbers, so `0x4` enables only slot 2
and `0x80` enables only slot 7. Two 420-frame checks:

```text
mask=0x4:
pc=80102a2c frameHash=0x1bd7b4a1
drawPackets=27321 directTriangles=1129 setupTriangles=546
fifoWords=8848487 fifoPackets=536670
texWrites=6908961 textureMapTouched=56247
colored=125378 nonBlack=146399

mask=0x80:
pc=80103360 frameHash=0xb1d3ec77
drawPackets=23870 directTriangles=1913 setupTriangles=937
fifoWords=9950803 fifoPackets=562913
texWrites=7889841 textureMapTouched=74737
colored=128905 nonBlack=133602
```

These runs are useful because they show different failure signatures. With
`mask=0x4`, early slot-2 seeding makes the main indexed QIO fill slot 3 next;
with `mask=0x80`, the normal QIO/short-read flow fills slots 2 and 3 while only
slot 7 is added early. Both paths produce more geometry than the current
partial-only baseline but reduce framebuffer coverage. Continue by tracing the
Voodoo state emit around the first divergent texture/state packet rather than
trying more broad header hydration.

## 2026-06-11 Checkpoint Probe: MAME FIFO White-Clear Failure

The MAME-style Voodoo command FIFO model remains an opt-in diagnostic:

```text
EUTHERDRIVE_GAUNTDL_FIX_VOODOO_MAME_CMD_FIFO_MODEL=1
```

The latest probes moved the failure from a vague "white framebuffer" symptom to
a specific command stream problem. Standard mode at f400 after the fastfill mask
default change still renders the castle plateau:

```text
standard f400:
frameHash=0x8e14c17e
drawPackets=1024 direct/setup=2818/1394
texWrites=5431619 fastFills=3399 swaps=2774
packetTypes=0:2422,1:42639,2:0,3:1024,4:125877,5:84869,6:0,7:6
framebuffer colored=307199
ffk=692/581/0
```

The MAME FIFO model reaches roughly the same workload envelope but clears both
visible buffers white and loses setup triangles:

```text
MAME FIFO f400:
frameHash=0x9ac85dc5
drawPackets=1046 direct/setup=46/0
texWrites=6555139 fastFills=1762 swaps=1382
packetTypes=0:9943,1:42142,2:0,3:1046,4:122690,5:102424,6:0,7:3
framebuffer colored=0
cmdstop=depth/0x00059604/4/3/0x1ADCD70/pc=0xFFFFFFFF801031A8/2087871
ffk=0/0/0
```

Running to f500 did not recover, so this is not just a frame-boundary partial
packet. Masking the MAME FIFO read index is neutral. Truncating partial type-4
packets is explicitly negative:

```text
EUTHERDRIVE_GAUNTDL_EXPERIMENT_VOODOO_MAME_FIFO_TRUNCATE_PARTIAL_TYPE4=1

f400:
packetTypes=0:57921,1:40547,2:0,3:1046,4:122631,5:102424,6:0,7:6169
framebuffer colored=0
cmdstop=partial-type4-truncate/0x00059604/4/2/0x1ADCD70/pc=0xFFFFFFFF8010319C/2071355
```

Do not promote partial type-4 tolerance; it creates a type-7 storm and no visual
win.

Added filtered command-FIFO tracing so
`EUTHERDRIVE_GAUNTDL_TRACE_VOODOO_CMD_FIFO_MODEL_COMMANDS` can filter both FIFO
writes and register-value traces by value. This made it easy to isolate the
recurring `0xffffffff` writes that keep the fastfill color white.

Important traces:

```text
0x00059604 producers:
pc=0xffffffff80106a74
pc=0xffffffff800bd18c

MAME FIFO f260 with value filter 0xffffffff:
packet=0x0001828c packetType=4 target=0x051/0x052 value=0xffffffff
packet=0x0104824c packetType=4 target=0x04c/0x052 value=0xffffffff
```

Fastfill/swap PC profiles now show the actual color divergence:

```text
standard f260:
frameHash=0x8e14c17e
direct/setup=2594/1282
fastFills=2338 swaps=2086
ffk=636/535/0
framebuffer colored=307199

MAME FIFO f260:
frameHash=0x1e212a0b
direct/setup=44/0
fastFills=1045 swaps=806
ffw=373/20/0 ffk=0/0/0
cmdstop=depth/0x0001828C/3/2/0x17C9D44/pc=0xFFFFFFFF8010319C/1948084
framebuffer colored=695
```

`EUTHERDRIVE_GAUNTDL_EXPERIMENT_VOODOO_FIFO_BULK_DECODE_WINDOW=1` on top of the
MAME model is also negative at f400. It keeps the framebuffer white, reduces
draw packets, and introduces type-7 packets:

```text
MAME FIFO + FIFO_BULK_DECODE_WINDOW f400:
frameHash=0x9ac85dc5
drawPackets=897 direct/setup=46/0
packetTypes=0:44398,1:40298,2:0,3:897,4:116552,5:102325,6:0,7:197
framebuffer colored=0
```

Added a decode-throttle diagnostic:

```text
EUTHERDRIVE_GAUNTDL_EXPERIMENT_VOODOO_MAME_FIFO_DECODE_PACKET_LIMIT=1
```

This limits each `DecodeCommandFifoPackets()` call to one packet in MAME FIFO
mode only, testing whether the failure is caused by draining too much FIFO
synchronously after each write. It is also negative:

```text
MAME FIFO + DECODE_PACKET_LIMIT=1 f260:
frameHash=0x3a91e1cf
drawPackets=648 direct/setup=44/0
fastFills=1059 swaps=806
ffw=386/21/0 ffk=0/0/0
framebuffer colored=739

MAME FIFO + DECODE_PACKET_LIMIT=1 f400:
frameHash=0x9ac85dc5
drawPackets=944 direct/setup=46/0
packetTypes=0:10827,1:41586,2:0,3:944,4:121133,5:102539,6:0,7:3
ffw=1103/25/0 ffk=0/0/0
framebuffer colored=0
```

The same white-clear signature remains, so simple decode throttling is not the
missing timing behavior.

Next target: track why the MAME depth/address-min model repeatedly decodes
type-4 packets that set `RegColor0`/`RegColor1` to `0xffffffff` and stalls on
the same packet class with only two of three words available. The fastfill code
itself is behaving consistently with the register state it receives.

`cmdstop` now also records the masked storage offset and the next two FIFO RAM
words at the stopped read pointer:

```text
cmdstop=reason/cmd/needed/depth/readByte/storageByte/next1/next2/pc/count
```

The first f260 check with the extra fields appeared to show a missing/stale
payload word in the ring:

```text
MAME FIFO f260:
cmdstop=depth/0x0001828C/3/2/0x17C9D44/0x9D44/0xFFFFFFFF/0x00000000/pc=0xFFFFFFFF8010319C/1948084
```

For `0x0001828c`, type 4 needs header plus two payload words for
`RegColor0`/`RegColor1`.

Follow-up storage tracing corrected that interpretation. With
`EUTHERDRIVE_GAUNTDL_TRACE_VOODOO_CMD_FIFO_MODEL_STORAGE=0x9d44,0x9d48,0x9d4c`
and `EUTHERDRIVE_GAUNTDL_TRACE_VOODOO_CMD_FIFO_MODEL_LIMIT=5000`, the final
targeted writes are:

```text
storage=0x09d44 value=0x0001828c pc=0xffffffff80103190 depth=1
storage=0x09d48 value=0xffffffff pc=0xffffffff8010319c depth=2
storage=0x09d4c value=0xffffffff pc=0xffffffff801031a8 depth=3
```

So the `cmdstop` snapshot in the score line is a stale "last stop", not proof
that the third word was never written. The MAME FIFO failure should now be
treated as an ordering/depth/read-index problem that corrupts later state, not
as a simple missing payload slot.

Additional negative diagnostic:

```text
EUTHERDRIVE_GAUNTDL_EXPERIMENT_DISABLE_OUTER_PAYLOAD_FASTPATH=1

MAME FIFO f260:
frameHash=0x967bd23f
drawPackets=635 direct/setup=69/0
fastFills=1010 swaps=822
packetTypes=0:992,1:33010,2:0,3:635,4:96697,5:81256,6:0,7:5
framebuffer colored=635
```

Disabling the `0xffffffff800fe5d4` outer-payload fastpath does not recover the
scene, so that fastpath is not the primary cause.

The current strongest lead is bad setup/type-3 state under the MAME FIFO model.
Normal MAME FIFO f260 does reach setup tracing, but the setup vertices are
degenerate and texture coordinates are often `NaN`, leaving almost the entire
frame white:

```text
MAME FIFO f260 + TRACE_VOODOO_SETUP_TRIANGLES=1:
setup trace pc=0xffffffff800c4e5c / 0xffffffff800fe5d4
xy=(0,-1)/(512,383)/(0,383)
st=(NaN,256)/(NaN,0)/(NaN,0)
fbz transitions from 0x00000460 to 0x00000000
framebuffer colored=695
```

By contrast, the old valid-slot model at the same f260 baseline still produces
the colored scene:

```text
standard f260:
frameHash=0x8e14c17e
drawPackets=764 direct/setup=2594/1282
fastFills=2338 swaps=2086
packetTypes=0:2273,1:34609,2:0,3:764,4:102075,5:80923,6:0,7:3
framebuffer colored=307199
```

Continue by comparing MAME vs standard type-3 packet buffers and the writes to
setup registers `0x98..0xa9`; avoid spending more time on type-4
`0x0001828c` missing-word tolerance unless a fresh trace shows it is current.

Type-3 packet tracing (`EUTHERDRIVE_GAUNTDL_TRACE_VOODOO_TYPE3_PACKETS=1`)
shows the first runtime setup packets are not payload-corrupt under the MAME
model. Standard and MAME both decode the same repeated packet:

```text
cmd=0x0180a8cb words=19 count=3 code=1 flags=0x602a
packet=0x0180a8cb/00000000/bf800000/437f0000/3f800000/ffc00000/43800000/44000000/43bf8000/437f0000/3f800000/ffc00000/00000000/00000000/43bf8000/437f0000
```

The visible difference is FIFO model state, not the type-3 packet words:

```text
standard type3 trace:
rd=0x00020908 mame=0 depth=19 holes=0 pc=0xffffffff800c4e5c

MAME type3 trace:
rd=0x00020908 mame=1 depth=19 holes=0 pc=0xffffffff800c4e5c
...
rd=0x0011a2dc mame=1 depth=3144 holes=0 pc=0xffffffff800fe5d4
rd=0x0031aab8 mame=1 depth=13909 holes=0 pc=0xffffffff800fe5d4
```

So the `NaN` setup trace is expected for this packet shape and is not unique to
MAME. The white-screen divergence is more likely due to which state/fill/swap
packets MAME chooses to drain around these valid type-3 packets, especially
when the unmasked read pointer/depth climbs through large outer-payload bursts.

Type-0/local-jump tracing (`EUTHERDRIVE_GAUNTDL_TRACE_VOODOO_TYPE0_PACKETS=1`)
rules out local jumps as the immediate cause of the `0xffffffff800fe5d4`
fill/swap divergence. At f260, the traced type-0 packets around that PC are
plain no-op zero words in both models:

```text
standard:
cmd=0x00000000 fn=0 rdBefore=0x00000000.. mame=0
frameHash=0x8e14c17e colored=307199

MAME FIFO:
cmd=0x00000000 fn=0 rdBefore=0x001000d0.. mame=1
frameHash=0x1e212a0b colored=695
```

The important difference is that the standard model repeatedly drains from the
low ring slots while the MAME model drains stale zero packets through large
unmasked read indices (`0x001000d0`, `0x0010cc00`, `0x0038de34`, etc.) even
when `addressMin` points elsewhere. `lj=0` in the baseline MAME run, so
`EUTHERDRIVE_GAUNTDL_EXPERIMENT_VOODOO_MAME_FIFO_MASK_LOCAL_JUMP=1` is not a
useful recovery path for this case.

Two storage-validity experiments are available but not fixes:

```text
EUTHERDRIVE_GAUNTDL_EXPERIMENT_VOODOO_MAME_FIFO_REQUIRE_VALID_STORAGE=1
frameHash=0x4c332cd2
drawPackets=216 direct/setup=44/0
packetTypes=0:56983,1:27763,2:0,3:216,4:78942,5:26800,6:0,7:588
framebuffer colored=4772
cmdstop=invalid-storage

EUTHERDRIVE_GAUNTDL_EXPERIMENT_VOODOO_MAME_FIFO_REQUIRE_VALID_STORAGE=1
EUTHERDRIVE_GAUNTDL_EXPERIMENT_VOODOO_MAME_FIFO_RESYNC_INVALID_STORAGE_TO_AMIN=1
frameHash=0x6c05a33c
drawPackets=196 direct/setup=44/0
packetTypes=0:23095,1:28028,2:0,3:196,4:81147,5:55494,6:0,7:3148
framebuffer colored=980
cmdstop=depth/0x0001828c

EUTHERDRIVE_GAUNTDL_EXPERIMENT_VOODOO_MAME_FIFO_SKIP_INVALID_STORAGE=1
frameHash=0xf3562103
drawPackets=354 direct/setup=44/0
packetTypes=0:6456,1:29484,2:0,3:354,4:84855,5:45229,6:0,7:3
framebuffer colored=643
cmdstop=depth/0x0001828c
```

`REQUIRE_VALID_STORAGE` is a weak positive signal because it reduces stale zero
drain enough to raise colored pixels from 695 to 4772, but it also stalls on an
invalid storage slot with millions of depth. Resyncing that invalid slot to
`addressMin` is negative and reintroduces a type-7 storm. Skipping invalid
storage slots is also negative: it reduces the type-0 storm, but it collapses
draw packet count and stays white. Continue by fixing the MAME
depth/address-min accounting so read pointer, valid storage, and available
depth describe the same FIFO window before decode readiness is evaluated.

## 2026-06-11 Checkpoint: MAME FIFO Validity Window

Added command-FIFO storage-validity accounting and a targeted trace:

```text
EUTHERDRIVE_GAUNTDL_TRACE_VOODOO_CMD_FIFO_VALIDITY=1
EUTHERDRIVE_GAUNTDL_TRACE_VOODOO_CMD_FIFO_VALIDITY_LIMIT=...
```

`DebugStatus` now reports `cmd=depth/holes/valid/amin/amax`, and the existing
command-FIFO model/stop traces include `valid=...`. This made the f260 MAME
failure more precise. The first useful validity trace fires at
`pc=0xffffffff800fe5d4`:

```text
rd=0x000fffc8 storage=0x3ffc8 readValid=0
depth=12620 holes=0 valid=12620 excess=0
amin=0x0000c5fc amax=0x0000c5fc
fifoPackets=100002 drawPackets=28
```

So the immediate issue is not missing words (`depth == valid`), but a read
pointer aimed at stale/invalid storage while the current contiguous write
window is elsewhere.

A PC-filtered command-FIFO trace around `0xffffffff800fe5d4` shows the same
thing at the producer boundary. When the outer-payload loop wraps and starts
writing address `0x00000`, the MAME path still has `rd=0xfffc8` and grows
`depth` from the old tail:

```text
write addr=0x00000 value=0xc0000205 rd=0xfffc8 depth=15 valid=14
write addr=0x00004 value=0x00008000 rd=0xfffc8 depth=16 valid=15
...
```

Two narrower recovery probes are available, but both are negative as fixes:

```text
EUTHERDRIVE_GAUNTDL_EXPERIMENT_VOODOO_MAME_FIFO_WRAP_CLEAR_INVALID_READ=1
frameHash=0xf802f22b
drawPackets=598 direct/setup=44/0
packetTypes=0:15426,1:32536,2:0,3:598,4:95104,5:77834,6:0,7:3
framebuffer colored=699

EUTHERDRIVE_GAUNTDL_EXPERIMENT_VOODOO_MAME_FIFO_BULK_RESYNC_INVALID_READ=1
frameHash=0x04cfe08b
drawPackets=444 direct/setup=44/0
packetTypes=0:24851,1:30622,2:0,3:444,4:88721,5:92314,6:0,7:21
framebuffer colored=889
```

`WRAP_CLEAR_INVALID_READ` is less destructive than the old blanket
`MAME_FIFO_WRAP_CLEAR`, but it still leaves the white-screen signature.
`BULK_RESYNC_INVALID_READ` keeps the valid map intact and only moves the read
pointer to the just-written bulk start when the old read storage is invalid;
it slightly raises colored pixels but collapses draw packet count and
introduces type-7 packets. Treat both as negative diagnostics.

A narrower wrap-gap skip was also tested:

```text
EUTHERDRIVE_GAUNTDL_EXPERIMENT_VOODOO_MAME_FIFO_SKIP_WRAP_GAP_INVALID_READ=1
frameHash=0xf02ebb13
drawPackets=615 direct/setup=44/0
packetTypes=0:18075,1:32764,2:0,3:615,4:95864,5:91924,6:0,7:8
framebuffer colored=699
lj=1
```

This only skips invalid read storage when the read pointer is within 64 words
of the ring end and storage slot 0 is already valid, without decrementing
depth. It is still negative: it does not recover the black clear/swap sequence
and it introduces a local-jump path that baseline MAME did not take.

Two follow-up validity-window probes are also negative:

```text
EUTHERDRIVE_GAUNTDL_EXPERIMENT_VOODOO_MAME_FIFO_REQUIRE_VALID_PACKET_WINDOW=1
frameHash=0x719aedfb
drawPackets=232 direct/setup=44/0
packetTypes=0:2792,1:27961,2:0,3:232,4:79725,5:29814,6:0,7:10
framebuffer colored=515
cmdstop=invalid-packet-window/0xC0000205/66/4188042/0x7CFF28/0xFF28/0x00012A00/0x00000000/pc=0xFFFFFFFF801031A8/1991659

EUTHERDRIVE_GAUNTDL_EXPERIMENT_VOODOO_MAME_FIFO_CONSUME_WRAP_GAP_INVALID_READ=1
frameHash=0xbe8d5453
drawPackets=636 direct/setup=44/0
packetTypes=0:22514,1:33014,2:0,3:636,4:96713,5:91746,6:0,7:41
framebuffer colored=683
cmdstop=depth/0xC0000205/66/38/0xF604E4/0x204E4/0x00011A00/0x00000000/pc=0xFFFFFFFF801031A8/1947438
```

`REQUIRE_VALID_PACKET_WINDOW` proves that simply requiring all packet words to
be valid storage blocks too much useful work once the MAME read pointer has
already diverged from the producer window. `CONSUME_WRAP_GAP_INVALID_READ`
tests the same 64-word ring-end skip while also decrementing depth, but it
keeps the same white-screen signature and increases type-7 packets. These
results point away from more read-side resync heuristics.

An opt-in producer-generation reconstruction was added:

```text
EUTHERDRIVE_GAUNTDL_EXPERIMENT_VOODOO_MAME_FIFO_TRACK_WRITE_GENERATION=1
frameHash=0x383411ef
drawPackets=542 direct/setup=44/0
packetTypes=0:6870,1:31836,2:0,3:542,4:92925,5:86684,6:0,7:3
framebuffer colored=499
cmd=0/1671418/65536/0x0/0x7C9D4C
cmdstop=depth/0xC0000205/66/2/0x165FFFC/0x1FFFC/0x0000A400/0x00000000/pc=0xFFFFFFFF800FE5FC/387117

EUTHERDRIVE_GAUNTDL_EXPERIMENT_VOODOO_MAME_FIFO_TRACK_WRITE_GENERATION=1
EUTHERDRIVE_GAUNTDL_EXPERIMENT_VOODOO_MAME_FIFO_REQUIRE_VALID_STORAGE=1
frameHash=0xfa153031
drawPackets=198 direct/setup=44/0
packetTypes=0:56004,1:27531,2:0,3:198,4:78180,5:24380,6:0,7:493
framebuffer colored=33
cmd=3201041/1671418/3767/0x0/0x7C9D4C
cmdstop=invalid-storage/0x00000000/1/3201041/0x289D50/0x9D50/0x00000000/0x00000000/pc=0xFFFFFFFF801031A8/1740907
```

This reconstructs a logical write generation when the storage index wraps from
the ring tail to the head, but it is not enough. The standalone run fills the
valid map and creates a huge hole count, and combining it with valid-storage
gating is worse than the earlier valid-storage-only probe. Default MAME f260
was re-run after adding the opt-in code and remained unchanged:

```text
frameHash=0x1e212a0b
drawPackets=738 direct/setup=44/0
packetTypes=0:8588,1:34284,2:0,3:738,4:100983,5:91713,6:0,7:3
framebuffer colored=695
```

Another opt-in guard tested whether the existing `addressMin/addressMax` values
can be used as the authoritative decode window:

```text
EUTHERDRIVE_GAUNTDL_EXPERIMENT_VOODOO_MAME_FIFO_REQUIRE_READ_IN_ADDRESS_WINDOW=1
frameHash=0x2d1c35fc
drawPackets=4 direct/setup=44/0
packetTypes=0:40,1:22717,2:0,3:4,4:67925,5:672,6:0,7:3
framebuffer colored=0
cmd=6170432/0/65536/0x9D4C/0x9D4C
cmdstop=read-outside-window/0x00000000/1/6170432/0x40050/0x50/0x00000000/0x00000000/pc=0xFFFFFFFF801031A8/2001340
```

This stops in the expected class of failure, but it is not a fix: the current
`addressMin/addressMax` accounting collapses the useful stream to almost no
draw packets. It confirms the window model is internally inconsistent rather
than giving a reliable readiness predicate.

MAME reference checked from current upstream `src/devices/video/voodoo_2.*` and
`voodoo_banshee.*`: `voodoo::command_fifo` stores command FIFO words in the
device framebuffer RAM and masks reads with the framebuffer RAM mask (`m_mask`),
not a standalone 64K command array. The Voodoo2 direct command-FIFO window still
uses a 16-bit write offset, but `peek_next()`/`read_next()` use
`m_ram[m_read_index & m_mask]`. This makes the next useful implementation probe
larger than another read-pointer heuristic: the bring-up backend should test a
full framebuffer-sized command FIFO storage/mask, or share storage with the
Voodoo framebuffer RAM model, before trusting depth/window traces.

The first framebuffer-sized storage probe is available but negative:

```text
EUTHERDRIVE_GAUNTDL_EXPERIMENT_VOODOO_MAME_FIFO_FRAMEBUFFER_STORAGE=1
frameHash=0xbd71006f
drawPackets=44 direct/setup=44/0
packetTypes=0:5843023,1:25611,2:0,3:44,4:71836,5:5326,6:0,7:3
framebuffer colored=443
cmdstop=depth/0xC0000205/66/65/0x143FFDC/0x3FFDC/0x0000DA00/0x00000000/pc=0xFFFFFFFF800FE5FC/187730
```

This follows MAME's larger storage mask more closely, but by itself it exposes
an even larger stale zero/NOP drain. The f260 default MAME run was rechecked
after the opt-in backing-array change and stayed unchanged:

```text
frameHash=0x1e212a0b
drawPackets=738 direct/setup=44/0
packetTypes=0:8588,1:34284,2:0,3:738,4:100983,5:91713,6:0,7:3
framebuffer colored=695
```

A follow-up shared-storage probe mirrored raw LFB writes into the command FIFO
backing array while framebuffer-sized command storage was active:

```text
EUTHERDRIVE_GAUNTDL_EXPERIMENT_VOODOO_MAME_FIFO_FRAMEBUFFER_STORAGE=1
EUTHERDRIVE_GAUNTDL_EXPERIMENT_VOODOO_MAME_FIFO_MIRROR_LFB_WRITES=1
frameHash=0xbd71006f
drawPackets=44 direct/setup=44/0
packetTypes=0:5843023,1:25611,2:0,3:44,4:71836,5:5326,6:0,7:3
framebuffer colored=443
```

This is byte-for-byte the same f260 signature as framebuffer storage alone, so
the stale zero/NOP drain is not caused by ordinary LFB writes missing from the
command FIFO backing store in this warm-snapshot phase.

Another MAME cycle-behavior probe yielded after command FIFO packets that
performed rendering work, matching MAME's `execute_if_ready()` pattern where a
packet handler can return nonzero cycles:

```text
EUTHERDRIVE_GAUNTDL_EXPERIMENT_VOODOO_MAME_CMD_FIFO_YIELD_ON_RENDER_WORK=1
frameHash=0x1e212a0b
drawPackets=738 direct/setup=44/0
packetTypes=0:8588,1:34284,2:0,3:738,4:100983,5:91713,6:0,7:3
framebuffer colored=695
```

This is exactly the same f260 signature as the default MAME command-FIFO model,
so per-render-packet yielding is not the missing behavior in the current
bring-up path.

A type 5 streaming probe tested another MAME semantic difference: upstream
`packet_type_5()` consumes command FIFO source words with `read_next()` while it
writes space 0 payloads back into `m_ram`, so the read pointer/depth move during
the payload copy rather than after the packet handler returns. The standalone
streaming run was unchanged:

```text
EUTHERDRIVE_GAUNTDL_EXPERIMENT_VOODOO_MAME_FIFO_TYPE5_STREAMING=1
frameHash=0x1e212a0b
drawPackets=738 direct/setup=44/0
packetTypes=0:8588,1:34284,2:0,3:738,4:100983,5:91713,6:0,7:3
framebuffer colored=695
```

Combining the same streaming consumption with framebuffer-sized command FIFO
storage was also unchanged from framebuffer storage alone:

```text
EUTHERDRIVE_GAUNTDL_EXPERIMENT_VOODOO_MAME_FIFO_FRAMEBUFFER_STORAGE=1
EUTHERDRIVE_GAUNTDL_EXPERIMENT_VOODOO_MAME_FIFO_TYPE5_STREAMING=1
frameHash=0xbd71006f
drawPackets=44 direct/setup=44/0
packetTypes=0:5843023,1:25611,2:0,3:44,4:71836,5:5326,6:0,7:3
framebuffer colored=443
```

So the gap is not explained by type 5 source/destination overlap timing in the
current warm-snapshot phase.

A command-FIFO register window probe matched MAME's shared `reg_cmdfifo_w`
side effect more directly: every base/AMin/AMax write refreshed base, end,
AMin, and AMax from the stored register set, and the command FIFO end was no
longer clamped to the legacy 64K storage size in that model. The f260 signature
was still unchanged:

```text
EUTHERDRIVE_GAUNTDL_EXPERIMENT_VOODOO_MAME_FIFO_REGISTER_WINDOW=1
frameHash=0x1e212a0b
drawPackets=738 direct/setup=44/0
packetTypes=0:8588,1:34284,2:0,3:738,4:100983,5:91713,6:0,7:3
framebuffer colored=695
```

So the current failure is not coming from base/end/AMin/AMax register update
ordering in the warm-snapshot path.

A depth/holes register-width probe matched another MAME difference: upstream
stores and returns full 32-bit `cmdFifoDepth`/`cmdFifoHoles` values, while the
bring-up backend had masked writes and clamped reads to 16 bits. Making those
register paths full-width under MAME command FIFO mode was also unchanged:

```text
EUTHERDRIVE_GAUNTDL_EXPERIMENT_VOODOO_MAME_FIFO_FULL_DEPTH_HOLES_REGS=1
frameHash=0x1e212a0b
drawPackets=738 direct/setup=44/0
packetTypes=0:8588,1:34284,2:0,3:738,4:100983,5:91713,6:0,7:3
framebuffer colored=695
```

So CPU-visible depth/holes register width is not the missing behavior in the
current f260 warm-snapshot path.

An operation-pending gate probe tested MAME's `if (!operation_pending())
execute_if_ready()` behavior more directly than the earlier render-yield test.
The opt-in gate stopped automatic decode after render-producing command FIFO
packets and resumed on status reads. This did change the signature, but in the
wrong direction:

```text
EUTHERDRIVE_GAUNTDL_EXPERIMENT_VOODOO_MAME_FIFO_OPERATION_PENDING_GATE=1
frameHash=0x298f2277
drawPackets=737 direct/setup=44/0
packetTypes=0:7543,1:34100,2:0,3:737,4:101308,5:91716,6:0,7:3
framebuffer colored=570
```

The result proves command-FIFO scheduling can affect the trace, but the
status-read approximation is not the missing MAME behavior. A real fix would
need cycle/time-based `operation_pending()` semantics, not just a one-status-read
gate.

An exact hole-accounting probe removed the bring-up backend's defensive
clamping around the MAME-style `holes/depth/addressMin/addressMax` write tracker,
letting the same raw signed deltas MAME's command FIFO model would produce flow
through the local accounting path. The f260 signature was byte-for-byte unchanged
from the MAME command FIFO baseline:

```text
EUTHERDRIVE_GAUNTDL_EXPERIMENT_VOODOO_MAME_FIFO_EXACT_HOLE_ACCOUNTING=1
frameHash=0x1e212a0b
drawPackets=738 direct/setup=44/0
packetTypes=0:8588,1:34284,2:0,3:738,4:100983,5:91713,6:0,7:3
framebuffer colored=695
```

So the warm-snapshot failure is not explained by the remaining clamped
hole/depth arithmetic in the local write tracker.

A stricter packet address-window probe moved the existing address-window guard
from the packet start word to the full decoded packet range after `wordsNeeded`
is known. It stopped even harder than the earlier start-word guard:

```text
EUTHERDRIVE_GAUNTDL_EXPERIMENT_VOODOO_MAME_FIFO_REQUIRE_PACKET_IN_ADDRESS_WINDOW=1
frameHash=0x9ac85dc5
drawPackets=0 direct/setup=44/0
packetTypes=0:618,1:22667,2:0,3:0,4:67757,5:0,6:0,7:3
framebuffer colored=0
cmdstop=packet-outside-window/0xC0000205/66/6214972/0x14860/0x14860/0x0001B800/0x00000000/pc=0xFFFFFFFF801031A8/2001340
```

This confirms that `addressMin/addressMax` are currently a symptom of the bad
window model, not a usable authoritative readiness predicate. Packet-window
validation should only become useful after the producer/read window is tracked
coherently.

A register-side decode probe called the command FIFO decoder after
`addressMin`, `addressMax`, `depth`, and `holes` register writes, matching the
possibility that MAME's command-FIFO register path can invoke
`execute_if_ready()` after visible FIFO accounting changes. The f260 signature
was unchanged:

```text
EUTHERDRIVE_GAUNTDL_EXPERIMENT_VOODOO_MAME_FIFO_DECODE_ON_REG_WRITE=1
frameHash=0x1e212a0b
drawPackets=738 direct/setup=44/0
packetTypes=0:8588,1:34284,2:0,3:738,4:100983,5:91713,6:0,7:3
framebuffer colored=695
```

So the current failure is not from missing decode attempts after CPU-visible
FIFO accounting register writes.

A storage-generation validity probe tagged each command FIFO storage slot with
the logical write index that produced it, then required reads to match that same
logical generation. It stopped on the expected alias class:

```text
EUTHERDRIVE_GAUNTDL_EXPERIMENT_VOODOO_MAME_FIFO_REQUIRE_STORAGE_GENERATION=1
frameHash=0x9ac85dc5
drawPackets=4 direct/setup=44/0
packetTypes=0:5,1:25111,2:0,3:4,4:70156,5:481,6:0,7:3
framebuffer colored=0
cmdstop=storage-generation/0x00000000/1/6170448/0x40010/0x10/0x00000000/0x00000000/pc=0xFFFFFFFF801031A8/1996012
```

This confirms that stale valid storage slots are being mistaken for the current
read generation, but a strict generation gate alone is not a fix because the
producer/read generation model still collapses the useful command stream.

A write-generation alignment probe tried to reconstruct each 16-bit FIFO write
offset against the current logical read generation: if the local write index was
behind `_cmdFifoReadIndex`, it was promoted by whole 64K generations until it
was at or beyond the read pointer. This changed the trace, but collapsed useful
rendering:

```text
EUTHERDRIVE_GAUNTDL_EXPERIMENT_VOODOO_MAME_FIFO_ALIGN_WRITES_TO_READ_GENERATION=1
frameHash=0x9ac85dc5
drawPackets=4 direct/setup=44/0
packetTypes=0:68,1:25111,2:0,3:4,4:70156,5:480,6:0,7:3
framebuffer colored=0
cmd=0/65474/65536/0x0/0x3FF08
cmdstop=depth/0xC0000205/66/62/0x3FF08/0x3FF08/0x0001C000/0x00000000/pc=0xFFFFFFFF800FE5FC/105280
```

So generation reconstruction cannot simply promote every local write behind the
read pointer. The useful model needs to preserve contiguous producer windows and
only advance generation when the producer actually wraps the command FIFO
stream, not merely when a local offset compares below the current read pointer.

A depth-short override probe tested the final f260 stall directly: when
`cmdFifoDepth < wordsNeeded`, it allowed decode to continue if the full packet
window was valid in local storage. This changed execution, but not in the right
direction:

```text
EUTHERDRIVE_GAUNTDL_EXPERIMENT_VOODOO_MAME_FIFO_ALLOW_VALID_PACKET_DEPTH_SHORT=1
frameHash=0xdb8223ab
drawPackets=726 direct/setup=44/0
packetTypes=0:7424,1:34632,2:0,3:726,4:102424,5:107534,6:0,7:3
framebuffer colored=651
cmdstop=depth/0x0001828C/3/2/0x1BC9D44/0x9D44/0xFFFFFFFF/0x00000000/pc=0xFFFFFFFF8010319C/1951887
```

The override does let some short-depth packets through, but the signature is
worse and the same type4 depth stall remains. The missing behavior is therefore
not simply "trust valid storage when depth is short"; the depth/window model
itself needs to stop drifting.

A depth I/O accounting probe added `cmdio=added/decoded/streamed` to the Voodoo
debug status. The default MAME FIFO f260 signature stayed unchanged:

```text
frameHash=0x1e212a0b
drawPackets=738 direct/setup=44/0
packetTypes=0:8588,1:34284,2:0,3:738,4:100983,5:91713,6:0,7:3
framebuffer colored=695
cmd=0/0/0/0x9D4C/0x9D4C
cmdio=6215585/6215585/0
cmdstop=depth/0x0001828C/3/2/0x17C9D44/0x9D44/0xFFFFFFFF/0x00000000/pc=0xFFFFFFFF8010319C/1948084
```

That rules out a simple cumulative depth leak: by the end of the f260 probe the
model has added and decoded the same number of FIFO words. The repeated
`cmdstop=depth` is a transient short-packet wait that later drains, not the
primary end-state cause of the bad frame. The useful next probe is to compare
the content/register side of the packet stream against the good direct FIFO
path, especially why MAME FIFO reaches only 44 direct triangles and no setup
triangles despite decoding many packets.

Next target: replace the ad hoc MAME `depth/holes/addressMin/addressMax`
tracking with a coherent command-FIFO window model. The useful invariant from
the new trace is that decode readiness must not be true when `depth` and
`valid` are nonzero but `_cmdFifoReadIndex & CmdFifoMask` is outside the
current producer generation. The likely implementation direction is to preserve
or reconstruct logical producer generation when writes wrap, because the
current write path often hands the Voodoo backend only a masked local storage
offset while the MAME read pointer is still unmasked.
