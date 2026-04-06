using System.Runtime.InteropServices;

namespace EutherDrive.Core.GbaEmu;

public partial class GbaVideo
{
    private static readonly uint[] ColorLookup = BuildColorLookup();

    private struct ScanlineState
    {
        public ushort DispCnt;
        public ushort BgCnt0;
        public ushort BgCnt1;
        public ushort BgCnt2;
        public ushort BgCnt3;
        public short BgHOfs0;
        public short BgVOfs0;
        public short BgHOfs1;
        public short BgVOfs1;
        public short BgHOfs2;
        public short BgVOfs2;
        public short BgHOfs3;
        public short BgVOfs3;
        public short Bg2PA;
        public short Bg2PB;
        public short Bg2PC;
        public short Bg2PD;
        public int Bg2X;
        public int Bg2Y;
        public short Bg3PA;
        public short Bg3PB;
        public short Bg3PC;
        public short Bg3PD;
        public int Bg3X;
        public int Bg3Y;
        public ushort BldCnt;
        public ushort BldAlpha;
        public ushort BldY;
        public ushort Win0H;
        public ushort Win0V;
        public ushort Win1H;
        public ushort Win1V;
        public ushort WinIn;
        public ushort WinOut;
        public ushort EnabledMask;
    }

    private const int FrameWidth = GbaConstants.ScreenWidth;
    private const int FrameHeight = GbaConstants.ScreenHeight;
    private const int FrameStride = FrameWidth * 4;
    private const int VramSize = 96 * 1024;
    private const int OamSize = 1024;
    private const int PaletteSize = 1024;

    private readonly uint[] _frameBuffer = new uint[FrameWidth * FrameHeight];
    private readonly byte[] _screenshotBuffer = new byte[FrameWidth * FrameHeight * 4];

    private readonly uint[] _bg0Scanline = new uint[FrameWidth];
    private readonly uint[] _bg1Scanline = new uint[FrameWidth];
    private readonly uint[] _bg2Scanline = new uint[FrameWidth];
    private readonly uint[] _bg3Scanline = new uint[FrameWidth];
    private readonly uint[] _objScanline = new uint[FrameWidth];
    private readonly byte[] _objPriorities = new byte[FrameWidth];
    private readonly byte[] _objOamIndices = new byte[FrameWidth];
    private readonly byte[] _objSemiTransparent = new byte[FrameWidth];
    private readonly byte[] _objWindowMask = new byte[FrameWidth];
    private readonly ScanlineState[] _scanlineStates = new ScanlineState[FrameHeight];
    private readonly byte[] _frameVram = new byte[VramSize];
    private readonly byte[] _frameOam = new byte[OamSize];
    private readonly byte[] _framePalette = new byte[PaletteSize * FrameHeight];
    private readonly uint[] _bgPaletteCache = new uint[256];
    private readonly uint[] _objPaletteCache = new uint[256];
    private bool _hasCapturedFrame;
    private bool _screenshotDirty = true;

    public ReadOnlySpan<byte> GetFrameBuffer() => MemoryMarshal.AsBytes<uint>(_frameBuffer);

    public void RefreshFrame()
    {
        RenderSoftwareFrame();
    }

    public byte[] CaptureScreenshot()
    {
        if (_screenshotDirty)
        {
            RebuildScreenshotBuffer();
        }

        byte[] copy = new byte[_screenshotBuffer.Length];
        Buffer.BlockCopy(_screenshotBuffer, 0, copy, 0, copy.Length);
        return copy;
    }

    private void CaptureScanline(int y)
    {
        if ((uint)y >= FrameHeight)
            return;

        _scanlineStates[y] = new ScanlineState
        {
            DispCnt = DispCnt,
            BgCnt0 = BgCnt[0],
            BgCnt1 = BgCnt[1],
            BgCnt2 = BgCnt[2],
            BgCnt3 = BgCnt[3],
            BgHOfs0 = BgHOfs[0],
            BgVOfs0 = BgVOfs[0],
            BgHOfs1 = BgHOfs[1],
            BgVOfs1 = BgVOfs[1],
            BgHOfs2 = BgHOfs[2],
            BgVOfs2 = BgVOfs[2],
            BgHOfs3 = BgHOfs[3],
            BgVOfs3 = BgVOfs[3],
            Bg2PA = BgPA[0],
            Bg2PB = BgPB[0],
            Bg2PC = BgPC[0],
            Bg2PD = BgPD[0],
            Bg2X = BgX[0],
            Bg2Y = BgY[0],
            Bg3PA = BgPA[1],
            Bg3PB = BgPB[1],
            Bg3PC = BgPC[1],
            Bg3PD = BgPD[1],
            Bg3X = BgX[1],
            Bg3Y = BgY[1],
            BldCnt = BldCnt,
            BldAlpha = BldAlpha,
            BldY = BldY,
            Win0H = Win0H,
            Win0V = Win0V,
            Win1H = Win1H,
            Win1V = Win1V,
            WinIn = WinIn,
            WinOut = WinOut,
            EnabledMask = GetEnabledMask(y),
        };

        Buffer.BlockCopy(Gba.Memory.PaletteRam, 0, _framePalette, y * PaletteSize, PaletteSize);
    }

