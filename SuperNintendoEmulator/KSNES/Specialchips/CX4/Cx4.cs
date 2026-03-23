using System;
using System.Runtime.CompilerServices;
using KSNES.SNESSystem;

namespace KSNES.Specialchips.CX4;

public sealed class Cx4
{
    private static readonly bool TraceCx4 =
        string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_CX4"), "1", StringComparison.Ordinal);
    private static readonly bool TraceCx4Io =
        string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_CX4_IO"), "1", StringComparison.Ordinal);
    private static readonly bool TraceCx4Ops =
        string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_CX4_OPS"), "1", StringComparison.Ordinal);
    private static readonly int TraceCx4Limit = GetTraceLimit();
    private static readonly bool Cx4Instant =
        string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_CX4_INSTANT"), "1", StringComparison.Ordinal);
    private static readonly uint[] SfrConstants =
    [
        0x000000, 0xffffff, 0x00ff00, 0xff0000,
        0x00ffff, 0xffff00, 0x800000, 0x7fffff,
        0x008000, 0x007fff, 0xff7fff, 0xffff7f,
        0x010000, 0xfeffff, 0x000100, 0x00feff
    ];
    private static int _traceCount;
    private enum Cc : byte
    {
        Z = 1 << 0,
        C = 1 << 1,
        N = 1 << 2,
        V = 1 << 3,
        I = 1 << 4
    }

    private enum BusMode
    {
        Idle = 0,
        Read = 1,
        Write = 2
    }

    private const int CachePage = 0x100;

    [NonSerialized]
    private readonly ISNESSystem _snes;

    private ulong _cycles;
    private ulong _cyclesStart;
    private long _suspendTimer;
    private uint _running;

    private uint _prgBaseAddress;
    private ushort _prgStartupBank;
    private byte _prgStartupPc;
    private byte _prgCachePage;
    private byte _prgCacheLock;
    private readonly uint[] _prgCache = new uint[2];
    private readonly ushort[] _prgPage0 = new ushort[0x100];
    private readonly ushort[] _prgPage1 = new ushort[0x100];
    [NonSerialized]
    private ushort[] _activePrg;
    private int _prgCacheTimer;
    private bool _programCacheDirty;

    private byte _pc;
    private ushort _pb;
    private ushort _pbLatch;
    private byte _cc;
    private uint _a;
    private uint _sp;
    private readonly StackEntry[] _stack = new StackEntry[0x08];
    private readonly uint[] _reg = new uint[0x10];
    private readonly byte[] _vectors = new byte[0x20];
    private readonly byte[] _ram = new byte[0x400 * 3];

    private ulong _multiplier;

    // bus
    private uint _busAddress;
    private BusMode _busMode;
    private uint _busData;
    private int _busTimer;

    // cpu registers (bus)
    private uint _busAddressPointer;
    private uint _ramAddressPointer;
    private uint _romData;
    private uint _ramData;

    private byte _irqcfg;
    private byte _unkcfg;
    private byte _waitstate;

    private uint _dmaSource;
    private uint _dmaDest;
    private ushort _dmaLength;
    private int _dmaTimer;

    private int _structDataLength;
    private double _cyclesPerMaster;
    private ulong _syncTo;
    private readonly uint[] _rom = new uint[0x400];

    private readonly int[] _regIndex =
    [
        0, 1, 2, 3, 4, 5, 6, 7,
        8, 9, 10, 11, 12, 13, 14, 15
    ];

    public Cx4(ISNESSystem snes)
    {
        _snes = snes;
        _activePrg = _prgPage0;
        Init();
    }

