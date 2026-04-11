using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using EutherDrive.Core;
using EutherDrive.Core.Savestates;

if (args.Length < 2 || args.Length > 5)
{
    Console.Error.WriteLine("Usage: PceStateProbe <rom_path> <savestate_path|-> [frames] [snapshot_frames] [start_ranges]");
    return 1;
}

string romPath = args[0];
string savestatePath = args[1];
int frames = args.Length >= 3 ? int.Parse(args[2]) : 180;
HashSet<int> snapshotFrames = args.Length >= 4 ? ParseFrameSet(args[3]) : new();
List<(int Start, int End)> startRanges = args.Length >= 5 ? ParseRanges(args[4]) : [];

var adapter = new PceCdAdapter();
adapter.LoadRom(romPath);

if (savestatePath != "-")
{
    byte[] payload = LoadSavestatePayload(savestatePath, adapter.RomIdentity);
    using var stateStream = new MemoryStream(payload, writable: false);
    using var stateReader = new BinaryReader(stateStream);
    adapter.LoadState(stateReader);
}

for (int frame = 0; frame < frames; frame++)
{
    int frameNumber = frame + 1;
    bool startPressed = IsInRanges(frameNumber, startRanges);
    adapter.SetInputState(
        up: false,
        down: false,
        left: false,
        right: false,
        a: false,
        b: false,
        c: false,
        start: startPressed,
        x: false,
        y: false,
        z: false,
        mode: false,
        padType: PadType.SixButton);

    adapter.RunFrame();
    if (snapshotFrames.Contains(frameNumber))
    {
        string snapshotPath = adapter.CaptureDebugSnapshot(Path.Combine(Path.GetTempPath(), "pce_probe_frames"));
        Console.WriteLine($"SNAPSHOT frame={frameNumber} start={(startPressed ? 1 : 0)} path={snapshotPath}");
    }

    var audio = adapter.GetAudioBuffer(out int sampleRate, out int channels);
    int peak = 0;
    int nonZero = 0;
    foreach (short sample in audio)
    {
        int abs = Math.Abs((int)sample);
        if (abs > peak)
            peak = abs;
        if (sample != 0)
            nonZero++;
    }

    Console.WriteLine($"frame={frameNumber} start={(startPressed ? 1 : 0)} audio_len={audio.Length} rate={sampleRate} ch={channels} peak={peak} nz={nonZero}");
}

return 0;

static HashSet<int> ParseFrameSet(string raw)
{
    HashSet<int> frames = [];
    foreach (string token in raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
    {
        if (!int.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out int frame) || frame <= 0)
            throw new InvalidDataException($"Invalid snapshot frame '{token}'.");
        frames.Add(frame);
    }
    return frames;
}

static List<(int Start, int End)> ParseRanges(string raw)
{
    List<(int Start, int End)> ranges = [];
    foreach (string token in raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
    {
        string[] parts = token.Split('-', 2, StringSplitOptions.TrimEntries);
        if (!int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int start) || start <= 0)
            throw new InvalidDataException($"Invalid range start '{token}'.");

        int end = start;
        if (parts.Length == 2)
        {
            if (!int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out end) || end < start)
                throw new InvalidDataException($"Invalid range end '{token}'.");
        }

        ranges.Add((start, end));
    }
    return ranges;
}

static bool IsInRanges(int frame, List<(int Start, int End)> ranges)
{
    foreach (var range in ranges)
    {
        if (frame >= range.Start && frame <= range.End)
            return true;
    }
    return false;
}

static byte[] LoadSavestatePayload(string savestatePath, RomIdentity romIdentity)
{
    const string fileMagic = "EUTHSTAT";
    const int fileVersion = 1;
    const int slotCountExpected = 3;
    const int slotHashLength = 32;

    using var stream = File.Open(savestatePath, FileMode.Open, FileAccess.Read, FileShare.Read);
    using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: false);

    string magic = Encoding.ASCII.GetString(reader.ReadBytes(fileMagic.Length));
    if (!string.Equals(magic, fileMagic, StringComparison.Ordinal))
        throw new InvalidDataException("Savestate magic mismatch.");

    int version = reader.ReadInt32();
    if (version != fileVersion)
        throw new InvalidDataException($"Savestate version mismatch: {version}.");

    int slotCount = reader.ReadInt32();
    if (slotCount != slotCountExpected)
        throw new InvalidDataException($"Savestate slot count mismatch: {slotCount}.");

    byte[] fileRomHash = reader.ReadBytes(romIdentity.Hash.Length);
    if (!fileRomHash.SequenceEqual(romIdentity.Hash))
        throw new InvalidDataException("Savestate ROM hash mismatch.");

    int nameLength = reader.ReadInt32();
    if (nameLength > 0)
        reader.ReadBytes(nameLength);

    for (int i = 0; i < slotCount; i++)
    {
        int slotIndex = reader.ReadInt32();
        bool hasData = reader.ReadByte() != 0;
        reader.ReadInt64();
        reader.ReadInt64();
        int payloadLength = reader.ReadInt32();
        long payloadOffset = reader.ReadInt64();
        byte[] hash = reader.ReadBytes(slotHashLength);

        if (slotIndex != 1 || !hasData || payloadLength <= 0)
            continue;

        if (payloadOffset < 0 || payloadOffset + payloadLength > stream.Length)
            continue;

        stream.Seek(payloadOffset, SeekOrigin.Begin);
        byte[] payload = reader.ReadBytes(payloadLength);
        byte[] checksum = SHA256.HashData(payload);
        if (!checksum.SequenceEqual(hash))
            continue;

        return payload;
    }

    throw new InvalidDataException("No valid slot 1 payload found.");
}
