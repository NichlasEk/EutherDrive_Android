using System.Buffers.Binary;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;
using Ryu64.MIPS;

internal static class DmaChecks
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    internal static void Run(string? reference)
    {
        string current = Check(typeof(Memory).Assembly);
        Console.WriteLine($"dmaDigest={current}");
        if (reference != null)
        {
            var context = new AssemblyLoadContext("reference-dma", isCollectible: true);
            var assembly = context.LoadFromAssemblyPath(Path.GetFullPath(reference));
            string expected = Check(assembly);
            if (current != expected) throw new Exception($"DMA differs from reference: {expected}");
            Console.WriteLine("dmaDifferential=passed");
            context.Unload();
        }
    }

    private static string Check(Assembly assembly)
    {
        Type type = assembly.GetType("Ryu64.MIPS.Memory")!;
        object memory = Activator.CreateInstance(type, new object[] { new byte[4096] })!;
        var execute = type.GetMethod("ExecuteSpDma", Private, new[] { typeof(bool) })!.CreateDelegate<Action<bool>>(memory);
        var tick = type.GetMethod("TickRspInterpreter", Private)!.CreateDelegate<Action<uint>>(memory);
        var save = type.GetMethod("SaveState")!.CreateDelegate<Action<BinaryWriter>>(memory);
        byte[] Array(string name) => (byte[])type.GetField(name)!.GetValue(memory)!;
        void Reg(string name, uint value) => BinaryPrimitives.WriteUInt32BigEndian(Array(name), value);
        var ram = Array("RDRAM");
        var sp = Array("SP_MEM_RW");
        new Random(631).NextBytes(ram);
        new Random(932).NextBytes(sp);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var state = new MemoryStream();
        using var writer = new BinaryWriter(state);
        int cases = 0;
        var timer = Stopwatch.StartNew();
        foreach (bool read in new[] { true, false })
        foreach (uint bank in new[] { 0u, 0x1000u })
        foreach (uint start in new[] { 0u, 0x3f8u, 0xffbu })
        foreach (uint dram in new[] { 0x1003u, 0x7ffff8u, 0xfffff9u })
        foreach (uint length in new[] { 0u, 0xfffu, 0x01703017u })
        {
            Reg("SP_MEM_ADDR_REG_RW", bank | start);
            Reg("SP_DRAM_ADDR_REG_RW", dram);
            Reg(read ? "SP_RD_LEN_REG_RW" : "SP_WR_LEN_REG_RW", length);
            execute(read);
            // Serialize busy state, pixels, dirty-page epochs, and register progress.
            Record();
            // Queue a second DMA while busy; its captured descriptors must survive.
            Reg("SP_MEM_ADDR_REG_RW", bank | 0xff8u);
            Reg("SP_DRAM_ADDR_REG_RW", 0x7ffff8u);
            Reg(read ? "SP_RD_LEN_REG_RW" : "SP_WR_LEN_REG_RW", 0x0100201fu);
            execute(read);
            Record();
            tick(0x100000);
            Record();
            tick(0x100000);
            Record();
            cases++;
        }
        Console.WriteLine($"dmaCases={cases} milliseconds={timer.Elapsed.TotalMilliseconds:F2}");
        return Convert.ToHexString(hash.GetHashAndReset());

        void Record()
        {
            state.SetLength(0);
            state.Position = 0;
            save(writer);
            writer.Flush();
            hash.AppendData(state.GetBuffer(), 0, (int)state.Length);
        }
    }
}
