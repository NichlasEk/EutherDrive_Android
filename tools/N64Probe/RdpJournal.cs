#if N64_RDP_JOURNAL
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Ryu64.MIPS;

// This is an RDP-boundary journal, not a cycle trace. Repeated CPU writes
// between commands coalesce, but even stores of unchanged bytes are retained.
internal sealed class RdpJournal : IRdpJournalObserver, IDisposable
{
    internal const int RamSize = 8 << 20, HiddenSize = 4 << 20;
    internal const byte Write = 1, Command = 2, Vi = 3, Checkpoint = 4, End = 5;
    internal static readonly byte[] Magic = Encoding.ASCII.GetBytes("NRDPJ001");
    private readonly Memory _memory;
    private readonly string _directory;
    private readonly int _targetFrames;
    private readonly bool _fromReset;
    private readonly ulong[] _dirty = new ulong[RamSize / 64];
    private readonly bool[] _dirtyPages = new bool[RamSize / 4096];
    private byte[]? _shadow;
    private byte[]? _vi;
    private BinaryWriter? _writer;
    private ulong _sequence, _commands, _patches, _bytes, _unchangedBytes;
    private int _frames;
    private readonly List<object> _frameStats = new();
    private volatile bool _completed;
    private volatile Exception? _failure;
    internal bool Completed => _completed;
    internal Exception? Failure => _failure;

    internal RdpJournal(Memory memory, string directory, int frames, bool fromReset)
    {
        if (frames <= 0 || frames > 120) throw new ArgumentOutOfRangeException(nameof(frames));
        if (Directory.Exists(directory) || memory.RdpJournal != null)
            throw new IOException("Use a new journal directory and an unattached memory instance");
        if (fromReset && memory.JournalCommandCount != 0) throw new InvalidOperationException("Reset capture already executed RDP commands");
        Directory.CreateDirectory(directory);
        _memory = memory; _directory = directory; _targetFrames = frames; _fromReset = fromReset;
        memory.RdpJournal = this;
    }

    public void RdramWritten(uint address, uint length)
    {
        if (_shadow == null || length == 0 || address >= RamSize) return;
        int start = (int)address, end = (int)Math.Min((ulong)RamSize, (ulong)address + length);
        for (int page = start >> 12; page <= (end - 1) >> 12; page++) _dirtyPages[page] = true;
        while (start < end)
        {
            int bit = start & 63, count = Math.Min(end - start, 64 - bit);
            _dirty[start >> 6] |= (ulong.MaxValue >> (64 - count)) << bit;
            start += count;
        }
    }

    public void BeforeCommand(Memory memory, uint[] words) => Guard(() =>
    {
        if (_writer == null) Start();
        AuditWrites();
        FlushWrites();
        var vi = memory.JournalVi.SelectMany(r => r).ToArray();
        if (_vi == null || !_vi.AsSpan().SequenceEqual(vi)) { Record(Vi, vi); _vi = vi; }
        byte[] command = new byte[words.Length * 4];
        for (int i = 0; i < words.Length; i++) BinaryPrimitives.WriteUInt32LittleEndian(command.AsSpan(i * 4), words[i]);
        Record(Command, command); _commands++;
    });

