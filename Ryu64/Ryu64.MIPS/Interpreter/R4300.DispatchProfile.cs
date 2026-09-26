#if N64_CPU_DISPATCH_PROFILE
using System.Diagnostics;

namespace Ryu64.MIPS
{
    public partial class R4300
    {
        private static long _dispatchCalls, _dispatchAccepted, _dispatchInstructions, _dispatchTicks;
        private static long _dispatchQuietTicks, _dispatchJitCalls, _dispatchJitSuccess;
        private static long _dispatchJitInstructions, _dispatchJitTicks;
        private static long _dispatchFallbackInstructions, _dispatchFallbackTicks;
        private static long _dispatchKindReject, _dispatchModeReject, _dispatchInterruptReject;
        private static long _dispatchAddressReject, _dispatchQuietReject, _dispatchZeroResult;
        private static long _dispatchCachedSuccessor;
        private static readonly long[] _dispatchKindRejectByPrimary = new long[64];
        private static readonly long[] _dispatchSpecialRejectByFunction = new long[64];

        public static string GetCpuDispatchProfile() =>
            $"calls={_dispatchCalls} accepted={_dispatchAccepted} instructions={_dispatchInstructions} " +
            $"totalMs={_dispatchTicks * 1000.0 / Stopwatch.Frequency:F3} " +
            $"quietMs={_dispatchQuietTicks * 1000.0 / Stopwatch.Frequency:F3} " +
            $"jitCalls={_dispatchJitCalls} jitSuccess={_dispatchJitSuccess} " +
            $"jitInstructions={_dispatchJitInstructions} jitMs={_dispatchJitTicks * 1000.0 / Stopwatch.Frequency:F3} " +
            $"fallbackInstructions={_dispatchFallbackInstructions} fallbackMs={_dispatchFallbackTicks * 1000.0 / Stopwatch.Frequency:F3} " +
            $"kindReject={_dispatchKindReject} modeReject={_dispatchModeReject} " +
            $"interruptReject={_dispatchInterruptReject} addressReject={_dispatchAddressReject} " +
            $"quietReject={_dispatchQuietReject} zeroResult={_dispatchZeroResult} " +
            $"cachedSuccessor={_dispatchCachedSuccessor} " +
            "kindRejectPrimary=" + FormatCounts(_dispatchKindRejectByPrimary) + " " +
            "specialRejectFunction=" + FormatCounts(_dispatchSpecialRejectByFunction);

        private static string FormatCounts(long[] counts) => string.Join(",",
            System.Linq.Enumerable.Select(
                System.Linq.Enumerable.Where(System.Linq.Enumerable.Range(0, 64), i => counts[i] != 0),
                i => $"{i:x2}:{counts[i]}"));
    }
}
#endif
