# Secret of Evermore SNES Debug Notes

Date: 2026-03-15

## Goal

Find the root cause of the remaining graphics corruption in `Secret of Evermore (USA)` on the SNES core.

ROM used:

- `/run/media/nichlas/Atlas/SNES/sd2snes/1 S-Z - US/Secret of Evermore (USA).sfc`

Savestate bundle used:

- `/home/nichlas/EutherDrive/savestates/Secret_of_Evermore__USA_.sfc_17c864a7.euthstate`

## Current local state

Relevant dirty SNES files:

- `/home/nichlas/EutherDrive/SuperNintendoEmulator/KSNES/PictureProcessing/PPU.cs`
- `/home/nichlas/EutherDrive/SuperNintendoEmulator/KSNES/SNESSystem/SNESSystem.cs`

Unrelated dirty files that should not be touched when resuming this work:

- `/home/nichlas/EutherDrive/EutherDrive.Core/SegaCd/SegaCdAdapter.cs`
- `/home/nichlas/EutherDrive/EutherDrive.Core/SegaCd/SegaCdCddStub.cs`
- `/home/nichlas/EutherDrive/EutherDrive.Core/SegaCd/SegaCdMemory.cs`

No commit was made for the SoE work in this round.

## High-level conclusion so far

The strongest remaining suspicion is no longer "bad DMA source data" in the CPU-side builder. That path was traced fairly deeply and looked internally consistent.

The stronger remaining suspect is now the SNES PPU/BG rendering path, especially tilemap fetch / layer decode / mode handling for the specific slot that shows the visible corruption.

## Most important findings

### 1. The DMA descriptor setup path looks sane

The routine around `0x8083E5..0x80841F` is a normal WRAM-driven DMA setup loop. It pulls descriptor data from a table and writes:

- `$4302`
- `$2116`
- `$4305`
- `$4300`
- `$2115`
- `$4304`
- `$420B`

This did not look like spontaneous bad register programming.

### 2. The WRAM descriptor table builder also looks sane

The routine around `0x808686..0x8086B1` packages DP values into entries under `$013F+X`, then advances `X` by 8 bytes. It looks like a clean descriptor builder, not an obvious corruption source.

### 3. The upstream pointer-building code also looked internally consistent

The code around `0x90A215..0x90A25F` computes ROM source pointers using:

- a table read via `[$A7],Y`
- long reads from bank `EE`
- final DMA sources in bank `DE`

These values looked weird at first, but the traced bytes matched the ROM tables correctly. That reduced confidence in the earlier theory that SoE was pulling obviously wrong source addresses.

### 4. Slot 2 is probably not the best current repro for the visible corruption

PPU snapshots showed that the specific DMA rows traced earlier in slot 2 were writing VRAM around `0x56A0/0x57A0/...`, while the visible BG2 map base in that slot was around `0x0400`.

That means the traced DMA chain was probably real but not necessarily the actual data path behind the visible corruption we care about.

### 5. The three prepared slots are in meaningfully different states

Headless snapshot summary:

- Slot 1:
  - `nonzero_pixels=13321`
  - PPU mode `1`
  - `bg1 map=0x0000 tile=0x2000 hoff=0x007A voff=0x020F`
  - `bg2 map=0x0400 tile=0x2000 hoff=0x007A voff=0x020F`
  - frame-end PC near `0x909E22`

- Slot 2:
  - `nonzero_pixels=47929`
  - PPU mode `1`
  - `bg1 map=0x0000 tile=0x2000 hoff=0x0200 voff=0xFFFF`
  - `bg2 map=0x0400 tile=0x2000 hoff=0x0200 voff=0xFFFF`
  - frame-end PC near `0x8FA201`

- Slot 3:
  - `nonzero_pixels=55045`
  - PPU mode `4`
  - different BG layout entirely
  - frame-end PC near `0x808592`

Working assumption at the end of the session:

- use **slot 1** as the main repro unless the user explicitly says the visible corruption matches slot 2 or slot 3 instead

## Visual clues collected

Existing dumps in `logs/` suggest slot 1 is around a character naming scene and slot 3 is not the same repro. Example files:

- `/home/nichlas/EutherDrive/logs/soe_slot1_60.png`
- `/home/nichlas/EutherDrive/logs/soe_slot3_60.png`

