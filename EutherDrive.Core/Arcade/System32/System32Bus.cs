using System;
using EutherDrive.Core.Cpu.V25Emu;
using EutherDrive.Core.Cpu.V60Emu;

namespace EutherDrive.Core.Arcade.System32;

// Sega System 32 memory maps are translated from MAME's BSD-3-Clause Sega
// System 32 driver by Aaron Giles.
internal sealed class System32Bus : IV60Bus, IV25Bus
{
    private const uint AddressMask = 0x00ff_ffff;
    private const int MainIrqVblankStart = 0;
    private const int MainIrqVblankStop = 1;

    private readonly byte[] _workRam = new byte[0x1_0000];
    private readonly byte[] _videoRam = new byte[0x2_0000];
    private readonly byte[] _spriteRam = new byte[0x2_0000];
    private readonly byte[] _paletteRam = new byte[0x1_0000];
    private readonly byte[] _mixerRam = new byte[0x80];
    private readonly byte[] _sharedRam = new byte[0x2000];
    private readonly byte[] _commShare = new byte[0x1000];
    private readonly byte[] _dpram = new byte[0x1000];
    private readonly byte[] _spriteControl = new byte[8];
    private readonly byte[] _spriteControlLatched = new byte[8];
    private readonly byte[] _ioOutputLatch = new byte[8];
    private readonly byte[] _irqControl = new byte[0x10];
    private uint _random = 0x12345678;
    private byte _ioCounter;
    private byte _ioDirection;
    private byte _tileBankExternal;
    private bool _displayEnabled;
    private byte _p1Input = 0xff;
    private byte _p2Input = 0xff;
    private byte _serviceInput = 0xff;
    private System32RomSet? _roms;

    public void Load(System32RomSet roms)
    {
        _roms = roms ?? throw new ArgumentNullException(nameof(roms));
        Reset();
    }

    public void Reset()
    {
        Array.Clear(_workRam);
        Array.Clear(_videoRam);
        Array.Clear(_spriteRam);
        Array.Clear(_paletteRam);
        Array.Fill(_mixerRam, (byte)0xff);
        Array.Clear(_sharedRam);
        Array.Clear(_commShare);
        Array.Clear(_dpram);
        Array.Clear(_spriteControl);
        Array.Clear(_spriteControlLatched);
        Array.Clear(_ioOutputLatch);
        Array.Fill(_irqControl, (byte)0xff);
        _ioCounter = 0;
        _ioDirection = 0;
        _tileBankExternal = 0;
        _displayEnabled = false;
        _p1Input = 0xff;
        _p2Input = 0xff;
        _serviceInput = 0xff;
        _random = 0x12345678;
        WriteArray16(_videoRam, 0x1ff00, 0x8000);

    }

    public byte TileBankExternal => _tileBankExternal;

    public bool DisplayEnabled => _displayEnabled;

    public void SetInput(
        bool up,
        bool down,
        bool left,
        bool right,
        bool button1,
        bool button2,
        bool button3,
        bool start,
        bool coin)
    {
        byte p1 = 0xff;
        if (button1)
            p1 &= unchecked((byte)~0x01);
        if (button2)
            p1 &= unchecked((byte)~0x02);
        if (button3)
            p1 &= unchecked((byte)~0x04);
        if (down)
            p1 &= unchecked((byte)~0x10);
        if (up)
            p1 &= unchecked((byte)~0x20);
        if (right)
            p1 &= unchecked((byte)~0x40);
        if (left)
            p1 &= unchecked((byte)~0x80);

        byte service = 0xff;
        if (coin)
            service &= unchecked((byte)~0x04);
        if (start)
            service &= unchecked((byte)~0x10);

        _p1Input = p1;
        _p2Input = 0xff;
        _serviceInput = service;
    }

    public ushort ReadVideoWord(int byteOffset)
    {
        int offset = byteOffset & 0x1fffe;
        return (ushort)(_videoRam[offset] | (_videoRam[offset + 1] << 8));
    }

    public ushort ReadPaletteWord(int colorIndex)
    {
        int offset = (colorIndex & 0x3fff) * 2;
        return (ushort)(_paletteRam[offset] | (_paletteRam[offset + 1] << 8));
    }

    public ushort ReadSpriteWord(int wordOffset)
    {
        int offset = (wordOffset * 2) & 0x1fffe;
        return (ushort)(_spriteRam[offset] | (_spriteRam[offset + 1] << 8));
    }

