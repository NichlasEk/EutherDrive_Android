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
        if (Environment.GetEnvironmentVariable("EUTHERDRIVE_N64_RSP_BLOCK_JIT") == "0")
            throw new Exception("Remove EUTHERDRIVE_N64_RSP_BLOCK_JIT=0 for this check.");
        Environment.SetEnvironmentVariable("EUTHERDRIVE_N64_RSP_TASK_MAX_INSTRUCTIONS", "61");
        string actual = Check(typeof(Memory).Assembly, true);
        var context = new AssemblyLoadContext("reference-blocks", true);
        string expected = Check(context.LoadFromAssemblyPath(Path.GetFullPath(reference)), false);
        if (actual != expected) throw new Exception($"Block execution differs: {actual}/{expected}");
        context.Unload();
        Console.WriteLine($"rspBlockCases=96 sha256={actual} differential=passed");
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
            0xc8002000, 0xe8002000, 0x20000000, 0x28000000, 0x2c000000,
            0x80000000, 0x84000000, 0x90000000, 0x94000000, 0xa0000000, 0xa4000000,
            0x00000004, 0x00000006, 0x00000007, 0x00000020, 0x00000022 };
        // Exercise every vector load/store helper, including negative offsets,
        // arbitrary elements and register aliasing in randomized surroundings.
        forms = forms.Concat(Enumerable.Range(0, 24).Select(i =>
            (i < 12 ? 0xc8000000u : 0xe8000000u) | (uint)(i % 12) << 11)).ToArray();
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        for (int iteration = 0; iteration < 96; iteration++)
        {
            // Reuse the interpreter/cache while changing first and interior
            // words. Every eighth program also tests IMEM wraparound.
            uint start = iteration % 8 == 0 ? 0xfe0u : 0u;
            int cursor = (int)start;
            void Op(uint word)
            {
                BinaryPrimitives.WriteUInt32BigEndian(sp.AsSpan(0x1000 + cursor), word);
                cursor = (cursor + 4) & 0xfff;
            }
            // Both encodings set r1 to the same value; alternating them also
            // checks invalidation when the first cached instruction changes.
            Op(iteration % 4 == 1 ? 0x34010fffu : 0x24010fffu);
            // Exercise each predecoded accumulate with all element selectors
            // and both aliased and distinct destination/source registers.
            for (uint accumulate = 13; accumulate <= 15; accumulate++)
            {
                uint source = (uint)(iteration % 32);
                uint destination = iteration % 2 == 0 ? source : (source + 1) & 31;
                Op(0x4a000000u | (uint)(iteration % 16) << 21
                    | source << 16 | ((source + 2) & 31) << 11
                    | destination << 6 | accumulate);
            }
            for (int i = 0; i < 24; i++)
            {
                // Every form is selected explicitly at least once, in addition
                // to randomized surrounding instructions and register values.
                uint form = i == 0 ? forms[iteration % forms.Length] : forms[random.Next(forms.Length)];
                uint fields = (uint)random.Next(32) << 21 | (uint)random.Next(32) << 16;
                uint word = form >> 26 == 0 ? form | fields | (uint)random.Next(32) << 11 | (uint)random.Next(32) << 6
                    : form >> 26 == 0x12 ? form | fields | (uint)random.Next(32) << 11 | (uint)random.Next(32) << 6
                    : form >> 26 == 0x32 || form >> 26 == 0x3a ? form | fields | (uint)random.Next(16) << 7 | (uint)random.Next(128)
                    : form | fields | (uint)random.Next(65536);
                if (i == 0 && (form >> 26) >= 0x20 && (form >> 26) <= 0x2b)
                    word = form | 1u << 21 | 2u << 16;
                Op(word);
            }
            uint target = (uint)((cursor + 16) & 0xfff);
            Op(0x24050000 | target | 3); // JR must capture r5 before the delay slot.
            uint branch = (iteration % 8) switch
            {
                0 => 0x10000002u, // BEQ taken
                1 => 0x14000002u, // BNE not taken
                2 => 0x18000002u, // BLEZ taken
                3 => 0x1c000002u, // BGTZ not taken
                4 => 0x08000000u | (target >> 2), // J
                5 => 0x0c000000u | (target >> 2), // JAL
                6 => 0x00a00008u, // JR r5 (unaligned target is masked at dispatch)
                _ => 0x10220002u, // BEQ with data-dependent condition
            };
            Op(branch);
            Op(0x24050000); // delay slot changes the JR source register
            Op(0x24210100); // skipped only by taken branches
            Op(0x54000001); // Not-taken likely branch: annul its delay slot.
            Op(0x24210100);
            Op(0x24420001);
            Op(0x0000000d);
            if (iteration >= 62)
            {
                cursor = 0;
                for (int i = 0; i < (iteration == 62 ? 60 : 96); i++) Op(0);
                if (iteration == 62)
                {
                    Op(0x0c000000); // Budget ends after JAL, before its delay slot.
                    Op(0);
                }
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
            bool expected = iteration >= 62
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
