using System.Diagnostics;
using System.Reflection;
using System.Runtime.Loader;
using Ryu64.MIPS;

internal static class OpcodeChecks
{
    internal static void Run(string reference)
    {
        OpcodeTable.Init();
        var all = ((List<OpcodeTable.InstInfo>)typeof(OpcodeTable)
            .GetField("AllInsts", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!).ToArray();
        var words = new List<uint>();
        var random = new Random(643000);
        uint Word() => (uint)random.NextInt64(0, 1L << 32);
        foreach (var instruction in all)
        {
            words.Add(instruction.Value);
            words.Add(instruction.Value | ~instruction.Mask);
            for (int i = 0; i < 4096; i++) words.Add(instruction.Value | (Word() & ~instruction.Mask));
        }
        int checkedCount = 0, rejected = 0;
        void Check(uint opcode)
        {
            OpcodeTable.InstInfo? expected = null;
            foreach (var instruction in all)
                if ((opcode & instruction.Mask) == instruction.Value) { expected = instruction; break; }
            OpcodeTable.InstInfo actual;
            try { actual = OpcodeTable.GetOpcodeInfo(opcode); }
            catch (NotImplementedException)
            {
                if (expected.HasValue) throw new Exception($"Valid instruction rejected: {opcode:x8}");
                rejected++; checkedCount++; return;
            }
            if (!expected.HasValue || actual.Mask != expected.Value.Mask || actual.Value != expected.Value.Value
                || actual.Cycles != expected.Value.Cycles || actual.Interpret != expected.Value.Interpret
                || actual.FormattedASM != expected.Value.FormattedASM)
                throw new Exception($"Instruction lookup differs from ordered full scan: {opcode:x8}");
            checkedCount++;
        }
        foreach (uint word in words) Check(word);
        // Sweep primary/function combinations including unsupported encodings
        // and differing COP/register fields, plus arbitrary malformed opcodes.
        for (uint primary = 0; primary < 64; primary++)
        for (uint function = 0; function < 64; function++)
        foreach (uint rs in new uint[] { 0, 1, 16, 17, 31 }) Check(primary << 26 | rs << 21 | function);
        for (int i = 0; i < 32768; i++) Check(Word());
        Console.WriteLine($"opcodeCases={checkedCount} rejected={rejected} fullOrderedScan=passed");

        // Identical valid instruction mix, fixed work, no emulation or timing
        // changes. Reflection.Emit adapts the private reference struct return
        // to a uint without per-call boxing or reflection invocation overhead.
        Func<uint, uint> Bind(Assembly assembly)
        {
            var table = assembly.GetType("Ryu64.MIPS.OpcodeTable")!;
            table.GetMethod("Init")!.Invoke(null, null);
            var method = table.GetMethod("GetOpcodeInfo")!;
            var dynamic = new System.Reflection.Emit.DynamicMethod("DecodeCycles", typeof(uint), new[] { typeof(uint) }, typeof(OpcodeChecks).Module, true);
            var il = dynamic.GetILGenerator();
            il.Emit(System.Reflection.Emit.OpCodes.Ldarg_0);
            il.Emit(System.Reflection.Emit.OpCodes.Call, method);
            il.Emit(System.Reflection.Emit.OpCodes.Ldfld, method.ReturnType.GetField("Cycles")!);
            il.Emit(System.Reflection.Emit.OpCodes.Ret);
            return dynamic.CreateDelegate<Func<uint, uint>>();
        }
        void Bench(Func<uint, uint> decode, string label)
        {
            ulong checksum = 0;
            foreach (uint word in words) checksum += decode(word);
            var runs = new List<double>();
            for (int repeat = 0; repeat < 5; repeat++)
            {
                long start = Stopwatch.GetTimestamp();
                foreach (uint word in words) checksum += decode(word);
                runs.Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
            }
            runs.Sort();
            Console.WriteLine($"opcodeBench={label} instructions={words.Count} medianMs={runs[2]:F3} checksum={checksum}");
        }
        if (reference != null)
        {
            var context = new AssemblyLoadContext("reference-opcodes", isCollectible: true);
            Bench(Bind(context.LoadFromAssemblyPath(Path.GetFullPath(reference))), "reference");
            context.Unload();
        }
        Bench(Bind(typeof(OpcodeTable).Assembly), "current");
    }
}
