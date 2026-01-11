using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace EutherDrive.Core.Savestates;

public sealed class SavestateService
{
    private const string FileMagic = "EUTHSTAT";
    private const int FileVersion = 1;
    private const int SlotCount = 3;
    private const int SlotHashLength = 32;

    private readonly string _rootDirectory;

    public SavestateService(string? rootDirectory = null)
    {
        _rootDirectory = string.IsNullOrWhiteSpace(rootDirectory)
            ? Path.Combine(Directory.GetCurrentDirectory(), "savestates")
            : rootDirectory;
    }

    public string RootDirectory => _rootDirectory;

    public SavestateSlotInfo[] GetSlotInfo(ISavestateCapable core)
    {
        if (core == null)
            throw new ArgumentNullException(nameof(core));
        if (core.RomIdentity == null)
            return BuildEmptySlots("ROM not loaded.");
        return GetSlotInfo(core.RomIdentity);
    }

    public SavestateSlotInfo[] GetSlotInfo(RomIdentity romId)
    {
        try
        {
            var file = ReadFile(romId);
            var results = new SavestateSlotInfo[SlotCount];
            for (int i = 0; i < SlotCount; i++)
            {
                var slot = file.Slots[i];
                results[i] = new SavestateSlotInfo(
                    slot.SlotIndex,
                    slot.HasData,
                    slot.IsCorrupt,
                    slot.SavedAt,
                    slot.FrameCounter,
                    slot.PayloadLength,
                    slot.Error);
            }
            return results;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Savestate] Slot info read failed: {ex.Message}");
            return BuildEmptySlots("Savestate file unreadable.");
        }
    }

    public void Save(ISavestateCapable core, int slotIndex)
    {
        if (core == null)
            throw new ArgumentNullException(nameof(core));
        if (core.RomIdentity == null)
            throw new InvalidOperationException("ROM not loaded.");

        Save(core.RomIdentity, core, slotIndex);
    }

    public void Save(RomIdentity romId, ISavestateCapable core, int slotIndex)
    {
        ValidateSlotIndex(slotIndex);

        byte[] payload = BuildPayload(core);
        byte[] checksum = ComputeSha256(payload);
        long frame = core.FrameCounter ?? 0;

        SavestateFile file = TryReadFile(romId) ?? CreateEmptyFile(romId);
        var slot = file.Slots[slotIndex - 1];
        slot.HasData = true;
        slot.IsCorrupt = false;
        slot.SavedAt = DateTimeOffset.UtcNow;
        slot.FrameCounter = frame;
        slot.Payload = payload;
        slot.PayloadHash = checksum;
        slot.PayloadLength = payload.Length;
        slot.Error = null;

        WriteFile(romId, file);
        Console.WriteLine($"[Savestate] Saved slot {slotIndex} for '{romId.Name}'.");
    }

    public void Load(ISavestateCapable core, int slotIndex)
    {
        if (core == null)
            throw new ArgumentNullException(nameof(core));
        if (core.RomIdentity == null)
            throw new InvalidOperationException("ROM not loaded.");

        Load(core.RomIdentity, core, slotIndex);
    }

    public void Load(RomIdentity romId, ISavestateCapable core, int slotIndex)
    {
        ValidateSlotIndex(slotIndex);
        var file = ReadFile(romId);
        var slot = file.Slots[slotIndex - 1];
        if (!slot.HasData)
            throw new InvalidOperationException($"Slot {slotIndex} is empty.");
        if (slot.IsCorrupt)
            throw new InvalidDataException($"Slot {slotIndex} is corrupt.");
        if (slot.Payload == null)
            throw new InvalidDataException($"Slot {slotIndex} payload missing.");

        using var stream = new MemoryStream(slot.Payload, writable: false);
        using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: false);
        core.LoadState(reader);
        Console.WriteLine($"[Savestate] Loaded slot {slotIndex} for '{romId.Name}'.");
    }

    public void Clear(ISavestateCapable core, int slotIndex)
    {
        if (core == null)
            throw new ArgumentNullException(nameof(core));
        if (core.RomIdentity == null)
            throw new InvalidOperationException("ROM not loaded.");
        Clear(core.RomIdentity, slotIndex);
    }

    public void Clear(RomIdentity romId, int slotIndex)
    {
        ValidateSlotIndex(slotIndex);
        SavestateFile file = TryReadFile(romId) ?? CreateEmptyFile(romId);
        var slot = file.Slots[slotIndex - 1];
        slot.HasData = false;
        slot.IsCorrupt = false;
        slot.SavedAt = null;
        slot.FrameCounter = 0;
        slot.Payload = null;
        slot.PayloadHash = null;
        slot.PayloadLength = 0;
        slot.Error = null;

        WriteFile(romId, file);
        Console.WriteLine($"[Savestate] Cleared slot {slotIndex} for '{romId.Name}'.");
    }

    private static void ValidateSlotIndex(int slotIndex)
    {
        if (slotIndex < 1 || slotIndex > SlotCount)
            throw new ArgumentOutOfRangeException(nameof(slotIndex), $"Slot index must be 1..{SlotCount}.");
    }

    private static SavestateSlotInfo[] BuildEmptySlots(string error)
    {
        var slots = new SavestateSlotInfo[SlotCount];
        for (int i = 0; i < SlotCount; i++)
        {
            slots[i] = new SavestateSlotInfo(i + 1, false, false, null, 0, 0, error);
        }
        return slots;
    }

    private static byte[] BuildPayload(ISavestateCapable core)
    {
        using var ms = new MemoryStream();
        using (var writer = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true))
        {
            core.SaveState(writer);
        }
        return ms.ToArray();
    }

    private SavestateFile CreateEmptyFile(RomIdentity romId)
    {
        var file = new SavestateFile(romId);
        for (int i = 0; i < SlotCount; i++)
            file.Slots[i] = new SavestateSlotEntry(i + 1);
        return file;
    }

    private SavestateFile? TryReadFile(RomIdentity romId)
    {
        string path = GetStatePath(romId);
        if (!File.Exists(path))
            return null;

        try
        {
            return ReadFile(romId);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Savestate] Failed to read file '{path}': {ex.Message}");
            return null;
        }
    }

    private SavestateFile ReadFile(RomIdentity romId)
    {
        string path = GetStatePath(romId);
        using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: false);

        string magic = Encoding.ASCII.GetString(reader.ReadBytes(FileMagic.Length));
        if (!string.Equals(magic, FileMagic, StringComparison.Ordinal))
            throw new InvalidDataException("Savestate magic mismatch.");

        int version = reader.ReadInt32();
        if (version != FileVersion)
            throw new InvalidDataException($"Savestate version mismatch: {version}.");

        int slotCount = reader.ReadInt32();
        if (slotCount != SlotCount)
            throw new InvalidDataException($"Savestate slot count mismatch: {slotCount}.");

        byte[] fileRomHash = reader.ReadBytes(romId.Hash.Length);
        if (!HashesEqual(romId.Hash, fileRomHash))
            throw new InvalidDataException("Savestate ROM hash mismatch.");

        int nameLength = reader.ReadInt32();
        string name = nameLength > 0 ? Encoding.UTF8.GetString(reader.ReadBytes(nameLength)) : romId.Name;
        var file = new SavestateFile(new RomIdentity(name, fileRomHash));

        for (int i = 0; i < SlotCount; i++)
        {
            int slotIndex = reader.ReadInt32();
            bool hasData = reader.ReadByte() != 0;
            long ticks = reader.ReadInt64();
            long frame = reader.ReadInt64();
            int payloadLength = reader.ReadInt32();
            long payloadOffset = reader.ReadInt64();
            byte[] hash = reader.ReadBytes(SlotHashLength);

            var slot = new SavestateSlotEntry(slotIndex)
            {
                HasData = hasData,
                SavedAt = ticks > 0 ? new DateTimeOffset(ticks, TimeSpan.Zero) : null,
                FrameCounter = frame,
                PayloadLength = payloadLength,
                PayloadOffset = payloadOffset,
                PayloadHash = hash
            };
            file.Slots[i] = slot;
        }

        for (int i = 0; i < SlotCount; i++)
        {
            var slot = file.Slots[i];
            if (!slot.HasData || slot.PayloadLength <= 0)
                continue;
            if (slot.PayloadOffset < 0 || slot.PayloadOffset + slot.PayloadLength > stream.Length)
            {
                slot.IsCorrupt = true;
                slot.Error = "Payload out of range.";
                continue;
            }

            stream.Seek(slot.PayloadOffset, SeekOrigin.Begin);
            slot.Payload = reader.ReadBytes(slot.PayloadLength);

            byte[] checksum = ComputeSha256(slot.Payload);
            if (slot.PayloadHash == null || !HashesEqual(checksum, slot.PayloadHash))
            {
                slot.IsCorrupt = true;
                slot.Error = "Checksum mismatch.";
                slot.Payload = null;
            }
        }

        return file;
    }

    private void WriteFile(RomIdentity romId, SavestateFile file)
    {
        Directory.CreateDirectory(_rootDirectory);
        string path = GetStatePath(romId);
        string tmpPath = path + ".tmp";

        int nameLength = Encoding.UTF8.GetByteCount(file.RomName);
        int headerSize = FileMagic.Length + sizeof(int) + sizeof(int) + romId.Hash.Length + sizeof(int) + nameLength;
        int slotEntrySize = sizeof(int) + sizeof(byte) + sizeof(long) + sizeof(long) + sizeof(int) + sizeof(long) + SlotHashLength;
        long payloadOffset = headerSize + (slotEntrySize * SlotCount);

        for (int i = 0; i < SlotCount; i++)
        {
            var slot = file.Slots[i];
            if (!slot.HasData || slot.Payload == null)
            {
                slot.PayloadLength = 0;
                slot.PayloadOffset = payloadOffset;
                slot.PayloadHash = new byte[SlotHashLength];
                continue;
            }

            slot.PayloadLength = slot.Payload.Length;
            slot.PayloadOffset = payloadOffset;
            payloadOffset += slot.Payload.Length;
        }

        using (var stream = File.Open(tmpPath, FileMode.Create, FileAccess.Write, FileShare.None))
        using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: false))
        {
            writer.Write(Encoding.ASCII.GetBytes(FileMagic));
            writer.Write(FileVersion);
            writer.Write(SlotCount);
            writer.Write(romId.Hash);
            writer.Write(nameLength);
            if (nameLength > 0)
                writer.Write(Encoding.UTF8.GetBytes(file.RomName));

            for (int i = 0; i < SlotCount; i++)
            {
                var slot = file.Slots[i];
                writer.Write(slot.SlotIndex);
                writer.Write((byte)(slot.HasData ? 1 : 0));
                writer.Write(slot.SavedAt?.UtcDateTime.Ticks ?? 0);
                writer.Write(slot.FrameCounter);
                writer.Write(slot.PayloadLength);
                writer.Write(slot.PayloadOffset);
                writer.Write(slot.PayloadHash ?? new byte[SlotHashLength]);
            }

            for (int i = 0; i < SlotCount; i++)
            {
                var slot = file.Slots[i];
                if (!slot.HasData || slot.Payload == null)
                    continue;
                writer.Write(slot.Payload);
            }
        }

        File.Copy(tmpPath, path, overwrite: true);
        File.Delete(tmpPath);
    }

    private string GetStatePath(RomIdentity romId)
    {
        string safeName = SanitizeFileName(romId.Name);
        string prefix = romId.HashPrefix();
        string fileName = $"{safeName}_{prefix}.euthstate";
        return Path.Combine(_rootDirectory, fileName);
    }

    private static string SanitizeFileName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return "rom";

        var sb = new StringBuilder(name.Length);
        foreach (char c in name)
        {
            if (c <= 0x7F && (char.IsLetterOrDigit(c) || c == '-' || c == '_' || c == '.'))
                sb.Append(c);
            else if (c == ' ')
                sb.Append('_');
            else
                sb.Append('_');
        }
        string result = sb.ToString().Trim('_');
        return string.IsNullOrWhiteSpace(result) ? "rom" : result;
    }

    private static byte[] ComputeSha256(byte[] data)
    {
        using var sha = SHA256.Create();
        return sha.ComputeHash(data);
    }

    private static bool HashesEqual(byte[] a, byte[] b)
    {
        if (a.Length != b.Length)
            return false;
        for (int i = 0; i < a.Length; i++)
        {
            if (a[i] != b[i])
                return false;
        }
        return true;
    }

    private sealed class SavestateFile
    {
        public SavestateFile(RomIdentity romId)
        {
            RomName = romId.Name;
            RomHash = romId.Hash;
            Slots = new SavestateSlotEntry[SlotCount];
        }

        public string RomName { get; }
        public byte[] RomHash { get; }
        public SavestateSlotEntry[] Slots { get; }
    }

    private sealed class SavestateSlotEntry
    {
        public SavestateSlotEntry(int slotIndex)
        {
            SlotIndex = slotIndex;
        }

        public int SlotIndex { get; }
        public bool HasData { get; set; }
        public bool IsCorrupt { get; set; }
        public DateTimeOffset? SavedAt { get; set; }
        public long FrameCounter { get; set; }
        public int PayloadLength { get; set; }
        public long PayloadOffset { get; set; }
        public byte[]? PayloadHash { get; set; }
        public byte[]? Payload { get; set; }
        public string? Error { get; set; }
    }
}
