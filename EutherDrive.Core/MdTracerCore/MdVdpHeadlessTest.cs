using System;

namespace EutherDrive.Core.MdTracerCore;

/// <summary>
/// Headless test-"VDP": producerar BGRA32 framebuffer och reagerar på input.
/// Inga beroenden till md_main/md_m68k/DMA osv.
/// </summary>
public sealed class MdVdpHeadlessTest
{
    public const int DefaultW = 320;
    public const int DefaultH = 224;

    public int Width { get; private set; } = DefaultW;
    public int Height { get; private set; } = DefaultH;
    public int Stride => Width * 4; // BGRA32

    private byte[] _fb = Array.Empty<byte>();
    private int _tick;

    // “sprite” (bara en cirkel)
    private int _px = DefaultW / 2;
    private int _py = DefaultH / 2;

    // input latch
    private bool _up, _down, _left, _right, _a, _b, _c, _start;

    public ReadOnlySpan<byte> GetFrame() => _fb;

    public void Reset(int w = DefaultW, int h = DefaultH)
    {
        Width = w;
        Height = h;
        _fb = new byte[Width * Height * 4];

        _tick = 0;
        _px = Width / 2;
        _py = Height / 2;
    }

    public void SetInput(bool up, bool down, bool left, bool right, bool a, bool b, bool c, bool start)
    {
        _up = up; _down = down; _left = left; _right = right;
        _a = a; _b = b; _c = c; _start = start;
    }

    public void RunFrame()
    {
        if (_fb.Length == 0)
            Reset();

        _tick++;

        // 1) uppdatera position
        int speed = _start ? 6 : 2;
        if (_left)  _px -= speed;
        if (_right) _px += speed;
        if (_up)    _py -= speed;
        if (_down)  _py += speed;

        int radius = 18;
        _px = Math.Clamp(_px, radius, Width - 1 - radius);
        _py = Math.Clamp(_py, radius, Height - 1 - radius);

        // 2) bakgrund (rörlig)
        int w = Width, h = Height;
        for (int y = 0; y < h; y++)
        {
            int row = y * w * 4;
            for (int x = 0; x < w; x++)
            {
                int i = row + x * 4;

                byte r = (byte)((x + _tick) & 0xFF);
                byte g = (byte)((y * 2 + (_tick >> 1)) & 0xFF);
                byte b = (byte)((x ^ y ^ _tick) & 0xFF);

                // BGRA
                _fb[i + 0] = b;
                _fb[i + 1] = g;
                _fb[i + 2] = r;
                _fb[i + 3] = 255;
            }
        }

        // 3) “sprite” (cirkeln) – färg med A/B/C
        byte sr = 255, sg = 255, sb = 255;
        if (_a) { sr = 255; sg = 80;  sb = 80;  }
        if (_b) { sr = 80;  sg = 255; sb = 80;  }
        if (_c) { sr = 80;  sg = 80;  sb = 255; }

        int rr = radius * radius;
        for (int y = _py - radius; y <= _py + radius; y++)
        {
            if ((uint)y >= (uint)h) continue;
            int dy = y - _py;

            int row = y * w * 4;
            for (int x = _px - radius; x <= _px + radius; x++)
            {
                if ((uint)x >= (uint)w) continue;
                int dx = x - _px;
                if (dx * dx + dy * dy > rr) continue;

                int i = row + x * 4;
                _fb[i + 0] = sb;   // B
                _fb[i + 1] = sg;   // G
                _fb[i + 2] = sr;   // R
                _fb[i + 3] = 255;  // A
            }
        }
    }
}
