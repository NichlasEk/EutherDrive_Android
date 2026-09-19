using System.Buffers.Binary;
using System.Reflection;
using EutherDrive.Core.Arcade.Taito;

internal static class TaskFrameFilterChecks
{
    internal static void Run()
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        Type type = typeof(DariusGaidenAdapter).GetNestedType("TaitoF3MainBus", BindingFlags.NonPublic)!;
        object bus = Activator.CreateInstance(type, nonPublic: true)!;
        byte[] ram = (byte[])type.GetField("_workRam", flags)!.GetValue(bus)!;
        uint[] observed = (uint[])type.GetField("_observedTaskStacks", flags)!.GetValue(bus)!;
        var record = type.GetMethod("RecordObservedTaskStack", flags)!.CreateDelegate<Action<uint>>(bus);
        var track = type.GetMethod("TrackTaskFrameWrite", flags)!.CreateDelegate<Action<uint>>(bus);
        var pcProperty = type.GetProperty("CurrentCpuPc")!;
        string[] fields = ["LastTaskFrameWritePc", "LastTaskFrameWriteAddress", "LastTaskFrameWriteStack", "LastTaskFrameWriteFramePc",
            "FirstBadTaskFrameWritePc", "FirstBadTaskFrameWriteAddress", "FirstBadTaskFrameWriteStack", "FirstBadTaskFrameWriteFramePc"];
        PropertyInfo[] properties = fields.Select(n => type.GetProperty(n)!).ToArray();
        Array.Fill(ram, (byte)0xff);
        uint[] stacks = [0x401fff, 0x402000, 0x402001, 0x402100, 0x404000, 0x4066b2, 0x4066b3, 0x4066b4, uint.MaxValue];
        for (int slot = 0; slot < 32; slot++)
            BinaryPrimitives.WriteUInt32BigEndian(ram.AsSpan(0x66b8 + slot * 4), stacks[slot % stacks.Length]);
        // Exercise both live-table fallback and the observed-stack priority.
        int checks = 0;
        for (int mode = 0; mode < 2; mode++)
        {
            if (mode != 0)
                foreach (uint stack in stacks.Reverse()) record(stack);
            int count = (int)type.GetField("_observedTaskStackCount", flags)!.GetValue(bus)!;
            for (uint offset = 0x1ff0; offset < 0x6710; offset++)
            foreach (uint mirror in new uint[] { 0, 0x20000 })
            {
                uint address = 0x400000 + offset + mirror;
                const uint pc = 0x123456;
                pcProperty.SetValue(bus, pc);
                foreach (var property in properties) property.SetValue(bus, 0U);
                uint canonical = 0x400000 + offset;
                uint found = 0;
                for (int i = 0; i < count && found == 0; i++)
                    if (canonical >= observed[i] + 60 && canonical <= observed[i] + 67) found = observed[i];
                for (int slot = 0; slot < 32 && found == 0; slot++)
                {
                    uint stack = BinaryPrimitives.ReadUInt32BigEndian(ram.AsSpan(0x66b8 + slot * 4));
                    if (stack >= 0x402000 && stack < 0x4066b4 && canonical >= stack + 60 && canonical <= stack + 67)
                        found = stack;
                }
                uint framePc = found == 0 ? 0 : BinaryPrimitives.ReadUInt32BigEndian(ram.AsSpan((int)(found - 0x400000 + 62)));
                uint[] expected = found == 0 ? new uint[8] :
                    [pc, canonical, found, framePc, framePc == uint.MaxValue ? pc : 0,
                        framePc == uint.MaxValue ? canonical : 0, framePc == uint.MaxValue ? found : 0,
                        framePc == uint.MaxValue ? framePc : 0];
                track(address);
                if (!properties.Select(p => (uint)p.GetValue(bus)!).SequenceEqual(expected))
                    throw new InvalidOperationException($"Task frame filter mismatch mode={mode} address={address:x8}");
                checks++;
            }
        }
        Console.WriteLine($"taskFrameFilterChecks=passed cases:{checks}");
    }
}
