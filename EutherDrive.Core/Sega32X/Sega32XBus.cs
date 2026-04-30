using EutherDrive.Core.Savestates;

namespace EutherDrive.Core.Sega32X;

internal sealed class Sega32XBus
{
    private static readonly bool TraceSh2CartridgeWrites =
        string.Equals(
            Environment.GetEnvironmentVariable("EUTHERDRIVE_S32X_TRACE_SH2_CART_WRITES"),
            "1",
            StringComparison.Ordinal);
    private static readonly bool TraceCramWrites =
        string.Equals(
            Environment.GetEnvironmentVariable("EUTHERDRIVE_S32X_TRACE_CRAM_WRITES"),
            "1",
            StringComparison.Ordinal);
    private static readonly int TraceCramWriteLimit = ParseTraceCramWriteLimit();
    public const uint M68kVectorsStart = 0x000000;
    public const uint M68kVectorsEnd = 0x0000FF;
    private const uint M68kHInterruptVectorStart = 0x000070;
    private const uint M68kHInterruptVectorEnd = 0x000073;
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
    public const uint M68kPwmRegistersStart = 0xA15130;
    public const uint M68kPwmRegistersEnd = 0xA1513F;
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
        Pwm = new Sega32XPwm();
        Sdram = new ushort[256 * 1024 / 2];
        Sh2FrameBuffer = new ushort[0x20000 / 2];
        Sh2Cram = new ushort[0x200 / 2];
    }

    public Sega32XSystemRegisters Registers { get; }
    public Sega32XVdp Vdp { get; }
    public Sega32XPwm Pwm { get; }
    public ushort[] Sdram { get; }
    public ushort[] Sh2FrameBuffer { get; }
    public ushort[] Sh2Cram { get; }
    private int _cramWriteTraceCount;

    public void SaveState(BinaryWriter writer) => StateBinarySerializer.WriteInto(writer, this);

    public void LoadState(BinaryReader reader)
    {
        StateBinarySerializer.ReadInto(reader, this);
        Registers.UpdateInterruptLevels();
    }

    public byte ReadM68kByte(uint address)
    {
        SyncSh2sIfTimingSensitiveRegisterAccessed(address & ~1u);

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

        if (address >= M68kPwmRegistersStart && address <= M68kPwmRegistersEnd)
        {
            ushort word = Pwm.ReadRegister(address & ~1u);
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
        SyncSh2sIfTimingSensitiveRegisterAccessed(aligned);
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

        if (aligned >= M68kPwmRegistersStart && aligned <= M68kPwmRegistersEnd)
            return Pwm.ReadRegister(aligned);

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
        uint aligned = address & ~1u;
        SyncSh2sIfTimingSensitiveRegisterAccessed(aligned);

        if (address >= M68kHInterruptVectorStart && address <= M68kHInterruptVectorEnd)
        {
            _m68kVectors[(int)address] = value;
            return;
        }

        bool isSystemRegister = address >= M68kSystemRegistersStart && address <= M68kSystemRegistersEnd;
        bool isPwmRegister = address >= M68kPwmRegistersStart && address <= M68kPwmRegistersEnd;
        bool isVdpRegister = address >= M68kVdpRegistersStart && address <= M68kVdpRegistersEnd;
        if (isSystemRegister || isPwmRegister || isVdpRegister)
        {
            ushort word = isVdpRegister ? Vdp.ReadRegister(aligned)
                : isPwmRegister ? Pwm.ReadRegister(aligned)
                : Registers.M68kRead(aligned);
            word = (address & 1) == 0
                ? (ushort)((word & 0x00FF) | (value << 8))
                : (ushort)((word & 0xFF00) | value);
            if (isVdpRegister)
            {
                if (Registers.VdpAccess != Sega32XAccess.M68k)
                    return;
                Vdp.WriteRegister(aligned, word);
            }
            else if (isPwmRegister)
                Pwm.M68kWriteRegister(aligned, word);
            else
                Registers.M68kWrite(aligned, word);

            if (aligned == 0xA15102 || IsM68kDreqRegister(aligned))
                _syncSh2sForM68kCommAccess?.Invoke();
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
            {
                TraceM68kCramWrite("write8-denied", address, value, 0);
                return;
            }
            ushort current = Vdp.ReadCramWord(address - M68kCramStart);
            ushort merged = (address & 1) == 0
                ? (ushort)((current & 0x00FF) | (value << 8))
                : (ushort)((current & 0xFF00) | value);
            Vdp.WriteCramWord(address - M68kCramStart, merged);
            TraceM68kCramWrite("write8", address, value, merged);
            return;
        }

        if (address >= M68kCartridgeStart && address <= M68kCartridgeEnd)
        {
            WriteCartridgeByte(address, value);
            return;
        }

        if (address >= M68kFirstCartBankStart && address <= M68kFirstCartBankEnd)
        {
            WriteCartridgeByte(address & 0x7FFFF, value);
            return;
        }

        if (address >= M68kMappableCartBankStart && address <= M68kMappableCartBankEnd)
        {
            uint romAddress = (uint)(Registers.M68kRomBank << 20) | (address & 0xFFFFF);
            WriteCartridgeByte(romAddress, value);
        }
    }

    public void WriteM68kWord(uint address, ushort value)
    {
        uint aligned = address & ~1u;
        SyncSh2sIfTimingSensitiveRegisterAccessed(aligned);

        if (aligned >= M68kHInterruptVectorStart && aligned <= M68kHInterruptVectorEnd)
        {
            _m68kVectors[(int)aligned] = (byte)(value >> 8);
            if (aligned + 1 <= M68kHInterruptVectorEnd)
                _m68kVectors[(int)(aligned + 1)] = (byte)value;
            return;
        }

        if ((aligned >= M68kSystemRegistersStart && aligned <= M68kSystemRegistersEnd)
            || (aligned >= M68kVdpRegistersStart && aligned <= M68kVdpRegistersEnd))
        {
            if (aligned >= M68kVdpRegistersStart && aligned <= M68kVdpRegistersEnd)
            {
                if (Registers.VdpAccess != Sega32XAccess.M68k)
                    return;
                Vdp.WriteRegister(aligned, value);
            }
            else
                Registers.M68kWrite(aligned, value);

            if (aligned == 0xA15102 || IsM68kDreqRegister(aligned))
                _syncSh2sForM68kCommAccess?.Invoke();
            return;
        }

        if (aligned >= M68kPwmRegistersStart && aligned <= M68kPwmRegistersEnd)
        {
            Pwm.M68kWriteRegister(aligned, value);
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
            {
                TraceM68kCramWrite("write16-denied", aligned, value, 0);
                return;
            }
            Vdp.WriteCramWord(address - M68kCramStart, value);
            TraceM68kCramWrite("write16", aligned, value, value);
            return;
        }

        if (aligned >= M68kCartridgeStart && aligned <= M68kCartridgeEnd)
        {
            WriteCartridgeWord(aligned, value);
            return;
        }

        if (aligned >= M68kFirstCartBankStart && aligned <= M68kFirstCartBankEnd)
        {
            WriteCartridgeWord(aligned & 0x7FFFF, value);
            return;
        }

        if (aligned >= M68kMappableCartBankStart && aligned <= M68kMappableCartBankEnd)
        {
            uint romAddress = (uint)(Registers.M68kRomBank << 20) | (aligned & 0xFFFFF);
            WriteCartridgeWord(romAddress, value);
        }
    }

    private void TraceM68kCramWrite(string op, uint address, uint value, ushort stored)
    {
        if (!TraceCramWrites || _cramWriteTraceCount >= TraceCramWriteLimit)
            return;

        _cramWriteTraceCount++;
        Console.WriteLine(
            $"[S32X-CRAM-M68K] op={op} addr=0x{address:X6} offset=0x{address - M68kCramStart:X3} " +
            $"value=0x{value:X4} stored=0x{stored:X4} fm={(Registers.VdpAccess == Sega32XAccess.Sh2 ? 1 : 0)}");
    }

    private static int ParseTraceCramWriteLimit()
    {
        string? raw = Environment.GetEnvironmentVariable("EUTHERDRIVE_S32X_TRACE_CRAM_WRITES_MAX");
        return int.TryParse(raw, out int value) && value > 0 ? value : 512;
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

    private void WriteCartridgeByte(uint romAddress, byte value)
    {
        if (_cartridgeRom.Length == 0)
            return;

        uint index = romAddress % (uint)_cartridgeRom.Length;
        _cartridgeRom[index] = value;
    }

    private void WriteCartridgeWord(uint romAddress, ushort value)
    {
        WriteCartridgeByte(romAddress, (byte)(value >> 8));
        WriteCartridgeByte(romAddress + 1, (byte)value);
    }

    private void SyncSh2sIfTimingSensitiveRegisterAccessed(uint alignedAddress)
    {
        if (IsTimingSensitiveM68kSystemRegister(alignedAddress))
            _syncSh2sForM68kCommAccess?.Invoke();
    }

    private static bool IsTimingSensitiveM68kSystemRegister(uint alignedAddress)
    {
        if (alignedAddress < M68kSystemRegistersStart || alignedAddress > M68kSystemRegistersEnd)
            return false;

        return alignedAddress == 0xA15102
            || IsM68kDreqRegister(alignedAddress)
            || (alignedAddress >= 0xA15120 && alignedAddress <= 0xA1512F);
    }

    private static bool IsM68kDreqRegister(uint alignedAddress) =>
        alignedAddress is >= 0xA15106 and <= 0xA15112;

    public byte ReadSh2CartridgeByte(uint romAddress) => ReadCartridgeByte(romAddress);

    public ushort ReadSh2CartridgeWord(uint romAddress) => ReadCartridgeWord(romAddress);

    public void WriteSh2CartridgeByte(uint romAddress, byte value)
    {
        WriteCartridgeByte(romAddress & 0x003FFFFF, value);
        if (TraceSh2CartridgeWrites)
        {
            Console.WriteLine(
                $"[S32X-CART-WRITE] addr=0x{(romAddress & 0x003FFFFF):X6} value=0x{value:X2}");
        }
    }

    public void WriteSh2CartridgeWord(uint romAddress, ushort value)
    {
        WriteCartridgeWord(romAddress & 0x003FFFFE, value);
        if (TraceSh2CartridgeWrites)
        {
            Console.WriteLine(
                $"[S32X-CART-WRITE] addr=0x{(romAddress & 0x003FFFFE):X6} value=0x{value:X4}");
        }
    }

    private static ushort ReadBigEndianWord(byte[] buffer, int offset)
    {
        if ((uint)(offset + 1) >= buffer.Length)
            return 0xFFFF;
        return (ushort)((buffer[offset] << 8) | buffer[offset + 1]);
    }
}
