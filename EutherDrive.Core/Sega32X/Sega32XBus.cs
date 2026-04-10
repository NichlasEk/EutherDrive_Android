using EutherDrive.Core.Savestates;

namespace EutherDrive.Core.Sega32X;

internal sealed class Sega32XBus
{
    public const uint M68kVectorsStart = 0x000000;
    public const uint M68kVectorsEnd = 0x0000FF;
    public const uint M68kCartridgeStart = 0x000000;
    public const uint M68kCartridgeEnd = 0x3FFFFF;
    public const uint M68kFrameBufferStart = 0x840000;
    public const uint M68kFrameBufferEnd = 0x85FFFF;
    public const uint M68kOverwriteImageStart = 0x860000;
    public const uint M68kOverwriteImageEnd = 0x87FFFF;
    public const uint M68kFirstCartBankStart = 0x880000;
    public const uint M68kFirstCartBankEnd = 0x8FFFFF;
    public const uint M68kMappableCartBankStart = 0x900000;
    public const uint M68kMappableCartBankEnd = 0x9FFFFF;
    public const uint M68k32XIdStart = 0xA130EC;
    public const uint M68k32XIdEnd = 0xA130EF;
    public const uint M68kSystemRegistersStart = 0xA15100;
    public const uint M68kSystemRegistersEnd = 0xA1512F;
    public const uint M68kVdpRegistersStart = 0xA15180;
    public const uint M68kVdpRegistersEnd = 0xA1518F;
    public const uint M68kCramStart = 0xA15200;
    public const uint M68kCramEnd = 0xA153FF;

    private static ReadOnlySpan<byte> MarsId => "MARS"u8;

    [NonSerialized] private readonly byte[] _cartridgeRom;
    [NonSerialized] private readonly byte[] _m68kVectors;
    [NonSerialized] private readonly Action? _syncSh2sForM68kCommAccess;

    public Sega32XBus(byte[] cartridgeRom, byte[] m68kVectors, Sega32XSystemRegisters registers, Action? syncSh2sForM68kCommAccess = null)
    {
        _cartridgeRom = cartridgeRom;
        _m68kVectors = m68kVectors;
        _syncSh2sForM68kCommAccess = syncSh2sForM68kCommAccess;
        Registers = registers;
        Vdp = new Sega32XVdp();
        Sdram = new ushort[256 * 1024 / 2];
        Sh2FrameBuffer = new ushort[0x20000 / 2];
        Sh2Cram = new ushort[0x200 / 2];
        Sh2PwmRegisters = new ushort[0x10 / 2];
    }

    public Sega32XSystemRegisters Registers { get; }
    public Sega32XVdp Vdp { get; }
    public ushort[] Sdram { get; }
    public ushort[] Sh2FrameBuffer { get; }
    public ushort[] Sh2Cram { get; }
    public ushort[] Sh2PwmRegisters { get; }

    public void SaveState(BinaryWriter writer) => StateBinarySerializer.WriteInto(writer, this);

    public void LoadState(BinaryReader reader)
    {
        StateBinarySerializer.ReadInto(reader, this);
        Registers.UpdateInterruptLevels();
    }

