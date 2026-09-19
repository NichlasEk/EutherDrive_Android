using System.Buffers.Binary;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using Ryu64.MIPS;

// Diagnostic command tape, captured synchronously through existing RDP logging.
// No callback or capture allocation is added to the normal emulator hot path.
internal sealed class RdpCapture : TextWriter
{
    private readonly TextWriter _previous;
    private readonly string _directory;
    private BinaryWriter? _tape;
    private bool _finished;
    private int _commands;
    public override Encoding Encoding => _previous.Encoding;
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    internal RdpCapture(string directory)
    {
        if (File.Exists(Path.Combine(directory, "rdp-start.bin"))
            || File.Exists(Path.Combine(directory, "rdp-tape.bin"))
            || File.Exists(Path.Combine(directory, "rdp-final.ppm")))
            throw new IOException("Use a fresh capture directory; existing tapes are not overwritten");
        _directory = directory;
        _previous = Console.Out;
        Console.SetOut(this);
    }

    public override void Write(string? value)
    {
        _previous.Write(value);
        if (value != null) Observe(value);
    }

    public override void WriteLine(string? value)
    {
        _previous.WriteLine(value);
        if (value != null) Observe(value);
    }

    private void Observe(string line)
    {
        if (_finished || !line.StartsWith("[N64RDP] list start=")) return;
        var match = Regex.Match(line, @"start=0x([0-9a-f]+) end=0x([0-9a-f]+) xbus=(True|False).*hist=(.*?) firstUnhandled");
        if (!match.Success) throw new InvalidDataException("Cannot parse RDP capture trace");
        uint start = Convert.ToUInt32(match.Groups[1].Value, 16);
        uint end = Convert.ToUInt32(match.Groups[2].Value, 16);
        bool xbus = match.Groups[3].Value == "True";
        Memory memory = R4300.memory;
        int length = checked((int)(end - start));
        if (length <= 0 || length > 0x20000) throw new InvalidDataException("Invalid RDP capture range");
        byte[] command = new byte[length];
        for (int i = 0; i < length; i++)
            command[i] = xbus ? memory.SP_MEM_RW[(start + i) & 0xfff] : memory.RDRAM[start + i];
        if (_tape == null)
        {
            // A fresh frame starts with SyncPipe, which does not change render state.
            // Refuse a partial-frame capture rather than silently invent its inputs.
            if (length != 8 || (command[0] & 0x3f) != 0x27) return;
            using (var state = new BinaryWriter(File.Create(Path.Combine(_directory, "rdp-start.bin")))) memory.SaveState(state);
            _tape = new BinaryWriter(File.Create(Path.Combine(_directory, "rdp-tape.bin")));
        }
        _tape.Write(start);
        _tape.Write(xbus);
        _tape.Write(length);
        _tape.Write(command);
        _tape.Flush();
        _commands++;
        // A gameplay frame can exceed the normal diagnostic log budget. Re-arm
        // it only while capturing, without changing normal renderer defaults.
        if (_commands % 1024 == 0)
            typeof(Memory).GetField("_traceRdpSummaryCount", BindingFlags.Static | BindingFlags.NonPublic)!.SetValue(null, 0);
        if (_commands % 64 == 0) SaveFrame(memory, Path.Combine(_directory, $"rdp-step-{_commands:D4}.ppm"));
        if (match.Groups[4].Value.Contains("29:"))
        {
            SaveFrame(memory, Path.Combine(_directory, "rdp-final.ppm"));
            File.WriteAllBytes(Path.Combine(_directory, "rdp-final-rdram.bin"), memory.RDRAM);
            _finished = true;
            _tape.Dispose();
            _tape = null;
            _previous.WriteLine($"rdpCapturedLists={_commands} complete=True");
        }
    }

    protected override void Dispose(bool disposing)
    {
        Console.SetOut(_previous);
        _tape?.Dispose();
        if (!_finished) _previous.WriteLine($"rdpCapturedLists={_commands} complete=False");
        base.Dispose(disposing);
    }

