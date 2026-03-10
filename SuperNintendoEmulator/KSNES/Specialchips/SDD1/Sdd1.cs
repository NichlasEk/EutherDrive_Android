using System;

namespace KSNES.Specialchips.SDD1;

internal sealed class Sdd1
{
    private static readonly bool TraceSdd1 =
        string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_SDD1"), "1", StringComparison.Ordinal);
    private static readonly int TraceSdd1Limit = ParseTraceLimit("EUTHERDRIVE_TRACE_SDD1_LIMIT", 256);
    private static readonly bool TraceSdd1Reads =
        string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_SDD1_READS"), "1", StringComparison.Ordinal);
    private byte[] _rom;
    private readonly byte[] _sram;
    private readonly Sdd1Mmc _mmc = new();
    private readonly Sdd1Decompressor _decompressor = new();
    private readonly bool[] _dmaEnabled1 = new bool[8];
    private readonly bool[] _dmaEnabled2 = new bool[8];
    private bool _dmaInProgress;
    private int _activeDmaChannel = -1;
    private int _traceRemaining = TraceSdd1Limit;

    public Sdd1(byte[] rom, byte[] sram)
    {
        _rom = rom;
        _sram = sram;
    }

    public void Reset()
    {
        Array.Clear(_dmaEnabled1);
        Array.Clear(_dmaEnabled2);
        _dmaInProgress = false;
        _activeDmaChannel = -1;
        _mmc.Reset();
        _decompressor.Reset();
        _traceRemaining = TraceSdd1Limit;
    }

    public bool Read(uint address, out byte value)
    {
        if (_dmaInProgress)
        {
            value = _decompressor.NextByte(_mmc, _rom);
            if (TraceSdd1Reads)
                Trace($"[SDD1-RD] dma byte=0x{value:X2}");
            return true;
        }

        uint bank = (address >> 16) & 0xFF;
        uint offset = address & 0xFFFF;

        if ((Sdd1Addressing.IsLoRomRegion(bank) && offset >= 0x6000 && offset <= 0x7FFF) ||
            (bank >= 0x70 && bank <= 0x73))
        {
            if (_sram.Length == 0)
            {
                value = 0;
                return false;
            }

            value = _sram[(int)(address & (uint)(_sram.Length - 1))];
            return true;
        }

        if (Sdd1Addressing.IsLoRomRegion(bank) && offset >= 0x4800 && offset <= 0x4807)
        {
            value = ReadRegister(offset);
            Trace($"[SDD1-REG-R] addr=0x{address:X6} val=0x{value:X2}");
            return true;
        }

        uint? romAddr = _mmc.MapRomAddress(address, (uint)_rom.Length);
        if (romAddr.HasValue && romAddr.Value < _rom.Length)
        {
            value = _rom[(int)romAddr.Value];
            return true;
        }

        value = 0;
        return false;
    }

    public bool Write(uint address, byte value, out bool wroteSram)
    {
        wroteSram = false;

        uint bank = (address >> 16) & 0xFF;
        uint offset = address & 0xFFFF;

        if ((Sdd1Addressing.IsLoRomRegion(bank) && offset >= 0x6000 && offset <= 0x7FFF) ||
            (bank >= 0x70 && bank <= 0x73))
        {
            if (_sram.Length == 0)
                return false;

            _sram[(int)(address & (uint)(_sram.Length - 1))] = value;
            wroteSram = true;
            Trace($"[SDD1-SRAM-W] addr=0x{address:X6} val=0x{value:X2}");
            return true;
        }

        if (Sdd1Addressing.IsLoRomRegion(bank) && offset >= 0x4800 && offset <= 0x4807)
        {
            WriteRegister(offset, value);
            Trace($"[SDD1-REG-W] addr=0x{address:X6} val=0x{value:X2}");
            return true;
        }

        return false;
    }

    public void NotifyDmaStart(byte channel, uint sourceAddress)
    {
        if (channel >= 8)
        {
            _dmaInProgress = false;
            Trace($"[SDD1-DMA-START] ch={channel} src=0x{sourceAddress:X6} reject=channel");
            return;
        }

        if (!_dmaEnabled1[channel] || !_dmaEnabled2[channel] || !_mmc.MapRomAddress(sourceAddress, (uint)_rom.Length).HasValue)
        {
            _dmaInProgress = false;
            Trace(
                $"[SDD1-DMA-START] ch={channel} src=0x{sourceAddress:X6} en1={(_dmaEnabled1[channel] ? 1 : 0)} en2={(_dmaEnabled2[channel] ? 1 : 0)} map={(_mmc.MapRomAddress(sourceAddress, (uint)_rom.Length).HasValue ? 1 : 0)} reject");
            return;
        }

        _dmaEnabled2[channel] = false;
        _dmaInProgress = true;
        _activeDmaChannel = channel;
        _decompressor.Init(sourceAddress, _mmc, _rom);
        Trace($"[SDD1-DMA-START] ch={channel} src=0x{sourceAddress:X6} accept");
    }

