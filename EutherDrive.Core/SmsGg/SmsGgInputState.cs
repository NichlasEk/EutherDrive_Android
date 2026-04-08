namespace EutherDrive.Core.SmsGg;

public sealed class SmsGgInputState
{
    public SmsGgPadState Player1 { get; private set; }
    public SmsGgPadState Player2 { get; private set; }
    public bool Pause { get; private set; }

    public void SetPlayer1(SmsGgPadState state) => Player1 = state;

    public void SetPause(bool pause) => Pause = pause;

    public void UpdateFromEutherInput(
        bool up,
        bool down,
        bool left,
        bool right,
        bool a,
        bool b,
        bool start)
    {
        Player1 = new SmsGgPadState(
            Up: up,
            Down: down,
            Left: left,
            Right: right,
            Button1: a,
            Button2: b);
        Pause = start;
    }
}

public record struct SmsGgPadState(
    bool Up,
    bool Down,
    bool Left,
    bool Right,
    bool Button1,
    bool Button2);
