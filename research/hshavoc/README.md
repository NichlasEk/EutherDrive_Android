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
python research/hshavoc/hshavoc_decrypt_lab.py --runs 0 --strict-peel-search
python research/hshavoc/hshavoc_decrypt_lab.py --runs 1 --deep-peel-search
```

Current strongest model:

- The startup area is partially understood, but the remaining weak islands are
  probably not plain linear 68000 code.
- `$0ea0-$1064` behaves like a dense halfword token/table region.
- `$0ed4-$102e` contains six clean `0x3a`-byte blocks with repeated columns and
  trailer markers such as `4fbd + param`.
- Several startup targets land on fixed block columns, and multiple trailer
  parameters converge on `$1030`, suggesting a table interpreter or state
  machine rather than a simple code decrypt pass.