    private void SnapshotVram()
    {
        Buffer.BlockCopy(Gba.Memory.Vram, 0, _frameVram, 0, Math.Min(Gba.Memory.Vram.Length, _frameVram.Length));
        Buffer.BlockCopy(Gba.Memory.Oam, 0, _frameOam, 0, Math.Min(Gba.Memory.Oam.Length, _frameOam.Length));
        _hasCapturedFrame = true;
    }

    private void CommitFrame()
    {
        RenderSoftwareFrame();
    }

    public bool UploadAndBuildCommandList() => false;

    public void InitGpu(int scale = 1)
    {
        _ = scale;
    }

    public void DisposeGpu()
    {
    }

    private ushort GetEnabledMask(int y)
    {
        uint mask = 0;
        for (int i = 0; i < 4; i++)
        {
            if ((DispCnt & (0x0100 << i)) != 0 && y >= _enabledAtY[i])
                mask |= (uint)(1 << i);
        }

        return (ushort)mask;
    }

    private ScanlineState GetScanlineState(int y)
    {
        if (_hasCapturedFrame && (uint)y < FrameHeight)
            return _scanlineStates[y];

        return new ScanlineState
        {
            DispCnt = DispCnt,
            BgCnt0 = BgCnt[0],
            BgCnt1 = BgCnt[1],
            BgCnt2 = BgCnt[2],
            BgCnt3 = BgCnt[3],
            BgHOfs0 = BgHOfs[0],
            BgVOfs0 = BgVOfs[0],
            BgHOfs1 = BgHOfs[1],
            BgVOfs1 = BgVOfs[1],
            BgHOfs2 = BgHOfs[2],
            BgVOfs2 = BgVOfs[2],
            BgHOfs3 = BgHOfs[3],
            BgVOfs3 = BgVOfs[3],
            Bg2PA = BgPA[0],
            Bg2PB = BgPB[0],
            Bg2PC = BgPC[0],
            Bg2PD = BgPD[0],
            Bg2X = BgX[0],
            Bg2Y = BgY[0],
            Bg3PA = BgPA[1],
            Bg3PB = BgPB[1],
            Bg3PC = BgPC[1],
            Bg3PD = BgPD[1],
            Bg3X = BgX[1],
            Bg3Y = BgY[1],
            BldCnt = BldCnt,
            BldAlpha = BldAlpha,
            BldY = BldY,
            Win0H = Win0H,
            Win0V = Win0V,
            Win1H = Win1H,
            Win1V = Win1V,
            WinIn = WinIn,
            WinOut = WinOut,
            EnabledMask = GetEnabledMask(y),
        };
    }

    private void RenderSoftwareFrame()
    {
        ScanlineState firstState = GetScanlineState(0);
        if ((firstState.DispCnt & 0x0080) != 0)
        {
            FillSolid(0xFFFFFFFFu);
            return;
        }

        int mode = firstState.DispCnt & 7;
        byte[] vram = _hasCapturedFrame ? _frameVram : Gba.Memory.Vram;
        byte[] oam = _hasCapturedFrame ? _frameOam : Gba.Memory.Oam;
        bool mapping1D = (firstState.DispCnt & 0x0040) != 0;

        for (int y = 0; y < FrameHeight; y++)
        {
            ScanlineState state = GetScanlineState(y);
            ClearScanlineBuffers();
            BuildPaletteCache(y);

            switch (mode)
            {
                case 0:
                    RenderTextBackgroundScanline(0, y, state, vram, _bg0Scanline);
                    RenderTextBackgroundScanline(1, y, state, vram, _bg1Scanline);
                    RenderTextBackgroundScanline(2, y, state, vram, _bg2Scanline);
                    RenderTextBackgroundScanline(3, y, state, vram, _bg3Scanline);
                    break;
                case 1:
                    RenderTextBackgroundScanline(0, y, state, vram, _bg0Scanline);
                    RenderTextBackgroundScanline(1, y, state, vram, _bg1Scanline);
                    RenderAffineBackgroundScanline(2, y, state, vram, _bg2Scanline);
                    break;
                case 2:
                    RenderAffineBackgroundScanline(2, y, state, vram, _bg2Scanline);
                    RenderAffineBackgroundScanline(3, y, state, vram, _bg3Scanline);
                    break;
                case 3:
                    RenderBitmapMode3Scanline(y, state, vram, _bg2Scanline);
                    break;
                case 4:
                    RenderBitmapMode4Scanline(y, state, vram, _bg2Scanline);
                    break;
                case 5:
                    RenderBitmapMode5Scanline(y, state, vram, _bg2Scanline);
                    break;
            }

            RenderSpritesScanline(y, state, oam, vram, mapping1D, mode);
            FinalizeScanline(y, state);
        }

        _screenshotDirty = true;
    }

