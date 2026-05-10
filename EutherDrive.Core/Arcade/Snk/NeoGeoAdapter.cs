using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using EutherDrive.Core.Savestates;
using SharpCompress.Archives;

namespace EutherDrive.Core.Arcade.Snk;

public sealed class NeoGeoAdapter : IEmulatorCore, ISavestateCapable, IDisposable
{
    private const string BiosArchiveName = "neogeo.zip";
    private const int PlaceholderWidth = 320;
    private const int PlaceholderHeight = 224;
    private const int PlaceholderStride = PlaceholderWidth * 4;
    private const uint RomGroupWord = 0x100;
    private static readonly object RomSetDatabaseLock = new();
    private static readonly object DynamicMcsDriverLock = new();
    private static readonly HashSet<string> s_installedDynamicMcsDrivers = new(StringComparer.OrdinalIgnoreCase);
    private static Dictionary<string, NeoGeoSoftwareRomSet>? s_softwareRomSets;
    private static Dictionary<string, NeoGeoRomSetInfo>? s_romSetDatabase;
    private readonly McsArcadeAdapter _mcs = new();
    private byte[] _placeholderFrame = Array.Empty<byte>();
    private string? _preparedDirectory;
    private string? _loadedRomPath;
    private string? _loadedDriverName;
    private string? _loadedBiosPath;

    public static string? BiosPath { get; set; }

    public RomInfo RomInfo { get; } = new()
    {
        Summary = "Neo Geo adapter idle",
        RegionHint = ConsoleRegion.Auto
    };

    public RomIdentity? RomIdentity => _mcs.RomIdentity;
    public long? FrameCounter => _mcs.FrameCounter;

    public static string? DefaultMameRoot => ResolveHomePath("mame");
    public static string? DefaultMameRomDirectory => ResolveHomePath(Path.Combine("mame", "roms"));
    public static string? DefaultMameNeoGeoHashPath => ResolveHomePath(Path.Combine("mame", "hash", "neogeo.xml"));
    public static string? DefaultMameNeoGeoSourcePath => ResolveHomePath(Path.Combine("mame", "src", "mame", "neogeo", "neogeo.cpp"));

    public NeoGeoAdapter()
    {
        _mcs.SetOutputGainPercent(200);
    }

    public static bool IsSupportedArchive(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !RomArchiveExtractor.IsArchivePath(path))
            return false;

        string driverName = GetDriverName(path);
        if (driverName.Equals("neogeo", StringComparison.OrdinalIgnoreCase))
            return false;

        if (TryGetRomSetInfo(driverName, out _))
            return true;

