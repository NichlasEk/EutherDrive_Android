# MCS Notice

This directory vendors MCS (Mame for C Sharp) from the local source tree at
`/home/nichlas/mcs`, commit `870a9e7caa211619416cdd854640bf7b609a6b5f`.

MCS is licensed under the BSD 3-Clause license. The original license text is
preserved in `LICENSE.md` and must remain with source and binary
redistributions that include this code.

The vendored `mcs/3rdparty` subtree contains additional third-party license
texts, including:

- `mcs/3rdparty/SharpCompress/LICENSE.txt`
- `mcs/3rdparty/SVG/license.txt`

EutherDrive adds an initial Midway MCR3 Rampage driver under
`mcs/src/src/mame/midway/mcr3.cs`, translated/adapted from the local MAME tree
at `/home/nichlas/mame/src/mame/midway/mcr3.cpp` and `mcr3.h`.

That MAME source is BSD 3-Clause licensed and credited to Aaron Giles. The
ported file keeps the BSD-3-Clause license header and copyright holder notice.

The current MCS bridge can identify and route arcade archives, and Rampage now
exists in the MCS driver catalog. The in-process EutherDrive video/audio/input
bridge is still the next integration step before Rampage is playable in the
normal EutherDrive UI.
