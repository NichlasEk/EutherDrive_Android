using System.Buffers.Binary;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Ryu64.MIPS;

// Offline bridge to paraLLEl-RDP's RDPDUMP2 tooling. This is deliberately not
// a live RDP backend or a lossless conversion of an emulator savestate.
internal static class RdpDumpExport
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    internal static void Run(string directory, string output)
    {
        if (!File.Exists(Path.Combine(directory, "rdp-final.ppm")))
            throw new InvalidDataException("Capture did not reach FullSync");
        if (Directory.Exists(output) || File.Exists(output))
            throw new IOException("Use a new output directory");

        var memory = new Memory(new byte[4096]);
        R4300.memory = memory;
        using (var reader = new BinaryReader(File.OpenRead(Path.Combine(directory, "rdp-start.bin"))))
            memory.LoadState(reader);
        if ((int)Field("_rdpPendingWordCount") != 0)
            throw new InvalidDataException("Capture begins inside a command");

        object Field(string name) => typeof(Memory).GetField(name, Private)!.GetValue(memory)!;
        uint U(string name) => Convert.ToUInt32(Field(name));
        int I(string name) => Convert.ToInt32(Field(name));
        var commands = ReadCommands(Path.Combine(directory, "rdp-tape.bin"), out int chunks);
        if (commands.Count == 0 || (commands[^1][0] >> 24 & 63) != 0x29)
            throw new InvalidDataException("Tape must end at FullSync");
        bool otherModes = false, combine = false;
        foreach (var words in commands)
        {
            uint op = words[0] >> 24 & 63;
            otherModes |= op == 0x2f;
            combine |= op == 0x3c;
            if ((op is >= 8 and <= 15 or 0x24 or 0x25 or 0x36) && !otherModes)
                throw new InvalidDataException("Drawing relies on uncaptured raw other-modes state");
            if ((op is >= 8 and <= 15 or 0x24 or 0x25) && !combine)
                throw new InvalidDataException("Drawing relies on uncaptured combiner state");
        }

        // The software renderer retains integer scissor bounds, not their
        // original subpixel/interlace bits. Make that approximation explicit.
        uint Scissor(int x, int y) => ((uint)Math.Clamp(x * 4, 0, 4095) << 12)
            | (uint)Math.Clamp(y * 4, 0, 4095);
        var primer = new List<uint[]>
        {
            new[] { 0xed000000u | Scissor(I("_rdpScissorX0"), I("_rdpScissorY0")),
                Scissor(I("_rdpScissorX1") + 1, I("_rdpScissorY1") + 1) },
            new[] { 0xee000000u, U("_rdpPrimitiveDepth") << 16 | U("_rdpPrimitiveDeltaZ") },
            new[] { 0xf7000000u, U("_rdpFillColor") },
            new[] { 0xf8000000u, U("_rdpFogColor") },
            new[] { 0xf9000000u, U("_rdpBlendColor") },
            new[] { 0xfa000000u, U("_rdpPrimColor") },
            new[] { 0xfb000000u, U("_rdpEnvColor") },
            new[] { 0xfe000000u, U("_rdpMaskImageAddress") }
        };
        byte[] rdram = new byte[memory.RDRAM.Length];
        for (int i = 0; i < rdram.Length; i++) rdram[i ^ 3] = memory.RDRAM[i];
        var hiddenSource = (byte[])Field("_rdpHiddenBits");
        byte[] hidden = new byte[hiddenSource.Length];
        for (int i = 0; i < hidden.Length; i++) hidden[i ^ 1] = hiddenSource[i];

        Directory.CreateDirectory(output);
        string dump = Path.Combine(output, "frame.rdp");
        using (var writer = new BinaryWriter(File.Open(dump, FileMode.CreateNew)))
        {
            writer.Write(Encoding.ASCII.GetBytes("RDPDUMP2"));
            writer.Write(rdram.Length);
            writer.Write(hidden.Length);
            writer.Write(1u); writer.Write(0u); writer.Write(rdram.Length); writer.Write(rdram);
            writer.Write(7u);
            writer.Write(8u); writer.Write(0u); writer.Write(hidden.Length); writer.Write(hidden);
            writer.Write(9u);
            foreach (var words in primer.Concat(commands))
            {
                writer.Write(2u); writer.Write(words[0] >> 24 & 63); writer.Write(words.Length);
                foreach (uint word in words) writer.Write(word);
            }
            writer.Write(5u); // Complete RDP work; VI scanout is a separate milestone.
            writer.Write(6u);
        }
        var manifest = new
        {
            format = "RDPDUMP2", source = Path.GetFullPath(directory), chunks,
            commands = commands.Count, primerCommands = primer.Count,
            sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(dump))),
            limitations = new[]
            {
                "Frozen-input replay; original tape does not record interleaved CPU/RSP texture-memory writes.",
                "Command staging writes are not RDRAM updates in this dump; command/texture aliasing is not validated.",
                "Integer scissor and color/depth primer reconstructed from software state; not raw hardware state.",
                "Initial TMEM, tile state, convert/key registers and primitive LOD are not restored.",
                "Conformance is between GPU and Angrylion on this explicit input, not proof of live-game equivalence.",
                "No VI emulation or CPU/GPU memory-coherency integration is exercised."
            }
        };
        File.WriteAllText(Path.Combine(output, "manifest.json"), JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"rdpDumpExport chunks={chunks} commands={commands.Count} primer={primer.Count} sha256={manifest.sha256}");
    }

    // Reassemble by arrival order, independently of ring addresses and RDRAM /
    // XBUS source changes. This preserves the Rampage split-rectangle case.
    internal static List<uint[]> ReadCommands(string path, out int chunks)
    {
        using var tape = new BinaryReader(File.OpenRead(path));
        var commands = new List<uint[]>();
        var pending = new List<uint>(44);
        chunks = 0;
        while (tape.BaseStream.Position < tape.BaseStream.Length)
        {
            tape.ReadUInt32();
            byte xbus = tape.ReadByte();
            int length = tape.ReadInt32();
            if (xbus > 1 || length <= 0 || length > 0x20000 || (length & 7) != 0)
                throw new InvalidDataException("Invalid RDP tape record");
            byte[] bytes = tape.ReadBytes(length);
            if (bytes.Length != length) throw new EndOfStreamException();
            for (int offset = 0; offset < length; offset += 4)
            {
                pending.Add(BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(offset)));
                int op = (int)(pending[0] >> 24 & 63);
                int count = op is >= 8 and <= 15
                    ? 8 + ((op & 4) != 0 ? 16 : 0) + ((op & 2) != 0 ? 16 : 0) + ((op & 1) != 0 ? 4 : 0)
                    : op is 0x24 or 0x25 ? 4 : 2;
                if (pending.Count == count)
                {
                    commands.Add(pending.ToArray());
                    pending.Clear();
                }
            }
            chunks++;
        }
        if (pending.Count != 0) throw new InvalidDataException("Truncated RDP command at end of tape");
        return commands;
    }
}
