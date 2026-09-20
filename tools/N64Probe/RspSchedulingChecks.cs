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
        Console.WriteLine($"rspSliceBoundaries={cases} saveRestore=passed producerConsumer=passed dpSync=passed");
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
