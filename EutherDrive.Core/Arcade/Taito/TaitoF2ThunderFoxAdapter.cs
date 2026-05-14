using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using EutherDrive.Core.Cpu.M68000Emu;
using EutherDrive.Core.Cpu.Z80Emu;
using EutherDrive.Core.Savestates;
using SharpCompress.Archives;

namespace EutherDrive.Core.Arcade.Taito;

// Thunder Fox hardware notes and ROM layout are translated from MAME's
// BSD-3-Clause Taito F2 driver by Bryan McPhail and Nicola Salmoria.
public sealed class TaitoF2ThunderFoxAdapter : IEmulatorCore, ISavestateCapable
{
    private const string SavestateMagic = "TAITOF2THUNDFOX";
    private const int SavestateVersion = 1;
    private const int FrameWidth = 320;
    private const int FrameHeight = 224;
    private const int FrameStride = FrameWidth * 4;
    private const int OutputSampleRate = 44_100;
    private const int OutputChannels = 2;
    private const int MainCpuClockHz = 12_000_000;
    private const int CpuCyclesPerFrame = MainCpuClockHz / 60;
    private const double TargetFps = 60.0;

    private readonly byte[] _frameBuffer = new byte[FrameHeight * FrameStride];
    private readonly byte[] _priorityBuffer = new byte[FrameWidth * FrameHeight];
    private readonly M68000 _mainCpu = M68000.CreateBuilder()
        .AllowTasWrites(true)
        .Name("taito-f2-thundfox-main")
        .Build();
    private readonly ThunderFoxBus _bus = new();
    private readonly TaitoF2SoundCpu _soundCpu = new();

    private short[] _audioBuffer = Array.Empty<short>();
    private short[] _scaledAudioBuffer = Array.Empty<short>();
    private ThunderFoxRomSet? _roms;
    private ArcadeInputState _input;
    private bool _loaded;
    private bool _cpuFaulted;
    private string _cpuFault = string.Empty;
    private long _frameCounter;
    private RomIdentity? _romIdentity;
    private bool _spritesDisabled;
    private bool _spritesFlipScreen;
    private int _spritesActiveArea;
    private int _spritesMasterScrollX;
    private int _spritesMasterScrollY;
    private int _masterVolumePercent = 100;
    private int _audioSampleFramesThisFrame;
    private double _audioSampleAccumulator;

    public static bool IsSupportedArchive(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !RomArchiveExtractor.IsArchivePath(path))
            return false;

