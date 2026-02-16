using System;

namespace EutherDrive.Core.SegaCd;

public sealed class SegaCdMemory
{
    public const int BiosLen = 128 * 1024;
    public const int PrgRamLen = 512 * 1024;
    public const int BackupRamLen = 8 * 1024;
    public const int RamCartLen = 128 * 1024;
    private const byte RamCartSizeByte = 0x04;
    private const uint TimerDivider = 1536;

    private readonly byte[] _bios;
    private readonly byte[] _prgRam = new byte[PrgRamLen];
    private readonly byte[] _backupRam = new byte[BackupRamLen];
    private readonly byte[] _ramCart = new byte[RamCartLen];
    private bool _ramCartWritesEnabled = true;
    private bool _backupRamDirty;
    private uint _timerDivider = TimerDivider;
    public const uint SubBusAddressMask = 0x0FFFFF;
    private const uint SubRegisterAddressMask = 0x01FF;

    private readonly BufferedWrite[] _bufferedSubWrites = new BufferedWrite[32];
    private int _bufferedSubWriteCount;

    private enum BufferedWriteKind
    {
        Byte,
        Word
    }

    private readonly struct BufferedWrite
    {
        public readonly BufferedWriteKind Kind;
        public readonly uint Address;
        public readonly ushort Value;

        public BufferedWrite(BufferedWriteKind kind, uint address, ushort value)
        {
            Kind = kind;
            Address = address;
            Value = value;
        }
    }

    public SegaCdRegisters Registers { get; } = new();
    public WordRam WordRam { get; } = new();
    public SegaCdFontRegisters FontRegisters { get; } = new();
    public SegaCdCdcStub Cdc { get; } = new();
    public SegaCdCddStub Cdd { get; } = new();
    public SegaCdPcmStub Pcm { get; } = new();
    public SegaCdGraphicsCoprocessor Graphics { get; } = new();

    public bool EnableRamCartridge { get; set; } = true;

    public SegaCdMemory(byte[] bios)
    {
        if (bios.Length != BiosLen)
            throw new InvalidOperationException($"BIOS must be {BiosLen} bytes.");
        _bios = bios;
    }

    public void Reset()
    {
        Registers.Reset();
        Cdc.Reset();
        Cdd.Reset();
        WordRam.Mode.Equals(WordRamMode.TwoM);
    }

    public void Tick(uint masterClockCycles)
    {
        uint cycles = masterClockCycles;
        while (cycles >= _timerDivider)
        {
            ClockTimers();
            cycles -= _timerDivider;
            _timerDivider = TimerDivider;
        }
        _timerDivider -= cycles;

        Cdc.Tick();
        Cdd.Tick(Registers.CddHostClockOn);
        Graphics.Tick(masterClockCycles, WordRam, Registers.GraphicsInterruptEnabled);
    }

    public void EmulateSubCpuHandshake()
    {
        if (Registers.SubCpuReset || Registers.SubCpuBusReq)
            return;

        for (int i = 0; i < Registers.CommunicationCommands.Length; i++)
            Registers.CommunicationStatuses[i] = Registers.CommunicationCommands[i];

        Registers.SubCpuCommunicationFlags = Registers.MainCpuCommunicationFlags;
    }

    public void FlushBufferedSubWrites()
    {
        if (_bufferedSubWriteCount == 0)
            return;

        for (int i = 0; i < _bufferedSubWriteCount; i++)
        {
            var write = _bufferedSubWrites[i];
            if (write.Kind == BufferedWriteKind.Byte)
                WriteSubRegisterByte(write.Address, (byte)write.Value);
            else
                WriteSubRegisterWord(write.Address, write.Value);
        }
        _bufferedSubWriteCount = 0;
    }

