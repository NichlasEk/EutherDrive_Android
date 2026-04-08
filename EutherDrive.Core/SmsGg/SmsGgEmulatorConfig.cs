namespace EutherDrive.Core.SmsGg;

public sealed record SmsGgEmulatorConfig
{
    public SmsGgTimingMode SmsTimingMode { get; init; } = SmsGgTimingMode.Ntsc;
    public SmsModel SmsModel { get; init; } = SmsModel.Sms2;
    public Sn76489Version? ForcedPsgVersion { get; init; }
    public bool RemoveSpriteLimit { get; init; }
    public SmsGgRegion? ForcedRegion { get; init; }
    public bool SmsCropVerticalBorder { get; init; }
    public bool SmsCropLeftBorder { get; init; }
    public bool GgFrameBlending { get; init; }
    public bool GgUseSmsResolution { get; init; }
    public bool FmSoundUnitEnabled { get; init; }
    public uint Z80Divider { get; init; } = 15;

    public SmsGgRegion ResolveRegion(SmsGgMemory memory)
    {
        string? debugForcedRegion = Environment.GetEnvironmentVariable("EUTHERDRIVE_SMSGG_FORCE_REGION");
        if (string.Equals(debugForcedRegion, "domestic", StringComparison.OrdinalIgnoreCase))
            return SmsGgRegion.Domestic;
        if (string.Equals(debugForcedRegion, "international", StringComparison.OrdinalIgnoreCase))
            return SmsGgRegion.International;

        return ForcedRegion ?? memory.GuessCartridgeRegion();
    }
}
