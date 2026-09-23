using System.Reflection;
using Ryu64.MIPS;
using Ryu64Core;

internal static class AudioChecks
{
    internal static void Run()
    {
        var memory = new Memory(new byte[4096]);
        R4300.memory = memory;
        const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Instance;
        void Complete() => typeof(Memory).GetMethod("FinalizeAiDmaCompletion", flags)!.Invoke(memory, null);
        void Push(uint address) {
            memory.WriteUInt32(0xa4500000, address);
            memory.WriteUInt32(0xa4500004, 8);
        }
        void Check(bool ok, string message) { if (!ok) throw new Exception(message); }
        memory.RDRAM[0] = 0x12; memory.RDRAM[1] = 0x34;
        memory.RDRAM[2] = 0xff; memory.RDRAM[3] = 0xfe;
        Push(0);
        memory.RDRAM[1] = 0x56;
        var first = memory.DequeueAudio(out uint rate);
        Check(first.SequenceEqual(new short[] { 0x1234, -2, 0, 0 }) && rate == 44100, "PCM snapshot/endian/address zero");
        memory.AI_LEN_READ_EVENT();
        Check(memory.DequeueAudio(out _).Length == 0, "Countdown repeated audio");
        Push(0);
        Check(memory.DequeueAudio(out _).Length == 0, "Queued FIFO played early");
        Complete();
        Check(memory.DequeueAudio(out _)[0] == 0x1256, "Reused address/length lost");
        Complete();
        memory.WriteUInt32(0xa4500010, 1519);
        Push(0);
        memory.DequeueAudio(out rate);
        Check(rate == (uint)Math.Round(48681812.0 / 1520), "DAC rate");
        Complete();
        Push(0x00fffff8);
        Check(memory.DequeueAudio(out _).All(value => value == 0), "Unpopulated RDRAM must be silent");
        Complete();
        for (int i = 0; i < 100; i++) { Push(0); Complete(); }
        int count = 0;
        while (memory.DequeueAudio(out _).Length > 0) count++;
        Check(count == 16, "Unbounded host queue");
        Push(0);
        using var stream = new MemoryStream();
        memory.SaveState(new BinaryWriter(stream));
        stream.Position = 0;
        memory.LoadState(new BinaryReader(stream));
        Check(memory.DequeueAudio(out _).Length == 0, "Stale savestate audio");

        foreach (uint source in new uint[] { 8000, 32000, 32028, 44100, 48000, 96000 })
        foreach (int target in new[] { 22050, 44100, 48000, 192000 })
        {
            short[] input = Enumerable.Range(0, 4000).Select(i => (short)(i % 2 == 0 ? i : -i)).ToArray();
            var whole = new StereoResampler().Convert(input, source, target);
            var resampler = new StereoResampler();
            var split = new List<short>();
            for (int i = 0; i < input.Length; i += 8)
            {
                split.AddRange(resampler.Convert(input[i..(i + 8)], source, target));
                resampler.Convert(Array.Empty<short>(), 44100, target);
            }
            Check(whole.Length == split.Count && whole.Zip(split).All(p => Math.Abs(p.First - p.Second) <= 1), "Resampler block continuity");
            Check(Math.Abs(whole.Length / 2.0 - 2000.0 * target / source) <= Math.Ceiling((double)target / source), "Resampler sample budget");
            resampler.Reset();
            Check(resampler.Convert(input, source, target).SequenceEqual(whole), "Resampler reset");
        }
        CheckTvAudio();
        Console.WriteLine("Audio checks passed: AI snapshots/FIFO/reuse/rate/bounds/state, NTSC/PAL/MPAL clocks and 24 streaming resampling combinations.");
    }

    private static void CheckTvAudio()
    {
        // 320 stereo samples at DAC divider 1520. PAL's slower VI must not
        // stretch AI DMA by 60/50; its independent DAC clock sets the rate.
        foreach (var (country, rate, viCycles, noViCycles, lines) in new[] {
            ('E', 32028u, 936620u, 936680u, 525u),
            ('P', 32669u, 918301u, 918301u, 625u),
            ('B', 31992u, 937674u, 937734u, 525u)
        })
        foreach (bool enableVi in new[] { false, true })
        {
            byte[] rom = new byte[4096];
            rom[0x3e] = (byte)country;
            var memory = new Memory(rom);
            R4300.memory = memory;
            if (enableVi) memory.WriteUInt32(0x04400018, lines - 1);
            memory.WriteUInt32(0x04500010, 1519);
            memory.WriteUInt32(0x04500000, 0);
            memory.WriteUInt32(0x04500004, 1280);
            if (memory.DequeueAudio(out uint actualRate).Length != 640 || actualRate != rate)
                throw new Exception($"Incorrect {country} AI DAC rate");
            memory.Tick((enableVi ? viCycles : noViCycles) - 1);
            if ((memory.ReadUInt32(0x04300008) & 4) != 0)
                throw new Exception("Premature regional AI completion");
            memory.Tick(1);
            if ((memory.ReadUInt32(0x04300008) & 4) == 0)
                throw new Exception("VI refresh rate stretched the audio DMA deadline");
        }
    }
}