    public ushort ReadMixerWord(int byteOffset)
    {
        int offset = byteOffset & 0x7e;
        return (ushort)(_mixerRam[offset] | (_mixerRam[offset + 1] << 8));
    }

    public (int VideoBytes, int PaletteBytes, int SpriteBytes, ushort TextControl, ushort ScreenControl) GetVideoStats()
    {
        return (
            CountNonZero(_videoRam),
            CountNonZero(_paletteRam),
            CountNonZero(_spriteRam),
            ReadVideoWord(0x1ff5c),
            ReadVideoWord(0x1ff00));
    }

    public ushort ReadDpramWord(int byteOffset)
    {
        int offset = byteOffset & 0x07ff;
        return (ushort)(_dpram[offset] | (_dpram[(offset + 1) & 0x07ff] << 8));
    }

    public string ReadDpramAscii(int byteOffset, int length)
    {
        char[] chars = new char[length];
        for (int i = 0; i < chars.Length; i++)
        {
            byte value = _dpram[(byteOffset + i) & 0x07ff];
            chars[i] = value is >= 0x20 and <= 0x7e ? (char)value : '.';
        }

        return new string(chars);
    }

    public byte Read8(uint address)
    {
        address &= AddressMask;
        System32RomSet roms = _roms ?? throw new InvalidOperationException("Sega System 32 ROMs have not been loaded.");

        if (address <= 0x1f_ffff)
            return ReadArray(roms.MainCpu, address);
        if (address >= 0xf0_0000)
            return ReadArray(roms.MainCpu, address - 0xf0_0000);
        if (address is >= 0x20_0000 and <= 0x2f_ffff)
            return _workRam[(address - 0x20_0000) & 0xffff];
        if (address is >= 0x30_0000 and <= 0x3f_ffff)
            return _videoRam[(address - 0x30_0000) & 0x1ffff];
        if (address is >= 0x40_0000 and <= 0x4f_ffff)
            return _spriteRam[(address - 0x40_0000) & 0x1ffff];
        if (address is >= 0x50_0000 and <= 0x5f_ffff)
            return ReadSpriteControl(address);
        if (address is >= 0x60_0000 and <= 0x6f_ffff)
            return ReadPaletteAndMixer(address);
        if (address is >= 0x70_0000 and <= 0x7f_ffff)
            return _sharedRam[(address - 0x70_0000) & 0x1fff];
        if (address is >= 0x80_0000 and <= 0x80_0fff)
            return _commShare[address - 0x80_0000];
        if (address is >= 0xa0_0000 and <= 0xa0_0fff)
            return ReadMainDpram(address);
        if (address is >= 0xc0_0000 and <= 0xcf_ffff)
            return ReadIoChip(address);
        if (address is >= 0xd0_0000 and <= 0xd7_ffff)
            return ReadInterruptControl(address);
        if (address is >= 0xd8_0000 and <= 0xdf_ffff)
            return NextRandomByte();

        return 0xff;
    }

    public void Write8(uint address, byte value)
    {
        address &= AddressMask;

        if (address is >= 0x20_0000 and <= 0x2f_ffff)
            _workRam[(address - 0x20_0000) & 0xffff] = value;
        else if (address is >= 0x30_0000 and <= 0x3f_ffff)
            _videoRam[(address - 0x30_0000) & 0x1ffff] = value;
        else if (address is >= 0x40_0000 and <= 0x4f_ffff)
            _spriteRam[(address - 0x40_0000) & 0x1ffff] = value;
        else if (address is >= 0x50_0000 and <= 0x5f_ffff)
            WriteSpriteControl(address, value);
        else if (address is >= 0x60_0000 and <= 0x6f_ffff)
            WritePaletteAndMixer(address, value);
        else if (address is >= 0x70_0000 and <= 0x7f_ffff)
            _sharedRam[(address - 0x70_0000) & 0x1fff] = value;
        else if (address is >= 0x80_0000 and <= 0x80_0fff)
            _commShare[address - 0x80_0000] = value;
        else if (address is >= 0xa0_0000 and <= 0xa0_0fff)
            WriteMainDpram(address, value);
        else if (address is >= 0xc0_0000 and <= 0xcf_ffff)
            WriteIoChip(address, value);
        else if (address is >= 0xd0_0000 and <= 0xd7_ffff)
            WriteInterruptControl(address, value);
        else if (address is >= 0xd8_0000 and <= 0xdf_ffff)
            _random = unchecked(_random + value + 1u);
    }

