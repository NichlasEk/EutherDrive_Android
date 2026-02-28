using System;

using System;
using System.IO;
using KSNES.Tracing;

namespace KSNES.Specialchips.SA1;

internal enum InterruptVectorSource
{
    Rom,
    IoPorts
}

internal static class InterruptVectorSourceExtensions
{
    public static InterruptVectorSource FromBit(bool bit) => bit ? InterruptVectorSource.IoPorts : InterruptVectorSource.Rom;
    public static bool ToBit(this InterruptVectorSource value) => value == InterruptVectorSource.IoPorts;
}

internal enum DmaSourceDevice
{
    Rom,
    Iram,
    Bwram
}

internal static class DmaSourceDeviceExtensions
{
    public static DmaSourceDevice FromByte(byte value)
    {
        return (value & 0x03) switch
        {
            0x00 => DmaSourceDevice.Rom,
            0x01 => DmaSourceDevice.Bwram,
            0x02 => DmaSourceDevice.Iram,
            _ => DmaSourceDevice.Iram
        };
    }
}

internal enum DmaDestinationDevice
{
    Iram,
    Bwram
}

internal static class DmaDestinationDeviceExtensions
{
    public static DmaDestinationDevice FromBit(bool bit) => bit ? DmaDestinationDevice.Bwram : DmaDestinationDevice.Iram;
}

internal enum DmaType
{
    Normal,
    CharacterConversion
}

internal static class DmaTypeExtensions
{
    public static DmaType FromBit(bool bit) => bit ? DmaType.CharacterConversion : DmaType.Normal;
}

internal enum DmaPriority
{
    Cpu,
    Dma
}

internal static class DmaPriorityExtensions
{
    public static DmaPriority FromBit(bool bit) => bit ? DmaPriority.Dma : DmaPriority.Cpu;
}

internal enum CharacterConversionType
{
    One,
    Two
}

internal static class CharacterConversionTypeExtensions
{
    public static CharacterConversionType FromBit(bool bit) => bit ? CharacterConversionType.Two : CharacterConversionType.One;
}

internal enum CharacterConversionColorBits
{
    Two,
    Four,
    Eight
}

internal static class CharacterConversionColorBitsExtensions
{
    public static CharacterConversionColorBits FromByte(byte value)
    {
        return (value & 0x03) switch
        {
            0x00 => CharacterConversionColorBits.Eight,
            0x01 => CharacterConversionColorBits.Four,
            _ => CharacterConversionColorBits.Two
        };
    }

    public static byte BitMask(this CharacterConversionColorBits bits)
    {
        return bits switch
        {
            CharacterConversionColorBits.Two => 0x03,
            CharacterConversionColorBits.Four => 0x0F,
            _ => 0xFF
        };
    }

    public static uint TileSize(this CharacterConversionColorBits bits)
    {
        return bits switch
        {
            CharacterConversionColorBits.Two => 16u,
            CharacterConversionColorBits.Four => 32u,
            _ => 64u
        };
    }

    public static uint Bitplanes(this CharacterConversionColorBits bits)
    {
        return bits switch
        {
            CharacterConversionColorBits.Two => 2u,
            CharacterConversionColorBits.Four => 4u,
            _ => 8u
        };
    }
}

internal enum DmaState
{
    Idle,
    NormalCopying,
    NormalWaitCycle,
    CharacterConversion2,
    CharacterConversion1Initial,
    CharacterConversion1Active
}

internal enum ArithmeticOp
{
    Multiply,
    Divide,
    MultiplyAccumulate
}

internal static class ArithmeticOpExtensions
{
    public static ArithmeticOp FromByte(byte value)
    {
        return (value & 0x03) switch
        {
            0x00 => ArithmeticOp.Multiply,
            0x01 => ArithmeticOp.Divide,
            _ => ArithmeticOp.MultiplyAccumulate
        };
    }
}

internal sealed class Sa1Registers
{
    private static readonly bool TraceSa1Regs =
        string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_SA1_REGS"), "1", StringComparison.Ordinal);
    private static readonly object TraceSa1RegsLock = new();
    private static StreamWriter? TraceSa1RegsWriter;
    public bool Sa1IrqFromSnes;
    public bool Sa1Nmi;
    public bool Sa1Reset = true;
    public bool Sa1Wait;
    public byte MessageToSa1;

    public bool SnesIrqFromSa1;
    public bool SnesIrqFromTimer;
    public bool SnesNmiFromSa1;
    public InterruptVectorSource SnesIrqVectorSource = InterruptVectorSource.Rom;
    public InterruptVectorSource SnesNmiVectorSource = InterruptVectorSource.Rom;
    public byte MessageToSnes;

    public bool SnesIrqFromSa1Enabled;
    public bool SnesIrqFromTimerEnabled;
    public bool SnesIrqFromDmaEnabled;
    public bool SnesNmiEnabled;

    public bool Sa1IrqFromSnesEnabled;
    public bool TimerIrqEnabled;
    public bool DmaIrqEnabled;
    public bool Sa1NmiEnabled;

    public ushort Sa1ResetVector;
    public ushort Sa1NmiVector;
    public ushort Sa1IrqVector;
    public ushort SnesNmiVector;
    public ushort SnesIrqVector;

    public bool SnesBwramWritesEnabled;
    public bool Sa1BwramWritesEnabled;
    public uint BwramWriteProtectionSize;

    public bool[] SnesIramWritesEnabled = new bool[8];
    public bool[] Sa1IramWritesEnabled = new bool[8];

