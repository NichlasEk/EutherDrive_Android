namespace EutherDrive.Core.Sega32X;

internal sealed class Sega32XSh2FreeRunTimer
{
    private readonly byte[] _registers = new byte[10];
    private ushort _counterBase;
    private ulong _counterCycleBase;

    public void Reset()
    {
        Array.Clear(_registers, 0, _registers.Length);
        _counterBase = 0;
        _counterCycleBase = 0;
    }

    public byte ReadRegister(uint address, ulong cycleCounter)
    {
        if (address is 0xFFFFFE12 or 0xFFFFFE13)
        {
            ushort frc = CurrentCounter(cycleCounter);
            return address == 0xFFFFFE12 ? (byte)(frc >> 8) : (byte)frc;
        }

        return _registers[address - 0xFFFFFE10];
    }

    public void WriteRegister(uint address, byte value, ulong cycleCounter)
    {
        _registers[address - 0xFFFFFE10] = value;

        if (address is 0xFFFFFE12 or 0xFFFFFE13)
        {
            ushort frc = CurrentCounter(cycleCounter);
            frc = address == 0xFFFFFE12
                ? (ushort)((frc & 0x00FF) | (value << 8))
                : (ushort)((frc & 0xFF00) | value);
            _counterBase = frc;
            _counterCycleBase = cycleCounter;
        }
    }

    private ushort CurrentCounter(ulong cycleCounter) =>
        (ushort)(_counterBase + ((cycleCounter - _counterCycleBase) & 0xFFFF));
}