    public byte ReadM68kByte(uint address)
    {
        SyncSh2sIfCommPortAccessed(address & ~1u);

        if (address >= M68kVectorsStart && address <= M68kVectorsEnd)
        {
            if (Registers.AdapterEnabled)
                return _m68kVectors[address];
            return ReadCartridgeByte(address);
        }

        if (address >= M68kCartridgeStart && address <= M68kCartridgeEnd)
            return ReadCartridgeByte(address);

        if (address >= M68kFirstCartBankStart && address <= M68kFirstCartBankEnd)
            return ReadCartridgeByte(address & 0x7FFFF);

        if (address >= M68kMappableCartBankStart && address <= M68kMappableCartBankEnd)
        {
            uint romAddress = (uint)(Registers.M68kRomBank << 20) | (address & 0xFFFFF);
            return ReadCartridgeByte(romAddress);
        }

        if (address >= M68kSystemRegistersStart && address <= M68kSystemRegistersEnd)
        {
            ushort word = Registers.M68kRead(address & ~1u);
            return (address & 1) == 0 ? (byte)(word >> 8) : (byte)word;
        }

        if (address >= M68kVdpRegistersStart && address <= M68kVdpRegistersEnd)
        {
            if (Registers.VdpAccess != Sega32XAccess.M68k)
                return 0xFF;
            ushort word = Vdp.ReadRegister(address & ~1u);
            return (address & 1) == 0 ? (byte)(word >> 8) : (byte)word;
        }

        if (address >= M68kFrameBufferStart && address <= M68kOverwriteImageEnd)
        {
            if (Registers.VdpAccess != Sega32XAccess.M68k)
                return 0xFF;
            ushort word = Vdp.ReadFrameBufferWord(address - M68kFrameBufferStart);
            return (address & 1) == 0 ? (byte)(word >> 8) : (byte)word;
        }

        if (address >= M68kCramStart && address <= M68kCramEnd)
        {
            if (Registers.VdpAccess != Sega32XAccess.M68k)
                return 0xFF;
            ushort word = Vdp.ReadCramWord(address - M68kCramStart);
            return (address & 1) == 0 ? (byte)(word >> 8) : (byte)word;
        }

        if (address >= M68k32XIdStart && address <= M68k32XIdEnd)
            return MarsId[(int)(address & 3)];

        return 0xFF;
    }

    public ushort ReadM68kWord(uint address)
    {
        uint aligned = address & ~1u;
        SyncSh2sIfCommPortAccessed(aligned);
        if (aligned >= M68kVectorsStart && aligned <= M68kVectorsEnd)
        {
            if (Registers.AdapterEnabled)
                return ReadBigEndianWord(_m68kVectors, (int)aligned);
            return ReadCartridgeWord(aligned);
        }

        if (aligned >= M68kCartridgeStart && aligned <= M68kCartridgeEnd)
            return ReadCartridgeWord(aligned);

        if (aligned >= M68kFirstCartBankStart && aligned <= M68kFirstCartBankEnd)
            return ReadCartridgeWord(aligned & 0x7FFFF);

        if (aligned >= M68kMappableCartBankStart && aligned <= M68kMappableCartBankEnd)
        {
            uint romAddress = (uint)(Registers.M68kRomBank << 20) | (aligned & 0xFFFFF);
            return ReadCartridgeWord(romAddress);
        }

        if (aligned >= M68kSystemRegistersStart && aligned <= M68kSystemRegistersEnd)
            return Registers.M68kRead(aligned);

        if (aligned >= M68kVdpRegistersStart && aligned <= M68kVdpRegistersEnd)
        {
            if (Registers.VdpAccess != Sega32XAccess.M68k)
                return 0xFFFF;
            return Vdp.ReadRegister(aligned);
        }

        if (aligned >= M68kFrameBufferStart && aligned <= M68kOverwriteImageEnd)
        {
            if (Registers.VdpAccess != Sega32XAccess.M68k)
                return 0xFFFF;
            return Vdp.ReadFrameBufferWord(aligned - M68kFrameBufferStart);
        }

        if (aligned >= M68kCramStart && aligned <= M68kCramEnd)
        {
            if (Registers.VdpAccess != Sega32XAccess.M68k)
                return 0xFFFF;
            return Vdp.ReadCramWord(aligned - M68kCramStart);
        }

        if (aligned >= M68k32XIdStart && aligned <= M68k32XIdEnd)
        {
            int offset = (int)(aligned - M68k32XIdStart) & 0x2;
            return (ushort)((MarsId[offset] << 8) | MarsId[offset + 1]);
        }

        return 0xFFFF;
    }

