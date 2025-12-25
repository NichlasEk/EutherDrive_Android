namespace EutherDrive.Core;

public sealed class RomInfo
{
    public string Summary { get; set; } = "(no rom)";
    public ConsoleRegion? RegionHint { get; set; }
    public string RegionHeaderRaw { get; set; } = string.Empty;
    public string SerialNumber { get; set; } = string.Empty;

    public override string ToString() => Summary;
}
