using System;
using System.IO;
using System.IO.Compression;
using EutherDrive.Core.MdTracerCore;

namespace EutherDrive.Core.Arcade.DataEast.Hshavoc;

public sealed class HshavocAdapter : IEmulatorCore, IDisposable
{
    private const string BoardModel = "Data East CG-2 / Sega Genesis-Mega Drive arcade board probe";
    private const string EvenRomName = "d-25.11a";
    private const string OddRomName = "d-26.9a";
    private const int InterleavedSize = 0x100000;
    private const int BaseDecodeEnd = 0x0E8000;

    private static readonly int[] DataBitswap =
    {
        7, 15, 6, 14, 5, 2, 1, 10, 13, 4, 12, 3, 11, 0, 8, 9
    };

    private static readonly int[] TailBitswap =
    {
        7, 15, 6, 14, 5, 2, 1, 0, 13, 4, 12, 3, 11, 10, 9, 8
    };

    private static readonly int[] Typedat =
    {
        1, 1, 1, 1, 1, 1, 1, 1, 1, 0, 0, 1, 1, 0, 1, 1
    };

    private static readonly (int Address, ushort Value)[] BestStartupPatch =
    {
        (0x0C42, 0x007C), (0x0C44, 0x0700), (0x0C46, 0x4EB9), (0x0C48, 0x0000),
        (0x0C4A, 0x109C), (0x0C4C, 0x4E71), (0x0C4E, 0x4E71), (0x0C50, 0x4E71),
        (0x0C52, 0x4E71), (0x0C54, 0x4E71), (0x0C56, 0x4E71), (0x0C58, 0x4E71),
        (0x0C5A, 0x4E71), (0x0C5C, 0x4E71), (0x0C5E, 0x4E71), (0x0C60, 0x4E71),
        (0x0C62, 0x4E71), (0x0C64, 0x4EB9), (0x0C66, 0x0000), (0x0C68, 0x10F8),
        (0x0C6A, 0x4EB9), (0x0C6C, 0x0000), (0x0C6E, 0x10F8), (0x0C70, 0x4E71),
        (0x0C72, 0x4E71), (0x0C74, 0x4E71), (0x0C76, 0x4E71), (0x0C78, 0x4E71),
        (0x0C7A, 0x4E71), (0x0C7C, 0x4E71), (0x0C7E, 0x4E71), (0x0C80, 0x4E71),
        (0x0C82, 0x4E71), (0x0C84, 0x4E71), (0x0C86, 0x4E71), (0x0C88, 0x4E71),
        (0x0C8A, 0x4E71), (0x0C8C, 0x4E71), (0x0C8E, 0x4E71), (0x0C90, 0x4E71),
        (0x0C92, 0x4E71), (0x0C94, 0x4EB9), (0x0C96, 0x0000), (0x0C98, 0x0A1C),
        (0x0C9A, 0x4EB9), (0x0C9C, 0x000D), (0x0C9E, 0x0000), (0x0CA0, 0x4EB9),
        (0x0CA2, 0x000D), (0x0CA4, 0x0682), (0x0CA6, 0x4EB9), (0x0CA8, 0x000D),
        (0x0CAA, 0x0692), (0x0CAC, 0x4EB9), (0x0CAE, 0x000D), (0x0CB0, 0x06D6),
        (0x0CB2, 0x4EF9), (0x0CB4, 0x0000), (0x0CB6, 0x1126),
        (0x065E, 0x4E71), (0x0660, 0x4E71), (0x0662, 0x4E71), (0x0664, 0x4E71),
        (0x0666, 0x4E71),
        (0xD05CA, 0x4E71), (0xD05CC, 0x4E71), (0xD05CE, 0x4E71), (0xD05D0, 0x4E71),
        (0xD05D2, 0x4E71),
        (0x0A30, 0x4E71), (0x0A32, 0x4E71), (0x0A34, 0x4E71), (0x0A36, 0x4E71)
    };

    private static readonly (int Address, ushort Value)[] OptionalPhase2OperandPatch =
    {
        (0x0C7A, 0x0E32),
        (0x0C86, 0x0AB8),
        (0x0C8C, 0x0AF8),
        (0x0C92, 0x0D32)
    };

    private readonly MdTracerAdapter _md = new();

    public RomInfo RomInfo => _md.RomInfo;

