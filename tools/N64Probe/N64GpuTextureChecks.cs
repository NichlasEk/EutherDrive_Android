using Ryu64Core;

// Compare the complete memory and TMEM after adversarial CPU/texture ordering
// with the unbatched GPU. Independent Angrylion replay covers renderer output.
internal static class N64GpuTextureChecks
{
    internal static void Run(string library)
    {
        byte[] initial = new byte[N64GpuBackend.RamSize], hidden = new byte[N64GpuBackend.HiddenSize];
        new Random(9122).NextBytes(initial); Array.Fill(hidden, (byte)3);
        var common = N64GpuFlags.Validate | N64GpuFlags.RequireDiscrete;
        using var strict = new N64GpuBackend(library, initial, hidden, common);
        using var candidate = new N64GpuBackend(library, initial, hidden, common | N64GpuFlags.DeferDisjointLoadBlocks);
        byte[][] a = { new byte[initial.Length], new byte[hidden.Length], new byte[4096] };
        byte[][] b = { new byte[initial.Length], new byte[hidden.Length], new byte[4096] };
        using var batch = new MemoryStream(); using var writer = new BinaryWriter(batch);
        int checks = 0;
        void Cmd(uint w0, uint w1) { writer.Write(2u); writer.Write(8u); writer.Write(w0); writer.Write(w1); }
        void Write(uint address, params byte[] bytes)
        { writer.Write(1u); writer.Write((uint)bytes.Length + 4); writer.Write(address); writer.Write(bytes); }
        void Image(uint address, uint width = 1) => Cmd(0xfd100000u | (width - 1), address);
        void Tile(uint index = 0, uint size = 2, uint format = 0, uint stride = 0, uint offset = 0) =>
            Cmd(0xf5000000u | format << 21 | size << 19 | stride << 9 | offset, index << 24);
        void Block(uint pixels, uint dt = 0, uint s = 0, uint t = 0, uint tile = 0) =>
            Cmd(0xf3000000u | s << 12 | t, tile << 24 | ((s + pixels - 1) & 4095) << 12 | dt);
        void Check(string name, ulong barriers)
        {
            Cmd(0xe9000000, 0);
            ulong before = candidate.GetStats().WriteBarriers;
            byte[] bytes = batch.ToArray(); batch.SetLength(0); batch.Position = 0;
            strict.Readback(strict.Submit(bytes), a[0], a[1], a[2]);
            candidate.Readback(candidate.Submit(bytes), b[0], b[1], b[2]);
            for (int i = 0; i < a.Length; i++)
                if (!a[i].AsSpan().SequenceEqual(b[i])) throw new Exception($"{name}: memory component {i} differs");
            var stats = candidate.GetStats();
            if (stats.WriteBarriers - before != barriers || stats.ValidationErrors != 0)
                throw new Exception($"{name}: expected {barriers} barriers, got {stats.WriteBarriers - before}");
            checks++;
        }

        // Padding, DXT permutation, all eight tile slots and wrapping TMEM
        // offsets. Nonzero S/T are pixels, and image width affects the source.
        foreach (uint pixels in new uint[] { 1, 3, 4, 5, 31, 512, 1023, 2048 })
        foreach (uint dt in new uint[] { 0, 1, 1024, 2048, 4095 })
        {
            uint tile = (pixels + dt) & 7, s = 9, t = 3;
            const uint source = 0x700002, width = 65;
            uint start = source + 2 * (s + width * t);
            Image(source, width); Tile(tile, offset: (pixels * 7) & 511);
            Write(0x100001, 17); Block(pixels, dt, s, t, tile);
            // This later store must not change the earlier texture load.
            Write(start + 1, 73); Check($"disjoint/{pixels}/{dt}", 1);
            // The last rounded-up texel is observable even past pixel_count.
            uint end = start + 2 * ((pixels + 3) & ~3u);
            Write(end - 1, 42); Block(pixels, dt, s, t, tile);
            Write(0x100003, 55); Check($"rounded-source/{pixels}/{dt}", 2);
        }
        Image(0x700000); Tile();
        Write(0x700000, 0x12, 0x35); Block(1);
        Write(0x700000, 0x12, 0x35); Block(1); // Same-value stores still own bytes.
        Write(0x700000, 0xab, 0xcd); Check("repeated-source", 3);

        // Texture image and tile state can expose an earlier deferred patch.
        Image(0x710000); Write(0x700003, 21); Block(16);
        Image(0x700000); Block(16); Write(0x100004, 22); Check("new-source", 2);
        Image(0x700000); Tile(1); Tile(2, stride: 1);
        Write(0x100000, 31); Block(16, tile: 1);
        Block(16, tile: 2); Write(0x100004, 32); Check("new-tile-layout", 2);

        // Unsupported layouts remain unconditional boundaries.
        foreach (uint format in new uint[] { 0, 1, 2, 3, 4 })
        {
            Tile(format: format); Image(0x700000);
            Write(0x100000, 41); Block(32, 1024); Write(0x100004, 42);
            Check($"format/{format}", format == 1 ? 2u : 1u);
        }
        Tile(size: 3); Write(0x100000, 43); Block(32); Write(0x100004, 44); Check("mismatched-size", 2);
        Tile(); Image(0x700001); Write(0x100000, 45); Block(32); Write(0x100004, 46); Check("odd-source", 2);
        Image(0x700000); Write(0x100000, 47); Cmd(0xf4000000, 0); Write(0x100004, 48); Check("load-tile", 2);
        Tile(offset: 256); Write(0x100000, 49); Cmd(0xf0000000, 0); Write(0x100004, 50); Check("load-tlut", 2);
        // Last installed aligned halfword is allowed. No out-of-RAM access.
        Tile(); Image(0x7ffff8); Write(0x100000, 51); Block(1); Write(0x100004, 52); Check("ram-end", 1);

        Console.WriteLine($"gpuTextureChecks={checks} fullMemoryAndTmem=exact sourceOrdering=passed barriers=checked");
    }
}
