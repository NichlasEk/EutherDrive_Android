using System;
using System.IO;
using System.Globalization;
using EutherDrive.Core.Cpu.V25Emu;
using EutherDrive.Core.Cpu.V60Emu;

namespace EutherDrive.Core.Arcade.System32;

// Sega System 32 hardware notes and ROM layouts are translated from MAME's
// BSD-3-Clause Sega System 32 driver by Aaron Giles.
public sealed class System32Adapter : IEmulatorCore
{
    private const int FrameWidth = 416;
    private const int FrameHeight = 224;
    private const int FrameStride = FrameWidth * 4;
    private const int OutputSampleRate = 44_100;
    private const int OutputChannels = 2;
    private const int LayerText = 0;
    private const int LayerNbg0 = 1;
    private const int LayerNbg1 = 2;
    private const int LayerNbg2 = 3;
    private const int LayerNbg3 = 4;
    private const int LayerBitmap = 5;
    private const int LayerSprites = 6;
    private const int LayerBackground = 7;

    private readonly object _frameSync = new();
    private byte[] _presentFrameBuffer = new byte[FrameHeight * FrameStride];
    private byte[] _renderFrameBuffer = new byte[FrameHeight * FrameStride];
    private byte[] _snapshotFrameBuffer = new byte[FrameHeight * FrameStride];
    private ushort[] _spriteVisiblePixels = new ushort[FrameWidth * FrameHeight];
    private ushort[] _spriteRenderPixels = new ushort[FrameWidth * FrameHeight];
    private int _spriteVisibleNumber = 6;
    private int _spriteRenderNumber = 8;
    private readonly ushort[][] _layerPixels =
    {
        new ushort[FrameWidth * FrameHeight],
        new ushort[FrameWidth * FrameHeight],
        new ushort[FrameWidth * FrameHeight],
        new ushort[FrameWidth * FrameHeight],
        new ushort[FrameWidth * FrameHeight],
        new ushort[FrameWidth * FrameHeight],
        new ushort[FrameWidth * FrameHeight],
        new ushort[FrameWidth * FrameHeight]
    };
    private readonly bool[][] _layerTransparent =
    {
        new bool[FrameHeight],
        new bool[FrameHeight],
        new bool[FrameHeight],
        new bool[FrameHeight],
        new bool[FrameHeight],
        new bool[FrameHeight],
        new bool[FrameHeight],
        new bool[FrameHeight]
    };
    private readonly System32Bus _bus = new();
    private readonly System32Sound _sound;
    private readonly V60 _mainCpu = new();
    private readonly V25 _mcu = new();
    private short[] _audioBuffer = Array.Empty<short>();
    private System32RomSet? _roms;
    private ArcadeInputState _input;
    private bool _loaded;
    private bool _cpuStoppedLogged;
    private bool _mcuStoppedLogged;
    private bool _traceBoot;
    private bool _traceMcu;
    private bool _traceTail;
    private bool _videoStats;
    private int _frameCounter;
    private int _lastTextTiles;
    private int _lastTextPixels;
    private int _lastTilePixels;
    private int _lastSpritePixels;
    private int _visibleWidth = 320;
    private int _presentVisibleWidth = 320;
    private int _mainCpuInstructionsPerFrame = 4096;
    private int _vblankStartInstructions = 512;
    private int _vblankStopInstructions = 256;
    private int _traceTailIndex;
    private int _traceTailCount;
    private readonly string[] _traceTailLines = new string[128];

    public System32Adapter()
    {
        _sound = new System32Sound(_bus.SharedRam);
        _bus.AttachSound(_sound);
    }

    private readonly record struct MixerLayer(
        int Index,
        int EffectivePriority,
        int PaletteBase,
        int MixShift);

    private readonly record struct ArcadeInputState(
        bool Up,
        bool Down,
        bool Left,
        bool Right,
        bool A,
        bool B,
        bool C,
        bool Start,
        bool X,
        bool Y,
        bool Z,
        bool Mode);

    public static bool IsSupportedArchive(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !RomArchiveExtractor.IsArchivePath(path))
            return false;

