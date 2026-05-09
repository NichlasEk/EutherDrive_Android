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
python research/hshavoc/hshavoc_decrypt_lab.py --vram-snapshot /tmp/hsh_arcade_240_snapshot/mdsnap_20260509_150256_118_vram.bin --vram-meta /tmp/hsh_arcade_240_snapshot/mdsnap_20260509_150256_118_meta.txt --compare-vram-snapshot /tmp/hsh_home_md_1500/mdsnap_20260509_150742_853_vram.bin --compare-vram-meta /tmp/hsh_home_md_1500/mdsnap_20260509_150742_853_meta.txt --only-vram-snapshot
python research/hshavoc/hshavoc_decrypt_lab.py --ram-snapshot /tmp/hsh_cold_bram_frames_input/mdsnap_20260509_161609_732_ram_ff0000.bin --ram-meta /tmp/hsh_cold_bram_frames_input/mdsnap_20260509_161609_732_meta.txt --compare-ram-snapshot /tmp/hsh_slot_compare_bram_3/mdsnap_20260509_161415_702_ram_ff0000.bin --compare-ram-meta /tmp/hsh_slot_compare_bram_3/mdsnap_20260509_161415_702_meta.txt --only-ram-snapshot
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
- `--compare-vram-snapshot` compares two VRAM captures and reports same-cell
  tile deltas plus tile-pattern offset scores. Use it for arcade-vs-reference
  checks before changing renderer or DMA timing.
- `--ram-snapshot` decodes the `$ffe800-$ffeac0` VDP command queue from an
  `*_ram_ff0000.bin` debug snapshot. `--compare-ram-snapshot` is the current
  best check for the post-start corruption: a bad cold frame 300 has only
  ROM-sourced queue blocks, while the good slot-3 attract/gameplay state has
  the missing RAM-sourced Plane A transfers:
  `FFD800 -> C100`, `FFD900 -> C024`, `FFD940 -> C026`, and `FFD980 -> E300`.
  This makes the likely fault the tilemap producer/state path, not layer
  ordering or a single global tile-bank offset.
- Runtime RAM seeding is available through
  `EUTHERDRIVE_HSHAVOC_RAM_SEED_WORDS=addr:value,...`. Use
  `EUTHERDRIVE_HSHAVOC_RAM_SEED_EVERY_FRAME=1` only as a diagnostic override.
  The current best proof is `FFDBEC`: forcing `0x000b` moves the cold frame
  5147 VDP queue much closer to user slot 3, but it is not a final fix.
