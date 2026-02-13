namespace KSNES.ROM;

public class ROM : IROM
{
#pragma warning disable CS8618 // Non-nullable field must contain a non-null value when exiting constructor. Consider declaring as nullable.
    public Header Header { get; private set; }
#pragma warning restore CS8618 // Non-nullable field must contain a non-null value when exiting constructor. Consider declaring as nullable.
    private byte[] _data = [];
    private byte[] _sram = [];
    private bool _hasSram;
    private int _banks;
    private int _sramSize;

    private ISNESSystem? _system;
    private KSNES.Specialchips.CX4.Cx4? _cx4;
    private static readonly bool TraceCx4Bus =
        string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_CX4_BUS"), "1", StringComparison.Ordinal);

    private Timer? _sRAMTimer;

    public void LoadROM(byte[] data, Header header)
    {
        _data = data;
        Header = header;
        _sram = new byte[header.RamSize];
        _hasSram = header.Chips > 0;
        _banks = header.RomSize / 0x8000;
        _sramSize = header.RamSize;

        if (header.ExCoprocessor == 0x10)
        {
            if (_system == null)
                throw new InvalidOperationException("ROM system not set.");
            _cx4 = new KSNES.Specialchips.CX4.Cx4(_system);
            _cx4.Reset();
        }
        else
        {
            _cx4 = null;
        }
    }

    public int RomLength => _data.Length;

    public void LoadSRAM()
    {
        string fileName = GetSRAMFileName();
        if (new FileInfo(fileName).Exists)
        {
            _sram = File.ReadAllBytes(fileName);
        }
    }

    public byte Read(int bank, int adr)
    {
        if (_cx4 != null)
        {
            if ((bank & 0x7f) < 0x40 && adr >= 0x6000 && adr < 0x8000)
            {
                if (TraceCx4Bus)
                    Console.WriteLine($"[CX4-BUS-RD] bank=0x{bank:X2} adr=0x{adr:X4}");
                return _cx4.Read(adr);
            }
            if (adr < 0x8000 && _hasSram && ((bank >= 0x70 && bank < 0x7e) || bank >= 0xf0))
            {
                return _sram[(((bank & 0x0f) << 15) | (adr & 0x7fff)) & (_sramSize - 1)];
            }
            bank &= 0x7f;
            if (adr >= 0x8000 || bank >= 0x40)
            {
                return _data[((bank & (_banks - 1)) << 15) | (adr & 0x7fff)];
            }
            return (byte)(_system?.OpenBus ?? 0);
        }

        if (adr < 0x8000)
        {
            if (bank >= 0x70 && bank < 0x7e && _hasSram)
            {
                return _sram[(((bank - 0x70) << 15) | (adr & 0x7fff)) & (_sramSize - 1)];
            }
        }
        return _data[((bank & (_banks - 1)) << 15) | (adr & 0x7fff)];
    }

    public void Write(int bank, int adr, byte value)
    {
        if (_cx4 != null)
        {
            if ((bank & 0x7f) < 0x40 && adr >= 0x6000 && adr < 0x8000)
            {
                if (TraceCx4Bus)
                    Console.WriteLine($"[CX4-BUS-WR] bank=0x{bank:X2} adr=0x{adr:X4} val=0x{value:X2}");
                _cx4.Write(adr, value);
                return;
            }
            if (adr < 0x8000 && _hasSram && ((bank >= 0x70 && bank < 0x7e) || bank >= 0xf0))
            {
                _sram[(((bank & 0x0f) << 15) | (adr & 0x7fff)) & (_sramSize - 1)] = value;
                _sRAMTimer ??= new Timer(SaveSRAM, null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));
            }
            return;
        }

        if (adr < 0x8000 && bank >= 0x70 && bank < 0x7e && _hasSram)
        {
            _sram[(((bank - 0x70) << 15) | (adr & 0x7fff)) & (_sramSize - 1)] = value;
            _sRAMTimer ??= new Timer(SaveSRAM, null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));
        }
    }

    public void SetSystem(ISNESSystem system)
    {
        _system = system;
    }

    public void ResetCoprocessor()
    {
        _cx4?.Reset();
    }

    public void RunCoprocessor(ulong snesCycles)
    {
        _cx4?.RunTo(snesCycles);
    }

    public byte ReadRomByteLoRom(uint address)
    {
        if (_data.Length == 0)
            return 0;
        uint mapped = ((address & 0x7F0000) >> 1) | (address & 0x007FFF);
        uint mask = (uint)_data.Length - 1;
        if ((_data.Length & (_data.Length - 1)) == 0)
            return _data[mapped & mask];
        return _data[mapped % (uint)_data.Length];
    }

    private void SaveSRAM(object? state)
    {
        string fileName = GetSRAMFileName();
        try
        {
            File.WriteAllBytes(fileName, _sram);
        }
        catch (UnauthorizedAccessException ex)
        {
            Console.WriteLine($"[SRAM] Save failed: {ex.Message}");
        }
        catch (IOException ex)
        {
            Console.WriteLine($"[SRAM] Save failed: {ex.Message}");
        }
        _sRAMTimer?.Dispose();
        _sRAMTimer = null;
    }

    private string GetSRAMFileName()
    {
        return Path.ChangeExtension(_system!.FileName, ".srm");
    }
}
