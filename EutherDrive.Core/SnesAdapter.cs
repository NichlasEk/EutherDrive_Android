using KSNES.AudioProcessing;
using KSNES.CPU;
using KSNES.PictureProcessing;
using KSNES.Rendering;
using KSNES.ROM;
using KSNES.SNESSystem;
using System.Diagnostics;

namespace EutherDrive.Core;

public sealed class SnesAdapter : IEmulatorCore
{
    private const int DefaultWidth = 256;
    private const int DefaultHeight = 224;
    private const int DefaultStride = DefaultWidth * 4;

    private readonly SNESSystem _system;
    private readonly SnesAudioHandler _audioHandler = new();
    private readonly SnesFrameRenderer _renderer = new();
    private byte[] _frameBuffer = new byte[DefaultHeight * DefaultStride];
    private short[] _audioBuffer = Array.Empty<short>();
    private string? _romSummary;
    private long _lastAudioLogTicks;
    private bool _traceAudio;
    private bool _lowpassEnabled;
    private ConsoleRegion _romRegionHint = ConsoleRegion.Auto;
    private byte? _romRegionCode;
    private ConsoleRegion _regionOverride = ConsoleRegion.Auto;
    private float _dcLastInL;
    private float _dcLastOutL;
    private float _dcLastInR;
    private float _dcLastOutR;
    private float _lpLastL;
    private float _lpLastR;
    private const float DcBlockCoeff = 0.995f;

    public string? RomSummary => _romSummary;
    public ConsoleRegion RomRegionHint => _romRegionHint;
    public byte? RomRegionCode => _romRegionCode;

    public SnesAdapter()
    {
        var cpu = new CPU();
        var ppu = new PPU();
        var rom = new ROM();
        var apu = new APU(new SPC700(), new DSP());
        _system = new SNESSystem(cpu, _renderer, rom, ppu, apu, _audioHandler);
        _traceAudio = Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_SNES_AUDIO") == "1";
        _lowpassEnabled = Environment.GetEnvironmentVariable("EUTHERDRIVE_SNES_AUDIO_LOWPASS") == "1";
    }

    public void LoadRom(string path)
    {
        _system.LoadROMForExternal(path);
        DetectRegion(path);
        UpdateIsPal();
        _romSummary = BuildRomSummary();
    }

    public void Reset()
    {
        _system.ResetForExternal();
    }

    public void SetRegionOverride(ConsoleRegion region)
    {
        _regionOverride = region;
        UpdateIsPal();
    }

    public void RunFrame()
    {
        _system.RunFrameForExternal();
        int[] pixels = _system.PPU.GetPixels();
        EnsureFrameBuffer();
        ConvertArgbToBgra(pixels, _frameBuffer);
        EnsureAudioBuffer();
        ConvertFloatToPcm(_audioHandler.SampleBufferL, _audioHandler.SampleBufferR, _audioBuffer);
        TraceAudioIfEnabled();
    }

    public ReadOnlySpan<byte> GetFrameBuffer(out int width, out int height, out int stride)
    {
        width = DefaultWidth;
        height = DefaultHeight;
        stride = DefaultStride;
        return _frameBuffer;
    }

    public ReadOnlySpan<short> GetAudioBuffer(out int sampleRate, out int channels)
    {
        sampleRate = SnesAudioHandler.SampleRate;
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
        SetButton(SNESButton.Up, up);
        SetButton(SNESButton.Down, down);
        SetButton(SNESButton.Left, left);
        SetButton(SNESButton.Right, right);
        SetButton(SNESButton.A, a);
        SetButton(SNESButton.B, b);
        SetButton(SNESButton.X, x);
        SetButton(SNESButton.Y, y);
        SetButton(SNESButton.L, z);
        SetButton(SNESButton.R, c);
        SetButton(SNESButton.Start, start);
        SetButton(SNESButton.Sel, mode);
    }

