namespace EutherDrive.Core.Sega32X;

internal enum Sega32XAccess : ushort
{
    M68k = 0,
    Sh2 = 1,
}

internal enum Sega32XCpu
{
    Master,
    Slave,
}

internal sealed class Sega32XSh2Interrupts
{
    public bool ResetPending { get; set; }
    public bool VPending { get; set; }
    public bool VEnabled { get; set; }
    public bool HPending { get; set; }
    public bool HEnabled { get; set; }
    public bool CommandPending { get; set; }
    public bool CommandEnabled { get; set; }
    public bool PwmPending { get; set; }
    public bool PwmEnabled { get; set; }
    public byte CurrentInterruptLevel { get; private set; }

    public ushort MaskBits =>
        (ushort)((VEnabled ? 1 << 3 : 0)
        | (HEnabled ? 1 << 2 : 0)
        | (CommandEnabled ? 1 << 1 : 0)
        | (PwmEnabled ? 1 : 0));

    public void WriteMaskBits(ushort value)
    {
        VEnabled = (value & 0x0008) != 0;
        HEnabled = (value & 0x0004) != 0;
        CommandEnabled = (value & 0x0002) != 0;
        PwmEnabled = (value & 0x0001) != 0;
        UpdateInterruptLevel();
    }

    public void ClearReset()
    {
        ResetPending = false;
        UpdateInterruptLevel();
    }

    public void ClearV()
    {
        if (VEnabled)
            VPending = false;
        UpdateInterruptLevel();
    }

    public void ClearH()
    {
        HPending = false;
        UpdateInterruptLevel();
    }

    public void ClearCommand()
    {
        CommandPending = false;
        UpdateInterruptLevel();
    }

    public void ClearPwm()
    {
        PwmPending = false;
        UpdateInterruptLevel();
    }

    public void UpdateInterruptLevel()
    {
        CurrentInterruptLevel = ResetPending ? (byte)14
            : VPending && VEnabled ? (byte)12
            : HPending ? (byte)10
            : CommandPending && CommandEnabled ? (byte)8
            : PwmPending ? (byte)6
            : (byte)0;
    }
}

internal sealed class Sega32XDmaFifo
{
    private const int BlockLength = 4;
    private readonly ushort[,] _blocks = new ushort[2, BlockLength];
    private readonly bool[] _ready = new bool[2];
    private int _m68kBlock;
    private int _m68kIndex;
    private int _sh2Block;
    private int _sh2Index;

    public bool Sh2IsEmpty => !_ready[_sh2Block];
    public bool IsFull => _ready[_m68kBlock];

    public void Push(ushort value)
    {
        _blocks[_m68kBlock, _m68kIndex++] = value;
        if (_m68kIndex == BlockLength)
        {
            _ready[_m68kBlock] = true;
            _m68kBlock ^= 1;
            _m68kIndex = 0;
        }
    }

    public ushort Pop()
    {
        ushort value = _blocks[_sh2Block, _sh2Index++];
        if (_sh2Index == BlockLength)
        {
            _ready[_sh2Block] = false;
            _sh2Block ^= 1;
            _sh2Index = 0;
        }

        return value;
    }

    public void Clear()
    {
        Array.Clear(_blocks);
        Array.Clear(_ready);
        _m68kBlock = 0;
        _m68kIndex = 0;
        _sh2Block = 0;
        _sh2Index = 0;
    }
}

internal sealed class Sega32XDmaRegisters
{
    public bool RomToVramDma { get; set; }
    public bool UnknownBit1 { get; set; }
    public bool Active { get; set; }
    public uint SourceAddress { get; set; }
    public uint DestinationAddress { get; set; }
    public ushort Length { get; set; } = 0xFFFF;
    public Sega32XDmaFifo Fifo { get; } = new();
}

