using System;
using System.Globalization;

namespace EutherDrive.Core.MdTracerCore;

internal readonly struct MdCycleCounterFrameStats
{
    public MdCycleCounterFrameStats(long expectedZ80Ticks, long expectedYmTicks, long expectedPsgTicks, long z80Drift)
    {
        ExpectedZ80Ticks = expectedZ80Ticks;
        ExpectedYmTicks = expectedYmTicks;
        ExpectedPsgTicks = expectedPsgTicks;
        Z80Drift = z80Drift;
    }

    public long ExpectedZ80Ticks { get; }
    public long ExpectedYmTicks { get; }
    public long ExpectedPsgTicks { get; }
    public long Z80Drift { get; }
}

internal sealed class MdCycleCounters
{
    // Matches jgenesis timing.rs for Genesis timing domain.
    private const ulong Z80Divider = 15;
    private const ulong Ym2612Divider = 7 * 6;
    private const ulong PsgDivider = 15;

    private ulong _z80MclkCounter;
    private ulong _ymMclkCounter;
    private ulong _psgMclkCounter;
    private readonly ulong _m68kDivider;

    public MdCycleCounters(ulong m68kDivider)
    {
        _m68kDivider = m68kDivider == 0 ? 1 : m68kDivider;
        _z80MclkCounter = 0;
        _ymMclkCounter = 0;
        _psgMclkCounter = 0;
    }

    public void Reset()
    {
        _z80MclkCounter = 0;
        _ymMclkCounter = 0;
        _psgMclkCounter = 0;
    }

    public void SeedFromTotalM68kCycles(long totalM68kCycles)
    {
        if (totalM68kCycles <= 0)
        {
            Reset();
            return;
        }

        ulong total = (ulong)totalM68kCycles;
        _z80MclkCounter = MultiplyModulo(total, _m68kDivider, Z80Divider);
        _ymMclkCounter = MultiplyModulo(total, _m68kDivider, Ym2612Divider);
        _psgMclkCounter = MultiplyModulo(total, _m68kDivider, PsgDivider);
    }

    public void IncrementM68kCycles(long m68kCycles)
    {
        if (m68kCycles <= 0)
            return;

        ulong mclkCycles = (ulong)m68kCycles * _m68kDivider;
        _z80MclkCounter += mclkCycles;
        _ymMclkCounter += mclkCycles;
        _psgMclkCounter += mclkCycles;
    }

    public long TakeZ80Ticks()
    {
        ulong ticks = _z80MclkCounter / Z80Divider;
        _z80MclkCounter %= Z80Divider;
        return ticks > long.MaxValue ? long.MaxValue : (long)ticks;
    }

    public long TakeYmTicks()
    {
        ulong ticks = _ymMclkCounter / Ym2612Divider;
        _ymMclkCounter %= Ym2612Divider;
        return ticks > long.MaxValue ? long.MaxValue : (long)ticks;
    }

    public long TakePsgTicks()
    {
        ulong ticks = _psgMclkCounter / PsgDivider;
        _psgMclkCounter %= PsgDivider;
        return ticks > long.MaxValue ? long.MaxValue : (long)ticks;
    }

    public static int ParseM68kDivider()
    {
        string? raw = Environment.GetEnvironmentVariable("EUTHERDRIVE_MD_M68K_DIVIDER");
        if (string.IsNullOrWhiteSpace(raw))
            return 7;
        if (int.TryParse(raw.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int value) && value > 0)
            return value;
        return 7;
    }

    private static ulong MultiplyModulo(ulong a, ulong b, ulong mod)
    {
        if (mod == 0)
            return 0;
        ulong aMod = a % mod;
        ulong bMod = b % mod;
        return (aMod * bMod) % mod;
    }
}

internal static partial class md_main
{
    private const double GenesisMclkNtscHz = 53_693_175.0;
    private const double GenesisMclkPalHz = 53_203_424.0;
    private const bool UseMdCycleCounters = true;
    private static readonly bool UseCycleCounterZ80Scheduling =
        ReadEnvDefaultOn("EUTHERDRIVE_MD_Z80_COUNTER_SCHED", defaultValue: false);
    private static readonly bool UseCycleCounterYmDrive =
        ReadEnvDefaultOn("EUTHERDRIVE_MD_YM_COUNTER_DRIVE", defaultValue: false);
    private static readonly bool UseCycleCounterPsgDrive =
        ReadEnvDefaultOn("EUTHERDRIVE_MD_PSG_COUNTER_DRIVE", defaultValue: false);
    private static readonly bool TraceMdCycleCounters =
        ReadEnvFlag("EUTHERDRIVE_TRACE_MD_CYCLE_COUNTERS");
    private static readonly int TraceMdCycleCountersEvery =
        ParseNonNegativeInt("EUTHERDRIVE_TRACE_MD_CYCLE_COUNTERS_EVERY", 60);
    private const int MdM68kDivider = 7;
    private static MdCycleCounters _mdCycleCounters = new((ulong)MdM68kDivider);
    private static long _mdCycleCounterFrameCount;
    private static long _mdCycleCounterZ80DriftAccum;
    private static long _mdCycleCounterZ80TakenThisFrame;
    private static long _mdCycleCounterYmTakenThisFrame;
    private static long _mdCycleCounterPsgTakenThisFrame;

    private static void AdvanceCycleCounters(long m68kCycles)
    {
        if (!UseMdCycleCounters)
            return;
        _mdCycleCounters.IncrementM68kCycles(m68kCycles);
    }

