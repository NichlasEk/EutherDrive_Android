using System;

namespace EutherDrive.Core;

/// <summary>
/// Test core for VDP interlace mode 2 rendering.
/// Generates a known 320x448 test pattern to verify:
/// - UI can display 448-line framebuffer correctly
/// - Pixel format (BGRA + alpha) is correct
/// - Field toggling (odd/even) works
/// </summary>
public sealed class InterlaceTestCore : IEmulatorCore
{
    private const int W = 320;
    private const int H = 448; // Interlace mode 2 resolution
    private const int BPP = 4; // BGRA32

    private readonly byte[] _fb = new byte[W * H * BPP];

    private int _frame;
    private int _field; // 0 = even, 1 = odd
    private bool _enabled;
    private bool _staticMode; // If true, don't toggle field (easier to verify)
    private readonly object _fbLock = new object(); // Protect framebuffer during generation

    // Palette colors (RGB)
    private static readonly int[] Palette = new int[]
    {
        0x0000FF,   // Red (BGR)
        0x00FF00,   // Green (BGR)
        0xFF0000,   // Blue (BGR)
        0xFFFFFF,   // White (BGR)
        0x000000,   // Black (BGR)
        0x00FFFF,   // Yellow (BGR)
        0xFFFF00,   // Cyan (BGR)
        0xFF00FF,   // Magenta (BGR)
    };

    public bool IsEnabled => _enabled;

    /// <summary>
    /// When true, field doesn't toggle (static 448-line image for easier verification)
    /// </summary>
    public bool StaticMode
    {
        get => _staticMode;
        set
        {
            if (_staticMode != value)
            {
                _staticMode = value;
                GenerateFrame(); // Regenerate with current field
            }
        }
    }

    /// <summary>
    /// Returns current frame ID for UI sync tracking
    /// </summary>
    public int GetFrameId() => _frame;

    public void LoadRom(string path) => Reset();

    public void Reset()
    {
        _frame = 0;
        _field = 0;
        _enabled = true;
        GenerateFrame();
    }

    public void SetInputState(
        bool up, bool down, bool left, bool right,
        bool a, bool b, bool c, bool start,
        bool x, bool y, bool z, bool mode, PadType padType)
    {
        // Input not used for test core
    }

    public void RunFrame()
    {
        lock (_fbLock)
        {
            _frame++;
            if (!_staticMode)
                _field = _frame % 2; // Toggle field every frame
            GenerateFrame();
        }
    }

    private int _lastGeneratedField = -1;
    private int _lastGeneratedFrameId = -1;

    private void GenerateFrame()
    {
        // Always regenerate everything - this is a test pattern
        // The "black flash" is likely a sync issue, not the clear
        GenerateBackground();
        GenerateScanlineMarkers();
        GeneratePaletteTest();
        GenerateFieldOverlay();
        GenerateBlinkMarker();
        GenerateCounter();
        _lastGeneratedFrameId = _frame;
    }

    private void GenerateBackground()
    {
        for (int y = 0; y < H; y++)
        {
            bool isUpperHalf = y < 224;

            for (int x = 0; x < W; x++)
            {
                int i = (y * W + x) * BPP;

                byte r, g, b;

                if (isUpperHalf)
                {
                    // Upper half: horizontal gradient (red to blue)
                    r = (byte)((x * 255) / W);
                    g = (byte)(64); // Slight green tint
                    b = (byte)(255 - r);
                }
                else
                {
                    // Lower half: vertical gradient (green to purple)
                    int yOffset = y - 224;
                    r = (byte)((yOffset * 200) / 224);
                    g = (byte)(128);
                    b = (byte)((yOffset * 127) / 224 + 128);
                }

                _fb[i + 0] = b;
                _fb[i + 1] = g;
                _fb[i + 2] = r;
                _fb[i + 3] = 255; // Opaque
            }
        }
    }

