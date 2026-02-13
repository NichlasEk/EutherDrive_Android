using KSNES.AudioProcessing;
using KSNES.CPU;
using KSNES.PictureProcessing;
using KSNES.Rendering;
using KSNES.ROM;
using KSNES.SNESSystem;

namespace EutherDrive.Core;

public sealed class SnesAdapter : IEmulatorCore
{
    private const int DefaultWidth = 256;
    private const int DefaultHeight = 224;
    private const int DefaultStride = DefaultWidth * 4;

    private readonly SNESSystem _system;
    private readonly SnesNullAudioHandler _audioHandler = new();
    private readonly SnesFrameRenderer _renderer = new();
    private byte[] _frameBuffer = new byte[DefaultHeight * DefaultStride];
    private string? _romSummary;

    public string? RomSummary => _romSummary;

    public SnesAdapter()
    {
        var cpu = new CPU();
        var ppu = new PPU();
        var rom = new ROM();
        var apu = new APU(new SPC700(), new DSP());
        _system = new SNESSystem(cpu, _renderer, rom, ppu, apu, _audioHandler);
    }

    public void LoadRom(string path)
    {
        _system.LoadROMForExternal(path);
        _romSummary = BuildRomSummary();
    }

    public void Reset()
    {
        _system.ResetForExternal();
    }

    public void RunFrame()
    {
        _system.RunFrameForExternal();
        int[] pixels = _system.PPU.GetPixels();
        EnsureFrameBuffer();
        ConvertArgbToBgra(pixels, _frameBuffer);
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
        sampleRate = 0;
        channels = 0;
        return ReadOnlySpan<short>.Empty;
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

    private void SetButton(SNESButton button, bool down)
    {
        if (down)
            _system.SetKeyDown(button);
        else
            _system.SetKeyUp(button);
    }

    private void EnsureFrameBuffer()
    {
        int needed = DefaultHeight * DefaultStride;
        if (_frameBuffer.Length < needed)
            _frameBuffer = new byte[needed];
    }

    private string BuildRomSummary()
    {
        var header = _system.ROM.Header;
        string name = header.Name?.Trim() ?? "Unknown";
        string romSize = $"{header.RomSize / 1024} KB";
        string ramSize = $"{header.RamSize / 1024} KB";
        return $"SNES: {name} | ROM {romSize} | RAM {ramSize} | Speed 0x{header.Speed:X} | Type 0x{header.Type:X} | Chips 0x{header.Chips:X}";
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

    private sealed class SnesNullAudioHandler : IAudioHandler
    {
        public float[] SampleBufferL { get; set; } = Array.Empty<float>();
        public float[] SampleBufferR { get; set; } = Array.Empty<float>();

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
