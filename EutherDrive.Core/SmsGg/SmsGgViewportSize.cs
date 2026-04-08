namespace EutherDrive.Core.SmsGg;

public record struct SmsGgViewportSize(
    ushort Width,
    ushort Height,
    ushort Top,
    ushort Left,
    ushort TopBorderHeight,
    ushort BottomBorderHeight,
    ushort LeftBorderWidth)
{
    public static SmsGgViewportSize NtscSms { get; } = new(256, 224, 0, 0, 16, 16, 8);
    public static SmsGgViewportSize PalSms { get; } = new(256, 240, 0, 0, 24, 24, 8);
    public static SmsGgViewportSize GameGear { get; } = new(160, 144, 24, 48, 0, 0, 0);
    public static SmsGgViewportSize GameGearExpanded { get; } = new(256, 192, 0, 0, 0, 0, 8);

    public ushort HeightWithoutBorder => (ushort)(Height - TopBorderHeight - BottomBorderHeight);
    public ushort WidthWithoutBorder => (ushort)(Width - LeftBorderWidth);
}
