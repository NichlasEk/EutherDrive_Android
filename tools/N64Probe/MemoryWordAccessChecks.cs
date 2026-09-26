using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;
using Ryu64.MIPS;

internal static class MemoryWordAccessChecks
{
    private delegate bool ReadPhysical(uint physical, out uint value);
    internal static void Run(string reference, bool instructionFetch = false, bool byteAccess = false)
    {
        var context = new AssemblyLoadContext("word-access-reference", true);
        var expected = Check(context.LoadFromAssemblyPath(Path.GetFullPath(reference)), instructionFetch, byteAccess);
        context.Unload();
        var actual = Check(typeof(Memory).Assembly, instructionFetch, byteAccess);
        if (expected != actual) throw new Exception($"Word access mismatch: {expected} != {actual}");
        Console.WriteLine($"{(byteAccess ? "byteAccess" : instructionFetch ? "opcodeFetch" : "wordAccess")}={actual} values=passed exceptions=passed framebufferEpochs=passed");
    }

    private static string Check(Assembly assembly, bool instructionFetch, bool byteAccess)
    {
        var cpu = assembly.GetType("Ryu64.MIPS.R4300")!;
        var memoryType = assembly.GetType("Ryu64.MIPS.Memory")!;
        byte[] rom = new byte[4096]; new Random(64001).NextBytes(rom);
        var memory = Activator.CreateInstance(memoryType, new object[] { rom })!;
        cpu.GetField("memory")!.SetValue(null,memory);
        assembly.GetType("Ryu64.MIPS.Registers+R4300")!.GetField("PC")!.SetValue(null,0x80010000u);
        var read = instructionFetch
            ? cpu.GetMethod("ReadOpcode", BindingFlags.Static | BindingFlags.NonPublic)!.CreateDelegate<Func<uint,uint>>()
            : memoryType.GetMethod("ReadUInt32")!.CreateDelegate<Func<uint,uint>>(memory);
        var write = memoryType.GetMethod("WriteUInt32")!.CreateDelegate<Action<uint,uint>>(memory);
        if (byteAccess)
        {
            var readByte = memoryType.GetMethod("ReadUInt8")!.CreateDelegate<Func<uint,byte>>(memory);
            var writeByte = memoryType.GetMethod("WriteUInt8")!.CreateDelegate<Action<uint,byte>>(memory);
            read = address => readByte(address);
            write = (address,value) => writeByte(address,(byte)value);
        }
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
        if (byteAccess)
        {
            // Backing-array mirrors up to the exact RAM-window boundary, then
            // open bus and device aliases. Reads retain register side effects.
            foreach (uint segment in new uint[] { 0x80000000,0xa0000000 })
            foreach (uint offset in new uint[] { 0x7fffff,0x800000,0xffffff,0x1000000,0x3effffc,0x3efffff,
                0x3f00000,0x3ffffff,0x4000000,0x403ffff,0x404001f,0x408001f,0x410000f,
                0x4200007,0x430000f,0x4400013,0x4500007,0x4600013,0x4700007,0x480001b,0x48fffff,0x4900000 })
            {
                uint address = segment + offset;
                if (offset <= 0x3efffff)
                {
                    write(address,(uint)(offset * 73 + 17)); operations++;
                    Read(address); Read(address ^ 0x20000000);
                }
                else Read(address);
            }
        }
        if (instructionFetch)
            foreach (uint address in new uint[] { 0x10000,0x70020000,0xc0020000 }) Read(address);
        if (instructionFetch)
        {
            // Repeated fetches on mapped pages, with live code writes, ASID
            // switches, remapping, reset and save/load between cache hits.
            var tlb = assembly.GetType("Ryu64.MIPS.TLB")!;
            var cop0 = (ulong[])assembly.GetType("Ryu64.MIPS.Registers+COP0")!.GetField("Reg")!.GetValue(null)!;
            var resetTlb = tlb.GetMethod("Reset")!.CreateDelegate<Action>();
            var map = tlb.GetMethod("WriteTLBEntryIndexed")!.CreateDelegate<Action>();
            var saveTlb = tlb.GetMethod("SaveState")!.CreateDelegate<Action<BinaryWriter>>();
            var loadTlb = tlb.GetMethod("LoadState")!.CreateDelegate<Action<BinaryReader>>();
            resetTlb();
            for (uint phase = 0; phase < 64; phase++)
            {
                for (uint asid = 0; asid < 2; asid++)
                {
                    cop0[Registers.COP0.INDEX_REG] = asid;
                    cop0[Registers.COP0.ENTRYHI_REG] = 0xe0000000u | asid;
                    cop0[Registers.COP0.PAGEMASK_REG] = (phase % 2 == 0 ? 0u : 3u) << 13;
                    cop0[Registers.COP0.ENTRYLO0_REG] = ((0x100u + phase * 8 + asid * 4) << 6) | 6u;
                    cop0[Registers.COP0.ENTRYLO1_REG] = ((0x104u + phase * 8 + asid * 4) << 6) | 6u;
                    map();
                }
                for (uint asid = 0; asid < 2; asid++)
                {
                    cop0[Registers.COP0.ENTRYHI_REG] = asid;
                    for (uint offset = 0; offset < 0x8000; offset += 0x3fc)
                    {
                        Read(0xe0000000u + offset);
                        write(0x80100000u + phase * 0x8000u + asid * 0x4000u + offset, phase ^ offset);
                        Read(0xe0000000u + offset);
                    }
                }
                using var tlbBytes = new MemoryStream();
                using (var w = new BinaryWriter(tlbBytes, System.Text.Encoding.UTF8, true)) saveTlb(w);
                Read(0xe0000000u);
                resetTlb(); Read(0xe0000000u);
                tlbBytes.Position = 0;
                using (var r = new BinaryReader(tlbBytes, System.Text.Encoding.UTF8, true)) loadTlb(r);
                Read(0xe0000000u);
            }
            var fast = memoryType.GetMethod("TryReadRdramUInt32PhysicalFast", BindingFlags.Instance | BindingFlags.NonPublic)!
                .CreateDelegate<ReadPhysical>(memory);
            void Physical(uint address)
            {
                writer.Write(address); writer.Write(fast(address, out uint word)); writer.Write(word); operations++;
            }
            foreach (uint segment in new uint[] { 0,0x80000000,0xa0000000,0xe0000000 })
            foreach (uint offset in new uint[] { 0,1,2,3,4,0x7ffff8,0x7ffffb,0x7ffffc,0x7ffffd,0x7ffffe,0x7fffff,0x800000,0x1fffffff })
                Physical(segment | offset);
            uint sample = 6400930;
            for (int i = 0; i < 65536; i++)
            {
                sample = unchecked(sample * 1664525 + 1013904223);
                Physical(sample); Physical(sample & 0x7fffff);
            }
        }
        writer.Flush();
        string values = Convert.ToHexString(SHA256.HashData(result.ToArray()));
        result.SetLength(0); save(writer); writer.Flush();
        string state = Convert.ToHexString(SHA256.HashData(result.ToArray()));
        return $"operations:{operations}/values:{values}/state:{state}";
    }
}
