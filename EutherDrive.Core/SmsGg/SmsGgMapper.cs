namespace EutherDrive.Core.SmsGg;

public enum SmsGgMapperType
{
    Sega = 0,
    Codemasters = 1,
    CosmicSpacehead = 2
}

public interface ISmsGgMapper
{
    SmsGgMapperType MapperType { get; }
    byte Read(ushort address, byte[] rom, byte[] ram);
    void Write(ushort address, byte value, byte[] ram, ref bool ramDirty);
}

public interface ICodemastersRamSupport
{
    void SetRamSupported(bool supported);
}

public static class SmsGgMapper
{
    private const uint CosmicSpaceheadCrc32 = 0x6CAA625Bu;
    private const int CodemastersChecksumAddress = 0x7FE6;
    private const int SegaHeaderStart = 0x7FF0;
    private const int SegaHeaderEnd = 0x7FFF;
    private static readonly bool TraceCodemasters =
        string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_GG_CODEMASTERS"), "1", StringComparison.Ordinal);
    private static readonly int TraceCodemastersLimit =
        ParseTraceLimit(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_GG_CODEMASTERS_LIMIT"), 256);

    public static ISmsGgMapper DetectFromRom(byte[] rom, uint crc32)
    {
        if (rom.Length < 32 * 1024)
            return new SegaMapper();

        if (rom.Length <= CodemastersChecksumAddress + 1)
            return new SegaMapper();

        if (crc32 == CosmicSpaceheadCrc32)
            return new CodemastersMapper(cosmicSpacehead: true);

        bool headerFound = HasSegaHeader(rom);
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

        if (checksum == expectedChecksum)
            return new CodemastersMapper();

        if (!headerFound && LooksLikeCodemastersMapper(rom))
            return new CodemastersMapper();

        return new SegaMapper();
    }

    private static bool HasSegaHeader(byte[] rom)
    {
        ReadOnlySpan<byte> tmr = "TMR SEGA"u8;
        ReadOnlySpan<int> candidates = stackalloc[] { 0x1FF0, 0x3FF0, 0x7FF0 };
        foreach (int offset in candidates)
        {
            if (offset + tmr.Length > rom.Length)
                continue;

            if (rom.AsSpan(offset, tmr.Length).SequenceEqual(tmr))
                return true;
        }

        return false;
    }

    private static bool LooksLikeCodemastersMapper(byte[] rom)
    {
        int writesToA000 = 0;
        int writesTo0000 = 0;
        int writesTo4000 = 0;
        int writesTo8000 = 0;
        for (int i = 0; i + 2 < rom.Length; i++)
        {
            if (rom[i] != 0x32)
                continue;

            ushort address = (ushort)(rom[i + 1] | (rom[i + 2] << 8));
            if (address == 0xA000)
                writesToA000++;
            else if (address == 0x0000)
                writesTo0000++;
            else if (address == 0x4000)
                writesTo4000++;
            else if (address == 0x8000)
                writesTo8000++;
        }

        bool hasCodemastersTriplet =
            writesTo0000 >= 2 && writesTo4000 >= 2 && writesTo8000 >= 2;
        return hasCodemastersTriplet || (writesToA000 >= 8 && writesTo8000 >= 2);
    }

    private static byte ReadWrapped(byte[] bytes, uint address)
    {
        if (bytes.Length == 0)
            return 0xFF;

        int wrappedAddress = (int)(address % (uint)bytes.Length);
        return bytes[wrappedAddress];
    }

    private static void WriteWrapped(byte[] bytes, uint address, byte value)
    {
        if (bytes.Length == 0)
            return;

        int wrappedAddress = (int)(address % (uint)bytes.Length);
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

    private static int ParseTraceLimit(string? rawValue, int defaultValue)
    {
        return int.TryParse(rawValue, out int parsed) && parsed > 0 ? parsed : defaultValue;
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

    public sealed class CodemastersMapper : ISmsGgMapper, ICodemastersRamSupport
    {
        // Codemasters carts power on with slots 0/1/2 mapped as 0,1,0.
        private readonly uint[] _romBanks = { 0, 1, 0 };
        private bool _ramEnabled;
        [NonSerialized]
        private bool _ramSupported;
        [NonSerialized]
        private int _traceRemaining = TraceCodemastersLimit;
        [NonSerialized]
        private readonly bool _cosmicSpacehead;

        public CodemastersMapper(bool cosmicSpacehead = false)
        {
            _cosmicSpacehead = cosmicSpacehead;
        }

        public SmsGgMapperType MapperType => _cosmicSpacehead ? SmsGgMapperType.CosmicSpacehead : SmsGgMapperType.Codemasters;

        public void SetRamSupported(bool supported)
        {
            _ramSupported = supported;
            if (!supported)
                _ramEnabled = false;
        }

        public byte Read(ushort address, byte[] rom, byte[] ram)
        {
            uint bankCount = (uint)Math.Max(1, (rom.Length + 0x3FFF) / 0x4000);
            return address switch
                {
                <= 0x3FFF => Read16KbBanked(rom, address, _romBanks[0] % bankCount),
                <= 0x7FFF => Read16KbBanked(rom, address, _romBanks[1] % bankCount),
                <= 0xBFFF => _ramSupported && _ramEnabled && address >= 0xA000
                    ? ReadWrapped(ram, (uint)(address & 0x1FFF))
                    : Read16KbBanked(rom, address, _romBanks[2] % bankCount),
                _ => throw new InvalidOperationException($"Invalid cartridge address {address:X4}")
            };
        }

        public void Write(ushort address, byte value, byte[] ram, ref bool ramDirty)
        {
            uint oldBank0 = _romBanks[0];
            uint oldBank1 = _romBanks[1];
            uint oldBank2 = _romBanks[2];
            bool oldRamEnabled = _ramEnabled;

            switch (address)
            {
                case <= 0x3FFF:
                    _romBanks[0] = (uint)(value & 0x7F);
                    break;
                case <= 0x7FFF:
                    _romBanks[1] = (uint)(value & 0x7F);
                    _ramEnabled = _ramSupported && (value & 0x80) != 0;
                    break;
                case <= 0xBFFF:
                    if (_ramSupported && _ramEnabled && address >= 0xA000)
                    {
                        WriteWrapped(ram, (uint)(address & 0x1FFF), value);
                        ramDirty = true;
                    }
                    else
                    {
                        _romBanks[2] = (uint)(value & 0x7F);
                    }
                    break;
            }

            if (TraceCodemasters && _traceRemaining > 0 &&
                (oldBank0 != _romBanks[0] || oldBank1 != _romBanks[1] || oldBank2 != _romBanks[2] || oldRamEnabled != _ramEnabled))
            {
                _traceRemaining--;
                Console.WriteLine(
                    $"{(_cosmicSpacehead ? "[GG-COSMIC]" : "[GG-CODEM]")} addr=0x{address:X4} val=0x{value:X2} " +
                    $"b0:{oldBank0:X2}->{_romBanks[0]:X2} b1:{oldBank1:X2}->{_romBanks[1]:X2} b2:{oldBank2:X2}->{_romBanks[2]:X2} " +
                    $"ram:{(oldRamEnabled ? 1 : 0)}->{(_ramEnabled ? 1 : 0)} supported:{(_ramSupported ? 1 : 0)}");
            }
        }
    }
}