    public void AfterCommand(Memory memory, int command) => Guard(() =>
    {
        // Only draw commands modify RDRAM. Refresh the audit baseline after
        // those writes so they can never become CPU patches in the next record.
        if (command is >= 8 and <= 15 or 0x24 or 0x25 or 0x36)
            Buffer.BlockCopy(memory.RDRAM, 0, _shadow!, 0, RamSize);
        if (command != 0x29) return;
        _frames++;
        using var payload = new MemoryStream();
        using (var writer = new BinaryWriter(payload, Encoding.UTF8, true))
        {
            writer.Write(_frames);
            foreach (uint value in memory.JournalImage) writer.Write(value);
            writer.Write(BinaryPrimitives.ReadUInt32BigEndian(memory.MI_INTR_REG_R) & 0x20u);
            foreach (byte[] component in Components(memory)) writer.Write(SHA256.HashData(component));
        }
        Record(Checkpoint, payload.ToArray());
        _frameStats.Add(new { frame = _frames, commands = _commands, patches = _patches, bytes = _bytes, unchangedBytes = _unchangedBytes });
        Console.WriteLine($"journalFrame={_frames} commands={_commands} patches={_patches} bytes={_bytes} unchangedWrittenBytes={_unchangedBytes} audit=passed");
        if (_frames < _targetFrames) return;
        using var end = new MemoryStream();
        using (var writer = new BinaryWriter(end, Encoding.UTF8, true))
        { writer.Write(_frames); writer.Write(_commands); writer.Write(_patches); writer.Write(_bytes); }
        Record(End, end.ToArray());
        _writer!.Dispose(); _writer = null;
        File.WriteAllText(Path.Combine(_directory, "manifest.json"), JsonSerializer.Serialize(new {
            format = "NRDPJ001", fromReset = _fromReset, frames = _frames, commands = _commands, patches = _patches,
            patchBytes = _bytes, unchangedWrittenBytes = _unchangedBytes, writeAudit = "passed", frameStats = _frameStats,
            scope = "Complete RDP commands, exact external write ranges and VI observations at command boundaries; not CPU-read or cycle-accurate VI replay"
        }, new JsonSerializerOptions { WriteIndented = true }));
        memory.RdpJournal = null;
        _completed = true;
    });

    private void Start()
    {
        if (_fromReset && (_memory.JournalCommandCount != 0 || _memory.JournalTmem.Any(b => b != 0)))
            throw new InvalidOperationException("Reset journal must start before the first RDP command");
        string state = Path.Combine(_directory, "start.bin");
        using (var writer = new BinaryWriter(File.Create(state))) _memory.JournalSaveStart(writer);
        _shadow = (byte[])_memory.RDRAM.Clone();
        _writer = new BinaryWriter(File.Create(Path.Combine(_directory, "journal.bin")));
        _writer.Write(Magic); _writer.Write(_fromReset ? 1u : 0u);
        _writer.Write(RamSize); _writer.Write(HiddenSize);
        _writer.Write(SHA256.HashData(File.ReadAllBytes(state)));
        _writer.Write(_shadow); _writer.Write(_memory.JournalHiddenBits);
    }

    private bool Dirty(int address) => (_dirty[address >> 6] & (1ul << (address & 63))) != 0;
    private void AuditWrites()
    {
        // SIMD page comparison keeps the strict whole-RAM audit affordable.
        for (int offset = 0; offset < RamSize; offset += 4096)
        {
            if (_shadow!.AsSpan(offset, 4096).SequenceEqual(_memory.RDRAM.AsSpan(offset, 4096))) continue;
            for (int i = offset; i < offset + 4096; i++)
                if (_shadow[i] != _memory.RDRAM[i] && !Dirty(i))
                    throw new InvalidDataException($"Untracked external RDRAM write at 0x{i:x6}, before command {_commands + 1}");
        }
    }

    private void FlushWrites()
    {
        for (int page = 0; page < _dirtyPages.Length; page++)
        {
            if (!_dirtyPages[page]) continue;
            for (int i = page * 4096; i < (page + 1) * 4096;)
            {
                if (!Dirty(i)) { i++; continue; }
                int start = i++;
                while (i < RamSize && Dirty(i)) i++;
                int length = i - start;
                byte[] payload = new byte[4 + length];
                BinaryPrimitives.WriteInt32LittleEndian(payload, start);
                _memory.RDRAM.AsSpan(start, length).CopyTo(payload.AsSpan(4));
                for (int j = start; j < i; j++) if (_shadow![j] == _memory.RDRAM[j]) _unchangedBytes++;
                payload.AsSpan(4).CopyTo(_shadow!.AsSpan(start));
                Record(Write, payload); _patches++; _bytes += (ulong)length;
                // A run may cross a page. Clear just its bits, not its neighbours.
                int at = start;
                while (at < i)
                {
                    int bit = at & 63, count = Math.Min(i - at, 64 - bit);
                    _dirty[at >> 6] &= ~((ulong.MaxValue >> (64 - count)) << bit);
                    at += count;
                }
            }
            _dirtyPages[page] = false;
        }
    }

