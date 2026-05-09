# High Seas Havoc Decryption Research

This folder contains ROM-free research material for the Data East / Sega
System C-2 High Seas Havoc arcade decryption work.

Included files:

- `hshavoc_decrypt_lab.py` - read-only analysis and hypothesis tool. It reads
  local ROM/reference inputs from `/home/nichlas/roms/MAME/DataEast/hshavoc`
  when available, builds candidate decode views in memory, and writes only
  temporary candidate binaries under `/tmp`.
- `hshavoc_decryption_plan.md` - current theory log, results, rejected
  hypotheses, and next steps.

Do not add ROMs, decrypted ROM images, generated candidate binaries, MAME
sample ZIPs, or other copyrighted binary dumps to this folder. Keep generated
outputs in `/tmp` unless they are source code, notes, or reproducible metadata.

Useful commands:

```sh
python research/hshavoc/hshavoc_decrypt_lab.py --runs 0
python research/hshavoc/hshavoc_decrypt_lab.py --runs 0 --cross-runs 8 --only-cross-ref
python research/hshavoc/hshavoc_decrypt_lab.py --only-token-graph
python research/hshavoc/hshavoc_decrypt_lab.py --vdp-log /tmp/hsh_slot3_scan.log --only-vdp-log
python research/hshavoc/hshavoc_decrypt_lab.py --vdp-log /tmp/hsh_arcade_240_dmalog.log --only-vdp-log
python research/hshavoc/hshavoc_decrypt_lab.py --vram-snapshot /tmp/hsh_slot3_regmeta/mdsnap_20260509_141617_564_vram.bin --vram-meta /tmp/hsh_slot3_regmeta/mdsnap_20260509_141617_564_meta.txt --only-vram-snapshot
python research/hshavoc/hshavoc_decrypt_lab.py --runs 0 --strict-peel-search
python research/hshavoc/hshavoc_decrypt_lab.py --runs 1 --deep-peel-search
```

Current strongest model:

- The startup area is partially understood, but the remaining weak islands are
  probably not plain linear 68000 code.
- `$0ea0-$1064` behaves like a dense halfword token/table region.
- `$0ed4-$102e` contains six clean `0x3a`-byte blocks with repeated columns and
  trailer markers such as `4fbd + param`.
- Several startup targets land on fixed block columns, and P00/B04/B05/B06
  trailer parameters converge on `$1030`, suggesting a table interpreter or
  state machine rather than a simple code decrypt pass.
- Current EutherDrive render traces can be fed back into the lab with
  `--vdp-log`. It accepts both HSHavoc command-block traces and generic
  `EUTHERDRIVE_TRACE_DMA_SRC=1` MD VDP DMA-source logs. The current title/attract
  path is RAM-sourced, so the most useful report is now the `$ff0000` and
  `$fff000/$fff200/$fff400` producer/consumer sequence rather than only direct
  ROM-sourced VDP operations.
- `EUTHERDRIVE_HSHAVOC_VDP_SOURCE_PROBE=1` can replay the current local
  source-block hypothesis in EutherDrive. It is intentionally disabled by
  default; the first slot-3 test proved that it changes ROM DMA source words
  but does not improve the visible corrupt framebuffer.
- `--vram-snapshot` scores Plane A/B name tables against actually loaded
  pattern tiles. Slot-3 analysis shows the noise is not only layer ordering:
  many filler cells reference tile 0, and tile 0 is nonblank in the captured
  VRAM. `EUTHERDRIVE_HSHAVOC_CLEAR_TILE0_PROBE=1` and
  `EUTHERDRIVE_HSHAVOC_CLEAR_TILE_PROBE_LIST=...` are narrow runtime probes for
  this blank-tile hypothesis, not a final fix.
