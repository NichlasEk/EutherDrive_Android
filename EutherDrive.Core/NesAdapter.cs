using System;
using System.IO;
using System.Globalization;
using System.Reflection;
using System.Text;
using XamariNES.Cartridge;
using XamariNES.Controller;
using XamariNES.Controller.Enums;
using XamariNES.Cartridge.Mappers;
using XamariNES.APU;
using EutherDrive.Core.Savestates;
using CpuCore = XamariNES.CPU.Core;
using PpuCore = XamariNES.PPU.Core;

namespace EutherDrive.Core;

public sealed class NesAdapter : IEmulatorCore, ISavestateCapable
{
    private const int DefaultWidth = 256;
    private const int DefaultHeight = 240;
    private const int DefaultStride = DefaultWidth * 4;

    private NESCartridge? _cartridge;
    private CpuCore? _cpu;
    private PpuCore? _ppu;
    private NESController? _controller;
    private IMapperIrqProvider? _irqProvider;
    private IMapperCpuTick? _cpuTickProvider;
    private Apu? _apu;
    private byte[] _frameBuffer = new byte[DefaultHeight * DefaultStride];
    private short[] _audioBuffer = Array.Empty<short>();
    private int _cpuIdleCycles;
    private bool _latchedNmi;
    private string? _romSummary;
    private string? _romPath;
    private string? _saveRamPath;
    private ISaveRamProvider? _saveRamProvider;
    private float _masterVolumeScale = 1.0f;
    private readonly object _stateLock = new();
    private long _frameCounter;
    private RomIdentity? _romIdentity;
    private readonly bool _traceIrqWire =
        string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_NES_IRQ_WIRE"), "1", StringComparison.Ordinal);
    private readonly int _traceIrqWireLimit = ParseTraceLimit("EUTHERDRIVE_TRACE_NES_IRQ_WIRE_LIMIT", 4000);
    private int _traceIrqWireCount;
    private readonly bool _traceFramePc =
        string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_NES_FRAME_PC"), "1", StringComparison.Ordinal);
    private readonly int _traceFramePcLimit = ParseTraceLimit("EUTHERDRIVE_TRACE_NES_FRAME_PC_LIMIT", 1200);
    private readonly long _traceFramePcStartFrame = ParseIntEnv("EUTHERDRIVE_TRACE_NES_FRAME_PC_START_FRAME", 0, minValue: 0);
    private int _traceFramePcCount;
    private readonly string? _traceFramePcFile = Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_NES_FRAME_PC_FILE");
    private readonly bool _disableApuIrqWire =
        string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_NES_DISABLE_APU_IRQ_WIRE"), "1", StringComparison.Ordinal);
    private readonly bool _disableNmiWire =
        string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_NES_DISABLE_NMI_WIRE"), "1", StringComparison.Ordinal);
    private readonly bool _traceOamDma =
        string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_NES_OAM_DMA"), "1", StringComparison.Ordinal);
    private readonly int _traceOamDmaLimit = ParseTraceLimit("EUTHERDRIVE_TRACE_NES_OAM_DMA_LIMIT", 64);
    private int _traceOamDmaCount;
    private readonly int _nmiInstructionDelay = ParseIntEnv("EUTHERDRIVE_NES_NMI_INSTR_DELAY", 1, minValue: 0);
    private int _pendingNmiDelayCounter = -1;

    public string? RomSummary => _romSummary;
    public RomIdentity? RomIdentity => _romIdentity;
    public long? FrameCounter => _frameCounter;