There is also an older highly corrupted dump:

- `/home/nichlas/EutherDrive/logs/soe_slot1_after60.png`

This may be useful for comparison, but it is not guaranteed to be from the exact current code state.

## Existing instrumentation already available

### DMA tracing

In `SNESSystem.cs`:

- `EUTHERDRIVE_TRACE_SNES_DMA=1`

Logs:

- `[DMA-REG]`
- `[DMA-CTL]`
- `[DMA-STATE]`

### WRAM tracing

In `SNESSystem.cs`:

- `EUTHERDRIVE_TRACE_SNES_WRAM=1`
- `EUTHERDRIVE_TRACE_SNES_WRAM_ADDRS=...`

Logs:

- `[WRAM-WR]`
- `[WRAM-RD]`

### CPU PC range tracing

In `SNESSystem.cs`:

- `EUTHERDRIVE_TRACE_SNES_CPU_PC_RANGE=START-END`
- `EUTHERDRIVE_TRACE_SNES_CPU_PC_RANGE_LIMIT=N`

Logs:

- `[CPU-PC-RANGE]`

### Read-by-PC window tracing

In `SNESSystem.cs`:

- `EUTHERDRIVE_TRACE_SNES_READ_PC_WINDOW=1`
- `EUTHERDRIVE_TRACE_SNES_READ_PC_START=...`
- `EUTHERDRIVE_TRACE_SNES_READ_PC_END=...`
- `EUTHERDRIVE_TRACE_SNES_READ_PC_LIMIT=...`

Logs:

- `[SNES-RD-PC]`

### PPU snapshot and layer isolation

In `PPU.cs`:

- `GetDebugSnapshot()` already exists and is reachable through `SnesAdapter.GetPpuDebugSnapshot()`
- `EUTHERDRIVE_TRACE_SNES_PPU_SNAPSHOT=1`
- `EUTHERDRIVE_HEADLESS_TRACE_FRAMES=1`

Layer kill switches:

- `EUTHERDRIVE_SNES_DISABLE_BG1=1`
- `EUTHERDRIVE_SNES_DISABLE_BG2=1`
- `EUTHERDRIVE_SNES_DISABLE_BG3=1`
- `EUTHERDRIVE_SNES_DISABLE_BG4=1`
- `EUTHERDRIVE_SNES_DISABLE_OBJ=1`

## Useful commands that already produced signal

### Slot comparison

```sh
/usr/bin/zsh -lc 'for slot in 1 2 3; do echo "=== SLOT $slot ==="; EUTHERDRIVE_SAVESTATE_SLOT=$slot EUTHERDRIVE_HEADLESS_TRACE_FRAMES=1 EUTHERDRIVE_TRACE_SNES_PPU_SNAPSHOT=1 EUTHERDRIVE_HEADLESS_CORE=snes dotnet /home/nichlas/EutherDrive/EutherDrive.Headless/bin/Release/net8.0/EutherDrive.Headless.dll --load-savestate "/run/media/nichlas/Atlas/SNES/sd2snes/1 S-Z - US/Secret of Evermore (USA).sfc" "/home/nichlas/EutherDrive/savestates/Secret_of_Evermore__USA_.sfc_17c864a7.euthstate" 1 | rg "^(=== SLOT|\\[HEADLESS\\] Frame 0: snes_fb_has_content|\\[HEADLESS\\] Frame 0 ending|\\[HEADLESS\\] Frame 0: ppu-snapshot|ppu mode=|bg1 map=|bg2 map=|bg3 map=)"; done'
```

### DMA/CPU trace around the descriptor setup loop

