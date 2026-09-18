using System.Reflection;

internal static class FifoPcFilterChecks
{
    internal static void Run(Assembly assembly)
    {
        Type type = assembly.GetType("EutherDrive.Core.Arcade.Vegas.VoodooBringupBackend", true)!;
        const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Static;
        var contains = type.GetMethod("ContainsCommandFifoPc", flags)!.CreateDelegate<Func<ulong[], ulong, bool>>();
        static bool Reference(ulong[] pcs, ulong pc) => pcs.Any(candidate =>
            (candidate & 0xffffffffUL) == (pc & 0xffffffffUL));
        int checks = 0;
        ulong[][] lists = [[], [0UL], [ulong.MaxValue], [0x800c4e5cUL, 0xffffffff800bc8ecUL],
            [0UL, 0x100000000UL, 0x200000000UL]];
        ulong[] probes = [0UL, 1UL, ulong.MaxValue, 0xffffffffUL, 0x800c4e5cUL,
            0xffffffff800c4e5cUL, 0x12345678800bc8ecUL];
        void Check(ulong[] pcs, ulong pc)
        {
            bool actual = contains(pcs, pc), expected = Reference(pcs, pc);
            if (actual != expected)
                throw new InvalidOperationException("FIFO PC filter changed low-32-bit/empty semantics");
            checks++;
        }
        foreach (ulong[] list in lists)
            foreach (ulong pc in probes) Check(list, pc);
        var random = new Random(18371);
        for (int i = 0; i < 2000; i++)
        {
            ulong[] pcs = Enumerable.Range(0, i % 17).Select(_ => unchecked((ulong)random.NextInt64())).ToArray();
            ulong pc = unchecked((ulong)random.NextInt64());
            if (pcs.Length > 0 && i % 2 == 0) pc = pcs[i % pcs.Length] ^ 0xffffffff00000000UL;
            Check(pcs, pc);
        }
        ulong[] measured = [1, 2, 0xffffffff800c4e5cUL];
        long Measure(Func<ulong[], ulong, bool> lookup)
        {
            int hits = 0;
            for (int i = 0; i < 1000; i++) lookup(measured, 0x800c4e5cUL);
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 100000; i++)
                if (lookup(measured, (i & 1) == 0 ? 0x800c4e5cUL : 123UL)) hits++;
            long bytes = GC.GetAllocatedBytesForCurrentThread() - before;
            if (hits != 50000) throw new InvalidOperationException("Allocation check lost lookup results");
            return bytes;
        }
        long oldBytes = Measure(Reference), newBytes = Measure(contains);
        if (newBytes != 0) throw new InvalidOperationException("Array lookup allocated");
        Console.WriteLine($"fifoPcFilterChecks=passed cases:{checks} calls:100000 oldBytes:{oldBytes} newBytes:{newBytes}");

        const string prefix = "EUTHERDRIVE_GAUNTDL_EXPERIMENT_VOODOO_FIFO_";
        string[] names = [prefix + "GATE_TYPE3_PRODUCER_BODY_HEADER", prefix + "GATE_TYPE4_PRODUCER_BODY_HEADER",
            prefix + "TYPE3_PRODUCER_HEADER_PCS", prefix + "TYPE4_PRODUCER_HEADER_PCS",
            prefix + "TYPE4_BODY_YIELD_TO_GLYPH_TYPE3_HEADER"];
        string?[] previous = names.Select(Environment.GetEnvironmentVariable).ToArray();
        try
        {
            Environment.SetEnvironmentVariable(names[0], "1");
            Environment.SetEnvironmentVariable(names[1], "1");
            Environment.SetEnvironmentVariable(names[4], "0");
            foreach (string filter in new[] { "", "0x800c4e5c,0x800bc8ec" })
            {
                Environment.SetEnvironmentVariable(names[2], filter);
                Environment.SetEnvironmentVariable(names[3], filter);
                object backend = Activator.CreateInstance(type, nonPublic: true)!;
                ulong pc = 0xffffffff800c4e5cUL;
                type.GetProperty("CpuPcProvider")!.SetValue(backend, (Func<ulong>)(() => pc));
                var track3 = type.GetMethod("TrackCommandFifoType3ProducerWord", BindingFlags.NonPublic | BindingFlags.Instance)!
                    .CreateDelegate<Action<int, uint>>(backend);
                var track4 = type.GetMethod("TrackCommandFifoType4ProducerWord", BindingFlags.NonPublic | BindingFlags.Instance)!
                    .CreateDelegate<Action<int, uint>>(backend);
                void Exercise()
                {
                    for (int i = 0; i < 10000; i++)
                    {
                        pc = (i & 1) == 0 ? 0xffffffff800c4e5cUL : 0x12345678UL;
                        track3(i & 65535, 0x43);
                        track4(i & 65535, 4);
                    }
                }
                Exercise();
                long before = GC.GetAllocatedBytesForCurrentThread();
                Exercise();
                long bytes = GC.GetAllocatedBytesForCurrentThread() - before;
                if (bytes != 0) throw new InvalidOperationException("FIFO producer caller allocated");
                const BindingFlags instance = BindingFlags.NonPublic | BindingFlags.Instance;
                bool[] body3 = (bool[])type.GetField("_cmdFifoStorageType3Body", instance)!.GetValue(backend)!;
                int end4 = (int)type.GetField("_cmdFifoType4ProducerPacketEnd", instance)!.GetValue(backend)!;
                // The last PC misses a populated filter. Empty header filters
                // instead remain wildcards and start a new packet at that word.
                if (body3[9999] != (filter.Length > 0) || end4 != (filter.Length == 0 ? 9999 : 9998))
                    throw new InvalidOperationException("FIFO caller changed empty-filter/header semantics");
                Console.WriteLine($"fifoPcFilterCallers filter={(filter.Length == 0 ? "empty" : "populated")} calls:20000 allocatedBytes:{bytes}");
            }
        }
        finally
        {
            for (int i = 0; i < names.Length; i++) Environment.SetEnvironmentVariable(names[i], previous[i]);
        }
    }
}
