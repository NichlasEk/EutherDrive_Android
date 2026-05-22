namespace EutherDrive.Core.Arcade.Taito;

using EutherDrive.Core.Cpu.M68000Emu;

internal sealed class TaitoF3SoundSystem
{
    private const int OutputSampleRate = 44_100;
    private const int OutputChannels = 2;
    private const int SoundCpuClockHz = 30_476_180 / 2;
    private const double TargetFps = 26_686_000.0 / 4.0 / (432.0 * 262.0);
    private const int SoundCyclesPerFrame = (int)(SoundCpuClockHz / TargetFps);
    private const int SilentSoundCyclesPerFrame = SoundCyclesPerFrame;

    private readonly M68000 _cpu = M68000.CreateBuilder()
        .AllowTasWrites(true)
        .AllowUnalignedWordLongAccess(false)
        .Name("taito-f3-sound")
        .Build();
    private readonly TaitoF3SoundBus _bus = new();
    private readonly TaitoF3Es5505 _otis = new();
    private readonly short[] _frameAudio = new short[2048];
    private short[] _scaledAudio = Array.Empty<short>();
    private double _sampleAccumulator;
    private bool _resetAsserted = true;
    private bool _loaded;
    private bool _suspended;
    private bool _lastAudioWasNonZero;
    private int _lastMainDualPortWriteSerial;
    private int _silentCommandDrainFrames;

    public string DebugSummary => _bus.DebugSummary(_cpu.Pc, _cpu.NextOpcode, _cpu.StatusRegister, _suspended, _otis.DebugSummary);

    public void Load(byte[] soundCpu, byte[] ensoniq, DariusGaidenAdapter.TaitoF3MainBus mainBus)
    {
        _otis.Load(ensoniq);
        _bus.Load(soundCpu, _otis, mainBus);
        _loaded = true;
        Reset(asserted: true);
    }

    public void Reset(bool asserted)
    {
        _resetAsserted = asserted;
        _bus.ResetRuntime(copyVectors: true);
        _suspended = false;
        _lastAudioWasNonZero = false;
        _lastMainDualPortWriteSerial = 0;
        _silentCommandDrainFrames = 0;
        if (!asserted)
            _cpu.Reset(_bus);
        Array.Clear(_frameAudio);
        _sampleAccumulator = 0;
    }

    public void SuspendLegacyState(bool resetAsserted)
    {
        _resetAsserted = resetAsserted;
        _suspended = true;
        _lastAudioWasNonZero = false;
        _lastMainDualPortWriteSerial = 0;
        _silentCommandDrainFrames = 0;
        Array.Clear(_frameAudio);
        _sampleAccumulator = 0;
    }

    public void RunFrame(bool resetReleased, int mainDualPortWriteSerial)
    {
        if (!_loaded)
            return;

        bool nextAsserted = !resetReleased;
        if (nextAsserted != _resetAsserted)
        {
            _resetAsserted = nextAsserted;
            if (_resetAsserted)
                return;

            _bus.ResetRuntime(copyVectors: true);
            _cpu.Reset(_bus);
            _lastAudioWasNonZero = false;
            _lastMainDualPortWriteSerial = mainDualPortWriteSerial;
            _silentCommandDrainFrames = 0;
        }

        int sampleFrames = BuildFrameAudio();
        if (_resetAsserted || _suspended)
            return;

        bool hasNewMainCommand = mainDualPortWriteSerial != _lastMainDualPortWriteSerial;
        if (hasNewMainCommand)
        {
            _lastMainDualPortWriteSerial = mainDualPortWriteSerial;
            _silentCommandDrainFrames = 8;
        }
        int cycleBudget = _lastAudioWasNonZero
            ? SoundCyclesPerFrame
            : SilentSoundCyclesPerFrame;
        int cycles = 0;
        int instructions = 0;
        uint lastPc = uint.MaxValue;
        ushort lastOp = 0;
        int repeatedLineA = 0;
        while (cycles < cycleBudget && instructions < 80_000)
        {
            uint pcBefore = _cpu.Pc;
            ushort opBefore = _cpu.NextOpcode;
            if (TryHandleSoundFastPath(pcBefore, opBefore, out uint soundFastCycles))
            {
                int fastCycles = Math.Max(1, (int)soundFastCycles);
                cycles += fastCycles;
                _bus.Tick(fastCycles);
                instructions++;
                repeatedLineA = 0;
                lastPc = pcBefore;
                lastOp = opBefore;
                continue;
            }

            if (TryHandleSoundLineA(pcBefore, opBefore, out uint lineACycles))
            {
                int fastCycles = Math.Max(1, (int)lineACycles);
                cycles += fastCycles;
                _bus.Tick(fastCycles);
                instructions++;
                repeatedLineA = 0;
                lastPc = pcBefore;
                lastOp = opBefore;
                continue;
            }

            if ((opBefore & 0xf000) == 0xa000 && pcBefore == lastPc && opBefore == lastOp)
            {
                _bus.NoteLineA(pcBefore, opBefore);
                repeatedLineA++;
                if (repeatedLineA >= 64)
                {
                    _suspended = true;
                    break;
                }
            }
            else
            {
                if ((opBefore & 0xf000) == 0xa000)
                    _bus.NoteLineA(pcBefore, opBefore);
                repeatedLineA = 0;
                lastPc = pcBefore;
                lastOp = opBefore;
            }

            uint used = _cpu.ExecuteInstruction(_bus);
            int executedCycles = Math.Max(1, (int)used);
            cycles += executedCycles;
            _bus.Tick(executedCycles);
            instructions++;
            if (_cpu.IsFrozen)
                break;
        }

        _bus.LastFrameCycles = cycles;
        _bus.LastFrameInstructions = instructions;
        _bus.LastFrameCycleBudget = cycleBudget;
        _otis.RenderStereo(_frameAudio, sampleFrames);
        _lastAudioWasNonZero = HasNonZeroAudio(_frameAudio, _lastAudioSamples);
        if (!_lastAudioWasNonZero && !hasNewMainCommand && _silentCommandDrainFrames > 0)
            _silentCommandDrainFrames--;
    }