    public DmaSourceDevice DmaSource = DmaSourceDevice.Rom;
    public DmaDestinationDevice DmaDestination = DmaDestinationDevice.Iram;
    public DmaType DmaType = DmaType.Normal;
    public CharacterConversionType CharacterConversionType = CharacterConversionType.Two;
    public DmaPriority DmaPriority = DmaPriority.Cpu;
    public bool DmaEnabled;

    public CharacterConversionColorBits CcdmaColorDepth = CharacterConversionColorBits.Eight;
    public byte VirtualVramWidthTiles = 1;
    public bool CharacterConversionIrqEnabled;

    public uint DmaSourceAddress;
    public uint DmaDestinationAddress;
    public ushort DmaTerminalCounter;

    public byte[] BitmapPixels = new byte[16];

    public ArithmeticOp ArithmeticOp = ArithmeticOp.Multiply;
    public ushort ArithmeticParamA;
    public ushort ArithmeticParamB;
    public ulong ArithmeticResult;
    public bool ArithmeticOverflow;

    public uint VarlenBitStartAddress;
    public ulong VarlenBitData;
    public int VarlenBitsRemaining;

    public DmaState DmaState = DmaState.Idle;
    public bool CcdmaTransferInProgress;
    public bool CharacterConversionIrq;
    public bool Sa1DmaIrq;

    private DmaState _lastDmaState = DmaState.Idle;

    public byte? SnesRead(uint address, Sa1Timer timer, Sa1Mmc mmc, byte[] rom)
    {
        uint addr = address & 0xFFFF;
        if (addr < 0x2300 || addr > 0x230F)
            return null;

        byte value = addr switch
        {
            0x2300 => ReadSfr(),
            0x2301 => ReadCfr(timer),
            0x2302 => timer.ReadHcrLow(),
            0x2303 => timer.ReadHcrHigh(),
            0x2304 => timer.ReadVcrLow(),
            0x2305 => timer.ReadVcrHigh(),
            >= 0x2306 and <= 0x230A => ReadMr(address),
            0x230B => ReadOf(),
            0x230C => ReadVdpLow(),
            0x230D => ReadVdpHigh(mmc, rom),
            _ => 0
        };

        if (Sa1Trace.IsEnabled)
        {
            string name = addr switch {
                0x2300 => "SFR", 0x2301 => "CFR", 0x2302 => "HCRL", 0x2303 => "HCRH",
                0x2304 => "VCRL", 0x2305 => "VCRH", 0x230B => "OF", 0x230C => "VDPL", 0x230D => "VDPH",
                >= 0x2306 and <= 0x230A => "MR", _ => "REG"
            };
            Sa1Trace.Log("SNES", 0, -1, address & 0xFFFFFF, "R", value, $"REG-{name}", null);
        }
        return value;
    }

    public byte Sa1Read(uint address, Sa1Timer timer, Sa1Mmc mmc, byte[] rom)
    {
        uint addr = address & 0xFFFF;
        byte value = addr switch
        {
            0x2301 => ReadCfr(timer),
            0x2302 => timer.ReadHcrLow(),
            0x2303 => timer.ReadHcrHigh(),
            0x2304 => timer.ReadVcrLow(),
            0x2305 => timer.ReadVcrHigh(),
            >= 0x2306 and <= 0x230A => ReadMr(address),
            0x230B => ReadOf(),
            0x230C => ReadVdpLow(),
            0x230D => ReadVdpHigh(mmc, rom),
            _ => 0
        };
        if (Sa1Trace.IsEnabled)
        {
            string name = addr switch {
                0x2301 => "CFR", 0x2302 => "HCRL", 0x2303 => "HCRH",
                0x2304 => "VCRL", 0x2305 => "VCRH", 0x230B => "OF", 0x230C => "VDPL", 0x230D => "VDPH",
                >= 0x2306 and <= 0x230A => "MR", _ => "REG"
            };
            Sa1Trace.Log("SA1", 0, -1, address & 0xFFFFFF, "R", value, $"REG-{name}", null);
        }
        return value;
    }

    public void SnesWrite(uint address, byte value, Sa1Mmc mmc, byte[] rom, byte[] iram)
    {
        uint addr = address & 0xFFFF;
        if (Sa1Trace.IsEnabled || TraceSa1Regs)
        {
            string name = addr switch
            {
                0x2200 => "CCNT", 0x2201 => "SIE", 0x2202 => "SIC",
                0x2203 => "CRV-L", 0x2204 => "CRV-H", 0x2205 => "CNV-L", 0x2206 => "CNV-H",
                0x2207 => "CIV-L", 0x2208 => "CIV-H",
                0x2220 => "CXB", 0x2221 => "DXB", 0x2222 => "EXB", 0x2223 => "FXB",
                0x2224 => "BMAP", 0x2225 => "BMAPS", 0x2226 => "SBWE", 0x2227 => "CBWE",
                0x2228 => "BWPA", 0x2229 => "SIWP", 0x222A => "CIWP",
                _ => null
            };
            if (name != null)
                LogReg($"[SA1-REGS] SNES W {name}=0x{value:X2} @ 0x{addr:X4}");
        }
        
        switch (addr)
        {
            case 0x2200: WriteCcnt(value); break;
            case 0x2201: WriteSie(value); break;
            case 0x2202: WriteSic(value); break;
            case 0x2203: WriteCrvLow(value); break;
            case 0x2204: WriteCrvHigh(value); break;
            case 0x2205: WriteCnvLow(value); break;
            case 0x2206: WriteCnvHigh(value); break;
            case 0x2207: WriteCivLow(value); break;
            case 0x2208: WriteCivHigh(value); break;
            case 0x2220: mmc.WriteCxb(value); break;
            case 0x2221: mmc.WriteDxb(value); break;
            case 0x2222: mmc.WriteExb(value); break;
            case 0x2223: mmc.WriteFxb(value); break;
            case 0x2224: mmc.WriteBmaps(value); break;
            case 0x2226: WriteSbwe(value); break;
            case 0x2228: WriteBwpa(value); break;
            case 0x2229: WriteSiwp(value); break;
            case 0x2231: WriteCdma(value); break;
            case 0x2232: WriteSdaLow(value); break;
            case 0x2233: WriteSdaMid(value); break;
            case 0x2234: WriteSdaHigh(value); break;
            case 0x2235: WriteDdaLow(value); break;
            case 0x2236: WriteDdaMid(value); break;
            case 0x2237: WriteDdaHigh(value); break;
            case 0x2238: WriteDtcLow(value); break;
            case 0x2239: WriteDtcHigh(value); break;
            case 0x223F: mmc.WriteBbf(value); break;
        }
    }

