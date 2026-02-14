namespace KSNES.Specialchips.SA1;

internal enum Sa1TimerMode
{
    Hv,
    Linear
}

internal static class Sa1TimerModeExtensions
{
    public static Sa1TimerMode FromBit(bool bit) => bit ? Sa1TimerMode.Linear : Sa1TimerMode.Hv;

    public static ushort MaxH(this Sa1TimerMode mode) => mode == Sa1TimerMode.Hv ? (ushort)341 : (ushort)512;

    public static ushort MaxV(this Sa1TimerMode mode, bool isPal)
    {
        if (mode == Sa1TimerMode.Linear)
            return 512;
        return isPal ? (ushort)312 : (ushort)262;
    }
}

internal enum Sa1TimerIrqMode
{
    Off,
    H,
    V,
    Hv
}

internal static class Sa1TimerIrqModeExtensions
{
    public static Sa1TimerIrqMode FromByte(byte value)
    {
        return (value & 0x03) switch
        {
            0x00 => Sa1TimerIrqMode.Off,
            0x01 => Sa1TimerIrqMode.H,
            0x02 => Sa1TimerIrqMode.V,
            _ => Sa1TimerIrqMode.Hv
        };
    }
}

internal sealed class Sa1Timer
{
    public Sa1TimerMode Mode = Sa1TimerMode.Hv;
    public Sa1TimerIrqMode IrqMode = Sa1TimerIrqMode.Off;
    public bool IsPal;
    public ushort HCpuTicks;
    public ushort V;
    public ushort MaxHCpuTicks;
    public ushort MaxV;
    public ushort IrqHTimeCpuTicks;
    public ushort IrqVTime;
    public bool IrqPending;
    public ushort LatchedH;
    public ushort LatchedV;

    public Sa1Timer(bool isPal)
    {
        IsPal = isPal;
        MaxHCpuTicks = (ushort)(Mode.MaxH() << 1);
        MaxV = Mode.MaxV(isPal);
    }

    public byte ReadHcrLow()
    {
        LatchedH = (ushort)(HCpuTicks >> 1);
        LatchedV = V;
        return LatchedH.Lsb();
    }

    public byte ReadHcrHigh() => LatchedH.Msb();
    public byte ReadVcrLow() => LatchedV.Lsb();
    public byte ReadVcrHigh() => LatchedV.Msb();

    public void WriteTmc(byte value)
    {
        Mode = Sa1TimerModeExtensions.FromBit(value.Bit(7));
        IrqMode = Sa1TimerIrqModeExtensions.FromByte(value);
        if (IrqMode == Sa1TimerIrqMode.Off)
            IrqPending = false;
        MaxHCpuTicks = (ushort)(Mode.MaxH() << 1);
        MaxV = Mode.MaxV(IsPal);
    }

    public void WriteHcntLow(byte value)
    {
        ushort htime = (ushort)(((IrqHTimeCpuTicks >> 1) & 0x100) | value);
        IrqHTimeCpuTicks = (ushort)((htime << 1) | (IrqHTimeCpuTicks & 0x1));
    }

    public void WriteHcntHigh(byte value)
    {
        ushort htime = (ushort)(((IrqHTimeCpuTicks >> 1) & 0x0FF) | ((value & 0x01) << 8));
        IrqHTimeCpuTicks = (ushort)((htime << 1) | (IrqHTimeCpuTicks & 0x1));
    }

    public void WriteVcntLow(byte value)
    {
        IrqVTime = (ushort)((IrqVTime & 0x100) | value);
    }

    public void WriteVcntHigh(byte value)
    {
        IrqVTime = (ushort)((IrqVTime & 0x0FF) | ((value & 0x01) << 8));
    }

    public void Tick()
    {
        HCpuTicks++;

        if (HCpuTicks >= MaxHCpuTicks)
        {
            HCpuTicks = (ushort)(HCpuTicks - MaxHCpuTicks);
            V++;
            if (V >= MaxV)
                V = 0;

            if (IrqMode == Sa1TimerIrqMode.V && V == IrqVTime)
                IrqPending = true;
        }

        if (IrqMode == Sa1TimerIrqMode.H && HCpuTicks == IrqHTimeCpuTicks)
            IrqPending = true;
        else if (IrqMode == Sa1TimerIrqMode.Hv && HCpuTicks == IrqHTimeCpuTicks && V == IrqVTime)
            IrqPending = true;
    }

    public void Reset()
    {
        HCpuTicks = 0;
        V = 0;
    }
}
