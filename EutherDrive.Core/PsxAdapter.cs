using System;
using System.IO;
using ProjectPSX;
using ProjectPSX.Devices.Input;

namespace EutherDrive.Core;

public sealed class PsxAdapter : IEmulatorCore
{
    private sealed class PsxHostWindow : IHostWindow
    {
        private readonly PsxAdapter _owner;
        private int _displayWidth = 320;
        private int _displayHeight = 240;
        private bool _is24Bit;
        private ushort _vramXStart;
        private ushort _vramYStart;

        public PsxHostWindow(PsxAdapter owner) => _owner = owner;

        public void Render(int[] vram)
        {
            _owner.UpdateFrame(vram, _displayWidth, _displayHeight, _vramXStart, _vramYStart, _is24Bit);
        }

        public void SetDisplayMode(int horizontalRes, int verticalRes, bool is24BitDepth)
        {
            _displayWidth = horizontalRes > 0 ? horizontalRes : 320;
            _displayHeight = verticalRes > 0 ? verticalRes : 240;
            _is24Bit = is24BitDepth;
        }

        public void SetHorizontalRange(ushort displayX1, ushort displayX2)
        {
            _ = displayX1;
            _ = displayX2;
        }

        public void SetVRAMStart(ushort displayVRAMXStart, ushort displayVRAMYStart)
        {
            _vramXStart = displayVRAMXStart;
            _vramYStart = displayVRAMYStart;
        }

        public void SetVerticalRange(ushort displayY1, ushort displayY2)
        {
            _ = displayY1;
            _ = displayY2;
        }

        public void Play(byte[] samples) => _owner.PushAudio(samples);
    }

    private ProjectPSX.ProjectPSX? _core;
    private PsxHostWindow? _host;
    private string? _diskPath;
    private byte[] _frameBuffer = Array.Empty<byte>();
    private int _frameWidth = 320;
    private int _frameHeight = 240;
    private int _frameStride = 320 * 4;
    private short[] _audioBuffer = Array.Empty<short>();

    public void LoadRom(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            throw new FileNotFoundException("PSX image not found.", path);

        _diskPath = path;
        _host = new PsxHostWindow(this);
        _core = new ProjectPSX.ProjectPSX(_host, path);
    }

    public void Reset()
    {
        if (string.IsNullOrWhiteSpace(_diskPath))
            return;
        LoadRom(_diskPath);
    }

    public void RunFrame()
    {
        _core?.RunFrame();
    }

    public ReadOnlySpan<byte> GetFrameBuffer(out int width, out int height, out int stride)
    {
        width = _frameWidth;
        height = _frameHeight;
        stride = _frameStride;
        return _frameBuffer;
    }

    public ReadOnlySpan<short> GetAudioBuffer(out int sampleRate, out int channels)
    {
        sampleRate = 44100;
        channels = 2;
        return _audioBuffer;
    }

    public void SetInputState(
        bool up,
        bool down,
        bool left,
        bool right,
        bool a,
        bool b,
        bool c,
        bool start,
        bool x,
        bool y,
        bool z,
        bool mode,
        PadType padType)
    {
        _ = padType;
        if (_core == null)
            return;

        SetButton(GamepadInputsEnum.Up, up);
        SetButton(GamepadInputsEnum.Down, down);
        SetButton(GamepadInputsEnum.Left, left);
        SetButton(GamepadInputsEnum.Right, right);
        SetButton(GamepadInputsEnum.Z, a);
        SetButton(GamepadInputsEnum.C, b);
        SetButton(GamepadInputsEnum.D1, c);
        SetButton(GamepadInputsEnum.Enter, start);
        SetButton(GamepadInputsEnum.D3, x);
        SetButton(GamepadInputsEnum.Q, y);
        SetButton(GamepadInputsEnum.E, z);
        SetButton(GamepadInputsEnum.Space, mode);
    }

    private void SetButton(GamepadInputsEnum button, bool pressed)
    {
        if (_core == null)
            return;
        if (pressed)
            _core.JoyPadDown(button);
        else
            _core.JoyPadUp(button);
    }

    private void UpdateFrame(int[] vram, int width, int height, ushort vramX, ushort vramY, bool is24Bit)
    {
        if (width <= 0 || height <= 0)
            return;

        int stride = width * 4;
        int required = stride * height;
        if (_frameBuffer.Length != required)
            _frameBuffer = new byte[required];

        _frameWidth = width;
        _frameHeight = height;
        _frameStride = stride;

        int baseX = vramX;
        int baseY = vramY;
        int vramWidth = 1024;
        int vramHeight = 512;

        for (int y = 0; y < height; y++)
        {
            int srcY = (baseY + y) % vramHeight;
            int srcRow = srcY * vramWidth;
            int dstRow = y * stride;
            for (int x = 0; x < width; x++)
            {
                int srcX = (baseX + x) % vramWidth;
                int color = vram[srcRow + srcX];
                int o = dstRow + (x << 2);
                _frameBuffer[o + 0] = (byte)(color & 0xFF);
                _frameBuffer[o + 1] = (byte)((color >> 8) & 0xFF);
                _frameBuffer[o + 2] = (byte)((color >> 16) & 0xFF);
                _frameBuffer[o + 3] = is24Bit ? (byte)0xFF : (byte)((color >> 24) & 0xFF);
            }
        }
    }

    private void PushAudio(byte[] samples)
    {
        int sampleCount = samples.Length / 2;
        if (_audioBuffer.Length != sampleCount)
            _audioBuffer = new short[sampleCount];

        int si = 0;
        for (int i = 0; i < sampleCount; i++)
        {
            _audioBuffer[i] = (short)(samples[si] | (samples[si + 1] << 8));
            si += 2;
        }
    }
}
