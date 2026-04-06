using System.IO;
using System.Runtime.InteropServices;
using EutherDrive.Core.GbEmu;
using EutherDrive.Core.Savestates;

namespace EutherDrive.Core;

public sealed class GbAdapter : IEmulatorCore, IDisposable, ISavestateCapable
{
    private const int SavestateVersion = 1;
    private const int FrameWidth = GameboyConstants.ScreenWidth;
    private const int FrameHeight = GameboyConstants.ScreenHeight;
    private const int FrameStride = FrameWidth * 4;
    private const int OutputSampleRate = 44100;
    private const int OutputChannels = 2;
    private const int AutoSaveFrameInterval = 120;

    private Emulator? _emulator;
    private string? _savePath;
    private string? _romPath;
    private RomHeader? _header;
    private RomIdentity? _romIdentity;
    private string? _romSummary;
    private int _masterVolumePercent = 100;
    private int _framesUntilAutoSave = AutoSaveFrameInterval;
    private short[] _scaledAudioBuffer = Array.Empty<short>();
    private long _frameCounter;

    public string? RomSummary => _romSummary;
    public RomIdentity? RomIdentity => _romIdentity;
    public long? FrameCounter => _frameCounter;

    public void LoadRom(string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException("ROM not found.", path);

        byte[] romData = File.ReadAllBytes(path);
        _header = RomHeader.Parse(romData);
        _romPath = path;

        string saveDirectory = PersistentStoragePath.ResolveSaveDirectory(path, "gb");
        Directory.CreateDirectory(saveDirectory);
        _savePath = Path.Combine(saveDirectory, Path.GetFileNameWithoutExtension(path) + ".sav");

        _emulator?.Dispose();
        var emulator = new Emulator();
        emulator.LoadRom(path);
        LoadPersistentData(emulator);

        _emulator = emulator;
        _framesUntilAutoSave = AutoSaveFrameInterval;
        _frameCounter = 0;
        _romIdentity = new RomIdentity(
            Path.GetFileName(path),
            RomIdentity.ComputeSha256(romData),
            PersistentStoragePath.ResolveSavestateDirectory(path, "gb"));
        _romSummary = BuildRomSummary(path, _header);
    }

    public void Reset()
    {
        if (string.IsNullOrWhiteSpace(_romPath))
            return;

        LoadRom(_romPath);
    }

    public void RunFrame()
    {
        if (_emulator == null)
            return;

        _emulator.RunFrame();
        _frameCounter++;
        if (--_framesUntilAutoSave <= 0)
        {
            FlushPersistentData();
            _framesUntilAutoSave = AutoSaveFrameInterval;
        }
    }

    public ReadOnlySpan<byte> GetFrameBuffer(out int width, out int height, out int stride)
    {
        width = FrameWidth;
        height = FrameHeight;
        stride = FrameStride;
        if (_emulator == null)
            return ReadOnlySpan<byte>.Empty;

        return MemoryMarshal.AsBytes<uint>(_emulator.Ppu.GetFrameBuffer().AsSpan());
    }

    public ReadOnlySpan<short> GetAudioBuffer(out int sampleRate, out int channels)
    {
        sampleRate = OutputSampleRate;
        channels = OutputChannels;
        if (_emulator == null)
            return ReadOnlySpan<short>.Empty;

        ReadOnlySpan<short> source = _emulator.ConsumeAudioBuffer();
        if (_masterVolumePercent >= 100 || source.IsEmpty)
            return source;

        if (_scaledAudioBuffer.Length < source.Length)
            _scaledAudioBuffer = new short[source.Length];

        int scale = _masterVolumePercent;
        for (int i = 0; i < source.Length; i++)
            _scaledAudioBuffer[i] = (short)((source[i] * scale) / 100);

        return _scaledAudioBuffer.AsSpan(0, source.Length);
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
        _ = c;
        _ = x;
        _ = y;
        _ = z;
        _ = padType;
        _emulator?.SetInputState(up, down, left, right, a, b, start, mode);
    }

    public void SetMasterVolumePercent(int percent)
    {
        if (percent < 0)
            percent = 0;
        else if (percent > 100)
            percent = 100;
        _masterVolumePercent = percent;
    }

    public double GetTargetFps() => GameboyConstants.FramesPerSecond;

    public void SaveState(BinaryWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);

        if (_emulator == null)
            throw new InvalidOperationException("GB core not initialized.");

        writer.Write(SavestateVersion);
        writer.Write(_frameCounter);
        StateBinarySerializer.WriteInto(writer, _emulator.Mmu);
        StateBinarySerializer.WriteInto(writer, _emulator.Cpu);
        StateBinarySerializer.WriteInto(writer, _emulator.Ppu);
        StateBinarySerializer.WriteInto(writer, _emulator.Timer);
        StateBinarySerializer.WriteInto(writer, _emulator.Apu);
        StateBinarySerializer.WriteInto(writer, _emulator.Joypad);
    }

    public void LoadState(BinaryReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);

        if (_emulator == null)
            throw new InvalidOperationException("GB core not initialized.");

        int version = reader.ReadInt32();
        if (version != SavestateVersion)
            throw new InvalidDataException($"Unsupported GB savestate version: {version}.");

        _frameCounter = reader.ReadInt64();
        StateBinarySerializer.ReadInto(reader, _emulator.Mmu);
        StateBinarySerializer.ReadInto(reader, _emulator.Cpu);
        StateBinarySerializer.ReadInto(reader, _emulator.Ppu);
        StateBinarySerializer.ReadInto(reader, _emulator.Timer);
        StateBinarySerializer.ReadInto(reader, _emulator.Apu);
        StateBinarySerializer.ReadInto(reader, _emulator.Joypad);
        ResetRuntimeAudioState();
    }

    public void Dispose()
    {
        FlushPersistentData();
        _emulator?.Dispose();
        _emulator = null;
    }

    private void LoadPersistentData(Emulator emulator)
    {
        if (string.IsNullOrWhiteSpace(_savePath) || !File.Exists(_savePath))
            return;

        try
        {
            using var stream = File.OpenRead(_savePath);
            using var reader = new BinaryReader(stream);
            emulator.Mmu.LoadPersistentData(reader);
        }
        catch
        {
            // Ignore broken battery saves for now and let the ROM boot cleanly.
        }
    }

    private void FlushPersistentData()
    {
        if (_emulator == null || string.IsNullOrWhiteSpace(_savePath))
            return;

        try
        {
            using var stream = File.Create(_savePath);
            using var writer = new BinaryWriter(stream);
            _emulator.Mmu.SavePersistentData(writer);
        }
        catch
        {
            // Ignore save failures; runtime emulation should continue.
        }
    }

    private void ResetRuntimeAudioState()
    {
        _scaledAudioBuffer = Array.Empty<short>();
        _emulator?.ResetRuntimeBuffers();
    }

    private static string BuildRomSummary(string path, RomHeader header)
    {
        string title = string.IsNullOrWhiteSpace(header.Title)
            ? Path.GetFileName(path)
            : header.Title.Trim();
        string model = header.CgbFlag switch
        {
            "CGB Only" => "GBC",
            "CGB Compatible" => "GB/GBC",
            _ => "GB"
        };

        return $"{model}: {title} | {header.CgbFlag} | ROM {header.RomSize / 1024}KB | RAM {header.RamSize / 1024}KB | cart 0x{header.CartridgeType:X2}";
    }
}
