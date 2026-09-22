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
        string sliced = CheckSlicedProgress(typeof(Memory).Assembly);
        var context = new AssemblyLoadContext("reference-blocks", true);
        string previousBlocks = Environment.GetEnvironmentVariable("EUTHERDRIVE_N64_RSP_BLOCK_JIT");
        try
        {
            // The candidate has already initialized its block switch. Initialize
            // the isolated reference with ordinary RSP execution for a real oracle.
            Environment.SetEnvironmentVariable("EUTHERDRIVE_N64_RSP_BLOCK_JIT", "0");
            var referenceAssembly = context.LoadFromAssemblyPath(Path.GetFullPath(reference));
            string expected = Check(referenceAssembly, false);
            if (actual != expected) throw new Exception($"Block execution differs: {actual}/{expected}");
            if (sliced != CheckSlicedProgress(referenceAssembly))
                throw new Exception("RSP progress/history differs across CPU writes, DMA or slice boundaries");
        }
        finally
        {
            Environment.SetEnvironmentVariable("EUTHERDRIVE_N64_RSP_BLOCK_JIT", previousBlocks);
            context.Unload();
        }
        Console.WriteLine($"rspBlockCases=96 sha256={actual} differential=passed");
        Console.WriteLine($"rspProgressSlices=768 sha256={sliced} differential=passed");
    }

    private delegate bool ExecuteSlice(out uint count, out string reason, bool resume, uint budget);

    private static string CheckSlicedProgress(Assembly assembly)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        foreach (bool validTask in new[] { false, true })
        foreach (int variant in new[] { 0, 1, 2 })
        foreach (uint budget in new uint[] { 1, 2, 3, 7, 15, 16, 17, 31 })
        {
            Type memoryType = assembly.GetType("Ryu64.MIPS.Memory")!;
            object memory = Activator.CreateInstance(memoryType, new object[] { new byte[4096] })!;
            assembly.GetType("Ryu64.MIPS.R4300")!.GetField("memory")!.SetValue(null, memory);
            object rsp = memoryType.GetField("_rspInterpreter", Private)!.GetValue(memory)!;
            Type rspType = rsp.GetType();
            if (validTask)
            {
                var taskField = memoryType.GetField("_activeRspTask", Private)!;
                object task = Activator.CreateInstance(taskField.FieldType)!;
                taskField.FieldType.GetField("Type")!.SetValue(task, 1u);
                taskField.SetValue(memory, task);
            }
            // Model synchronous dispatch so lifecycle ticks cannot recursively
            // dispatch this manually sliced test program.
            memoryType.GetField("_rspTaskDispatching", Private)!.SetValue(memory, true);
            byte[] sp = (byte[])memoryType.GetField("SP_MEM_RW")!.GetValue(memory)!;
            uint[] gpr = (uint[])rspType.GetField("_gpr", Private)!.GetValue(rsp)!;
            gpr[3] = 0x400; gpr[4] = 0x200; gpr[5] = 15;
            bool dmaProgram = variant == 1;
            uint[] program = {
                0x24210001, 0x34300000, 0x34310000, 0x24420001,
                dmaProgram ? 0x40830000u : 0u, // SP_MEM_ADDR
                dmaProgram ? 0x40840800u : 0u, // SP_DRAM_ADDR
                dmaProgram ? 0x40851000u : 0u, // SP_RD_LEN
                dmaProgram ? 0x40182000u : 0u, // read SP_STATUS into tracked r24
                0x27390001, 0x275a0001, 0x0c000000, 0 // tracked r25/r26/r31, JAL + delay
            };
            if (variant == 2)
            {
                // More than eight consecutive blocks, with unequal lengths,
                // distinct PCs/words and all ring positions. This forces any
                // bounded pending-history queue to discard overwritten entries.
                var blocks = new List<uint>();
                for (int block = 0; block < 16; block++)
                {
                    int length = block < 4 ? new[] { 2, 3, 5, 16 }[block] : 2;
                    for (int i = 0; i < length - 2; i++)
                        blocks.Add(0x24420000u | (uint)(block + i + 1));
                    uint target = block == 15 ? 0u : (uint)blocks.Count + 2;
                    blocks.Add(0x08000000u | target);
                    blocks.Add(0x24210000u | (uint)block); // tracked write in delay slot
                }
                program = blocks.ToArray();
            }
            for (int i = 0; i < program.Length; i++)
                BinaryPrimitives.WriteUInt32BigEndian(sp.AsSpan(0x1000 + i * 4), program[i]);
            var execute = rspType.GetMethod("ExecuteSlice", Private)!.CreateDelegate<ExecuteSlice>(rsp);
            var cp0 = memoryType.GetMethod("WriteRspCp0", Private)!.CreateDelegate<Action<int, uint>>(memory);
            var tick = memoryType.GetMethod("TickRspInterpreter", Private)!.CreateDelegate<Action<uint>>(memory);
            var save = memoryType.GetMethod("SaveState")!.CreateDelegate<Action<BinaryWriter>>(memory);
            for (int slice = 0; slice < 16; slice++)
            {
                // CPU changes between slices must become visible at exactly
                // the same progress checkpoints as in the uncached reference.
                if (slice % 4 == 1) cp0(0, (uint)(0x600 + slice * 8));
                if (slice % 4 == 2) cp0(4, (slice & 4) == 0 ? 0x400u : 0x200u);
                if (slice % 4 == 3 && variant != 2)
                {
                    cp0(0, 0x800); cp0(1, 0x200); cp0(2, 15);
                    tick((uint)slice);
                }
                bool completed = execute(out uint instructions, out string reason, slice != 0, budget);
                if (completed || instructions != budget || reason != "slice")
                    throw new Exception($"Sliced RSP program stopped at budget {budget}: {reason}");
                stream.SetLength(0);
                save(writer);
                writer.Write(RspTaskCapture.SaveRegisters(rsp, includeScratch: false));
                writer.Write(instructions); writer.Write(reason); writer.Flush();
                hash.AppendData(stream.GetBuffer(), 0, (int)stream.Length);
            }
        }
        return Convert.ToHexString(hash.GetHashAndReset());
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
            // Exercise every specialized operation through a real compiled block.
            uint[] simdOps = { 0,1,4,5,6,7,8,9,12,13,14,15,16,17,19,20,21,32,33,34,35,36,37,38,39,40,41,42,43,44,45 };
            uint simdOp = simdOps[iteration % simdOps.Length];
            uint simdSource = (uint)(iteration % 32);
            uint simdDestination = iteration % 2 == 0 ? simdSource : (simdSource + 1) & 31;
            Op(0x4a000000u | (uint)(iteration % 16) << 21 | simdSource << 16
                | ((simdSource + 2) & 31) << 11 | simdDestination << 6 | simdOp);
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
            // Mix SIMD clipping with accumulator/reciprocal instructions in
            // compiled blocks, including VCH -> VCL flag flow.
            foreach (uint operation in new uint[] { 19,29,32,33,34,35,37,36,38,51,48,49,50,52,53,54 })
            {
                uint source = (uint)(iteration % 32);
                uint destination = iteration % 2 == 0 ? source : (source + 1) & 31;
                Op(0x4a000000u | (uint)(iteration % 16) << 21
                    | source << 16 | ((source + 2) & 31) << 11 | destination << 6 | operation);
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
            hash.AppendData(RspTaskCapture.SaveRegisters(rsp, includeScratch: false));
            hash.AppendData(BitConverter.GetBytes(instructions));
            hash.AppendData(System.Text.Encoding.UTF8.GetBytes(reason));
        }
        if (requireBlocks && (long)rspType.GetField("_blockInstructions", Private)!.GetValue(rsp)! == 0)
            throw new Exception("Block path was not exercised.");
        return Convert.ToHexString(hash.GetHashAndReset());
    }
}
