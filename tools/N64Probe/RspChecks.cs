using System.Diagnostics;
using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;
using Ryu64.MIPS;

internal static class RspChecks
{
    private delegate bool Execute(uint pc, uint instruction, out string reason);
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    internal static void Run(string? reference)
    {
        string current = Check(typeof(Memory).Assembly, out int count);
        Console.WriteLine($"rspCases={count} sha256={current}");
        if (reference != null)
        {
            var context = new AssemblyLoadContext("reference-rsp", isCollectible: true);
            var assembly = context.LoadFromAssemblyPath(Path.GetFullPath(reference));
            string expected = Check(assembly, out int referenceCount);
            if (expected != current || referenceCount != count)
                throw new Exception($"RSP state differs from reference: {expected}");
            Console.WriteLine("rspDifferential=passed");
            Benchmark(assembly, "reference");
            context.Unload();
        }
        Benchmark(typeof(Memory).Assembly, "current");
    }

    private static (object memory, object rsp, Type type) Create(Assembly assembly)
    {
        Type memoryType = assembly.GetType("Ryu64.MIPS.Memory")!;
        object memory = Activator.CreateInstance(memoryType, new object[] { new byte[4096] })!;
        Type type = assembly.GetType("Ryu64.MIPS.RspInterpreter")!;
        object rsp = Activator.CreateInstance(type, new[] { memory })!;
        return (memory, rsp, type);
    }

    private static string Check(Assembly assembly, out int count)
    {
        var (memory, rsp, type) = Create(assembly);
        var step = type.GetMethod("Step", Private)!.CreateDelegate<Execute>(rsp);
        string[] names = { "_gpr", "_vr", "_vcc", "_vco", "_accHi", "_accMd", "_accLo" };
        var arrays = names.Select(n => (Array)type.GetField(n, Private)!.GetValue(rsp)!).ToArray();
        var dmem = (byte[])memory.GetType().GetField("SP_MEM_RW")!.GetValue(memory)!;
        string[] scalars = { "_vce", "_divIn", "_divOut", "_dpFlag" };
        var scalarFields = scalars.Select(n => type.GetField(n, Private)!).ToArray();
        uint seed = 0x12345678;
        byte Next() { seed ^= seed << 13; seed ^= seed >> 17; seed ^= seed << 5; return (byte)seed; }
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] scratch = new byte[8192];
        count = 0;
        void Initialize()
        {
            foreach (Array array in arrays)
            {
                for (int i = 0; i < Buffer.ByteLength(array); i++) scratch[i] = Next();
                Buffer.BlockCopy(scratch, 0, array, 0, Buffer.ByteLength(array));
            }
            for (int i = 0; i < dmem.Length; i++) dmem[i] = Next();
            foreach (var field in scalarFields) field.SetValue(rsp, Convert.ChangeType(Next(), field.FieldType));
        }
        void Record(bool supported)
        {
            hash.AppendData(new[] { (byte)(supported ? 1 : 0) });
            foreach (Array array in arrays)
            {
                Buffer.BlockCopy(array, 0, scratch, 0, Buffer.ByteLength(array));
                hash.AppendData(scratch, 0, Buffer.ByteLength(array));
            }
            foreach (var field in scalarFields)
                hash.AppendData(BitConverter.GetBytes(Convert.ToUInt32(field.GetValue(rsp))));
            hash.AppendData(dmem);
        }
        for (uint op = 0; op < 64; op++)
        for (uint element = 0; element < 16; element++)
        for (uint alias = 0; alias < 4; alias++)
        {
            Initialize();
            uint vs = 3, vt = alias == 3 ? vs : 7, vd = alias == 0 ? 11 : alias == 1 ? vs : vt;
            uint instruction = 0x4a000000 | element << 21 | vt << 16 | vs << 11 | vd << 6 | op;
            // Repeat to catch scratch reuse and state-dependent accumulator/flag bugs.
            for (int repeat = 0; repeat < 3; repeat++) Record(step(0, instruction, out _));
            count++;
        }
        for (uint op = 0; op < 12; op++)
        for (uint element = 0; element < 16; element++)
        for (uint alignment = 0; alignment < 16; alignment++)
        {
            Initialize();
            ((uint[])arrays[0])[1] = 0xff0 + alignment;
            uint instruction = 1u << 21 | 31u << 16 | op << 11 | element << 7;
            Record(step(0, 0xc8000000 | instruction, out _));
            Record(step(0, 0xe8000000 | instruction, out _));
            count++;
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static void Benchmark(Assembly assembly, string label)
    {
        var (_, rsp, type) = Create(assembly);
        var execute = type.GetMethod("ExecuteVectorCompute", Private)!.CreateDelegate<Execute>(rsp);
        for (int i = 0; i < 20000; i++) execute(0, 0x4a071ac0 | (uint)(i & 15), out _);
        long before = GC.GetAllocatedBytesForCurrentThread();
        var timer = Stopwatch.StartNew();
        for (int i = 0; i < 1_000_000; i++) execute(0, 0x4a071ac0 | (uint)(i & 15), out _);
        Console.WriteLine($"rspBench={label} milliseconds={timer.Elapsed.TotalMilliseconds:F2} allocated={GC.GetAllocatedBytesForCurrentThread() - before}");
    }
}