    private void ClockTimers()
    {
        if (Registers.TimerCounter == 1)
        {
            Registers.TimerInterruptPending = true;
            Registers.TimerCounter = 0;
        }
        else if (Registers.TimerCounter == 0)
        {
            Registers.TimerCounter = Registers.TimerInterval;
        }
        else
        {
            Registers.TimerCounter--;
        }

        Registers.StopwatchCounter = (ushort)((Registers.StopwatchCounter + 1) & 0x0FFF);
    }

    public byte ReadMainByte(uint address)
    {
        if (address <= 0x1FFFFF)
        {
            if ((address & 0x20000) == 0)
            {
                switch (address)
                {
                    case 0x000070:
                    case 0x000071:
                        return 0xFF;
                    case 0x000072:
                        return (byte)(Registers.HInterruptVector >> 8);
                    case 0x000073:
                        return (byte)(Registers.HInterruptVector & 0xFF);
                    default:
                        return _bios[address & 0x1FFFF];
                }
            }

            uint prgAddr = Registers.PrgRamAddr(address);
            return _prgRam[prgAddr];
        }

        if (address <= 0x3FFFFF)
            return WordRam.MainCpuReadRam(address);

        if (address <= 0x7FFFFF)
            return ReadRamCartByte(address);

        if (address >= 0xA12000 && address <= 0xA1202F)
            return ReadMainRegisterByte(address);

        return 0xFF;
    }

    public ushort ReadMainWord(uint address)
    {
        if (address <= 0x1FFFFF)
        {
            if ((address & 0x20000) == 0)
            {
                if (address == 0x000070) return 0xFFFF;
                if (address == 0x000072) return Registers.HInterruptVector;
                int addr = (int)(address & 0x1FFFF);
                return (ushort)((_bios[addr] << 8) | _bios[addr + 1]);
            }

            uint prgAddr = Registers.PrgRamAddr(address);
            return (ushort)((_prgRam[prgAddr] << 8) | _prgRam[prgAddr + 1]);
        }

        if (address <= 0x3FFFFF)
        {
            byte msb = WordRam.MainCpuReadRam(address);
            byte lsb = WordRam.MainCpuReadRam(address | 1);
            return (ushort)((msb << 8) | lsb);
        }

        if (address <= 0x7FFFFF)
        {
            return ReadRamCartByte(address | 1);
        }

        if (address >= 0xA12000 && address <= 0xA1202F)
            return ReadMainRegisterWord(address);

        return 0xFFFF;
    }

    public void WriteMainByte(uint address, byte value)
    {
        if (address <= 0x1FFFFF)
        {
            if ((address & 0x20000) != 0)
            {
                uint prgAddr = Registers.PrgRamAddr(address);
                WritePrgRam(prgAddr, value, ScdCpu.Main);
            }
            return;
        }

        if (address <= 0x3FFFFF)
        {
            WordRam.MainCpuWriteRam(address, value);
            return;
        }

        if (address <= 0x7FFFFF)
        {
            WriteRamCartByte(address, value);
            return;
        }

        if (address >= 0xA12000 && address <= 0xA1202F)
        {
            WriteMainRegisterByte(address, value);
        }
    }

    public void WriteMainWord(uint address, ushort value)
    {
        if (address <= 0x1FFFFF)
        {
            if ((address & 0x20000) != 0)
            {
                uint prgAddr = Registers.PrgRamAddr(address);
                WritePrgRam(prgAddr, (byte)(value >> 8), ScdCpu.Main);
                WritePrgRam(prgAddr + 1, (byte)value, ScdCpu.Main);
            }
            return;
        }

        if (address <= 0x3FFFFF)
        {
            WordRam.MainCpuWriteRam(address, (byte)(value >> 8));
            WordRam.MainCpuWriteRam(address | 1, (byte)value);
            return;
        }

        if (address <= 0x7FFFFF)
        {
            WriteRamCartByte(address | 1, (byte)value);
            return;
        }

        if (address >= 0xA12000 && address <= 0xA1202F)
        {
            WriteMainRegisterWord(address, value);
        }
    }