    private bool TryHandleSoundFastPath(uint pc, ushort opcode, out uint cycles)
    {
        cycles = 0;

        if (pc == 0x00c108e2 && opcode == 0x20c0)
        {
            var state = _cpu.GetState();
            state.Address[0] = 0x0004_0000;
            ushort sr = (ushort)((state.Sr & 0xffe0) | 0x0004);
            uint nextPc = 0x00c108ec;
            ushort prefetch = _bus.ReadOpcodeWord(nextPc);
            _cpu.SetState(new M68000.M68000State(state.Data, state.Address, state.Usp, state.Ssp, sr, nextPc, prefetch));
            cycles = 40;
            return true;
        }

        if (pc == 0x00c1106e && opcode == 0x5383)
        {
            var state = _cpu.GetState();
            state.Data[3] = 0;
            ushort sr = (ushort)((state.Sr & 0xffe0) | 0x0004);
            uint nextPc = 0x00c11072;
            ushort prefetch = _bus.ReadOpcodeWord(nextPc);
            _cpu.SetState(new M68000.M68000State(state.Data, state.Address, state.Usp, state.Ssp, sr, nextPc, prefetch));
            cycles = 40;
            return true;
        }

        if (pc == 0x00c111fa && opcode == 0x12d8)
        {
            var state = _cpu.GetState();
            uint source = state.Address[0] & 0x00ff_ffff;
            uint dest = state.Address[1] & 0x00ff_ffff;
            int count = Math.Clamp(0x0000_cf52 - (int)(dest & 0xffff), 0, 0x4000);
            for (int i = 0; i < count; i++)
                _bus.WriteByte(dest + (uint)i, _bus.ReadByte(source + (uint)i));

            state.Address[0] = (source + (uint)count) & 0x00ff_ffff;
            state.Address[1] = (dest + (uint)count) & 0x00ff_ffff;
            ushort sr = (ushort)(state.Sr & ~0x000f);
            uint nextPc = 0x00c11202;
            ushort prefetch = _bus.ReadOpcodeWord(nextPc);
            _cpu.SetState(new M68000.M68000State(state.Data, state.Address, state.Usp, state.Ssp, sr, nextPc, prefetch));
            cycles = (uint)Math.Max(40, count / 4);
            return true;
        }

        if (pc == 0x00c11a92 && opcode == 0x32d8)
        {
            var state = _cpu.GetState();
            uint source = state.Address[0] & 0x00ff_ffff;
            uint dest = state.Address[1] & 0x00ff_ffff;
            int words = Math.Clamp((0x0000_510e - (int)(dest & 0xffff)) / 2, 0, 0x4000);
            for (int i = 0; i < words; i++)
            {
                ushort value = _bus.ReadWord(source + (uint)(i * 2));
                _bus.WriteWord(dest + (uint)(i * 2), value);
            }

            state.Address[0] = (source + (uint)(words * 2)) & 0x00ff_ffff;
            state.Address[1] = (dest + (uint)(words * 2)) & 0x00ff_ffff;
            ushort sr = (ushort)(state.Sr & ~0x000f);
            uint nextPc = 0x00c11a9a;
            ushort prefetch = _bus.ReadOpcodeWord(nextPc);
            _cpu.SetState(new M68000.M68000State(state.Data, state.Address, state.Usp, state.Ssp, sr, nextPc, prefetch));
            cycles = (uint)Math.Max(40, words / 2);
            return true;
        }

        return false;
    }

    private bool TryHandleSoundLineA(uint pc, ushort opcode, out uint cycles)
    {
        cycles = 0;
        if (opcode != 0xa000)
            return false;

        // The Taito F3 sound ROM uses A000 as a privileged SR gate. MAME reaches
        // vector 10, whose handler stores D0 into the stacked SR, advances the
        // stacked PC by one word, and RTEs. Apply that net 68000 state change
        // here so the sound CPU does not burn a frame budget on the software gate.
        var state = _cpu.GetState();
        ushort sr = (ushort)state.Data[0];
        uint nextPc = (pc + 2) & 0x00ff_ffff;
        ushort prefetch = _bus.ReadOpcodeWord(nextPc);
        _cpu.SetState(new M68000.M68000State(state.Data, state.Address, state.Usp, state.Ssp, sr, nextPc, prefetch));
        _bus.NoteLineA(pc, opcode);
        _bus.NoteFastLineA();
        cycles = 54;
        return true;
    }

    public ReadOnlySpan<short> GetAudioBuffer(out int sampleRate, out int channels, int masterVolumePercent)
    {
        sampleRate = OutputSampleRate;
        channels = OutputChannels;

        int count = _lastAudioSamples;
        if (count == 0)
            return ReadOnlySpan<short>.Empty;

        int volume = Math.Clamp(masterVolumePercent, 0, 200);
        if (volume == 100)
            return _frameAudio.AsSpan(0, count);

        if (_scaledAudio.Length < count)
            _scaledAudio = new short[count];
        for (int i = 0; i < count; i++)
            _scaledAudio[i] = Clamp16((_frameAudio[i] * volume) / 100);
        return _scaledAudio.AsSpan(0, count);
    }

    public void SaveState(BinaryWriter writer)
    {
        writer.Write("F3SND");
        writer.Write(3);
        writer.Write(_resetAsserted);
        writer.Write(_suspended);
        writer.Write(_lastAudioWasNonZero);
        writer.Write(_sampleAccumulator);
        writer.Write(_lastAudioSamples);
        var state = _cpu.GetState();
        writer.Write(state.Pc);
        writer.Write(state.Ssp);
        writer.Write(state.Usp);
        writer.Write(state.Sr);
        writer.Write(state.Prefetch);
        for (int i = 0; i < 8; i++) writer.Write(state.Data[i]);
        for (int i = 0; i < 7; i++) writer.Write(state.Address[i]);
        _bus.SaveState(writer);
        _otis.SaveState(writer);
    }

    public void LoadState(BinaryReader reader)
    {
        if (reader.ReadString() != "F3SND")
            throw new InvalidDataException("Not a Taito F3 sound savestate.");
        int version = reader.ReadInt32();
        if (version is < 1 or > 3)
            throw new InvalidDataException($"Unsupported Taito F3 sound savestate version {version}.");

        _resetAsserted = reader.ReadBoolean();
        _suspended = reader.ReadBoolean();
        _lastAudioWasNonZero = reader.ReadBoolean();
        _sampleAccumulator = reader.ReadDouble();
        _lastAudioSamples = Math.Clamp(reader.ReadInt32(), 0, _frameAudio.Length);
        uint pc = reader.ReadUInt32();
        uint ssp = reader.ReadUInt32();
        uint usp = reader.ReadUInt32();
        ushort sr = reader.ReadUInt16();
        ushort prefetch = reader.ReadUInt16();
        uint[] data = new uint[8];
        uint[] address = new uint[7];
        for (int i = 0; i < data.Length; i++) data[i] = reader.ReadUInt32();
        for (int i = 0; i < address.Length; i++) address[i] = reader.ReadUInt32();
        _cpu.SetState(new M68000.M68000State(data, address, usp, ssp, sr, pc, prefetch));
        _bus.LoadState(reader, version);
        _otis.LoadState(reader);
        Array.Clear(_frameAudio);
        _lastAudioWasNonZero = false;
        _silentCommandDrainFrames = 0;
    }

    private int _lastAudioSamples;

    private int BuildFrameAudio()
    {
        _sampleAccumulator += OutputSampleRate / TargetFps;
        int frames = (int)_sampleAccumulator;
        _sampleAccumulator -= frames;
        frames = Math.Clamp(frames, 0, _frameAudio.Length / OutputChannels);
        _lastAudioSamples = frames * OutputChannels;
        if (_lastAudioSamples != 0)
            Array.Clear(_frameAudio, 0, _lastAudioSamples);
        return frames;
    }

    private static short Clamp16(int value)
        => value > short.MaxValue ? short.MaxValue : value < short.MinValue ? short.MinValue : (short)value;

    private static bool HasNonZeroAudio(short[] samples, int count)
    {
        for (int i = 0; i < count; i++)
        {
            if (samples[i] != 0)
                return true;
        }
        return false;
    }

