using System;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;

namespace KSNES.SNESSystem;

public class SNESSystem : ISNESSystem
{
    private enum GpDmaState
    {
        Idle,
        Pending,
        Transfer
    }

    private enum BusPageKind : byte
    {
        Rom,
        WramBank,
        LowWram,
        BBus,
        JoypadPage,
        CpuRegs
    }

    [field: NonSerialized] public ICPU CPU { get; private set; }
    [field: NonSerialized] public IPPU PPU { get; private set; }
    [field: NonSerialized] public IAPU APU { get; private set; }

    [JsonIgnore]
    [NonSerialized]
    private IROM _rom = null!;

    [JsonIgnore]
    [NonSerialized]
    private KSNES.ROM.ROM? _romImpl;

    [JsonIgnore]
    public IROM ROM
    {
        get => _rom;
        set
        {
            _rom = value;
            _romImpl = value as KSNES.ROM.ROM;
        }
    }

    [JsonIgnore]
    private KSNES.ROM.ROM RomImpl => _romImpl ?? throw new InvalidOperationException("SNES ROM is not initialized.");

    private byte[] _ram = [];
    [NonSerialized]
    private byte[] _busPageKind = [];
    [NonSerialized]
    private ushort[] _busPageData = [];

    private const int BusPageAccessMask = 0xff;
    private const int BusPageKindShift = 8;

    [JsonIgnore]
    private readonly int[] _dmaOffs = [
        0, 0, 0, 0,
        0, 1, 0, 1,
        0, 0, 0, 0,
        0, 0, 1, 1,
        0, 1, 2, 3,
        0, 1, 0, 1,
        0, 0, 0, 0,
        0, 0, 1, 1
    ];
   
    [JsonIgnore]
    private readonly int[] _dmaOffLengths = [1, 2, 2, 4, 4, 4, 2, 4];

    private const ulong ApuOutputFrequency = 32040;
    private const ulong ApuMasterClockFrequency = ApuOutputFrequency * 768;
    private const ulong NtscMasterClockFrequency = 21_477_270;
    private const ulong PalMasterClockFrequency = 21_281_370;

    private byte[] _dmaBadr = [];
    private ushort[] _dmaAadr = [];
    private byte[] _dmaAadrBank = [];
    private ushort[] _dmaSize = [];
    private byte[] _hdmaIndBank = [];
    private ushort[] _hdmaTableAdr = [];
    private byte[] _hdmaRepCount = [];
    private byte[] _dmaUnusedByte = [];
    private bool[] _dmaNotifyActive = [];
    private int _dmaNotifyCount;

    public int XPos { get; private set; }
    public int YPos { get; private set; }
    public bool InVblank => _inVblank;
    public bool InHblank => _inHblank;
    public bool InNmi => _vblankNmiFlag;

    private int _cpuCyclesLeft;
    private int _cpuMemOps;
    private ulong _apuMasterCyclesProduct;
    [NonSerialized]
    private int _apuBorrowedMainCycles;

    private int _ramAdr;

    private bool _hIrqEnabled;
    private bool _vIrqEnabled;
    private bool _nmiEnabled;
    private int _hTimer;
    private int _vTimer;
    // Savestate compatibility: older states serialized this field.
    private bool _inNmi;
    private bool _vblankNmiFlag;
    private bool _inIrq;
    private bool _irqLine;
    private int _lastIrqHTime;
    [NonSerialized]
    private ulong _irqRaisedAtCycle;
    private bool _inHblank;
    private bool _inVblank;
    private bool _oddFrame;

    private bool _autoJoyRead;
    private bool _autoJoyBusy;
    private int _autoJoyTimer;
    private bool _autoJoyPendingStart;
    public bool PPULatch { get; private set; }

    private int _joypad1Val;
    private int _joypad2Val;
    private int _joypad1AutoRead;
    private int _joypad2AutoRead;
    private bool _joypadStrobe;
    private int _joypad1State;
    private int _joypad2State;

    private int _multiplyA;
    private int _divA;
    private int _divResult;
    private int _mulResult;

    private bool _fastMem;

    private int _dmaTimer;
    private int _hdmaTimer;
    private bool[] _dmaActive = [];
    private bool[] _hdmaActive = [];
    private GpDmaState _gpdmaState;
    private byte _gpdmaChannel;
    private ushort _gpdmaBytesCopied;
    private ulong _gpdmaStartCycles;
    private bool _pendingDmaWriteValid;
    private bool _pendingDmaWriteBusB;
    private int _pendingDmaWriteAddress;
    private int _pendingDmaWriteValue;

