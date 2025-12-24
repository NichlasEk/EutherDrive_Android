using System;

namespace EutherDrive.Core.MdTracerCore
{
internal static class MdLog
{
    internal static bool Enabled
    {
        get => _enabled || TraceVdpLogging;
        set => _enabled = value;
    }

    private static bool _enabled;

    internal static readonly bool TraceZ80InstructionLogging =
        string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_Z80"), "1", StringComparison.Ordinal);

    internal static readonly bool TraceVdpLogging =
        string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_VDP"), "1", StringComparison.Ordinal);

    internal static void WriteLine(string message)
    {
        if (Enabled)
            Console.WriteLine(message);
    }
}
}
