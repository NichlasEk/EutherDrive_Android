namespace EutherDrive.Core.GbaEmu;

public partial class GbaVideo
{
    private const int FrameWidth = GbaConstants.ScreenWidth;
    private const int FrameHeight = GbaConstants.ScreenHeight;
    private const int FrameStride = FrameWidth * 4;

    private readonly uint[] _finalPixels = new uint[FrameWidth * FrameHeight];
    private readonly byte[] _pixelPriorities = new byte[FrameWidth * FrameHeight];
    private readonly byte[] _pixelOrders = new byte[FrameWidth * FrameHeight];
    private readonly byte[] _frameBuffer = new byte[FrameHeight * FrameStride];
    private readonly byte[] _screenshotBuffer = new byte[FrameWidth * FrameHeight * 4];

    public ReadOnlySpan<byte> GetFrameBuffer() => _frameBuffer;

    public void RefreshFrame()
    {
        RenderSoftwareFrame();
    }

    public byte[] CaptureScreenshot()
    {
        byte[] copy = new byte[_screenshotBuffer.Length];
        Buffer.BlockCopy(_screenshotBuffer, 0, copy, 0, copy.Length);
        return copy;
    }

    private void CaptureScanline(int y)
    {
        _ = y;
    }

    private void SnapshotVram()
    {
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

    private void RenderSoftwareFrame()
    {
        if ((DispCnt & 0x0080) != 0)
        {
            FillSolid(0xFFFFFFFFu);
            return;
        }

        uint backdrop = ReadPaletteColor(0, objPalette: false);
        Array.Fill(_finalPixels, backdrop);
        Array.Fill(_pixelPriorities, byte.MaxValue);
        Array.Fill(_pixelOrders, byte.MaxValue);

        int mode = DispCnt & 7;
        switch (mode)
        {
            case 0:
                RenderTextBackground(3, order: 0);
                RenderTextBackground(2, order: 1);
                RenderTextBackground(1, order: 2);
                RenderTextBackground(0, order: 3);
                break;
            case 1:
                RenderAffineBackground(2, order: 1);
                RenderTextBackground(1, order: 2);
                RenderTextBackground(0, order: 3);
                break;
            case 2:
                RenderAffineBackground(3, order: 0);
                RenderAffineBackground(2, order: 1);
                break;
            case 3:
                RenderBitmapMode3(order: 1);
                break;
            case 4:
                RenderBitmapMode4(order: 1);
                break;
            case 5:
                RenderBitmapMode5(order: 1);
                break;
        }

        RenderSprites();
        WriteFrameBuffers();
    }

    private void FillSolid(uint color)
    {
        Array.Fill(_finalPixels, color);
        for (int i = 0; i < _frameBuffer.Length; i += 4)
        {
            _frameBuffer[i] = (byte)color;
            _frameBuffer[i + 1] = (byte)(color >> 8);
            _frameBuffer[i + 2] = (byte)(color >> 16);
            _frameBuffer[i + 3] = (byte)(color >> 24);
        }

        for (int i = 0; i < _screenshotBuffer.Length; i += 4)
        {
            _screenshotBuffer[i] = (byte)(color >> 16);
            _screenshotBuffer[i + 1] = (byte)(color >> 8);
            _screenshotBuffer[i + 2] = (byte)color;
            _screenshotBuffer[i + 3] = (byte)(color >> 24);
        }
    }

    private void WriteFrameBuffers()
    {
        for (int i = 0, dst = 0; i < _finalPixels.Length; i++, dst += 4)
        {
            uint color = _finalPixels[i];
            _frameBuffer[dst] = (byte)color;
            _frameBuffer[dst + 1] = (byte)(color >> 8);
            _frameBuffer[dst + 2] = (byte)(color >> 16);
            _frameBuffer[dst + 3] = (byte)(color >> 24);

            _screenshotBuffer[dst] = (byte)(color >> 16);
            _screenshotBuffer[dst + 1] = (byte)(color >> 8);
            _screenshotBuffer[dst + 2] = (byte)color;
            _screenshotBuffer[dst + 3] = (byte)(color >> 24);
        }
    }

    private void RenderTextBackground(int bg, byte order)
    {
        uint enableMask = (uint)(0x0100 << bg);
        if ((DispCnt & enableMask) == 0)
            return;

        int mode = DispCnt & 7;
        if (mode > 1 && bg < 2)
            return;

        ushort control = BgCnt[bg];
        int priority = control & 0x3;
        int charBase = ((control >> 2) & 0x3) * 0x4000;
        int screenBase = ((control >> 8) & 0x1F) * 0x800;
        bool use256Colors = (control & 0x0080) != 0;
        GetTextBgDimensions((control >> 14) & 0x3, out int width, out int height);
        int blocksPerRow = width >> 8;

        for (int y = 0; y < FrameHeight; y++)
        {
            int sourceY = Mod9Bit(y + BgVOfs[bg], height);
            int tileY = sourceY >> 3;
            int rowInTile = sourceY & 0x7;
            int blockY = tileY >> 5;

            for (int x = 0; x < FrameWidth; x++)
            {
                int sourceX = Mod9Bit(x + BgHOfs[bg], width);
                int tileX = sourceX >> 3;
                int blockX = tileX >> 5;
                int blockIndex = blockY * blocksPerRow + blockX;
                int mapOffset = screenBase + blockIndex * 0x800 + (((tileY & 31) * 32 + (tileX & 31)) * 2);
                if ((uint)(mapOffset + 1) >= (uint)Gba.Memory.Vram.Length)
                    continue;

                ushort entry = ReadU16(Gba.Memory.Vram, mapOffset);
                int tileNumber = entry & 0x03FF;
                bool hFlip = (entry & 0x0400) != 0;
                bool vFlip = (entry & 0x0800) != 0;
                int paletteBank = entry >> 12;

                int column = hFlip ? 7 - (sourceX & 0x7) : (sourceX & 0x7);
                int row = vFlip ? 7 - rowInTile : rowInTile;
                int colorIndex = ReadTilePixel(charBase, tileNumber, row, column, use256Colors, paletteBank);
                if (colorIndex == 0)
                    continue;

                TryPlot(x, y, ReadPaletteColor(colorIndex, objPalette: false), priority, order);
            }
        }
    }

    private void RenderAffineBackground(int bg, byte order)
    {
        uint enableMask = (uint)(0x0100 << bg);
        if ((DispCnt & enableMask) == 0)
            return;

        int affineIndex = bg - 2;
        if (affineIndex < 0 || affineIndex > 1)
            return;

        ushort control = BgCnt[bg];
        int priority = control & 0x3;
        int charBase = ((control >> 2) & 0x3) * 0x4000;
        int screenBase = ((control >> 8) & 0x1F) * 0x800;
        bool wrap = (control & 0x2000) != 0;
        int size = 128 << ((control >> 14) & 0x3);
        int tilesPerAxis = size >> 3;

        int refX = BgRefX[affineIndex];
        int refY = BgRefY[affineIndex];
        int pa = BgPA[affineIndex];
        int pb = BgPB[affineIndex];
        int pc = BgPC[affineIndex];
        int pd = BgPD[affineIndex];

        for (int y = 0; y < FrameHeight; y++)
        {
            for (int x = 0; x < FrameWidth; x++)
            {
                int sourceX = (refX + pa * x + pb * y) >> 8;
                int sourceY = (refY + pc * x + pd * y) >> 8;

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
                if ((uint)mapOffset >= (uint)Gba.Memory.Vram.Length)
                    continue;

                int tileNumber = Gba.Memory.Vram[mapOffset];
                int pixelOffset = charBase + tileNumber * 64 + ((sourceY & 0x7) * 8) + (sourceX & 0x7);
                if ((uint)pixelOffset >= (uint)Gba.Memory.Vram.Length)
                    continue;

                int colorIndex = Gba.Memory.Vram[pixelOffset];
                if (colorIndex == 0)
                    continue;

                TryPlot(x, y, ReadPaletteColor(colorIndex, objPalette: false), priority, order);
            }
        }
    }

    private void RenderBitmapMode3(byte order)
    {
        if ((DispCnt & 0x0400) == 0)
            return;

        int priority = BgCnt[2] & 0x3;
        byte[] vram = Gba.Memory.Vram;
        for (int y = 0; y < FrameHeight; y++)
        {
            int rowBase = y * FrameWidth * 2;
            for (int x = 0; x < FrameWidth; x++)
            {
                int offset = rowBase + x * 2;
                if ((uint)(offset + 1) >= (uint)vram.Length)
                    continue;
                TryPlot(x, y, ToBgra(ReadU16(vram, offset)), priority, order);
            }
        }
    }

    private void RenderBitmapMode4(byte order)
    {
        if ((DispCnt & 0x0400) == 0)
            return;

        int priority = BgCnt[2] & 0x3;
        int frameBase = (DispCnt & 0x0010) != 0 ? 0xA000 : 0;
        byte[] vram = Gba.Memory.Vram;
        for (int y = 0; y < FrameHeight; y++)
        {
            int rowBase = frameBase + y * FrameWidth;
            for (int x = 0; x < FrameWidth; x++)
            {
                int offset = rowBase + x;
                if ((uint)offset >= (uint)vram.Length)
                    continue;
                int colorIndex = vram[offset];
                if (colorIndex == 0)
                    continue;
                TryPlot(x, y, ReadPaletteColor(colorIndex, objPalette: false), priority, order);
            }
        }
    }

    private void RenderBitmapMode5(byte order)
    {
        if ((DispCnt & 0x0400) == 0)
            return;

        int priority = BgCnt[2] & 0x3;
        int frameBase = (DispCnt & 0x0010) != 0 ? 0xA000 : 0;
        byte[] vram = Gba.Memory.Vram;
        for (int y = 0; y < 128; y++)
        {
            int rowBase = frameBase + y * 160 * 2;
            for (int x = 0; x < 160; x++)
            {
                int offset = rowBase + x * 2;
                if ((uint)(offset + 1) >= (uint)vram.Length)
                    continue;
                TryPlot(x, y, ToBgra(ReadU16(vram, offset)), priority, order);
            }
        }
    }

    private void RenderSprites()
    {
        if ((DispCnt & 0x1000) == 0)
            return;

        byte[] oam = Gba.Memory.Oam;
        byte[] vram = Gba.Memory.Vram;
        bool mapping1D = (DispCnt & 0x0040) != 0;
        int bgMode = DispCnt & 0x7;

        for (int i = 127; i >= 0; i--)
        {
            int offset = i * 8;
            ushort attr0 = ReadU16(oam, offset);
            ushort attr1 = ReadU16(oam, offset + 2);
            ushort attr2 = ReadU16(oam, offset + 4);

            int objMode = (attr0 >> 10) & 0x3;
            if (objMode == 2 || objMode == 3)
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
            if (use256Colors)
                tileNumber &= ~1;
            if (bgMode >= 3 && tileNumber < 512)
                continue;

            int renderWidth = doubleSize ? width * 2 : width;
            int renderHeight = doubleSize ? height * 2 : height;
            int left = Math.Max(spriteX, 0);
            int top = Math.Max(spriteY, 0);
            int right = Math.Min(spriteX + renderWidth, FrameWidth);
            int bottom = Math.Min(spriteY + renderHeight, FrameHeight);
            if (left >= right || top >= bottom)
                continue;

            int priority = (attr2 >> 10) & 0x3;
            int paletteBank = attr2 >> 12;
            bool hFlip = !isAffine && (attr1 & 0x1000) != 0;
            bool vFlip = !isAffine && (attr1 & 0x2000) != 0;

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

            for (int y = top; y < bottom; y++)
            {
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

                    int colorIndex = ReadObjectTilePixel(vram, tileNumber, sourceX, sourceY, width, mapping1D, use256Colors, paletteBank);
                    if (colorIndex == 0)
                        continue;

                    TryPlot(x, y, ReadPaletteColor(colorIndex, objPalette: true), priority, order: 4);
                }
            }
        }
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

    private int ReadObjectTilePixel(byte[] vram, int tileNumber, int sourceX, int sourceY, int spriteWidth, bool mapping1D, bool use256Colors, int paletteBank)
    {
        int tileUnitsPerTile = use256Colors ? 2 : 1;
        int tileUnitsPerRow = mapping1D ? (spriteWidth >> 3) * tileUnitsPerTile : 32;
        int tileUnit = tileNumber + ((sourceY >> 3) * tileUnitsPerRow) + ((sourceX >> 3) * tileUnitsPerTile);
        int byteOffset = 0x10000 + tileUnit * 32 + ((sourceY & 0x7) * (use256Colors ? 8 : 4));
        if ((uint)byteOffset >= (uint)vram.Length)
            return 0;

        if (use256Colors)
        {
            int offset = byteOffset + (sourceX & 0x7);
            if ((uint)offset >= (uint)vram.Length)
                return 0;
            return vram[offset];
        }

        int packedOffset = byteOffset + ((sourceX & 0x7) >> 1);
        if ((uint)packedOffset >= (uint)vram.Length)
            return 0;

        int sample = vram[packedOffset];
        int nibble = ((sourceX & 1) == 0) ? (sample & 0x0F) : (sample >> 4);
        if (nibble == 0)
            return 0;
        return (paletteBank << 4) | nibble;
    }

    private int ReadTilePixel(int charBase, int tileNumber, int row, int column, bool use256Colors, int paletteBank)
    {
        byte[] vram = Gba.Memory.Vram;
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

    private void TryPlot(int x, int y, uint color, int priority, byte order)
    {
        int index = y * FrameWidth + x;
        byte currentPriority = _pixelPriorities[index];
        byte currentOrder = _pixelOrders[index];
        byte newPriority = (byte)priority;
        if (newPriority > currentPriority)
            return;
        if (newPriority == currentPriority && order <= currentOrder)
            return;

        _pixelPriorities[index] = newPriority;
        _pixelOrders[index] = order;
        _finalPixels[index] = color;
    }

    private uint ReadPaletteColor(int colorIndex, bool objPalette)
    {
        int baseOffset = objPalette ? 0x200 : 0;
        int offset = baseOffset + colorIndex * 2;
        if ((uint)(offset + 1) >= (uint)Gba.Memory.PaletteRam.Length)
            return 0;
        return ToBgra(ReadU16(Gba.Memory.PaletteRam, offset));
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
}
