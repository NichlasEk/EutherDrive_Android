using System;

namespace KSNES.Specialchips.ST010;

internal sealed class Registers
{
    public ushort Dp;
    public ushort Rp;
    public ushort Pc;
    public readonly ushort[] Stack = new ushort[8]; // 8-level stack for ST010/ST011 (vs 4 for DSP1)
    public byte StackIndex;
    public short K;
    public short L;
    public short AccA;
    public short AccB;
    public Flags FlagsA;
    public Flags FlagsB;
    public short Tr;
    public short Trb;
    public Status Status;
    public ushort Dr;
    public ushort So;
    public Action<ushort>? OnUpdWriteData;
    public bool SwapIoBytes;

    public void Reset()
    {
        Dp = 0;
        Rp = 0x7FF; // Different initial RP for ST010/ST011
        Pc = 0;
        StackIndex = 0;
        K = 0;
        L = 0;
        AccA = 0;
        AccB = 0;
        FlagsA = new Flags();
        FlagsB = new Flags();
        Tr = 0;
        Trb = 0;
        Status = new Status();
        Dr = 0;
        So = 0;
    }

    public byte SnesReadData()
    {
        if (Status.DrControl == DataRegisterBits.Eight)
        {
            Status.RequestForMaster = false;
            return (byte)(Dr & 0xFF);
        }

        if (Status.DrBusy)
        {
            Status.DrBusy = false;
            Status.RequestForMaster = false;
            return SwapIoBytes ? (byte)(Dr & 0xFF) : (byte)(Dr >> 8);
        }

        Status.DrBusy = true;
        return SwapIoBytes ? (byte)(Dr >> 8) : (byte)(Dr & 0xFF);
    }

    public bool SnesWriteData(byte value, out ushort word)
    {
        word = 0;
        if (Status.DrControl == DataRegisterBits.Eight)
        {
            Status.RequestForMaster = false;
            Dr = value;
            word = Dr;
            return true;
        }

        if (Status.DrBusy)
        {
            Status.DrBusy = false;
            Status.RequestForMaster = false;
            if (SwapIoBytes)
                Dr = (ushort)((Dr & 0xFF00) | value);
            else
                Dr = (ushort)((Dr & 0x00FF) | (value << 8));
            word = Dr;
            return true;
        }

        Status.DrBusy = true;
        if (SwapIoBytes)
            Dr = (ushort)((Dr & 0x00FF) | (value << 8));
        else
            Dr = (ushort)((Dr & 0xFF00) | value);
        return false;
    }

    public void UpdWriteData(ushort value)
    {
        Dr = value;
        Status.RequestForMaster = true;
        OnUpdWriteData?.Invoke(value);
    }

    public void PushStack(ushort pc)
    {
        Stack[StackIndex & 0x07] = pc;
        StackIndex = (byte)((StackIndex + 1) & 0x07);
    }

    public ushort PopStack()
    {
        StackIndex = (byte)((StackIndex - 1) & 0x07);
        return Stack[StackIndex & 0x07];
    }

    public int KlProduct() => K * L;
}