    private void FillSolid(uint color)
    {
        Array.Fill(_frameBuffer, color);

        _screenshotDirty = true;
    }

    private void ClearScanlineBuffers()
    {
        Array.Clear(_bg0Scanline);
        Array.Clear(_bg1Scanline);
        Array.Clear(_bg2Scanline);
        Array.Clear(_bg3Scanline);
        Array.Clear(_objScanline);
        Array.Fill(_objPriorities, byte.MaxValue);
        Array.Fill(_objOamIndices, byte.MaxValue);
        Array.Clear(_objSemiTransparent);
        Array.Clear(_objWindowMask);
    }

    private void RenderTextBackgroundScanline(int bg, int y, ScanlineState state, byte[] vram, uint[] target)
    {
        if ((state.EnabledMask & (1 << bg)) == 0)
            return;

        ushort control = GetBgCnt(state, bg);
        int charBase = ((control >> 2) & 0x3) * 0x4000;
        int screenBase = ((control >> 8) & 0x1F) * 0x800;
        bool use256Colors = (control & 0x0080) != 0;
        GetTextBgDimensions((control >> 14) & 0x3, out int width, out int height);
        int blocksPerRow = width >> 8;
        int sourceY = Mod9Bit(y + GetBgVOfs(state, bg), height);
        int tileY = sourceY >> 3;
        int rowInTile = sourceY & 0x7;
        int blockY = tileY >> 5;
        int sourceX = Mod9Bit(GetBgHOfs(state, bg), width);

        for (int x = 0; x < FrameWidth;)
        {
            int tileX = sourceX >> 3;
            int blockX = tileX >> 5;
            int blockIndex = blockY * blocksPerRow + blockX;
            int mapOffset = screenBase + blockIndex * 0x800 + (((tileY & 31) * 32 + (tileX & 31)) * 2);
            int columnStart = sourceX & 0x7;
            int run = Math.Min(FrameWidth - x, 8 - columnStart);

            if ((uint)(mapOffset + 1) < (uint)vram.Length)
            {
                ushort entry = ReadU16(vram, mapOffset);
                int tileNumber = entry & 0x03FF;
                bool hFlip = (entry & 0x0400) != 0;
                bool vFlip = (entry & 0x0800) != 0;
                int paletteBank = entry >> 12;
                int row = vFlip ? 7 - rowInTile : rowInTile;

                RenderTextTileRun(vram, target, x, run, charBase, tileNumber, row, columnStart, use256Colors, paletteBank, hFlip);
            }

            x += run;
            sourceX += run;
            if (sourceX >= width)
                sourceX -= width;
        }
    }

    private void RenderAffineBackgroundScanline(int bg, int y, ScanlineState state, byte[] vram, uint[] target)
    {
        int affineIndex = bg - 2;
        if (affineIndex < 0 || affineIndex > 1 || (state.EnabledMask & (1 << bg)) == 0)
            return;

        ushort control = GetBgCnt(state, bg);
        int charBase = ((control >> 2) & 0x3) * 0x4000;
        int screenBase = ((control >> 8) & 0x1F) * 0x800;
        bool wrap = (control & 0x2000) != 0;
        int size = 128 << ((control >> 14) & 0x3);
        int tilesPerAxis = size >> 3;
        int refX = affineIndex == 0 ? state.Bg2X : state.Bg3X;
        int refY = affineIndex == 0 ? state.Bg2Y : state.Bg3Y;
        int pa = affineIndex == 0 ? state.Bg2PA : state.Bg3PA;
        int pc = affineIndex == 0 ? state.Bg2PC : state.Bg3PC;

        for (int x = 0; x < FrameWidth; x++)
        {
            int sourceX = (refX + pa * x) >> 8;
            int sourceY = (refY + pc * x) >> 8;

            if (wrap)
            {
                sourceX &= size - 1;
                sourceY &= size - 1;
            }
            else if ((uint)sourceX >= (uint)size || (uint)sourceY >= (uint)size)
            {
                continue;
            }

            int mapOffset = screenBase + (sourceY >> 3) * tilesPerAxis + (sourceX >> 3);
            if ((uint)mapOffset >= (uint)vram.Length)
                continue;

            int tileNumber = vram[mapOffset];
            int pixelOffset = charBase + tileNumber * 64 + ((sourceY & 0x7) * 8) + (sourceX & 0x7);
            if ((uint)pixelOffset >= (uint)vram.Length)
                continue;

            int colorIndex = vram[pixelOffset];
            if (colorIndex == 0)
                continue;

            target[x] = _bgPaletteCache[colorIndex];
        }
    }