    public byte V25Read8(uint address)
    {
        System32RomSet roms = _roms ?? throw new InvalidOperationException("Sega System 32 ROMs have not been loaded.");
        address &= 0x000f_ffff;

        if (address <= 0x0_ffff)
            return ReadArray(roms.Mcu, address);
        if (address is >= 0x1_0000 and <= 0x1_ffff)
            return _dpram[(address - 0x1_0000) & 0x07ff];
        if (address >= 0xf_0000)
            return ReadArray(roms.Mcu, address - 0xf_0000);

        return 0xff;
    }

    public void V25Write8(uint address, byte value)
    {
        address &= 0x000f_ffff;
        if (address is >= 0x1_0000 and <= 0x1_ffff)
            _dpram[(address - 0x1_0000) & 0x07ff] = value;
    }

    public void SignalVblankStartIrq()
    {
        SignalV60Irq(MainIrqVblankStart);
    }

    public void SignalVblankStopIrq()
    {
        SignalV60Irq(MainIrqVblankStop);
    }

    public int GetPendingV60InterruptVector()
    {
        int effective = _irqControl[7] & ~_irqControl[6] & 0x1f;
        for (int vector = 0; vector < 5; vector++)
        {
            if ((effective & (1 << vector)) != 0)
                return vector + 0x40;
        }

        return -1;
    }

    private byte ReadMainDpram(uint address)
    {
        uint offset = address - 0xa0_0000;
        if ((offset & 1) != 0)
            return 0xff;

        return _dpram[(offset >> 1) & 0x07ff];
    }

    private void WriteMainDpram(uint address, byte value)
    {
        uint offset = address - 0xa0_0000;
        if ((offset & 1) == 0)
            _dpram[(offset >> 1) & 0x07ff] = value;
    }

    public void EndFrame()
    {
        _spriteControl.AsSpan().CopyTo(_spriteControlLatched);
        if ((_spriteControl[0] & 0x03) != 0)
            _spriteControl[0] = 0;
    }

    private byte ReadSpriteControl(uint address)
    {
        if ((address & 1) != 0)
            return 0xff;

        int offset = (int)((address >> 1) & 7);
        return offset switch
        {
            0 => 0xfd,
            1 => 0xfd,
            2 => (byte)(0xfc | (_spriteControlLatched[2] & 0x03)),
            3 => (byte)(0xfc | (_spriteControlLatched[3] & 0x03)),
            4 => (byte)(0xfc | (_spriteControlLatched[4] & 0x03)),
            5 => (byte)(0xfc | (_spriteControlLatched[5] & 0x03)),
            6 => (byte)(0xfc | (_spriteControlLatched[6] & 0x01)),
            _ => 0xfc
        };
    }

    private void WriteSpriteControl(uint address, byte value)
    {
        if ((address & 1) != 0)
            return;

        _spriteControl[(int)((address >> 1) & 7)] = value;
    }

    private byte ReadIoChip(uint address)
    {
        if ((address & 1) != 0)
            return 0xff;

        int offset = (int)((address >> 1) & 0x0f);
        return offset switch
        {
            <= 0x07 => ((_ioDirection & (1 << offset)) != 0) ? _ioOutputLatch[offset] : ReadIoInputPort(offset),
            0x08 => (byte)'S',
            0x09 => (byte)'E',
            0x0a => (byte)'G',
            0x0b => (byte)'A',
            0x0c or 0x0e => _ioCounter,
            0x0d or 0x0f => _ioDirection,
            _ => 0xff
        };
    }

    private void WriteIoChip(uint address, byte value)
    {
        if ((address & 1) != 0)
            return;

        int offset = (int)((address >> 1) & 0x0f);
        switch (offset)
        {
            case <= 0x07:
                _ioOutputLatch[offset] = value;
                if ((_ioDirection & (1 << offset)) != 0)
                    ApplyIoOutput(offset, value);
                break;

            case 0x0e:
                _ioCounter = value;
                _displayEnabled = (value & 0x02) != 0;
                break;

            case 0x0f:
                _ioDirection = value;
                for (int port = 0; port < _ioOutputLatch.Length; port++)
                {
                    if ((_ioDirection & (1 << port)) != 0)
                        ApplyIoOutput(port, _ioOutputLatch[port]);
                }
                break;
        }
    }