    private void GenerateScanlineMarkers()
    {
        // White line every 16 lines
        for (int y = 0; y < H; y += 16)
        {
            for (int x = 0; x < W; x++)
            {
                int i = (y * W + x) * BPP;
                _fb[i + 0] = 255; // B
                _fb[i + 1] = 255; // G
                _fb[i + 2] = 255; // R
                _fb[i + 3] = 255; // A
            }
        }

        // Extra bright line at the middle boundary (line 224)
        for (int x = 0; x < W; x++)
        {
            int i = (224 * W + x) * BPP;
            _fb[i + 0] = 0;   // B
            _fb[i + 1] = 255; // G (green for middle marker)
            _fb[i + 2] = 255; // R
            _fb[i + 3] = 255; // A
        }
    }

    private void GeneratePaletteTest()
    {
        // 8x8 squares in top-left corner
        int startX = 10;
        int startY = 10;
        int size = 16;
        int gap = 2;

        for (int p = 0; p < 8; p++)
        {
            int px = startX + (p % 4) * (size + gap);
            int py = startY + (p / 4) * (size + gap);
            int color = Palette[p]; // BGR format

            for (int y = py; y < py + size && y < H; y++)
            {
                for (int x = px; x < px + size && x < W; x++)
                {
                    int i = (y * W + x) * BPP;
                    _fb[i + 0] = (byte)(color >> 0);  // B
                    _fb[i + 1] = (byte)(color >> 8);  // G
                    _fb[i + 2] = (byte)(color >> 16); // R
                    _fb[i + 3] = 255;
                }
            }
        }
    }

    private void GenerateFieldOverlay()
    {
        string text = _field == 0 ? "FIELD EVEN" : "FIELD  ODD ";
        int x = 200;
        int y = _field == 0 ? 100 : 330; // Upper half for even, lower for odd

        // Draw text background for readability
        DrawRect(x - 5, y - 20, 100, 30, _field == 0 ? 0xCC0000FF : 0xCC00FF00);

        // Draw text (simplified - pixel by pixel)
        DrawText(x, y, text, _field == 0 ? 0xFFFFFF00 : 0xFFFFFF00);
    }

    private void GenerateBlinkMarker()
    {
        // Column that only appears on specific field
        // Position: column 50, visible on odd lines of the active field
        int col = 50;

        // Blink pattern: visible every 4th line within the field
        int blinkPeriod = 4;
        bool show = ((_frame / blinkPeriod) % 2) == 0;

        if (show)
        {
            for (int y = 0; y < H; y++)
            {
                // Only draw on lines that belong to current field
                bool isFieldLine = (y / 2) % 2 == _field;
                if (!isFieldLine) continue;

                int i = (y * W + col) * BPP;
                _fb[i + 0] = 255; // B - cyan/magenta alternates
                _fb[i + 1] = 0;
                _fb[i + 2] = 255; // R
                _fb[i + 3] = 255;
            }

            // Also draw at column 51 for a 2-pixel wide marker
            for (int y = 0; y < H; y++)
            {
                bool isFieldLine = (y / 2) % 2 == _field;
                if (!isFieldLine) continue;

                int i = (y * W + col + 1) * BPP;
                _fb[i + 0] = 255;
                _fb[i + 1] = 0;
                _fb[i + 2] = 255;
                _fb[i + 3] = 255;
            }
        }
    }

    private void GenerateCounter()
    {
        // Frame counter in top-right
        DrawText(230, 10, $"FRAME={_frame:D5}", 0xFFFFFF00);

        // Field counter
        DrawText(230, 30, $"FIELD={_field}", 0xFFFFFF00);

        // Resolution indicator
        DrawText(230, 50, $"RES={W}x{H}", 0xFFFFFF00);
    }

    private void DrawRect(int x, int y, int w, int h, uint color)
    {
        // color is BGRA (32-bit)
        byte b = (byte)(color >> 0);
        byte g = (byte)(color >> 8);
        byte r = (byte)(color >> 16);
        byte a = (byte)(color >> 24);

        for (int dy = y; dy < y + h && dy < H; dy++)
        {
            for (int dx = x; dx < x + w && dx < W; dx++)
            {
                int i = (dy * W + dx) * BPP;
                _fb[i + 0] = b;
                _fb[i + 1] = g;
                _fb[i + 2] = r;
                _fb[i + 3] = a;
            }
        }
    }

