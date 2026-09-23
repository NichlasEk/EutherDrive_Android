using Ryu64Core;

// Compare the complete memory and TMEM after adversarial CPU/texture ordering
// with the unbatched GPU. Independent Angrylion replay covers renderer output.
internal static class N64GpuTextureChecks
{
    internal static void Run(string library, bool liveReadback = false)
    {
        byte[] initial = new byte[N64GpuBackend.RamSize], hidden = new byte[N64GpuBackend.HiddenSize];
        new Random(9122).NextBytes(initial); Array.Fill(hidden, (byte)3);
        var common = N64GpuFlags.Validate | N64GpuFlags.RequireDiscrete;
        using var strict = new N64GpuBackend(library, initial, hidden, common);
        using var candidate = new N64GpuBackend(library, initial, hidden, common | N64GpuFlags.DeferDisjointLoadBlocks);
        byte[][] a = { new byte[initial.Length], new byte[hidden.Length], new byte[4096] };
        byte[][] b = { new byte[initial.Length], new byte[hidden.Length], new byte[4096] };
        if (liveReadback) initial.CopyTo(b[0], 0);
        using var batch = new MemoryStream(); using var writer = new BinaryWriter(batch);
        int checks = 0;
        void Cmd(uint w0, uint w1) { writer.Write(2u); writer.Write(8u); writer.Write(w0); writer.Write(w1); }
        void Write(uint address, params byte[] bytes)
        {
            writer.Write(1u); writer.Write((uint)bytes.Length + 4); writer.Write(address); writer.Write(bytes);
            if (liveReadback) bytes.CopyTo(b[0], (int)address);
        }
        void Image(uint address, uint width = 1) => Cmd(0xfd100000u | (width - 1), address);
        void Tile(uint index = 0, uint size = 2, uint format = 0, uint stride = 0, uint offset = 0) =>
            Cmd(0xf5000000u | format << 21 | size << 19 | stride << 9 | offset, index << 24);
        void Block(uint pixels, uint dt = 0, uint s = 0, uint t = 0, uint tile = 0) =>
            Cmd(0xf3000000u | s << 12 | t, tile << 24 | ((s + pixels - 1) & 4095) << 12 | dt);
        void Tlut(uint pixels, uint s = 0, uint t = 0, uint tile = 0, uint fraction = 0) =>
            Cmd(0xf0000000u | s << 14 | fraction << 12 | t << 2 | fraction,
                tile << 24 | ((s + pixels - 1) & 1023) << 14 | fraction << 12 | t << 2 | fraction);
        void LoadTile(uint pixels, uint s = 0, uint t = 0, uint tile = 0, uint fraction = 0, uint rows = 1) =>
            Cmd(0xf4000000u | s << 14 | fraction << 12 | t << 2 | fraction,
                tile << 24 | ((s + pixels - 1) & 1023) << 14 | fraction << 12 | (t + rows - 1) << 2 | fraction);
        void Check(string name, ulong barriers)
        {
            Cmd(0xe9000000, 0);
            ulong before = candidate.GetStats().WriteBarriers;
            byte[] bytes = batch.ToArray(); batch.SetLength(0); batch.Position = 0;
            strict.Readback(strict.Submit(bytes), a[0], a[1], a[2]);
            ulong timeline = candidate.Submit(bytes);
            if (liveReadback) candidate.ReadbackLive(timeline, b[0], b[1], b[2]);
            else candidate.Readback(timeline, b[0], b[1], b[2]);
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
        Image(0x700000); Write(0x100000, 47); Cmd(0xf4000000, 0); Write(0x100004, 48); Check("load-tile", 1);
        Tile(offset: 256); Write(0x100000, 49); Cmd(0xf0000000, 0); Write(0x100004, 50); Check("load-tlut", 1);
        // Last installed aligned halfword is allowed. No out-of-RAM access.
        Tile(); Image(0x7ffff8); Write(0x100000, 51); Block(1); Write(0x100004, 52); Check("ram-end", 1);

        // RGBA16 palettes read one source halfword per entry, with quarter-pixel
        // S/T and no LoadBlock rounding. TMEM destination wrap cannot expand
        // that source range. Compare all RAM, hidden bytes and TMEM with strict
        // ordering, including writes immediately after a deferred palette load.
        foreach (uint pixels in new uint[] { 1, 2, 3, 4, 15, 16, 17, 255, 256 })
        foreach (uint tile in new uint[] { 0, 7 })
        foreach (uint offset in new uint[] { 256, 511 })
        foreach (uint tileSize in new uint[] { 0, 1, 2 })
        {
            const uint source = 0x700002, width = 77, s = 9, t = 3;
            uint begin = source + 2 * (s + width * t), end = begin + 2 * pixels;
            Image(source, width); Tile(tile, size: tileSize, offset: offset);
            Write(0x100001, 61); Tlut(pixels, s, t, tile, fraction: 3);
            Write(begin + 1, 62); Check($"tlut-disjoint/{pixels}/{tile}/{offset}/{tileSize}", 1);
            Write(end - 1, 63); Tlut(pixels, s, t, tile);
            Write(0x100003, 64); Check($"tlut-source-end/{pixels}/{tile}/{offset}/{tileSize}", 2);
        }
        Image(0x700000); Tile(offset: 256);
        Write(0x700001, 71); Tlut(16); Write(0x700001, 71); Tlut(16);
        Write(0x700001, 72); Check("tlut-same-value-source", 3);
        Image(0x710000); Write(0x700001, 73); Tlut(16);
        Image(0x700000); Tlut(16); Write(0x100000, 74); Check("tlut-new-source", 2);
        // Conservative padding includes the other half of the source word.
        Image(0x700002); Write(0x700001, 75); Tlut(1);
        Write(0x100000, 76); Check("tlut-source-word-padding", 2);
        Image(0x7ffffe); Write(0x100000, 77); Tlut(1);
        Write(0x100004, 78); Check("tlut-last-halfword", 1);
        // These valid but unproven layouts retain the original wait.
        Image(0x700001); Write(0x100000, 81); Tlut(16);
        Write(0x100004, 82); Check("tlut-odd-source", 2);
        Image(0x700000);
        foreach (var (size, format, stride) in new (uint, uint, uint)[] { (2, 3, 0), (2, 0, 1) })
        {
            Tile(size: size, format: format, stride: stride, offset: 256);
            Write(0x100000, 83); Tlut(16); Write(0x100004, 84);
            Check($"tlut-fallback/{size}/{format}/{stride}", 2);
        }
        Tile(offset: 256); Write(0x100000, 85); Tlut(257);
        Write(0x100004, 86); Check("tlut-wide-fallback", 2);

        // Single-row LoadTile reads a rounded eight-byte span. Exercise both
        // sizes, all tile slots, destination wrap, stride, fractional S/T and
        // later source stores against the independently ordered strict GPU.
        uint variant = 0;
        foreach (uint size in new uint[] { 1, 2 })
        foreach (uint pixels in new uint[] { 1, 3, 4, 5, 7, 8, 9, 31, 128, 1000 })
        foreach (uint stride in new uint[] { 0, 1, 16, 511 })
        foreach (uint offset in new uint[] { 0, 511 })
        {
            const uint source = 0x700002, width = 77, s = 9, t = 3;
            uint tile = variant & 7, format = new uint[] { 0, 2, 3, 4 }[(variant++ >> 1) & 3];
            uint start = source + (s + width * t) * (1u << (int)(size - 1));
            uint end = start + ((pixels * (1u << (int)(size - 1)) + 7) & ~7u);
            Cmd(0xfd000000u | size << 19 | (width - 1), source);
            Tile(tile, size: size, format: format, stride: stride, offset: offset);
            Write(0x100001, 91); LoadTile(pixels, s, t, tile, fraction: 3);
            Write(start + 1, 92); Check($"tile-disjoint/{size}/{pixels}/{stride}/{offset}", 1);
            // Rounded texels past the requested width still belong to the read.
            Write(end - 1, 93); LoadTile(pixels, s, t, tile);
            Write(0x100003, 94); Check($"tile-rounded-source/{size}/{pixels}/{stride}/{offset}", 2);
        }
        Image(0x700000); Tile();
        Write(0x700001, 95); LoadTile(16); Write(0x700001, 95); LoadTile(16);
        Write(0x700001, 96); Check("tile-same-value-source", 3);
        Image(0x710000); Write(0x700001, 97); LoadTile(16);
        Image(0x700000); LoadTile(16); Write(0x100000, 98); Check("tile-new-source", 2);
        Image(0x700002); Write(0x700001, 99); LoadTile(1);
        Write(0x100000, 100); Check("tile-source-word-padding", 2);
        Image(0x7ffff8); Write(0x100000, 101); LoadTile(1);
        Write(0x100004, 102); Check("tile-ram-end", 1);
        // Multiline source reads must include every row and its rounded word
        // padding. Odd, wrapped-coordinate and mismatched layouts still wait.
        Image(0x700001); Write(0x100000, 103); LoadTile(16);
        Write(0x100004, 104); Check("tile-odd-source", 2);
        Image(0x700000, 32); Tile(stride: 8);
        Write(0x100000, 105); LoadTile(16, rows: 2);
        Write(0x100004, 106); Check("tile-multiline", 1);
        foreach (uint size in new uint[] { 1, 2 })
        foreach (uint stride in new uint[] { 0, 3, 8, 64 })
        {
            uint row = 0x700000 + (32u << (int)(size - 1));
            uint lastRow = 0x700000 + (64u << (int)(size - 1));
            uint roundedRowBytes = ((13u << (int)(size - 1)) + 7u) & ~7u;
            Cmd(0xfd000000u | size << 19 | 31u, 0x700000);
            Tile(size: size, stride: stride);
            Write(0x100000, 127); Write(row + 1, 128); LoadTile(13, rows: 3);
            Write(0x100004, 129); Check($"tile-multiline-overlap/{size}/{stride}", 2);
            Write(0x100000, 130); Write(lastRow + roundedRowBytes - 1, 131); LoadTile(13, rows: 3);
            Write(0x100004, 132);
            Check($"tile-multiline-last-row-padding/{size}/{stride}", 2);
            Write(0x100000, 133); LoadTile(13, rows: 3);
            Write(lastRow + roundedRowBytes, 134);
            Check($"tile-multiline-after-padding/{size}/{stride}", 1);
        }
        Image(0x7ffff8, 32); Tile(size: 2, stride: 3);
        Write(0x100000, 135); LoadTile(13, rows: 3);
        Write(0x100004, 136); Check("tile-multiline-ram-end", 2);
        Image(0x700000, 32);
        Write(0x100000, 137); LoadTile(13, t: 1023, rows: 2);
        Write(0x100004, 138); Check("tile-multiline-wrapped-t", 2);
        Tile(size: 2, stride: 3);
        Write(0x100000, 139); LoadTile(13, s: 5, t: 3, rows: 3);
        Write(0x100004, 140); Check("tile-multiline-offset-disjoint", 1);
        Write(0x100000, 141); Write(0x700000 + 2u * (5u + 32u * 4u) + 1u, 142);
        LoadTile(13, s: 5, t: 3, rows: 3); Write(0x100004, 143);
        Check("tile-multiline-offset-overlap", 2);
        Tile(); Write(0x100000, 107); LoadTile(16, s: 1020);
        Write(0x100004, 108); Check("tile-wrapped-s", 2);
        foreach (var (sourceSize, tileSize, format) in new (uint, uint, uint)[] { (1, 2, 0), (2, 1, 0), (2, 2, 1), (3, 3, 0) })
        {
            Cmd(0xfd000000u | sourceSize << 19, 0x700000);
            Tile(size: tileSize, format: format);
            Write(0x100000, 109); LoadTile(16); Write(0x100004, 110);
            Check($"tile-layout-fallback/{sourceSize}/{tileSize}/{format}", 2);
        }

        // Independent patches on both sides of a texture must not become a
        // false source hazard. A later store to that source must still wait
        // for the load, and a real overlap in any patch must flush first.
        foreach (uint op in new uint[] { 0x30, 0x33, 0x34 })
        {
            void Load() { if (op == 0x30) Tlut(16); else if (op == 0x33) Block(16); else LoadTile(16); }
            Image(0x700000); Tile(offset: 256);
            Write(0x6ffffc, 111); Write(0x700024, 112); Load();
            Write(0x700001, 113); Check($"sparse-texture/{op:x}", 1);
            Write(0x6ffffc, 114); Write(0x70001f, 115); Write(0x700024, 116); Load();
            Write(0x100000, 117); Check($"sparse-real-overlap/{op:x}", 2);
            Write(0x6ffffc, 118); Write(0x700024, 119); Load();
            Image(0x700024); Load(); Write(0x100000, 120); Check($"sparse-new-source/{op:x}", 2);
            // Source-word padding and the bounded-scan fallback are preserved.
            Image(0x700002); Write(0x700001, 121); Write(0x700030, 122); Load();
            Write(0x100000, 123); Check($"sparse-padding/{op:x}", 2);
            Image(0x700000);
            foreach (int count in new int[] { 128, 129, 4096, 4097 })
            {
                for (int i = 0; i < count; i++) Write((i & 1) == 0 ? 0x6ffffcu : 0x700024u, (byte)i);
                Load(); Write(0x700001, 124); Check($"sparse-limit/{op:x}/{count}", count <= 4096 ? 1u : 2u);
            }
            for (int i = 0; i < 128; i++) Write((i & 1) == 0 ? 0x6ffffcu : 0x700024u, (byte)i);
            Write(0x700001, 125); Load(); Write(0x100000, 126);
            Check($"sparse-overlap-after-128/{op:x}", 2);
        }

        Console.WriteLine($"gpuTextureChecks={checks} fullMemoryAndTmem=exact sourceOrdering=passed barriers=checked liveReadback={liveReadback}");
    }
}
