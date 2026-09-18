using System.Reflection;

internal static class RuntimeMainStateChecks
{
    internal static void Run(Assembly assembly)
    {
        Type type = assembly.GetType("EutherDrive.Core.Arcade.Vegas.VegasMemoryMap", true)!;
        object memory = Activator.CreateInstance(type)!;
        byte[] ram = (byte[])type.GetField("_mainRam", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(memory)!;
        var read = type.GetProperty("RuntimeMainState")!.GetMethod!.CreateDelegate<Func<uint>>(memory);
        const int offset = 0x00227ab0;
        int checks = 0;
        void Check()
        {
            // Independent original byte-wise expression, including high-bit
            // values. Each call follows a direct RAM update (no cached state).
            uint expected = (uint)(ram[offset] | ram[offset + 1] << 8 |
                ram[offset + 2] << 16 | ram[offset + 3] << 24);
            byte[] before = ram.AsSpan(offset - 4, 12).ToArray();
            if (read() != expected || !ram.AsSpan(offset - 4, 12).SequenceEqual(before))
                throw new InvalidOperationException($"RuntimeMainState mismatch, expected {expected:x8}");
            checks++;
        }
        ram.AsSpan(offset - 4, 12).Fill(0xa5);
        for (int lane = 0; lane < 4; lane++)
        for (int value = 0; value < 256; value++)
        {
            ram.AsSpan(offset, 4).Fill(0xa5);
            ram[offset + lane] = (byte)value;
            Check();
        }
        var random = new Random(0x5a17);
        for (int sample = 0; sample < 4096; sample++)
        {
            random.NextBytes(ram.AsSpan(offset, 4));
            Check();
        }
        Console.WriteLine($"runtimeMainStateChecks=passed cases:{checks}");
    }
}