    private void DrawText(int x, int y, string text, uint color)
    {
        byte b = (byte)(color >> 0);
        byte g = (byte)(color >> 8);
        byte r = (byte)(color >> 16);

        // Simple 3x5 pixel font using string patterns
        // Each string row is 3-4 chars, representing pixels
        var font = new Dictionary<char, string[]>
        {
            ['0'] = new[] { "####", "#..#", "#..#", "#..#", "####" },
            ['1'] = new[] { ".#..", "##..", ".#..", ".#..", "###." },
            ['2'] = new[] { "####", "...#", "####", "#...", "####" },
            ['3'] = new[] { "####", "...#", "####", "...#", "####" },
            ['4'] = new[] { "#.##", "#.##", "####", "...#", "...#" },
            ['5'] = new[] { "####", "#...", "####", "...#", "####" },
            ['6'] = new[] { "####", "#...", "####", "#.##", "####" },
            ['7'] = new[] { "####", "...#", "...#", "...#", "...#" },
            ['8'] = new[] { "####", "#.##", "####", "#.##", "####" },
            ['9'] = new[] { "####", "#.##", "####", "...#", "####" },
            ['='] = new[] { "....", "####", "....", "####", "...." },
            ['F'] = new[] { "####", "#...", "###.", "#...", "#..." },
            ['R'] = new[] { "####", "#.##", "####", "#.#.", "#..#" },
            ['A'] = new[] { "####", "#.##", "####", "#.##", "#.##" },
            ['M'] = new[] { "#.##", "#.##", "#.##", "#.##", "####" },
            ['E'] = new[] { "####", "#...", "###.", "#...", "####" },
            ['L'] = new[] { "#...", "#...", "#...", "#...", "####" },
            ['D'] = new[] { "####", "#.##", "#.##", "#.##", "####" },
            ['O'] = new[] { "####", "#.##", "#.##", "#.##", "####" },
            [' '] = new[] { "....", "....", "....", "....", "...." },
            ['I'] = new[] { "####", ".##.", ".##.", ".##.", "####" },
            ['N'] = new[] { "#.##", "#.##", "#.##", "#.##", "####" },
            ['S'] = new[] { "####", "#...", "####", "...#", "####" },
            ['Z'] = new[] { "####", "...#", "..#.", ".#..", "####" },
            ['H'] = new[] { "#.##", "#.##", "####", "#.##", "#.##" },
            ['G'] = new[] { ".###", "#...", "#.##", "#.##", ".###" },
            ['.'] = new[] { "....", "....", "....", "....", ".#.." },
        };

        foreach (char ch in text)
        {
            if (!font.TryGetValue(ch, out var rows))
                rows = font[' '];

            for (int py = 0; py < 5 && py < rows.Length; py++)
            {
                string row = rows[py];
                for (int px = 0; px < row.Length; px++)
                {
                    int drawX = x + px;
                    int drawY = y + py;

                    if (drawX < W && drawY < H && row[px] == '#')
                    {
                        int i = (drawY * W + drawX) * BPP;
                        _fb[i + 0] = b;
                        _fb[i + 1] = g;
                        _fb[i + 2] = r;
                        _fb[i + 3] = 255;
                    }
                }
            }

            x += 5; // char width + spacing
        }
    }

    private readonly byte[] _fbStable = new byte[W * H * BPP];

    public ReadOnlySpan<byte> GetFrameBuffer(out int width, out int height, out int stride)
    {
        width = W;
        height = H;
        stride = W * BPP;

        // Copy to stable buffer to avoid race conditions
        // The UI might read while we're generating
        lock (_fbLock)
        {
            Buffer.BlockCopy(_fb, 0, _fbStable, 0, _fb.Length);
        }
        return _fbStable;
    }

    public ReadOnlySpan<short> GetAudioBuffer(out int sampleRate, out int channels)
    {
        sampleRate = 44100;
        channels = 0; // No audio for test core
        return ReadOnlySpan<short>.Empty;
    }
}
