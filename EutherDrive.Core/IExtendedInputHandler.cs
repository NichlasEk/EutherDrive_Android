namespace EutherDrive.Core;

public readonly record struct ExtendedInputState(
    bool Up,
    bool Down,
    bool Left,
    bool Right,
    bool South,
    bool East,
    bool West,
    bool North,
    bool Start,
    bool Select,
    bool Menu,
    bool L1,
    bool L2,
    bool R1,
    bool R2,
    PadType PadType);

public interface IExtendedInputHandler
{
    void SetExtendedInputState(ExtendedInputState input);
}