    private void RenderBitmapMode3Scanline(int y, ScanlineState state, byte[] vram, uint[] target)
    {
        if ((state.EnabledMask & (1 << 2)) == 0 || (state.DispCnt & 0x0400) == 0)
            return;

        int rowBase = y * FrameWidth * 2;
        for (int x = 0; x < FrameWidth; x++)
        {
            int offset = rowBase + x * 2;
            if ((uint)(offset + 1) >= (uint)vram.Length)
                continue;
            target[x] = ColorLookup[ReadU16(vram, offset) & 0x7FFF];
        }
    }

    private void RenderBitmapMode4Scanline(int y, ScanlineState state, byte[] vram, uint[] target)
    {
        if ((state.EnabledMask & (1 << 2)) == 0 || (state.DispCnt & 0x0400) == 0)
            return;

        int frameBase = (state.DispCnt & 0x0010) != 0 ? 0xA000 : 0;
        int rowBase = frameBase + y * FrameWidth;
        for (int x = 0; x < FrameWidth; x++)
        {
            int offset = rowBase + x;
            if ((uint)offset >= (uint)vram.Length)
                continue;
            int colorIndex = vram[offset];
            if (colorIndex == 0)
                continue;
            target[x] = _bgPaletteCache[colorIndex];
        }
    }

    private void RenderBitmapMode5Scanline(int y, ScanlineState state, byte[] vram, uint[] target)
    {
        if ((state.EnabledMask & (1 << 2)) == 0 || (state.DispCnt & 0x0400) == 0 || y >= 128)
            return;

        int frameBase = (state.DispCnt & 0x0010) != 0 ? 0xA000 : 0;
        int rowBase = frameBase + y * 160 * 2;
        for (int x = 0; x < 160; x++)
        {
            int offset = rowBase + x * 2;
            if ((uint)(offset + 1) >= (uint)vram.Length)
                continue;
            target[x] = ColorLookup[ReadU16(vram, offset) & 0x7FFF];
        }
    }

    private void RenderSpritesScanline(int y, ScanlineState state, byte[] oam, byte[] vram, bool mapping1D, int bgMode)
    {
        if ((state.DispCnt & 0x1000) == 0)
            return;

        for (int i = 127; i >= 0; i--)
        {
            int offset = i * 8;
            ushort attr0 = ReadU16(oam, offset);
            ushort attr1 = ReadU16(oam, offset + 2);
            ushort attr2 = ReadU16(oam, offset + 4);

            int objMode = (attr0 >> 10) & 0x3;
            if (objMode == 3)
                continue;

            bool isAffine = (attr0 & 0x0100) != 0;
            bool doubleSize = isAffine && (attr0 & 0x0200) != 0;
            if (!isAffine && (attr0 & 0x0200) != 0)
                continue;

            GetSpriteSize((attr0 >> 14) & 0x3, (attr1 >> 14) & 0x3, out int width, out int height);
            int spriteY = attr0 & 0xFF;
            int spriteX = attr1 & 0x1FF;
            if (spriteY >= 160)
                spriteY -= 256;
            if (spriteX >= 240)
                spriteX -= 512;

            bool use256Colors = (attr0 & 0x2000) != 0;
            int tileNumber = attr2 & 0x03FF;
            if (use256Colors && !mapping1D)
                tileNumber &= ~1;
            if (bgMode >= 3 && tileNumber < 512)
                continue;

            uint charBase = (uint)((0x10000 >> 1) + (tileNumber * 0x10));
            uint tileBase = 0;
            if (!mapping1D)
            {
                tileBase = use256Colors
                    ? ((charBase >> 5) & 0xFu)
                    : ((charBase >> 4) & 0x1Fu);
                charBase &= ~0x1FFu;
            }
            uint stride = mapping1D
                ? (uint)(width >> 3)
                : (uint)(0x20 >> (use256Colors ? 1 : 0));

            int renderWidth = doubleSize ? width * 2 : width;
            int renderHeight = doubleSize ? height * 2 : height;
            int left = Math.Max(spriteX, 0);
            int right = Math.Min(spriteX + renderWidth, FrameWidth);
            if (left >= right || y < spriteY || y >= spriteY + renderHeight)
                continue;

            int priority = (attr2 >> 10) & 0x3;
            int paletteBank = attr2 >> 12;
            bool hFlip = !isAffine && (attr1 & 0x1000) != 0;
            bool vFlip = !isAffine && (attr1 & 0x2000) != 0;
            bool semiTransparent = objMode == 1;
            bool objWindow = objMode == 2;

            int pa = 256;
            int pb = 0;
            int pc = 0;
            int pd = 256;
            if (isAffine)
            {
                int affineIndex = (attr1 >> 9) & 0x1F;
                pa = (short)ReadU16(oam, affineIndex * 32 + 6);
                pb = (short)ReadU16(oam, affineIndex * 32 + 14);
                pc = (short)ReadU16(oam, affineIndex * 32 + 22);
                pd = (short)ReadU16(oam, affineIndex * 32 + 30);
            }

            for (int x = left; x < right; x++)
            {
                if (!TryResolveSpritePixel(
                        x - spriteX,
                        y - spriteY,
                        width,
                        height,
                        renderWidth,
                        renderHeight,
                        isAffine,
                        hFlip,
                        vFlip,
                        pa,
                        pb,
                        pc,
                        pd,
                        out int sourceX,
                        out int sourceY))
                {
                    continue;
                }

                int colorIndex = ReadObjectTilePixel(vram, charBase, tileBase, stride, sourceX, sourceY, use256Colors, paletteBank);
                if (colorIndex == 0)
                    continue;

                if (objWindow)
                {
                    _objWindowMask[x] = 1;
                    continue;
                }

                if (!ShouldReplaceObjectPixel(x, priority, i))
                    continue;

                _objScanline[x] = _objPaletteCache[colorIndex & 0xFF];
                _objPriorities[x] = (byte)priority;
                _objOamIndices[x] = (byte)i;
                _objSemiTransparent[x] = semiTransparent ? (byte)1 : (byte)0;
            }
        }
    }

