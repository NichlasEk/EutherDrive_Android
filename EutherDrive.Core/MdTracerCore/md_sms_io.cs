using EutherDrive.Core;

namespace EutherDrive.Core.MdTracerCore;

internal static class md_sms_io
{
    private static MdPadState _pad1;
    private static MdPadState _pad2;
    private static bool _prevPause;

    internal static void SetPad1Input(in MdPadState state)
    {
        _pad1 = state;
        HandlePauseEdge(state.Start);
    }

    internal static void SetPad2Input(in MdPadState state)
    {
        _pad2 = state;
    }

    internal static bool TryReadPort(ushort port, out byte value)
    {
        value = 0xFF;
        switch (port)
        {
            case 0xDC:
                value = BuildPadValue(_pad1);
                return true;
            case 0xDD:
                value = BuildPadValue(_pad2);
                return true;
            default:
                return false;
        }
    }

    private static void HandlePauseEdge(bool pausePressed)
    {
        if (!md_main.g_masterSystemMode)
        {
            _prevPause = pausePressed;
            return;
        }

        if (pausePressed && !_prevPause)
            md_main.g_md_z80?.RequestNmi();

        _prevPause = pausePressed;
    }

    private static byte BuildPadValue(in MdPadState pad)
    {
        byte value = 0xFF;
        if (pad.Up) value &= 0xFE;     // bit 0
        if (pad.Down) value &= 0xFD;   // bit 1
        if (pad.Left) value &= 0xFB;   // bit 2
        if (pad.Right) value &= 0xF7;  // bit 3
        if (pad.A) value &= 0xEF;      // bit 4 (Button 1)
        if (pad.B) value &= 0xDF;      // bit 5 (Button 2)
        return value;
    }
}