    private byte ReadRamCartByte(uint address)
    {
        if (!EnableRamCartridge)
            return 0xFF;
        if ((address & 1) == 0)
            return 0x00;
        if (address <= 0x4FFFFF)
            return RamCartSizeByte;
        if (address <= 0x5FFFFF)
            return 0x00;
        if (address <= 0x6FFFFF)
            return _ramCart[(address & 0x3FFFF) >> 1];
        if (address <= 0x7FFFFF)
            return _ramCartWritesEnabled ? (byte)1 : (byte)0;
        return 0x00;
    }

    private void WriteRamCartByte(uint address, byte value)
    {
        if (!EnableRamCartridge)
            return;
        if ((address & 1) == 0)
            return;
        if (address <= 0x5FFFFF)
            return;
        if (address <= 0x6FFFFF)
        {
            if (_ramCartWritesEnabled)
            {
                _ramCart[(address & 0x3FFFF) >> 1] = value;
                _backupRamDirty = true;
            }
            return;
        }
        if (address <= 0x7FFFFF)
        {
            _ramCartWritesEnabled = (value & 1) != 0;
        }
    }

    private void WritePrgRam(uint address, byte value, ScdCpu cpu)
    {
        if (cpu == ScdCpu.Main && !(Registers.SubCpuBusReq || Registers.SubCpuReset))
            return;

        uint boundary = (uint)Registers.PrgRamWriteProtect * 0x200;
        if (cpu == ScdCpu.Main || address >= boundary)
            _prgRam[address] = value;
    }

    private byte ReadMainRegisterByte(uint address)
    {
        return address switch
        {
            0xA12000 => (byte)(((Registers.SoftwareInterruptEnabled ? 1 : 0) << 7) | (Registers.SoftwareInterruptPending ? 1 : 0)),
            0xA12001 => (byte)(((Registers.SubCpuBusReq ? 1 : 0) << 1) | (Registers.SubCpuReset ? 0 : 1)),
            0xA12002 => Registers.PrgRamWriteProtect,
            0xA12003 => (byte)((Registers.PrgRamBank << 6) | WordRam.ReadControl()),
            0xA12004 => (byte)(((Cdc.EndOfDataTransfer ? 1 : 0) << 7) | ((Cdc.DataReady ? 1 : 0) << 6) | (byte)Cdc.DeviceDestination),
            0xA12006 => (byte)(Registers.HInterruptVector >> 8),
            0xA12007 => (byte)(Registers.HInterruptVector & 0xFF),
            0xA12008 => (byte)(Cdc.ReadHostData() >> 8),
            0xA12009 => (byte)(Cdc.ReadHostData() & 0xFF),
            0xA1200C => (byte)(Registers.StopwatchCounter >> 8),
            0xA1200D => (byte)(Registers.StopwatchCounter & 0xFF),
            0xA1200E => Registers.MainCpuCommunicationFlags,
            0xA1200F => Registers.SubCpuCommunicationFlags,
            >= 0xA12010 and <= 0xA1201F => ReadCommBuffer(Registers.CommunicationCommands, address),
            >= 0xA12020 and <= 0xA1202F => ReadCommBuffer(Registers.CommunicationStatuses, address),
            _ => 0x00
        };
    }

    private ushort ReadMainRegisterWord(uint address)
    {
        return address switch
        {
            0xA12000 or 0xA12002 => (ushort)((ReadMainRegisterByte(address) << 8) | ReadMainRegisterByte(address | 1)),
            0xA12004 => (ushort)(ReadMainRegisterByte(address) << 8),
            0xA12006 => Registers.HInterruptVector,
            0xA12008 => Cdc.ReadHostData(),
            0xA1200C => Registers.StopwatchCounter,
            0xA1200E => (ushort)((Registers.MainCpuCommunicationFlags << 8) | Registers.SubCpuCommunicationFlags),
            >= 0xA12010 and <= 0xA1201F => ReadCommWord(Registers.CommunicationCommands, address),
            >= 0xA12020 and <= 0xA1202F => ReadCommWord(Registers.CommunicationStatuses, address),
            _ => 0x0000
        };
    }

