namespace EutherDrive.Core.Sega32X;

internal sealed class Sega32XBus
{
    public const uint M68kVectorsStart = 0x000000;
    public const uint M68kVectorsEnd = 0x0000FF;
    public const uint M68kCartridgeStart = 0x000100;
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

    private readonly byte[] _cartridgeRom;
    private readonly byte[] _m68kVectors;

    public Sega32XBus(byte[] cartridgeRom, byte[] m68kVectors, Sega32XSystemRegisters registers)
    {
        _cartridgeRom = cartridgeRom;
        _m68kVectors = m68kVectors;
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

    public byte ReadM68kByte(uint address)
    {
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
            ushort word = Vdp.ReadRegister(address & ~1u);
            return (address & 1) == 0 ? (byte)(word >> 8) : (byte)word;
        }

        if (address >= M68kFrameBufferStart && address <= M68kOverwriteImageEnd)
        {
            ushort word = Vdp.ReadFrameBufferWord(address - M68kFrameBufferStart);
            return (address & 1) == 0 ? (byte)(word >> 8) : (byte)word;
        }

        if (address >= M68kCramStart && address <= M68kCramEnd)
        {
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
            return Vdp.ReadRegister(aligned);

        if (aligned >= M68kFrameBufferStart && aligned <= M68kOverwriteImageEnd)
            return Vdp.ReadFrameBufferWord(aligned - M68kFrameBufferStart);

        if (aligned >= M68kCramStart && aligned <= M68kCramEnd)
            return Vdp.ReadCramWord(aligned - M68kCramStart);

        if (aligned >= M68k32XIdStart && aligned <= M68k32XIdEnd)
            return (ushort)((MarsId[0] << 8) | MarsId[1]);

        return 0xFFFF;
    }

    public void WriteM68kByte(uint address, byte value)
    {
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
                Vdp.WriteRegister(aligned, word);
            else
                Registers.M68kWrite(aligned, word);
            return;
        }

        if (address >= M68kFrameBufferStart && address <= M68kOverwriteImageEnd)
        {
            ushort current = Vdp.ReadFrameBufferWord(address - M68kFrameBufferStart);
            ushort merged = (address & 1) == 0
                ? (ushort)((current & 0x00FF) | (value << 8))
                : (ushort)((current & 0xFF00) | value);
            Vdp.WriteFrameBufferWord(address - M68kFrameBufferStart, merged);
            return;
        }

        if (address >= M68kCramStart && address <= M68kCramEnd)
        {
            ushort current = Vdp.ReadCramWord(address - M68kCramStart);
            ushort merged = (address & 1) == 0
                ? (ushort)((current & 0x00FF) | (value << 8))
                : (ushort)((current & 0xFF00) | value);
            Vdp.WriteCramWord(address - M68kCramStart, merged);
        }
    }

    public void WriteM68kWord(uint address, ushort value)
    {
        if ((address >= M68kSystemRegistersStart && address <= M68kSystemRegistersEnd)
            || (address >= M68kVdpRegistersStart && address <= M68kVdpRegistersEnd))
        {
            if (address >= M68kVdpRegistersStart && address <= M68kVdpRegistersEnd)
                Vdp.WriteRegister(address & ~1u, value);
            else
                Registers.M68kWrite(address & ~1u, value);
            return;
        }

        if (address >= M68kFrameBufferStart && address <= M68kOverwriteImageEnd)
        {
            if (address < M68kOverwriteImageStart)
                Vdp.WriteFrameBufferWord(address - M68kFrameBufferStart, value);
            else
                Vdp.OverwriteFrameBufferWord(address - M68kFrameBufferStart, value);
            return;
        }

        if (address >= M68kCramStart && address <= M68kCramEnd)
            Vdp.WriteCramWord(address - M68kCramStart, value);
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
