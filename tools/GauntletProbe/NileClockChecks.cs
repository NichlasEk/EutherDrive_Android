using System.Buffers.Binary;
using System.Reflection;

internal static class NileClockChecks
{
    internal static void Run(Assembly assembly)
    {
        Type type = assembly.GetType("EutherDrive.Core.Arcade.Vegas.VegasMemoryMap", true)!;
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        object memory = Activator.CreateInstance(type)!;
        FieldInfo Field(string name) => type.GetField(name, flags)!;
        var registers = (byte[])Field("_nileRegisters").GetValue(memory)!;
        var advance = type.GetMethod("AdvanceNileClock")!.CreateDelegate<Action<ulong>>(memory);
        var write = type.GetMethod("WriteNileRegister32", flags)!.CreateDelegate<Action<uint, uint>>(memory);
        var restore = type.GetMethod("RestoreNileTimerMaskAfterSnapshotLoad")!.CreateDelegate<Action>(memory);
        uint[] reloads = [0, 1, 2, 1023, 1024, 65535, 0x7fffffff, uint.MaxValue];
        ulong[] ticks = [0, 1, 1023, 1024, 1537, 2048, 4096, 1048576,
            0x40000000000, ulong.MaxValue];
        int checks = 0;
        foreach (uint reload in reloads)
        foreach (uint counter in new[] { 0U, 1U, reload, unchecked(reload + 1), unchecked(reload + 2), uint.MaxValue })
        foreach (ulong step in ticks)
        for (byte mask = 0; mask < 16; mask++)
        for (int state = 0; state < 4; state++)
        {
            Array.Clear(registers);
            for (uint timer = 0; timer < 4; timer++)
            {
                uint offset = 0x1c0 + timer * 0x10;
                write(offset, reload);
                write(offset + 8, counter);
                write(offset + 4, (uint)((mask >> (int)timer) & 1));
            }
            bool active = (state & 1) != 0, suppress = (state & 2) != 0;
            Field("_runtimeTimerInterruptActive").SetValue(memory, active);
            Field("_suppressRuntimeNileWatchdog").SetValue(memory, suppress);
            Field("_nileIrqState").SetValue(memory, (ushort)0x0102);
            var expected = (byte[])registers.Clone();
            ushort expectedIrq = 0x0102;
            // Original algorithm retained as the differential oracle, including
            // out-of-range counters and one tick for sub-1024 clock increments.
            ulong timerTicks = Math.Max(1UL, step >> 10);
            for (int timer = 0; timer < 4; timer++)
            {
                if ((mask & (1 << timer)) == 0) continue;
                ulong period = (ulong)reload + 1;
                ulong decrement = period == 0 ? timerTicks : timerTicks % period;
                bool expired = timerTicks >= (ulong)counter + 1 || (period != 0 && timerTicks >= period);
                ulong next = counter >= decrement ? counter - decrement : period - ((decrement - counter) % period);
                if (next == period) next = 0;
                BinaryPrimitives.WriteUInt32LittleEndian(expected.AsSpan(0x1c8 + timer * 0x10), (uint)next);
                if (expired && timer == 2) expectedIrq |= 1 << 6;
                if (expired && timer == 3 && !active && !suppress) expectedIrq |= 1 << 5;
            }
            advance(step);
            if (!registers.AsSpan().SequenceEqual(expected) ||
                (ushort)Field("_nileIrqState").GetValue(memory)! != expectedIrq ||
                (byte)Field("_nileActiveTimerMask").GetValue(memory)! != mask ||
                (bool)Field("_runtimeTimerInterruptActive").GetValue(memory)! != active)
                throw new InvalidOperationException($"Nile clock mismatch reload={reload:x8} counter={counter:x8} ticks={step} mask={mask} state={state}");
            checks++;
        }
        // Snapshot imports replace register bytes without passing write hooks.
        // Exercise a stale cache in both directions, preserving all device bytes.
        for (byte mask = 0; mask < 16; mask++)
        {
            for (int timer = 0; timer < 4; timer++)
                BinaryPrimitives.WriteUInt32LittleEndian(registers.AsSpan(0x1c4 + timer * 0x10),
                    (uint)((mask >> timer) & 1));
            Field("_nileActiveTimerMask").SetValue(memory, (byte)(mask ^ 15));
            var before = (byte[])registers.Clone();
            restore();
            if ((byte)Field("_nileActiveTimerMask").GetValue(memory)! != mask ||
                !registers.AsSpan().SequenceEqual(before))
                throw new InvalidOperationException("Nile snapshot cache restore mismatch");
            checks++;
        }
        // Fractional CPU-to-Nile ticks are rounded per call. This is a guard
        // against future JIT batching that incorrectly aggregates clock steps.
        for (uint timer = 0; timer < 4; timer++) write(0x1c4 + timer * 0x10, 0);
        write(0x1e0, 1000);
        write(0x1e8, 100);
        write(0x1e4, 1);
        advance(1537);
        advance(1537);
        if (BinaryPrimitives.ReadUInt32LittleEndian(registers.AsSpan(0x1e8)) != 98)
            throw new InvalidOperationException("Nile per-call rounding changed");
        checks++;
        write(0x1e8, 100);
        advance(3074);
        if (BinaryPrimitives.ReadUInt32LittleEndian(registers.AsSpan(0x1e8)) != 97)
            throw new InvalidOperationException("Nile aggregated-step reference changed");
        checks++;

        // Exercise the actual serializer/loader used by both probe and desktop,
        // not only the cache-rebuild helper. No ROM is required for this roundtrip.
        MethodInfo FindSnapshotMethod(string name, int parameters) => typeof(NileClockChecks).Assembly
            .GetTypes().SelectMany(t => t.GetMethods(BindingFlags.Static | BindingFlags.NonPublic))
            .Single(m => m.Name.Contains(name, StringComparison.Ordinal) && m.GetParameters().Length == parameters);
        for (uint timer = 0; timer < 4; timer++)
            write(0x1c4 + timer * 0x10, timer is 0 or 3 ? 1U : 0U);
        object loaded = Activator.CreateInstance(type)!;
        Field("_nileActiveTimerMask").SetValue(loaded, (byte)6);
        Field("_suppressRuntimeNileWatchdog").SetValue(memory,
            Field("_suppressRuntimeNileWatchdog").GetValue(loaded));
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true))
            FindSnapshotMethod("SaveMemoryMap", 2).Invoke(null, [writer, memory]);
        stream.Position = 0;
        using (var reader = new BinaryReader(stream, System.Text.Encoding.UTF8, leaveOpen: true))
            FindSnapshotMethod("LoadMemoryMap", 3).Invoke(null, [reader, loaded, 18]);
        var loadedRegisters = (byte[])Field("_nileRegisters").GetValue(loaded)!;
        if ((byte)Field("_nileActiveTimerMask").GetValue(loaded)! != 9 ||
            !registers.AsSpan().SequenceEqual(loadedRegisters) || stream.Position != stream.Length)
            throw new InvalidOperationException("Nile actual snapshot roundtrip failed");
        checks++;
        advance(1537);
        type.GetMethod("AdvanceNileClock")!.CreateDelegate<Action<ulong>>(loaded)(1537);
        if (!registers.AsSpan().SequenceEqual(loadedRegisters) ||
            !Equals(Field("_nileIrqState").GetValue(memory), Field("_nileIrqState").GetValue(loaded)))
            throw new InvalidOperationException("Nile snapshot continuation diverged");
        checks++;
        Console.WriteLine($"nileClockChecks=passed cases:{checks}");
    }
}