    public static bool IsSupportedArchive(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return false;
        if (!string.Equals(Path.GetExtension(path), ".zip", StringComparison.OrdinalIgnoreCase))
            return false;

        try
        {
            using ZipArchive archive = ZipFile.OpenRead(path);
            return FindEntry(archive, EvenRomName) != null && FindEntry(archive, OddRomName) != null;
        }
        catch
        {
            return false;
        }
    }

    public void LoadRom(string path)
    {
        string profile = GetDecodeProfile();
        byte[] decoded = DecodeArchive(path, profile);
        string tempPath = Path.Combine(Path.GetTempPath(), $"eutherdrive_hshavoc_{Guid.NewGuid():N}.gen");
        File.WriteAllBytes(tempPath, decoded);
        try
        {
            _md.PowerCycleAndLoadRom(tempPath);
            InstallBoardAckProbe();
            RomInfo.Summary = $"High Seas Havoc arcade probe | decode={profile} | {BoardModel}";
            RomInfo.ExtraInfo =
                "Data East hshavoc.zip via HshavocAdapter. This is not a Sega System 16 target; it runs the " +
                "Mega Drive-compatible board path with arcade-only startup/PIC probing layered on top. " +
                "Applies MAME base decode plus current startup probe patch. " +
                "No decoded ROM is kept; temp image is deleted after load.";
        }
        finally
        {
            TryDelete(tempPath);
        }
    }

    public void Reset() => _md.Reset();

    public void RunFrame() => _md.RunFrame();

    public uint GetM68kPc() => _md.GetM68kPc();

    public ushort GetZ80Pc() => _md.GetZ80Pc();

    public ushort ReadM68kWord(uint address) => _md.DebugReadM68kWord(address);

    public uint GetM68kDataRegister(int index) => _md.DebugGetM68kDataRegister(index);

    public uint GetM68kAddressRegister(int index) => _md.DebugGetM68kAddressRegister(index);

    public ushort GetM68kStatusRegister() => _md.DebugGetM68kStatusRegister();

    public bool IsVdpDisplayOn() => _md.IsVdpDisplayOn();

    public int GetVdpDisplayStatus() => _md.GetVdpDisplayStatus();

    public string CaptureDebugSnapshot(string? directory = null) => _md.CaptureDebugSnapshot(directory);

    public ReadOnlySpan<byte> GetFrameBuffer(out int width, out int height, out int stride)
        => _md.GetFrameBuffer(out width, out height, out stride);

    public ReadOnlySpan<short> GetAudioBuffer(out int sampleRate, out int channels)
        => _md.GetAudioBuffer(out sampleRate, out channels);

    public void SetInputState(
        bool up,
        bool down,
        bool left,
        bool right,
        bool a,
        bool b,
        bool c,
        bool start,
        bool x,
        bool y,
        bool z,
        bool mode,
        PadType padType)
    {
        _md.SetInputState(up, down, left, right, a, b, c, start, x, y, z, mode, padType);
    }

    public void Dispose() => _md.Dispose();

    private static void InstallBoardAckProbe()
    {
        if (md_main.g_md_bus == null)
            return;

        IM68kBusOverride? existing = md_main.g_md_bus.OverrideBus;
        if (existing is HshavocBoardBusOverride)
            return;

        md_main.g_md_bus.OverrideBus = new HshavocBoardBusOverride(existing);
    }

    private static byte[] DecodeArchive(string path, string profile)
    {
        using ZipArchive archive = ZipFile.OpenRead(path);
        byte[] even = ReadRequiredEntry(archive, EvenRomName);
        byte[] odd = ReadRequiredEntry(archive, OddRomName);
        if (even.Length != 0x80000 || odd.Length != 0x80000)
            throw new InvalidDataException($"Unexpected HSHavoc ROM sizes: {EvenRomName}=0x{even.Length:X}, {OddRomName}=0x{odd.Length:X}");

        byte[] rom = new byte[InterleavedSize];
        for (int i = 0; i < even.Length; i++)
        {
            rom[i * 2] = even[i];
            rom[i * 2 + 1] = odd[i];
        }

        DecodeBaseInPlace(rom);
        if (profile != "base")
            ApplyPatch(rom, BestStartupPatch);
        if (profile == "phase2" || profile == "island10a0")
            ApplyPatch(rom, OptionalPhase2OperandPatch);
        if (profile == "island10a0")
            ApplyIsland10A0Probe(rom);
        return rom;
    }

