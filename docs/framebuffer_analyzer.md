# Framebuffer Analyzer

A live debugging tool for inspecting the rendered framebuffer pixel-by-pixel.

## Quick Start

```bash
# Enable framebuffer analyzer
export EUTHERDRIVE_FB_ANALYZER=1

# Run with headless
dotnet run --project EutherDrive.Headless -- ~/roms/sonic2.md 60

# Run with UI
dotnet run --project EutherDrive.UI -- ~/roms/sonic2.md
```

## Environment Variables

| Variable | Description |
|----------|-------------|
| `EUTHERDRIVE_FB_ANALYZER=1` | Enable the framebuffer analyzer |
| `EUTHERDRIVE_LOAD_SLOT1_ON_BOOT=1` | Auto-load savestate slot 1 |

## Output Format

The analyzer samples pixels from an 8x6 grid and displays:

```
[FB-ANALYZER] Frame 1 - 320x224 framebuffer analysis:
======================================================================
Row 00: (020,018)YELLOW   (060,018)DARKGRAY (100,018)WHITE    ...
Row 01: (020,055)DARKGRAY (060,055)WHITE    (100,055)DARKGRAY ...
Row 02: (020,092)GRAY     (060,092)GRAY     (100,092)ORANGE   ...
Row 03: (020,129)GRAY     (060,129)GRAY     (100,129)GRAY     ...
Row 04: (020,166)ORANGE   (060,166)DARKGRAY (100,166)ORANGE   ...
Row 05: (020,203)GRAY     (060,203)BLACK    (100,203)ORANGE   ...
[FB-ANALYZER] Pixel format: BGRA (byte order: B=90, G=00, R=00, A=FF)
======================================================================
```

### Column Meanings

| Column | Description |
|--------|-------------|
| `(x,y)` | Pixel coordinates (column, row) |
| `YELLOW` | Detected color name |
| `BGR=(xx,xx,xx)` | Raw byte values (Blue, Green, Red) |
| `A=FF` | Alpha channel (always FF for opaque) |

## Color Names

The analyzer recognizes these colors:

| Name | RGB Range |
|------|-----------|
| `BLACK` | Gray value < 20 |
| `WHITE` | Gray value > 235 |
| `RED` | R > 200, G < 50, B < 50 |
| `GREEN` | G > 200, R < 50, B < 50 |
| `BLUE` | B > 200, R < 50, G < 50 |
| `YELLOW` | R > 200, G > 200, B < 50 |
| `CYAN` | G > 200, B > 200, R < 50 |
| `MAGENTA` | R > 200, B > 200, G < 50 |
| `ORANGE` | R > 150, G > 100, B < 50 |
| `PINK` | R > 150, G > 50, B > 150 |
| `LAVENDER` | R > 100, G > 100, B > 200 |
| `GRAY` | 80 <= gray < 160 |
| `DARKGRAY` | 20 <= gray < 80 |
| `LTGRAY` | 160 <= gray <= 235 |

## Filtering Output

```bash
# Only show grid rows
EUTHERDRIVE_FB_ANALYZER=1 dotnet run --project EutherDrive.Headless -- ~/roms/sonic2.md 60 2>&1 | grep "Row "

# Show only color summary
EUTHERDRIVE_FB_ANALYZER=1 dotnet run --project EutherDrive.Headless -- ~/roms/sonic2.md 60 2>&1 | grep -E "Row |Pixel format"

# Show frame info only
EUTHERDRIVE_FB_ANALYZER=1 dotnet run --project EutherDrive.Headless -- ~/roms/sonic2.md 60 2>&1 | grep "Frame "
```

## Programmatic Usage

```csharp
var adapter = new MdTracerAdapter();

// Enable analyzer
adapter.FbAnalyzer.Enabled = true;

// Configure grid (default: 8x6)
adapter.FbAnalyzer.ConfigureGrid(8, 6);

// Sample every N frames (default: 1, logs every 60th frame)
adapter.FbAnalyzer.SetSampleRate(1);

// Manual analysis
adapter.FbAnalyzer.AnalyzeFrame();

// Dump specific region as hex
adapter.FbAnalyzer.DumpRegionHex(x: 0, y: 0, width: 16, height: 16);
```

## Output Streams

- **stdout**: Disabled by default in UI to avoid FPS drops
- **stderr**: Always enabled for analyzer output

```bash
# Show only analyzer (stderr)
EUTHERDRIVE_FB_ANALYZER=1 dotnet run --project EutherDrive.UI -- ~/roms/sonic2.md 2>&1 | grep -E "Row|Frame|Pixel"
```

## Examples

### Check if special stage has correct colors

```bash
EUTHERDRIVE_FB_ANALYZER=1 EUTHERDRIVE_LOAD_SLOT1_ON_BOOT=1 dotnet run --project EutherDrive.Headless -- ~/roms/sonic2.md 10 2>&1 | grep "Row "
```

Expected: ORANGE for pipes, BLUE for background, YELLOW for rings.

### Compare two frames

```bash
# Save frame 0
EUTHERDRIVE_FB_ANALYZER=1 dotnet run --project EutherDrive.Headless -- ~/roms/sonic2.md 1 > frame0.txt 2>&1

# Save frame 10
EUTHERDRIVE_FB_ANALYZER=1 dotnet run --project EutherDrive.Headless -- ~/roms/sonic2.md 11 > frame10.txt 2>&1

# Compare
diff frame0.txt frame10.txt
```

### Debug display-off handling

```bash
# Run until display turns off
EUTHERDRIVE_FB_ANALYZER=1 dotnet run --project EutherDrive.Headless -- ~/roms/sonic2.md 120 2>&1 | grep -E "Row |display="
```

## Troubleshooting

### No output in UI

Make sure `EUTHERDRIVE_FB_ANALYZER=1` is set before launching:

```bash
export EUTHERDRIVE_FB_ANALYZER=1
dotnet run --project EutherDrive.UI -- ~/roms/sonic2.md
```

### Too much other output

Filter only analyzer lines:

```bash
EUTHERDRIVE_FB_ANALYZER=1 dotnet run --project EutherDrive.UI -- ~/roms/sonic2.md 2>&1 | grep -E "Row |Frame |Pixel"
```

### Analyzer shows all BLACK pixels

This indicates the display is OFF and the framebuffer wasn't preserved. Check:
- `EUTHERDRIVE_PRESERVE_FB_OFF=1` flag
- VDP display register state