    internal static void Replay(string directory, string output)
    {
        if (Path.GetFullPath(directory) == Path.GetFullPath(output))
            throw new ArgumentException("Replay output must not overwrite the captured frame");
        if (!File.Exists(Path.Combine(directory, "rdp-final.ppm")))
            throw new InvalidDataException("Capture did not reach FullSync");
        Directory.CreateDirectory(output);
        var memory = new Memory(new byte[4096]);
        R4300.memory = memory;
        using (var state = new BinaryReader(File.OpenRead(Path.Combine(directory, "rdp-start.bin")))) memory.LoadState(state);
        var execute = typeof(Memory).GetMethod("ExecuteRdpDisplayList", Private)!.CreateDelegate<Func<uint, uint, uint>>(memory);
        using var tape = new BinaryReader(File.OpenRead(Path.Combine(directory, "rdp-tape.bin")));
        int count = 0;
        while (tape.BaseStream.Position < tape.BaseStream.Length)
        {
            uint start = tape.ReadUInt32();
            bool xbus = tape.ReadBoolean();
            int length = tape.ReadInt32();
            if (length <= 0 || length > 0x20000) throw new InvalidDataException("Invalid RDP tape record");
            byte[] bytes = tape.ReadBytes(length);
            if (bytes.Length != length) throw new EndOfStreamException();
            for (int i = 0; i < length; i++)
            {
                if (xbus) memory.SP_MEM_RW[(start + i) & 0xfff] = bytes[i];
                else memory.RDRAM[start + i] = bytes[i];
            }
            uint status = BinaryPrimitives.ReadUInt32BigEndian(memory.DPC_STATUS_REG_R);
            BinaryPrimitives.WriteUInt32BigEndian(memory.DPC_STATUS_REG_R, xbus ? status | 1u : status & ~1u);
            uint consumed = execute(start, start + (uint)length);
            if (consumed != start + (uint)length) throw new Exception("Incomplete RDP replay command");
            if (++count % 64 == 0) SaveFrame(memory, Path.Combine(output, $"rdp-step-{count:D4}.ppm"));
        }
        SaveFrame(memory, Path.Combine(output, "rdp-final.ppm"));
        File.WriteAllBytes(Path.Combine(output, "rdp-final-rdram.bin"), memory.RDRAM);
        Console.WriteLine($"replayedLists={count} framebuffer={memory.LastRdpColorImageAddress:x8}");
        string expected = Path.Combine(directory, "rdp-final.ppm");
        bool matches = File.Exists(expected) && File.ReadAllBytes(expected).AsSpan().SequenceEqual(File.ReadAllBytes(Path.Combine(output, "rdp-final.ppm")));
        Console.WriteLine($"capturedFramebufferMatches={matches}");
    }

    private static void SaveFrame(Memory memory, string path)
    {
        uint address = memory.LastRdpColorImageAddress;
        int width = (int)(uint)typeof(Memory).GetField("_rdpColorImageWidth", Private)!.GetValue(memory)!;
        uint size = (uint)typeof(Memory).GetField("_rdpColorImageSize", Private)!.GetValue(memory)!;
        if (size != 2 && size != 3) return;
        int bytes = size == 3 ? 4 : 2;
        if (width <= 0 || address + width * 240 * bytes > memory.RDRAM.Length) return;
        using var stream = File.Create(path);
        stream.Write(Encoding.ASCII.GetBytes($"P6\n{width} 240\n255\n"));
        int[] shifts = { 11, 6, 1 };
        for (int i = 0; i < width * 240; i++)
        {
            int offset = (int)address + i * bytes;
            if (bytes == 4) stream.Write(memory.RDRAM, offset, 3);
            else
            {
                ushort pixel = BinaryPrimitives.ReadUInt16BigEndian(memory.RDRAM.AsSpan(offset));
                foreach (int shift in shifts)
                {
                    int c = pixel >> shift & 31;
                    stream.WriteByte((byte)(c << 3 | c >> 2));
                }
            }
        }
    }
}
