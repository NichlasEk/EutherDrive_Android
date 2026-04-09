using System;
using System.Threading;

namespace EutherDrive.Core;

public enum PerfHotspot
{
    CpuStep = 0,
    VdpRender = 1,
    VdpBlit = 2,
    S32xComposite = 3,
    UiBlit = 4,
    UiLock = 5,
    UiTick = 6,
    Count = 7
}

public static class PerfHotspots
{
    private static readonly long[] Ticks = new long[(int)PerfHotspot.Count];

    public static void Add(PerfHotspot bucket, long ticks)
    {
        if (ticks <= 0)
            return;
        Interlocked.Add(ref Ticks[(int)bucket], ticks);
    }

    public static void SnapshotAndReset(Span<long> destination)
    {
        int count = Math.Min(destination.Length, Ticks.Length);
        for (int i = 0; i < count; i++)
            destination[i] = Interlocked.Exchange(ref Ticks[i], 0);
    }

    public static string GetName(PerfHotspot bucket)
    {
        return bucket switch
        {
            PerfHotspot.CpuStep => "CPU",
            PerfHotspot.VdpRender => "VDP",
            PerfHotspot.VdpBlit => "VDPBlit",
            PerfHotspot.S32xComposite => "32XComp",
            PerfHotspot.UiBlit => "UIBlit",
            PerfHotspot.UiLock => "UILock",
            PerfHotspot.UiTick => "UITick",
            _ => "Unknown"
        };
    }
}
