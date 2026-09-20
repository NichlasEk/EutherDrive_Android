using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;
using Ryu64.MIPS;

// Compare serialized memory after framebuffer replacement, page-edge writes,
// dirty-page consumption and restoration of older tracking bounds.
internal static class FramebufferWriteChecks
{
    internal static void Run(string reference)
    {
        var context = new AssemblyLoadContext("framebuffer-write-reference", true);
        string expected = Check(context.LoadFromAssemblyPath(Path.GetFullPath(reference)));
        context.Unload();
        string actual = Check(typeof(Memory).Assembly);
        if (actual != expected) throw new Exception($"Framebuffer tracking differs: {expected} != {actual}");
        Console.WriteLine($"framebufferWritePhases=48 fullState=passed restore=passed sha256={actual}");
    }

    private static string Check(Assembly assembly)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        Type type = assembly.GetType("Ryu64.MIPS.Memory")!;
        object memory = Activator.CreateInstance(type, new object[] { new byte[4096] })!;
        assembly.GetType("Ryu64.MIPS.R4300")!.GetField("memory")!.SetValue(null, memory);
        var register = type.GetMethod("RegisterFramebufferInfo", flags)!.CreateDelegate<Action<uint,uint,uint,uint>>(memory);
        var consume = type.GetMethod("NotifyFramebufferConsumerRead")!.CreateDelegate<Action<uint,uint>>(memory);
        var write8 = type.GetMethod("WriteUInt8")!.CreateDelegate<Action<uint,byte>>(memory);
        var write16 = type.GetMethod("WriteUInt16")!.CreateDelegate<Action<uint,ushort>>(memory);
        var write32 = type.GetMethod("WriteUInt32")!.CreateDelegate<Action<uint,uint>>(memory);
        var write64 = type.GetMethod("WriteUInt64")!.CreateDelegate<Action<uint,ulong>>(memory);
        var save = type.GetMethod("SaveState")!.CreateDelegate<Action<BinaryWriter>>(memory);
        var load = type.GetMethod("LoadState")!.CreateDelegate<Action<BinaryReader>>(memory);
        using var state = new MemoryStream();
        using var writer = new BinaryWriter(state, System.Text.Encoding.UTF8, true);
        using var result = new MemoryStream();
        byte[] Snapshot() { state.SetLength(0); save(writer); writer.Flush(); return state.ToArray(); }
        void Restore(byte[] bytes) { using var reader = new BinaryReader(new MemoryStream(bytes)); load(reader); }
        byte[] earlier = Snapshot();
        var random = new Random(6400920);
        uint[] origins = { 0x20000,0x20ffe,0x21000,0x30000,0x50000,0x80000,0x60000,0x20000,0x7ffff0,0x1000,0x20000,0x22000 };
        for (int phase = 0; phase < 48; phase++)
        {
            uint origin = origins[phase % origins.Length];
            register(origin,2, phase % 3 == 0 ? 320u : 8u, phase % 4 == 0 ? 2u : 1u);
            consume(origin, 8192);
            if (phase % 8 == 0) type.GetField("_rdramWriteEpoch", flags)!.SetValue(memory,uint.MaxValue - 2);
            if (phase % 12 == 0) earlier = Snapshot();
            if (phase % 12 == 11) Restore(earlier);
            if (phase == 30)
            {
                // A malformed but readable saved framebuffer must use the
                // original overlap logic when its end wraps around uint.
                var infos = (Array)type.GetField("_fbInfos",flags)!.GetValue(memory)!;
                object info = infos.GetValue(0)!;
                foreach (var field in new[] { ("Addr",0x200000u),("Size",65535u),("Width",65535u),("Height",1u) })
                    info.GetType().GetField(field.Item1)!.SetValue(info,field.Item2);
                infos.SetValue(info,0); Restore(Snapshot());
            }
            var addresses = new List<uint>();
            for (uint page = 0; page < 2048; page++) addresses.Add(page * 4096);
            foreach (int offset in new[] { -8,-4,-1,0,1,2,3,4,8,15,16,31,32,63,64,639,640,4093,4095,4096 })
                addresses.Add(unchecked(origin + (uint)offset));
            for (int n = 0; n < 64; n++) addresses.Add((uint)random.Next(0x800000 - 8));
            foreach (uint physical in addresses)
            {
                if (physical + 8 > 0x800000) continue;
                uint address = physical | (phase % 2 == 0 ? 0x80000000u : 0xa0000000u);
                uint value = (uint)random.NextInt64();
                write8(address,(byte)value); write16(address,(ushort)value);
                write32(address,value); write64(address,((ulong)value << 32) | ~value);
            }
            result.Write(SHA256.HashData(Snapshot()));
        }
        return Convert.ToHexString(SHA256.HashData(result.ToArray()));
    }
}