```sh
/usr/bin/zsh -lc 'EUTHERDRIVE_SAVESTATE_SLOT=2 EUTHERDRIVE_TRACE_SNES_DMA=1 EUTHERDRIVE_TRACE_SNES_CPU_PC_RANGE=8083E0-808420 EUTHERDRIVE_TRACE_SNES_CPU_PC_RANGE_LIMIT=400 EUTHERDRIVE_TRACE_SNES_READ_PC_WINDOW=1 EUTHERDRIVE_TRACE_SNES_READ_PC_START=8083E0 EUTHERDRIVE_TRACE_SNES_READ_PC_END=808420 EUTHERDRIVE_TRACE_SNES_READ_PC_LIMIT=400 EUTHERDRIVE_HEADLESS_CORE=snes dotnet /home/nichlas/EutherDrive/EutherDrive.Headless/bin/Release/net8.0/EutherDrive.Headless.dll --load-savestate "/run/media/nichlas/Atlas/SNES/sd2snes/1 S-Z - US/Secret of Evermore (USA).sfc" "/home/nichlas/EutherDrive/savestates/Secret_of_Evermore__USA_.sfc_17c864a7.euthstate" 2 > /tmp/soe_cpu_dma_trace.log 2>&1 && tail -n 220 /tmp/soe_cpu_dma_trace.log'
```

### WRAM descriptor table trace

```sh
/usr/bin/zsh -lc 'ADDRS=$(python - <<\"PY\"
vals=[]
for x in range(0x1B8,0x1F8,8):
    vals.extend([0x013F+x,0x0140+x,0x0141+x,0x0142+x,0x0143+x,0x0144+x,0x0145+x,0x0146+x])
print(",".join(f"{v:04X}" for v in vals))
PY
)
EUTHERDRIVE_SAVESTATE_SLOT=2 EUTHERDRIVE_TRACE_SNES_WRAM=1 EUTHERDRIVE_TRACE_SNES_WRAM_ADDRS="$ADDRS" EUTHERDRIVE_HEADLESS_CORE=snes dotnet /home/nichlas/EutherDrive/EutherDrive.Headless/bin/Release/net8.0/EutherDrive.Headless.dll --load-savestate "/run/media/nichlas/Atlas/SNES/sd2snes/1 S-Z - US/Secret of Evermore (USA).sfc" "/home/nichlas/EutherDrive/savestates/Secret_of_Evermore__USA_.sfc_17c864a7.euthstate" 2 > /tmp/soe_table_region.log 2>&1 && tail -n 260 /tmp/soe_table_region.log'
```

### Source-builder trace

```sh
/usr/bin/zsh -lc 'EUTHERDRIVE_SAVESTATE_SLOT=2 EUTHERDRIVE_TRACE_SNES_CPU_PC_RANGE=90A210-90A260 EUTHERDRIVE_TRACE_SNES_CPU_PC_RANGE_LIMIT=500 EUTHERDRIVE_TRACE_SNES_WRAM=1 EUTHERDRIVE_TRACE_SNES_WRAM_ADDRS=0012,0013,0026,0027,0028,0029,002E,002F EUTHERDRIVE_HEADLESS_CORE=snes dotnet /home/nichlas/EutherDrive/EutherDrive.Headless/bin/Release/net8.0/EutherDrive.Headless.dll --load-savestate "/run/media/nichlas/Atlas/SNES/sd2snes/1 S-Z - US/Secret of Evermore (USA).sfc" "/home/nichlas/EutherDrive/savestates/Secret_of_Evermore__USA_.sfc_17c864a7.euthstate" 1 > /tmp/soe_src_builder.log 2>&1 && sed -n "1,260p" /tmp/soe_src_builder.log'
```

## What still needs to be done

1. Lock the correct repro slot with confidence.
   - Current best guess: slot 1.

2. Stop chasing the CPU/DMA builder unless new evidence contradicts the current traces.
   - That path looked surprisingly sane.

3. Use the layer kill switches and PPU snapshots on the chosen slot to isolate:
   - BG1 only
   - BG2 only
   - BG3 only
   - OBJ only

4. If the corruption survives isolation on a single BG, focus on:
   - `FetchTileInBuffer(...)`
   - `GetPixelForLayer(...)`
   - tilemap screen selection / 32x32 quadrant handling
   - mode-dependent tile decode
   - priority path for mode 1

5. Only revisit DMA visibility if a new trace directly ties the visible corruption to a live VRAM update for the actual corrupted map region.

## Practical next start

If resuming this tomorrow, the cleanest next move is:

1. Use slot 1.
2. Dump one frame with each BG/OBJ disabled combination.
3. Compare the corrupted output visually.
4. Narrow the bug to one layer and then instrument `PPU.cs` on that layer only.

That is a better next step than more broad DMA tracing.