    private void WriteMainRegisterByte(uint address, byte value)
    {
        switch (address)
        {
            case 0xA12000:
                Registers.SoftwareInterruptPending = (value & 1) != 0;
                break;
            case 0xA12001:
                Registers.SubCpuBusReq = (value & 0x02) != 0;
                Registers.SubCpuReset = (value & 0x01) == 0;
                break;
            case 0xA12002:
                Registers.PrgRamWriteProtect = value;
                break;
            case 0xA12003:
                Registers.PrgRamBank = (byte)(value >> 6);
                WordRam.MainCpuWriteControl(value);
                break;
            case 0xA12006:
            case 0xA12007:
                Registers.HInterruptVector = (ushort)((value << 8) | value);
                break;
            case 0xA12008:
            case 0xA12009:
                Cdc.WriteHostData();
                break;
            case 0xA1200E:
            case 0xA1200F:
                Registers.MainCpuCommunicationFlags = value;
                break;
            case >= 0xA12010 and <= 0xA1201F:
                WriteCommBuffer(Registers.CommunicationCommands, address, value);
                break;
        }
    }

    private void WriteMainRegisterWord(uint address, ushort value)
    {
        switch (address)
        {
            case 0xA12000:
            case 0xA12002:
                WriteMainRegisterByte(address, (byte)(value >> 8));
                WriteMainRegisterByte(address | 1, (byte)value);
                break;
            case 0xA12006:
                Registers.HInterruptVector = value;
                break;
            case 0xA12008:
                Cdc.WriteHostData();
                break;
            case 0xA1200E:
                Registers.MainCpuCommunicationFlags = (byte)(value >> 8);
                break;
            case >= 0xA12010 and <= 0xA1201F:
                WriteCommWord(Registers.CommunicationCommands, address, value);
                break;
        }
    }

    private static byte ReadCommBuffer(ushort[] buffer, uint address)
    {
        int idx = (int)((address & 0xF) >> 1);
        ushort word = buffer[idx];
        return (address & 1) != 0 ? (byte)(word & 0xFF) : (byte)(word >> 8);
    }

    private static ushort ReadCommWord(ushort[] buffer, uint address)
    {
        int idx = (int)((address & 0xF) >> 1);
        return buffer[idx];
    }

    private static void WriteCommBuffer(ushort[] buffer, uint address, byte value)
    {
        int idx = (int)((address & 0xF) >> 1);
        ushort current = buffer[idx];
        if ((address & 1) != 0)
            buffer[idx] = (ushort)((current & 0xFF00) | value);
        else
            buffer[idx] = (ushort)((current & 0x00FF) | (value << 8));
    }

    private static void WriteCommWord(ushort[] buffer, uint address, ushort value)
    {
        int idx = (int)((address & 0xF) >> 1);
        buffer[idx] = value;
    }

