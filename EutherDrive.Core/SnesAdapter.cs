using KSNES.AudioProcessing;
using KSNES.CPU;
using KSNES.PictureProcessing;
using KSNES.Rendering;
using KSNES.ROM;
using KSNES.SNESSystem;
using System.Diagnostics;
using EutherDrive.Core.Savestates;
using System.IO;

namespace EutherDrive.Core;

public sealed class SnesAdapter : IEmulatorCore, ISavestateCapable, IExtendedInputHandler
{
    private const double NtscFps = 21477272.0 / (1364.0 * 262.0);
    private const double PalFps = 21281370.0 / (1364.0 * 312.0);
    private const int DefaultWidth = 256;
    private const int DefaultHeight = 224;
    private const int DefaultStride = DefaultWidth * 4;
    private readonly SNESSystem _system;
    public SNESSystem System => _system;
    private readonly SnesAudioHandler _audioHandler = new();
    private readonly SnesFrameRenderer _renderer = new();
    private byte[] _frameBuffer = new byte[DefaultHeight * DefaultStride];
    private int _frameWidth = DefaultWidth;
    private int _frameHeight = DefaultHeight;
    private int _frameStride = DefaultStride;
    private short[] _audioBuffer = Array.Empty<short>();
    private string? _romSummary;
    private long _lastAudioLogTicks;
    private bool _traceAudio;
    private bool _lowpassEnabled;
    private ConsoleRegion _romRegionHint = ConsoleRegion.Auto;
    private byte? _romRegionCode;
    private ConsoleRegion _regionOverride = ConsoleRegion.Auto;
    private readonly object _stateLock = new();
    private long _frameCounter;
    private RomIdentity? _romIdentity;
    private float _dcLastInL;
    private float _dcLastOutL;
    private float _dcLastInR;
    private float _dcLastOutR;
    private float _lpLastL;
    private float _lpLastR;
    private float _masterVolumeScale = 1.0f;
    private const float SnesMixScale = 0.5f;
    private bool _sawBrightFrame;
    private const float DcBlockCoeff = 0.995f;
    private double _audioSampleAccumulator;
    private volatile string _framePerfSummary = "SNES draw --";
    private long _framePerfWindowStartTicks = Stopwatch.GetTimestamp();
    private long _framePerfAccumulatedCoreTicks;
    private long _framePerfAccumulatedBlitTicks;
    private long _framePerfAccumulatedPcmTicks;
    private long _framePerfAccumulatedBytes;
    private int _framePerfSamples;

