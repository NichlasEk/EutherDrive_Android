using System.Collections;
using System.Reflection;
using Ryu64.MIPS;

internal static class CpuJitCacheChecks
{
    internal static void Run()
    {
        const BindingFlags cpu = BindingFlags.Static | BindingFlags.NonPublic;
        const BindingFlags instance = BindingFlags.Instance | BindingFlags.NonPublic;
        var reset = typeof(R4300).GetMethod("ResetCpuJitCache", cpu)!.CreateDelegate<Action>();
        var compile = typeof(R4300).GetMethod("CompileCpuJit", cpu)!.CreateDelegate<Func<uint, Func<uint, uint, bool, uint>>>();
        var run = typeof(R4300).GetMethod("TryRunCpuJit", cpu)!.CreateDelegate<Func<uint, uint, uint, uint, bool, uint>>();
        var versions = (IDictionary)typeof(R4300).GetField("CpuJitVersions", cpu)!.GetValue(null)!;
        var entries = (IDictionary)typeof(R4300).GetField("CpuJitEntries", cpu)!.GetValue(null)!;
        var cache = (Array)typeof(R4300).GetField("CpuJitCache", cpu)!.GetValue(null)!;
        var synchronous = typeof(R4300).GetField("CpuJitSynchronous", cpu)!;
        var threshold = typeof(R4300).GetField("CpuJitHotThreshold", cpu)!;
        var cycles = typeof(R4300).GetField("CycleCounter", cpu | BindingFlags.Public)!;
        object originalThreshold = threshold.GetValue(null)!;
        object originalSynchronous = synchronous.GetValue(null)!;
        object originalCycles = cycles.GetValue(null)!;
        OpcodeTable.Init();
        R4300.memory = new Memory(new byte[4096]);
        void Code(uint pc, uint first = 0x24420001)
        {
            R4300.memory.WriteUInt32(pc, first);
            R4300.memory.WriteUInt32(pc + 4, 0x03e00008); // JR ra
            R4300.memory.WriteUInt32(pc + 8, 0);
        }
        void Execute(uint pc, uint first = 0x24420001)
        {
            Registers.R4300.PC = pc; Registers.R4300.Reg[31] = 0x80040000;
            ulong before = Registers.R4300.Reg[2];
            if (run(pc, first, 3, 0, false) != 3 || Registers.R4300.PC != 0x80040000 ||
                Registers.R4300.Reg[2] != unchecked((ulong)(long)(int)((uint)before + (ushort)first)))
                throw new Exception("Cached code failed to execute the expected instructions");
        }
        try
        {
            reset(); synchronous.SetValue(null, true); threshold.SetValue(null, 1); cycles.SetValue(null, 0UL);
            for (uint i = 0; i < 128; i++)
            {
                uint pc = 0x80010000 + i * 64; Code(pc);
                if (compile(pc) == null) throw new Exception("Initial JIT cache filled prematurely");
            }
            uint next = 0x80014000; Code(next);
            if (compile(next) != null || versions.Count != 128) throw new Exception("Recent native code was evicted or cap exceeded");
            Execute(0x80010000); // Keep an address entry referring to the oldest code.
            object oldest = cache.GetValue((0x80010000u >> 2) & 65535)!;
            object oldHolder = oldest.GetType().GetField("Code", instance)!.GetValue(oldest)!;
            foreach (object holder in versions.Values)
                holder.GetType().GetField("LastUsed", instance)!.SetValue(holder, 128UL);
            oldHolder.GetType().GetField("LastUsed", instance)!.SetValue(oldHolder, 0UL);
            cycles.SetValue(null, (1UL << 27) + 128);
            Execute(0x80010040); // This code must survive the eviction.
            object active = cache.GetValue((0x80010040u >> 2) & 65535)!;
            object activeHolder = active.GetType().GetField("Code", instance)!.GetValue(active)!;
            if (compile(next) == null || versions.Count != 128) throw new Exception("Cold native code did not make room");
            if (oldHolder.GetType().GetField("Run", instance)!.GetValue(oldHolder) != null ||
                activeHolder.GetType().GetField("Run", instance)!.GetValue(activeHolder) == null)
                throw new Exception("Retirement retained old executable code or evicted active code");
            Code(0x80010000, 0x24420002); Execute(0x80010000, 0x24420002);
            Execute(0x80010040);

            // New game code must still learn through the bounded direct cache
            // after all slots in the secondary address dictionary are occupied.
            reset();
            Type entryType = cache.GetType().GetElementType()!;
            for (uint i = 0; i < 8192; i++)
            {
                object entry = Activator.CreateInstance(entryType, true)!;
                uint pc = 0x80100000 + i * 4;
                entryType.GetField("Pc", instance)!.SetValue(entry, pc);
                entries.Add(pc, entry);
            }
            Code(next); Execute(next);
            if (entries.Count != 8192 || versions.Count != 1) throw new Exception("Late address admission broke cache bounds");
            reset();
            if (versions.Count != 0 || entries.Count != 0 || cache.Cast<object>().Any(e => e != null))
                throw new Exception("Reset retained derived code/address state");
            Console.WriteLine("cpuJitCacheChecks=passed recentCode=retained coldCode=replaced retiredCode=released lateAddress=compiled nativeCap=128 reset=empty");
        }
        finally { reset(); synchronous.SetValue(null, originalSynchronous); threshold.SetValue(null, originalThreshold); cycles.SetValue(null, originalCycles); }
    }
}
