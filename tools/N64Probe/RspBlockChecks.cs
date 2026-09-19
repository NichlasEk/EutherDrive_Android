using System.Buffers.Binary;
using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;
using Ryu64.MIPS;

internal static class RspBlockChecks
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private delegate bool ExecuteTask(out uint count, out string reason);

    internal static void Run(string reference)
    {
        if (Environment.GetEnvironmentVariable("EUTHERDRIVE_N64_RSP_BLOCK_JIT") != "1")
            throw new Exception("Enable EUTHERDRIVE_N64_RSP_BLOCK_JIT=1 for this check.");
        Environment.SetEnvironmentVariable("EUTHERDRIVE_N64_RSP_TASK_MAX_INSTRUCTIONS", "61");
        string actual = Check(typeof(Memory).Assembly, true);
        var context = new AssemblyLoadContext("reference-blocks", true);
        string expected = Check(context.LoadFromAssemblyPath(Path.GetFullPath(reference)), false);
        if (actual != expected) throw new Exception($"Block execution differs: {actual}/{expected}");
        context.Unload();
        Console.WriteLine($"rspBlockCases=64 sha256={actual} differential=passed");
    }

    private static string Check(Assembly assembly, bool requireBlocks)
    {
        Type memoryType = assembly.GetType("Ryu64.MIPS.Memory")!;
        object memory = Activator.CreateInstance(memoryType, new object[] { new byte[4096] })!;
        Type rspType = assembly.GetType("Ryu64.MIPS.RspInterpreter")!;
        object rsp = Activator.CreateInstance(rspType, new[] { memory })!;
        var taskField = memoryType.GetField("_activeRspTask", Private)!;
        object task = Activator.CreateInstance(taskField.FieldType)!;
        taskField.FieldType.GetField("Type")!.SetValue(task, 1u);
        taskField.SetValue(memory, task);
        byte[] sp = (byte[])memoryType.GetField("SP_MEM_RW")!.GetValue(memory)!;
        var execute = rspType.GetMethod("ExecuteTask")!.CreateDelegate<ExecuteTask>(rsp);
        var writePc = memoryType.GetMethod("WriteRspPc", Private)!.CreateDelegate<Action<uint>>(memory);
        var writeCp0 = memoryType.GetMethod("WriteRspCp0", Private)!.CreateDelegate<Action<int, uint>>(memory);
        var save = memoryType.GetMethod("SaveState")!.CreateDelegate<Action<BinaryWriter>>(memory);
        var random = new Random(643210);
        random.NextBytes(sp.AsSpan(0, 4096));
        foreach (string name in new[] { "_vr", "_accLo", "_accMd", "_accHi" })
        {
            var array = (Array)rspType.GetField(name, Private)!.GetValue(rsp)!;
            byte[] bytes = new byte[Buffer.ByteLength(array)];
            random.NextBytes(bytes);
            Buffer.BlockCopy(bytes, 0, array, 0, bytes.Length);
        }
        uint[] forms = { 0x24000000, 0x30000000, 0x34000000, 0x38000000, 0x3c000000,
            0x8c000000, 0xac000000, 0x00000021, 0x00000023, 0x00000024, 0x00000025,
            0x00000026, 0x00000027, 0x0000002a, 0x0000002b, 0x00000000, 0x00000002,
            0x00000003, 0x4a000005, 0x4a00000d, 0x4a00000e, 0x4a00000f, 0x4a00002c,
            0xc8002000, 0xe8002000 };
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        for (int iteration = 0; iteration < 64; iteration++)
        {
            // Reuse the interpreter/cache, keep the first word fixed, and change
            // interior words. Every eighth program also tests IMEM wraparound.
            uint start = iteration % 8 == 0 ? 0xfe0u : 0u;
            int cursor = (int)start;
            void Op(uint word)
            {
                BinaryPrimitives.WriteUInt32BigEndian(sp.AsSpan(0x1000 + cursor), word);
                cursor = (cursor + 4) & 0xfff;
            }
            Op(0x24010001);
            for (int i = 0; i < 24; i++)
            {
                uint form = forms[random.Next(forms.Length)];
                uint fields = (uint)random.Next(32) << 21 | (uint)random.Next(32) << 16;
                uint word = form >> 26 == 0 ? form | fields | (uint)random.Next(32) << 11 | (uint)random.Next(32) << 6
                    : form >> 26 == 0x12 ? form | fields | (uint)random.Next(32) << 11 | (uint)random.Next(32) << 6
                    : form >> 26 == 0x32 || form >> 26 == 0x3a ? form | fields | (uint)random.Next(16) << 7 | (uint)random.Next(128)
                    : form | fields | (uint)random.Next(65536);
                Op(word);
            }
            Op(0x10000001); // Taken branch with a meaningful delay slot.
            Op(0x24210001);
            Op(0x54000001); // Not-taken likely branch: annul its delay slot.
            Op(0x24210100);
            Op(0x24420001);
            Op(0x0000000d);
            if (iteration == 63)
            {
                cursor = 0;
                for (int i = 0; i < 96; i++) Op(0);
                Op(0x0000000d);
            }
            if (iteration % 8 == 3)
            {
                // DMA replaces two interior instructions with NOPs. Pending
                // lifecycle work must stay on the interpreter until completed.
                writeCp0(0, 0x1020);
                writeCp0(1, 0x200);
                writeCp0(2, 7);
            }
            writePc(start);
            bool completed = execute(out uint instructions, out string reason);
            bool expected = iteration == 63
                ? !completed && instructions == 61 && reason.Contains("max-instructions executed=61")
                : completed && reason == "break";
            if (!expected)
                throw new Exception($"Synthetic block task failed: {reason}");
            stream.SetLength(0);
            save(writer); writer.Flush();
            StateChecks.NormalizeForLegacyComparison(stream);
            hash.AppendData(stream.GetBuffer(), 0, (int)stream.Length);
            hash.AppendData(RspTaskCapture.SaveRegisters(rsp));
            hash.AppendData(BitConverter.GetBytes(instructions));
            hash.AppendData(System.Text.Encoding.UTF8.GetBytes(reason));
        }
        if (requireBlocks && (long)rspType.GetField("_blockInstructions", Private)!.GetValue(rsp)! == 0)
            throw new Exception("Block path was not exercised.");
        return Convert.ToHexString(hash.GetHashAndReset());
    }
}
