namespace EutherDrive.Core.SmsGg;

public sealed class SmsGgMemory
{
    private const int SystemRamSize = 8 * 1024;
    private const int CartridgeRamSize = 32 * 1024;

    private readonly Cartridge _cartridge;
    private readonly byte[]? _biosRom;
    private readonly uint[] _biosRomBanks = { 0, 1, 2 };
    private readonly byte[] _ram = new byte[SystemRamSize];
    private readonly AudioControl _audioControl = new();
    private readonly SmsGgHardware _hardware;

    public SmsGgMemory(byte[] rom, byte[]? biosRom, byte[]? initialCartridgeRam, SmsGgHardware hardware)
    {
        _hardware = hardware;
        _cartridge = new Cartridge(rom, initialCartridgeRam);
        _biosRom = biosRom is { Length: > 0 } ? (byte[])biosRom.Clone() : null;
        MemoryControl = new SmsGgMemoryControl(_biosRom is not null);
        GameGearRegisters = new SmsGgGameGearRegisters();

        if (_biosRom is null)
            _ram[0] = 0xAB;
    }

    public SmsGgMemoryControl MemoryControl { get; }
    public SmsGgGameGearRegisters GameGearRegisters { get; }
    public SmsGgMapperType MapperType => _cartridge.Mapper.MapperType;
    public bool CartridgeHasBattery => _cartridge.HasBattery;
    public bool CartridgeRamDirty => _cartridge.RamDirty;

    public byte Read(ushort address)
    {
        if (address <= 0xBFFF)
        {
            if (_hardware == SmsGgHardware.GameGear)
            {
                if (MemoryControl.BiosEnabled && address <= 0x03FF)
                    return _biosRom is null ? (byte)0xFF : _biosRom[address & (_biosRom.Length - 1)];

                return _cartridge.Read(address);
            }

            if (MemoryControl.CartridgeEnabled)
            {
                byte cartridgeByte = _cartridge.Read(address);
                return MemoryControl.BiosEnabled ? (byte)(cartridgeByte & ReadSmsBios(address)) : cartridgeByte;
            }

            if (MemoryControl.BiosEnabled)
                return ReadSmsBios(address);

            return 0xFF;
        }

        return _ram[address & 0x1FFF];
    }

    public void Write(ushort address, byte value)
    {
        if (address >= 0xC000)
            _ram[address & 0x1FFF] = value;

        if (MemoryControl.BiosEnabled && address >= 0xFFFD)
            _biosRomBanks[address - 0xFFFD] = value;

        if (MemoryControl.CartridgeEnabled)
            _cartridge.Write(address, value);
    }

    public void Reset()
    {
        byte[] rom = _cartridge.TakeRom();
        byte[] ram = _cartridge.TakeRam();
        byte[]? biosRom = _biosRom is null ? null : (byte[])_biosRom.Clone();
        Array.Clear(_ram);
        if (biosRom is null)
            _ram[0] = 0xAB;

        MemoryControl.Reset(biosRom is not null);
        GameGearRegisters.Reset();
        _audioControl.Reset();
        Array.Clear(_biosRomBanks);
        _biosRomBanks[1] = 1;
        _biosRomBanks[2] = 2;
        _cartridge.Restore(rom, ram);
    }

    public bool FmEnabled => _audioControl.FmEnabled;
    public bool PsgEnabled => _audioControl.PsgEnabled;

    public byte ReadAudioControl()
    {
        return (_audioControl.FmEnabled, _audioControl.PsgEnabled) switch
        {
            (false, true) => 0x00,
            (true, false) => 0x01,
            (false, false) => 0x02,
            (true, true) => 0x03
        };
    }

    public void WriteAudioControl(byte value)
    {
        int controlBits = value & 0x03;
        _audioControl.FmEnabled = (controlBits & 0x01) != 0;
        _audioControl.PsgEnabled = controlBits is 0 or 3;
    }