    public void NotifyDmaEnd(byte channel)
    {
        if (_activeDmaChannel != channel)
            return;

        _dmaInProgress = false;
        _activeDmaChannel = -1;
        Trace("[SDD1-DMA-END]");
    }

    private byte ReadRegister(uint offset)
    {
        return offset switch
        {
            0x4800 => PackDmaBits(_dmaEnabled1),
            0x4801 => PackDmaBits(_dmaEnabled2),
            0x4802 => 0x00,
            0x4803 => 0x00,
            0x4804 => (byte)(_mmc.BankCBaseAddr >> 20),
            0x4805 => (byte)(_mmc.BankDBaseAddr >> 20),
            0x4806 => (byte)(_mmc.BankEBaseAddr >> 20),
            0x4807 => (byte)(_mmc.BankFBaseAddr >> 20),
            _ => 0x00
        };
    }

    private void WriteRegister(uint offset, byte value)
    {
        switch (offset)
        {
            case 0x4800:
                UnpackDmaBits(value, _dmaEnabled1);
                break;
            case 0x4801:
                UnpackDmaBits(value, _dmaEnabled2);
                break;
            case 0x4804:
                _mmc.BankCBaseAddr = (uint)value << 20;
                break;
            case 0x4805:
                _mmc.BankDBaseAddr = (uint)value << 20;
                break;
            case 0x4806:
                _mmc.BankEBaseAddr = (uint)value << 20;
                break;
            case 0x4807:
                _mmc.BankFBaseAddr = (uint)value << 20;
                break;
        }
    }

    private static byte PackDmaBits(bool[] bits)
    {
        byte value = 0;
        for (int i = 0; i < bits.Length; i++)
        {
            if (bits[i])
                value |= (byte)(1 << i);
        }

        return value;
    }

    private static void UnpackDmaBits(byte value, bool[] bits)
    {
        for (int i = 0; i < bits.Length; i++)
            bits[i] = ((value >> i) & 0x01) != 0;
    }

    private void Trace(string message)
    {
        if (!TraceSdd1 || _traceRemaining <= 0)
            return;

        _traceRemaining--;
        Console.WriteLine(message);
    }

    private static int ParseTraceLimit(string name, int fallback)
    {
        string? raw = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(raw))
            return fallback;
        return int.TryParse(raw, out int value) && value > 0 ? value : fallback;
    }

    internal sealed class Sdd1Mmc
    {
        public uint BankCBaseAddr = 0x000000;
        public uint BankDBaseAddr = 0x100000;
        public uint BankEBaseAddr = 0x200000;
        public uint BankFBaseAddr = 0x300000;

        public void Reset()
        {
            BankCBaseAddr = 0x000000;
            BankDBaseAddr = 0x100000;
            BankEBaseAddr = 0x200000;
            BankFBaseAddr = 0x300000;
        }

        public uint? MapRomAddress(uint address, uint romLen)
        {
            uint bank = (address >> 16) & 0xFF;
            uint offset = address & 0xFFFF;
            return (bank, offset) switch
            {
                (<= 0x3F, >= 0x8000) or (>= 0x80 and <= 0xBF, >= 0x8000)
                    => Sdd1Addressing.LoRomMapAddress(address & 0x7FFFFF, romLen),
                (>= 0xC0 and <= 0xCF, _) => BankCBaseAddr | (address & 0x0FFFFF),
                (>= 0xD0 and <= 0xDF, _) => BankDBaseAddr | (address & 0x0FFFFF),
                (>= 0xE0 and <= 0xEF, _) => BankEBaseAddr | (address & 0x0FFFFF),
                (>= 0xF0 and <= 0xFF, _) => BankFBaseAddr | (address & 0x0FFFFF),
                _ => null
            };
        }
    }
}

internal static class Sdd1Addressing
{
    public static bool IsLoRomRegion(uint bank)
    {
        return bank <= 0x3F || (bank >= 0x80 && bank <= 0xBF);
    }

    public static uint LoRomMapAddress(uint address, uint romLen)
    {
        if (romLen == 0)
            return 0;

        uint mapped = ((address & 0x7F0000) >> 1) | (address & 0x007FFF);
        if ((romLen & (romLen - 1)) == 0)
            return mapped & (romLen - 1);
        return mapped % romLen;
    }
}