        string name = Path.GetFileNameWithoutExtension(path).Trim().ToLowerInvariant();
        return name is "thundfox" or "thundfoxu" or "thundfoxj" or "thunderfox";
    }

    public RomIdentity? RomIdentity => _romIdentity;
    public long? FrameCounter => _loaded ? _frameCounter : null;

    public void LoadRom(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("Thunder Fox ROM path is empty.", nameof(path));
        if (!RomArchiveExtractor.FileExists(path))
            throw new FileNotFoundException("Thunder Fox ROM archive not found.", path);

        byte[] romHash;
        using (Stream stream = RomArchiveExtractor.OpenRead(path))
            romHash = RomIdentity.ComputeSha256(stream);

        _roms = ThunderFoxRomSet.Load(path);
        _bus.Load(_roms);
        _soundCpu.Load(_roms.AudioCpu, _roms.AdpcmA, _roms.AdpcmB);
        _bus.AttachSound(_soundCpu);
        _mainCpu.Reset(_bus);
        _loaded = true;
        _cpuFaulted = false;
        _cpuFault = string.Empty;
        _frameCounter = 0;
        _audioSampleAccumulator = 0;
        _audioSampleFramesThisFrame = 0;
        ResetSpriteState();
        _romIdentity = new RomIdentity(
            Path.GetFileName(path),
            romHash,
            PersistentStoragePath.ResolveSavestateDirectory(path, "taito-f2"));

        DrawFrame();
    }

    public void Reset()
    {
        if (_roms == null)
            return;

        _bus.ResetMachine();
        _soundCpu.ResetSound();
        _mainCpu.Reset(_bus);
        _cpuFaulted = false;
        _cpuFault = string.Empty;
        _frameCounter = 0;
        _audioSampleAccumulator = 0;
        _audioSampleFramesThisFrame = 0;
        ResetSpriteState();
        DrawFrame();
    }

    private void ResetSpriteState()
    {
        _spritesDisabled = true;
        _spritesFlipScreen = false;
        _spritesActiveArea = 0;
        _spritesMasterScrollX = 0;
        _spritesMasterScrollY = 0;
    }

    public void SaveState(BinaryWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        if (!_loaded || _roms == null)
            throw new InvalidOperationException("Thunder Fox core not initialized.");

        writer.Write(SavestateMagic);
        writer.Write(SavestateVersion);
        writer.Write(_frameCounter);
        writer.Write(_cpuFaulted);
        writer.Write(_cpuFault);
        writer.Write(_spritesDisabled);
        writer.Write(_spritesFlipScreen);
        writer.Write(_spritesActiveArea);
        writer.Write(_spritesMasterScrollX);
        writer.Write(_spritesMasterScrollY);
        WriteInputState(writer, _input);
        StateBinarySerializer.WriteInto(writer, _mainCpu);
        _bus.SaveState(writer);
        writer.Write(_audioSampleAccumulator);
        writer.Write(_masterVolumePercent);
        _soundCpu.SaveState(writer);
    }

    public void LoadState(BinaryReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);
        if (!_loaded || _roms == null)
            throw new InvalidOperationException("Thunder Fox core not initialized.");

        string magic = reader.ReadString();
        if (!string.Equals(magic, SavestateMagic, StringComparison.Ordinal))
            throw new InvalidDataException("Thunder Fox savestate magic mismatch.");

        int version = reader.ReadInt32();
        if (version != SavestateVersion)
            throw new InvalidDataException($"Unsupported Thunder Fox savestate version: {version}.");

        _frameCounter = reader.ReadInt64();
        _cpuFaulted = reader.ReadBoolean();
        _cpuFault = reader.ReadString();
        _spritesDisabled = reader.ReadBoolean();
        _spritesFlipScreen = reader.ReadBoolean();
        _spritesActiveArea = reader.ReadInt32();
        _spritesMasterScrollX = reader.ReadInt32();
        _spritesMasterScrollY = reader.ReadInt32();
        _input = ReadInputState(reader);
        StateBinarySerializer.ReadInto(reader, _mainCpu);
        _bus.LoadState(reader);
        _audioSampleAccumulator = reader.ReadDouble();
        _masterVolumePercent = reader.ReadInt32();
        _soundCpu.LoadState(reader);
        _audioBuffer = Array.Empty<short>();
        _scaledAudioBuffer = Array.Empty<short>();
        _audioSampleFramesThisFrame = 0;
        DrawFrame();
    }

    public void RunFrame()
    {
        if (!_loaded || _roms == null)
            return;

        _bus.SetInput(_input);

        if (!_cpuFaulted)
        {
            try
            {
                // MAME raises IRQ5 at vblank and schedules IRQ6 about 500 cycles later.
                // Thunder Fox uses both during boot/video-buffer maintenance.
                _bus.AssertInterrupt(5);
                ExecuteMainCpuCycles(500);
                _bus.AssertInterrupt(6);
                ExecuteMainCpuCycles(CpuCyclesPerFrame - 500);
            }
            catch (Exception ex)
            {
                _cpuFaulted = true;
                _cpuFault = ex.GetType().Name + ": " + ex.Message;
            }
            finally
            {
                _bus.ClearInterrupt();
            }
        }

        _audioSampleFramesThisFrame = GetAudioSampleFramesPerFrame();
        EnsureAudioBuffer(_audioSampleFramesThisFrame * OutputChannels);
        _soundCpu.RunFrame(_audioBuffer.AsSpan(0, _audioSampleFramesThisFrame * OutputChannels));
        UpdateSpriteControlStateFromBuffered();
        _bus.BufferSpritesForThunderFox();
        DrawFrame();
        _frameCounter++;
    }

    private void ExecuteMainCpuCycles(int budget)
    {
        int cycles = 0;
        while (cycles < budget && !_mainCpu.IsFrozen && !_mainCpu.AddressError)
            cycles += checked((int)_mainCpu.ExecuteInstruction(_bus));
    }

    public ReadOnlySpan<byte> GetFrameBuffer(out int width, out int height, out int stride)
    {
        width = FrameWidth;
        height = FrameHeight;
        stride = FrameStride;
        return _frameBuffer;
    }

    public ReadOnlySpan<short> GetAudioBuffer(out int sampleRate, out int channels)
    {
        sampleRate = OutputSampleRate;
        channels = OutputChannels;
        int sampleCount = _audioSampleFramesThisFrame * OutputChannels;
        ReadOnlySpan<short> source = _audioBuffer.AsSpan(0, Math.Min(sampleCount, _audioBuffer.Length));
        if (_masterVolumePercent == 100 || source.IsEmpty)
            return source;

        if (_scaledAudioBuffer.Length < source.Length)
            _scaledAudioBuffer = new short[source.Length];

        int volume = _masterVolumePercent;
        for (int i = 0; i < source.Length; i++)
            _scaledAudioBuffer[i] = (short)Math.Clamp((source[i] * volume) / 100, short.MinValue, short.MaxValue);
        return _scaledAudioBuffer.AsSpan(0, source.Length);
    }

    public void SetMasterVolumePercent(int percent)
    {
        _masterVolumePercent = Math.Clamp(percent, 0, 200);
    }

    private int GetAudioSampleFramesPerFrame()
    {
        _audioSampleAccumulator += OutputSampleRate / TargetFps;
        int sampleFrames = (int)_audioSampleAccumulator;
        if (sampleFrames < 1)
            sampleFrames = 1;
        _audioSampleAccumulator -= sampleFrames;
        return sampleFrames;
    }

    private void EnsureAudioBuffer(int samples)
    {
        if (_audioBuffer.Length != samples)
            _audioBuffer = new short[samples];
    }

    public void SetInputState(
        bool up, bool down, bool left, bool right,
        bool a, bool b, bool c, bool start,
        bool x, bool y, bool z, bool mode,
        PadType padType)
    {
        _input = new ArcadeInputState(up, down, left, right, a, b, c, start, x, y, z, mode);
    }

    public bool TryGetFramePerfSummary(out string summary)
    {
        summary = _bus.DebugSummary(_mainCpu, _frameCounter, _cpuFaulted ? _cpuFault : null);
        return true;
    }

    private void DrawFrame()
    {
        Clear(0xff060810);
        Array.Clear(_priorityBuffer);
        if (_roms == null)
        {
            DrawText(12, 16, "TAITO F2 / THUNDER FOX", 0xfff8f8f8, 2);
            DrawText(12, 40, "ROM NOT LOADED", 0xffff6060, 1);
            return;
        }

        int pixels = DrawRuntimeTilemaps();
        if (pixels == 0)
            DrawRomTilePreview();

        if (_cpuFaulted)
            DrawStatusOverlay(pixels);
    }

    private int DrawRuntimeTilemaps()
    {
        int pixels = 0;
        int[][] layer =
        [
            [BottomLayer(_bus.Tc0100Ctrl0), BottomLayer(_bus.Tc0100Ctrl0) ^ 1, 2],
            [BottomLayer(_bus.Tc0100Ctrl1), BottomLayer(_bus.Tc0100Ctrl1) ^ 1, 2]
        ];
        int[][] tilePri =
        [
            new int[3],
            new int[3]
        ];
        tilePri[0][layer[0][0]] = _bus.ReadPriority(5) & 0x0f;
        tilePri[0][layer[0][1]] = _bus.ReadPriority(5) >> 4;
        tilePri[0][layer[0][2]] = _bus.ReadPriority(4) >> 4;
        tilePri[1][layer[1][0]] = _bus.ReadPriority(9) & 0x0f;
        tilePri[1][layer[1][1]] = _bus.ReadPriority(9) >> 4;
        tilePri[1][layer[1][2]] = _bus.ReadPriority(8) >> 4;

        int[] drawn = [0, 0];
        while (drawn[0] < 3 && drawn[1] < 3)
        {
            int pick = tilePri[0][drawn[0]] < tilePri[1][drawn[1]] ? 0 : 1;
            pixels += DrawTc0100Layer(
                pick == 0 ? _bus.Tc0100Ram0 : _bus.Tc0100Ram1,
                pick == 0 ? _bus.Tc0100Ctrl0 : _bus.Tc0100Ctrl1,
                pick == 0 ? _roms!.Screen0 : _roms!.Screen1,
                layer[pick][drawn[pick]],
                false,
                pick,
                (byte)(1 << (drawn[pick] + 3 * pick)));
            drawn[pick]++;
        }

        while (drawn[0] < 3)
        {
            pixels += DrawTc0100Layer(_bus.Tc0100Ram0, _bus.Tc0100Ctrl0, _roms!.Screen0, layer[0][drawn[0]], false, 0, (byte)(1 << drawn[0]));
            drawn[0]++;
        }

        while (drawn[1] < 3)
        {
            pixels += DrawTc0100Layer(_bus.Tc0100Ram1, _bus.Tc0100Ctrl1, _roms!.Screen1, layer[1][drawn[1]], false, 1, (byte)(1 << (drawn[1] + 3)));
            drawn[1]++;
        }

        pixels += DrawSprites(tilePri);
        return pixels;
    }

    private static int BottomLayer(ushort[] ctrl) => (ctrl[6] & 0x08) >> 3;

    private void UpdateSpriteControlStateFromBuffered()
    {
        byte[] ram = _bus.SpriteRamBuffered;
        int activeArea = _spritesActiveArea;
        if (activeArea == 0x8000 && ReadSpriteWord(ram, 0x8000 + 6) == 0 && ReadSpriteWord(ram, 0x8000 + 10) == 0)
            activeArea = 0;

        for (int off = 0; off < 0x4000; off += 16)
        {
            int offs = off + activeArea;
            ushort yWord = ReadSpriteWord(ram, offs + 6);
            if ((yWord & 0x8000) != 0)
            {
                ushort ext = ReadSpriteWord(ram, offs + 10);
                _spritesDisabled = (ext & 0x1000) != 0;
                activeArea = (ext & 0x0001) != 0 ? 0x8000 : 0;
                continue;
            }

            ushort xWord = ReadSpriteWord(ram, offs + 4);
            if ((xWord & 0xf000) == 0xa000)
            {
                _spritesMasterScrollX = SignExtend12(xWord);
                _spritesMasterScrollY = SignExtend12(yWord);
            }
        }

        _spritesActiveArea = activeArea;
    }

    private int DrawSprites(int[][] tilePri)
    {
        byte[] ram = _bus.SpriteRamBuffered;
        byte[] rom = _roms!.Sprites;
        int drawn = 0;
        bool bigSprite = false;
        bool lastContinuationTile = false;
        int yNo = 0;
        int xNo = 0;
        int xLatch = 0;
        int yLatch = 0;
        int xCurrent = 0;
        int yCurrent = 0;
        int zoomXLatch = 0;
        int zoomYLatch = 0;
        int masterScrollX = _spritesMasterScrollX;
        int masterScrollY = _spritesMasterScrollY;
        int extraScrollX = 0;
        int extraScrollY = 0;
        int currentScrollX = 0;
        int currentScrollY = 0;
        int color = 0;
        byte[] spriteMasks = BuildSpritePriorityMasks(tilePri);
        bool disabled = _spritesDisabled;
        bool flipScreen = _spritesFlipScreen;
        int activeArea = _spritesActiveArea;
        if (activeArea == 0x8000 && ReadSpriteWord(ram, 0x8000 + 6) == 0 && ReadSpriteWord(ram, 0x8000 + 10) == 0)
            activeArea = 0;
        var sprites = new List<SpriteDrawCommand>(0x400);
        int x = 0;
        int y = 0;

        for (int off = 0; off < 0x4000; off += 16)
        {
            int offs = off + activeArea;
            ushort codeWord = ReadSpriteWord(ram, offs + 0);
            ushort zoomWord = ReadSpriteWord(ram, offs + 2);
            ushort xWord = ReadSpriteWord(ram, offs + 4);
            ushort yWord = ReadSpriteWord(ram, offs + 6);
            ushort attr = ReadSpriteWord(ram, offs + 8);

            if ((yWord & 0x8000) != 0)
            {
                ushort ext = ReadSpriteWord(ram, offs + 10);
                disabled = (ext & 0x1000) != 0;
                flipScreen = (ext & 0x2000) != 0;
                activeArea = (ext & 0x0001) != 0 ? 0x8000 : 0;
                continue;
            }

            if ((xWord & 0xf000) == 0xa000)
            {
                masterScrollX = SignExtend12(xWord);
                masterScrollY = SignExtend12(yWord);
            }

            if ((xWord & 0xf000) == 0x5000)
            {
                extraScrollX = SignExtend12(xWord);
                extraScrollY = SignExtend12(yWord);
            }

            if (disabled)
                continue;

            int spriteCont = attr >> 8;
            if ((spriteCont & 0x08) != 0)
            {
                if (!bigSprite)
                {
                    xLatch = xWord & 0x0fff;
                    yLatch = yWord & 0x0fff;
                    xNo = 0;
                    yNo = 0;
                    zoomYLatch = (zoomWord >> 8) & 0xff;
                    zoomXLatch = zoomWord & 0xff;
                    bigSprite = true;
                }
            }
            else if (bigSprite)
            {
                lastContinuationTile = true;
            }

            if ((spriteCont & 0x04) == 0)
                color = attr & 0xff;

            if (!bigSprite || (spriteCont & 0xf0) == 0)
            {
                xLatch = xWord & 0x0fff;
                yLatch = yWord & 0x0fff;
                x = xLatch;
                y = yLatch;
                xCurrent = x;
                yCurrent = y;
                if ((xWord & 0x8000) != 0)
                {
                    currentScrollX = -0x63;
                    currentScrollY = 0;
                }
                else if ((xWord & 0x4000) != 0)
                {
                    currentScrollX = masterScrollX - 0x63;
                    currentScrollY = masterScrollY;
                }
                else
                {
                    currentScrollX = masterScrollX + extraScrollX - 0x63;
                    currentScrollY = masterScrollY + extraScrollY;
                }
            }
            else
            {
                if ((spriteCont & 0x10) == 0)
                    y = yCurrent;
                else if ((spriteCont & 0x20) != 0)
                {
                    y += 16;
                    yNo++;
                }
                if ((spriteCont & 0x40) == 0)
                    x = xCurrent;
                else if ((spriteCont & 0x80) != 0)
                {
                    x += 16;
                    yNo = 0;
                    xNo++;
                }
            }

            int width;
            int height;
            if (bigSprite)
            {
                int zoomX = zoomXLatch;
                int zoomY = zoomYLatch;
                width = 16;
                height = 16;
                if (zoomX != 0 || zoomY != 0)
                {
                    x = xLatch + (xNo * (0xff - zoomX) + 15) / 16;
                    y = yLatch + (yNo * (0xff - zoomY) + 15) / 16;
                    width = xLatch + ((xNo + 1) * (0xff - zoomX) + 15) / 16 - x;
                    height = yLatch + ((yNo + 1) * (0xff - zoomY) + 15) / 16 - y;
                }
            }
            else
            {
                int zoomX = zoomWord & 0xff;
                int zoomY = (zoomWord >> 8) & 0xff;
                width = (0x100 - zoomX) / 16;
                height = (0x100 - zoomY) / 16;
            }

            if (lastContinuationTile)
            {
                bigSprite = false;
                lastContinuationTile = false;
            }

            int bank = (codeWord & 0x1c00) >> 10;
            int code = (bank * 0x400) + (codeWord & 0x03ff);
            if (code == 0)
                continue;

            int dstX = SignExtend12((x & 0x0fff) + currentScrollX);
            int dstY = SignExtend12((y & 0x0fff) + currentScrollY);
            bool flipX = (spriteCont & 0x01) != 0;
            bool flipY = (spriteCont & 0x02) != 0;
            if (width <= 0 || height <= 0)
                continue;

            if (flipScreen)
            {
                dstX = 320 - dstX - width;
                dstY = 256 - dstY - height;
                flipX = !flipX;
                flipY = !flipY;
            }

            sprites.Add(new SpriteDrawCommand(code, color, dstX, dstY, width, height, flipX, flipY, spriteMasks[(color >> 6) & 3]));
        }

        for (int i = sprites.Count - 1; i >= 0; i--)
        {
            SpriteDrawCommand sprite = sprites[i];
            drawn += DrawSpriteTile(rom, sprite.Tile, sprite.Color, sprite.X, sprite.Y, sprite.Width, sprite.Height, sprite.FlipX, sprite.FlipY, sprite.PriorityMask);
        }

        _spritesDisabled = disabled;
        _spritesFlipScreen = flipScreen;
        _spritesActiveArea = activeArea;
        _spritesMasterScrollX = masterScrollX;
        _spritesMasterScrollY = masterScrollY;
        return drawn;
    }

    private readonly record struct SpriteDrawCommand(
        int Tile,
        int Color,
        int X,
        int Y,
        int Width,
        int Height,
        bool FlipX,
        bool FlipY,
        byte PriorityMask);

    private byte[] BuildSpritePriorityMasks(int[][] tilePri)
    {
        int[] spritePri =
        [
            _bus.ReadPriority(6) & 0x0f,
            _bus.ReadPriority(6) >> 4,
            _bus.ReadPriority(7) & 0x0f,
            _bus.ReadPriority(7) >> 4
        ];
        var masks = new byte[4];
        for (int i = 0; i < 4; i++)
        {
            if (spritePri[i] < tilePri[0][0]) masks[i] |= 0x01;
            if (spritePri[i] < tilePri[0][1]) masks[i] |= 0x02;
            if (spritePri[i] < tilePri[0][2]) masks[i] |= 0x04;
            if (spritePri[i] < tilePri[1][0]) masks[i] |= 0x08;
            if (spritePri[i] < tilePri[1][1]) masks[i] |= 0x10;
            if (spritePri[i] < tilePri[1][2]) masks[i] |= 0x20;
        }

        return masks;
    }

    private int DrawSpriteTile(byte[] rom, int tile, int color, int dstX, int dstY, int width, int height, bool flipX, bool flipY, byte primask)
    {
        int drawn = 0;
        for (int y = 0; y < height; y++)
        {
            int sy = Math.Min(15, (y * 16) / height);
            if (flipY)
                sy = 15 - sy;
            for (int x = 0; x < width; x++)
            {
                int sx = Math.Min(15, (x * 16) / width);
                if (flipX)
                    sx = 15 - sx;

                int pen = DecodePackedLsb4Sprite(rom, tile, sx, sy);
                if (pen == 0)
                    continue;

                int px = dstX + x;
                int py = dstY + y;
                if ((uint)px >= FrameWidth || (uint)py >= FrameHeight)
                    continue;

                int priorityIndex = (py * FrameWidth) + px;
                byte priority = _priorityBuffer[priorityIndex];
                if ((priority & 0x80) != 0)
                    continue;

                bool hiddenByTile = (priority & primask) != 0;
                _priorityBuffer[priorityIndex] = (byte)(priority | 0x80);
                if (hiddenByTile)
                    continue;

                uint rgb = _bus.ReadPaletteColor((color * 16) + pen, FallbackTileColor(color, pen, 4));
                PutPixel(px, py, rgb);
                drawn++;
            }
        }

        return drawn;
    }

    private int DrawTc0100Layer(byte[] ram, ushort[] ctrl, byte[] tileRom, int layer, bool opaque, int chipIndex, byte priority)
    {
        int disable = ctrl[6] & 0xf7;
        if ((disable & (1 << layer)) != 0)
            return 0;

        int baseWord = layer switch
        {
            0 => 0x0000,
            1 => 0x4000,
            _ => 0x2000
        };
        int columns = 64;
        int rows = 64;
        int scrollDeltaX = chipIndex == 0 ? 19 : 21;
        int scrollDeltaY = chipIndex == 0 ? -8 : -1;

        int drawn = 0;
        for (int y = 0; y < FrameHeight; y++)
        {
            int rowScrollIndex = (y + scrollDeltaY) & 0x1ff;
            int scrollX = layer switch
            {
                0 => -ctrl[0] - ReadTcWord(ram, 0x6000 + rowScrollIndex) + scrollDeltaX,
                1 => -ctrl[1] - ReadTcWord(ram, 0x6200 + rowScrollIndex) + scrollDeltaX,
                _ => -ctrl[2] + scrollDeltaX
            };
            int scrollY = layer switch
            {
                0 => -ctrl[3] + scrollDeltaY,
                1 => -ctrl[4] + scrollDeltaY,
                _ => -ctrl[5] + scrollDeltaY
            };
            int srcY = (y + scrollY) & 0x1ff;
            for (int x = 0; x < FrameWidth; x++)
            {
                int srcX = (x + scrollX) & 0x1ff;
                int layerSrcY = srcY;
                if (layer == 1)
                {
                    int columnOffset = ReadTcWord(ram, 0x7000 + ((srcX & 0x3ff) >> 3));
                    layerSrcY = (srcY - columnOffset) & 0x1ff;
                }
                int tileY = (layerSrcY >> 3) & (rows - 1);
                int pixelYBase = layerSrcY & 7;
                int tileX = (srcX >> 3) & (columns - 1);
                int tileIndex = (tileY * columns) + tileX;
                int color;
                int code;
                int flip;

                if (layer == 2)
                {
                    ushort attr = ReadTcWord(ram, baseWord + tileIndex);
                    if (attr == 0)
                        continue;

                    code = attr & 0x00ff;
                    color = (attr >> 8) & 0x3f;
                    flip = (attr >> 14) & 3;
                }
                else
                {
                    ushort attr = ReadTcWord(ram, baseWord + (tileIndex * 2));
                    code = ReadTcWord(ram, baseWord + (tileIndex * 2) + 1);
                    if (attr == 0 && code == 0)
                        continue;

                    color = attr & 0xff;
                    flip = (attr >> 14) & 3;
                }

                int pixelX = srcX & 7;
                int pixelY = pixelYBase;
                if ((flip & 2) != 0)
                    pixelX = 7 - pixelX;
                if ((flip & 1) != 0)
                    pixelY = 7 - pixelY;

                int pen = layer == 2
                    ? DecodeTextRam2Bpp(ram, code, pixelX, pixelY)
                    : DecodePackedMsb4(tileRom, code, pixelX, pixelY);
                if (pen == 0 && !opaque)
                    continue;

                uint rgb = _bus.ReadPaletteColor((color * 16) + pen, FallbackTileColor(color, pen, layer));
                PutPixelWithPriority(x, y, rgb, priority);
                drawn++;
            }
        }

        return drawn;
    }

    private void DrawRomTilePreview()
    {
        DrawText(10, 8, "THUNDER FOX / TAITO F2", 0xfff8f8f8, 2);
        DrawText(10, 28, "MAME SET: thundfox  CPU BUS ACTIVE  VIDEO RAM WAITING", 0xffe0d080, 1);

        DrawTileAtlas(_roms!.Screen0, 8, 48, 20, 9, 0);
        DrawTileAtlas(_roms.Screen1, 172, 48, 17, 9, 1);
    }

    private void DrawTileAtlas(byte[] rom, int x0, int y0, int columns, int rows, int bank)
    {
        int tile = (_frameCounter == 0) ? 0 : (int)((_frameCounter / 6) & 0x3f);
        for (int ty = 0; ty < rows; ty++)
        {
            for (int tx = 0; tx < columns; tx++)
            {
                DrawPreviewTile(rom, tile++, x0 + tx * 8, y0 + ty * 8, bank);
            }
        }
    }

    private void DrawPreviewTile(byte[] rom, int tile, int dstX, int dstY, int bank)
    {
        for (int y = 0; y < 8; y++)
        {
            for (int x = 0; x < 8; x++)
            {
                int pen = DecodePackedMsb4(rom, tile, x, y);
                if (pen == 0)
                    continue;

                PutPixel(dstX + x, dstY + y, FallbackTileColor((tile + bank * 23) & 0x3f, pen, bank));
            }
        }
    }

    private void DrawStatusOverlay(int tilePixels)
    {
        FillRect(0, FrameHeight - 32, FrameWidth, 32, 0xcc000000);
        string title = _roms?.DriverName ?? "thundfox";
        DrawText(8, FrameHeight - 28, $"{title}  frame={_frameCounter} pc=0x{_mainCpu.Pc:X6}", 0xffffffff, 1);
        DrawText(8, FrameHeight - 16, _bus.DebugSummary(_mainCpu, _frameCounter, _cpuFaulted ? _cpuFault : null), _cpuFaulted ? 0xffff7070 : 0xffa8e8ff, 1);
        if (tilePixels == 0)
            DrawText(8, 204, "RAW SCR ROM PREVIEW UNTIL TC0100SCN RAM IS POPULATED", 0xffffff80, 1);
    }

    private void Clear(uint color)
    {
        byte b = (byte)color;
        byte g = (byte)(color >> 8);
        byte r = (byte)(color >> 16);
        byte a = (byte)(color >> 24);
        for (int i = 0; i < _frameBuffer.Length; i += 4)
        {
            _frameBuffer[i + 0] = b;
            _frameBuffer[i + 1] = g;
            _frameBuffer[i + 2] = r;
            _frameBuffer[i + 3] = a;
        }
    }

    private void PutPixel(int x, int y, uint color)
    {
        if ((uint)x >= FrameWidth || (uint)y >= FrameHeight)
            return;

        int offset = (y * FrameStride) + (x * 4);
        _frameBuffer[offset + 0] = (byte)color;
        _frameBuffer[offset + 1] = (byte)(color >> 8);
        _frameBuffer[offset + 2] = (byte)(color >> 16);
        _frameBuffer[offset + 3] = (byte)(color >> 24);
    }

    private void PutPixelWithPriority(int x, int y, uint color, byte priority)
    {
        PutPixel(x, y, color);
        if ((uint)x < FrameWidth && (uint)y < FrameHeight)
            _priorityBuffer[(y * FrameWidth) + x] |= priority;
    }

    private void FillRect(int x, int y, int width, int height, uint color)
    {
        for (int yy = y; yy < y + height; yy++)
            for (int xx = x; xx < x + width; xx++)
                PutPixel(xx, yy, color);
    }

    private static ushort ReadTcWord(byte[] ram, int wordOffset)
    {
        int offset = wordOffset * 2;
        if ((uint)(offset + 1) >= ram.Length)
            return 0;

        return (ushort)((ram[offset] << 8) | ram[offset + 1]);
    }

    private static ushort ReadSpriteWord(byte[] ram, int byteOffset)
    {
        if ((uint)(byteOffset + 1) >= ram.Length)
            return 0;

        return (ushort)((ram[byteOffset] << 8) | ram[byteOffset + 1]);
    }

    private static int DecodePackedMsb4(byte[] rom, int tile, int x, int y)
    {
        int offset = (tile * 32) + (y * 4) + (x >> 1);
        if ((uint)offset >= rom.Length)
            return 0;

        byte packed = rom[offset];
        return (x & 1) == 0 ? packed & 0x0f : packed >> 4;
    }

    private static int DecodePackedLsb4Sprite(byte[] rom, int tile, int x, int y)
    {
        int offset = (tile * 128) + (y * 8) + (x >> 1);
        if ((uint)offset >= rom.Length)
            return 0;

        byte packed = rom[offset];
        return (x & 1) == 0 ? packed & 0x0f : packed >> 4;
    }

    private static int DecodeTextRam2Bpp(byte[] ram, int tile, int x, int y)
    {
        int offset = 0x6000 + ((tile & 0xff) * 16) + (y * 2);
        if ((uint)(offset + 1) >= ram.Length)
            return 0;

        int bit = 7 - (x & 7);
        int plane0 = (ram[offset] >> bit) & 1;
        int plane1 = (ram[offset + 1] >> bit) & 1;
        return plane0 | (plane1 << 1);
    }

    private static int SignExtend12(int value)
    {
        value &= 0x0fff;
        return (value & 0x0800) != 0 ? value - 0x1000 : value;
    }

    private static uint FallbackTileColor(int palette, int pen, int layer)
    {
        int r = (pen * 15 + palette * 5 + layer * 31) & 0xff;
        int g = (pen * 23 + palette * 11 + layer * 17) & 0xff;
        int b = (pen * 35 + palette * 7 + layer * 43) & 0xff;
        return 0xff000000u | ((uint)r << 16) | ((uint)g << 8) | (uint)b;
    }

    private void DrawText(int x, int y, string text, uint color, int scale)
    {
        int cursor = x;
        foreach (char ch in text)
        {
            if (ch == '\n')
            {
                cursor = x;
                y += 8 * scale;
                continue;
            }

            DrawGlyph(cursor, y, ch, color, scale);
            cursor += 6 * scale;
        }
    }

    private void DrawGlyph(int x, int y, char ch, uint color, int scale)
    {
        ReadOnlySpan<byte> glyph = GetGlyph(ch);
        for (int row = 0; row < 7; row++)
        {
            byte bits = glyph[row];
            for (int col = 0; col < 5; col++)
            {
                if (((bits >> (4 - col)) & 1) == 0)
                    continue;

                for (int sy = 0; sy < scale; sy++)
                    for (int sx = 0; sx < scale; sx++)
                        PutPixel(x + col * scale + sx, y + row * scale + sy, color);
            }
        }
    }

    private static ReadOnlySpan<byte> GetGlyph(char ch)
    {
        return ch switch
        {
            '0' => new byte[] { 0x0e, 0x11, 0x13, 0x15, 0x19, 0x11, 0x0e },
            '1' => new byte[] { 0x04, 0x0c, 0x04, 0x04, 0x04, 0x04, 0x0e },
            '2' => new byte[] { 0x0e, 0x11, 0x01, 0x02, 0x04, 0x08, 0x1f },
            '3' => new byte[] { 0x1e, 0x01, 0x01, 0x0e, 0x01, 0x01, 0x1e },
            '4' => new byte[] { 0x02, 0x06, 0x0a, 0x12, 0x1f, 0x02, 0x02 },
            '5' => new byte[] { 0x1f, 0x10, 0x1e, 0x01, 0x01, 0x11, 0x0e },
            '6' => new byte[] { 0x06, 0x08, 0x10, 0x1e, 0x11, 0x11, 0x0e },
            '7' => new byte[] { 0x1f, 0x01, 0x02, 0x04, 0x08, 0x08, 0x08 },
            '8' => new byte[] { 0x0e, 0x11, 0x11, 0x0e, 0x11, 0x11, 0x0e },
            '9' => new byte[] { 0x0e, 0x11, 0x11, 0x0f, 0x01, 0x02, 0x0c },
            'A' => new byte[] { 0x0e, 0x11, 0x11, 0x1f, 0x11, 0x11, 0x11 },
            'B' => new byte[] { 0x1e, 0x11, 0x11, 0x1e, 0x11, 0x11, 0x1e },
            'C' => new byte[] { 0x0f, 0x10, 0x10, 0x10, 0x10, 0x10, 0x0f },
            'D' => new byte[] { 0x1e, 0x11, 0x11, 0x11, 0x11, 0x11, 0x1e },
            'E' => new byte[] { 0x1f, 0x10, 0x10, 0x1e, 0x10, 0x10, 0x1f },
            'F' => new byte[] { 0x1f, 0x10, 0x10, 0x1e, 0x10, 0x10, 0x10 },
            'G' => new byte[] { 0x0f, 0x10, 0x10, 0x13, 0x11, 0x11, 0x0f },
            'H' => new byte[] { 0x11, 0x11, 0x11, 0x1f, 0x11, 0x11, 0x11 },
            'I' => new byte[] { 0x0e, 0x04, 0x04, 0x04, 0x04, 0x04, 0x0e },
            'J' => new byte[] { 0x01, 0x01, 0x01, 0x01, 0x11, 0x11, 0x0e },
            'K' => new byte[] { 0x11, 0x12, 0x14, 0x18, 0x14, 0x12, 0x11 },
            'L' => new byte[] { 0x10, 0x10, 0x10, 0x10, 0x10, 0x10, 0x1f },
            'M' => new byte[] { 0x11, 0x1b, 0x15, 0x15, 0x11, 0x11, 0x11 },
            'N' => new byte[] { 0x11, 0x19, 0x15, 0x13, 0x11, 0x11, 0x11 },
            'O' => new byte[] { 0x0e, 0x11, 0x11, 0x11, 0x11, 0x11, 0x0e },
            'P' => new byte[] { 0x1e, 0x11, 0x11, 0x1e, 0x10, 0x10, 0x10 },
            'Q' => new byte[] { 0x0e, 0x11, 0x11, 0x11, 0x15, 0x12, 0x0d },
            'R' => new byte[] { 0x1e, 0x11, 0x11, 0x1e, 0x14, 0x12, 0x11 },
            'S' => new byte[] { 0x0f, 0x10, 0x10, 0x0e, 0x01, 0x01, 0x1e },
            'T' => new byte[] { 0x1f, 0x04, 0x04, 0x04, 0x04, 0x04, 0x04 },
            'U' => new byte[] { 0x11, 0x11, 0x11, 0x11, 0x11, 0x11, 0x0e },
            'V' => new byte[] { 0x11, 0x11, 0x11, 0x11, 0x11, 0x0a, 0x04 },
            'W' => new byte[] { 0x11, 0x11, 0x11, 0x15, 0x15, 0x1b, 0x11 },
            'X' => new byte[] { 0x11, 0x11, 0x0a, 0x04, 0x0a, 0x11, 0x11 },
            'Y' => new byte[] { 0x11, 0x11, 0x0a, 0x04, 0x04, 0x04, 0x04 },
            'Z' => new byte[] { 0x1f, 0x01, 0x02, 0x04, 0x08, 0x10, 0x1f },
            '-' => new byte[] { 0x00, 0x00, 0x00, 0x1f, 0x00, 0x00, 0x00 },
            '/' => new byte[] { 0x01, 0x01, 0x02, 0x04, 0x08, 0x10, 0x10 },
            ':' => new byte[] { 0x00, 0x04, 0x04, 0x00, 0x04, 0x04, 0x00 },
            '=' => new byte[] { 0x00, 0x00, 0x1f, 0x00, 0x1f, 0x00, 0x00 },
            'x' => new byte[] { 0x00, 0x11, 0x0a, 0x04, 0x0a, 0x11, 0x00 },
            '.' => new byte[] { 0x00, 0x00, 0x00, 0x00, 0x00, 0x0c, 0x0c },
            ' ' => new byte[] { 0, 0, 0, 0, 0, 0, 0 },
            _ => new byte[] { 0x1f, 0x11, 0x02, 0x04, 0x04, 0x00, 0x04 }
        };
    }

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

    private static void WriteInputState(BinaryWriter writer, ArcadeInputState input)
    {
        writer.Write(input.Up);
        writer.Write(input.Down);
        writer.Write(input.Left);
        writer.Write(input.Right);
        writer.Write(input.A);
        writer.Write(input.B);
        writer.Write(input.C);
        writer.Write(input.Start);
        writer.Write(input.X);
        writer.Write(input.Y);
        writer.Write(input.Z);
        writer.Write(input.Mode);
    }

    private static ArcadeInputState ReadInputState(BinaryReader reader)
        => new(
            reader.ReadBoolean(),
            reader.ReadBoolean(),
            reader.ReadBoolean(),
            reader.ReadBoolean(),
            reader.ReadBoolean(),
            reader.ReadBoolean(),
            reader.ReadBoolean(),
            reader.ReadBoolean(),
            reader.ReadBoolean(),
            reader.ReadBoolean(),
            reader.ReadBoolean(),
            reader.ReadBoolean());

    private static void WriteByteArray(BinaryWriter writer, byte[] data)
    {
        writer.Write(data.Length);
        writer.Write(data);
    }

    private static void ReadByteArray(BinaryReader reader, byte[] destination)
    {
        int length = reader.ReadInt32();
        if (length != destination.Length)
            throw new InvalidDataException($"Thunder Fox byte array length mismatch: expected {destination.Length}, got {length}.");

        int read = reader.Read(destination, 0, destination.Length);
        if (read != destination.Length)
            throw new EndOfStreamException("Unexpected end of Thunder Fox savestate.");
    }

    private static void WriteUshortArray(BinaryWriter writer, ushort[] data)
    {
        writer.Write(data.Length);
        for (int i = 0; i < data.Length; i++)
            writer.Write(data[i]);
    }

    private static void ReadUshortArray(BinaryReader reader, ushort[] destination)
    {
        int length = reader.ReadInt32();
        if (length != destination.Length)
            throw new InvalidDataException($"Thunder Fox ushort array length mismatch: expected {destination.Length}, got {length}.");

        for (int i = 0; i < destination.Length; i++)
            destination[i] = reader.ReadUInt16();
    }

    private sealed class ThunderFoxBus : EutherDrive.Core.Cpu.M68000Emu.IBusInterface, EutherDrive.Core.Cpu.M68000Emu.IOpcodeBusInterface
    {
        private readonly byte[] _program = new byte[0x80000];
        private readonly byte[] _mainRam = new byte[0x10000];
        private readonly byte[] _paletteRam = new byte[0x2000];
        private readonly byte[] _spriteRam = new byte[0x10000];
        private readonly byte[] _spriteRamDelayed = new byte[0x10000];
        private readonly byte[] _spriteRamBuffered = new byte[0x10000];
        private readonly byte[] _priority = new byte[0x10];
        private TaitoF2SoundCpu? _sound;
        private ArcadeInputState _input;
        private byte _interruptLevel;
        private bool _spriteBufferPrimed;
        private int _ramWrites;
        private int _paletteWrites;
        private int _tc0Writes;
        private int _tc1Writes;
        private int _spriteWrites;
        private int _unknownReads;
        private int _unknownWrites;

        public readonly byte[] Tc0100Ram0 = new byte[0x10000];
        public readonly byte[] Tc0100Ram1 = new byte[0x10000];
        public readonly ushort[] Tc0100Ctrl0 = new ushort[0x10];
        public readonly ushort[] Tc0100Ctrl1 = new ushort[0x10];
        public byte[] SpriteRam => _spriteRam;
        public byte[] SpriteRamBuffered => _spriteRamBuffered;

        public BusSignals Signals => default;
        public ushort CurrentOpcode => 0;

        public void Load(ThunderFoxRomSet roms)
        {
            Array.Copy(roms.Program, _program, Math.Min(_program.Length, roms.Program.Length));
            ResetMachine();
        }

        public void AttachSound(TaitoF2SoundCpu sound) => _sound = sound;

        public void ResetMachine()
        {
            Array.Clear(_mainRam);
            Array.Clear(_paletteRam);
            Array.Clear(_spriteRam);
            Array.Clear(_spriteRamDelayed);
            Array.Clear(_spriteRamBuffered);
            Array.Clear(Tc0100Ram0);
            Array.Clear(Tc0100Ram1);
            Array.Clear(Tc0100Ctrl0);
            Array.Clear(Tc0100Ctrl1);
            Array.Clear(_priority);
            _spriteBufferPrimed = false;
            _interruptLevel = 0;
            _ramWrites = 0;
            _paletteWrites = 0;
            _tc0Writes = 0;
            _tc1Writes = 0;
            _spriteWrites = 0;
            _unknownReads = 0;
            _unknownWrites = 0;
        }

        public void SaveState(BinaryWriter writer)
        {
            WriteByteArray(writer, _mainRam);
            WriteByteArray(writer, _paletteRam);
            WriteByteArray(writer, _spriteRam);
            WriteByteArray(writer, _spriteRamDelayed);
            WriteByteArray(writer, _spriteRamBuffered);
            WriteByteArray(writer, Tc0100Ram0);
            WriteByteArray(writer, Tc0100Ram1);
            WriteUshortArray(writer, Tc0100Ctrl0);
            WriteUshortArray(writer, Tc0100Ctrl1);
            WriteByteArray(writer, _priority);
            writer.Write(_interruptLevel);
            writer.Write(_spriteBufferPrimed);
            writer.Write(_ramWrites);
            writer.Write(_paletteWrites);
            writer.Write(_tc0Writes);
            writer.Write(_tc1Writes);
            writer.Write(_spriteWrites);
            writer.Write(_unknownReads);
            writer.Write(_unknownWrites);
        }

        public void LoadState(BinaryReader reader)
        {
            ReadByteArray(reader, _mainRam);
            ReadByteArray(reader, _paletteRam);
            ReadByteArray(reader, _spriteRam);
            ReadByteArray(reader, _spriteRamDelayed);
            ReadByteArray(reader, _spriteRamBuffered);
            ReadByteArray(reader, Tc0100Ram0);
            ReadByteArray(reader, Tc0100Ram1);
            ReadUshortArray(reader, Tc0100Ctrl0);
            ReadUshortArray(reader, Tc0100Ctrl1);
            ReadByteArray(reader, _priority);
            _interruptLevel = reader.ReadByte();
            _spriteBufferPrimed = reader.ReadBoolean();
            _ramWrites = reader.ReadInt32();
            _paletteWrites = reader.ReadInt32();
            _tc0Writes = reader.ReadInt32();
            _tc1Writes = reader.ReadInt32();
            _spriteWrites = reader.ReadInt32();
            _unknownReads = reader.ReadInt32();
            _unknownWrites = reader.ReadInt32();
        }

        public void SetInput(ArcadeInputState input) => _input = input;
        public void AssertInterrupt(byte level) => _interruptLevel = (byte)(level & 7);
        public void ClearInterrupt() => _interruptLevel = 0;

        public void BufferSpritesForThunderFox()
        {
            if (!_spriteBufferPrimed)
            {
                Array.Copy(_spriteRam, _spriteRamBuffered, _spriteRam.Length);
                Array.Copy(_spriteRam, _spriteRamDelayed, _spriteRam.Length);
                _spriteBufferPrimed = true;
                return;
            }

            Array.Copy(_spriteRamDelayed, _spriteRamBuffered, _spriteRamDelayed.Length);
            for (int offset = 0; offset < _spriteRam.Length; offset += 16)
            {
                _spriteRamBuffered[offset + 0] = _spriteRam[offset + 0];
                _spriteRamBuffered[offset + 1] = _spriteRam[offset + 1];
                _spriteRamBuffered[offset + 2] = _spriteRam[offset + 2];
                _spriteRamBuffered[offset + 3] = _spriteRam[offset + 3];
                _spriteRamBuffered[offset + 8] = _spriteRam[offset + 8];
                _spriteRamBuffered[offset + 9] = _spriteRam[offset + 9];
            }
            Array.Copy(_spriteRam, _spriteRamDelayed, _spriteRam.Length);
        }

        public string DebugSummary(M68000 cpu, long frame, string? fault)
        {
            string suffix = string.IsNullOrWhiteSpace(fault) ? string.Empty : " fault=" + fault;
            string sound = _sound?.DebugSummary ?? "snd=none";
            return string.Create(
                CultureInfo.InvariantCulture,
                $"f={frame} sr=0x{cpu.StatusRegister:X4} ramW={_ramWrites} palW={_paletteWrites} tc={_tc0Writes}/{_tc1Writes} sprW={_spriteWrites} unk={_unknownReads}/{_unknownWrites} {sound}{suffix}");
        }

        public uint ReadPaletteColor(int colorIndex, uint fallback)
        {
            int offset = (colorIndex & 0x0fff) * 2;
            if (offset + 1 >= _paletteRam.Length)
                return fallback;

            ushort data = (ushort)((_paletteRam[offset] << 8) | _paletteRam[offset + 1]);
            if (data == 0)
                return fallback;

            int r = Expand4((data >> 12) & 0x0f);
            int g = Expand4((data >> 8) & 0x0f);
            int b = Expand4((data >> 4) & 0x0f);
            return 0xff000000u | ((uint)r << 16) | ((uint)g << 8) | (uint)b;
        }

        public byte ReadByte(uint address)
        {
            address &= 0x00ff_ffff;
            if (address < _program.Length)
                return _program[address];
            if (address is >= 0x100000 and <= 0x101fff)
                return _paletteRam[address - 0x100000];
            if (address is >= 0x300000 and <= 0x30ffff)
                return _mainRam[address - 0x300000];
            if (address is >= 0x400000 and <= 0x40ffff)
                return Tc0100Ram0[address - 0x400000];
            if (address is >= 0x500000 and <= 0x50ffff)
                return Tc0100Ram1[address - 0x500000];
            if (address is >= 0x600000 and <= 0x60ffff)
                return _spriteRam[address - 0x600000];
            if (address is >= 0x420000 and <= 0x42000f)
                return ReadWordByte(Tc0100Ctrl0[(address - 0x420000) >> 1], address);
            if (address is >= 0x520000 and <= 0x52000f)
                return ReadWordByte(Tc0100Ctrl1[(address - 0x520000) >> 1], address);
            if (address is >= 0x200000 and <= 0x20000f)
                return (address & 1) != 0 ? ReadIoRegister((int)((address - 0x200000) >> 1)) : (byte)0xff;
            if (address == 0x220002)
            {
                _sound?.SynchronizeFromMain();
                return _sound?.MasterCommRead() ?? (byte)0xff;
            }
            if (address is >= 0x220000 and <= 0x220003)
                return 0xff;

            _unknownReads++;
            return 0xff;
        }

        public ushort ReadWord(uint address)
        {
            address &= 0x00ff_fffe;
            if (address is >= 0x200000 and <= 0x20000f)
                return (ushort)(0xff00 | ReadIoRegister((int)((address - 0x200000) >> 1)));

            return (ushort)((ReadByte(address) << 8) | ReadByte(address + 1));
        }

        public uint ReadLong(uint address)
            => ((uint)ReadWord(address) << 16) | ReadWord(address + 2);

        public ushort ReadOpcodeWord(uint address)
            => ReadWord(address);

        public void WriteByte(uint address, byte value)
        {
            address &= 0x00ff_ffff;
            if (address is >= 0x100000 and <= 0x101fff)
            {
                _paletteRam[address - 0x100000] = value;
                _paletteWrites++;
                return;
            }
            if (address is >= 0x300000 and <= 0x30ffff)
            {
                _mainRam[address - 0x300000] = value;
                _ramWrites++;
                return;
            }
            if (address is >= 0x400000 and <= 0x40ffff)
            {
                Tc0100Ram0[address - 0x400000] = value;
                _tc0Writes++;
                return;
            }
            if (address is >= 0x500000 and <= 0x50ffff)
            {
                Tc0100Ram1[address - 0x500000] = value;
                _tc1Writes++;
                return;
            }
            if (address is >= 0x600000 and <= 0x60ffff)
            {
                _spriteRam[address - 0x600000] = value;
                _spriteWrites++;
                return;
            }
            if (address is >= 0x420000 and <= 0x42000f)
            {
                WriteCtrlByte(Tc0100Ctrl0, (int)(address - 0x420000), value);
                _tc0Writes++;
                return;
            }
            if (address is >= 0x520000 and <= 0x52000f)
            {
                WriteCtrlByte(Tc0100Ctrl1, (int)(address - 0x520000), value);
                _tc1Writes++;
                return;
            }
            if (address is >= 0x800000 and <= 0x80001f)
            {
                WritePriorityByte((int)(address - 0x800000), value);
                return;
            }
            if (address is >= 0x200000 and <= 0x20000f)
            {
                if ((address & 1) != 0)
                    WriteIoRegister((int)((address - 0x200000) >> 1), value);
                return;
            }
            if (address == 0x220000)
            {
                _sound?.MasterPortWrite(value);
                return;
            }
            if (address == 0x220002)
            {
                _sound?.MasterCommWrite(value);
                _sound?.SynchronizeFromMain();
                return;
            }
            if (address is >= 0x220000 and <= 0x220003)
                return;

            _unknownWrites++;
        }

        public void WriteWord(uint address, ushort value)
        {
            address &= 0x00ff_fffe;
            if (address is >= 0x200000 and <= 0x20000f)
            {
                WriteIoRegister((int)((address - 0x200000) >> 1), (byte)value);
                return;
            }
            if (address == 0x220000)
            {
                _sound?.MasterPortWrite((byte)(value >> 8));
                return;
            }
            if (address == 0x220002)
            {
                _sound?.MasterCommWrite((byte)(value >> 8));
                _sound?.SynchronizeFromMain();
                return;
            }

            WriteByte(address, (byte)(value >> 8));
            WriteByte(address + 1, (byte)value);
        }

        public void WriteLong(uint address, uint value)
        {
            WriteWord(address, (ushort)(value >> 16));
            WriteWord(address + 2, (ushort)value);
        }

        public byte InterruptLevel() => _interruptLevel;

        public void AcknowledgeInterrupt(byte level)
        {
            if (_interruptLevel == (level & 7))
                _interruptLevel = 0;
        }

        public bool Reset() => false;
        public bool Halt() => false;

        private byte ReadIoRegister(int offset)
        {
            byte p1 = 0xff;
            if (_input.Up) p1 &= unchecked((byte)~0x01);
            if (_input.Down) p1 &= unchecked((byte)~0x02);
            if (_input.Left) p1 &= unchecked((byte)~0x04);
            if (_input.Right) p1 &= unchecked((byte)~0x08);
            if (_input.A) p1 &= unchecked((byte)~0x10);
            if (_input.B) p1 &= unchecked((byte)~0x20);
            if (_input.C) p1 &= unchecked((byte)~0x40);
            if (_input.Start) p1 &= unchecked((byte)~0x80);

            byte system = 0xff;
            if (_input.Mode)
                system &= unchecked((byte)~0x04);

            return offset switch
            {
                0x00 => 0xff,
                0x01 => 0xff,
                0x02 => p1,
                0x03 => 0xff,
                0x04 => 0x00,
                0x05 => 0xff,
                0x07 => system,
                _ => 0xff
            };
        }

        private void WriteIoRegister(int offset, byte value)
        {
            if ((uint)offset >= 8)
                return;

            if (offset == 4)
                return;
        }

        private static byte ReadWordByte(ushort word, uint address)
            => (address & 1) == 0 ? (byte)(word >> 8) : (byte)word;

        private static void WriteCtrlByte(ushort[] ctrl, int byteOffset, byte value)
        {
            int index = byteOffset >> 1;
            if ((uint)index >= ctrl.Length)
                return;

            if ((byteOffset & 1) == 0)
                ctrl[index] = (ushort)((ctrl[index] & 0x00ff) | (value << 8));
            else
                ctrl[index] = (ushort)((ctrl[index] & 0xff00) | value);
        }

        private void WritePriorityByte(int byteOffset, byte value)
        {
            if ((byteOffset & 1) != 0)
                return;

            int index = byteOffset >> 1;
            if ((uint)index >= _priority.Length)
                return;

            _priority[index] = value;
        }

        public byte ReadPriority(int offset)
            => (uint)offset < _priority.Length ? _priority[offset] : (byte)0;

        private static int Expand4(int value)
            => (value << 4) | value;
    }

    private sealed class TaitoF2SoundCpu : EutherDrive.Core.Cpu.Z80Emu.IBusInterface
    {
        private const int AudioCpuClock = 24_000_000 / 6;
        private const int OutputChannels = 2;
        private readonly byte[] _ram = new byte[0x2000];
        private readonly Z80 _cpu = new();
        private readonly TaitoYm2610Lite _ym = new();
        [NonSerialized] private byte[] _rom = Array.Empty<byte>();
        private readonly byte[] _slaveData = new byte[4];
        private readonly byte[] _masterData = new byte[4];
        private byte _mainMode;
        private byte _subMode;
        private byte _status;
        private bool _nmiEnabled;
        [NonSerialized] private bool _resetAsserted;
        private int _bank;
        [NonSerialized] private int _frameCounter;
        [NonSerialized] private int _masterWrites;
        [NonSerialized] private int _masterReads;
        [NonSerialized] private int _slaveWrites;
        [NonSerialized] private int _slaveReads;
        [NonSerialized] private int _ymWrites;
        [NonSerialized] private int _bankWrites;
        [NonSerialized] private int _nmiEnables;
        [NonSerialized] private int _lastPeak;
        [NonSerialized] private int _lastYmPort;
        [NonSerialized] private int _lastYmAddress;
        [NonSerialized] private int _lastYmData;
        [NonSerialized] private int _synchronizedCycles;

        public string DebugSummary
            => $"sndZ80=0x{_cpu.Pc:X4} sndSt=0x{_status:X2} nmi={(_nmiEnabled ? 1 : 0)} rst={(_resetAsserted ? 1 : 0)} modes={_mainMode:X1}/{_subMode:X1} bank={_bank} sync={_synchronizedCycles} mW/R={_masterWrites}/{_masterReads} sW/R={_slaveWrites}/{_slaveReads} ymW={_ymWrites} ym={_lastYmPort}:{_lastYmAddress:X2}={_lastYmData:X2} {_ym.DebugSummary} bW={_bankWrites} nmiEn={_nmiEnables} audPeak={_lastPeak}";

        public void Load(byte[] program, byte[] adpcmA, byte[] adpcmB)
        {
            _rom = program;
            _ym.Load(adpcmA, adpcmB);
            ResetSound();
        }

        public void ResetSound()
        {
            Array.Clear(_ram);
            Array.Clear(_slaveData);
            Array.Clear(_masterData);
            _mainMode = 0;
            _subMode = 0;
            _status = 0;
            _nmiEnabled = false;
            _resetAsserted = false;
            _bank = 0;
            _frameCounter = 0;
            _masterWrites = 0;
            _masterReads = 0;
            _slaveWrites = 0;
            _slaveReads = 0;
            _ymWrites = 0;
            _bankWrites = 0;
            _nmiEnables = 0;
            _lastPeak = 0;
            _lastYmPort = 0;
            _lastYmAddress = 0;
            _lastYmData = 0;
            _synchronizedCycles = 0;
            _ym.Reset();
            _cpu.ApplyResetLine();
        }

        public void SaveState(BinaryWriter writer) => StateBinarySerializer.WriteInto(writer, this);
        public void LoadState(BinaryReader reader)
        {
            StateBinarySerializer.ReadInto(reader, this);
            _ym.AfterLoadState();
            _status = (byte)(_status & 0x03);
            _synchronizedCycles = 0;
        }

        public void RunFrame(Span<short> audioBuffer)
        {
            audioBuffer.Clear();
            int sampleFrames = audioBuffer.Length / OutputChannels;
            int cycles = 0;
            int budget = Math.Max(0, (AudioCpuClock / 60) - _synchronizedCycles);
            _synchronizedCycles = 0;
            int rendered = 0;

            while (cycles < budget)
            {
                uint elapsed = _cpu.ExecuteInstruction(this);
                int elapsedCycles = Math.Max(1, (int)elapsed);
                _ym.ClockTimersForCpuCycles(elapsedCycles, AudioCpuClock);
                cycles += elapsedCycles;
                int target = (int)Math.Min(sampleFrames, ((long)cycles * sampleFrames) / budget);
                if (target > rendered)
                {
                    _ym.RenderStereo(audioBuffer, rendered, target - rendered, OutputSampleRate);
                    rendered = target;
                }
            }

            if (rendered < sampleFrames)
                _ym.RenderStereo(audioBuffer, rendered, sampleFrames - rendered, OutputSampleRate);
            _lastPeak = AudioPeak(audioBuffer);
            _frameCounter++;
        }

        public void SynchronizeFromMain()
        {
            if (_resetAsserted)
                return;

            int budget = AudioCpuClock / 1200;
            int cycles = 0;
            while (cycles < budget)
            {
                uint elapsed = _cpu.ExecuteInstruction(this);
                int elapsedCycles = Math.Max(1, (int)elapsed);
                _ym.ClockTimersForCpuCycles(elapsedCycles, AudioCpuClock);
                cycles += elapsedCycles;
            }
            _synchronizedCycles += cycles;
        }

        public void MasterPortWrite(byte data)
        {
            _mainMode = (byte)(data & 0x0f);
        }

        public void MasterCommWrite(byte data)
        {
            data = (byte)(data & 0x0f);
            _masterWrites++;
            switch (_mainMode)
            {
                case 0:
                case 2:
                    _slaveData[_mainMode++] = data;
                    break;
                case 1:
                    _slaveData[_mainMode++] = data;
                    _status |= 0x01;
                    break;
                case 3:
                    _slaveData[_mainMode++] = data;
                    _status |= 0x02;
                    break;
                case 4:
                    _resetAsserted = data != 0;
                    if (_resetAsserted)
                        _cpu.ApplyResetLine();
                    break;
            }
        }

        public byte MasterCommRead()
        {
            byte result = 0;
            _masterReads++;
            switch (_mainMode)
            {
                case 0:
                case 2:
                    result = _masterData[_mainMode++];
                    break;
                case 1:
                    result = _masterData[_mainMode++];
                    _status &= unchecked((byte)~0x04);
                    break;
                case 3:
                    result = _masterData[_mainMode++];
                    _status &= unchecked((byte)~0x08);
                    break;
                case 4:
                    result = _status;
                    break;
            }
            return result;
        }

        public byte ReadMemory(ushort address)
        {
            if (address < 0x4000)
                return _rom.Length == 0 ? (byte)0xff : _rom[address % _rom.Length];
            if (address < 0x8000)
            {
                int banks = Math.Max(1, _rom.Length / 0x4000);
                int offset = ((_bank % banks) * 0x4000) + (address - 0x4000);
                return _rom.Length == 0 ? (byte)0xff : _rom[offset % _rom.Length];
            }
            if (address is >= 0xc000 and <= 0xdfff)
                return _ram[address - 0xc000];
            if (address is >= 0xe000 and <= 0xe003)
                return _ym.Read(address & 3);
            if (address == 0xe201)
                return SlaveCommRead();
            return 0xff;
        }

        public void WriteMemory(ushort address, byte value)
        {
            if (address is >= 0xc000 and <= 0xdfff)
            {
                _ram[address - 0xc000] = value;
                return;
            }
            if (address is >= 0xe000 and <= 0xe003)
            {
                _ymWrites++;
                _lastYmPort = address & 3;
                if ((address & 1) == 0)
                    _lastYmAddress = value;
                else
                    _lastYmData = value;
                _ym.Write(address & 3, value);
                return;
            }
            if (address == 0xe200)
            {
                _subMode = (byte)(value & 0x0f);
                return;
            }
            if (address == 0xe201)
            {
                SlaveCommWrite(value);
                return;
            }
            if (address == 0xf200)
            {
                _bank = value & 7;
                _bankWrites++;
            }
        }

        private byte SlaveCommRead()
        {
            byte result = 0;
            _slaveReads++;
            switch (_subMode)
            {
                case 0:
                case 2:
                    result = _slaveData[_subMode++];
                    break;
                case 1:
                    result = _slaveData[_subMode++];
                    _status &= unchecked((byte)~0x01);
                    break;
                case 3:
                    result = _slaveData[_subMode++];
                    _status &= unchecked((byte)~0x02);
                    break;
                case 4:
                    result = _status;
                    break;
            }
            return result;
        }

        private void SlaveCommWrite(byte data)
        {
            data = (byte)(data & 0x0f);
            _slaveWrites++;
            switch (_subMode)
            {
                case 0:
                case 2:
                    _masterData[_subMode++] = data;
                    break;
                case 1:
                    _masterData[_subMode++] = data;
                    break;
                case 3:
                    _masterData[_subMode++] = data;
                    break;
                case 5:
                    _nmiEnabled = false;
                    break;
                case 6:
                    _nmiEnabled = true;
                    _nmiEnables++;
                    break;
            }
        }

        private static int AudioPeak(ReadOnlySpan<short> audio)
        {
            int peak = 0;
            foreach (short sample in audio)
            {
                int value = sample < 0 ? -sample : sample;
                if (value > peak)
                    peak = value;
            }
            return peak;
        }

        public byte ReadIo(ushort address) => ReadMemory(address);
        public void WriteIo(ushort address, byte value) => WriteMemory(address, value);
        public InterruptLine Nmi() => _nmiEnabled && (_status & 0x03) != 0 ? InterruptLine.Low : InterruptLine.High;
        public InterruptLine Int() => _ym.IrqAsserted ? InterruptLine.Low : InterruptLine.High;
        public byte InterruptVector() => 0xff;
        public bool BusReq() => false;
        public bool Reset() => _resetAsserted;
    }

    private sealed class TaitoYm2610Lite
    {
        private const double YmClock = 24_000_000.0 / 3.0;
        private const int ChannelsA = 6;
        private const int FmChannels = 6;
        private const int FmOperators = 4;
        private const int SsgChannels = 3;
        private const int AdpcmAddressShift = 8;
        private const int AdpcmBStepMin = 127;
        private const int AdpcmBStepMax = 24576;
        private const double EnvelopeAttackStep = 0.0025;
        private const double EnvelopeDecayStep = 0.00007;
        private const double EnvelopeReleaseStep = 0.0012;
        private static readonly ushort[] s_adpcmaSteps =
        {
             16, 17, 19, 21, 23, 25, 28, 31, 34, 37, 41, 45, 50, 55, 60, 66,
             73, 80, 88, 97, 107, 118, 130, 143, 157, 173, 190, 209, 230, 253,
             279, 307, 337, 371, 408, 449, 494, 544, 598, 658, 724, 796, 876,
             963, 1060, 1166, 1282, 1411, 1552
        };
        private static readonly sbyte[] s_adpcmaStepInc = { -1, -1, -1, -1, 2, 5, 7, 9 };
        private static readonly byte[] s_adpcmbStepScale = { 57, 57, 57, 57, 77, 102, 128, 153 };
        [NonSerialized] private byte[] _adpcmA = Array.Empty<byte>();
        [NonSerialized] private byte[] _adpcmB = Array.Empty<byte>();
        private readonly byte[] _regs = new byte[0x200];
        private readonly byte[] _address = new byte[2];
        private readonly byte[] _bRegs = new byte[0x10];
        [NonSerialized] private readonly bool[] _fmPlaying = new bool[FmChannels];
        [NonSerialized] private readonly byte[] _fmKeyMask = new byte[FmChannels];
        [NonSerialized] private readonly ushort[] _fmFnum = new ushort[FmChannels];
        [NonSerialized] private readonly byte[] _fmBlock = new byte[FmChannels];
        [NonSerialized] private readonly byte[] _fmPan = new byte[FmChannels];
        [NonSerialized] private readonly byte[] _fmAlgorithm = new byte[FmChannels];
        [NonSerialized] private readonly byte[] _fmFeedback = new byte[FmChannels];
        [NonSerialized] private readonly double[] _fmFeedbackSample = new double[FmChannels];
        [NonSerialized] private readonly byte[] _fmMultiple = new byte[FmChannels * FmOperators];
        [NonSerialized] private readonly byte[] _fmTotalLevel = new byte[FmChannels * FmOperators];
        [NonSerialized] private readonly byte[] _fmAttackRate = new byte[FmChannels * FmOperators];
        [NonSerialized] private readonly byte[] _fmDecayRate = new byte[FmChannels * FmOperators];
        [NonSerialized] private readonly byte[] _fmSustainRate = new byte[FmChannels * FmOperators];
        [NonSerialized] private readonly byte[] _fmReleaseRate = new byte[FmChannels * FmOperators];
        [NonSerialized] private readonly byte[] _fmSustainLevel = new byte[FmChannels * FmOperators];
        [NonSerialized] private readonly double[] _fmPhase = new double[FmChannels * FmOperators];
        [NonSerialized] private readonly double[] _fmEnvelope = new double[FmChannels * FmOperators];
        [NonSerialized] private readonly double[] _ssgPhase = new double[SsgChannels];
        private readonly bool[] _aPlaying = new bool[ChannelsA];
        private readonly uint[] _aAddress = new uint[ChannelsA];
        private readonly byte[] _aNibble = new byte[ChannelsA];
        private readonly byte[] _aByte = new byte[ChannelsA];
        private readonly int[] _aAccumulator = new int[ChannelsA];
        private readonly int[] _aStepIndex = new int[ChannelsA];
        private uint _bStatus = 0x02;
        private uint _bBuffer;
        private uint _bNibbles;
        private uint _bPosition;
        private uint _bAddress;
        private int _bAccumulator;
        private int _bOutput;
        private int _bPrevOutput;
        private int _bStep = AdpcmBStepMin;
        private byte _eosStatus;
        private byte _flagMask = 0xbf;
        [NonSerialized] private byte _timerStatus;
        [NonSerialized] private byte _timerControl;
        [NonSerialized] private ushort _timerALatch;
        [NonSerialized] private byte _timerBLatch;
        [NonSerialized] private double _timerASamples;
        [NonSerialized] private double _timerBSamples;
        private double _aClockAccumulator;
        [NonSerialized] private int _fmKeyOns;
        [NonSerialized] private int _aKeyOns;
        [NonSerialized] private int _bKeyOns;

        public string DebugSummary
            => $"irq={(IrqAsserted ? 1 : 0)} t={_timerStatus:X1}/{_timerControl:X2} ta={_timerALatch:X3} tb={_timerBLatch:X2} ssg={CountActiveSsg()} fmKey={_fmKeyOns} fmPlay={CountPlayingFm()} aKey={_aKeyOns} bKey={_bKeyOns} aPlay={CountPlayingA()} bPlay={((_bStatus & 0x04) != 0 ? 1 : 0)} b0={_bRegs[0]:X2} b1={_bRegs[1]:X2} bAdr={_bAddress:X6}";

        public bool IrqAsserted
            => ((_timerStatus & 0x01) != 0 && (_timerControl & 0x04) != 0)
                || ((_timerStatus & 0x02) != 0 && (_timerControl & 0x08) != 0);

        public void Load(byte[] adpcmA, byte[] adpcmB)
        {
            _adpcmA = adpcmA;
            _adpcmB = adpcmB.Length == 0 ? adpcmA : adpcmB;
            Reset();
        }

        public void Reset()
        {
            Array.Clear(_regs);
            Array.Clear(_address);
            Array.Clear(_bRegs);
            Array.Clear(_fmPlaying);
            Array.Clear(_fmKeyMask);
            Array.Clear(_fmFnum);
            Array.Clear(_fmBlock);
            Array.Fill(_fmPan, (byte)0xc0);
            Array.Clear(_fmAlgorithm);
            Array.Clear(_fmFeedback);
            Array.Clear(_fmFeedbackSample);
            Array.Fill(_fmMultiple, (byte)1);
            Array.Clear(_fmTotalLevel);
            Array.Clear(_fmAttackRate);
            Array.Clear(_fmDecayRate);
            Array.Clear(_fmSustainRate);
            Array.Clear(_fmReleaseRate);
            Array.Clear(_fmSustainLevel);
            Array.Clear(_fmPhase);
            Array.Clear(_fmEnvelope);
            Array.Clear(_ssgPhase);
            Array.Clear(_aPlaying);
            Array.Clear(_aAddress);
            Array.Clear(_aNibble);
            Array.Clear(_aByte);
            Array.Clear(_aAccumulator);
            Array.Clear(_aStepIndex);
            _bStatus = 0x02;
            _bBuffer = 0;
            _bNibbles = 0;
            _bPosition = 0;
            _bAddress = 0;
            _bAccumulator = 0;
            _bOutput = 0;
            _bPrevOutput = 0;
            _bStep = AdpcmBStepMin;
            _eosStatus = 0;
            _flagMask = 0xbf;
            _timerStatus = 0;
            _timerControl = 0;
            _timerALatch = 0;
            _timerBLatch = 0;
            _timerASamples = TimerASamples();
            _timerBSamples = TimerBSamples();
            _aClockAccumulator = 0;
            _fmKeyOns = 0;
            _aKeyOns = 0;
            _bKeyOns = 0;
        }

        public void AfterLoadState()
        {
            Array.Clear(_fmPlaying);
            Array.Clear(_fmKeyMask);
            Array.Clear(_fmFnum);
            Array.Clear(_fmBlock);
            Array.Fill(_fmPan, (byte)0xc0);
            Array.Clear(_fmAlgorithm);
            Array.Clear(_fmFeedback);
            Array.Clear(_fmFeedbackSample);
            Array.Fill(_fmMultiple, (byte)1);
            Array.Clear(_fmTotalLevel);
            Array.Clear(_fmAttackRate);
            Array.Clear(_fmDecayRate);
            Array.Clear(_fmSustainRate);
            Array.Clear(_fmReleaseRate);
            Array.Clear(_fmSustainLevel);
            Array.Clear(_fmPhase);
            Array.Clear(_fmEnvelope);
            Array.Clear(_ssgPhase);
            RebuildFmStateFromRegisters();
            _timerStatus = 0;
            _timerControl = 0;
            _timerALatch = TimerALatchFromRegisters();
            _timerBLatch = _regs[0x26];
            _timerASamples = TimerASamples();
            _timerBSamples = TimerBSamples();
            WriteTimerControl(_regs[0x27]);
            _fmKeyOns = 0;
            _aKeyOns = 0;
            _bKeyOns = 0;
        }

        public byte Read(int offset)
        {
            return (offset & 3) switch
            {
                0 => _timerStatus,
                1 => _address[0] < 0x0e ? _regs[_address[0]] : (byte)0,
                2 => (byte)(_eosStatus & _flagMask),
                _ => 0
            };
        }

        public void Write(int offset, byte data)
        {
            switch (offset & 3)
            {
                case 0:
                    _address[0] = data;
                    break;
                case 1:
                    WriteData(0, data);
                    break;
                case 2:
                    _address[1] = data;
                    break;
                case 3:
                    WriteData(1, data);
                    break;
            }
        }

        private void WriteData(int port, byte data)
        {
            int index = (port << 8) | _address[port];
            if ((uint)index < _regs.Length)
                _regs[index] = data;

            if (port == 0)
            {
                byte reg = _address[0];
                if (reg == 0x1c)
                {
                    _flagMask = (byte)(~data & 0xbf);
                    _eosStatus = (byte)(_eosStatus & ~(data & 0xbf));
                }
                else if (reg == 0x24 || reg == 0x25)
                {
                    _timerALatch = TimerALatchFromRegisters();
                    if ((_timerControl & 0x01) == 0)
                        _timerASamples = TimerASamples();
                }
                else if (reg == 0x26)
                {
                    _timerBLatch = data;
                    if ((_timerControl & 0x02) == 0)
                        _timerBSamples = TimerBSamples();
                }
                else if (reg == 0x27)
                    WriteTimerControl(data);
                else if (reg == 0x28)
                    WriteFmKeyOn(data);
                else if (reg >= 0x10 && reg < 0x1c)
                    WriteAdpcmB((byte)(reg & 0x0f), data);
            }
            if (TryMapFmChannel(port, _address[port], out int fmChannel, out byte fmReg))
                WriteFmRegister(fmChannel, fmReg, data);

            if (port == 1 && _address[1] < 0x30)
            {
                WriteAdpcmA(_address[1], data);
            }
        }

        public void RenderStereo(Span<short> output, int startFrame, int frames, double sampleRate)
        {
            const double adpcmAClock = (24_000_000.0 / 3.0) / 144.0 / 3.0;
            for (int frame = startFrame; frame < startFrame + frames; frame++)
            {
                byte ended = 0;
                _aClockAccumulator += adpcmAClock;
                while (_aClockAccumulator >= sampleRate)
                {
                    _aClockAccumulator -= sampleRate;
                    for (int channel = 0; channel < ChannelsA; channel++)
                        if (ClockAdpcmA(channel))
                            ended |= (byte)(1 << channel);
                }

                int left = 0;
                int right = 0;
                MixSsg(sampleRate, ref left, ref right);
                MixFm(sampleRate, ref left, ref right);
                for (int channel = 0; channel < ChannelsA; channel++)
                    MixAdpcmA(channel, ref left, ref right);
                if (ClockAdpcmB())
                    ended |= 0x80;
                MixAdpcmB(ref left, ref right);
                _eosStatus |= ended;

                int offset = frame * 2;
                output[offset] = ClampSample(output[offset] + left);
                output[offset + 1] = ClampSample(output[offset + 1] + right);
            }
        }

        private static short ClampSample(int value) => (short)Math.Clamp(value, short.MinValue, short.MaxValue);
        private static int Gain(int value, int percent) => (int)Math.Clamp((long)value * percent / 100, short.MinValue, short.MaxValue);

        private void WriteTimerControl(byte data)
        {
            byte oldControl = _timerControl;
            if ((data & 0x10) != 0)
                _timerStatus &= unchecked((byte)~0x01);
            if ((data & 0x20) != 0)
                _timerStatus &= unchecked((byte)~0x02);
            _timerControl = (byte)(data & 0x0f);
            if ((data & 0x01) != 0 && (oldControl & 0x01) == 0)
                _timerASamples = TimerASamples();
            else if ((data & 0x01) == 0)
                _timerASamples = TimerASamples();
            if ((data & 0x02) != 0 && (oldControl & 0x02) == 0)
                _timerBSamples = TimerBSamples();
            else if ((data & 0x02) == 0)
                _timerBSamples = TimerBSamples();
        }

        public void ClockTimersForCpuCycles(int cycles, double cpuClock)
        {
            if (cycles <= 0 || cpuClock <= 0.0)
                return;
            ClockTimers(cycles * OutputSampleRate / cpuClock);
        }

        private void ClockTimers(double elapsedSamples)
        {
            if ((_timerControl & 0x01) != 0)
            {
                _timerASamples -= elapsedSamples;
                if (_timerASamples <= 0.0)
                {
                    _timerASamples += TimerASamples();
                    _timerStatus |= 0x01;
                }
            }

            if ((_timerControl & 0x02) != 0)
            {
                _timerBSamples -= elapsedSamples;
                if (_timerBSamples <= 0.0)
                {
                    _timerBSamples += TimerBSamples();
                    _timerStatus |= 0x02;
                }
            }
        }

        private ushort TimerALatchFromRegisters()
            => (ushort)(((_regs[0x24] << 2) | (_regs[0x25] & 0x03)) & 0x03ff);

        private double TimerASamples()
        {
            int count = 1024 - (_timerALatch & 0x03ff);
            if (count <= 0)
                count = 1024;
            return Math.Max(1.0, count * 144.0 * OutputSampleRate / YmClock);
        }

        private double TimerBSamples()
        {
            int count = 256 - _timerBLatch;
            if (count <= 0)
                count = 256;
            return Math.Max(1.0, count * 2304.0 * OutputSampleRate / YmClock);
        }

        private static bool TryMapFmChannel(int port, byte reg, out int channel, out byte fmReg)
        {
            channel = -1;
            fmReg = reg;
            if (reg is < 0x30 or > 0xb6)
                return false;

            int low = reg & 0x03;
            if (low == 3)
                return false;

            channel = low + (port != 0 ? 3 : 0);
            return (uint)channel < 6;
        }

        private void WriteFmRegister(int channel, byte reg, byte data)
        {
            if (reg is >= 0x30 and <= 0x9e)
            {
                int op = (reg >> 2) & 3;
                int opIndex = (channel * FmOperators) + op;
                switch (reg & 0xf0)
                {
                    case 0x30:
                        _fmMultiple[opIndex] = (byte)(data & 0x0f);
                        if (_fmMultiple[opIndex] == 0)
                            _fmMultiple[opIndex] = 1;
                        break;
                    case 0x40:
                        _fmTotalLevel[opIndex] = (byte)(data & 0x7f);
                        break;
                    case 0x50:
                        _fmAttackRate[opIndex] = (byte)(data & 0x1f);
                        break;
                    case 0x60:
                        _fmDecayRate[opIndex] = (byte)(data & 0x1f);
                        break;
                    case 0x70:
                        _fmSustainRate[opIndex] = (byte)(data & 0x1f);
                        break;
                    case 0x80:
                        _fmSustainLevel[opIndex] = (byte)((data >> 4) & 0x0f);
                        _fmReleaseRate[opIndex] = (byte)(data & 0x0f);
                        break;
                }
            }
            else if (reg is >= 0xa0 and <= 0xa2)
            {
                _fmFnum[channel] = (ushort)((_fmFnum[channel] & 0x0700) | data);
            }
            else if (reg is >= 0xa4 and <= 0xa6)
            {
                _fmFnum[channel] = (ushort)((_fmFnum[channel] & 0x00ff) | ((data & 0x07) << 8));
                _fmBlock[channel] = (byte)((data >> 3) & 0x07);
            }
            else if (reg is >= 0xb4 and <= 0xb6)
            {
                _fmPan[channel] = (byte)(data & 0xc0);
            }
            else if (reg is >= 0xb0 and <= 0xb2)
            {
                _fmFeedback[channel] = (byte)((data >> 3) & 0x07);
                _fmAlgorithm[channel] = (byte)(data & 0x07);
            }
        }

        private void WriteFmKeyOn(byte data)
        {
            int channel = data & 0x03;
            if (channel == 3)
                return;
            if ((data & 0x04) != 0)
                channel += 3;

            byte keyMask = (byte)(data >> 4);
            bool keyOn = keyMask != 0;
            _fmKeyMask[channel] = keyMask;
            _fmPlaying[channel] = keyOn;
            if (keyOn)
            {
                _fmKeyOns++;
                for (int op = 0; op < FmOperators; op++)
                {
                    if ((keyMask & (1 << op)) == 0)
                        continue;
                    int opIndex = (channel * FmOperators) + op;
                    _fmEnvelope[opIndex] = 0.0;
                    _fmPhase[opIndex] = 0.0;
                }
            }
        }

        private void MixFm(double sampleRate, ref int left, ref int right)
        {
            for (int channel = 0; channel < _fmPlaying.Length; channel++)
            {
                if (!_fmPlaying[channel])
                    continue;

                int fnum = _fmFnum[channel];
                if (fnum == 0)
                    continue;

                double frequency = (fnum / 1024.0) * 440.0 * Math.Pow(2.0, _fmBlock[channel] - 4);
                if (frequency < 20.0)
                    frequency = 20.0;
                else if (frequency > 6000.0)
                    frequency = 6000.0;

                double modulation = 0.0;
                double parallel = 0.0;
                for (int op = 0; op < FmOperators; op++)
                {
                    int opIndex = (channel * FmOperators) + op;
                    if ((_fmKeyMask[channel] & (1 << op)) == 0)
                    {
                        _fmEnvelope[opIndex] = Math.Max(0.0, _fmEnvelope[opIndex] - ReleaseStep(opIndex));
                        if (_fmEnvelope[opIndex] <= 0.0)
                            continue;
                    }
                    else
                    {
                        AdvanceFmEnvelope(opIndex);
                    }

                    double phase = _fmPhase[opIndex] + frequency * _fmMultiple[opIndex] / sampleRate;
                    phase -= Math.Floor(phase);
                    _fmPhase[opIndex] = phase;
                    double level = _fmEnvelope[opIndex] * TotalLevelGain(opIndex);
                    double input = modulation;
                    if (op == 0 && _fmFeedback[channel] != 0)
                        input += _fmFeedbackSample[channel] * _fmFeedback[channel] * 0.08;
                    double opValue = Math.Sin((phase + input) * Math.Tau) * level;

                    if (_fmAlgorithm[channel] >= 4 || op == FmOperators - 1)
                        parallel += opValue;
                    else
                        modulation = opValue * 0.55;

                    if (op == 0)
                        _fmFeedbackSample[channel] = opValue;
                }

                int value = (int)(parallel * 1800.0);
                byte pan = _fmPan[channel];
                if ((pan & 0xc0) == 0)
                    pan = 0xc0;
                if ((pan & 0x80) != 0) left += value;
                if ((pan & 0x40) != 0) right += value;
            }
        }

        private void AdvanceFmEnvelope(int opIndex)
        {
            double sustain = 1.0 - (_fmSustainLevel[opIndex] / 15.0);
            double attack = EnvelopeAttackStep * Math.Max(1, (int)_fmAttackRate[opIndex]);
            if (_fmEnvelope[opIndex] < 1.0)
            {
                _fmEnvelope[opIndex] = Math.Min(1.0, _fmEnvelope[opIndex] + attack);
                return;
            }

            double decayRate = EnvelopeDecayStep * Math.Max(1, (int)_fmDecayRate[opIndex]);
            if (_fmEnvelope[opIndex] > sustain)
                _fmEnvelope[opIndex] = Math.Max(sustain, _fmEnvelope[opIndex] - decayRate);
        }

        private double ReleaseStep(int opIndex)
            => EnvelopeReleaseStep * Math.Max(1, (int)_fmReleaseRate[opIndex]);

        private double TotalLevelGain(int opIndex)
            => Math.Pow(10.0, -_fmTotalLevel[opIndex] / 48.0);

        private void MixSsg(double sampleRate, ref int left, ref int right)
        {
            byte mixer = _regs[0x07];
            for (int channel = 0; channel < SsgChannels; channel++)
            {
                if ((mixer & (1 << channel)) != 0)
                    continue;

                int periodReg = channel * 2;
                int period = _regs[periodReg] | ((_regs[periodReg + 1] & 0x0f) << 8);
                if (period == 0)
                    period = 1;
                int volume = _regs[0x08 + channel] & 0x0f;
                if (volume == 0)
                    continue;

                double frequency = YmClock / 16.0 / period;
                if (frequency > sampleRate * 0.45)
                    frequency = sampleRate * 0.45;
                _ssgPhase[channel] += frequency / sampleRate;
                _ssgPhase[channel] -= Math.Floor(_ssgPhase[channel]);
                int value = (_ssgPhase[channel] < 0.5 ? 1 : -1) * volume * 70;
                left += value;
                right += value;
            }
        }

        private void RebuildFmStateFromRegisters()
        {
            for (int port = 0; port < 2; port++)
            {
                for (byte reg = 0x30; reg <= 0xb6; reg++)
                {
                    if (!TryMapFmChannel(port, reg, out int channel, out byte fmReg))
                        continue;
                    WriteFmRegister(channel, fmReg, _regs[(port << 8) | reg]);
                }
            }
        }

        private void WriteAdpcmA(byte reg, byte data)
        {
            if (reg != 0)
                return;
            bool keyon = (data & 0x80) == 0;
            for (int channel = 0; channel < ChannelsA; channel++)
            {
                if ((data & (1 << channel)) == 0)
                    continue;
                _aPlaying[channel] = keyon;
                if (!keyon)
                    continue;
                _aKeyOns++;
                _aAddress[channel] = AdpcmAStart(channel) << AdpcmAddressShift;
                _aNibble[channel] = 0;
                _aByte[channel] = 0;
                _aAccumulator[channel] = 0;
                _aStepIndex[channel] = 0;
            }
        }

        private bool ClockAdpcmA(int channel)
        {
            if (!_aPlaying[channel])
            {
                _aAccumulator[channel] = 0;
                return false;
            }
            uint end = (AdpcmAEnd(channel) + 1) << AdpcmAddressShift;
            if (_aNibble[channel] == 0 && ((_aAddress[channel] ^ end) & 0x0fffff) == 0)
            {
                _aPlaying[channel] = false;
                _aAccumulator[channel] = 0;
                return true;
            }
            byte data;
            if (_aNibble[channel] == 0)
            {
                _aByte[channel] = _aAddress[channel] < _adpcmA.Length ? _adpcmA[(int)_aAddress[channel]++] : (byte)0;
                data = (byte)(_aByte[channel] >> 4);
                _aNibble[channel] = 1;
            }
            else
            {
                data = (byte)(_aByte[channel] & 0x0f);
                _aNibble[channel] = 0;
            }
            int stepIndex = _aStepIndex[channel];
            int delta = (2 * (data & 7) + 1) * s_adpcmaSteps[stepIndex] / 8;
            if ((data & 8) != 0)
                delta = -delta;
            _aAccumulator[channel] = (_aAccumulator[channel] + delta) & 0x0fff;
            _aStepIndex[channel] = Math.Clamp(stepIndex + s_adpcmaStepInc[data & 7], 0, 48);
            return false;
        }

        private void MixAdpcmA(int channel, ref int left, ref int right)
        {
            if (!_aPlaying[channel])
                return;
            int reg = 0x108 + channel;
            int attenuation = ((_regs[reg] & 0x1f) ^ 0x1f) + ((_regs[0x101] & 0x3f) ^ 0x3f);
            if (attenuation >= 63)
                return;
            short signed = unchecked((short)(_aAccumulator[channel] << 4));
            int value = ((signed * (15 - (attenuation & 7))) >> (5 + (attenuation >> 3))) & ~3;
            value = Gain(value, 300);
            if ((_regs[reg] & 0x80) != 0) left += value;
            if ((_regs[reg] & 0x40) != 0) right += value;
        }

        private uint AdpcmAStart(int channel) => (uint)(_regs[0x110 + channel] | (_regs[0x118 + channel] << 8));
        private uint AdpcmAEnd(int channel) => (uint)(_regs[0x120 + channel] | (_regs[0x128 + channel] << 8));

        private void WriteAdpcmB(byte reg, byte data)
        {
            if (reg == 0)
                data = (byte)((data | 0x20) & ~0x40);
            _bRegs[reg] = data;
            if (reg != 0)
                return;
            if ((data & 1) != 0)
            {
                _bStatus = 0x02 | ((_bStatus & 0x04) != 0 ? 0x01u : 0u);
                return;
            }
            _bStatus = 0x02;
            _bAddress = AdpcmBStart() << AdpcmAddressShift;
            if ((data & 0x80) == 0)
                return;
            _bBuffer = 0;
            _bNibbles = 0;
            _bPosition = 0;
            _bAccumulator = 0;
            _bOutput = 0;
            _bPrevOutput = 0;
            _bStep = AdpcmBStepMin;
            _bStatus = 0x02 | 0x04;
            _eosStatus = (byte)(_eosStatus & ~0x80);
            _bKeyOns++;
        }

        private int CountPlayingA()
        {
            int count = 0;
            for (int i = 0; i < _aPlaying.Length; i++)
                if (_aPlaying[i])
                    count++;
            return count;
        }

        private int CountPlayingFm()
        {
            int count = 0;
            for (int i = 0; i < _fmPlaying.Length; i++)
                if (_fmPlaying[i])
                    count++;
            return count;
        }

        private int CountActiveSsg()
        {
            int count = 0;
            byte mixer = _regs[0x07];
            for (int channel = 0; channel < SsgChannels; channel++)
            {
                if ((mixer & (1 << channel)) != 0)
                    continue;
                if ((_regs[0x08 + channel] & 0x0f) != 0)
                    count++;
            }
            return count;
        }

        private bool ClockAdpcmB()
        {
            if ((_bStatus & 0x04) == 0 || (_bRegs[0] & 0x80) == 0)
            {
                _bPrevOutput = _bOutput;
                _bPosition = 0;
                return false;
            }
            uint deltaN = AdpcmBDelta();
            if (deltaN == 0)
                return false;
            uint position = _bPosition + deltaN;
            _bPosition = position & 0xffff;
            if (position < 0x10000)
                return false;
            if (_bNibbles == 0 && RequestAdpcmBData())
                return FinishAdpcmB();
            uint data = ConsumeAdpcmBNibbles(1);
            int delta = (2 * (int)(data & 7) + 1) * _bStep / 8;
            if ((data & 8) != 0)
                delta = -delta;
            _bAccumulator = Math.Clamp(_bAccumulator + delta, -32768, 32767);
            _bStep = Math.Clamp((_bStep * s_adpcmbStepScale[data & 7]) / 64, AdpcmBStepMin, AdpcmBStepMax);
            _bPrevOutput = _bOutput;
            _bOutput = _bAccumulator;
            if (_bNibbles < 3 && RequestAdpcmBData())
                return FinishAdpcmB();
            return false;
        }

        private bool FinishAdpcmB()
        {
            if ((_bRegs[0] & 0x10) != 0)
            {
                _bAddress = AdpcmBStart() << AdpcmAddressShift;
                return true;
            }
            _bStatus = (_bStatus | 0x01) & ~0x04u;
            _bBuffer = 0;
            _bNibbles = 0;
            return true;
        }

        private bool RequestAdpcmBData()
        {
            byte data = _bAddress < _adpcmB.Length ? _adpcmB[(int)_bAddress] : (byte)0;
            if (_bNibbles > 6)
                _bNibbles = 6;
            _bBuffer |= (uint)data << (int)(24 - 4 * _bNibbles);
            _bNibbles += 2;
            if ((_bAddress & 0xff) == 0xff && ((_bAddress >> AdpcmAddressShift) == AdpcmBEnd()))
                return true;
            _bAddress = (_bAddress + 1) & 0xffffff;
            return false;
        }

        private uint ConsumeAdpcmBNibbles(byte count)
        {
            uint result = _bBuffer >> (32 - 4 * count);
            _bBuffer <<= 4 * count;
            _bNibbles = _bNibbles > count ? _bNibbles - count : 0;
            return result;
        }

        private void MixAdpcmB(ref int left, ref int right)
        {
            if ((_bStatus & 0x04) == 0)
                return;
            int interp = (int)(((long)_bPrevOutput * (((_bPosition ^ 0xffff) + 1) & 0xffff) + (long)_bOutput * _bPosition) >> 16);
            int value = Gain((interp * _bRegs[0x0b]) >> 9, 100);
            if ((_bRegs[1] & 0x80) != 0) left += value;
            if ((_bRegs[1] & 0x40) != 0) right += value;
        }

        private uint AdpcmBStart() => (uint)(_bRegs[2] | (_bRegs[3] << 8));
        private uint AdpcmBEnd() => (uint)(_bRegs[4] | (_bRegs[5] << 8));
        private uint AdpcmBDelta() => (uint)(_bRegs[9] | (_bRegs[10] << 8));
    }

    private sealed class ThunderFoxRomSet
    {
        private ThunderFoxRomSet(string driverName)
        {
            DriverName = driverName;
        }

        public string DriverName { get; }
        public byte[] Program { get; } = new byte[0x80000];
        public byte[] Sprites { get; } = new byte[0x100000];
        public byte[] Screen0 { get; } = new byte[0x80000];
        public byte[] Screen1 { get; } = new byte[0x80000];
        public byte[] AudioCpu { get; } = new byte[0x10000];
        public byte[] AdpcmA { get; } = new byte[0x80000];
        public byte[] AdpcmB { get; } = new byte[0x80000];

        public static ThunderFoxRomSet Load(string path)
        {
            string driverName = CanonicalDriverName(Path.GetFileNameWithoutExtension(path).Trim().ToLowerInvariant());
            var roms = new ThunderFoxRomSet(driverName);
            Dictionary<string, byte[]> entries = ReadArchive(path);

            Load16Byte(entries, roms.Program, 0x00000, "c28-13-1.51");
            Load16Byte(entries, roms.Program, 0x00001, driverName == "thundfoxu" ? "c28-15-1.40" : "c28-16-1.40");
            Load16Byte(entries, roms.Program, 0x40000, "c28-08.50");
            Load16Byte(entries, roms.Program, 0x40001, "c28-07.39");

            Load16Byte(entries, roms.Sprites, 0x00000, "c28-03.29");
            Load16Byte(entries, roms.Sprites, 0x00001, "c28-04.28");

            Load16WordSwap(entries, roms.Screen0, 0x00000, "c28-02.61");
            Load16WordSwap(entries, roms.Screen1, 0x00000, "c28-01.63");
            LoadRaw(entries, roms.AudioCpu, 0x00000, "c28-14.3");
            LoadRaw(entries, roms.AdpcmA, 0x00000, "c28-06.41");
            LoadRaw(entries, roms.AdpcmB, 0x00000, "c28-05.42");
            return roms;
        }

        private static string CanonicalDriverName(string name)
            => name == "thunderfox" ? "thundfox" : name;

        private static Dictionary<string, byte[]> ReadArchive(string path)
        {
            using IArchive archive = RomArchiveExtractor.OpenArchive(path);
            var entries = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
            foreach (IArchiveEntry entry in archive.Entries)
            {
                if (entry.IsDirectory || string.IsNullOrWhiteSpace(entry.Key))
                    continue;

                using Stream stream = entry.OpenEntryStream();
                using var memory = new MemoryStream();
                stream.CopyTo(memory);
                entries[Path.GetFileName(entry.Key)] = memory.ToArray();
            }

            return entries;
        }

        private static void Load16Byte(Dictionary<string, byte[]> entries, byte[] destination, int offset, string name)
        {
            byte[] source = Find(entries, name);
            for (int i = 0; i < source.Length; i++)
            {
                int dst = offset + (i * 2);
                if ((uint)dst >= destination.Length)
                    throw new InvalidDataException($"ROM '{name}' is too large for its Thunder Fox region.");
                destination[dst] = source[i];
            }
        }

        private static void Load16WordSwap(Dictionary<string, byte[]> entries, byte[] destination, int offset, string name)
        {
            byte[] source = Find(entries, name);
            if (offset + source.Length > destination.Length)
                throw new InvalidDataException($"ROM '{name}' is too large for its Thunder Fox region.");

            for (int i = 0; i < source.Length; i += 2)
            {
                destination[offset + i] = source[i + 1];
                destination[offset + i + 1] = source[i];
            }
        }

        private static void LoadRaw(Dictionary<string, byte[]> entries, byte[] destination, int offset, string name)
        {
            byte[] source = Find(entries, name);
            if (offset + source.Length > destination.Length)
                throw new InvalidDataException($"ROM '{name}' is too large for its Thunder Fox region.");
            Array.Copy(source, 0, destination, offset, source.Length);
        }

        private static byte[] Find(Dictionary<string, byte[]> entries, string name)
        {
            if (entries.TryGetValue(name, out byte[]? data))
                return data;

            throw new InvalidDataException($"Thunder Fox ROM set is missing '{name}'.");
        }
    }
}
