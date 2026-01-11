using System;

namespace EutherDrive.Core.Savestates;

public sealed class SavestateSlotInfo
{
    public SavestateSlotInfo(
        int slotIndex,
        bool hasData,
        bool isCorrupt,
        DateTimeOffset? savedAt,
        long frameCounter,
        int payloadLength,
        string? error)
    {
        SlotIndex = slotIndex;
        HasData = hasData;
        IsCorrupt = isCorrupt;
        SavedAt = savedAt;
        FrameCounter = frameCounter;
        PayloadLength = payloadLength;
        Error = error;
    }

    public int SlotIndex { get; }
    public bool HasData { get; }
    public bool IsCorrupt { get; }
    public DateTimeOffset? SavedAt { get; }
    public long FrameCounter { get; }
    public int PayloadLength { get; }
    public string? Error { get; }
}
