using EutherDrive.Core.Cpu.Z80Emu;
using EutherDrive.Core.MdTracerCore;

namespace EutherDrive.Core.SmsGg;

public sealed class SmsGgSeedCore
{
    private const int NtscMasterClockPerFrame = 342 * 262 * 10;
    private const int PalMasterClockPerFrame = 342 * 313 * 10;
    private const uint VdpDivider = 10;
    private const int SmsFrameWidth = 256;
    private const int SmsFrameHeight = 240;
    private const int SmsBytesPerPixel = 4;
    private const int GgFrameWidth = 160;
    private const int GgFrameHeight = 144;
    private const int AudioSampleRate = 44100;
    private const int AudioChannels = 2;
    private const uint PsgDivider = 15;
    private const double NtscMasterClockFrequency = 53_693_175.0;
    private const double PalMasterClockFrequency = 53_203_424.0;
    private const double DefaultPsgGain = 0.35;
    private const double MasterVolumeExponent = 1.75;
    private const double PsgMixExponent = 1.2;

    private readonly SmsGgEmulatorConfig _config;
    private readonly byte[] _presentFrameBuffer;
    private readonly byte[] _presentGgFrameBuffer = new byte[GgFrameWidth * GgFrameHeight * SmsBytesPerPixel];
    private readonly int[] _psgOutputVolume = { 1, 1, 1, 1, 1, 1, 1, 1, 1, 1 };
    private short[] _psgInternalBuffer = Array.Empty<short>();
    private short[] _audioBuffer = Array.Empty<short>();

    private Z80? _z80;
    private SmsGgMemory? _memory;
    private SmsGgInputPorts? _inputPorts;
    private SmsGgBus? _bus;
    private SmsGgZ80BusAdapter? _z80Bus;
    private SmsGgVdp? _vdp;
    private JgSn76489? _psg;
    private SmsGgVdpVersion _vdpVersion;
    private long _frameCounter;
    private uint _vdpMclkCounter;
    private uint _psgMclkCounter;
    private int _masterVolumePercent = 100;
    private int _psgMixPercent = 100;
    private double _psgResamplePhase;
    private bool _psgResampleHasCarry;
    private short _psgResampleCarry;
    private double _psgDcBlockX1;
    private double _psgDcBlockY1;

    public SmsGgSeedCore(SmsGgEmulatorConfig? config = null)
    {
        _config = config ?? new SmsGgEmulatorConfig();
        _presentFrameBuffer = new byte[SmsFrameWidth * SmsFrameHeight * SmsBytesPerPixel];
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
        _psg = new JgSn76489();
        _psg.Reset();
        UpdatePsgOutputGain();
        _bus = new SmsGgBus(_vdpVersion, _memory, _inputPorts, _vdp, OnPsgWrite, OnStereoWrite);
        _z80Bus = new SmsGgZ80BusAdapter(_bus);
        _z80 = new Z80();
        _z80.SetPc(0x0000);
        _z80.SetSp(0xDFFF);
        _z80.SetInterruptMode(InterruptMode.Mode1);
        _frameCounter = 0;
        _vdpMclkCounter = 0;
        _psgMclkCounter = 0;
        _psgResamplePhase = 0.0;
        _psgResampleHasCarry = false;
        _psgResampleCarry = 0;
        _psgDcBlockX1 = 0.0;
        _psgDcBlockY1 = 0.0;
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
        _psg = new JgSn76489();
        _psg.Reset();
        UpdatePsgOutputGain();
        _bus = new SmsGgBus(_vdpVersion, _memory, _inputPorts, _vdp, OnPsgWrite, OnStereoWrite);
        _z80Bus = new SmsGgZ80BusAdapter(_bus);
        _z80 = new Z80();
        _z80.SetPc(0x0000);
        _z80.SetSp(0xDFFF);
        _z80.SetInterruptMode(InterruptMode.Mode1);
        _frameCounter = 0;
        _vdpMclkCounter = 0;
        _psgMclkCounter = 0;
        _psgResamplePhase = 0.0;
        _psgResampleHasCarry = false;
        _psgResampleCarry = 0;
        _psgDcBlockX1 = 0.0;
        _psgDcBlockY1 = 0.0;
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
            _psgMclkCounter += masterClocks;
            if (_psg != null && _psgMclkCounter >= PsgDivider)
            {
                int psgTicks = (int)(_psgMclkCounter / PsgDivider);
                _psgMclkCounter %= PsgDivider;
                _psg.AdvancePsgTicks(psgTicks, _psgOutputVolume, 100);
            }

            while (_vdpMclkCounter >= VdpDivider && !frameComplete)
            {
                _vdpMclkCounter -= VdpDivider;
                frameComplete = _vdp.Tick();
            }
        }