    private sealed class TaitoF3SoundBus : IBusInterface, IOpcodeBusInterface
    {
        private readonly byte[] _ram = new byte[0x10000];
        private readonly ushort[] _otisBank = new ushort[0x20];
        private readonly MinimalDuart _duart = new();
        private readonly MinimalEs5510Host _esp = new();
        private byte[] _rom = Array.Empty<byte>();
        private TaitoF3Es5505? _otis;
        private DariusGaidenAdapter.TaitoF3MainBus? _mainBus;
        private bool _es5505IrqAsserted;

        public int Es5505Reads { get; private set; }
        public int Es5505Writes { get; private set; }
        public int DpramReads { get; private set; }
        public int DpramWrites { get; private set; }
        public int BankWrites { get; private set; }
        public int EspReads { get; private set; }
        public int EspWrites { get; private set; }
        public int DuartWrites { get; private set; }
        public int UnmappedReads { get; private set; }
        public int UnmappedWrites { get; private set; }
        public uint LastUnmappedRead { get; private set; }
        public uint LastUnmappedWrite { get; private set; }
        public int LineATraps { get; private set; }
        public int FastLineA { get; private set; }
        public uint LastLineAPc { get; private set; }
        public ushort LastLineAOpcode { get; private set; }
        public int LastFrameCycles { get; set; }
        public int LastFrameInstructions { get; set; }
        public int LastFrameCycleBudget { get; set; }
        private int _acknowledgedInterruptVector = -1;

        public BusSignals Signals => new(false);
        public ushort CurrentOpcode { get; private set; }

        public void Load(byte[] rom, TaitoF3Es5505 otis, DariusGaidenAdapter.TaitoF3MainBus mainBus)
        {
            _rom = rom;
            _otis = otis;
            _otis.SetIrqCallback(SetEs5505IrqLine);
            _mainBus = mainBus;
            ResetRuntime(copyVectors: true);
        }

        public void ResetRuntime(bool copyVectors)
        {
            Array.Clear(_ram);
            Array.Clear(_otisBank);
            _es5505IrqAsserted = false;
            Es5505Reads = Es5505Writes = DpramReads = DpramWrites = BankWrites = EspReads = EspWrites = DuartWrites = 0;
            UnmappedReads = UnmappedWrites = 0;
            LastUnmappedRead = LastUnmappedWrite = 0;
            LineATraps = 0;
            FastLineA = 0;
            LastLineAPc = 0;
            LastLineAOpcode = 0;
            LastFrameCycles = LastFrameInstructions = LastFrameCycleBudget = 0;
            _acknowledgedInterruptVector = -1;
            _duart.Reset();
            _esp.Reset();
            _otis?.Reset();

            if (!copyVectors || _rom.Length < 0x100008)
                return;

            Array.Copy(_rom, 0x100000, _ram, 0, 8);
        }

        public string DebugSummary(uint pc, ushort op, ushort sr, bool suspended, string otisSummary)
            => $"snd=pc0x{pc:X6}/op0x{op:X4}/sr{sr:X4}/cy{LastFrameCycles}/i{LastFrameInstructions} " +
               $"bud{LastFrameCycleBudget} susp={(suspended ? 1 : 0)} irq={((_es5505IrqAsserted || _duart.IrqAsserted) ? 6 : 0)} es={Es5505Reads}/{Es5505Writes} dpr={DpramReads}/{DpramWrites} bankW={BankWrites} espW={EspWrites} duartW={DuartWrites} du={_duart.DebugSummary} " +
               $"esp={EspReads}/{EspWrites}/{_esp.DebugSummary} " +
               $"{otisSummary} linea={LineATraps}/fast{FastLineA}@0x{LastLineAPc:X6}/0x{LastLineAOpcode:X4} unm={UnmappedReads}@0x{LastUnmappedRead:X6}/{UnmappedWrites}@0x{LastUnmappedWrite:X6}";

        public void NoteLineA(uint pc, ushort opcode)
        {
            LineATraps++;
            LastLineAPc = pc;
            LastLineAOpcode = opcode;
        }

        public void NoteFastLineA()
        {
            FastLineA++;
        }

        public byte ReadByte(uint address)
        {
            address &= 0x00ff_ffff;
            if (TryMapRam(address, out int ramOffset))
                return _ram[ramOffset];

            if (address >= 0x140000 && address <= 0x140fff)
            {
                DpramReads++;
                return (address & 1) == 0
                    ? _mainBus?.SoundReadDualPortByte((int)((address - 0x140000) >> 1)) ?? (byte)0xff
                    : (byte)0xff;
            }

            if (address >= 0x200000 && address <= 0x20001f)
            {
                ushort word = _otis?.Read((int)((address - 0x200000) >> 1)) ?? (ushort)0;
                Es5505Reads++;
                return (address & 1) == 0 ? (byte)(word >> 8) : (byte)word;
            }

            if (address >= 0x260000 && address <= 0x2601ff)
            {
                if ((address & 1) == 0)
                    return 0xff;
                EspReads++;
                return _esp.Read((int)((address - 0x260000) >> 1));
            }
            if (address >= 0x280000 && address <= 0x28001f)
            {
                if ((address & 1) == 0)
                    return 0xff;
                return _duart.Read((int)((address - 0x280000) >> 1));
            }
            if (TryReadBankedRom(address, out byte rom))
                return rom;

            UnmappedReads++;
            LastUnmappedRead = address;
            return 0xff;
        }

        public ushort ReadWord(uint address)
        {
            CurrentOpcode = (ushort)((ReadByte(address) << 8) | ReadByte(address + 1));
            return CurrentOpcode;
        }

        public uint ReadLong(uint address)
        {
            address &= 0x00ff_ffff;
            if (address == 0x000078 && _acknowledgedInterruptVector >= 0)
            {
                int vector = _acknowledgedInterruptVector;
                _acknowledgedInterruptVector = -1;
                uint vectorAddress = (uint)(vector << 2) & 0x00ff_ffff;
                return ((uint)ReadWord(vectorAddress) << 16) | ReadWord(vectorAddress + 2);
            }

            return ((uint)ReadWord(address) << 16) | ReadWord(address + 2);
        }

        public ushort ReadOpcodeWord(uint address) => ReadWord(address);

        public void WriteByte(uint address, byte value)
        {
            address &= 0x00ff_ffff;
            if (TryMapRam(address, out int ramOffset))
            {
                _ram[ramOffset] = value;
                return;
            }

            if (address >= 0x140000 && address <= 0x140fff)
            {
                DpramWrites++;
                if ((address & 1) == 0)
                    _mainBus?.SoundWriteDualPortByte((int)((address - 0x140000) >> 1), value);
                return;
            }

            if (address >= 0x200000 && address <= 0x20001f)
            {
                int offset = (int)((address - 0x200000) >> 1);
                ushort data = (address & 1) == 0 ? (ushort)(value << 8) : value;
                ushort mask = (address & 1) == 0 ? (ushort)0xff00 : (ushort)0x00ff;
                _otis?.Write(offset, data, mask);
                Es5505Writes++;
                return;
            }

            if (address >= 0x260000 && address <= 0x2601ff)
            {
                if ((address & 1) != 0)
                {
                    EspWrites++;
                    _esp.Write((int)((address - 0x260000) >> 1), value);
                }
                return;
            }

            if (address >= 0x280000 && address <= 0x28001f)
            {
                if ((address & 1) != 0)
                {
                    DuartWrites++;
                    _duart.Write((int)((address - 0x280000) >> 1), value);
                }
                return;
            }

            if (address >= 0x300000 && address <= 0x30003f)
            {
                int offset = (int)((address - 0x300000) >> 1) & 0x1f;
                ushort old = _otisBank[offset];
                _otisBank[offset] = (address & 1) == 0
                    ? (ushort)((old & 0x00ff) | (value << 8))
                    : (ushort)((old & 0xff00) | value);
                _otis?.SetBank(offset, _otisBank[offset]);
                BankWrites++;
                return;
            }

            if (address >= 0x340000 && address <= 0x340003)
                return;

            UnmappedWrites++;
            LastUnmappedWrite = address;
        }

