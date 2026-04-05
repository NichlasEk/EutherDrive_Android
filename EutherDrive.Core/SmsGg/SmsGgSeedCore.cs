using EutherDrive.Core.Cpu.Z80Emu;

namespace EutherDrive.Core.SmsGg;

public sealed class SmsGgSeedCore
{
    private const int NtscMasterClockPerFrame = 342 * 262 * 10;
    private const int PalMasterClockPerFrame = 342 * 313 * 10;
    private const uint VdpDivider = 10;

    private readonly SmsGgEmulatorConfig _config;
    private readonly byte[] _frameBuffer;
    private readonly byte[] _ggFrameBuffer = new byte[160 * 144 * 4];
    private readonly short[] _audioBuffer = Array.Empty<short>();

    private Z80? _z80;
    private SmsGgMemory? _memory;
    private SmsGgInputPorts? _inputPorts;
    private SmsGgBus? _bus;
    private SmsGgZ80BusAdapter? _z80Bus;
    private SmsGgVdp? _vdp;
    private SmsGgVdpVersion _vdpVersion;
    private long _frameCounter;
    private uint _vdpMclkCounter;

    public SmsGgSeedCore(SmsGgEmulatorConfig? config = null)
    {
        _config = config ?? new SmsGgEmulatorConfig();
        _frameBuffer = new byte[256 * 240 * 4];
    }

    public SmsGgHardware Hardware { get; private set; }
    public SmsGgRegion Region { get; private set; } = SmsGgRegion.Domestic;
    public SmsGgMemory? Memory => _memory;
    public long FrameCounter => _frameCounter;
    public ushort ProgramCounter => _z80?.Pc ?? 0;
    public SmsGgVdpVersion VdpVersion => _vdpVersion;
    public double TargetFps => _vdpVersion.TimingMode() == SmsGgTimingMode.Pal ? 50.0 : 60.0;

    public void LoadRom(string path)
    {
        var loaded = SmsGgRomLoader.Load(path);
        byte[] romBytes = loaded.RomBytes;
        Hardware = DetectHardware(loaded.DisplayName, romBytes);
        _memory = new SmsGgMemory(romBytes, biosRom: null, initialCartridgeRam: null, Hardware);
        Region = _config.ResolveRegion(_memory);
        _inputPorts = new SmsGgInputPorts(Region);
        _vdpVersion = DetermineVdpVersion(Hardware, _config);
        _vdp = new SmsGgVdp(_vdpVersion, _config);
        _bus = new SmsGgBus(_vdpVersion, _memory, _inputPorts, _vdp);
        _z80Bus = new SmsGgZ80BusAdapter(_bus);
        _z80 = new Z80();
        _z80.SetPc(0x0000);
        _z80.SetSp(0xDFFF);
        _z80.SetInterruptMode(InterruptMode.Mode1);
        _frameCounter = 0;
        _vdpMclkCounter = 0;
        ClearFrameBuffer();
    }

    public void Reset()
    {
        if (_memory is null || _inputPorts is null)
            return;

        _memory.Reset();
        Region = _config.ResolveRegion(_memory);
        _inputPorts.SetRegion(Region);
        _vdp = new SmsGgVdp(_vdpVersion, _config);
        _bus = new SmsGgBus(_vdpVersion, _memory, _inputPorts, _vdp);
        _z80Bus = new SmsGgZ80BusAdapter(_bus);
        _z80 = new Z80();
        _z80.SetPc(0x0000);
        _z80.SetSp(0xDFFF);
        _z80.SetInterruptMode(InterruptMode.Mode1);
        _frameCounter = 0;
        _vdpMclkCounter = 0;
        ClearFrameBuffer();
    }

    public void SetInputState(SmsGgInputState state)
    {
        _inputPorts?.SetInputs(state);
    }

