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

    public SmsGgRegion ResolveRegion(SmsGgMemory memory) => ForcedRegion ?? memory.GuessCartridgeRegion();
}
