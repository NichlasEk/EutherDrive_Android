# Knights of Valour PGM Port Plan

Date: 2026-05-09

Target ROM: `/home/nichlas/roms/MAME/kov/kov.zip`

Source reference: `/home/nichlas/mame/src/mame/igs/`

## Goal

Bring up `kov.zip` (Knights of Valour / Sanguo Zhan Ji / Sangoku Senki, ver. 117 Hong Kong) in EutherDrive through the MCS/MAME-backed arcade path.

This is a board port, not just a drivlist entry. `kov` uses IGS PGM hardware and depends on devices that are not currently present in this MCS snapshot:

- IGS023 video device
- ICS2115 sample/synth sound device
- V3021 RTC device
- PGM memory maps, interrupts, latches, SRAM/NVRAM, input ports
- PGM BIOS ROM loading and merged parent/child archive lookup
- KOV 68000 program decryption
- IGS027A Type 1 protection simulation used by `pgm_arm_type1_sim`

## Current Local Facts

`kov.zip` contains:

| File | Size | Meaning |
| --- | ---: | --- |
| `p0600.117` | 4 MiB | 68000 game program |
| `t0600.rom` | 8 MiB | text/background tiles |
| `a0600.rom` | 8 MiB | sprite color data |
| `a0601.rom` | 8 MiB | sprite color data |
| `a0602.rom` | 8 MiB | sprite color data |
| `a0603.rom` | 4 MiB | sprite color data |
| `b0600.rom` | 8 MiB | sprite masks/indexes |
| `b0601.rom` | 4 MiB | sprite masks/indexes |
| `m0600.rom` | 4 MiB | ICS2115 sample data |

The parent BIOS archive is stored at `/home/nichlas/roms/bios/pgm.zip`. The PGM BIOS needs at least:

| File | Region |
| --- | --- |
| `pgm_p01s.u20` | 68000 BIOS |
| `pgm_t01s.rom` | video BIOS tiles |
| `pgm_m01s.rom` | audio BIOS samples |

MAME source expects canonical names like `pgm_p0601_v117.u1`, `pgm_t0600.u11`, `pgm_a0600.u2`, and so on. The local archive uses shorter names, so the MCS loader must either support CRC/SHA1 matching or a KOV filename alias table.

## Source Files To Port From MAME

Start with the narrow KOV path, not every IGS game:

| Purpose | Source file |
| --- | --- |
| Base PGM state, memory maps, inputs, ROM definitions | `/home/nichlas/mame/src/mame/igs/pgm.cpp`, `pgm.h` |
| Program decryption | `/home/nichlas/mame/src/mame/igs/pgmcrypt.cpp`, `pgmcrypt.h` |
| KOV Type 1 protection simulation | `/home/nichlas/mame/src/mame/igs/pgmprot_igs027a_type1.cpp`, `pgmprot_igs027a_type1.h` |
| IGS023 renderer | `/home/nichlas/mame/src/mame/igs/igs023_video.cpp`, `igs023_video.h` |
| Later protection/device expansion | `igs027a.cpp`, `igs027a.h` |

Do not start with `pgm2` or `pgm3`; KOV is first-generation PGM.

## EutherDrive Touch Points

Expected main files/modules:

| Area | File or directory |
| --- | --- |
| MCS drivlist registration | `Third_party/MCS/mcs/src/build/generated/mame/mame/drivlist.cs` |
| MCS game driver implementation | `Third_party/MCS/mcs/src/src/mame/igs/pgm.cs` and related new files |
| MCS device ports | `Third_party/MCS/mcs/src/src/devices/...` or `Third_party/MCS/mcs/src/src/mame/igs/...` |
| Arcade host integration | `EutherDrive.Core/Arcade/McsArcadeAdapter.cs` |
| ROM set recognition | `McsDriverCatalog` via generated drivlist |
| User-facing status | `README.md` arcade status table, after meaningful boot progress |

Keep the PGM implementation inside MCS first. Avoid writing a separate EutherDrive-only PGM emulator unless the MCS device model blocks progress.

## Port Order

### 1. Create The PGM Skeleton

Add a minimal `mame.igs.pgm` C# driver file that compiles and registers:

- `pgm_state`
- `driver_pgm`
- `driver_kov`
- `ROM_START(pgm)`
- `ROM_START(kov)`
- `GAME(... kov ...)`

Initial implementation can throw or draw blank, but the build must pass and `McsDriverCatalog.Contains("kov")` must return true only after the skeleton is coherent enough for loader testing.

