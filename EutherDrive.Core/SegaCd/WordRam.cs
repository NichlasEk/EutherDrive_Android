using System;

namespace EutherDrive.Core.SegaCd;

public enum ScdCpu
{
    Main,
    Sub
}

public enum WordRamMode
{
    TwoM,
    OneM
}

public enum WordRamPriorityMode
{
    Off,
    Underwrite,
    Overwrite,
    Invalid
}

public enum Nibble
{
    High,
    Low
}

internal enum WordRamSubMapResultKind
{
    None,
    Byte,
    Pixel
}

internal readonly struct WordRamSubMapResult
{
    public WordRamSubMapResultKind Kind { get; }
    public uint Address { get; }

    private WordRamSubMapResult(WordRamSubMapResultKind kind, uint address)
    {
        Kind = kind;
        Address = address;
    }

    public static WordRamSubMapResult None => new(WordRamSubMapResultKind.None, 0);
    public static WordRamSubMapResult Byte(uint addr) => new(WordRamSubMapResultKind.Byte, addr);
    public static WordRamSubMapResult Pixel(uint addr) => new(WordRamSubMapResultKind.Pixel, addr);
}

public sealed class WordRam
{
    public const int WordRamLen = 256 * 1024;
    public const uint AddressMask = 0x03FFFF;
    public const uint SubBaseAddress = 0x080000;

    private readonly byte[] _ram = new byte[WordRamLen];
    private readonly (uint addr, byte value)[] _subBufferedWrites = new (uint, byte)[32];
    private int _subBufferedCount;
    private bool _subBlockedRead;
    private bool _swapRequest;

    private WordRamMode _mode = WordRamMode.TwoM;
    private WordRamPriorityMode _priorityMode = WordRamPriorityMode.Off;
    private ScdCpu _owner2m = ScdCpu.Main;
    private ScdCpu _bank0Owner1m = ScdCpu.Main;
    private static readonly bool LogWordRam =
        string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_SCD_LOG_WORDRAM"), "1", StringComparison.Ordinal);

    private const uint CellImageV32Size = 32 * 8 * 8 / 2;
    private const uint CellImageV16Size = 16 * 8 * 8 / 2;
    private const uint CellImageV8Size = 8 * 8 * 8 / 2;
    private const uint CellImageV4Size = 4 * 8 * 8 / 2;
    private const uint CellImageHSize = 64 * 8 / 2;

    public WordRamMode Mode => _mode;
    public WordRamPriorityMode PriorityMode => _priorityMode;

    public void Reset()
    {
        Array.Clear(_ram, 0, _ram.Length);
        _subBufferedCount = 0;
        _subBlockedRead = false;
        _swapRequest = false;
        _mode = WordRamMode.TwoM;
        _priorityMode = WordRamPriorityMode.Off;
        _owner2m = ScdCpu.Main;
        _bank0Owner1m = ScdCpu.Main;
    }

    public string GetDebugState()
    {
        return $"mode={_mode} owner2m={_owner2m} bank0Owner1m={_bank0Owner1m} swapRequest={_swapRequest} priority={_priorityMode}";
    }

    public byte ReadControl()
    {
        bool dmna;
        bool ret;
        if (_mode == WordRamMode.TwoM)
        {
            dmna = _owner2m == ScdCpu.Sub;
            // RET bit: 1 if Sub CPU has returned the Word RAM, or if Main CPU owns it.
            // When Sub CPU requests Word RAM, it writes 0 to RET, which we might need to track if we want to model the exact handshake.
            // For now, standard emulation practice: if Main CPU owns it, RET=1. If Sub CPU owns it, RET=0 until Sub CPU returns it.
            ret = _owner2m == ScdCpu.Main;
        }
        else
        {
            dmna = _swapRequest;
            ret = _bank0Owner1m == ScdCpu.Sub;
        }

        byte modeBit = _mode == WordRamMode.OneM ? (byte)1 : (byte)0;
        return (byte)((modeBit << 2) | ((dmna ? 1 : 0) << 1) | (ret ? 1 : 0));
    }

    public void MainCpuWriteControl(byte value)
    {
        bool dmna = (value & 0x02) != 0;
        // In 2M mode, DMNA=1 hands Word RAM to the sub CPU.
        // DMNA=0 does not immediately give it back to the main CPU.
        if (dmna)
        {
            _owner2m = ScdCpu.Sub;
            FlushBufferedSubWrites();
            _subBlockedRead = false;
        }

        if (_mode == WordRamMode.OneM && !dmna)
            _swapRequest = true;

        if (LogWordRam)
            Console.WriteLine($"[SCD-WORDRAM] main ctl=0x{value:X2} {GetDebugState()}");
    }