    private byte ReadSubRegisterByte(uint address)
    {
        uint reg = address & SubRegisterAddressMask;
        switch (reg)
        {
            case 0x0000:
                return (byte)(((Registers.LedGreen ? 1 : 0) << 1) | (Registers.LedRed ? 1 : 0));
            case 0x0001:
                return 0x01;
            case 0x0002:
                return Registers.PrgRamWriteProtect;
            case 0x0003:
                return (byte)(WordRam.ReadControl() | ((byte)WordRam.PriorityMode << 3));
            case 0x0004:
                return (byte)(((Cdc.EndOfDataTransfer ? 1 : 0) << 7) | ((Cdc.DataReady ? 1 : 0) << 6) | (byte)Cdc.DeviceDestination);
            case 0x0005:
                return Cdc.RegisterAddress;
            case 0x0007:
                return Cdc.ReadRegister();
            case 0x0008:
                return (byte)(Cdc.ReadHostData() >> 8);
            case 0x0009:
                return (byte)(Cdc.ReadHostData() & 0xFF);
            case 0x000C:
                return (byte)(Registers.StopwatchCounter >> 8);
            case 0x000D:
                return (byte)(Registers.StopwatchCounter & 0xFF);
            case 0x000E:
                return Registers.MainCpuCommunicationFlags;
            case 0x000F:
                return Registers.SubCpuCommunicationFlags;
            case >= 0x0010 and <= 0x001F:
                return ReadCommBuffer(Registers.CommunicationCommands, reg);
            case >= 0x0020 and <= 0x002F:
                return ReadCommBuffer(Registers.CommunicationStatuses, reg);
            case 0x0031:
                return Registers.TimerInterval;
            case 0x0033:
                return (byte)(
                    ((Registers.SubcodeInterruptEnabled ? 1 : 0) << 6)
                    | ((Registers.CdcInterruptEnabled ? 1 : 0) << 5)
                    | ((Registers.CddInterruptEnabled ? 1 : 0) << 4)
                    | ((Registers.TimerInterruptEnabled ? 1 : 0) << 3)
                    | ((Registers.SoftwareInterruptEnabled ? 1 : 0) << 2)
                    | ((Registers.GraphicsInterruptEnabled ? 1 : 0) << 1));
            case 0x0036:
                return (byte)(Cdd.PlayingAudio ? 0 : 1);
            case 0x0037:
                return (byte)((Registers.CddHostClockOn ? 1 : 0) << 2);
            case >= 0x0038 and <= 0x0041:
                return Cdd.Status[(reg - 8) & 0x0F];
            case >= 0x0042 and <= 0x004B:
                return Registers.CddCommand[(reg - 2) & 0x0F];
            case 0x004C:
                return FontRegisters.ReadColor();
            case 0x004E:
                return (byte)(FontRegisters.FontBits >> 8);
            case 0x004F:
                return (byte)(FontRegisters.FontBits & 0xFF);
            case >= 0x0050 and <= 0x0057:
                {
                    ushort word = FontRegisters.ReadFontData(reg);
                    return (reg & 1) != 0 ? (byte)(word & 0xFF) : (byte)(word >> 8);
                }
            case >= 0x0058 and <= 0x0067:
                return Graphics.ReadRegisterByte(reg);
            default:
                return 0x00;
        }
    }

    private ushort ReadSubRegisterWord(uint address)
    {
        uint reg = address & SubRegisterAddressMask;
        switch (reg)
        {
            case 0x0000:
            case 0x0002:
            case 0x0004:
            case 0x0036:
                return (ushort)((ReadSubRegisterByte(reg) << 8) | ReadSubRegisterByte(reg | 1));
            case 0x0006:
                return ReadSubRegisterByte(reg | 1);
            case 0x0008:
                return Cdc.ReadHostData();
            case 0x000A:
                return (ushort)(Cdc.DmaAddress >> 3);
            case 0x000C:
                return Registers.StopwatchCounter;
            case 0x000E:
                return (ushort)((Registers.MainCpuCommunicationFlags << 8) | Registers.SubCpuCommunicationFlags);
            case >= 0x0010 and <= 0x001F:
                return ReadCommWord(Registers.CommunicationCommands, reg);
            case >= 0x0020 and <= 0x002F:
                return ReadCommWord(Registers.CommunicationStatuses, reg);
            case 0x0030:
                return Registers.TimerInterval;
            case 0x0032:
                return ReadSubRegisterByte(reg | 1);
            case >= 0x0038 and <= 0x0041:
                {
                    int rel = (int)((reg - 8) & 0x0F);
                    return (ushort)((Cdd.Status[rel] << 8) | Cdd.Status[(rel + 1) & 0x0F]);
                }
            case >= 0x0042 and <= 0x004B:
                {
                    int rel = (int)((reg - 2) & 0x0F);
                    return (ushort)((Registers.CddCommand[rel] << 8) | Registers.CddCommand[(rel + 1) & 0x0F]);
                }
            case 0x004C:
                return FontRegisters.ReadColor();
            case 0x004E:
                return FontRegisters.FontBits;
            case >= 0x0050 and <= 0x0057:
                return FontRegisters.ReadFontData(reg);
            case >= 0x0058 and <= 0x0067:
                return Graphics.ReadRegisterWord(reg);
            default:
                return 0x0000;
        }
    }

