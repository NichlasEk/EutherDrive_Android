#if N64_RDP_JOURNAL
using System.Buffers.Binary;
using System.Reflection;
using Ryu64.MIPS;

internal static class RdpJournalChecks
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    internal static void Run()
    {
        string root = Environment.GetEnvironmentVariable("N64_PROBE_JOURNAL_TEST_OUTPUT")
            ?? Path.Combine(Path.GetTempPath(), "n64-journal-check-" + Guid.NewGuid().ToString("N"));
        if (Directory.Exists(root)) throw new IOException("Use a new test directory");
        Directory.CreateDirectory(root);
        int checks = 0;
        void Check(bool condition, string message) { if (!condition) throw new Exception(message); checks++; }
        void Reject(Action action, string message)
        {
            try { action(); }
            catch (InvalidDataException) { checks++; return; }
            catch (EndOfStreamException) { checks++; return; }
            throw new Exception(message);
        }
        var memory = new Memory(new byte[4096]); R4300.memory = memory;
        string capture = Path.Combine(root, "synthetic");
        using (var journal = new RdpJournal(memory, capture, 2, true))
        {
            void Cmd(params uint[] words) => memory.JournalReplayCommand(words);
            Cmd(0xef300000, 0); // Fill mode.
            memory.WriteUInt8(0x80700123, 0); // Unchanged store, no tracked framebuffer yet.
            memory.WriteUInt16(0x80700ffe, 0x1234);
            memory.WriteUInt32(0x80701000, 0x55667788);
            memory.WriteUInt64(0x80700020, 0x0123456789abcdef);
            memory[0x81001234] = 0x56; // Mirrored backing RAM, outside physical 8 MiB.
            memory.FastMemoryWrite(0x807ffffe, new byte[] { 9, 8, 7, 6 }); // Wrap through the byte indexer.
            typeof(Memory).GetMethod("WriteValidatedRdramUInt32", Private)!.Invoke(memory, new object[] { 0x700008u, 0u });
            // SP DMA to RDRAM, with arbitrary byte values and unchanged bytes.
            for (int i = 0; i < 16; i++) memory.SP_MEM_RW[0x800 + i] = (byte)i;
            memory.WriteUInt32(0x04040000, 0x800);
            memory.WriteUInt32(0x04040004, 0x700100);
            memory.WriteUInt32(0x0404000c, 15);
            // PI cartridge -> RDRAM copy, independent of CPU store paths.
            memory.WriteUInt32(0x04600000, 0x700200);
            memory.WriteUInt32(0x04600004, 0x10000000);
            memory.WriteUInt32(0x0460000c, 15);
            Cmd(0xff10003f, 0x300000); // RGBA16, width 64.
            Cmd(0xed000000, 0x00100080);
            Cmd(0xf7000000, 0xff01ff01);
            Cmd(0xf60fc07c, 0); // Draw red, must never appear as external write patches.
            Cmd(0xe9000000, 0);
            memory.WriteUInt8(0x80300001, 1); // Same value in a just-rendered pixel.
            memory.WriteUInt8(0x80300004, 0x12); // Must not clobber adjacent pixel bytes.
            memory.WriteUInt16(0x80700000, 0xabcd);
            Cmd(0xfd100000, 0x700000); // Load texture with first contents.
            Cmd(0xf5100200, 0);
            Cmd(0xf4000000, 0);
            memory.WriteUInt16(0x80700000, 0x2469);
            Cmd(0xf4000000, 0); // Same load sees changed contents.
            Cmd(0xe9000000, 0);
            journal.RequireComplete();
            Check(memory.JournalTmem[2] == 0x24 && memory.JournalTmem[3] == 0x69,
                $"Texture fixture did not exercise interleaved writes: ram={Convert.ToHexString(memory.RDRAM.AsSpan(0x700000, 8))} tmem={Convert.ToHexString(memory.JournalTmem.AsSpan(0, 16))}");
        }
        RdpJournalReplay.Run(capture); checks++;
        Reject(() => RdpJournalReplay.Run(capture, corruptWrite: true), "Corrupted patch went undetected");
        using (var reader = new RdpJournalReader(Path.Combine(capture, "journal.bin")))
        {
            var covered = new HashSet<int>();
            while (!reader.Ended)
            {
                var (kind, data) = reader.Next();
                if (kind != RdpJournal.Write) continue;
                int offset = BinaryPrimitives.ReadInt32LittleEndian(data);
                for (int i = 0; i < data.Length - 4; i++) covered.Add(offset + i);
            }
            foreach (int address in new[] { 0, 1, 0x1234, 0x7ffffe, 0x7fffff, 0x700123, 0x700008, 0x70000b, 0x700020, 0x700027, 0x700100, 0x70010f, 0x700200, 0x70020f, 0x700ffe, 0x701003, 0x300001, 0x300004 })
                Check(covered.Contains(address), $"Lost write at {address:x}");
            foreach (int address in new[] { 0x300000, 0x300002, 0x300003, 0x300005, 0x700122, 0x700124, 0x701004 })
                Check(!covered.Contains(address), $"Patch overwrote untouched byte {address:x}");
        }

        // Start with an incomplete command, change the source and recycle the
        // original buffer. Only its assembled words belong in the journal.
        memory = new Memory(new byte[4096]); R4300.memory = memory;
        string split = Path.Combine(root, "split");
        using (var journal = new RdpJournal(memory, split, 1, true))
        {
            memory.JournalReplayCommand(new uint[] { 0xe7000000, 0 });
            memory.WriteUInt32(0x0410000c, 1);
            memory.WriteUInt32(0x80001000, 0xe4000000);
            memory.WriteUInt32(0x80001004, 0);
            memory.WriteUInt32(0x04100000, 0x1000); memory.WriteUInt32(0x04100004, 0x1008);
            memory.WriteUInt64(0x80001000, ulong.MaxValue);
            memory.WriteUInt32(0x0410000c, 2);
            BinaryPrimitives.WriteUInt32BigEndian(memory.SP_MEM_RW.AsSpan(0xff8), 0x08600000);
            BinaryPrimitives.WriteUInt32BigEndian(memory.SP_MEM_RW.AsSpan(0xffc), 0xfc000400);
            BinaryPrimitives.WriteUInt32BigEndian(memory.SP_MEM_RW, 0xe9000000);
            BinaryPrimitives.WriteUInt32BigEndian(memory.SP_MEM_RW.AsSpan(4), 0);
            memory.WriteUInt32(0x04100000, 0xff8); memory.WriteUInt32(0x04100004, 0x1008);
            journal.RequireComplete();
        }
        RdpJournalReplay.Run(split); checks++;

        memory = new Memory(new byte[4096]); R4300.memory = memory;
        memory.WriteUInt32(0x80001000, 0xe4000000); memory.WriteUInt32(0x80001004, 0);
        memory.WriteUInt32(0x04100000, 0x1000); memory.WriteUInt32(0x04100004, 0x1008);
        using var saved = new MemoryStream();
        using (var writer = new BinaryWriter(saved, System.Text.Encoding.UTF8, true)) memory.SaveState(writer);
        saved.Position = 0;
        memory = new Memory(new byte[4096]); R4300.memory = memory;
        using (var reader = new BinaryReader(saved, System.Text.Encoding.UTF8, true)) memory.LoadState(reader);
        string warm = Path.Combine(root, "warm-pending");
        using (var journal = new RdpJournal(memory, warm, 1, false))
        {
            memory.WriteUInt32(0x80002000, 0x08600000); memory.WriteUInt32(0x80002004, 0xfc000400);
            memory.WriteUInt32(0x80002008, 0xe9000000); memory.WriteUInt32(0x8000200c, 0);
            memory.WriteUInt32(0x04100000, 0x2000); memory.WriteUInt32(0x04100004, 0x2010);
            journal.RequireComplete();
        }
        RdpJournalReplay.Run(warm); checks++;

        memory = new Memory(new byte[4096]); R4300.memory = memory;
        using (var audit = new RdpJournal(memory, Path.Combine(root, "audit"), 1, true))
        {
            memory.JournalReplayCommand(new uint[] { 0xe7000000, 0 });
            memory.RDRAM[0x777777] = 42; // Deliberately bypass every write hook.
            memory.JournalReplayCommand(new uint[] { 0xe9000000, 0 });
            Check(audit.Failure is InvalidDataException && !audit.Completed, "Untracked write audit failed");
        }
        byte[] valid = File.ReadAllBytes(Path.Combine(capture, "journal.bin"));
        int headerLength = 8 + 12 + 32 + RdpJournal.RamSize + RdpJournal.HiddenSize;
        string malformed = Path.Combine(root, "malformed.bin");
        void Parse() { using var reader = new RdpJournalReader(malformed); while (!reader.Ended) reader.Next(); }
        foreach (int length in new[] { 0, 7, 19, headerLength - 1, headerLength, valid.Length - 1, valid.Length - 13 - 28 })
        {
            File.WriteAllBytes(malformed, valid.AsSpan(0, length).ToArray());
            Reject(Parse, "Truncated journal accepted");
        }
        foreach (int offset in new[] { 0, 8, 12, headerLength, headerLength + 1, headerLength + 5 })
        {
            byte[] bad = (byte[])valid.Clone(); bad[offset] ^= 0xff;
            File.WriteAllBytes(malformed, bad); Reject(Parse, "Corrupted journal accepted");
        }
        File.WriteAllBytes(malformed, valid.Concat(new byte[] { 0 }).ToArray());
        Reject(Parse, "Trailing bytes accepted");
        Console.WriteLine($"rdpJournalChecks={checks} exactRanges=passed unchangedStores=passed cpuAndJitAndDma=passed textureUpdates=passed splitCommands=passed negativeControls=passed output={root}");
        if (Environment.GetEnvironmentVariable("N64_PROBE_JOURNAL_TEST_OUTPUT") == null) Directory.Delete(root, recursive: true);
    }
}
#endif
