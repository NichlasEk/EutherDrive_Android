using System;
using System.IO;
using ePceCD;

namespace EutherDrive.Core;

public sealed class PceCdAdapter : IEmulatorCore, IRenderHandler, IAudioHandler, IDisposable
{
    private const int DefaultWidth = 256;
    private const int DefaultHeight = 240;
    private const double DefaultFps = 60.0;
    private const int MaxTicksPerFrame = 4000;

    private readonly BUS _bus;
    private byte[] _frameBuffer = new byte[DefaultWidth * DefaultHeight * 4];
    private short[] _audioBuffer = Array.Empty<short>();
    private int _frameWidth = DefaultWidth;
    private int _frameHeight = DefaultHeight;
    private int _frameStride = DefaultWidth * 4;
    private bool _frameReady;
    private float _masterVolumeScale = 1.0f;

    public PceCdAdapter()
    {
        _bus = new BUS(this, this);
    }

    public void LoadRom(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return;

        string ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext == ".cue")
        {
            string? biosPath = FindBiosPath();
            if (!string.IsNullOrWhiteSpace(biosPath))
            {
                _bus.LoadRom(biosPath, swap: false);
            }
            else
            {
                Console.WriteLine("[PCE] BIOS not found in ./bios (expected System Card).");
            }
            _bus.LoadCue(path);
        }
        else
        {
            bool swap = Environment.GetEnvironmentVariable("EUTHERDRIVE_PCE_SWAP") == "1";
            _bus.LoadRom(path, swap);
        }

        Reset();
    }

    public void Reset()
    {
        _bus.Reset();
        _frameReady = false;
        _bus.PPU.FrameReady = false;
    }

    public void RunFrame()
    {
        _frameReady = false;
        _bus.PPU.FrameReady = false;

        int safety = 0;
        while (!_frameReady && safety < MaxTicksPerFrame)
        {
            int cycles = _bus.tick();
            _bus.CPU.m_Clock += cycles;
            _bus.CPU.cycle();
            safety++;
        }

        int samplesPerFrame = GetSamplesPerFrame();
        EnsureAudioBuffer(samplesPerFrame * 2);
        _bus.APU.GetSamples(_audioBuffer, _audioBuffer.Length);
        ApplyMasterVolume(_audioBuffer);

        if (!_frameReady)
        {
            EnsureFrameBuffer();
            Array.Clear(_frameBuffer, 0, _frameBuffer.Length);
        }
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
        sampleRate = _bus.APU.m_SampleRate;
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
        _bus.JoyPort.KeyState(PCEKEY.DPadUp, (short)(up ? 0 : 1));
        _bus.JoyPort.KeyState(PCEKEY.DPadDown, (short)(down ? 0 : 1));
        _bus.JoyPort.KeyState(PCEKEY.DPadLeft, (short)(left ? 0 : 1));
        _bus.JoyPort.KeyState(PCEKEY.DPadRight, (short)(right ? 0 : 1));
        _bus.JoyPort.KeyState(PCEKEY.A, (short)(a ? 0 : 1));
        _bus.JoyPort.KeyState(PCEKEY.B, (short)(b ? 0 : 1));
        _bus.JoyPort.KeyState(PCEKEY.Start, (short)(start ? 0 : 1));
        _bus.JoyPort.KeyState(PCEKEY.Select, (short)(mode ? 0 : 1));
    }

    public void SetMasterVolumePercent(int percent)
    {
        if (percent < 0) percent = 0;
        else if (percent > 100) percent = 100;
        _masterVolumeScale = percent / 100f;
    }

    public void RenderFrame(int[] pixels, int width, int height)
    {
        if (width <= 0 || height <= 0)
            return;

        _frameWidth = width;
        _frameHeight = height;
        _frameStride = width * 4;
        EnsureFrameBuffer();

        int count = Math.Min(pixels.Length, width * height);
        int dst = 0;
        for (int i = 0; i < count; i++)
        {
            int argb = pixels[i];
            _frameBuffer[dst + 0] = (byte)(argb & 0xFF);
            _frameBuffer[dst + 1] = (byte)((argb >> 8) & 0xFF);
            _frameBuffer[dst + 2] = (byte)((argb >> 16) & 0xFF);
            _frameBuffer[dst + 3] = (byte)((argb >> 24) & 0xFF);
            dst += 4;
        }

        _frameReady = true;
    }

    public void PlaySamples(short[] samples)
    {
        // Unused. Audio is pulled explicitly via GetSamples().
    }

    public double GetTargetFps() => DefaultFps;

    public void Dispose()
    {
        _bus.PPU.Dispose();
    }

    private int GetSamplesPerFrame()
    {
        return (int)Math.Round(_bus.APU.m_SampleRate / DefaultFps);
    }

    private void EnsureFrameBuffer()
    {
        int needed = _frameHeight * _frameStride;
        if (_frameBuffer.Length < needed)
            _frameBuffer = new byte[needed];
    }

    private void EnsureAudioBuffer(int neededSamples)
    {
        if (_audioBuffer.Length != neededSamples)
            _audioBuffer = new short[neededSamples];
    }

    private void ApplyMasterVolume(short[] buffer)
    {
        if (buffer.Length == 0)
            return;
        if (_masterVolumeScale >= 0.999f)
            return;

        float scale = _masterVolumeScale;
        for (int i = 0; i < buffer.Length; i++)
        {
            int v = (int)(buffer[i] * scale);
            if (v > short.MaxValue) v = short.MaxValue;
            else if (v < short.MinValue) v = short.MinValue;
            buffer[i] = (short)v;
        }
    }

    private static string? FindBiosPath()
    {
        string biosDir = Path.Combine(Directory.GetCurrentDirectory(), "bios");
        if (!Directory.Exists(biosDir))
            return null;

        string[] candidates =
        {
            "syscard3.pce",
            "syscard2.pce",
            "syscard1.pce",
            "systemcard.pce",
            "bios.pce",
            "syscard3.bin",
            "syscard2.bin",
            "syscard1.bin",
            "systemcard.bin",
            "bios.bin"
        };

        foreach (string name in candidates)
        {
            string path = Path.Combine(biosDir, name);
            if (File.Exists(path))
                return path;
        }

        return null;
    }
}
