using System;
using System.Diagnostics;
using System.IO;
using System.Threading;

namespace EutherDrive.Core;

public sealed class N64Adapter : IEmulatorCore
{
    private const uint HeaderZ64 = 0x80371240;
    private const uint HeaderN64 = 0x40123780;
    private const uint HeaderV64 = 0x37804012;

    private const int DefaultWidth = 320;
    private const int DefaultHeight = 240;
    private const int DefaultStride = DefaultWidth * 4;

    private readonly Ryu64Core.Ryu64Core _core = new();
    private byte[] _frameBuffer = new byte[DefaultHeight * DefaultStride];
    private short[] _audioBuffer = Array.Empty<short>();
    private int _frameWidth = DefaultWidth;
    private int _frameHeight = DefaultHeight;
    private int _frameStride = DefaultStride;
    private string? _romPath;
    private string? _resolvedRomPath;
    private string? _tempConvertedRomPath;
    private bool _started;
    private float _masterVolumeScale = 1.0f;
    private uint _sampleRate = 44100;
    private uint _channels = 2;
    private long _runFrameCount;
    private long _noFramebufferCount;
    private long _noAudioCount;
    private bool? _swap5551BytesDecision;
    private readonly ulong _targetCyclesPerRunFrame = ReadUlongEnv("EUTHERDRIVE_N64_TARGET_CYCLES_PER_RUNFRAME", 300_000);
    private readonly int _runFrameWaitMs = ReadIntEnv("EUTHERDRIVE_N64_RUNFRAME_WAIT_MS", 12);

    ~N64Adapter()
    {
        CleanupTempRom();
    }

    public void LoadRom(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            throw new FileNotFoundException("N64 ROM not found.", path);

        CleanupTempRom();
        _core.Stop();

        _resolvedRomPath = PrepareRomPathForCore(path);
        _core.LoadROM(_resolvedRomPath);
        _romPath = path;
        _started = false;
        _audioBuffer = Array.Empty<short>();
        _runFrameCount = 0;
        _noFramebufferCount = 0;
        _noAudioCount = 0;
        _swap5551BytesDecision = null;
        EnsureFrameBuffer(DefaultWidth, DefaultHeight);
        Console.WriteLine($"[N64Adapter] ROM loaded: '{Path.GetFileName(path)}' -> '{Path.GetFileName(_resolvedRomPath)}'");
    }

    public void Reset()
    {
        if (string.IsNullOrWhiteSpace(_romPath))
            return;

        _core.Stop();
        if (string.IsNullOrWhiteSpace(_resolvedRomPath) || !File.Exists(_resolvedRomPath))
            _resolvedRomPath = PrepareRomPathForCore(_romPath);
        _core.LoadROM(_resolvedRomPath);
        _started = false;
        _audioBuffer = Array.Empty<short>();
        _runFrameCount = 0;
        _noFramebufferCount = 0;
        _noAudioCount = 0;
        _swap5551BytesDecision = null;
    }

    public void RunFrame()
    {
        EnsureStarted();
        _runFrameCount++;
        WaitForCpuProgress();
        PullFrame();
        PullAudio();
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
        sampleRate = (int)_sampleRate;
        channels = (int)_channels;
        return _audioBuffer;
    }

    public void SetMasterVolumePercent(int percent)
    {
        if (percent < 0)
            percent = 0;
        else if (percent > 100)
            percent = 100;

        _masterVolumeScale = percent / 100f;
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

        var input = new Ryu64Core.InputState
        {
            Up = up,
            Down = down,
            Left = left,
            Right = right,
            A = a,
            B = b,
            Start = start,
            Z = z,
            L = c,
            R = mode,
            CLeft = x,
            CUp = y,
            CRight = false,
            CDown = false,
            StickX = 0,
            StickY = 0
        };

        _core.SetInputState(input);
    }

    private void EnsureStarted()
    {
        if (_started)
            return;

        _core.Start();
        _started = true;
    }