    public void Reset()
    {
        if (TraceCx4)
            System.Threading.Interlocked.Exchange(ref _traceCount, 0);
        Array.Clear(_ram, 0, _ram.Length);
        Array.Clear(_vectors, 0, _vectors.Length);
        Array.Clear(_reg, 0, _reg.Length);
        Array.Clear(_prgPage0, 0, _prgPage0.Length);
        Array.Clear(_prgPage1, 0, _prgPage1.Length);
        Array.Clear(_stack, 0, _stack.Length);

        _cycles = 0;
        _cyclesStart = 0;
        _suspendTimer = 0;
        _running = 0;
        _prgBaseAddress = 0;
        _prgStartupBank = 0;
        _prgStartupPc = 0;
        _prgCachePage = 0;
        _prgCacheLock = 0;
        _activePrg = _prgPage0;
        _prgCacheTimer = 0;
        InvalidateProgramCache();
        _pc = 0;
        _pb = 0;
        _pbLatch = 0;
        _cc = 0x00;
        _a = 0xffffff;
        _sp = 0;
        _multiplier = 0;
        _busAddress = 0;
        _busMode = BusMode.Idle;
        _busData = 0;
        _busTimer = 0;
        _busAddressPointer = 0;
        _ramAddressPointer = 0;
        _romData = 0;
        _ramData = 0;
        _irqcfg = 0;
        _unkcfg = 1;
        _waitstate = 0x33;
        _dmaSource = 0;
        _dmaDest = 0;
        _dmaLength = 0;
        _dmaTimer = 0;
        Trace("[CX4] reset");
        Trace($"[CX4] trace_io={TraceCx4Io} trace_ops={TraceCx4Ops}");
    }

    public byte Read(int address)
    {
        SyncToSnes();
        TraceRead(address);
        if ((address & 0xfff) < 0xc00)
            return _ram[address & 0xfff];
        if (address >= 0x7f80 && address <= 0x7faf)
        {
            int reg = address & 0x3f;
            return (byte)GetByte(_reg[reg / 3], reg % 3);
        }
        return ReadStatus(address);
    }

    public void Write(int address, byte data)
    {
        SyncToSnes();
        TraceWrite(address, data);
        if ((address & 0xfff) < 0xc00)
        {
            _ram[address & 0xfff] = data;
            return;
        }
        if (address >= 0x7f80 && address <= 0x7faf)
        {
            int reg = address & 0x3f;
            SetByte(ref _reg[reg / 3], data, reg % 3);
            return;
        }
        WriteStatus(address, data);
    }

    public void RunTo(ulong snesCycles)
    {
        _syncTo = (ulong)(snesCycles * _cyclesPerMaster);
        Run();
    }

    public void SetPal(bool isPal)
    {
        _cyclesPerMaster = 20000000.0 / (isPal ? (1364 * 312 * 50.0) : (1364 * 262 * 60.0));
    }

    public void ResyncAfterLoad()
    {
        _activePrg = _prgCachePage == 0 ? _prgPage0 : _prgPage1;
        if (_running != 0 && FindCache(ResolveCacheAddress()) == -1)
            _programCacheDirty = true;
    }

    private void Init()
    {
        SetPal((_snes as KSNES.SNESSystem.SNESSystem)?.IsPal ?? false);
        _structDataLength = 0; // unused in C# port
        double pi = Math.Atan(1) * 4;
        for (int i = 0; i < 0x100; i++)
        {
            _rom[0x000 + i] = i == 0 ? 0xffffffu : (uint)(0x800000 / i);
            _rom[0x100 + i] = (uint)(0x100000 * Math.Sqrt(i));
        }
        for (int i = 0; i < 0x80; i++)
        {
            _rom[0x200 + i] = (uint)(0x1000000 * Math.Sin(((i * 90.0) / 128.0) * pi / 180.0));
            _rom[0x280 + i] = (uint)(0x800000 / (90.0 * pi / 180.0) * Math.Asin(i / 128.0));
            _rom[0x300 + i] = (uint)(0x10000 * (Math.Tan(((i * 90.0) / 128.0) * pi / 180.0) + 0.00000001));
            _rom[0x380 + i] = i == 0 ? 0xffffffu : (uint)(0x1000000 * Math.Cos(((i * 90.0) / 128.0) * pi / 180.0));
        }
        long hash = 0;
        for (int i = 0; i < 0x400; i++)
            hash += _rom[i];
        if (hash != 0x169c91535L)
            Console.WriteLine($"[CX4] rom generation failed (bad hash, {hash:x})");
        Reset();
    }

