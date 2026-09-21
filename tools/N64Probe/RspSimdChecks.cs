using System.Buffers.Binary;
using System.Reflection;
using Ryu64.MIPS;

// Compare each instruction, not just the final digest: this also exercises
// transitions between SIMD and scalar instructions that reuse scratch buffers.
internal static class RspSimdChecks
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private delegate bool Step(uint pc, uint word, out string reason);
    private sealed class Machine
    {
        internal readonly object Rsp;
        internal readonly Step Execute;
        internal readonly byte[] Registers;
        internal readonly ushort[] Hi, Md, Lo, Vco, Vcc;
        internal readonly Array[] State;
        internal readonly FieldInfo[] Scalars;
        internal Machine(Assembly assembly)
        {
            object memory = Activator.CreateInstance(assembly.GetType("Ryu64.MIPS.Memory")!, new object[] { new byte[4096] })!;
            Type type = assembly.GetType("Ryu64.MIPS.RspInterpreter")!;
            Rsp = Activator.CreateInstance(type, new[] { memory })!;
            Execute = type.GetMethod("Step", Private)!.CreateDelegate<Step>(Rsp);
            Array Get(string name) => (Array)type.GetField(name, Private)!.GetValue(Rsp)!;
            Registers = (byte[])Get("_vr");
            Hi = (ushort[])Get("_accHi"); Md = (ushort[])Get("_accMd"); Lo = (ushort[])Get("_accLo");
            Vco = (ushort[])Get("_vco"); Vcc = (ushort[])Get("_vcc");
            State = new[] { Get("_gpr"), Registers, Hi, Md, Lo, Vco, Vcc };
            Scalars = new[] { "_vce", "_divIn", "_divOut", "_dpFlag" }.Select(n => type.GetField(n, Private)!).ToArray();
        }
    }

    internal static void Run(Assembly reference)
    {
        var actual = new Machine(typeof(Memory).Assembly);
        var expected = new Machine(reference);
        var enabled = actual.Rsp.GetType().GetField("VectorSimdEnabled", BindingFlags.Static | BindingFlags.NonPublic);
        bool simd = (bool?)enabled?.GetValue(null) ?? false;
        if (System.Runtime.Intrinsics.X86.Ssse3.IsSupported
            && Environment.GetEnvironmentVariable("EUTHERDRIVE_N64_RSP_SIMD") != "0" && !simd)
            throw new Exception("The net8 SIMD path was not enabled in this build.");
        byte[] actualBytes = new byte[512], expectedBytes = new byte[512];
        int count = 0;
        void CopyState()
        {
            for (int i = 0; i < actual.State.Length; i++)
                Buffer.BlockCopy(actual.State[i], 0, expected.State[i], 0, Buffer.ByteLength(actual.State[i]));
            for (int i = 0; i < actual.Scalars.Length; i++)
                expected.Scalars[i].SetValue(expected.Rsp, actual.Scalars[i].GetValue(actual.Rsp));
        }
        void Check(uint word)
        {
            bool a = actual.Execute(0, word, out string ar), b = expected.Execute(0, word, out string br);
            if (a != b || ar != br) throw new Exception($"RSP vector completion mismatch: {word:x8}");
            for (int i = 0; i < actual.State.Length; i++)
            {
                int length = Buffer.ByteLength(actual.State[i]);
                Buffer.BlockCopy(actual.State[i], 0, actualBytes, 0, length);
                Buffer.BlockCopy(expected.State[i], 0, expectedBytes, 0, length);
                if (!actualBytes.AsSpan(0, length).SequenceEqual(expectedBytes.AsSpan(0, length)))
                    throw new Exception($"RSP vector mismatch case={count} instruction={word:x8} state={i}");
            }
            for (int i = 0; i < actual.Scalars.Length; i++)
                if (!Equals(actual.Scalars[i].GetValue(actual.Rsp), expected.Scalars[i].GetValue(expected.Rsp)))
                    throw new Exception($"RSP vector scalar mismatch: {word:x8} {actual.Scalars[i].Name}");
            count++;
        }

        ushort[] edges = { 0, 1, 0x7ffe, 0x7fff, 0x8000, 0x8001, 0xfffe, 0xffff };
        int[] ops = { 0, 1, 4, 5, 6, 7, 8, 9, 12, 13, 14, 15, 16, 17, 20, 21, 39, 40, 41, 42, 43, 44, 45 };
        var random = new Random(640128);
        foreach (int op in ops)
        for (int pattern = 0; pattern < 64; pattern++)
        for (int element = 0; element < 16; element++)
        for (int alias = 0; alias < 4; alias++)
        {
            random.NextBytes(actual.Registers);
            int vs = (pattern + element) & 31, vt = alias == 3 ? vs : (vs + 7) & 31;
            int vd = alias == 0 ? (vs + 11) & 31 : alias == 1 ? vs : vt;
            for (int lane = 0; lane < 8; lane++)
            {
                BinaryPrimitives.WriteUInt16BigEndian(actual.Registers.AsSpan(vs * 16 + lane * 2), edges[(pattern + lane) & 7]);
                BinaryPrimitives.WriteUInt16BigEndian(actual.Registers.AsSpan(vt * 16 + lane * 2), edges[((pattern >> 3) + lane) & 7]);
                actual.Hi[lane] = edges[(lane + (pattern >> 3)) & 7];
                actual.Md[lane] = edges[(lane + pattern) & 7];
                actual.Lo[lane] = edges[(7 - lane + pattern) & 7];
            }
            actual.Vco[0] = (ushort)random.Next(256); actual.Vco[1] = (ushort)random.Next(256);
            actual.Vcc[0] = (ushort)random.Next(256); actual.Vcc[1] = (ushort)random.Next(256);
            CopyState();
            uint word = 0x4a000000u | (uint)element << 21 | (uint)vt << 16 | (uint)vs << 11 | (uint)vd << 6 | (uint)op;
            Check(word); Check(word); // Accumulator carry/wrap and destination/source aliases.
        }
        // Exhaust every packed carry/selection mask, including saturation with
        // a carry on the signed limit. Preserve unrelated control flags.
        foreach (int op in new[] { 16, 17, 20, 21, 39 })
        for (int flags = 0; flags < 256; flags++)
        {
            for (int lane = 0; lane < 8; lane++)
            {
                BinaryPrimitives.WriteUInt16BigEndian(actual.Registers.AsSpan(3 * 16 + lane * 2), edges[lane]);
                BinaryPrimitives.WriteUInt16BigEndian(actual.Registers.AsSpan(7 * 16 + lane * 2), 0);
            }
            actual.Vco[0] = actual.Vco[1] = (ushort)flags;
            actual.Vcc[0] = actual.Vcc[1] = (ushort)flags;
            CopyState();
            Check(0x4a071ac0u | (uint)op);
        }
        // Mixed instructions also catch stale work buffers when execution
        // alternates between optimized operations, reciprocal/clip and VSAR.
        for (int i = 0; i < 25000; i++)
            Check(0x4a000000u | (uint)random.Next(1 << 25));
        Console.WriteLine($"rspSimdCases={count} simdEnabled={simd} differential=passed");
    }
}
