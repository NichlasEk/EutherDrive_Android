using System;
using System.Globalization;
using System.IO;
using System.Text;
using EutherDrive.Core.Savestates;
using ePceCD;

namespace EutherDrive.Core;

public sealed class PceCdAdapter : IEmulatorCore, IRenderHandler, IAudioHandler, IDisposable, ISavestateCapable
{
    private const int DefaultWidth = 256;
    private const int DefaultHeight = 240;
    private const double DefaultFps = 60.0;
    private const int MaxTicksPerFrame = 200000;

    private readonly BUS _bus;
    public static string? BiosPath { get; set; }
    private byte[] _frameBuffer = new byte[DefaultWidth * DefaultHeight * 4];
    private short[] _audioBuffer = Array.Empty<short>();
    private int _frameWidth = DefaultWidth;
    private int _frameHeight = DefaultHeight;
    private int _frameStride = DefaultWidth * 4;
    private bool _frameReady;
    private float _masterVolumeScale = 1.0f;
    private readonly object _stateLock = new();
    private long _frameCounter;
    private RomIdentity? _romIdentity;
    private string? _romPath;

    public RomIdentity? RomIdentity => _romIdentity;
    public long? FrameCounter => _frameCounter;

    public PceCdAdapter()
    {
        _bus = new BUS(this, this);
    }

    public void LoadRom(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return;

        _romPath = path;
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
            AttachCdSaveRam(path);
            _bus.LoadCue(path);
        }
        else
        {
            bool? swapOverride = null;
            string? swapEnv = Environment.GetEnvironmentVariable("EUTHERDRIVE_PCE_SWAP");
            if (!string.IsNullOrWhiteSpace(swapEnv))
                swapOverride = swapEnv == "1" || swapEnv.Equals("true", StringComparison.OrdinalIgnoreCase);

            bool swap = swapOverride ?? false;
            _bus.LoadRom(path, swap);
            AttachHuCardSaveRam(path);
        }

