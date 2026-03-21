using System;

namespace EutherDrive.Rendering;

public readonly record struct FrameBlitOptions(
    bool ForceOpaque = false,
    bool ApplyScanlines = false,
    bool ApplyAdvancedPixelFilter = false,
    int ScanlineDarkenFactor = 256);

public readonly record struct FrameBlitMetrics(long LockTicks, long BlitTicks)
{
    public static FrameBlitMetrics None => default;
}
