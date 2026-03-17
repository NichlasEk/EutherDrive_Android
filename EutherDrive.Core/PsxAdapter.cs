using System;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading;
using ProjectPSX;
using ProjectPSX.Devices.Input;
using ProjectPSX.IO;
using EutherDrive.Core.Savestates;

namespace EutherDrive.Core;

public sealed class PsxAdapter : IEmulatorCore, ISavestateCapable, IExtendedInputHandler
{
    private const uint OpaqueBlackPixel = 0xFF000000u;
    private static readonly Vector<uint> OpaqueAlphaVector = new(0xFF000000u);
    private const int SuperFastBootMaxTotalFrames = 2400;
    private const int SuperFastBootMaxPostBiosFrames = 900;
    private const int SuperFastBootRequiredVisibleFrames = 2;
    private const int SuperFastBootMinVisibleSamples = 8;

    public static string? BiosPath { get; set; }
    public static bool AnalogControllerEnabled { get; set; }
    public static bool FastLoadEnabled { get; set; }
    public static bool SuperFastBootEnabled { get; set; }
    public static FrameRateMode FrameRateMode { get; set; }
    public static PsxVideoStandardMode VideoStandardMode { get; set; }
    public long? FrameCounter => Interlocked.Read(ref _frameCounter);
    private sealed class PsxHostWindow : IHostWindow
    {
        private const double DefaultAspectRatio = 4.0 / 3.0;
        private readonly PsxAdapter _owner;
        private int _displayWidth = 320;
        private int _displayHeight = 240;
        private bool _is24Bit;
        private ushort _vramXStart;
        private ushort _vramYStart;
        private ushort _displayX1;
        private ushort _displayX2;
        private ushort _displayY1;
        private ushort _displayY2;

        public PsxHostWindow(PsxAdapter owner) => _owner = owner;

        public void Render(int[] vram, ushort[] vram1555)
        {
            if (!TryGetSourceViewport(out int sourceX, out int sourceY, out int sourceWidth, out int sourceHeight))
            {
                sourceX = _vramXStart;
                sourceY = _vramYStart;
                sourceWidth = _displayWidth;
                sourceHeight = _displayHeight;
            }

            _owner.UpdateFrame(vram, vram1555, sourceWidth, sourceHeight, (ushort)sourceX, (ushort)sourceY, _is24Bit);
        }

        public void SetDisplayMode(int horizontalRes, int verticalRes, bool is24BitDepth)
        {
            _displayWidth = horizontalRes > 0 ? horizontalRes : 320;
            _displayHeight = verticalRes > 0 ? verticalRes : 240;
            _is24Bit = is24BitDepth;
        }

        public void SetHorizontalRange(ushort displayX1, ushort displayX2)
        {
            _displayX1 = displayX1;
            _displayX2 = displayX2;
        }

        public void SetVRAMStart(ushort displayVRAMXStart, ushort displayVRAMYStart)
        {
            _vramXStart = displayVRAMXStart;
            _vramYStart = displayVRAMYStart;
        }

        public void SetVerticalRange(ushort displayY1, ushort displayY2)
        {
            _displayY1 = displayY1;
            _displayY2 = displayY2;
        }

        public void Play(byte[] samples) => _owner.PushAudio(samples);

        public bool TryGetVerticalPixelRange(out int y1, out int y2)
        {
            y1 = 0;
            y2 = 0;
            if (_displayY2 <= _displayY1)
                return false;

            int timing = (_displayY2 > 300 || _displayY1 > 300) ? 314 : 263;
            double scale = _displayHeight / (double)timing;
            int py1 = (int)Math.Round(_displayY1 * scale);
            int py2 = (int)Math.Round(_displayY2 * scale);

            if (py2 <= py1)
                return false;
            if (py1 < 0) py1 = 0;
            if (py2 > _displayHeight) py2 = _displayHeight;

            int range = py2 - py1;
            if (range >= _displayHeight - 2)
                return false;

            y1 = py1;
            y2 = py2;
            return true;
        }

