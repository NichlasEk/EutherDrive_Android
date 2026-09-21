using System.Buffers.Binary;

internal static class RdpDumpExportChecks
{
    internal static void Run()
    {
        string path = Path.Combine(Path.GetTempPath(), $"n64-rdp-export-{Guid.NewGuid():N}.bin");
        int cases = 0;
        try
        {
            void Record(BinaryWriter writer, byte[] bytes, bool xbus, uint address)
            {
                writer.Write(address); writer.Write(xbus); writer.Write(bytes.Length); writer.Write(bytes);
            }
            byte[] fullSync = { 0xe9, 0, 0, 0, 0, 0, 0, 0 };
            foreach (int op in new[] { 8, 9, 10, 11, 12, 13, 14, 15, 0x24, 0x25 })
            {
                int words = op < 16 ? 8 + ((op & 4) != 0 ? 16 : 0) + ((op & 2) != 0 ? 16 : 0) + ((op & 1) != 0 ? 4 : 0) : 4;
                byte[] command = new byte[words * 4];
                for (int i = 0; i < words; i++) BinaryPrimitives.WriteUInt32BigEndian(command.AsSpan(i * 4), 0x08600000u + (uint)i);
                command[0] = (byte)(0xc0 | op);
                for (int split = 8; split < command.Length; split += 8)
                {
                    using (var writer = new BinaryWriter(File.Create(path)))
                    {
                        Record(writer, command[..split], false, 0x18f158);
                        Record(writer, command[split..].Concat(fullSync).ToArray(), true, 0x18d160);
                    }
                    var commands = RdpDumpExport.ReadCommands(path, out int chunks);
                    if (chunks != 2 || commands.Count != 2 || commands[0].Length != words || commands[1][0] != 0xe9000000)
                        throw new Exception("Lost a split command or trailing FullSync");
                    for (int i = 0; i < words; i++)
                        if (commands[0][i] != BinaryPrimitives.ReadUInt32BigEndian(command.AsSpan(i * 4)))
                            throw new Exception("Word order changed");
                    cases++;
                }
            }
            for (int invalid = 0; invalid < 4; invalid++)
            {
                using (var writer = new BinaryWriter(File.Create(path)))
                {
                    writer.Write(0u); writer.Write((byte)(invalid == 0 ? 2 : 0));
                    writer.Write(invalid == 1 ? 7 : invalid == 2 ? 16 : 8);
                    writer.Write(invalid == 3 ? new byte[] { 0xe4, 0, 0, 0, 0, 0, 0, 0 } : fullSync);
                }
                bool rejected = false;
                try { RdpDumpExport.ReadCommands(path, out _); }
                catch (Exception error) when (error is IOException or InvalidDataException) { rejected = true; }
                if (!rejected) throw new Exception("Accepted malformed or incomplete tape");
                cases++;
            }
        }
        finally { File.Delete(path); }
        Console.WriteLine($"rdpDumpExportCases={cases} passed");
    }
}