    private void Record(byte kind, byte[] payload)
    { _writer!.Write(kind); _writer.Write(payload.Length); _writer.Write(++_sequence); _writer.Write(payload); }

    private void Guard(Action action)
    {
        if (Completed || Failure != null) return;
        try { action(); }
        catch (Exception error)
        {
            _failure = error; _memory.RdpJournal = null;
            _writer?.Dispose(); _writer = null;
            Console.Error.WriteLine($"journalFailed={error.Message}");
        }
    }

    internal void RequireComplete()
    {
        if (Failure != null) throw new InvalidDataException("Journal capture failed", Failure);
        if (!Completed) throw new InvalidDataException($"Journal reached {_frames}/{_targetFrames} FULL_SYNC checkpoints");
    }

    internal static byte[][] Components(Memory memory)
    {
        byte[] tlut = new byte[memory.JournalTlut.Length * 2];
        for (int i = 0; i < memory.JournalTlut.Length; i++) BinaryPrimitives.WriteUInt16LittleEndian(tlut.AsSpan(i * 2), memory.JournalTlut[i]);
        return new[] { memory.RDRAM, memory.JournalHiddenBits, memory.JournalTmem, tlut };
    }

    public void Dispose()
    {
        if (ReferenceEquals(_memory.RdpJournal, this)) _memory.RdpJournal = null;
        _writer?.Dispose(); _writer = null;
    }
}

internal sealed class RdpJournalReader : IDisposable
{
    private readonly BinaryReader _reader;
    internal readonly bool FromReset;
    internal readonly byte[] StateHash, Ram, Hidden;
    private ulong _sequence, _commands, _patches, _bytes;
    private int _frames;
    private bool _needCheckpoint, _atCheckpoint;
    internal bool Ended { get; private set; }

    internal RdpJournalReader(string path)
    {
        _reader = new BinaryReader(File.OpenRead(path));
        try
        {
            if (!ReadExact(8).AsSpan().SequenceEqual(RdpJournal.Magic)) throw new InvalidDataException("Invalid journal magic");
            uint flags = _reader.ReadUInt32();
            if (flags > 1 || _reader.ReadInt32() != RdpJournal.RamSize || _reader.ReadInt32() != RdpJournal.HiddenSize)
                throw new InvalidDataException("Unsupported journal header");
            FromReset = flags == 1; StateHash = ReadExact(32);
            Ram = ReadExact(RdpJournal.RamSize); Hidden = ReadExact(RdpJournal.HiddenSize);
        }
        catch { _reader.Dispose(); throw; }
    }

    private byte[] ReadExact(int length)
    {
        var bytes = _reader.ReadBytes(length);
        if (bytes.Length != length) throw new EndOfStreamException("Truncated journal");
        return bytes;
    }

    internal (byte Kind, byte[] Payload) Next()
    {
        if (Ended) throw new InvalidOperationException("Journal already ended");
        byte kind = _reader.ReadByte();
        int length = _reader.ReadInt32();
        if (length < 0 || length > RdpJournal.RamSize + 4 || _reader.ReadUInt64() != ++_sequence)
            throw new InvalidDataException("Invalid journal envelope");
        var data = ReadExact(length);
        uint U32(int at) => BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(at));
        ulong U64(int at) => BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(at));
        if (_needCheckpoint && kind != RdpJournal.Checkpoint) throw new InvalidDataException("Missing FULL_SYNC checkpoint");
        switch (kind)
        {
            case RdpJournal.Write:
                if (length <= 4 || U32(0) >= RdpJournal.RamSize || (ulong)U32(0) + (ulong)length - 4 > RdpJournal.RamSize)
                    throw new InvalidDataException("Invalid RDRAM write range");
                _patches++; _bytes += (ulong)length - 4; break;
            case RdpJournal.Command:
                if (length < 8 || length % 4 != 0) throw new InvalidDataException("Invalid command length");
                int op = (int)(U32(0) >> 24 & 63);
                int words = op is >= 8 and <= 15 ? 8 + ((op & 4) != 0 ? 16 : 0) + ((op & 2) != 0 ? 16 : 0) + ((op & 1) != 0 ? 4 : 0) : op is 0x24 or 0x25 ? 4 : 2;
                if (length != words * 4) throw new InvalidDataException("Incomplete RDP command");
                _commands++; _needCheckpoint = op == 0x29; break;
            case RdpJournal.Vi:
                if (length != 56) throw new InvalidDataException("Invalid VI observation"); break;
            case RdpJournal.Checkpoint:
                if (!_needCheckpoint || length != 152 || U32(0) != ++_frames) throw new InvalidDataException("Invalid checkpoint");
                _needCheckpoint = false; break;
            case RdpJournal.End:
                if (!_atCheckpoint || length != 28 || U32(0) != _frames || U64(4) != _commands || U64(12) != _patches || U64(20) != _bytes
                    || _reader.BaseStream.Position != _reader.BaseStream.Length)
                    throw new InvalidDataException("Invalid journal end or trailing bytes");
                Ended = true; break;
            default: throw new InvalidDataException("Unknown journal record");
        }
        _atCheckpoint = kind == RdpJournal.Checkpoint;
        return (kind, data);
    }
    public void Dispose() => _reader.Dispose();
}