    private void WriteSubRegisterByte(uint address, byte value)
    {
        uint reg = address & SubRegisterAddressMask;
        switch (reg)
        {
            case 0x0000:
                Registers.LedGreen = (value & 0x02) != 0;
                Registers.LedRed = (value & 0x01) != 0;
                break;
            case 0x0001:
                if ((value & 0x01) == 0)
                    Cdd.Reset();
                break;
            case 0x0002:
            case 0x0003:
                WordRam.SubCpuWriteControl(value);
                break;
            case 0x0004:
                Cdc.SetDeviceDestination((SegaCdDeviceDestination)(value & 0x07));
                break;
            case 0x0005:
                Cdc.SetRegisterAddress((byte)(value & 0x1F));
                break;
            case 0x0007:
                Cdc.WriteRegister(value);
                break;
            case 0x0008:
            case 0x0009:
                Cdc.WriteHostData();
                break;
            case 0x000A:
            case 0x000B:
                Cdc.SetDmaAddress((uint)((value << 8) | value) << 3);
                break;
            case 0x000C:
            case 0x000D:
                Registers.StopwatchCounter = (ushort)(((value << 8) | value) & 0x0FFF);
                break;
            case 0x000E:
            case 0x000F:
                Registers.SubCpuCommunicationFlags = value;
                break;
            case >= 0x0020 and <= 0x002F:
                WriteCommBuffer(Registers.CommunicationStatuses, reg, value);
                break;
            case 0x0030:
            case 0x0031:
                Registers.TimerInterval = value;
                Registers.TimerCounter = value;
                break;
            case 0x0033:
                Registers.SubcodeInterruptEnabled = (value & 0x40) != 0;
                Registers.CdcInterruptEnabled = (value & 0x20) != 0;
                Registers.CddInterruptEnabled = (value & 0x10) != 0;
                Registers.TimerInterruptEnabled = (value & 0x08) != 0;
                Registers.SoftwareInterruptEnabled = (value & 0x04) != 0;
                Registers.GraphicsInterruptEnabled = (value & 0x02) != 0;
                if (!Registers.GraphicsInterruptEnabled)
                    Graphics.AcknowledgeInterrupt();
                break;
            case 0x0034:
            case 0x0035:
                Cdd.SetFaderVolume((ushort)((value << 8) | value));
                break;
            case 0x0037:
                Registers.CddHostClockOn = (value & 0x04) != 0;
                break;
            case >= 0x0042 and <= 0x004B:
                {
                    int rel = (int)((reg - 2) & 0x0F);
                    Registers.CddCommand[rel] = (byte)(value & 0x0F);
                    if (reg == 0x004B)
                        Cdd.SendCommand(Registers.CddCommand);
                    break;
                }
            case 0x004C:
            case 0x004D:
                FontRegisters.WriteColor(value);
                break;
            case 0x004E:
                FontRegisters.WriteFontBitsMsb(value);
                break;
            case 0x004F:
                FontRegisters.WriteFontBitsLsb(value);
                break;
            case >= 0x0058 and <= 0x0067:
                Graphics.WriteRegisterByte(reg, value);
                break;
        }
    }

