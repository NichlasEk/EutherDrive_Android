namespace EutherDrive.Core.Arcade.Igs;

using System.IO.Compression;
using EutherDrive.Core.Savestates;

public sealed class Pgm2Adapter : IEmulatorCore, ISavestateCapable, IDisposable, mame.IPgmArm7Bus
{
    private const string SavestateMagic = "PGM2NAT";
    private const int SavestateVersion = 5;
    private const int Width = 448;
    private const int Height = 224;
    private const int Stride = Width * 4;
    private const int CpuClockHz = 100_000_000;
    private const double RefreshHz = 59.08;
    private const int CyclesPerFrame = (int)(CpuClockHz / RefreshHz);
    private const int AudioRate = 44_100;
    private const int AudioChannels = 2;
    private const uint AicBase = 0xfffff000;
    private const int AicMcuSource = 3;
    private const int AicVblankSource = 12;

    private static readonly string[] KnownDriverNames =
    {
        "kov2nl",
        "kov2nl_301",
        "kov2nl_300",
        "kov2nl_302cn",
        "kov2nl_301cn",
        "kov2nl_300cn",
        "orleg2",
        "orleg2_103",
        "orleg2_101",
        "orleg2_104cn",
        "orleg2_103cn",
        "orleg2_101cn",
        "orleg2_104jp",
        "orleg2_103jp",
        "orleg2_101jp",
        "ddpdojt",
        "kov3",
        "kov3_102",
        "kov3_101",
        "kov3_100",
        "kof98umh",
        "bubucar"
    };

    private static readonly HashSet<string> DriverNames = new(KnownDriverNames, StringComparer.OrdinalIgnoreCase);
    private static readonly bool Trace = Environment.GetEnvironmentVariable("EUTHERDRIVE_PGM2_TRACE") == "1";
    private static readonly bool TraceUnknown = Environment.GetEnvironmentVariable("EUTHERDRIVE_PGM2_TRACE_UNKNOWN") == "1";
    private static readonly bool TraceStack = Environment.GetEnvironmentVariable("EUTHERDRIVE_PGM2_TRACE_STACK") == "1";
    private static readonly string SpriteMaskOrder = (Environment.GetEnvironmentVariable("EUTHERDRIVE_PGM2_SPRITE_MASK_ORDER") ?? "be").Trim().ToLowerInvariant();
    private static readonly string SpriteKeyMode = (Environment.GetEnvironmentVariable("EUTHERDRIVE_PGM2_SPRITE_KEY_MODE") ?? "reverse-xor").Trim().ToLowerInvariant();

    private readonly mame.PgmArm7Core _cpu;
    private readonly byte[] _frameBuffer = new byte[Height * Stride];
    private readonly short[] _audioSilence = new short[(int)(AudioRate / RefreshHz) * AudioChannels];
    private readonly byte[] _internalRom = new byte[0x4000];
    private readonly byte[] _mainRom = new byte[0x1000000];
    private readonly byte[] _mainRomEncrypted = new byte[0x1000000];
    private readonly byte[] _textRom = new byte[0x200000];
    private readonly byte[] _bgTileRom = new byte[0x1000000];
    private readonly byte[] _spriteMaskRom = new byte[0x2000000];
    private readonly byte[] _spriteColorRom = new byte[0x4000000];
    private readonly byte[] _sram = new byte[0x10000];
    private readonly byte[] _mainRam = new byte[0x80000];
    private readonly byte[] _spriteVideoRam = new byte[0x2000];
    private readonly byte[] _bgVideoRam = new byte[0x2000];
    private readonly byte[] _fgVideoRam = new byte[0x6000];
    private readonly byte[] _spritePaletteRam = new byte[0x4000];
    private readonly byte[] _bgPaletteRam = new byte[0x2000];
    private readonly byte[] _textPaletteRam = new byte[0x800];
    private readonly byte[] _spriteZoomRam = new byte[0x200];
    private readonly byte[] _lineRam = new byte[0x400];
    private readonly byte[] _shareRam = new byte[0x100];
    private readonly byte[] _gpuRegs = new byte[0x40];
    private readonly byte[] _encryptionTable = new byte[0x100];
    private readonly byte[] _ymzRegs = new byte[4];
    private readonly uint[] _mcuRegs = new uint[8];
    private readonly uint[] _aicSourceModes = new uint[32];
    private readonly uint[] _aicSourceVectors = new uint[32];
    private readonly int[] _waitstatesNonseq16 = new int[16];
    private readonly int[] _waitstatesNonseq32 = new int[16];
    private readonly int[] _waitstatesSeq16 = new int[16];
    private readonly int[] _waitstatesSeq32 = new int[16];

    private RomIdentity? _romIdentity;
    private Pgm2InputState _input;
    private bool _loaded;
    private string _driverName = string.Empty;
    private string _romPath = string.Empty;
    private long _frameCounter;
    private long _targetCycles;
    private uint _lastPrefetchedPc;
    private uint _openBus;
    private uint _input0 = 0xffffffff;
    private uint _input1 = 0xffffffff;
    private uint _shareBank;
    private uint _spriteKey;
    private uint _realSpriteKey;
    private uint _mcuResult0;
    private uint _mcuResult1;
    private byte _mcuLastCommand;
    private bool _hasDecrypted;
    private uint _lastReadAddress;
    private uint _lastWriteAddress;
    private uint _lastWriteValue;
    private int _romBytes;
    private int _internalRomBytes;
    private string _loadedMainProgramName = string.Empty;
    private string _loadedInternalRomName = string.Empty;
    private int _textRomBytes;
    private int _bgTileRomBytes;
    private int _spriteMaskRomBytes;
    private int _spriteColorRomBytes;
    private int _mainRamWrites;
    private int _videoWrites;
    private int _paletteWrites;
    private int _renderedBgPixels;
    private int _renderedSpritePixels;
    private int _renderedFgPixels;
    private int _fgTileEntries;
    private int _fgFirstColumn;
    private int _fgLastColumn;
    private int _bgTileEntries;
    private int _bgFirstColumn;
    private int _bgLastColumn;
    private int _mcuWrites;
    private int _aicReads;
    private int _aicWrites;
    private int _irqAsserts;
    private int _vblankIrqs;
    private int _mcuIrqs;
    private int _encryptionWrites;
    private int _encryptionTriggers;
    private int _externalFetches;
    private int _ymzReads;
    private int _ymzWrites;
    private int _unknownReads;
    private int _unknownWrites;
    private int _stackTraceLines;
    private uint _lastExternalFetchAddress;
    private uint _aicMask;
    private uint _aicPending;
    private uint _aicActiveSource = uint.MaxValue;
    private uint _aicSpuriousVector;

    public Pgm2Adapter()
    {
        _cpu = new mame.PgmArm7Core(this);
        for (int i = 0; i < 16; i++)
        {
            _waitstatesNonseq16[i] = 1;
            _waitstatesNonseq32[i] = 1;
            _waitstatesSeq16[i] = 1;
            _waitstatesSeq32[i] = 1;
        }
    }

    public int[] WaitstatesNonseq16 => _waitstatesNonseq16;
    public int[] WaitstatesNonseq32 => _waitstatesNonseq32;
    public int[] WaitstatesSeq16 => _waitstatesSeq16;
    public int[] WaitstatesSeq32 => _waitstatesSeq32;
    public uint LastPrefetchedPc { get => _lastPrefetchedPc; set => _lastPrefetchedPc = value; }
    public RomIdentity? RomIdentity => _romIdentity;
    public long? FrameCounter => _loaded ? _frameCounter : null;
    public double GetTargetFps() => RefreshHz;

    public string DebugSummary
        => !_loaded
            ? "not loaded"
            : $"drv={_driverName} frame={_frameCounter} pc=0x{CurrentPc:X8} cpsr=0x{_cpu.GetCpsrRaw():X8} thumb={(_cpu.ThumbMode ? 1 : 0)} crash={(_cpu.CrashDetected ? 1 : 0)} crashPc=0x{_cpu.CrashPc:X8} cyc={_cpu.Cycles}/{_targetCycles} r0=0x{_cpu.Registers[0]:X8} r1=0x{_cpu.Registers[1]:X8} r2=0x{_cpu.Registers[2]:X8} r3=0x{_cpu.Registers[3]:X8} r4=0x{_cpu.Registers[4]:X8} r5=0x{_cpu.Registers[5]:X8} r6=0x{_cpu.Registers[6]:X8} r7=0x{_cpu.Registers[7]:X8} r8=0x{_cpu.Registers[8]:X8} sp=0x{_cpu.Registers[13]:X8} lr=0x{_cpu.Registers[14]:X8} rom={_romBytes:X} int={_internalRomBytes:X} gfx={_textRomBytes:X}/{_bgTileRomBytes:X}/{_spriteMaskRomBytes:X}/{_spriteColorRomBytes:X} rd=0x{_lastReadAddress:X8} wr=0x{_lastWriteAddress:X8}:0x{_lastWriteValue:X8} ramW={_mainRamWrites} vidW={_videoWrites} palW={_paletteWrites} pix={_renderedBgPixels}/{_renderedSpritePixels}/{_renderedFgPixels} fg={_fgTileEntries}:{_fgFirstColumn}-{_fgLastColumn} bg={_bgTileEntries}:{_bgFirstColumn}-{_bgLastColumn} gpu={ReadGpu16(0):X4}/{ReadGpu16(2):X4}/{ReadGpu16(8):X4}/{ReadGpu16(0x0a):X4}/{ReadGpu16(0x0e):X4}/{ReadGpu16(0x14):X4}/{ReadGpu16(0x16):X4} vw={CurrentVisibleWidth()} sk=0x{CurrentSpriteMaskKey():X8} smo={SpriteMaskOrder} skm={SpriteKeyMode} mcuW={_mcuWrites} ymz={_ymzReads}/{_ymzWrites}:0x{_ymzRegs[0]:X2}:0x{_ymzRegs[1]:X2} aic={_aicPending:X8}/{_aicMask:X8}/{_aicActiveSource:X2} aic3=0x{_aicSourceModes[AicMcuSource]:X8}:0x{_aicSourceVectors[AicMcuSource]:X8} aic12=0x{_aicSourceModes[AicVblankSource]:X8}:0x{_aicSourceVectors[AicVblankSource]:X8} irq={_irqAsserts}/{_vblankIrqs}/{_mcuIrqs} aicRW={_aicReads}/{_aicWrites} encW={_encryptionWrites}/{_encryptionTriggers} dec={(_hasDecrypted ? 1 : 0)} extF={_externalFetches}:0x{_lastExternalFetchAddress:X8} unk={_unknownReads}/{_unknownWrites}{(_cpu.CrashDetected ? $" trace={CrashTraceSummary}" : string.Empty)}";

    public string CrashTraceSummary => BuildCrashTraceSummary();

    private uint CurrentPc => _cpu.ThumbMode ? _cpu.Registers[15] - 4u : _cpu.Registers[15] - 8u;

    private string BuildCrashTraceSummary()
    {
        if (!_loaded || !_cpu.CrashDetected)
            return string.Empty;

        var parts = new string[64];
        int start = _cpu.PcTraceIndex - parts.Length;
        for (int i = 0; i < parts.Length; i++)
        {
            int slot = (start + i) & (_cpu.PcTrace.Length - 1);
            bool thumb = _cpu.PcTraceThumb[slot & (_cpu.PcTraceThumb.Length - 1)];
            parts[i] = $"{(thumb ? "T" : "A")}:{_cpu.PcTrace[slot]:X8}";
        }

        return string.Join(",", parts);
    }

    public static bool IsSupportedArchive(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !RomArchiveExtractor.IsArchivePath(path))
            return false;

        if (IsSupportedDriverName(Path.GetFileNameWithoutExtension(path).Trim()))
            return true;

