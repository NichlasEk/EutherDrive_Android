#if N64_LIVE_GPU && N64_RDP_JOURNAL
using System.Buffers.Binary;
using System.Reflection;
using System.Security.Cryptography;
using Ryu64.MIPS;
using Ryu64Core;

internal static class N64GpuSavestateChecks
{
    internal static void Run(string library, string output)
    {
        Directory.CreateDirectory(output);
        var memory = new Memory(new byte[4096]); R4300.memory = memory;
        memory.AttachGpu(new N64LiveGpu(memory, library, true));
        typeof(Memory).GetField("_rspTaskDispatching", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(memory, true);
        int checks = 0;
        byte[] Save()
        {
            using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream);
            memory.SaveState(writer); return stream.ToArray();
        }
        void Cmd(params uint[] words) => memory.JournalReplayCommand(words);
        void RoundTrip(string name, Action continuation)
        {
            memory.TryGetGpuFramebuffer(out var before, out _, out _, out _);
            long frameCount = memory.GpuFrames;
            byte[] state = Save();
            memory.TryGetGpuFramebuffer(out var after, out _, out _, out _);
            if (memory.GpuFrames != frameCount || !before.AsSpan().SequenceEqual(after))
                throw new Exception(name + ": saving published an unfinished image");
            continuation();
            memory.TryGetGpuFramebuffer(out var expectedFrame, out _, out _, out _);
            byte[] expected = Save();
            memory.LoadState(new BinaryReader(new MemoryStream(state, false)));
            if (memory.GpuRenderer == null) throw new Exception(name + ": GPU detached on load");
            memory.TryGetGpuFramebuffer(out var loadedFrame, out _, out _, out _);
            if (!loadedFrame.AsSpan().SequenceEqual(before)) throw new Exception(name + ": completed image lost on load");
            if (!state.AsSpan().SequenceEqual(Save())) throw new Exception(name + ": checkpoint did not roundtrip exactly");
            continuation();
            memory.TryGetGpuFramebuffer(out var actualFrame, out _, out _, out _);
            byte[] actual = Save();
            if (!expected.AsSpan().SequenceEqual(actual))
            {
                File.WriteAllBytes(Path.Combine(output, name + "-expected.bin"), expected);
                File.WriteAllBytes(Path.Combine(output, name + "-actual.bin"), actual);
                int at = Enumerable.Range(0, Math.Min(expected.Length, actual.Length)).FirstOrDefault(i => expected[i] != actual[i]);
                throw new Exception($"{name}: resumed state differs at {at:x}, lengths {expected.Length}/{actual.Length}");
            }
            if (!expectedFrame.AsSpan().SequenceEqual(actualFrame)) throw new Exception(name + ": resumed frame differs");
            Console.WriteLine($"gpuSavestate={name} fullMemoryHiddenTmemState=exact sha256={Convert.ToHexString(SHA256.HashData(actual))}");
            checks++;
        }
        try
        {
            RoundTrip("before-first-command", () => Cmd(0xe9000000, 0));
            Cmd(0xef300000, 0); Cmd(0xff10003f, 0x300000); Cmd(0xfe000000, 0x500000); Cmd(0xed000000, 0x00100080);
            memory.WriteUInt32(0x04400000, 2); memory.WriteUInt32(0x04400004, 0x300000); memory.WriteUInt32(0x04400008, 64);
            memory.WriteUInt32(0x04400028, 64);
            Cmd(0xf7000000, 0xff01ff01); Cmd(0xf60fc07c, 0); Cmd(0xe9000000, 0);
            Cmd(0xf7000000, 0x003f003f); Cmd(0xf60fc07c, 0);
            memory.WriteUInt8(0x80300001, 0x55); // Store after queued draw, before synchronization.
            RoundTrip("pending-frame-partial-store", () => Cmd(0xe9000000, 0));
            memory.TryGetGpuFramebuffer(out var visible, out _, out _, out _);
            if (BinaryPrimitives.ReadUInt16BigEndian(visible) != 0x0055) throw new Exception("Queued CPU byte did not survive GPU save");

            // LoadTile changes bounds itself. Continue sampling TMEM without
            // reissuing any texture, tile, combiner, image, or scissor setup.
            byte[] texture = new byte[128];
            for (int i = 0; i < 64; i++) BinaryPrimitives.WriteUInt16BigEndian(texture.AsSpan(i * 2), (ushort)(0x8001 | i * 32));
            memory.FastMemoryWrite(0x80100000, texture);
            Cmd(0xfd100007, 0x100000); Cmd(0xf5100400, 0); Cmd(0xf4000000, 0x0001c01c);
            Cmd(0xef0000f0, 0); Cmd(0xfcffffff, 0xfffcf279);
            Cmd(0xe40fc07c, 0, 0, 0x04000400); Cmd(0xe9000000, 0);
            RoundTrip("resident-rgba-texture", () => { Cmd(0xe40bc05c, 0, 0, 0x02000400); Cmd(0xe9000000, 0); });

            // CI4 plus TLUT, with LoadBlock updating the tile bounds and TMEM.
            memory.FastMemoryWrite(0x80110000, Enumerable.Range(0, 32).Select(i => (byte)(i * 17)).ToArray());
            Cmd(0xfd10000f, 0x100000); Cmd(0xf5100100, 0x07000000); Cmd(0xf0000000, 0x0703c000);
            Cmd(0xfd500007, 0x110000); Cmd(0xf5400200, 0); Cmd(0xf3000000, 0x0003f800);
            Cmd(0xef0080f0, 0);
            RoundTrip("resident-ci-tlut-block", () => { Cmd(0xe40fc07c, 0, 0, 0x04000400); Cmd(0xe9000000, 0); });

            // RGB noise and alpha dithering continue from the same primitive
            // index. Repeat without reissuing the pipeline state after load.
            Cmd(0xef0000a0, 3); Cmd(0xfb000000, 0xff8844cc); Cmd(0xfa000080, 0x77aaeeff);
            Cmd(0xfc000000u | 7u << 20 | 5u << 15 | 7u << 12 | 7u << 9 | 7u << 5 | 5u,
                15u << 28 | 15u << 24 | 7u << 21 | 7u << 18 | 7u << 15 | 7u << 12 | 3u << 9 | 7u << 6 | 7u << 3 | 3u);
            Cmd(0xe40fc07c, 0, 0, 0x04000400); Cmd(0xe9000000, 0);
            RoundTrip("noise-dither-counter", () => { Cmd(0xe40cc06c, 0, 0, 0x04000400); Cmd(0xe9000000, 0); });

            // Save in the middle of a recycled XBUS FIFO command. The retained
            // words, pending targets, and following FULL_SYNC all survive.
            Cmd(0xef300000, 0); Cmd(0xf7000000, 0xff01ff01);
            uint[] triangle = { 0xc8800040, 0x00400000, 16u << 16, 0, 0, 0, 16u << 16, 0 };
            void Part(int i)
            {
                BinaryPrimitives.WriteUInt32BigEndian(memory.SP_MEM_RW.AsSpan(0xff8), triangle[i]);
                BinaryPrimitives.WriteUInt32BigEndian(memory.SP_MEM_RW.AsSpan(0xffc), triangle[i + 1]);
                memory.WriteUInt32(0x0410000c, 2); memory.WriteUInt32(0x04100000, 0xff8); memory.WriteUInt32(0x04100004, 0x1000);
                memory.SP_MEM_RW.AsSpan(0xff8, 8).Fill(0xcc);
            }
            Part(0); Part(2);
            RoundTrip("partial-rdp-command", () => { Part(4); Part(6); Cmd(0xe9000000, 0); });
            Console.WriteLine($"gpuSavestateChecks={checks} validationErrors=0");
        }
        finally { memory.DetachGpu(); }
    }
}
#endif