    public void LoadRom(string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException("ROM not found", path);

        byte[] rom = File.ReadAllBytes(path);
        _romPath = path;
        _cartridge = new NESCartridge(rom);
        _controller = new NESController();
        _ppu = new PpuCore(_cartridge.MemoryMapper, DmaTransfer);
        _cpu = new CpuCore(_cartridge.MemoryMapper, _controller);
        _apu = new Apu(ReadCpuMemory);
        _cpu.CPUMemory.AttachApu(_apu);
        _irqProvider = _cartridge.MemoryMapper as IMapperIrqProvider;
        _cpuTickProvider = _cartridge.MemoryMapper as IMapperCpuTick;
        if (_cartridge.MemoryMapper is IExpansionAudioProvider expansion)
            _apu.AttachExpansionAudio(expansion);
        _saveRamProvider = _cartridge.MemoryMapper as ISaveRamProvider;
        _saveRamPath = null;
        if (_saveRamProvider != null && _saveRamProvider.BatteryBacked && !string.IsNullOrWhiteSpace(_romPath))
        {
            _saveRamPath = Path.ChangeExtension(_romPath, ".srm");
            if (File.Exists(_saveRamPath))
            {
                try
                {
                    var data = File.ReadAllBytes(_saveRamPath);
                    var ram = _saveRamProvider.GetSaveRam();
                    int copy = Math.Min(data.Length, ram.Length);
                    Buffer.BlockCopy(data, 0, ram, 0, copy);
                }
                catch
                {
                    // Ignore load failures for now
                }
            }
        }
        Reset();
        _romSummary = BuildRomSummary(path, rom);
        _romIdentity = new RomIdentity(
            Path.GetFileName(path),
            RomIdentity.ComputeSha256(rom),
            PersistentStoragePath.ResolveSavestateDirectory(path, "nes"));
    }

    public void Reset()
    {
        if (_cpu == null || _ppu == null)
            return;
        _cpu.Reset();
        _ppu.Reset();
        _cpu.Cycles = 4;
        _cpuIdleCycles = 0;
        _latchedNmi = false;
        _frameCounter = 0;
        _apu?.ConsumeAudioBuffer();
    }

    public void RunFrame()
    {
        if (_cpu == null || _ppu == null)
            return;

        while (true)
        {
            int cpuTicks;
            if (_cpuIdleCycles == 0)
            {
                _cpu.NMI = !_disableNmiWire && _latchedNmi;
                _latchedNmi = false;
                cpuTicks = _cpu.Tick();
            }
            else
            {
                _cpuIdleCycles--;
                _cpu.Instruction.Cycles = 1;
                _cpu.Cycles++;
                cpuTicks = 1;
            }

            for (int i = 0; i < cpuTicks * 3; i++)
            {
                _ppu.Tick();
            }

            _cpu.CPUMemory.FlushDeferredWrites();

            if (_ppu.NMI)
            {
                _ppu.NMI = false;
                // Approximate the $2002 race: a status read in the same CPU instruction can suppress NMI delivery.
                if (!_disableNmiWire && !_cpu.CPUMemory.ReadPpuStatusThisInstruction)
                {
                    if (_nmiInstructionDelay > 0)
                    {
                        if (_pendingNmiDelayCounter < 0)
                            _pendingNmiDelayCounter = _nmiInstructionDelay;
                    }
                    else
                    {
                        _latchedNmi = true;
                    }
                }
            }

            if (_pendingNmiDelayCounter >= 0)
            {
                // If PPUSTATUS was read during this CPU instruction, suppress queued NMI.
                if (_cpu.CPUMemory.ReadPpuStatusThisInstruction)
                {
                    _pendingNmiDelayCounter = -1;
                }
                else if (_pendingNmiDelayCounter == 0)
                {
                    _latchedNmi = true;
                    _pendingNmiDelayCounter = -1;
                }
                else
                {
                    _pendingNmiDelayCounter--;
                }
            }

            if (_apu != null)
                _apu.TickCpu(cpuTicks);
            if (_cpuTickProvider != null)
                _cpuTickProvider.TickCpu(cpuTicks);

            bool mapperIrq = _irqProvider != null && _irqProvider.IrqPending;
            bool apuIrq = _apu != null && _apu.IrqPending;
            if (_disableApuIrqWire)
                apuIrq = false;
            if (_traceIrqWire && _traceIrqWireCount < _traceIrqWireLimit && (mapperIrq || apuIrq))
            {
                Console.WriteLine($"[NES-IRQ-WIRE] frame={_frameCounter} pc=0x{_cpu.PC:X4} mapper={(mapperIrq ? 1 : 0)} apu={(apuIrq ? 1 : 0)} I={( _cpu.Status.InterruptDisable ? 1 : 0)}");
                if (_traceIrqWireCount != int.MaxValue)
                    _traceIrqWireCount++;
            }
            _cpu.IRQ = mapperIrq || apuIrq;

            if (_ppu.FrameReady)
            {
                ConvertFrameBuffer(_ppu.FrameBuffer, _frameBuffer);
                _ppu.FrameReady = false;
                _frameCounter++;
                if (_traceFramePc &&
                    _frameCounter >= _traceFramePcStartFrame &&
                    _traceFramePcCount < _traceFramePcLimit)
                {
                    bool mapperIrqNow = _irqProvider != null && _irqProvider.IrqPending;
                    bool apuIrqNow = _apu != null && _apu.IrqPending;
                    byte op = _cpu.CPUMemory.ReadByte(_cpu.PC);
                    byte b1 = _cpu.CPUMemory.ReadByte((_cpu.PC + 1) & 0xFFFF);
                    byte b2 = _cpu.CPUMemory.ReadByte((_cpu.PC + 2) & 0xFFFF);
                    string line =
                        $"[NES-FRAME] frame={_frameCounter} pc=0x{_cpu.PC:X4} op={op:X2} b1={b1:X2} b2={b2:X2} A={_cpu.A:X2} X={_cpu.X:X2} Y={_cpu.Y:X2} SP={_cpu.SP:X2} P={_cpu.Status.ToByte():X2} mapper_irq={(mapperIrqNow ? 1 : 0)} apu_irq={(apuIrqNow ? 1 : 0)} I={(_cpu.Status.InterruptDisable ? 1 : 0)}";
                    if (!string.IsNullOrWhiteSpace(_traceFramePcFile))
                    {
                        try
                        {
                            string? directory = Path.GetDirectoryName(_traceFramePcFile);
                            if (!string.IsNullOrEmpty(directory))
                                Directory.CreateDirectory(directory);
                            File.AppendAllText(_traceFramePcFile, line + Environment.NewLine);
                        }
                        catch
                        {
                            Console.WriteLine(line);
                        }
                    }
                    else
                    {
                        Console.WriteLine(line);
                    }
                    if (_traceFramePcCount != int.MaxValue)
                        _traceFramePcCount++;
                }
                if (_apu != null)
                {
                    _audioBuffer = _apu.ConsumeAudioBuffer();
                    ApplyMasterVolume(_audioBuffer);
                }
                SaveRamIfDirty();
                break;
            }
        }
    }