        return LooksLikeNeoGeoArchive(path);
    }

    public static bool TryGetRomSetMetadata(string path, out NeoGeoRomSetInfo info)
    {
        info = default;
        if (string.IsNullOrWhiteSpace(path) || !RomArchiveExtractor.IsArchivePath(path))
            return false;

        string driverName = GetDriverName(path);
        if (driverName.Equals("neogeo", StringComparison.OrdinalIgnoreCase))
            return false;

        if (TryGetRomSetInfo(driverName, out info))
            return true;

        if (!LooksLikeNeoGeoArchive(path))
            return false;

        info = new NeoGeoRomSetInfo(driverName, driverName, null, "zip");
        return true;
    }

    public static IReadOnlyList<string> FindLocalMameRomDirectories()
    {
        var paths = new List<string>();
        AddExistingDirectory(paths, DefaultMameRomDirectory);

        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        AddNeoGeoDirectoriesUnder(paths, Path.Combine(home, "roms", "MAME"));
        AddNeoGeoDirectoriesUnder(paths, Path.Combine(home, "ROMs", "MAME"));
        AddNeoGeoDirectoriesUnder(paths, Path.Combine(home, "mame", "roms"));

        return paths;
    }

    public void LoadRom(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("Neo Geo ROM path is empty.", nameof(path));
        if (!File.Exists(path))
            throw new FileNotFoundException("Neo Geo ROM archive not found.", path);
        if (!IsSupportedArchive(path))
            throw new NotSupportedException($"'{Path.GetFileName(path)}' is not recognized as a Neo Geo MAME set.");

        _mcs.Dispose();
        DisposePreparedDirectory();
        DrawPlaceholderFrame();

        string driverName = GetDriverName(path);
        string biosPath = ResolveBiosPath(path)
            ?? throw new FileNotFoundException(
                $"Neo Geo BIOS archive '{BiosArchiveName}' was not found. Select it in the UI or place it beside the ROM set / in ~/mame/roms.");

        ValidateBiosArchive(biosPath);
        EnsureDynamicMcsDriver(driverName);

        string loadPath = PrepareMcsLoadPath(path, biosPath);
        if (!McsArcadeAdapter.IsDriverAvailableForArchive(loadPath))
        {
            throw new NotSupportedException(
                $"Neo Geo set '{driverName}' was recognized, but the bundled MCS/MAME snapshot does not expose that driver yet. " +
                "The UI and ROM/BIOs handoff are ready; the remaining work is adding the BSD-3-Clause Neo Geo driver path to MCS.");
        }

        _mcs.LoadRom(loadPath);
        _loadedRomPath = Path.GetFullPath(path);
        _loadedDriverName = driverName;
        _loadedBiosPath = biosPath;
        UpdateRomInfo();
    }

    public void Reset() => _mcs.Reset();

    public void RunFrame() => _mcs.RunFrame();

    public ReadOnlySpan<byte> GetFrameBuffer(out int width, out int height, out int stride)
    {
        ReadOnlySpan<byte> frame = _mcs.GetFrameBuffer(out width, out height, out stride);
        if (!frame.IsEmpty)
            return frame;

        width = PlaceholderWidth;
        height = PlaceholderHeight;
        stride = PlaceholderStride;
        return _placeholderFrame;
    }

    public ReadOnlySpan<short> GetAudioBuffer(out int sampleRate, out int channels)
        => _mcs.GetAudioBuffer(out sampleRate, out channels);

    public void SetMasterVolumePercent(int percent) => _mcs.SetMasterVolumePercent(percent);

    public double GetTargetFps() => 59.185606;

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
        _mcs.SetInputState(up, down, left, right, a, b, c, start, x, y, z, mode, padType);
    }

    public void SaveState(BinaryWriter writer) => _mcs.SaveState(writer);

    public void LoadState(BinaryReader reader) => _mcs.LoadState(reader);

    public void Dispose()
    {
        _mcs.Dispose();
        DisposePreparedDirectory();
    }

    public static string? ResolveBiosPath(string? romPath = null)
    {
        foreach (string? candidate in EnumerateBiosCandidates(romPath))
        {
            if (string.IsNullOrWhiteSpace(candidate))
                continue;

            string path = ExpandHome(candidate);
            if (Directory.Exists(path))
                path = Path.Combine(path, BiosArchiveName);

            if (File.Exists(path))
                return Path.GetFullPath(path);
        }

        return null;
    }

    private static IEnumerable<string?> EnumerateBiosCandidates(string? romPath)
    {
        yield return BiosPath;

        if (!string.IsNullOrWhiteSpace(romPath))
        {
            string? romDirectory = Path.GetDirectoryName(Path.GetFullPath(romPath));
            if (!string.IsNullOrWhiteSpace(romDirectory))
                yield return Path.Combine(romDirectory, BiosArchiveName);
        }

        string? mameRoms = DefaultMameRomDirectory;
        if (!string.IsNullOrWhiteSpace(mameRoms))
            yield return Path.Combine(mameRoms, BiosArchiveName);

        yield return Path.Combine(Directory.GetCurrentDirectory(), "bios", BiosArchiveName);
        yield return Path.Combine(Directory.GetCurrentDirectory(), BiosArchiveName);
    }

    private static bool LooksLikeNeoGeoArchive(string path)
    {
        try
        {
            using IArchive archive = RomArchiveExtractor.OpenArchive(path);
            bool hasProgram = false;
            bool hasSprites = false;
            bool hasFixed = false;
            bool hasAudioCpu = false;
            foreach (IArchiveEntry entry in archive.Entries)
            {
                if (entry.IsDirectory)
                    continue;

                string name = Path.GetFileName(entry.Key).ToLowerInvariant();
                hasProgram |= name.EndsWith(".p1", StringComparison.Ordinal) || name.Contains("-p1.", StringComparison.Ordinal);
                hasSprites |= name.EndsWith(".c1", StringComparison.Ordinal) || name.Contains("-c1.", StringComparison.Ordinal);
                hasFixed |= name.EndsWith(".s1", StringComparison.Ordinal) || name.Contains("-s1.", StringComparison.Ordinal);
                hasAudioCpu |= name.EndsWith(".m1", StringComparison.Ordinal) || name.Contains("-m1.", StringComparison.Ordinal);
            }

            return hasProgram && hasSprites && (hasFixed || hasAudioCpu);
        }
        catch
        {
            return false;
        }
    }

    private static void ValidateBiosArchive(string path)
    {
        try
        {
            using IArchive archive = RomArchiveExtractor.OpenArchive(path);
            var names = new HashSet<string>(
                archive.Entries
                    .Where(static entry => !entry.IsDirectory)
                    .Select(static entry => Path.GetFileName(entry.Key).ToLowerInvariant()),
                StringComparer.OrdinalIgnoreCase);

            if (names.Contains("000-lo.lo") ||
                names.Contains("sfix.sfix") ||
                names.Contains("sm1.sm1") ||
                names.Contains("sp-s2.sp1") ||
                names.Contains("uni-bios_4_0.rom"))
            {
                return;
            }
        }
        catch (Exception ex)
        {
            throw new InvalidDataException($"Neo Geo BIOS archive could not be opened: {ex.Message}", ex);
        }

        throw new InvalidDataException($"'{Path.GetFileName(path)}' does not look like a Neo Geo BIOS archive.");
    }

    private string PrepareMcsLoadPath(string romPath, string biosPath)
    {
        string romFullPath = Path.GetFullPath(romPath);
        string biosFullPath = Path.GetFullPath(biosPath);
        string? romDirectory = Path.GetDirectoryName(romFullPath);
        string? biosDirectory = Path.GetDirectoryName(biosFullPath);

        if (!string.IsNullOrWhiteSpace(romDirectory) &&
            string.Equals(romDirectory, biosDirectory, StringComparison.OrdinalIgnoreCase))
        {
            return romFullPath;
        }

        string tempRoot = Path.Combine(Path.GetTempPath(), "EutherDrive", "neogeo", BuildStableDirectoryName(romFullPath, biosFullPath));
        Directory.CreateDirectory(tempRoot);
        _preparedDirectory = tempRoot;

        string preparedRomPath = Path.Combine(tempRoot, Path.GetFileName(romFullPath));
        string preparedBiosPath = Path.Combine(tempRoot, BiosArchiveName);
        CopyIfChanged(romFullPath, preparedRomPath);
        CopyIfChanged(biosFullPath, preparedBiosPath);
        return preparedRomPath;
    }

    private static void CopyIfChanged(string source, string destination)
    {
        if (File.Exists(destination))
        {
            var sourceInfo = new FileInfo(source);
            var destinationInfo = new FileInfo(destination);
            if (sourceInfo.Length == destinationInfo.Length && sourceInfo.LastWriteTimeUtc <= destinationInfo.LastWriteTimeUtc)
                return;
        }

        File.Copy(source, destination, overwrite: true);
    }

    private static string BuildStableDirectoryName(string romPath, string biosPath)
    {
        byte[] bytes = System.Text.Encoding.UTF8.GetBytes(romPath + "\n" + biosPath);
        byte[] hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash, 0, 8).ToLowerInvariant();
    }

    private void UpdateRomInfo()
    {
        string driverName = _loadedDriverName ?? "neogeo";
        string title = TryGetSoftwareDescription(driverName) ?? driverName;
        RomInfo.Summary = $"Neo Geo: {title}";
        RomInfo.ExtraInfo =
            $"MAME set: {driverName}\n" +
            $"BIOS: {_loadedBiosPath ?? "(auto)"}\n" +
            "ROM definitions are read from the local ~/mame hash/source tree when available. No MAME source is copied into this adapter.";
        RomInfo.RegionHint = ConsoleRegion.Auto;
    }

    private static string? TryGetSoftwareDescription(string driverName)
        => TryGetRomSetInfo(driverName, out NeoGeoRomSetInfo info) ? info.Description : null;

    private static bool TryGetRomSetInfo(string driverName, out NeoGeoRomSetInfo info)
        => GetRomSetDatabase().TryGetValue(driverName, out info);

    private static bool EnsureDynamicMcsDriver(string driverName)
    {
        if (string.IsNullOrWhiteSpace(driverName))
            return false;

        lock (DynamicMcsDriverLock)
        {
            McsArcadeAdapter.EnsureMcsInitialized();
            if (mame.driver_list.find(driverName) >= 0)
                return true;
            if (s_installedDynamicMcsDrivers.Contains(driverName))
                return true;

            bool installed = InstallDynamicMcsDriver(driverName, GetSoftwareRomSets(), new HashSet<string>(StringComparer.OrdinalIgnoreCase));
            if (installed)
                McsDriverCatalog.Invalidate();
            return installed;
        }
    }

    private static bool InstallDynamicMcsDriver(string driverName, IReadOnlyDictionary<string, NeoGeoSoftwareRomSet> sets, HashSet<string> visiting)
    {
        if (mame.driver_list.find(driverName) >= 0)
            return true;
        if (s_installedDynamicMcsDrivers.Contains(driverName))
            return true;
        if (!visiting.Add(driverName))
            return false;
        if (!sets.TryGetValue(driverName, out NeoGeoSoftwareRomSet? set))
            return false;

        if (!string.IsNullOrWhiteSpace(set.CloneOf))
            InstallDynamicMcsDriver(set.CloneOf, sets, visiting);

        mame.tiny_rom_entry[]? entries = BuildMcsRomEntries(set);
        if (entries == null || entries.Length == 0)
            return false;

        string parent = !string.IsNullOrWhiteSpace(set.CloneOf) ? set.CloneOf : "neogeo";
        bool installed = mame.neogeo.install_dynamic_driver(set.Name, parent, set.Year, set.Publisher, set.Description, entries);
        if (installed || mame.driver_list.find(set.Name) >= 0)
        {
            s_installedDynamicMcsDrivers.Add(set.Name);
            return true;
        }

        return false;
    }

    private static mame.tiny_rom_entry[]? BuildMcsRomEntries(NeoGeoSoftwareRomSet set)
    {
        var entries = new List<mame.tiny_rom_entry>
        {
            mame.romentry_global.ROM_REGION(0x80000, "mainbios", 0),
            RomLoad16WordSwap("sp-s2.sp1", 0x00000, 0x020000, Hash("9036d879", "4f5ed7105b7128794654ce82b51723e16e389543")),
            mame.romentry_global.ROM_REGION(0x20000, "spritegen:zoomy", 0),
            mame.romentry_global.ROM_LOAD("000-lo.lo", 0x00000, 0x20000, Hash("5a86cff2", "5992277debadeb64d1c1c64b0a92d9293eaf7e4a")),
            mame.romentry_global.ROM_REGION(0x20000, "fixedbios", 0),
            mame.romentry_global.ROM_LOAD("sfix.sfix", 0x000000, 0x20000, Hash("c2ea0cfd", "fd4a618cdcdbf849374f0a50dd8efe9dbab706c3"))
        };

        foreach (NeoGeoDataArea dataArea in set.DataAreas)
        {
            entries.Add(mame.romentry_global.ROM_REGION(dataArea.Size, dataArea.Name, 0));
            foreach (NeoGeoRomFile rom in dataArea.Roms)
            {
                mame.tiny_rom_entry? entry = BuildMcsRomEntry(rom);
                if (entry == null)
                    return null;
                entries.Add(entry);
            }
        }

        entries.Add(mame.romentry_global.ROM_END);
        return entries.ToArray();
    }

    private static mame.tiny_rom_entry? BuildMcsRomEntry(NeoGeoRomFile rom)
    {
        string loadFlag = rom.LoadFlag ?? string.Empty;
        return loadFlag switch
        {
            "" => !string.IsNullOrWhiteSpace(rom.Name) ? mame.romentry_global.ROM_LOAD(rom.Name, rom.Offset, rom.Size, Hash(rom.Crc, rom.Sha1)) : null,
            "load16_byte" => !string.IsNullOrWhiteSpace(rom.Name) ? mame.romentry_global.ROM_LOAD16_BYTE(rom.Name, rom.Offset, rom.Size, Hash(rom.Crc, rom.Sha1)) : null,
            "load16_word_swap" => !string.IsNullOrWhiteSpace(rom.Name) ? RomLoad16WordSwap(rom.Name, rom.Offset, rom.Size, Hash(rom.Crc, rom.Sha1)) : null,
            "load32_byte" => !string.IsNullOrWhiteSpace(rom.Name) ? mame.romentry_global.ROMX_LOAD(rom.Name, rom.Offset, rom.Size, Hash(rom.Crc, rom.Sha1), mame.romentry_global.ROM_SKIP(3)) : null,
            "load32_word_swap" => !string.IsNullOrWhiteSpace(rom.Name) ? mame.romentry_global.ROMX_LOAD(rom.Name, rom.Offset, rom.Size, Hash(rom.Crc, rom.Sha1), RomGroupWord | mame.romentry_global.ROM_REVERSE | mame.romentry_global.ROM_SKIP(2)) : null,
            "continue" => mame.romentry_global.ROM_CONTINUE(rom.Offset, rom.Size),
            "fill" => mame.romentry_global.ROM_FILL(rom.Offset, rom.Size, rom.FillValue),
            "ignore" => new mame.tiny_rom_entry(null, null, 0, rom.Size, mame.romentry_global.ROMENTRYTYPE_IGNORE | mame.romentry_global.ROM_INHERITFLAGS),
            _ => null
        };
    }

    private static mame.tiny_rom_entry RomLoad16WordSwap(string name, uint offset, uint size, string hash)
        => mame.romentry_global.ROMX_LOAD(name, offset, size, hash, RomGroupWord | mame.romentry_global.ROM_REVERSE);

    private static string Hash(string? crc, string? sha1)
    {
        string hash = string.Empty;
        if (!string.IsNullOrWhiteSpace(crc))
            hash += mame.hash_global.CRC(crc);
        if (!string.IsNullOrWhiteSpace(sha1))
            hash += mame.hash_global.SHA1(sha1);
        return hash;
    }

    private static IReadOnlyDictionary<string, NeoGeoSoftwareRomSet> GetSoftwareRomSets()
    {
        lock (RomSetDatabaseLock)
        {
            if (s_softwareRomSets != null)
                return s_softwareRomSets;

            s_softwareRomSets = LoadSoftwareRomSets();
            return s_softwareRomSets;
        }
    }

    private static Dictionary<string, NeoGeoSoftwareRomSet> LoadSoftwareRomSets()
    {
        var result = new Dictionary<string, NeoGeoSoftwareRomSet>(StringComparer.OrdinalIgnoreCase);
        string? hashPath = DefaultMameNeoGeoHashPath;
        if (string.IsNullOrWhiteSpace(hashPath) || !File.Exists(hashPath))
            return result;

        try
        {
            using XmlReader reader = CreateXmlReader(hashPath);
            XDocument document = XDocument.Load(reader);
            foreach (XElement software in document.Root?.Elements("software") ?? Enumerable.Empty<XElement>())
            {
                NeoGeoSoftwareRomSet? set = ParseSoftwareRomSet(software);
                if (set != null)
                    result[set.Name] = set;
            }
        }
        catch
        {
        }

        return result;
    }

    private static NeoGeoSoftwareRomSet? ParseSoftwareRomSet(XElement software)
    {
        string? name = (string?)software.Attribute("name");
        if (string.IsNullOrWhiteSpace(name))
            return null;

        XElement? cart = software.Elements("part")
            .FirstOrDefault(static part => string.Equals((string?)part.Attribute("interface"), "neo_cart", StringComparison.OrdinalIgnoreCase));
        if (cart == null)
            return null;

        var dataAreas = new List<NeoGeoDataArea>();
        foreach (XElement dataAreaElement in cart.Elements("dataarea"))
        {
            string? areaName = (string?)dataAreaElement.Attribute("name");
            if (string.IsNullOrWhiteSpace(areaName) || !IsSupportedDynamicDataArea(areaName))
                continue;
            if (!TryParseUInt((string?)dataAreaElement.Attribute("size"), out uint areaSize))
                continue;

            var roms = new List<NeoGeoRomFile>();
            foreach (XElement romElement in dataAreaElement.Elements("rom"))
            {
                if (!TryParseUInt((string?)romElement.Attribute("offset"), out uint offset) ||
                    !TryParseUInt((string?)romElement.Attribute("size"), out uint size))
                {
                    continue;
                }

                string loadFlag = ((string?)romElement.Attribute("loadflag") ?? string.Empty).Trim().ToLowerInvariant();
                byte fillValue = 0;
                if (loadFlag == "fill")
                    TryParseByte((string?)romElement.Attribute("value"), out fillValue);

                roms.Add(new NeoGeoRomFile(
                    (string?)romElement.Attribute("name"),
                    loadFlag,
                    offset,
                    size,
                    (string?)romElement.Attribute("crc"),
                    (string?)romElement.Attribute("sha1"),
                    fillValue));
            }

            if (roms.Count > 0)
                dataAreas.Add(new NeoGeoDataArea(areaName, areaSize, roms));
        }

        if (dataAreas.Count == 0)
            return null;

        string description = software.Element("description")?.Value.Trim() ?? name.Trim();
        string year = software.Element("year")?.Value.Trim() ?? "????";
        string publisher = software.Element("publisher")?.Value.Trim() ?? "SNK";
        string? cloneOf = (string?)software.Attribute("cloneof");
        return new NeoGeoSoftwareRomSet(
            name.Trim(),
            string.IsNullOrWhiteSpace(description) ? name.Trim() : description,
            string.IsNullOrWhiteSpace(cloneOf) ? null : cloneOf.Trim(),
            string.IsNullOrWhiteSpace(year) ? "????" : year,
            string.IsNullOrWhiteSpace(publisher) ? "SNK" : publisher,
            dataAreas);
    }

    private static bool IsSupportedDynamicDataArea(string areaName)
        => areaName is "maincpu" or "fixed" or "audiocpu" or "ymsnd:adpcma" or "ymsnd:adpcmb" or "sprites";

    private static bool TryParseUInt(string? raw, out uint value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(raw))
            return false;
        raw = raw.Trim();
        return raw.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? uint.TryParse(raw[2..], System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out value)
            : uint.TryParse(raw, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out value);
    }

    private static bool TryParseByte(string? raw, out byte value)
    {
        value = 0;
        return TryParseUInt(raw, out uint parsed) && parsed <= byte.MaxValue && (value = (byte)parsed) == parsed;
    }

    private static Dictionary<string, NeoGeoRomSetInfo> GetRomSetDatabase()
    {
        lock (RomSetDatabaseLock)
        {
            if (s_romSetDatabase != null)
                return s_romSetDatabase;

            var sets = new Dictionary<string, NeoGeoRomSetInfo>(StringComparer.OrdinalIgnoreCase);
            foreach (string name in FallbackSoftwareNames)
                AddRomSetInfo(sets, new NeoGeoRomSetInfo(name, name, null, "fallback"));

            LoadHashRomSets(sets);
            LoadSourceRomSets(sets);

            s_romSetDatabase = sets;
            return s_romSetDatabase;
        }
    }

    private static void LoadHashRomSets(Dictionary<string, NeoGeoRomSetInfo> sets)
    {
        string? hashPath = DefaultMameNeoGeoHashPath;
        if (string.IsNullOrWhiteSpace(hashPath) || !File.Exists(hashPath))
            return;

        try
        {
            using XmlReader reader = CreateXmlReader(hashPath);
            XDocument document = XDocument.Load(reader);
            foreach (XElement software in document.Root?.Elements("software") ?? Enumerable.Empty<XElement>())
            {
                string? name = (string?)software.Attribute("name");
                if (string.IsNullOrWhiteSpace(name))
                    continue;

                string? description = software.Element("description")?.Value;
                string? cloneOf = (string?)software.Attribute("cloneof");
                AddRomSetInfo(sets, new NeoGeoRomSetInfo(
                    name.Trim(),
                    string.IsNullOrWhiteSpace(description) ? name.Trim() : description.Trim(),
                    string.IsNullOrWhiteSpace(cloneOf) ? null : cloneOf.Trim(),
                    "hash"));
            }
        }
        catch
        {
        }
    }

    private static void LoadSourceRomSets(Dictionary<string, NeoGeoRomSetInfo> sets)
    {
        string? sourcePath = DefaultMameNeoGeoSourcePath;
        if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
            return;

        try
        {
            foreach (string line in File.ReadLines(sourcePath))
            {
                Match romStart = Regex.Match(line, @"ROM_START\(\s*(\w+)\s*\)");
                if (romStart.Success)
                {
                    string romStartName = romStart.Groups[1].Value;
                    if (!sets.ContainsKey(romStartName))
                        AddRomSetInfo(sets, new NeoGeoRomSetInfo(romStartName, romStartName, null, "source"));
                    continue;
                }

                int gameIndex = line.IndexOf("GAME(", StringComparison.Ordinal);
                if (gameIndex < 0)
                    continue;

                IReadOnlyList<string> args = SplitMacroArguments(line[(gameIndex + "GAME(".Length)..]);
                if (args.Count < 10)
                    continue;

                string name = args[1].Trim();
                string parent = args[2].Trim();
                string description = TrimMacroString(args[9]);
                if (string.IsNullOrWhiteSpace(name))
                    continue;

                AddRomSetInfo(sets, new NeoGeoRomSetInfo(
                    name,
                    string.IsNullOrWhiteSpace(description) ? name : description,
                    parent == "0" || string.Equals(parent, "neogeo", StringComparison.OrdinalIgnoreCase) ? null : parent,
                    "source"));
            }
        }
        catch
        {
        }
    }

    private static XmlReader CreateXmlReader(string path)
    {
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Ignore,
            XmlResolver = null
        };
        return XmlReader.Create(path, settings);
    }

    private static void AddRomSetInfo(Dictionary<string, NeoGeoRomSetInfo> sets, NeoGeoRomSetInfo info)
    {
        if (string.IsNullOrWhiteSpace(info.Name))
            return;

        if (!sets.TryGetValue(info.Name, out NeoGeoRomSetInfo existing) ||
            existing.Source == "fallback" ||
            (existing.Source == "source" && info.Source == "hash"))
        {
            sets[info.Name] = info;
        }
    }

    private static IReadOnlyList<string> SplitMacroArguments(string text)
    {
        var args = new List<string>();
        int depth = 0;
        bool inString = false;
        int start = 0;

        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '"' && (i == 0 || text[i - 1] != '\\'))
            {
                inString = !inString;
                continue;
            }

            if (inString)
                continue;

            if (c == '(')
            {
                depth++;
            }
            else if (c == ')')
            {
                if (depth == 0)
                {
                    args.Add(text[start..i].Trim());
                    break;
                }

                depth--;
            }
            else if (c == ',' && depth == 0)
            {
                args.Add(text[start..i].Trim());
                start = i + 1;
            }
        }

        return args;
    }

    private static string TrimMacroString(string value)
    {
        value = value.Trim();
        if (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
            return value[1..^1];
        return value;
    }

    private static void AddExistingDirectory(List<string> paths, string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
            return;

        string fullPath = Path.GetFullPath(path);
        if (!paths.Any(existing => string.Equals(existing, fullPath, StringComparison.OrdinalIgnoreCase)))
            paths.Add(fullPath);
    }

    private static void AddNeoGeoDirectoriesUnder(List<string> paths, string root)
    {
        if (!Directory.Exists(root))
            return;

        try
        {
            foreach (string directory in Directory.EnumerateDirectories(root))
            {
                string normalized = Regex.Replace(Path.GetFileName(directory), @"[^a-z0-9]", "", RegexOptions.IgnoreCase);
                if (normalized.Equals("neogeo", StringComparison.OrdinalIgnoreCase))
                    AddExistingDirectory(paths, directory);
            }
        }
        catch
        {
        }
    }

    private static string GetDriverName(string path)
        => Path.GetFileNameWithoutExtension(path).Trim().ToLowerInvariant();

    private static string? ResolveHomePath(string relativePath)
    {
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return string.IsNullOrWhiteSpace(home) ? null : Path.Combine(home, relativePath);
    }

    private static string ExpandHome(string path)
    {
        if (path == "~")
            return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (path.StartsWith("~/", StringComparison.Ordinal))
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), path[2..]);
        return path;
    }

    private void DrawPlaceholderFrame()
    {
        if (_placeholderFrame.Length != PlaceholderHeight * PlaceholderStride)
            _placeholderFrame = new byte[PlaceholderHeight * PlaceholderStride];

        for (int y = 0; y < PlaceholderHeight; y++)
        {
            for (int x = 0; x < PlaceholderWidth; x++)
            {
                int index = y * PlaceholderStride + x * 4;
                byte r = (byte)(24 + ((x / 16) & 1) * 24);
                byte g = (byte)(20 + ((y / 16) & 1) * 18);
                byte b = (byte)(28 + (((x + y) / 24) & 1) * 30);
                _placeholderFrame[index + 0] = b;
                _placeholderFrame[index + 1] = g;
                _placeholderFrame[index + 2] = r;
                _placeholderFrame[index + 3] = 0xFF;
            }
        }
    }

    private void DisposePreparedDirectory()
    {
        string? directory = _preparedDirectory;
        _preparedDirectory = null;
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
            return;

        try
        {
            Directory.Delete(directory, recursive: true);
        }
        catch
        {
        }
    }

    private static readonly string[] FallbackSoftwareNames =
    [
        "nam1975", "bstars", "tpgolf", "mahretsu", "maglord", "ridhero", "alpham2", "ncombat",
        "cyberlip", "superspy", "mutnat", "kotm", "sengoku", "burningf", "lbowling", "gpilots",
        "joyjoy", "bjourney", "quizdais", "lresort", "eightman", "minasan", "legendos", "2020bb",
        "socbrawl", "roboarmy", "fatfury1", "fbfrenzy", "bakatono", "crsword", "trally", "kotm2",
        "sengoku2", "bstars2", "quizdai2", "3countb", "aof", "samsho", "tophuntr", "fatfury2",
        "janshin", "androdun", "ncommand", "viewpoin", "ssideki", "wh1", "kof94", "aof2",
        "wh2", "fatfursp", "savagere", "fightfev", "ssideki2", "spinmast", "samsho2", "wh2j",
        "wjammers", "karnovr", "gururin", "pspikes2", "fatfury3", "zupapa", "panicbom", "aodk",
        "sonicwi2", "zedblade", "galaxyfg", "strhoop", "quizkof", "ssideki3", "doubledr", "pbobblen",
        "kof95", "tws96", "samsho3", "stakwin", "pulstar", "whp", "kabukikl", "neobombe",
        "gowcaizr", "rbff1", "aof3", "sonicwi3", "turfmast", "mslug", "puzzledp", "mosyougi",
        "marukodq", "neomrdo", "sdodgeb", "goalx3", "overtop", "neodrift", "kof96", "ssideki4",
        "kizuna", "ninjamas", "ragnagrd", "pgoal", "magdrop2", "samsho4", "rbffspec", "twinspri",
        "wakuwak7", "stakwin2", "ghostlop", "breakers", "miexchng", "kof97", "magdrop3", "lastblad",
        "puzzldpr", "irrmaze", "popbounc", "shocktro", "blazstar", "rbff2", "mslug2", "kof98",
        "lastbld2", "neocup98", "breakrev", "shocktr2", "flipshot", "pbobbl2n", "ctomaday", "mslugx",
        "kof99", "garou", "s1945p", "preisle2", "mslug3", "kof2000", "bangbead", "nitd",
        "zupapa", "sengoku3", "kof2001", "mslug4", "rotd", "kof2002", "matrim", "svc",
        "samsho5", "samsh5sp", "mslug5", "kof2003"
    ];
}

public readonly record struct NeoGeoRomSetInfo(string Name, string Description, string? CloneOf, string Source);

internal sealed record NeoGeoSoftwareRomSet(
    string Name,
    string Description,
    string? CloneOf,
    string Year,
    string Publisher,
    IReadOnlyList<NeoGeoDataArea> DataAreas);

internal sealed record NeoGeoDataArea(
    string Name,
    uint Size,
    IReadOnlyList<NeoGeoRomFile> Roms);

internal sealed record NeoGeoRomFile(
    string? Name,
    string LoadFlag,
    uint Offset,
    uint Size,
    string? Crc,
    string? Sha1,
    byte FillValue);
