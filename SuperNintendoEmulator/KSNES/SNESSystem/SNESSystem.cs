using System;
using System.Collections.Generic;
using System.Globalization;

namespace KSNES.SNESSystem;

public class SNESSystem : ISNESSystem
{
    [field: NonSerialized] public ICPU CPU { get; private set; }
    [field: NonSerialized] public IPPU PPU { get; private set; }
    [field: NonSerialized] public IAPU APU { get; private set; }

    [JsonIgnore]
    [field: NonSerialized]
    public IROM ROM { get; set; }

    private byte[] _ram = [];

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

    private const double _apuCyclesPerMaster = 32040 * 32.0 / (1364.0 * 262 * 60);

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
    public bool InNmi => _inNmi;

    private int _cpuCyclesLeft;
    private int _cpuMemOps;
    private double _apuCatchCycles;

    private int _ramAdr;

    private bool _hIrqEnabled;
    private bool _vIrqEnabled;
    private bool _nmiEnabled;
    private int _hTimer;
    private int _vTimer;
    private bool _inNmi;
    private bool _inIrq;
    private bool _inHblank;
    private bool _inVblank;

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
    private bool _dmaBusy;
    private bool[] _dmaActive = [];
    private bool[] _hdmaActive = [];

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
    private int _traceApuPortsCount;
    private bool HasExplicitWramTraceFilter => _traceWramAddrs.Count > 0;

    private int[] _dmaMode = [];
    private bool[] _dmaFixed = [];
    private bool[] _dmaDec = [];
    private bool[] _hdmaInd = [];
    private bool[] _dmaFromB = [];
    private bool[] _dmaUnusedBit = [];