    private void PullFrame()
    {
        if (!_core.TryGetFramebuffer(out var raw, out int width, out int height, out int bytesPerPixel))
        {
            _noFramebufferCount++;
            if (_noFramebufferCount <= 10 || (_noFramebufferCount % 120) == 0)
            {
                Console.WriteLine(
                    $"[N64Adapter] No framebuffer yet (runFrame={_runFrameCount}, miss={_noFramebufferCount}): {_core.LastFramebufferStatus}");
                if (_noFramebufferCount <= 10 || (_noFramebufferCount % 240) == 0)
                    Console.WriteLine($"[N64Adapter] Exec: {_core.LastExecutionStatus}");
            }
            return;
        }

        if (width <= 0 || height <= 0)
        {
            _noFramebufferCount++;
            if (_noFramebufferCount <= 10 || (_noFramebufferCount % 120) == 0)
            {
                Console.WriteLine(
                    $"[N64Adapter] Invalid framebuffer size (runFrame={_runFrameCount}, miss={_noFramebufferCount}) width={width} height={height}");
                if (_noFramebufferCount <= 10 || (_noFramebufferCount % 240) == 0)
                    Console.WriteLine($"[N64Adapter] Exec: {_core.LastExecutionStatus}");
            }
            return;
        }

        if (_noFramebufferCount > 0)
        {
            Console.WriteLine(
                $"[N64Adapter] Framebuffer recovered at runFrame={_runFrameCount} after {_noFramebufferCount} misses " +
                $"(w={width} h={height} bpp={bytesPerPixel}) status={_core.LastFramebufferStatus}");
            _noFramebufferCount = 0;
        }

        EnsureFrameBuffer(width, height);

        if (bytesPerPixel == 4)
        {
            ConvertRgba8888ToBgra(raw, _frameBuffer);
        }
        else
        {
            if (!_swap5551BytesDecision.HasValue)
            {
                var (swap, normalScore, swappedScore) = DetectRgba5551ByteOrder(raw);
                _swap5551BytesDecision = swap;
                Console.WriteLine(
                    $"[N64Adapter] RGBA5551 byte-order auto-detect: swap={swap} scoreNormal={normalScore} scoreSwapped={swappedScore}");
            }

            ConvertRgba5551ToBgra(raw, _frameBuffer, _swap5551BytesDecision.Value);
        }
    }

    private void PullAudio()
    {
        short[] samples = _core.GetAudioSamples(out _sampleRate, out _channels);
        if (samples.Length == 0)
        {
            _noAudioCount++;
            if (_noAudioCount <= 10 || (_noAudioCount % 240) == 0)
            {
                Console.WriteLine(
                    $"[N64Adapter] No audio yet (runFrame={_runFrameCount}, miss={_noAudioCount}) rate={_sampleRate} ch={_channels}");
            }
            _audioBuffer = Array.Empty<short>();
            return;
        }

        if (_noAudioCount > 0)
        {
            Console.WriteLine(
                $"[N64Adapter] Audio recovered at runFrame={_runFrameCount} after {_noAudioCount} misses");
            _noAudioCount = 0;
        }

        if (_masterVolumeScale < 0.999f)
        {
            float scale = _masterVolumeScale;
            for (int i = 0; i < samples.Length; i++)
            {
                int v = (int)Math.Round(samples[i] * scale);
                if (v > short.MaxValue) v = short.MaxValue;
                else if (v < short.MinValue) v = short.MinValue;
                samples[i] = (short)v;
            }
        }

        _audioBuffer = samples;
    }

    private void EnsureFrameBuffer(int width, int height)
    {
        _frameWidth = width;
        _frameHeight = height;
        _frameStride = width * 4;

        int needed = _frameStride * height;
        if (_frameBuffer.Length != needed)
            _frameBuffer = new byte[needed];
    }

    private void WaitForCpuProgress()
    {
        if (_targetCyclesPerRunFrame == 0 || _runFrameWaitMs <= 0)
            return;

        ulong startCycles = _core.GetCycleCounter();
        ulong targetCycles = startCycles + _targetCyclesPerRunFrame;
        var sw = Stopwatch.StartNew();

        while (_core.GetCycleCounter() < targetCycles)
        {
            if (sw.ElapsedMilliseconds >= _runFrameWaitMs)
                break;

            if (sw.ElapsedMilliseconds == 0)
                Thread.SpinWait(512);
            else
                Thread.Sleep(0);
        }

        if (_runFrameCount <= 10 || (_runFrameCount % 240) == 0)
        {
            ulong endCycles = _core.GetCycleCounter();
            ulong deltaCycles = endCycles >= startCycles ? endCycles - startCycles : 0;
            Console.WriteLine(
                $"[N64Adapter] RunFrame pacing: frame={_runFrameCount} cycles+={deltaCycles} target={_targetCyclesPerRunFrame} waitMs={sw.ElapsedMilliseconds}/{_runFrameWaitMs}");
        }
    }

    private static int ReadIntEnv(string name, int fallback)
    {
        string? raw = Environment.GetEnvironmentVariable(name);
        if (int.TryParse(raw, out int value))
            return value;
        return fallback;
    }

    private static ulong ReadUlongEnv(string name, ulong fallback)
    {
        string? raw = Environment.GetEnvironmentVariable(name);
        if (ulong.TryParse(raw, out ulong value))
            return value;
        return fallback;
    }