    public void Sa1Write(uint address, byte value, Sa1Timer timer, Sa1Mmc mmc, byte[] rom, byte[] iram)
    {
        uint addr = address & 0xFFFF;
        if (Sa1Trace.IsEnabled || TraceSa1Regs)
        {
            string name = addr switch
            {
                0x2209 => "SCNT", 0x220A => "CIE", 0x220B => "CIC",
                0x220C => "SNV-L", 0x220D => "SNV-H", 0x220E => "SIV-L", 0x220F => "SIV-H",
                0x2225 => "BMAPS", 0x2230 => "DCNT", 0x2231 => "CDMA",
                0x223F => "BBF", 0x2250 => "MCNT", 0x2258 => "VDA-L",
                _ => null
            };
            if (name != null)
                LogReg($"[SA1-REGS] SA1  W {name}=0x{value:X2} @ 0x{addr:X4}");
        }
        
        switch (addr)
        {
            case 0x2209: WriteScnt(value); break;
            case 0x220A: WriteCie(value); break;
            case 0x220B: WriteCic(value, timer); break;
            case 0x220C: WriteSnvLow(value); break;
            case 0x220D: WriteSnvHigh(value); break;
            case 0x220E: WriteSivLow(value); break;
            case 0x220F: WriteSivHigh(value); break;
            case 0x2210: timer.WriteTmc(value); break;
            case 0x2211: timer.Reset(); break;
            case 0x2212: timer.WriteHcntLow(value); break;
            case 0x2213: timer.WriteHcntHigh(value); break;
            case 0x2214: timer.WriteVcntLow(value); break;
            case 0x2215: timer.WriteVcntHigh(value); break;
            case 0x2225: mmc.WriteBmap(value); break;
            case 0x2227: WriteCbwe(value); break;
            case 0x222A: WriteCiwp(value); break;
            case 0x2230: WriteDcnt(value); break;
            case 0x2231: WriteCdma(value); break;
            case 0x2232: WriteSdaLow(value); break;
            case 0x2233: WriteSdaMid(value); break;
            case 0x2234: WriteSdaHigh(value); break;
            case 0x2235: WriteDdaLow(value); break;
            case 0x2236: WriteDdaMid(value); break;
            case 0x2237: WriteDdaHigh(value); break;
            case 0x2238: WriteDtcLow(value); break;
            case 0x2239: WriteDtcHigh(value); break;
            case 0x223F: mmc.WriteBbf(value); break;
            case >= 0x2240 and <= 0x224F: WriteBrf(address, value, iram); break;
            case 0x2250: WriteMcnt(value); break;
            case 0x2251: WriteMaLow(value); break;
            case 0x2252: WriteMaHigh(value); break;
            case 0x2253: WriteMbLow(value); break;
            case 0x2254: WriteMbHigh(value); break;
            case 0x2258: WriteVbd(value, mmc, rom); break;
            case 0x2259: WriteVdaLow(value); break;
            case 0x225A: WriteVdaMid(value); break;
            case 0x225B: WriteVdaHigh(value, mmc, rom); break;
        }
    }

    private byte ReadSfr()
    {
        byte value = (byte)(MessageToSnes & 0x0F);
        if (SnesIrqFromSa1) value |= 0x80;
        if (SnesIrqFromTimer) value |= 0x40;
        if (CharacterConversionIrq) value |= 0x20;
        if (SnesNmiFromSa1) value |= 0x10;
        return value;
    }

    private byte ReadCfr(Sa1Timer timer)
    {
        return (byte)((Sa1IrqFromSnes ? 0x80 : 0)
            | (timer.IrqPending ? 0x40 : 0)
            | (Sa1DmaIrq ? 0x20 : 0)
            | (Sa1Nmi ? 0x10 : 0)
            | (MessageToSa1 & 0x0F));
    }

    private byte ReadMr(uint address)
    {
        int shift = 8 * (int)((address & 0xF) - 0x6);
        return (byte)(ArithmeticResult >> shift);
    }

    private byte ReadOf() => (byte)(ArithmeticOverflow ? 0x80 : 0x00);

    public byte VarlenBitControl;

    private byte ReadVdpLow() => ((ushort)VarlenBitData).Lsb();
    private byte ReadVdpHigh(Sa1Mmc mmc, byte[] rom)
    {
        byte value = ((ushort)VarlenBitData).Msb();
        if (!VarlenBitControl.Bit(7))
        {
            ShiftVbd(mmc, rom);
        }
        return value;
    }

