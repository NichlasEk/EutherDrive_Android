using System;

namespace KSNES.Specialchips.SPC7110;

[Serializable]
internal sealed class Spc7110Registers
{
    internal enum DirectRomStep
    {
        One,
        Step
    }

    internal enum DirectRomStepTarget
    {
        Base,
        Offset
    }

    internal enum DirectRomSpecialAction
    {
        None,
        Add8Bit,
        Add16BitAfterWrite,
        Add16BitAfterRead
    }

    [Serializable]
    internal struct DirectDataRomMode
    {
        public DirectRomStep Step;
        public DirectRomStepTarget StepTarget;
        public bool OffsetEnabled;
        public bool SignExtendStep;
        public bool SignExtendOffset;
        public DirectRomSpecialAction SpecialAction;

        public static DirectDataRomMode FromByte(byte value)
        {
            return new DirectDataRomMode
            {
                Step = (value & 0x01) != 0 ? DirectRomStep.Step : DirectRomStep.One,
                StepTarget = (value & 0x10) != 0 ? DirectRomStepTarget.Offset : DirectRomStepTarget.Base,
                OffsetEnabled = (value & 0x02) != 0,
                SignExtendStep = (value & 0x04) != 0,
                SignExtendOffset = (value & 0x08) != 0,
                SpecialAction = (DirectRomSpecialAction)((value >> 5) & 0x03)
            };
        }
    }

    [Serializable]
    internal struct MathRegisters
    {
        public uint Dividend;
        public ushort Multiplier;
        public ushort Divisor;
        public uint Result;
        public ushort Remainder;
        public bool Signed;

        public void ExecuteMultiplication()
        {
            Result = Signed
                ? unchecked((uint)((short)Dividend * (short)Multiplier))
                : (uint)((Dividend & 0xFFFF) * Multiplier);
        }

        public void ExecuteDivision()
        {
            if (Divisor == 0)
            {
                Result = 0;
                Remainder = (ushort)Dividend;
                return;
            }

            if (Signed)
            {
                int dividend = unchecked((int)Dividend);
                int divisor = (short)Divisor;
                Result = unchecked((uint)(dividend / divisor));
                Remainder = unchecked((ushort)(dividend % divisor));
            }
            else
            {
                uint divisor = Divisor;
                Result = Dividend / divisor;
                Remainder = (ushort)(Dividend % divisor);
            }
        }
    }

    public bool SramEnabled;
    public byte RomBankD;
    public byte RomBankE;
    public byte RomBankF;
    public bool DirectDataRomInitialized;
    public uint DirectDataRomBase;
    public ushort DirectDataRomOffset;
    public ushort DirectDataRomStepValue;
    public DirectDataRomMode DirectDataRomModeValue;
    public byte DirectDataRomModeByte;
    public bool R4814Written;
    public bool R4815Written;
    public MathRegisters Math;
    public byte SramBank;

    public Spc7110Registers()
    {
        RomBankD = 0x00;
        RomBankE = 0x01;
        RomBankF = 0x02;
    }

    public byte ReadSramEnabled()
    {
        return (byte)(SramEnabled ? 0x80 : 0x00);
    }

    public void WriteSramEnabled(byte value)
    {
        SramEnabled = (value & 0x80) != 0;
    }

    public byte ReadDirectDataRom4810(ReadOnlySpan<byte> dataRom)
    {
        if (!DirectDataRomInitialized)
            return 0;

        DirectDataRomMode mode = DirectDataRomModeValue;
        uint romAddr = mode.OffsetEnabled
            ? (DirectDataRomBase + ExtendU16(DirectDataRomOffset, mode.SignExtendOffset)) & 0xFFFFFF
            : DirectDataRomBase;
        byte value = RomGet(dataRom, romAddr);

        uint step = mode.Step == DirectRomStep.One
            ? 1U
            : ExtendU16(DirectDataRomStepValue, mode.SignExtendStep);

        if (mode.StepTarget == DirectRomStepTarget.Base)
            DirectDataRomBase = (DirectDataRomBase + step) & 0xFFFFFF;
        else
            DirectDataRomOffset = (ushort)(DirectDataRomOffset + step);

        return value;
    }

    public byte ReadDirectDataRom481A(ReadOnlySpan<byte> dataRom)
    {
        if (!DirectDataRomInitialized)
            return 0;

        uint romAddr = (DirectDataRomBase + ExtendU16(DirectDataRomOffset, DirectDataRomModeValue.SignExtendOffset)) & 0xFFFFFF;
        byte value = RomGet(dataRom, romAddr);

        if (DirectDataRomModeValue.SpecialAction == DirectRomSpecialAction.Add16BitAfterRead)
            DirectDataRomBase = romAddr;

        return value;
    }

