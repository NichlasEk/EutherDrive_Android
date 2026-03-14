using System;
using System.IO;
using ProjectPSX;
using ProjectPSX.Devices.Input;

namespace EutherDrive.Core;

public sealed class PsxAdapter : IEmulatorCore
{
    public static string? BiosPath { get; set; }
    public static bool AnalogControllerEnabled { get; set; }
    public static bool FastLoadEnabled { get; set; }
    public long? FrameCounter => _frameCounter;
    private sealed class PsxHostWindow : IHostWindow
    {
        private const double DefaultAspectRatio = 4.0 / 3.0;
        private readonly PsxAdapter _owner;
        private int _displayWidth = 320;
        private int _displayHeight = 240;
        private bool _is24Bit;
        private ushort _vramXStart;
        private ushort _vramYStart;
        private ushort _displayX1;
        private ushort _displayX2;
        private ushort _displayY1;
        private ushort _displayY2;

        public PsxHostWindow(PsxAdapter owner) => _owner = owner;

        public void Render(int[] vram, ushort[] vram1555)
        {
            if (!TryGetSourceViewport(out int sourceX, out int sourceY, out int sourceWidth, out int sourceHeight))
            {
                sourceX = _vramXStart;
                sourceY = _vramYStart;
                sourceWidth = _displayWidth;
                sourceHeight = _displayHeight;
            }

            _owner.UpdateFrame(vram, vram1555, sourceWidth, sourceHeight, (ushort)sourceX, (ushort)sourceY, _is24Bit);
        }

        public void SetDisplayMode(int horizontalRes, int verticalRes, bool is24BitDepth)
        {
            _displayWidth = horizontalRes > 0 ? horizontalRes : 320;
            _displayHeight = verticalRes > 0 ? verticalRes : 240;
            _is24Bit = is24BitDepth;
        }

        public void SetHorizontalRange(ushort displayX1, ushort displayX2)
        {
            _displayX1 = displayX1;
            _displayX2 = displayX2;
        }

        public void SetVRAMStart(ushort displayVRAMXStart, ushort displayVRAMYStart)
        {
            _vramXStart = displayVRAMXStart;
            _vramYStart = displayVRAMYStart;
        }

        public void SetVerticalRange(ushort displayY1, ushort displayY2)
        {
            _displayY1 = displayY1;
            _displayY2 = displayY2;
        }

        public void Play(byte[] samples) => _owner.PushAudio(samples);

        public bool TryGetVerticalPixelRange(out int y1, out int y2)
        {
            y1 = 0;
            y2 = 0;
            if (_displayY2 <= _displayY1)
                return false;

            int timing = (_displayY2 > 300 || _displayY1 > 300) ? 314 : 263;
            double scale = _displayHeight / (double)timing;
            int py1 = (int)Math.Round(_displayY1 * scale);
            int py2 = (int)Math.Round(_displayY2 * scale);

            if (py2 <= py1)
                return false;
            if (py1 < 0) py1 = 0;
            if (py2 > _displayHeight) py2 = _displayHeight;

            int range = py2 - py1;
            if (range >= _displayHeight - 2)
                return false;

            y1 = py1;
            y2 = py2;
            return true;
        }

        public bool TryGetSourceViewport(out int sourceX, out int sourceY, out int sourceWidth, out int sourceHeight)
        {
            sourceX = _vramXStart;
            sourceY = _vramYStart;
            sourceWidth = _displayWidth > 0 ? _displayWidth : 320;
            sourceHeight = _displayHeight > 0 ? _displayHeight : 240;

            if (_displayX2 > _displayX1)
            {
                int cyclesPerPixel = GetCyclesPerPixel();
                int displayCycles = _displayX2 - _displayX1;
                int visibleWidth = (((displayCycles / cyclesPerPixel) + 2) & ~3);
                if (visibleWidth > 0)
                    sourceWidth = visibleWidth;
            }

            if (_displayY2 > _displayY1)
            {
                int visibleHeight = _displayY2 - _displayY1;
                if (_displayHeight >= 480 && visibleHeight <= 288)
                    visibleHeight *= 2;
                if (visibleHeight > 0)
                    sourceHeight = visibleHeight;
            }

            if (sourceWidth <= 0 || sourceHeight <= 0)
                return false;

            if (sourceX < 0) sourceX = 0;
            if (sourceY < 0) sourceY = 0;
            int maxWidth = 1024 - sourceX;
            if (_is24Bit)
                maxWidth = (2048 - (sourceX * 2)) / 3;
            if (sourceWidth > maxWidth)
                sourceWidth = Math.Max(0, maxWidth);
            if (sourceY + sourceHeight > 512)
                sourceHeight = Math.Max(0, 512 - sourceY);

            return sourceWidth > 0 && sourceHeight > 0;
        }