    private void ShiftVbd(Sa1Mmc mmc, byte[] rom)
    {
        int shift = (VarlenBitControl & 0x0F) == 0 ? 16 : (VarlenBitControl & 0x0F);
        VarlenBitData >>= shift;
        VarlenBitsRemaining -= shift;

        while (VarlenBitsRemaining <= 16)
        {
            uint romAddr = mmc.MapRomAddress(VarlenBitStartAddress) ?? 0;
            byte lsb = romAddr < rom.Length ? rom[romAddr] : (byte)0;
            byte msb = romAddr + 1 < rom.Length ? rom[romAddr + 1] : (byte)0;
            ushort word = (ushort)(lsb | (msb << 8));

            VarlenBitData |= (ulong)word << VarlenBitsRemaining;
            VarlenBitStartAddress = (VarlenBitStartAddress + 2) & 0xFFFFFF;
            VarlenBitsRemaining += 16;
        }
    }

    private void WriteCcnt(byte value)
    {
        if (value.Bit(7))
            Sa1IrqFromSnes = true;

        Sa1Wait = value.Bit(6);
        Sa1Reset = value.Bit(5);

        if (value.Bit(4))
            Sa1Nmi = true;

        MessageToSa1 = (byte)(value & 0x0F);

        if (TraceSa1Regs)
            LogReg($"[SA1-REGS] CCNT=0x{value:X2} wait={(Sa1Wait ? 1 : 0)} reset={(Sa1Reset ? 1 : 0)} msg=0x{MessageToSa1:X2}");
    }

    private void WriteSie(byte value)
    {
        SnesIrqFromSa1Enabled = value.Bit(7);
        SnesIrqFromTimerEnabled = value.Bit(6);
        SnesIrqFromDmaEnabled = value.Bit(5);
        SnesNmiEnabled = value.Bit(4);
    }

    private void WriteSic(byte value)
    {
        if (value.Bit(7))
            SnesIrqFromSa1 = false;
        if (value.Bit(6))
            SnesIrqFromTimer = false;
        if (value.Bit(5))
            CharacterConversionIrq = false;
        if (value.Bit(4))
            SnesNmiFromSa1 = false;
    }

    private void WriteCie(byte value)
    {
        Sa1IrqFromSnesEnabled = value.Bit(7);
        TimerIrqEnabled = value.Bit(6);
        DmaIrqEnabled = value.Bit(5);
        Sa1NmiEnabled = value.Bit(4);
    }

    private void WriteCic(byte value, Sa1Timer timer)
    {
        if (value.Bit(7))
            Sa1IrqFromSnes = false;
        if (value.Bit(6))
            timer.IrqPending = false;
        if (value.Bit(5))
            Sa1DmaIrq = false;
        if (value.Bit(4))
            Sa1Nmi = false;
    }

    private void WriteCrvLow(byte value)
    {
        Sa1Utils.SetLsb(ref Sa1ResetVector, value);
        if (TraceSa1Regs)
            LogReg($"[SA1-REGS] CRV=0x{Sa1ResetVector:X4}");
    }

    private void WriteCrvHigh(byte value)
    {
        Sa1Utils.SetMsb(ref Sa1ResetVector, value);
        if (TraceSa1Regs)
            LogReg($"[SA1-REGS] CRV=0x{Sa1ResetVector:X4}");
    }
    private void WriteCnvLow(byte value) => Sa1Utils.SetLsb(ref Sa1NmiVector, value);
    private void WriteCnvHigh(byte value) => Sa1Utils.SetMsb(ref Sa1NmiVector, value);
    private void WriteCivLow(byte value) => Sa1Utils.SetLsb(ref Sa1IrqVector, value);
    private void WriteCivHigh(byte value) => Sa1Utils.SetMsb(ref Sa1IrqVector, value);

    private void WriteSnvLow(byte value) => Sa1Utils.SetLsb(ref SnesNmiVector, value);
    private void WriteSnvHigh(byte value) => Sa1Utils.SetMsb(ref SnesNmiVector, value);
    private void WriteSivLow(byte value) => Sa1Utils.SetLsb(ref SnesIrqVector, value);
    private void WriteSivHigh(byte value) => Sa1Utils.SetMsb(ref SnesIrqVector, value);

    public void LogReg(string line)
    {
        lock (TraceSa1RegsLock)
        {
            TraceSa1RegsWriter ??= CreateTraceWriter();
            TraceSa1RegsWriter.WriteLine(line);
        }
    }

    private static void TraceReg(string cpu, string name, uint address, byte value)
    {
        Sa1Trace.Log(cpu, 0, -1, address & 0xFFFFFF, "W", value, $"REG-{name}", null);
    }

    private static StreamWriter CreateTraceWriter()
    {
        string baseDir = Environment.CurrentDirectory;
        string logDir = Path.Combine(baseDir, "logs");
        Directory.CreateDirectory(logDir);
        string path = Path.Combine(logDir, "sa1_regs.log");
        return new StreamWriter(File.Open(path, FileMode.Append, FileAccess.Write, FileShare.Read))
        {
            AutoFlush = true
        };
    }

    private void WriteScnt(byte value)
    {
        if (value.Bit(7))
            SnesIrqFromSa1 = true;
        if (value.Bit(4))
            SnesNmiFromSa1 = true;
        SnesIrqVectorSource = InterruptVectorSourceExtensions.FromBit(value.Bit(6));
        SnesNmiVectorSource = InterruptVectorSourceExtensions.FromBit(value.Bit(4));

        MessageToSnes = (byte)(value & 0x0F);
        if (TraceSa1Regs)
            LogReg($"[SA1-REGS] SCNT=0x{value:X2} snes_irq={(SnesIrqFromSa1 ? 1 : 0)} snes_nmi={(SnesNmiFromSa1 ? 1 : 0)} msg=0x{MessageToSnes:X2}");
    }

