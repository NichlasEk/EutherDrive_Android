using System.Buffers.Binary;
using System.Reflection;
using Ryu64.MIPS;

internal static class LowViFramebufferChecks
{
    internal static void Run()
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        int cases = 0;
        foreach (int bpp in new[] { 2, 4 })
        foreach (uint low in new uint[] { 0, 0x400, 0x800 })
        {
            var core = new Ryu64Core.Ryu64Core();
            var memory = new Memory(new byte[4096]);
            R4300.memory = memory;
            typeof(Ryu64Core.Ryu64Core).GetField("isRunning", flags)!.SetValue(core, true);
            typeof(Memory).GetField("_rspTaskDispatching", flags)!.SetValue(memory, true);
            var execute = typeof(Memory).GetMethod("ExecuteRdpDisplayList", flags)!.CreateDelegate<Func<uint, uint, uint>>(memory);
            memory.WriteUInt32(0xa4400000, bpp == 2 ? 2u : 3u);
            memory.WriteUInt32(0xa4400008, 32);
            memory.WriteUInt32(0xa4400028, 0x00200040); // 16 visible rows
            memory.WriteUInt32(0xa4400034, 0x400);
            uint p = 0x100000;
            void Command(uint a, uint b)
            {
                BinaryPrimitives.WriteUInt32BigEndian(memory.RDRAM.AsSpan((int)p), a);
                BinaryPrimitives.WriteUInt32BigEndian(memory.RDRAM.AsSpan((int)p + 4), b);
                execute(p, p + 8); p += 8;
            }
            byte[] Render(uint address, bool other)
            {
                Command(0xfe000000, 0x700000); // Distinct depth target, including for color address zero.
                Command(bpp == 2 ? 0xff10001fu : 0xff18001fu, address);
                Command(0xef300000, 0); // Fill cycle
                Command(0xed000000, (32u * 4 << 12) | 24u * 4);
                for (uint y = 0; y < 24; y++)
                {
                    uint color = bpp == 2
                        ? (y % 2 == 0 ? 0xf801f801u : 0x07c107c1u)
                        : (y % 2 == 0 ? 0xff0000ffu : 0x00ff00ffu);
                    if (other) color = bpp == 2 ? color ^ 0xfffefffeu : color ^ 0xffffff00u;
                    Command(0xf7000000, color);
                    Command(0xf6000000 | (31u * 4 << 12) | y * 4, y * 4);
                }
                // Low-address fill clears intentionally do not publish by themselves.
                // Request publication as a triangle/texture draw does, then FullSync.
                typeof(Memory).GetMethod("QueueVisibleRdpFramebufferSnapshot", flags)!.Invoke(memory, new object[] { (uint)bpp, 0L });
                Command(0xe9000000, 0);
                return memory.RDRAM.AsSpan((int)address, 32 * 24 * bpp).ToArray();
            }
            byte[] lowImage = Render(low, false);
            if (!memory.TryCopyLastVisibleRdpFramebufferSnapshot(low, 32, 16, (uint)bpp, out _, out _, out _))
                throw new Exception($"Missing low snapshot after render: {low:x8}");
            // The save format retains the latest snapshot, which is low here.
            using var state = new MemoryStream();
            using var writer = new BinaryWriter(state, System.Text.Encoding.UTF8, true);
            memory.SaveState(writer); writer.Flush(); state.Position = 0;
            using var reader = new BinaryReader(state, System.Text.Encoding.UTF8, true);
            memory.LoadState(reader);
            if (!memory.TryCopyLastVisibleRdpFramebufferSnapshot(low, 32, 16, (uint)bpp, out _, out _, out _))
                throw new Exception($"Missing low snapshot after load: {low:x8}");
            byte[] highImage = Render(0x300000, true); // Newer, unrelated producer must not override VI.
            // Rendering can have begun overwriting RAM; display the completed snapshot.
            memory.RDRAM.AsSpan((int)low, lowImage.Length).Fill(0xff);
            foreach (int row in new[] { 0, 1, 7 })
            foreach (bool useLow in new[] { false, true, false, true })
            {
                uint origin = (useLow ? low : 0x300000u) + (uint)(row * 32 * bpp);
                memory.WriteUInt32(0xa4400004, origin);
                if (!core.TryGetFramebuffer(out var pixels, out int w, out int h, out int bytes)
                    || w != 32 || h != 16 || bytes != bpp
                    || !pixels.AsSpan(0, w * h * bytes).SequenceEqual((useLow ? lowImage : highImage).AsSpan(row * 32 * bpp, w * h * bytes)))
                    throw new Exception($"VI selected wrong buffer at {origin:x8}: {core.LastFramebufferStatus}");
                cases++;
            }
        }
        // No rendered evidence: a low pointer alone must not enable the new path.
        {
            var core = new Ryu64Core.Ryu64Core();
            R4300.memory = new Memory(new byte[4096]);
            typeof(Ryu64Core.Ryu64Core).GetField("isRunning", flags)!.SetValue(core, true);
            R4300.memory.WriteUInt32(0xa4400000, 2);
            R4300.memory.WriteUInt32(0xa4400008, 320);
            R4300.memory.WriteUInt32(0xa4400004, 0x680);
            if (core.TryGetFramebuffer(out _, out _, out _, out _)) throw new Exception("Unproven low VI pointer accepted");
            cases++;
        }
        Console.WriteLine($"lowViCases={cases} bufferSwitch=passed rowOffset=passed saveLoad=passed snapshotIsolation=passed");
    }
}