    public void WriteM68kByte(uint address, byte value)
    {
        SyncSh2sIfCommPortAccessed(address & ~1u);

        bool isSystemRegister = address >= M68kSystemRegistersStart && address <= M68kSystemRegistersEnd;
        bool isVdpRegister = address >= M68kVdpRegistersStart && address <= M68kVdpRegistersEnd;
        uint aligned = address & ~1u;
        if (isSystemRegister || isVdpRegister)
        {
            ushort word = isVdpRegister ? Vdp.ReadRegister(aligned) : Registers.M68kRead(aligned);
            word = (address & 1) == 0
                ? (ushort)((word & 0x00FF) | (value << 8))
                : (ushort)((word & 0xFF00) | value);
            if (isVdpRegister)
            {
                if (Registers.VdpAccess != Sega32XAccess.M68k)
                    return;
                Vdp.WriteRegister(aligned, word);
            }
            else
                Registers.M68kWrite(aligned, word);
            return;
        }

        if (address >= M68kFrameBufferStart && address <= M68kOverwriteImageEnd)
        {
            if (Registers.VdpAccess != Sega32XAccess.M68k)
                return;
            Vdp.WriteFrameBufferByte(address - M68kFrameBufferStart, value, address >= M68kOverwriteImageStart);
            return;
        }

        if (address >= M68kCramStart && address <= M68kCramEnd)
        {
            if (Registers.VdpAccess != Sega32XAccess.M68k)
                return;
            ushort current = Vdp.ReadCramWord(address - M68kCramStart);
            ushort merged = (address & 1) == 0
                ? (ushort)((current & 0x00FF) | (value << 8))
                : (ushort)((current & 0xFF00) | value);
            Vdp.WriteCramWord(address - M68kCramStart, merged);
        }
    }

    public void WriteM68kWord(uint address, ushort value)
    {
        SyncSh2sIfCommPortAccessed(address & ~1u);

        if ((address >= M68kSystemRegistersStart && address <= M68kSystemRegistersEnd)
            || (address >= M68kVdpRegistersStart && address <= M68kVdpRegistersEnd))
        {
            if (address >= M68kVdpRegistersStart && address <= M68kVdpRegistersEnd)
            {
                if (Registers.VdpAccess != Sega32XAccess.M68k)
                    return;
                Vdp.WriteRegister(address & ~1u, value);
            }
            else
                Registers.M68kWrite(address & ~1u, value);
            return;
        }

        if (address >= M68kFrameBufferStart && address <= M68kOverwriteImageEnd)
        {
            if (Registers.VdpAccess != Sega32XAccess.M68k)
                return;
            if (address < M68kOverwriteImageStart)
                Vdp.WriteFrameBufferWord(address - M68kFrameBufferStart, value);
            else
                Vdp.OverwriteFrameBufferWord(address - M68kFrameBufferStart, value);
            return;
        }

        if (address >= M68kCramStart && address <= M68kCramEnd)
        {
            if (Registers.VdpAccess != Sega32XAccess.M68k)
                return;
            Vdp.WriteCramWord(address - M68kCramStart, value);
        }
    }

    private byte ReadCartridgeByte(uint romAddress)
    {
        if (_cartridgeRom.Length == 0)
            return 0xFF;

        uint index = romAddress % (uint)_cartridgeRom.Length;
        return _cartridgeRom[index];
    }

    private ushort ReadCartridgeWord(uint romAddress)
    {
        byte msb = ReadCartridgeByte(romAddress);
        byte lsb = ReadCartridgeByte(romAddress + 1);
        return (ushort)((msb << 8) | lsb);
    }

    private void SyncSh2sIfCommPortAccessed(uint alignedAddress)
    {
        if (alignedAddress < M68kSystemRegistersStart || alignedAddress > M68kSystemRegistersEnd)
            return;
        if (alignedAddress < 0xA15120 || alignedAddress > 0xA1512F)
            return;

        _syncSh2sForM68kCommAccess?.Invoke();
    }

    public byte ReadSh2CartridgeByte(uint romAddress) => ReadCartridgeByte(romAddress);

    public ushort ReadSh2CartridgeWord(uint romAddress) => ReadCartridgeWord(romAddress);

    public void WriteSh2CartridgeByte(uint romAddress, byte value)
    {
        if (_cartridgeRom.Length == 0)
            return;

        uint index = romAddress % (uint)_cartridgeRom.Length;
        _cartridgeRom[index] = value;
    }

    public void WriteSh2CartridgeWord(uint romAddress, ushort value)
    {
        WriteSh2CartridgeByte(romAddress, (byte)(value >> 8));
        WriteSh2CartridgeByte(romAddress + 1, (byte)value);
    }

    private static ushort ReadBigEndianWord(byte[] buffer, int offset)
    {
        if ((uint)(offset + 1) >= buffer.Length)
            return 0xFFFF;
        return (ushort)((buffer[offset] << 8) | buffer[offset + 1]);
    }
}
