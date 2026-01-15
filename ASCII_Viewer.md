# EutherDrive ASCII Viewer

Live ASCII art framebuffer viewer for EutherDrive Sega Genesis emulator. Renders the framebuffer as colored ASCII characters in real-time.

## Features

- TrueColor (24-bit) ANSI color output
- Configurable resolution and character set
- Real-time updates (~60fps)
- Works with both UI and headless modes
- Debug logging for troubleshooting

## Usage

### Prerequisites

1. Start the emulator with ASCII streaming enabled
2. In a separate terminal, start the viewer

### Starting the Emulator

**Headless mode:**
```bash
export EUTHERDRIVE_ASCII_STREAM=1
dotnet run --project EutherDrive.Headless -- ~/roms/sonic2.md
```

**UI mode:**
```bash
# Enable ASCII Stream toggle in the UI, or:
export EUTHERDRIVE_ASCII_STREAM=1
dotnet run --project EutherDrive.UI
```

### Starting the Viewer

```bash
dotnet run --project EutherDrive.AsciiViewer -- [options]
```

## Command Line Options

| Option | Default | Description |
|--------|---------|-------------|
| `--width N` | 80 | Output width (20-160) |
| `--height N` | 40 | Output height (20-60) |
| `--chars STRING` | ` .:-=+*#%@` | ASCII character ramp |
| `--8bit` | false | Use 8-bit colors instead of TrueColor |
| `--sample N` | 1 | Sample every Nth frame |
| `--file PATH` | `/tmp/eutherdrive_ascii_fb.dat` | Framebuffer file path |
| `--poll N` | 16 | Poll interval in ms (~60fps) |
| `--help` | - | Show help |

## Examples

**Standard view (80x40):**
```bash
dotnet run --project EutherDrive.AsciiViewer -- --width 80 --height 40
```

**Low resolution retro look:**
```bash
dotnet run --project EutherDrive.AsciiViewer -- --width 40 --height 25 --chars ' .,-+*#'
```

**Higher resolution:**
```bash
dotnet run --project EutherDrive.AsciiViewer -- --width 120 --height 50
```

**Slower framerate (30fps):**
```bash
dotnet run --project EutherDrive.AsciiViewer -- --poll 33
```

## Character Ramps

Different character ramps create different visual effects:

| Ramp | Style |
|------|-------|
| ` .:-=+*#%@` | Standard (default) |
| ` .,-+*#` | Retro 5-level |
| ` .'` | Minimal |
| `$@B%8&WM#*oahkbdpqwmZO0QLCJUYXzcvunxrjft/\|()1{}[]?-_+~<>i!lI;:,"^`'. ` | Dense (many levels) |

## Terminal Requirements

- Terminal must support TrueColor (24-bit color)
- Font should be monospaced
- Recommended: gnome-terminal, kitty, alacritty, or iTerm2

Check TrueColor support:
```bash
curl -s https://raw.githubusercontent.com/termstandard/colors/master/24-bit.sh | bash
```

## Debug Logging

Debug logs are written to separate files:

- `/tmp/eutherdrive_ascii_viewer.log` - Viewer read operations
- `/tmp/eutherdrive_ascii_adapter.log` - Adapter write operations

View logs in real-time:
```bash
tail -f /tmp/eutherdrive_ascii_viewer.log
tail -f /tmp/eutherdrive_ascii_adapter.log
```

## How It Works

```
┌─────────────────┐      IPC       ┌─────────────────┐
│  EutherDrive    │ ────────────── │ ASCII Viewer    │
│  (Adapter)      │  /tmp/euther-  │  (Terminal)     │
│                 │  drive_ascii_  │                 │
│  - Writes FB    │  fb.dat        │  - Polls file   │
│  - 16B header   │                │  - Renders ASCII│
│  - RGBA data    │                │  - TrueColor    │
└─────────────────┘                └─────────────────┘
```

### File Format

```
Offset  Size  Description
------  ----  -----------
0       4     Width (little-endian int32)
4       4     Height (little-endian int32)
8       4     Data size (little-endian int32)
12      4     Frame number (little-endian int32)
16      N     Framebuffer data (RGBA, 4 bytes per pixel)
```

## Troubleshooting

### Viewer doesn't update

1. Check emulator is running with `EUTHERDRIVE_ASCII_STREAM=1`
2. Check debug logs for errors
3. Verify framebuffer file exists: `ls -la /tmp/eutherdrive_ascii_fb.dat`

### Colors look wrong

1. Ensure terminal supports TrueColor
2. Try `--8bit` flag for 8-bit color fallback
3. Check `$TERM` environment variable

### Performance issues

1. Reduce resolution with `--width 60 --height 30`
2. Increase poll interval: `--poll 33` (~30fps)
3. Use simpler character ramp: `--chars ' .:-=+*#'`

## Exit

Press `Ctrl+C` to exit the viewer. The alternate screen mode will be restored.