    private readonly bool _tracePpuBusWrites =
        string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_SNES_PPU_BUS"), "1", StringComparison.Ordinal);
    private readonly int _tracePpuBusLimit;
    private int _tracePpuBusCount;
    private readonly bool _traceWramWrites =
        string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_SNES_WRAM"), "1", StringComparison.Ordinal);
    [NonSerialized]
    private readonly HashSet<int> _traceWramAddrs = ParseTraceWramAddrs(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_SNES_WRAM_ADDRS"));
    private readonly bool _traceDma =
        string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_SNES_DMA"), "1", StringComparison.Ordinal);
    private readonly bool _traceInidisp =
        string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_SNES_INIDISP"), "1", StringComparison.Ordinal);
    private readonly bool _traceApuPorts =
        string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_SNES_APU_PORTS"), "1", StringComparison.Ordinal);
    private readonly int _traceApuPortsLimit = ParseTraceLimit("EUTHERDRIVE_TRACE_SNES_APU_PORTS_LIMIT", 256);
    private int _traceApuPortsCount;
    private readonly bool _traceStarOceanApuLoop =
        string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_STAROCEAN_APU_LOOP"), "1", StringComparison.Ordinal);
    [NonSerialized]
    private readonly bool _traceStarOcean4212Loop =
        string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_STAROCEAN_4212_LOOP"), "1", StringComparison.Ordinal);
    [NonSerialized]
    private readonly bool _traceJoypad =
        string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_SNES_JOYPAD"), "1", StringComparison.Ordinal);
    [NonSerialized]
    private readonly int _traceJoypadLimit =
        ParseTraceLimit("EUTHERDRIVE_TRACE_SNES_JOYPAD_LIMIT", 400);
    [NonSerialized]
    private int _traceJoypadCount;
    [NonSerialized]
    private readonly bool _trace4212 =
        string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_4212"), "1", StringComparison.Ordinal);
    [NonSerialized]
    private readonly bool _logVerbose =
        string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_LOG_VERBOSE"), "1", StringComparison.Ordinal);
    [NonSerialized]
    private readonly bool _traceSgngIrqWindow =
        string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_SGNG_IRQ_WINDOW"), "1", StringComparison.Ordinal);
    [NonSerialized]
    private readonly int _traceSgngIrqWindowLimit =
        ParseTraceLimit("EUTHERDRIVE_TRACE_SGNG_IRQ_WINDOW_LIMIT", 128);
    [NonSerialized]
    private int _traceSgngIrqWindowCount;
    private bool HasExplicitWramTraceFilter => _traceWramAddrs.Count > 0;

    private int[] _dmaMode = [];
    private bool[] _dmaFixed = [];
    private bool[] _dmaDec = [];
    private bool[] _hdmaInd = [];
    private bool[] _dmaFromB = [];
    private bool[] _dmaUnusedBit = [];

    private bool[] _hdmaDoTransfer = [];
    private bool[] _hdmaTerminated = [];
    public int OpenBus { get; private set; }
    public string FileName { get; set; }

    public event EventHandler FrameRendered;

    public bool IsPal { get; set; }
    public ulong Cycles { get; private set; }

    [JsonIgnore]
    [field: NonSerialized]
    public IRenderer Renderer { get; set; }

    [JsonIgnore]
    [field: NonSerialized]
    public IAudioHandler AudioHandler { get; set; }

    [JsonIgnore]
    [field: NonSerialized]
    public string GameName { get; set; }

    [JsonIgnore]
    private bool _isExecuting;
    [NonSerialized]
    private readonly bool _useFastReadPath;
    [NonSerialized]
    private readonly bool _useFastWritePath;

#pragma warning disable CS8618 // Non-nullable field must contain a non-null value when exiting constructor. Consider declaring as nullable.
    public SNESSystem(ICPU cpu, IRenderer renderer, IROM rom, IPPU ppu, IAPU apu, IAudioHandler audioHandler)
#pragma warning restore CS8618 // Non-nullable field must contain a non-null value when exiting constructor. Consider declaring as nullable.
    {
        CPU = cpu;
        Renderer = renderer;
        AudioHandler = audioHandler;
        ROM = rom;
        rom?.SetSystem(this);
        PPU = ppu;
        PPU?.SetSystem(this);
        APU = apu;
        CPU?.SetSystem(this);

        if (int.TryParse(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_SNES_PPU_BUS_LIMIT"), out int limit) &&
            limit > 0)
        {
            _tracePpuBusLimit = limit;
        }

        _useFastReadPath = !_traceWramWrites
            && !_traceApuPorts
            && !_traceStarOceanApuLoop
            && !_traceStarOcean4212Loop
            && !_traceJoypad
            && !_traceDma
            && !_trace4212;
        _useFastWritePath = !_traceWramWrites
            && !_tracePpuBusWrites
            && !_traceInidisp
            && !_traceApuPorts
            && !_traceDma
            && !_logVerbose;
        EnsureBusTables();
    }

    private void EnsureBusTables()
    {
        if (_busPageKind.Length != 1 << 16)
        {
            _busPageKind = new byte[1 << 16];
            BuildBusPageKindTable();
        }

        if (_busPageData.Length != 1 << 16)
            _busPageData = new ushort[1 << 16];

        RebuildAccessTimeTable();
    }

    private void BuildBusPageKindTable()
    {
        Array.Fill(_busPageKind, (byte)BusPageKind.Rom);
        for (int bank = 0; bank < 0x100; bank++)
        {
            int baseIndex = bank << 8;
            if (bank == 0x7e || bank == 0x7f)
            {
                for (int page = 0; page < 0x100; page++)
                    _busPageKind[baseIndex + page] = (byte)BusPageKind.WramBank;
                continue;
            }

            bool isLowMirrorBank = bank < 0x40 || (bank >= 0x80 && bank < 0xc0);
            if (!isLowMirrorBank)
                continue;

            for (int page = 0; page < 0x20; page++)
                _busPageKind[baseIndex + page] = (byte)BusPageKind.LowWram;
            _busPageKind[baseIndex + 0x21] = (byte)BusPageKind.BBus;
            _busPageKind[baseIndex + 0x40] = (byte)BusPageKind.JoypadPage;
            _busPageKind[baseIndex + 0x42] = (byte)BusPageKind.CpuRegs;
            _busPageKind[baseIndex + 0x43] = (byte)BusPageKind.CpuRegs;
        }
    }

    private void RebuildAccessTimeTable()
    {
        for (int bank = 0; bank < 0x100; bank++)
        {
            int baseIndex = bank << 8;
            if (bank >= 0x40 && bank < 0x80)
            {
                for (int page = 0; page < 0x100; page++)
                    SetBusPageData(baseIndex + page, 8);
                continue;
            }

            if (bank >= 0xc0)
            {
                byte fastRomTime = _fastMem ? (byte)6 : (byte)8;
                for (int page = 0; page < 0x100; page++)
                    SetBusPageData(baseIndex + page, fastRomTime);
                continue;
            }

            byte highRomTime = _fastMem && bank >= 0x80 ? (byte)6 : (byte)8;
            for (int page = 0; page < 0x100; page++)
            {
                byte accessTime = page switch
                {
                    < 0x20 => 8,
                    < 0x40 => 6,
                    < 0x42 => 12,
                    < 0x60 => 6,
                    < 0x80 => 8,
                    _ => highRomTime
                };
                SetBusPageData(baseIndex + page, accessTime);
            }
        }
    }

    private void SetBusPageData(int pageIndex, byte accessTime)
    {
        _busPageData[pageIndex] = (ushort)(accessTime | (_busPageKind[pageIndex] << BusPageKindShift));
    }

    private BusPageKind GetBusPageKind(ushort pageData)
    {
        return (BusPageKind)(pageData >> BusPageKindShift);
    }

    public ISNESSystem Merge(ISNESSystem system)
    {
        system.AudioHandler = AudioHandler;
        system.Renderer = Renderer;
        system.ROM = ROM;
        ROM.SetSystem(system);
        system.APU.Attach();
        return system;
    }

    public void LoadROM(string fileName)
    {
        FileName = fileName;
        byte[] data = File.ReadAllBytes(FileName);
        LoadRom(data);
        GameName = ROM.Header.Name;
        Reset1();
        CPU.Reset();
        PPU.Reset();
        APU.Reset();
        Reset2();
        Run();
    }

    public void LoadROMForExternal(string fileName)
    {
        FileName = fileName;
        byte[] data = File.ReadAllBytes(FileName);
        LoadRom(data);
        ROM.LoadSRAM();
        GameName = ROM.Header.Name;
        Reset1();
        CPU.Reset();
        PPU.Reset();
        APU.Reset();
        Reset2();
    }

    public void ResetForExternal()
    {
        Reset1();
        CPU.Reset();
        PPU.Reset();
        APU.Reset();
        Reset2();
    }

    public void RunFrameForExternal()
    {
        RunFrame(false);
        Renderer?.RenderBuffer(PPU.GetPixels());
        APU.SetSamples(AudioHandler.SampleBufferL, AudioHandler.SampleBufferR);
        AudioHandler.NextBuffer();
        ROM.RunCoprocessor(Cycles);
        FrameRendered?.Invoke(this, EventArgs.Empty);
    }

    public void StopEmulation()
    {
        _isExecuting = false;
        AudioHandler.Pauze();
    }

    public bool IsRunning()
    {
        return _isExecuting;
    }

    public void ResumeEmulation()
    {
        AudioHandler.Resume();
        if (!string.IsNullOrEmpty(FileName))
        {
            if (ROM?.Header == null)
            {
                byte[] data = File.ReadAllBytes(FileName);
                LoadRom(data);
            }
            ROM!.LoadSRAM();
            _isExecuting = true;
            while (_isExecuting)
            {
                RunFrame(false);
                Renderer.RenderBuffer(PPU.GetPixels());
                APU.SetSamples(AudioHandler.SampleBufferL, AudioHandler.SampleBufferR);
                AudioHandler.NextBuffer();
                ROM.RunCoprocessor(Cycles);
                FrameRendered?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    public void Run()
    {
        ResumeEmulation();
    }

    public int Read(int adr, bool dma = false)
    {
        int fullAdr = adr & 0xffffff;
        ushort pageData = _busPageData[fullAdr >> 8];
        int accessTime = 0;
        if (!dma)
        {
            _cpuMemOps++;
            accessTime = pageData & BusPageAccessMask;
            _cpuCyclesLeft += accessTime;
        }
        int val = _useFastReadPath ? RreadFast(fullAdr, GetBusPageKind(pageData)) : Rread(fullAdr);
        if (!dma && accessTime > 0 && IsCpuApuPortAccess(fullAdr))
        {
            AdvanceApuForCpuAccess(accessTime);
        }
        OpenBus = val;
        return val;
    }

    public void Write(int adr, int value, bool dma = false)
    {
        int fullAdr = adr & 0xffffff;
        ushort pageData = _busPageData[fullAdr >> 8];
        int accessTime = 0;
        if (!dma)
        {
            _cpuMemOps++;
            accessTime = pageData & BusPageAccessMask;
            _cpuCyclesLeft += accessTime;
        }
        OpenBus = value;
        bool isApuPortAccess = !dma && accessTime > 0 && IsCpuApuPortAccess(fullAdr);
        if (_useFastWritePath)
            WwriteFast(fullAdr, value, dma, GetBusPageKind(pageData));
        else
            Wwrite(fullAdr, value, dma);
        if (isApuPortAccess)
        {
            AdvanceApuForCpuAccess(accessTime);
        }
    }

    private void Reset1()
    {
        _ram = new byte[0x20000];
        _dmaBadr = new byte[8];
        _dmaAadr = new ushort[8];
        _dmaAadrBank = new byte[8];
        _dmaSize = new ushort[8];
        _hdmaIndBank = new byte[8];
        _hdmaTableAdr = new ushort[8];
        _hdmaRepCount = new byte[8];
        _dmaUnusedByte = new byte[8];
        _dmaNotifyActive = new bool[8];
        _dmaNotifyCount = 0;
        EnsureBusTables();
    }

    private void Reset2()
    {
        XPos = 0;
        YPos = 0;
        Cycles = 0;
        _cpuCyclesLeft = 5 * 8 + 12;
        _cpuMemOps = 0;
        _apuMasterCyclesProduct = 0;
        _apuBorrowedMainCycles = 0;
        _ramAdr = 0;
        _hIrqEnabled = false;
        _vIrqEnabled = false;
        _nmiEnabled = false;
        _hTimer = 0x1ff;
        _vTimer = 0x1ff;
        _inNmi = false;
        _vblankNmiFlag = false;
        _inIrq = false;
        _irqLine = false;
        _irqRaisedAtCycle = 0;
        _lastIrqHTime = 0;
        _inHblank = false;
        _inVblank = false;
        _oddFrame = false;
        _autoJoyRead = false;
        _autoJoyBusy = false;
        _autoJoyTimer = 0;
        _autoJoyPendingStart = false;
        PPULatch = true;
        _joypad1Val = 0;
        _joypad2Val = 0;
        _joypad1AutoRead = 0;
        _joypad2AutoRead = 0;
        _joypadStrobe = false;
        _joypad1State = 0;
        _joypad2State = 0;
        _multiplyA = 0xff;
        _divA = 0xffff;
        _divResult = 0x101;
        _mulResult = 0xfe01;
        _fastMem = false;
        _dmaTimer = 0;
        _hdmaTimer = 0;
        _dmaActive = new bool[8];
        _hdmaActive = new bool[8];
        _gpdmaState = GpDmaState.Idle;
        _gpdmaChannel = 0;
        _gpdmaBytesCopied = 0;
        _gpdmaStartCycles = 0;
        _pendingDmaWriteValid = false;
        _pendingDmaWriteBusB = false;
        _pendingDmaWriteAddress = 0;
        _pendingDmaWriteValue = 0;
        _dmaMode = new int[8];
        _dmaFixed = new bool[8];
        _dmaDec = new bool[8];
        _hdmaInd = new bool[8];
        _dmaFromB = new bool[8];
        _dmaUnusedBit = new bool[8];
        _hdmaDoTransfer = new bool[8];
        _hdmaTerminated = new bool[8];
        OpenBus = 0;
        RomImpl.ResetCoprocessor();
        EnsureBusTables();
    }

    public void ResyncAfterLoad()
    {
        // Savestates already serialize the live beam position and interrupt/DMA state.
        // Clobbering those latched values on load breaks late-frame states that rely on
        // pending IRQ/NMI/autojoy activity to reproduce the next frame correctly.
        EnsureBusTables();
    }

    private void LoadRom(byte[] rom)
    {
        Header header;
        if (rom.Length % 0x8000 == 0)
        {
            header = ParseHeader(rom);
        }
        else if ((rom.Length - 512) % 0x8000 == 0)
        {
            var newData = new byte[rom.Length - 0x200];
            Array.Copy(rom, 0x200, newData, 0, newData.Length);
            rom = newData;
            header = ParseHeader(rom);
        }
        else
        {
            return;
        }
        GameName = header.Name;
        if (rom.Length < header.RomSize)
        {
            rom = MirrorRomToNextPowerOfTwo(rom);
            header.RomSize = rom.Length;
        }
        RomImpl.LoadROM(rom, header);
    }

    private static byte[] MirrorRomToNextPowerOfTwo(byte[] rom)
    {
        if (rom.Length == 0 || (rom.Length & (rom.Length - 1)) == 0)
            return rom;

        var mirrored = new List<byte>(rom);
        while ((mirrored.Count & (mirrored.Count - 1)) != 0)
        {
            int sourceLen = mirrored.Count & -mirrored.Count;
            int remainingLen = mirrored.Count & ~sourceLen;
            int copyLen = (1 << BitOperations.TrailingZeroCount(remainingLen)) - sourceLen;
            int baseAddr = mirrored.Count & ~sourceLen;
            for (int i = 0; i < copyLen; i++)
            {
                mirrored.Add(mirrored[baseAddr + (i & (sourceLen - 1))]);
            }
        }

        return mirrored.ToArray();
    }

    private void Cycle(bool noPpu) 
    {
        if (TryRunFastCpuWindow(noPpu))
            return;

        AdvanceBaseClocksForStep();
        bool queueNmiForNextCpuSlot = false;
        int currentLineMclks = GetCurrentLineMclks();
        int vBlankStart = IsPal ? 240 : (PPU.FrameOverscan ? 240 : 225);
        if (XPos == 0)
        {
            // HVBJOY reports HBlank during the first 4 master cycles of each scanline.
            _inHblank = true;
            PPU.CheckOverscan(YPos);

            if (YPos == vBlankStart)
            {
                if (_logVerbose)
                {
                    Console.WriteLine($"[VBLANK-START] Y={YPos} IsPal={IsPal} Overscan={PPU.FrameOverscan} Start={vBlankStart}");
                }
                _inNmi = true;
                _vblankNmiFlag = true;
                _inVblank = true;
                if (_autoJoyRead)
                {
                    _autoJoyPendingStart = true;
                }
                if (_nmiEnabled)
                {
                    // Raise the RDNMI latch immediately, but defer CPU delivery until after the
                    // current CPU slot. Games such as Ka-blooey poll $4210 on the VBlank edge and
                    // expect to observe bit 7 before the NMI handler consumes it.
                    queueNmiForNextCpuSlot = true;
                }
            }
            else if (YPos == 0)
            {
                if (_inVblank && _logVerbose)
                {
                    Console.WriteLine($"[VBLANK-END] Y={YPos} X={XPos}");
                }
                _inNmi = false;
                _vblankNmiFlag = false;
                _inVblank = false;
                _autoJoyPendingStart = false;
                InitHdma();
            }
        }
        else if (XPos == 4)
        {
            _inHblank = false;
        }
        else if (XPos == 1096)
        {
            _inHblank = true;
        }
        else if (XPos == 1104)
        {
            if (!_inVblank)
            {
                HandleHdma();
                PPU.PrepareSpriteLine(YPos);
            }
        }

        if (_hdmaTimer > 0)
        {
            _hdmaTimer -= 2;
        }
        else if (_dmaTimer > 0)
        {
            _dmaTimer -= 2;
        }
        else if (_gpdmaState != GpDmaState.Idle)
        {
            HandleDma();
        }
        else if (XPos < 536 || XPos >= 576)
        {
            CpuCycle();
        }
        UpdateIrqLine();
        if (queueNmiForNextCpuSlot)
        {
            CPU.NmiWanted = true;
        }
        
        CPU.IrqWanted = _inIrq || RomImpl.IrqWanted;
        if (XPos == 512 && !noPpu)
        {
            PPU.RenderLine(YPos);
        }
        if (_autoJoyPendingStart && YPos == vBlankStart && XPos == 130)
        {
            _autoJoyPendingStart = false;
            _autoJoyBusy = true;
            _autoJoyTimer = 4224;
        }
        if (_autoJoyBusy)
        {
            _autoJoyTimer -= 2;
            if (_autoJoyTimer <= 0)
            {
                DoAutoJoyRead();
                _autoJoyBusy = false;
                _autoJoyTimer = 0;
            }
        }
        RomImpl.RunCoprocessor(Cycles);
        CatchUpApu();
        AdvanceBeamPosition(currentLineMclks);
    }

    private bool TryRunFastCpuWindow(bool noPpu)
    {
        if (_hdmaTimer > 0
            || _dmaTimer > 0
            || _gpdmaState != GpDmaState.Idle
            || _hIrqEnabled
            || _vIrqEnabled
            || _autoJoyBusy
            || _autoJoyPendingStart)
        {
            return false;
        }

        int currentLineMclks = GetCurrentLineMclks();
        int endX = GetFastCpuWindowEnd(currentLineMclks, noPpu);
        if (endX <= XPos)
            return false;

        bool cpuCanRun = XPos < 536 || XPos >= 576;
        int chunkMclks = endX - XPos;
        if (cpuCanRun)
        {
            // Only fast-forward while the CPU is between instruction boundaries. Once
            // `_cpuCyclesLeft` reaches zero we must fall back to the regular 2-mclk path
            // so CPU register writes, DMA enables, and interrupt sampling still happen at
            // their exact cycle edges.
            int cpuWaitMclks = _cpuCyclesLeft & ~0x1;
            if (cpuWaitMclks <= 0)
                return false;

            chunkMclks = Math.Min(chunkMclks, cpuWaitMclks);
        }

        chunkMclks &= ~0x1;
        if (chunkMclks <= 0)
            return false;

        AdvanceBaseClocks(chunkMclks);
        if (cpuCanRun)
            _cpuCyclesLeft -= chunkMclks;
        RomImpl.RunCoprocessor(Cycles);
        CatchUpApu();
        CPU.IrqWanted = _inIrq || RomImpl.IrqWanted;
        AdvanceBeamPositionBy(chunkMclks, currentLineMclks);
        _lastIrqHTime = GetIrqHTime();

        return true;
    }

    private int GetFastCpuWindowEnd(int currentLineMclks, bool noPpu)
    {
        if (XPos == 0 || XPos == 4 || XPos == 1096 || XPos == 1104 || (!noPpu && XPos == 512))
            return XPos;

        if (XPos < 4)
            return 4;

        if (XPos < 512)
            return noPpu ? 536 : 512;

        if (XPos < 536)
            return 536;

        if (XPos < 576)
            return 576;

        if (XPos < 1096)
            return 1096;

        if (XPos < 1104)
            return 1104;

        if (XPos < currentLineMclks)
            return currentLineMclks;

        return XPos;
    }

    private void AdvanceBaseClocksForStep()
    {
        AdvanceBaseClocks(2);
    }

    private void AdvanceBaseClocks(int mainMasterCycles)
    {
        if (mainMasterCycles <= 0)
            return;

        Cycles += (ulong)mainMasterCycles;
        int apuStepMclks = mainMasterCycles;
        if (_apuBorrowedMainCycles > 0)
        {
            int borrowed = Math.Min(apuStepMclks, _apuBorrowedMainCycles);
            apuStepMclks -= borrowed;
            _apuBorrowedMainCycles -= borrowed;
        }
        if (apuStepMclks > 0)
        {
            _apuMasterCyclesProduct += ApuMasterClockFrequency * (ulong)apuStepMclks;
        }
    }

    private void AdvanceBeamPosition(int currentLineMclks)
    {
        AdvanceBeamPositionBy(2, currentLineMclks);
    }

    private void AdvanceBeamPositionBy(int mainMasterCycles, int currentLineMclks)
    {
        if (mainMasterCycles <= 0)
            return;

        int newX = XPos + mainMasterCycles;
        if (newX < currentLineMclks)
        {
            XPos = newX;
            return;
        }

        XPos = 0;
        YPos++;
        int maxV = IsPal ? 312 : 262;
        if (YPos == maxV)
        {
            YPos = 0;
            _oddFrame = !_oddFrame;
            // The PPU is no longer in VBlank once the frame wraps back to scanline 0.
            // Clear both the live VBlank state and the RDNMI latch at frame wrap.
            _inNmi = false;
            _vblankNmiFlag = false;
            _inVblank = false;
            _autoJoyPendingStart = false;
        }
    }

    private int GetIrqVTime()
    {
        const int irqOffsetMclks = 10;
        int maxV = IsPal ? 312 : 262;
        if (XPos >= irqOffsetMclks)
            return YPos;

        return YPos == 0 ? maxV - 1 : YPos - 1;
    }

    private int GetIrqHTime()
    {
        const int irqOffsetMclks = 10;
        int scanlineMclks = XPos >= irqOffsetMclks
            ? XPos - irqOffsetMclks
            : GetPreviousLineMclks() - (irqOffsetMclks - XPos);
        return scanlineMclks / 4;
    }

    private int GetCurrentLineMclks()
    {
        if (!IsPal && !IsInterlacedPpu() && _oddFrame && YPos == 240)
        {
            return 1360;
        }

        return 1364;
    }

    private int GetPreviousLineMclks()
    {
        if (YPos == 0)
        {
            return 1364;
        }

        int previousLine = YPos - 1;
        if (!IsPal && !IsInterlacedPpu() && _oddFrame && previousLine == 240)
        {
            return 1360;
        }

        return 1364;
    }

    private bool IsInterlacedPpu()
    {
        return PPU is KSNES.PictureProcessing.PPU ppu && ppu.Interlace;
    }

    private static bool RangeContainsExclusiveEnd(int startExclusive, int endExclusive, int value)
    {
        return value > startExclusive && value < endExclusive;
    }

    private static bool RangeContainsInclusiveEnd(int startExclusive, int endInclusive, int value)
    {
        return value > startExclusive && value <= endInclusive;
    }

    private int GetPreviousLineMaxHTime()
    {
        return GetPreviousLineMclks() / 4;
    }

    private void UpdateIrqLine()
    {
        int ppuHTime = GetIrqHTime();
        int ppuVTime = GetIrqVTime();

        bool CheckH()
        {
            int previousLineMaxHTime = GetPreviousLineMaxHTime();
            if (ppuHTime < _lastIrqHTime)
            {
                return _hTimer <= ppuHTime || RangeContainsExclusiveEnd(_lastIrqHTime, previousLineMaxHTime, _hTimer);
            }

            return RangeContainsInclusiveEnd(_lastIrqHTime, ppuHTime, _hTimer);
        }

        bool CheckV() => ppuVTime == _vTimer;

        bool CheckHv()
        {
            if (ppuHTime >= _lastIrqHTime)
            {
                return RangeContainsInclusiveEnd(_lastIrqHTime, ppuHTime, _hTimer) && CheckV();
            }

            if (_hTimer <= ppuHTime)
                return CheckV();

            if (RangeContainsExclusiveEnd(_lastIrqHTime, GetPreviousLineMaxHTime(), _hTimer))
            {
                int maxV = IsPal ? 312 : 262;
                int prevVTime = ppuVTime == 0 ? maxV - 1 : ppuVTime - 1;
                return prevVTime == _vTimer;
            }

            return false;
        }

        bool newIrqLine;
        if (!_hIrqEnabled && !_vIrqEnabled)
        {
            newIrqLine = false;
        }
        else if (_hIrqEnabled && _vIrqEnabled)
        {
            newIrqLine = CheckHv();
        }
        else if (_hIrqEnabled)
        {
            newIrqLine = CheckH();
        }
        else
        {
            newIrqLine = CheckV();
        }

        _lastIrqHTime = ppuHTime;
        if (!_irqLine && newIrqLine)
        {
            _inIrq = true;
            _irqRaisedAtCycle = Cycles;
            if (_traceSgngIrqWindow && _traceSgngIrqWindowCount < _traceSgngIrqWindowLimit && CPU is KSNES.CPU.CPU cpu)
            {
                int pc = cpu.ProgramCounter24;
                if (pc >= 0x028270 && pc <= 0x0282C0)
                {
                    Console.WriteLine(
                        $"[SGNG-IRQ-WINDOW] pc=0x{pc:X6} xy=({XPos},{YPos}) ppuHt={ppuHTime} ppuVt={ppuVTime} hTimer={_hTimer} vTimer={_vTimer} hIrq={(_hIrqEnabled ? 1 : 0)} vIrq={(_vIrqEnabled ? 1 : 0)} regs=[{cpu.GetTraceState()}]");
                    _traceSgngIrqWindowCount++;
                }
            }
        }
        _irqLine = newIrqLine;
    }

    private bool GetCurrentHblankFlag()
    {
        return XPos < 4 || XPos >= 1096;
    }

    private bool GetCurrentVblankFlag()
    {
        int vBlankStart = IsPal ? 240 : (PPU.FrameOverscan ? 240 : 225);
        int maxV = IsPal ? 312 : 262;
        return YPos >= vBlankStart && YPos < maxV;
    }

    private void CpuCycle()
    {
        if (_cpuCyclesLeft == 0)
        {
            CPU.CyclesLeft = 0;
            _cpuMemOps = 0;
            CPU.Cycle();
            _cpuCyclesLeft += (CPU.CyclesLeft + 1 - _cpuMemOps) * 6;
        }
        _cpuCyclesLeft -= 2;
    }

    private void CatchUpApu() 
    {
        ulong mainMasterClockFrequency = IsPal ? PalMasterClockFrequency : NtscMasterClockFrequency;
        ulong threshold = 24UL * mainMasterClockFrequency;
        while (_apuMasterCyclesProduct >= threshold)
        {
            APU.Cycle();
            _apuMasterCyclesProduct -= threshold;
        }
    }

    private void AdvanceApuForCpuAccess(int mainMasterCycles)
    {
        if (mainMasterCycles <= 0)
            return;

        _apuBorrowedMainCycles += mainMasterCycles;
        _apuMasterCyclesProduct += ApuMasterClockFrequency * (ulong)mainMasterCycles;
        CatchUpApu();
    }

    private static bool IsCpuApuPortAccess(int adr)
    {
        adr &= 0xffffff;
        int bank = adr >> 16;
        int address = adr & 0xffff;
        return address >= 0x2140
            && address < 0x2144
            && (bank < 0x40 || (bank >= 0x80 && bank < 0xC0));
    }

    private void RunFrame(bool noPpu)
    {
        do
        {
            Cycle(noPpu);
        } while (!(XPos == 0 && YPos == 0));
    }

    private void DoAutoJoyRead()
    {
        _joypad1AutoRead = 0;
        _joypad2AutoRead = 0;
        _joypad1Val = _joypad1State;
        _joypad2Val = _joypad2State;
        for (var i = 0; i < 16; i++)
        {
            int bit = _joypad1Val & 0x1;
            _joypad1Val >>= 1;
            _joypad1Val |= 0x8000;
            _joypad1AutoRead |= bit << (15 - i);
            bit = _joypad2Val & 0x1;
            _joypad2Val >>= 1;
            _joypad2Val |= 0x8000;
            _joypad2AutoRead |= bit << (15 - i);
        }
    }

    private void HandleDma() 
    {
        if (_pendingDmaWriteValid)
        {
            ApplyPendingDmaWrite();
            return;
        }

        if (_gpdmaState == GpDmaState.Pending)
        {
            bool anyActive = false;
            for (int i = 0; i < 8; i++)
            {
                if (_dmaActive[i])
                {
                    anyActive = true;
                    break;
                }
            }

            if (!anyActive)
            {
                _gpdmaState = GpDmaState.Idle;
                return;
            }

            _dmaTimer = 8 + GetDmaStartAlignmentDelay();
            _gpdmaStartCycles = Cycles + (ulong)_dmaTimer;
            _gpdmaState = GpDmaState.Transfer;
            _gpdmaChannel = 0;
            _gpdmaBytesCopied = 0;
            return;
        }

        while (_gpdmaChannel < 8 && !_dmaActive[_gpdmaChannel])
        {
            _gpdmaChannel++;
            _gpdmaBytesCopied = 0;
        }

        if (_gpdmaChannel >= 8)
        {
            if (_dmaNotifyCount > 0)
            {
                for (int i = 0; i < 8; i++)
                {
                    if (!_dmaNotifyActive[i])
                        continue;

                    _dmaNotifyActive[i] = false;
                    _dmaNotifyCount--;
                    RomImpl.NotifyDmaEnd((byte)i);
                }
            }
            _gpdmaState = GpDmaState.Idle;
            _gpdmaChannel = 0;
            _gpdmaBytesCopied = 0;
            _dmaTimer = GetDmaEndCpuAlignmentDelay();
            return;
        }

        int channel = _gpdmaChannel;
        if (!_dmaFromB[channel] && !_dmaNotifyActive[channel])
        {
            uint sourceAddress = (uint)((_dmaAadrBank[channel] << 16) | _dmaAadr[channel]);
            RomImpl.NotifyDmaStart((byte)channel, sourceAddress);
            _dmaNotifyActive[channel] = true;
            _dmaNotifyCount++;
        }

        int tableOff = _dmaMode[channel] * 4 + (_gpdmaBytesCopied & 0x3);
        if (_traceDma)
        {
            int pc = -1;
            if (CPU is KSNES.CPU.CPU cpu)
                pc = cpu.ProgramCounter24;
            Console.WriteLine(
                $"[GPDMA-STATE] ch={channel} bytes={_gpdmaBytesCopied} size=0x{_dmaSize[channel]:X4} " +
                $"mode={_dmaMode[channel]} bbus=0x{_dmaBadr[channel]:X2} offs={_dmaOffs[tableOff]} " +
                $"fromB={(_dmaFromB[channel] ? 1 : 0)} fixed={(_dmaFixed[channel] ? 1 : 0)} dec={(_dmaDec[channel] ? 1 : 0)} " +
                $"a=0x{_dmaAadrBank[channel]:X2}:0x{_dmaAadr[channel]:X4} xy=({XPos},{YPos}) pc=0x{pc:X6}");
        }
        if (_dmaFromB[channel])
        {
            QueueDmaWriteBusA((_dmaAadrBank[channel] << 16) | _dmaAadr[channel],
                ReadBBus((_dmaBadr[channel] + _dmaOffs[tableOff]) & 0xff));
        }
        else
        {
            QueueDmaWriteBusB((_dmaBadr[channel] + _dmaOffs[tableOff]) & 0xff,
                DmaReadBusA((_dmaAadrBank[channel] << 16) | _dmaAadr[channel]));
        }

        _dmaTimer = 4;
        if (_gpdmaBytesCopied == 0)
            _dmaTimer += 4;

        if (!_dmaFixed[channel])
        {
            if (_dmaDec[channel])
            {
                _dmaAadr[channel]--;
            }
            else
            {
                _dmaAadr[channel]++;
            }
        }
        _dmaSize[channel]--;
        if (_dmaSize[channel] == 0)
        {
            _dmaActive[channel] = false;
            _gpdmaChannel++;
            _gpdmaBytesCopied = 0;
        }
        else
        {
            _gpdmaBytesCopied++;
        }

        _dmaTimer = 4;
        if (_gpdmaBytesCopied == 0)
            _dmaTimer += 4;
    }

    private void InitHdma() 
    {
        _hdmaTimer = 18;
        for (var i = 0; i < 8; i++)
        {
            int dmaBank = _dmaAadrBank[i] << 16;
            if (_hdmaActive[i])
            {
                _dmaActive[i] = false;
                _hdmaTableAdr[i] = _dmaAadr[i];
                _hdmaRepCount[i] = (byte) Read(dmaBank | _hdmaTableAdr[i]++, true);
                _hdmaTimer += 8;
                if (_hdmaInd[i])
                {
                    _dmaSize[i] = (ushort) Read(dmaBank | _hdmaTableAdr[i]++, true);
                    _dmaSize[i] |= (ushort) (Read(dmaBank | _hdmaTableAdr[i]++, true) << 8);
                    _hdmaTimer += 16;
                }
                _hdmaTerminated[i] = _hdmaRepCount[i] == 0;
                _hdmaDoTransfer[i] = !_hdmaTerminated[i];
            }
            else
            {
                _hdmaDoTransfer[i] = false;
                _hdmaTerminated[i] = false;
            }
        }
    }

    private void HandleHdma()
    {
        _hdmaTimer = 18;
        for (var i = 0; i < 8; i++)
        {
            if (_hdmaActive[i] && !_hdmaTerminated[i])
            {
                _dmaActive[i] = false;
                _hdmaTimer += 8;
                if (_traceDma)
                {
                    int pc = -1;
                    if (CPU is KSNES.CPU.CPU cpu)
                        pc = cpu.ProgramCounter24;
                    int sourceBank = _hdmaInd[i] ? _hdmaIndBank[i] : _dmaAadrBank[i];
                    int sourceAddr = _hdmaInd[i] ? _dmaSize[i] : _hdmaTableAdr[i];
                    Console.WriteLine(
                        $"[HDMA-STATE] ch={i} do={(_hdmaDoTransfer[i] ? 1 : 0)} rep=0x{_hdmaRepCount[i]:X2} " +
                        $"mode={_dmaMode[i]} bbus=0x{_dmaBadr[i]:X2} ind={(_hdmaInd[i] ? 1 : 0)} fromB={(_dmaFromB[i] ? 1 : 0)} " +
                        $"tabBank=0x{_dmaAadrBank[i]:X2} tabAdr=0x{_hdmaTableAdr[i]:X4} srcBank=0x{sourceBank:X2} srcAdr=0x{sourceAddr:X4} " +
                        $"xy=({XPos},{YPos}) pc=0x{pc:X6}");
                }
                if (_hdmaDoTransfer[i])
                {
                    for (var j = 0; j < _dmaOffLengths[_dmaMode[i]]; j++)
                    {
                        int tableOff = _dmaMode[i] * 4 + j;
                        _hdmaTimer += 8;
                        if (_hdmaInd[i])
                        {
                            if (_dmaFromB[i])
                            {
                                DmaWriteBusA((_hdmaIndBank[i] << 16) | _dmaSize[i],
                                    ReadBBus((_dmaBadr[i] + _dmaOffs[tableOff]) & 0xff));
                            }
                            else
                            {
                                WriteBBus((_dmaBadr[i] + _dmaOffs[tableOff]) & 0xff,
                                    DmaReadBusA((_hdmaIndBank[i] << 16) | _dmaSize[i]), true);
                            }
                            _dmaSize[i]++;
                        }
                        else
                        {
                            if (_dmaFromB[i])
                            {
                                DmaWriteBusA((_dmaAadrBank[i] << 16) | _hdmaTableAdr[i],
                                    ReadBBus((_dmaBadr[i] + _dmaOffs[tableOff]) & 0xff));
                            }
                            else
                            {
                                WriteBBus((_dmaBadr[i] + _dmaOffs[tableOff]) & 0xff,
                                    DmaReadBusA((_dmaAadrBank[i] << 16) | _hdmaTableAdr[i]), true);
                            }
                            _hdmaTableAdr[i]++;
                        }
                    }
                }
                _hdmaRepCount[i]--;
                _hdmaDoTransfer[i] = (_hdmaRepCount[i] & 0x80) > 0;
                int dmaBank = _dmaAadrBank[i] << 16;
                if ((_hdmaRepCount[i] & 0x7f) == 0)
                {
                    _hdmaRepCount[i] = (byte) Read(dmaBank | _hdmaTableAdr[i]++, true);
                    if (_hdmaInd[i])
                    {
                        _dmaSize[i] = (ushort) Read(dmaBank | _hdmaTableAdr[i]++, true);
                        _dmaSize[i] |= (ushort) (Read(dmaBank | _hdmaTableAdr[i]++, true) << 8);
                        _hdmaTimer += 16;
                    }
                    if (_hdmaRepCount[i] == 0)
                    {
                        _hdmaTerminated[i] = true;
                    }
                    _hdmaDoTransfer[i] = true;
                }
            }
        }
    }

    private int ReadReg(int adr) 
    {
        switch (adr)
        {
            case 0x4210:
                int val = 0x1;
                val |= _vblankNmiFlag ? 0x80 : 0;
                val |= OpenBus & 0x70;
                if (_traceDma)
                {
                    int pc = -1;
                    if (CPU is KSNES.CPU.CPU cpu)
                        pc = cpu.ProgramCounter24;
                    Console.WriteLine($"[INT-STAT] RDNMI read pc=0x{pc:X6} val=0x{val:X2}");
                }
                _inNmi = false;
                _vblankNmiFlag = false;
                return val;
            case 0x4211:
                int val2 = _inIrq ? 0x80 : 0;
                val2 |= OpenBus & 0x7f;
                if (Cycles - _irqRaisedAtCycle >= 4)
                {
                    _inIrq = false;
                    CPU.IrqWanted = false;
                }
                return val2;
            case 0x4212:
                int val3 = _autoJoyBusy ? 0x1 : 0;
                val3 |= GetCurrentHblankFlag() ? 0x40 : 0;
                val3 |= GetCurrentVblankFlag() ? 0x80 : 0;
                val3 |= OpenBus & 0x3e;
                if (_traceStarOcean4212Loop && CPU is KSNES.CPU.CPU so4212Cpu)
                {
                    int pc = so4212Cpu.ProgramCounter24;
                    if (pc >= 0xCC0538 && pc <= 0xCC0540)
                    {
                        Console.WriteLine($"[SO-4212] pc=0x{pc:X6} val=0x{val3:X2} v={GetCurrentVblankFlag()} h={GetCurrentHblankFlag()} autojoy={_autoJoyBusy} xy=({XPos},{YPos})");
                    }
                }
                if (_trace4212)
                {
                    Console.WriteLine($"[4212] R val=0x{val3:X2} vblank={GetCurrentVblankFlag()} hblank={GetCurrentHblankFlag()} autojoy={_autoJoyBusy} Y={YPos} X={XPos}");
                }
                return val3;
            case 0x4213:
                return PPULatch ? 0x80 : 0;
            case 0x4214:
                return _divResult & 0xff;
            case 0x4215:
                return (_divResult & 0xff00) >> 8;
            case 0x4216:
                return _mulResult & 0xff;
            case 0x4217:
                return (_mulResult & 0xff00) >> 8;
            case 0x4218:
                return TraceJoypadRead(adr, _joypad1AutoRead & 0xff);
            case 0x4219:
                return TraceJoypadRead(adr, (_joypad1AutoRead & 0xff00) >> 8);
            case 0x421a:
                return TraceJoypadRead(adr, _joypad2AutoRead & 0xff);
            case 0x421b:
                return TraceJoypadRead(adr, (_joypad2AutoRead & 0xff00) >> 8);
            case 0x421c:
            case 0x421d:
            case 0x421e:
            case 0x421f:
                return 0;
        }
        if (adr >= 0x4300 && adr < 0x4380)
        {
            int channel = (adr & 0xf0) >> 4;
            switch (adr & 0xff0f)
            {
                case 0x4300:
                    int val = _dmaMode[channel];
                    val |= _dmaFixed[channel] ? 0x8 : 0;
                    val |= _dmaDec[channel] ? 0x10 : 0;
                    val |= _dmaUnusedBit[channel] ? 0x20 : 0;
                    val |= _hdmaInd[channel] ? 0x40 : 0;
                    val |= _dmaFromB[channel] ? 0x80 : 0;
                    return val;
                case 0x4301:
                    return _dmaBadr[channel];
                case 0x4302:
                    return _dmaAadr[channel] & 0xff;
                case 0x4303:
                    return (_dmaAadr[channel] & 0xff00) >> 8;
                case 0x4304:
                    return _dmaAadrBank[channel];
                case 0x4305:
                    return _dmaSize[channel] & 0xff;
                case 0x4306:
                    return (_dmaSize[channel] & 0xff00) >> 8;
                case 0x4307:
                    return _hdmaIndBank[channel];
                case 0x4308:
                    return _hdmaTableAdr[channel] & 0xff;
                case 0x4309:
                    return (_hdmaTableAdr[channel] & 0xff00) >> 8;
                case 0x430a:
                    return _hdmaRepCount[channel];
                case 0x430b:
                case 0x430f:
                    return _dmaUnusedByte[channel];
            }
        }

        return OpenBus;
    }

    private static bool IsDmaForbiddenBusAAccess(int address)
    {
        int bank = (address >> 16) & 0xff;
        int adr = address & 0xffff;
        return (bank < 0x40 || (bank >= 0x80 && bank < 0xc0)) &&
               (adr is >= 0x2100 and <= 0x21ff || adr is >= 0x4300 and <= 0x43ff);
    }

    private int DmaReadBusA(int address)
    {
        if (IsDmaForbiddenBusAAccess(address))
            return OpenBus;

        if (RomImpl.TryReadForDma(address, out int dmaValue))
            return dmaValue;

        return Read(address, true);
    }

    private void QueueDmaWriteBusA(int address, int value)
    {
        if (IsDmaForbiddenBusAAccess(address))
            return;

        Write(address, value, true);
    }

    private void QueueDmaWriteBusB(int address, int value)
    {
        WriteBBus(address & 0xff, value, true);
    }

    private void ApplyPendingDmaWrite()
    {
        if (!_pendingDmaWriteValid)
            return;

        if (_pendingDmaWriteBusB)
        {
            WriteBBus(_pendingDmaWriteAddress, _pendingDmaWriteValue, true);
        }
        else
        {
            Write(_pendingDmaWriteAddress, _pendingDmaWriteValue, true);
        }

        _pendingDmaWriteValid = false;
        _pendingDmaWriteBusB = false;
        _pendingDmaWriteAddress = 0;
        _pendingDmaWriteValue = 0;
    }

    private void DmaWriteBusA(int address, int value)
    {
        if (IsDmaForbiddenBusAAccess(address))
            return;

        Write(address, value, true);
    }

    private void WriteReg(int adr, int value) 
    {
        switch (adr)
        {
            case 0x4200:
                if (_logVerbose)
                {
                    int pcLog = CPU is KSNES.CPU.CPU cpuLog ? cpuLog.ProgramCounter24 : -1;
                    Console.WriteLine($"[SNES-REG] W 0x4200 val=0x{value:X2} PC=0x{pcLog:X6}");
                }
                _autoJoyRead = (value & 0x1) > 0;
                _hIrqEnabled = (value & 0x10) > 0;
                _vIrqEnabled = (value & 0x20) > 0;
                bool newNmiEnabled = (value & 0x80) > 0;
                if (!_nmiEnabled && newNmiEnabled && _vblankNmiFlag)
                {
                    CPU.NmiWanted = true;
                }
                _nmiEnabled = newNmiEnabled;
                if (!_hIrqEnabled && !_vIrqEnabled)
                {
                    _inIrq = false;
                    _irqLine = false;
                }
                if (_traceDma)
                {
                    int pc = -1;
                    if (CPU is KSNES.CPU.CPU cpu)
                        pc = cpu.ProgramCounter24;
                    Console.WriteLine($"[INT-CTL] NMI={(value & 0x80) > 0} VIRQ={(value & 0x20) > 0} HIRQ={(value & 0x10) > 0} AUTOJOY={(value & 0x1) > 0} pc=0x{pc:X6} val=0x{value:X2}");
                }
                return;
            case 0x4201:
                if (PPULatch && (value & 0x80) == 0)
                {
                    PPU.LatchedHpos = XPos >> 2;
                    PPU.LatchedVpos = YPos;
                    PPU.CountersLatched = true;
                }
                PPULatch = (value & 0x80) > 0;
                return;
            case 0x4202:
                _multiplyA = value;
                return;
            case 0x4203:
                _mulResult = _multiplyA * value;
                return;
            case 0x4204:
                _divA = (_divA & 0xff00) | value;
                return;
            case 0x4205:
                _divA = (_divA & 0xff) | (value << 8);
                return;
            case 0x4206:
                _divResult = 0xffff;
                _mulResult = _divA;
                if (value != 0)
                {
                    _divResult = (_divA / value) & 0xffff;
                    _mulResult = _divA % value;
                }
                return;
            case 0x4207:
                _hTimer = (_hTimer & 0x100) | value;
                return;
            case 0x4208:
                _hTimer = (_hTimer & 0xff) | ((value & 0x1) << 8);
                return;
            case 0x4209:
                _vTimer = (_vTimer & 0x100) | value;
                return;
            case 0x420a:
                _vTimer = (_vTimer & 0xff) | ((value & 0x1) << 8);
                return;
            case 0x420b:
                _dmaActive[0] = (value & 0x1) > 0;
                _dmaActive[1] = (value & 0x2) > 0;
                _dmaActive[2] = (value & 0x4) > 0;
                _dmaActive[3] = (value & 0x8) > 0;
                _dmaActive[4] = (value & 0x10) > 0;
                _dmaActive[5] = (value & 0x20) > 0;
                _dmaActive[6] = (value & 0x40) > 0;
                _dmaActive[7] = (value & 0x80) > 0;
                if (value > 0)
                {
                    _gpdmaState = GpDmaState.Pending;
                    _gpdmaChannel = 0;
                    _gpdmaBytesCopied = 0;
                    _gpdmaStartCycles = 0;
                    _dmaTimer = 0;
                }
                else
                {
                    _gpdmaState = GpDmaState.Idle;
                    _gpdmaChannel = 0;
                    _gpdmaBytesCopied = 0;
                    _gpdmaStartCycles = 0;
                }
                if (_traceDma)
                {
                    int pc = -1;
                    if (CPU is KSNES.CPU.CPU cpu)
                        pc = cpu.ProgramCounter24;
                    Console.WriteLine($"[DMA-CTL] MDMAEN=0x{value:X2} pc=0x{pc:X6}");
                    for (int ch = 0; ch < 8; ch++)
                    {
                        if (!_dmaActive[ch])
                            continue;
                        int sourceAddress = (_dmaAadrBank[ch] << 16) | _dmaAadr[ch];
                        Span<byte> preview = stackalloc byte[8];
                        for (int i = 0; i < preview.Length; i++)
                        {
                            preview[i] = (byte)DmaReadBusA((sourceAddress + i) & 0xffffff);
                        }
                        string previewHex = Convert.ToHexString(preview.ToArray());
                        Console.WriteLine($"[DMA-STATE] ch={ch} mode={_dmaMode[ch]} bbus=0x{_dmaBadr[ch]:X2} aaddr=0x{_dmaAadr[ch]:X4} abank=0x{_dmaAadrBank[ch]:X2} size=0x{_dmaSize[ch]:X4} fromB={_dmaFromB[ch]} fixed={_dmaFixed[ch]} dec={_dmaDec[ch]} src8={previewHex}");
                    }
                }
                return;
            case 0x420c:
                _hdmaActive[0] = (value & 0x1) > 0;
                _hdmaActive[1] = (value & 0x2) > 0;
                _hdmaActive[2] = (value & 0x4) > 0;
                _hdmaActive[3] = (value & 0x8) > 0;
                _hdmaActive[4] = (value & 0x10) > 0;
                _hdmaActive[5] = (value & 0x20) > 0;
                _hdmaActive[6] = (value & 0x40) > 0;
                _hdmaActive[7] = (value & 0x80) > 0;
                return;
            case 0x420d:
                _fastMem = (value & 0x1) > 0;
                RebuildAccessTimeTable();
                return;
        }

        if (adr >= 0x4300 && adr < 0x4380)
        {
            int channel = (adr & 0xf0) >> 4;
            if (_traceDma)
            {
                int pc = -1;
                if (CPU is KSNES.CPU.CPU cpu)
                    pc = cpu.ProgramCounter24;
                int reg = adr & 0x0f;
                Console.WriteLine($"[DMA-REG] ch={channel} reg=0x{reg:X2} adr=0x{adr:X4} val=0x{value:X2} pc=0x{pc:X6}");
            }
            switch (adr & 0xff0f)
            {
                case 0x4300:
                    _dmaMode[channel] = value & 0x7;
                    _dmaFixed[channel] = (value & 0x08) > 0;
                    _dmaDec[channel] = (value & 0x10) > 0;
                    _dmaUnusedBit[channel] = (value & 0x20) > 0;
                    _hdmaInd[channel] = (value & 0x40) > 0;
                    _dmaFromB[channel] = (value & 0x80) > 0;
                    return;
                case 0x4301:
                    _dmaBadr[channel] = (byte) value;
                    return;
                case 0x4302:
                    _dmaAadr[channel] = (ushort) ((_dmaAadr[channel] & 0xff00) | value);
                    return;
                case 0x4303:
                    _dmaAadr[channel] = (ushort) ((_dmaAadr[channel] & 0xff) | (value << 8));
                    return;
                case 0x4304:
                    _dmaAadrBank[channel] = (byte) value;
                    return;
                case 0x4305:
                    _dmaSize[channel] = (ushort) ((_dmaSize[channel] & 0xff00) | value);
                    return;
                case 0x4306:
                    _dmaSize[channel] = (ushort) ((_dmaSize[channel] & 0xff) | (value << 8));
                    return;
                case 0x4307:
                    _hdmaIndBank[channel] = (byte) value;
                    return;
                case 0x4308:
                    _hdmaTableAdr[channel] = (ushort) ((_hdmaTableAdr[channel] & 0xff00) | value);
                    return;
                case 0x4309:
                    _hdmaTableAdr[channel] = (ushort) ((_hdmaTableAdr[channel] & 0xff) | (value << 8));
                    return;
                case 0x430a:
                    _hdmaRepCount[channel] = (byte) value;
                    return;
                case 0x430b:
                case 0x430f:
                    _dmaUnusedByte[channel] = (byte) value;
                    return;
            }
        }
    }

    private int ReadBBus(int adr) 
    {
        if (adr > 0x33 && adr < 0x40)
        {
            return PPU.Read(adr);
        }
        if (adr >= 0x40 && adr < 0x80)
        {
            int port = adr & 0x3;
            int pc = -1;
            if (CPU is KSNES.CPU.CPU cpu)
                pc = cpu.ProgramCounter24;
            int val = APU.SpcWritePorts[port];
            TraceApuPort($"[APU-PORT-CPU-RD] port={port} val=0x{val:X2} pc=0x{pc:X6}");
            if (_traceStarOceanApuLoop && adr == 0x40 && pc >= 0xC00331 && pc <= 0xC0033B)
            {
                Console.WriteLine($"[SO-2140] pc=0x{pc:X6} port0=0x{val:X2} wr4A=0x{_ram[0x004A]:X2} spcpc=0x{APU.Spc.ProgramCounter:X4} xy=({XPos},{YPos})");
            }
            return val;
        }
        if (adr == 0x80)
        {
            int addr = _ramAdr;
            int val = _ram[addr];
            _ramAdr = (addr + 1) & 0x1ffff;
            if (_traceWramWrites && ShouldTraceWramPortAccess(addr, addr))
            {
                int pc = -1;
                if (CPU is KSNES.CPU.CPU cpu)
                    pc = cpu.ProgramCounter24;
                Console.WriteLine($"[WRAM-PORT-RD] addr=0x{addr:X5} val=0x{val:X2} pc=0x{pc:X6}");
            }
            return val;
        }
        return OpenBus;
    }

    private void WriteBBus(int adr, int value, bool dma = false)
    {
        if (adr < 0x34)
        {
            if (_traceInidisp && adr == 0x00)
            {
                int pc = -1;
                string regs = string.Empty;
                if (CPU is KSNES.CPU.CPU cpu)
                {
                    pc = cpu.ProgramCounter24;
                    regs = cpu.GetTraceState();
                }
                Console.WriteLine($"[INIDISP] write $2100=0x{value:X2} pc=0x{pc:X6} dma={(dma ? 1 : 0)} regs=[{regs}]");
            }
            TracePpuBusWrite(adr, value, dma);
            PPU.Write(adr, value, dma);
            return;
        }
        if (adr >= 0x40 && adr < 0x80)
        {
            int port = adr & 0x3;
            bool accepted = APU.TryWriteMainCpuPort(port, (byte)value);
            int pc = -1;
            if (CPU is KSNES.CPU.CPU cpu)
                pc = cpu.ProgramCounter24;
            TraceApuPort($"[APU-PORT-CPU-WR] port={port} val=0x{value:X2} accepted={(accepted ? 1 : 0)} pc=0x{pc:X6} dma={(dma ? 1 : 0)}");
            return;
        }
        switch (adr)
        {
            case 0x80:
                int portWritePc = -1;
                if (CPU is KSNES.CPU.CPU portWriteCpu)
                    portWritePc = portWriteCpu.ProgramCounter24;
                bool tracePortWrite = _traceWramWrites &&
                    (ShouldTraceWramPortAccess(_ramAdr, _ramAdr) ||
                     (portWritePc >= 0x02E1E0 && portWritePc <= 0x02E1FF));
                if (tracePortWrite)
                {
                    Console.WriteLine($"[WRAM-PORT-WR] addr=0x{_ramAdr:X5} val=0x{value:X2} pc=0x{portWritePc:X6}");
                }
                _ram[_ramAdr++] = (byte) value;
                _ramAdr &= 0x1ffff;
                return;
            case 0x81:
                _ramAdr = (_ramAdr & 0x1ff00) | value;
                if (_traceWramWrites)
                {
                    int pc = -1;
                    if (CPU is KSNES.CPU.CPU cpu)
                        pc = cpu.ProgramCounter24;
                    Console.WriteLine($"[WRAM-PORT-ADR] reg=0x2181 val=0x{value:X2} ramAdr=0x{_ramAdr:X5} pc=0x{pc:X6}");
                }
                return;
            case 0x82:
                _ramAdr = (_ramAdr & 0x100ff) | (value << 8);
                if (_traceWramWrites)
                {
                    int pc = -1;
                    if (CPU is KSNES.CPU.CPU cpu)
                        pc = cpu.ProgramCounter24;
                    Console.WriteLine($"[WRAM-PORT-ADR] reg=0x2182 val=0x{value:X2} ramAdr=0x{_ramAdr:X5} pc=0x{pc:X6}");
                }
                return;
            case 0x83:
                _ramAdr = (_ramAdr & 0x0ffff) | ((value & 1) << 16);
                if (_traceWramWrites)
                {
                    int pc = -1;
                    if (CPU is KSNES.CPU.CPU cpu)
                        pc = cpu.ProgramCounter24;
                    Console.WriteLine($"[WRAM-PORT-ADR] reg=0x2183 val=0x{value:X2} ramAdr=0x{_ramAdr:X5} pc=0x{pc:X6}");
                }
                return;
        }
    }

    private void TracePpuBusWrite(int adr, int value, bool dma)
    {
        if (!_tracePpuBusWrites)
        {
            return;
        }
        if (_tracePpuBusLimit > 0 && _tracePpuBusCount >= _tracePpuBusLimit)
        {
            return;
        }
        switch (adr)
        {
            case 0x01: // OBJSEL
            case 0x02: // OAMADDL
            case 0x03: // OAMADDH
            case 0x04: // OAMDATA
            case 0x07: // BG1SC
            case 0x08: // BG2SC
            case 0x09: // BG3SC
            case 0x0A: // BG4SC
            case 0x0B: // BG12NBA
            case 0x0C: // BG34NBA
            case 0x0D: // BG1HOFS / M7HOFS
            case 0x0E: // BG1VOFS / M7VOFS
            case 0x0F: // BG2HOFS
            case 0x10: // BG2VOFS
            case 0x11: // BG3HOFS
            case 0x12: // BG3VOFS
            case 0x13: // BG4HOFS
            case 0x14: // BG4VOFS
            case 0x15: // VMAIN
            case 0x16: // VMADDL
            case 0x17: // VMADDH
            case 0x18: // VMDATAL
            case 0x19: // VMDATAH
            case 0x21: // CGADD
            case 0x22: // CGDATA
            case 0x00: // INIDISP
            case 0x05: // BGMODE
            case 0x2C: // TM
            case 0x2D: // TS
            case 0x2E: // TMW
            case 0x2F: // TSW
            case 0x30: // CGWSEL
            case 0x31: // CGADSUB
            case 0x32: // COLDATA
            case 0x33: // SETINI
                string src = dma ? "DMA" : "CPU";
                int pc = -1;
                string pcBytes = "";
                string regs = "";
                if (CPU is KSNES.CPU.CPU cpu)
                {
                    pc = cpu.ProgramCounter24;
                    int b0 = Rread(pc);
                    int b1 = Rread((pc + 1) & 0xffffff);
                    int b2 = Rread((pc + 2) & 0xffffff);
                    pcBytes = $" op=[{b0:X2} {b1:X2} {b2:X2}]";
                    if (adr == 0x00)
                    {
                        regs = $" regs={cpu.GetTraceState()}";
                    }
                }
                Console.WriteLine(
                    $"[PPU-BUS] {src} write $21{adr:X2}=0x{value:X2} pc=0x{pc:X6}{pcBytes}{regs} xy=({XPos},{YPos}) vblank={_inVblank} hblank={_inHblank}");
                _tracePpuBusCount++;
                return;
        }
    }

    private void TraceApuPort(string message)
    {
        if (!_traceApuPorts || _traceApuPortsCount >= _traceApuPortsLimit)
            return;

        _traceApuPortsCount++;
        Console.WriteLine(message);
    }

    private int ReadJoypadPortFast(int adr)
    {
        if (adr == 0x4016)
        {
            int val = _joypad1Val & 0x1;
            _joypad1Val >>= 1;
            _joypad1Val |= 0x8000;
            return val | (OpenBus & 0xfc);
        }

        if (adr == 0x4017)
        {
            int val = _joypad2Val & 0x1;
            _joypad2Val >>= 1;
            _joypad2Val |= 0x8000;
            return 0x1c | val | (OpenBus & 0xe0);
        }

        return -1;
    }

    private void WriteJoypadStrobeFast(int value)
    {
        bool newStrobe = (value & 0x1) > 0;
        if (!_joypadStrobe && newStrobe)
        {
            _joypad1Val = _joypad1State;
            _joypad2Val = _joypad2State;
        }
        _joypadStrobe = newStrobe;
    }

    private void WriteBBusFast(int adr, int value, bool dma)
    {
        if (adr < 0x34)
        {
            PPU.Write(adr, value, dma);
            return;
        }

        if (adr >= 0x40 && adr < 0x80)
        {
            APU.TryWriteMainCpuPort(adr & 0x3, (byte)value);
            return;
        }

        switch (adr)
        {
            case 0x80:
                _ram[_ramAdr] = (byte)value;
                _ramAdr = (_ramAdr + 1) & 0x1ffff;
                return;
            case 0x81:
                _ramAdr = (_ramAdr & 0x1ff00) | value;
                return;
            case 0x82:
                _ramAdr = (_ramAdr & 0x100ff) | (value << 8);
                return;
            case 0x83:
                _ramAdr = (_ramAdr & 0x0ffff) | ((value & 1) << 16);
                return;
        }
    }

    private int RreadFast(int fullAdr, BusPageKind pageKind)
    {
        int bank = fullAdr >> 16;
        int adr = fullAdr & 0xffff;
        switch (pageKind)
        {
            case BusPageKind.WramBank:
                return _ram[((bank & 0x1) << 16) | adr];
            case BusPageKind.LowWram:
                return _ram[adr & 0x1fff];
            case BusPageKind.BBus:
                return ReadBBus(adr & 0xff);
            case BusPageKind.JoypadPage:
                int joypadValue = ReadJoypadPortFast(adr);
                if (joypadValue >= 0)
                    return joypadValue;
                break;
            case BusPageKind.CpuRegs:
                if (adr >= 0x4200 && adr < 0x4380)
                    return ReadReg(adr);
                break;
        }

        return RomImpl.ReadFast(fullAdr);
    }

    private void WwriteFast(int fullAdr, int value, bool dma, BusPageKind pageKind)
    {
        int bank = fullAdr >> 16;
        int adr = fullAdr & 0xffff;
        switch (pageKind)
        {
            case BusPageKind.WramBank:
                _ram[((bank & 0x1) << 16) | adr] = (byte)value;
                break;
            case BusPageKind.LowWram:
                _ram[adr & 0x1fff] = (byte)value;
                break;
            case BusPageKind.BBus:
                WriteBBusFast(adr & 0xff, value, dma);
                break;
            case BusPageKind.JoypadPage:
                if (adr == 0x4016)
                    WriteJoypadStrobeFast(value);
                break;
            case BusPageKind.CpuRegs:
                if (adr >= 0x4200 && adr < 0x4380)
                    WriteReg(adr, value);
                break;
        }

        RomImpl.WriteFast(fullAdr, (byte)value);
    }

    private void Wwrite(int fullAdr, int value, bool dma)
    {
        int bank = fullAdr >> 16;
        int adr = fullAdr & 0xffff;
        if (bank == 0x7e || bank == 0x7f)
        {
            _ram[((bank & 0x1) << 16) | adr] = (byte) value;
            if (_traceWramWrites && ShouldTraceWramWrite(((bank & 0x1) << 16) | adr, adr))
            {
                int pc = -1;
                if (CPU is KSNES.CPU.CPU cpu)
                    pc = cpu.ProgramCounter24;
                Console.WriteLine($"[WRAM-WR] bank=0x{bank:X2} adr=0x{adr:X4} val=0x{value:X2} pc=0x{pc:X6}");
            }
            if (dma && _traceWramWrites && ShouldTraceWramPortAccess(((bank & 0x1) << 16) | adr, adr))
            {
                int pc = -1;
                if (CPU is KSNES.CPU.CPU cpu)
                    pc = cpu.ProgramCounter24;
                Console.WriteLine($"[WRAM-DMA] bank=0x{bank:X2} adr=0x{adr:X4} val=0x{value:X2} pc=0x{pc:X6}");
            }
        }
        if (adr < 0x8000 && (bank < 0x40 || bank >= 0x80 && bank < 0xc0))
        {
            if (adr < 0x2000)
            {
                _ram[adr & 0x1fff] = (byte) value;
                if (_traceWramWrites && ShouldTraceWramWrite(adr, adr))
                {
                    int pc = -1;
                    if (CPU is KSNES.CPU.CPU cpu)
                        pc = cpu.ProgramCounter24;
                    Console.WriteLine($"[WRAM-WR] adr=0x{adr:X4} val=0x{value:X2} pc=0x{pc:X6}");
                }
                if (_traceWramWrites && ShouldTraceWramPortAccess(adr, adr))
                {
                    int pc = -1;
                    if (CPU is KSNES.CPU.CPU cpu)
                        pc = cpu.ProgramCounter24;
                    Console.WriteLine($"[WRAM-WR] adr=0x{adr:X4} val=0x{value:X2} pc=0x{pc:X6}");
                }
            }
            if (adr >= 0x2100 && adr < 0x2200)
            {
                WriteBBus(adr & 0xff, value, dma);
            }
            if (adr == 0x4016)
            {
                WriteJoypadStrobeFast(value);
            }
            if (adr >= 0x4200 && adr < 0x4380)
            {
                if (_traceDma && (adr == 0x420B || adr == 0x420C))
                {
                    int pc = -1;
                    if (CPU is KSNES.CPU.CPU cpu)
                        pc = cpu.ProgramCounter24;
                    string reg = adr == 0x420B ? "MDMAEN" : "HDMAEN";
                    Console.WriteLine($"[DMA-CTL] {reg}=0x{value:X2} pc=0x{pc:X6}");
                }
                WriteReg(adr, value);
            }
        }
        RomImpl.Write(bank, adr, (byte) value);
    }

    private int Rread(int adr) 
    {
        adr &= 0xffffff;
        int bank = adr >> 16;
        adr &= 0xffff;
        if (bank == 0x7e || bank == 0x7f)
        {
            int val = _ram[((bank & 0x1) << 16) | adr];
            if (_traceWramWrites && ShouldTraceWramRead(((bank & 0x1) << 16) | adr, adr))
            {
                int pc = -1;
                if (CPU is KSNES.CPU.CPU cpu)
                    pc = cpu.ProgramCounter24;
                Console.WriteLine($"[WRAM-RD] bank=0x{bank:X2} adr=0x{adr:X4} val=0x{val:X2} pc=0x{pc:X6}");
            }
            return val;
        }
        if (adr < 0x8000 && (bank < 0x40 || bank >= 0x80 && bank < 0xc0))
        {
            if (adr < 0x2000)
            {
                int val = _ram[adr & 0x1fff];
                if (_traceWramWrites && ShouldTraceWramRead(adr, adr))
                {
                    int pc = -1;
                    if (CPU is KSNES.CPU.CPU cpu)
                        pc = cpu.ProgramCounter24;
                    Console.WriteLine($"[WRAM-RD] adr=0x{adr:X4} val=0x{val:X2} pc=0x{pc:X6}");
                }
                return val;
            }
            if (adr >= 0x2100 && adr < 0x2200)
            {
                return ReadBBus(adr & 0xff);
            }
            if (adr == 0x4016)
            {
                int val = _joypad1Val & 0x1;
                _joypad1Val >>= 1;
                _joypad1Val |= 0x8000;
                return TraceJoypadRead(adr, val | (OpenBus & 0xfc));
            }
            if (adr == 0x4017)
            {
                int val = _joypad2Val & 0x1;
                _joypad2Val >>= 1;
                _joypad2Val |= 0x8000;
                return TraceJoypadRead(adr, 0x1c | val | (OpenBus & 0xe0));
            }
            if (adr >= 0x4200 && adr < 0x4380)
            {
                return ReadReg(adr);
            }
        }
        return RomImpl.Read(bank, adr);
    }

    public int Peek(int adr)
    {
        return Rread(adr);
    }

    public byte[] GetWramDebugCopy()
    {
        return (byte[])_ram.Clone();
    }

    private int GetAccessTime(int adr)
    {
        return _busPageData[(adr & 0xffffff) >> 8] & BusPageAccessMask;
    }

    private int GetDmaStartAlignmentDelay()
    {
        int remainder = (int)(Cycles & 0x7);
        return 8 - remainder;
    }

    private int GetDmaEndCpuAlignmentDelay()
    {
        int nextCpuCycleMclk = 6;
        if (CPU is KSNES.CPU.CPU cpu)
            nextCpuCycleMclk = GetAccessTime(cpu.ProgramCounter24);

        ulong dmaElapsed = Cycles - _gpdmaStartCycles;
        int remainder = (int)(dmaElapsed % (ulong)nextCpuCycleMclk);
        return nextCpuCycleMclk - remainder;
    }

    public void SetKeyDown(SNESButton button)
    {
        _joypad1State |= 1 << (int) button;
    }

    private static int ParseTraceLimit(string envName, int defaultValue)
    {
        string? raw = Environment.GetEnvironmentVariable(envName);
        if (!string.IsNullOrWhiteSpace(raw)
            && int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed)
            && parsed >= 0)
        {
            return parsed;
        }

        return defaultValue;
    }

    private int TraceJoypadRead(int adr, int value)
    {
        if (_traceJoypad && _traceJoypadCount < _traceJoypadLimit)
        {
            int pc = -1;
            if (CPU is KSNES.CPU.CPU cpu)
                pc = cpu.ProgramCounter24;
            Console.WriteLine(
                $"[JOY] R 0x{adr:X4}=0x{value:X2} pc=0x{pc:X6} strobe={(_joypadStrobe ? 1 : 0)} auto=0x{_joypad1AutoRead:X4} man=0x{_joypad1Val:X4} state=0x{_joypad1State:X3} autoBusy={(_autoJoyBusy ? 1 : 0)} xy=({XPos},{YPos})");
            _traceJoypadCount++;
        }

        return value;
    }

    public void SetKeyUp(SNESButton button)
    {
        _joypad1State &= ~(1 << (int) button) & 0xfff;
    }

    public void SetKeyDown2(SNESButton button)
    {
        _joypad2State |= 1 << (int) button;
    }

    public void SetKeyUp2(SNESButton button)
    {
        _joypad2State &= ~(1 << (int) button) & 0xfff;
    }

    private static Header ParseHeader(byte[] rom)
    {
        int baseOff = SelectHeaderBase(rom);
        string str = Encoding.ASCII.GetString(rom, baseOff, 21);
        int mapMode = rom[baseOff + 0x15];
        int mapModeLo = mapMode & 0x0F;
        var header = new Header
        {
            Name = str,
            Type = mapModeLo,
            Speed = mapMode >> 4,
            MapMode = mapMode,
            Chips = rom[baseOff + 0x16] & 0xf,
            ChipsetByte = rom[baseOff + 0x16],
            RomSize = 0x400 << rom[baseOff + 0x17],
            RamSize = 0x400 << rom[baseOff + 0x18],
            Region = rom[baseOff + 0x19],
            ExCoprocessor = ReadExCoprocessor(rom, baseOff),
            IsExHiRom = mapModeLo == 0x05,
            IsHiRom = mapModeLo == 0x01
        };
        if (header.RomSize < rom.Length)
        {
            double bankCount = Math.Pow(2, Math.Ceiling(Math.Log(rom.Length / 0x8000, 2)));
            header.RomSize = (int) bankCount * 0x8000;
        }
        return header;
    }

    private static int SelectHeaderBase(byte[] data)
    {
        if (data.Length < 0x10000)
            return 0x7FC0;
        int bestOffset = 0x7FC0;
        int bestScore = ScoreHeaderCandidate(data, bestOffset);

        int hiScore = ScoreHeaderCandidate(data, 0xFFC0);
        if (hiScore > bestScore)
        {
            bestOffset = 0xFFC0;
            bestScore = hiScore;
        }

        if (data.Length >= 0x410000)
        {
            int exHiScore = ScoreHeaderCandidate(data, 0x40FFC0);
            if (exHiScore > bestScore)
            {
                bestOffset = 0x40FFC0;
            }
        }

        return bestOffset;
    }

    private static int CountPrintable(byte[] data, int offset)
    {
        if (offset < 0 || offset + 21 > data.Length)
            return -1;
        int printable = 0;
        for (int i = 0; i < 21; i++)
        {
            byte b = data[offset + i];
            if (b >= 0x20 && b < 0x7f)
                printable++;
        }
        return printable;
    }

    private static int ScoreHeaderCandidate(byte[] data, int offset)
    {
        int printable = CountPrintable(data, offset);
        if (printable < 0)
            return -1;

        int score = printable;
        int mapModeLo = data[offset + 0x15] & 0x0F;
        if (mapModeLo is 0x00 or 0x01 or 0x05)
            score += 8;
        if (data[offset + 0x16] != 0xFF)
            score += 2;
        return score;
    }

    private static int ReadExCoprocessor(byte[] data, int baseOff)
    {
        if (baseOff <= 0)
            return 0;
        byte maker = data[baseOff + 0x1a];
        if (maker == 0x33 || data[baseOff + 0x14] == 0)
            return data[baseOff - 1];
        return 0;
    }

    private static HashSet<int> ParseTraceWramAddrs(string? raw)
    {
        var result = new HashSet<int>();
        if (string.IsNullOrWhiteSpace(raw))
            return result;

        foreach (string part in raw.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string token = part.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? part[2..] : part;
            if (int.TryParse(token, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int value))
                result.Add(value & 0x1FFFF);
        }

        return result;
    }

    private bool ShouldTraceWramWrite(int fullAddr, int adr)
    {
        if (HasExplicitWramTraceFilter)
            return _traceWramAddrs.Contains(fullAddr);

        return adr == 0x0028 || adr == 0x002A || adr == 0x002B || adr == 0x00AD || adr < 0x0400;
    }

    private bool ShouldTraceWramRead(int fullAddr, int adr)
    {
        if (HasExplicitWramTraceFilter)
            return _traceWramAddrs.Contains(fullAddr);

        return adr == 0x002E || adr == 0x002F || adr == 0x004C || adr == 0x004E || adr == 0x1F4E;
    }

    private bool ShouldTraceWramPortAccess(int fullAddr, int adr)
    {
        if (HasExplicitWramTraceFilter)
            return _traceWramAddrs.Contains(fullAddr);

        return adr < 0x0400 || (adr >= 0x1F00 && adr < 0x2000);
    }
}
