using System;

namespace KSNES.Specialchips.SPC7110;

[Serializable]
internal sealed class Spc7110
{
    private const int DataRomStart = 0x100000;
    private const int SramLen = 8 * 1024;

    [NonSerialized]
    private byte[] _rom;
    private byte[] _sram;
    private readonly Spc7110Registers _registers = new();
    private readonly Spc7110Decompressor _decompressor = new();
    private readonly Rtc4513? _rtc;

    public Spc7110(byte[] rom, byte[] initialSram, bool hasRtc)
    {
        _rom = rom;
        _sram = initialSram.Length == SramLen ? initialSram : new byte[SramLen];
        _rtc = hasRtc ? new Rtc4513() : null;
    }

    public byte[] Sram => _sram;
    public Rtc4513? Rtc => _rtc;

    public void SetRom(byte[] rom)
    {
        _rom = rom;
    }

    public bool Read(uint address, out byte value)
    {
        uint bank = (address >> 16) & 0xFF;
        uint offset = address & 0xFFFF;
        switch (bank, offset)
        {
            case (<= 0x3F or >= 0x80 and <= 0xBF, >= 0x4800 and <= 0x4842):
                return ReadRegister(address, out value);
            case (<= 0x0F or >= 0x80 and <= 0x8F, >= 0x8000 and <= 0xFFFF):
            case (>= 0xC0 and <= 0xCF, _):
                value = _rom[(int)(address & 0x0FFFFF)];
                return true;
            case (>= 0x40 and <= 0x4F, _):
                value = _rom[_rom.Length >= 0x700000
                    ? (0x600000 | (int)(address & 0x0FFFFF))
                    : (int)(address & 0x0FFFFF)];
                return true;
            case (>= 0xD0 and <= 0xDF, _):
                return ReadDataRomBank(address, _registers.RomBankD, out value);
            case (>= 0xE0 and <= 0xEF, _):
                return ReadDataRomBank(address, _registers.RomBankE, out value);
            case (>= 0xF0 and <= 0xFF, _):
                return ReadDataRomBank(address, _registers.RomBankF, out value);
            case (<= 0x3F or >= 0x80 and <= 0xBF, >= 0x6000 and <= 0x7FFF):
                if (_registers.SramEnabled)
                {
                    value = _sram[address & 0x1FFF];
                    return true;
                }
                break;
            case (0x50, _):
                value = _decompressor.NextByte(DataRom());
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

        switch (bank, offset)
        {
            case (<= 0x3F or >= 0x80 and <= 0xBF, >= 0x4800 and <= 0x4842):
                WriteRegister(address, value);
                return true;
            case (<= 0x3F or >= 0x80 and <= 0xBF, >= 0x6000 and <= 0x7FFF):
                if (_registers.SramEnabled)
                {
                    _sram[address & 0x1FFF] = value;
                    wroteSram = true;
                }
                return true;
        }

        return false;
    }

    private bool ReadRegister(uint address, out byte value)
    {
        value = (address & 0xFFFF) switch
        {
            0x4800 => _decompressor.NextByte(DataRom()),
            0x4801 => (byte)_decompressor.RomDirectoryBase,
            0x4802 => (byte)(_decompressor.RomDirectoryBase >> 8),
            0x4803 => (byte)(_decompressor.RomDirectoryBase >> 16),
            0x4804 => _decompressor.RomDirectoryIndex,
            0x4805 => (byte)_decompressor.TargetOffset,
            0x4806 => (byte)(_decompressor.TargetOffset >> 8),
            0x4807 => 0x00,
            0x4808 => 0x00,
            0x4809 => (byte)_decompressor.LengthCounter,
            0x480A => (byte)(_decompressor.LengthCounter >> 8),
            0x480B => _decompressor.ReadMode(),
            0x480C => _decompressor.ReadStatus(),
            0x4810 => _registers.ReadDirectDataRom4810(DataRom()),
            0x4811 => (byte)_registers.DirectDataRomBase,
            0x4812 => (byte)(_registers.DirectDataRomBase >> 8),
            0x4813 => (byte)(_registers.DirectDataRomBase >> 16),
            0x4814 => (byte)_registers.DirectDataRomOffset,
            0x4815 => (byte)(_registers.DirectDataRomOffset >> 8),
            0x4816 => (byte)_registers.DirectDataRomStepValue,
            0x4817 => (byte)(_registers.DirectDataRomStepValue >> 8),
            0x4818 => 0x00,
            0x481A => _registers.ReadDirectDataRom481A(DataRom()),
            >= 0x4820 and <= 0x4823 => _registers.ReadDividend(address),
            0x4824 => (byte)_registers.Math.Multiplier,
            0x4825 => (byte)(_registers.Math.Multiplier >> 8),
            0x4826 => (byte)_registers.Math.Divisor,
            0x4827 => (byte)(_registers.Math.Divisor >> 8),
            >= 0x4828 and <= 0x482B => _registers.ReadMathResult(address),
            0x482C => (byte)_registers.Math.Remainder,
            0x482D => (byte)(_registers.Math.Remainder >> 8),
            0x482E => 0x00,
            0x482F => 0x00,
            0x4830 => _registers.ReadSramEnabled(),
            0x4831 => _registers.RomBankD,
            0x4832 => _registers.RomBankE,
            0x4833 => _registers.RomBankF,
            0x4834 => _registers.SramBank,
            0x4840 when _rtc != null => _rtc.ReadChipSelect(),
            0x4841 when _rtc != null => _rtc.ReadDataPort(),
            0x4842 when _rtc != null => Rtc4513.StatusByte,
            _ => 0
        };

        return true;
    }

    private void WriteRegister(uint address, byte value)
    {
        switch (address & 0xFFFF)
        {
            case 0x4801: _decompressor.WriteRomDirectoryBaseLow(value); break;
            case 0x4802: _decompressor.WriteRomDirectoryBaseMid(value); break;
            case 0x4803: _decompressor.WriteRomDirectoryBaseHigh(value); break;
            case 0x4804: _decompressor.RomDirectoryIndex = value; break;
            case 0x4805: _decompressor.WriteTargetOffsetLow(value); break;
            case 0x4806: _decompressor.WriteTargetOffsetHigh(value, DataRom()); break;
            case 0x4809: _decompressor.WriteLengthCounterLow(value); break;
            case 0x480A: _decompressor.WriteLengthCounterHigh(value); break;
            case 0x480B: _decompressor.WriteMode(value); break;
            case 0x4811: _registers.WriteDirectDataRomBaseLow(value); break;
            case 0x4812: _registers.WriteDirectDataRomBaseMid(value); break;
            case 0x4813: _registers.WriteDirectDataRomBaseHigh(value); break;
            case 0x4814: _registers.WriteDirectDataRomOffsetLow(value); break;
            case 0x4815: _registers.WriteDirectDataRomOffsetHigh(value); break;
            case 0x4816: _registers.WriteDirectDataRomStepLow(value); break;
            case 0x4817: _registers.WriteDirectDataRomStepHigh(value); break;
            case 0x4818: _registers.WriteDirectDataRomMode(value); break;
            case >= 0x4820 and <= 0x4823: _registers.WriteDividend(address, value); break;
            case 0x4824: _registers.WriteMultiplierLow(value); break;
            case 0x4825: _registers.WriteMultiplierHigh(value); break;
            case 0x4826: _registers.WriteDivisorLow(value); break;
            case 0x4827: _registers.WriteDivisorHigh(value); break;
            case 0x482E: _registers.WriteMathMode(value); break;
            case 0x4830: _registers.WriteSramEnabled(value); break;
            case 0x4831: _registers.RomBankD = value; break;
            case 0x4832: _registers.RomBankE = value; break;
            case 0x4833: _registers.RomBankF = value; break;
            case 0x4834: _registers.SramBank = value; break;
            case 0x4840 when _rtc != null: _rtc.WriteChipSelect(value); break;
            case 0x4841 when _rtc != null: _rtc.WriteDataPort(value); break;
        }
    }

    private bool ReadDataRomBank(uint address, byte bankSelect, out byte value)
    {
        uint romAddr = (address & 0x0FFFFF) | ((uint)bankSelect << 20);
        ReadOnlySpan<byte> dataRom = DataRom();
        if (romAddr < dataRom.Length)
        {
            value = dataRom[(int)romAddr];
            return true;
        }

        value = 0;
        return false;
    }

    private ReadOnlySpan<byte> DataRom()
    {
        return _rom.Length > DataRomStart ? _rom.AsSpan(DataRomStart) : ReadOnlySpan<byte>.Empty;
    }
}
