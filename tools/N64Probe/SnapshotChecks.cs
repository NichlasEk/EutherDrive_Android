using System.Buffers.Binary;
using System.Reflection;
using System.Security.Cryptography;
using Ryu64.MIPS;

internal static class SnapshotChecks
{
    internal static void Run()
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        (string ram, string snapshot, long calls) Render(bool streamed)
        {
            var memory = new Memory(new byte[4096]);
            var execute = typeof(Memory).GetMethod("ExecuteRdpDisplayList", flags)!.CreateDelegate<Func<uint, uint, uint>>(memory);
            typeof(Memory).GetField("_rspTaskDispatching", flags)!.SetValue(memory, streamed);
            uint p = 0x100000;
            void Command(uint a, uint b)
            {
                BinaryPrimitives.WriteUInt32BigEndian(memory.RDRAM.AsSpan((int)p), a);
                BinaryPrimitives.WriteUInt32BigEndian(memory.RDRAM.AsSpan((int)p + 4), b);
                execute(p, p + 8);
                p += 8;
            }
            Command(0xff10013f, 0x300000); // RGBA16, 320 wide
            Command(0xed000000, 0x005003c0); // 320 x 240 scissor
            for (uint row = 0; row < 240; row++)
            {
                Command(0xf7000000, row % 2 == 0 ? 0xf801f801u : 0x07c107c1u);
                Command(0xf6000000 | (319u * 4 << 12) | row * 4, row * 4);
            }
            Command(0xe9000000, 0); // Full sync must publish the final frame.
            var snapshot = (byte[])typeof(Memory).GetField("_lastVisibleRdpFramebufferSnapshot", flags)!.GetValue(memory)!;
            if (snapshot == null || snapshot.Length == 0) throw new Exception("No completed snapshot published");
            long calls = (long)typeof(Memory).GetField("_perfRdpSnapshotCalls", flags)!.GetValue(memory)!;
            return (Convert.ToHexString(SHA256.HashData(memory.RDRAM)), Convert.ToHexString(SHA256.HashData(snapshot)), calls);
        }
        var reference = Render(false);
        var streamed = Render(true);
        if (reference.ram != streamed.ram || reference.snapshot != streamed.snapshot)
            throw new Exception("Coalesced snapshot changed pixels");
        if (reference.calls != 240 || streamed.calls != 1)
            throw new Exception($"Unexpected snapshot counts {reference.calls}/{streamed.calls}; run with EUTHERDRIVE_N64_PERF=1");
        Console.WriteLine($"snapshotChecks=passed calls={reference.calls}->{streamed.calls} ram={streamed.ram} snapshot={streamed.snapshot}");
    }
}
