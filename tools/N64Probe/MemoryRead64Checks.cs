using System.Buffers.Binary;
using Ryu64.MIPS;

internal static class MemoryRead64Checks
{
    internal static void Run()
    {
        var memory = new Memory(new byte[4096]);
        new Random(6430).NextBytes(memory.RDRAM);
        int cases = 0;
        void Check(uint address)
        {
            ulong expected = BinaryPrimitives.ReadUInt64BigEndian(memory[address, 8]);
            if (memory.ReadUInt64(address) != expected || unchecked((ulong)memory.ReadInt64(address)) != expected)
                throw new Exception($"Read64 mismatch at {address:x8}");
            cases++;
        }
        foreach (uint segment in new uint[] { 0x80000000, 0xa0000000 })
        {
            for (uint offset = 0; offset < 8192; offset++) Check(segment + offset);
            // RAM end: reads beyond installed RAM retain the existing mirroring behavior.
            for (uint offset = (uint)memory.RDRAM.Length - 16; offset < memory.RDRAM.Length + 16; offset++) Check(segment + offset);
            var random = new Random(64);
            for (int i = 0; i < 10000; i++) Check(segment + (uint)random.Next(memory.RDRAM.Length - 7));
            // Device memory stays on the byte path.
            for (uint offset = 0; offset < 32; offset++) Check(segment + 0x04000000 + offset);
        }
        Console.WriteLine($"read64Cases={cases} endian=passed unaligned=passed boundaries=passed");
    }
}
