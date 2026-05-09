using System;
using System.Collections.Generic;
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

    private static readonly bool UiProofMode = IsUiProofMode();
    private static readonly bool ForceDisplayEnable =
        IsEnvEnabled("EUTHERDRIVE_HSHAVOC_FORCE_DISPLAY") || UiProofMode;
    private static readonly bool ForceTestPalette =
        IsEnvEnabled("EUTHERDRIVE_HSHAVOC_FORCE_TEST_PALETTE") || UiProofMode;
    private static readonly bool FlushDmaQueue =
        IsEnvEnabled("EUTHERDRIVE_HSHAVOC_FLUSH_DMA_QUEUE");
    private static readonly bool TraceDmaQueueFlush =
        IsEnvEnabled("EUTHERDRIVE_HSHAVOC_TRACE_DMA_QUEUE_FLUSH");
    private static readonly bool FlushVdpCommandBlocks =
        IsEnvEnabled("EUTHERDRIVE_HSHAVOC_FLUSH_VDP_COMMAND_BLOCKS") || UiProofMode;
    private static readonly bool TraceVdpCommandBlockFlush =
        IsEnvEnabled("EUTHERDRIVE_HSHAVOC_TRACE_VDP_COMMAND_BLOCKS");
    private static readonly uint FlushVdpCommandBlockStart =
        ParseEnvHex("EUTHERDRIVE_HSHAVOC_VDP_COMMAND_BLOCK_START", 0x00FFE900);
    private static readonly uint FlushVdpCommandBlockEnd =
        ParseEnvHex("EUTHERDRIVE_HSHAVOC_VDP_COMMAND_BLOCK_END", 0x00FFEA80);
    private static readonly bool TraceVdpCommandBlockScan =
        IsEnvEnabled("EUTHERDRIVE_HSHAVOC_TRACE_VDP_COMMAND_BLOCK_SCAN");
    private static readonly uint TraceVdpCommandBlockScanStart =
        ParseEnvHex("EUTHERDRIVE_HSHAVOC_TRACE_VDP_COMMAND_BLOCK_SCAN_START", 0x00FF0000);
    private static readonly uint TraceVdpCommandBlockScanEnd =
        ParseEnvHex("EUTHERDRIVE_HSHAVOC_TRACE_VDP_COMMAND_BLOCK_SCAN_END", 0x00FFFFFF);
    private static readonly int TraceVdpCommandBlockScanMax =
        ParseEnvInt("EUTHERDRIVE_HSHAVOC_TRACE_VDP_COMMAND_BLOCK_SCAN_MAX", 256);
    private static readonly bool FlushStaticPalettePlan =
        IsEnvEnabled("EUTHERDRIVE_HSHAVOC_FLUSH_STATIC_PALETTE_PLAN");
    private static readonly bool TraceStaticPalettePlan =
        IsEnvEnabled("EUTHERDRIVE_HSHAVOC_TRACE_STATIC_PALETTE_PLAN");
    private static readonly bool FlushLowPatternRamProbe =
        IsEnvEnabled("EUTHERDRIVE_HSHAVOC_FLUSH_LOW_PATTERN_RAM_PROBE");
    private static readonly bool MirrorLowPatternRamProbePages =
        IsEnvEnabled("EUTHERDRIVE_HSHAVOC_FLUSH_LOW_PATTERN_RAM_PROBE_MIRROR_PAGES");
    private static readonly bool RepeatLowPatternRamProbe =
        IsEnvEnabled("EUTHERDRIVE_HSHAVOC_FLUSH_LOW_PATTERN_RAM_PROBE_EVERY_FRAME");
    private static readonly bool TraceLowPatternRamProbe =
        IsEnvEnabled("EUTHERDRIVE_HSHAVOC_TRACE_LOW_PATTERN_RAM_PROBE");
    private static readonly bool LatchVBlankGate =
        IsEnvEnabled("EUTHERDRIVE_HSHAVOC_LATCH_VBLANK_GATE");
    private static readonly bool TraceVBlankGate =
        IsEnvEnabled("EUTHERDRIVE_HSHAVOC_TRACE_VBLANK_GATE");
    private static readonly bool TraceCramCommandBlockCandidates =
        IsEnvEnabled("EUTHERDRIVE_HSHAVOC_TRACE_CRAM_COMMAND_BLOCKS");
    private static readonly uint TraceCramCommandBlockStart =
        ParseEnvHex("EUTHERDRIVE_HSHAVOC_TRACE_CRAM_COMMAND_BLOCK_START", 0x00FF0000);
    private static readonly uint TraceCramCommandBlockEnd =
        ParseEnvHex("EUTHERDRIVE_HSHAVOC_TRACE_CRAM_COMMAND_BLOCK_END", 0x00FFFFFF);
    private static readonly int TraceCramCommandBlockMax =
        ParseEnvInt("EUTHERDRIVE_HSHAVOC_TRACE_CRAM_COMMAND_BLOCK_MAX", 128);
    private static readonly string? RamSeedWords =
        Environment.GetEnvironmentVariable("EUTHERDRIVE_HSHAVOC_RAM_SEED_WORDS");

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
        // Preserve the home startup tail that lowers the 68000 interrupt mask
        // before entering the main loop; otherwise the real VBlank dispatcher
        // at $0ab8 never reaches its $1332 VDP flush call.
        (0x0CB2, 0x027C), (0x0CB4, 0xF8FF), (0x0CB6, 0x4EF9),
        (0x0CB8, 0x0000), (0x0CBA, 0x1126),
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

    private static readonly (int Address, ushort Value)[] InitMirrorProbePatch =
    {
        // Probe only: these arcade addresses match the home ROM VDP-list
        // startup routines at $0ed0 and $10f8. The current startup bridge
        // never reaches them naturally.
        (0x0C70, 0x4EB9), (0x0C72, 0x0000), (0x0C74, 0x13F6),
        (0x0C76, 0x4EB9), (0x0C78, 0x0000), (0x0C7A, 0x161E)
    };

    private static readonly (int Address, ushort Value)[] InitQueueProbePatch =
    {
        // Probe only: $13fe is the executable queue-flush entry observed in the
        // home ROM at $0ed8. $13f6 includes the preceding table/entry word.
        (0x0C70, 0x4EB9), (0x0C72, 0x0000), (0x0C74, 0x13FE),
        (0x0C76, 0x4EB9), (0x0C78, 0x0000), (0x0C7A, 0x161E)
    };

    private static readonly (int Address, ushort Value)[] InitListProbePatch =
    {
        // Probe only: isolate the mirrored home VDP-list dispatcher without
        // entering the queue flusher. $161e is inside the first slot writer;
        // $160e includes the flag check for that slot.
        (0x0C70, 0x4EB9), (0x0C72, 0x0000), (0x0C74, 0x160E),
        (0x0C76, 0x4E71), (0x0C78, 0x4E71), (0x0C7A, 0x4E71)
    };

    private static readonly (int Address, ushort Value)[] InitDispatcherProbePatch =
    {
        // Probe only: $1332 is the stack-correct entry for the mirrored VDP
        // dispatcher. The narrower anchors below it return through the shared
        // movem restore at $19ac and corrupt the caller stack if entered alone.
        (0x0C70, 0x4EB9), (0x0C72, 0x0000), (0x0C74, 0x1332),
        (0x0C76, 0x4E71), (0x0C78, 0x4E71), (0x0C7A, 0x4E71)
    };

    private readonly MdTracerAdapter _md = new();
    private readonly HashSet<ulong> _flushedQueueEntries = new();
    private readonly HashSet<ulong> _flushedVdpCommandBlocks = new();
    private readonly HashSet<ulong> _flushedStaticPalettePlans = new();
    private readonly HashSet<ulong> _flushedLowPatternRamProbes = new();
    private readonly HashSet<ulong> _tracedVdpCommandBlocks = new();
    private readonly HashSet<ulong> _tracedCramCommandBlocks = new();
    private bool _testPaletteSeeded;
    private long _lastVBlankGateTraceFrame = long.MinValue;

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
        _flushedQueueEntries.Clear();
        _flushedVdpCommandBlocks.Clear();
        _flushedStaticPalettePlans.Clear();
        _flushedLowPatternRamProbes.Clear();
        _tracedVdpCommandBlocks.Clear();
        _tracedCramCommandBlocks.Clear();
        _testPaletteSeeded = false;
        _lastVBlankGateTraceFrame = long.MinValue;
        string profile = GetDecodeProfile();
        byte[] decoded = DecodeArchive(path, profile);
        string tempPath = Path.Combine(Path.GetTempPath(), $"eutherdrive_hshavoc_{Guid.NewGuid():N}.gen");
        File.WriteAllBytes(tempPath, decoded);
        try
        {
            _md.PowerCycleAndLoadRom(tempPath);
            InstallBoardAckProbe();
            ApplyRamSeedWordsIfRequested();
            ForceVdpDisplayIfRequested();
            SeedTestPaletteIfRequested();
            string proofSuffix = UiProofMode ? " | ui-proof=display+vram-dma+synthetic-palette" : string.Empty;
            RomInfo.Summary = $"High Seas Havoc arcade probe | decode={profile}{proofSuffix} | {BoardModel}";
            RomInfo.ExtraInfo =
                "Data East hshavoc.zip via HshavocAdapter. This is not a Sega System 16 target; it runs the " +
                "Mega Drive-compatible board path with arcade-only startup/PIC probing layered on top. " +
                "Applies MAME base decode plus current startup probe patch. " +
                "No decoded ROM is kept; temp image is deleted after load." +
                (UiProofMode
                    ? " UI proof mode is active: generated VDP DMA command blocks are flushed and a synthetic palette is supplied until the real palette producer is mapped."
                    : string.Empty);
        }
        finally
        {
            TryDelete(tempPath);
        }
    }

    public void Reset()
    {
        _flushedQueueEntries.Clear();
        _flushedVdpCommandBlocks.Clear();
        _flushedStaticPalettePlans.Clear();
        _flushedLowPatternRamProbes.Clear();
        _tracedVdpCommandBlocks.Clear();
        _tracedCramCommandBlocks.Clear();
        _testPaletteSeeded = false;
        _lastVBlankGateTraceFrame = long.MinValue;
        _md.Reset();
        ApplyRamSeedWordsIfRequested();
    }

    public void RunFrame()
    {
        LatchVBlankGateIfRequested();
        _md.RunFrame();
        ForceVdpDisplayIfRequested();
        TraceVdpCommandBlocksIfRequested();
        FlushVdpCommandBlocksIfRequested();
        FlushLowPatternRamProbeIfRequested();
        FlushStaticPalettePlanIfRequested();
        TraceCramCommandBlocksIfRequested();
        FlushVdpDmaQueueIfRequested();
        SeedTestPaletteIfRequested();
    }

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

    private static void ForceVdpDisplayIfRequested()
    {
        if (!ForceDisplayEnable)
            return;

        md_vdp? vdp = md_main.g_md_vdp;
        if (vdp == null)
            return;

        vdp.read16(0x00C00004);
        vdp.write16(0x00C00004, 0x8174);
    }

    private void SeedTestPaletteIfRequested()
    {
        if (!ForceTestPalette || _testPaletteSeeded || md_main.g_md_vdp == null)
            return;

        md_vdp vdp = md_main.g_md_vdp;
        for (int index = 0; index < 64; index++)
        {
            ushort color = (ushort)((((index >> 4) & 0x07) * 0x200) | (((index >> 2) & 0x07) * 0x020) | ((index & 0x03) * 0x002));
            if (color == 0)
                color = 0x0222;
            ushort address = (ushort)(index * 2);
            vdp.read16(0x00C00004);
            vdp.write16(0x00C00004, (ushort)(0xC000 | (address & 0x3FFF)));
            vdp.write16(0x00C00004, (ushort)((address >> 14) & 0x0003));
            vdp.write16(0x00C00000, color);
        }

        vdp.read16(0x00C00004);
        _testPaletteSeeded = true;
    }

    private void FlushVdpDmaQueueIfRequested()
    {
        if (!FlushDmaQueue || md_main.g_md_vdp == null)
            return;

        // Legacy false-color probe for the RAM-side list around $ffe800.
        // The generated command blocks are authoritative for VRAM; this path
        // only proves that queued data can light the renderer when treated as
        // CRAM and should not be considered the final palette model.
        for (uint slot = 0x00FFE800; slot <= 0x00FFEA00; slot += 2)
        {
            uint source = ReadLong(slot);
            ushort commandWord = _md.DebugReadM68kWord(slot + 4);
            ushort byteCount = _md.DebugReadM68kWord(slot + 6);
            ushort active = _md.DebugReadM68kWord(slot + 8);

            if (active == 0 || byteCount == 0 || byteCount > 0x0400)
                continue;
            if ((source & 0x00FF0000) != 0x00FF0000)
                continue;
            if ((commandWord & 0xC000) != 0xC000)
                continue;

            ulong signature =
                ((ulong)slot << 40) |
                ((ulong)(source & 0x00FFFFFF) << 16) |
                ((ulong)commandWord << 0) ^
                ((ulong)byteCount << 24);
            if (!_flushedQueueEntries.Add(signature))
                continue;

            FlushCramQueueEntry(slot, source, commandWord, byteCount);
        }
    }

    private void FlushVdpCommandBlocksIfRequested()
    {
        if (!FlushVdpCommandBlocks || md_main.g_md_vdp == null)
            return;

        // The startup code builds 14-byte command blocks in work RAM:
        // five VDP DMA register writes (93..97) followed by a two-word
        // control command. Feeding that exact stream lets the MD VDP core
        // perform the copy and avoids guessing VRAM destination addresses.
        uint start = ClampM68kRamScanStart(FlushVdpCommandBlockStart);
        uint end = ClampM68kRamScanEnd(FlushVdpCommandBlockEnd);
        if (end < start)
            return;

        for (uint block = start; block <= end; block += 2)
        {
            ushort reg19 = _md.DebugReadM68kWord(block);
            ushort reg20 = _md.DebugReadM68kWord(block + 2);
            ushort reg21 = _md.DebugReadM68kWord(block + 4);
            ushort reg22 = _md.DebugReadM68kWord(block + 6);
            ushort reg23 = _md.DebugReadM68kWord(block + 8);
            ushort control1 = _md.DebugReadM68kWord(block + 10);
            ushort control2 = _md.DebugReadM68kWord(block + 12);

            if (!LooksLikeVdpCommandBlock(reg19, reg20, reg21, reg22, reg23, control1, control2))
                continue;

            ulong signature = BuildVdpCommandBlockSignature(block, reg19, reg20, reg21, reg22, reg23, control1, control2);
            if (!_flushedVdpCommandBlocks.Add(signature))
                continue;

            ExecuteVdpCommandBlock(block, reg19, reg20, reg21, reg22, reg23, control1, control2);
        }
    }

    private void TraceVdpCommandBlocksIfRequested()
    {
        if (!TraceVdpCommandBlockScan || md_main.g_md_vdp == null)
            return;

        uint start = ClampM68kRamScanStart(TraceVdpCommandBlockScanStart);
        uint end = ClampM68kRamScanEnd(TraceVdpCommandBlockScanEnd);
        if (end < start)
            return;

        int logged = 0;
        for (uint block = start; block <= end; block += 2)
        {
            ushort reg19 = _md.DebugReadM68kWord(block);
            ushort reg20 = _md.DebugReadM68kWord(block + 2);
            ushort reg21 = _md.DebugReadM68kWord(block + 4);
            ushort reg22 = _md.DebugReadM68kWord(block + 6);
            ushort reg23 = _md.DebugReadM68kWord(block + 8);
            ushort control1 = _md.DebugReadM68kWord(block + 10);
            ushort control2 = _md.DebugReadM68kWord(block + 12);

            if (!LooksLikeVdpCommandBlock(reg19, reg20, reg21, reg22, reg23, control1, control2))
                continue;

            ulong signature = BuildVdpCommandBlockSignature(block, reg19, reg20, reg21, reg22, reg23, control1, control2);
            if (!_tracedVdpCommandBlocks.Add(signature))
                continue;

            LogVdpCommandBlock("HSHAVOC-VDPBLK-CANDIDATE", block, reg19, reg20, reg21, reg22, reg23, control1, control2);
            logged++;
            if (TraceVdpCommandBlockScanMax > 0 && logged >= TraceVdpCommandBlockScanMax)
                return;
        }
    }

    private void FlushStaticPalettePlanIfRequested()
    {
        if (!FlushStaticPalettePlan || md_main.g_md_vdp == null)
            return;

        const uint source = 0x00FFF700;
        const int words = 0x40;
        if (IsM68kWordRangeAllZero(source, words))
            return;

        ulong signature = HashM68kWords(source, words);
        if (!_flushedStaticPalettePlans.Add(signature))
            return;

        ExecuteVdpCommandBlock(
            source,
            0x9340,
            0x9400,
            0x9580,
            0x96FB,
            0x977F,
            0xC000,
            0x0080);

        if (TraceStaticPalettePlan)
        {
            Console.WriteLine(
                $"[HSHAVOC-STATIC-PALETTE-FLUSH] frame={md_main.g_md_vdp?.FrameCounter ?? -1} " +
                $"source=0x{source:X6} words=0x{words:X2} hash=0x{signature:X16}");
        }
    }

    private void FlushLowPatternRamProbeIfRequested()
    {
        if (!FlushLowPatternRamProbe || md_main.g_md_vdp == null)
            return;

        // Probe only: the home ROM's matching VDP queue eventually DMAs
        // decompressed graphics from $ff0000 into low pattern VRAM. The current
        // arcade path builds that RAM buffer but sends observed command blocks
        // to high VRAM pages, leaving low tile indices black. This replay tests
        // that single missing edge without storing or emitting decoded ROM data.
        const uint source = 0x00FF0000;
        const int words = 0x0800;
        if (IsM68kWordRangeAllZero(source, words))
            return;

        ulong hash = HashM68kWords(source, words);
        int[] destinations = MirrorLowPatternRamProbePages
            ? new[] { 0x0000, 0x2000, 0x4000, 0x6000 }
            : new[] { 0x0000 };

        md_vdp? vdp = md_main.g_md_vdp;
        foreach (int destination in destinations)
        {
            ulong signature = hash ^ ((ulong)destination << 32);
            if (!RepeatLowPatternRamProbe && !_flushedLowPatternRamProbes.Add(signature))
                continue;

            vdp.read16(0x00C00004);
            // The control latch may be waiting for an address second word when
            // the frame-level probe runs. A duplicate register write makes the
            // DMA enable transition deterministic without depending on caller timing.
            vdp.write16(0x00C00004, 0x8174);
            vdp.write16(0x00C00004, 0x8174);
            vdp.read16(0x00C00004);
            vdp.write16(0x00C00004, 0x8F02);
            ExecuteVdpCommandBlock(
                source,
                0x9300,
                0x9408,
                0x9500,
                0x9680,
                0x977F,
                (ushort)(0x4000 | (destination & 0x3FFF)),
                (ushort)(0x0080 | ((destination >> 14) & 0x0007)));
            vdp.read16(0x00C00004);
            vdp.write16(0x00C00004, 0x8164);
            vdp.write16(0x00C00004, 0x8164);

            if (TraceLowPatternRamProbe)
            {
                Console.WriteLine(
                    $"[HSHAVOC-LOWPAT-FLUSH] frame={md_main.g_md_vdp?.FrameCounter ?? -1} " +
                    $"source=0x{source:X6} words=0x{words:X4} dest=0x{destination:X4} hash=0x{hash:X16}");
            }
        }
    }

    private bool IsM68kWordRangeAllZero(uint source, int words)
    {
        for (int i = 0; i < words; i++)
        {
            if (_md.DebugReadM68kWord(source + (uint)(i * 2)) != 0)
                return false;
        }

        return true;
    }

    private void TraceCramCommandBlocksIfRequested()
    {
        if (!TraceCramCommandBlockCandidates || md_main.g_md_vdp == null)
            return;

        uint start = ClampM68kRamScanStart(TraceCramCommandBlockStart);
        uint end = ClampM68kRamScanEnd(TraceCramCommandBlockEnd);
        if (end < start)
            return;

        int logged = 0;
        for (uint block = start; block <= end; block += 2)
        {
            ushort reg19 = _md.DebugReadM68kWord(block);
            ushort reg20 = _md.DebugReadM68kWord(block + 2);
            ushort reg21 = _md.DebugReadM68kWord(block + 4);
            ushort reg22 = _md.DebugReadM68kWord(block + 6);
            ushort reg23 = _md.DebugReadM68kWord(block + 8);
            ushort control1 = _md.DebugReadM68kWord(block + 10);
            ushort control2 = _md.DebugReadM68kWord(block + 12);

            if (!LooksLikeVdpCommandBlock(reg19, reg20, reg21, reg22, reg23, control1, control2))
                continue;

            int codeLow = DecodeVdpCodeLow(control1, control2);
            if (codeLow != 0x03)
                continue;

            ulong signature = BuildVdpCommandBlockSignature(block, reg19, reg20, reg21, reg22, reg23, control1, control2);
            if (!_tracedCramCommandBlocks.Add(signature))
                continue;

            LogVdpCommandBlock("HSHAVOC-CRAMBLK-CANDIDATE", block, reg19, reg20, reg21, reg22, reg23, control1, control2);
            logged++;
            if (TraceCramCommandBlockMax > 0 && logged >= TraceCramCommandBlockMax)
                return;
        }
    }

    private static uint ClampM68kRamScanStart(uint value)
        => Math.Max(0x00FF0000, value & 0x00FFFFFE);

    private static uint ClampM68kRamScanEnd(uint value)
        => Math.Min(0x00FFFFF2, value & 0x00FFFFFE);

    private static bool LooksLikeVdpCommandBlock(
        ushort reg19,
        ushort reg20,
        ushort reg21,
        ushort reg22,
        ushort reg23,
        ushort control1,
        ushort control2)
    {
        if ((reg19 & 0xFF00) != 0x9300 || (reg20 & 0xFF00) != 0x9400 ||
            (reg21 & 0xFF00) != 0x9500 || (reg22 & 0xFF00) != 0x9600 ||
            (reg23 & 0xFF00) != 0x9700)
            return false;

        if ((control1 & 0xC000) == 0x8000)
            return false;

        int codeLow = DecodeVdpCodeLow(control1, control2);
        if (codeLow != 0x01 && codeLow != 0x03 && codeLow != 0x05)
            return false;

        if ((control2 & 0x0080) == 0)
            return false;

        int length = (reg19 & 0x00FF) | ((reg20 & 0x00FF) << 8);
        if (length == 0 || length > 0x4000)
            return false;

        uint sourceByte = DecodeVdpDmaSourceByte(reg21, reg22, reg23);
        uint byteLength = (uint)length * 2;
        bool romSource = sourceByte < InterleavedSize && sourceByte + byteLength <= InterleavedSize;
        bool ramSource = sourceByte >= 0x00FF0000 && sourceByte + byteLength - 1 <= 0x00FFFFFF;
        return romSource || ramSource;
    }

    private ulong BuildVdpCommandBlockSignature(
        uint block,
        ushort reg19,
        ushort reg20,
        ushort reg21,
        ushort reg22,
        ushort reg23,
        ushort control1,
        ushort control2)
    {
        ulong signature =
            ((ulong)block << 40) ^
            ((ulong)reg19 << 48) ^
            ((ulong)reg20 << 32) ^
            ((ulong)reg21 << 16) ^
            reg22 ^
            ((ulong)reg23 << 8) ^
            ((ulong)control1 << 24) ^
            ((ulong)control2 << 4);

        uint sourceByte = DecodeVdpDmaSourceByte(reg21, reg22, reg23);
        if (sourceByte < 0x00FF0000 || sourceByte > 0x00FFFFFF)
            return signature;

        int length = (reg19 & 0x00FF) | ((reg20 & 0x00FF) << 8);
        return signature ^ HashM68kWords(sourceByte, length);
    }

    private ulong HashM68kWords(uint source, int words)
    {
        ulong hash = 1469598103934665603UL;
        for (int i = 0; i < words; i++)
        {
            ushort value = _md.DebugReadM68kWord(source + (uint)(i * 2));
            hash ^= value;
            hash *= 1099511628211UL;
        }

        return hash;
    }

    private static uint DecodeVdpDmaSourceByte(ushort reg21, ushort reg22, ushort reg23)
    {
        uint sourceWord = (uint)((reg21 & 0x00FF) | ((reg22 & 0x00FF) << 8) | ((reg23 & 0x007F) << 16));
        return sourceWord << 1;
    }

    private static int DecodeVdpCodeLow(ushort control1, ushort control2)
        => ((control1 >> 14) & 0x03) | ((control2 >> 2) & 0x0C);

    private static void ExecuteVdpCommandBlock(
        uint block,
        ushort reg19,
        ushort reg20,
        ushort reg21,
        ushort reg22,
        ushort reg23,
        ushort control1,
        ushort control2)
    {
        md_vdp? vdp = md_main.g_md_vdp;
        if (vdp == null)
            return;

        ushort[] words = { reg19, reg20, reg21, reg22, reg23 };
        foreach (ushort word in words)
        {
            vdp.read16(0x00C00004);
            vdp.write16(0x00C00004, word);
        }

        vdp.read16(0x00C00004);
        vdp.write16(0x00C00004, control1);
        vdp.write16(0x00C00004, control2);

        if (TraceVdpCommandBlockFlush)
        {
            LogVdpCommandBlock("HSHAVOC-VDPBLK-FLUSH", block, reg19, reg20, reg21, reg22, reg23, control1, control2);
        }
    }

    private static void LogVdpCommandBlock(
        string tag,
        uint block,
        ushort reg19,
        ushort reg20,
        ushort reg21,
        ushort reg22,
        ushort reg23,
        ushort control1,
        ushort control2)
    {
        int codeLow = DecodeVdpCodeLow(control1, control2);
        int dest = (control1 & 0x3FFF) | ((control2 & 0x0007) << 14);
        int length = (reg19 & 0x00FF) | ((reg20 & 0x00FF) << 8);
        int sourceWord = (reg21 & 0x00FF) | ((reg22 & 0x00FF) << 8) | ((reg23 & 0x007F) << 16);
        Console.WriteLine(
            $"[{tag}] frame={md_main.g_md_vdp?.FrameCounter ?? -1} " +
            $"block=0x{block:X6} len=0x{length:X4} sourceWord=0x{sourceWord:X6} " +
            $"sourceByte=0x{(sourceWord << 1):X6} dest=0x{dest:X4} code=0x{codeLow:X2} " +
            $"regs={reg19:X4},{reg20:X4},{reg21:X4},{reg22:X4},{reg23:X4} cmd={control1:X4},{control2:X4}");
    }

    private void FlushCramQueueEntry(uint slot, uint source, ushort commandWord, ushort byteCount)
    {
        md_vdp? vdp = md_main.g_md_vdp;
        if (vdp == null)
            return;

        vdp.read16(0x00C00004);
        vdp.write16(0x00C00004, commandWord);
        vdp.write16(0x00C00004, 0x0000);

        int words = byteCount / 2;
        for (int i = 0; i < words; i++)
        {
            ushort data = _md.DebugReadM68kWord(source + (uint)(i * 2));
            vdp.write16(0x00C00000, data);
        }

        if (TraceDmaQueueFlush)
        {
            Console.WriteLine(
                $"[HSHAVOC-DMAQ-FLUSH] frame={md_main.g_md_vdp?.FrameCounter ?? -1} " +
                $"slot=0x{slot:X6} source=0x{source:X6} command=0x{commandWord:X4} bytes=0x{byteCount:X4} words={words}");
        }
    }

    private uint ReadLong(uint address)
        => ((uint)_md.DebugReadM68kWord(address) << 16) | _md.DebugReadM68kWord(address + 2);

    private static bool IsUiProofMode()
    {
        string? raw = Environment.GetEnvironmentVariable("EUTHERDRIVE_HSHAVOC_UI_PROOF_MODE");
        if (string.Equals(raw, "0", StringComparison.Ordinal))
            return false;
        if (string.Equals(raw, "1", StringComparison.Ordinal))
            return true;

        return string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("EUTHERDRIVE_HEADLESS_CORE"));
    }

    private static bool IsEnvEnabled(string name)
        => string.Equals(Environment.GetEnvironmentVariable(name), "1", StringComparison.Ordinal);

    private static bool IsEnvDisabled(string name)
        => string.Equals(Environment.GetEnvironmentVariable(name), "0", StringComparison.Ordinal);

    private void LatchVBlankGateIfRequested()
    {
        if (!LatchVBlankGate)
            return;

        WriteM68kRamWord(0x00FFF906, 0x0001);

        if (!TraceVBlankGate)
            return;

        long frame = md_main.g_md_vdp?.FrameCounter ?? -1;
        if (frame == _lastVBlankGateTraceFrame)
            return;

        _lastVBlankGateTraceFrame = frame;
        Console.WriteLine($"[HSHAVOC-VBLANK-GATE] frame={frame} addr=0xFFF906 value=0x0001");
    }

    private static int ParseEnvInt(string name, int fallback)
    {
        string? raw = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(raw))
            return fallback;
        return int.TryParse(raw, out int parsed) ? parsed : fallback;
    }

    private static uint ParseEnvHex(string name, uint fallback)
    {
        string? raw = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(raw))
            return fallback;
        raw = raw.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? raw[2..] : raw;
        return uint.TryParse(raw, System.Globalization.NumberStyles.HexNumber, null, out uint parsed)
            ? parsed
            : fallback;
    }

    private static void ApplyRamSeedWordsIfRequested()
    {
        if (string.IsNullOrWhiteSpace(RamSeedWords))
            return;

        md_m68k.InitMemoryIfNeeded();
        byte[]? memory = md_m68k.g_memory;
        if (memory == null)
            return;

        int applied = 0;
        foreach (string entry in RamSeedWords.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string[] parts = entry.Split(':', 2, StringSplitOptions.TrimEntries);
            if (parts.Length != 2)
                throw new InvalidDataException($"Invalid HSHavoc RAM seed word '{entry}'. Use address:value.");
            uint address = ParseHexLiteral(parts[0]);
            ushort value = checked((ushort)ParseHexLiteral(parts[1]));
            if ((address & 1) != 0 || address >= memory.Length - 1)
                throw new InvalidDataException($"Invalid HSHavoc RAM seed address 0x{address:X8}.");
            WriteM68kRamWord(address, value);
            applied++;
        }

        Console.WriteLine($"[HSHAVOC-RAM-SEED] words={applied}");
    }

    private static void WriteM68kRamWord(uint address, ushort value)
    {
        md_m68k.InitMemoryIfNeeded();
        byte[]? memory = md_m68k.g_memory;
        if (memory == null || (address & 1) != 0 || address >= memory.Length - 1)
            return;

        memory[address] = (byte)(value >> 8);
        memory[address + 1] = (byte)value;
    }

    private static uint ParseHexLiteral(string raw)
    {
        string text = raw.Trim();
        text = text.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? text[2..] : text;
        if (!uint.TryParse(text, System.Globalization.NumberStyles.HexNumber, null, out uint value))
            throw new InvalidDataException($"Invalid hex literal '{raw}'.");
        return value;
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
        if (profile == "initmirror")
            ApplyPatch(rom, InitMirrorProbePatch);
        if (profile == "initqueue")
            ApplyPatch(rom, InitQueueProbePatch);
        if (profile == "initlist")
            ApplyPatch(rom, InitListProbePatch);
        if (profile == "initdispatcher")
            ApplyPatch(rom, InitDispatcherProbePatch);
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
            "initmirror" => "initmirror",
            "initqueue" => "initqueue",
            "initlist" => "initlist",
            "initdispatcher" => "initdispatcher",
            _ => throw new InvalidDataException($"Unknown HSHavoc decode profile '{raw}'. Use base, startup, phase2, island10a0, initmirror, initqueue, initlist, or initdispatcher.")
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