    public void WriteDirectDataRomBaseLow(byte value) => DirectDataRomBase = (DirectDataRomBase & 0xFFFF00) | value;
    public void WriteDirectDataRomBaseMid(byte value) => DirectDataRomBase = (DirectDataRomBase & 0xFF00FF) | ((uint)value << 8);

    public void WriteDirectDataRomBaseHigh(byte value)
    {
        DirectDataRomBase = (DirectDataRomBase & 0x00FFFF) | ((uint)value << 16);
        DirectDataRomInitialized = true;
    }

    public void WriteDirectDataRomOffsetLow(byte value)
    {
        DirectDataRomOffset = (ushort)((DirectDataRomOffset & 0xFF00) | value);
        R4814Written = true;
        if (R4814Written && R4815Written)
            ApplyModeWrite();
    }

    public void WriteDirectDataRomOffsetHigh(byte value)
    {
        DirectDataRomOffset = (ushort)((DirectDataRomOffset & 0x00FF) | (value << 8));
        R4815Written = true;
        if (R4814Written && R4815Written)
            ApplyModeWrite();
    }

    private void ApplyModeWrite()
    {
        DirectDataRomModeValue = DirectDataRomMode.FromByte(DirectDataRomModeByte);
        R4814Written = false;
        R4815Written = false;

        switch (DirectDataRomModeValue.SpecialAction)
        {
            case DirectRomSpecialAction.Add8Bit:
                DirectDataRomBase = (DirectDataRomBase + ExtendU8((byte)DirectDataRomOffset, DirectDataRomModeValue.SignExtendOffset)) & 0xFFFFFF;
                break;
            case DirectRomSpecialAction.Add16BitAfterWrite:
                DirectDataRomBase = (DirectDataRomBase + ExtendU16(DirectDataRomOffset, DirectDataRomModeValue.SignExtendOffset)) & 0xFFFFFF;
                break;
        }
    }

    public void WriteDirectDataRomStepLow(byte value)
    {
        DirectDataRomStepValue = (ushort)((DirectDataRomStepValue & 0xFF00) | value);
    }

    public void WriteDirectDataRomStepHigh(byte value)
    {
        DirectDataRomStepValue = (ushort)((DirectDataRomStepValue & 0x00FF) | (value << 8));
    }

    public void WriteDirectDataRomMode(byte value)
    {
        DirectDataRomModeByte = value;
        R4814Written = false;
        R4815Written = false;
        DirectDataRomOffset = 0;
    }

    public byte ReadDividend(uint address)
    {
        int shift = 8 * (int)(address & 0x03);
        return (byte)(Math.Dividend >> shift);
    }

    public void WriteDividend(uint address, byte value)
    {
        int shift = 8 * (int)(address & 0x03);
        Math.Dividend = (Math.Dividend & ~(0xFFu << shift)) | ((uint)value << shift);
    }

    public void WriteMultiplierLow(byte value) => Math.Multiplier = (ushort)((Math.Multiplier & 0xFF00) | value);

    public void WriteMultiplierHigh(byte value)
    {
        Math.Multiplier = (ushort)((Math.Multiplier & 0x00FF) | (value << 8));
        Math.ExecuteMultiplication();
    }

    public void WriteDivisorLow(byte value) => Math.Divisor = (ushort)((Math.Divisor & 0xFF00) | value);

    public void WriteDivisorHigh(byte value)
    {
        Math.Divisor = (ushort)((Math.Divisor & 0x00FF) | (value << 8));
        Math.ExecuteDivision();
    }

    public byte ReadMathResult(uint address)
    {
        int shift = 8 * (int)(address & 0x03);
        return (byte)(Math.Result >> shift);
    }

    public void WriteMathMode(byte value)
    {
        Math.Signed = (value & 0x01) != 0;
    }

    private static byte RomGet(ReadOnlySpan<byte> dataRom, uint address)
    {
        return address < dataRom.Length ? dataRom[(int)address] : (byte)0;
    }

    private static uint ExtendU8(byte value, bool signExtend)
    {
        return signExtend ? unchecked((uint)(sbyte)value) & 0xFFFFFF : value;
    }

    private static uint ExtendU16(ushort value, bool signExtend)
    {
        return signExtend ? unchecked((uint)(short)value) & 0xFFFFFF : value;
    }
}
