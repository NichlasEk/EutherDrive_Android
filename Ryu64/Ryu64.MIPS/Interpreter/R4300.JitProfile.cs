#if N64_CPU_JIT_PROFILE
using System.Collections.Generic;

namespace Ryu64.MIPS
{
    public partial class R4300
    {
        // CPU-thread-owned, deliberately instrumented separate from timing.
        public static bool CpuJitProfileActive;
        public static readonly Dictionary<uint, Dictionary<string, long>> CpuJitProfile = new Dictionary<uint, Dictionary<string, long>>();

        private static void ProfileCpuJit(uint pc, string reason, long value)
        {
            if (!CpuJitProfileActive || value == 0) return;
            if (!CpuJitProfile.TryGetValue(pc, out var row))
                CpuJitProfile.Add(pc, row = new Dictionary<string, long>());
            row.TryGetValue(reason, out long count);
            row[reason] = count + value;
        }
    }
}
#endif
