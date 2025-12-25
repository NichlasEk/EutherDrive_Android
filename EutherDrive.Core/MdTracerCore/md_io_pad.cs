using EutherDrive.Core;

namespace EutherDrive.Core.MdTracerCore;

internal struct MdPadState
{
    public bool Up, Down, Left, Right;
    public bool A, B, C, Start;
    public bool X, Y, Z, Mode;

    public void Reset()
    {
        Up = Down = Left = Right = false;
        A = B = C = Start = false;
        X = Y = Z = Mode = false;
    }
}

