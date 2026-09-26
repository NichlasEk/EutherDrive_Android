using Ryu64Core;

internal static class N64GpuReadbackChecks
{
    internal static void Run(string library)
    {
        byte[] initial = new byte[N64GpuBackend.RamSize], initialHidden = new byte[N64GpuBackend.HiddenSize];
        new Random(64323).NextBytes(initial); Array.Fill(initialHidden, (byte)3);
        var flags = N64GpuFlags.Validate | N64GpuFlags.RequireDiscrete;
        using var strict = new N64GpuBackend(library, initial, initialHidden, flags);
        using var live = new N64GpuBackend(library, initial, initialHidden, flags | N64GpuFlags.DeferDisjointLoadBlocks);
        byte[][] expected = { new byte[initial.Length], new byte[initialHidden.Length], new byte[4096] };
        byte[][] actual = { (byte[])initial.Clone(), new byte[initialHidden.Length], new byte[4096] };
        byte[][] snapshot = { new byte[initial.Length], new byte[initialHidden.Length], new byte[4096] };
        using var batch = new MemoryStream(); using var writer = new BinaryWriter(batch);
        int checks = 0;
        bool oneCycle = false;
        void Cmd(uint w0, uint w1) { writer.Write(2u); writer.Write(8u); writer.Write(w0); writer.Write(w1); }
        void Write(uint address, params byte[] bytes)
        {
            writer.Write(1u); writer.Write((uint)bytes.Length + 4); writer.Write(address); writer.Write(bytes);
            bytes.CopyTo(actual[0], (int)address);
        }
        void Targets(uint color, uint depth, uint width = 8, uint size = 2)
        {
            Cmd(0xff000000u | size << 19 | (width - 1), color); Cmd(0xfe000000, depth);
            Cmd(0xed000000, Math.Min(width * 4, 4095u) << 12 | 128);
            // I4 fill mode crashes real RDP hardware. Exercise its byte writes
            // with a valid one-cycle primitive-color rectangle instead.
            oneCycle = size == 0;
            Cmd(oneCycle ? 0xef000000u : 0xef300000u, 0); Cmd(0xfcffffff, 0xfffdf6fb);
        }
        void Draw(uint color, uint width = 8)
        {
            Cmd(0xf7000000, color); Cmd(0xfa000000, color);
            uint right = Math.Min((oneCycle ? width : width - 1) * 4, 4095u);
            Cmd(0xf6000000u | right << 12 | (oneCycle ? 128u : 124u), 0);
        }
        void Check(string name, long ramBytes = -1, bool fullSnapshot = false)
        {
            Cmd(0xe9000000, 0);
            byte[] bytes = batch.ToArray(); batch.SetLength(0); batch.Position = 0;
            strict.Readback(strict.Submit(bytes), expected[0], expected[1], expected[2]);
            ulong timeline = live.Submit(bytes);
            if (fullSnapshot)
            {
                live.Readback(timeline, snapshot[0], snapshot[1], snapshot[2]);
                for (int i = 0; i < expected.Length; i++)
                    if (!expected[i].AsSpan().SequenceEqual(snapshot[i])) throw new Exception(name + ": full snapshot differs");
            }
            ulong before = live.GetStats().ReadbackBytes;
            live.ReadbackLive(timeline, actual[0], actual[1], actual[2]);
            var stats = live.GetStats();
            ulong copiedMemory = stats.ReadbackBytes - before - 4096;
            for (int i = 0; i < expected.Length; i++)
                if (!expected[i].AsSpan().SequenceEqual(actual[i]))
                    throw new Exception($"{name}: live memory component {i} differs");
            ulong fullMemory = N64GpuBackend.RamSize + N64GpuBackend.HiddenSize;
            if ((ramBytes >= 0 && copiedMemory != (ulong)ramBytes * 3 / 2)
                || (ramBytes < 0 && (copiedMemory == 0 || copiedMemory >= fullMemory)))
                throw new Exception($"{name}: unexpected RAM/hidden copy size {copiedMemory}");
            if (stats.ValidationErrors != 0) throw new Exception(name + ": GPU validation error");
            Console.WriteLine($"gpuReadback={name} copiedMemory={copiedMemory} allMemory=exact"); checks++;
        }
        Check("first-full", N64GpuBackend.RamSize);
        Write(0x123401, 1, 2, 3, 4, 5); Write(0x7ffffc, 6, 7, 8, 9);
        Write(0x600000, actual[0][0x600000]); Check("cpu-only", 0);
        Check("no-new-work", 0);
        Cmd(0, 0); Check("unclassified-command-full", N64GpuBackend.RamSize);
        Cmd(0xff100007, 0x300000); Cmd(0xed000000, 32u << 12 | 128); Cmd(0xef300000, 0);
        Draw(0xf801f801); Check("unknown-depth-full", N64GpuBackend.RamSize);

        foreach (uint size in new uint[] { 0, 1, 2, 3 })
        foreach (uint width in new uint[] { 1, 8, 65, 320, 1024 })
        {
            Targets(0x300000, 0x500000, width, size); Draw(0xf801f801, width);
            Write(0x300003, actual[0][0x300003]); Write(0x123fff, 10, 11, 12);
            Check($"partial-cpu/{size}/{width}");
            // No target command between readbacks: the next draw must mark the
            // same attachment again after the previous dirty pages were consumed.
            Draw(0x07c107c1, width); Check($"same-target/{size}/{width}");
            Draw(0x003f003f, width); Check($"snapshot-interleave/{size}/{width}", fullSnapshot: true);
        }
        Targets(0x300000, 0x500000); Draw(0xf801f801);
        Targets(0x600000, 0x700000); Draw(0x003f003f);
        Write(0x300001, 13); Write(0x600003, 14); Check("multiple-targets");
        Targets(0x400000, 0x400080); Draw(0x07c107c1); Check("aliased-targets");
        Targets(0x300001, 0x500003, 65); Draw(0xf801f801); Check("unaligned-targets");
        foreach (uint size in new uint[] { 1, 2, 3 })
        {
            Targets(0x300000, 0x500000, 65, size);
            Cmd(0xed000000, 260u << 12 | 4095);
            Cmd(0xf7000000, 0xf801f801); Cmd(0xf6100fff, 4092);
            Check($"last-scissor-row/{size}");
        }
        byte[] depthBefore = expected[0].AsSpan(0x500000, 128).ToArray();
        Targets(0x300000, 0x500000, 65);
        Cmd(0xef000000, 0x24); Cmd(0xee000000, 0x40000001);
        oneCycle = true; Draw(0xff8844ff, 65); Check("primitive-depth-write");
        if (expected[0].AsSpan(0x500000, 128).SequenceEqual(depthBefore))
            throw new Exception("Depth fixture did not write depth memory");
        Targets(0, 0x400000, 1024, 3); Draw(0xf801f801, 1024);
        Targets(0x400000, 0, 1024, 3); Draw(0x003f003f, 1024);
        Check("all-pages", N64GpuBackend.RamSize);
        foreach (bool depthWrap in new[] { false, true })
        {
            Targets(depthWrap ? 0x300000u : 0x7fff80u, depthWrap ? 0x7fff80u : 0x500000u, 65);
            Draw(0xf801f801, 65); Write(0x40, actual[0][0x40]); Write(0x7fffff, 15);
            Check($"wrapped-target/{depthWrap}");
        }
        Check("after-targets-no-work", 0);
        // Restoring creates a fresh context. Its first live readback must copy
        // all RAM even if the first submitted command is only FULL_SYNC.
        byte[] saved = live.SaveState();
        using (var restored = new N64GpuBackend(library, actual[0], actual[1], flags))
        {
            restored.LoadState(saved);
            Cmd(0xe9000000, 0); byte[] bytes = batch.ToArray();
            restored.ReadbackLive(restored.Submit(bytes), snapshot[0], snapshot[1], snapshot[2]);
            for (int i = 0; i < actual.Length; i++)
                if (!actual[i].AsSpan().SequenceEqual(snapshot[i])) throw new Exception("Restored first readback differs");
            if (restored.GetStats().ReadbackBytes != N64GpuBackend.RamSize + N64GpuBackend.HiddenSize + 4096)
                throw new Exception("Restored context skipped initial full readback");
            checks++;
        }
        Console.WriteLine($"gpuReadbackChecks={checks} allMemoryHiddenTmem=exact validationErrors=0");
    }
}