        public bool TryGetSourceViewport(out int sourceX, out int sourceY, out int sourceWidth, out int sourceHeight)
        {
            sourceX = _vramXStart;
            sourceY = _vramYStart;
            sourceWidth = _displayWidth > 0 ? _displayWidth : 320;
            sourceHeight = _displayHeight > 0 ? _displayHeight : 240;

            if (_displayX2 > _displayX1)
            {
                int cyclesPerPixel = GetCyclesPerPixel();
                int displayCycles = _displayX2 - _displayX1;
                int visibleWidth = (((displayCycles / cyclesPerPixel) + 2) & ~3);
                if (visibleWidth > 0)
                    sourceWidth = visibleWidth;
            }

            if (_displayY2 > _displayY1)
            {
                int visibleHeight = _displayY2 - _displayY1;
                if (_displayHeight >= 480 && visibleHeight <= 288)
                    visibleHeight *= 2;
                if (visibleHeight > 0)
                    sourceHeight = visibleHeight;
            }

            if (sourceWidth <= 0 || sourceHeight <= 0)
                return false;

            if (sourceX < 0) sourceX = 0;
            if (sourceY < 0) sourceY = 0;
            int maxWidth = 1024 - sourceX;
            if (_is24Bit)
                maxWidth = (2048 - (sourceX * 2)) / 3;
            if (sourceWidth > maxWidth)
                sourceWidth = Math.Max(0, maxWidth);
            if (sourceY + sourceHeight > 512)
                sourceHeight = Math.Max(0, 512 - sourceY);

            return sourceWidth > 0 && sourceHeight > 0;
        }

        public double GetPresentationAspectRatio()
        {
            if (_displayX2 <= _displayX1 || _displayWidth <= 0)
                return DefaultAspectRatio;

            int displayCycles = _displayX2 - _displayX1;
            int nominalCycles = _displayWidth * GetCyclesPerPixel();
            if (displayCycles <= 0 || nominalCycles <= 0)
                return DefaultAspectRatio;

            double aspect = DefaultAspectRatio * (displayCycles / (double)nominalCycles);
            return aspect > 0.1 ? aspect : DefaultAspectRatio;
        }

        private int GetCyclesPerPixel() => _displayWidth switch
        {
            256 => 10,
            320 => 8,
            384 => 7,
            512 => 5,
            640 => 4,
            _ => 8
        };
    }

    private ProjectPSX.ProjectPSX? _core;
    private PsxHostWindow? _host;
    private string? _diskPath;
    private readonly object _stateLock = new();
    private readonly object _frameLock = new();
    private byte[] _workFrameBuffer = new byte[320 * 240 * 4];
    private byte[] _presentFrameBuffer = new byte[320 * 240 * 4];
    private byte[] _spareFrameBuffer = new byte[320 * 240 * 4];
    private int _workFrameWidth = 320;
    private int _workFrameHeight = 240;
    private int _workFrameStride = 320 * 4;
    private int _presentFrameWidth = 320;
    private int _presentFrameHeight = 240;
    private int _presentFrameStride = 320 * 4;
    private readonly object _audioLock = new();
    private short[] _audioQueue = Array.Empty<short>();
    private int _audioQueuedCount;
    private short[] _audioReadBuffer = Array.Empty<short>();
    private bool _dropAudioOutput;
    private float _masterVolumeScale = 1.0f;
    private long _frameCounter;
    private RomIdentity? _romIdentity;
    private string _bootEnvironmentSummary = "bios=(none)";
    public RomIdentity? RomIdentity => _romIdentity;

    public void LoadRom(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !VirtualFileSystem.Exists(path))
            throw new FileNotFoundException("PSX image not found.", path);

