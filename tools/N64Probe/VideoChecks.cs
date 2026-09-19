using System.Reflection;
using Ryu64.MIPS;

internal static class VideoChecks
{
    internal static void Run()
    {
        var core = new Ryu64Core.Ryu64Core();
        R4300.memory = new Memory(new byte[4096]);
        var memory = R4300.memory;
        new Random(64920).NextBytes(memory.RDRAM);
        typeof(Ryu64Core.Ryu64Core).GetField("isRunning", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(core, true);
        var hint = typeof(Memory).GetMethod("GetFramebufferHeightHint", BindingFlags.Instance | BindingFlags.NonPublic)!;
        int checks = 0;
        foreach (var (vStart, scale, expected) in new (uint, uint, uint)[]
        {
            (0x002301fd, 0x800, 474), // Perfect Dark saved VI registers.
            (0x002301fd, 0x400, 237),
            (0x002301fd, 0x200, 118),
            (0x002301fd, 0x02000800, 474), // Fractional offset is not scale.
            (0x00200200, 0x400, 240),
            (0x00200260, 0x800, 576), // PAL high-resolution source.
            (0x03f00010, 0x400, 16), // Wrapped timing range.
            (0, 0x800, 240),
            (0x00200200, 0, 240),
            (0x00200260, 0xfff, 240) // Reject implausible source height.
        })
        foreach (int width in new[] { 320, 576 })
        foreach (int bpp in new[] { 2, 4 })
        {
            memory.WriteUInt32(0xa4400000, bpp == 2 ? 0x13056u : 0x13057u);
            memory.WriteUInt32(0xa4400004, 0x100000);
            memory.WriteUInt32(0xa4400008, (uint)width);
            memory.WriteUInt32(0xa4400028, vStart);
            memory.WriteUInt32(0xa4400034, scale);
            if (Memory.ComputeViFramebufferHeight(vStart, scale) != expected
                || (uint)hint.Invoke(memory, null)! != expected)
                throw new Exception("VI source height mismatch");
            for (int cached = 0; cached < 2; cached++)
            {
                if (!core.TryGetFramebuffer(out var frame, out int w, out int h, out int pixelBytes)
                    || w != width || h != expected || pixelBytes != bpp
                    || !frame.AsSpan(0, w * h * bpp).SequenceEqual(memory.RDRAM.AsSpan(0x100000, w * h * bpp)))
                    throw new Exception($"VI readback mismatch: {core.LastFramebufferStatus}");
                checks++;
            }
        }
        // An out-of-range VI buffer must fail, never perform an unchecked copy.
        memory.WriteUInt32(0xa4400004, 0x7ffff0);
        if (core.TryGetFramebuffer(out _, out _, out _, out _))
            throw new Exception("Out-of-range VI framebuffer accepted");
        Console.WriteLine($"videoChecks={checks + 1} passed");
    }
}