    private static void ConvertRgba5551ToBgra(ReadOnlySpan<byte> src, Span<byte> dst, bool swapBytes)
    {
        int pixels = Math.Min(src.Length / 2, dst.Length / 4);
        int si = 0;
        int di = 0;

        for (int i = 0; i < pixels; i++)
        {
            ushort p = swapBytes
                ? (ushort)((src[si + 1] << 8) | src[si])
                : (ushort)((src[si] << 8) | src[si + 1]);
            si += 2;

            int r5 = (p >> 11) & 0x1F;
            int g5 = (p >> 6) & 0x1F;
            int b5 = (p >> 1) & 0x1F;
            byte a = (byte)((p & 1) != 0 ? 0xFF : 0x00);

            dst[di + 0] = (byte)((b5 * 255) / 31);
            dst[di + 1] = (byte)((g5 * 255) / 31);
            dst[di + 2] = (byte)((r5 * 255) / 31);
            dst[di + 3] = a;
            di += 4;
        }

        if (di < dst.Length)
            dst[di..].Clear();
    }

    private static (bool Swap, int NormalScore, int SwappedScore) DetectRgba5551ByteOrder(ReadOnlySpan<byte> src)
    {
        int pixels = Math.Min(src.Length / 2, 4096);
        int normalScore = 0;
        int swappedScore = 0;
        int si = 0;

        for (int i = 0; i < pixels; i++)
        {
            ushort pNormal = (ushort)((src[si] << 8) | src[si + 1]);
            ushort pSwapped = (ushort)((src[si + 1] << 8) | src[si]);
            si += 2;

            // Score non-black RGB values (ignore alpha bit).
            if ((pNormal & 0xFFFE) != 0)
                normalScore++;
            if ((pSwapped & 0xFFFE) != 0)
                swappedScore++;
        }

        return (swappedScore > normalScore, normalScore, swappedScore);
    }

    private static void ConvertRgba8888ToBgra(ReadOnlySpan<byte> src, Span<byte> dst)
    {
        int pixels = Math.Min(src.Length / 4, dst.Length / 4);
        int si = 0;
        int di = 0;

        for (int i = 0; i < pixels; i++)
        {
            byte r = src[si + 0];
            byte g = src[si + 1];
            byte b = src[si + 2];
            byte a = src[si + 3];
            si += 4;

            dst[di + 0] = b;
            dst[di + 1] = g;
            dst[di + 2] = r;
            dst[di + 3] = a;
            di += 4;
        }

        if (di < dst.Length)
            dst[di..].Clear();
    }

    private string PrepareRomPathForCore(string originalPath)
    {
        RomByteOrder order = DetectRomByteOrder(originalPath);
        if (order == RomByteOrder.Z64)
            return originalPath;

        byte[] source = File.ReadAllBytes(originalPath);
        byte[] converted = new byte[source.Length];

        if (order == RomByteOrder.N64)
        {
            int i = 0;
            for (; i + 3 < source.Length; i += 4)
            {
                converted[i + 0] = source[i + 3];
                converted[i + 1] = source[i + 2];
                converted[i + 2] = source[i + 1];
                converted[i + 3] = source[i + 0];
            }

            for (; i < source.Length; i++)
                converted[i] = source[i];
        }
        else if (order == RomByteOrder.V64)
        {
            int i = 0;
            for (; i + 1 < source.Length; i += 2)
            {
                converted[i + 0] = source[i + 1];
                converted[i + 1] = source[i + 0];
            }

            if (i < source.Length)
                converted[i] = source[i];
        }
        else
        {
            throw new InvalidDataException("Unsupported N64 ROM byte order. Expected .z64/.n64/.v64 header.");
        }

        string name = Path.GetFileNameWithoutExtension(originalPath);
        string tempPath = Path.Combine(Path.GetTempPath(), $"eutherdrive_n64_{name}_{Guid.NewGuid():N}.z64");
        File.WriteAllBytes(tempPath, converted);
        _tempConvertedRomPath = tempPath;
        return tempPath;
    }

    private static RomByteOrder DetectRomByteOrder(string path)
    {
        using var stream = File.OpenRead(path);
        if (stream.Length < 4)
            return RomByteOrder.Unknown;

        byte[] header = new byte[4];
        int read = stream.Read(header, 0, 4);
        if (read < 4)
            return RomByteOrder.Unknown;

        uint value = ((uint)header[0] << 24)
            | ((uint)header[1] << 16)
            | ((uint)header[2] << 8)
            | header[3];

        return value switch
        {
            HeaderZ64 => RomByteOrder.Z64,
            HeaderN64 => RomByteOrder.N64,
            HeaderV64 => RomByteOrder.V64,
            _ => RomByteOrder.Unknown
        };
    }

    private void CleanupTempRom()
    {
        if (string.IsNullOrWhiteSpace(_tempConvertedRomPath))
            return;

        try
        {
            if (File.Exists(_tempConvertedRomPath))
                File.Delete(_tempConvertedRomPath);
        }
        catch
        {
            // Best-effort cleanup.
        }
        finally
        {
            _tempConvertedRomPath = null;
        }
    }

    private enum RomByteOrder
    {
        Unknown,
        Z64,
        N64,
        V64
    }
}
