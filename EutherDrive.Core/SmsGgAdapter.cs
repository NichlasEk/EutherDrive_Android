using EutherDrive.Core.Savestates;
using EutherDrive.Core.SmsGg;

namespace EutherDrive.Core;

public sealed class SmsGgAdapter : IEmulatorCore, ISavestateCapable
{
    private const int SavestateMagic = 0x53474731;

    private readonly MdTracerAdapter _fallback = new();
    private readonly SmsGgSeedCore _seedCore = new();
    private SmsGgPortSession? _port;
    private string? _loadedPath;
    private RomIdentity? _seedRomIdentity;

    public SmsGgHardware Hardware => _port?.Hardware ?? SmsGgHardware.MasterSystem;
    public bool IsMasterSystemMode => Hardware != SmsGgHardware.GameGear;
    public bool IsGameGearMode => Hardware == SmsGgHardware.GameGear;
    public RomInfo RomInfo { get; private set; } = new();
    public string? RomSummary => RomInfo.Summary;
    public RomIdentity? RomIdentity => IsGameGearMode ? _seedRomIdentity ?? ((ISavestateCapable)_fallback).RomIdentity : ((ISavestateCapable)_fallback).RomIdentity;
    public long? FrameCounter => IsGameGearMode ? _seedCore.FrameCounter : ((ISavestateCapable)_fallback).FrameCounter;

    public void LoadRom(string path)
    {
        _loadedPath = path;
        _port = SmsGgPortSession.Load(path);
        RomInfo = _port.BuildRomInfo();
        _seedCore.LoadRom(path);
        _seedRomIdentity = new RomIdentity(
            Path.GetFileName(path),
            RomIdentity.ComputeSha256(_port.RomBytes),
            PersistentStoragePath.ResolveSavestateDirectory(path, _port.Hardware == SmsGgHardware.GameGear ? "gg" : "sms"));
        _fallback.PowerCycleAndLoadRom(path);
        _fallback.HardFlushAudioState();
    }

    public void Reset()
    {
        _seedCore.Reset();
        _fallback.Reset();
    }

    public void RunFrame()
    {
        _seedCore.RunFrame();
        _fallback.RunFrame();
    }

    public ReadOnlySpan<byte> GetFrameBuffer(out int width, out int height, out int stride) =>
        IsGameGearMode
            ? _seedCore.GetFrameBuffer(out width, out height, out stride)
            : _fallback.GetFrameBuffer(out width, out height, out stride);

    public ReadOnlySpan<short> GetAudioBuffer(out int sampleRate, out int channels) =>
        IsGameGearMode
            ? _seedCore.GetAudioBuffer(out sampleRate, out channels)
            : _fallback.GetAudioBuffer(out sampleRate, out channels);

    public ReadOnlySpan<short> GetAudioBufferForFrames(int frames, out int sampleRate, out int channels) =>
        IsGameGearMode
            ? _seedCore.GetAudioBufferForFrames(frames, out sampleRate, out channels)
            : _fallback.GetAudioBufferForFrames(frames, out sampleRate, out channels);

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
        _port?.SetInputState(up, down, left, right, a, b, start);
        if (_port != null)
            _seedCore.SetInputState(_port.InputState);
        _fallback.SetInputState(up, down, left, right, a, b, c, start, x, y, z, mode, padType);
    }

    public void SaveState(BinaryWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);

        writer.Write(SavestateMagic);
        writer.Write(IsGameGearMode);
        if (IsGameGearMode)
        {
            _seedCore.SaveState(writer);
            return;
        }

        ((ISavestateCapable)_fallback).SaveState(writer);
    }

    public void LoadState(BinaryReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);

        long start = reader.BaseStream.CanSeek ? reader.BaseStream.Position : 0;
        if (reader.BaseStream.CanSeek && reader.BaseStream.Length - start >= sizeof(int) + sizeof(bool))
        {
            int magic = reader.ReadInt32();
            if (magic == SavestateMagic)
            {
                bool isGameGearState = reader.ReadBoolean();
                if (isGameGearState)
                {
                    _seedCore.LoadState(reader);
                    if (_port != null)
                        _seedCore.SetInputState(_port.InputState);
                    return;
                }

                ((ISavestateCapable)_fallback).LoadState(reader);
                return;
            }

            reader.BaseStream.Position = start;
        }

        ((ISavestateCapable)_fallback).LoadState(reader);

        // Seed GG video/input is currently the active render path for Game Gear.
        // Reinitialize it after fallback state loads so at least runtime input/video stay coherent.
        if (IsGameGearMode && !string.IsNullOrWhiteSpace(_loadedPath))
        {
            _seedCore.LoadRom(_loadedPath);
            if (_port != null)
                _seedCore.SetInputState(_port.InputState);
        }
    }

    public double GetTargetFps() => IsGameGearMode ? _seedCore.TargetFps : _fallback.GetTargetFps();

    public void SetFrameRateMode(FrameRateMode mode) => _fallback.SetFrameRateMode(mode);

    public void SetMasterVolumePercent(int percent)
    {
        _seedCore.SetMasterVolumePercent(percent);
        _fallback.SetMasterVolumePercent(percent);
    }

    public void SetPsgMixPercent(int percent)
    {
        _seedCore.SetPsgMixPercent(percent);
        _fallback.SetPsgMixPercent(percent);
    }

    public void SetYmMixPercent(int percent) => _fallback.SetYmMixPercent(percent);

    public void SetPsgNoiseMixPercent(int percent) => _fallback.SetPsgNoiseMixPercent(percent);

    public void HardFlushAudioState() => _fallback.HardFlushAudioState();
}
