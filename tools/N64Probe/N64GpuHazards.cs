#if N64_RDP_JOURNAL
using Ryu64.MIPS;

// Synthetic ordered input for independent Angrylion/native-ABI comparison.
// Software hashes describe this implementation's output, not the oracle.
internal static class N64GpuHazards
{
    internal static void Capture(string output)
    {
        var memory = new Memory(new byte[4096]); R4300.memory = memory;
        using var journal = new RdpJournal(memory, output, 10, true);
        void Cmd(params uint[] words) => memory.JournalReplayCommand(words);
        void Store(uint address, byte value) => memory.WriteUInt8(0x80000000u | address, value);
        void Target(uint address, uint size = 2, uint width = 64) => Cmd(0xff000000 | size << 19 | (width - 1), address);
        void Fill(uint value) { Cmd(0xf7000000, value); Cmd(0xf60fc07c, 0); }
        void End() => Cmd(0xe9000000, 0);
        Cmd(0xef300000, 0); Cmd(0xed000000, 0x00100080); Target(0x300000);
        // No depth target yet: conservative fallback, followed by several
        // independent writes that can safely cross draws after initialization.
        Store(0x100001, 1); Fill(0xff01ff01);
        Cmd(0xfe000000, 0x500000);
        for (uint i = 0; i < 8; i++) { Store(0x100003 + i * 5, (byte)i); Fill(0x07c107c1); }
        End();
        // Write-before-draw must not reappear after that draw. Repeated and
        // same-value writes stay in order, including unaligned byte lanes.
        Store(0x300001, 0xc1); Store(0x300002, 0x12); Fill(0xf801f801);
        memory.FastMemoryWrite(0x80300003, new byte[] { 7, 6, 5, 4, 3 });
        Fill(0x003f003f); Store(0x300005, 0x55); End();
        // Formerly independent data becomes a framebuffer after state change.
        Store(0x100001, 0x42); Target(0x100000); Fill(0xff01ff01); End();
        // The conservative attachment range wraps, while the actual draw stays
        // within installed RAM. Full out-of-RAM draws differ between the two
        // pinned backends even in strict mode and are a separate accuracy gate.
        Cmd(0xed000000, 0x00100004);
        Target(0x7fff00); Store(0x10, 0x88); Store(0x7fffff, 0x99); Fill(0x07c107c1); End();
        // Width changes and pending writes to the newly exposed rows.
        Cmd(0xed000000, 0x00100080);
        Target(0x300000, 2, 1024); Store(0x308011, 0x67); Fill(0x003f003f); End();
        // Maximum scissor Y; a patch on the last row must be ordered. The
        // range proof must not use just the previous primitive's small height.
        Target(0x300000); Cmd(0xed000000, 0x00100fff);
        Store(0x31ff80, 0x44); Cmd(0xf7000000, 0xff01ff01); Cmd(0xf60fcffc, 0x00000ffc); End();
        // Four color sizes exercise byte/halfword/word address conversion.
        Cmd(0xed000000, 0x00100080);
        // I4 does not support the hardware fill cycle; use primitive color.
        Cmd(0xef000000, 0); Cmd(0xfcffffff, 0xfffdf6fb); Cmd(0xfa000000, 0x12345679);
        foreach (uint size in new uint[] { 0, 1, 2, 3 })
        {
            Target(0x300003, size);
            Store(0x300000, 0x76); Store(0x300003, 0x54);
            Cmd(0xf60fc07c, 0);
        }
        End();
        // Depth memory can be read even when not written. Force primitive Z
        // on rectangles with a primitive-color combiner and compare/update.
        Target(0x300000); Cmd(0xfe000000, 0x500000);
        Cmd(0xfcffffff, 0xfffdf6fb); Cmd(0xfa000000, 0xff0000ff);
        Cmd(0xef000000, 0x34); Cmd(0xee000000, 0x01000001);
        memory.FastMemoryWrite(0x80500000, Enumerable.Repeat((byte)0xff, 4096).ToArray());
        Cmd(0xf60fc07c, 0); Store(0x500003, 0); Cmd(0xf60fc07c, 0); End();
        // A conservative wrapped depth range also requires low-memory writes.
        Cmd(0xed000000, 0x00100004);
        Cmd(0xfe000000, 0x7fff00); Store(0x20, 0x44); Cmd(0xf60fc07c, 0); End();
        // Texture loads remain unconditional boundaries; updated source bytes
        // must reach TMEM in order even though outside both framebuffers.
        Cmd(0xef300000, 0); Cmd(0xfe000000, 0x500000); Target(0x300000);
        Cmd(0xfd100000, 0x700000); Cmd(0xf5100200, 0);
        memory.WriteUInt16(0x80700000, 0xabcd); Cmd(0xf4000000, 0);
        memory.WriteUInt16(0x80700000, 0x2469); Cmd(0xf4000000, 0); End();
        journal.RequireComplete();
        Console.WriteLine($"gpuHazardJournal=passed frames=10 output={output}");
    }
}
#endif