        string name = Path.GetFileNameWithoutExtension(path).Trim().ToLowerInvariant();
        return name is "ga2" or "ga2u" or "ga2j";
    }

    public void LoadRom(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("Sega System 32 ROM path is empty.", nameof(path));
        if (!File.Exists(path))
            throw new FileNotFoundException("Sega System 32 ROM archive not found.", path);

        _roms = System32RomSet.LoadGoldenAxe2(path);
        _bus.Load(_roms);
        _sound.Load(_roms);
        _traceBoot = ReadBoolEnv("EUTHERDRIVE_SYSTEM32_TRACE_BOOT");
        _traceMcu = ReadBoolEnv("EUTHERDRIVE_SYSTEM32_TRACE_MCU");
        _traceTail = ReadBoolEnv("EUTHERDRIVE_SYSTEM32_TRACE_TAIL");
        _videoStats = ReadBoolEnv("EUTHERDRIVE_SYSTEM32_VIDEO_STATS");
        _mainCpuInstructionsPerFrame = ReadPositiveIntEnv("EUTHERDRIVE_SYSTEM32_MAINCPU_SLICE", 4096);
        _vblankStartInstructions = ReadPositiveIntEnv("EUTHERDRIVE_SYSTEM32_VBLANK_START_SLICE", 512);
        _vblankStopInstructions = ReadPositiveIntEnv("EUTHERDRIVE_SYSTEM32_VBLANK_STOP_SLICE", 256);
        _bus.ConfigureMainCpuTiming(_mainCpuInstructionsPerFrame, _vblankStartInstructions, _vblankStopInstructions);
        _loaded = true;
        Reset();
    }

    public void Reset()
    {
        _bus.Reset();
        _sound.ResetSound();
        _bus.ConfigureMainCpuTiming(_mainCpuInstructionsPerFrame, _vblankStartInstructions, _vblankStopInstructions);
        if (_roms is not null)
        {
            _mainCpu.Reset(_bus);
            _mcu.Reset(_bus);
        }
        lock (_frameSync)
        {
            Array.Clear(_presentFrameBuffer);
            Array.Clear(_snapshotFrameBuffer);
            _presentVisibleWidth = 320;
        }
        Array.Clear(_renderFrameBuffer);
        Array.Fill(_spriteVisiblePixels, (ushort)0xffff);
        Array.Fill(_spriteRenderPixels, (ushort)0xffff);
        _spriteVisibleNumber = 6;
        _spriteRenderNumber = 8;
        _bus.SetSpriteBufferStatus(_spriteVisibleNumber < _spriteRenderNumber);
        _audioBuffer = new short[(OutputSampleRate / 60) * OutputChannels];
        _cpuStoppedLogged = false;
        _mcuStoppedLogged = false;
        _frameCounter = 0;
        _traceTailIndex = 0;
        _traceTailCount = 0;
        Array.Clear(_traceTailLines);
    }

    public void RunFrame()
    {
        if (!_loaded)
            return;

        _bus.SetInput(_input.Up, _input.Down, _input.Left, _input.Right, _input.A, _input.B, _input.C, _input.Start, _input.Mode);

        ExecuteMcuSlice();
        ExecuteMainCpuSlice(_mainCpuInstructionsPerFrame);

        _visibleWidth = GetVisibleWidth();
        if (_bus.DisplayEnabled)
        {
            Array.Clear(_renderFrameBuffer);
            ClearLayerBuffers();
            RenderBackgroundLayer();
            RenderTileLayers();
            RenderBitmapLayer();
            RenderTextLayer();
            MixLayers();
            LogVideoStats();
        }
        else
        {
            Array.Clear(_renderFrameBuffer);
        }

        _bus.SignalVblankStartIrq();
        ExecuteMainCpuSlice(_vblankStartInstructions);
        _bus.SignalVblankStopIrq();
        ProcessSpriteEndOfVblank(_bus.EndFrame());
        ExecuteMainCpuSlice(_vblankStopInstructions);
        _sound.RunFrame(_audioBuffer);

        lock (_frameSync)
        {
            Buffer.BlockCopy(_renderFrameBuffer, 0, _presentFrameBuffer, 0, _renderFrameBuffer.Length);
            _presentVisibleWidth = _visibleWidth;
        }

        _ = _roms;
        _ = _input;
        _frameCounter++;
    }

    public ReadOnlySpan<byte> GetFrameBuffer(out int width, out int height, out int stride)
    {
        lock (_frameSync)
        {
            Buffer.BlockCopy(_presentFrameBuffer, 0, _snapshotFrameBuffer, 0, _presentFrameBuffer.Length);
            width = _presentVisibleWidth;
            height = FrameHeight;
            stride = FrameStride;
            return _snapshotFrameBuffer;
        }
    }

    public ReadOnlySpan<short> GetAudioBuffer(out int sampleRate, out int channels)
    {
        sampleRate = OutputSampleRate;
        channels = OutputChannels;
        return _audioBuffer;
    }

    public double GetTargetFps() => 60.0;

    private void ExecuteMainCpuSlice(int instructionBudget)
    {
        if (_mainCpu.Halted)
        {
            LogCpuStopOnce();
            return;
        }

        for (int i = 0; i < instructionBudget && !_mainCpu.Halted; i++)
        {
            int vector = _bus.GetPendingV60InterruptVector();
            if (vector >= 0)
                _mainCpu.TryInterrupt(vector);

            uint pc = _mainCpu.Pc;
            int cycles = _mainCpu.ExecuteInstruction();
            _bus.AdvanceMainCpuTimers(1);
            string? traceLine = null;
            if (_traceBoot || _traceTail)
                traceLine = string.Create(
                    CultureInfo.InvariantCulture,
                    $"[System32 V60] pc=0x{pc:X8} op=0x{_mainCpu.LastOpcode:X4} cycles={cycles} next=0x{_mainCpu.Pc:X8} r0=0x{_mainCpu.DebugRegister(0):X8} r1=0x{_mainCpu.DebugRegister(1):X8} r27=0x{_mainCpu.DebugRegister(27):X8} r28=0x{_mainCpu.DebugRegister(28):X8} sp=0x{_mainCpu.DebugRegister(31):X8}");
            if (_traceTail && traceLine is not null)
                RecordCpuTrace(traceLine);
            if (_traceBoot && traceLine is not null)
                Console.WriteLine(traceLine);
        }

        LogCpuStopOnce();
    }

    private void ExecuteMcuSlice()
    {
        if (_mcu.Halted)
        {
            LogMcuStopOnce();
            return;
        }

        for (int i = 0; i < 30000 && !_mcu.Halted; i++)
        {
            uint pc = _mcu.Pc;
            _mcu.ExecuteInstruction();
            if (_traceMcu)
            {
                Console.WriteLine(string.Create(
                    CultureInfo.InvariantCulture,
                    $"[System32 V25] pc=0x{pc:X5} op=0x{_mcu.LastOpcode:X2} next=0x{_mcu.Pc:X5}"));
            }
        }

        LogMcuStopOnce();
    }

    private void LogMcuStopOnce()
    {
        if (!_mcu.Halted || _mcuStoppedLogged)
            return;

        _mcuStoppedLogged = true;
        Console.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"[System32 V25] stopped at pc=0x{_mcu.PreviousPc:X5}: {_mcu.LastStopReason}"));
    }

    private void LogCpuStopOnce()
    {
        if (!_mainCpu.Halted || _cpuStoppedLogged)
            return;

        _cpuStoppedLogged = true;
        if (_traceTail)
            DumpCpuTraceTail();
        Console.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"[System32 V60] stopped at pc=0x{_mainCpu.PreviousPc:X8}: {_mainCpu.LastStopReason}"));
    }

    private void RecordCpuTrace(string line)
    {
        _traceTailLines[_traceTailIndex] = line;
        _traceTailIndex = (_traceTailIndex + 1) % _traceTailLines.Length;
        if (_traceTailCount < _traceTailLines.Length)
            _traceTailCount++;
    }

    private void DumpCpuTraceTail()
    {
        Console.WriteLine("[System32 V60] last instructions:");
        int start = (_traceTailIndex - _traceTailCount + _traceTailLines.Length) % _traceTailLines.Length;
        for (int i = 0; i < _traceTailCount; i++)
        {
            string? line = _traceTailLines[(start + i) % _traceTailLines.Length];
            if (line is not null)
                Console.WriteLine(line);
        }
    }

    private void LogVideoStats()
    {
        if (!_videoStats || (_frameCounter % 60) != 0)
            return;

        var stats = _bus.GetVideoStats();
        Console.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"[System32 Video] frame={_frameCounter} pc=0x{_mainCpu.Pc:X8} r0=0x{_mainCpu.DebugRegister(0):X8} r7=0x{_mainCpu.DebugRegister(7):X8} r10=0x{_mainCpu.DebugRegister(10):X8} r11=0x{_mainCpu.DebugRegister(11):X8} vram={stats.VideoBytes} pal={stats.PaletteBytes} spr={stats.SpriteBytes} screen=0x{stats.ScreenControl:X4} layer=0x{_bus.ReadVideoWord(0x1ff02):X4} pages={_bus.ReadVideoWord(0x1ff40):X4}/{_bus.ReadVideoWord(0x1ff42):X4}/{_bus.ReadVideoWord(0x1ff44):X4}/{_bus.ReadVideoWord(0x1ff46):X4} spr0={_bus.ReadSpriteWord(0):X4}/{_bus.ReadSpriteWord(1):X4}/{_bus.ReadSpriteWord(2):X4}/{_bus.ReadSpriteWord(3):X4} spr1={_bus.ReadSpriteWord(8):X4}/{_bus.ReadSpriteWord(9):X4}/{_bus.ReadSpriteWord(10):X4}/{_bus.ReadSpriteWord(11):X4} spr2={_bus.ReadSpriteWord(16):X4}/{_bus.ReadSpriteWord(17):X4}/{_bus.ReadSpriteWord(18):X4}/{_bus.ReadSpriteWord(19):X4} text=0x{stats.TextControl:X4} dpr80=0x{_bus.ReadDpramWord(0x80):X4} dpr100='{_bus.ReadDpramAscii(0x100, 24)}' tile_px={_lastTilePixels} spr_px={_lastSpritePixels} text_tiles={_lastTextTiles} text_px={_lastTextPixels}"));
    }

    private void ClearLayerBuffers()
    {
        for (int layer = 0; layer < _layerPixels.Length; layer++)
        {
            Array.Clear(_layerPixels[layer]);
            Array.Fill(_layerTransparent[layer], true);
        }
    }

    private int GetVisibleWidth()
    {
        return (_bus.ReadVideoWord(0x1ff00) & 0x8000) != 0 ? 416 : 320;
    }

    private void RenderBackgroundLayer()
    {
        ushort backgroundControl = _bus.ReadVideoWord(0x1ff5e);
        ushort[] layer = _layerPixels[LayerBackground];
        for (int y = 0; y < FrameHeight; y++)
        {
            int color;
            if ((backgroundControl & 0x8000) != 0)
            {
                int yOffset = (backgroundControl + y) & 0x1ff;
                color = (backgroundControl & 0x1e00) + yOffset;
            }
            else
            {
                color = backgroundControl & 0x1e00;
            }

            int row = y * FrameWidth;
            for (int x = 0; x < _visibleWidth; x++)
                layer[row + x] = (ushort)color;
            _layerTransparent[LayerBackground][y] = false;
        }
    }

    private void RenderTileLayers()
    {
        if (_roms is null)
            return;

        int pixels = 0;
        ushort screenControl = _bus.ReadVideoWord(0x1ff00);
        ushort layerDisable = _bus.ReadVideoWord(0x1ff02);
        ushort mixerDisable = _bus.ReadVideoWord(0x1ff8e);
        int tileBank = ((_bus.TileBankExternal & 1) << 1) | (((screenControl & 0x0400) != 0) ? 1 : 0);

        for (int bgnum = 3; bgnum >= 0; bgnum--)
        {
            bool enabled = (layerDisable & (1 << bgnum)) == 0 && (mixerDisable & (1 << (bgnum + 1))) == 0;
            if (bgnum == 2 && (screenControl & 0x1000) != 0)
                enabled = false;
            if (bgnum == 3 && (screenControl & 0x2000) != 0)
                enabled = false;
            if (!enabled)
                continue;

            pixels += bgnum < 2
                ? RenderZoomTileLayer(bgnum, tileBank)
                : RenderRowscrollTileLayer(bgnum, tileBank);
        }

        _lastTilePixels = pixels;
    }

    private int RenderZoomTileLayer(int bgnum, int tileBank)
    {
        ushort screenControl = _bus.ReadVideoWord(0x1ff00);
        int dstXStep = _bus.ReadVideoWord(0x1ff50 + 4 * bgnum) & 0x0fff;
        int dstYStep = (screenControl & 0x4000) != 0
            ? _bus.ReadVideoWord(0x1ff52 + 4 * bgnum) & 0x0fff
            : dstXStep;
        if (dstXStep < 0x80)
            dstXStep = 0x80;
        if (dstYStep < 0x80)
            dstYStep = 0x80;

        long srcXStep = ((long)0x200 << 20) / dstXStep;
        long srcYStep = ((long)0x200 << 20) / dstYStep;
        long srcXStart = (long)(_bus.ReadVideoWord(0x1ff12 + 8 * bgnum) & 0x03ff) << 20;
        srcXStart += (long)(_bus.ReadVideoWord(0x1ff10 + 8 * bgnum) & 0xff00) << 4;
        long srcY = (long)(_bus.ReadVideoWord(0x1ff16 + 8 * bgnum) & 0x01ff) << 20;
        srcY += (long)(_bus.ReadVideoWord(0x1ff14 + 8 * bgnum) & 0xfe00) << 4;

        srcXStart -= (long)SignExtendBits(_bus.ReadVideoWord(0x1ff30 + 4 * bgnum), dstXStep != 0x200 ? 10 : 9) * srcXStep;
        srcY -= (long)SignExtendBits(_bus.ReadVideoWord(0x1ff32 + 4 * bgnum), dstYStep != 0x200 ? 10 : 9) * srcYStep;

        ComputeTilemapFlips(bgnum, out bool flipX, out bool flipY);
        if (flipY)
        {
            srcY += (FrameHeight - 1) * srcYStep;
            srcYStep = -srcYStep;
        }

        if (flipX)
        {
            srcXStart += (_visibleWidth - 1) * srcXStep;
            srcXStep = -srcXStep;
        }

        int pixels = 0;
        for (int y = 0; y < FrameHeight; y++, srcY += srcYStep)
        {
            long srcX = srcXStart;
            int sourceY = (int)((srcY >> 20) & 0x1ff);
            for (int x = 0; x < _visibleWidth; x++, srcX += srcXStep)
            {
                if (!IsTileLayerPixelDrawn(bgnum, x, y))
                    continue;

                int sourceX = (int)((srcX >> 20) & 0x3ff);
                if (!TryReadTilePixel(bgnum, tileBank, sourceX, sourceY, out int rawPixel))
                    continue;

                PutLayerPixel(bgnum + 1, x, y, (ushort)rawPixel);
                pixels++;
            }
        }

        return pixels;
    }

    private int RenderRowscrollTileLayer(int bgnum, int tileBank)
    {
        ushort control = _bus.ReadVideoWord(0x1ff04);
        bool rowScroll = ((control >> (bgnum - 2)) & 1) != 0;
        bool rowSelect = ((control >> bgnum) & 1) != 0;
        if (((control >> (bgnum + 2)) & 1) != 0)
        {
            rowScroll = false;
            rowSelect = false;
        }

        int tableBase = (control >> 10) * 0x800;
        int xScroll = (_bus.ReadVideoWord(0x1ff12 + 8 * bgnum) & 0x03ff) - (_bus.ReadVideoWord(0x1ff30 + 4 * bgnum) & 0x01ff);
        int yScroll = _bus.ReadVideoWord(0x1ff16 + 8 * bgnum) & 0x01ff;
        ComputeTilemapFlips(bgnum, out bool flipX, out bool flipY);

        int pixels = 0;
        for (int y = 0; y < FrameHeight; y++)
        {
            int yLookup = flipY ? FrameHeight - 1 - y : y;
            int sourceY = yScroll + yLookup;
            int sourceXBase = xScroll;
            if (rowScroll)
                sourceXBase += _bus.ReadVideoWord(tableBase + (0x000 + 0x100 * (bgnum - 2) + yLookup) * 2) & 0x03ff;
            if (rowSelect)
                sourceY = yScroll + _bus.ReadVideoWord(tableBase + (0x200 + 0x100 * (bgnum - 2) + yLookup) * 2);

            sourceY &= 0x01ff;
            for (int x = 0; x < _visibleWidth; x++)
            {
                if (!IsTileLayerPixelDrawn(bgnum, x, y))
                    continue;

                int sourceX = (flipX ? _visibleWidth - 1 - x : x) + sourceXBase;
                if (!TryReadTilePixel(bgnum, tileBank, sourceX & 0x03ff, sourceY, out int rawPixel))
                    continue;

                PutLayerPixel(bgnum + 1, x, y, (ushort)rawPixel);
                pixels++;
            }
        }

        return pixels;
    }

    private bool TryReadTilePixel(int bgnum, int tileBank, int sourceX, int sourceY, out int rawPixel)
    {
        rawPixel = 0;
        if (_roms is null)
            return false;

        ushort pageWord0 = _bus.ReadVideoWord(0x1ff40 + bgnum * 4);
        ushort pageWord1 = _bus.ReadVideoWord(0x1ff42 + bgnum * 4);
        int pageIndex = ((sourceY >> 7) & 2) | ((sourceX >> 9) & 1);
        int page = pageIndex switch
        {
            0 => pageWord0 & 0x7f,
            1 => (pageWord0 >> 8) & 0x7f,
            2 => pageWord1 & 0x7f,
            _ => (pageWord1 >> 8) & 0x7f
        };

        int localX = sourceX & 0x0f;
        int localY = sourceY & 0x0f;
        int tileX = (sourceX & 0x1ff) >> 4;
        int tileY = (sourceY & 0x0ff) >> 4;
        ushort tile = _bus.ReadVideoWord(page * 0x400 + (tileY * 32 + tileX) * 2);
        if ((tile & 0x4000) != 0)
            localX = 15 - localX;
        if ((tile & 0x8000) != 0)
            localY = 15 - localY;

        int pen = ReadTilePen(_roms.Tiles, (tileBank << 13) | (tile & 0x1fff), localX, localY);
        if (pen == 0)
            return false;

        rawPixel = (((tile >> 4) & 0x1ff) << 4) | pen;
        return true;
    }

    private void ComputeTilemapFlips(int bgnum, out bool flipX, out bool flipY)
    {
        ushort control = _bus.ReadVideoWord(0x1ff00);
        bool globalFlip = (control & 0x0200) != 0;
        bool layerFlip = (control & (1 << bgnum)) != 0;
        bool prohibitFlipY = (control & 0x0100) != 0;
        flipX = layerFlip ? !globalFlip : globalFlip;
        flipY = layerFlip && !prohibitFlipY ? !globalFlip : globalFlip;
    }

    private bool IsTileLayerPixelDrawn(int bgnum, int x, int y)
    {
        ushort layerControl = _bus.ReadVideoWord(0x1ff02);
        bool clipEnable = ((layerControl >> (11 + bgnum)) & 1) != 0;
        if (!clipEnable)
            return true;

        bool clipOut = ((layerControl >> (6 + bgnum)) & 1) != 0;
        int clipMask = (_bus.ReadVideoWord(0x1ff06) >> (4 * bgnum)) & 0x0f;
        bool inside = false;
        for (int i = 0; i < 5; i++)
        {
            if ((clipMask & (1 << i)) == 0)
                continue;

            int minX = _bus.ReadVideoWord(0x1ff60 + i * 8) & 0x1ff;
            int minY = _bus.ReadVideoWord(0x1ff62 + i * 8) & 0x0ff;
            int maxX = _bus.ReadVideoWord(0x1ff64 + i * 8) & 0x1ff;
            int maxY = _bus.ReadVideoWord(0x1ff66 + i * 8) & 0x0ff;
            if (x >= minX && x <= maxX && y >= minY && y <= maxY)
            {
                inside = true;
                break;
            }
        }

        return clipOut ? !inside : inside;
    }

    private void ProcessSpriteEndOfVblank(byte spriteCommand)
    {
        if ((spriteCommand & 0x02) != 0)
            Array.Fill(_spriteVisiblePixels, (ushort)0xffff);

        if ((spriteCommand & 0x01) == 0)
            return;

        (_spriteVisiblePixels, _spriteRenderPixels) = (_spriteRenderPixels, _spriteVisiblePixels);
        (_spriteVisibleNumber, _spriteRenderNumber) = (_spriteRenderNumber, _spriteVisibleNumber);
        _bus.SetSpriteBufferStatus(_spriteVisibleNumber < _spriteRenderNumber);
        _bus.LatchSpriteControl();
        RenderSpritesTo(_spriteRenderPixels);
    }

    private void RenderSpritesTo(ushort[] target)
    {
        if (_roms is null)
            return;

        int pixels = 0;
        int spriteNumber = 0;
        int xOffset = 0;
        int yOffset = 0;
        int outerClipMaxX = (_bus.SpriteControlLatchedByte(6) & 1) != 0 ? 415 : 319;
        int clipMinX = 0;
        int clipMinY = 0;
        int clipMaxX = outerClipMaxX;
        int clipMaxY = FrameHeight - 1;
        int clipOutMinX = 0;
        int clipOutMinY = 0;
        int clipOutMaxX = -1;
        int clipOutMaxY = -1;

        for (int entry = 0; entry < 0x2000 && spriteNumber < 0x2000; entry++)
        {
            int wordOffset = (spriteNumber & 0x1fff) * 8;
            ushort command = _bus.ReadSpriteWord(wordOffset);
            switch (command >> 14)
            {
                case 0:
                    pixels += DrawOneSprite(target, wordOffset, xOffset, yOffset, clipMinX, clipMinY, clipMaxX, clipMaxY, clipOutMinX, clipOutMinY, clipOutMaxX, clipOutMaxY);
                    spriteNumber++;
                    break;
                case 1:
                    if ((command & 0x1000) != 0)
                    {
                        clipMinY = Math.Max(0, SignExtend12(command));
                        clipMaxY = Math.Min(FrameHeight - 1, SignExtend12(_bus.ReadSpriteWord(wordOffset + 1)));
                        clipMinX = Math.Max(0, SignExtend12(_bus.ReadSpriteWord(wordOffset + 2)));
                        clipMaxX = Math.Min(outerClipMaxX, SignExtend12(_bus.ReadSpriteWord(wordOffset + 3)));
                    }

                    if ((command & 0x2000) != 0)
                    {
                        clipOutMinY = SignExtend12(_bus.ReadSpriteWord(wordOffset + 4));
                        clipOutMaxY = SignExtend12(_bus.ReadSpriteWord(wordOffset + 5));
                        clipOutMinX = SignExtend12(_bus.ReadSpriteWord(wordOffset + 6));
                        clipOutMaxX = SignExtend12(_bus.ReadSpriteWord(wordOffset + 7));
                    }

                    spriteNumber++;
                    break;
                case 2:
                    if ((command & 0x2000) != 0)
                    {
                        yOffset = SignExtend12(_bus.ReadSpriteWord(wordOffset + 1));
                        xOffset = SignExtend12(_bus.ReadSpriteWord(wordOffset + 2));
                    }

                    spriteNumber = command & 0x1fff;
                    break;
                default:
                    _lastSpritePixels = pixels;
                    return;
            }
        }

        _lastSpritePixels = pixels;
    }

    private int DrawOneSprite(ushort[] target, int wordOffset, int xOffset, int yOffset, int clipMinX, int clipMinY, int clipMaxX, int clipMaxY, int clipOutMinX, int clipOutMinY, int clipOutMaxX, int clipOutMaxY)
    {
        ushort data0 = _bus.ReadSpriteWord(wordOffset + 0);
        bool bpp8 = (data0 & 0x0200) != 0;
        bool flipY = (data0 & 0x0080) != 0;
        bool flipX = (data0 & 0x0040) != 0;
        bool applyY = (data0 & 0x0020) != 0;
        bool applyX = (data0 & 0x0010) != 0;
        int adjustY = (data0 >> 2) & 3;
        int adjustX = data0 & 3;
        ushort data1 = _bus.ReadSpriteWord(wordOffset + 1);
        ushort data2 = _bus.ReadSpriteWord(wordOffset + 2);
        ushort data3 = _bus.ReadSpriteWord(wordOffset + 3);
        int sourceHeight = data1 >> 8;
        int sourceWidth = bpp8 ? data1 & 0x3f : (data1 >> 1) & 0x3f;
        int destHeight = data2 & 0x03ff;
        int destWidth = data3 & 0x03ff;
        int yPos = SignExtend12(_bus.ReadSpriteWord(wordOffset + 4));
        int xPos = SignExtend12(_bus.ReadSpriteWord(wordOffset + 5));
        int address = _bus.ReadSpriteWord(wordOffset + 6) | ((data2 & 0xf000) << 4);
        int bank = ((data3 & 0x0800) >> 11) | ((data3 & 0x4000) >> 13);
        int colorBase = 0x8000 | (_bus.ReadSpriteWord(wordOffset + 7) & (bpp8 ? 0x7f00 : 0x7ff0));

        if (sourceWidth == 0 || sourceHeight == 0 || destWidth == 0 || destHeight == 0)
            return 0;

        if (applyX)
            xPos += xOffset;
        if (applyY)
            yPos += yOffset;

        xPos = AdjustSpritePosition(xPos, destWidth, adjustX);
        yPos = AdjustSpritePosition(yPos, destHeight, adjustY);

        int drawn = 0;
        int xDelta = flipX ? -1 : 1;
        int yDelta = flipY ? -1 : 1;
        if (flipX)
            xPos += destWidth - 1;
        if (flipY)
            yPos += destHeight - 1;

        int hZoom = (((bpp8 ? 4 : 8) * sourceWidth) << 16) / destWidth;
        int vZoom = (sourceHeight << 16) / destHeight;
        int yAccumulator = 0;
        int rowAddress = address;

        for (int yStep = 0, y = yPos; yStep < destHeight; yStep++, y += yDelta)
        {
            if (y >= clipMinY && y <= clipMaxY)
            {
                bool clipOutY = y >= clipOutMinY && y <= clipOutMaxY;
                int xAccumulator = 0;
                int currentAddress = rowAddress;
                int sourcePixelsRead = 0;
                uint packed = 0;

                for (int xStep = 0, x = xPos; xStep < destWidth; xStep++, x += xDelta)
                {
                    int pen;
                    if (!bpp8)
                    {
                        if ((sourcePixelsRead & 7) == 0)
                            packed = ReadSpriteLong(_roms!.Sprites, bank, currentAddress++);
                        int shift = 28 - (sourcePixelsRead & 7) * 4;
                        pen = (int)((packed >> shift) & 0x0f);
                    }
                    else
                    {
                        if ((sourcePixelsRead & 3) == 0)
                            packed = ReadSpriteLong(_roms!.Sprites, bank, currentAddress++);
                        int shift = 24 - (sourcePixelsRead & 3) * 8;
                        pen = (int)((packed >> shift) & 0xff);
                    }

                    bool clippedOut = clipOutY && x >= clipOutMinX && x <= clipOutMaxX;
                    if (pen != 0 && pen != (bpp8 ? 0xff : 0x0f) && x >= clipMinX && x <= clipMaxX && !clippedOut)
                    {
                        target[y * FrameWidth + x] = (ushort)(colorBase | pen);
                        drawn++;
                    }

                    xAccumulator += hZoom;
                    while (xAccumulator >= 0x10000)
                    {
                        sourcePixelsRead++;
                        xAccumulator -= 0x10000;
                    }
                }
            }

            yAccumulator += vZoom;
            rowAddress += sourceWidth * (yAccumulator >> 16);
            yAccumulator &= 0xffff;
        }

        return drawn;
    }

    private static int AdjustSpritePosition(int position, int size, int adjust)
    {
        return adjust switch
        {
            1 => position - size + 1,
            2 => position,
            _ => position - ((size - 1) / 2)
        };
    }

    private static int SignExtend12(ushort value)
    {
        int result = value & 0x0fff;
        return (result & 0x0800) != 0 ? result - 0x1000 : result;
    }

    private static int SignExtendBits(ushort value, int bits)
    {
        int mask = (1 << bits) - 1;
        int sign = 1 << (bits - 1);
        int result = value & mask;
        return (result & sign) != 0 ? result - (1 << bits) : result;
    }

    private void RenderTextLayer()
    {
        ushort control = _bus.ReadVideoWord(0x1ff00);
        int width = _visibleWidth;
        int textControl = _bus.ReadVideoWord(0x1ff5c);
        int tileBase = ((textControl >> 4) & 0x1f) * 0x800 * 2;
        int gfxBase = (textControl & 7) * 0x2000 * 2;
        bool flipped = (control & 0x0200) != 0;

        int columns = Math.Min(width / 8, 64);
        int nonZeroTiles = 0;
        int drawnPixels = 0;
        for (int tileY = 0; tileY < 28; tileY++)
        {
            for (int tileX = 0; tileX < columns; tileX++)
            {
                ushort tile = _bus.ReadVideoWord(tileBase + (tileY * 64 + tileX) * 2);
                if (tile != 0)
                    nonZeroTiles++;

                int tileIndex = tile & 0x1ff;
                int colorBase = (tile & 0xfe00) >> 5;
                int glyph = gfxBase + tileIndex * 16 * 2;

                for (int row = 0; row < 8; row++)
                {
                    ushort pixels0 = _bus.ReadVideoWord(glyph + row * 4);
                    ushort pixels1 = _bus.ReadVideoWord(glyph + row * 4 + 2);
                    drawnPixels += DrawTextPixel(tileX, tileY, row, 0, (pixels0 >> 4) & 0x0f, colorBase, flipped, width);
                    drawnPixels += DrawTextPixel(tileX, tileY, row, 1, pixels0 & 0x0f, colorBase, flipped, width);
                    drawnPixels += DrawTextPixel(tileX, tileY, row, 2, (pixels0 >> 12) & 0x0f, colorBase, flipped, width);
                    drawnPixels += DrawTextPixel(tileX, tileY, row, 3, (pixels0 >> 8) & 0x0f, colorBase, flipped, width);
                    drawnPixels += DrawTextPixel(tileX, tileY, row, 4, (pixels1 >> 4) & 0x0f, colorBase, flipped, width);
                    drawnPixels += DrawTextPixel(tileX, tileY, row, 5, pixels1 & 0x0f, colorBase, flipped, width);
                    drawnPixels += DrawTextPixel(tileX, tileY, row, 6, (pixels1 >> 12) & 0x0f, colorBase, flipped, width);
                    drawnPixels += DrawTextPixel(tileX, tileY, row, 7, (pixels1 >> 8) & 0x0f, colorBase, flipped, width);
                }
            }
        }

        _lastTextTiles = nonZeroTiles;
        _lastTextPixels = drawnPixels;
    }

    private int DrawTextPixel(int tileX, int tileY, int row, int column, int pen, int colorBase, bool flipped, int visibleWidth)
    {
        if (pen == 0)
            return 0;

        int x = tileX * 8 + column;
        int y = tileY * 8 + row;
        if (flipped)
        {
            x = visibleWidth - 1 - x;
            y = FrameHeight - 1 - y;
        }

        if ((uint)x >= FrameWidth || (uint)y >= FrameHeight)
            return 0;

        PutLayerPixel(LayerText, x, y, (ushort)(colorBase | pen));
        return 1;
    }

    private void RenderBitmapLayer()
    {
        ushort layerDisable = _bus.ReadVideoWord(0x1ff02);
        ushort mixerDisable = _bus.ReadVideoWord(0x1ff8e);
        if ((layerDisable & 0x0020) != 0 || (mixerDisable & 0x0020) != 0)
            return;

        ushort screenControl = _bus.ReadVideoWord(0x1ff00);
        int bpp = (screenControl & 0x0800) != 0 ? 8 : 4;
        int xScroll = _bus.ReadVideoWord(0x1ff88) & 0x01ff;
        int yScroll = _bus.ReadVideoWord(0x1ff8a) & 0x01ff;
        int color = (_bus.ReadVideoWord(0x1ff8c) << 4) & 0x1fff0 & ~((1 << bpp) - 1);
        bool clipEnable = (layerDisable & 0x8000) != 0;
        bool clipOut = (layerDisable & 0x0400) != 0;

        for (int y = 0; y < FrameHeight; y++)
        {
            bool any = false;
            int sourceY = (y + yScroll) & (bpp == 8 ? 0x0ff : 0x1ff);
            for (int x = 0; x < _visibleWidth; x++)
            {
                if (!IsBitmapPixelDrawn(x, y, clipEnable, clipOut))
                    continue;

                int sourceX = (x + xScroll) & 0x01ff;
                int pen;
                if (bpp == 8)
                {
                    pen = ReadVideoByte(sourceY * 512 + sourceX);
                    if (pen == 0)
                        continue;
                }
                else
                {
                    ushort packed = _bus.ReadVideoWord(sourceY * 256 + (sourceX >> 2) * 2);
                    pen = (packed >> (4 * (sourceX & 3))) & 0x0f;
                    if (pen == 0)
                        continue;
                }

                _layerPixels[LayerBitmap][y * FrameWidth + x] = (ushort)(color + pen);
                any = true;
            }

            _layerTransparent[LayerBitmap][y] = !any;
        }
    }

    private bool IsBitmapPixelDrawn(int x, int y, bool clipEnable, bool clipOut)
    {
        if (!clipEnable)
            return true;

        int minX = _bus.ReadVideoWord(0x1ff60 + 4 * 8) & 0x1ff;
        int minY = _bus.ReadVideoWord(0x1ff62 + 4 * 8) & 0x0ff;
        int maxX = _bus.ReadVideoWord(0x1ff64 + 4 * 8) & 0x1ff;
        int maxY = _bus.ReadVideoWord(0x1ff66 + 4 * 8) & 0x0ff;
        bool inside = x >= minX && x <= maxX && y >= minY && y <= maxY;
        return clipOut ? !inside : inside;
    }

    private byte ReadVideoByte(int byteOffset)
    {
        ushort value = _bus.ReadVideoWord(byteOffset & ~1);
        return (byte)(((byteOffset & 1) == 0) ? value : value >> 8);
    }

    private void PutLayerPixel(int layer, int x, int y, ushort value)
    {
        _layerPixels[layer][y * FrameWidth + x] = value;
        _layerTransparent[layer][y] = false;
    }

    private void MixLayers()
    {
        MixerLayer[] baseLayers = BuildBaseMixerLayers(out int baseLayerCount);
        ushort spriteControl = _bus.ReadMixerWord(0x4c);
        GetSpriteGroupParameters(spriteControl, out int spriteGroupShift, out int spriteGroupMask, out int spriteGroupOr);
        int spritePixelMask = ((1 << spriteGroupShift) - 1) & 0x3fff;

        ushort[] spriteLayer = _spriteVisiblePixels;
        for (int y = 0; y < FrameHeight; y++)
        {
            int row = y * FrameWidth;
            for (int x = 0; x < _visibleWidth; x++)
            {
                int offset = row + x;
                MixerLayer best = default;
                bool found = false;

                for (int i = 0; i < baseLayerCount; i++)
                {
                    MixerLayer candidate = baseLayers[i];
                    ushort raw = _layerPixels[candidate.Index][offset];
                    if (candidate.Index != LayerBackground && raw == 0)
                        continue;
                    if (!found || candidate.EffectivePriority > best.EffectivePriority)
                    {
                        best = candidate;
                        found = true;
                    }
                }

                ushort spriteRaw = spriteLayer[offset];
                if ((spriteRaw & 0x7fff) != 0x7fff)
                {
                    int spriteGroup = (spriteRaw >> spriteGroupShift) & spriteGroupMask;
                    int effectiveGroup = spriteGroupOr | spriteGroup;
                    ushort spriteMixer = _bus.ReadMixerWord(effectiveGroup * 2);
                    int spritePriority = spriteMixer & 0x000f;
                    if (spritePriority != 0)
                    {
                        int spriteEffPriority = (spritePriority << 3) | 7;
                        ushort paletteSource = (spriteControl & 3) != 3 ? spriteMixer : spriteControl;
                        var spriteCandidate = new MixerLayer(
                            LayerSprites,
                            spriteEffPriority,
                            (paletteSource & 0x00f0) << 6,
                            (spriteMixer >> 8) & 3);
                        if (!found || spriteCandidate.EffectivePriority > best.EffectivePriority)
                        {
                            best = spriteCandidate;
                            found = true;
                        }
                    }
                }

                ushort finalRaw = 0;
                if (found)
                {
                    ushort rawPixel = best.Index == LayerSprites ? spriteRaw : _layerPixels[best.Index][offset];
                    if (best.Index == LayerSprites)
                        rawPixel = (ushort)(rawPixel & spritePixelMask);
                    int paletteIndex = best.PaletteBase + ((rawPixel >> best.MixShift) & 0xfff0) + (rawPixel & 0x0f);
                    finalRaw = _bus.ReadPaletteWord(paletteIndex);
                }

                WritePixel(x, y, PaletteRawToBgra(finalRaw));
            }
        }
    }

    private MixerLayer[] BuildBaseMixerLayers(out int count)
    {
        ushort layerDisable = _bus.ReadVideoWord(0x1ff02);
        ushort mixerDisable = _bus.ReadVideoWord(0x1ff8e);
        ushort screenControl = _bus.ReadVideoWord(0x1ff00);
        var layers = new MixerLayer[7];
        count = 0;

        AddMixerLayerIfEnabled(LayerText, (layerDisable & 0x0010) == 0 && (mixerDisable & 0x0001) == 0, layers, ref count);
        AddMixerLayerIfEnabled(LayerNbg0, (layerDisable & 0x0001) == 0 && (mixerDisable & 0x0002) == 0, layers, ref count);
        AddMixerLayerIfEnabled(LayerNbg1, (layerDisable & 0x0002) == 0 && (mixerDisable & 0x0004) == 0, layers, ref count);
        AddMixerLayerIfEnabled(LayerNbg2, (layerDisable & 0x0004) == 0 && (mixerDisable & 0x0008) == 0 && (screenControl & 0x1000) == 0, layers, ref count);
        AddMixerLayerIfEnabled(LayerNbg3, (layerDisable & 0x0008) == 0 && (mixerDisable & 0x0010) == 0 && (screenControl & 0x2000) == 0, layers, ref count);
        AddMixerLayerIfEnabled(LayerBitmap, (layerDisable & 0x0020) == 0 && (mixerDisable & 0x0020) == 0, layers, ref count);

        ushort backgroundMixer = _bus.ReadMixerWord(0x2c);
        layers[count++] = new MixerLayer(
            LayerBackground,
            (1 << 3) | 0,
            (backgroundMixer & 0x00f0) << 6,
            (backgroundMixer >> 8) & 3);

        for (int i = 0; i < count; i++)
        {
            for (int j = i + 1; j < count; j++)
            {
                if (layers[j].EffectivePriority > layers[i].EffectivePriority)
                    (layers[i], layers[j]) = (layers[j], layers[i]);
            }
        }

        return layers;
    }

    private void AddMixerLayerIfEnabled(int layer, bool enabled, MixerLayer[] layers, ref int count)
    {
        if (!enabled)
            return;

        ushort control = _bus.ReadMixerWord(0x20 + layer * 2);
        int priority = control & 0x000f;
        if (priority == 0)
            return;

        layers[count++] = new MixerLayer(
            layer,
            (priority << 3) | (6 - layer),
            (control & 0x00f0) << 6,
            (control >> 8) & 3);
    }

    private void WritePixel(int x, int y, uint bgra)
    {
        int offset = y * FrameStride + x * 4;
        _renderFrameBuffer[offset + 0] = (byte)bgra;
        _renderFrameBuffer[offset + 1] = (byte)(bgra >> 8);
        _renderFrameBuffer[offset + 2] = (byte)(bgra >> 16);
        _renderFrameBuffer[offset + 3] = 0xff;
    }

    private static int ReadTilePen(byte[] tiles, int tileIndex, int x, int y)
    {
        int tileBase = tileIndex * 128;
        if ((uint)(tileBase + 127) >= (uint)tiles.Length)
            return 0;

        int bitOffset = x switch
        {
            0 => 0,
            1 => 4,
            2 => 16,
            3 => 20,
            4 => 8,
            5 => 12,
            6 => 24,
            7 => 28,
            8 => 32,
            9 => 36,
            10 => 48,
            11 => 52,
            12 => 40,
            13 => 44,
            14 => 56,
            _ => 60
        };

        byte packed = tiles[tileBase + y * 8 + (bitOffset >> 3)];
        return (bitOffset & 4) == 0 ? packed & 0x0f : packed >> 4;
    }

    private static uint ReadSpriteLong(byte[] sprites, int bank, int wordAddress)
    {
        int wordIndex = ((bank & 0x0f) << 20) | (wordAddress & 0x0f_ffff);
        int offset = wordIndex * 4;
        if ((uint)(offset + 3) >= (uint)sprites.Length)
            return 0;

        return (uint)((sprites[offset] << 24) | (sprites[offset + 1] << 16) | (sprites[offset + 2] << 8) | sprites[offset + 3]);
    }

    private static uint PaletteRawToBgra(ushort raw)
    {
        int r = Expand5(raw >> 0);
        int g = Expand5(raw >> 5);
        int b = Expand5(raw >> 10);
        return (uint)(b | (g << 8) | (r << 16) | unchecked((int)0xff000000));
    }

    private static void GetSpriteGroupParameters(ushort control, out int groupShift, out int groupMask, out int groupOr)
    {
        switch (control & 0x0f)
        {
            default:
            case 0x0: groupShift = 14; groupMask = 0x00; groupOr = 0x01; break;
            case 0x1: groupShift = 14; groupMask = 0x01; groupOr = 0x02; break;
            case 0x2: groupShift = 13; groupMask = 0x03; groupOr = 0x04; break;
            case 0x3: groupShift = 12; groupMask = 0x07; groupOr = 0x08; break;
            case 0x4: groupShift = 14; groupMask = 0x01; groupOr = 0x00; break;
            case 0x5: groupShift = 13; groupMask = 0x03; groupOr = 0x00; break;
            case 0x6: groupShift = 12; groupMask = 0x07; groupOr = 0x00; break;
            case 0x7: groupShift = 11; groupMask = 0x0f; groupOr = 0x00; break;
            case 0x8: groupShift = 14; groupMask = 0x01; groupOr = 0x00; break;
            case 0x9: groupShift = 13; groupMask = 0x03; groupOr = 0x00; break;
            case 0xa: groupShift = 12; groupMask = 0x07; groupOr = 0x00; break;
            case 0xb: groupShift = 11; groupMask = 0x0f; groupOr = 0x00; break;
            case 0xc: groupShift = 13; groupMask = 0x01; groupOr = 0x00; break;
            case 0xd: groupShift = 12; groupMask = 0x03; groupOr = 0x00; break;
            case 0xe: groupShift = 11; groupMask = 0x07; groupOr = 0x00; break;
            case 0xf: groupShift = 10; groupMask = 0x0f; groupOr = 0x00; break;
        }
    }

    private static int Expand5(int value)
    {
        value &= 0x1f;
        return (value << 3) | (value >> 2);
    }

    private static bool ReadBoolEnv(string name)
    {
        string? raw = Environment.GetEnvironmentVariable(name);
        return raw is "1" || raw?.Equals("true", StringComparison.OrdinalIgnoreCase) == true;
    }

    private static int ReadPositiveIntEnv(string name, int fallback)
    {
        string? raw = Environment.GetEnvironmentVariable(name);
        return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value) && value > 0
            ? value
            : fallback;
    }

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
        _input = new ArcadeInputState(up, down, left, right, a, b, c, start, x, y, z, mode);
    }
}