    private bool ShouldReplaceObjectPixel(int x, int priority, int oamIndex)
    {
        byte currentPriority = _objPriorities[x];
        if (priority < currentPriority)
            return true;
        if (priority > currentPriority)
            return false;
        return oamIndex < _objOamIndices[x];
    }

    private static bool TryResolveSpritePixel(
        int localX,
        int localY,
        int width,
        int height,
        int renderWidth,
        int renderHeight,
        bool isAffine,
        bool hFlip,
        bool vFlip,
        int pa,
        int pb,
        int pc,
        int pd,
        out int sourceX,
        out int sourceY)
    {
        if (!isAffine)
        {
            if ((uint)localX >= (uint)width || (uint)localY >= (uint)height)
            {
                sourceX = sourceY = 0;
                return false;
            }

            sourceX = hFlip ? width - 1 - localX : localX;
            sourceY = vFlip ? height - 1 - localY : localY;
            return true;
        }

        int centerX = renderWidth >> 1;
        int centerY = renderHeight >> 1;
        int dx = localX - centerX;
        int dy = localY - centerY;

        sourceX = ((pa * dx + pb * dy) >> 8) + (width >> 1);
        sourceY = ((pc * dx + pd * dy) >> 8) + (height >> 1);
        return (uint)sourceX < (uint)width && (uint)sourceY < (uint)height;
    }

    private static int ReadObjectTilePixel(byte[] vram, uint charBase, uint tileBase, uint stride, int sourceX, int sourceY, bool use256Colors, int paletteBank)
    {
        if (use256Colors)
        {
            uint tileIndex = ((((uint)sourceX >> 3) + tileBase) & 0xFu) + (((uint)sourceY >> 3) * stride);
            uint byteOffset = (charBase * 2u) + (tileIndex * 64u) + ((uint)(sourceY & 0x7) * 8u) + (uint)(sourceX & 0x7);
            if (byteOffset >= (uint)vram.Length)
                return 0;
            int offset = (int)byteOffset;
            if ((uint)offset >= (uint)vram.Length)
                return 0;
            return vram[offset];
        }

        uint tileIndex4 = ((((uint)sourceX >> 3) + tileBase) & 0x1Fu) + (((uint)sourceY >> 3) * stride);
        uint packedOffsetU = (charBase * 2u) + (tileIndex4 * 32u) + ((uint)(sourceY & 0x7) * 4u) + ((uint)(sourceX & 0x7) >> 1);
        if (packedOffsetU >= (uint)vram.Length)
            return 0;

        int packedOffset = (int)packedOffsetU;
        if ((uint)packedOffset >= (uint)vram.Length)
            return 0;

        int sample = vram[packedOffset];
        int nibble = ((sourceX & 1) == 0) ? (sample & 0x0F) : (sample >> 4);
        if (nibble == 0)
            return 0;
        return (paletteBank << 4) | nibble;
    }