    private void WriteSubRegisterWord(uint address, ushort value)
    {
        uint reg = address & SubRegisterAddressMask;
        switch (reg)
        {
            case 0x0000:
                WriteSubRegisterByte(reg, (byte)(value >> 8));
                WriteSubRegisterByte(reg | 1, (byte)value);
                break;
            case 0x0002:
                WriteSubRegisterByte(reg | 1, (byte)value);
                break;
            case 0x0004:
                WriteSubRegisterByte(reg, (byte)(value >> 8));
                WriteSubRegisterByte(reg | 1, (byte)value);
                break;
            case 0x0006:
                WriteSubRegisterByte(reg | 1, (byte)value);
                break;
            case 0x0008:
                Cdc.WriteHostData();
                break;
            case 0x000A:
                Cdc.SetDmaAddress((uint)value << 3);
                break;
            case 0x000C:
                Registers.StopwatchCounter = (ushort)(value & 0x0FFF);
                break;
            case 0x000E:
                Registers.SubCpuCommunicationFlags = (byte)value;
                break;
            case >= 0x0020 and <= 0x002F:
                WriteCommWord(Registers.CommunicationStatuses, reg, value);
                break;
            case 0x0030:
                Registers.TimerInterval = (byte)value;
                Registers.TimerCounter = (byte)value;
                break;
            case 0x0032:
                WriteSubRegisterByte(reg | 1, (byte)value);
                break;
            case 0x0034:
                Cdd.SetFaderVolume((ushort)((value >> 4) & 0x7FF));
                break;
            case 0x0036:
                WriteSubRegisterByte(reg | 1, (byte)value);
                break;
            case >= 0x0042 and <= 0x004B:
                {
                    int rel = (int)((reg - 2) & 0x0F);
                    Registers.CddCommand[rel] = (byte)((value >> 8) & 0x0F);
                    Registers.CddCommand[(rel + 1) & 0x0F] = (byte)(value & 0x0F);
                    if (reg == 0x004A)
                        Cdd.SendCommand(Registers.CddCommand);
                    break;
                }
            case 0x004C:
                FontRegisters.WriteColor((byte)value);
                break;
            case 0x004E:
                FontRegisters.WriteFontBits(value);
                break;
            case >= 0x0050 and <= 0x0057:
                // Read-only font data registers
                break;
            case >= 0x0058 and <= 0x0067:
                Graphics.WriteRegisterWord(reg, value);
                break;
        }
    }

    public byte ReadSubByte(uint address)
    {
        uint addr = address & SubBusAddressMask;
        switch (addr)
        {
            case <= 0x07FFFF:
                return _prgRam[addr];
            case <= 0x0DFFFF:
                return WordRam.SubCpuReadRam(addr);
            case <= 0x0EFFFF:
                if ((addr & 1) != 0)
                {
                    int backupAddr = (int)((addr & 0x3FFF) >> 1);
                    return _backupRam[backupAddr];
                }
                return 0x00;
            case <= 0x0F7FFF:
                return (addr & 1) != 0 ? Pcm.Read((addr & 0x3FFF) >> 1) : (byte)0x00;
            case <= 0x0FFFFF:
                return ReadSubRegisterByte(addr);
            default:
                return 0x00;
        }
    }

    public ushort ReadSubWord(uint address)
    {
        uint addr = address & SubBusAddressMask;
        switch (addr)
        {
            case <= 0x07FFFF:
                return (ushort)((_prgRam[addr] << 8) | _prgRam[addr + 1]);
            case <= 0x0DFFFF:
                {
                    byte msb = WordRam.SubCpuReadRam(addr);
                    byte lsb = WordRam.SubCpuReadRam(addr | 1);
                    return (ushort)((msb << 8) | lsb);
                }
            case <= 0x0EFFFF:
                {
                    int backupAddr = (int)((addr & 0x3FFF) >> 1);
                    return _backupRam[backupAddr];
                }
            case <= 0x0F7FFF:
                return Pcm.Read((addr & 0x3FFF) >> 1);
            case <= 0x0FFFFF:
                return ReadSubRegisterWord(addr);
            default:
                return 0x0000;
        }
    }

