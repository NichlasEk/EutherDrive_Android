#if N64_LIVE_GPU && N64_RDP_JOURNAL
using System.Buffers.Binary;
using Ryu64.MIPS;
using Ryu64Core;

internal static class N64GpuCpuFramebufferChecks
{
    internal static void Run(string library)
    {
        int checks = 0;
        foreach (int bpp in new[] { 2, 4 })
        {
            var memory = new Memory(new byte[4096]); R4300.memory = memory;
            using var gpu = new N64LiveGpu(memory, library, true); memory.AttachGpu(gpu);
            const uint origin = 0x600000;
            const int width = 64, height = 32;
            memory.WriteUInt32(0x04400000, bpp == 2 ? 2u : 3u);
            memory.WriteUInt32(0x04400008, width);
            memory.WriteUInt32(0x04400028, height * 2);
            memory.WriteUInt32(0x04400030, 1024);
            memory.WriteUInt32(0x04400018, 524);
            memory.WriteUInt32(0x0440000c, 2);
            void Field() => memory.Tick(1_600_000);
            byte[] Read()
            {
                if (!memory.TryGetGpuFramebuffer(out var pixels, out int w, out int h, out int format)
                    || w != width || h != height || format != bpp)
                    throw new Exception("CPU-written VI buffer was not published with its programmed format");
                return pixels;
            }
            void Equal(byte[] expected, string name)
            {
                if (!Read().AsSpan().SequenceEqual(expected)) throw new Exception(name);
                checks++;
            }
            byte[] first = Enumerable.Range(0, width * height * bpp).Select(i => (byte)(i * 13 + 7)).ToArray();
            memory.FastMemoryWrite(0x80000000 | origin, first);
            memory.WriteUInt32(0x04400004, origin);
            Equal(first, "CPU-only video requires no RDP command");
            byte[] immutable = Read();
            var next = (byte[])first.Clone(); next[17] ^= 0x55;
            memory.WriteUInt8(0x80000000 | origin + 17, next[17]);
            Equal(first, "UI polling read partially changed live RAM");
            Field(); Equal(next, "VI field did not publish an update to the same origin");
            if (!immutable.AsSpan().SequenceEqual(first)) throw new Exception("Published CPU frame was mutated");
            checks++;
            long frames = memory.GpuFrames;
            Field(); memory.WriteUInt32(0x04400004, origin);
            if (memory.GpuFrames != frames) throw new Exception("Unchanged CPU image was published repeatedly");
            checks++;

            // A save taken after a CPU write but before scanout must preserve
            // both the held image and the next field, with no derived dirty state.
            memory.WriteUInt8(0x80000000 | origin + 18, 0x66); next[18] = 0x66;
            byte[] Save()
            {
                using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream);
                memory.SaveState(writer); return stream.ToArray();
            }
            byte[] state = Save();
            Field(); Equal(next, "Uninterrupted CPU field mismatch"); byte[] expected = Save();
            memory.LoadState(new BinaryReader(new MemoryStream(state, false)));
            if (!Save().AsSpan().SequenceEqual(state)) throw new Exception("CPU-frame checkpoint changed on restore");
            Field(); Equal(next, "Restored CPU field mismatch");
            if (!Save().AsSpan().SequenceEqual(expected)) throw new Exception("CPU-frame continuation state differs");
            checks++;

            // RSP DMA can produce video without an RDP command as well.
            memory.FastMemoryWrite(0x04000800, Enumerable.Repeat((byte)0x99, 8).ToArray());
            memory.WriteUInt32(0x04040000, 0x800); memory.WriteUInt32(0x04040004, origin + 32);
            memory.WriteUInt32(0x0404000c, 7);
            next.AsSpan(32, 8).Fill(0x99);
            Field(); Equal(next, "RSP DMA video update was lost");

            // An all-black indexer-written buffer is still a valid frame.
            byte[] black = new byte[first.Length];
            memory.FastMemoryWrite(0x80720000, black);
            memory.WriteUInt32(0x04400004, 0x720000); Equal(black, "Black CPU frame was mistaken for unwritten memory");
            // A block write to another buffer, then an origin change, must publish it.
            byte[] alternate = Enumerable.Repeat((byte)0x88, first.Length).ToArray();
            memory.FastMemoryWrite(0x80700000, alternate);
            memory.WriteUInt32(0x04400004, 0x700000); Equal(alternate, "CPU video buffer swap was lost");
            // VI may be programmed in origin/width/format order.
            memory.WriteUInt32(0x04400008, 0);
            memory.WriteUInt32(0x04400004, origin);
            memory.WriteUInt32(0x04400008, width);
            Field(); Equal(next, "Mode setup order lost the next VI field");
            foreach (uint address in new[] { 0x400000u, 0x7ffff0u, 0xffffffu })
            {
                memory.WriteUInt32(0x04400004, address); Field();
                Equal(next, "Unwritten or invalid VI buffer replaced the completed image");
            }
            foreach (uint type in new[] { 0u, 1u })
            {
                memory.WriteUInt32(0x04400000, type); memory.WriteUInt32(0x04400004, origin); Field();
                Equal(next, "Blank/reserved VI format published a buffer");
            }
        }

        // Readback can finish a queued draw before FULL_SYNC. Neither a VI
        // swap nor a field may expose that unfinished frame or a depth clear.
        {
            var memory = new Memory(new byte[4096]); R4300.memory = memory;
            using var gpu = new N64LiveGpu(memory, library, true); memory.AttachGpu(gpu);
            void Cmd(params uint[] words) => memory.JournalReplayCommand(words);
            memory.WriteUInt32(0x04400000, 2); memory.WriteUInt32(0x04400008, 64);
            memory.WriteUInt32(0x04400028, 64); memory.WriteUInt32(0x04400018, 524); memory.WriteUInt32(0x0440000c, 2);
            memory.WriteUInt32(0x04400004, 0x300000);
            Cmd(0xef300000, 0); Cmd(0xff10003f, 0x300000); Cmd(0xfe000000, 0x500000); Cmd(0xed000000, 0x00100080);
            Cmd(0xf7000000, 0xff01ff01); Cmd(0xf60fc07c, 0); Cmd(0xe9000000, 0);
            memory.TryGetGpuFramebuffer(out var finished, out _, out _, out _);
            if (BinaryPrimitives.ReadUInt16BigEndian(finished) != 0xff01) throw new Exception("GPU fixture failed");
            Cmd(0xf7000000, 0x003f003f); Cmd(0xf60fc07c, 0);
            memory.ReadUInt32(0x80300000); // Explicit read hazard clears GPU pending ranges.
            memory.WriteUInt32(0x04400004, 0x300000); memory.Tick(1_600_000);
            memory.TryGetGpuFramebuffer(out var held, out _, out _, out _);
            if (!held.AsSpan().SequenceEqual(finished)) throw new Exception("VI published a partially rendered GPU frame");
            checks++;
            Cmd(0xe9000000, 0);
            memory.WriteUInt32(0x80300000, 0x12345678); memory.Tick(1_600_000);
            memory.TryGetGpuFramebuffer(out var patched, out _, out _, out _);
            if (BinaryPrimitives.ReadUInt32BigEndian(patched) != 0x12345678)
                throw new Exception("CPU overlay was hidden by an older completed GPU snapshot");
            checks++;
        }
        Console.WriteLine($"gpuCpuFramebufferChecks={checks} cpuVideo=passed viFields=passed immutable=passed savestate=exact incompleteRdp=held");
    }
}
#endif