        public void WriteWord(uint address, ushort value)
        {
            address &= 0x00ff_ffff;
            if (address >= 0x200000 && address <= 0x20001f)
            {
                _otis?.Write((int)((address - 0x200000) >> 1), value, 0xffff);
                Es5505Writes++;
                return;
            }

            if (address >= 0x300000 && address <= 0x30003f)
            {
                int offset = (int)((address - 0x300000) >> 1) & 0x1f;
                _otisBank[offset] = value;
                _otis?.SetBank(offset, value);
                BankWrites++;
                return;
            }

            WriteByte(address, (byte)(value >> 8));
            WriteByte(address + 1, (byte)value);
        }

        public void WriteLong(uint address, uint value)
        {
            WriteWord(address, (ushort)(value >> 16));
            WriteWord(address + 2, (ushort)value);
        }

        public byte InterruptLevel() => (_es5505IrqAsserted || _duart.IrqAsserted) ? (byte)6 : (byte)0;
        public void AcknowledgeInterrupt(byte level)
        {
            if (level == 6 && _duart.IrqAsserted)
                _acknowledgedInterruptVector = _duart.AcknowledgeVector();
        }

        private void SetEs5505IrqLine(bool asserted)
        {
            _es5505IrqAsserted = asserted;
        }

        public bool Reset() => false;
        public bool Halt() => false;

        private bool TryMapRam(uint address, out int offset)
        {
            if (address <= 0x03ffff)
            {
                offset = (int)(address & 0xffff);
                return true;
            }

            if (address >= 0xff0000)
            {
                offset = (int)(address & 0xffff);
                return true;
            }

            offset = 0;
            return false;
        }

        private bool TryReadBankedRom(uint address, out byte value)
        {
            int bank = address switch
            {
                >= 0xc00000 and <= 0xc1ffff => 0,
                >= 0xc20000 and <= 0xc3ffff => 1,
                >= 0xc40000 and <= 0xc7ffff => 2,
                _ => -1
            };
            if (bank < 0 || _rom.Length <= 0x100000)
            {
                value = 0xff;
                return false;
            }

            int max = Math.Max(1, (_rom.Length - 0x100000) / 0x20000);
            int entry = bank % max;
            int offset = 0x100000 + entry * 0x20000 + (int)(address & 0x1ffff);
            value = (uint)offset < (uint)_rom.Length ? _rom[offset] : (byte)0xff;
            return true;
        }

        public void Tick(int cycles)
        {
            _duart.Tick(cycles);
        }

        public void SaveState(BinaryWriter writer)
        {
            writer.Write(_ram);
            for (int i = 0; i < _otisBank.Length; i++)
                writer.Write(_otisBank[i]);
            writer.Write(_es5505IrqAsserted);
            writer.Write(Es5505Reads);
            writer.Write(Es5505Writes);
            writer.Write(DpramReads);
            writer.Write(DpramWrites);
            writer.Write(BankWrites);
            writer.Write(EspReads);
            writer.Write(EspWrites);
            writer.Write(DuartWrites);
            writer.Write(UnmappedReads);
            writer.Write(UnmappedWrites);
            writer.Write(LastUnmappedRead);
            writer.Write(LastUnmappedWrite);
            writer.Write(LastFrameCycles);
            writer.Write(LastFrameInstructions);
            writer.Write(LastFrameCycleBudget);
            writer.Write(_acknowledgedInterruptVector);
            _duart.SaveState(writer);
        }

        public void LoadState(BinaryReader reader, int version)
        {
            ReadExact(reader, _ram);
            for (int i = 0; i < _otisBank.Length; i++)
            {
                _otisBank[i] = reader.ReadUInt16();
                _otis?.SetBank(i, _otisBank[i]);
            }
            _es5505IrqAsserted = reader.ReadBoolean();
            Es5505Reads = reader.ReadInt32();
            Es5505Writes = reader.ReadInt32();
            DpramReads = reader.ReadInt32();
            DpramWrites = reader.ReadInt32();
            BankWrites = reader.ReadInt32();
            EspReads = version >= 2 ? reader.ReadInt32() : 0;
            EspWrites = reader.ReadInt32();
            DuartWrites = reader.ReadInt32();
            UnmappedReads = reader.ReadInt32();
            UnmappedWrites = reader.ReadInt32();
            LastUnmappedRead = reader.ReadUInt32();
            LastUnmappedWrite = reader.ReadUInt32();
            LastFrameCycles = reader.ReadInt32();
            LastFrameInstructions = reader.ReadInt32();
            LastFrameCycleBudget = reader.ReadInt32();
            _acknowledgedInterruptVector = version >= 3 ? reader.ReadInt32() : -1;
            _duart.LoadState(reader);
            _esp.Reset();
        }

        private static void ReadExact(BinaryReader reader, byte[] destination)
        {
            int read = 0;
            while (read < destination.Length)
            {
                int count = reader.Read(destination, read, destination.Length - read);
                if (count <= 0)
                    throw new EndOfStreamException("Unexpected end of Taito F3 sound state.");
                read += count;
            }
        }

        private sealed class MinimalEs5510Host
        {
            private readonly int[] _gpr = new int[0x100];
            private readonly long[] _instr = new long[0x100];
            private int _gprLatch;
            private long _instrLatch;
            private int _dilLatch;
            private int _dolLatch;
            private int _dadrLatch;
            private byte _ramSelect;
            private byte _hostControl = 0x04;
            private byte _hostSerial;
            private readonly short[] _dram = new short[1 << 16];

            public string DebugSummary => $"hc{_hostControl:X2}/hs{_hostSerial:X2}/rs{_ramSelect:X2}/g{_gprLatch & 0xffffff:X6}/do{_dolLatch & 0xffffff:X6}/da{_dadrLatch & 0xffffff:X6}";

            public void Reset()
            {
                _gprLatch = 0;
                _instrLatch = 0;
                _dilLatch = 0;
                _dolLatch = 0;
                _dadrLatch = 0;
                _ramSelect = 0;
                _hostControl = 0x04;
                _hostSerial = 0;
                Array.Clear(_gpr);
                Array.Clear(_instr);
                Array.Clear(_dram);
            }