    private bool[] _hdmaDoTransfer = [];
    private bool[] _hdmaTerminated = [];
    private int _dmaOffIndex;
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
        if (!dma)
        {
            _cpuMemOps++;
            _cpuCyclesLeft += GetAccessTime(adr);
        }
        int val = Rread(adr);
        OpenBus = val;
        return val;
    }

    public void Write(int adr, int value, bool dma = false)
    {
        if (!dma)
        {
            _cpuMemOps++;
            _cpuCyclesLeft += GetAccessTime(adr);
        }
        OpenBus = value;
        adr &= 0xffffff;
        int bank = adr >> 16;
        adr &= 0xffff;
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
                _joypadStrobe = (value & 0x1) > 0;
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
        ROM.Write(bank, adr, (byte) value);
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
    }

    private void Reset2()
    {
        XPos = 0;
        YPos = 0;
        Cycles = 0;
        _cpuCyclesLeft = 5 * 8 + 12;
        _cpuMemOps = 0;
        _apuCatchCycles = 0;
        _ramAdr = 0;
        _hIrqEnabled = false;
        _vIrqEnabled = false;
        _nmiEnabled = false;
        _hTimer = 0x1ff;
        _vTimer = 0x1ff;
        _inNmi = false;
        _inIrq = false;
        _inHblank = false;
        _inVblank = false;
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
        _dmaBusy = false;
        _dmaActive = new bool[8];
        _hdmaActive = new bool[8];
        _dmaMode = new int[8];
        _dmaFixed = new bool[8];
        _dmaDec = new bool[8];
        _hdmaInd = new bool[8];
        _dmaFromB = new bool[8];
        _dmaUnusedBit = new bool[8];
        _hdmaDoTransfer = new bool[8];
        _hdmaTerminated = new bool[8];
        _dmaOffIndex = 0;
        OpenBus = 0;
        ROM.ResetCoprocessor();
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
            int extraData = rom.Length - header.RomSize / 2;
            var nRom = new byte[header.RomSize];
            for (var i = 0; i < nRom.Length; i++)
            {
                if (i < header.RomSize / 2)
                {
                    nRom[i] = rom[i];
                }
                else
                {
                    nRom[i] = rom[header.RomSize / 2 + i % extraData];
                }
            }
            rom = nRom;
        }
        ROM.LoadROM(rom, header);
    }

    private void Cycle(bool noPpu) 
    {
        Cycles += 2;
        _apuCatchCycles += _apuCyclesPerMaster * 2;
        if (_joypadStrobe)
        {
            _joypad1Val = _joypad1State;
            _joypad2Val = _joypad2State;
        }
        int vBlankStart = IsPal ? 240 : (PPU.FrameOverscan ? 240 : 225);
        if (XPos == 0)
        {
            // HVBJOY reports HBlank during the first 4 master cycles of each scanline.
            _inHblank = true;
            PPU.CheckOverscan(YPos);

            if (YPos == vBlankStart)
            {
                if (string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_LOG_VERBOSE"), "1", StringComparison.Ordinal))
                {
                    Console.WriteLine($"[VBLANK-START] Y={YPos} IsPal={IsPal} Overscan={PPU.FrameOverscan} Start={vBlankStart}");
                }
                _inNmi = true;
                _inVblank = true;
                if (_autoJoyRead)
                {
                    _autoJoyPendingStart = true;
                }
                if (_nmiEnabled)
                {
                    CPU.NmiWanted = true;
                }
            }
            else if (YPos == 0)
            {
                if (_inVblank && string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_LOG_VERBOSE"), "1", StringComparison.Ordinal))
                {
                    Console.WriteLine($"[VBLANK-END] Y={YPos} X={XPos}");
                }
                _inNmi = false;
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

        if (_hdmaTimer > 0)
        {
            _hdmaTimer -= 2;
        }
        else if (_dmaBusy)
        {
            HandleDma();
        }
        else if (XPos < 536 || XPos >= 576)
        {
            CpuCycle();
        }
        if (YPos == _vTimer && _vIrqEnabled)
        {
            if (!_hIrqEnabled)
            {
                if (XPos == 0)
                {
                    _inIrq = true;
                }
            }
            else
            {
                if (XPos == _hTimer * 4)
                {
                    _inIrq = true;
                }
            }
        }
        else if (XPos == _hTimer * 4 && _hIrqEnabled && !_vIrqEnabled)
        {
            _inIrq = true;
        }
        
        CPU.IrqWanted = _inIrq || ROM.IrqWanted;
        if (XPos == 512 && !noPpu)
        {
            PPU.RenderLine(YPos);
        }
        else if (XPos == 1096)
        {
            if (!_inVblank)
            {
                HandleHdma();
            }
        }
        if (_autoJoyPendingStart && YPos == vBlankStart && XPos == 130)
        {
            _autoJoyPendingStart = false;
            _autoJoyBusy = true;
            _autoJoyTimer = 4224;
            DoAutoJoyRead();
        }
        if (_autoJoyBusy)
        {
            _autoJoyTimer -= 2;
            if (_autoJoyTimer == 0)
            {
                _autoJoyBusy = false;
            }
        }
        ROM.RunCoprocessor(Cycles);
        CatchUpApu();
        XPos += 2;
        if (XPos == 1364)
        {
            XPos = 0;
            YPos++;
            int maxV = IsPal ? 312 : 262;
            if (YPos == maxV)
            {
                YPos = 0;
            }
        }
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
        long catchUpCycles = (long) _apuCatchCycles & 0xffffffff;
        for (var i = 0; i < catchUpCycles; i++)
        {
            APU.Cycle();
        }
        _apuCatchCycles -= catchUpCycles;
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
        if (_dmaTimer > 0)
        {
            _dmaTimer -= 2;
            return;
        }
        int i;
        for (i = 0; i < 8; i++)
        {
            if (_dmaActive[i])
            {
                break;
            }
        }
        if (i == 8)
        {
            _dmaBusy = false;
            _dmaOffIndex = 0;
            return;
        }
        if (!_dmaFromB[i] && !_dmaNotifyActive[i])
        {
            uint sourceAddress = (uint)((_dmaAadrBank[i] << 16) | _dmaAadr[i]);
            if (ROM is KSNES.ROM.ROM rom)
            {
                rom.NotifyDmaStart((byte)i, sourceAddress);
                _dmaNotifyActive[i] = true;
                _dmaNotifyCount++;
            }
        }
        int tableOff = _dmaMode[i] * 4 + _dmaOffIndex++;
        _dmaOffIndex &= 0x3;
        if (_dmaFromB[i])
        {
            Write((_dmaAadrBank[i] << 16) | _dmaAadr[i], ReadBBus((_dmaBadr[i] + _dmaOffs[tableOff]) & 0xff), true);
        }
        else
        {
            WriteBBus((_dmaBadr[i] + _dmaOffs[tableOff]) & 0xff,
                Read((_dmaAadrBank[i] << 16) | _dmaAadr[i], true), true);
        }
        _dmaTimer += 6;
        if (!_dmaFixed[i])
        {
            if (_dmaDec[i])
            {
                _dmaAadr[i]--;
            }
            else
            {
                _dmaAadr[i]++;
            }
        }
        _dmaSize[i]--;
        if (_dmaSize[i] == 0)
        {
            _dmaOffIndex = 0;
            _dmaActive[i] = false;
            if (_dmaNotifyActive[i])
            {
                _dmaNotifyActive[i] = false;
                _dmaNotifyCount--;
                if (ROM is KSNES.ROM.ROM rom)
                    rom.NotifyDmaEnd((byte)i);
            }
            _dmaTimer += 8;
        }
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
                _hdmaDoTransfer[i] = true;
            }
            else
            {
                _hdmaDoTransfer[i] = false;
            }
            _hdmaTerminated[i] = false;
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
                                Write((_hdmaIndBank[i] << 16) | _dmaSize[i], ReadBBus((_dmaBadr[i] + _dmaOffs[tableOff]) & 0xff), true);
                            }
                            else
                            {
                                WriteBBus((_dmaBadr[i] + _dmaOffs[tableOff]) & 0xff,
                                    Read((_hdmaIndBank[i] << 16) | _dmaSize[i], true), true);
                            }
                            _dmaSize[i]++;
                        }
                        else
                        {
                            if (_dmaFromB[i])
                            {
                                Write((_dmaAadrBank[i] << 16) | _hdmaTableAdr[i], ReadBBus((_dmaBadr[i] + _dmaOffs[tableOff]) & 0xff), true);
                            }
                            else
                            {
                                WriteBBus((_dmaBadr[i] + _dmaOffs[tableOff]) & 0xff,
                                    Read((_dmaAadrBank[i] << 16) | _hdmaTableAdr[i], true), true);
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
                val |= _inNmi ? 0x80 : 0;
                val |= OpenBus & 0x70;
                if (_traceDma)
                {
                    int pc = -1;
                    if (CPU is KSNES.CPU.CPU cpu)
                        pc = cpu.ProgramCounter24;
                    Console.WriteLine($"[INT-STAT] RDNMI read pc=0x{pc:X6} val=0x{val:X2}");
                }
                _inNmi = false;
                return val;
            case 0x4211:
                int val2 = _inIrq ? 0x80 : 0;
                val2 |= OpenBus & 0x7f;
                _inIrq = false;
                CPU.IrqWanted = false;
                return val2;
            case 0x4212:
                int val3 = _autoJoyBusy ? 0x1 : 0;
                val3 |= _inHblank ? 0x40 : 0;
                val3 |= _inVblank ? 0x80 : 0;
                val3 |= OpenBus & 0x3e;
                if (string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_4212"), "1", StringComparison.Ordinal))
                {
                    Console.WriteLine($"[4212] R val=0x{val3:X2} vblank={_inVblank} hblank={_inHblank} autojoy={_autoJoyBusy} Y={YPos} X={XPos}");
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
                return _joypad1AutoRead & 0xff;
            case 0x4219:
                return (_joypad1AutoRead & 0xff00) >> 8;
            case 0x421a:
                return _joypad2AutoRead & 0xff;
            case 0x421b:
                return (_joypad2AutoRead & 0xff00) >> 8;
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

    private void WriteReg(int adr, int value) 
    {
        switch (adr)
        {
            case 0x4200:
                if (string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_LOG_VERBOSE"), "1", StringComparison.Ordinal))
                {
                    int pcLog = CPU is KSNES.CPU.CPU cpuLog ? cpuLog.ProgramCounter24 : -1;
                    Console.WriteLine($"[SNES-REG] W 0x4200 val=0x{value:X2} PC=0x{pcLog:X6}");
                }
                _autoJoyRead = (value & 0x1) > 0;
                _hIrqEnabled = (value & 0x10) > 0;
                _vIrqEnabled = (value & 0x20) > 0;
                _nmiEnabled = (value & 0x80) > 0;
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
                _dmaBusy = value > 0;
                _dmaTimer += _dmaBusy ? 8 : 0;
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
                        Console.WriteLine($"[DMA-STATE] ch={ch} mode={_dmaMode[ch]} bbus=0x{_dmaBadr[ch]:X2} aaddr=0x{_dmaAadr[ch]:X4} abank=0x{_dmaAadrBank[ch]:X2} size=0x{_dmaSize[ch]:X4} fromB={_dmaFromB[ch]} fixed={_dmaFixed[ch]} dec={_dmaDec[ch]}");
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
            CatchUpApu();
            int port = adr & 0x3;
            int val = APU.SpcWritePorts[port];
            int pc = -1;
            if (CPU is KSNES.CPU.CPU cpu)
                pc = cpu.ProgramCounter24;
            TraceApuPort($"[APU-PORT-CPU-RD] port={port} val=0x{val:X2} pc=0x{pc:X6}");
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
            PPU.Write(adr, value);
            return;
        }
        if (adr >= 0x40 && adr < 0x80)
        {
            CatchUpApu();
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
                if (_traceWramWrites && ShouldTraceWramPortAccess(_ramAdr, _ramAdr))
                {
                    int pc = -1;
                    if (CPU is KSNES.CPU.CPU cpu)
                        pc = cpu.ProgramCounter24;
                    Console.WriteLine($"[WRAM-PORT-WR] addr=0x{_ramAdr:X5} val=0x{value:X2} pc=0x{pc:X6}");
                }
                _ram[_ramAdr++] = (byte) value;
                _ramAdr &= 0x1ffff;
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
            case 0x02: // OAMADDL
            case 0x03: // OAMADDH
            case 0x04: // OAMDATA
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
        if (!_traceApuPorts || _traceApuPortsCount >= 256)
            return;

        _traceApuPortsCount++;
        Console.WriteLine(message);
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
                return val;
            }
            if (adr == 0x4017)
            {
                int val = _joypad2Val & 0x1;
                _joypad2Val >>= 1;
                _joypad2Val |= 0x8000;
                return val;
            }
            if (adr >= 0x4200 && adr < 0x4380)
            {
                return ReadReg(adr);
            }
        }
        return ROM.Read(bank, adr);
    }

    public int Peek(int adr)
    {
        return Rread(adr);
    }

    private int GetAccessTime(int adr)
    {
        adr &= 0xffffff;
        int bank = adr >> 16;
        adr &= 0xffff;
        if (bank >= 0x40 && bank < 0x80)
        {
            return 8;
        }
        if (bank >= 0xc0)
        {
            return _fastMem ? 6 : 8;
        }
        if (adr < 0x2000)
        {
            return 8;
        }
        if (adr < 0x4000)
        {
            return 6;
        }
        if (adr < 0x4200)
        {
            return 12;
        }
        if (adr < 0x6000)
        {
            return 6;
        }
        if (adr < 0x8000)
        {
            return 8;
        }
        return _fastMem && bank >= 0x80 ? 6 : 8;
    }

    public void SetKeyDown(SNESButton button)
    {
        _joypad1State |= 1 << (int) button;
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
        var header = new Header
        {
            Name = str,
            Type = rom[baseOff + 0x15] & 0xf,
            Speed = rom[baseOff + 0x15] >> 4,
            MapMode = rom[baseOff + 0x15],
            Chips = rom[baseOff + 0x16] & 0xf,
            ChipsetByte = rom[baseOff + 0x16],
            RomSize = 0x400 << rom[baseOff + 0x17],
            RamSize = 0x400 << rom[baseOff + 0x18],
            Region = rom[baseOff + 0x19],
            ExCoprocessor = ReadExCoprocessor(rom, baseOff),
            IsHiRom = (rom[baseOff + 0x15] & 0x0F) == 0x01
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
        int scoreLo = CountPrintable(data, 0x7FC0);
        int scoreHi = CountPrintable(data, 0xFFC0);
        return scoreHi > scoreLo ? 0xFFC0 : 0x7FC0;
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