    public ReadOnlySpan<byte> GetFrameBuffer(out int width, out int height, out int stride)
    {
        width = DefaultWidth;
        height = DefaultHeight;
        stride = DefaultStride;
        return _frameBuffer;
    }

    public ReadOnlySpan<short> GetAudioBuffer(out int sampleRate, out int channels)
    {
        sampleRate = _apu != null ? _apu.SampleRate : 44100;
        channels = 2;
        return _audioBuffer;
    }

    public string CaptureDebugSnapshot(string? directory = null)
    {
        string dir = directory ?? Path.Combine(Directory.GetCurrentDirectory(), "logs");
        Directory.CreateDirectory(dir);

        string prefix = $"nes_{DateTime.UtcNow:yyyyMMdd_HHmmss}";
        if (_ppu != null)
        {
            DumpPpuRange(Path.Combine(dir, $"{prefix}_pattern.bin"), 0x0000, 0x2000);
            DumpPpuRange(Path.Combine(dir, $"{prefix}_nametables.bin"), 0x2000, 0x1000);
            DumpPpuRange(Path.Combine(dir, $"{prefix}_palette.bin"), 0x3F00, 0x20);
            DumpPrivateByteArray(_ppu, "_oamData", Path.Combine(dir, $"{prefix}_oam.bin"));
        }

        DumpMapperBinaryRegions(dir, prefix);
        File.WriteAllText(Path.Combine(dir, $"{prefix}_mapper.txt"), BuildMapperDebugReport());
        return prefix;
    }

    public void SetMasterVolumePercent(int percent)
    {
        if (percent < 0) percent = 0;
        else if (percent > 100) percent = 100;
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
        _ = c;
        _ = x;
        _ = y;
        _ = z;
        _ = padType;

        if (_controller == null)
            return;

        SetButton(enumButtons.Up, up);
        SetButton(enumButtons.Down, down);
        SetButton(enumButtons.Left, left);
        SetButton(enumButtons.Right, right);
        SetButton(enumButtons.A, a);
        SetButton(enumButtons.B, b);
        SetButton(enumButtons.Start, start);
        SetButton(enumButtons.Select, mode);
    }

    private void SetButton(enumButtons button, bool pressed)
    {
        if (_controller == null)
            return;
        if (pressed)
            _controller.ButtonPress(button);
        else
            _controller.ButtonRelease(button);
    }