internal sealed class Sega32XSystemRegisters
{
    public bool AdapterEnabled { get; private set; }
    public bool ResetSh2 { get; private set; }
    public Sega32XAccess VdpAccess { get; private set; }
    public byte M68kRomBank { get; private set; }
    public ushort[] CommunicationPorts { get; } = new ushort[8];
    public Sega32XSh2Interrupts MasterInterrupts { get; } = new();
    public Sega32XSh2Interrupts SlaveInterrupts { get; } = new();
    public Sega32XDmaRegisters Dma { get; } = new();
    public ushort SegaTvBits { get; private set; }
    public ushort HInterruptInterval { get; private set; }
    public bool HInterruptInVBlank { get; private set; }
    public ushort DisplayMode { get; private set; }
    public ushort ScreenShift { get; private set; }
    public ushort AutoFillLength { get; private set; } = 1;
    public ushort AutoFillStartAddress { get; private set; }
    public ushort AutoFillData { get; private set; }
    public ushort FrameBufferControl { get; private set; }

    public Sega32XSystemRegisters()
    {
        Reset();
    }

    public void Reset()
    {
        AdapterEnabled = false;
        ResetSh2 = false;
        VdpAccess = Sega32XAccess.M68k;
        M68kRomBank = 0;
        Array.Clear(CommunicationPorts);
        Dma.RomToVramDma = false;
        Dma.UnknownBit1 = false;
        Dma.Active = false;
        Dma.SourceAddress = 0;
        Dma.DestinationAddress = 0;
        Dma.Length = 0xFFFF;
        Dma.Fifo.Clear();
        SegaTvBits = 0;
        HInterruptInterval = 0;
        HInterruptInVBlank = false;
        DisplayMode = 0;
        ScreenShift = 0;
        AutoFillLength = 1;
        AutoFillStartAddress = 0;
        AutoFillData = 0;
        FrameBufferControl = 0;

        MasterInterrupts.ResetPending = true;
        SlaveInterrupts.ResetPending = true;
        MasterInterrupts.UpdateInterruptLevel();
        SlaveInterrupts.UpdateInterruptLevel();
    }

    public ushort M68kRead(uint address)
    {
        return address switch
        {
            0xA15100 => ReadAdapterControl(),
            0xA15102 => ReadInterruptControl(),
            0xA15104 => M68kRomBank,
            0xA15106 => ReadM68kDreqControl(),
            0xA15108 => (ushort)(Dma.SourceAddress >> 16),
            0xA1510A => (ushort)Dma.SourceAddress,
            0xA1510C => (ushort)(Dma.DestinationAddress >> 16),
            0xA1510E => (ushort)Dma.DestinationAddress,
            0xA15110 => Dma.Length,
            0xA1511A => SegaTvBits,
            0xA15180 => DisplayMode,
            0xA15182 => ScreenShift,
            0xA15184 => (ushort)((AutoFillLength - 1) & 0x00FF),
            0xA15186 => AutoFillStartAddress,
            >= 0xA15120 and <= 0xA1512F => ReadCommunicationPort(address),
            _ => 0,
        };
    }

