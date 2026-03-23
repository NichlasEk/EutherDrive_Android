using System;
using System.Runtime.CompilerServices;

namespace KSNES.Specialchips.SuperFX;

internal enum MultiplierSpeed
{
    Standard,
    High
}

internal static class MultiplierSpeedExtensions
{
    public static MultiplierSpeed FromBit(bool bit) => bit ? MultiplierSpeed.High : MultiplierSpeed.Standard;
}

internal enum ClockSpeed
{
    Slow,
    Fast
}

internal static class ClockSpeedExtensions
{
    public static ClockSpeed FromBit(bool bit) => bit ? ClockSpeed.Fast : ClockSpeed.Slow;

    public static ulong MclkDivider(this ClockSpeed speed) => speed == ClockSpeed.Fast ? 1UL : 2UL;

    public static byte MemoryAccessCycles(this ClockSpeed speed) => speed == ClockSpeed.Fast ? (byte)5 : (byte)3;

    public static byte RomBufferWaitCycles(this ClockSpeed speed) => speed == ClockSpeed.Fast ? (byte)7 : (byte)5;
}

internal enum ColorGradientColors
{
    Four,
    Sixteen,
    TwoFiftySix
}

internal static class ColorGradientColorsExtensions
{
    public static ColorGradientColors FromByte(byte value)
    {
        return (value & 0x03) switch
        {
            0x00 => ColorGradientColors.Four,
            0x01 => ColorGradientColors.Sixteen,
            _ => ColorGradientColors.TwoFiftySix
        };
    }

    public static uint TileSize(this ColorGradientColors c) => c switch
    {
        ColorGradientColors.Four => 16,
        ColorGradientColors.Sixteen => 32,
        _ => 64
    };

    public static uint Bitplanes(this ColorGradientColors c) => c switch
    {
        ColorGradientColors.Four => 2,
        ColorGradientColors.Sixteen => 4,
        _ => 8
    };

    public static byte ColorMask(this ColorGradientColors c) => c switch
    {
        ColorGradientColors.Four => 0x03,
        ColorGradientColors.Sixteen => 0x0F,
        _ => 0xFF
    };
}

internal enum ScreenHeight
{
    Bg128Pixel,
    Bg160Pixel,
    Bg192Pixel,
    ObjMode
}

internal static class ScreenHeightExtensions
{
    public static ScreenHeight FromByte(byte value)
    {
        return (value.Bit(5), value.Bit(2)) switch
        {
            (false, false) => ScreenHeight.Bg128Pixel,
            (false, true) => ScreenHeight.Bg160Pixel,
            (true, false) => ScreenHeight.Bg192Pixel,
            _ => ScreenHeight.ObjMode
        };
    }
}

internal enum BusAccess
{
    Snes,
    Gsu
}

internal static class BusAccessExtensions
{
    public static BusAccess FromBit(bool bit) => bit ? BusAccess.Gsu : BusAccess.Snes;
}

internal enum StopState
{
    None,
    StopExecuted,
    StopPending
}

internal static class StopStateExtensions
{
    public static StopState Next(this StopState state)
    {
        return state switch
        {
            StopState.None => StopState.None,
            _ => StopState.StopPending
        };
    }
}

internal sealed class GsuState
{
    public byte OpcodeBuffer = Instructions.NopOpcode;
    public byte RomBuffer;
    public byte RomBufferWaitCycles;
    public byte RamBufferWaitCycles;
    public ushort RamAddressBuffer;
    public bool RomPointerChanged;
    public bool RamBufferWritten;
    public bool JustJumped;
}

internal sealed class GraphicsSupportUnit
{
    private static readonly bool TraceBus =
        string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_SNES_SUPERFX_BUS"), "1", StringComparison.Ordinal);
    private static readonly int TraceBusLimit = 512;
    private static int _traceBusCount;

    private const byte VersionRegister = 0x04;