Status: done 2026-05-09. `Third_party/MCS/mcs/src/src/mame/igs/pgm.cs` now registers `driver_pgm` and `driver_kov`, declares the local `kov.zip` filenames, and draws a diagnostic raster frame.

Done when:

- [x] `dotnet build EutherDrive.Core/EutherDrive.Core.csproj --no-restore /clp:ErrorsOnly` gets past the new PGM files.
- [x] The generated drivlist includes `pgm.driver_pgm` and `pgm.driver_kov`.
- [x] `McsDriverCatalog.Contains("kov")` returns true.
- [x] `McsArcadeAdapter.IsDriverAvailableForArchive("/home/nichlas/roms/MAME/kov/kov.zip")` returns true.

### 2. Fix ROM Loading And BIOS Handling

Implement the ROM definitions exactly enough for `pgm` and `kov`.

Status: partially done 2026-05-09. EutherDrive now has a selectable PGM BIOS path in the UI and auto-detects `/home/nichlas/roms/bios/pgm.zip`; MCS `-rompath` is built as the game ROM directory plus the BIOS directory. The current MCS `kov` ROM definition uses the local archive names directly, so the listed aliases are not needed for this local set unless we later want to accept canonical MAME filenames in the same driver entry.

Required work:

- Add PGM BIOS archive lookup. MAME expects child `kov.zip` plus parent `pgm.zip`; EutherDrive currently points MCS at the ROM directory.
- Add KOV filename aliases for the local set:
  - `p0600.117` -> `pgm_p0601_v117.u1`
  - `t0600.rom` -> `pgm_t0600.u11`
  - `a0600.rom` -> `pgm_a0600.u2`
  - `a0601.rom` -> `pgm_a0601.u4`
  - `a0602.rom` -> `pgm_a0602.u6`
  - `a0603.rom` -> `pgm_a0603.u9`
  - `b0600.rom` -> `pgm_b0600.u5`
  - `b0601.rom` -> `pgm_b0601.u7`
  - `m0600.rom` -> `pgm_m0600.u3`
- Keep CRC/SHA1 checks where MCS already supports them; do not silently accept wrong contents if the hash layer is reachable.

Done when:

- [x] `kov.zip` is recognized as an available MCS driver.
- [x] PGM BIOS path can be selected/cleared from the UI.
- [x] `/home/nichlas/roms/bios/pgm.zip` is auto-detected.
- [ ] Loading `kov.zip` finds all KOV child ROMs at runtime.
- [ ] Missing `pgm.zip` reports the exact missing BIOS files.
- [ ] With `pgm.zip` present, all ROM regions populate without manual extraction.

### 3. Bring Up Base 68000 Execution

Port the base PGM memory map first:

- `0x000000-0x0fffff`: BIOS ROM
- `0x100000-0x3fffff`: game ROM bank
- main SRAM
- PGM video RAM/register ranges as stubbed devices
- sound latches
- input ports
- Z80 shared RAM window
- vblank and scanline IRQ behavior

Use existing MCS `m68000` and `z80` devices where possible. Stub devices may return stable default values at first, but they must log reads/writes behind an environment flag so the next blocker is visible.

Done when:

- 68000 reset vector is read from PGM BIOS.
- The PC advances into BIOS/game code.
- No crash occurs from unmapped basic RAM/input/IRQ paths.
- A headless trace can show first few thousand 68000 PCs.

### 4. Port KOV Program Decryption

Port only the KOV decryption path from `pgm_kov_decrypt`.

Required work:

- Apply decryption to the 68000 game program region after load and before reset.
- Add a small verification harness that checks known decrypted words near the reset/entry path against native MAME output or a saved reference dump.
- Keep the decryption isolated, e.g. `PgmCrypt.KovDecrypt(...)`, so Plus/bootleg variants can be added later without mixing logic.

Done when:

- The post-decrypt 68000 stream disassembles as plausible code.
- Execution reaches KOV init code rather than illegal instruction loops caused by encrypted opcodes.

### 5. Add Minimal IGS027A Type 1 Simulation For KOV

Port the specific `init_kov` and Type 1 simulation behavior used by `pgm_arm_type1_sim`.

Do not port all Type 1 games at once. Start with:

- `pgm_arm_type1_state`
- `pgm_arm_type1_sim`
- KOV protection commands/data tables
- Region behavior needed by `sango` input configuration

Done when:

- KOV passes the earliest protection checks.
- The game reaches a stable attract/title path instead of resetting or hanging on protection reads.
- Protection reads/writes are traceable with `EUTHERDRIVE_PGM_PROT_TRACE=1`.

### 6. Port IGS023 Video Enough For Title Screen

Port IGS023 in layers:

1. Device shell and VRAM/register maps.
2. Palette writes and xRGB555 output.
3. Text/background tile decode.
4. Sprite RAM buffering on vblank.
5. Sprite mask/color decode.
6. Priority and scroll behavior.

Use MCS `bitmap_ind16`/palette flow first, since `McsArcadeAdapter` already has palette framebuffer paths.

Done when:

- BIOS/test screen or KOV title screen renders nonblank.
- Width/height match PGM visible output: 448x224 from the raw screen setup.
- Attract mode shows stable tile/sprite content, even if priorities still need polish.

### 7. Wire PGM Inputs

Map EutherDrive arcade input into PGM ports:

- P1/P2 start, directions, buttons 1-4
- coin/service
- default DSW values with music and voice enabled

KOV is a 4-player game, but initial EutherDrive control mapping can start with P1/P2 as long as P3/P4 read inactive.

Done when:

- Coin/start enters gameplay.
- P1 movement and buttons work.
- Test mode can be entered through service input when explicitly mapped.

### 8. Port Sound After Gameplay Is Visible

Sound should come after video and input. The sound path needs:

- Z80 sound CPU memory and IO maps
- sound latches
- ICS2115 device port
- sample ROM region and banking
- mono route into MCS audio mixer

If ICS2115 is too large for first pass, keep Z80/latches wired and leave a clear "sound chip missing" status. Avoid fake audio that hides missing device behavior.

Done when:

- Z80 starts and responds to latch commands.
- ICS2115 produces music/samples from `pgm_m01s.rom` and `m0600.rom`.
- Audio does not starve or flood `McsArcadeAdapter`'s queue.

### 9. Add Save State Support

Only after boot/playability:

- PGM main RAM/SRAM
- video RAM/registers
- palette RAM
- sound RAM/latches
- protection state
- device timers

Done when:

- Save/load during attract and during gameplay resumes without immediate crash.
- Repeated save/load does not desync input or audio state.

### 10. Performance And Android Pass

PGM is much heavier than the older MCS drivers currently active. After correctness:

- profile IGS023 sprite/tile drawing
- avoid per-pixel allocations
- keep decoded graphics caches stable
- validate on desktop first, then Android

Done when:

- Desktop can run attract/gameplay at a usable speed.
- Android build compiles.
- Android launches `kov.zip` without filesystem or audio backend regressions.

## Verification Commands

Use these as checkpoints. Some may need adjustment as tools evolve.

```bash
dotnet build EutherDrive.Core/EutherDrive.Core.csproj --no-restore /clp:ErrorsOnly
dotnet build EutherDrive.UI/EutherDrive.UI.csproj --no-restore /clp:ErrorsOnly
dotnet build EutherDrive.Android/EutherDrive.Android.csproj --no-restore /clp:ErrorsOnly
```

If headless support is available for MCS arcade in the local tree, add a KOV smoke command that runs 300-600 frames and dumps:

- first visible PNG
- last visible PNG
- 68000 PC trace
- protection trace, only when enabled
- audio queue stats

## Risk Register

| Risk | Impact | Mitigation |
| --- | --- | --- |
| PGM BIOS missing | KOV cannot load | Require `pgm.zip` next to `kov.zip` and report exact file names |
| ICS2115 absent in MCS | no sound and possible Z80 stalls | stub carefully first, then port sound after video |
| IGS027A simulation incomplete | boot loops or hangs | keep trace logging and port only KOV commands first |
| IGS023 renderer large | slow or wrong video | implement in visible layers: palette, tiles, sprites, priority |
| ROM filename mismatch | false missing-ROM errors | add alias/hash-based matching for the known local set |
| Existing dirty tree | accidental regression | keep PGM changes isolated and do not modify unrelated arcade ports |

## Recommended First Commit Boundary

First useful commit should include only:

- PGM/KOV skeleton driver
- ROM definitions and alias handling
- drivlist registration
- documentation update

It should not include half-ported video or sound. That keeps the first diff reviewable and gives a clean baseline for loader/build issues.

## Working Status

- [x] Identify target board and ROM contents.
- [x] Identify required MAME source files.
- [x] Identify missing MCS devices.
- [x] Add PGM skeleton driver.
- [ ] Add ROM/BIOS loading. Partial: UI selection, auto-detect, and `kov` catalog recognition are done.
- [ ] Bring up 68000 execution.
- [ ] Add KOV decryption.
- [ ] Add Type 1 protection simulation.
- [ ] Add IGS023 video.
- [ ] Wire inputs.
- [ ] Add ICS2115 sound.
- [ ] Add savestates.
- [ ] Validate desktop and Android.