        _diskPath = path;
        if (!string.IsNullOrWhiteSpace(BiosPath))
            Environment.SetEnvironmentVariable("EUTHERDRIVE_PSX_BIOS", BiosPath);
        _bootEnvironmentSummary = BuildBootEnvironmentSummary();
        _host = new PsxHostWindow(this);
        bool superFastBoot = SuperFastBootEnabled && !path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);
        bool bootFastLoadEnabled = FastLoadEnabled || superFastBoot;
        _core = new ProjectPSX.ProjectPSX(_host, path, AnalogControllerEnabled, bootFastLoadEnabled, superFastBoot);
        ApplyConfiguredTimingToCore(_core);
        if (superFastBoot)
            RunSuperFastBoot(_core);
        if (bootFastLoadEnabled != FastLoadEnabled)
            _core.SetFastLoadEnabled(FastLoadEnabled);
        _frameCounter = 0;
        using Stream romStream = VirtualFileSystem.OpenRead(path);
        _romIdentity = new RomIdentity(
            Path.GetFileName(path),
            RomIdentity.ComputeSha256(romStream),
            PersistentStoragePath.ResolveSavestateDirectory(path, "psx"));
    }

    public void Reset()
    {
        if (string.IsNullOrWhiteSpace(_diskPath))
            return;
        LoadRom(_diskPath);
    }

    public void SaveState(BinaryWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);

        lock (_stateLock)
        {
            if (_core == null)
                throw new InvalidOperationException("PSX core is not loaded.");

            const int version = 1;
            writer.Write(version);
            writer.Write(_frameCounter);
            writer.Write(_core.BootBiosExited);

            StateBinarySerializer.WriteInto(writer, _core.CpuStateObject);

            _core.Bus.SaveRawState(writer);
            StateBinarySerializer.WriteInto(writer, _core.Bus);
            StateBinarySerializer.WriteInto(writer, _core.InterruptController);

            var dma = _core.Bus.Dma;
            writer.Write(dma.ChannelCount);
            for (int i = 0; i < dma.ChannelCount; i++)
                StateBinarySerializer.WriteInto(writer, dma.GetChannelStateObject(i));

            StateBinarySerializer.WriteInto(writer, _core.GPU);
            StateBinarySerializer.WriteInto(writer, _core.CDROM);
            StateBinarySerializer.WriteInto(writer, _core.JOYPAD);
            StateBinarySerializer.WriteInto(writer, _core.Timers);
            StateBinarySerializer.WriteInto(writer, _core.MDEC);
            _core.SPU.SaveRawState(writer);
            StateBinarySerializer.WriteInto(writer, _core.SPU);
        }
    }

    public void LoadState(BinaryReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);

        lock (_stateLock)
        {
            if (_core == null)
                throw new InvalidOperationException("PSX core is not loaded.");

            int version = reader.ReadInt32();
            if (version != 1)
                throw new InvalidDataException($"Unsupported PSX savestate version: {version}.");

            _frameCounter = reader.ReadInt64();
            _core.BootBiosExited = reader.ReadBoolean();

            StateBinarySerializer.ReadInto(reader, _core.CpuStateObject);

            _core.Bus.LoadRawState(reader);
            StateBinarySerializer.ReadInto(reader, _core.Bus);
            StateBinarySerializer.ReadInto(reader, _core.InterruptController);

            var dma = _core.Bus.Dma;
            int channelCount = reader.ReadInt32();
            if (channelCount != dma.ChannelCount)
                throw new InvalidDataException($"Unsupported PSX DMA channel count: {channelCount}.");
            for (int i = 0; i < channelCount; i++)
                StateBinarySerializer.ReadInto(reader, dma.GetChannelStateObject(i));

            StateBinarySerializer.ReadInto(reader, _core.GPU);
            if (_host != null)
                _core.GPU.ResyncAfterLoad(_host);
            ApplyConfiguredTimingToCore(_core);
            StateBinarySerializer.ReadInto(reader, _core.CDROM);
            StateBinarySerializer.ReadInto(reader, _core.JOYPAD);
            StateBinarySerializer.ReadInto(reader, _core.Timers);
            StateBinarySerializer.ReadInto(reader, _core.MDEC);
            _core.MDEC.ResyncAfterLoad();
            _core.SPU.LoadRawState(reader);
            StateBinarySerializer.ReadInto(reader, _core.SPU);

            lock (_audioLock)
            {
                _audioQueuedCount = 0;
            }
            lock (_frameLock)
            {
                Array.Clear(_workFrameBuffer, 0, _workFrameBuffer.Length);
                Array.Clear(_presentFrameBuffer, 0, _presentFrameBuffer.Length);
                Array.Clear(_spareFrameBuffer, 0, _spareFrameBuffer.Length);
            }
        }
    }

    public void RunFrame()
    {
        var core = _core;
        if (core == null)
            return;

        core.RunFrame();

        lock (_frameLock)
        {
            RotateFrameBuffers();
            _presentFrameWidth = _workFrameWidth;
            _presentFrameHeight = _workFrameHeight;
            _presentFrameStride = _workFrameStride;
        }

        Interlocked.Increment(ref _frameCounter);
    }

    public ReadOnlySpan<byte> GetFrameBuffer(out int width, out int height, out int stride)
    {
        lock (_frameLock)
        {
            width = _presentFrameWidth;
            height = _presentFrameHeight;
            stride = _presentFrameStride;
            return _presentFrameBuffer;
        }
    }

    public bool TryGetPresentationSize(out double width, out double height)
    {
        width = 0;
        height = 0;
        int presentHeight;
        lock (_frameLock)
            presentHeight = _presentFrameHeight;

        if (presentHeight <= 0)
            return false;

        double aspect = _host?.GetPresentationAspectRatio() ?? (4.0 / 3.0);
        width = Math.Round(presentHeight * aspect);
        height = presentHeight;
        return width > 0 && height > 0;
    }

    public ReadOnlySpan<short> GetAudioBuffer(out int sampleRate, out int channels)
    {
        sampleRate = 44100;
        channels = 2;
        lock (_audioLock)
        {
            if (_audioQueuedCount == 0)
                return ReadOnlySpan<short>.Empty;

            int count = _audioQueuedCount;

            if (_audioReadBuffer.Length < count)
                _audioReadBuffer = new short[count];

            _audioQueue.AsSpan(0, count).CopyTo(_audioReadBuffer.AsSpan(0, count));
            _audioQueuedCount = 0;
            return _audioReadBuffer.AsSpan(0, count);
        }
    }

    public void SetMasterVolumePercent(int percent)
    {
        if (percent < 0)
            percent = 0;
        else if (percent > 100)
            percent = 100;
        _masterVolumeScale = percent / 100f;
    }

    public void SetAnalogControllerEnabled(bool enabled)
    {
        AnalogControllerEnabled = enabled;
        _core?.SetAnalogControllerEnabled(enabled);
    }

    public void SetFastLoadEnabled(bool enabled)
    {
        FastLoadEnabled = enabled;
        _core?.SetFastLoadEnabled(enabled);
    }

    public void SetSuperFastBootEnabled(bool enabled)
    {
        SuperFastBootEnabled = enabled;
    }

    public void SetFrameRateMode(FrameRateMode mode)
    {
        FrameRateMode = mode;
        _core?.SetFrameRateOverrideHz(GetFrameRateOverrideHz(mode));
    }

    public void SetVideoStandardMode(PsxVideoStandardMode mode)
    {
        VideoStandardMode = mode;
        _core?.SetVideoStandardOverride(GetVideoStandardOverride(mode));
    }

    public double GetTargetFps()
    {
        if (_core != null)
            return _core.GetTargetFps();

        return FrameRateMode switch
        {
            FrameRateMode.Hz50 => 50.0,
            FrameRateMode.Hz60 => 60.0,
            _ => VideoStandardMode == PsxVideoStandardMode.PAL ? 49.76 : 59.29
        };
    }

    public bool TryGetDebugState(out string state)
    {
        lock (_stateLock)
        {
            if (_core == null)
            {
                state = string.Empty;
                return false;
            }

            state = _core.DebugStartSummary();
            return true;
        }
    }

    public bool TryGetBootProgressSummary(out string summary)
    {
        lock (_stateLock)
        {
            if (_core == null)
            {
                summary = string.Empty;
                return false;
            }

            summary = $"PSX pc={_core.DebugCurrentPC:x8} biosExited={(_core.BootBiosExited ? 1 : 0)} {_bootEnvironmentSummary}";
            return true;
        }
    }

    private static string BuildBootEnvironmentSummary()
    {
        string biosName = string.IsNullOrWhiteSpace(BiosPath) ? "(none)" : Path.GetFileName(BiosPath);
        if (string.IsNullOrWhiteSpace(BiosPath) || !VirtualFileSystem.Exists(BiosPath))
        {
            return $"bios={biosName} biosLen=missing fast={(FastLoadEnabled ? 1 : 0)} super={(SuperFastBootEnabled ? 1 : 0)}";
        }

        try
        {
            byte[] biosBytes = VirtualFileSystem.ReadAllBytes(BiosPath);
            byte[] hash = SHA256.HashData(biosBytes);
            string shortHash = Convert.ToHexString(hash.AsSpan(0, 4));
            return $"bios={biosName} biosLen={biosBytes.Length} biosSha={shortHash} fast={(FastLoadEnabled ? 1 : 0)} super={(SuperFastBootEnabled ? 1 : 0)}";
        }
        catch
        {
            return $"bios={biosName} biosLen=err fast={(FastLoadEnabled ? 1 : 0)} super={(SuperFastBootEnabled ? 1 : 0)}";
        }
    }

    public bool TryGetDebugCodeWindow(out string codeWindow, int wordsBefore = 8, int wordsAfter = 16)
    {
        lock (_stateLock)
        {
            if (_core == null)
            {
                codeWindow = string.Empty;
                return false;
            }

            codeWindow = _core.DebugCodeWindow(wordsBefore, wordsAfter);
            return true;
        }
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
        if (_core == null)
            return;

        SetButton(GamepadInputsEnum.Up, up);
        SetButton(GamepadInputsEnum.Down, down);
        SetButton(GamepadInputsEnum.Left, left);
        SetButton(GamepadInputsEnum.Right, right);
        SetButton(GamepadInputsEnum.S, a);     // Cross
        SetButton(GamepadInputsEnum.D, b);     // Circle
        SetButton(GamepadInputsEnum.A, c);     // Square
        SetButton(GamepadInputsEnum.Enter, start);
        SetButton(GamepadInputsEnum.W, x);     // Triangle
        SetButton(GamepadInputsEnum.Q, y);     // L1
        SetButton(GamepadInputsEnum.E, z);     // R1
        SetButton(GamepadInputsEnum.Space, mode);
    }

    public void SetExtendedInputState(ExtendedInputState input)
    {
        if (_core == null)
            return;

        SetButton(GamepadInputsEnum.Up, input.Up);
        SetButton(GamepadInputsEnum.Down, input.Down);
        SetButton(GamepadInputsEnum.Left, input.Left);
        SetButton(GamepadInputsEnum.Right, input.Right);
        SetButton(GamepadInputsEnum.S, input.South);   // Cross
        SetButton(GamepadInputsEnum.D, input.East);    // Circle
        SetButton(GamepadInputsEnum.A, input.West);    // Square
        SetButton(GamepadInputsEnum.W, input.North);   // Triangle
        SetButton(GamepadInputsEnum.Enter, input.Start);
        SetButton(GamepadInputsEnum.Space, input.Select || input.Menu);
        SetButton(GamepadInputsEnum.Q, input.L1);
        SetButton(GamepadInputsEnum.E, input.R1);
        SetButton(GamepadInputsEnum.D1, input.L2);
        SetButton(GamepadInputsEnum.D3, input.R2);
    }

    private void SetButton(GamepadInputsEnum button, bool pressed)
    {
        if (_core == null)
            return;
        if (pressed)
            _core.JoyPadDown(button);
        else
            _core.JoyPadUp(button);
    }

    private void UpdateFrame(int[] vram, ushort[] vram1555, int width, int height, ushort vramX, ushort vramY, bool is24Bit)
    {
        if (width <= 0 || height <= 0)
            return;

        int stride = width * 4;
        int required = stride * height;
        EnsureWorkFrameCapacity(required);

        _workFrameWidth = width;
        _workFrameHeight = height;
        _workFrameStride = stride;

        Span<uint> dstPixels = MemoryMarshal.Cast<byte, uint>(_workFrameBuffer.AsSpan(0, required));
        int baseX = vramX;
        int baseY = vramY;
        int vramWidth = 1024;
        int vramHeight = 512;
        if (is24Bit)
        {
            UpdateFrame24(vram1555, dstPixels, width, height, baseX, baseY, vramWidth, vramHeight);
            return;
        }

        for (int y = 0; y < height; y++)
        {
            Span<uint> dstRow = dstPixels.Slice(y * width, width);
            int srcY = baseY + y;
            if ((uint)srcY >= (uint)vramHeight)
            {
                dstRow.Fill(OpaqueBlackPixel);
                continue;
            }

            int srcRow = srcY * vramWidth;
            if (baseX >= vramWidth)
            {
                dstRow.Fill(OpaqueBlackPixel);
                continue;
            }

            int copyWidth = Math.Min(width, vramWidth - baseX);
            if (copyWidth <= 0)
            {
                dstRow.Fill(OpaqueBlackPixel);
                continue;
            }

            BlitOpaqueBgrx32(
                MemoryMarshal.Cast<int, uint>(vram.AsSpan(srcRow + baseX, copyWidth)),
                dstRow.Slice(0, copyWidth));

            if (copyWidth < width)
                dstRow.Slice(copyWidth).Fill(OpaqueBlackPixel);
        }
    }

    private static void BlitOpaqueBgrx32(ReadOnlySpan<uint> source, Span<uint> destination)
    {
        int x = 0;
        if (Vector.IsHardwareAccelerated)
        {
            int vectorWidth = Vector<uint>.Count;
            for (; x <= source.Length - vectorWidth; x += vectorWidth)
            {
                (new Vector<uint>(source.Slice(x, vectorWidth)) | OpaqueAlphaVector).CopyTo(destination.Slice(x, vectorWidth));
            }
        }

        for (; x < source.Length; x++)
            destination[x] = source[x] | OpaqueBlackPixel;
    }

    private void UpdateFrame24(ushort[] vram1555, Span<uint> dstPixels, int width, int height, int baseX, int baseY, int vramWidth, int vramHeight)
    {
        for (int y = 0; y < height; y++)
        {
            Span<uint> dstRow = dstPixels.Slice(y * width, width);
            int srcY = baseY + y;
            if ((uint)srcY >= (uint)vramHeight)
            {
                ClearFrameRow(dstRow);
                continue;
            }

            int rowBase = srcY * vramWidth;
            int rowByteBase = baseX * 2;
            int maxBytes = vramWidth * 2;
            if (rowByteBase >= maxBytes)
            {
                ClearFrameRow(dstRow);
                continue;
            }

            for (int x = 0; x < width; x++)
            {
                int pixelByteIndex = rowByteBase + (x * 3);
                if (pixelByteIndex + 2 >= maxBytes)
                {
                    dstRow.Slice(x).Fill(OpaqueBlackPixel);
                    continue;
                }

                byte r = ReadVramByte(vram1555, rowBase, pixelByteIndex);
                byte g = ReadVramByte(vram1555, rowBase, pixelByteIndex + 1);
                byte b = ReadVramByte(vram1555, rowBase, pixelByteIndex + 2);

                dstRow[x] = OpaqueBlackPixel | ((uint)r << 16) | ((uint)g << 8) | b;
            }
        }
    }

    private static byte ReadVramByte(ushort[] vram1555, int rowBase, int byteIndex)
    {
        int wordOffset = byteIndex >> 1;
        ushort word = vram1555[rowBase + wordOffset];
        return (byte)(((byteIndex & 1) == 0) ? (word & 0xFF) : (word >> 8));
    }

    private static void ClearFrameRow(Span<uint> row)
    {
        row.Fill(OpaqueBlackPixel);
    }

    private void EnsureWorkFrameCapacity(int required)
    {
        if (_workFrameBuffer.Length < required)
            _workFrameBuffer = new byte[required];
        if (_presentFrameBuffer.Length < required)
            _presentFrameBuffer = new byte[required];
        if (_spareFrameBuffer.Length < required)
            _spareFrameBuffer = new byte[required];
    }

    private void RotateFrameBuffers()
    {
        (_presentFrameBuffer, _spareFrameBuffer, _workFrameBuffer) = (_workFrameBuffer, _presentFrameBuffer, _spareFrameBuffer);
    }

    private static double? GetFrameRateOverrideHz(FrameRateMode mode)
    {
        return mode switch
        {
            FrameRateMode.Hz50 => 50.0,
            FrameRateMode.Hz60 => 60.0,
            _ => null
        };
    }

    private static bool? GetVideoStandardOverride(PsxVideoStandardMode mode)
    {
        return mode switch
        {
            PsxVideoStandardMode.PAL => true,
            PsxVideoStandardMode.NTSC => false,
            _ => null
        };
    }

    private static void ApplyConfiguredTimingToCore(ProjectPSX.ProjectPSX core)
    {
        core.SetVideoStandardOverride(GetVideoStandardOverride(VideoStandardMode));
        core.SetFrameRateOverrideHz(GetFrameRateOverrideHz(FrameRateMode));
    }

    private void RunSuperFastBoot(ProjectPSX.ProjectPSX core)
    {
        _dropAudioOutput = true;
        bool visibleFrameObserved = false;
        int visibleFrames = 0;
        int postBiosFrames = 0;
        int totalFrames = 0;
        var stopwatch = Stopwatch.StartNew();

        try
        {
            for (; totalFrames < SuperFastBootMaxTotalFrames; totalFrames++)
            {
                core.RunFrame();

                if (core.BootBiosExited)
                    postBiosFrames++;

                if (core.BootBiosExited && HasMeaningfulWorkFrame())
                {
                    visibleFrames++;
                    if (visibleFrames >= SuperFastBootRequiredVisibleFrames)
                    {
                        visibleFrameObserved = true;
                        break;
                    }
                }
                else
                {
                    visibleFrames = 0;
                }

                if (core.BootBiosExited && postBiosFrames >= SuperFastBootMaxPostBiosFrames)
                    break;
            }
        }
        finally
        {
            _dropAudioOutput = false;
            lock (_audioLock)
                _audioQueuedCount = 0;
        }

        PromoteWorkFrameToPresent();
        Console.WriteLine(
            $"[PSX-SUPERFAST] bootedFrames={totalFrames + 1} biosExited={(core.BootBiosExited ? 1 : 0)} " +
            $"postBiosFrames={postBiosFrames} visible={(visibleFrameObserved ? 1 : 0)} elapsedMs={stopwatch.ElapsedMilliseconds}");
    }

    private bool HasMeaningfulWorkFrame()
    {
        int width = _workFrameWidth;
        int height = _workFrameHeight;
        int stride = _workFrameStride;
        if (width <= 0 || height <= 0 || stride <= 0)
            return false;

        int byteLength = stride * height;
        if (byteLength <= 0 || byteLength > _workFrameBuffer.Length)
            return false;

        ReadOnlySpan<uint> pixels = MemoryMarshal.Cast<byte, uint>(_workFrameBuffer.AsSpan(0, byteLength));
        int pixelCount = width * height;
        if (pixelCount <= 0 || pixels.Length < pixelCount)
            return false;

        int step = Math.Max(1, pixelCount / 4096);
        int samples = 0;
        int nonBlackSamples = 0;
        for (int i = 0; i < pixelCount; i += step)
        {
            samples++;
            if (pixels[i] != OpaqueBlackPixel)
                nonBlackSamples++;
        }

        int threshold = Math.Max(SuperFastBootMinVisibleSamples, samples / 64);
        return nonBlackSamples >= threshold;
    }

    private void PromoteWorkFrameToPresent()
    {
        if (_workFrameWidth <= 0 || _workFrameHeight <= 0 || _workFrameStride <= 0)
            return;

        lock (_frameLock)
        {
            RotateFrameBuffers();
            _presentFrameWidth = _workFrameWidth;
            _presentFrameHeight = _workFrameHeight;
            _presentFrameStride = _workFrameStride;
        }
    }

    private void PushAudio(byte[] samples)
    {
        if (_dropAudioOutput)
            return;

        int sampleCount = samples.Length / 2;
        if (sampleCount == 0)
            return;

        lock (_audioLock)
        {
            int needed = _audioQueuedCount + sampleCount;
            if (_audioQueue.Length < needed)
            {
                int newSize = Math.Max(needed, _audioQueue.Length == 0 ? sampleCount : _audioQueue.Length * 2);
                Array.Resize(ref _audioQueue, newSize);
            }

            int si = 0;
            int di = _audioQueuedCount;
            if (_masterVolumeScale == 1.0f)
            {
                MemoryMarshal.Cast<byte, short>(samples.AsSpan()).CopyTo(_audioQueue.AsSpan(di, sampleCount));
                di += sampleCount;
            }
            else
            {
                for (int i = 0; i < sampleCount; i++)
                {
                    short raw = (short)(samples[si] | (samples[si + 1] << 8));
                    int scaled = (int)(raw * _masterVolumeScale);
                    if (scaled > short.MaxValue)
                        scaled = short.MaxValue;
                    else if (scaled < short.MinValue)
                        scaled = short.MinValue;
                    _audioQueue[di++] = (short)scaled;
                    si += 2;
                }
            }
            _audioQueuedCount = di;
        }
    }
}