    private static int SignExtend(int value, int fromBits)
    {
        return (int)((value << (32 - fromBits)) >> (32 - fromBits));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void SetByte(ref uint var, uint data, int offset)
    {
        int idx = offset & 3;
        if (idx >= 3)
            return;
        int shift = idx * 8;
        var = (var & ~(0xffu << shift)) | ((data & 0xffu) << shift);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void SetByte(ref ushort var, uint data, int offset)
    {
        int idx = offset & 3;
        if (idx >= 2)
            return;
        int shift = idx * 8;
        var = (ushort)(((uint)var & ~(0xffu << shift)) | ((data & 0xffu) << shift));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint GetByte(uint var, int offset)
    {
        int idx = offset & 3;
        if (idx >= 3)
            return 0;
        int shift = idx * 8;
        return (var >> shift) & 0xffu;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void SetFlag(Cc flag, bool value)
    {
        _cc = (byte)((_cc & ~(byte)flag) | (value ? (byte)flag : 0));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool GetFlag(Cc flag) => (_cc & (byte)flag) != 0;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void SetNZ(int value)
    {
        SetFlag(Cc.N, (value & 0x800000) != 0);
        SetFlag(Cc.Z, value == 0);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void SetA(int value) => _a = (uint)(value & 0xffffff);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private uint GetA(int subOp)
    {
        int shift = subOp switch
        {
            0 => 0,
            1 => 1,
            2 => 8,
            3 => 16,
            _ => 0
        };
        return (_a << shift) & 0xffffff;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private uint ResolveCacheAddress() => _prgBaseAddress + (uint)(_pb * (CachePage << 1));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int FindCache(uint address)
    {
        if (_prgCache[0] == address)
            return 0;
        return _prgCache[1] == address ? 1 : -1;
    }

    private void PopulateCache(uint address)
    {
        ushort[] page = _prgCachePage == 0 ? _prgPage0 : _prgPage1;
        _activePrg = page;
        _prgCacheTimer = 224;
        if (_prgCache[_prgCachePage] == address)
            return;
        _prgCache[_prgCachePage] = address;
        for (int i = 0; i < CachePage; i++)
        {
            uint a = address++;
            int lo = ReadRomLoRom(a);
            int hi = ReadRomLoRom(address++);
            page[i] = (ushort)(lo | (hi << 8));
        }
        _prgCacheTimer += ((_waitstate & 0x07) * CachePage) * 2;
    }

    private void SelectCachePage()
    {
        if (_prgCacheLock != 0)
        {
            _prgCachePage = (byte)((_prgCachePage + 1) & 1);
            if ((_prgCacheLock & (1 << _prgCachePage)) != 0)
                _prgCachePage = (byte)((_prgCachePage + 1) & 1);
            if ((_prgCacheLock & (1 << _prgCachePage)) != 0)
                _running = 0;
        }
        else
        {
            _prgCachePage = (byte)((_prgCachePage + 1) & 1);
        }
        PopulateCache(ResolveCacheAddress());
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int BusCyclesLeft()
    {
        long left = (long)_syncTo - (long)_cycles;
        return left < 0 ? 0 : (int)left;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void CycleAdvance(int cyc)
    {
        if (_busTimer > 0)
        {
            _busTimer -= cyc;
            if (_busTimer < 1)
            {
                switch (_busMode)
                {
                    case BusMode.Read:
                        _busData = (uint)ReadSnes(_busAddress);
                        break;
                    case BusMode.Write:
                        WriteSnes(_busAddress, (byte)_busData);
                        break;
                }
                _busMode = BusMode.Idle;
                _busTimer = 0;
            }
        }
        _cycles += (uint)cyc;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int GetMemAccessTime(uint address)
    {
        return IsInternalRam(address) ? 0 : (_waitstate & 0x07);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool IsInternalRam(uint address)
    {
        return (address & 0x40e000) == 0x6000;
    }

    private void DoDma()
    {
        uint dest = _dmaDest;
        uint source = _dmaSource;
        for (int i = 0; i < _dmaLength; i++)
        {
            WriteSnes(dest++, (byte)ReadSnes(source++));
        }
        _dmaTimer = _dmaLength * (1 + GetMemAccessTime(dest) + GetMemAccessTime(source));
    }

    private byte ReadStatus(int address)
    {
        switch (address)
        {
            case 0x7f40: return (byte)((_dmaSource >> 0) & 0xff);
            case 0x7f41: return (byte)((_dmaSource >> 8) & 0xff);
            case 0x7f42: return (byte)((_dmaSource >> 16) & 0xff);
            case 0x7f43: return (byte)((_dmaLength >> 0) & 0xff);
            case 0x7f44: return (byte)((_dmaLength >> 8) & 0xff);
            case 0x7f45: return (byte)((_dmaDest >> 0) & 0xff);
            case 0x7f46: return (byte)((_dmaDest >> 8) & 0xff);
            case 0x7f47: return (byte)((_dmaDest >> 16) & 0xff);
            case 0x7f48: return _prgCachePage;
            case 0x7f49: return (byte)((_prgBaseAddress >> 0) & 0xff);
            case 0x7f4a: return (byte)((_prgBaseAddress >> 8) & 0xff);
            case 0x7f4b: return (byte)((_prgBaseAddress >> 16) & 0xff);
            case 0x7f4c: return _prgCacheLock;
            case 0x7f4d: return (byte)((_prgStartupBank >> 0) & 0xff);
            case 0x7f4e: return (byte)((_prgStartupBank >> 8) & 0xff);
            case 0x7f4f: return _prgStartupPc;
            case 0x7f50: return _waitstate;
            case 0x7f51: return _irqcfg;
            case 0x7f52: return _unkcfg;
            case 0x7f53:
            case 0x7f54:
            case 0x7f55:
            case 0x7f56:
            case 0x7f57:
            case 0x7f59:
            case 0x7f5b:
            case 0x7f5c:
            case 0x7f5d:
            case 0x7f5e:
            case 0x7f5f:
            {
                int transfer = (_prgCacheTimer > 0) || (_busTimer > 0) || (_dmaTimer > 0) ? 1 : 0;
                int running = transfer | (_running != 0 ? 1 : 0);
                int res = (transfer << 7) | (running << 6) | (GetFlag(Cc.I) ? 2 : 0) | (_suspendTimer != 0 ? 1 : 0);
                return (byte)res;
            }
            case 0x7f58:
            case 0x7f5a:
                return 0;
            default:
                if (address >= 0x7f60 && address <= 0x7f7f)
                    return _vectors[address & 0x1f];
                break;
        }
        return 0;
    }

    private void WriteStatus(int address, byte data)
    {
        switch (address)
        {
            case 0x7f40: _dmaSource = (_dmaSource & 0xffff00) | data; break;
            case 0x7f41: _dmaSource = (_dmaSource & 0xff00ff) | ((uint)data << 8); break;
            case 0x7f42: _dmaSource = (_dmaSource & 0x00ffff) | ((uint)data << 16); break;
            case 0x7f43: _dmaLength = (ushort)((_dmaLength & 0xff00) | data); break;
            case 0x7f44: _dmaLength = (ushort)((_dmaLength & 0x00ff) | (data << 8)); break;
            case 0x7f45: _dmaDest = (_dmaDest & 0xffff00) | data; break;
            case 0x7f46: _dmaDest = (_dmaDest & 0xff00ff) | ((uint)data << 8); break;
            case 0x7f47: _dmaDest = (_dmaDest & 0x00ffff) | ((uint)data << 16); DoDma(); break;
            case 0x7f48: _prgCachePage = (byte)(data & 0x01); PopulateCache(ResolveCacheAddress()); break;
            case 0x7f49: _prgBaseAddress = (_prgBaseAddress & 0xffff00) | data; InvalidateProgramCache(); break;
            case 0x7f4a: _prgBaseAddress = (_prgBaseAddress & 0xff00ff) | ((uint)data << 8); InvalidateProgramCache(); break;
            case 0x7f4b: _prgBaseAddress = (_prgBaseAddress & 0x00ffff) | ((uint)data << 16); InvalidateProgramCache(); break;
            case 0x7f4c: _prgCacheLock = (byte)(data & 0x03); break;
            case 0x7f4d: _prgStartupBank = (ushort)((_prgStartupBank & 0xff00) | data); break;
            case 0x7f4e: _prgStartupBank = (ushort)((_prgStartupBank & 0x00ff) | ((data & 0x7f) << 8)); break;
            case 0x7f4f:
                _prgStartupPc = data;
                if (_running == 0)
                {
                    _pb = _prgStartupBank;
                    _pc = _prgStartupPc;
                    _running = 1;
                    _cyclesStart = _cycles;
                    DoCache();
                    Trace($"[CX4] start pb=0x{_pb:X4} pc=0x{_pc:X2} base=0x{_prgBaseAddress:X6}");
                    if (Cx4Instant)
                        RunUntilStop();
                }
                break;
            case 0x7f50: _waitstate = (byte)(data & 0x77); break;
            case 0x7f51:
                _irqcfg = (byte)(data & 0x01);
                if ((_irqcfg & 0x01) != 0)
                {
                    _snes.CPU.IrqWanted = false;
                    SetFlag(Cc.I, false);
                }
                break;
            case 0x7f52: _unkcfg = (byte)(data & 0x01); break;
            case 0x7f53: _running = 0; break;
            case >= 0x7f55 and <= 0x7f5c:
                {
                    int offset = address - 0x7f55;
                    _suspendTimer = offset == 0 ? -1 : (offset << 5);
                    break;
                }
            case 0x7f5d: _suspendTimer = 0; break;
            case 0x7f5e:
                SetFlag(Cc.I, false);
                break;
            default:
                if (address >= 0x7f60 && address <= 0x7f7f)
                    _vectors[address & 0x1f] = data;
                break;
        }
    }

    private void Run()
    {
        EnsureProgramCache();
        while (_cycles < _syncTo)
        {
            int tcyc = 2;
            if (_prgCacheTimer > 0)
            {
                tcyc = BusCyclesLeft() > _prgCacheTimer ? _prgCacheTimer : 1;
                _prgCacheTimer -= tcyc;
            }
            else if (_dmaTimer > 0)
            {
                tcyc = BusCyclesLeft() > _dmaTimer ? _dmaTimer : 1;
                _dmaTimer -= tcyc;
            }
            else if (_suspendTimer != 0)
            {
                if (_suspendTimer < 0)
                {
                    CycleAdvance(1);
                    continue;
                }
                tcyc = BusCyclesLeft() > _suspendTimer ? (int)_suspendTimer : 1;
                if (_suspendTimer > 0)
                    _suspendTimer -= tcyc;
            }
            else if (_running == 0)
            {
                tcyc = BusCyclesLeft();
            }
            else
            {
                RunInsn();
                continue;
            }
            CycleAdvance(tcyc);
        }
    }

    private void RunUntilStop()
    {
        const int maxSteps = 2_000_000;
        int steps = 0;
        int ops = 0;
        EnsureProgramCache();
        if (_suspendTimer < 0)
            _suspendTimer = 0;
        while (steps++ < maxSteps)
        {
            if (_prgCacheTimer > 0)
            {
                int tcyc = _prgCacheTimer > 1 ? 1 : _prgCacheTimer;
                _prgCacheTimer -= tcyc;
                CycleAdvance(tcyc);
                continue;
            }
            if (_dmaTimer > 0)
            {
                int tcyc = _dmaTimer > 1 ? 1 : _dmaTimer;
                _dmaTimer -= tcyc;
                CycleAdvance(tcyc);
                continue;
            }
            if (_suspendTimer != 0)
            {
                if (_suspendTimer < 0)
                {
                    _suspendTimer = 0;
                }
                int tcyc = _suspendTimer > 1 ? 1 : (int)_suspendTimer;
                if (_suspendTimer > 0)
                    _suspendTimer -= tcyc;
                CycleAdvance(tcyc);
                continue;
            }
            if (_running == 0)
                return;
            ops++;
            RunInsn();
        }
        Trace($"[CX4] instant run hit step limit ops={ops} cache={_prgCacheTimer} dma={_dmaTimer} suspend={_suspendTimer}");
    }

    private void RunInsn()
    {
        ushort opcode = Fetch();
        int subOp = (opcode & 0x0300) >> 8;
        int immed = opcode & 0x00ff;
        uint temp;

        switch (opcode & 0xfc00)
        {
            case 0x0000:
                break;
            case 0x0800: JmpJsr(false, true, subOp, immed); break;
            case 0x0c00: JmpJsr(false, GetFlag(Cc.Z), subOp, immed); break;
            case 0x1000: JmpJsr(false, GetFlag(Cc.C), subOp, immed); break;
            case 0x1400: JmpJsr(false, GetFlag(Cc.N), subOp, immed); break;
            case 0x1800: JmpJsr(false, GetFlag(Cc.V), subOp, immed); break;
            case 0x2800: JmpJsr(true, true, subOp, immed); break;
            case 0x2c00: JmpJsr(true, GetFlag(Cc.Z), subOp, immed); break;
            case 0x3000: JmpJsr(true, GetFlag(Cc.C), subOp, immed); break;
            case 0x3400: JmpJsr(true, GetFlag(Cc.N), subOp, immed); break;
            case 0x3800: JmpJsr(true, GetFlag(Cc.V), subOp, immed); break;
            case 0x3c00:
                PullStack();
                DoCache();
                CycleAdvance(2);
                break;
            case 0x1c00:
                CycleAdvance(_busTimer);
                break;
            case 0x2400:
                {
                    int value = immed & 1;
                    bool flag = subOp switch
                    {
                        1 => GetFlag(Cc.C),
                        2 => GetFlag(Cc.Z),
                        3 => GetFlag(Cc.N),
                        _ => false
                    };
                    if ((flag ? 1 : 0) == value)
                        Fetch();
                    break;
                }
            case 0x4000:
                _busAddressPointer = (_busAddressPointer + 1) & 0xffffff;
                break;
            case 0x4800:
            case 0x4c00:
                Sub(GetImmed(opcode, subOp, immed), (uint)GetA(subOp));
                break;
            case 0x5000:
            case 0x5400:
                Sub((uint)GetA(subOp), GetImmed(opcode, subOp, immed));
                break;
            case 0x5800:
                if (subOp == 1)
                {
                    _a = (uint)((sbyte)_a) & 0xffffff;
                    SetNZ((int)_a);
                }
                else if (subOp == 2)
                {
                    _a = (uint)((short)_a) & 0xffffff;
                    SetNZ((int)_a);
                }
                break;
            case 0x6000:
            case 0x6400:
                switch (subOp)
                {
                    case 0: _a = GetImmed(opcode, subOp, immed); break;
                    case 1:
                        _busData = GetImmed(opcode, subOp, immed);
                        break;
                    case 2: _busAddressPointer = GetImmed(opcode, subOp, immed); break;
                    case 3: _pbLatch = (ushort)(GetImmed(opcode, subOp, immed) & 0x7fff); break;
                }
                break;
            case 0xe000:
                switch (subOp)
                {
                    case 0: SetSfr(immed, _a); break;
                    case 1: SetSfr(immed, _busData); break;
                    case 2: SetSfr(immed, _busAddressPointer); break;
                    case 3: SetSfr(immed, _pbLatch); break;
                }
                break;
            case 0x6800:
                temp = _a & 0xfff;
                if (temp < 0xc00)
                    SetByte(ref _ramData, _ram[temp], subOp);
                break;
            case 0x6c00:
                temp = (_ramAddressPointer + (uint)immed) & 0xfff;
                if (temp < 0xc00)
                    SetByte(ref _ramData, _ram[temp], subOp);
                break;
            case 0xe800:
                temp = _a & 0xfff;
                if (temp < 0xc00)
                    _ram[temp] = (byte)GetByte(_ramData, subOp);
                break;
            case 0xec00:
                temp = (_ramAddressPointer + (uint)immed) & 0xfff;
                if (temp < 0xc00)
                    _ram[temp] = (byte)GetByte(_ramData, subOp);
                break;
            case 0x7000:
                _romData = _rom[_a & 0x3ff];
                break;
            case 0x7400:
                _romData = _rom[((subOp << 8) | immed) & 0x3ff];
                break;
            case 0x7800:
                SetByte(ref _pbLatch, GetImmed(opcode, subOp, immed), subOp);
                _pbLatch &= 0x7fff;
                break;
            case 0x7c00:
                SetByte(ref _pbLatch, (uint)immed, subOp);
                _pbLatch &= 0x7fff;
                break;
            case 0x8000:
            case 0x8400:
                _a = Add((uint)GetA(subOp), GetImmed(opcode, subOp, immed));
                break;
            case 0x8800:
            case 0x8c00:
                _a = Sub(GetImmed(opcode, subOp, immed), (uint)GetA(subOp));
                break;
            case 0x9000:
            case 0x9400:
                _a = Sub((uint)GetA(subOp), GetImmed(opcode, subOp, immed));
                break;
            case 0x9800:
            case 0x9c00:
                _multiplier = (ulong)((long)SignExtend((int)GetImmed(opcode, subOp, immed), 24) * (long)SignExtend((int)_a, 24)) & 0xffffffffffff;
                break;
            case 0xa000:
            case 0xa400:
                SetA((int)(~GetA(subOp) ^ GetImmed(opcode, subOp, immed)));
                SetNZ((int)_a);
                break;
            case 0xa800:
            case 0xac00:
                SetA((int)(GetA(subOp) ^ GetImmed(opcode, subOp, immed)));
                SetNZ((int)_a);
                break;
            case 0xb000:
            case 0xb400:
                SetA((int)(GetA(subOp) & GetImmed(opcode, subOp, immed)));
                SetNZ((int)_a);
                break;
            case 0xb800:
            case 0xbc00:
                SetA((int)(GetA(subOp) | GetImmed(opcode, subOp, immed)));
                SetNZ((int)_a);
                break;
            case 0xc000:
            case 0xc400:
                SetA((int)(_a >> (int)(GetImmed(opcode, subOp, immed) & 0x1f)));
                SetNZ((int)_a);
                break;
            case 0xc800:
            case 0xcc00:
                SetA(SignExtend((int)_a, 24) >> (int)(GetImmed(opcode, subOp, immed) & 0x1f));
                SetNZ((int)_a);
                break;
            case 0xd000:
            case 0xd400:
                temp = GetImmed(opcode, subOp, immed) & 0x1f;
                SetA((int)((_a >> (int)temp) | (_a << (24 - (int)temp))));
                SetNZ((int)_a);
                break;
            case 0xd800:
            case 0xdc00:
                SetA((int)(_a << (int)(GetImmed(opcode, subOp, immed) & 0x1f)));
                SetNZ((int)_a);
                break;
            case 0xf000:
                temp = _a;
                _a = _reg[immed & 0xf] & 0xffffff;
                _reg[immed & 0xf] = temp & 0xffffff;
                break;
            case 0xf800:
                _a = _ramAddressPointer = _ramData = _pbLatch = 0;
                break;
            case 0xfc00:
                _running = 0;
                if ((_irqcfg & 1) == 0)
                {
                    SetFlag(Cc.I, true);
                    _snes.CPU.IrqWanted = true;
                    Trace("[CX4] stop -> IRQ");
                }
                break;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private ushort Fetch()
    {
        ushort opcode = _activePrg[_pc];
        _pc++;
        if (_pc == 0)
        {
            _pb = _pbLatch;
            DoCache();
        }
        CycleAdvance(1);
        if (TraceCx4Ops)
            Trace($"[CX4-OP] op=0x{opcode:X4} pb=0x{_pb:X4} pc=0x{_pc:X2} a=0x{_a:X6}");
        return opcode;
    }

    private void DoCache()
    {
        int newPage = FindCache(ResolveCacheAddress());
        if (newPage != -1)
        {
            _prgCachePage = (byte)newPage;
            _activePrg = newPage == 0 ? _prgPage0 : _prgPage1;
            return;
        }
        _prgCachePage = (byte)((_prgCachePage + 1) & 1);
        PopulateCache(ResolveCacheAddress());
    }

    private void EnsureProgramCache()
    {
        if (!_programCacheDirty)
            return;

        _programCacheDirty = false;
        DoCache();
    }

    private void InvalidateProgramCache()
    {
        _prgCache[0] = uint.MaxValue;
        _prgCache[1] = uint.MaxValue;
        _programCacheDirty = true;
    }

    private void JmpJsr(bool isJsr, bool take, int page, int address)
    {
        if (!take)
            return;
        if (isJsr)
        {
            _stack[_sp].PC = _pc;
            _stack[_sp].PB = _pb;
            _sp = (_sp + 1) & 0x07;
        }
        if (page != 0)
        {
            _pb = _pbLatch;
            DoCache();
        }
        _pc = (byte)address;
        CycleAdvance(2);
    }

    private void PushStack()
    {
        _stack[_sp].PC = _pc;
        _stack[_sp].PB = _pb;
        _sp = (_sp + 1) & 0x07;
    }

    private void PullStack()
    {
        _sp = (_sp - 1) & 0x07;
        _pc = _stack[_sp].PC;
        _pb = _stack[_sp].PB;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private uint GetImmed(ushort opcode, int subOp, int immed)
    {
        const int DirectImm = 0x0400;
        return (opcode & DirectImm) != 0 ? (uint)immed : GetSfr((byte)immed);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private uint Add(uint a1, uint a2)
    {
        uint sum = a1 + a2;
        SetFlag(Cc.C, (sum & 0xff000000) != 0);
        SetFlag(Cc.V, (((a1 ^ sum) & (a2 ^ sum)) & 0x800000) != 0);
        SetFlag(Cc.N, (sum & 0x800000) != 0);
        SetFlag(Cc.Z, (sum & 0xffffff) == 0);
        return sum & 0xffffff;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private uint Sub(uint m, uint s)
    {
        int diff = (int)m - (int)s;
        SetFlag(Cc.C, diff >= 0);
        SetFlag(Cc.V, (((m ^ (uint)diff) & (s ^ (uint)diff)) & 0x800000) != 0);
        SetNZ(diff & 0xffffff);
        return (uint)diff & 0xffffff;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private uint GetSfr(byte address)
    {
        int sfr = address & 0x7f;
        if ((uint)(sfr - 0x60) <= 0x0f)
            return _reg[sfr & 0x0f] & 0xffffff;
        if ((uint)(sfr - 0x50) <= 0x0f)
            return SfrConstants[sfr - 0x50];

        switch (sfr)
        {
            case 0x00: return _a & 0xffffff;
            case 0x01: return (uint)((_multiplier >> 24) & 0xffffff);
            case 0x02: return (uint)((_multiplier >> 0) & 0xffffff);
            case 0x03: return _busData & 0xff;
            case 0x08: return _romData;
            case 0x0c: return _ramData;
            case 0x13: return _busAddressPointer;
            case 0x1c: return _ramAddressPointer & 0xfff;
            case 0x20: return _pc;
            case 0x28: return _pbLatch;
            case 0x2e:
            case 0x2f:
                _busTimer = ((_waitstate >> ((~address & 1) << 2)) & 0x07) + 1;
                _busAddress = _busAddressPointer;
                _busMode = BusMode.Read;
                return 0;
        }
        return 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void SetSfr(int address, uint data)
    {
        int sfr = address & 0x7f;
        if ((uint)(sfr - 0x60) <= 0x0f)
        {
            _reg[sfr & 0x0f] = data & 0xffffff;
            return;
        }

        switch (sfr)
        {
            case 0x00: _a = data & 0xffffff; break;
            case 0x01: _multiplier = (_multiplier & 0x000000ffffff) | ((ulong)data << 24); break;
            case 0x02: _multiplier = (_multiplier & 0xffffff000000) | ((ulong)data << 0); break;
            case 0x03: _busData = data & 0xff; break;
            case 0x08: _romData = data; break;
            case 0x0c: _ramData = data; break;
            case 0x13: _busAddressPointer = data & 0xffffff; break;
            case 0x1c: _ramAddressPointer = data & 0xfff; break;
            case 0x20: _pc = (byte)data; break;
            case 0x28: _pbLatch = (ushort)(data & 0x7fff); break;
            case 0x2e:
            case 0x2f:
                _busTimer = ((_waitstate >> ((~address & 1) << 2)) & 0x07) + 1;
                _busAddress = _busAddressPointer;
                _busMode = BusMode.Write;
                break;
        }
    }

    private int ReadSnes(uint address)
    {
        return _snes.Read((int)address, true);
    }

    private void WriteSnes(uint address, byte value)
    {
        _snes.Write((int)address, value, true);
    }

    private byte ReadRomLoRom(uint address)
    {
        return _snes.ROM.ReadRomByteLoRom(address);
    }

    private void SyncToSnes()
    {
        if (_snes is KSNES.SNESSystem.SNESSystem snesSystem)
            RunTo(snesSystem.Cycles);
        else
            Run();
    }

    private static int GetTraceLimit()
    {
        if (int.TryParse(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_CX4_LIMIT"), out int limit) && limit > 0)
            return limit;
        return 2000;
    }

    private static void Trace(string message)
    {
        if (!TraceCx4)
            return;
        int count = System.Threading.Interlocked.Increment(ref _traceCount);
        if (count > TraceCx4Limit)
            return;
        Console.WriteLine(message);
    }

    private static void TraceRead(int address)
    {
        if (TraceCx4Io)
            Trace($"[CX4-RD] addr=0x{address:X4}");
    }

    private static void TraceWrite(int address, byte data)
    {
        if (TraceCx4Io)
            Trace($"[CX4-WR] addr=0x{address:X4} val=0x{data:X2}");
    }

    private struct StackEntry
    {
        public byte PC;
        public ushort PB;
    }
}