    internal static void ResetCycleCounters()
    {
        _mdCycleCounters = new MdCycleCounters((ulong)MdM68kDivider);
        _mdCycleCounters.SeedFromTotalM68kCycles(_systemCycles);
        _mdCycleCounterFrameCount = 0;
        _mdCycleCounterZ80DriftAccum = 0;
        _mdCycleCounterZ80TakenThisFrame = 0;
        _mdCycleCounterYmTakenThisFrame = 0;
        _mdCycleCounterPsgTakenThisFrame = 0;
    }

    internal static bool IsCycleCounterZ80SchedulingEnabled()
    {
        return UseCycleCounterZ80Scheduling;
    }

    internal static int TakeZ80TicksForScheduling()
    {
        if (!IsCycleCounterZ80SchedulingEnabled())
            return 0;

        long ticks = _mdCycleCounters.TakeZ80Ticks();
        _mdCycleCounterZ80TakenThisFrame += ticks;
        if (ticks <= 0)
            return 0;
        return ticks > int.MaxValue ? int.MaxValue : (int)ticks;
    }

    internal static bool IsCycleCounterYmDriveEnabled()
    {
        return UseCycleCounterYmDrive;
    }

    internal static int TakeYmTicksForScheduling()
    {
        if (!IsCycleCounterYmDriveEnabled())
            return 0;

        long ticks = _mdCycleCounters.TakeYmTicks();
        _mdCycleCounterYmTakenThisFrame += ticks;
        if (ticks <= 0)
            return 0;
        return ticks > int.MaxValue ? int.MaxValue : (int)ticks;
    }

    internal static int TakePsgTicksForScheduling()
    {
        if (!UseMdCycleCounters)
            return 0;

        long ticks = _mdCycleCounters.TakePsgTicks();
        _mdCycleCounterPsgTakenThisFrame += ticks;
        if (ticks <= 0)
            return 0;
        return ticks > int.MaxValue ? int.MaxValue : (int)ticks;
    }

    internal static bool IsCycleCounterPsgDriveEnabled()
    {
        return UseCycleCounterPsgDrive;
    }

    internal static bool IsPalTimingMode()
    {
        // VDP line count is the active timing source for the running MD core.
        int lines = g_md_vdp?.g_vertical_line_max ?? 262;
        return lines >= 312;
    }

    internal static double GetGenesisMasterClockHz()
    {
        return IsPalTimingMode() ? GenesisMclkPalHz : GenesisMclkNtscHz;
    }

    internal static double GetM68kClockHzFromTiming()
    {
        return GetGenesisMasterClockHz() / 7.0;
    }

    internal static double GetYmSampleRateHzFromTiming()
    {
        // YM2612 output rate in jgenesis: mclk / 7 / 6 / 24.
        return GetGenesisMasterClockHz() / 7.0 / 6.0 / 24.0;
    }

    internal static double GetPsgSampleRateHzFromTiming()
    {
        // SN76489 output step rate in jgenesis: mclk / 15 / 16.
        return GetGenesisMasterClockHz() / 15.0 / 16.0;
    }

    internal static MdCycleCounterFrameStats ConsumeCycleCounterFrameStats(long z80ActualCycles)
    {
        if (!UseMdCycleCounters)
            return new MdCycleCounterFrameStats(0, 0, 0, 0);

        long expectedZ80 = _mdCycleCounterZ80TakenThisFrame + _mdCycleCounters.TakeZ80Ticks();
        long expectedYm = _mdCycleCounterYmTakenThisFrame + _mdCycleCounters.TakeYmTicks();
        long expectedPsg = _mdCycleCounterPsgTakenThisFrame + _mdCycleCounters.TakePsgTicks();
        long z80Drift = z80ActualCycles - expectedZ80;
        _mdCycleCounterFrameCount++;
        _mdCycleCounterZ80DriftAccum += z80Drift;
        _mdCycleCounterZ80TakenThisFrame = 0;
        _mdCycleCounterYmTakenThisFrame = 0;
        _mdCycleCounterPsgTakenThisFrame = 0;

        return new MdCycleCounterFrameStats(expectedZ80, expectedYm, expectedPsg, z80Drift);
    }

    internal static void MaybeLogCycleCounterFrame(long frame, MdCycleCounterFrameStats stats, long z80ActualCycles)
    {
        if (!UseMdCycleCounters || !TraceMdCycleCounters)
            return;
        if (TraceMdCycleCountersEvery > 0 && frame % TraceMdCycleCountersEvery != 0)
            return;

        double avgDrift = _mdCycleCounterFrameCount > 0
            ? _mdCycleCounterZ80DriftAccum / (double)_mdCycleCounterFrameCount
            : 0.0;
        Console.WriteLine(
            $"[MD-CYCLE] frame={frame} z80Actual={z80ActualCycles} z80Expected={stats.ExpectedZ80Ticks} " +
            $"z80Drift={stats.Z80Drift} z80DriftAvg={avgDrift:0.00} ymExpected={stats.ExpectedYmTicks} psgExpected={stats.ExpectedPsgTicks}");
    }

    private static bool ReadEnvDefaultOn(string name, bool defaultValue)
    {
        string? raw = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(raw))
            return defaultValue;
        return raw == "1" || raw.Equals("true", StringComparison.OrdinalIgnoreCase);
    }
}