    public ushort[] R = new ushort[16];
    public byte RLatch;
    public byte Pbr;
    public byte Rombr;
    public CodeCache CodeCache = new();
    public GsuState State = new();
    public StopState StopState = StopState.None;
    public PlotState PlotState = new();
    public bool ZeroFlag;
    public bool CarryFlag;
    public bool SignFlag;
    public bool OverflowFlag;
    public bool Go;
    public bool Alt1;
    public bool Alt2;
    public bool B;
    public byte SReg;
    public byte DReg;
    public bool Irq;
    public bool IrqEnabled;
    public MultiplierSpeed MultiplierSpeed = MultiplierSpeed.Standard;
    public ClockSpeed ClockSpeed = ClockSpeed.Slow;
    public uint ScreenBase;
    public byte Color;
    public ColorGradientColors ColorGradient = ColorGradientColors.Four;
    public ScreenHeight ScreenHeight = ScreenHeight.Bg128Pixel;
    public bool PlotTransparentPixels;
    public bool DitherOn;
    public bool PorHighNibbleFlag;
    public bool PorFreezeHighNibble;
    public bool ForceObjMode;
    public BusAccess RomAccess = BusAccess.Snes;
    public BusAccess RamAccess = BusAccess.Snes;
    public byte WaitCycles;

    public byte? ReadRegister(uint address)
    {
        if (Go)
        {
            return (address & 0x3F) switch
            {
                0x30 => ReadSfrLow(),
                0x31 => ReadSfrHigh(),
                0x3B => VersionRegister,
                _ => null
            };
        }

        return (address & 0x3F) switch
        {
            <= 0x1F => ReadR(address),
            0x20 or 0x30 => ReadSfrLow(),
            0x21 or 0x31 => ReadSfrHigh(),
            0x24 or 0x34 => Pbr,
            0x26 or 0x36 => Rombr,
            0x2B or 0x3B => VersionRegister,
            0x2E or 0x3E => BitUtils.Msb(CodeCache.Cbr),
            0x2F or 0x3F => BitUtils.Lsb(CodeCache.Cbr),
            _ => (byte)0x00
        };
    }

    public void WriteRegister(uint address, byte value)
    {
        if (Go)
        {
            switch (address & 0xFFFF)
            {
                case 0x3030:
                    WriteSfr(value);
                    break;
                case 0x303A:
                    WriteScmr(value);
                    break;
            }
            return;
        }

        switch (address & 0xFFFF)
        {
            case >= 0x3000 and <= 0x301F:
                WriteR(address, value);
                break;
            case 0x3030:
                WriteSfr(value);
                break;
            case 0x3034:
                WritePbr(value);
                break;
            case 0x3037:
                WriteCfgr(value);
                break;
            case 0x3038:
                WriteScbr(value);
                break;
            case 0x3039:
                WriteClsr(value);
                break;
            case 0x303A:
                WriteScmr(value);
                break;
        }
    }

    public byte? ReadCodeCacheRam(uint address)
    {
        if (Go)
            return null;
        ushort ramAddr = MapSnesCodeCacheAddress(address, CodeCache.Cbr);
        return CodeCache.ReadRam(ramAddr);
    }

    public void WriteCodeCacheRam(uint address, byte value)
    {
        if (Go)
            return;
        ushort ramAddr = MapSnesCodeCacheAddress(address, CodeCache.Cbr);
        CodeCache.WriteRam(ramAddr, value);
    }

    public bool IsRunning() => Go;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Tick(ulong masterCyclesElapsed, byte[] rom, byte[] ram)
    {
        if (!Go)
        {
            WaitCycles = 0;
            return;
        }

        ulong gsuCycles = masterCyclesElapsed / ClockSpeed.MclkDivider();
        while (gsuCycles >= WaitCycles)
        {
            gsuCycles -= WaitCycles;
            WaitCycles = Instructions.Execute(this, rom, ram);
            if (!Go)
            {
                WaitCycles = 0;
                return;
            }
        }
        WaitCycles -= (byte)gsuCycles;
    }

    public bool IrqAsserted() => IrqEnabled && Irq;

    public void Reset()
    {
        Go = false;
        CodeCache.FullClear();
        Irq = false;
    }

    private byte ReadR(uint address)
    {
        int idx = (int)((address & 0x1F) >> 1);
        return !address.Bit(0) ? BitUtils.Lsb(R[idx]) : BitUtils.Msb(R[idx]);
    }