    private static int ReadTilePixel(byte[] vram, int charBase, int tileNumber, int row, int column, bool use256Colors, int paletteBank)
    {
        if (use256Colors)
        {
            int offset = charBase + tileNumber * 64 + row * 8 + column;
            if ((uint)offset >= (uint)vram.Length)
                return 0;
            return vram[offset];
        }

        int packedOffset = charBase + tileNumber * 32 + row * 4 + (column >> 1);
        if ((uint)packedOffset >= (uint)vram.Length)
            return 0;

        int sample = vram[packedOffset];
        int nibble = ((column & 1) == 0) ? (sample & 0x0F) : (sample >> 4);
        if (nibble == 0)
            return 0;
        return (paletteBank << 4) | nibble;
    }

    private void RenderTextTileRun(
        byte[] vram,
        uint[] target,
        int targetX,
        int runLength,
        int charBase,
        int tileNumber,
        int row,
        int columnStart,
        bool use256Colors,
        int paletteBank,
        bool hFlip)
    {
        if (use256Colors)
        {
            int rowBase = charBase + tileNumber * 64 + row * 8;
            if ((uint)(rowBase + 7) >= (uint)vram.Length)
                return;

            for (int i = 0; i < runLength; i++)
            {
                int sampleColumn = columnStart + i;
                if (hFlip)
                    sampleColumn = 7 - sampleColumn;

                int colorIndex = vram[rowBase + sampleColumn];
                if (colorIndex != 0)
                    target[targetX + i] = _bgPaletteCache[colorIndex];
            }

            return;
        }

        int packedRowBase = charBase + tileNumber * 32 + row * 4;
        if ((uint)(packedRowBase + 3) >= (uint)vram.Length)
            return;

        int paletteBase = paletteBank << 4;
        for (int i = 0; i < runLength; i++)
        {
            int sampleColumn = columnStart + i;
            if (hFlip)
                sampleColumn = 7 - sampleColumn;

            int packed = vram[packedRowBase + (sampleColumn >> 1)];
            int nibble = (sampleColumn & 1) == 0 ? (packed & 0x0F) : (packed >> 4);
            if (nibble != 0)
                target[targetX + i] = _bgPaletteCache[paletteBase | nibble];
        }
    }

    private static ushort GetBgCnt(ScanlineState state, int bg) => bg switch
    {
        0 => state.BgCnt0,
        1 => state.BgCnt1,
        2 => state.BgCnt2,
        3 => state.BgCnt3,
        _ => 0,
    };

    private static short GetBgHOfs(ScanlineState state, int bg) => bg switch
    {
        0 => state.BgHOfs0,
        1 => state.BgHOfs1,
        2 => state.BgHOfs2,
        3 => state.BgHOfs3,
        _ => 0,
    };

    private static short GetBgVOfs(ScanlineState state, int bg) => bg switch
    {
        0 => state.BgVOfs0,
        1 => state.BgVOfs1,
        2 => state.BgVOfs2,
        3 => state.BgVOfs3,
        _ => 0,
    };