    public void M68kWrite(uint address, ushort value)
    {
        switch (address)
        {
            case 0xA15100:
                AdapterEnabled = (value & 0x0001) != 0;
                ResetSh2 = (value & 0x0002) == 0;
                VdpAccess = (value & 0x8000) != 0 ? Sega32XAccess.Sh2 : Sega32XAccess.M68k;
                break;
            case 0xA15102:
                MasterInterrupts.CommandPending = (value & 0x0001) != 0;
                SlaveInterrupts.CommandPending = (value & 0x0002) != 0;
                MasterInterrupts.UpdateInterruptLevel();
                SlaveInterrupts.UpdateInterruptLevel();
                break;
            case 0xA15104:
                M68kRomBank = (byte)(value & 0x0003);
                break;
            case 0xA15106:
                Dma.RomToVramDma = (value & 0x0001) != 0;
                Dma.UnknownBit1 = (value & 0x0002) != 0;
                Dma.Active = (value & 0x0004) != 0;
                if (!Dma.Active)
                    Dma.Fifo.Clear();
                break;
            case 0xA15108:
                Dma.SourceAddress = (Dma.SourceAddress & 0x0000FFFFu) | ((uint)(value & 0x00FF) << 16);
                break;
            case 0xA1510A:
                Dma.SourceAddress = (Dma.SourceAddress & 0xFFFF0000u) | (uint)(value & 0xFFFE);
                break;
            case 0xA1510C:
                Dma.DestinationAddress = (Dma.DestinationAddress & 0x0000FFFFu) | ((uint)(value & 0x00FF) << 16);
                break;
            case 0xA1510E:
                Dma.DestinationAddress = (Dma.DestinationAddress & 0xFFFF0000u) | value;
                break;
            case 0xA15110:
                Dma.Length = (ushort)(value & 0xFFFC);
                break;
            case 0xA15112:
                if (Dma.Active)
                    Dma.Fifo.Push(value);
                break;
            case 0xA1511A:
                SegaTvBits = (ushort)(value & 0x0101);
                break;
            case 0xA15180:
                DisplayMode = (ushort)(value & 0x00C3);
                break;
            case 0xA15182:
                ScreenShift = (ushort)(value & 0x0001);
                break;
            case 0xA15184:
                AutoFillLength = (ushort)((value & 0x00FF) + 1);
                break;
            case 0xA15186:
                AutoFillStartAddress = value;
                break;
            case 0xA15188:
                AutoFillData = value;
                break;
            case 0xA1518A:
                FrameBufferControl = (ushort)(value & 0x0001);
                break;
            default:
                if (address >= 0xA15120 && address <= 0xA1512F)
                    WriteCommunicationPort(address, value);
                break;
        }
    }

    public ushort Sh2Read(uint address, Sega32XCpu whichCpu)
    {
        return address switch
        {
            0x4000 => ReadSh2InterruptMask(whichCpu),
            0x4004 => HInterruptInterval,
            0x4006 => ReadSh2DreqControl(),
            0x4008 => (ushort)(Dma.SourceAddress >> 16),
            0x400A => (ushort)Dma.SourceAddress,
            0x400C => (ushort)(Dma.DestinationAddress >> 16),
            0x400E => (ushort)Dma.DestinationAddress,
            0x4010 => Dma.Length,
            0x4012 => ReadSh2DreqFifo(),
            >= 0x4020 and <= 0x402F => ReadCommunicationPort(address),
            0x4100 => DisplayMode,
            0x4102 => ScreenShift,
            0x4104 => (ushort)((AutoFillLength - 1) & 0x00FF),
            0x4106 => AutoFillStartAddress,
            _ => 0,
        };
    }

    public void Sh2Write(uint address, ushort value, Sega32XCpu whichCpu)
    {
        switch (address)
        {
            case 0x4000:
                VdpAccess = (value & 0x8000) != 0 ? Sega32XAccess.Sh2 : Sega32XAccess.M68k;
                HInterruptInVBlank = (value & 0x0080) != 0;
                GetInterrupts(whichCpu).WriteMaskBits(value);
                break;
            case 0x4004:
                HInterruptInterval = (ushort)(value & 0x00FF);
                break;
            case 0x4014:
                GetInterrupts(whichCpu).ClearReset();
                break;
            case 0x4016:
                GetInterrupts(whichCpu).ClearV();
                break;
            case 0x4018:
                GetInterrupts(whichCpu).ClearH();
                break;
            case 0x401A:
                GetInterrupts(whichCpu).ClearCommand();
                break;
            case 0x401C:
                GetInterrupts(whichCpu).ClearPwm();
                break;
            case 0x4100:
                DisplayMode = (ushort)(value & 0x00C3);
                break;
            case 0x4102:
                ScreenShift = (ushort)(value & 0x0001);
                break;
            case 0x4104:
                AutoFillLength = (ushort)((value & 0x00FF) + 1);
                break;
            case 0x4106:
                AutoFillStartAddress = value;
                break;
            case 0x4108:
                AutoFillData = value;
                break;
            case 0x410A:
                FrameBufferControl = (ushort)(value & 0x0001);
                break;
            default:
                if (address >= 0x4020 && address <= 0x402F)
                    WriteCommunicationPort(address, value);
                break;
        }
    }

