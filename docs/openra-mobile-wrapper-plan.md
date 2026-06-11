# OpenRA Mobile Wrapper Plan

## Goal

Use upstream OpenRA as the first runnable engine path, with original Westwood/EA
assets kept local and ignored by git. Once that is runnable, move Android toward
a mobile-first wrapper and later replace desktop assumptions with an EutherDrive
frontend where needed.

## Local Layout

- `external/OpenRA/`: upstream OpenRA checkout, gitignored.
- `external/openra-content/`: local cache/import area for OpenRA game content,
  gitignored.
- `data/openra-content/`: optional local app-facing content directory,
  gitignored.
- `scripts/fetch-openra.sh`: clone/update upstream OpenRA.
- `scripts/build-openra.sh`: build the local OpenRA checkout.
- `scripts/run-openra.sh`: run a local OpenRA mod for desktop smoke testing.

Default upstream revision is `release-20250330`. Current `bleed` was tested on
2026-06-11 and failed to compile locally because of an ambiguous
`CryptoUtil.SHA1Hash([])` call in `OpenRA.Game/Map/Map.cs`. The stable release
builds with `dotnet build OpenRA.sln -c Release -p:TargetPlatform=linux-x64`.

The stable release targets `net6.0`, but this workstation only has .NET 8/10
runtimes. `scripts/build-openra.sh` therefore applies a local ignored patch to
`external/OpenRA/Directory.Build.props` and builds with `net8.0` by default. Set
`OPENRA_TARGET_FRAMEWORK=net6.0` if a .NET 6 runtime is installed and upstream
behavior is preferred.

In this repository `scripts/build-openra.sh` defaults to `--no-restore`, because
network restore can be sandbox-sensitive. Use
`OPENRA_RESTORE=1 scripts/build-openra.sh` after a fresh checkout if NuGet
packages are missing.

## Licensing Boundary

OpenRA source is GPL-3.0. The classic game content used by the bundled mods is
separate from the OpenRA source license. Do not commit downloaded or extracted
Red Alert, Tiberian Dawn, Dune 2000, or remastered assets to this repository.

## Implementation Direction

1. Get upstream OpenRA building and launching locally from `external/OpenRA`.
2. Add a narrow EutherDrive OpenRA adapter that can discover the local checkout
   and content roots without taking ownership of the assets.
3. Build a mobile wrapper around the runnable engine:
   - Android lifecycle and fullscreen handling.
   - Touch RTS input: pan, pinch zoom, drag select, long-press/context command.
   - UI scaling and safe-area handling for portrait and landscape.
   - Audio focus and suspend/resume behavior.
4. Replace brittle desktop assumptions incrementally with EutherDrive-native
   frontend pieces while keeping OpenRA rules/assets compatible.

## 2026-06-11 Bring-up Notes

- `scripts/fetch-openra.sh` cloned upstream OpenRA into `external/OpenRA`.
- Local checkout is on `release-20250330`.
- `scripts/build-openra.sh` builds the ignored checkout with a local net8 target
  patch.
- `EutherDrive.OpenRA` was added as a thin adapter project for path discovery
  and future process-launch integration.
- `EutherDrive.OpenRA` also contains a platform-neutral mobile RTS input mapper
  for tap, long-press command, pan, pinch zoom, and drag-select gestures.
- `ENGINE_DIR=.. dotnet bin/OpenRA.Utility.dll ra --check-yaml` passed for the
  bundled Red Alert mod data.
- Direct game launch reaches SDL/OpenGL initialization in this headless session,
  then fails at renderer initialization because no usable display is available.
  This is expected for the current shell environment and should be retested from
  a real desktop session before Android wrapper work.