internal static class RdpJournalReplay
{
    internal static void Run(string directory, bool corruptWrite = false)
    {
        using var reader = new RdpJournalReader(Path.Combine(directory, "journal.bin"));
        byte[] state = File.ReadAllBytes(Path.Combine(directory, "start.bin"));
        if (!SHA256.HashData(state).AsSpan().SequenceEqual(reader.StateHash)) throw new InvalidDataException("Wrong journal start state");
        var memory = new Memory(new byte[4096]); R4300.memory = memory;
        using (var input = new BinaryReader(new MemoryStream(state))) memory.LoadState(input);
        if (!memory.RDRAM.AsSpan().SequenceEqual(reader.Ram) || !memory.JournalHiddenBits.AsSpan().SequenceEqual(reader.Hidden))
            throw new InvalidDataException("Start memory differs from journal header");
        bool corrupted = false;
        while (!reader.Ended)
        {
            var (kind, data) = reader.Next();
            switch (kind)
            {
                case RdpJournal.Write:
                    int address = BinaryPrimitives.ReadInt32LittleEndian(data);
                    data.AsSpan(4).CopyTo(memory.RDRAM.AsSpan(address));
                    if (corruptWrite && !corrupted) { memory.RDRAM[address] ^= 1; corrupted = true; }
                    break;
                case RdpJournal.Vi:
                    var vi = memory.JournalVi;
                    for (int i = 0; i < vi.Length; i++) data.AsSpan(i * 4, 4).CopyTo(vi[i]);
                    break;
                case RdpJournal.Command:
                    var words = new uint[data.Length / 4];
                    for (int i = 0; i < words.Length; i++) words[i] = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(i * 4));
                    memory.JournalReplayCommand(words); break;
                case RdpJournal.Checkpoint:
                    int frame = BinaryPrimitives.ReadInt32LittleEndian(data);
                    for (int i = 0; i < 4; i++)
                        if (memory.JournalImage[i] != BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(4 + i * 4)))
                            throw new InvalidDataException($"Frame {frame}: image state mismatch");
                    if ((BinaryPrimitives.ReadUInt32BigEndian(memory.MI_INTR_REG_R) & 0x20u) != BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(20)))
                        throw new InvalidDataException($"Frame {frame}: DP completion mismatch");
                    var components = RdpJournal.Components(memory);
                    string[] names = { "RDRAM", "hidden", "TMEM", "TLUT" };
                    for (int i = 0; i < components.Length; i++)
                        if (!SHA256.HashData(components[i]).AsSpan().SequenceEqual(data.AsSpan(24 + i * 32, 32)))
                            throw new InvalidDataException($"Frame {frame}: {names[i]} hash mismatch");
                    Console.WriteLine($"journalReplayFrame={frame} rdram=exact hidden=exact tmem=exact tlut=exact dp=exact");
                    break;
            }
        }
        if (corruptWrite) throw new InvalidOperationException("Negative control was not detected");
        Console.WriteLine($"journalSoftwareReplay=passed fromReset={reader.FromReset}");
    }
}
#endif