            public byte Read(int offset)
            {
                offset &= 0xff;
                return offset switch
                {
                    0x00 => (byte)(_gprLatch >> 16),
                    0x01 => (byte)(_gprLatch >> 8),
                    0x02 => (byte)_gprLatch,
                    0x03 => (byte)(_instrLatch >> 40),
                    0x04 => (byte)(_instrLatch >> 32),
                    0x05 => (byte)(_instrLatch >> 24),
                    0x06 => (byte)(_instrLatch >> 16),
                    0x07 => (byte)(_instrLatch >> 8),
                    0x08 => (byte)_instrLatch,
                    0x09 => (byte)(_dilLatch >> 16),
                    0x0a => (byte)(_dilLatch >> 8),
                    0x0b => 0x00,
                    0x0c => (byte)(_dolLatch >> 16),
                    0x0d => (byte)(_dolLatch >> 8),
                    0x0e => 0xff,
                    0x0f => (byte)(_dadrLatch >> 16),
                    0x10 => (byte)(_dadrLatch >> 8),
                    0x11 => (byte)_dadrLatch,
                    0x12 => 0x00,
                    0x16 => 0x27,
                    _ => 0x00
                };
            }

            public void Write(int offset, byte data)
            {
                offset &= 0xff;
                switch (offset)
                {
                    case 0x00:
                        _gprLatch = (_gprLatch & 0x00ffff) | (data << 16);
                        break;
                    case 0x01:
                        _gprLatch = (_gprLatch & 0xff00ff) | (data << 8);
                        break;
                    case 0x02:
                        _gprLatch = (_gprLatch & 0xffff00) | data;
                        break;
                    case 0x03:
                        _instrLatch = (_instrLatch & 0x00ffffffffffL) | ((long)data << 40);
                        break;
                    case 0x04:
                        _instrLatch = (_instrLatch & unchecked((long)0xff00ffffffffL)) | ((long)data << 32);
                        break;
                    case 0x05:
                        _instrLatch = (_instrLatch & unchecked((long)0xffff00ffffffL)) | ((long)data << 24);
                        break;
                    case 0x06:
                        _instrLatch = (_instrLatch & unchecked((long)0xffffff00ffffL)) | ((long)data << 16);
                        break;
                    case 0x07:
                        _instrLatch = (_instrLatch & unchecked((long)0xffffffff00ffL)) | ((long)data << 8);
                        break;
                    case 0x08:
                        _instrLatch = (_instrLatch & unchecked((long)0xffffffffff00L)) | data;
                        break;
                    case 0x0c:
                        _dolLatch = (_dolLatch & 0x00ffff) | (data << 16);
                        break;
                    case 0x0d:
                        _dolLatch = (_dolLatch & 0xff00ff) | (data << 8);
                        break;
                    case 0x0e:
                        _dolLatch = (_dolLatch & 0xffff00) | data;
                        break;
                    case 0x0f:
                        _dadrLatch = (_dadrLatch & 0x00ffff) | (data << 16);
                        AccessDram();
                        break;
                    case 0x10:
                        _dadrLatch = (_dadrLatch & 0xff00ff) | (data << 8);
                        break;
                    case 0x11:
                        _dadrLatch = (_dadrLatch & 0xffff00) | data;
                        break;
                    case 0x12:
                        _hostControl = (byte)((_hostControl & 0x04) | (data & 0x03));
                        _hostControl &= unchecked((byte)~0x02);
                        break;
                    case 0x14:
                        _ramSelect = (byte)(data & 0x80);
                        break;
                    case 0x18:
                        _hostSerial = data;
                        break;
                    case 0x80:
                        if (data < 0xa0)
                            _instrLatch = _instr[data] & 0x0000_ffff_ffff_ffffL;
                        if (data < 0xc0)
                            _gprLatch = _gpr[data] & 0x00ff_ffff;
                        break;
                    case 0xa0:
                        if (data < 0xc0)
                            _gpr[data] = _gprLatch & 0x00ff_ffff;
                        break;
                    case 0xc0:
                        if (data < 0xa0)
                            _instr[data] = _instrLatch & 0x0000_ffff_ffff_ffffL;
                        break;
                    case 0xe0:
                        if (data < 0xa0)
                            _instr[data] = _instrLatch & 0x0000_ffff_ffff_ffffL;
                        if (data < 0xc0)
                            _gpr[data] = _gprLatch & 0x00ff_ffff;
                        break;
                }
            }

            private void AccessDram()
            {
                int address = (_dadrLatch >> 8) & 0xffff;
                if (_ramSelect != 0)
                    _dilLatch = _dram[address] << 8;
                else
                    _dram[address] = (short)(_dolLatch >> 8);
            }
        }

        private sealed class MinimalDuart
        {
            private const byte IntCounterReady = 0x08;
            private byte _acr;
            private byte _imr;
            private byte _isr;
            private byte _ivr = 0x0f;
            private ushort _counterReload = 1;
            private int _counterCycles;
            private bool _timerEnabled;
            private byte _inputPort = 0xfc; // IP0/IP1 high, IP2/IP3 clocks idle low, IP7 set on reads.

            public bool IrqAsserted => (_isr & _imr) != 0;
            public string DebugSummary => $"{_isr:X2}/{_imr:X2}/{_acr:X2}/ct{_counterReload:X4}/{(_timerEnabled ? 1 : 0)}";
            public int AcknowledgeVector() => _ivr;

            public void Reset()
            {
                _acr = 0;
                _imr = 0;
                _isr = 0;
                _ivr = 0x0f;
                _counterReload = 1;
                _counterCycles = 0;
                _timerEnabled = false;
            }

            public byte Read(int offset)
            {
                switch (offset & 0x0f)
                {
                    case 0x01: // SRA
                    case 0x09: // SRB
                        return 0x0c; // Tx ready/empty, no RX data.
                    case 0x04: // IPCR
                        return 0;
                    case 0x05: // ISR
                        return _isr;
                    case 0x06: // counter upper
                        return (byte)(_counterReload >> 8);
                    case 0x07: // counter lower
                        return (byte)_counterReload;
                    case 0x0a:
                        return 0x61;
                    case 0x0c: // IVR
                        return _ivr;
                    case 0x0d: // IP
                        return (byte)(_inputPort | 0x80);
                    case 0x0e: // start counter command
                        StartCounter();
                        return 0xff;
                    case 0x0f: // stop counter command
                        _isr &= unchecked((byte)~IntCounterReady);
                        if ((_acr & 0x40) == 0)
                            _timerEnabled = false;
                        return 0xff;
                    default:
                        return 0xff;
                }
            }

            public void Write(int offset, byte value)
            {
                switch (offset & 0x0f)
                {
                    case 0x04: // ACR
                        _acr = value;
                        if ((value & 0x40) != 0)
                            StartCounter();
                        break;
                    case 0x05: // IMR
                        _imr = value;
                        break;
                    case 0x06: // CTUR
                        _counterReload = (ushort)((_counterReload & 0x00ff) | (value << 8));
                        break;
                    case 0x07: // CTLR
                        _counterReload = (ushort)((_counterReload & 0xff00) | value);
                        break;
                    case 0x0c: // IVR
                        _ivr = value;
                        break;
                    case 0x0e: // set output bits
                    case 0x0f: // reset output bits
                        break;
                }
            }

            public void Tick(int soundCpuCycles)
            {
                if (!_timerEnabled)
                    return;

                _counterCycles -= Math.Max(1, soundCpuCycles);
                while (_counterCycles <= 0)
                {
                    _isr |= IntCounterReady;
                    _counterCycles += CounterPeriodInSoundCpuCycles();
                }
            }