    public void SubCpuWriteControl(byte value)
    {
        _mode = (value & 0x04) != 0 ? WordRamMode.OneM : WordRamMode.TwoM;
        bool ret = (value & 0x01) != 0;

        // RET=1 always returns 2M Word RAM ownership to the main CPU, even
        // when the register write also switches the hardware into 1M mode.
        if (ret)
        {
            _owner2m = ScdCpu.Main;
        }

        ScdCpu prev = _bank0Owner1m;
        _bank0Owner1m = ret ? ScdCpu.Sub : ScdCpu.Main;
        if (prev != _bank0Owner1m)
            _swapRequest = false;

        _priorityMode = ((value >> 3) & 0x03) switch
        {
            0x00 => WordRamPriorityMode.Off,
            0x01 => WordRamPriorityMode.Underwrite,
            0x02 => WordRamPriorityMode.Overwrite,
            _ => WordRamPriorityMode.Invalid
        };

        if (LogWordRam)
            Console.WriteLine($"[SCD-WORDRAM] sub ctl=0x{value:X2} {GetDebugState()}");
    }

    public byte MainCpuReadRam(uint address)
    {
        uint? mapped = MainCpuMapAddress(address);
        return mapped.HasValue ? _ram[mapped.Value] : (byte)0x00;
    }

    // VDP DMA on Sega CD can source from Word RAM even when main CPU access is gated.
    // Keep normal mapping for 1M mode, but bypass 2M ownership gating for DMA reads.
    public ushort MainCpuDmaReadWord(uint address)
    {
        if (_mode == WordRamMode.TwoM)
        {
            uint a0 = address & AddressMask;
            uint a1 = (a0 + 1) & AddressMask;
            return (ushort)((_ram[a0] << 8) | _ram[a1]);
        }

        byte msb = MainCpuReadRam(address);
        byte lsb = MainCpuReadRam(address | 1);
        return (ushort)((msb << 8) | lsb);
    }

    public void MainCpuWriteRam(uint address, byte value)
    {
        uint? mapped = MainCpuMapAddress(address);
        if (mapped.HasValue)
            _ram[mapped.Value] = value;
    }

    public byte SubCpuReadRam(uint address)
    {
        WordRamSubMapResult mapped = SubCpuMapAddress(address);
        switch (mapped.Kind)
        {
            case WordRamSubMapResultKind.None:
                return 0x00;
            case WordRamSubMapResultKind.Byte:
                _subBlockedRead |= IsSubAccessBlocked();
                return _ram[mapped.Address];
            case WordRamSubMapResultKind.Pixel:
                {
                    int byteAddr = (int)(mapped.Address >> 1);
                    byte value = _ram[byteAddr];
                    return (mapped.Address & 1) != 0 ? (byte)(value & 0x0F) : (byte)(value >> 4);
                }
            default:
                return 0x00;
        }
    }

    public void SubCpuWriteRam(uint address, byte value)
    {
        WordRamSubMapResult mapped = SubCpuMapAddress(address);
        switch (mapped.Kind)
        {
            case WordRamSubMapResultKind.None:
                return;
            case WordRamSubMapResultKind.Byte:
                if (!IsSubAccessBlocked())
                    _ram[mapped.Address] = value;
                else
                    BufferSubWrite(mapped.Address, value);
                return;
            case WordRamSubMapResultKind.Pixel:
                Write1mPixel(mapped.Address, (byte)(value & 0x0F));
                return;
        }
    }

    public void GraphicsWriteRam(uint address, Nibble nibble, byte pixel)
    {
        WordRamSubMapResult mapped = SubCpuMapAddress(address);
        switch (mapped.Kind)
        {
            case WordRamSubMapResultKind.None:
                return;
            case WordRamSubMapResultKind.Byte:
                {
                    byte current = _ram[mapped.Address];
                    byte currentNibble = nibble == Nibble.High ? (byte)(current >> 4) : (byte)(current & 0x0F);
                    if (!ShouldWritePixel(currentNibble, pixel))
                        return;
                    byte newValue = nibble == Nibble.High
                        ? (byte)((pixel << 4) | (current & 0x0F))
                        : (byte)(pixel | (current & 0xF0));
                    _ram[mapped.Address] = newValue;
                    return;
                }
            case WordRamSubMapResultKind.Pixel:
                if (nibble == Nibble.Low)
                    Write1mPixel(mapped.Address, pixel);
                return;
        }
    }

    public void DmaWrite(uint address, byte value)
    {
        uint baseAddr = _mode == WordRamMode.TwoM ? SubBaseAddress : 0x0C0000;
        SubCpuWriteRam(baseAddr | address, value);
    }