        try
        {
            using ZipArchive archive = ZipFile.OpenRead(path);
            return archive.Entries.Any(e => IsKnownPgm2Entry(Path.GetFileName(e.FullName)));
        }
        catch
        {
            return false;
        }
    }

    public static bool IsSupportedDriverName(string driverName)
        => DriverNames.Contains(driverName);

    public static Pgm2AuxFileReport InspectAuxFiles(string path)
    {
        string driverName = DetectDriverName(path);
        Pgm2AuxFileSpec spec = GetAuxFileSpec(driverName);
        var present = new List<string>();
        var missing = new List<string>();

        foreach (string name in spec.RequiredFiles)
        {
            if (ContainsArchiveEntry(path, name) || FindSidecarFile(path, name) != null)
                present.Add(name);
            else
                missing.Add(name);
        }

        return new Pgm2AuxFileReport(driverName, present.ToArray(), missing.ToArray());
    }

    public void LoadRom(string path)
    {
        if (!IsSupportedArchive(path))
            throw new NotSupportedException($"'{Path.GetFileName(path)}' is not a known PGM2 MAME set.");

        Array.Clear(_internalRom);
        Array.Clear(_mainRom);
        Array.Clear(_mainRomEncrypted);
        Array.Clear(_textRom);
        Array.Clear(_bgTileRom);
        Array.Clear(_spriteMaskRom);
        Array.Clear(_spriteColorRom);
        Array.Clear(_sram);
        Array.Clear(_mainRam);
        Array.Clear(_spriteVideoRam);
        Array.Clear(_bgVideoRam);
        Array.Clear(_fgVideoRam);
        Array.Clear(_spritePaletteRam);
        Array.Clear(_bgPaletteRam);
        Array.Clear(_textPaletteRam);
        Array.Clear(_spriteZoomRam);
        Array.Clear(_lineRam);
        Array.Clear(_shareRam);
        Array.Clear(_gpuRegs);
        Array.Clear(_encryptionTable);
        Array.Clear(_ymzRegs);
        Array.Clear(_mcuRegs);
        ResetAic();

        _romPath = Path.GetFullPath(path);
        _driverName = DetectDriverName(path);
        _romBytes = 0;
        _internalRomBytes = 0;
        _loadedMainProgramName = string.Empty;
        _loadedInternalRomName = string.Empty;
        _textRomBytes = 0;
        _bgTileRomBytes = 0;
        _spriteMaskRomBytes = 0;
        _spriteColorRomBytes = 0;
        _hasDecrypted = false;
        _shareBank = 0;
        _spriteKey = 0;
        _realSpriteKey = 0;
        _mcuResult0 = 0;
        _mcuResult1 = 0;
        _mcuLastCommand = 0;
        _frameCounter = 0;
        _targetCycles = 0;
        _mainRamWrites = _videoWrites = _paletteWrites = _renderedBgPixels = _renderedFgPixels = _mcuWrites = _aicReads = _aicWrites = _irqAsserts = _vblankIrqs = _mcuIrqs = _encryptionWrites = _encryptionTriggers = _externalFetches = _ymzReads = _ymzWrites = 0;
        _lastExternalFetchAddress = 0;
        _unknownReads = _unknownWrites = 0;

        using (ZipArchive archive = ZipFile.OpenRead(path))
            LoadRegions(archive);

        LoadSidecarAuxFiles(path);
        Array.Copy(_mainRom, _mainRomEncrypted, _mainRom.Length);

        using (FileStream fs = File.OpenRead(path))
            _romIdentity = new RomIdentity(Path.GetFileName(path), RomIdentity.ComputeSha256(fs), Path.GetDirectoryName(_romPath));

        _cpu.Reset(0);
        _loaded = true;
        UpdateInputPorts();
        DrawFrame();

        Console.WriteLine($"[PGM2] Loaded {_driverName}: internal={_loadedInternalRomName}:0x{_internalRomBytes:X} main={_loadedMainProgramName}:0x{_romBytes:X} missing=[{string.Join(",", InspectAuxFiles(path).MissingFiles)}]");
    }

    public void Reset()
    {
        if (!_loaded)
            return;

        Array.Copy(_mainRomEncrypted, _mainRom, _mainRom.Length);
        Array.Clear(_mainRam);
        Array.Clear(_shareRam);
        Array.Clear(_gpuRegs);
        Array.Clear(_ymzRegs);
        Array.Clear(_mcuRegs);
        ResetAic();
        _hasDecrypted = false;
        _shareBank = 0;
        _spriteKey = 0;
        _realSpriteKey = 0;
        _mcuResult0 = 0;
        _mcuResult1 = 0;
        _mcuLastCommand = 0;
        _ymzReads = 0;
        _ymzWrites = 0;
        _frameCounter = 0;
        _targetCycles = 0;
        _cpu.Reset(0);
        DrawFrame();
    }

    public void RunFrame()
    {
        if (!_loaded)
            return;

        UpdateInputPorts();
        DrawFrame();
        SetAicLine(AicVblankSource, true);
        _targetCycles += CyclesPerFrame;
        _cpu.Run(_targetCycles);

        if (Trace)
            Console.WriteLine($"[PGM2] {DebugSummary}");

        _frameCounter++;
    }

    public ReadOnlySpan<byte> GetFrameBuffer(out int width, out int height, out int stride)
    {
        width = Width;
        height = Height;
        stride = Stride;
        return _frameBuffer;
    }

    public void DumpDebugLayers(string directory, string prefix)
    {
        Directory.CreateDirectory(directory);
        byte[] savedFrame = (byte[])_frameBuffer.Clone();
        int savedBgPixels = _renderedBgPixels;
        int savedSpritePixels = _renderedSpritePixels;
        int savedFgPixels = _renderedFgPixels;

        try
        {
            WriteFgTileDump(Path.Combine(directory, $"{prefix}_fg_tiles.txt"));
            WriteSpriteListDump(Path.Combine(directory, $"{prefix}_sprites.txt"));

            ClearFrame(0xff000000);
            if (_bgTileRomBytes > 0)
                DrawBgTilemap();
            WriteFrameBufferPpm(Path.Combine(directory, $"{prefix}_bg.ppm"));

            ClearFrame(0xff000000);
            if (_spriteMaskRomBytes > 0 && _spriteColorRomBytes > 0)
            {
                DrawSprites(1);
                DrawSprites(0);
            }
            WriteFrameBufferPpm(Path.Combine(directory, $"{prefix}_sprites.ppm"));

            ClearFrame(0xff000000);
            if (_textRomBytes > 0)
                DrawTextTilemap();
            WriteFrameBufferPpm(Path.Combine(directory, $"{prefix}_fg.ppm"));

            ClearFrame(ReadPaletteColor(_bgPaletteRam, 0));
            if (_spriteMaskRomBytes > 0 && _spriteColorRomBytes > 0)
                DrawSprites(1);
            WriteFrameBufferPpm(Path.Combine(directory, $"{prefix}_sprite_pri1.ppm"));

            ClearFrame(0xff000000);
            if (_spriteMaskRomBytes > 0 && _spriteColorRomBytes > 0)
                DrawSprites(0);
            WriteFrameBufferPpm(Path.Combine(directory, $"{prefix}_sprite_pri0.ppm"));
        }
        finally
        {
            savedFrame.CopyTo(_frameBuffer, 0);
            _renderedBgPixels = savedBgPixels;
            _renderedSpritePixels = savedSpritePixels;
            _renderedFgPixels = savedFgPixels;
        }
    }

    public ReadOnlySpan<short> GetAudioBuffer(out int sampleRate, out int channels)
    {
        sampleRate = AudioRate;
        channels = AudioChannels;
        return _audioSilence;
    }

    public void SetMasterVolumePercent(int percent)
    {
    }

    public void SetInputState(
        bool up, bool down, bool left, bool right,
        bool a, bool b, bool c, bool start,
        bool x, bool y, bool z, bool mode,
        PadType padType)
    {
        _input = new Pgm2InputState(up, down, left, right, a, b, c, start, x, y, z, mode);
    }

    public void SaveState(BinaryWriter writer)
    {
        writer.Write(SavestateMagic);
        writer.Write(SavestateVersion);
        writer.Write(_driverName);
        writer.Write(_frameCounter);
        writer.Write(_targetCycles);
        writer.Write(_cpu.Cycles);
        writer.Write(_cpu.GetCpsrRaw());
        for (int i = 0; i < _cpu.Registers.Length; i++)
            writer.Write(_cpu.Registers[i]);
        _cpu.SerializePipeline(writer);
        _cpu.SerializeBankedState(writer);
        writer.Write(_mainRam);
        writer.Write(_spriteVideoRam);
        writer.Write(_bgVideoRam);
        writer.Write(_fgVideoRam);
        writer.Write(_spritePaletteRam);
        writer.Write(_bgPaletteRam);
        writer.Write(_textPaletteRam);
        writer.Write(_spriteZoomRam);
        writer.Write(_lineRam);
        writer.Write(_gpuRegs);
        writer.Write(_sram);
        writer.Write(_shareRam);
        writer.Write(_encryptionTable);
        writer.Write(_shareBank);
        writer.Write(_spriteKey);
        writer.Write(_realSpriteKey);
        writer.Write(_mcuResult0);
        writer.Write(_mcuResult1);
        writer.Write(_mcuLastCommand);
        writer.Write(_hasDecrypted);
        for (int i = 0; i < _aicSourceModes.Length; i++)
            writer.Write(_aicSourceModes[i]);
        for (int i = 0; i < _aicSourceVectors.Length; i++)
            writer.Write(_aicSourceVectors[i]);
        writer.Write(_aicMask);
        writer.Write(_aicPending);
        writer.Write(_aicActiveSource);
        writer.Write(_aicSpuriousVector);
    }

    public void LoadState(BinaryReader reader)
    {
        string magic = reader.ReadString();
        int version = reader.ReadInt32();
        if (magic != SavestateMagic || version < 1 || version > SavestateVersion)
            throw new InvalidDataException("Unsupported PGM2 savestate.");

        _driverName = reader.ReadString();
        _frameCounter = reader.ReadInt64();
        _targetCycles = reader.ReadInt64();
        _cpu.Cycles = version >= 4 ? reader.ReadInt64() : _targetCycles;
        uint cpsr = reader.ReadUInt32();
        for (int i = 0; i < _cpu.Registers.Length; i++)
            _cpu.Registers[i] = reader.ReadUInt32();
        _cpu.SetCpsrForStateLoad(cpsr);
        _cpu.DeserializePipeline(reader);
        if (version >= 5)
            _cpu.DeserializeBankedState(reader);
        else
            _cpu.SeedMissingBankedStateFromVisibleRegisters();
        ReadExact(reader, _mainRam);
        if (version >= 3)
        {
            ReadExact(reader, _spriteVideoRam);
            ReadExact(reader, _bgVideoRam);
            ReadExact(reader, _fgVideoRam);
            ReadExact(reader, _spritePaletteRam);
            ReadExact(reader, _bgPaletteRam);
            ReadExact(reader, _textPaletteRam);
            ReadExact(reader, _spriteZoomRam);
            ReadExact(reader, _lineRam);
            ReadExact(reader, _gpuRegs);
        }
        else
        {
            Array.Clear(_spriteVideoRam);
            Array.Clear(_bgVideoRam);
            Array.Clear(_fgVideoRam);
            Array.Clear(_spritePaletteRam);
            Array.Clear(_bgPaletteRam);
            Array.Clear(_textPaletteRam);
            Array.Clear(_spriteZoomRam);
            Array.Clear(_lineRam);
            Array.Clear(_gpuRegs);
        }
        ReadExact(reader, _sram);
        ReadExact(reader, _shareRam);
        ReadExact(reader, _encryptionTable);
        _shareBank = reader.ReadUInt32();
        _spriteKey = reader.ReadUInt32();
        _realSpriteKey = reader.ReadUInt32();
        if (version >= 2)
        {
            _mcuResult0 = reader.ReadUInt32();
            _mcuResult1 = reader.ReadUInt32();
            _mcuLastCommand = reader.ReadByte();
        }
        else
        {
            _mcuResult0 = 0;
            _mcuResult1 = 0;
            _mcuLastCommand = 0;
        }
        _hasDecrypted = reader.ReadBoolean();
        if (version >= 2)
        {
            for (int i = 0; i < _aicSourceModes.Length; i++)
                _aicSourceModes[i] = reader.ReadUInt32();
            for (int i = 0; i < _aicSourceVectors.Length; i++)
                _aicSourceVectors[i] = reader.ReadUInt32();
            _aicMask = reader.ReadUInt32();
            _aicPending = reader.ReadUInt32();
            _aicActiveSource = reader.ReadUInt32();
            _aicSpuriousVector = reader.ReadUInt32();
            UpdateIrqLine();
        }
        else
        {
            ResetAic();
        }
        _loaded = true;
        DrawFrame();
    }

    public byte Load8(uint address)
    {
        _lastReadAddress = address;
        byte value = Read8(address);
        _openBus = (_openBus & 0xffffff00u) | value;
        return value;
    }

    public ushort Load16(uint address)
    {
        address &= ~1u;
        _lastReadAddress = address;
        ushort value = (ushort)(Read8(address) | (Read8(address + 1) << 8));
        _openBus = (_openBus & 0xffff0000u) | value;
        return value;
    }

    public uint Load32(uint address)
    {
        address &= ~3u;
        _lastReadAddress = address;
        if (IsAicAddress(address))
        {
            uint aicValue = ReadAic32(address & ~3u, true);
            _openBus = aicValue;
            return aicValue;
        }

        if (TryReadSpeedup(address, out uint speedupValue))
        {
            _openBus = speedupValue;
            return speedupValue;
        }

        uint value = (uint)(Read8(address) | (Read8(address + 1) << 8) | (Read8(address + 2) << 16) | (Read8(address + 3) << 24));
        TraceStackAccess("R32", address, value);
        _openBus = value;
        return value;
    }

    public ushort Fetch16(uint address)
    {
        TrackExternalFetch(address);
        return Load16(address);
    }

    public uint Fetch32(uint address)
    {
        TrackExternalFetch(address);
        return Load32(address);
    }

    public void Store8(uint address, byte value)
    {
        _lastWriteAddress = address;
        _lastWriteValue = value;
        if (IsAicAddress(address))
        {
            WriteAicByte(address, value);
            return;
        }

        Write8(address, value);
    }

    public void Store16(uint address, ushort value)
    {
        address &= ~1u;
        _lastWriteAddress = address;
        _lastWriteValue = value;
        if (IsAicAddress(address))
        {
            WriteAicByte(address, (byte)value);
            WriteAicByte(address + 1, (byte)(value >> 8));
            return;
        }

        Write8(address, (byte)value);
        Write8(address + 1, (byte)(value >> 8));
        HandleWriteSideEffects(address & ~1u, value, 0xffff);
    }

    public void Store32(uint address, uint value)
    {
        address &= ~3u;
        _lastWriteAddress = address;
        _lastWriteValue = value;
        if (IsAicAddress(address))
        {
            WriteAic32(address & ~3u, value);
            return;
        }

        if (IsMcuAddress(address))
        {
            WriteMcuRegister32(address, value);
            return;
        }

        Write8(address, (byte)value);
        Write8(address + 1, (byte)(value >> 8));
        Write8(address + 2, (byte)(value >> 16));
        Write8(address + 3, (byte)(value >> 24));
        TraceStackAccess("W32", address, value);
        HandleWriteSideEffects(address & ~3u, value, 0xffffffff);
    }

    public int MemoryStall(uint pc, int wait) => wait;
    public bool IsExecutableAddress(uint address) => IsMapped(address);
    public void OnIrqEnable() => UpdateIrqLine();
    public void Dispose() { }

    private void TrackExternalFetch(uint address)
    {
        if (address < 0x10000000 || address > 0x10ffffff)
            return;

        _externalFetches++;
        _lastExternalFetchAddress = address;
    }

    private void TraceStackAccess(string op, uint address, uint value)
    {
        if (!TraceStack || _stackTraceLines >= 4096 || address < 0x20000000 || address > 0x2007ffff)
            return;

        uint pc = CurrentPc;
        if (pc < 0x10058f20 || pc > 0x100590dc)
            return;

        _stackTraceLines++;
        Console.WriteLine($"[PGM2:STACK] frame={_frameCounter} pc=0x{pc:X8} {op} addr=0x{address:X8} val=0x{value:X8} sp=0x{_cpu.Registers[13]:X8} lr=0x{_cpu.Registers[14]:X8}");
    }

    private bool TryReadSpeedup(uint address, out uint value)
    {
        value = 0;
        if (!string.Equals(_driverName, "kov2nl", StringComparison.OrdinalIgnoreCase) || address != 0x20020470)
            return false;

        value = ReadMainRam32(0x20470);
        uint next = ReadMainRam32(0x20474);
        uint pc = CurrentPc;
        if (value == 0 && next == 0 && (pc == 0x10053a94 || pc == 0x1005332c || pc == 0x1005327c))
            _cpu.Cycles = Math.Max(_cpu.Cycles, _targetCycles);

        return true;
    }

    private uint ReadMainRam32(int offset)
    {
        return (uint)(_mainRam[offset]
            | (_mainRam[offset + 1] << 8)
            | (_mainRam[offset + 2] << 16)
            | (_mainRam[offset + 3] << 24));
    }

    private void LoadRegions(ZipArchive archive)
    {
        string mainProgramName = GetMainProgramName(_driverName);
        Pgm2AuxFileSpec auxSpec = GetAuxFileSpec(_driverName);
        string internalRomName = auxSpec.RequiredFiles.FirstOrDefault(IsInternalRomName) ?? string.Empty;
        byte[]? fallbackMainProgram = null;
        string fallbackMainProgramName = string.Empty;

        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            string name = Path.GetFileName(entry.FullName);
            if (string.IsNullOrWhiteSpace(name))
                continue;

            byte[] data = ReadEntry(entry);
            if (string.Equals(name, mainProgramName, StringComparison.OrdinalIgnoreCase)
                || (string.IsNullOrEmpty(mainProgramName) && IsMainProgramName(name)))
            {
                Buffer.BlockCopy(data, 0, _mainRom, 0, Math.Min(data.Length, _mainRom.Length));
                _romBytes = Math.Max(_romBytes, Math.Min(data.Length, _mainRom.Length));
                _loadedMainProgramName = name;
            }
            else if (IsMainProgramName(name) && fallbackMainProgram == null)
            {
                fallbackMainProgram = data;
                fallbackMainProgramName = name;
            }
            else if (string.Equals(name, internalRomName, StringComparison.OrdinalIgnoreCase)
                || (string.IsNullOrEmpty(internalRomName) && IsInternalRomName(name)))
            {
                Buffer.BlockCopy(data, 0, _internalRom, 0, Math.Min(data.Length, _internalRom.Length));
                _internalRomBytes = Math.Max(_internalRomBytes, Math.Min(data.Length, _internalRom.Length));
                _loadedInternalRomName = name;
            }
            else if (IsTextRomName(name))
            {
                Buffer.BlockCopy(data, 0, _textRom, 0, Math.Min(data.Length, _textRom.Length));
                _textRomBytes = Math.Max(_textRomBytes, Math.Min(data.Length, _textRom.Length));
            }
            else if (string.Equals(name, "ig-a3_bgl.u35", StringComparison.OrdinalIgnoreCase))
            {
                LoadInterleavedWordRegion(data, _bgTileRom, 0);
                _bgTileRomBytes = Math.Max(_bgTileRomBytes, Math.Min(_bgTileRom.Length, data.Length * 2));
            }
            else if (string.Equals(name, "ig-a3_bgh.u36", StringComparison.OrdinalIgnoreCase))
            {
                LoadInterleavedWordRegion(data, _bgTileRom, 2);
                _bgTileRomBytes = Math.Max(_bgTileRomBytes, Math.Min(_bgTileRom.Length, data.Length * 2));
            }
            else if (string.Equals(name, "ig-a3_bml.u12", StringComparison.OrdinalIgnoreCase))
            {
                LoadInterleavedWordRegion(data, _spriteMaskRom, 0);
                _spriteMaskRomBytes = Math.Max(_spriteMaskRomBytes, Math.Min(_spriteMaskRom.Length, data.Length * 2));
            }
            else if (string.Equals(name, "ig-a3_bmh.u16", StringComparison.OrdinalIgnoreCase))
            {
                LoadInterleavedWordRegion(data, _spriteMaskRom, 2);
                _spriteMaskRomBytes = Math.Max(_spriteMaskRomBytes, Math.Min(_spriteMaskRom.Length, data.Length * 2));
            }
            else if (string.Equals(name, "ig-a3_cgl.u18", StringComparison.OrdinalIgnoreCase))
            {
                LoadInterleavedWordRegion(data, _spriteColorRom, 0);
                _spriteColorRomBytes = Math.Max(_spriteColorRomBytes, Math.Min(_spriteColorRom.Length, data.Length * 2));
            }
            else if (string.Equals(name, "ig-a3_cgh.u26", StringComparison.OrdinalIgnoreCase))
            {
                LoadInterleavedWordRegion(data, _spriteColorRom, 2);
                _spriteColorRomBytes = Math.Max(_spriteColorRomBytes, Math.Min(_spriteColorRom.Length, data.Length * 2));
            }
            else if (string.Equals(name, "gsyx_nvram", StringComparison.OrdinalIgnoreCase))
            {
                Buffer.BlockCopy(data, 0, _sram, 0, Math.Min(data.Length, _sram.Length));
            }
        }

        if (_romBytes == 0 && fallbackMainProgram != null)
        {
            Buffer.BlockCopy(fallbackMainProgram, 0, _mainRom, 0, Math.Min(fallbackMainProgram.Length, _mainRom.Length));
            _romBytes = Math.Min(fallbackMainProgram.Length, _mainRom.Length);
            _loadedMainProgramName = fallbackMainProgramName;
        }

        if (_spriteMaskRomBytes > 0)
            DecodeSpriteMaskRom();
        if (_spriteColorRomBytes > 0)
            DecodeSpriteColorRom();
    }

    private void LoadSidecarAuxFiles(string archivePath)
    {
        Pgm2AuxFileSpec spec = GetAuxFileSpec(_driverName);
        foreach (string fileName in spec.RequiredFiles)
        {
            if (ContainsArchiveEntry(archivePath, fileName))
                continue;

            string? sidecar = FindSidecarFile(archivePath, fileName);
            if (sidecar == null)
                continue;

            byte[] data = File.ReadAllBytes(sidecar);
            if (IsInternalRomName(fileName))
            {
                Buffer.BlockCopy(data, 0, _internalRom, 0, Math.Min(data.Length, _internalRom.Length));
                _internalRomBytes = Math.Max(_internalRomBytes, Math.Min(data.Length, _internalRom.Length));
                _loadedInternalRomName = fileName;
            }
        }
    }

    private static void LoadInterleavedWordRegion(byte[] source, byte[] destination, int wordOffset)
    {
        int sourceOffset = 0;
        int destinationOffset = wordOffset;
        while (sourceOffset + 1 < source.Length && destinationOffset + 1 < destination.Length)
        {
            destination[destinationOffset] = source[sourceOffset];
            destination[destinationOffset + 1] = source[sourceOffset + 1];
            sourceOffset += 2;
            destinationOffset += 4;
        }
    }

    private byte Read8(uint address)
    {
        if (address < 0x00004000)
            return _internalRom[address & 0x3fff];
        if (address >= 0x02000000 && address <= 0x0200ffff)
            return _sram[address - 0x02000000];
        if (address >= 0x03600000 && address <= 0x036bffff)
            return ReadMcuRegisterByte(address);
        if (address >= 0x03900000 && address <= 0x03900003)
            return (byte)(_input0 >> (int)((address & 3) * 8));
        if (address >= 0x03a00000 && address <= 0x03a00003)
            return (byte)(_input1 >> (int)((address & 3) * 8));
        if (address >= 0x10000000 && address <= 0x10ffffff)
            return _mainRom[address - 0x10000000];
        if (address >= 0x20000000 && address <= 0x2007ffff)
            return _mainRam[address - 0x20000000];
        if (address >= 0x30000000 && address <= 0x30001fff)
            return _spriteVideoRam[address - 0x30000000];
        if (address >= 0x30020000 && address <= 0x30021fff)
            return _bgVideoRam[address - 0x30020000];
        if (address >= 0x30040000 && address <= 0x30045fff)
            return _fgVideoRam[address - 0x30040000];
        if (address >= 0x30060000 && address <= 0x30063fff)
            return _spritePaletteRam[address - 0x30060000];
        if (address >= 0x30080000 && address <= 0x30081fff)
            return _bgPaletteRam[address - 0x30080000];
        if (address >= 0x300a0000 && address <= 0x300a07ff)
            return _textPaletteRam[address - 0x300a0000];
        if (address >= 0x300c0000 && address <= 0x300c01ff)
            return _spriteZoomRam[address - 0x300c0000];
        if (address >= 0x300e0000 && address <= 0x300e0fff)
            return _lineRam[(address - 0x300e0000) & 0x3ff];
        if (IsShareRamAddress(address))
            return TryMapShareRamByte(address, out int shareOffset)
                ? _shareRam[shareOffset]
                : (byte)(_openBus >> (int)((address & 3) * 8));
        if (address >= 0x30120000 && address <= 0x3012003f)
            return _gpuRegs[address - 0x30120000];
        if (address >= 0x40000000 && address <= 0x40000003)
            return ReadYmz774Byte(address);
        if (address >= 0xfffffc00 && address <= 0xfffffcff)
            return _encryptionTable[address - 0xfffffc00];
        if (IsAicAddress(address))
            return (byte)(ReadAic32(address & ~3u, false) >> (int)((address & 3) * 8));
        if (address >= 0xfffff43c && address <= 0xfffff43f)
            return 0;
        if (address >= 0xfffffd28 && address <= 0xfffffd2b)
            return (byte)((uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds() >> (int)((address & 3) * 8));
        if (address >= 0xfffffa0c && address <= 0xfffffa0f)
            return (byte)(0x00000180u >> (int)((address & 3) * 8));

        _unknownReads++;
        if (TraceUnknown && _unknownReads <= 128)
            Console.WriteLine($"[PGM2:UNK-R] pc=0x{CurrentPc:X8} addr=0x{address:X8} open=0x{_openBus:X8}");
        return (byte)(_openBus >> (int)((address & 3) * 8));
    }

    private void Write8(uint address, byte value)
    {
        if (address >= 0x02000000 && address <= 0x0200ffff)
        {
            _sram[address - 0x02000000] = value;
            return;
        }
        if (address >= 0x03600000 && address <= 0x036bffff)
        {
            WriteMcuRegisterByte(address, value);
            return;
        }
        if (address >= 0x20000000 && address <= 0x2007ffff)
        {
            _mainRam[address - 0x20000000] = value;
            _mainRamWrites++;
            return;
        }
        if (address >= 0x30000000 && address <= 0x30001fff)
        {
            _spriteVideoRam[address - 0x30000000] = value;
            _videoWrites++;
            return;
        }
        if (address >= 0x30020000 && address <= 0x30021fff)
        {
            _bgVideoRam[address - 0x30020000] = value;
            _videoWrites++;
            return;
        }
        if (address >= 0x30040000 && address <= 0x30045fff)
        {
            _fgVideoRam[address - 0x30040000] = value;
            _videoWrites++;
            return;
        }
        if (address >= 0x30060000 && address <= 0x30063fff)
        {
            _spritePaletteRam[address - 0x30060000] = value;
            _paletteWrites++;
            return;
        }
        if (address >= 0x30080000 && address <= 0x30081fff)
        {
            _bgPaletteRam[address - 0x30080000] = value;
            _paletteWrites++;
            return;
        }
        if (address >= 0x300a0000 && address <= 0x300a07ff)
        {
            _textPaletteRam[address - 0x300a0000] = value;
            _paletteWrites++;
            return;
        }
        if (address >= 0x300c0000 && address <= 0x300c01ff)
        {
            _spriteZoomRam[address - 0x300c0000] = value;
            return;
        }
        if (address >= 0x300e0000 && address <= 0x300e0fff)
        {
            _lineRam[(address - 0x300e0000) & 0x3ff] = value;
            return;
        }
        if (IsShareRamAddress(address))
        {
            if (TryMapShareRamByte(address, out int shareOffset))
                _shareRam[shareOffset] = value;
            return;
        }
        if (address >= 0x30120000 && address <= 0x3012003f)
        {
            _gpuRegs[address - 0x30120000] = value;
            return;
        }
        if (address >= 0x40000000 && address <= 0x40000003)
        {
            WriteYmz774Byte(address, value);
            return;
        }
        if (address >= 0xfffffc00 && address <= 0xfffffcff)
        {
            _encryptionTable[address - 0xfffffc00] = value;
            _encryptionWrites++;
            return;
        }
        if (address >= 0xfffff430 && address <= 0xfffff437)
            return;

        _unknownWrites++;
        if (TraceUnknown && _unknownWrites <= 128)
            Console.WriteLine($"[PGM2:UNK-W] pc=0x{CurrentPc:X8} addr=0x{address:X8} val=0x{value:X2}");
    }

    private byte ReadYmz774Byte(uint address)
    {
        _ymzReads++;
        int offset = (int)(address & 3);

        // Enough of the YMZ774 command/status port for program bringup. Bit 7 clear
        // means ready/not busy to the PGM2 games; sample playback is handled later.
        if (offset == 1)
            return 0;

        return _ymzRegs[offset];
    }

    private static bool IsShareRamAddress(uint address)
        => address >= 0x30100000 && address <= 0x301000ff;

    private bool TryMapShareRamByte(uint address, out int offset)
    {
        uint relative = address - 0x30100000;
        uint lane = relative & 3;
        if ((lane & 1) != 0)
        {
            offset = 0;
            return false;
        }

        offset = (int)(((_shareBank & 1) * 0x80) + ((relative >> 2) * 2) + (lane >> 1));
        return true;
    }

    private void WriteYmz774Byte(uint address, byte value)
    {
        _ymzWrites++;
        _ymzRegs[(int)(address & 3)] = value;
    }

    private void HandleWriteSideEffects(uint address, uint value, uint mask)
    {
        if (address >= 0xfffffa08 && address <= 0xfffffa0b)
        {
            _encryptionTriggers++;
            Array.Copy(_mainRomEncrypted, _mainRom, _mainRom.Length);
            Pgm2Igs036Decryptor.DecryptRom(_mainRom, _romBytes, 0, _encryptionTable);
            _hasDecrypted = true;
            Console.WriteLine($"[PGM2] encryption trigger value=0x{value:X8}; decrypted 0x{_romBytes:X} bytes");
            return;
        }

        if (address >= 0x30120018 && address <= 0x3012001b)
        {
            SetAicLine(AicVblankSource, false);
            return;
        }

        if (address >= 0x30120032 && address <= 0x30120033)
        {
            _shareBank = value & 0xffff;
            return;
        }

        if (address >= 0x30120038 && address <= 0x3012003b)
        {
            int shift = (int)((address & 2) * 8);
            if (mask == 0xffffffff)
            {
                _spriteKey = value;
            }
            else
            {
                uint halfMask = 0xffffu << shift;
                _spriteKey = (_spriteKey & ~halfMask) | ((value & 0xffffu) << shift);
            }

            _realSpriteKey = ReverseBits32(_spriteKey ^ 0x90055555u);
        }
    }

    private void WriteMcuRegisterByte(uint address, byte value)
    {
        uint offset = (address - 0x03600000) >> 2;
        int reg = (int)((offset >> 15) & 7);
        int shift = (int)((address & 3) * 8);
        _mcuRegs[reg] = (_mcuRegs[reg] & ~(0xffu << shift)) | ((uint)value << shift);
        _mcuWrites++;
        HandleMcuRegisterWrite(reg);
    }

    private void WriteMcuRegister32(uint address, uint value)
    {
        uint offset = (address - 0x03600000) >> 2;
        int reg = (int)((offset >> 15) & 7);
        _mcuRegs[reg] = value;
        _mcuWrites++;
        HandleMcuRegisterWrite(reg);
    }

    private void HandleMcuRegisterWrite(int reg)
    {
        if (reg == 2 && _mcuRegs[2] != 0)
        {
            ExecuteMcuCommand(true);
            SetAicLine(AicMcuSource, true);
        }
        else if (reg == 5 && _mcuRegs[5] != 0)
        {
            bool hadCommand = _mcuLastCommand != 0;
            SetAicLine(AicMcuSource, false);
            ExecuteMcuCommand(false);
            if (hadCommand)
                SetAicLine(AicMcuSource, true);
        }
    }

    private byte ReadMcuRegisterByte(uint address)
    {
        uint offset = (address - 0x03600000) >> 2;
        int reg = (int)((offset >> 15) & 7);
        int shift = (int)((address & 3) * 8);
        return (byte)(_mcuRegs[reg] >> shift);
    }

    private static bool IsMcuAddress(uint address)
        => address >= 0x03600000 && address <= 0x036bffff;

    private void ExecuteMcuCommand(bool isCommand)
    {
        if (!isCommand)
        {
            if (_mcuLastCommand != 0)
            {
                _mcuRegs[3] = (_mcuRegs[3] & 0xff00ffffu) | 0x00f20000u;
                _mcuLastCommand = 0;
            }

            return;
        }

        byte cmd = (byte)(_mcuRegs[0] & 0xff);
        byte arg1 = (byte)(_mcuRegs[0] >> 8);
        byte arg2 = (byte)(_mcuRegs[0] >> 16);
        byte arg3 = (byte)(_mcuRegs[0] >> 24);
        byte status = 0xf7;
        _mcuLastCommand = cmd;

        switch (cmd)
        {
            case 0xf6:
                _mcuRegs[3] = _mcuResult0;
                _mcuRegs[4] = _mcuResult1;
                _mcuLastCommand = 0;
                break;
            case 0xe0:
                _mcuResult0 = _mcuRegs[0];
                _mcuResult1 = _mcuRegs[1];
                break;
            case 0xe1:
            {
                byte mode = arg2;
                byte data = arg3;
                if (mode == 2)
                {
                    int baseOffset = (((int)~_shareBank) & 1) * 0x80;
                    for (int i = 0; i < 0x80; i++)
                        _shareRam[baseOffset + i] = data;
                }

                _mcuResult0 = cmd;
                _mcuResult1 = 0;
                break;
            }
            case >= 0xc0 and <= 0xc9:
                status = 0xf4;
                _mcuResult0 = cmd;
                _mcuResult1 = 0;
                break;
            default:
                status = 0xf4;
                _mcuResult0 = cmd;
                _mcuResult1 = 0;
                break;
        }

        _mcuRegs[3] = (_mcuRegs[3] & 0xff00ffffu) | ((uint)status << 16);
    }

    private void ResetAic()
    {
        Array.Clear(_aicSourceModes);
        Array.Clear(_aicSourceVectors);
        _aicMask = 0;
        _aicPending = 0;
        _aicActiveSource = uint.MaxValue;
        _aicSpuriousVector = 0;
        _cpu.IrqPending = false;
    }

    private static bool IsAicAddress(uint address)
        => address >= AicBase && address <= 0xfffff14b;

    private uint ReadAic32(uint address, bool sideEffects)
    {
        _aicReads++;
        uint offset = address - AicBase;
        if (offset < 0x80)
            return _aicSourceModes[offset >> 2];
        if (offset >= 0x80 && offset < 0x100)
            return _aicSourceVectors[(offset - 0x80) >> 2];

        switch (offset)
        {
            case 0x100:
            {
                int source = HighestPendingAicSource();
                if (source < 0)
                    return _aicSpuriousVector;

                if (sideEffects)
                    _aicActiveSource = (uint)source;
                return _aicSourceVectors[source];
            }
            case 0x104:
                return _aicActiveSource < 32 ? _aicSourceVectors[_aicActiveSource] : _aicSpuriousVector;
            case 0x108:
                return _aicActiveSource < 32 ? _aicActiveSource : 0;
            case 0x10c:
                return _aicPending;
            case 0x110:
                return _aicMask;
            case 0x114:
                return (_aicPending & _aicMask) != 0 ? 1u : 0u;
            case 0x120:
                return _aicMask;
            case 0x124:
                return ~_aicMask;
            case 0x134:
                return _aicSpuriousVector;
            case 0x148:
                return 0;
            default:
                return 0;
        }
    }

    private void WriteAicByte(uint address, byte value)
    {
        uint aligned = address & ~3u;
        int shift = (int)((address & 3) * 8);
        uint current = ReadAic32(aligned, false);
        uint next = (current & ~(0xffu << shift)) | ((uint)value << shift);
        WriteAic32(aligned, next);
    }

    private void WriteAic32(uint address, uint value)
    {
        _aicWrites++;
        uint offset = address - AicBase;
        if (offset < 0x80)
        {
            _aicSourceModes[offset >> 2] = value;
            return;
        }

        if (offset >= 0x80 && offset < 0x100)
        {
            _aicSourceVectors[(offset - 0x80) >> 2] = value;
            return;
        }

        switch (offset)
        {
            case 0x120:
                _aicMask |= value;
                UpdateIrqLine();
                break;
            case 0x124:
                _aicMask &= ~value;
                UpdateIrqLine();
                break;
            case 0x128:
                _aicPending &= ~value;
                if (_aicActiveSource < 32 && (value & (1u << (int)_aicActiveSource)) != 0)
                    _aicActiveSource = uint.MaxValue;
                UpdateIrqLine();
                break;
            case 0x12c:
                _aicPending |= value;
                UpdateIrqLine();
                break;
            case 0x130:
                _aicActiveSource = uint.MaxValue;
                UpdateIrqLine();
                break;
            case 0x134:
                _aicSpuriousVector = value;
                break;
        }
    }

    private void SetAicLine(int source, bool asserted)
    {
        uint bit = 1u << source;
        bool wasPending = (_aicPending & bit) != 0;
        if (asserted)
        {
            _aicPending |= bit;
            if (!wasPending)
            {
                _irqAsserts++;
                if (source == AicVblankSource)
                    _vblankIrqs++;
                else if (source == AicMcuSource)
                    _mcuIrqs++;
            }
        }
        else
        {
            _aicPending &= ~bit;
            if (_aicActiveSource == source)
                _aicActiveSource = uint.MaxValue;
        }

        UpdateIrqLine();
    }

    private int HighestPendingAicSource()
    {
        uint eligible = _aicPending & _aicMask;
        if (eligible == 0)
            return -1;

        int best = -1;
        uint bestPriority = 0;
        for (int source = 0; source < 32; source++)
        {
            uint bit = 1u << source;
            if ((eligible & bit) == 0)
                continue;

            uint priority = _aicSourceModes[source] & 7;
            if (best < 0 || priority > bestPriority)
            {
                best = source;
                bestPriority = priority;
            }
        }

        return best;
    }

    private void UpdateIrqLine()
    {
        _cpu.IrqPending = (_aicPending & _aicMask) != 0;
    }

    private bool IsMapped(uint address)
    {
        return address < 0x00004000
            || (address >= 0x02000000 && address <= 0x0200ffff)
            || (address >= 0x03600000 && address <= 0x036bffff)
            || (address >= 0x03900000 && address <= 0x03a00003)
            || (address >= 0x10000000 && address <= 0x10ffffff)
            || (address >= 0x20000000 && address <= 0x2007ffff)
            || (address >= 0x30000000 && address <= 0x3012003f)
            || (address >= 0x40000000 && address <= 0x40000003)
            || address >= 0xfffff000;
    }

    private void UpdateInputPorts()
    {
        uint inputs0 = 0xffffffff;
        uint inputs1 = 0xffffffff;
        if (_input.Up) inputs0 &= ~0x00000001u;
        if (_input.Down) inputs0 &= ~0x00000002u;
        if (_input.Left) inputs0 &= ~0x00000004u;
        if (_input.Right) inputs0 &= ~0x00000008u;
        if (_input.A) inputs0 &= ~0x00000010u;
        if (_input.B) inputs0 &= ~0x00000020u;
        if (_input.C) inputs0 &= ~0x00000040u;
        if (_input.X) inputs0 &= ~0x00000080u;
        if (_input.Start) inputs1 &= ~0x00000400u;
        if (_input.Mode) inputs1 &= ~0x00004000u;
        _input0 = inputs0;
        _input1 = inputs1;
    }

    private void DrawFrame()
    {
        ClearFrame(ReadPaletteColor(_bgPaletteRam, 0));

        _renderedBgPixels = 0;
        _renderedSpritePixels = 0;
        _renderedFgPixels = 0;
        UpdateTilemapDebugStats();
        if (_spriteMaskRomBytes > 0 && _spriteColorRomBytes > 0)
            _renderedSpritePixels += DrawSprites(1);
        if (_bgTileRomBytes > 0)
            _renderedBgPixels = DrawBgTilemap();
        if (_spriteMaskRomBytes > 0 && _spriteColorRomBytes > 0)
            _renderedSpritePixels += DrawSprites(0);
        if (_textRomBytes > 0)
            _renderedFgPixels = DrawTextTilemap();

        if (_renderedBgPixels == 0 && _renderedSpritePixels == 0 && _renderedFgPixels == 0 && _frameCounter == 0 && _videoWrites == 0 && _paletteWrites == 0)
            DrawBringupFrame();
    }

    private void UpdateTilemapDebugStats()
    {
        const int TxColumns = 96;
        const int TxRows = 64;
        const int BgColumns = 64;
        const int BgRows = 32;

        _fgTileEntries = 0;
        _fgFirstColumn = -1;
        _fgLastColumn = -1;
        for (int y = 0; y < TxRows; y++)
        {
            for (int x = 0; x < TxColumns; x++)
            {
                if (ReadLe32(_fgVideoRam, ((y * TxColumns) + x) * 4) == 0)
                    continue;

                _fgTileEntries++;
                if (_fgFirstColumn < 0 || x < _fgFirstColumn)
                    _fgFirstColumn = x;
                if (x > _fgLastColumn)
                    _fgLastColumn = x;
            }
        }

        _bgTileEntries = 0;
        _bgFirstColumn = -1;
        _bgLastColumn = -1;
        for (int y = 0; y < BgRows; y++)
        {
            for (int x = 0; x < BgColumns; x++)
            {
                if (ReadLe32(_bgVideoRam, ((y * BgColumns) + x) * 4) == 0)
                    continue;

                _bgTileEntries++;
                if (_bgFirstColumn < 0 || x < _bgFirstColumn)
                    _bgFirstColumn = x;
                if (x > _bgLastColumn)
                    _bgLastColumn = x;
            }
        }
    }

    private void ClearFrame(uint color)
    {
        byte b = (byte)color;
        byte g = (byte)(color >> 8);
        byte r = (byte)(color >> 16);
        byte a = (byte)(color >> 24);
        for (int i = 0; i < _frameBuffer.Length; i += 4)
        {
            _frameBuffer[i + 0] = b;
            _frameBuffer[i + 1] = g;
            _frameBuffer[i + 2] = r;
            _frameBuffer[i + 3] = a;
        }
    }

    private void WriteFgTileDump(string path)
    {
        const int TxColumns = 96;
        const int TxRows = 64;
        using var writer = new StreamWriter(path);
        writer.WriteLine($"scrollX={ReadGpu16(8)} scrollY={ReadGpu16(0x0a)} visibleWidth={CurrentVisibleWidth()}");
        for (int y = 0; y < TxRows; y++)
        {
            for (int x = 0; x < TxColumns; x++)
            {
                uint entry = ReadLe32(_fgVideoRam, ((y * TxColumns) + x) * 4);
                if (entry == 0)
                    continue;

                writer.WriteLine(
                    $"row={y:D2} col={x:D2} entry=0x{entry:X8} tile=0x{entry & 0x0003ffff:X5} pal={(entry >> 18) & 0x1f:D2} flip={(entry >> 23) & 3}");
            }
        }
    }

    private void WriteSpriteListDump(string path)
    {
        int wordCount = _spriteVideoRam.Length / 4;
        using var writer = new StreamWriter(path);
        for (int i = 0; i < wordCount; i += 4)
        {
            uint spr0 = ReadSpriteRam32(i + 0);
            uint spr1 = ReadSpriteRam32(i + 1);
            uint spr2 = ReadSpriteRam32(i + 2);
            uint spr3 = ReadSpriteRam32(i + 3);
            if ((spr2 & 0x80000000u) != 0)
            {
                writer.WriteLine($"end index={i} spr0=0x{spr0:X8} spr1=0x{spr1:X8} spr2=0x{spr2:X8} spr3=0x{spr3:X8}");
                break;
            }

            int x = (int)(spr0 & 0x000007ff);
            int y = (int)((spr0 >> 11) & 0x7ff);
            if ((x & 0x400) != 0)
                x -= 0x800;
            if ((y & 0x400) != 0)
                y -= 0x800;

            writer.WriteLine(
                $"index={i:D4} x={x,4} y={y,4} pri={(spr0 >> 31) & 1} dis={(spr0 >> 30) & 1} pal={(spr0 >> 22) & 0x3f:D2} " +
                $"sx={spr1 & 0x3f:D2} sy={(spr1 >> 6) & 0x1ff:D3} zx={(spr1 >> 16) & 0x7f:D3} zy={(spr1 >> 24) & 0x7f:D3} " +
                $"flipX={((spr1 & 0x00800000u) != 0 ? 1 : 0)} rev={((spr1 & 0x80000000u) != 0 ? 1 : 0)} mask=0x{spr2:X8} color=0x{spr3:X8}");
        }
    }

    private int DrawBgTilemap()
    {
        const int TileSize = 32;
        const int BgColumns = 64;
        const int BgRows = 32;
        const int TileBytes = TileSize * TileSize;
        int availableTiles = Math.Min(_bgTileRomBytes, _bgTileRom.Length) / TileBytes;
        if (availableTiles == 0)
            return 0;

        int scrollX = ReadGpu16(0);
        int scrollY = ReadGpu16(2);
        int visibleWidth = CurrentVisibleWidth();
        int drawn = 0;
        for (int y = 0; y < Height; y++)
        {
            int srcY = (y + scrollY) & 0x3ff;
            int tileY = (srcY >> 5) & (BgRows - 1);
            int pixelYBase = srcY & 31;
            int rowScroll = ReadLineScroll(y);
            int srcXBase = scrollX + rowScroll;
            int row = y * Stride;
            for (int x = 0; x < visibleWidth; x++)
            {
                int srcX = (x + srcXBase) & 0x7ff;
                int tileX = (srcX >> 5) & (BgColumns - 1);
                int tileIndex = (tileY * BgColumns) + tileX;
                uint entry = ReadLe32(_bgVideoRam, tileIndex * 4);
                if (entry == 0)
                    continue;

                int tile = (int)(entry & 0x0003ffff);
                if (tile >= availableTiles)
                    continue;

                int pixelX = srcX & 31;
                int flip = (int)((entry >> 23) & 3);
                if ((flip & 1) != 0)
                    pixelX = 31 - pixelX;

                int pixelY = (flip & 2) != 0 ? 31 - pixelYBase : pixelYBase;
                int colorIndex = DecodeBgPixel(tile, pixelX, pixelY);
                if (colorIndex == 0)
                    continue;

                int palette = (int)((entry >> 18) & 0x0f);
                uint color = ReadPaletteColor(_bgPaletteRam, (palette * 0x80) + colorIndex);
                if ((color & 0x00ffffff) == 0)
                    continue;

                PutPixel(row + (x * 4), color);
                drawn++;
            }
        }

        return drawn;
    }

    private int DrawTextTilemap()
    {
        const int TileSize = 8;
        const int TxColumns = 96;
        const int TxRows = 64;
        const int TileBytes = 32;
        int availableTiles = Math.Min(_textRomBytes, _textRom.Length) / TileBytes;
        if (availableTiles == 0)
            return 0;

        int scrollX = ReadGpu16(8);
        int scrollY = ReadGpu16(0x0a);
        int visibleWidth = CurrentVisibleWidth();
        int drawn = 0;
        for (int y = 0; y < Height; y++)
        {
            int srcY = (y + scrollY) & 0x1ff;
            int tileY = (srcY >> 3) & (TxRows - 1);
            int pixelYBase = srcY & 7;
            int row = y * Stride;
            for (int x = 0; x < visibleWidth; x++)
            {
                int srcX = (x + scrollX) % (TxColumns * TileSize);
                if (srcX < 0)
                    srcX += TxColumns * TileSize;
                int tileX = (srcX >> 3) & (TxColumns - 1);
                int tileIndex = (tileY * TxColumns) + tileX;
                uint entry = ReadLe32(_fgVideoRam, tileIndex * 4);
                if (entry == 0)
                    continue;

                int tile = (int)(entry & 0x0003ffff);
                if (tile >= availableTiles)
                    continue;

                int pixelX = srcX & 7;
                int flip = (int)((entry >> 23) & 3);
                if ((flip & 1) != 0)
                    pixelX = 7 - pixelX;

                int pixelY = (flip & 2) != 0 ? 7 - pixelYBase : pixelYBase;
                int packedOffset = (tile * TileBytes) + (pixelY * 4) + (pixelX >> 1);
                byte packed = _textRom[packedOffset];
                int colorIndex = (pixelX & 1) == 0 ? packed & 0x0f : packed >> 4;
                if (colorIndex == 0)
                    continue;

                int palette = (int)((entry >> 18) & 0x1f);
                uint color = ReadPaletteColor(_textPaletteRam, (palette * 0x10) + colorIndex);
                if ((color & 0x00ffffff) == 0)
                    continue;

                PutPixel(row + (x * 4), color);
                drawn++;
            }
        }

        return drawn;
    }

    private int DecodeBgPixel(int tile, int x, int y)
    {
        int offset = (tile * 32 * 32) + (y * 32) + x;
        if ((uint)offset >= (uint)Math.Min(_bgTileRomBytes, _bgTileRom.Length))
            return 0;

        return (_bgTileRom[offset] >> 1) & 0x7f;
    }

    private int DrawSprites(int priority)
    {
        int maskLength = Math.Min(_spriteMaskRomBytes, _spriteMaskRom.Length);
        int colorLength = Math.Min(_spriteColorRomBytes, _spriteColorRom.Length);
        if (maskLength == 0 || colorLength == 0)
            return 0;

        int maskWrap = maskLength - 1;
        int colorWrap = colorLength - 1;
        int wordCount = _spriteVideoRam.Length / 4;
        int endOfList = -1;
        for (int i = 0; i < wordCount; i += 4)
        {
            if ((ReadSpriteRam32(i + 2) & 0x80000000u) != 0)
            {
                endOfList = i;
                break;
            }
        }

        if (endOfList <= 0)
            return 0;

        int drawn = 0;
        for (int i = 0; i < endOfList - 2; i += 4)
        {
            uint spr0 = ReadSpriteRam32(i + 0);
            uint spr1 = ReadSpriteRam32(i + 1);
            uint spr2 = ReadSpriteRam32(i + 2);
            uint spr3 = ReadSpriteRam32(i + 3);

            if ((spr0 & 0x40000000u) != 0)
                continue;

            int spritePriority = (int)((spr0 >> 31) & 1);
            if (spritePriority != priority)
                continue;

            int x = (int)(spr0 & 0x000007ff);
            int y = (int)((spr0 >> 11) & 0x7ff);
            if ((x & 0x400) != 0)
                x -= 0x800;
            if ((y & 0x400) != 0)
                y -= 0x800;

            int palette = (int)((spr0 >> 22) & 0x3f);
            int sizeX = (int)(spr1 & 0x3f);
            int sizeY = (int)((spr1 >> 6) & 0x1ff);
            if (sizeX == 0 || sizeY == 0)
                continue;

            bool flipX = (spr1 & 0x00800000u) != 0;
            bool reverse = (spr1 & 0x80000000u) != 0;
            int zoomX = (int)((spr1 >> 16) & 0x7f);
            int zoomY = (int)((spr1 >> 24) & 0x7f);
            int maskOffset = (int)(spr2 << 1) & maskWrap;
            int paletteOffset = (int)spr3 & colorWrap;
            if (reverse)
                maskOffset = (maskOffset - 2) & maskWrap;

            uint zoomXBits = ReadZoomBits(zoomX);
            uint zoomYBits = ReadZoomBits(zoomY);
            int xRepeats = (zoomX & 0x60) >> 5;
            int yRepeats = (zoomY & 0x60) >> 5;
            int realY = y;
            int sourceLine = 0;

            for (int yDraw = 0; yDraw < sizeY; sourceLine++)
            {
                bool zoomYBit = ((zoomYBits >> (sourceLine & 0x1f)) & 1) != 0;
                int prePaletteOffset = paletteOffset;
                int preMaskOffset = maskOffset;

                if (yRepeats != 0)
                {
                    for (int repeat = 0; repeat < yRepeats; repeat++)
                    {
                        paletteOffset = prePaletteOffset;
                        maskOffset = preMaskOffset;
                        drawn += DrawSpriteLine(maskWrap, colorWrap, ref maskOffset, ref paletteOffset, x, realY, flipX, reverse, sizeX, palette, true, zoomXBits, xRepeats);
                        realY++;
                    }

                    if (zoomYBit)
                    {
                        paletteOffset = prePaletteOffset;
                        maskOffset = preMaskOffset;
                        drawn += DrawSpriteLine(maskWrap, colorWrap, ref maskOffset, ref paletteOffset, x, realY, flipX, reverse, sizeX, palette, true, zoomXBits, xRepeats);
                        realY++;
                    }

                    yDraw++;
                }
                else
                {
                    drawn += DrawSpriteLine(maskWrap, colorWrap, ref maskOffset, ref paletteOffset, x, realY, flipX, reverse, sizeX, palette, true, zoomXBits, xRepeats);
                    if (zoomYBit)
                        realY++;
                    yDraw++;
                }
            }
        }

        return drawn;
    }

    private int DrawSpriteLine(int maskWrap, int colorWrap, ref int maskOffset, ref int paletteOffset, int x, int realY, bool flipX, bool reverse, int sizeX, int palette, bool zoomYBit, uint zoomXBits, int xRepeats)
    {
        int drawn = 0;
        int realXDraw = 0;
        if (flipX ^ reverse)
            realXDraw = (PopCount32(zoomXBits) * sizeX) - 1;

        for (int xDraw = 0; xDraw < sizeX; xDraw++)
        {
            uint maskData = ReadSpriteMaskData(maskOffset, maskWrap) ^ CurrentSpriteMaskKey();

            maskOffset = reverse ? (maskOffset - 4) & maskWrap : (maskOffset + 4) & maskWrap;

            if (zoomYBit)
            {
                if (!flipX)
                    drawn += reverse
                        ? DrawSpriteChunk(colorWrap, ref paletteOffset, x, realY, palette, maskData, zoomXBits, xRepeats, ref realXDraw, -1, -1)
                        : DrawSpriteChunk(colorWrap, ref paletteOffset, x, realY, palette, maskData, zoomXBits, xRepeats, ref realXDraw, 1, 1);
                else
                    drawn += reverse
                        ? DrawSpriteChunk(colorWrap, ref paletteOffset, x, realY, palette, maskData, zoomXBits, xRepeats, ref realXDraw, 1, -1)
                        : DrawSpriteChunk(colorWrap, ref paletteOffset, x, realY, palette, maskData, zoomXBits, xRepeats, ref realXDraw, -1, 1);
            }
            else
            {
                SkipSpriteChunk(colorWrap, ref paletteOffset, maskData, reverse);
            }
        }

        return drawn;
    }

    private int DrawSpriteChunk(int colorWrap, ref int paletteOffset, int x, int y, int palette, uint maskData, uint zoomXBits, int repeats, ref int realXDraw, int realDrawIncrement, int paletteIncrement)
    {
        int drawn = 0;
        for (int xChunk = 0; xChunk < 32; xChunk++)
        {
            int bit = paletteIncrement == -1 ? xChunk : 31 - xChunk;
            bool visible = ((maskData >> bit) & 1) != 0;
            bool zoomXBit = ((zoomXBits >> bit) & 1) != 0;

            if (visible)
            {
                if (repeats != 0)
                {
                    for (int i = 0; i < repeats; i++)
                    {
                        drawn += DrawSpritePixel(colorWrap, paletteOffset, x + realXDraw, y, palette);
                        realXDraw += realDrawIncrement;
                    }

                    if (zoomXBit)
                    {
                        drawn += DrawSpritePixel(colorWrap, paletteOffset, x + realXDraw, y, palette);
                        realXDraw += realDrawIncrement;
                    }

                    paletteOffset = (paletteOffset + paletteIncrement) & colorWrap;
                }
                else
                {
                    if (zoomXBit)
                        drawn += DrawSpritePixel(colorWrap, paletteOffset, x + realXDraw, y, palette);

                    paletteOffset = (paletteOffset + paletteIncrement) & colorWrap;
                    if (zoomXBit)
                        realXDraw += realDrawIncrement;
                }
            }
            else if (repeats != 0)
            {
                realXDraw += repeats * realDrawIncrement;
                if (zoomXBit)
                    realXDraw += realDrawIncrement;
            }
            else if (zoomXBit)
            {
                realXDraw += realDrawIncrement;
            }
        }

        return drawn;
    }

    private int DrawSpritePixel(int colorWrap, int paletteOffset, int x, int y, int palette)
    {
        if ((uint)x >= CurrentVisibleWidth() || (uint)y >= Height)
            return 0;

        int pixel = _spriteColorRom[paletteOffset & colorWrap] & 0x3f;
        uint color = ReadPaletteColor(_spritePaletteRam, (palette * 0x40) + pixel);
        PutPixel((y * Stride) + (x * 4), color);
        return 1;
    }

    private static void SkipSpriteChunk(int colorWrap, ref int paletteOffset, uint maskData, bool reverse)
    {
        int pixels = PopCount32(maskData);
        paletteOffset = reverse ? (paletteOffset - pixels) & colorWrap : (paletteOffset + pixels) & colorWrap;
    }

    private void PutPixel(int offset, uint color)
    {
        _frameBuffer[offset + 0] = (byte)color;
        _frameBuffer[offset + 1] = (byte)(color >> 8);
        _frameBuffer[offset + 2] = (byte)(color >> 16);
        _frameBuffer[offset + 3] = 0xff;
    }

    private void WriteFrameBufferPpm(string path)
    {
        using var stream = File.Create(path);
        using var writer = new StreamWriter(stream, System.Text.Encoding.ASCII, bufferSize: 1024, leaveOpen: true);
        writer.Write($"P6\n{Width} {Height}\n255\n");
        writer.Flush();
        byte[] rgb = new byte[Width * Height * 3];
        int dst = 0;
        for (int y = 0; y < Height; y++)
        {
            int row = y * Stride;
            for (int x = 0; x < Width; x++)
            {
                int src = row + (x * 4);
                rgb[dst++] = _frameBuffer[src + 2];
                rgb[dst++] = _frameBuffer[src + 1];
                rgb[dst++] = _frameBuffer[src + 0];
            }
        }
        stream.Write(rgb, 0, rgb.Length);
    }

    private uint ReadPaletteColor(byte[] palette, int colorIndex)
    {
        int offset = colorIndex * 4;
        if (offset + 3 >= palette.Length)
            return 0xff000000;

        uint color = ReadLe32(palette, offset) & 0x00ffffffu;
        return 0xff000000u | color;
    }

    private uint ReadSpriteRam32(int wordIndex)
        => ReadLe32(_spriteVideoRam, wordIndex * 4);

    private int CurrentVisibleWidth()
    {
        int mode = ReadGpu16(0x0e) & 3;
        return mode == 0 ? 320 : Width;
    }

    private ushort ReadGpu16(int offset)
        => (ushort)(_gpuRegs[offset & 0x3f] | (_gpuRegs[(offset + 1) & 0x3f] << 8));

    private int ReadLineScroll(int y)
    {
        uint packed = ReadLe32(_lineRam, ((y >> 1) * 4) & 0x3fc);
        return (y & 1) == 0 ? (ushort)packed : (ushort)(packed >> 16);
    }

    private uint ReadZoomBits(int index)
        => ReadLe32(_spriteZoomRam, (index * 4) & 0x1fc);

    private uint ReadSpriteMaskData(int offset, int wrap)
    {
        uint b0 = _spriteMaskRom[(offset + 0) & wrap];
        uint b1 = _spriteMaskRom[(offset + 1) & wrap];
        uint b2 = _spriteMaskRom[(offset + 2) & wrap];
        uint b3 = _spriteMaskRom[(offset + 3) & wrap];

        return SpriteMaskOrder switch
        {
            "le" => b0 | (b1 << 8) | (b2 << 16) | (b3 << 24),
            "swap16" => (b1 << 24) | (b0 << 16) | (b3 << 8) | b2,
            "swapwords" => (b2 << 24) | (b3 << 16) | (b0 << 8) | b1,
            "swapwords16" => (b3 << 24) | (b2 << 16) | (b1 << 8) | b0,
            _ => (b0 << 24) | (b1 << 16) | (b2 << 8) | b3,
        };
    }

    private uint CurrentSpriteMaskKey()
    {
        uint xorKey = _spriteKey ^ 0x90055555u;
        return SpriteKeyMode switch
        {
            "none" => 0,
            "raw" => _spriteKey,
            "xor" => xorKey,
            "reverse-raw" => ReverseBits32(_spriteKey),
            _ => _realSpriteKey,
        };
    }

    private static ushort ReadLe16(byte[] data, int offset)
    {
        if (offset < 0 || offset + 1 >= data.Length)
            return 0;

        return (ushort)(data[offset] | (data[offset + 1] << 8));
    }

    private static uint ReadLe32(byte[] data, int offset)
    {
        if (offset < 0 || offset + 3 >= data.Length)
            return 0;

        return (uint)(data[offset]
            | (data[offset + 1] << 8)
            | (data[offset + 2] << 16)
            | (data[offset + 3] << 24));
    }

    private static void WriteLe16(byte[] data, int offset, ushort value)
    {
        if (offset < 0 || offset + 1 >= data.Length)
            return;

        data[offset] = (byte)value;
        data[offset + 1] = (byte)(value >> 8);
    }

    private void DecodeSpriteMaskRom()
    {
        int words = Math.Min(_spriteMaskRomBytes, _spriteMaskRom.Length) / 2;
        for (int i = 0; i < words; i += 2)
        {
            ushort value = ReadLe16(_spriteMaskRom, i * 2);
            ushort xor = 0;
            int bitIndex = i >> 1;
            if ((bitIndex & 0x000001) != 0) xor ^= 0x9004;
            if ((bitIndex & 0x000002) != 0) xor ^= 0x0028;
            if ((bitIndex & 0x000004) != 0) xor ^= 0x0182;
            if ((bitIndex & 0x000008) != 0) xor ^= 0x0010;
            if ((bitIndex & 0x000010) != 0) xor ^= 0x2040;
            if ((bitIndex & 0x000020) != 0) xor ^= 0x0801;
            if ((bitIndex & 0x000100) != 0) xor ^= 0x4000;
            if ((bitIndex & 0x000200) != 0) xor ^= 0x0600;
            WriteLe16(_spriteMaskRom, i * 2, BitswapSpriteMaskWord((ushort)(value ^ xor)));
        }

        for (int i = 1; i < words; i += 2)
        {
            ushort value = ReadLe16(_spriteMaskRom, i * 2);
            ushort xor = 0;
            int bitIndex = i >> 1;
            if ((bitIndex & 0x000001) != 0) xor ^= 0x0010;
            if ((bitIndex & 0x000002) != 0) xor ^= 0x2004;
            if ((bitIndex & 0x000004) != 0) xor ^= 0x0801;
            if ((bitIndex & 0x000008) != 0) xor ^= 0x0300;
            if ((bitIndex & 0x000010) != 0) xor ^= 0x0080;
            if ((bitIndex & 0x000020) != 0) xor ^= 0x0020;
            if ((bitIndex & 0x000040) != 0) xor ^= 0x4008;
            if ((bitIndex & 0x000080) != 0) xor ^= 0x1002;
            if ((bitIndex & 0x000100) != 0) xor ^= 0x0400;
            if ((bitIndex & 0x000200) != 0) xor ^= 0x0040;
            if ((bitIndex & 0x000400) != 0) xor ^= 0x8000;
            WriteLe16(_spriteMaskRom, i * 2, BitswapSpriteMaskWord((ushort)(value ^ xor)));
        }
    }

    private void DecodeSpriteColorRom()
    {
        int words = Math.Min(_spriteColorRomBytes, _spriteColorRom.Length) / 2;
        for (int i = 0; i < words; i++)
        {
            ushort value = ReadLe16(_spriteColorRom, i * 2);
            WriteLe16(_spriteColorRom, i * 2, BitswapSpriteColor(value));
        }
    }

    private static ushort BitswapSpriteMaskWord(ushort value)
    {
        return (ushort)(
            (((value >> 8) & 1) << 15)
            | (((value >> 9) & 1) << 14)
            | (((value >> 10) & 1) << 13)
            | (((value >> 11) & 1) << 12)
            | (((value >> 12) & 1) << 11)
            | (((value >> 13) & 1) << 10)
            | (((value >> 14) & 1) << 9)
            | (((value >> 15) & 1) << 8)
            | (((value >> 0) & 1) << 7)
            | (((value >> 1) & 1) << 6)
            | (((value >> 2) & 1) << 5)
            | (((value >> 3) & 1) << 4)
            | (((value >> 4) & 1) << 3)
            | (((value >> 5) & 1) << 2)
            | (((value >> 6) & 1) << 1)
            | ((value >> 7) & 1));
    }

    private static ushort BitswapSpriteColor(ushort value)
    {
        return (ushort)(
            (((value >> 15) & 1) << 15)
            | (((value >> 14) & 1) << 14)
            | (((value >> 13) & 1) << 13)
            | (((value >> 12) & 1) << 12)
            | (((value >> 11) & 1) << 11)
            | (((value >> 5) & 1) << 10)
            | (((value >> 4) & 1) << 9)
            | (((value >> 3) & 1) << 8)
            | (((value >> 7) & 1) << 7)
            | (((value >> 6) & 1) << 6)
            | (((value >> 10) & 1) << 5)
            | (((value >> 9) & 1) << 4)
            | (((value >> 8) & 1) << 3)
            | (((value >> 2) & 1) << 2)
            | (((value >> 1) & 1) << 1)
            | ((value >> 0) & 1));
    }

    private static uint ReverseBits32(uint value)
    {
        value = ((value & 0x55555555u) << 1) | ((value >> 1) & 0x55555555u);
        value = ((value & 0x33333333u) << 2) | ((value >> 2) & 0x33333333u);
        value = ((value & 0x0f0f0f0fu) << 4) | ((value >> 4) & 0x0f0f0f0fu);
        value = ((value & 0x00ff00ffu) << 8) | ((value >> 8) & 0x00ff00ffu);
        return (value << 16) | (value >> 16);
    }

    private static int PopCount32(uint value)
    {
        value -= (value >> 1) & 0x55555555u;
        value = (value & 0x33333333u) + ((value >> 2) & 0x33333333u);
        return (int)((((value + (value >> 4)) & 0x0f0f0f0fu) * 0x01010101u) >> 24);
    }

    private void DrawBringupFrame()
    {
        uint pc = CurrentPc;
        byte r = (byte)(0x20 + ((pc >> 4) & 0x7f));
        byte g = (byte)(0x20 + ((_frameCounter * 3) & 0x7f));
        byte b = _cpu.CrashDetected ? (byte)0x30 : (byte)0x80;

        for (int y = 0; y < Height; y++)
        {
            int row = y * Stride;
            for (int x = 0; x < Width; x++)
            {
                int i = row + x * 4;
                _frameBuffer[i + 0] = (byte)(b + ((x ^ y) & 0x1f));
                _frameBuffer[i + 1] = (byte)(g + (y & 0x1f));
                _frameBuffer[i + 2] = (byte)(r + (x & 0x1f));
                _frameBuffer[i + 3] = 0xff;
            }
        }

        DrawStatusBar(0, _internalRomBytes, 0xff40c060);
        DrawStatusBar(1, _romBytes >> 12, 0xff5090e0);
        DrawStatusBar(2, _mainRamWrites, 0xffd0b050);
        DrawStatusBar(3, _videoWrites, 0xffd06060);
        DrawStatusBar(4, _paletteWrites, 0xff60c0c0);
        DrawStatusBar(5, _encryptionTriggers, 0xffc060d0);
    }

    private void DrawStatusBar(int index, int value, uint color)
    {
        int y0 = 8 + index * 8;
        int width = Math.Clamp(value, 0, Width - 16);
        for (int y = y0; y < y0 + 5; y++)
        {
            int row = y * Stride;
            for (int x = 8; x < 8 + width; x++)
            {
                int i = row + x * 4;
                _frameBuffer[i + 0] = (byte)color;
                _frameBuffer[i + 1] = (byte)(color >> 8);
                _frameBuffer[i + 2] = (byte)(color >> 16);
                _frameBuffer[i + 3] = 0xff;
            }
        }
    }

    private static string DetectDriverName(string path)
    {
        string pathName = Path.GetFileNameWithoutExtension(path).Trim();
        if (DriverNames.Contains(pathName))
            return pathName;

        try
        {
            using ZipArchive archive = ZipFile.OpenRead(path);
            var names = archive.Entries.Select(e => Path.GetFileName(e.FullName)).ToArray();
            if (names.Any(n => string.Equals(n, "gsyx_v302cn.u7", StringComparison.OrdinalIgnoreCase)))
                return "kov2nl_302cn";
            if (names.Any(n => string.Equals(n, "gsyx_v301cn.u7", StringComparison.OrdinalIgnoreCase)))
                return "kov2nl_301cn";
            if (names.Any(n => string.Equals(n, "gsyx_v300cn.u7", StringComparison.OrdinalIgnoreCase)))
                return "kov2nl_300cn";
            if (names.Any(n => string.Equals(n, "kov2nl_v302fa.u7", StringComparison.OrdinalIgnoreCase)))
                return "kov2nl";
            if (names.Any(n => string.Equals(n, "kov2nl_v301fa.u7", StringComparison.OrdinalIgnoreCase)))
                return "kov2nl_301";
            if (names.Any(n => string.Equals(n, "kov2nl_v300fa.u7", StringComparison.OrdinalIgnoreCase)))
                return "kov2nl_300";
        }
        catch
        {
        }

        return pathName;
    }

    private static Pgm2AuxFileSpec GetAuxFileSpec(string driverName)
    {
        if (driverName.EndsWith("cn", StringComparison.OrdinalIgnoreCase))
            return new Pgm2AuxFileSpec("gsyx_igs036_china.rom", "blank_gsyx_china.pg2");

        if (driverName.StartsWith("kov2nl", StringComparison.OrdinalIgnoreCase))
            return new Pgm2AuxFileSpec("kov2nl_igs036_oversea.rom", "blank_kov2nl_overseas_card.pg2");

        return Pgm2AuxFileSpec.Empty;
    }

    private static string GetMainProgramName(string driverName)
        => driverName.ToLowerInvariant() switch
        {
            "kov2nl" => "kov2nl_v302fa.u7",
            "kov2nl_301" => "kov2nl_v301fa.u7",
            "kov2nl_300" => "kov2nl_v300fa.u7",
            "kov2nl_302cn" => "gsyx_v302cn.u7",
            "kov2nl_301cn" => "gsyx_v301cn.u7",
            "kov2nl_300cn" => "gsyx_v300cn.u7",
            _ => string.Empty
        };

    private static bool IsKnownPgm2Entry(string name)
        => IsMainProgramName(name)
            || IsInternalRomName(name)
            || string.Equals(name, "ig-a3_text.u4", StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, "ig-a3_bgl.u35", StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, "ig-a3_bgh.u36", StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, "ig-a3_bml.u12", StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, "ig-a3_bmh.u16", StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, "ig-a3_cgl.u18", StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, "ig-a3_cgh.u26", StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, "ig-a3_sp.u37", StringComparison.OrdinalIgnoreCase);

    private static bool IsMainProgramName(string name)
        => name.EndsWith(".u7", StringComparison.OrdinalIgnoreCase)
            && (name.StartsWith("gsyx_", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("kov2nl_", StringComparison.OrdinalIgnoreCase));

    private static bool IsInternalRomName(string name)
        => name.EndsWith("_igs036_china.rom", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith("_igs036_oversea.rom", StringComparison.OrdinalIgnoreCase);

    private static bool IsTextRomName(string name)
        => string.Equals(name, "ig-a3_text.u4", StringComparison.OrdinalIgnoreCase);

    private static bool ContainsArchiveEntry(string archivePath, string fileName)
    {
        try
        {
            using ZipArchive archive = ZipFile.OpenRead(archivePath);
            return archive.Entries.Any(e => string.Equals(Path.GetFileName(e.FullName), fileName, StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            return false;
        }
    }

    private static string? FindSidecarFile(string archivePath, string fileName)
    {
        string fullArchivePath = Path.GetFullPath(archivePath);
        string? romDirectory = Path.GetDirectoryName(fullArchivePath);
        string? parentDirectory = romDirectory != null ? Directory.GetParent(romDirectory)?.FullName : null;
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        string?[] searchRoots =
        {
            romDirectory,
            parentDirectory,
            Path.Combine(home, "roms", "MAME", "PGM2"),
            Path.Combine(home, "roms", "bios"),
            Path.Combine(home, "roms", "MAME")
        };

        foreach (string? root in searchRoots)
        {
            if (string.IsNullOrWhiteSpace(root))
                continue;

            string candidate = Path.Combine(root, fileName);
            if (File.Exists(candidate))
                return candidate;
        }

        return null;
    }

    private static byte[] ReadEntry(ZipArchiveEntry entry)
    {
        using Stream stream = entry.Open();
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        return ms.ToArray();
    }

    private static void ReadExact(BinaryReader reader, byte[] destination)
    {
        int read = reader.Read(destination, 0, destination.Length);
        if (read != destination.Length)
            throw new EndOfStreamException();
    }
}

public sealed record Pgm2AuxFileReport(string DriverName, IReadOnlyList<string> PresentFiles, IReadOnlyList<string> MissingFiles);

internal sealed record Pgm2AuxFileSpec(params string[] RequiredFiles)
{
    public static readonly Pgm2AuxFileSpec Empty = new();
}

internal readonly record struct Pgm2InputState(
    bool Up,
    bool Down,
    bool Left,
    bool Right,
    bool A,
    bool B,
    bool C,
    bool Start,
    bool X,
    bool Y,
    bool Z,
    bool Mode);

internal static class Pgm2Igs036Decryptor
{
    private static readonly uint[,] Triggers =
    {
        {0x000101, 0x000001}, {0x000802, 0x000800}, {0x000204, 0x000004}, {0x000408, 0x000408},
        {0x010010, 0x000010}, {0x020020, 0x000020}, {0x040040, 0x000040}, {0x080080, 0x080080},
        {0x100100, 0x000100}, {0x200200, 0x200000}, {0x400400, 0x400000}, {0x800801, 0x000001},
        {0x001004, 0x001000}, {0x002010, 0x002000}, {0x004040, 0x000040}, {0x008100, 0x008100}
    };

    private static readonly Func<int, int>[,] RotEnabling =
    {
        {Bit3, Not3, Bit3, Not3},
        {Bit3, Not3, Bit3, Not3},
        {Bit4, Bit4, Bit4, Bit4},
        {Bit4, Not4, Bit4, Not4},
        {Bit3, Bit3, Bit3, Bit3},
        {Nor34, Bit7, Bit7, Zero},
        {Zero, One, Zero, One},
        {Impl43, Xor37, Xnor37, Not3},
        {Bit3, Bit3, Not3, Not3},
        {Bit4, Bit4, Not4, Not4},
        {Zero, Zero, Zero, Zero},
        {Nor34, Bit7, Not7, One},
        {Bit3, Not3, Bit3, Not3},
        {Zero, One, One, Zero},
        {Bit4, Not4, Bit4, Not4},
        {Zero, Zero, Zero, Zero}
    };

    private static readonly Func<int, int>[,] RotDirection =
    {
        {Bit3, Xor37, Xnor37, Not3, Bit3, Xor37, Xnor37, Not3},
        {Zero, Not7, Not7, Zero, Zero, Not7, Not7, Zero},
        {Bit4, Xor47, Xnor47, Not4, Bit4, Xor47, Xnor47, Not4},
        {Bit3, Not7, Bit7, Zero, One, Not7, Bit7, Zero}
    };

    public static void DecryptRom(byte[] rom, int size, int wordOffset, byte[] key)
    {
        int words = Math.Min(size, rom.Length) >> 1;
        for (int i = 0; i < words; i++)
        {
            int byteOffset = i << 1;
            ushort cipher = (ushort)(rom[byteOffset] | (rom[byteOffset + 1] << 8));
            ushort plain = Decrypt(cipher, i + wordOffset, key);
            rom[byteOffset] = (byte)plain;
            rom[byteOffset + 1] = (byte)(plain >> 8);
        }
    }

    private static ushort Decrypt(ushort cipherWord, int wordAddress, byte[] key)
    {
        int aux = Deobfuscate(cipherWord, wordAddress);
        for (int i = 0; i < 16; i++)
        {
            if (((uint)wordAddress & Triggers[i, 0]) == Triggers[i, 1])
                aux ^= Bit(key[wordAddress & 0xff], i & 7) << i;
            else
                aux ^= Bit(0x1a3a, i) << i;
        }

        return (ushort)aux;
    }

    private static int Deobfuscate(ushort cipherWord, int wordAddress)
    {
        int aux = Rol(cipherWord, Rotation(wordAddress));
        return Bitswap16(aux, 10, 9, 8, 7, 0, 15, 6, 5, 14, 13, 4, 3, 12, 11, 2, 1);
    }

    private static int Rotation(int address)
    {
        ReadOnlySpan<int> group15 = stackalloc[] { 15, 11, 7, 5 };
        ReadOnlySpan<int> group14 = stackalloc[] { 14, 9, 3, 2 };
        ReadOnlySpan<int> group13 = stackalloc[] { 13, 10, 6, 1 };
        ReadOnlySpan<int> group12 = stackalloc[] { 12, 8, 4, 0 };

        int enabled0 = RotEnabled(address, group15);
        int rot = enabled0 * RotGroup(address, group15) * 9;
        rot += (enabled0 ^ RotEnabled(address, group14)) * RotGroup(address, group14);
        rot += (enabled0 ^ RotEnabled(address, group13)) * RotGroup(address, group13) * 2;
        rot += (enabled0 ^ RotEnabled(address, group12)) * RotGroup(address, group12) * 4;

        int rot2 = 4 * Bit(address, 0);
        rot2 += Bit(address, 4) * (Bit(address, 0) * 2 - 1);
        rot2 += 4 * Bit(address, 3) * (Bit(address, 0) * 2 - 1);
        rot2 *= (Bit(address, 7) | (Bit(address, 0) ^ Bit(address, 1) ^ 1)) * 2 - 1;
        rot2 += 2 * ((Bit(address, 0) ^ Bit(address, 1)) & (Bit(address, 7) ^ 1));
        return (rot + rot2) & 0xf;
    }

    private static int RotEnabled(int address, ReadOnlySpan<int> group)
    {
        for (int j = 0; j < 4; j++)
        {
            if (Bit(address, 8 + group[j]) == 0)
                continue;

            int aux = address ^ (0x1b * Bit(address, 2));
            return RotEnabling[group[j], aux & 3](aux);
        }

        return 0;
    }

    private static int RotGroup(int address, ReadOnlySpan<int> group)
        => RotDirection[group[0] & 3, address & 7](address) * 2 - 1;

    private static int Rol(int value, int shift)
        => ((value << shift) | (value >> (16 - shift))) & 0xffff;

    private static int Bitswap16(int value, params int[] bits)
    {
        int result = 0;
        for (int i = 0; i < 16; i++)
            result |= Bit(value, bits[i]) << (15 - i);
        return result;
    }

    private static int Bit(int value, int bit) => (value >> bit) & 1;
    private static int Zero(int address) => 0;
    private static int One(int address) => 1;
    private static int Bit3(int address) => Bit(address, 3);
    private static int Bit4(int address) => Bit(address, 4);
    private static int Bit7(int address) => Bit(address, 7);
    private static int Not3(int address) => Bit(address, 3) ^ 1;
    private static int Not4(int address) => Bit(address, 4) ^ 1;
    private static int Not7(int address) => Bit(address, 7) ^ 1;
    private static int Xor37(int address) => Bit(address, 3) ^ Bit(address, 7);
    private static int Xnor37(int address) => Bit(address, 3) ^ Bit(address, 7) ^ 1;
    private static int Xor47(int address) => Bit(address, 4) ^ Bit(address, 7);
    private static int Xnor47(int address) => Bit(address, 4) ^ Bit(address, 7) ^ 1;
    private static int Nor34(int address) => (Bit(address, 3) | Bit(address, 4)) ^ 1;
    private static int Impl43(int address) => Bit(address, 3) | (Bit(address, 4) ^ 1);
}