    public void NotifyVBlankStart()
    {
        MasterInterrupts.VPending = true;
        SlaveInterrupts.VPending = true;
        MasterInterrupts.UpdateInterruptLevel();
        SlaveInterrupts.UpdateInterruptLevel();
    }

    public void NotifyVBlankEnd()
    {
        MasterInterrupts.VPending = false;
        SlaveInterrupts.VPending = false;
        MasterInterrupts.UpdateInterruptLevel();
        SlaveInterrupts.UpdateInterruptLevel();
    }

    public void NotifyHInterrupt()
    {
        MasterInterrupts.HPending |= MasterInterrupts.HEnabled;
        SlaveInterrupts.HPending |= SlaveInterrupts.HEnabled;
        MasterInterrupts.UpdateInterruptLevel();
        SlaveInterrupts.UpdateInterruptLevel();
    }

    public bool EitherHInterruptEnabled => MasterInterrupts.HEnabled || SlaveInterrupts.HEnabled;

    private ushort ReadAdapterControl()
    {
        return (ushort)(((ushort)VdpAccess << 15)
            | (1 << 7)
            | ((!ResetSh2 ? 1 : 0) << 1)
            | (AdapterEnabled ? 1 : 0));
    }

    private ushort ReadInterruptControl()
    {
        return (ushort)(((SlaveInterrupts.CommandPending ? 1 : 0) << 1)
            | (MasterInterrupts.CommandPending ? 1 : 0));
    }

    private ushort ReadM68kDreqControl()
    {
        return (ushort)(((Dma.Fifo.IsFull ? 1 : 0) << 7)
            | ((Dma.Active ? 1 : 0) << 2)
            | ((Dma.UnknownBit1 ? 1 : 0) << 1)
            | (Dma.RomToVramDma ? 1 : 0));
    }

    private ushort ReadSh2DreqControl()
    {
        return (ushort)(((Dma.Fifo.IsFull ? 1 : 0) << 15)
            | ((Dma.Fifo.Sh2IsEmpty ? 1 : 0) << 14)
            | ((Dma.Active ? 1 : 0) << 2)
            | ((Dma.UnknownBit1 ? 1 : 0) << 1)
            | (Dma.RomToVramDma ? 1 : 0));
    }

    private ushort ReadSh2InterruptMask(Sega32XCpu whichCpu)
    {
        Sega32XSh2Interrupts interrupts = GetInterrupts(whichCpu);
        return (ushort)(((ushort)VdpAccess << 15)
            | (AdapterEnabled ? 1 << 9 : 0)
            | (HInterruptInVBlank ? 1 << 7 : 0)
            | interrupts.MaskBits);
    }

    private ushort ReadSh2DreqFifo()
    {
        Dma.Length = (ushort)(Dma.Length - 1);
        if (Dma.Length == 0)
            Dma.Active = false;
        return Dma.Fifo.Pop();
    }

    private ushort ReadCommunicationPort(uint address)
    {
        int index = (int)((address >> 1) & 0x7);
        return CommunicationPorts[index];
    }

    private void WriteCommunicationPort(uint address, ushort value)
    {
        int index = (int)((address >> 1) & 0x7);
        CommunicationPorts[index] = value;
    }

    private Sega32XSh2Interrupts GetInterrupts(Sega32XCpu whichCpu)
        => whichCpu == Sega32XCpu.Master ? MasterInterrupts : SlaveInterrupts;
}