        public double GetPresentationAspectRatio()
        {
            if (_displayX2 <= _displayX1 || _displayWidth <= 0)
                return DefaultAspectRatio;

            int displayCycles = _displayX2 - _displayX1;
            int nominalCycles = _displayWidth * GetCyclesPerPixel();
            if (displayCycles <= 0 || nominalCycles <= 0)
                return DefaultAspectRatio;

            double aspect = DefaultAspectRatio * (displayCycles / (double)nominalCycles);
            return aspect > 0.1 ? aspect : DefaultAspectRatio;
        }

        private int GetCyclesPerPixel() => _displayWidth switch
        {
            256 => 10,
            320 => 8,
            384 => 7,
            512 => 5,
            640 => 4,
            _ => 8
        };
    }

    private ProjectPSX.ProjectPSX? _core;
    private PsxHostWindow? _host;
    private string? _diskPath;
    private readonly object _stateLock = new();
    private byte[] _frameBuffer = new byte[320 * 240 * 4];
    private byte[] _frameSnapshotBuffer = new byte[320 * 240 * 4];
    private int _frameWidth = 320;
    private int _frameHeight = 240;
    private int _frameStride = 320 * 4;
    private readonly object _audioLock = new();
    private short[] _audioQueue = Array.Empty<short>();
    private int _audioQueuedCount;
    private short[] _audioReadBuffer = Array.Empty<short>();
    private float _masterVolumeScale = 1.0f;
    private long _frameCounter;

    public void LoadRom(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            throw new FileNotFoundException("PSX image not found.", path);

        _diskPath = path;
        if (!string.IsNullOrWhiteSpace(BiosPath))
            Environment.SetEnvironmentVariable("EUTHERDRIVE_PSX_BIOS", BiosPath);
        _host = new PsxHostWindow(this);
        _core = new ProjectPSX.ProjectPSX(_host, path, AnalogControllerEnabled, FastLoadEnabled);
    }

    public void Reset()
    {
        if (string.IsNullOrWhiteSpace(_diskPath))
            return;
        LoadRom(_diskPath);
    }

    public void RunFrame()
    {
        lock (_stateLock)
        {
            _core?.RunFrame();
            _frameCounter++;
        }
    }

    public ReadOnlySpan<byte> GetFrameBuffer(out int width, out int height, out int stride)
    {
        lock (_stateLock)
        {
            int required = _frameStride * _frameHeight;
            if (_frameBuffer.Length != required)
                _frameBuffer = new byte[Math.Max(required, 0)];
            if (_frameSnapshotBuffer.Length != required)
                _frameSnapshotBuffer = new byte[Math.Max(required, 0)];

            Array.Copy(_frameBuffer, _frameSnapshotBuffer, required);

            width = _frameWidth;
            height = _frameHeight;
            stride = _frameStride;
            return _frameSnapshotBuffer;
        }
    }

    public bool TryGetPresentationSize(out double width, out double height)
    {
        width = 0;
        height = 0;
        if (_frameHeight <= 0)
            return false;

        double aspect = _host?.GetPresentationAspectRatio() ?? (4.0 / 3.0);
        width = Math.Round(_frameHeight * aspect);
        height = _frameHeight;
        return width > 0 && height > 0;
    }

    public ReadOnlySpan<short> GetAudioBuffer(out int sampleRate, out int channels)
    {
        sampleRate = 44100;
        channels = 2;
        lock (_audioLock)
        {
            if (_audioQueuedCount == 0)
                return ReadOnlySpan<short>.Empty;

            if (_audioReadBuffer.Length != _audioQueuedCount)
                _audioReadBuffer = new short[_audioQueuedCount];

            _audioQueue.AsSpan(0, _audioQueuedCount).CopyTo(_audioReadBuffer);
            _audioQueuedCount = 0;
            return _audioReadBuffer;
        }
    }

    public void SetMasterVolumePercent(int percent)
    {
        if (percent < 0)
            percent = 0;
        else if (percent > 100)
            percent = 100;
        _masterVolumeScale = percent / 100f;
    }

    public void SetAnalogControllerEnabled(bool enabled)
    {
        AnalogControllerEnabled = enabled;
        _core?.SetAnalogControllerEnabled(enabled);
    }

    public void SetFastLoadEnabled(bool enabled)
    {
        FastLoadEnabled = enabled;
        _core?.SetFastLoadEnabled(enabled);
    }

