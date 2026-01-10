# EutherDrive

![EutherDrive logo](Icons/logo.jpeg)

EutherDrive är en Mega Drive / Genesis-emulator skriven i C# med [Avalonia UI](https://avaloniaui.net/) som frontend.  
Projektet bygger på kärnkod från [MDTracer](https://github.com/sasayaki-japan/MDTracer) (MIT-licens) och utökar den med ett modernt, plattformsoberoende gränssnitt och förbättrad kompatibilitet.

## Funktioner
- Spelar upp Mega Drive ROM:ar direkt från filväljare
- Avalonia-baserad frontend (Windows, Linux, macOS, Android)
- Input-hantering för tangentbord och gamepads
- Ljudutgång via Pipewire på Linux (planerar android fork)
- SRAM-sparning fungerar
- Interlace-stöd fungerar, inklusive Sonic 2 interlaced-läge
- PAL/NTSC-switch fungerar
- Region hantering fungerar

## Installation
Bygg från källkod med .NET 8:
```bash
git clone https://github.com/[dittkonto]/EutherDrive
cd EutherDrive
dotnet build
dotnet run --project EutherDrive.UI

## Att göra
Fixa Z80 beroende ljud. något är trasigt
Implementera kontroller för spelare 2
Implementera joypad stödet helt
Skapa overlay för kontroller på android
Eventuellt göra mer korrekt HBLANK metod enligt TODO.md