    public void RunFrame()
    {
        if (_z80 is null || _z80Bus is null || _vdp is null)
            return;

        int safetyMasterClocks = GetMasterClocksPerFrame() * 2;
        uint divider = _config.Z80Divider == 0 ? 15u : _config.Z80Divider;
        bool frameComplete = false;

        while (!frameComplete && safetyMasterClocks > 0)
        {
            uint z80TCycles = _z80.ExecuteInstruction(_z80Bus);
            uint masterClocks = z80TCycles * divider;
            safetyMasterClocks -= (int)Math.Min((uint)safetyMasterClocks, masterClocks);

            _vdpMclkCounter += masterClocks;
            while (_vdpMclkCounter >= VdpDivider && !frameComplete)
            {
                _vdpMclkCounter -= VdpDivider;
                frameComplete = _vdp.Tick();
            }
        }

        _vdp.RenderFrame();
        _frameCounter++;
    }

    public ReadOnlySpan<byte> GetFrameBuffer(out int width, out int height, out int stride)
    {
        width = Hardware == SmsGgHardware.GameGear && !_config.GgUseSmsResolution ? 160 : 256;
        height = Hardware == SmsGgHardware.GameGear && !_config.GgUseSmsResolution ? 144 : 240;
        stride = width * 4;
        if (_vdp != null)
        {
            ReadOnlySpan<byte> source = _vdp.GetFrameBuffer();
            if (Hardware == SmsGgHardware.GameGear && !_config.GgUseSmsResolution)
            {
                const int sourceStride = 256 * 4;
                const int rowBytes = 160 * 4;
                for (int y = 0; y < 144; y++)
                {
                    source.Slice(y * sourceStride, rowBytes).CopyTo(_ggFrameBuffer.AsSpan(y * rowBytes, rowBytes));
                }

                return _ggFrameBuffer;
            }

            return source;
        }

        return _frameBuffer;
    }

    public ReadOnlySpan<short> GetAudioBuffer(out int sampleRate, out int channels)
    {
        sampleRate = 44100;
        channels = 2;
        return _audioBuffer;
    }

    private int GetMasterClocksPerFrame() =>
        _vdpVersion.TimingMode() == SmsGgTimingMode.Pal ? PalMasterClockPerFrame : NtscMasterClockPerFrame;

    private void ClearFrameBuffer()
    {
        Array.Clear(_frameBuffer);
    }

    private static SmsGgHardware DetectHardware(string displayName, byte[] romBytes)
    {
        if (Path.GetExtension(displayName).Equals(".gg", StringComparison.OrdinalIgnoreCase))
            return SmsGgHardware.GameGear;

        int[] headerLocations = { 0x1FF0, 0x3FF0, 0x7FF0 };
        foreach (int offset in headerLocations)
        {
            if (romBytes.Length < offset + 16)
                continue;

            if (!romBytes.AsSpan(offset, 8).SequenceEqual("TMR SEGA"u8))
                continue;

            int regionCode = romBytes[offset + 15] >> 4;
            if (regionCode is 5 or 6 or 7)
                return SmsGgHardware.GameGear;
        }

        return SmsGgHardware.MasterSystem;
    }

    private static SmsGgVdpVersion DetermineVdpVersion(SmsGgHardware hardware, SmsGgEmulatorConfig config)
    {
        if (hardware == SmsGgHardware.GameGear)
            return SmsGgVdpVersion.GameGear;

        return (config.SmsTimingMode, config.SmsModel) switch
        {
            (SmsGgTimingMode.Pal, SmsModel.Sms1) => SmsGgVdpVersion.PalMasterSystem1,
            (SmsGgTimingMode.Pal, SmsModel.Sms2) => SmsGgVdpVersion.PalMasterSystem2,
            (SmsGgTimingMode.Ntsc, SmsModel.Sms1) => SmsGgVdpVersion.NtscMasterSystem1,
            _ => SmsGgVdpVersion.NtscMasterSystem2
        };
    }
}
