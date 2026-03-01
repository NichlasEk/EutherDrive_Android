# Sonic 3 Savestate Freeze Debug (UI)

This note documents how to capture useful logs when Sonic 3 video freezes while audio continues.

## 1) Build

```bash
dotnet build EutherDrive.UI -v minimal
```

## 2) Run UI with MD trace + video hang detection

```bash
EUTHERDRIVE_LOG_VERBOSE=1 \
EUTHERDRIVE_UI_MD_TRACE_FILE=/home/nichlas/EutherDrive/logs/ui_md_trace_sonic3.csv \
EUTHERDRIVE_UI_MD_TRACE_EVERY=1 \
EUTHERDRIVE_UI_VIDEO_HANG_FRAMES=30 \
EUTHERDRIVE_UI_TRIPWIRE_VIDEO_FRAMES=20 \
EUTHERDRIVE_M68K_TRACE_IRQ=1 \
dotnet run --project EutherDrive.UI -c Debug
```

### Fast tripwire mode (low overhead, savestate-friendly)

```bash
EUTHERDRIVE_MD_USE_M68KEMU=1 \
EUTHERDRIVE_UI_VIDEO_HANG_FRAMES=20 \
EUTHERDRIVE_UI_TRIPWIRE_VIDEO_FRAMES=20 \
EUTHERDRIVE_M68K_IRQ_AUTOUNMASK=1 \
dotnet run --project EutherDrive.UI -c Debug
```

## 3) Reproduce

1. Load `sonic3.md`.
2. Load the problematic savestate.
3. Let it run until video freezes (audio may continue).

## 4) What to collect

- `logs/ui_md_trace_sonic3.csv`
  - now includes IRQ columns: `sr`, `irq_mask`, `pending_irq`, `bus_irq`, `irq_take`, `hreq/vreq/extreq`, request/ack counters
- Any line containing `[UI-VIDEO-HANG]` from terminal output
- Any line containing `[UI-TRIPWIRE]` from terminal output
- `logs/ui_tripwire_video_*.txt`
- `logs/ui_tripwire_video_*.ppm`
- Any generated `logs/ui_video_hang_*.ppm`

## 5) Quick checks

### Show last rows from trace

```bash
tail -n 150 /home/nichlas/EutherDrive/logs/ui_md_trace_sonic3.csv
```

### List hang dumps

```bash
ls -lt /home/nichlas/EutherDrive/logs/ui_video_hang_*.ppm 2>/dev/null | head
```

## 6) Optional: also run legacy PC/cycle stall detector

Only needed if you also want old `[UI-HANG]` logging:

```bash
EUTHERDRIVE_LOG_VERBOSE=1 \
EUTHERDRIVE_UI_HANG_FRAMES=30 \
EUTHERDRIVE_UI_MD_TRACE_FILE=/home/nichlas/EutherDrive/logs/ui_md_trace_sonic3.csv \
EUTHERDRIVE_UI_MD_TRACE_EVERY=1 \
EUTHERDRIVE_UI_VIDEO_HANG_FRAMES=30 \
dotnet run --project EutherDrive.UI -c Debug
```

## 7) Troubleshooting

- If `ui_md_trace_sonic3.csv` only has the header row:
  - ensure you are running an MD core (`sonic3.md`), not another system.
  - ensure the env vars are on the same command invocation as `dotnet run`.
- If no `[UI-VIDEO-HANG]` appears:
  - freeze duration may be shorter than threshold; try lower value:
    - `EUTHERDRIVE_UI_VIDEO_HANG_FRAMES=10`
