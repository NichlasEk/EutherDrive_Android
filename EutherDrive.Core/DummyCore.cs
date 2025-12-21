using System;

namespace EutherDrive.Core;

public sealed class DummyCore : IEmulatorCore
{
    private const int W = 320;
    private const int H = 224;
    private const int BPP = 4; // BGRA
    private readonly byte[] _fb = new byte[W * H * BPP];

    private int _tick;

    // "Spelare"
    private int _px = W / 2;
    private int _py = H / 2;

    // Input (latched av UI varje frame)
    private bool _up, _down, _left, _right, _a, _b, _c, _start;

    // Edge-detektion (för toggles)
    private bool _prevA, _prevB, _prevC, _prevStart;

    // Togglar
    private bool _paused;
    private bool _invertBg;
    private bool _trail;

    // Cursor speed
    private int _baseSpeed = 2;

    // Simulerad VBlank “flagga” (bara för debug/visual)
    private bool _vblank;

    public void LoadRom(string path) => Reset();

    public void Reset()
    {
        _tick = 0;
        _px = W / 2;
        _py = H / 2;

        _paused = false;
        _invertBg = false;
        _trail = false;
        _vblank = false;

        Array.Clear(_fb, 0, _fb.Length);
    }

    public void SetInputState(bool up, bool down, bool left, bool right, bool a, bool b, bool c, bool start)
    {
        _up = up; _down = down; _left = left; _right = right;
        _a = a; _b = b; _c = c; _start = start;
    }

    public void RunFrame()
    {
        // --- Edge-detektion (tryck) ---
        bool aPressed = _a && !_prevA;
        bool bPressed = _b && !_prevB;
        bool cPressed = _c && !_prevC;
        bool startPressed = _start && !_prevStart;

        _prevA = _a; _prevB = _b; _prevC = _c; _prevStart = _start;

        // Start togglar pause
        if (startPressed)
            _paused = !_paused;

        // C togglar invert background
        if (cPressed)
            _invertBg = !_invertBg;

        // B togglar trail
        if (bPressed)
            _trail = !_trail;

        // “VBlank” (simulerad): växla varje frame så du kan se det visuellt
        _vblank = !_vblank;

        // Om vi är pausade: rita bara overlays men håll tick/pos still (för tydlig kontroll)
        if (!_paused)
        {
            _tick++;

            // A = speed boost (håll nere)
            int speed = _baseSpeed + (_a ? 3 : 0);

            if (_left)  _px -= speed;
            if (_right) _px += speed;
            if (_up)    _py -= speed;
            if (_down)  _py += speed;

            int radius = 18;
            _px = Math.Clamp(_px, radius, W - 1 - radius);
            _py = Math.Clamp(_py, radius, H - 1 - radius);
        }

        // --- 1) Bakgrund ---
        // trail = lämna gamla pixlar (kul effekt)
        if (!_trail)
        {
            for (int y = 0; y < H; y++)
            {
                for (int x = 0; x < W; x++)
                {
                    int i = (y * W + x) * BPP;

                    byte r = (byte)((x + _tick) & 0xFF);
                    byte g = (byte)((y * 2 + (_tick >> 1)) & 0xFF);
                    byte b = (byte)((x ^ y ^ _tick) & 0xFF);

                    if (_invertBg) { r = (byte)(255 - r); g = (byte)(255 - g); b = (byte)(255 - b); }

                    _fb[i + 0] = b;
                    _fb[i + 1] = g;
                    _fb[i + 2] = r;
                    _fb[i + 3] = 255;
                }
            }
        }

        // --- 2) Rita “spelare” (cirkeln) ---
        DrawCircle(_px, _py, 18, 255, 255, 255);

        // A/B/C ger färg-feedback direkt på cirkeln (håll)
        if (_a) DrawCircle(_px, _py, 10, 255, 200, 80);   // A: varm
        if (_b) DrawCircle(_px, _py, 10, 80, 200, 255);   // B: kall
        if (_c) DrawCircle(_px, _py, 10, 200, 80, 255);   // C: lila

        // --- 3) Overlay-rad: VBlank + input bits (syns alltid) ---
        // Rad 0: VBlank-indikator
        DrawBar(0, 0, 40, _vblank ? (byte)0 : (byte)255, _vblank ? (byte)255 : (byte)0, 0);

        // Rad 1: knappar som små block
        DrawBit(1, 0,  _up);
        DrawBit(1, 8,  _down);
        DrawBit(1, 16, _left);
        DrawBit(1, 24, _right);
        DrawBit(1, 40, _a);
        DrawBit(1, 48, _b);
        DrawBit(1, 56, _c);
        DrawBit(1, 64, _start);

        // Rad 2: pause-indikator
        if (_paused) DrawBar(2, 0, 80, 255, 0, 0);
    }

    private void DrawCircle(int cx, int cy, int radius, byte r, byte g, byte b)
    {
        int rr = radius * radius;
        for (int y = cy - radius; y <= cy + radius; y++)
        {
            if ((uint)y >= (uint)H) continue;
            int dy = y - cy;

            for (int x = cx - radius; x <= cx + radius; x++)
            {
                if ((uint)x >= (uint)W) continue;
                int dx = x - cx;
                if (dx * dx + dy * dy > rr) continue;

                int i = (y * W + x) * BPP;
                _fb[i + 0] = b;
                _fb[i + 1] = g;
                _fb[i + 2] = r;
                _fb[i + 3] = 255;
            }
        }
    }

    private void DrawBar(int y, int x0, int width, byte r, byte g, byte b)
    {
        if ((uint)y >= (uint)H) return;
        int x1 = Math.Min(W, x0 + width);

        for (int x = Math.Max(0, x0); x < x1; x++)
        {
            int i = (y * W + x) * BPP;
            _fb[i + 0] = b;
            _fb[i + 1] = g;
            _fb[i + 2] = r;
            _fb[i + 3] = 255;
        }
    }

    private void DrawBit(int y, int x, bool on)
    {
        // liten 6x1 “pixel”-bit
        DrawBar(y, x, 6, on ? (byte)0 : (byte)60, on ? (byte)255 : (byte)60, on ? (byte)0 : (byte)60);
    }

    public ReadOnlySpan<byte> GetFrameBuffer(out int width, out int height, out int stride)
    {
        width = W;
        height = H;
        stride = W * BPP;
        return _fb;
    }

    public ReadOnlySpan<short> GetAudioBuffer(out int sampleRate, out int channels)
    {
        sampleRate = 44100;
        channels = 1;
        return ReadOnlySpan<short>.Empty;
    }
}
