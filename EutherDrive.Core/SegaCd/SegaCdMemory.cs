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

    public SegaCdRegisters Registers { get; } = new();
    public WordRam WordRam { get; } = new();
    public SegaCdCdcStub Cdc { get; } = new();
    public SegaCdCddStub Cdd { get; } = new();
    public SegaCdPcmStub Pcm { get; } = new();
    public SegaCdGraphicsStub Graphics { get; } = new();

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
}