    public void SaveState(BinaryWriter writer)
    {
        if (writer == null)
            throw new ArgumentNullException(nameof(writer));
        if (_cpu == null || _ppu == null || _apu == null || _controller == null || _cartridge == null)
            throw new InvalidOperationException("NES core not initialized.");

        lock (_stateLock)
        {
            const int version = 1;
            writer.Write(version);
            writer.Write(_frameCounter);
            writer.Write(_cpuIdleCycles);

            // CPU registers/state
            writer.Write(_cpu.A);
            writer.Write(_cpu.X);
            writer.Write(_cpu.Y);
            writer.Write(_cpu.PC);
            writer.Write(_cpu.SP);
            writer.Write(_cpu.Cycles);
            writer.Write(_cpu.NMI);
            writer.Write(_cpu.IRQ);
            writer.Write(_cpu.Status.ToByte());

            // CPU internal RAM
            byte[] ram = _cpu.CPUMemory.GetInternalRam();
            writer.Write(ram.Length);
            writer.Write(ram);

            // Mapper state (PRG/CHR RAM, IRQs, banks, etc.)
            StateBinarySerializer.WriteInto(writer, _cartridge.MemoryMapper);

            // PPU/APU/Controller state
            StateBinarySerializer.WriteInto(writer, _ppu);
            StateBinarySerializer.WriteInto(writer, _apu);
            StateBinarySerializer.WriteInto(writer, _controller);
        }
    }

    public void LoadState(BinaryReader reader)
    {
        if (reader == null)
            throw new ArgumentNullException(nameof(reader));
        if (_cpu == null || _ppu == null || _apu == null || _controller == null || _cartridge == null)
            throw new InvalidOperationException("NES core not initialized.");

        lock (_stateLock)
        {
            int version = reader.ReadInt32();
            if (version != 1)
                throw new InvalidDataException($"Unsupported NES savestate version: {version}.");

            _frameCounter = reader.ReadInt64();
            _cpuIdleCycles = reader.ReadInt32();

            _cpu.A = reader.ReadByte();
            _cpu.X = reader.ReadByte();
            _cpu.Y = reader.ReadByte();
            _cpu.PC = reader.ReadInt32();
            _cpu.SP = reader.ReadByte();
            _cpu.Cycles = reader.ReadInt64();
            _cpu.NMI = reader.ReadBoolean();
            _cpu.IRQ = reader.ReadBoolean();
            _cpu.Status.FromByte(reader.ReadByte());

            int ramLen = reader.ReadInt32();
            byte[] ram = reader.ReadBytes(ramLen);
            _cpu.CPUMemory.SetInternalRam(ram);

            StateBinarySerializer.ReadInto(reader, _cartridge.MemoryMapper);
            StateBinarySerializer.ReadInto(reader, _ppu);
            StateBinarySerializer.ReadInto(reader, _apu);
            StateBinarySerializer.ReadInto(reader, _controller);

            _apu.ConsumeAudioBuffer();
        }
    }

    private byte[] DmaTransfer(byte[] oam, int oamOffset, int offset)
    {
        if (_cpu == null)
            return oam;

        byte[] preview = _traceOamDma && _traceOamDmaCount < _traceOamDmaLimit ? new byte[8] : Array.Empty<byte>();
        for (int i = 0; i < 256; i++)
        {
            byte value = _cpu.CPUMemory.ReadByte(offset + i);
            oam[(oamOffset + i) % 256] = value;
            if (i < preview.Length)
                preview[i] = value;
        }

        if (_traceOamDma && _traceOamDmaCount < _traceOamDmaLimit)
        {
            Console.WriteLine(
                $"[NES-OAMDMA] frame={_frameCounter} pc=0x{_cpu.PC:X4} src=0x{offset:X4} oamAddr=0x{oamOffset:X2} bytes={BitConverter.ToString(preview)}");
            if (_traceOamDmaCount != int.MaxValue)
                _traceOamDmaCount++;
        }

        _cpuIdleCycles = 513;
        if (_cpu.Cycles % 2 == 1)
            _cpuIdleCycles++;

        return oam;
    }

    private byte ReadCpuMemory(int address)
    {
        if (_cpu == null)
            return 0;
        return _cpu.CPUMemory.ReadByte(address);
    }