            private void StartCounter()
            {
                _timerEnabled = true;
                _counterCycles = CounterPeriodInSoundCpuCycles();
            }

            private int CounterPeriodInSoundCpuCycles()
            {
                int reload = Math.Max(1, (int)_counterReload);
                int clockDivisor = ((_acr >> 4) & 0x03) == 0x03 ? 16 : 1;
                double duartTicks = reload * clockDivisor;
                double soundCycles = duartTicks * SoundCpuClockHz / 4_000_000.0;
                return Math.Max(16, (int)Math.Round(soundCycles));
            }

            public void SaveState(BinaryWriter writer)
            {
                writer.Write(_acr);
                writer.Write(_imr);
                writer.Write(_isr);
                writer.Write(_ivr);
                writer.Write(_counterReload);
                writer.Write(_counterCycles);
                writer.Write(_timerEnabled);
                writer.Write(_inputPort);
            }

            public void LoadState(BinaryReader reader)
            {
                _acr = reader.ReadByte();
                _imr = reader.ReadByte();
                _isr = reader.ReadByte();
                _ivr = reader.ReadByte();
                _counterReload = reader.ReadUInt16();
                _counterCycles = reader.ReadInt32();
                _timerEnabled = reader.ReadBoolean();
                _inputPort = reader.ReadByte();
            }
        }
    }

        private sealed class TaitoF3Es5505
        {
        private const ushort ControlIrq = 0x0080;
        private const ushort ControlDir = 0x0040;
        private const ushort ControlIrqe = 0x0020;
        private const ushort ControlBle = 0x0010;
        private const ushort ControlLpe = 0x0008;
        private const ushort ControlStopMask = 0x0003;
        private const int AddressFracBits = 9;
        private readonly Voice[] _voices = new Voice[32];
        private readonly int[] _banks = new int[0x20];
        private byte[] _samples = Array.Empty<byte>();
        private int _sampleWordMask;
        private double _chipSampleRate = SoundCpuClockHz / 16.0;
        private byte _currentPage;
        private byte _activeVoices;
        private byte _irqv = 0x80;
        private ushort _serialMode;
        private Action<bool>? _irqCallback;
        private int _controlWrites;
        private int _runControlWrites;
        private int _lastControlVoice;
        private ushort _lastControlData;
        private ushort _lastControlMask;
        private ushort _lastControlValue;

        public TaitoF3Es5505()
        {
            for (int i = 0; i < _voices.Length; i++)
                _voices[i] = new Voice(i);
        }

        public string DebugSummary
        {
            get
            {
                int running = 0;
                int audible = 0;
                int first = -1;
                for (int i = 0; i < _voices.Length; i++)
                {
                    Voice voice = _voices[i];
                    if ((voice.Control & ControlStopMask) == 0)
                    {
                        running++;
                        if (first < 0)
                            first = i;
                        if (voice.Freq != 0 && (voice.LeftVolume != 0 || voice.RightVolume != 0))
                            audible++;
                    }
                }

                string firstVoice = first >= 0
                    ? $"v{first}=ctl{_voices[first].Control:X4}/fr{_voices[first].Freq:X4}/vol{_voices[first].LeftVolume:X2},{_voices[first].RightVolume:X2}/acc{_voices[first].Accum:X8}/st{_voices[first].Start:X8}/en{_voices[first].End:X8}"
                    : "v-";
                return $"otis=pg{_currentPage:X2}/act{_activeVoices}/run{running}/aud{audible}/irq{_irqv:X2}/cw{_controlWrites}/{_runControlWrites}/last{_lastControlVoice}:{_lastControlData:X4}&{_lastControlMask:X4}->{_lastControlValue:X4}/{firstVoice}";
            }
        }

        public void Load(byte[] samples)
        {
            _samples = samples;
            _sampleWordMask = Math.Max(1, samples.Length / 2) - 1;
            Reset();
        }

        public void SetIrqCallback(Action<bool> callback) => _irqCallback = callback;

        public void Reset()
        {
            _currentPage = 0;
            _activeVoices = 0;
            UpdateChipSampleRate();
            _irqv = 0x80;
            _serialMode = 0;
            _controlWrites = 0;
            _runControlWrites = 0;
            _lastControlVoice = 0;
            _lastControlData = 0;
            _lastControlMask = 0;
            _lastControlValue = 0;
            _irqCallback?.Invoke(false);
            Array.Clear(_banks);
            foreach (Voice voice in _voices)
                voice.Reset();
        }

        public void SetBank(int offset, ushort value)
        {
            if ((uint)offset >= (uint)_banks.Length)
                return;
            int bankMask = Math.Max(0, (_samples.Length / 0x200000) - 1);
            _banks[offset] = (value & bankMask) << 20;
        }

        public ushort Read(int offset)
        {
            offset &= 0x0f;
            Voice voice = _voices[_currentPage & 0x1f];
            if (_currentPage < 0x20)
                return ReadLow(voice, offset);
            if (_currentPage < 0x40)
                return ReadHigh(voice, offset);
            return ReadTest(offset);
        }

        public void Write(int offset, ushort data, ushort mask)
        {
            offset &= 0x0f;
            Voice voice = _voices[_currentPage & 0x1f];
            if (_currentPage < 0x20)
                WriteLow(voice, offset, data, mask);
            else if (_currentPage < 0x40)
                WriteHigh(voice, offset, data, mask);
            else
                WriteTest(offset, data, mask);
        }

        public void RenderStereo(short[] destination, int sampleFrames)
        {
            for (int frame = 0; frame < sampleFrames; frame++)
            {
                int left = 0;
                int right = 0;
                int maxVoice = Math.Min(_activeVoices, (byte)31);
                for (int i = 0; i <= maxVoice; i++)
                    RenderVoice(_voices[i], sampleFrames, ref left, ref right);

                int outOffset = frame * 2;
                destination[outOffset] = Clamp16(left);
                destination[outOffset + 1] = Clamp16(right);
            }
        }

        private ushort ReadLow(Voice voice, int offset)
            => offset switch
            {
                0x00 => (ushort)(voice.Control | 0xf000),
                0x01 => (ushort)(voice.Freq << 1),
                0x02 => (ushort)(voice.Start >> 16),
                0x03 => (ushort)voice.Start,
                0x04 => (ushort)(voice.End >> 16),
                0x05 => (ushort)voice.End,
                0x06 => voice.K2,
                0x07 => voice.K1,
                0x08 => (ushort)(voice.LeftVolume << 8),
                0x09 => (ushort)(voice.RightVolume << 8),
                0x0a => (ushort)(voice.Accum >> 16),
                0x0b => (ushort)voice.Accum,
                0x0c => 0,
                0x0d => _activeVoices,
                0x0e => ReadIrqv(),
                0x0f => _currentPage,
                _ => 0
            };

        private ushort ReadHigh(Voice voice, int offset)
        {
            if (offset == 0x06 && (voice.Control & ControlStopMask) != 0)
                voice.O1 = ReadSample(voice, voice.Accum >> AddressFracBits);

            return offset switch
            {
                0x00 => (ushort)(voice.Control | 0xf000),
                0x01 => (ushort)voice.O4,
                0x02 => (ushort)voice.O3,
                0x03 => (ushort)voice.O3Prev,
                0x04 => (ushort)voice.O2,
                0x05 => (ushort)voice.O2Prev,
                0x06 => (ushort)voice.O1,
                >= 0x07 and <= 0x0c => 0,
                0x0d => _activeVoices,
                0x0e => ReadIrqv(),
                0x0f => _currentPage,
                _ => 0
            };
        }

