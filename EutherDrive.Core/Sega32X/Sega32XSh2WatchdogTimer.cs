namespace EutherDrive.Core.Sega32X;

internal sealed class Sega32XSh2WatchdogTimer
{
    private byte _control;
    private byte _counter;
    private bool _enabled;
    private bool _intervalOverflow;
    private byte _systemClockShift = 1;
    private ulong _systemClockCounter;

    public byte Counter => _counter;
    public bool IntervalOverflowPending => _intervalOverflow;

    public void Reset()
    {
        _control = 0;
        _counter = 0;
        _enabled = false;
        _intervalOverflow = false;
        _systemClockShift = 1;
        _systemClockCounter = 0;
    }

    public void Tick(ulong sh2CyclesElapsed)
    {
        if (!_enabled)
            return;

        _systemClockCounter += sh2CyclesElapsed;
        ulong elapsed = _systemClockCounter >> _systemClockShift;
        _systemClockCounter &= (1UL << _systemClockShift) - 1;
        if (elapsed == 0)
            return;

        bool exceedsByte = elapsed >= 0x100;
        int nextCounter = _counter + (int)(elapsed & 0xFF);
        bool overflowed = nextCounter > 0xFF;
        _counter = (byte)nextCounter;
        _intervalOverflow |= exceedsByte || overflowed;
    }

    public byte ReadControl()
    {
        return (byte)((_intervalOverflow ? 0x80 : 0)
            | (_enabled ? 0x20 : 0)
            | (_control & 0x5F));
    }

    public void WriteControl(ushort value)
    {
        byte msb = (byte)(value >> 8);
        byte lsb = (byte)value;
        switch (msb)
        {
            case 0x5A:
                _counter = lsb;
                break;
            case 0xA5:
                _intervalOverflow &= (lsb & 0x80) != 0;
                _control = (byte)(lsb & 0xE7);
                _enabled = (lsb & 0x20) != 0;
                _systemClockShift = ComputeClockShift(lsb);
                if (!_enabled)
                    _counter = 0;
                break;
        }
    }

    private static byte ComputeClockShift(byte value)
    {
        return (value & 0x7) switch
        {
            0 => 1,
            1 => 6,
            2 => 7,
            3 => 8,
            4 => 9,
            5 => 10,
            6 => 12,
            _ => 13,
        };
    }
}
