using System.Reflection;

internal static class FifoPacketMembershipChecks
{
    internal static void Run(Assembly assembly)
    {
        const string variable = "EUTHERDRIVE_GAUNTDL_EXPERIMENT_FIFO_PACKET_MEMBERSHIP";
        string? previous = Environment.GetEnvironmentVariable(variable);
        try
        {
            Type type = assembly.GetType("EutherDrive.Core.Arcade.Vegas.VoodooBringupBackend", true)!;
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            if (assembly.GetType("EutherDrive.Core.Arcade.Vegas.VoodooTraceBackend", true)!
                    .GetMethod("RebuildCommandFifoCompletePacketMembership", flags) is null)
                throw new InvalidOperationException("Trace backend cannot rebuild restored membership");
            int checks = 0;
            foreach (string mode in new[] { "0", "1" })
            {
                Environment.SetEnvironmentVariable(variable, mode);
                object backend = Activator.CreateInstance(type, nonPublic: true)!;
                void Call(string method, params object[] args) => type.GetMethod(method, flags)!.Invoke(backend, args);
                bool Contains(int header) => (bool)type.GetMethod("ContainsCompleteCommandFifoPacket", flags)!
                    .Invoke(backend, [header])!;
                var sorted = (SortedSet<int>)type.GetField("_cmdFifoCompletePacketHeaders", flags)!.GetValue(backend)!;
                var membership = (HashSet<int>?)type.GetField("_cmdFifoCompletePacketMembership", flags)!.GetValue(backend);
                void Check(bool value, string reason)
                {
                    if (!value) throw new InvalidOperationException($"FIFO membership mode={mode}: {reason}");
                    checks++;
                }
                Check((membership is null) == (mode == "0"), "Disabled index allocated storage");
                var expected = new SortedSet<int>();
                void Verify(int header)
                {
                    Check(Contains(header) == expected.Contains(header), "Membership mismatch");
                    Check(sorted.SequenceEqual(expected), "Ordered headers changed");
                    if (membership is not null) Check(membership.SetEquals(expected), "Index drifted");
                }
                int[] edges = [0, 1, -1, int.MinValue, int.MaxValue, 0x10000, 0x20000, 0x1000000];
                foreach (int header in edges)
                {
                    Call("AddCompleteCommandFifoPacket", header);
                    Call("AddCompleteCommandFifoPacket", header); // idempotent
                    expected.Add(header);
                    Verify(header);
                }
                foreach (int header in edges)
                {
                    Call("RemoveCompleteCommandFifoPacket", header);
                    Call("RemoveCompleteCommandFifoPacket", header); // repeated body overwrites
                    expected.Remove(header);
                    Verify(header);
                }
                var random = new Random(51473);
                for (int i = 0; i < 2000; i++)
                {
                    int header = random.Next(64) + random.Next(4) * 0x10000;
                    if (random.Next(2) == 0)
                    {
                        Call("AddCompleteCommandFifoPacket", header);
                        expected.Add(header);
                    }
                    else
                    {
                        Call("RemoveCompleteCommandFifoPacket", header);
                        expected.Remove(header);
                    }
                    Verify(header);
                }
                // Same restore sequence as the warm loader: canonical ordered
                // set is loaded first, then the derived index is rebuilt.
                sorted.Clear();
                sorted.UnionWith(edges);
                expected = new SortedSet<int>(edges);
                Call("RebuildCommandFifoCompletePacketMembership");
                foreach (int header in edges) Verify(header);
                Call("ClearCommandFifoStorageLastWriters");
                expected.Clear();
                foreach (int header in edges) Verify(header);
                Call("AddCompleteCommandFifoPacket", 42);
                type.GetField("_cmdFifoBulkFirstWritePending", flags)!.SetValue(backend, true);
                type.GetField("_cmdFifoBulkWriteDepth", flags)!.SetValue(backend, 1);
                type.GetMethod("WriteFifo")!.Invoke(backend, [0U, uint.MaxValue]);
                Verify(42);
            }
            Console.WriteLine($"fifoPacketMembershipChecks=passed cases:{checks}");
        }
        finally { Environment.SetEnvironmentVariable(variable, previous); }
    }
}
