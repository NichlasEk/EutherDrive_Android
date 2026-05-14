namespace EutherDrive.Core.Arcade.Toaplan;

using EutherDrive.Core.Arcade.Cps1;
using EutherDrive.Core.Cpu.V25Emu;
using EutherDrive.Core.Savestates;
using SharpCompress.Archives;

public sealed class BatsugunAdapter : IEmulatorCore, ISavestateCapable, IDisposable
{
    private static readonly HashSet<string> SupportedDrivers = new(StringComparer.OrdinalIgnoreCase)
    {
        "batsugunsp"
    };

    private static readonly string[] RequiredSpecialSetEntries =
    {
        "tp-030sp.u69",
        "tp030_2.bin",
        "tp030_3l.bin",
        "tp030_3h.bin",
        "tp030_4l.bin",
        "tp030_4h.bin",
        "tp030_5.bin",
        "tp030_6.bin"
    };

    private readonly McsArcadeAdapter _adapter = new();
    private readonly BatsugunSoundBridge _sound = new();

    public RomInfo RomInfo { get; } = new()
    {
        Summary = "Toaplan Batsugun adapter idle",
        RegionHint = ConsoleRegion.Auto
    };

    public RomIdentity? RomIdentity => _adapter.RomIdentity;
    public long? FrameCounter => _adapter.FrameCounter;

    public BatsugunAdapter()
    {
        if (Environment.GetEnvironmentVariable("EUTHERDRIVE_BATSUGUN_ENABLE_SOUND_BRIDGE") == "1" ||
            Environment.GetEnvironmentVariable("EUTHERDRIVE_BATSUGUN_TRACE_SOUND") == "1")
        {
            _adapter.BatsugunSharedRamProcessor = _sound.RunFrame;
        }
    }

    public static bool IsSupportedArchive(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !RomArchiveExtractor.IsArchivePath(path))
            return false;

        string name = GetDriverName(path);
        if (SupportedDrivers.Contains(name))
            return true;