    private void WriteSbwe(byte value)
    {
        SnesBwramWritesEnabled = value.Bit(7);
    }

    private void WriteCbwe(byte value)
    {
        Sa1BwramWritesEnabled = value.Bit(7);
    }

    private void WriteBwpa(byte value)
    {
        byte sizeCode = (byte)(value & 0x0F);
        BwramWriteProtectionSize = sizeCode == 0 ? 0 : (uint)(0x100 << (sizeCode - 1));
    }

    private void WriteSiwp(byte value)
    {
        for (int i = 0; i < 8; i++)
            SnesIramWritesEnabled[i] = value.Bit(i);
    }

    private void WriteCiwp(byte value)
    {
        for (int i = 0; i < 8; i++)
            Sa1IramWritesEnabled[i] = value.Bit(i);
    }

    private void WriteDcnt(byte value)
    {
        DmaSource = DmaSourceDeviceExtensions.FromByte(value);
        DmaDestination = DmaDestinationDeviceExtensions.FromBit(value.Bit(2));
        CharacterConversionType = CharacterConversionTypeExtensions.FromBit(value.Bit(4));
        DmaType = DmaTypeExtensions.FromBit(value.Bit(5));
        DmaPriority = DmaPriorityExtensions.FromBit(value.Bit(6));
        DmaEnabled = value.Bit(7);
        if (Sa1Trace.IsEnabled)
            Sa1Trace.Log("SA1", 0, -1, 0x2230, "W", value, "REG-DCNT", null);
    }

    private void WriteCdma(byte value)
    {
        CcdmaColorDepth = CharacterConversionColorBitsExtensions.FromByte(value);
        VirtualVramWidthTiles = (byte)Math.Min(32, 1 << ((value >> 2) & 0x07));
        CharacterConversionIrqEnabled = value.Bit(7);

        if (Sa1Trace.IsEnabled)
            Sa1Trace.Log("SA1", 0, -1, 0x2231, "W", value, "REG-CDMA", null);
    }

    private void WriteSdaLow(byte value)
    {
        Sa1Utils.SetLowByte(ref DmaSourceAddress, value);
        if (Sa1Trace.IsEnabled)
            Sa1Trace.Log("SA1", 0, -1, 0x2232, "W", value, "REG-SDA-L", null);
    }

    private void WriteSdaMid(byte value)
    {
        Sa1Utils.SetMidByte(ref DmaSourceAddress, value);
        if (Sa1Trace.IsEnabled)
            Sa1Trace.Log("SA1", 0, -1, 0x2233, "W", value, "REG-SDA-M", null);
    }

    private void WriteSdaHigh(byte value)
    {
        Sa1Utils.SetHighByte(ref DmaSourceAddress, value);
        if (Sa1Trace.IsEnabled)
            Sa1Trace.Log("SA1", 0, -1, 0x2234, "W", value, "REG-SDA-H", null);
    }

    private void WriteDdaLow(byte value)
    {
        Sa1Utils.SetLowByte(ref DmaDestinationAddress, value);
        if (Sa1Trace.IsEnabled)
            Sa1Trace.Log("SA1", 0, -1, 0x2235, "W", value, "REG-DDA-L", null);
    }
    private void WriteDdaMid(byte value)
    {
        Sa1Utils.SetMidByte(ref DmaDestinationAddress, value);
        if (DmaEnabled && DmaType == DmaType.Normal && DmaDestination == DmaDestinationDevice.Iram)
        {
            DmaState = DmaState.NormalCopying;
            if (Sa1Trace.IsEnabled)
                Sa1Trace.Log("SA1", 0, -1, 0x2236, "S", 0, "DMA-START-IRAM", null);
        }
        if (DmaEnabled && DmaType == DmaType.CharacterConversion)
        {
            StartCharacterConversion();
        }
        if (Sa1Trace.IsEnabled)
            Sa1Trace.Log("SA1", 0, -1, 0x2236, "W", value, "REG-DDA-M", null);
    }

    private void WriteDdaHigh(byte value)
    {
        Sa1Utils.SetHighByte(ref DmaDestinationAddress, value);
        if (DmaEnabled && DmaType == DmaType.Normal && DmaDestination == DmaDestinationDevice.Bwram)
        {
            DmaState = DmaState.NormalCopying;
            if (Sa1Trace.IsEnabled)
                Sa1Trace.Log("SA1", 0, -1, 0x2237, "S", 0, "DMA-START-BWRAM", null);
        }
        if (Sa1Trace.IsEnabled)
            Sa1Trace.Log("SA1", 0, -1, 0x2237, "W", value, "REG-DDA-H", null);
    }

    private void WriteDtcLow(byte value)
    {
        Sa1Utils.SetLsb(ref DmaTerminalCounter, value);
        if (Sa1Trace.IsEnabled)
            Sa1Trace.Log("SA1", 0, -1, 0x2238, "W", value, "REG-DTC-L", null);
    }

    private void WriteDtcHigh(byte value)
    {
        Sa1Utils.SetMsb(ref DmaTerminalCounter, value);
        if (Sa1Trace.IsEnabled)
            Sa1Trace.Log("SA1", 0, -1, 0x2239, "W", value, "REG-DTC-H", null);
    }

    private void StartCharacterConversion()
    {
        DmaState = CharacterConversionType == CharacterConversionType.Two
            ? DmaState.CharacterConversion2
            : DmaState.CharacterConversion1Initial;
        if (DmaState == DmaState.CharacterConversion1Initial)
            _ccdmaInitialCyclesRemaining = (byte)CcdmaColorDepth.TileSize();
        if (DmaState == DmaState.CharacterConversion2)
        {
            _ccdmaBufferIdx = 0;
            _ccdmaRowsCopied = 0;
        }
    }