    private byte ReadIoInputPort(int offset)
    {
        return offset switch
        {
            0x00 => _p1Input,
            0x01 => _p2Input,
            0x02 => 0xff,
            0x04 => _serviceInput,
            0x05 => 0xff,
            0x06 => 0xff,
            0x07 => 0xff,
            _ => 0xff
        };
    }

    private void ApplyIoOutput(int offset, byte value)
    {
        if (offset == 0x07)
            _tileBankExternal = value;
    }

    private byte ReadInterruptControl(uint address)
    {
        int offset = (int)(address & 0x0f);
        return offset is 8 or 10 ? (byte)0 : (byte)0xff;
    }

    private void WriteInterruptControl(uint address, byte value)
    {
        int offset = (int)(address & 0x0f);
        if (offset == 7)
            _irqControl[offset] &= value;
        else
            _irqControl[offset] = value;
    }

    private void SignalV60Irq(int which)
    {
        for (int i = 0; i < 5; i++)
        {
            if (_irqControl[i] == which)
                _irqControl[7] |= (byte)(1 << i);
        }
    }

    private byte NextRandomByte()
    {
        _random = unchecked(_random * 1103515245u + 12345u);
        return (byte)(_random >> 16);
    }

    private byte ReadPaletteAndMixer(uint address)
    {
        if ((address & 0xff_0000) == 0x61_0000)
            return _mixerRam[address & 0x7f];

        uint byteOffset = (address - 0x60_0000) & 0xffff;
        uint wordOffset = byteOffset >> 1;
        ushort value = ReadPaletteWord((int)wordOffset);
        if ((wordOffset & 0x4000) != 0)
            value = PackPaletteUpperFormat(value);

        return (byte)(((byteOffset & 1) == 0) ? value : value >> 8);
    }

    private void WritePaletteAndMixer(uint address, byte value)
    {
        if ((address & 0xff_0000) == 0x61_0000)
        {
            _mixerRam[address & 0x7f] = value;
            return;
        }

        uint byteOffset = (address - 0x60_0000) & 0xffff;
        uint wordOffset = byteOffset >> 1;
        int paletteOffset = (int)(wordOffset & 0x3fff) * 2;
        ushort oldValue = (ushort)(_paletteRam[paletteOffset] | (_paletteRam[paletteOffset + 1] << 8));
        bool convert = (wordOffset & 0x4000) != 0;
        ushort visibleValue = convert ? PackPaletteUpperFormat(oldValue) : oldValue;
        if ((byteOffset & 1) == 0)
            visibleValue = (ushort)((visibleValue & 0xff00) | value);
        else
            visibleValue = (ushort)((visibleValue & 0x00ff) | (value << 8));

        ushort canonical = convert ? UnpackPaletteUpperFormat(visibleValue) : visibleValue;
        _paletteRam[paletteOffset] = (byte)canonical;
        _paletteRam[paletteOffset + 1] = (byte)(canonical >> 8);
    }

    private static ushort PackPaletteUpperFormat(ushort value)
    {
        int r = (value >> 0) & 0x1f;
        int g = (value >> 5) & 0x1f;
        int b = (value >> 10) & 0x1f;
        int result = value & 0x8000;
        result |= (b & 0x01) << 14;
        result |= (g & 0x01) << 13;
        result |= (r & 0x01) << 12;
        result |= (b & 0x1e) << 7;
        result |= (g & 0x1e) << 3;
        result |= (r & 0x1e) >> 1;
        return (ushort)result;
    }

    private static ushort UnpackPaletteUpperFormat(ushort value)
    {
        int r = ((value >> 12) & 0x01) | ((value << 1) & 0x1e);
        int g = ((value >> 13) & 0x01) | ((value >> 3) & 0x1e);
        int b = ((value >> 14) & 0x01) | ((value >> 7) & 0x1e);
        return (ushort)((value & 0x8000) | (b << 10) | (g << 5) | r);
    }

    private static byte ReadArray(byte[] data, uint offset)
    {
        return offset < data.Length ? data[offset] : (byte)0xff;
    }

    private static void WriteArray16(byte[] data, int byteOffset, ushort value)
    {
        data[byteOffset] = (byte)value;
        data[byteOffset + 1] = (byte)(value >> 8);
    }

    private static int CountNonZero(byte[] data)
    {
        int count = 0;
        for (int i = 0; i < data.Length; i++)
        {
            if (data[i] != 0)
                count++;
        }

        return count;
    }
}