        private ushort ReadTest(int offset)
            => offset switch
            {
                0x08 => (ushort)(_serialMode | 0x07f8),
                0x09 => 0,
                0x0d => _activeVoices,
                0x0e => ReadIrqv(),
                0x0f => _currentPage,
                _ => 0
            };

        private ushort ReadIrqv()
        {
            ushort value = _irqv;
            _irqv = 0x80;
            _irqCallback?.Invoke(false);
            return value;
        }

        private void WriteLow(Voice voice, int offset, ushort data, ushort mask)
        {
            bool hi = (mask & 0xff00) != 0;
            bool lo = (mask & 0x00ff) != 0;
            switch (offset)
            {
                case 0x00:
                    voice.Control = (ushort)(voice.Control | 0xf000);
                    if (lo)
                        voice.Control = (ushort)((voice.Control & ~0x00ff) | (data & 0x00ff));
                    if (hi)
                        voice.Control = (ushort)((voice.Control & ~0x0f00) | (data & 0x0f00));
                    NoteControlWrite(voice, data, mask);
                    break;
                case 0x01:
                    ushort freq = (ushort)(voice.Freq << 1);
                    if (lo)
                        freq = (ushort)((freq & ~0x00fe) | (data & 0x00fe));
                    if (hi)
                        freq = (ushort)((freq & ~0xff00) | (data & 0xff00));
                    voice.Freq = (uint)(freq >> 1);
                    break;
                case 0x02:
                    WriteAddressHigh(ref voice.Start, data, hi, lo);
                    break;
                case 0x03:
                    WriteAddressLow(ref voice.Start, data, hi, lo);
                    break;
                case 0x04:
                    WriteAddressHigh(ref voice.End, data, hi, lo);
                    break;
                case 0x05:
                    WriteAddressLow(ref voice.End, data, hi, lo);
                    break;
                case 0x06:
                    WriteFilter(ref voice.K2, data, hi, lo);
                    break;
                case 0x07:
                    WriteFilter(ref voice.K1, data, hi, lo);
                    break;
                case 0x08:
                    if (hi)
                        voice.LeftVolume = (byte)(data >> 8);
                    break;
                case 0x09:
                    if (hi)
                        voice.RightVolume = (byte)(data >> 8);
                    break;
                case 0x0a:
                    WriteAddressHigh(ref voice.Accum, data, hi, lo);
                    break;
                case 0x0b:
                    WriteAddressLoFull(ref voice.Accum, data, hi, lo);
                    break;
                case 0x0d:
                    if (lo)
                        SetActiveVoices(data);
                    break;
                case 0x0f:
                    if (lo)
                        _currentPage = (byte)(data & 0x7f);
                    break;
            }
        }

        private void WriteHigh(Voice voice, int offset, ushort data, ushort mask)
        {
            switch (offset)
            {
                case 0x00:
                    WriteLow(voice, offset, data, mask);
                    break;
                case 0x01:
                    voice.O4 = (short)MergeWordMasked((ushort)voice.O4, data, mask);
                    break;
                case 0x02:
                    voice.O3 = (short)MergeWordMasked((ushort)voice.O3, data, mask);
                    break;
                case 0x03:
                    voice.O3Prev = (short)MergeWordMasked((ushort)voice.O3Prev, data, mask);
                    break;
                case 0x04:
                    voice.O2 = (short)MergeWordMasked((ushort)voice.O2, data, mask);
                    break;
                case 0x05:
                    voice.O2Prev = (short)MergeWordMasked((ushort)voice.O2Prev, data, mask);
                    break;
                case 0x06:
                    voice.O1 = (short)MergeWordMasked((ushort)voice.O1, data, mask);
                    break;
                case 0x0d:
                    if ((mask & 0x00ff) != 0)
                        SetActiveVoices(data);
                    break;
                case 0x0f:
                    if ((mask & 0x00ff) != 0)
                        _currentPage = (byte)(data & 0x7f);
                    break;
            }
        }

        private void WriteTest(int offset, ushort data, ushort mask)
        {
            if (offset == 0x08)
            {
                if ((mask & 0xff00) != 0)
                    _serialMode = (ushort)((_serialMode & ~0xf800) | (data & 0xf800));
                if ((mask & 0x00ff) != 0)
                    _serialMode = (ushort)((_serialMode & ~0x0007) | (data & 0x0007));
            }
            else if (offset == 0x0d && (mask & 0x00ff) != 0)
                SetActiveVoices(data);
            else if (offset == 0x0f && (mask & 0x00ff) != 0)
                _currentPage = (byte)(data & 0x7f);
        }

        private void SetActiveVoices(ushort data)
        {
            _activeVoices = (byte)(data & 0x1f);
            UpdateChipSampleRate();
        }

        private void NoteControlWrite(Voice voice, ushort data, ushort mask)
        {
            _controlWrites++;
            if ((voice.Control & ControlStopMask) == 0)
                _runControlWrites++;
            _lastControlVoice = voice.Index;
            _lastControlData = data;
            _lastControlMask = mask;
            _lastControlValue = voice.Control;
        }

        private void UpdateChipSampleRate()
        {
            _chipSampleRate = SoundCpuClockHz / (16.0 * (_activeVoices + 1));
        }

        private void RenderVoice(Voice voice, int sampleFrames, ref int left, ref int right)
        {
            if ((voice.Control & ControlStopMask) != 0)
                return;

            uint accum = voice.Accum;
            short s0 = ReadSample(voice, accum >> AddressFracBits);
            short s1 = ReadSample(voice, (accum >> AddressFracBits) + 1);
            int frac = (int)(accum & ((1 << AddressFracBits) - 1));
            int sample = ((s0 * ((1 << AddressFracBits) - frac)) + (s1 * frac)) >> AddressFracBits;
            ApplyFilters(voice, ref sample);

            left += (sample * voice.LeftVolume) >> 8;
            right += (sample * voice.RightVolume) >> 8;

            double advance = voice.AdvanceRemainder + voice.Freq * (_chipSampleRate / OutputSampleRate);
            uint step = (uint)advance;
            voice.AdvanceRemainder = advance - step;
            if (step == 0 && voice.Freq != 0 && sampleFrames > 0)
                voice.AdvanceRemainder = advance;

            voice.Accum = (voice.Control & ControlDir) == 0
                ? (accum + step) & 0x1fffffff
                : (accum - step) & 0x1fffffff;
            CheckEnd(voice);
        }

