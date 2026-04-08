namespace EutherDrive.Core.SmsGg;

public sealed class SmsGgInputPorts
{
    private PinDirection _portATr = PinDirection.Input;
    private PinDirection _portATh = PinDirection.Input;
    private PinDirection _portBTr = PinDirection.Input;
    private PinDirection _portBTh = PinDirection.Input;

    public SmsGgInputPorts(SmsGgRegion region)
    {
        Region = region;
    }

    public SmsGgInputState Inputs { get; private set; } = new();
    public SmsGgRegion Region { get; private set; }
    public bool ResetPressed { get; private set; }
    public bool PausePressed => Inputs.Pause;

    public void SetInputs(SmsGgInputState inputs)
    {
        Inputs = inputs;
    }

    public void SetRegion(SmsGgRegion region)
    {
        Region = region;
    }

    public void SetReset(bool reset)
    {
        ResetPressed = reset;
    }

    public void WriteControl(byte value, SmsGgVdp vdp)
    {
        bool prevATh = _portATh != PinDirection.Output(false);
        bool prevBTh = _portBTh != PinDirection.Output(false);

        _portBTh = ((value >> 3) & 1) != 0 ? PinDirection.Input : PinDirection.Output(((value >> 7) & 1) != 0);
        _portBTr = ((value >> 2) & 1) != 0 ? PinDirection.Input : PinDirection.Output(((value >> 6) & 1) != 0);
        _portATh = ((value >> 1) & 1) != 0 ? PinDirection.Input : PinDirection.Output(((value >> 5) & 1) != 0);
        _portATr = (value & 1) != 0 ? PinDirection.Input : PinDirection.Output(((value >> 4) & 1) != 0);

        if ((!prevATh && _portATh != PinDirection.Output(false))
            || (!prevBTh && _portBTh != PinDirection.Output(false)))
        {
            vdp.LatchHCounterOnThChange();
        }
    }

    public byte PortDc()
    {
        SmsGgPadState p1 = Inputs.Player1;
        SmsGgPadState p2 = Inputs.Player2;
        byte portATrBit = (byte)((_portATr.ReadBit(!p1.Button2) ? 1 : 0) << 5);

        return (byte)(
            ((!p2.Down ? 1 : 0) << 7) |
            ((!p2.Up ? 1 : 0) << 6) |
            portATrBit |
            ((!p1.Button1 ? 1 : 0) << 4) |
            ((!p1.Right ? 1 : 0) << 3) |
            ((!p1.Left ? 1 : 0) << 2) |
            ((!p1.Down ? 1 : 0) << 1) |
            (!p1.Up ? 1 : 0));
    }

    public byte PortDd()
    {
        SmsGgPadState p2 = Inputs.Player2;
        byte portBThBit = (byte)(((Region == SmsGgRegion.International && _portBTh.ReadBit(true)) ? 1 : 0) << 7);
        byte portAThBit = (byte)(((Region == SmsGgRegion.International && _portATh.ReadBit(true)) ? 1 : 0) << 6);
        byte portBTrBit = (byte)((_portBTr.ReadBit(!p2.Button2) ? 1 : 0) << 3);

        return (byte)(
            portBThBit |
            portAThBit |
            0x20 |
            ((!ResetPressed ? 1 : 0) << 4) |
            portBTrBit |
            ((!p2.Button1 ? 1 : 0) << 2) |
            ((!p2.Right ? 1 : 0) << 1) |
            (!p2.Left ? 1 : 0));
    }

    private record struct PinDirection(bool IsInput, bool OutputValue)
    {
        public static PinDirection Input { get; } = new(true, false);
        public static PinDirection Output(bool value) => new(false, value);

        public bool ReadBit(bool joypadValue) => IsInput ? joypadValue : OutputValue;
    }
}