    private void SaveRamIfDirty()
    {
        if (_saveRamProvider == null || !_saveRamProvider.BatteryBacked || _saveRamPath == null)
            return;
        if (!_saveRamProvider.IsSaveRamDirty)
            return;
        try
        {
            File.WriteAllBytes(_saveRamPath, _saveRamProvider.GetSaveRam());
            _saveRamProvider.ClearSaveRamDirty();
        }
        catch
        {
            // Ignore save failures for now
        }
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
            int v = (int)Math.Round(buffer[i] * scale);
            if (v > short.MaxValue) v = short.MaxValue;
            else if (v < short.MinValue) v = short.MinValue;
            buffer[i] = (short)v;
        }
    }

    private void DumpPpuRange(string path, int startAddress, int length)
    {
        if (_ppu == null)
            return;

        byte[] data = new byte[length];
        for (int i = 0; i < length; i++)
            data[i] = _ppu.PPUMemory.ReadByte((startAddress + i) & 0x3FFF);

        File.WriteAllBytes(path, data);
    }

    private string BuildMapperDebugReport()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"rom={_romPath ?? string.Empty}");
        sb.AppendLine($"frame={_frameCounter}");
        if (_ppu != null)
        {
            Type ppuType = _ppu.GetType();
            AppendField(sb, _ppu, ppuType, "_registerPPUCTRL");
            AppendField(sb, _ppu, ppuType, "_registerPPUMASK");
            AppendField(sb, _ppu, ppuType, "_registerPPUSTATUS");
            AppendField(sb, _ppu, ppuType, "_registerPPUADDR");
            AppendField(sb, _ppu, ppuType, "_registerPPUSCROLL");
            AppendField(sb, _ppu, ppuType, "_X");
        }

        if (_cartridge?.MemoryMapper == null)
            return sb.ToString();

        object mapper = _cartridge.MemoryMapper;
        Type type = mapper.GetType();
        sb.AppendLine($"mapperType={type.FullName}");
        sb.AppendLine($"nametableMirroring={_cartridge.MemoryMapper.NametableMirroring}");
        AppendField(sb, mapper, type, "_bankSelect");
        AppendField(sb, mapper, type, "_prgMode");
        AppendField(sb, mapper, type, "_chrMode");
        AppendField(sb, mapper, type, "_irqLatch");
        AppendField(sb, mapper, type, "_irqCounter");
        AppendField(sb, mapper, type, "_irqReload");
        AppendField(sb, mapper, type, "_irqEnabled");
        AppendField(sb, mapper, type, "_irqPending");
        AppendField(sb, mapper, type, "_isTqrom");
        AppendArrayField(sb, mapper, type, "_bankRegs");
        AppendArrayField(sb, mapper, type, "_tqromChrRam", 64);
        return sb.ToString();
    }

    private void DumpMapperBinaryRegions(string dir, string prefix)
    {
        if (_cartridge?.MemoryMapper == null)
            return;

        object mapper = _cartridge.MemoryMapper;
        Type type = mapper.GetType();
        FieldInfo? tqromChrRamField = type.GetField("_tqromChrRam", BindingFlags.Instance | BindingFlags.NonPublic);
        if (tqromChrRamField?.GetValue(mapper) is byte[] tqromChrRam && tqromChrRam.Length != 0)
            File.WriteAllBytes(Path.Combine(dir, $"{prefix}_tqrom_chr_ram.bin"), tqromChrRam);
    }

    private static void DumpPrivateByteArray(object instance, string fieldName, string path)
    {
        Type type = instance.GetType();
        FieldInfo? field = type.GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
        if (field?.GetValue(instance) is byte[] data && data.Length != 0)
            File.WriteAllBytes(path, data);
    }

    private static void AppendField(StringBuilder sb, object instance, Type type, string fieldName)
    {
        FieldInfo? field = type.GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
        if (field == null)
            return;

        sb.AppendLine($"{fieldName}={FormatDebugValue(field.GetValue(instance))}");
    }

    private static void AppendArrayField(StringBuilder sb, object instance, Type type, string fieldName, int previewBytes = -1)
    {
        FieldInfo? field = type.GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
        if (field?.GetValue(instance) is not Array array)
            return;

        sb.AppendLine($"{fieldName}.length={array.Length}");
        if (array is byte[] bytes)
        {
            int count = previewBytes >= 0 ? Math.Min(previewBytes, bytes.Length) : bytes.Length;
            sb.AppendLine($"{fieldName}.preview={BitConverter.ToString(bytes, 0, count)}");
            return;
        }

        var items = new string[array.Length];
        for (int i = 0; i < array.Length; i++)
            items[i] = FormatDebugValue(array.GetValue(i));
        sb.AppendLine($"{fieldName}=[{string.Join(", ", items)}]");
    }

    private static void ConvertFrameBuffer(byte[] src, byte[] dst)
    {
        int count = DefaultWidth * DefaultHeight;
        if (dst.Length < count * 4)
            return;

        for (int i = 0; i < count; i++)
        {
            int color = (src[i] & 0x3F) * 4;
            int o = i * 4;
            dst[o + 0] = PaletteBgra[color + 0];
            dst[o + 1] = PaletteBgra[color + 1];
            dst[o + 2] = PaletteBgra[color + 2];
            dst[o + 3] = 0xFF;
        }
    }

    private static int ParseTraceLimit(string name, int fallback)
    {
        string? raw = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(raw))
            return fallback;
        if (!int.TryParse(raw.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int value))
            return fallback;
        return value <= 0 ? int.MaxValue : value;
    }

    private static int ParseIntEnv(string name, int fallback, int minValue = int.MinValue, int maxValue = int.MaxValue)
    {
        string? raw = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(raw))
            return fallback;
        if (!int.TryParse(raw.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int value))
            return fallback;
        if (value < minValue)
            return minValue;
        if (value > maxValue)
            return maxValue;
        return value;
    }

    private static string FormatDebugValue(object? value)
    {
        return value switch
        {
            null => "<null>",
            byte b => $"0x{b:X2}",
            sbyte sb => $"0x{(byte)sb:X2}",
            ushort us => $"0x{us:X4}",
            short s => $"0x{(ushort)s:X4}",
            uint ui => $"0x{ui:X8}",
            int i => $"0x{i:X8}",
            long l => $"0x{l:X16}",
            bool flag => flag ? "1" : "0",
            _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? value.ToString() ?? string.Empty,
        };
    }

    private static string BuildRomSummary(string path, byte[] data)
    {
        string name = Path.GetFileName(path);
        if (data.Length < 16)
            return $"NES: {name}";

        int prgBanks = data[4];
        int chrBanks = data[5];
        int mapper = (data[7] & 0xF0) | ((data[6] >> 4) & 0x0F);
        int prgSize = prgBanks * 16;
        int chrSize = chrBanks * 8;
        return $"NES: {name} | PRG {prgSize}KB | CHR {chrSize}KB | Mapper {mapper}";
    }

    private static readonly byte[] PaletteBgra = BuildPaletteBgra();

    private static byte[] BuildPaletteBgra()
    {
        uint[] rgb =
        {
            0x7C7C7C, 0x0000FC, 0x0000BC, 0x4428BC, 0x940084, 0xA80020, 0xA81000, 0x881400,
            0x503000, 0x007800, 0x006800, 0x005800, 0x004058, 0x000000, 0x000000, 0x000000,
            0xBCBCBC, 0x0078F8, 0x0058F8, 0x6844FC, 0xD800CC, 0xE40058, 0xF83800, 0xE45C10,
            0xAC7C00, 0x00B800, 0x00A800, 0x00A844, 0x008888, 0x000000, 0x000000, 0x000000,
            0xF8F8F8, 0x3CBCFC, 0x6888FC, 0x9878F8, 0xF878F8, 0xF85898, 0xF87858, 0xFCA044,
            0xF8B800, 0xB8F818, 0x58D854, 0x58F898, 0x00E8D8, 0x787878, 0x000000, 0x000000,
            0xFCFCFC, 0xA4E4FC, 0xB8B8F8, 0xD8B8F8, 0xF8B8F8, 0xF8A4C0, 0xF0D0B0, 0xFCE0A8,
            0xF8D878, 0xD8F878, 0xB8F8B8, 0xB8F8D8, 0x00FCFC, 0xF8D8F8, 0x000000, 0x000000,
        };

        var palette = new byte[64 * 4];
        for (int i = 0; i < 64; i++)
        {
            uint c = rgb[i];
            int o = i * 4;
            palette[o + 0] = (byte)(c & 0xFF);
            palette[o + 1] = (byte)((c >> 8) & 0xFF);
            palette[o + 2] = (byte)((c >> 16) & 0xFF);
            palette[o + 3] = 0xFF;
        }

        return palette;
    }
}
