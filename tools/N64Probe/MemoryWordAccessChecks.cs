using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;
using Ryu64.MIPS;

internal static class MemoryWordAccessChecks
{
    internal static void Run(string reference)
    {
        var context = new AssemblyLoadContext("word-access-reference", true);
        var expected = Check(context.LoadFromAssemblyPath(Path.GetFullPath(reference)));
        context.Unload();
        var actual = Check(typeof(Memory).Assembly);
        if (expected != actual) throw new Exception($"Word access mismatch: {expected} != {actual}");
        Console.WriteLine($"wordAccess={actual} values=passed exceptions=passed framebufferEpochs=passed");
    }

    private static string Check(Assembly assembly)
    {
        var cpu = assembly.GetType("Ryu64.MIPS.R4300")!;
        var memoryType = assembly.GetType("Ryu64.MIPS.Memory")!;
        byte[] rom = new byte[4096]; new Random(64001).NextBytes(rom);
        var memory = Activator.CreateInstance(memoryType, new object[] { rom })!;
        cpu.GetField("memory")!.SetValue(null,memory);
        assembly.GetType("Ryu64.MIPS.Registers+R4300")!.GetField("PC")!.SetValue(null,0x80010000u);
        var read = memoryType.GetMethod("ReadUInt32")!.CreateDelegate<Func<uint,uint>>(memory);
        var write = memoryType.GetMethod("WriteUInt32")!.CreateDelegate<Action<uint,uint>>(memory);
        var save = cpu.GetMethod("SaveState")!.CreateDelegate<Action<BinaryWriter>>();
        byte[] ram = (byte[])memoryType.GetField("RDRAM")!.GetValue(memory)!;
        new Random(64002).NextBytes(ram);
        memoryType.GetMethod("RegisterFramebufferInfo", BindingFlags.Instance | BindingFlags.NonPublic)!
            .CreateDelegate<Action<uint,uint,uint,uint>>(memory)(0x20000,2,320,240);
        var addresses = new List<uint>();
        foreach (uint segment in new uint[] { 0x80000000,0xa0000000 })
        {
            foreach (uint offset in new uint[] { 0,1,2,3,0xfc,0xfd,0xff,0x100,0x180,0xffc,0xffd,0xffe,0xfff,0x1000,0x1ffff,0x20000,0x20001,0x457fc,0x7ffff8,0x7ffffb,0x7ffffc,0x7ffffd,0x7ffffe,0x7fffff,0x800000 })
                addresses.Add(segment + offset);
            var random = new Random(64003);
            for (int i = 0; i < 16384; i++) addresses.Add(segment + (uint)random.Next(ram.Length - 3));
        }
        using var result = new MemoryStream(); using var writer = new BinaryWriter(result);
        uint value = 0;
        int operations = 0;
        void Read(uint address)
        {
            writer.Write(address);
            try { writer.Write(read(address)); writer.Write(""); }
            catch (Exception ex) { writer.Write(0u); writer.Write(ex.GetType().FullName + ":" + ex.Message); }
            operations++;
        }
        foreach (uint address in addresses)
        {
            Read(address);
            value = unchecked(value * 1664525 + 1013904223);
            try { write(address,value); writer.Write(""); }
            catch (Exception ex) { writer.Write(ex.GetType().FullName + ":" + ex.Message); }
            operations++;
            Read(address); Read(address ^ 0x20000000);
        }
        foreach (uint address in new uint[] { 0xb0000000,0xb0000001,0xb0000ffc,0xa4400010,0xa404001c,0xa404001c }) Read(address);
        writer.Flush();
        string values = Convert.ToHexString(SHA256.HashData(result.ToArray()));
        result.SetLength(0); save(writer); writer.Flush();
        string state = Convert.ToHexString(SHA256.HashData(result.ToArray()));
        return $"operations:{operations}/values:{values}/state:{state}";
    }
}
