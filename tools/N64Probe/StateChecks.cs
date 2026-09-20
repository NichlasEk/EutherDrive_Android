using System.Buffers.Binary;
using System.Reflection;
using Ryu64.MIPS;

internal static class StateChecks
{
    // v5 appends a pending-slice flag and 720 bytes of architectural RSP state.
    internal const int RspSchedulingTrailerBytes = 721;

    // DMA/RSP-only cases never set coverage mode. Normalize the additive v4
    // schema header/trailer to compare their FULL remaining state against v3.
    internal static void NormalizeForLegacyComparison(MemoryStream state)
    {
        byte[] bytes = state.GetBuffer();
        int version = BinaryPrimitives.ReadInt32LittleEndian(bytes);
        if (version == 5)
        {
            if (bytes[state.Length - RspSchedulingTrailerBytes] != 0)
                throw new InvalidDataException("Cannot compare a pending RSP slice against a legacy DLL");
            state.SetLength(state.Length - RspSchedulingTrailerBytes);
            version = 4;
        }
        if (version == 3) return;
        if (version != 4 || bytes[state.Length - 1] != 0)
            throw new InvalidDataException("Cannot compare active new render state against a legacy DLL");
        BinaryPrimitives.WriteInt32LittleEndian(bytes, 3);
        state.SetLength(state.Length - 1);
        state.Position = state.Length;
    }

    internal static int CheckCoverageRoundTrip()
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var mode = typeof(Memory).GetField("_rdpOtherModesCvgTimesAlpha", flags)!;
        var source = new Memory(new byte[4096]);
        mode.SetValue(source, true);
        using var state = new MemoryStream();
        using var writer = new BinaryWriter(state, System.Text.Encoding.UTF8, leaveOpen: true);
        source.SaveState(writer);
        writer.Flush();
        var restored = new Memory(new byte[4096]);
        R4300.memory = restored;
        state.Position = 0;
        using var reader = new BinaryReader(state, System.Text.Encoding.UTF8, leaveOpen: true);
        restored.LoadState(reader);
        if (!(bool)mode.GetValue(restored)!) throw new Exception("Coverage mode lost across v4 savestate");
        if (state.Position != state.Length) throw new Exception("Savestate not completely consumed");
        // Old snapshots have no coverage field. Loading one over a live core
        // must reset the missing field, not retain the previous game's mode.
        byte[] legacy = state.ToArray()[..^(RspSchedulingTrailerBytes + 1)];
        BinaryPrimitives.WriteInt32LittleEndian(legacy, 3);
        using var legacyReader = new BinaryReader(new MemoryStream(legacy));
        restored.LoadState(legacyReader);
        if ((bool)mode.GetValue(restored)!) throw new Exception("Legacy savestate retained stale coverage mode");
        if (legacyReader.BaseStream.Position != legacy.Length) throw new Exception("Legacy savestate alignment changed");
        return 4;
    }
}
