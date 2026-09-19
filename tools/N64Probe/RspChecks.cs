using System.Diagnostics;
using System.Buffers.Binary;
using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;
using Ryu64.MIPS;

internal static class RspChecks
{
    private delegate bool Execute(uint pc, uint instruction, out string reason);
    private delegate bool ExecuteTask(out uint count, out string reason);
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    internal static void Run(string? reference)
    {
        // Keep watchdog-stop regression cases short; normal emulator runs do not
        // set this diagnostic override. Finite synthetic tasks stay below it.
        Environment.SetEnvironmentVariable("EUTHERDRIVE_N64_RSP_TASK_NO_PROGRESS_LIMIT", "4096");
        string current = Check(typeof(Memory).Assembly, out int count);
        string tasks = CheckTasks(typeof(Memory).Assembly);
        Console.WriteLine($"rspCases={count} sha256={current}");
        Console.WriteLine($"rspTaskDigest={tasks}");
        if (reference != null)
        {
            var context = new AssemblyLoadContext("reference-rsp", isCollectible: true);
            var assembly = context.LoadFromAssemblyPath(Path.GetFullPath(reference));
            string expected = Check(assembly, out int referenceCount);
            if (expected != current || referenceCount != count)
                throw new Exception($"RSP state differs from reference: {expected}");
            if (CheckTasks(assembly) != tasks)
                throw new Exception("RSP task execution differs from reference");
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

    private static string CheckTasks(Assembly assembly)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (bool validTask in new[] { false, true })
        foreach (uint tracked in new[] { 1u, 2u, 16u, 17u, 24u, 25u, 26u, 31u })
        foreach (bool dma in new[] { false, true })
        {
            var (memory, rsp, type) = Create(assembly);
            var memoryType = memory.GetType();
            byte[] sp = (byte[])memoryType.GetField("SP_MEM_RW")!.GetValue(memory)!;
            byte[] ram = (byte[])memoryType.GetField("RDRAM")!.GetValue(memory)!;
            new Random(7541).NextBytes(ram);
            if (validTask)
            {
                var field = memoryType.GetField("_activeRspTask", Private)!;
                object task = Activator.CreateInstance(field.FieldType)!;
                field.FieldType.GetField("Type")!.SetValue(task, 1u);
                field.SetValue(memory, task);
            }
            int cursor = 0x1000;
            void Op(uint value) { BinaryPrimitives.WriteUInt32BigEndian(sp.AsSpan(cursor), value); cursor += 4; }
            Op(0x24030040); // addiu r3, zero, 64 (loop counter)
            int loop = cursor - 0x1000;
            Op(0x24000000 | tracked << 21 | tracked << 16 | 1); // tracked/untracked GPR changes
            Op(0x4a071ac0); // VMULF, no GPR or MMIO effects
            Op(0x4a071acf); // VMADH
            if (dma)
            {
                Op(0x24040200); // DMEM destination (does not overwrite task descriptor)
                Op(0x40840000); // mtc0 r4, SP_MEM_ADDR
                Op(0x24040100);
                Op(0x40840800); // mtc0 r4, SP_DRAM_ADDR
                Op(0x2404003f);
                Op(0x40841000); // mtc0 r4, SP_RD_LEN
                Op(0x40053000); // mfc0 r5, SP_DMA_BUSY
            }
            Op(0x2463ffff); // addiu r3, r3, -1
            int branch = cursor - 0x1000;
            Op(0x14600000u | (ushort)((loop - branch - 4) / 4)); // bne r3, zero, loop
            Op(0); // delay slot
            Op(0x0000000d); // break
            var execute = type.GetMethod("ExecuteTask")!.CreateDelegate<ExecuteTask>(rsp);
            bool completed = execute(out uint instructions, out string reason);
            if (!completed || reason != "break") throw new Exception($"Synthetic RSP task did not complete: {reason}");
            hash.AppendData(BitConverter.GetBytes(instructions));
            using var state = new MemoryStream();
            using var writer = new BinaryWriter(state);
            memoryType.GetMethod("SaveState")!.Invoke(memory, new object[] { writer });
            writer.Flush();
            StateChecks.NormalizeForLegacyComparison(state);
            foreach (string name in new[] { "_gpr", "_vr", "_vcc", "_vco", "_accHi", "_accMd", "_accLo", "_recentPcs", "_recentInstrs" })
            {
                Array data = (Array)type.GetField(name, Private)!.GetValue(rsp)!;
                byte[] bytes = new byte[Buffer.ByteLength(data)];
                Buffer.BlockCopy(data, 0, bytes, 0, bytes.Length);
                writer.Write(bytes);
            }
            foreach (string name in new[] { "_lastProgressSignature", "_stagnantInstructionCount", "_samePcRunLength", "_pc" })
                writer.Write(Convert.ToUInt64(type.GetField(name, Private)!.GetValue(rsp)));
            writer.Flush();
            hash.AppendData(state.GetBuffer(), 0, (int)state.Length);
        }
        foreach (bool validTask in new[] { false, true })
        {
            var (memory, rsp, type) = Create(assembly);
            var memoryType = memory.GetType();
            if (validTask)
            {
                var field = memoryType.GetField("_activeRspTask", Private)!;
                object task = Activator.CreateInstance(field.FieldType)!;
                field.FieldType.GetField("Type")!.SetValue(task, 1u);
                field.SetValue(memory, task);
            }
            byte[] sp = (byte[])memoryType.GetField("SP_MEM_RW")!.GetValue(memory)!;
            // Untracked r2 keeps changing, but the watchdog signature must not.
            BinaryPrimitives.WriteUInt32BigEndian(sp.AsSpan(0x1000), 0x24420001u);
            BinaryPrimitives.WriteUInt32BigEndian(sp.AsSpan(0x1004), 0x08000000u);
            BinaryPrimitives.WriteUInt32BigEndian(sp.AsSpan(0x1008), 0u);
            bool completed = type.GetMethod("ExecuteTask")!.CreateDelegate<ExecuteTask>(rsp)(out uint instructions, out string reason);
            uint expected = validTask ? 4096u : 2_000_000u;
            if (completed || instructions != expected || !reason.Contains("no-progress stagnant="))
                throw new Exception($"RSP watchdog failed at {instructions}: {reason}");
            hash.AppendData(BitConverter.GetBytes(instructions));
            hash.AppendData(System.Text.Encoding.UTF8.GetBytes(reason));
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }
}
