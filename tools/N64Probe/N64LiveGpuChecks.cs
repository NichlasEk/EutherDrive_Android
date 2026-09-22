#if N64_LIVE_GPU && N64_RDP_JOURNAL
using System.Buffers.Binary;
using System.Reflection;
using Ryu64.MIPS;
using Ryu64Core;

internal static class N64LiveGpuChecks
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    internal static void Lifecycle(string library, string rom, string output)
    {
        Directory.CreateDirectory(output);
        string? previous = Environment.GetEnvironmentVariable("EUTHERDRIVE_N64_GPU_LIBRARY");
        using var core = new Ryu64Core.Ryu64Core();
        try
        {
            Environment.SetEnvironmentVariable("EUTHERDRIVE_N64_GPU_LIBRARY", null);
            core.LoadROM(rom);
            using var state = new MemoryStream();
            using (var writer = new BinaryWriter(state, System.Text.Encoding.UTF8, true)) core.SaveState(writer);
            Environment.SetEnvironmentVariable("EUTHERDRIVE_N64_GPU_LIBRARY", library);
            core.LoadROM(rom);
            string slot = Path.Combine(output, "existing-slot.bin");
            byte[] marker = { 12, 34, 56, 78 }; File.WriteAllBytes(slot, marker);
            try { core.SaveState(slot); throw new Exception("Saved an uninitialized CPU"); }
            catch (InvalidOperationException) { }
            if (!File.ReadAllBytes(slot).AsSpan().SequenceEqual(marker)) throw new Exception("Failed save modified existing file");
            core.Start();
            var timeout = System.Diagnostics.Stopwatch.StartNew();
            while (R4300.memory.RdpCommandCount == 0 && timeout.Elapsed.TotalSeconds < 45) Thread.Sleep(20);
            core.Stop();
            if (R4300.memory.RdpCommandCount == 0) throw new Exception("Boot produced no live GPU command");
            core.SaveState(slot);
            core.LoadState(slot);
            if (R4300.memory.GpuRenderer == null) throw new Exception("GPU save resumed in software");
            core.Start(); core.Stop();
            var old = R4300.memory;
            core.Start(); core.Stop();
            if (ReferenceEquals(old, R4300.memory) || old.GpuRenderer != null || R4300.memory.GpuRenderer == null)
                throw new Exception("Restart retained stale GPU state");
            // Initialize the replacement renderer before loading a software
            // save, so the test also exercises real native resource teardown.
            R4300.memory.JournalReplayCommand(new uint[] { 0xe9000000, 0 });
            state.Position = 0;
            core.LoadState(new BinaryReader(state));
            if (R4300.memory.GpuRenderer != null) throw new Exception("Old save resumed against reset GPU state");
            core.Dispose();
            Console.WriteLine("liveGpuLifecycle=passed gpuSaveLoad=passed failedSave=preserved restart=fresh oldSave=software stop=joined dispose=passed");
        }
        finally { Environment.SetEnvironmentVariable("EUTHERDRIVE_N64_GPU_LIBRARY", previous); }
    }
    internal static void Run(string library, string journal, string reference)
    {
        int checks = 0;
        using (var input = new RdpJournalReader(Path.Combine(journal, "journal.bin")))
        {
            if (!input.FromReset) throw new Exception("Reset fixture required");
            var memory = new Memory(new byte[4096]); R4300.memory = memory;
            input.Ram.CopyTo(memory.RDRAM, 0); input.Hidden.CopyTo(memory.GpuHiddenBits, 0);
            using var gpu = new N64LiveGpu(memory, library, true); memory.AttachGpu(gpu);
            typeof(Memory).GetField("_rspTaskDispatching", Private)!.SetValue(memory, true);
            int frame = 0;
            while (!input.Ended)
            {
                var (kind, data) = input.Next();
                if (kind == RdpJournal.Write)
                {
                    uint address = BinaryPrimitives.ReadUInt32LittleEndian(data);
                    memory.FastMemoryWrite(0x80000000u | address, data.AsSpan(4).ToArray());
                }
                else if (kind == RdpJournal.Command)
                {
                    uint[] words = new uint[data.Length / 4];
                    for (int i = 0; i < words.Length; i++) words[i] = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(i * 4));
                    memory.JournalReplayCommand(words);
                }
                else if (kind == RdpJournal.Checkpoint)
                {
                    byte[] expected = File.ReadAllBytes(Path.Combine(reference, $"frame-{++frame:D4}.bin"));
                    for (int i = 0; i < memory.RDRAM.Length; i++)
                        if (memory.RDRAM[i] != expected[i ^ 3]) throw new Exception($"Live RDRAM mismatch frame={frame} at={i:x}");
                    for (int i = 0; i < memory.GpuHiddenBits.Length; i++)
                        if (memory.GpuHiddenBits[i] != expected[memory.RDRAM.Length + (i ^ 1)]) throw new Exception($"Live hidden mismatch {frame}:{i:x}");
                    if ((memory.MI_INTR_REG_R[3] & 0x20) == 0) throw new Exception("Missing completed DP interrupt");
                    checks++; Console.WriteLine($"liveGpuOracleFrame={frame} exact=true");
                }
            }
        }
        {
            // Sparse writes must survive readback in every page/group, including
            // the last RAM byte and re-arming pages after a completed batch.
            var memory = new Memory(new byte[4096]); R4300.memory = memory;
            using var gpu = new N64LiveGpu(memory, library, true); memory.AttachGpu(gpu);
            typeof(Memory).GetField("_rspTaskDispatching", Private)!.SetValue(memory, true);
            memory.JournalReplayCommand(new uint[] { 0xe9000000, 0 });
            foreach (int pass in new[] { 0, 1 })
            {
                for (int page = 2047; page >= 0; page--)
                {
                    uint address = 0x80000000u + (uint)page * 4096;
                    memory.WriteUInt8(address, (byte)(page + pass + 1));
                    memory.WriteUInt8(address + 4095, (byte)(page * 3 + pass + 2));
                }
                byte[] expected = (byte[])memory.RDRAM.Clone();
                memory.JournalReplayCommand(new uint[] { 0xe9000000, 0 });
                if (!expected.AsSpan().SequenceEqual(memory.RDRAM)) throw new Exception("Sparse CPU page writes were lost during GPU readback");
                checks++;
            }
        }
        {
            var memory = new Memory(new byte[4096]); R4300.memory = memory;
            using var gpu = new N64LiveGpu(memory, library, true); memory.AttachGpu(gpu);
            typeof(Memory).GetField("_rspTaskDispatching", Private)!.SetValue(memory, true);
            void Cmd(params uint[] words) => memory.JournalReplayCommand(words);
            Cmd(0xef300000, 0); Cmd(0xff10003f, 0x300000); Cmd(0xfe000000, 0x500000); Cmd(0xed000000, 0x00100080);
            void Draw() { Cmd(0xf7000000, 0xff01ff01); Cmd(0xf60fc07c, 0); }
            void Read(Func<ulong> read, ulong expected)
            {
                Draw(); long before = memory.GpuReadHazards;
                if (read() != expected || memory.GpuReadHazards != before + 1) throw new Exception("Missing CPU/DMA read synchronization");
                checks++;
            }
            Read(() => memory[0x80300000], 0xff);
            Read(() => memory[0x81300000], 0xff); // Mirrored backing array.
            Read(() => memory.ReadUInt16(0x80300000), 0xff01);
            Read(() => memory.ReadUInt32(0x80300000), 0xff01ff01);
            Read(() => memory.ReadUInt32(0x00300000), 0xff01ff01); // Slow low-physical path.
            Read(() => memory.ReadUInt64(0x80300000), 0xff01ff01ff01ff01);
            Read(() => memory.FastMemoryRead(0x80300000, 8)[0], 0xff);
            Read(() => {
                memory.WriteUInt32(0x04040000, 0x800); memory.WriteUInt32(0x04040004, 0x300000); memory.WriteUInt32(0x04040008, 7);
                return memory.SP_MEM_RW[0x800];
            }, 0xff);
            Read(() => {
                typeof(Memory).GetMethod("DmaCopyPhysical", Private)!.Invoke(memory, new object[] { 0x04000800u, 0x300000u, 8 });
                return memory.SP_MEM_RW[0x800];
            }, 0xff);
            Read(() => {
                memory.WriteUInt32(0x04500000, 0x300000); memory.WriteUInt32(0x04500004, 8);
                return (ushort)memory.DequeueAudio(out _)[0];
            }, 0xff01);
            // A partial CPU store after a queued draw must preserve GPU bytes
            // around it when readback reconciles the two owners.
            Draw(); memory.WriteUInt8(0x80300001, 0x55);
            if (memory.ReadUInt32(0x80300000) != 0xff55ff01) throw new Exception("CPU/GPU partial write clobber");
            checks++;
            // A same-value store is still a write: the GPU has a newer value
            // than the stale CPU mirror at this point.
            memory.WriteUInt8(0x80300001, 0x55); Draw(); memory.WriteUInt8(0x80300001, 0x55);
            if (memory.ReadUInt32(0x80300000) != 0xff55ff01) throw new Exception("Same-value store disappeared");
            checks++;
            // Exercise real generated JIT loads, including branch delay slots,
            // against a newer GPU-owned value than the CPU's RAM mirror.
            foreach (var (opcode, expected) in new (uint, ulong)[] {
                (0x80820000, 0xffffffffffffffff), (0x90820000, 0xff),
                (0x84820000, 0xffffffffffffff01), (0x94820000, 0xff01),
                (0x8c820000, 0xffffffffff01ff01), (0x9c820000, 0xff01ff01),
                (0xdc820000, 0xff01ff01ff01ff01) })
            foreach (bool delaySlot in new[] { false, true })
            {
                var words = delaySlot ? new List<uint> { 0x03e00008, opcode }
                    : new List<uint> { opcode, 0x03e00008, 0 };
                for (int i = 0; i < words.Count; i++) memory.WriteUInt32(0x80010000u + (uint)i * 4, words[i]);
                var run = (Func<uint,uint,bool,uint>)typeof(R4300).GetMethod("BuildCpuJit", BindingFlags.Static | BindingFlags.NonPublic)!
                    .Invoke(null, new object[] { 0x80010000u, words })!;
                Registers.R4300.Reg[4] = 0x80300000; Registers.R4300.Reg[31] = 0x80020000;
                Registers.R4300.PC = 0x80010000;
                Read(() => {
                    if (run((uint)words.Count, 0, false) != words.Count || Registers.R4300.PC != 0x80020000)
                        throw new Exception("JIT load did not finish at the branch target");
                    return Registers.R4300.Reg[2];
                }, expected);
            }
            // A delay-slot store must retain surrounding GPU bytes and finish
            // before the branch target can be fetched, just like a straight store.
            var storeCode = new List<uint> { 0x03e00008, 0xac820000 };
            for (int i = 0; i < storeCode.Count; i++) memory.WriteUInt32(0x80010000u + (uint)i * 4, storeCode[i]);
            var store = (Func<uint,uint,bool,uint>)typeof(R4300).GetMethod("BuildCpuJit", BindingFlags.Static | BindingFlags.NonPublic)!
                .Invoke(null, new object[] { 0x80010000u, storeCode })!;
            Registers.R4300.Reg[4] = 0x80300000; Registers.R4300.Reg[31] = 0x80020000;
            foreach (uint value in new uint[] { 0x12345678, 0x12345678 })
            {
                Draw(); Registers.R4300.Reg[2] = value; Registers.R4300.PC = 0x80010000;
                if (store(2, 0, false) != 2 || Registers.R4300.PC != 0x80020000
                    || memory.ReadUInt64(0x80300000) != ((ulong)value << 32 | 0xff01ff01u))
                    throw new Exception("JIT delay store lost CPU/GPU bytes or branch ordering");
                checks++;
            }
            // If the RDP overwrites compiled guest instructions, the JIT must
            // synchronize before validating its code and reject the stale block.
            var code = new List<uint> { 0x24020000, 0x03e00008, 0 };
            for (int i = 0; i < code.Count; i++) memory.WriteUInt32(0x80010000u + (uint)i * 4, code[i]);
            var compiled = (Func<uint,uint,bool,uint>)typeof(R4300).GetMethod("BuildCpuJit", BindingFlags.Static | BindingFlags.NonPublic)!
                .Invoke(null, new object[] { 0x80010000u, code })!;
            Cmd(0xff10003f, 0x10000); Draw();
            long codeHazards = memory.GpuReadHazards;
            if (compiled(3, 0, false) != uint.MaxValue || memory.GpuReadHazards != codeHazards + 1)
                throw new Exception("JIT executed code overwritten by the GPU");
            Cmd(0xff10003f, 0x300000);
            checks++;
            Draw(); long hazards = memory.GpuReadHazards;
            memory.ReadUInt32(0x80100000);
            if (memory.GpuReadHazards != hazards) throw new Exception("Unrelated read forced synchronization");
            checks++;
            Cmd(0xfe000000, 0x400); Draw(); hazards = memory.GpuReadHazards;
            memory.ReadUInt32(0x80000180); memory.ReadUInt32(0x800003fc);
            if (memory.GpuReadHazards != hazards) throw new Exception("Same-page exception vector unnecessarily waited for depth");
            // Reading across the actual range boundary must still wait.
            memory.ReadUInt64(0x800003fc);
            if (memory.GpuReadHazards != hazards + 1) throw new Exception("Cross-boundary depth read missed synchronization");
            Cmd(0xfe000000, 0x500000);
            checks++;
            memory.WriteUInt32(0x04400000, 2); memory.WriteUInt32(0x04400004, 0x300000); memory.WriteUInt32(0x04400008, 64);
            memory.WriteUInt32(0x04400028, 64); // 32 visible rows.
            Cmd(0xe9000000, 0);
            if (!memory.TryGetGpuFramebuffer(out byte[] pixels, out int w, out int h, out int bpp)
                || w != 64 || h != 32 || bpp != 2 || BinaryPrimitives.ReadUInt16BigEndian(pixels) != 0xff01)
                throw new Exception("No VI-selected GPU frame");
            checks++;
            long frames = memory.GpuFrames;
            Cmd(0xf7000000, 0x003f003f); Cmd(0xf60fc07c, 0);
            Cmd(0xff10003f, 0x500000); Cmd(0xf7000000, 0xffffffff); Cmd(0xf60fc07c, 0);
            memory.TryGetGpuFramebuffer(out var intermediate, out _, out _, out _);
            if (memory.GpuFrames != frames || !pixels.AsSpan().SequenceEqual(intermediate)) throw new Exception("Presented intermediate draw/clear");
            Cmd(0xe9000000, 0);
            memory.TryGetGpuFramebuffer(out var completed, out _, out _, out _);
            if (BinaryPrimitives.ReadUInt16BigEndian(completed) != 0x003f) throw new Exception("Presented depth target instead of VI buffer");
            if (BinaryPrimitives.ReadUInt16BigEndian(pixels) != 0xff01) throw new Exception("Mutated a published frame");
            checks++;
            memory.WriteUInt32(0x04400004, 0x300080);
            memory.TryGetGpuFramebuffer(out var offsetFrame, out _, out _, out _);
            if (!offsetFrame.AsSpan(0, 64 * 31 * 2).SequenceEqual(completed.AsSpan(128))) throw new Exception("VI row offset ignored");
            memory.WriteUInt32(0x04400004, 0x600000);
            memory.TryGetGpuFramebuffer(out var held, out _, out _, out _);
            if (!offsetFrame.AsSpan().SequenceEqual(held)) throw new Exception("Unfinished VI buffer did not hold completed image");
            checks++;
            using var state = new MemoryStream();
            using (var writer = new BinaryWriter(state, System.Text.Encoding.UTF8, true)) memory.SaveState(writer);
            state.Position = 0; memory.LoadState(new BinaryReader(state));
            if (memory.GpuRenderer == null) throw new Exception("GPU state resumed in software");
            memory.TryGetGpuFramebuffer(out var restored, out _, out _, out _);
            if (!restored.AsSpan().SequenceEqual(held)) throw new Exception("Savestate lost held GPU image");
            checks++;
        }
        // Commands split across recycled DMEM must reach the live GPU only
        // after the real FIFO assembler has retained and joined every word.
        {
            var memory = new Memory(new byte[4096]); R4300.memory = memory;
            using var gpu = new N64LiveGpu(memory, library, true); memory.AttachGpu(gpu);
            memory.JournalReplayCommand(new uint[] { 0xef300000, 0 });
            memory.JournalReplayCommand(new uint[] { 0xff10003f, 0x300000 });
            memory.JournalReplayCommand(new uint[] { 0xfe000000, 0x500000 });
            memory.JournalReplayCommand(new uint[] { 0xed000000, 0x00100080 });
            memory.JournalReplayCommand(new uint[] { 0xf7000000, 0xff01ff01 });
            uint[] words = { 0xc8800040, 0x00400000, 16u << 16, 0, 0, 0, 16u << 16, 0 };
            long before = memory.RdpCommandCount;
            for (int part = 0; part < words.Length; part += 2)
            {
                BinaryPrimitives.WriteUInt32BigEndian(memory.SP_MEM_RW.AsSpan(0xff8), words[part]);
                BinaryPrimitives.WriteUInt32BigEndian(memory.SP_MEM_RW.AsSpan(0xffc), words[part + 1]);
                memory.WriteUInt32(0x0410000c, 2); memory.WriteUInt32(0x04100000, 0xff8); memory.WriteUInt32(0x04100004, 0x1000);
                memory.SP_MEM_RW.AsSpan(0xff8, 8).Fill(0xcc);
                if (memory.RdpCommandCount != before + (part == 6 ? 1 : 0)) throw new Exception("Partial live command submitted");
            }
            memory.JournalReplayCommand(new uint[] { 0xe9000000, 0 });
            if ((memory.ReadUInt32(0x04300008) & 0x20) == 0 || memory.GpuFrames != 1) throw new Exception("Split FIFO lost FULL_SYNC");
            checks++;
        }
        Console.WriteLine($"liveGpuChecks={checks} oracle=exact readHazards=passed jit=passed dma=passed partialStores=passed");
    }
}
#endif