    public string? RomSummary => _romSummary;
    public ConsoleRegion RomRegionHint => _romRegionHint;
    public byte? RomRegionCode => _romRegionCode;
    public RomIdentity? RomIdentity => _romIdentity;
    public long? FrameCounter => _frameCounter;

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
        _audioSampleAccumulator = 0;
        _frameCounter = 0;
        _romSummary = BuildRomSummary();
        ResetFramePerfCounters();
        if (File.Exists(path))
        {
            byte[] data = File.ReadAllBytes(path);
            _romIdentity = new RomIdentity(
                Path.GetFileName(path),
                RomIdentity.ComputeSha256(data),
                PersistentStoragePath.ResolveSavestateDirectory(path, "snes"));
        }
        else
        {
            _romIdentity = null;
        }
    }

    public void Reset()
    {
        _system.ResetForExternal();
        _sawBrightFrame = false;
        _audioSampleAccumulator = 0;
        _lpLastL = 0;
        _lpLastR = 0;
        _frameCounter = 0;
        ResetFramePerfCounters();
    }

    public void SetRegionOverride(ConsoleRegion region)
    {
        _regionOverride = region;
        UpdateIsPal();
    }

    public void SetMasterVolumePercent(int percent)
    {
        if (percent < 0) percent = 0;
        else if (percent > 100) percent = 100;
        _masterVolumeScale = percent / 100f;
    }

    public void RunFrame()
    {
        lock (_stateLock)
        {
            int samplesPerFrame = GetSamplesPerFrame();
            _audioHandler.EnsureCapacity(samplesPerFrame);
            long coreStart = Stopwatch.GetTimestamp();
            _system.RunFrameForExternal();
            long coreTicks = Stopwatch.GetTimestamp() - coreStart;
            int[] pixels = _system.PPU.GetPixels();
            int presentWidth = DefaultWidth;
            int presentHeight = DefaultHeight;
            if (_system.PPU is PPU ppu)
            {
                if (!_sawBrightFrame)
                {
                    presentWidth = ppu.PresentWidth;
                    presentHeight = ppu.PresentHeight;
                    if (ppu.Brightness == 0 && !HasVisiblePixels(pixels, presentWidth, presentHeight))
                    {
                        EnsureFrameBuffer(presentWidth, presentHeight);
                        Array.Clear(_frameBuffer, 0, _frameBuffer.Length);
                        EnsureAudioBuffer(samplesPerFrame);
                        long pcmStart = Stopwatch.GetTimestamp();
                        ConvertFloatToPcm(_audioHandler.SampleBufferL, _audioHandler.SampleBufferR, _audioBuffer);
                        long pcmTicks = Stopwatch.GetTimestamp() - pcmStart;
                        UpdateFramePerfStats(coreTicks, 0, pcmTicks, 0);
                        TraceAudioIfEnabled();
                        return;
                    }
                    _sawBrightFrame = true;
                }
                else
                {
                    presentWidth = ppu.PresentWidth;
                    presentHeight = ppu.PresentHeight;
                }
            }
            EnsureFrameBuffer(presentWidth, presentHeight);
            long blitStart = Stopwatch.GetTimestamp();
            ConvertArgbToBgra(pixels, _frameBuffer, presentWidth, presentHeight, PPU.MaxFrameWidth);
            long blitTicks = Stopwatch.GetTimestamp() - blitStart;
            EnsureAudioBuffer(samplesPerFrame);
            long pcmStart2 = Stopwatch.GetTimestamp();
            ConvertFloatToPcm(_audioHandler.SampleBufferL, _audioHandler.SampleBufferR, _audioBuffer);
            long pcmTicks2 = Stopwatch.GetTimestamp() - pcmStart2;
            UpdateFramePerfStats(coreTicks, blitTicks, pcmTicks2, presentHeight * _frameStride);
            TraceAudioIfEnabled();
            _frameCounter++;
        }
    }

    private static bool HasVisiblePixels(int[] pixels, int width, int height)
    {
        int srcStride = PPU.MaxFrameWidth;
        for (int y = 0; y < height; y++)
        {
            int rowBase = y * srcStride;
            for (int x = 0; x < width; x++)
            {
                if ((pixels[rowBase + x] & 0x00FFFFFF) != 0)
                    return true;
            }
        }

        return false;
    }

    public void SaveState(BinaryWriter writer)
    {
        if (writer == null)
            throw new ArgumentNullException(nameof(writer));

        lock (_stateLock)
        {
            const int version = 2;
            writer.Write(version);
            writer.Write(_frameCounter);
            writer.Write(_audioSampleAccumulator);
            writer.Write(_sawBrightFrame);
            writer.Write(_dcLastInL);
            writer.Write(_dcLastOutL);
            writer.Write(_dcLastInR);
            writer.Write(_dcLastOutR);
            writer.Write(_lpLastL);
            writer.Write(_lpLastR);

            StateBinarySerializer.WriteInto(writer, _system);
            StateBinarySerializer.WriteInto(writer, (CPU)_system.CPU);
            StateBinarySerializer.WriteInto(writer, (PPU)_system.PPU);
            var rom = (ROM)_system.ROM;
            StateBinarySerializer.WriteInto(writer, rom);
            var apu = (APU)_system.APU;
            StateBinarySerializer.WriteInto(writer, apu);
            StateBinarySerializer.WriteInto(writer, apu.Spc);
            StateBinarySerializer.WriteInto(writer, apu.Dsp);
            WriteChipState(writer, rom.Cx4);
            WriteChipState(writer, rom.Dsp1);
            WriteChipState(writer, rom.SuperFx);
            WriteChipState(writer, rom.Sa1);
        }
    }

    public void LoadState(BinaryReader reader)
    {
        if (reader == null)
            throw new ArgumentNullException(nameof(reader));

        lock (_stateLock)
        {
            int version = reader.ReadInt32();
            if (version != 2)
                throw new InvalidDataException($"Unsupported SNES savestate version: {version}.");

            _frameCounter = reader.ReadInt64();
            _audioSampleAccumulator = reader.ReadDouble();
            _sawBrightFrame = reader.ReadBoolean();
            _dcLastInL = reader.ReadSingle();
            _dcLastOutL = reader.ReadSingle();
            _dcLastInR = reader.ReadSingle();
            _dcLastOutR = reader.ReadSingle();
            _lpLastL = reader.ReadSingle();
            _lpLastR = reader.ReadSingle();

            StateBinarySerializer.ReadInto(reader, _system);
            StateBinarySerializer.ReadInto(reader, (CPU)_system.CPU);
            StateBinarySerializer.ReadInto(reader, (PPU)_system.PPU);
            var rom = (ROM)_system.ROM;
            StateBinarySerializer.ReadInto(reader, rom);
            var apu = (APU)_system.APU;
            StateBinarySerializer.ReadInto(reader, apu);
            StateBinarySerializer.ReadInto(reader, apu.Spc);
            StateBinarySerializer.ReadInto(reader, apu.Dsp);
            ReadChipState(reader, rom.Cx4, "CX4");
            ReadChipState(reader, rom.Dsp1, "DSP1");
            ReadChipState(reader, rom.SuperFx, "SuperFX");
            ReadChipState(reader, rom.Sa1, "SA1");
            rom.Cx4?.ResyncAfterLoad();

            _system.ROM.SetSystem(_system);
            _system.CPU.SetSystem(_system);
            if (_system.CPU is CPU cpu)
                cpu.RefreshTraceConfig();
            _system.PPU.SetSystem(_system);
            _system.APU.Attach();
            apu.ResyncAfterLoad();
            if (_system is SNESSystem snesSystem)
                snesSystem.ResyncAfterLoad();
            rom.ResyncCoprocessors(_system.Cycles);
        }
    }

    private static void WriteChipState(BinaryWriter writer, object? chip)
    {
        bool has = chip != null;
        writer.Write(has);
        if (has)
            StateBinarySerializer.WriteInto(writer, chip!);
    }

    private static void ReadChipState(BinaryReader reader, object? chip, string name)
    {
        bool has = reader.ReadBoolean();
        if (!has)
        {
            if (chip != null)
                throw new InvalidDataException($"Savestate expects no {name} chip, but ROM has one.");
            return;
        }

        if (chip == null)
            throw new InvalidDataException($"Savestate expects {name} chip, but ROM does not have one.");

        StateBinarySerializer.ReadInto(reader, chip);
    }

    public ReadOnlySpan<byte> GetFrameBuffer(out int width, out int height, out int stride)
    {
        width = _frameWidth;
        height = _frameHeight;
        stride = _frameStride;
        return _frameBuffer;
    }

    public bool TrySwapPresentationBuffer(ref byte[] buffer, out int width, out int height, out int stride)
    {
        lock (_stateLock)
        {
            width = _frameWidth;
            height = _frameHeight;
            stride = _frameStride;

            int requiredBytes = height * stride;
            if (width <= 0 || height <= 0 || stride <= 0 || _frameBuffer.Length < requiredBytes)
            {
                return false;
            }

            if (buffer.Length < requiredBytes)
            {
                buffer = new byte[requiredBytes];
            }

            (_frameBuffer, buffer) = (buffer, _frameBuffer);
            return true;
        }
    }

    public SnesPpuState GetPpuState()
    {
        if (_system.PPU is PPU ppu)
        {
            return new SnesPpuState(
                ppu.ForcedBlank,
                ppu.Brightness,
                ppu.Mode,
                ppu.OverscanEnabled,
                ppu.FrameOverscan,
                ppu.PseudoHires,
                ppu.Interlace,
                ppu.ObjInterlace,
                ppu.MainScreenMask,
                ppu.SubScreenMask,
                _system.InVblank,
                _system.InHblank,
                _system.InNmi,
                _system.XPos,
                _system.YPos);
        }

        return new SnesPpuState(
            ForcedBlank: false,
            Brightness: 0,
            Mode: 0,
            OverscanEnabled: false,
            FrameOverscan: false,
            PseudoHires: false,
            Interlace: false,
            ObjInterlace: false,
            MainScreenMask: 0,
            SubScreenMask: 0,
            InVblank: _system.InVblank,
            InHblank: _system.InHblank,
            InNmi: _system.InNmi,
            XPos: _system.XPos,
            YPos: _system.YPos);
    }

    public string? GetPpuDebugSnapshot()
    {
        return _system.PPU is PPU ppu ? ppu.GetDebugSnapshot() : null;
    }

    public bool TryGetFramePerfSummary(out string summary)
    {
        summary = _framePerfSummary;
        if (_system is SNESSystem snesSystem)
        {
            string corePerf = snesSystem.GetPerfSummary();
            if (!string.IsNullOrWhiteSpace(corePerf))
            {
                summary = string.IsNullOrWhiteSpace(summary)
                    ? corePerf
                    : $"{summary}\n{corePerf}";
            }
        }

        return !string.IsNullOrWhiteSpace(summary);
    }

    public string GetDivergenceCheckpoint()
    {
        lock (_stateLock)
        {
            ulong frameHash = HashBytes(_frameBuffer.AsSpan(0, Math.Min(_frameBuffer.Length, _frameHeight * _frameStride)));
            ulong vramHash = 0;
            ulong cgramHash = 0;
            ulong oamHash = 0;
            string ppuSummary = "ppu=none";
            if (_system.PPU is PPU ppu)
            {
                vramHash = ppu.ComputeVramHash();
                cgramHash = ppu.ComputeCgramHash();
                oamHash = ppu.ComputeOamHash();
                ppuSummary = ppu.GetDivergenceSummary();
            }

            string cpuSummary = _system.CPU is CPU cpu
                ? $"cpu=0x{cpu.ProgramCounter24:X6}"
                : "cpu=--";

            string sfxSummary = (_system.ROM as ROM)?.SuperFx is KSNES.Specialchips.SuperFX.SuperFx superFx
                ? superFx.GetDivergenceSummary()
                : "sfx=none";

            return $"emuFrame={_frameCounter} {cpuSummary} fb=0x{frameHash:X16} size={_frameWidth}x{_frameHeight} stride={_frameStride} " +
                $"vram=0x{vramHash:X16} cgram=0x{cgramHash:X16} oam=0x{oamHash:X16} {ppuSummary} {sfxSummary}";
        }
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

    public void SetExtendedInputState(ExtendedInputState input)
    {
        SetButton(SNESButton.Up, input.Up);
        SetButton(SNESButton.Down, input.Down);
        SetButton(SNESButton.Left, input.Left);
        SetButton(SNESButton.Right, input.Right);
        SetButton(SNESButton.A, input.South);
        SetButton(SNESButton.B, input.East);
        SetButton(SNESButton.X, input.North);
        SetButton(SNESButton.Y, input.West);
        SetButton(SNESButton.L, input.L1 || input.L2);
        SetButton(SNESButton.R, input.R1 || input.R2);
        SetButton(SNESButton.Start, input.Start);
        SetButton(SNESButton.Sel, input.Select || input.Menu);
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

    private void EnsureFrameBuffer(int width, int height)
    {
        _frameWidth = width;
        _frameHeight = height;
        _frameStride = width * 4;
        int needed = height * _frameStride;
        if (_frameBuffer.Length != needed)
            _frameBuffer = new byte[needed];
    }

    private void EnsureAudioBuffer(int samplesPerFrame)
    {
        int needed = samplesPerFrame * 2;
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
            l *= _masterVolumeScale * SnesMixScale;
            r *= _masterVolumeScale * SnesMixScale;
            dest[di++] = FloatToShort(SoftClip(l));
            dest[di++] = FloatToShort(SoftClip(r));
        }
    }

    private static short FloatToShort(float value)
    {
        float clamped = Math.Clamp(value, -1f, 1f);
        return (short)MathF.Round(clamped * short.MaxValue);
    }

    private static float SoftClip(float x)
    {
        // Simple cubic soft clip: preserves small signals, tames peaks.
        if (x <= -1f) return -1f;
        if (x >= 1f) return 1f;
        return x - (x * x * x) / 3f;
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

    private int GetSamplesPerFrame()
    {
        double fps = GetTargetFps(_regionOverride);
        if (fps <= 0)
            fps = 60.0;
        _audioSampleAccumulator += SnesAudioHandler.SampleRate / fps;
        int samples = (int)_audioSampleAccumulator;
        if (samples < 1)
            samples = 1;
        _audioSampleAccumulator -= samples;
        return samples;
    }

    private string BuildRomSummary()
    {
        var header = _system.ROM.Header;
        string name = header.Name?.Trim() ?? "Unknown";
        string romSize = $"{header.RomSize / 1024} KB";
        string ramSize = $"{header.RamSize / 1024} KB";
        string region = _romRegionHint == ConsoleRegion.Auto ? "?" : _romRegionHint.ToString();
        return $"SNES: {name} | ROM {romSize} | RAM {ramSize} | Region {region} | Speed 0x{header.Speed:X} | Type 0x{header.Type:X} | Chips 0x{header.Chips:X} | Chipset 0x{header.ChipsetByte:X2}";
    }

    public double GetTargetFps(ConsoleRegion overrideRegion)
    {
        ConsoleRegion region = overrideRegion == ConsoleRegion.Auto ? _romRegionHint : overrideRegion;
        return region == ConsoleRegion.EU ? PalFps : NtscFps;
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

    private static void ConvertArgbToBgra(int[] source, byte[] dest, int width, int height, int sourceStridePixels)
    {
        int bytesPerRow = width * sizeof(int);
        if (width == sourceStridePixels)
        {
            Buffer.BlockCopy(source, 0, dest, 0, height * bytesPerRow);
            return;
        }

        for (int y = 0; y < height; y++)
        {
            Buffer.BlockCopy(source, y * sourceStridePixels * sizeof(int), dest, y * bytesPerRow, bytesPerRow);
        }
    }

    private void ResetFramePerfCounters()
    {
        _framePerfWindowStartTicks = Stopwatch.GetTimestamp();
        _framePerfAccumulatedCoreTicks = 0;
        _framePerfAccumulatedBlitTicks = 0;
        _framePerfAccumulatedPcmTicks = 0;
        _framePerfAccumulatedBytes = 0;
        _framePerfSamples = 0;
        _framePerfSummary = "SNES draw --";
    }

    private void UpdateFramePerfStats(long coreTicks, long blitTicks, long pcmTicks, int byteCount)
    {
        _framePerfAccumulatedCoreTicks += coreTicks;
        _framePerfAccumulatedBlitTicks += blitTicks;
        _framePerfAccumulatedPcmTicks += pcmTicks;
        _framePerfAccumulatedBytes += byteCount;
        _framePerfSamples++;

        long nowTicks = Stopwatch.GetTimestamp();
        double windowMs = (nowTicks - _framePerfWindowStartTicks) * 1000.0 / Stopwatch.Frequency;
        if (windowMs < 250 || _framePerfSamples <= 0)
            return;

        double avgCoreMs = (_framePerfAccumulatedCoreTicks * 1000.0 / Stopwatch.Frequency) / _framePerfSamples;
        double avgBlitMs = (_framePerfAccumulatedBlitTicks * 1000.0 / Stopwatch.Frequency) / _framePerfSamples;
        double avgPcmMs = (_framePerfAccumulatedPcmTicks * 1000.0 / Stopwatch.Frequency) / _framePerfSamples;
        double mbPerSec = windowMs > 0
            ? (_framePerfAccumulatedBytes / (1024.0 * 1024.0)) / (windowMs / 1000.0)
            : 0;
        _framePerfSummary = $"SNES core:{avgCoreMs:0.0}ms  scanout:{avgBlitMs:0.0}ms  pcm:{avgPcmMs:0.0}ms  copy:{mbPerSec:0.0}MB/s";

        _framePerfWindowStartTicks = nowTicks;
        _framePerfAccumulatedCoreTicks = 0;
        _framePerfAccumulatedBlitTicks = 0;
        _framePerfAccumulatedPcmTicks = 0;
        _framePerfAccumulatedBytes = 0;
        _framePerfSamples = 0;
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
        public const int SampleRate = 44100;

        public float[] SampleBufferL { get; set; } = Array.Empty<float>();
        public float[] SampleBufferR { get; set; } = Array.Empty<float>();

        public void EnsureCapacity(int samplesPerFrame)
        {
            if (samplesPerFrame < 1)
                samplesPerFrame = 1;
            if (SampleBufferL.Length != samplesPerFrame)
            {
                SampleBufferL = new float[samplesPerFrame];
                SampleBufferR = new float[samplesPerFrame];
            }
        }

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

    private static ulong HashBytes(ReadOnlySpan<byte> data)
    {
        const ulong offset = 14695981039346656037UL;
        const ulong prime = 1099511628211UL;

        ulong hash = offset;
        foreach (byte value in data)
        {
            hash ^= value;
            hash *= prime;
        }

        return hash;
    }

    public readonly record struct SnesPpuState(
        bool ForcedBlank,
        int Brightness,
        int Mode,
        bool OverscanEnabled,
        bool FrameOverscan,
        bool PseudoHires,
        bool Interlace,
        bool ObjInterlace,
        byte MainScreenMask,
        byte SubScreenMask,
        bool InVblank,
        bool InHblank,
        bool InNmi,
        int XPos,
        int YPos);
}