    private void WriteBrf(uint address, byte value, byte[] iram)
    {
        int idx = (int)(address & 0xF);
        BitmapPixels[idx] = (byte)(value & CcdmaColorDepth.BitMask());

        if ((idx & 0x7) == 0x7 && DmaState == DmaState.CharacterConversion2)
        {
            CharacterConversion2(idx & 0x8, iram);
        }
    }

    private void WriteMcnt(byte value)
    {
        ArithmeticOp = ArithmeticOpExtensions.FromByte(value);
        if (value.Bit(1))
            ArithmeticOverflow = false;
    }

    private void WriteMaLow(byte value) => Sa1Utils.SetLsb(ref ArithmeticParamA, value);
    private void WriteMaHigh(byte value)
    {
        Sa1Utils.SetMsb(ref ArithmeticParamA, value);
        if (ArithmeticOp == ArithmeticOp.MultiplyAccumulate)
            PerformArithmeticOp();
    }

    private void WriteMbLow(byte value) => Sa1Utils.SetLsb(ref ArithmeticParamB, value);
    private void WriteMbHigh(byte value)
    {
        Sa1Utils.SetMsb(ref ArithmeticParamB, value);
        if (ArithmeticOp != ArithmeticOp.MultiplyAccumulate)
            PerformArithmeticOp();
    }

    private void WriteVbd(byte value, Sa1Mmc mmc, byte[] rom)
    {
        VarlenBitControl = value;
        if (value.Bit(7))
        {
            ShiftVbd(mmc, rom);
        }
    }

    private void WriteVdaLow(byte value) => Sa1Utils.SetLowByte(ref VarlenBitStartAddress, value);
    private void WriteVdaMid(byte value) => Sa1Utils.SetMidByte(ref VarlenBitStartAddress, value);
    private void WriteVdaHigh(byte value, Sa1Mmc mmc, byte[] rom)
    {
        Sa1Utils.SetHighByte(ref VarlenBitStartAddress, value);
        VarlenBitData = 0;
        VarlenBitsRemaining = 0;
        ShiftVbd(mmc, rom);
    }

    public bool CanWriteBwram(uint bwramAddr, bool isSnes)
    {
        bool writeEnabled = isSnes ? SnesBwramWritesEnabled : Sa1BwramWritesEnabled;
        return writeEnabled || bwramAddr >= BwramWriteProtectionSize;
    }

    public void Reset(Sa1Timer timer, Sa1Mmc mmc)
    {
        WriteCcnt(0x20);
        WriteSie(0x00);
        WriteSic(0x00);
        WriteScnt(0x00);
        WriteCie(0x00);
        WriteCic(0x00, timer);
        timer.WriteTmc(0x00);
        WriteSbwe(0x00);
        WriteCbwe(0x00);
        WriteBwpa(0x00);
        WriteSiwp(0xFF);
        WriteCiwp(0xFF);
        WriteDcnt(0x00);
        WriteCdma(0x80);
        WriteMcnt(0x00);

        ArithmeticParamA = 0;
        ArithmeticParamB = 0;
        ArithmeticResult = 0;
        ArithmeticOverflow = false;

        VarlenBitData = 0;
        VarlenBitsRemaining = 0;

        DmaState = DmaState.Idle;
        CcdmaTransferInProgress = false;
        CharacterConversionIrq = false;
        Sa1DmaIrq = false;

        mmc.WriteCxb(0x00);
        mmc.WriteDxb(0x01);
        mmc.WriteExb(0x02);
        mmc.WriteFxb(0x03);
        mmc.WriteBmaps(0x00);
        mmc.WriteBmap(0x00);
        mmc.WriteBbf(0x00);
    }

    public bool CpuHalted()
    {
        return (DmaState == DmaState.NormalCopying || DmaState == DmaState.NormalWaitCycle)
            && (DmaPriority == DmaPriority.Dma || DmaSource == DmaSourceDevice.Rom);
    }

    private bool IsCharacterConversion()
    {
        return DmaState == DmaState.CharacterConversion2
            || DmaState == DmaState.CharacterConversion1Initial
            || DmaState == DmaState.CharacterConversion1Active;
    }

    private void PerformArithmeticOp()
    {
        const long Min = -(1L << 39);
        const long Max = (1L << 39) - 1;
        const ulong Mask = (1UL << 40) - 1;

        switch (ArithmeticOp)
        {
            case ArithmeticOp.Multiply:
            {
                long product = Multiply(ArithmeticParamA, ArithmeticParamB);
                ArithmeticResult = (ulong)product & Mask;
                break;
            }
            case ArithmeticOp.Divide:
            {
                (short quotient, ushort remainder) = Divide(ArithmeticParamA, ArithmeticParamB);
                ArithmeticResult = (ushort)quotient | ((ulong)remainder << 16);
                break;
            }
            case ArithmeticOp.MultiplyAccumulate:
            {
                long product = Multiply(ArithmeticParamA, ArithmeticParamB);
                long currentResult = (long)(ArithmeticResult << 24) >> 24;
                long sum = currentResult + product;
                ArithmeticResult = (ulong)sum & Mask;
                ArithmeticOverflow = sum < Min || sum > Max;
                break;
            }
        }
    }

    private static long Multiply(ushort a, ushort b)
    {
        return (short)a * (short)b;
    }

    private static (short Quotient, ushort Remainder) Divide(ushort a, ushort b)
    {
        if (b == 0)
        {
            return ((short)-1, a);
        }

        int dividend = (short)a;
        int divisor = (short)b;
        int quotient = dividend / divisor;
        int remainder = dividend % divisor;
        return ((short)quotient, (ushort)remainder);
    }