    private void FinalizeScanline(int y, ScanlineState state)
    {
        bool windowsEnabled = (state.DispCnt & 0xE000) != 0;
        uint backdrop = _bgPaletteCache[0];
        int bg0Prio = state.BgCnt0 & 0x3;
        int bg1Prio = state.BgCnt1 & 0x3;
        int bg2Prio = state.BgCnt2 & 0x3;
        int bg3Prio = state.BgCnt3 & 0x3;
        uint bldCnt = state.BldCnt;
        int blendEffect = (int)((bldCnt >> 6) & 0x3);
        int eva = Math.Min(state.BldAlpha & 0x1F, 16);
        int evb = Math.Min((state.BldAlpha >> 8) & 0x1F, 16);
        int evy = Math.Min(state.BldY & 0x1F, 16);
        int rowDst = y * FrameWidth;

        for (int x = 0; x < FrameWidth; x++)
        {
            uint windowMask = windowsEnabled ? ComputeWindowMask(state, x, y) : 0x3F;

            var top = new LayerCandidate(backdrop, priority: 32, t1: ((bldCnt >> 5) & 1) != 0, t2: ((bldCnt >> 13) & 1) != 0, semiTransparent: false);
            var bottom = top;

            if ((windowMask & 0x10) != 0 && _objScanline[x] != 0)
            {
                CompositeCandidate(
                    ref top,
                    ref bottom,
                    new LayerCandidate(
                        _objScanline[x],
                        _objPriorities[x],
                        t1: ((bldCnt >> 4) & 1) != 0 || _objSemiTransparent[x] != 0,
                        t2: ((bldCnt >> 12) & 1) != 0,
                        semiTransparent: _objSemiTransparent[x] != 0));
            }

            if ((windowMask & 0x01) != 0 && _bg0Scanline[x] != 0 && (state.DispCnt & 0x0100) != 0)
            {
                CompositeCandidate(ref top, ref bottom, new LayerCandidate(_bg0Scanline[x], bg0Prio, (bldCnt & 1) != 0, ((bldCnt >> 8) & 1) != 0, false));
            }

            if ((windowMask & 0x02) != 0 && _bg1Scanline[x] != 0 && (state.DispCnt & 0x0200) != 0)
            {
                CompositeCandidate(ref top, ref bottom, new LayerCandidate(_bg1Scanline[x], bg1Prio, ((bldCnt >> 1) & 1) != 0, ((bldCnt >> 9) & 1) != 0, false));
            }

            if ((windowMask & 0x04) != 0 && _bg2Scanline[x] != 0 && (state.DispCnt & 0x0400) != 0)
            {
                CompositeCandidate(ref top, ref bottom, new LayerCandidate(_bg2Scanline[x], bg2Prio, ((bldCnt >> 2) & 1) != 0, ((bldCnt >> 10) & 1) != 0, false));
            }

            if ((windowMask & 0x08) != 0 && _bg3Scanline[x] != 0 && (state.DispCnt & 0x0800) != 0)
            {
                CompositeCandidate(ref top, ref bottom, new LayerCandidate(_bg3Scanline[x], bg3Prio, ((bldCnt >> 3) & 1) != 0, ((bldCnt >> 11) & 1) != 0, false));
            }

            if ((windowMask & 0x20) == 0)
                top.T1 = false;

            uint finalColor = top.Color;
            if ((top.SemiTransparent || (blendEffect == 1 && top.T1)) && bottom.T2)
            {
                finalColor = BlendColors(top.Color, bottom.Color, eva, evb);
            }
            else if (!top.SemiTransparent && top.T1)
            {
                if (blendEffect == 2)
                    finalColor = BrightenColor(top.Color, evy);
                else if (blendEffect == 3)
                    finalColor = DarkenColor(top.Color, evy);
            }

            _frameBuffer[rowDst + x] = finalColor;
        }
    }

    private void BuildPaletteCache(int y)
    {
        if (_hasCapturedFrame)
        {
            int lineBase = y * PaletteSize;
            for (int i = 0; i < 256; i++)
            {
                int bgOffset = lineBase + (i * 2);
                int objOffset = lineBase + 0x200 + (i * 2);
                _bgPaletteCache[i] = ColorLookup[ReadU16(_framePalette, bgOffset) & 0x7FFF];
                _objPaletteCache[i] = ColorLookup[ReadU16(_framePalette, objOffset) & 0x7FFF];
            }

            return;
        }

        byte[] paletteRam = Gba.Memory.PaletteRam;
        for (int i = 0; i < 256; i++)
        {
            int bgOffset = i * 2;
            int objOffset = 0x200 + (i * 2);
            _bgPaletteCache[i] = ColorLookup[ReadU16(paletteRam, bgOffset) & 0x7FFF];
            _objPaletteCache[i] = ColorLookup[ReadU16(paletteRam, objOffset) & 0x7FFF];
        }
    }

    private void RebuildScreenshotBuffer()
    {
        for (int i = 0; i < _frameBuffer.Length; i++)
        {
            uint color = _frameBuffer[i];
            int dst = i * 4;
            _screenshotBuffer[dst] = (byte)(color >> 16);
            _screenshotBuffer[dst + 1] = (byte)(color >> 8);
            _screenshotBuffer[dst + 2] = (byte)color;
            _screenshotBuffer[dst + 3] = (byte)(color >> 24);
        }

        _screenshotDirty = false;
    }

    private uint ComputeWindowMask(ScanlineState state, int x, int y)
    {
        bool win0Enable = (state.DispCnt & 0x2000) != 0;
        bool win1Enable = (state.DispCnt & 0x4000) != 0;
        bool objWinEnable = (state.DispCnt & 0x8000) != 0;
        if (!win0Enable && !win1Enable && !objWinEnable)
            return 0x3F;

        if (win0Enable && WindowContains(x, y, state.Win0H, state.Win0V))
            return (uint)(state.WinIn & 0x3F);
        if (win1Enable && WindowContains(x, y, state.Win1H, state.Win1V))
            return (uint)((state.WinIn >> 8) & 0x3F);
        if (objWinEnable && _objWindowMask[x] != 0)
            return (uint)((state.WinOut >> 8) & 0x3F);
        return (uint)(state.WinOut & 0x3F);
    }