    private byte ReadSfrLow()
    {
        return (byte)((ZeroFlag ? 0x02 : 0x00)
            | (CarryFlag ? 0x04 : 0x00)
            | (SignFlag ? 0x08 : 0x00)
            | (OverflowFlag ? 0x10 : 0x00)
            | (Go ? 0x20 : 0x00)
            | (State.RomBufferWaitCycles != 0 ? 0x40 : 0x00));
    }

    private byte ReadSfrHigh()
    {
        byte value = (byte)((Alt1 ? 0x01 : 0x00)
            | (Alt2 ? 0x02 : 0x00)
            | (B ? 0x10 : 0x00)
            | (Irq ? 0x80 : 0x00));
        Irq = false;
        return value;
    }

    private void WriteR(uint address, byte value)
    {
        uint regAddress = address & 0x1F;
        if (!address.Bit(0))
        {
            RLatch = value;
        }
        else
        {
            int idx = (int)(regAddress >> 1);
            R[idx] = (ushort)(RLatch | (value << 8));
            if (TraceBus && _traceBusCount < TraceBusLimit && (idx == 14 || idx == 15))
            {
                _traceBusCount++;
                Console.WriteLine(
                    $"[SFX-BUS] reg=R{idx} val=0x{R[idx]:X4} go={(Go ? 1 : 0)} rom={RomAccess} ram={RamAccess} " +
                    $"pbr=0x{Pbr:X2} rombr=0x{Rombr:X2} r15=0x{R[15]:X4}");
            }
        }

        if (regAddress == 0x1F)
        {
            Go = true;
            State.JustJumped = true;
            if (TraceBus && _traceBusCount < TraceBusLimit)
            {
                _traceBusCount++;
                Console.WriteLine(
                    $"[SFX-BUS] reg=GO-START val=0x{R[15]:X4} go={(Go ? 1 : 0)} rom={RomAccess} ram={RamAccess} " +
                    $"pbr=0x{Pbr:X2} rombr=0x{Rombr:X2} r15=0x{R[15]:X4}");
            }
        }
    }

    private void WriteSfr(byte value)
    {
        ZeroFlag = value.Bit(1);
        CarryFlag = value.Bit(2);
        SignFlag = value.Bit(3);
        OverflowFlag = value.Bit(4);

        bool prevGo = Go;
        Go = value.Bit(5);

        if (!Go)
        {
            CodeCache.FullClear();
        }

        if (!prevGo && Go)
        {
            State.JustJumped = true;
        }

        TraceBusState("SFR", value);
    }

    private void WritePbr(byte value)
    {
        Pbr = value;
        TraceBusState("PBR", value);
    }

    private void WriteCfgr(byte value)
    {
        MultiplierSpeed = MultiplierSpeedExtensions.FromBit(value.Bit(5));
        IrqEnabled = !value.Bit(7);
    }

    private void WriteClsr(byte value)
    {
        ClockSpeed = ClockSpeedExtensions.FromBit(value.Bit(0));
    }

    private void WriteScbr(byte value)
    {
        ScreenBase = (uint)value << 10;
    }

    private void WriteScmr(byte value)
    {
        RamAccess = BusAccessExtensions.FromBit(value.Bit(3));
        RomAccess = BusAccessExtensions.FromBit(value.Bit(4));

        if (!Go)
        {
            ColorGradient = ColorGradientColorsExtensions.FromByte(value);
            ScreenHeight = ScreenHeightExtensions.FromByte(value);
        }

        TraceBusState("SCMR", value);
    }

    private static ushort MapSnesCodeCacheAddress(uint address, ushort cbr)
    {
        uint snesOffset = (address & 0xFFFF) - 0x3100;
        return (ushort)((snesOffset - (cbr & 0x1FF)) & 0x1FF);
    }

    private void TraceBusState(string reg, byte value)
    {
        if (!TraceBus || _traceBusCount >= TraceBusLimit)
            return;

        _traceBusCount++;
        Console.WriteLine(
            $"[SFX-BUS] reg={reg} val=0x{value:X2} go={(Go ? 1 : 0)} rom={RomAccess} ram={RamAccess} " +
            $"pbr=0x{Pbr:X2} rombr=0x{Rombr:X2} r15=0x{R[15]:X4}");
    }
}
