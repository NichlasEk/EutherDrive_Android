namespace EutherDrive.Core.SmsGg;

public enum SmsGgMapperType
{
    Sega = 0,
    Codemasters = 1
}

public interface ISmsGgMapper
{
    SmsGgMapperType MapperType { get; }
    byte Read(ushort address, byte[] rom, byte[] ram);
    void Write(ushort address, byte value, byte[] ram, ref bool ramDirty);
}

public static class SmsGgMapper
{
    private const int CodemastersChecksumAddress = 0x7FE6;
    private const int SegaHeaderStart = 0x7FF0;
    private const int SegaHeaderEnd = 0x7FFF;

    public static ISmsGgMapper DetectFromRom(byte[] rom)
    {
        if (rom.Length < 32 * 1024)
            return new SegaMapper();

        ushort expectedChecksum = (ushort)(rom[CodemastersChecksumAddress] | (rom[CodemastersChecksumAddress + 1] << 8));
        ushort checksum = 0;
        int evenLength = rom.Length & ~1;
        for (int address = 0; address < evenLength; address += 2)
        {
            if (address >= SegaHeaderStart && address <= SegaHeaderEnd)
                continue;

            ushort word = (ushort)(rom[address] | (rom[address + 1] << 8));
            checksum = (ushort)(checksum + word);
        }

        return checksum == expectedChecksum ? new CodemastersMapper() : new SegaMapper();
    }

    private static byte ReadWrapped(byte[] bytes, uint address)
    {
        if (bytes.Length == 0)
            return 0xFF;

        int wrappedAddress = (int)(address & (uint)(bytes.Length - 1));
        return bytes[wrappedAddress];
    }

    private static void WriteWrapped(byte[] bytes, uint address, byte value)
    {
        if (bytes.Length == 0)
            return;

        int wrappedAddress = (int)(address & (uint)(bytes.Length - 1));
        bytes[wrappedAddress] = value;
    }

    private static byte Read16KbBanked(byte[] bytes, ushort address, uint bank)
    {
        uint romAddress = (bank << 14) | (uint)(address & 0x3FFF);
        return ReadWrapped(bytes, romAddress);
    }

    private static void Write16KbBanked(byte[] bytes, ushort address, uint bank, byte value)
    {
        uint romAddress = (bank << 14) | (uint)(address & 0x3FFF);
        WriteWrapped(bytes, romAddress, value);
    }

    public sealed class SegaMapper : ISmsGgMapper
    {
        private readonly uint[] _romBanks = { 0, 1, 2 };
        private uint _ramBank;
        private bool _ramEnabled;

        public SmsGgMapperType MapperType => SmsGgMapperType.Sega;

        public byte Read(ushort address, byte[] rom, byte[] ram)
        {
            return address switch
            {
                <= 0x03FF => ReadWrapped(rom, address),
                <= 0x7FFF => Read16KbBanked(rom, address, _romBanks[address / 0x4000]),
                <= 0xBFFF => _ramEnabled
                    ? Read16KbBanked(ram, address, _ramBank)
                    : Read16KbBanked(rom, address, _romBanks[2]),
                _ => throw new InvalidOperationException($"Invalid cartridge address {address:X4}")
            };
        }

        public void Write(ushort address, byte value, byte[] ram, ref bool ramDirty)
        {
            switch (address)
            {
                case >= 0x8000 and <= 0xBFFF:
                    if (_ramEnabled)
                    {
                        Write16KbBanked(ram, address, _ramBank, value);
                        ramDirty = true;
                    }
                    break;
                case 0xFFFC:
                    _ramBank = (uint)((value >> 2) & 0x01);
                    _ramEnabled = (value & 0x08) != 0;
                    break;
                case >= 0xFFFD and <= 0xFFFF:
                    _romBanks[address - 0xFFFD] = value;
                    break;
            }
        }
    }

    public sealed class CodemastersMapper : ISmsGgMapper
    {
        private readonly uint[] _romBanks = { 0, 1, 2 };
        private bool _ramEnabled;

        public SmsGgMapperType MapperType => SmsGgMapperType.Codemasters;

        public byte Read(ushort address, byte[] rom, byte[] ram)
        {
            return address switch
            {
                <= 0x9FFF => Read16KbBanked(rom, address, _romBanks[address / 0x4000]),
                <= 0xBFFF => _ramEnabled
                    ? ReadWrapped(ram, (uint)(address & 0x1FFF))
                    : Read16KbBanked(rom, address, _romBanks[2]),
                _ => throw new InvalidOperationException($"Invalid cartridge address {address:X4}")
            };
        }

        public void Write(ushort address, byte value, byte[] ram, ref bool ramDirty)
        {
            switch (address)
            {
                case <= 0x3FFF:
                    _romBanks[0] = value;
                    break;
                case <= 0x7FFF:
                    _romBanks[1] = value;
                    _ramEnabled = (value & 0x80) != 0;
                    break;
                case <= 0xBFFF:
                    if (_ramEnabled && address >= 0xA000)
                    {
                        WriteWrapped(ram, (uint)(address & 0x1FFF), value);
                        ramDirty = true;
                    }
                    else
                    {
                        _romBanks[2] = value;
                    }
                    break;
            }
        }
    }
}
