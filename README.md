# EutherDrive

![EutherDrive logo](Icons/logo.jpeg)

EutherDrive är en Mega Drive / Genesis-emulator skriven i C# med [Avalonia UI](https://avaloniaui.net/) som frontend.  
Projektet bygger på kärnkod från [MDTracer](https://github.com/sasayaki-japan/MDTracer) (MIT-licens) och utökar den med ett modernt, plattformsoberoende gränssnitt och förbättrad kompatibilitet.

Emulatorn spelar också Sega Mastersystem spel. Alla titlar som jag provat fungerar inklusive korean mappers och Codemaster spel.

Grundläggande SNES-stöd är inlagt via SuperNintendoEmulator-projektet (se referens nedan).

Det grundläggande SNES stödet är påbyggt med specialcip och ny ljudmotor. Dessa är främst portar från jgenesis se referens nedan.
Emulatorn spelar nu väldigt många SNES titlar

## Funktioner
- Spelar upp Mega Drive ROM:ar direkt från filväljare
- Grundläggande SNES-stöd (via separat core)
- Avalonia-baserad frontend (Windows, Linux, macOS, Android)
- Input-hantering för tangentbord och gamepads
- Ljudutgång via Pipewire på Linux (planerar android fork)
- Savestates (3 slots per ROM, en fil per ROM)
- SRAM-sparning fungerar
- Interlace-stöd fungerar, inklusive Sonic 2 interlaced-läge
- PAL/NTSC-switch fungerar
- Region hantering fungerar

## Kortkommandon (UI)
- F1: Fullscreen
- F5: Save Slot 1
- F6: Save Slot 2
- F7: Save Slot 3
- F8: Load Slot 1
- F9: Load Slot 2
- F10: Load Slot 3

## Installation
Bygg från källkod med .NET 8:
```bash
git clone https://github.com/[dittkonto]/EutherDrive
cd EutherDrive
dotnet build
dotnet run --project EutherDrive.UI
```

## Referenser
```
https://github.com/Kookpot/SuperNintendoEmulator
```

https://github.com/jsgroth/jgenesis/

## Att göra
Fixa Z80 beroende ljud. något är trasigt
Implementera kontroller för spelare 2
Implementera joypad stödet helt
Skapa overlay för kontroller på android
Eventuellt göra mer korrekt HBLANK metod enligt TODO.md
