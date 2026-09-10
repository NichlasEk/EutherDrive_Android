using System.Reflection;

// Run in a disposable warm-probe process: compare the emitted body against the
// existing hand-written reference on branch, signed arithmetic and side-exit edges.
internal static class GeneratedConditionalBlockChecks
{
    internal static void Run(object cpu)
    {
        const BindingFlags privateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
        Type type = cpu.GetType();
        FieldInfo Field(string name) => type.GetField(name, privateInstance)!;
        ulong[] gpr = (ulong[])Field("_gpr").GetValue(cpu)!;
        ulong[] fpr = (ulong[])Field("_fpr").GetValue(cpu)!;
        object memory = Field("_memory").GetValue(cpu)!;
        MethodInfo read = memory.GetType().GetMethod("ReadRuntimeData32")!;
        MethodInfo write = memory.GetType().GetMethod("WriteRuntimeData32")!;
        uint State() => (uint)memory.GetType().GetProperty("RuntimeMainState")!.GetValue(memory)!;
        uint Read(ulong address) => (uint)read.Invoke(memory, [address])!;
        void Write(ulong address, uint value) => write.Invoke(memory, [address, value]);
        const ulong entry = 0xffffffff800c9c98UL;
        const ulong source = 0xffffffff81e00000UL;
        const ulong destination = source + 0x100;
        uint[] words = [0xc4600000U, 0x24630004U, 0x24e70001U, 0x28e20003U,
            0xe4c00000U, 0x1440fffaU, 0x24c60004U];
        var runner = (Delegate)type.GetMethod("EmitConditionalBlock", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, [entry, words])!;
        MethodInfo reference = type.GetMethod("TryRunRuntimeCompactConditionalBlock", privateInstance)!;
        PropertyInfo pc = type.GetProperty("Pc")!;
        PropertyInfo last = type.GetProperty("LastFetchedInstruction")!;
        ulong[] initialGpr = (ulong[])gpr.Clone();
        ulong[] initialFpr = (ulong[])fpr.Clone();
        ulong[] counters = [0UL, 1UL, 2UL, 3UL, ulong.MaxValue, 0x7fffffffUL,
            0x80000000UL, 0xffffffffUL, 0x7fffffffffffffffUL, 0x8000000000000000UL];
        int checks = 0;
        foreach (ulong counter in counters)
        foreach (bool sideExit in new[] { false, true })
        foreach (uint payload in new[] { 0U, 0x80000000U, 0x7fc01234U, uint.MaxValue })
        {
            // Exercise an actual store to the runtime-state word, not just a
            // mismatched argument at entry. All payloads differ from the sentinel.
            ulong target = sideExit ? 0xffffffff80227ab0UL : destination;
            void Reset()
            {
                initialGpr.CopyTo(gpr, 0);
                initialFpr.CopyTo(fpr, 0);
                gpr[0] = 0;
                gpr[3] = source;
                gpr[6] = target;
                gpr[7] = counter;
                Write(source, payload);
                Write(target, 0xdeadbeefU);
                pc.SetValue(cpu, entry);
                last.SetValue(cpu, 0U);
                Field("_remainingProbeSteps").SetValue(cpu, 7);
                Field("_probeStepDebt").SetValue(cpu, 0);
            }
            Reset();
            uint runtimeState = State();
            if (!(bool)reference.Invoke(cpu, [runtimeState])!)
                throw new InvalidOperationException("Reference rejected the loaded loop signature");
            ulong[] expectedGpr = (ulong[])gpr.Clone();
            ulong[] expectedFpr = (ulong[])fpr.Clone();
            ulong expectedPc = (ulong)pc.GetValue(cpu)!;
            uint expectedLast = (uint)last.GetValue(cpu)!;
            uint expectedWord = Read(target);
            int expectedCount = (int)Field("_probeStepDebt").GetValue(cpu)! + 1;
            Reset();
            int count = (int)runner.DynamicInvoke(cpu, runtimeState)!;
            if (!gpr.SequenceEqual(expectedGpr) || !fpr.SequenceEqual(expectedFpr) ||
                (ulong)pc.GetValue(cpu)! != expectedPc || (uint)last.GetValue(cpu)! != expectedLast ||
                Read(target) != expectedWord || count != expectedCount)
                throw new InvalidOperationException($"Generated mismatch: counter={counter:x16} sideExit={sideExit} payload={payload:x8}");
            checks++;
        }
        Console.WriteLine($"generatedConditionalBlockChecks=passed cases:{checks}");
    }
}
