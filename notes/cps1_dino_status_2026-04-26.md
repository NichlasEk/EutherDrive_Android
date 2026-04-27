# CPS1 Dino status 2026-04-26

## Goal

Make `dino.zip` / Cadillacs and Dinosaurs run in EutherDrive as pure C#:

- no native core bridge
- no MCS runtime for Dino
- no fake/stub sound pretending to be QSound

## Current state

Dino is routed before the generic MCS arcade archive path:

- `EutherDrive.UI/MainWindow.axaml.cs`
- `EutherDrive.Core/Arcade/Cps1/Cps1DinoAdapter.cs`
- `EutherDrive.Core/Arcade/Cps1/Cps1QSound.cs`
- `EutherDrive.Core/Cpu/Z80Emu/Z80Bus.cs`
- `EutherDrive.Core/Cpu/Z80Emu/Instructions.cs`

The pure C# CPS1 path currently includes:

- 68000 main CPU with CPS1 QSound memory map
- CPS1 main ROM, work RAM, gfx RAM, palette RAM, CPS-A/CPS-B registers
- CPS1 tile/sprite rendering for Dino enough to reach gameplay
- Kabuki-decrypted Z80 audio CPU ROM with separate opcode/data views
- Z80 opcode fetch support through `IOpcodeBusInterface`
- QSound shared RAM banks at `0xf18000` and `0xf1e000`
- QSound sample ROM and DL-1425 DSP ROM loading
- Pure C# QSound HLE model
- basic 93C46-style serial EEPROM lines at `0xf1c006`

## Latest findings

The last UI screenshot showed two issues: no sound and odd/pale colors.

Color issue:

- This was a real CPS1 graphics decode bug.
- Plane offsets `{24, 16, 8, 0}` were already correct.
- The bit significance was reversed compared with MAME `gfx_element::decode`.
- Current fix is in `Cps1Graphics.Decode`: `pen |= ReadBit(gfx, bit) << (3 - plane);`
- Harness palette/pixel output now looks internally sane; needs UI visual confirmation.

Sound issue:

- `qsound.zip` is now present beside the ROM, so `dl-1425.bin` loads.
- QSound HLE initializes and reaches normal ready state.
- A short harness run can stay silent because it has not actually reached/triggered gameplay audio yet.
- A longer scripted run with repeated coin/start/right/button input produced real non-zero audio:

```text
frames=3600
audio nonZero=1468 peak=6463
qsound writes=119773 status=119773
last qsound write=0x52/0x1000
```

- In that run, QSound PCM voices had non-zero banks/rates/volumes and the output samples were non-zero.
- That means the QSound mixer path is not fundamentally dead.
- The user's UI report of no sound may be because the test point was still before gameplay sound, or because UI input/coin/start timing differs from the harness.

## Important implementation notes

Z80 opcode/data split:

- `Z80Bus.cs` now has `IOpcodeBusInterface`.
- `Instructions.cs` fetches opcodes through `FetchOpcode()`.
- Immediates/displacements still use normal data `FetchOperand()`.
- This is required for Kabuki-protected CPS1 audio ROMs.

QSound:

- `Cps1QSound.cs` is a hand C# port based on MAME's BSD-3-Clause QSound HLE by superctr and Valley Bell.
- It currently has internal debug counters/properties from bring-up (`DebugWriteCount`, `DebugState`, etc.).
- Those are useful while finishing Dino, but should probably be removed or hidden before a clean final commit.

Audio IRQ:

- The QSound Z80 periodic interrupt is modeled at `8 MHz / 32000`.
- Current code holds the INT line low for a small cycle window instead of one instant instruction.
- This matched the intended MAME-style line-hold behavior better, but it was not the reason audio started; gameplay command flow was.

EEPROM:

- A small serial EEPROM model was added because Dino uses the QSound CPS1 EEPROM port.
- It is enough for basic read/write/erase protocol experiments, but not a complete audited 93C46 implementation.
- If UI behavior around defaults/settings is odd, revisit this.

## Verification done

Harness path:

```sh
dotnet run --project /tmp/cps1dino_harness/cps1dino_harness.csproj --no-restore
```

Last useful harness result:

- Dino loads `/home/nichlas/Hamtningar/dino.zip`.
- `qsound.zip`/`dl-1425.bin` is found.
- Framebuffer reaches active gameplay-like state.
- Audio becomes non-zero after longer scripted input.
- QSound writes real voice registers.

Earlier build that passed before the latest note:

```sh
dotnet build EutherDrive.Core/EutherDrive.Core.csproj -c Debug --no-restore
```

Need rerun before commit:

```sh
dotnet build EutherDrive.UI/EutherDrive.UI.csproj -c Debug --no-restore
```

Expect many pre-existing warnings.

## Next steps

1. Re-test Dino in UI after the graphics plane-bit fix.
2. Confirm colors against MAME/local expectation.
3. From UI, insert coin/start and get into gameplay; sound may not appear at the title/early boot point.
4. If UI still has no sound in gameplay, trace UI input mapping first, then trace main CPU writes to QSound shared RAM 0.
5. Remove or gate QSound debug properties before final commit.
6. Build UI.
7. Commit only the CPS1/Z80/UI/notes changes, not unrelated dirty 32X/Android/README/script/cfg/nvram files.

## Dirty worktree caution

Relevant Dino files:

- `EutherDrive.Core/Arcade/Cps1/Cps1DinoAdapter.cs`
- `EutherDrive.Core/Arcade/Cps1/Cps1QSound.cs`
- `EutherDrive.Core/Cpu/Z80Emu/Z80Bus.cs`
- `EutherDrive.Core/Cpu/Z80Emu/Instructions.cs`
- `EutherDrive.UI/MainWindow.axaml.cs`
- `notes/cps1_dino_status_2026-04-26.md`

There are unrelated dirty files in the repository from earlier work. Do not revert or stage them unless explicitly requested.

## License/attribution

The CPS1 and QSound behavior is hand-ported from local MAME source references.

- CPS1 hardware/register model: MAME Capcom CPS1 driver, BSD-3-Clause, Paul Leaman.
- QSound HLE: MAME QSound HLE, BSD-3-Clause, superctr and Valley Bell.

Keep attribution comments in the relevant files and make sure final documentation/licensing remains BSD-3-Clause compatible.