    private static bool WindowContains(int x, int y, ushort winH, ushort winV)
    {
        int left = (winH >> 8) & 0xFF;
        int right = winH & 0xFF;
        int top = (winV >> 8) & 0xFF;
        int bottom = winV & 0xFF;
        return RangeContains(x, left, right) && RangeContains(y, top, bottom);
    }

    private static bool RangeContains(int value, int start, int end)
    {
        if (start <= end)
            return value >= start && value < end;
        return value >= start || value < end;
    }

    private static void CompositeCandidate(ref LayerCandidate top, ref LayerCandidate bottom, LayerCandidate candidate)
    {
        if (candidate.Priority >= top.Priority)
        {
            if (candidate.Priority >= bottom.Priority)
                return;
            bottom = candidate;
            return;
        }

        bottom = top;
        top = candidate;
    }

    private static uint BlendColors(uint top, uint bottom, int eva, int evb)
    {
        int b = Math.Min((((int)(top & 0xFF) * eva) + ((int)(bottom & 0xFF) * evb)) / 16, 255);
        int g = Math.Min((((int)((top >> 8) & 0xFF) * eva) + ((int)((bottom >> 8) & 0xFF) * evb)) / 16, 255);
        int r = Math.Min((((int)((top >> 16) & 0xFF) * eva) + ((int)((bottom >> 16) & 0xFF) * evb)) / 16, 255);
        return (uint)(b | (g << 8) | (r << 16) | 0xFF000000);
    }

    private static uint BrightenColor(uint color, int evy)
    {
        int b = (int)(color & 0xFF);
        int g = (int)((color >> 8) & 0xFF);
        int r = (int)((color >> 16) & 0xFF);
        b += ((255 - b) * evy) / 16;
        g += ((255 - g) * evy) / 16;
        r += ((255 - r) * evy) / 16;
        return (uint)(Math.Min(b, 255) | (Math.Min(g, 255) << 8) | (Math.Min(r, 255) << 16) | 0xFF000000);
    }

    private static uint DarkenColor(uint color, int evy)
    {
        int b = (int)(color & 0xFF);
        int g = (int)((color >> 8) & 0xFF);
        int r = (int)((color >> 16) & 0xFF);
        b -= (b * evy) / 16;
        g -= (g * evy) / 16;
        r -= (r * evy) / 16;
        return (uint)(Math.Max(b, 0) | (Math.Max(g, 0) << 8) | (Math.Max(r, 0) << 16) | 0xFF000000);
    }

    private static void GetTextBgDimensions(int size, out int width, out int height)
    {
        switch (size & 0x3)
        {
            case 0:
                width = 256;
                height = 256;
                break;
            case 1:
                width = 512;
                height = 256;
                break;
            case 2:
                width = 256;
                height = 512;
                break;
            default:
                width = 512;
                height = 512;
                break;
        }
    }

    private static void GetSpriteSize(int shape, int size, out int width, out int height)
    {
        switch (shape)
        {
            case 0:
                width = height = 8 << size;
                break;
            case 1:
                (width, height) = size switch
                {
                    0 => (16, 8),
                    1 => (32, 8),
                    2 => (32, 16),
                    _ => (64, 32),
                };
                break;
            case 2:
                (width, height) = size switch
                {
                    0 => (8, 16),
                    1 => (8, 32),
                    2 => (16, 32),
                    _ => (32, 64),
                };
                break;
            default:
                width = height = 8;
                break;
        }
    }

    private static int Mod9Bit(int value, int modulo)
    {
        int result = value % modulo;
        return result < 0 ? result + modulo : result;
    }

    private static ushort ReadU16(byte[] source, int offset)
    {
        return (ushort)(source[offset] | (source[offset + 1] << 8));
    }

    private static uint ToBgra(ushort gbaColor)
    {
        uint r = (uint)(gbaColor & 0x1F);
        uint g = (uint)((gbaColor >> 5) & 0x1F);
        uint b = (uint)((gbaColor >> 10) & 0x1F);
        r = (r << 3) | (r >> 2);
        g = (g << 3) | (g >> 2);
        b = (b << 3) | (b >> 2);
        return b | (g << 8) | (r << 16) | 0xFF000000u;
    }

    private static uint[] BuildColorLookup()
    {
        uint[] lookup = new uint[0x8000];
        for (int i = 0; i < lookup.Length; i++)
            lookup[i] = ToBgra((ushort)i);
        return lookup;
    }

    private struct LayerCandidate
    {
        public LayerCandidate(uint color, int priority, bool t1, bool t2, bool semiTransparent)
        {
            Color = color;
            Priority = priority;
            T1 = t1;
            T2 = t2;
            SemiTransparent = semiTransparent;
        }

        public uint Color;
        public int Priority;
        public bool T1;
        public bool T2;
        public bool SemiTransparent;
    }
}
