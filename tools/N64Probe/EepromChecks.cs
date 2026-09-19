using System.Buffers.Binary;
using System.Reflection;
using Ryu64.MIPS;

internal static class EepromChecks
{
    internal static void Run()
    {
        int checks = 0;
        foreach (var (crc1, crc2, bytes) in new (uint, uint, int)[] {
            (0x635A2BFF, 0x8B022326, 512), (0xA03CF036, 0xBCC1C5D2, 512),
            (0x4EAA3D0E, 0x74757C24, 512), (0, 0, 2048), (0x635A2BFF, 0, 2048) })
        {
            byte[] rom = new byte[0x1000];
            BinaryPrimitives.WriteUInt32BigEndian(rom.AsSpan(0x10), crc1);
            BinaryPrimitives.WriteUInt32BigEndian(rom.AsSpan(0x14), crc2);
            var memory = new Memory(rom);
            var process = typeof(Memory).GetMethod("ProcessPifJoybusCommands", BindingFlags.Instance | BindingFlags.NonPublic)!.CreateDelegate<Action>(memory);
            var pif = memory.PIFRAM;
            void Command(byte command, byte block, int tx, int rx)
            {
                Array.Clear(pif);
                // Four empty channels, then the cartridge EEPROM channel.
                pif[4] = (byte)tx; pif[5] = (byte)rx; pif[6] = command; pif[7] = block;
                pif[6 + tx + rx] = 0xfe;
            }
            Command(0, 0, 1, 3);
            process();
            if (pif[7] != 0 || pif[8] != (bytes == 512 ? 0x80 : 0xc0) || pif[9] != 0)
                throw new InvalidOperationException("Wrong cartridge EEPROM Joybus ID");
            checks++;
            foreach (byte block in new byte[] { 0, (byte)(bytes / 8 - 1) })
            {
                Command(5, block, 10, 1);
                for (int i = 0; i < 8; i++) pif[8 + i] = (byte)(block ^ (i + 0x20));
                process();
                if (pif[16] != 0) throw new InvalidOperationException("EEPROM write failed");
                Command(4, block, 2, 8);
                process();
                for (int i = 0; i < 8; i++)
                    if (pif[8 + i] != (byte)(block ^ (i + 0x20))) throw new InvalidOperationException("EEPROM data mismatch");
                checks++;
            }
            if (bytes == 512)
            {
                Command(5, 64, 10, 1);
                process();
                Command(4, 0, 2, 8);
                process();
                for (int i = 0; i < 8; i++)
                    if (pif[8 + i] != i + 0x20) throw new InvalidOperationException("Out-of-range EEPROM write aliased block zero");
                checks++;
            }
        }
        Console.WriteLine($"eepromChecks=passed cases={checks}");
    }
}
