namespace EutherDrive.Core.SmsGg;

public sealed class SmsGgMemoryControl
{
    public SmsGgMemoryControl(bool hasBios)
    {
        Reset(hasBios);
    }

    public bool CartridgeEnabled { get; set; }
    public bool BiosEnabled { get; set; }

    public void Reset(bool hasBios)
    {
        CartridgeEnabled = !hasBios;
        BiosEnabled = hasBios;
    }
}
