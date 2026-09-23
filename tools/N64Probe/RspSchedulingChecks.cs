using System.Buffers.Binary;
using System.Reflection;
using Ryu64.MIPS;

internal static class RspSchedulingChecks
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private delegate bool Slice(out uint count, out string reason, bool resume, uint budget);

    internal static void Run()
    {
        // Cut execution at every boundary, including between a branch and its
        // delay slot. Restore into a different Memory/RSP, not the same object.
        uint[] program = { 0x24010007, 0x2421ffff, 0x1420fffe, 0x24420003, 0xac020040, 0x0000000d };
        int cases = 0;
        for (uint budget = 1; budget <= 32; budget++)
        {
            var memory = NewMemory(program);
            bool resume = false;
            for (int iteration = 0; ; iteration++)
            {
                if (iteration > 100) throw new Exception("Sliced program did not terminate");
                object rsp = typeof(Memory).GetField("_rspInterpreter", Private)!.GetValue(memory)!;
                var execute = rsp.GetType().GetMethod("ExecuteSlice", Private)!.CreateDelegate<Slice>(rsp);
                bool complete = execute(out _, out string reason, resume, budget);
                if (complete)
                {
                    if (reason != "break" || BinaryPrimitives.ReadUInt32BigEndian(memory.SP_MEM_RW.AsSpan(0x40)) != 21)
                        throw new Exception($"Delay-slot execution differs with budget {budget}");
                    break;
                }
                if (reason != "slice") throw new Exception(reason);
                using var bytes = new MemoryStream();
                using (var writer = new BinaryWriter(bytes, System.Text.Encoding.UTF8, true)) memory.SaveState(writer);
                memory = new Memory(new byte[4096]);
                R4300.memory = memory;
                bytes.Position = 0;
                using (var reader = new BinaryReader(bytes, System.Text.Encoding.UTF8, true)) memory.LoadState(reader);
                using var restored = new MemoryStream();
                using (var writer = new BinaryWriter(restored, System.Text.Encoding.UTF8, true)) memory.SaveState(writer);
                if (!bytes.ToArray().AsSpan().SequenceEqual(restored.ToArray()))
                    throw new Exception("RSP/memory state changed during save/load");
                resume = true;
            }
            cases++;
        }

        // Model a producer which publishes more work only after RSP dispatch
        // returns: poll a shared DMEM word until the CPU replaces it.
        var polling = NewMemory(new uint[] { 0x8c010080, 0x1020fffe, 0, 0xac010084, 0x0000000d });
        polling.WriteUInt32(0x04040010, 0x101); // INTR_BREAK + clear HALT
        if ((polling.ReadUInt32(0x04040010) & 3) != 0 || (polling.ReadUInt32(0x04300008) & 1) != 0)
            throw new Exception("A timeslice fabricated RSP completion");
        polling.WriteUInt32(0x04000080, 0x12345678);
        using (var bytes = new MemoryStream())
        {
            using (var writer = new BinaryWriter(bytes, System.Text.Encoding.UTF8, true)) polling.SaveState(writer);
            polling = new Memory(new byte[4096]);
            R4300.memory = polling;
            bytes.Position = 0;
            using var reader = new BinaryReader(bytes);
            polling.LoadState(reader);
        }
        var tick = typeof(Memory).GetMethod("TickRspInterpreter", Private)!.CreateDelegate<Action<uint>>(polling);
        polling.WriteUInt32(0x04040010, 2); // CPU HALT must suspend the pending slice
        tick(32768);
        if (polling.ReadUInt32(0x04000084) != 0) throw new Exception("HALT did not suspend RSP");
        polling.WriteUInt32(0x04040010, 1);
        tick(16384);
        tick(4000);
        if (polling.ReadUInt32(0x04000084) != 0x12345678 || (polling.ReadUInt32(0x04300008) & 1) == 0)
            throw new Exception("CPU publication or pending-task savestate did not resume");

        var redirected = NewMemory(new uint[] { 0x1000ffff, 0, 0, 0, 0x0000000d });
        redirected.WriteUInt32(0x04040010, 0x101);
        redirected.WriteUInt32(0x04040010, 2);
        redirected.WriteUInt32(0x04080000, 16);
        redirected.WriteUInt32(0x04040010, 1);
        var redirectTick = typeof(Memory).GetMethod("TickRspInterpreter", Private)!.CreateDelegate<Action<uint>>(redirected);
        redirectTick(4000);
        if ((redirected.ReadUInt32(0x04040010) & 3) != 3)
            throw new Exception("Explicit PC write retained the old continuation");

        // DPC_END only submits work. The DP interrupt belongs to SyncFull.
        var dp = new Memory(new byte[4096]);
        R4300.memory = dp;
        dp.WriteUInt32(0x1000, 0xe7000000); // pipe sync
        dp.WriteUInt32(0x1008, 0xe9000000); // full sync
        dp.WriteUInt32(0x04100000, 0x1000);
        dp.WriteUInt32(0x04100004, 0x1008);
        if ((dp.ReadUInt32(0x04300008) & 0x20) != 0) throw new Exception("Premature DP interrupt");
        dp.WriteUInt32(0x04100004, 0x1010);
        if ((dp.ReadUInt32(0x04300008) & 0x20) == 0) throw new Exception("Missing SyncFull interrupt");
        CheckRspDpCompletion();
        CheckFramebufferPublication();
        Console.WriteLine($"rspSliceBoundaries={cases} saveRestore=passed producerConsumer=passed dpSync=passed rspDpOrder=passed framebufferPublication=passed");
    }

    private static void CheckRspDpCompletion()
    {
        foreach (bool restore in new[] { false, true })
        foreach (bool fullSync in new[] { false, true })
        {
            // A short graphics task submits its RDP tail, then immediately
            // breaks. SP retirement must precede its deferred DP retirement.
            var memory = NewMemory(new uint[] {
                0x24011000, 0x40814000, // DPC_START = 0x1000
                0x24011008, 0x40814800, // DPC_END = 0x1008
                0x0000000d
            });
            memory.WriteUInt32(0x1000, fullSync ? 0xe9000000u : 0xe7000000u);
            memory.WriteUInt32(0x04040010, 0x101);
            if ((memory.ReadUInt32(0x04300008) & 0x21) != 0)
                throw new Exception("Synchronous dispatch exposed a completion before retirement");
            if (restore)
            {
                using var state = new MemoryStream();
                using (var writer = new BinaryWriter(state, System.Text.Encoding.UTF8, true)) memory.SaveState(writer);
                memory = new Memory(new byte[4096]);
                R4300.memory = memory;
                state.Position = 0;
                using (var reader = new BinaryReader(state, System.Text.Encoding.UTF8, true)) memory.LoadState(reader);
                using var restored = new MemoryStream();
                using (var writer = new BinaryWriter(restored)) memory.SaveState(writer);
                if (!state.ToArray().AsSpan().SequenceEqual(restored.ToArray()))
                    throw new Exception("Pending SP/DP events changed during save/load");
            }
            memory.Tick(999);
            if ((memory.ReadUInt32(0x04300008) & 0x21) != 0)
                throw new Exception("Premature SP/DP retirement");
            memory.Tick(1);
            if ((memory.ReadUInt32(0x04300008) & 0x21) != 1)
                throw new Exception("Short graphics task did not retire SP before DP");
            memory.WriteUInt32(0x04040010, 8); // acknowledge SP
            memory.Tick(3000);
            if ((memory.ReadUInt32(0x04300008) & 0x21) != (fullSync ? 0x20u : 0u))
                throw new Exception("Missing FULL_SYNC completion or fabricated DP without FULL_SYNC");
            memory.WriteUInt32(0x04300000, 0x800); // acknowledge DP
            memory.Tick(10000);
            if ((memory.ReadUInt32(0x04300008) & 0x21) != 0)
                throw new Exception("SP/DP completion was delivered twice");
        }

        // FULL_SYNC does not depend on a later BREAK. A producer can submit a
        // completed RDP list and keep waiting for CPU work in the same RSP task.
        var polling = NewMemory(new uint[] {
            0x24011000, 0x40814000, 0x24011008, 0x40814800,
            0x8c010080, 0x1020fffe, 0, 0x0000000d
        });
        polling.WriteUInt32(0x1000, 0xe9000000);
        polling.WriteUInt32(0x04040010, 0x101);
        if ((polling.ReadUInt32(0x04300008) & 0x21) != 0x20
            || (polling.ReadUInt32(0x04040010) & 3) != 0)
            throw new Exception("FULL_SYNC waited for RSP BREAK or fabricated SP completion");
        polling.WriteUInt32(0x04300000, 0x800);
        polling.WriteUInt32(0x04000080, 1);
        polling.Tick(16384);
        polling.Tick(4000);
        if ((polling.ReadUInt32(0x04300008) & 0x21) != 1)
            throw new Exception("RSP completion rearmed an already retired FULL_SYNC");
    }

    private static void CheckFramebufferPublication()
    {
        foreach (bool restore in new[] { false, true })
        foreach (bool fullSync in new[] { false, true })
        foreach (bool repeatTarget in new[] { false, true })
        foreach (uint bpp in new uint[] { 2, 4 })
        {
            var memory = NewMemory(Array.Empty<uint>());
            memory.WriteUInt32(0x04400000, bpp == 2 ? 2u : 3u);
            memory.WriteUInt32(0x04400008, 32);
            memory.WriteUInt32(0x04400028, 0x00200040);
            memory.WriteUInt32(0x04400034, 0x400);
            var execute = typeof(Memory).GetMethod("ExecuteRdpDisplayList", Private)!.CreateDelegate<Func<uint, uint, uint>>(memory);
            var dispatching = typeof(Memory).GetField("_rspTaskDispatching", Private)!;
            dispatching.SetValue(memory, true);
            uint p = 0x1000;
            void Command(uint a, uint b)
            {
                BinaryPrimitives.WriteUInt32BigEndian(memory.RDRAM.AsSpan((int)p), a);
                BinaryPrimitives.WriteUInt32BigEndian(memory.RDRAM.AsSpan((int)p + 4), b);
                p += 8;
            }
            uint colorImage = bpp == 2 ? 0xff10001fu : 0xff18001fu;
            Command(0xfe000000, 0x700000);
            Command(colorImage, 0x300000);
            Command(0xef300000, 0);
            Command(0xed000000, (32u * 4 << 12) | 24u * 4);
            for (uint row = 0; row < 24; row++)
            {
                Command(0xf7000000, bpp == 2
                    ? (row % 2 == 0 ? 0xf801f801u : 0x07c107c1u)
                    : (row % 2 == 0 ? 0xff0000ffu : 0x00ff00ffu));
                Command(0xf6000000 | (31u * 4 << 12) | row * 4, row * 4);
            }
            Command(0xe9000000, 0);
            execute(0x1000, p);
            byte[] before = memory.RDRAM.AsSpan(0x300000, (int)(32 * 16 * bpp)).ToArray();
            uint start = p;
            // Change only half the frame, then let RSP wait for a CPU producer.
            // A repeated SetColorImage is also not a completed image boundary.
            Command(0xf7000000, bpp == 2 ? 0x003f003fu : 0x0000ffffu);
            Command(0xf6000000 | (31u * 4 << 12) | 7u * 4, 0);
            if (repeatTarget) Command(colorImage, 0x300000);
            uint middle = p;
            if (fullSync) Command(0xe9000000, 0);
            uint[] program = {
                0x24020000 | start, 0x40824000, // DPC_START
                0x24030000 | middle, 0x40834800, // DPC_END, partial image
                0x8c010080, 0x1020fffe, 0, // Poll shared DMEM until CPU resumes us
                0x24030000 | p, 0x40834800, 0x0000000d
            };
            for (int i = 0; i < program.Length; i++)
                BinaryPrimitives.WriteUInt32BigEndian(memory.SP_MEM_RW.AsSpan(0x1000 + i * 4), program[i]);
            dispatching.SetValue(memory, false);
            memory.WriteUInt32(0x04040010, 0x101);
            if ((memory.ReadUInt32(0x04040010) & 3) != 0) throw new Exception("Drawing task did not yield");
            byte[] after = memory.RDRAM.AsSpan(0x300000, before.Length).ToArray();
            if (before.AsSpan().SequenceEqual(after)) throw new Exception("Drawing task did not draw");
            CheckSnapshot(before, "RSP slice published a partially drawn image");
            if (restore)
            {
                using var state = new MemoryStream();
                using (var writer = new BinaryWriter(state, System.Text.Encoding.UTF8, true)) memory.SaveState(writer);
                memory = new Memory(new byte[4096]);
                R4300.memory = memory;
                state.Position = 0;
                using var reader = new BinaryReader(state);
                memory.LoadState(reader);
                CheckSnapshot(before, "Save/load published a partially drawn image");
                using var roundTrip = new MemoryStream();
                using (var writer = new BinaryWriter(roundTrip, System.Text.Encoding.UTF8, true)) memory.SaveState(writer);
                if (!state.ToArray().AsSpan().SequenceEqual(roundTrip.ToArray()))
                    throw new Exception("Pending drawing changed during save/load");
            }
            memory.WriteUInt32(0x04000080, 1);
            typeof(Memory).GetMethod("TickRspInterpreter", Private)!.Invoke(memory, new object[] { 16384u });
            CheckSnapshot(after, "Task completion did not publish its image");

            void CheckSnapshot(byte[] expected, string message)
            {
                if (!memory.TryCopyLastVisibleRdpFramebufferSnapshot(0x300000, 32, 16, bpp, out var pixels, out _, out _)
                    || !pixels.AsSpan().SequenceEqual(expected))
                    throw new Exception($"{message} (bpp={bpp}, restore={restore}, sync={fullSync}, repeatTarget={repeatTarget})");
            }
        }
    }

    private static Memory NewMemory(uint[] program)
    {
        var memory = new Memory(new byte[4096]);
        R4300.memory = memory;
        for (int i = 0; i < program.Length; i++)
            BinaryPrimitives.WriteUInt32BigEndian(memory.SP_MEM_RW.AsSpan(0x1000 + i * 4), program[i]);
        // Minimal valid OSTask descriptor; the program itself lives in IMEM.
        memory.WriteUInt32(0x04000fc0, 1);
        memory.WriteUInt32(0x04000fd0, 0x1000);
        memory.WriteUInt32(0x04000fd4, 0x1000);
        object rsp = typeof(Memory).GetField("_rspInterpreter", Private)!.GetValue(memory)!;
        var random = new Random(64321);
        foreach (string name in new[] { "_vr", "_vcc", "_vco", "_accHi", "_accMd", "_accLo" })
        {
            var array = (Array)rsp.GetType().GetField(name, Private)!.GetValue(rsp)!;
            byte[] data = new byte[Buffer.ByteLength(array)];
            random.NextBytes(data);
            Buffer.BlockCopy(data, 0, array, 0, data.Length);
        }
        var taskField = typeof(Memory).GetField("_activeRspTask", Private)!;
        object task = Activator.CreateInstance(taskField.FieldType)!;
        taskField.FieldType.GetField("Type")!.SetValue(task, 1u);
        taskField.SetValue(memory, task);
        return memory;
    }
}
