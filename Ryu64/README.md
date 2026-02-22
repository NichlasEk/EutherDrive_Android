# Ryu64 Core

Extraherad kärna från Ryu64 N64-emulatorn för användning i egna UI-implementationer.

## Struktur

```
Ryu64Core/
├── Ryu64Core.csproj          # Huvudprojektfil
├── Ryu64Core.cs              # API-wrapper för enkel integration
├── Ryu64.MIPS/               # CPU-emulering (MIPS R4300)
├── Ryu64.Common/             # Delade resurser och verktyg
└── Ryu64.Formats/            # ROM-läsning (Z64-format)
```

## Användning

### Grundläggande integration

```csharp
using Ryu64Core;

// Skapa en instans av kärnan
var core = new Ryu64Core();

// Ladda en ROM
core.LoadROM("path/to/game.z64");

// Prenumerera på händelser
core.FramebufferUpdated += (sender, e) => {
    // Hantera grafikbuffer här
    // e.Framebuffer, e.Width, e.Height, e.BytesPerPixel
};

core.StateChanged += (sender, e) => {
    Console.WriteLine($"Emulation {(e.IsRunning ? "started" : "stopped")}");
};

// Starta emuleringen
core.Start();

// Stoppa emuleringen
core.Stop();
```

### API-översikt

**Huvudmetoder:**
- `LoadROM(string path)` - Laddar en N64-ROM
- `Start()` - Startar emuleringen
- `Stop()` - Stoppar emuleringen
- `GetFramebuffer()` - Hämtar aktuell grafikbuffer
- `SetInputState(InputState input)` - Skickar kontrollinput

**Händelser:**
- `FramebufferUpdated` - När grafikbuffern uppdateras
- `AudioBufferReady` - När ljudbuffer är redo (ej implementerad)
- `StateChanged` - När emuleringsstatus ändras

## Bygginstruktioner

```bash
# Bygg kärnan
dotnet build Ryu64Core/Ryu64Core.csproj

# Referera till kärnan i ditt UI-projekt
<ProjectReference Include="path/to/Ryu64Core/Ryu64Core.csproj" />
```

## Begränsningar

1. **Grafikåtkomst** - Du måste implementera din egen grafikrendering baserat på framebuffern
2. **Ljud** - Ljudemulering är inte fullt implementerad i originalet
3. **Input** - Kontrollinput måste mappas till rätt minnesadresser
4. **Prestanda** - Kärnan använder interpreter-baserad CPU-emulering, inte JIT

## Exempel på UI-integration

Se `Ryu64Core.cs` för ett komplett API-exempel. Du behöver:
1. Ett grafikbibliotek (OpenGL, DirectX, SDL, etc.)
2. En input-hanterare för kontroller
3. En huvudloop som anropar `GetFramebuffer()` regelbundet

## Licens

Samma som Ryu64 - Public Domain (UNLICENSE)