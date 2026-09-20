using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;
using Ryu64.MIPS;

internal static class TlbCacheChecks
{
    internal static void Run(string reference)
    {
        var context = new AssemblyLoadContext("tlb-reference", true);
        string expected = Check(context.LoadFromAssemblyPath(Path.GetFullPath(reference)));
        string actual = Check(typeof(TLB).Assembly);
        if (actual != expected) throw new Exception($"TLB translation/state differs: {expected}/{actual}");
        context.Unload();
        CheckReaderThread();
        Console.WriteLine($"tlbCacheDifferential=passed translations=264192 readerThreadUpdates=128 sha256={actual}");
    }

    private static void CheckReaderThread()
    {
        TLB.Reset();
        Registers.COP0.Reg[Registers.COP0.INDEX_REG] = 0;
        Registers.COP0.Reg[Registers.COP0.ENTRYHI_REG] = 0xe0000000;
        Registers.COP0.Reg[Registers.COP0.PAGEMASK_REG] = 0;
        Registers.COP0.Reg[Registers.COP0.ENTRYLO1_REG] = 7;
        using var requests = new System.Collections.Concurrent.BlockingCollection<uint>();
        using var responses = new System.Collections.Concurrent.BlockingCollection<uint>();
        var reader = Task.Run(() =>
        {
            foreach (uint address in requests.GetConsumingEnumerable())
                responses.Add(TLB.TranslateAddress(address, true));
        });
        try
        {
            for (uint i = 0; i < 128; i++)
            {
                Registers.COP0.Reg[Registers.COP0.ENTRYLO0_REG] = (i << 6) | 7;
                TLB.WriteTLBEntryIndexed();
                for (int repeat = 0; repeat < 2; repeat++)
                {
                    requests.Add(0xe0000123);
                    if (!responses.TryTake(out uint physical, 5000) || physical != (i << 12) + 0x123)
                        throw new Exception("Reader thread retained a stale TLB mapping");
                }
            }
        }
        finally { requests.CompleteAdding(); reader.GetAwaiter().GetResult(); }
    }

    private static string Check(Assembly assembly)
    {
        Type type = assembly.GetType("Ryu64.MIPS.TLB")!;
        var regs = (ulong[])assembly.GetType("Ryu64.MIPS.Registers+COP0")!.GetField("Reg")!.GetValue(null)!;
        var reset = type.GetMethod("Reset")!.CreateDelegate<Action>();
        var write = type.GetMethod("WriteTLBEntryIndexed")!.CreateDelegate<Action>();
        var randomWrite = type.GetMethod("WriteTLBEntryRandom")!.CreateDelegate<Action>();
        var read = type.GetMethod("ReadTLBEntry")!.CreateDelegate<Action>();
        var save = type.GetMethod("SaveState")!.CreateDelegate<Action<BinaryWriter>>();
        var load = type.GetMethod("LoadState")!.CreateDelegate<Action<BinaryReader>>();
        var translate = type.GetMethod("TranslateAddress", new[] { typeof(uint), typeof(bool), typeof(bool) })!
            .CreateDelegate<Func<uint, bool, bool, uint>>();
        var random = new Random(64920);
        uint[] masks = { 0, 3, 15, 63, 255, 1023, 4095, 7 }; // include malformed masks, retaining existing behavior
        using var result = new MemoryStream();
        using var output = new BinaryWriter(result);
        reset();
        byte[] snapshot = Array.Empty<byte>();
        for (int phase = 0; phase < 256; phase++)
        {
            if (phase % 16 == 0) reset();
            for (int i = 0; i < 32; i++)
            {
                regs[Registers.COP0.INDEX_REG] = (uint)i;
                regs[Registers.COP0.RANDOM_REG] = (uint)i;
                regs[Registers.COP0.ENTRYHI_REG] = 0xe0000000u | ((uint)random.Next(128) << 13) | (uint)random.Next(4);
                regs[Registers.COP0.PAGEMASK_REG] = masks[random.Next(masks.Length)] << 13;
                regs[Registers.COP0.ENTRYLO0_REG] = (uint)random.Next(1 << 28);
                regs[Registers.COP0.ENTRYLO1_REG] = (uint)random.Next(1 << 28);
                if ((i & 1) == 0) write(); else randomWrite();
            }
            if (phase % 4 == 0)
            {
                using var bytes = new MemoryStream();
                using var writer = new BinaryWriter(bytes);
                save(writer); writer.Flush(); snapshot = bytes.ToArray();
            }
            for (int i = 0; i < 512; i++)
            {
                uint address = 0xe0000000u + (uint)random.Next(1 << 22);
                regs[Registers.COP0.ENTRYHI_REG] = (uint)random.Next(4);
                for (int repeat = 0; repeat < 2; repeat++)
                {
                    // Second access changes only the offset in a cached subpage.
                    uint at = repeat == 0 ? address : (address & ~0xfffu) | (uint)random.Next(4096);
                    try { output.Write((byte)0); output.Write(translate(at, (i & 1) != 0, (i & 2) != 0)); }
                    catch (Exception ex) { output.Write((byte)1); output.Write(ex.GetType().FullName!); output.Write(ex.Message); }
                    // Populate, then invalidate a previously matching mapping.
                    if (i % 32 == 0 && repeat == 0)
                    {
                        regs[Registers.COP0.INDEX_REG] = (uint)random.Next(32);
                        regs[Registers.COP0.ENTRYLO0_REG] = 0;
                        regs[Registers.COP0.ENTRYLO1_REG] = 0;
                        write();
                    }
                    if (i == 128 && repeat == 0) reset();
                    if (i == 256 && repeat == 0)
                    {
                        using var reader = new BinaryReader(new MemoryStream(snapshot));
                        load(reader);
                    }
                }
                if (i % 64 == 0)
                {
                    regs[Registers.COP0.INDEX_REG] = (uint)random.Next(32);
                    read(); // changes ASID without modifying any TLB entry
                    output.Write(translate(address, false, false));
                }
            }
            save(output);
        }
        output.Flush();
        return Convert.ToHexString(SHA256.HashData(result.ToArray()));
    }
}