    private byte _ccdmaInitialCyclesRemaining;

    public void TickDma(Sa1Mmc mmc, byte[] rom, byte[] iram, byte[] bwram)
    {
        if (TraceSa1Regs && DmaState != _lastDmaState)
        {
            LogReg($"[SA1-REGS] DMA state {_lastDmaState} -> {DmaState} src={DmaSource} dst={DmaDestination} tc={DmaTerminalCounter} ccdma={CharacterConversionType}");
            _lastDmaState = DmaState;
        }
        switch (DmaState)
        {
            case DmaState.NormalCopying:
                ProgressNormalDma(mmc, rom, iram, bwram);
                break;
            case DmaState.NormalWaitCycle:
                DmaState = DmaState.NormalCopying;
                break;
            case DmaState.CharacterConversion1Initial:
                if (_ccdmaInitialCyclesRemaining <= 1)
                {
                    StartCcdmaType1(iram, bwram);
                }
                else
                {
                    _ccdmaInitialCyclesRemaining--;
                }
                break;
        }
    }

    public void NotifySnesDmaStart(uint sourceAddress)
    {
        bool isBwRamBank = sourceAddress >= 0x400000 && sourceAddress < 0x600000;
        bool isBwRamWindow = ((sourceAddress >> 16) <= 0x3F || ((sourceAddress >> 16) >= 0x80 && (sourceAddress >> 16) <= 0xBF)) 
                             && (sourceAddress & 0xFFFF) >= 0x6000 && (sourceAddress & 0xFFFF) < 0x8000;

        if (DmaState == DmaState.CharacterConversion1Active && (isBwRamBank || isBwRamWindow))
        {
            CcdmaTransferInProgress = true;
        }
    }

    public void NotifySnesDmaEnd()
    {
        CcdmaTransferInProgress = false;
    }

    private void ProgressNormalDma(Sa1Mmc mmc, byte[] rom, byte[] iram, byte[] bwram)
    {
        byte sourceByte;
        switch (DmaSource)
        {
            case DmaSourceDevice.Rom:
                {
                    uint? romAddr = mmc.MapRomAddress(DmaSourceAddress);
                    if (!romAddr.HasValue)
                    {
                        DmaState = DmaState.Idle;
                        return;
                    }
                    uint addr = romAddr.Value;
                    sourceByte = addr < rom.Length ? rom[addr] : (byte)0;
                    break;
                }
            case DmaSourceDevice.Iram:
                sourceByte = iram[(int)(DmaSourceAddress & 0x7FF)];
                break;
            default:
                sourceByte = bwram[(int)(DmaSourceAddress & (uint)(bwram.Length - 1))];
                break;
        }

        if (DmaDestination == DmaDestinationDevice.Iram)
        {
            iram[(int)(DmaDestinationAddress & 0x7FF)] = sourceByte;
        }
        else
        {
            bwram[(int)(DmaDestinationAddress & (uint)(bwram.Length - 1))] = sourceByte;
        }

        DmaSourceAddress = (DmaSourceAddress + 1) & 0xFFFFFF;
        DmaDestinationAddress = (DmaDestinationAddress + 1) & 0xFFFFFF;
        DmaTerminalCounter--;

        if (DmaTerminalCounter == 0)
        {
            DmaState = DmaState.Idle;
            Sa1DmaIrq = true;
        }
        else
        {
            DmaState = (DmaSource, DmaDestination) switch
            {
                (DmaSourceDevice.Rom, DmaDestinationDevice.Iram) => DmaState.NormalCopying,
                _ => DmaState.NormalWaitCycle
            };
        }
    }

    private void CharacterConversion2(int baseIdx, byte[] iram)
    {
        uint bufferIdx = _ccdmaBufferIdx;
        uint rowsCopied = _ccdmaRowsCopied;

        CharacterConversionColorBits colorDepth = CcdmaColorDepth;
        uint tileSize = colorDepth.TileSize();
        uint baseIramAddr = (DmaDestinationAddress & 0x7FF) + bufferIdx * tileSize + 2 * rowsCopied;

        for (int pixelIdx = 0; pixelIdx < 8; pixelIdx++)
        {
            byte pixel = BitmapPixels[baseIdx + pixelIdx];
            int shift = 7 - pixelIdx;

            for (uint plane = 0; plane < colorDepth.Bitplanes(); plane += 2)
            {
                int iramAddr = (int)((baseIramAddr + 8 * plane) & 0x7FF);
                int nextIramAddr = (iramAddr + 1) & 0x7FF;

                iram[iramAddr] = (byte)((iram[iramAddr] & ~(1 << shift)) | (((pixel >> (int)plane) & 1) << shift));
                iram[nextIramAddr] = (byte)((iram[nextIramAddr] & ~(1 << shift)) | (((pixel >> ((int)plane + 1)) & 1) << shift));
            }
        }

        rowsCopied = (rowsCopied + 1) & 0x07;
        if (rowsCopied == 0)
        {
            bufferIdx = 1 - bufferIdx;
            CharacterConversionIrq = true;
        }
        _ccdmaRowsCopied = (byte)rowsCopied;
        _ccdmaBufferIdx = (byte)bufferIdx;
        DmaState = DmaState.CharacterConversion2;
    }