        return LooksLikeBatsugunSpecialArchive(path);
    }

    public static bool IsSupportedDriverName(string driverName)
        => SupportedDrivers.Contains(driverName);

    public void LoadRom(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("Batsugun ROM path is empty.", nameof(path));
        if (!File.Exists(path))
            throw new FileNotFoundException("Batsugun ROM archive not found.", path);
        if (!IsSupportedArchive(path))
            throw new NotSupportedException($"'{Path.GetFileName(path)}' is not recognized as a Toaplan Batsugun Special MAME set.");

        string driverName = GetDriverName(path);
        if (!SupportedDrivers.Contains(driverName))
            driverName = "batsugunsp";

        UpdateRomInfo(path, driverName);
        _sound.LoadOkiRom(ReadArchiveEntry(path, "tp030_2.bin"));
        _adapter.LoadRom(path);
    }

    public void Reset() => _adapter.Reset();

    public void RunFrame() => _adapter.RunFrame();

    public ReadOnlySpan<byte> GetFrameBuffer(out int width, out int height, out int stride)
        => _adapter.GetFrameBuffer(out width, out height, out stride);

    public ReadOnlySpan<short> GetAudioBuffer(out int sampleRate, out int channels)
    {
        ReadOnlySpan<short> sound = _sound.GetAudioBuffer(out sampleRate, out channels);
        if (!sound.IsEmpty)
            return sound;

        return _adapter.GetAudioBuffer(out sampleRate, out channels);
    }

    public void SetMasterVolumePercent(int percent)
    {
        percent = Math.Clamp(percent, 0, 200);
        _adapter.SetMasterVolumePercent(percent);
        _sound.SetMasterVolumePercent(percent);
    }

    public double GetTargetFps() => 27_000_000.0 / 4.0 / (432.0 * 262.0);

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
        => _adapter.SetInputState(up, down, left, right, a, b, c, start, x, y, z, mode, padType);

    public void SaveState(BinaryWriter writer) => _adapter.SaveState(writer);
    public void LoadState(BinaryReader reader) => _adapter.LoadState(reader);

    public void Dispose() => _adapter.Dispose();

    private void UpdateRomInfo(string path, string driverName)
    {
        RomInfo.Summary = "Toaplan Batsugun - Special Version";
        RomInfo.ExtraInfo =
            $"MAME set: {driverName}\n" +
            $"Archive: {Path.GetFileName(path)}\n" +
            "Reference: ~/mame/src/mame/toaplan/batsugun.cpp\n" +
            "Hardware: Toaplan2, 68000 @ 16 MHz, V25 audio CPU, dual GP9001 video, YM2151 + OKIM6295.";
        RomInfo.RegionHint = ConsoleRegion.Auto;
    }

    private static string GetDriverName(string path)
        => Path.GetFileNameWithoutExtension(path).Trim().ToLowerInvariant();

    private static bool LooksLikeBatsugunSpecialArchive(string path)
    {
        try
        {
            using IArchive archive = ArchiveFactory.Open(path);
            var names = new HashSet<string>(
                archive.Entries
                    .Where(static entry => !entry.IsDirectory)
                    .Select(static entry => Path.GetFileName(entry.Key).ToLowerInvariant()),
                StringComparer.OrdinalIgnoreCase);

            return RequiredSpecialSetEntries.All(names.Contains);
        }
        catch
        {
            return false;
        }
    }

    private static byte[] ReadArchiveEntry(string path, string entryName)
    {
        using IArchive archive = ArchiveFactory.Open(path);
        foreach (IArchiveEntry entry in archive.Entries)
        {
            if (entry.IsDirectory || !string.Equals(Path.GetFileName(entry.Key), entryName, StringComparison.OrdinalIgnoreCase))
                continue;

            using Stream stream = entry.OpenEntryStream();
            using var memory = new MemoryStream();
            stream.CopyTo(memory);
            return memory.ToArray();
        }

        throw new InvalidDataException($"Batsugun ROM archive is missing '{entryName}'.");
    }

    private sealed class BatsugunSoundBridge : IV25Bus
    {
        private const int SampleRate = 44_100;
        private const int Channels = 2;
        private const double TargetFps = 27_000_000.0 / 4.0 / (432.0 * 262.0);
        private const int SharedWindowSize = 0x8000;
        private const int MaxV25InstructionsPerFrame = 20_000;
        private static readonly bool Trace = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("EUTHERDRIVE_BATSUGUN_TRACE_SOUND"));

        private readonly V25 _v25 = new();
        private readonly byte[] _plainOpcodes = new byte[256];
        private readonly Cps1Ym2151 _ym = new();
        private readonly Cps1Oki6295 _oki = new();
        private readonly byte[] _internalRam = new byte[0x80000];
        private readonly short[] _frameAudio = new short[1600];
        private readonly List<short> _audioQueue = new();
        private readonly object _sync = new();
        private short[] _audioBuffer = Array.Empty<short>();
        private byte[] _sharedRam = Array.Empty<byte>();
        private double _sampleAccumulator;
        private bool _loaded;
        private bool _v25Reset;
        private bool _v25UnavailableLogged;
        private bool _sharedDirty;
        private int _traceFrameCount;
        private bool _traceLoopDumped;
        private int _masterVolumePercent = 100;

        public void LoadOkiRom(byte[] rom)
        {
            lock (_sync)
            {
                _oki.Load(rom);
                _oki.SetClock(32_000_000 / 8);
                _oki.SetPin7(false);
                _ym.Reset();
                Array.Clear(_internalRam);
                _v25.SetOpcodeTable(_plainOpcodes);
                _v25Reset = false;
                _v25UnavailableLogged = false;
                _traceFrameCount = 0;
                _traceLoopDumped = false;
                _sampleAccumulator = 0.0;
                _audioQueue.Clear();
                _loaded = true;
            }
        }

        public void SetMasterVolumePercent(int percent)
        {
            lock (_sync)
                _masterVolumePercent = Math.Clamp(percent, 0, 200);
        }

        public bool RunFrame(byte[] sharedRam)
        {
            lock (_sync)
            {
                if (!_loaded)
                    return false;

                _sharedRam = sharedRam;
                _sharedDirty = false;
                if (TryRunV25())
                    RenderFrameAudio();
                return _sharedDirty;
            }
        }

        public ReadOnlySpan<short> GetAudioBuffer(out int sampleRate, out int channels)
        {
            sampleRate = SampleRate;
            channels = Channels;
            lock (_sync)
            {
                if (_audioQueue.Count == 0)
                    return ReadOnlySpan<short>.Empty;

                if (_audioBuffer.Length != _audioQueue.Count)
                    _audioBuffer = new short[_audioQueue.Count];

                for (int i = 0; i < _audioQueue.Count; i++)
                    _audioBuffer[i] = _audioQueue[i];
                _audioQueue.Clear();
                return _audioBuffer;
            }
        }

        public byte V25Read8(uint address)
        {
            address &= 0x0f_ffff;
            return address switch
            {
                <= 0x00001 => _ym.ReadStatus(),
                0x00004 => _oki.ReadStatus(),
                >= 0x80000 => _sharedRam[(int)((address - 0x80000) & (SharedWindowSize - 1))],
                _ => _internalRam[(int)address]
            };
        }

        public void V25Write8(uint address, byte value)
        {
            address &= 0x0f_ffff;
            if (address <= 0x00001)
            {
                _ym.Write((int)address, value);
                return;
            }

            if (address == 0x00004)
            {
                if (Trace)
                    Console.Error.WriteLine($"[BATSUGUN sound] OKI write value=0x{value:x2}");
                _oki.Write(value);
                return;
            }

            if (address < 0x80000)
            {
                _internalRam[(int)address] = value;
                return;
            }

            int index = (int)((address - 0x80000) & (SharedWindowSize - 1));
            if (_sharedRam[index] == value)
                return;

            if (Trace && index >= 0x7800)
                Console.Error.WriteLine($"[BATSUGUN sound] shared[{index:x4}]=0x{value:x2} pc=0x{_v25.PreviousPc:x5}");
            _sharedRam[index] = value;
            _sharedDirty = true;
        }

        private bool TryRunV25()
        {
            if (_sharedRam.Length < SharedWindowSize)
                return false;

            if (!_v25Reset)
            {
                bool hasResetVector =
                    _sharedRam[0x7ff0] != 0 ||
                    _sharedRam[0x7ff1] != 0 ||
                    _sharedRam[0x7ff2] != 0 ||
                    _sharedRam[0x7ff3] != 0;
                bool hasMainProgram =
                    _sharedRam[0x0040] == 0x06 &&
                    _sharedRam[0x0041] == 0x1e &&
                    _sharedRam[0x0042] == 0x07 &&
                    _sharedRam[0x004b] == 0xf3 &&
                    _sharedRam[0x004c] == 0xab;
                if (!hasResetVector || !hasMainProgram)
                {
                    TraceOnce("waiting for uploaded V25 program");
                    return false;
                }

                _v25.Reset(this);
                _v25Reset = true;
                _v25UnavailableLogged = false;
            }

            if (_v25.Halted)
            {
                TraceOnce(_v25.LastStopReason);
                return false;
            }

            for (int i = 0; i < MaxV25InstructionsPerFrame && !_v25.Halted; i++)
                _v25.ExecuteInstruction();

            if (Trace && (++_traceFrameCount % 30) == 0)
                Console.Error.WriteLine($"[BATSUGUN sound] pc=0x{_v25.Pc:x5} prev=0x{_v25.PreviousPc:x5} op=0x{_v25.LastOpcode:x2} shared7800=0x{_sharedRam[0x7800]:x2} tick=0x{_sharedRam[0x7ff9]:x2} code0080={FormatBytes(_sharedRam, 0x0080, 0x40)}");
            if (Trace && !_traceLoopDumped && _v25.Pc >= 0xa0080 && _v25.Pc <= 0xa00b0)
            {
                _traceLoopDumped = true;
                Console.Error.WriteLine($"[BATSUGUN sound] code0080={FormatBytes(_sharedRam, 0x0080, 0x40)}");
            }

            if (_v25.Halted)
            {
                TraceOnce(_v25.LastStopReason);
                return false;
            }

            return true;
        }

        private void RenderFrameAudio()
        {
            _sampleAccumulator += SampleRate / TargetFps;
            int frameSamples = (int)_sampleAccumulator;
            _sampleAccumulator -= frameSamples;
            if (frameSamples <= 0)
                return;

            int sampleCount = frameSamples * Channels;
            if (_frameAudio.Length < sampleCount)
                return;

            Array.Clear(_frameAudio, 0, sampleCount);
            int frameIndex = 0;
            _ym.RenderStereo(_frameAudio, ref frameIndex, frameSamples, gain: 0.35f, outputSampleRate: SampleRate);
            frameIndex = 0;
            _oki.RenderStereo(_frameAudio, ref frameIndex, frameSamples, gain: 0.50f, outputSampleRate: SampleRate);

            int volume = _masterVolumePercent;
            for (int i = 0; i < sampleCount; i++)
                _audioQueue.Add((short)Math.Clamp((_frameAudio[i] * volume) / 100, short.MinValue, short.MaxValue));
        }

        private void TraceOnce(string message)
        {
            if (!Trace || _v25UnavailableLogged)
                return;

            Console.Error.WriteLine($"[BATSUGUN sound] {message}");
            _v25UnavailableLogged = true;
        }

        private static string FormatBytes(byte[] data, int offset, int length)
        {
            Span<char> chars = stackalloc char[length * 3 - 1];
            int pos = 0;
            for (int i = 0; i < length; i++)
            {
                if (i != 0)
                    chars[pos++] = ' ';
                byte value = data[offset + i];
                chars[pos++] = GetHex(value >> 4);
                chars[pos++] = GetHex(value & 0x0f);
            }

            return new string(chars);
        }

        private static char GetHex(int value) => (char)(value < 10 ? '0' + value : 'a' + value - 10);
    }
}