    public void WriteSubByte(uint address, byte value)
    {
        uint addr = address & SubBusAddressMask;
        switch (addr)
        {
            case <= 0x07FFFF:
                WritePrgRam(addr, value, ScdCpu.Sub);
                break;
            case <= 0x0DFFFF:
                WordRam.SubCpuWriteRam(addr, value);
                break;
            case <= 0x0EFFFF:
                if ((addr & 1) != 0)
                {
                    int backupAddr = (int)((addr & 0x3FFF) >> 1);
                    _backupRam[backupAddr] = value;
                    _backupRamDirty = true;
                }
                break;
            case <= 0x0F7FFF:
                if ((addr & 1) != 0)
                    Pcm.Write((addr & 0x3FFF) >> 1, value);
                break;
            case <= 0x0FFFFF:
                if ((addr & SubRegisterAddressMask) == 0x003)
                {
                    BufferSubWrite(BufferedWriteKind.Byte, addr, value);
                }
                else
                {
                    WriteSubRegisterByte(addr, value);
                }
                break;
        }
    }

    public void WriteSubWord(uint address, ushort value)
    {
        uint addr = address & SubBusAddressMask;
        switch (addr)
        {
            case <= 0x07FFFF:
                WritePrgRam(addr, (byte)(value >> 8), ScdCpu.Sub);
                WritePrgRam(addr + 1, (byte)value, ScdCpu.Sub);
                break;
            case <= 0x0DFFFF:
                WordRam.SubCpuWriteRam(addr, (byte)(value >> 8));
                WordRam.SubCpuWriteRam(addr | 1, (byte)value);
                break;
            case <= 0x0EFFFF:
                {
                    int backupAddr = (int)((addr & 0x3FFF) >> 1);
                    _backupRam[backupAddr] = (byte)value;
                    _backupRamDirty = true;
                    break;
                }
            case <= 0x0F7FFF:
                Pcm.Write((addr & 0x3FFF) >> 1, (byte)value);
                break;
            case <= 0x0FFFFF:
                if ((addr & SubRegisterAddressMask) == 0x002)
                {
                    BufferSubWrite(BufferedWriteKind.Word, addr, value);
                }
                else
                {
                    WriteSubRegisterWord(addr, value);
                }
                break;
        }
    }

    private void BufferSubWrite(BufferedWriteKind kind, uint address, ushort value)
    {
        if (_bufferedSubWriteCount >= _bufferedSubWrites.Length)
            return;
        _bufferedSubWrites[_bufferedSubWriteCount++] = new BufferedWrite(kind, address, value);
    }

    public byte GetSubInterruptLevel()
    {
        if (Registers.CdcInterruptEnabled && Cdc.InterruptPending)
            return 5;
        if (Registers.CddInterruptEnabled && Registers.CddHostClockOn && Cdd.InterruptPending)
            return 4;
        if (Registers.TimerInterruptEnabled && Registers.TimerInterruptPending)
            return 3;
        if (Registers.SoftwareInterruptEnabled && Registers.SoftwareInterruptPending)
            return 2;
        if (Registers.GraphicsInterruptEnabled && Graphics.InterruptPending)
            return 1;
        return 0;
    }

    public void AcknowledgeSubInterrupt(byte level)
    {
        switch (level)
        {
            case 1:
                Graphics.AcknowledgeInterrupt();
                break;
            case 2:
                Registers.SoftwareInterruptPending = false;
                break;
            case 3:
                Registers.TimerInterruptPending = false;
                break;
            case 4:
                Cdd.AcknowledgeInterrupt();
                break;
            case 5:
                Cdc.AcknowledgeInterrupt();
                break;
        }
    }

    public bool SubCpuHalt => Registers.SubCpuBusReq;
    public bool SubCpuReset => Registers.SubCpuReset;
}