    private void StartCcdmaType1(byte[] iram, byte[] bwram)
    {
        if (TraceSa1Regs)
            LogReg($"[SA1-REGS] CCDMA1 start src=0x{DmaSourceAddress:X6} dst=0x{DmaDestinationAddress:X6} vramW={VirtualVramWidthTiles} depth={CcdmaColorDepth}");
        uint sourceAddr = DmaSourceAddress & (uint)(bwram.Length - 1) & CcdmaSourceAddrMask();
        uint destAddr = DmaDestinationAddress & CcdmaDestAddrMask();

        CharacterConversion1CopyTile(sourceAddr, destAddr, 0, CcdmaColorDepth, VirtualVramWidthTiles, iram, bwram);

        DmaState = DmaState.CharacterConversion1Active;
        _ccdmaBufferIdx = 0;
        _ccdmaBytesRemaining = (byte)CcdmaColorDepth.TileSize();
        _ccdmaNextTileNumber = 1;

        CharacterConversionIrq = true;
    }

    public byte NextCcdmaByte(byte[] iram, byte[] bwram)
    {
        if (DmaState != DmaState.CharacterConversion1Active)
            return 0;

        uint tileSize = CcdmaColorDepth.TileSize();
        uint baseIramAddr = (DmaDestinationAddress & CcdmaDestAddrMask()) + (uint)_ccdmaBufferIdx * tileSize;
        uint iramAddr = baseIramAddr + tileSize - _ccdmaBytesRemaining;
        byte nextByte = iram[(int)(iramAddr & 0x7FF)];

        if (_ccdmaBytesRemaining == 1)
        {
            ProgressCcdmaType1(iram, bwram);
        }
        else
        {
            _ccdmaBytesRemaining--;
        }

        return nextByte;
    }

    private void ProgressCcdmaType1(byte[] iram, byte[] bwram)
    {
        if (TraceSa1Regs && _ccdmaNextTileNumber == 1)
            LogReg($"[SA1-REGS] CCDMA1 progress src=0x{DmaSourceAddress:X6} dst=0x{DmaDestinationAddress:X6}");
        uint bufferIdx = (uint)(1 - _ccdmaBufferIdx);
        uint sourceAddr = DmaSourceAddress & (uint)(bwram.Length - 1) & CcdmaSourceAddrMask();
        uint destAddr = (DmaDestinationAddress & CcdmaDestAddrMask()) + bufferIdx * CcdmaColorDepth.TileSize();

        CharacterConversion1CopyTile(sourceAddr, destAddr, _ccdmaNextTileNumber, CcdmaColorDepth, VirtualVramWidthTiles, iram, bwram);

        _ccdmaBufferIdx = (byte)bufferIdx;
        _ccdmaBytesRemaining = (byte)CcdmaColorDepth.TileSize();
        _ccdmaNextTileNumber++;
        
        CharacterConversionIrq = true;
    }

    private uint CcdmaSourceAddrMask()
    {
        int shift = Log2PowerOfTwo((int)CcdmaColorDepth.Bitplanes()) + Log2PowerOfTwo(VirtualVramWidthTiles) + 3;
        return ~((uint)(1 << shift) - 1);
    }

    private uint CcdmaDestAddrMask()
    {
        return CcdmaColorDepth switch
        {
            CharacterConversionColorBits.Two => ~((uint)(1 << 5) - 1),
            CharacterConversionColorBits.Four => ~((uint)(1 << 6) - 1),
            _ => ~((uint)(1 << 7) - 1)
        };
    }

    private static void CharacterConversion1CopyTile(
        uint sourceAddr,
        uint destAddr,
        uint tileNumber,
        CharacterConversionColorBits colorDepth,
        uint vramWidth,
        byte[] iram,
        byte[] bwram)
    {
        uint bitplanes = colorDepth.Bitplanes();
        uint pixelsPerByte = 8 / bitplanes;
        byte pixelMask = bitplanes == 8 ? (byte)0xFF : (byte)((1 << (int)bitplanes) - 1);

        uint sourceTileAddr = sourceAddr
            + (tileNumber & (vramWidth - 1)) * bitplanes
            + tileNumber / vramWidth * colorDepth.TileSize() * vramWidth;

        for (uint line = 0; line < 8; line++)
        {
            uint sourceLineAddr = sourceTileAddr + line * bitplanes * vramWidth;
            uint destLineAddr = destAddr + 2 * line;

            for (uint pixel = 0; pixel < 8; pixel++)
            {
                uint pixelAddr = (sourceLineAddr + pixel / pixelsPerByte) & (uint)(bwram.Length - 1);
                int pixelShift = colorDepth switch
                {
                    CharacterConversionColorBits.Two => (int)(2 * (pixel % 4)),
                    CharacterConversionColorBits.Four => (int)(4 * (pixel % 2)),
                    _ => 0
                };

                byte bmPixel = (byte)((bwram[(int)pixelAddr] >> pixelShift) & pixelMask);
                int bmShift = 7 - (int)pixel;

                for (uint plane = 0; plane < bitplanes; plane += 2)
                {
                    int iramAddr = (int)((destLineAddr + 8 * plane) & 0x7FF);
                    int nextIramAddr = (iramAddr + 1) & 0x7FF;

                    iram[iramAddr] = (byte)((iram[iramAddr] & ~(1 << bmShift)) | (((bmPixel >> (int)plane) & 1) << bmShift));
                    iram[nextIramAddr] = (byte)((iram[nextIramAddr] & ~(1 << bmShift)) | (((bmPixel >> ((int)plane + 1)) & 1) << bmShift));
                }
            }
        }
    }

    private byte _ccdmaBufferIdx;
    private byte _ccdmaRowsCopied;
    private byte _ccdmaBytesRemaining;
    private uint _ccdmaNextTileNumber;

    private static int Log2PowerOfTwo(int value)
    {
        int shift = 0;
        while ((1 << shift) < value)
            shift++;
        return shift;
    }
}
