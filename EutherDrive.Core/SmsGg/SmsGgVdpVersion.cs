namespace EutherDrive.Core.SmsGg;

public enum SmsGgVdpVersion
{
    NtscMasterSystem1 = 0,
    PalMasterSystem1 = 1,
    NtscMasterSystem2 = 2,
    PalMasterSystem2 = 3,
    GameGear = 4
}

public static class SmsGgVdpVersionExtensions
{
    public static bool IsMasterSystem(this SmsGgVdpVersion version)
    {
        return version != SmsGgVdpVersion.GameGear;
    }

    public static SmsGgTimingMode TimingMode(this SmsGgVdpVersion version)
    {
        return version switch
        {
            SmsGgVdpVersion.PalMasterSystem1 or SmsGgVdpVersion.PalMasterSystem2 => SmsGgTimingMode.Pal,
            _ => SmsGgTimingMode.Ntsc
        };
    }
}