    public SmsGgRegion GuessCartridgeRegion()
    {
        int[] headerLocations = { 0x7FF0, 0x3FF0, 0x1FF0 };
        byte[] rom = _cartridge.Rom;
        foreach (int headerStart in headerLocations)
        {
            if (rom.Length < headerStart + 16)
                continue;

            if (!rom.AsSpan(headerStart, 8).SequenceEqual("TMR SEGA"u8))
                continue;

            int regionCode = rom[headerStart + 15] >> 4;
            if (regionCode is 3 or 5)
                return SmsGgRegion.Domestic;
            if (regionCode is 4 or 6 or 7)
                return SmsGgRegion.International;
        }

        return SmsGgRegion.Domestic;
    }

    private byte ReadSmsBios(ushort address)
    {
        if (_biosRom is null || _biosRom.Length == 0)
            return 0xFF;

        uint biosAddress;
        if (_biosRom.Length > 32 * 1024)
        {
            biosAddress = address switch
            {
                <= 0x03FF => address,
                <= 0xBFFF => (_biosRomBanks[address / 0x4000] << 14) | (uint)(address & 0x3FFF),
                _ => throw new InvalidOperationException($"Invalid BIOS address {address:X4}")
            };
        }
        else
        {
            biosAddress = address;
        }

        return _biosRom[(int)(biosAddress & (uint)(_biosRom.Length - 1))];
    }

    private sealed class AudioControl
    {
        public bool FmEnabled { get; set; }
        public bool PsgEnabled { get; set; } = true;

        public void Reset()
        {
            FmEnabled = false;
            PsgEnabled = true;
        }
    }

    private sealed class Cartridge
    {
        public Cartridge(byte[] rom, byte[]? initialRam)
        {
            Rom = (byte[])rom.Clone();
            Mapper = SmsGgMapper.DetectFromRom(Rom);
            Crc32 = ComputeCrc32(Rom);
            HasBattery = SmsGgCartridgeMetadata.HasBatteryBackup(Crc32);
            Ram = initialRam is { Length: CartridgeRamSize } ? (byte[])initialRam.Clone() : new byte[CartridgeRamSize];
        }

        public byte[] Rom { get; private set; }
        public byte[] Ram { get; private set; }
        public ISmsGgMapper Mapper { get; private set; }
        public uint Crc32 { get; private set; }
        public bool HasBattery { get; private set; }
        public bool RamDirty { get; private set; }

        public byte Read(ushort address) => Mapper.Read(address, Rom, Ram);

        public void Write(ushort address, byte value)
        {
            bool ramDirty = RamDirty;
            Mapper.Write(address, value, Ram, ref ramDirty);
            RamDirty = ramDirty;
            HasBattery |= RamDirty;
        }

        public byte[] TakeRom()
        {
            byte[] rom = Rom;
            Rom = Array.Empty<byte>();
            return rom;
        }

        public byte[] TakeRam()
        {
            byte[] ram = Ram;
            Ram = Array.Empty<byte>();
            return ram;
        }

        public void Restore(byte[] rom, byte[] ram)
        {
            Rom = rom;
            Ram = ram;
            Mapper = SmsGgMapper.DetectFromRom(rom);
            Crc32 = ComputeCrc32(rom);
            HasBattery = SmsGgCartridgeMetadata.HasBatteryBackup(Crc32) || RamDirty;
            RamDirty = false;
        }

        private static byte[] MirrorToNextPowerOfTwo(byte[] data)
        {
            if (data.Length == 0)
                return new byte[1];

            int size = 1;
            while (size < data.Length)
                size <<= 1;

            if (size == data.Length)
                return (byte[])data.Clone();

            byte[] mirrored = new byte[size];
            int offset = 0;
            while (offset < size)
            {
                int copyLength = Math.Min(data.Length, size - offset);
                Buffer.BlockCopy(data, 0, mirrored, offset, copyLength);
                offset += copyLength;
            }

            return mirrored;
        }

        private static uint ComputeCrc32(byte[] data)
        {
            const uint polynomial = 0xEDB88320u;
            uint[] table = new uint[256];
            for (uint i = 0; i < table.Length; i++)
            {
                uint crc = i;
                for (int bit = 0; bit < 8; bit++)
                    crc = (crc & 1) != 0 ? polynomial ^ (crc >> 1) : crc >> 1;

                table[i] = crc;
            }

            uint value = 0xFFFFFFFFu;
            for (int i = 0; i < data.Length; i++)
                value = table[(value ^ data[i]) & 0xFF] ^ (value >> 8);

            return ~value;
        }
    }
}