    private static void ApplyIsland10A0Probe(byte[] rom)
    {
        // Probe only: if the 0x10A6 clear loop is a false island, skip it and test the next startup region.
        WriteWord(rom, 0x10A6 / 2, 0x4E71);
        WriteWord(rom, 0x10A8 / 2, 0x4E71);
        WriteWord(rom, 0x10AA / 2, 0x4E71);
        WriteWord(rom, 0x10AC / 2, 0x4E71);
        WriteWord(rom, 0x10AE / 2, 0x4E71);
        WriteWord(rom, 0x10B0 / 2, 0x4E71);
        WriteWord(rom, 0x10B2 / 2, 0x4E71);
        WriteWord(rom, 0x10B4 / 2, 0x4E71);
        WriteWord(rom, 0x10B6 / 2, 0x4E71);
        WriteWord(rom, 0x10B8 / 2, 0x4E71);
    }

    private static void DecodeBaseInPlace(byte[] rom)
    {
        int wordCount = rom.Length / 2;
        for (int index = 0; index < BaseDecodeEnd / 2; index++)
        {
            ushort word = BitSwap16(ReadWord(rom, index), DataBitswap);
            word ^= Typedat[index & 0x0F] != 0 ? (ushort)0x0501 : (ushort)0x0406;
            if ((word & 0x0400) != 0)
                word ^= 0x0200;
            if (Typedat[index & 0x0F] == 0)
            {
                if ((word & 0x0100) != 0)
                    word ^= 0x0004;
                word = BitSwap16(word, new[] { 15, 14, 13, 12, 11, 9, 10, 8, 7, 6, 5, 4, 3, 2, 1, 0 });
            }
            WriteWord(rom, index, word);
        }

        for (int index = BaseDecodeEnd / 2; index < wordCount; index++)
            WriteWord(rom, index, BitSwap16(ReadWord(rom, index), TailBitswap));

        WriteWord(rom, 0, (ushort)(ReadWord(rom, 0) ^ 0x0107));
        WriteWord(rom, 1, (ushort)(ReadWord(rom, 1) ^ 0x0107));
        WriteWord(rom, 2, (ushort)(ReadWord(rom, 2) ^ 0x0107));
        WriteWord(rom, 3, (ushort)(ReadWord(rom, 3) ^ 0x0707));
    }

    private static void ApplyPatch(byte[] rom, ReadOnlySpan<(int Address, ushort Value)> patch)
    {
        foreach ((int address, ushort value) in patch)
            WriteWord(rom, address / 2, value);
    }

    private static ushort ReadWord(byte[] rom, int wordIndex)
        => (ushort)((rom[wordIndex * 2] << 8) | rom[wordIndex * 2 + 1]);

    private static void WriteWord(byte[] rom, int wordIndex, ushort value)
    {
        rom[wordIndex * 2] = (byte)(value >> 8);
        rom[wordIndex * 2 + 1] = (byte)value;
    }

    private static ushort BitSwap16(ushort value, int[] order)
    {
        int output = 0;
        for (int index = 0; index < order.Length; index++)
            output |= ((value >> order[index]) & 1) << (order.Length - 1 - index);
        return (ushort)output;
    }

    private static byte[] ReadRequiredEntry(ZipArchive archive, string name)
    {
        ZipArchiveEntry? entry = FindEntry(archive, name);
        if (entry == null)
            throw new InvalidDataException($"Missing required HSHavoc ROM entry '{name}'.");

        using Stream stream = entry.Open();
        using MemoryStream memory = new();
        stream.CopyTo(memory);
        return memory.ToArray();
    }

    private static ZipArchiveEntry? FindEntry(ZipArchive archive, string name)
    {
        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            if (string.Equals(Path.GetFileName(entry.FullName), name, StringComparison.OrdinalIgnoreCase))
                return entry;
        }
        return null;
    }

    private static string GetDecodeProfile()
    {
        string? raw = Environment.GetEnvironmentVariable("EUTHERDRIVE_HSHAVOC_DECODE_PROFILE");
        if (string.IsNullOrWhiteSpace(raw))
            return string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_HSHAVOC_PHASE2"), "1", StringComparison.Ordinal)
                ? "phase2"
                : "startup";

        string profile = raw.Trim().ToLowerInvariant();
        return profile switch
        {
            "base" => "base",
            "startup" => "startup",
            "phase2" => "phase2",
            "island10a0" => "island10a0",
            _ => throw new InvalidDataException($"Unknown HSHavoc decode profile '{raw}'. Use base, startup, phase2, or island10a0.")
        };
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch
        {
            // The decoded image is temporary research material; keep load path robust if deletion is delayed.
        }
    }
}
