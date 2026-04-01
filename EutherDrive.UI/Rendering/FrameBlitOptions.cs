using System;

namespace EutherDrive.Rendering;

public enum AdvancedPixelFilterProfile
{
    Default = 0,
    PsxTextSafe = 1,
    PsxInterlacedTextSafe = PsxTextSafe
}

public readonly record struct FrameBlitOptions(
    bool SharpPixels = true,
    bool ForceOpaque = false,
    bool ApplyScanlines = false,
    bool ApplyAdvancedPixelFilter = false,
    int ScanlineDarkenFactor = 256,
    AdvancedPixelFilterProfile AdvancedFilterProfile = AdvancedPixelFilterProfile.Default);

public readonly record struct FrameBlitMetrics(long LockTicks, long BlitTicks)
{
    public static FrameBlitMetrics None => default;
}
