using System.Reflection;

internal static class SafeCop1DispatchChecks
{
    internal static void Run(Assembly assembly)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        Type memoryType = assembly.GetType("EutherDrive.Core.Arcade.Vegas.VegasMemoryMap", true)!;
        Type cpuType = assembly.GetType("EutherDrive.Core.Arcade.Vegas.MipsR5000Core", true)!;
        Type decodedType = cpuType.GetNestedType("RuntimeSafeInstruction", BindingFlags.NonPublic)!;
        object reference = Activator.CreateInstance(cpuType, [Activator.CreateInstance(memoryType)!])!;
        object candidate = Activator.CreateInstance(cpuType, [Activator.CreateInstance(memoryType)!])!;
        MethodInfo execute = cpuType.GetMethod("Execute", flags)!;
        MethodInfo safe = cpuType.GetMethod("ExecuteRuntimeSafeInstruction", flags)!;
        MethodInfo reset = cpuType.GetMethod("Reset")!;
        FieldInfo[] fields = cpuType.GetFields(flags).Where(f => f.FieldType.IsPrimitive ||
            f.FieldType.IsArray && f.FieldType.GetElementType()!.IsPrimitive).ToArray();
        var random = new Random(1979);
        ulong[] edge = [0, 1, ulong.MaxValue, 0x8000000000000000, 0x7fffffffffffffff,
            0x80000000, 0x7fffffff, 0x7f800000, 0xff800000, 0x7fc01234, 0x80000001];
        int checks = 0;
        foreach (bool profiling in new[] { false, true })
        foreach (uint format in new uint[] { 0, 1, 2, 4, 5, 6, 8, 16 })
        foreach (uint function in format == 16
            ? new uint[] { 0, 1, 2, 3, 4, 5, 6, 7, 12, 13, 14, 15, 17, 18, 19, 21, 22, 32, 33, 36, 50, 60, 62 }
            : new uint[] { 0 })
        for (int sample = 0; sample < 32; sample++)
        {
            uint rt = (uint)sample;
            uint rd = (uint)(sample % 3 == 0 ? sample : (sample + 7) & 31);
            uint fd = (uint)(sample % 2 == 0 ? sample : (sample + 13) & 31);
            Check(0x44000000U | format << 21 | rt << 16 | rd << 11 | fd << 6 | function, profiling);
        }
        foreach (uint function in new uint[] { 15, 32, 33, 40, 41, 48, 49, 56, 57 })
        for (int sample = 0; sample < 32; sample++)
            Check(0x4c000000U | (uint)sample << 21 | (uint)sample << 16 |
                (uint)((sample + 1) & 31) << 11 | (uint)sample << 6 | function, false);
        foreach (uint opcode in new uint[] { 24, 25 })
        foreach (ushort immediate in new ushort[] { 0, 1, 0x7fff, 0x8000, 0xffff })
        for (int register = 0; register < 32; register++)
            Check(opcode << 26 | (uint)register << 21 | (uint)register << 16 | immediate, false);
        Console.WriteLine($"safeCop1Dispatch checks={checks}");

        void Check(uint op, bool profiling)
        {
            reset.Invoke(reference, null);
            reset.Invoke(candidate, null);
            cpuType.GetField("_profileOpcodes", flags)!.SetValue(reference, profiling);
            cpuType.GetField("_profileOpcodes", flags)!.SetValue(candidate, profiling);
            foreach (string name in new[] { "_gpr", "_fpr" })
            {
                var a = (ulong[])cpuType.GetField(name, flags)!.GetValue(reference)!;
                var b = (ulong[])cpuType.GetField(name, flags)!.GetValue(candidate)!;
                for (int i = 0; i < a.Length; i++)
                    a[i] = b[i] = (checks + i) % 3 == 0 ? edge[(checks + i) % edge.Length]
                        : unchecked((ulong)random.NextInt64() ^ ((ulong)random.NextInt64() << 32));
            }
            var referenceControl = (uint[])cpuType.GetField("_fcr", flags)!.GetValue(reference)!;
            var candidateControl = (uint[])cpuType.GetField("_fcr", flags)!.GetValue(candidate)!;
            for (int i = 0; i < referenceControl.Length; i++)
                referenceControl[i] = candidateControl[i] = unchecked((uint)random.NextInt64());
            const ulong pc = 0xffffffff80010000UL;
            execute.Invoke(reference, [pc, op, true]);
            safe.Invoke(candidate, [pc, Activator.CreateInstance(decodedType, [op])!]);
            foreach (FieldInfo field in fields)
            {
                object? a = field.GetValue(reference), b = field.GetValue(candidate);
                bool equal = a is Array aa && b is Array bb
                    ? aa.Cast<object>().SequenceEqual(bb.Cast<object>()) : Equals(a, b);
                if (!equal)
                    throw new InvalidOperationException($"op={op:x8} profile={profiling}: {field.Name} differs");
            }
            checks++;
        }
    }
}
