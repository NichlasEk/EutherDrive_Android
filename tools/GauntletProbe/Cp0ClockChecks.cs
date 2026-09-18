using System.Reflection;

internal static class Cp0ClockChecks
{
    internal static void Run(Assembly assembly)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        Type memoryType = assembly.GetType("EutherDrive.Core.Arcade.Vegas.VegasMemoryMap", true)!;
        Type cpuType = assembly.GetType("EutherDrive.Core.Arcade.Vegas.MipsR5000Core", true)!;
        object memory = Activator.CreateInstance(memoryType)!;
        object referenceMemory = Activator.CreateInstance(memoryType)!;
        object cpu = Activator.CreateInstance(cpuType, [memory])!;
        FieldInfo Field(string name) => cpuType.GetField(name, flags)!;
        FieldInfo MemoryField(string name) => memoryType.GetField(name, flags)!;
        var cp0 = (ulong[])Field("_cp0").GetValue(cpu)!;
        var registers = (byte[])MemoryField("_nileRegisters").GetValue(memory)!;
        var expectedRegisters = (byte[])MemoryField("_nileRegisters").GetValue(referenceMemory)!;
        var advance = cpuType.GetMethod("AdvanceCp0Count", flags)!.CreateDelegate<Action<ulong>>(cpu);
        var referenceNile = memoryType.GetMethod("AdvanceNileClock")!.CreateDelegate<Action<ulong>>(referenceMemory);
        var write = memoryType.GetMethod("WriteNileRegister32", flags)!.CreateDelegate<Action<uint, uint>>(memory);
        uint[] values = [0, 1, 0x7fffffff, 0xfffffffe, uint.MaxValue];
        ulong[] deltas = [0, 1, 2, 1023, 1024, 1537, uint.MaxValue, 0x100000000, ulong.MaxValue];
        int checks = 0;
        foreach (uint count in values)
        foreach (uint compare in values)
        foreach (ulong delta in deltas)
        for (int state = 0; state < 4; state++)
        {
            bool frozen = (state & 1) != 0;
            bool pending = (state & 2) != 0;
            for (int i = 0; i < cp0.Length; i++) cp0[i] = 0x1234567800000000UL + (uint)i;
            cp0[9] = count;
            cp0[11] = compare;
            var expected = (ulong[])cp0.Clone();
            Field("_freezeCp0CountAdvance").SetValue(cpu, frozen);
            Field("_timerInterruptPending").SetValue(cpu, pending);
            Array.Clear(registers);
            for (uint timer = 0; timer < 4; timer++)
            {
                uint offset = 0x1c0 + timer * 0x10;
                write(offset, 3 + timer);
                write(offset + 8, timer);
                write(offset + 4, 1);
            }
            registers.CopyTo(expectedRegisters, 0);
            foreach (object map in new[] { memory, referenceMemory })
            {
                MemoryField("_nileActiveTimerMask").SetValue(map, (byte)15);
                MemoryField("_nileIrqState").SetValue(map, (ushort)0x0102);
                MemoryField("_runtimeTimerInterruptActive").SetValue(map, false);
                MemoryField("_suppressRuntimeNileWatchdog").SetValue(map, false);
            }
            if (delta != 0 && !frozen)
            {
                referenceNile(delta);
                ulong distance = compare >= count ? compare - (ulong)count : 0x100000000UL - count + compare;
                expected[9] = (uint)unchecked(count + delta);
                pending |= distance != 0 && delta >= distance;
            }
            advance(delta);
            if (!cp0.AsSpan().SequenceEqual(expected) ||
                (bool)Field("_timerInterruptPending").GetValue(cpu)! != pending ||
                !registers.AsSpan().SequenceEqual(expectedRegisters) ||
                !Equals(MemoryField("_nileIrqState").GetValue(memory), MemoryField("_nileIrqState").GetValue(referenceMemory)))
                throw new InvalidOperationException($"CP0 clock mismatch count={count:x8} compare={compare:x8} delta={delta} state={state}");
            checks++;
        }
        Console.WriteLine($"cp0ClockChecks=passed cases:{checks}");
    }
}
