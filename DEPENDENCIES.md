# EutherDrive Dependencies

This file tracks runtime tools and native libraries that are not fully covered by NuGet restore.

## Desktop App

Required to build:

- .NET 8 SDK
- A platform toolchain capable of building Avalonia desktop projects

Required at runtime:

- .NET 8 runtime, unless publishing with `SELF_CONTAINED=true`
- SDL2 native library for desktop audio and gamepad input
  - Windows package expects `SDL2.dll` next to `EutherDrive.UI.exe`
  - Linux package expects `libSDL2-2.0.so` in the package or available through the system loader

Optional runtime tools:

- `mpv`
  - Preferred Machine Room media backend.
  - Required for runtime volume, pause, and seek control in the mini media player.
- `ffplay`
  - Fallback Machine Room media backend when `mpv` is unavailable.
  - Playback works, but runtime controls are limited.
- `ffmpeg`
  - Used for embedded cover extraction and Machine Room video frame decoding.
- `ffprobe`
  - Used to read video dimensions, duration, and frame rate for Machine Room video.

The Machine Room media tools are found through `PATH`, using `.exe` names on Windows.

## Windows Release Status

Basic desktop release packaging exists through:

```sh
scripts/release-windows.sh
```

Current Windows expectations:

- `SDL2.dll` must be shipped in the release folder or installed where the loader can find it.
- `mpv.exe`, `ffmpeg.exe`, `ffprobe.exe`, and optionally `ffplay.exe` must either be in `PATH` or placed somewhere already covered by `PATH`.
- Machine Room audio/video playback can launch external tools on Windows.
- Machine Room cover extraction and video frame decoding should work on Windows when `ffmpeg.exe` and `ffprobe.exe` are available.
- Machine Room `mpv` IPC control uses Windows named pipes, so seek, pause, and live volume control are expected to work when `mpv.exe` supports `--input-ipc-server`.

Windows release test checklist:

- Start audio media through Machine Room.
- Confirm Play/Pause and live volume work.
- Start video media through Machine Room.
- Confirm cover/video frame rendering works in the small slot and after Swap.
- Confirm seekbar position updates and dragging seeks both audio and video.

## Linux Release Status

Linux release packaging exists through:

```sh
scripts/release-linux.sh
```

Current Linux expectations:

- `libSDL2-2.0.so` must be shipped in the release folder or installed system-wide.
- `mpv`, `ffmpeg`, `ffprobe`, and optionally `ffplay` should be installed and discoverable through `PATH`.
- Machine Room media playback, seek, volume control, cover extraction, and video frame decoding are expected to work when those tools are installed.

## Renderer Notes

Desktop renderer options:

- Bitmap fallback
- OpenGL
- Vulkan on Linux and Windows

Vulkan requires a working platform Vulkan loader and GPU driver:

- Windows: Vulkan runtime from the GPU driver stack.
- Linux: system Vulkan loader, ICD, and matching GPU driver.

If Vulkan fails, the app should remain usable through another renderer mode, but release testing should verify the selected default on the target machine.

## Android App

Android builds use the dedicated `EutherDrive.Android` project and do not use the desktop Machine Room external media tool path.

Required:

- .NET Android workload
- Android SDK / platform tools

Build helper scripts live under `scripts/`.