    public void SetInputState2(
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
        bool mode)
    {
        SetButton2(SNESButton.Up, up);
        SetButton2(SNESButton.Down, down);
        SetButton2(SNESButton.Left, left);
        SetButton2(SNESButton.Right, right);
        SetButton2(SNESButton.A, a);
        SetButton2(SNESButton.B, b);
        SetButton2(SNESButton.X, x);
        SetButton2(SNESButton.Y, y);
        SetButton2(SNESButton.L, z);
        SetButton2(SNESButton.R, c);
        SetButton2(SNESButton.Start, start);
        SetButton2(SNESButton.Sel, mode);
    }

    private void SetButton(SNESButton button, bool down)
    {
        if (down)
            _system.SetKeyDown(button);
        else
            _system.SetKeyUp(button);
    }

    private void SetButton2(SNESButton button, bool down)
    {
        if (down)
            _system.SetKeyDown2(button);
        else
            _system.SetKeyUp2(button);
    }

    private void EnsureFrameBuffer()
    {
        int needed = DefaultHeight * DefaultStride;
        if (_frameBuffer.Length < needed)
            _frameBuffer = new byte[needed];
    }

    private void EnsureAudioBuffer()
    {
        int needed = SnesAudioHandler.SamplesPerFrame * 2;
        if (_audioBuffer.Length != needed)
            _audioBuffer = new short[needed];
    }

    private void ConvertFloatToPcm(float[] left, float[] right, short[] dest)
    {
        int count = Math.Min(left.Length, right.Length);
        int di = 0;
        for (int i = 0; i < count && di + 1 < dest.Length; i++)
        {
            float l = ApplyDcBlock(left[i], ref _dcLastInL, ref _dcLastOutL);
            float r = ApplyDcBlock(right[i], ref _dcLastInR, ref _dcLastOutR);
            if (_lowpassEnabled)
            {
                l = ApplyLowpass(l, ref _lpLastL);
                r = ApplyLowpass(r, ref _lpLastR);
            }
            dest[di++] = FloatToShort(l);
            dest[di++] = FloatToShort(r);
        }
    }

    private static short FloatToShort(float value)
    {
        float clamped = Math.Clamp(value, -1f, 1f);
        return (short)MathF.Round(clamped * short.MaxValue);
    }

    private static float ApplyDcBlock(float input, ref float lastInput, ref float lastOutput)
    {
        float output = input - lastInput + (DcBlockCoeff * lastOutput);
        lastInput = input;
        lastOutput = output;
        return output;
    }

    private static float ApplyLowpass(float input, ref float last)
    {
        const float alpha = 0.2f;
        float output = last + alpha * (input - last);
        last = output;
        return output;
    }

    private void TraceAudioIfEnabled()
    {
        if (!_traceAudio)
            return;
        long now = Stopwatch.GetTimestamp();
        if (_lastAudioLogTicks != 0)
        {
            double elapsed = (now - _lastAudioLogTicks) / (double)Stopwatch.Frequency;
            if (elapsed < 1.0)
                return;
        }
        _lastAudioLogTicks = now;
        int peak = 0;
        for (int i = 0; i < _audioBuffer.Length; i++)
        {
            int v = _audioBuffer[i];
            if (v < 0) v = -v;
            if (v > peak) peak = v;
        }
        Console.WriteLine($"[SNES-AUDIO] peak={peak} samples={_audioBuffer.Length}");
    }

    private string BuildRomSummary()
    {
        var header = _system.ROM.Header;
        string name = header.Name?.Trim() ?? "Unknown";
        string romSize = $"{header.RomSize / 1024} KB";
        string ramSize = $"{header.RamSize / 1024} KB";
        string region = _romRegionHint == ConsoleRegion.Auto ? "?" : _romRegionHint.ToString();
        return $"SNES: {name} | ROM {romSize} | RAM {ramSize} | Region {region} | Speed 0x{header.Speed:X} | Type 0x{header.Type:X} | Chips 0x{header.Chips:X}";
    }

