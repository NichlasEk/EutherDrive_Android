using System.Diagnostics;
using System.Reflection;
using EutherDrive.Core;

internal static class FrameConversionChecks
{
    private delegate void Convert(ReadOnlySpan<byte> source, Span<byte> target, bool swap);

    internal static void Run()
    {
        var convert = typeof(N64Adapter).GetMethod("ConvertRgba5551ToBgra", BindingFlags.NonPublic | BindingFlags.Static)!
            .CreateDelegate<Convert>();
        int cases = 0;
        void Check(byte[] data, int sourceOffset, int sourceLength, int targetOffset, int targetLength, bool swap)
        {
            byte[] original = (byte[])data.Clone();
            byte[] actual = Enumerable.Repeat((byte)0x5a, targetOffset + targetLength + 17).ToArray();
            byte[] expected = (byte[])actual.Clone();
            var src = data.AsSpan(sourceOffset, sourceLength);
            var dst = expected.AsSpan(targetOffset, targetLength);
            int pixels = Math.Min(sourceLength / 2, targetLength / 4);
            for (int i = 0; i < pixels; i++)
            {
                int p = swap ? src[i * 2] | src[i * 2 + 1] << 8 : src[i * 2] << 8 | src[i * 2 + 1];
                dst[i * 4] = (byte)(((p >> 1) & 31) * 255 / 31);
                dst[i * 4 + 1] = (byte)(((p >> 6) & 31) * 255 / 31);
                dst[i * 4 + 2] = (byte)(((p >> 11) & 31) * 255 / 31);
                dst[i * 4 + 3] = 255;
            }
            dst[(pixels * 4)..].Clear();
            convert(src, actual.AsSpan(targetOffset, targetLength), swap);
            if (!actual.AsSpan().SequenceEqual(expected) || !data.AsSpan().SequenceEqual(original))
                throw new Exception($"Frame conversion differs: offset={sourceOffset}/{targetOffset} length={sourceLength}/{targetLength} swap={swap}");
            cases++;
        }
        var all = new byte[131072 + 7];
        for (int p = 0; p < 65536; p++) { all[3 + p * 2] = (byte)(p >> 8); all[4 + p * 2] = (byte)p; }
        byte[] edges = all.AsSpan(0, 80).ToArray();
        foreach (bool swap in new[] { false, true })
        {
            Check(all, 3, 131072, 1, 262144, swap);
            foreach (int sourceOffset in new[] { 0, 1, 3 })
            foreach (int targetOffset in new[] { 0, 1, 5 })
            for (int sourceLength = 0; sourceLength <= 65; sourceLength++)
            foreach (int targetLength in new[] { 0, 1, 3, 4, 7, 12, 63, 129, 133 })
                Check(edges, sourceOffset, sourceLength, targetOffset, targetLength, swap);
        }
        byte[] frame = new byte[320 * 237 * 2]; new Random(64).NextBytes(frame);
        byte[] screen = new byte[320 * 237 * 4];
        for (int i = 0; i < 200; i++) convert(frame, screen, false);
        var watch = Stopwatch.StartNew();
        for (int i = 0; i < 2000; i++) convert(frame, screen, false);
        watch.Stop();
        Console.WriteLine($"frameConversionCases={cases} colors=65536 byteOrders=2 unaligned=passed tails=passed canaries=passed source=unchanged");
        Console.WriteLine($"frameConversionBenchmark frames=2000 milliseconds={watch.Elapsed.TotalMilliseconds:F3} checksum={screen[123]}");
    }
}