        Reset();
        try
        {
            byte[] data = File.ReadAllBytes(path);
            _romIdentity = new RomIdentity(Path.GetFileName(path), RomIdentity.ComputeSha256(data));
        }
        catch
        {
            _romIdentity = null;
        }
    }

    public void Reset()
    {
        lock (_stateLock)
        {
            _bus.Reset();
            _frameReady = false;
            _bus.PPU.FrameReady = false;
            _frameCounter = 0;
        }
    }

    public void RunFrame()
    {
        lock (_stateLock)
        {
            _frameReady = false;
            _bus.PPU.FrameReady = false;

            int maxTicksPerFrame = MaxTicksPerFrame;
            string? headlessTicksEnv = Environment.GetEnvironmentVariable("EUTHERDRIVE_PCE_HEADLESS_MAX_TICKS");
            if (!string.IsNullOrWhiteSpace(headlessTicksEnv) && int.TryParse(headlessTicksEnv, out int ticksOverride) && ticksOverride > 0)
                maxTicksPerFrame = ticksOverride;

            int safety = 0;
            while (!_frameReady && safety < maxTicksPerFrame)
            {
                int cycles = _bus.CPU.Step();
                _bus.Clock(cycles);
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
            else
            {
                _frameCounter++;
            }
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
        _bus.JoyPort.SetSixButtonMode(padType == PadType.SixButton);
        _bus.JoyPort.KeyState(PCEKEY.DPadUp, (short)(up ? 0 : 1));
        _bus.JoyPort.KeyState(PCEKEY.DPadDown, (short)(down ? 0 : 1));
        _bus.JoyPort.KeyState(PCEKEY.DPadLeft, (short)(left ? 0 : 1));
        _bus.JoyPort.KeyState(PCEKEY.DPadRight, (short)(right ? 0 : 1));
        _bus.JoyPort.KeyState(PCEKEY.A, (short)(a ? 0 : 1));
        _bus.JoyPort.KeyState(PCEKEY.B, (short)(b ? 0 : 1));
        _bus.JoyPort.KeyState(PCEKEY.C, (short)(c ? 0 : 1));
        _bus.JoyPort.KeyState(PCEKEY.X, (short)(x ? 0 : 1));
        _bus.JoyPort.KeyState(PCEKEY.Y, (short)(y ? 0 : 1));
        _bus.JoyPort.KeyState(PCEKEY.Z, (short)(z ? 0 : 1));
        _bus.JoyPort.KeyState(PCEKEY.Start, (short)(start ? 0 : 1));
        _bus.JoyPort.KeyState(PCEKEY.Select, (short)(mode ? 0 : 1));
    }

    public void SetMasterVolumePercent(int percent)
    {
        if (percent < 0) percent = 0;
        else if (percent > 100) percent = 100;
        _masterVolumeScale = percent / 100f;
    }

    public void SaveState(BinaryWriter writer)
    {
        if (writer == null)
            throw new ArgumentNullException(nameof(writer));

        lock (_stateLock)
        {
            const int version = 1;
            writer.Write(version);
            writer.Write(_frameCounter);
            writer.Write(_masterVolumeScale);
            _bus.ReadySerializable();
            try
            {
                StateBinarySerializer.WriteInto(writer, _bus);
            }
            finally
            {
                _bus.RestoreSerializable();
            }
        }
    }

    public void LoadState(BinaryReader reader)
    {
        if (reader == null)
            throw new ArgumentNullException(nameof(reader));

        lock (_stateLock)
        {
            int version = reader.ReadInt32();
            if (version != 1)
                throw new InvalidDataException($"Unsupported PCE CD savestate version: {version}.");

            _frameCounter = reader.ReadInt64();
            _masterVolumeScale = reader.ReadSingle();
            // Deserialize into a deterministic baseline so unsupported/non-serialized runtime
            // fields don't keep history-dependent values across repeated loads.
            _bus.Reset();
            StateBinarySerializer.ReadInto(reader, _bus);
            _bus.DeSerializable(this, this);
            _frameReady = false;
            _bus.PPU.FrameReady = false;
        }
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

    public string CaptureDebugSnapshot(string? directory = null)
    {
        lock (_stateLock)
        {
            string dir = string.IsNullOrWhiteSpace(directory)
                ? Path.Combine(Environment.CurrentDirectory, "logs", "snapshots")
                : directory;
            Directory.CreateDirectory(dir);

            string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss_fff", CultureInfo.InvariantCulture);
            string prefix = $"pcesnap_{stamp}";
            string basePath = Path.Combine(dir, prefix);

            EnsureFrameBuffer();
            DumpFrameBufferToPpm(Path.Combine(dir, $"{prefix}_screen.ppm"));

            _bus.DumpDebugSnapshot(dir, prefix);

            using (var fs = new FileStream(Path.Combine(dir, $"{prefix}_state_raw.bin"), FileMode.Create, FileAccess.Write, FileShare.Read))
            using (var writer = new BinaryWriter(fs, Encoding.UTF8, leaveOpen: false))
            {
                SaveState(writer);
                writer.Flush();
            }

            using (var writer = new StreamWriter(Path.Combine(dir, $"{prefix}_meta.txt"), false, Encoding.UTF8))
            {
                writer.WriteLine($"stamp={stamp}");
                writer.WriteLine($"frame={_frameCounter}");
                writer.WriteLine($"rom={_romPath ?? string.Empty}");
                writer.WriteLine($"identity={_romIdentity?.HashHex ?? string.Empty}");
            }

            return basePath;
        }
    }

    public string BuildDeterminismTraceLine(long frameIndex)
    {
        lock (_stateLock)
        {
            EnsureFrameBuffer();
            ulong frameHash = ComputeFrameHash(_frameBuffer.AsSpan(0, _frameHeight * _frameStride));
            return _bus.BuildDeterminismTraceLine(frameIndex, frameHash);
        }
    }

    public double GetTargetFps() => DefaultFps;

    public void Dispose()
    {
        _bus.BRAM?.Dispose();
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

    private void DumpFrameBufferToPpm(string path)
    {
        int width = _frameWidth > 0 ? _frameWidth : DefaultWidth;
        int height = _frameHeight > 0 ? _frameHeight : DefaultHeight;
        int stride = _frameStride > 0 ? _frameStride : width * 4;

        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
        using var bw = new BinaryWriter(fs, Encoding.ASCII, leaveOpen: false);
        bw.Write(Encoding.ASCII.GetBytes($"P6\n{width} {height}\n255\n"));

        for (int y = 0; y < height; y++)
        {
            int row = y * stride;
            for (int x = 0; x < width; x++)
            {
                int o = row + x * 4;
                byte b = _frameBuffer[o + 0];
                byte g = _frameBuffer[o + 1];
                byte r = _frameBuffer[o + 2];
                bw.Write(r);
                bw.Write(g);
                bw.Write(b);
            }
        }
    }

    private static ulong ComputeFrameHash(ReadOnlySpan<byte> data)
    {
        const ulong offset = 1469598103934665603ul;
        const ulong prime = 1099511628211ul;
        ulong h = offset;
        for (int i = 0; i < data.Length; i++)
        {
            h ^= data[i];
            h *= prime;
        }
        return h;
    }

    private static string? FindBiosPath()
    {
        if (!string.IsNullOrWhiteSpace(BiosPath) && File.Exists(BiosPath))
            return BiosPath;

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

    private static bool ShouldBitSwapPce(string path)
    {
        try
        {
            byte[] data = File.ReadAllBytes(path);
            if (data.Length < 0x2000)
                return false;

            int headerOffset = (data.Length % 0x2000) == 0x200 ? 0x200 : 0;

            int plainScore = ScorePceEntry(data, headerOffset);
            byte[] swapped = BitSwapCopy(data);
            int swapScore = ScorePceEntry(swapped, headerOffset);

            int plainAscii = ScoreAsciiSequences(data);
            int swapAscii = ScoreAsciiSequences(swapped);

            int asciiThreshold = Math.Max(500, plainAscii / 20);
            bool asciiSwap = swapAscii > plainAscii + asciiThreshold;
            bool asciiPlain = plainAscii > swapAscii + Math.Max(500, swapAscii / 20);
            bool closeScores = Math.Abs(plainScore - swapScore) <= 2;
            string decision = "plain";

            if (plainScore > swapScore)
            {
                decision = "plain";
                Console.WriteLine($"[PCE] Bit-swap detect: path='{path}', len=0x{data.Length:X}, header=0x{headerOffset:X}, plainScore={plainScore}, swapScore={swapScore}, plainAscii={plainAscii}, swapAscii={swapAscii}, choice={decision}");
                return false;
            }
            if (swapScore > plainScore)
            {
                decision = "swap";
                Console.WriteLine($"[PCE] Bit-swap detect: path='{path}', len=0x{data.Length:X}, header=0x{headerOffset:X}, plainScore={plainScore}, swapScore={swapScore}, plainAscii={plainAscii}, swapAscii={swapAscii}, choice={decision}");
                return true;
            }

            decision = "plain";
            if (plainScore == 0 && swapScore == 0 && asciiSwap)
                decision = "swap";
            else if (asciiPlain && closeScores)
                decision = "plain";

            Console.WriteLine($"[PCE] Bit-swap detect: path='{path}', len=0x{data.Length:X}, header=0x{headerOffset:X}, plainScore={plainScore}, swapScore={swapScore}, plainAscii={plainAscii}, swapAscii={swapAscii}, choice={decision}");
            if (decision == "swap")
                return true;
            return false;
        }
        catch
        {
            return false;
        }
    }

    private void AttachHuCardSaveRam(string path)
    {
        try
        {
            _bus.BRAM?.Dispose();
            string saveDir = GetSaveDirectory();
            Directory.CreateDirectory(saveDir);
            string name = Path.GetFileNameWithoutExtension(path);
            string savePath = Path.Combine(saveDir, name);
            _bus.AttachBram(new SaveMemoryBank(savePath));
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[PCE] Failed to init save RAM: {ex.Message}");
        }
    }

    private void AttachCdSaveRam(string cuePath)
    {
        try
        {
            _bus.BRAM?.Dispose();
            string saveDir = GetSaveDirectory();
            Directory.CreateDirectory(saveDir);
            string name = Path.GetFileNameWithoutExtension(cuePath);
            string savePath = Path.Combine(saveDir, name);
            _bus.AttachBram(new SaveMemoryBank(savePath));
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[PCE] Failed to init CD save RAM: {ex.Message}");
        }
    }

    private static string GetSaveDirectory()
    {
        string? overrideDir = Environment.GetEnvironmentVariable("EUTHERDRIVE_PCE_SAVE_DIR");
        if (!string.IsNullOrWhiteSpace(overrideDir))
            return overrideDir;
        return Path.Combine(Directory.GetCurrentDirectory(), "saves", "pce");
    }

    private static int ScorePceEntry(byte[] data, int offset)
    {
        if (offset < 0 || data.Length < offset + 0x2000)
            return 0;
        int vec = data[offset + 0x1FFC] | (data[offset + 0x1FFD] << 8);
        if (vec < 0xE000 || vec > 0xFFFF)
            return 0;

        int entryIndex = offset + (vec & 0x1FFF);
        if (entryIndex < 0 || entryIndex >= data.Length)
            return 0;

        byte op0 = data[entryIndex];
        int score = 1;

        if (op0 is 0x78 or 0xD8 or 0xA9 or 0xA2 or 0xA0 or 0x20 or 0x4C or 0xEA or 0x9A)
            score += 2;

        if (entryIndex + 1 < data.Length)
        {
            byte op1 = data[entryIndex + 1];
            if (op1 is 0xD8 or 0xA2 or 0xA9 or 0xA0 or 0x9A or 0x20 or 0x4C)
                score += 1;
        }

        int maxCheck = Math.Min(16, data.Length - entryIndex);
        for (int i = 0; i < maxCheck; i++)
        {
            byte op = data[entryIndex + i];
            if (op is 0x78 or 0xD8 or 0x58 or 0x18 or 0x38 or 0x9A or 0xA9 or 0xA2 or 0xA0 or 0x20 or 0x4C or 0xEA or 0xAD or 0xAE or 0xAC or 0xBD or 0xB9)
                score += 1;
        }

        return score;
    }

    private static byte[] BitSwapCopy(byte[] data)
    {
        byte[] result = new byte[data.Length];
        for (int i = 0; i < data.Length; i++)
        {
            byte b = data[i];
            result[i] = (byte)(
                ((b & 0x80) >> 7) |
                ((b & 0x40) >> 5) |
                ((b & 0x20) >> 3) |
                ((b & 0x10) >> 1) |
                ((b & 0x08) << 1) |
                ((b & 0x04) << 3) |
                ((b & 0x02) << 5) |
                ((b & 0x01) << 7));
        }
        return result;
    }

    private static int ScoreAsciiSequences(byte[] data)
    {
        int score = 0;
        int run = 0;
        for (int i = 0; i < data.Length; i++)
        {
            byte b = data[i];
            if (b >= 0x20 && b <= 0x7E)
            {
                run++;
            }
            else
            {
                if (run >= 4)
                    score += run;
                run = 0;
            }
        }

        if (run >= 4)
            score += run;

        return score;
    }
}