    public bool IsSubAccessBlocked()
    {
        return _mode == WordRamMode.TwoM && _owner2m == ScdCpu.Main;
    }

    public bool SubPerformedBlockedAccess()
    {
        return _subBufferedCount > 0 || _subBlockedRead;
    }

    private uint? MainCpuMapAddress(uint address)
    {
        uint masked = address & AddressMask;
        if (_mode == WordRamMode.TwoM)
            return _owner2m == ScdCpu.Main ? masked : null;

        if (masked <= 0x1FFFF)
            return Determine1mAddress(masked, ScdCpu.Main, _bank0Owner1m);

        uint addr = masked & 0x1FFFF;
        return addr switch
        {
            <= 0x0FFFF => MapCellImageAddress(addr & 0xFFFF, CellImageV32Size, _bank0Owner1m, 0x00000),
            <= 0x17FFF => MapCellImageAddress(addr & 0x7FFF, CellImageV16Size, _bank0Owner1m, 0x10000),
            <= 0x1BFFF => MapCellImageAddress(addr & 0x3FFF, CellImageV8Size, _bank0Owner1m, 0x18000),
            <= 0x1DFFF => MapCellImageAddress(addr & 0x1FFF, CellImageV4Size, _bank0Owner1m, 0x1C000),
            _ => MapCellImageAddress(addr & 0x1FFF, CellImageV4Size, _bank0Owner1m, 0x1E000)
        };
    }

    private WordRamSubMapResult SubCpuMapAddress(uint address)
    {
        return (_mode, address) switch
        {
            (WordRamMode.TwoM, >= 0x080000 and <= 0x0BFFFF) => WordRamSubMapResult.Byte(address & AddressMask),
            (WordRamMode.TwoM, >= 0x0C0000 and <= 0x0DFFFF) => WordRamSubMapResult.None,
            (WordRamMode.OneM, >= 0x080000 and <= 0x0BFFFF) => Map1mPixel(address),
            (WordRamMode.OneM, >= 0x0C0000 and <= 0x0DFFFF) =>
                WordRamSubMapResult.Byte(Determine1mAddress(address & 0x1FFFF, ScdCpu.Sub, _bank0Owner1m)),
            _ => WordRamSubMapResult.None
        };
    }

    private WordRamSubMapResult Map1mPixel(uint address)
    {
        uint byteAddr = Determine1mAddress((address & 0x3FFFF) >> 1, ScdCpu.Sub, _bank0Owner1m);
        uint pixelAddr = (byteAddr << 1) | (address & 1);
        return WordRamSubMapResult.Pixel(pixelAddr);
    }

    private static uint Determine1mAddress(uint address, ScdCpu cpu, ScdCpu bank0Owner)
    {
        return ((address & ~1u) << 1) | ((cpu != bank0Owner ? 1u : 0u) << 1) | (address & 1u);
    }

    private static uint MapCellImageAddress(uint maskedAddress, uint vSizeBytes, ScdCpu bank0Owner, uint baseAddr)
    {
        uint row = (maskedAddress & (vSizeBytes - 1)) >> 2;
        uint col = maskedAddress / vSizeBytes;
        uint byteAddr = baseAddr | (row * CellImageHSize) | (col << 2) | (maskedAddress & 0x03);
        return Determine1mAddress(byteAddr, ScdCpu.Main, bank0Owner);
    }

    private void Write1mPixel(uint pixelAddr, byte pixel)
    {
        int byteAddr = (int)(pixelAddr >> 1);
        byte current = _ram[byteAddr];
        byte currentNibble = (pixelAddr & 1) != 0 ? (byte)(current & 0x0F) : (byte)(current >> 4);
        if (!ShouldWritePixel(currentNibble, pixel))
            return;

        _ram[byteAddr] = (pixelAddr & 1) != 0
            ? (byte)(pixel | (current & 0xF0))
            : (byte)((pixel << 4) | (current & 0x0F));
    }

    private bool ShouldWritePixel(byte currentNibble, byte pixel)
    {
        return _priorityMode switch
        {
            WordRamPriorityMode.Off => true,
            WordRamPriorityMode.Invalid => true,
            WordRamPriorityMode.Underwrite => currentNibble == 0,
            WordRamPriorityMode.Overwrite => pixel != 0,
            _ => true
        };
    }

    private void BufferSubWrite(uint addr, byte value)
    {
        if (_subBufferedCount >= _subBufferedWrites.Length)
            return;
        _subBufferedWrites[_subBufferedCount++] = (addr, value);
    }

    private void FlushBufferedSubWrites()
    {
        for (int i = 0; i < _subBufferedCount; i++)
        {
            var (addr, value) = _subBufferedWrites[i];
            _ram[addr] = value;
        }
        _subBufferedCount = 0;
    }
}