        private void CheckEnd(Voice voice)
        {
            bool reverse = (voice.Control & ControlDir) != 0;
            bool hit = reverse ? voice.Accum < voice.Start : voice.Accum > voice.End;
            if (!hit)
                return;

            if ((voice.Control & ControlIrqe) != 0)
                voice.Control |= ControlIrq;
            if ((voice.Control & ControlIrq) != 0 && (_irqv & 0x80) != 0)
            {
                _irqv = (byte)(voice.Index & 0x1f);
                voice.Control = (ushort)(voice.Control & ~ControlIrq);
                _irqCallback?.Invoke(true);
            }

            switch (voice.Control & (ControlLpe | ControlBle))
            {
                case 0:
                case ControlBle:
                    voice.Control |= 0x0001;
                    break;
                case ControlLpe:
                    voice.Accum = reverse
                        ? voice.End - (voice.Start - voice.Accum)
                        : voice.Start + (voice.Accum - voice.End);
                    break;
                case ControlLpe | ControlBle:
                    voice.Accum = reverse
                        ? voice.Start + (voice.Start - voice.Accum)
                        : voice.End - (voice.Accum - voice.End);
                    voice.Control ^= ControlDir;
                    break;
            }
        }

        private short ReadSample(Voice voice, uint wordAddress)
        {
            int bank = _banks[voice.Index & 0x1f];
            int word = (bank + (int)wordAddress) & _sampleWordMask;
            int byteOffset = word << 1;
            if ((uint)(byteOffset + 1) >= (uint)_samples.Length)
                return 0;
            return unchecked((short)((_samples[byteOffset] << 8) | _samples[byteOffset + 1]));
        }

        private static void ApplyFilters(Voice voice, ref int sample)
        {
            sample = ApplyLowpass(sample, voice.K1, voice.O1);
            voice.O1 = (short)Clamp16(sample);
            sample = ApplyLowpass(sample, voice.K1, voice.O2);
            voice.O2Prev = voice.O2;
            voice.O2 = (short)Clamp16(sample);

            int lp = (voice.Control >> 10) & 3;
            if ((lp & 1) != 0)
                sample = ApplyLowpass(sample, voice.K1, voice.O3);
            else
                sample = ApplyHighpass(sample, voice.K2, voice.O3, voice.O2Prev);
            voice.O3Prev = voice.O3;
            voice.O3 = (short)Clamp16(sample);

            if ((lp & 2) != 0)
                sample = ApplyLowpass(sample, voice.K2, voice.O4);
            else
                sample = ApplyHighpass(sample, voice.K2, voice.O4, voice.O3Prev);
            voice.O4 = (short)Clamp16(sample);
        }

        private static int ApplyLowpass(int output, ushort cutoff, int input)
            => (((cutoff >> 4) * (output - input)) >> 12) + input;

        private static int ApplyHighpass(int output, ushort cutoff, int input, int previous)
            => output - previous + (((cutoff >> 4) * input) >> 13) + input / 2;

        private static void WriteAddressHigh(ref uint target, ushort data, bool hi, bool lo)
        {
            if (lo)
                target = (target & ~0x00ff0000u) | ((uint)(data & 0x00ff) << 16);
            if (hi)
                target = (target & ~0x1f000000u) | ((uint)(data & 0x1f00) << 16);
        }

        private static void WriteAddressLow(ref uint target, ushort data, bool hi, bool lo)
        {
            if (lo)
                target = (target & ~0x000000e0u) | (uint)(data & 0x00e0);
            if (hi)
                target = (target & ~0x0000ff00u) | (uint)(data & 0xff00);
        }

        private static void WriteAddressLoFull(ref uint target, ushort data, bool hi, bool lo)
        {
            if (lo)
                target = (target & ~0x000000ffu) | (uint)(data & 0x00ff);
            if (hi)
                target = (target & ~0x0000ff00u) | (uint)(data & 0xff00);
        }

        private static void WriteFilter(ref ushort target, ushort data, bool hi, bool lo)
        {
            if (lo)
                target = (ushort)((target & ~0x00f0) | (data & 0x00f0));
            if (hi)
                target = (ushort)((target & ~0xff00) | (data & 0xff00));
        }

        private static ushort MergeWordMasked(ushort old, ushort data, ushort mask)
        {
            if ((mask & 0xff00) != 0)
                old = (ushort)((old & 0x00ff) | (data & 0xff00));
            if ((mask & 0x00ff) != 0)
                old = (ushort)((old & 0xff00) | (data & 0x00ff));
            return old;
        }

        private static short Clamp16(int value)
            => value > short.MaxValue ? short.MaxValue : value < short.MinValue ? short.MinValue : (short)value;

        public void SaveState(BinaryWriter writer)
        {
            writer.Write(_chipSampleRate);
            writer.Write(_currentPage);
            writer.Write(_activeVoices);
            writer.Write(_irqv);
            writer.Write(_serialMode);
            for (int i = 0; i < _banks.Length; i++)
                writer.Write(_banks[i]);
            for (int i = 0; i < _voices.Length; i++)
                _voices[i].SaveState(writer);
        }

        public void LoadState(BinaryReader reader)
        {
            _chipSampleRate = reader.ReadDouble();
            _currentPage = reader.ReadByte();
            _activeVoices = reader.ReadByte();
            _irqv = reader.ReadByte();
            _serialMode = reader.ReadUInt16();
            for (int i = 0; i < _banks.Length; i++)
                _banks[i] = reader.ReadInt32();
            for (int i = 0; i < _voices.Length; i++)
                _voices[i].LoadState(reader);
            _irqCallback?.Invoke((_irqv & 0x80) == 0);
        }

        private sealed class Voice
        {
            public readonly int Index;
            public ushort Control;
            public uint Freq;
            public uint Start;
            public uint End;
            public uint Accum;
            public byte LeftVolume;
            public byte RightVolume;
            public ushort K1;
            public ushort K2;
            public short O1;
            public short O2;
            public short O2Prev;
            public short O3;
            public short O3Prev;
            public short O4;
            public double AdvanceRemainder;

            public Voice(int index)
            {
                Index = index;
                Reset();
            }

            public void Reset()
            {
                Control = ControlStopMask;
                Freq = 0;
                Start = 0;
                End = 0;
                Accum = 0;
                LeftVolume = 0x80;
                RightVolume = 0x80;
                K1 = 0;
                K2 = 0;
                O1 = O2 = O2Prev = O3 = O3Prev = O4 = 0;
                AdvanceRemainder = 0;
            }

            public void SaveState(BinaryWriter writer)
            {
                writer.Write(Control);
                writer.Write(Freq);
                writer.Write(Start);
                writer.Write(End);
                writer.Write(Accum);
                writer.Write(LeftVolume);
                writer.Write(RightVolume);
                writer.Write(K1);
                writer.Write(K2);
                writer.Write(O1);
                writer.Write(O2);
                writer.Write(O2Prev);
                writer.Write(O3);
                writer.Write(O3Prev);
                writer.Write(O4);
                writer.Write(AdvanceRemainder);
            }

            public void LoadState(BinaryReader reader)
            {
                Control = reader.ReadUInt16();
                Freq = reader.ReadUInt32();
                Start = reader.ReadUInt32();
                End = reader.ReadUInt32();
                Accum = reader.ReadUInt32();
                LeftVolume = reader.ReadByte();
                RightVolume = reader.ReadByte();
                K1 = reader.ReadUInt16();
                K2 = reader.ReadUInt16();
                O1 = reader.ReadInt16();
                O2 = reader.ReadInt16();
                O2Prev = reader.ReadInt16();
                O3 = reader.ReadInt16();
                O3Prev = reader.ReadInt16();
                O4 = reader.ReadInt16();
                AdvanceRemainder = reader.ReadDouble();
            }
        }
    }
}
