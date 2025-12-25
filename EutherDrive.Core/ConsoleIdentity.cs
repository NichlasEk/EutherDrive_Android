namespace EutherDrive.Core;

public enum ConsoleRegion
{
    Auto = 0,
    US = 1,
    EU = 2,
    JP = 3
}

public sealed class ConsoleIdentity
{
    public ConsoleRegion RegionOverride { get; set; } = ConsoleRegion.Auto;

    public bool IsPal => RegionOverride == ConsoleRegion.EU;
    public bool IsDomestic => RegionOverride == ConsoleRegion.JP;
}