    public bool TryGetDebugState(out string state)
    {
        lock (_stateLock)
        {
            if (_core == null)
            {
                state = string.Empty;
                return false;
            }

            state = _core.DebugStartSummary();
            return true;
        }
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
        SetButton(GamepadInputsEnum.S, a);     // Cross
        SetButton(GamepadInputsEnum.D, b);     // Circle
        SetButton(GamepadInputsEnum.A, c);     // Square
        SetButton(GamepadInputsEnum.Enter, start);
        SetButton(GamepadInputsEnum.W, x);     // Triangle
        SetButton(GamepadInputsEnum.Q, y);     // L1
        SetButton(GamepadInputsEnum.E, z);     // R1
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

    private void UpdateFrame(int[] vram, ushort[] vram1555, int width, int height, ushort vramX, ushort vramY, bool is24Bit)
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
        if (is24Bit)
        {
            UpdateFrame24(vram1555, width, height, baseX, baseY, vramWidth, vramHeight);
            return;
        }

        for (int y = 0; y < height; y++)
        {
            int dstRow = y * stride;
            int srcY = baseY + y;
            if ((uint)srcY >= (uint)vramHeight)
            {
                for (int x = 0; x < width; x++)
                {
                    int o = dstRow + (x << 2);
                    _frameBuffer[o + 0] = 0;
                    _frameBuffer[o + 1] = 0;
                    _frameBuffer[o + 2] = 0;
                    _frameBuffer[o + 3] = 0xFF;
                }
                continue;
            }

            int srcRow = srcY * vramWidth;
            for (int x = 0; x < width; x++)
            {
                int srcX = baseX + x;
                int color = 0;
                if ((uint)srcX < (uint)vramWidth)
                    color = vram[srcRow + srcX];
                int o = dstRow + (x << 2);
                _frameBuffer[o + 0] = (byte)(color & 0xFF);
                _frameBuffer[o + 1] = (byte)((color >> 8) & 0xFF);
                _frameBuffer[o + 2] = (byte)((color >> 16) & 0xFF);
                _frameBuffer[o + 3] = 0xFF;
            }
        }
    }

    private void UpdateFrame24(ushort[] vram1555, int width, int height, int baseX, int baseY, int vramWidth, int vramHeight)
    {
        for (int y = 0; y < height; y++)
        {
            int dstRow = y * _frameStride;
            int srcY = baseY + y;
            if ((uint)srcY >= (uint)vramHeight)
            {
                ClearFrameRow(dstRow, width);
                continue;
            }

            int rowBase = srcY * vramWidth;
            int rowByteBase = baseX * 2;
            for (int x = 0; x < width; x++)
            {
                int pixelByteIndex = rowByteBase + (x * 3);
                int maxBytes = vramWidth * 2;
                int o = dstRow + (x << 2);
                if (pixelByteIndex + 2 >= maxBytes)
                {
                    _frameBuffer[o + 0] = 0;
                    _frameBuffer[o + 1] = 0;
                    _frameBuffer[o + 2] = 0;
                    _frameBuffer[o + 3] = 0xFF;
                    continue;
                }

                byte r = ReadVramByte(vram1555, rowBase, pixelByteIndex);
                byte g = ReadVramByte(vram1555, rowBase, pixelByteIndex + 1);
                byte b = ReadVramByte(vram1555, rowBase, pixelByteIndex + 2);

                _frameBuffer[o + 0] = b;
                _frameBuffer[o + 1] = g;
                _frameBuffer[o + 2] = r;
                _frameBuffer[o + 3] = 0xFF;
            }
        }
    }

    private static byte ReadVramByte(ushort[] vram1555, int rowBase, int byteIndex)
    {
        int wordOffset = byteIndex >> 1;
        ushort word = vram1555[rowBase + wordOffset];
        return (byte)(((byteIndex & 1) == 0) ? (word & 0xFF) : (word >> 8));
    }

    private void ClearFrameRow(int dstRow, int width)
    {
        for (int x = 0; x < width; x++)
        {
            int o = dstRow + (x << 2);
            _frameBuffer[o + 0] = 0;
            _frameBuffer[o + 1] = 0;
            _frameBuffer[o + 2] = 0;
            _frameBuffer[o + 3] = 0xFF;
        }
    }

    private void PushAudio(byte[] samples)
    {
        int sampleCount = samples.Length / 2;
        if (sampleCount == 0)
            return;

        lock (_audioLock)
        {
            int needed = _audioQueuedCount + sampleCount;
            if (_audioQueue.Length < needed)
            {
                int newSize = Math.Max(needed, _audioQueue.Length == 0 ? sampleCount : _audioQueue.Length * 2);
                Array.Resize(ref _audioQueue, newSize);
            }

            int si = 0;
            int di = _audioQueuedCount;
            for (int i = 0; i < sampleCount; i++)
            {
                short raw = (short)(samples[si] | (samples[si + 1] << 8));
                if (_masterVolumeScale != 1.0f)
                {
                    int scaled = (int)(raw * _masterVolumeScale);
                    if (scaled > short.MaxValue)
                        scaled = short.MaxValue;
                    else if (scaled < short.MinValue)
                        scaled = short.MinValue;
                    _audioQueue[di++] = (short)scaled;
                }
                else
                {
                    _audioQueue[di++] = raw;
                }
                si += 2;
            }
            _audioQueuedCount = di;
        }
    }
}
