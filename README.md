# EutherDrive

EutherDrive är en Mega Drive / Genesis-emulator skriven i C# med [Avalonia UI](https://avaloniaui.net/) som frontend.  
Projektet bygger på kärnkod från [MDTracer](https://github.com/sasayaki-japan/MDTracer) (MIT-licens) och utökar den med ett modernt, plattformsoberoende gränssnitt och förbättrad kompatibilitet.

## Funktioner
- Spelar upp Mega Drive ROM:ar direkt från filväljare
- Avalonia-baserad frontend (Windows, Linux, macOS, Android)
- Input-hantering för tangentbord och gamepads
- (Planerat) Ljudutgång via NAudio / OpenAL
- (Planerat) SRAM-sparning, PAL/NTSC-switch, interlace-stöd

## Installation
Bygg från källkod med .NET 8:
```bash
git clone https://github.com/[dittkonto]/EutherDrive
cd EutherDrive
dotnet build
dotnet run --project EutherDrive.UI