    public double GetTargetFps(ConsoleRegion overrideRegion)
    {
        ConsoleRegion region = overrideRegion == ConsoleRegion.Auto ? _romRegionHint : overrideRegion;
        return region == ConsoleRegion.EU ? 50.0 : 60.0;
    }

    private void DetectRegion(string path)
    {
        try
        {
            byte[] data = File.ReadAllBytes(path);
            int headerOffset = GetHeaderOffset(data);
            if (headerOffset < 0 || headerOffset + 0x1A >= data.Length)
            {
                _romRegionHint = ConsoleRegion.Auto;
                _romRegionCode = null;
                return;
            }

            byte region = data[headerOffset + 0x19];
            _romRegionCode = region;
            _romRegionHint = MapRegion(region);
        }
        catch
        {
            _romRegionHint = ConsoleRegion.Auto;
            _romRegionCode = null;
        }
    }

    private void UpdateIsPal()
    {
        ConsoleRegion region = _regionOverride == ConsoleRegion.Auto ? _romRegionHint : _regionOverride;
        _system.IsPal = region == ConsoleRegion.EU;
    }

    private static int GetHeaderOffset(byte[] data)
    {
        int headerBase = 0;
        if (data.Length % 0x8000 == 512)
            headerBase = 512;

        if (LooksLikeSnesHeader(data, headerBase + 0x7FC0))
            return headerBase + 0x7FC0;
        if (LooksLikeSnesHeader(data, headerBase + 0xFFC0))
            return headerBase + 0xFFC0;
        return -1;
    }

    private static bool LooksLikeSnesHeader(byte[] data, int offset)
    {
        if (offset < 0 || offset + 21 > data.Length)
            return false;
        int printable = 0;
        for (int i = 0; i < 21; i++)
        {
            byte b = data[offset + i];
            if (b >= 0x20 && b <= 0x7E)
                printable++;
        }
        return printable >= 10;
    }

    private static ConsoleRegion MapRegion(byte code)
    {
        return code switch
        {
            0x00 => ConsoleRegion.JP,
            0x01 => ConsoleRegion.US,
            0x02 => ConsoleRegion.EU,
            0x03 => ConsoleRegion.EU,
            0x04 => ConsoleRegion.EU,
            0x05 => ConsoleRegion.EU,
            0x06 => ConsoleRegion.EU,
            0x0A => ConsoleRegion.EU,
            0x0B => ConsoleRegion.EU,
            0x0C => ConsoleRegion.EU,
            _ => ConsoleRegion.US
        };
    }

    private static void ConvertArgbToBgra(int[] source, byte[] dest)
    {
        int di = 0;
        for (int i = 0; i < source.Length; i++)
        {
            uint argb = unchecked((uint)source[i]);
            dest[di + 0] = (byte)argb;         // B
            dest[di + 1] = (byte)(argb >> 8);  // G
            dest[di + 2] = (byte)(argb >> 16); // R
            dest[di + 3] = (byte)(argb >> 24); // A
            di += 4;
        }
    }

    private sealed class SnesFrameRenderer : IRenderer
    {
        public void RenderBuffer(int[] buffer)
        {
            // Rendering is pulled by the adapter directly from PPU.GetPixels().
        }

        public void SetTargetControl(IHasWidthAndHeight box)
        {
        }
    }

    private sealed class SnesAudioHandler : IAudioHandler
    {
        public const int SamplesPerFrame = 735;
        public const int SampleRate = 44100;

        public float[] SampleBufferL { get; set; } = new float[SamplesPerFrame];
        public float[] SampleBufferR { get; set; } = new float[SamplesPerFrame];

        public void NextBuffer()
        {
        }

        public void Pauze()
        {
        }

        public void Resume()
        {
        }
    }
}