        _vdp.RenderFrame();
        LatchPresentationFrame();
        _frameCounter++;
    }

    public ReadOnlySpan<byte> GetFrameBuffer(out int width, out int height, out int stride)
    {
        width = Hardware == SmsGgHardware.GameGear && !_config.GgUseSmsResolution ? 160 : 256;
        height = Hardware == SmsGgHardware.GameGear && !_config.GgUseSmsResolution ? 144 : 240;
        stride = width * 4;
        if (Hardware == SmsGgHardware.GameGear && !_config.GgUseSmsResolution)
        {
            return _presentGgFrameBuffer;
        }

        return _presentFrameBuffer;
    }

    public ReadOnlySpan<short> GetAudioBuffer(out int sampleRate, out int channels)
    {
        int frames = GetDefaultAudioFrameCount();
        return GetAudioBufferForFrames(frames, out sampleRate, out channels);
    }

    public ReadOnlySpan<short> GetAudioBufferForFrames(int frames, out int sampleRate, out int channels)
    {
        sampleRate = AudioSampleRate;
        channels = AudioChannels;

        if (_psg is null || frames <= 0)
            return ReadOnlySpan<short>.Empty;

        int sampleCount = frames * AudioChannels;
        if (_audioBuffer.Length < sampleCount)
            _audioBuffer = new short[sampleCount];

        double sourceRate = GetPsgSampleRate();
        double ratio = sourceRate / AudioSampleRate;
        double phase = _psgResamplePhase;
        int neededInternal = (int)Math.Floor(phase + ((frames - 1) * ratio)) + 2;
        if (neededInternal < 2)
            neededInternal = 2;

        if (_psgInternalBuffer.Length < neededInternal)
            _psgInternalBuffer = new short[neededInternal];

        int writeOffset = 0;
        if (_psgResampleHasCarry)
        {
            _psgInternalBuffer[0] = _psgResampleCarry;
            writeOffset = 1;
        }

        for (int i = writeOffset; i < neededInternal; i++)
        {
            _psgInternalBuffer[i] = (short)_psg.UpdateSample(_psgOutputVolume, 100);
        }

        for (int i = 0; i < frames; i++)
        {
            int baseIndex = (int)phase;
            if (baseIndex < 0)
                baseIndex = 0;
            int maxBase = neededInternal - 2;
            if (baseIndex > maxBase)
                baseIndex = maxBase;
            double frac = phase - baseIndex;
            if (frac < 0.0)
                frac = 0.0;
            else if (frac > 1.0)
                frac = 1.0;

            short s1 = _psgInternalBuffer[baseIndex];
            short s2 = _psgInternalBuffer[baseIndex + 1];
            short sample = ApplyDcBlock((short)LinearInterpolate(s1, s2, frac));
            int offset = i * AudioChannels;
            _audioBuffer[offset] = sample;
            _audioBuffer[offset + 1] = sample;
            phase += ratio;
        }

        _psgResampleCarry = _psgInternalBuffer[neededInternal - 1];
        _psgResampleHasCarry = true;
        _psgResamplePhase = phase - (neededInternal - 1);
        if (_psgResamplePhase < 0.0 || _psgResamplePhase > neededInternal)
            _psgResamplePhase = 0.0;

        return _audioBuffer.AsSpan(0, sampleCount);
    }

    public void SetMasterVolumePercent(int percent)
    {
        _masterVolumePercent = Math.Clamp(percent, 0, 200);
        UpdatePsgOutputGain();
    }

    public void SetPsgMixPercent(int percent)
    {
        _psgMixPercent = Math.Clamp(percent, 0, 200);
        UpdatePsgOutputGain();
    }

    private int GetMasterClocksPerFrame() =>
        _vdpVersion.TimingMode() == SmsGgTimingMode.Pal ? PalMasterClockPerFrame : NtscMasterClockPerFrame;

    private int GetDefaultAudioFrameCount()
    {
        double fps = TargetFps <= 0 ? 60.0 : TargetFps;
        return Math.Max(1, (int)Math.Round(AudioSampleRate / fps));
    }

    private void ClearFrameBuffer()
    {
        Array.Clear(_presentFrameBuffer);
        Array.Clear(_presentGgFrameBuffer);
    }

    private void LatchPresentationFrame()
    {
        if (_vdp is null)
            return;

        ReadOnlySpan<byte> source = _vdp.GetFrameBuffer();
        source.CopyTo(_presentFrameBuffer);

        if (Hardware != SmsGgHardware.GameGear || _config.GgUseSmsResolution)
            return;

        const int sourceStride = SmsFrameWidth * SmsBytesPerPixel;
        const int rowBytes = GgFrameWidth * SmsBytesPerPixel;
        for (int y = 0; y < GgFrameHeight; y++)
        {
            _presentFrameBuffer.AsSpan(y * sourceStride, rowBytes)
                .CopyTo(_presentGgFrameBuffer.AsSpan(y * rowBytes, rowBytes));
        }
    }

    private void OnPsgWrite(byte value)
    {
        _psg?.Write(value);
    }

    private void OnStereoWrite(byte value)
    {
        _psg?.WriteStereoControl(value);
    }

    private void UpdatePsgOutputGain()
    {
        double master = Math.Pow(_masterVolumePercent / 100.0, MasterVolumeExponent);
        double psgMix = Math.Pow(_psgMixPercent / 100.0, PsgMixExponent);
        double gain = DefaultPsgGain * psgMix * master;
        _psg?.SetOutputGain(gain);
    }

    private double GetPsgSampleRate()
    {
        double masterClock = _vdpVersion.TimingMode() == SmsGgTimingMode.Pal
            ? PalMasterClockFrequency
            : NtscMasterClockFrequency;
        return masterClock / PsgDivider / 16.0;
    }

    private static int LinearInterpolate(short a, short b, double frac)
    {
        double sample = a + ((b - a) * frac);
        if (sample > short.MaxValue)
            return short.MaxValue;
        if (sample < short.MinValue)
            return short.MinValue;
        return (int)Math.Round(sample);
    }

    private short ApplyDcBlock(short sample)
    {
        double x = sample;
        double y = x - _psgDcBlockX1 + (0.9993 * _psgDcBlockY1);
        _psgDcBlockX1 = x;
        _psgDcBlockY1 = y;
        if (y > short.MaxValue)
            return short.MaxValue;
        if (y < short.MinValue)
            return short.MinValue;
        return (short)Math.Round(y);
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
