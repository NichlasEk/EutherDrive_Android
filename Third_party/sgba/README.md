Third-party source reference for the GBA integration in EutherDrive.

Upstream project:
- Name: `sGBA`
- URL: `https://github.com/Asphaltian/sgba`
- Local donor commit used during import: `534d9f1ce1b60a0762285949865801ce93e9f7b0`
- License: MIT, see [LICENSE](/home/nichlas/EutherDrive_Android/Third_party/sgba/LICENSE)

What EutherDrive imported:
- Core emulator sources adapted into `/home/nichlas/EutherDrive_Android/EutherDrive.Core/GbaEmu`
- A headless video path and EutherDrive-specific adapter glue in `/home/nichlas/EutherDrive_Android/EutherDrive.Core/GbaAdapter.cs`

What was intentionally not imported as runtime code:
- s&box frontend and component wrappers
- s&box rendering-specific GPU presentation path
