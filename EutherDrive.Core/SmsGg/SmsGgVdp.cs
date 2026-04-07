namespace EutherDrive.Core.SmsGg;

public sealed class SmsGgVdp
{
    private static readonly bool TraceGgCram = Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_GG_CRAM") == "1";
    private static readonly bool TraceGgSprites = Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_GG_SPRITES") == "1";
    private static readonly int TraceGgSpritesLineMin = ParseTraceInt("EUTHERDRIVE_TRACE_GG_SPRITES_LINE_MIN", 0);
    private static readonly int TraceGgSpritesLineMax = ParseTraceInt("EUTHERDRIVE_TRACE_GG_SPRITES_LINE_MAX", 255);
    public const int ScreenWidth = 256;
    public const int ScreenHeight = 240;

    private const int VramLength = 16 * 1024;
    private const int CramLength = 64;
    private const ushort DataAddressMask = 0x3FFF;

    private readonly SmsGgVdpVersion _version;
    private readonly bool _ggUseSmsResolution;
    private readonly byte[] _vram = new byte[VramLength];
    private readonly byte[] _cram = new byte[CramLength];
    private readonly byte[] _frameBuffer = new byte[ScreenWidth * ScreenHeight * 4];
    private readonly byte[] _spritePixels = new byte[ScreenWidth];
    private readonly bool[] _spriteCollisions = new bool[ScreenWidth];

    private SmsGgViewportSize _viewport;
    private SmsGgVdpMode _mode = SmsGgVdpMode.Mode4;
    private bool _displayEnabled;
    private bool _frameInterruptEnabled;
    private bool _lineInterruptEnabled;
    private bool _hideLeftColumn;
    private bool _verticalScrollLock;
    private bool _horizontalScrollLock;
    private byte _backdropColor;
    private byte _xScroll;
    private byte _yScroll;
    private byte _lineCounterReloadValue;
    private ushort _baseNameTableAddress = 0x3800;
    private ushort _nameTableAddressMask = 0xFFFF;
    private ushort _baseSpriteTableAddress = 0x3F00;
    private ushort _baseSpritePatternAddress = 0x2000;
    private bool _doubleSpriteHeight;
    private bool _doubleSpriteSize;
    private bool _shiftSpritesLeft;
    private ushort _latchedBaseSpriteTableAddress = 0x3F00;
    private ushort _latchedBaseSpritePatternAddress = 0x2000;
    private bool _latchedDoubleSpriteHeight;
    private bool _latchedDoubleSpriteSize;
    private bool _latchedShiftSpritesLeft;
    private bool _frameInterruptPending;
    private bool _frameInterruptFlag;
    private bool _lineInterruptPending;
    private bool _spriteOverflow;
    private bool _spriteCollision;
    private bool _controlFirst = true;
    private byte _latchedControlByte;
    private bool _cramWriteMode;
    private ushort _dataAddress;
    private byte _dataReadBuffer;
    private byte _cramWriteLatch;
    private byte _lineCounter = 0xFF;
    private byte _latchedHCounter;
    private ushort _scanline;
    private ushort _dot;
    private byte _eventIndex;
    private int _cramTraceCount;
    private bool _lineSpriteOverflow;
    private int _spriteTraceCount;

    public SmsGgVdp(SmsGgVdpVersion version, SmsGgEmulatorConfig config)
    {
        _version = version;
        _ggUseSmsResolution = config.GgUseSmsResolution;
        _viewport = DefaultViewport(version, _ggUseSmsResolution);
        ClearFrameBuffer();
    }

    public SmsGgViewportSize Viewport => _viewport;

    public bool InterruptPending =>
        (_frameInterruptEnabled && _frameInterruptPending) ||
        (_lineInterruptEnabled && _lineInterruptPending);

    public byte ReadData()
    {
        byte buffered = _dataReadBuffer;
        _dataReadBuffer = _vram[_dataAddress];
        _dataAddress = (ushort)((_dataAddress + 1) & DataAddressMask);
        _controlFirst = true;
        return buffered;
    }

    public byte ReadControl()
    {
        byte status = (byte)((_frameInterruptFlag ? 0x80 : 0) | (_spriteOverflow ? 0x40 : 0) | (_spriteCollision ? 0x20 : 0));
        _frameInterruptPending = false;
        _frameInterruptFlag = false;
        _lineInterruptPending = false;
        _spriteOverflow = false;
        _spriteCollision = false;
        _controlFirst = true;
        return status;
    }

    public void WriteControl(byte value)
    {
        if (_controlFirst)
        {
            _latchedControlByte = value;
            _dataAddress = (ushort)((_dataAddress & 0xFF00) | value);
            _controlFirst = false;
            return;
        }

        _dataAddress = (ushort)((_dataAddress & 0x00FF) | ((value & 0x3F) << 8));
        switch (value & 0xC0)
        {
            case 0x00:
                _dataReadBuffer = _vram[_dataAddress];
                _dataAddress = (ushort)((_dataAddress + 1) & DataAddressMask);
                _cramWriteMode = false;
                break;
            case 0x40:
                _cramWriteMode = false;
                break;
            case 0x80:
                WriteRegister((byte)(value & 0x0F), _latchedControlByte);
                _cramWriteMode = false;
                break;
            case 0xC0:
                _cramWriteMode = true;
                break;
        }

        _controlFirst = true;
    }

    public void WriteData(byte value)
    {
        if (_cramWriteMode)
        {
            int cramAddress = _dataAddress & (_version == SmsGgVdpVersion.GameGear ? 0x3F : 0x1F);
            if (_version.IsMasterSystem())
            {
                _cram[cramAddress] = value;
            }
            else
            {
                if ((cramAddress & 1) == 0)
                {
                    _cramWriteLatch = value;
                }
                else
                {
                    _cram[cramAddress & ~1] = _cramWriteLatch;
                    _cram[cramAddress] = value;
                    if (TraceGgCram && _cramTraceCount < 64)
                    {
                        ushort word = (ushort)(_cram[cramAddress & ~1] | (_cram[cramAddress] << 8));
                        Console.WriteLine($"[GG-CRAM] addr=0x{(cramAddress & ~1):X2} word=0x{word:X4} r={(word & 0x0F)} g={((word >> 4) & 0x0F)} b={((word >> 8) & 0x0F)} line={_scanline} dot={_dot}");
                        _cramTraceCount++;
                    }
                }
            }
        }
        else
        {
            _vram[_dataAddress] = value;
        }

        _dataAddress = (ushort)((_dataAddress + 1) & DataAddressMask);
        _dataReadBuffer = value;
        _controlFirst = true;
    }

    public byte VCounter()
    {
        int scanline = _dot >= 308 ? (_scanline + 1) % ScanlinesPerFrame() : _scanline;

        return (_version.TimingMode(), _mode) switch
        {
            (SmsGgTimingMode.Ntsc, SmsGgVdpMode.Mode4) => (byte)(scanline <= 0xDA ? scanline : scanline - 6),
            (SmsGgTimingMode.Pal, SmsGgVdpMode.Mode4) => (byte)(scanline <= 0xF2 ? scanline : scanline - 57),
            (SmsGgTimingMode.Ntsc, SmsGgVdpMode.Mode4_224) => (byte)(scanline <= 0xEA ? scanline : scanline - 6),
            (SmsGgTimingMode.Pal, SmsGgVdpMode.Mode4_224) => scanline switch
            {
                <= 0xFF => (byte)scanline,
                <= 0x102 => (byte)(scanline - 0x100),
                _ => (byte)(scanline - 57)
            },
            _ => (byte)scanline
        };
    }

    public byte HCounter() => _latchedHCounter;

    public void LatchHCounterOnThChange()
    {
        int dot = _dot + 10;
        if (dot >= 342)
            dot -= 342;

        _latchedHCounter = dot >= 296
            ? unchecked((byte)(-((342 - dot) >> 1)))
            : (byte)(dot >> 1);
    }

    public bool Tick()
    {
        ushort activeScanlines = ActiveScanlines();
        ushort scanlinesPerFrame = ScanlinesPerFrame();

        _dot++;
        if (_dot == 342)
        {
            _scanline++;
            _dot = 0;
            _eventIndex = 0;
            if (_scanline >= scanlinesPerFrame)
                _scanline = 0;
        }

        ProcessEvents(activeScanlines, scanlinesPerFrame);

        if (_displayEnabled &&
            _scanline < activeScanlines &&
            _spriteCollisions.ElementAtOrDefault((int)_dot - 2))
        {
            _spriteCollision = true;
        }

        return _scanline == activeScanlines + 1 && _dot == 0;
    }

    public void RenderFrame()
    {
        // Frame output is built incrementally in Tick() using per-scanline events.
    }

    public ReadOnlySpan<byte> GetFrameBuffer() => _frameBuffer;

    private void WriteRegister(byte register, byte value)
    {
        switch (register)
        {
            case 0:
                _verticalScrollLock = (value & 0x80) != 0;
                _horizontalScrollLock = (value & 0x40) != 0;
                _hideLeftColumn = (value & 0x20) != 0;
                _lineInterruptEnabled = (value & 0x10) != 0;
                _shiftSpritesLeft = (value & 0x08) != 0;
                UpdateMode((value & 0x04) != 0, null, (value & 0x02) != 0, null);
                break;
            case 1:
                _displayEnabled = (value & 0x40) != 0;
                _frameInterruptEnabled = (value & 0x20) != 0;
                _doubleSpriteHeight = (value & 0x02) != 0;
                _doubleSpriteSize = (value & 0x01) != 0;
                UpdateMode(null, (value & 0x10) != 0, null, (value & 0x08) != 0);
                break;
            case 2:
                _baseNameTableAddress = (ushort)((value & 0x0F) << 10);
                _nameTableAddressMask = _version is SmsGgVdpVersion.NtscMasterSystem1 or SmsGgVdpVersion.PalMasterSystem1
                    ? (ushort)(0xFBFF | ((value & 0x01) << 10))
                    : (ushort)0xFFFF;
                break;
            case 5:
                _baseSpriteTableAddress = (ushort)((value & 0x7F) << 7);
                break;
            case 6:
                _baseSpritePatternAddress = (ushort)((value & 0x07) << 11);
                break;
            case 7:
                _backdropColor = (byte)(value & 0x0F);
                break;
            case 8:
                _xScroll = value;
                break;
            case 9:
                _yScroll = value;
                break;
            case 10:
                _lineCounterReloadValue = value;
                break;
        }
    }

    private void UpdateMode(bool? bit3, bool? bit0, bool? bit1, bool? bit2)
    {
        bool m3 = bit3 ?? (_mode == SmsGgVdpMode.Mode4 || _mode == SmsGgVdpMode.Mode4_224);
        bool m0 = bit0 ?? (_mode == SmsGgVdpMode.Mode4 || _mode == SmsGgVdpMode.Mode4_224);
        bool m1 = bit1 ?? false;
        bool m2 = bit2 ?? (_mode == SmsGgVdpMode.Mode4 || _mode == SmsGgVdpMode.Mode4_224);

        _mode = (m3, m0, m1, m2) switch
        {
            (true, true, false, true) => SmsGgVdpMode.Mode4_224,
            _ => SmsGgVdpMode.Mode4
        };

        _viewport = DefaultViewport(_version, _ggUseSmsResolution, _mode);
    }

    private void DecrementLineCounter()
    {
        if (_scanline < ActiveScanlines() || _scanline == ScanlinesPerFrame() - 1)
        {
            if (_lineCounter == 0)
            {
                _lineCounter = _lineCounterReloadValue;
                _lineInterruptPending = true;
            }
            else
            {
                _lineCounter--;
            }
        }
        else
        {
            _lineCounter = _lineCounterReloadValue;
        }
    }

    private ushort ActiveScanlines() => _mode == SmsGgVdpMode.Mode4_224 ? (ushort)224 : (ushort)192;

    private ushort ScanlinesPerFrame() => _version.TimingMode() == SmsGgTimingMode.Pal ? (ushort)313 : (ushort)262;

    private byte GetTileColor(ushort tileIndex, int row, int col)
    {
        int tileAddress = ((tileIndex * 32) + (row * 4)) & 0x3FFF;
        byte bit0 = (byte)((_vram[tileAddress] >> col) & 1);
        byte bit1 = (byte)((_vram[tileAddress + 1] >> col) & 1);
        byte bit2 = (byte)((_vram[tileAddress + 2] >> col) & 1);
        byte bit3 = (byte)((_vram[tileAddress + 3] >> col) & 1);
        return (byte)(bit0 | (bit1 << 1) | (bit2 << 2) | (bit3 << 3));
    }

    private bool RenderSpritesForScanline(
        ushort scanline,
        Span<byte> spritePixels,
        Span<bool> spriteCollisions,
        ushort baseSpriteTableAddress,
        ushort baseSpritePatternAddress,
        bool doubleSpriteHeight,
        bool doubleSpriteSize,
        bool shiftSpritesLeft)
    {
        spritePixels.Clear();
        spriteCollisions.Clear();

        int spriteHeight = GetSpriteHeight(doubleSpriteSize, doubleSpriteHeight);
        int spriteWidth = GetSpriteWidth(doubleSpriteSize);
        int satBase = baseSpriteTableAddress & 0xFF00;
        int spriteCount = 0;

        for (int i = 0; i < 64; i++)
        {
            byte y = _vram[(satBase | i) & 0x3FFF];
            if (_mode != SmsGgVdpMode.Mode4_224 && y == 0xD0)
                return false;

            int spriteBottom = (y + spriteHeight) & 0xFF;
            bool overlaps = y < spriteBottom
                ? scanline >= y && scanline < spriteBottom
                : scanline >= y || scanline < spriteBottom;
            if (!overlaps)
                continue;

            spriteCount++;
            if (spriteCount > 8)
            {
                return true;
            }

            byte x = _vram[(satBase | 0x80 | (2 * i)) & 0x3FFF];
            byte rawTileIndex = _vram[(satBase | 0x80 | (2 * i + 1)) & 0x3FFF];
            int spriteTileRow = ((scanline - y) & 0xFF) >> (doubleSpriteSize ? 1 : 0);
            int tileIndex = doubleSpriteHeight ? ((rawTileIndex & 0xFE) | (spriteTileRow >= 8 ? 1 : 0)) : rawTileIndex;
            int tileAddress = ((baseSpritePatternAddress & 0x2000) | (tileIndex * 32)) & 0x3FFF;
            int spriteXDelta = shiftSpritesLeft ? -8 : 0;

            bool interestingSprite = x != 0 || y != 0 || rawTileIndex != 0;
            if (TraceGgSprites &&
                _version == SmsGgVdpVersion.GameGear &&
                interestingSprite &&
                scanline >= TraceGgSpritesLineMin &&
                scanline <= TraceGgSpritesLineMax &&
                _spriteTraceCount < 256)
            {
                Console.WriteLine(
                    $"[GG-SPR] line={scanline} idx={i} sat=0x{satBase:X4} x=0x{x:X2} y=0x{y:X2} tile=0x{rawTileIndex:X2} row={spriteTileRow} size={spriteWidth}x{spriteHeight} shift={(shiftSpritesLeft ? 1 : 0)}");
                _spriteTraceCount++;
            }

            for (int dx = 0; dx < spriteWidth; dx++)
            {
                int pixelX = x + dx + spriteXDelta;
                if ((uint)pixelX >= ScreenWidth)
                    continue;

                int spriteTileCol = dx >> (doubleSpriteSize ? 1 : 0);
                byte colorId = GetTileColorAtAddress(tileAddress, spriteTileRow & 7, spriteTileCol);
                if (colorId == 0)
                    continue;

                if (spritePixels[pixelX] != 0)
                    spriteCollisions[pixelX] = true;
                else
                    spritePixels[pixelX] = colorId;
            }
        }

        return false;
    }

    private byte GetTileColorAtAddress(int tileAddress, int row, int col)
    {
        int address = (tileAddress + (row * 4)) & 0x3FFF;
        byte bit0 = (byte)((_vram[address] >> (7 - col)) & 1);
        byte bit1 = (byte)((_vram[address + 1] >> (7 - col)) & 1);
        byte bit2 = (byte)((_vram[address + 2] >> (7 - col)) & 1);
        byte bit3 = (byte)((_vram[address + 3] >> (7 - col)) & 1);
        return (byte)(bit0 | (bit1 << 1) | (bit2 << 2) | (bit3 << 3));
    }

    private int GetSpriteHeight(bool doubleSpriteSize, bool doubleSpriteHeight)
    {
        return (doubleSpriteSize, doubleSpriteHeight) switch
        {
            (true, true) => 32,
            (true, false) or (false, true) => 16,
            _ => 8
        };
    }

    private int GetSpriteWidth(bool doubleSpriteSize) => doubleSpriteSize ? 16 : 8;

    private ushort GetNameTableBaseAddress()
    {
        return _mode == SmsGgVdpMode.Mode4_224
            ? (ushort)((_baseNameTableAddress & 0xF000) | 0x0700)
            : (ushort)(_baseNameTableAddress & 0xF800);
    }

    private BgTileData ReadNameTableWord(int row, int col)
    {
        ushort nameAddress = (ushort)((GetNameTableBaseAddress() + (row << 6) + (col << 1)) & _nameTableAddressMask);
        byte low = _vram[nameAddress & 0x3FFF];
        byte high = _vram[(nameAddress + 1) & 0x3FFF];
        return new BgTileData(
            Priority: (high & 0x10) != 0,
            Palette1: (high & 0x08) != 0,
            VerticalFlip: (high & 0x04) != 0,
            HorizontalFlip: (high & 0x02) != 0,
            TileIndex: (ushort)(low | ((high & 0x01) << 8)));
    }

    private uint BackdropColor() => GetPaletteColorWord((byte)(0x10 | _backdropColor));

    private void ProcessEvents(ushort activeScanlines, ushort scanlinesPerFrame)
    {
        ReadOnlySpan<ushort> eventDots = stackalloc ushort[] { 297, 307, 308, 309, 325, 326, ushort.MaxValue };

        while (_dot >= eventDots[_eventIndex])
        {
            switch (_eventIndex)
            {
                case 0:
                    PerLineSpriteProcessing(activeScanlines, scanlinesPerFrame);
                    LatchSpriteRegisters();
                    break;
                case 1:
                {
                    ushort nextLine = _scanline == scanlinesPerFrame - 1 ? (ushort)0 : (ushort)(_scanline + 1);
                    if (nextLine < activeScanlines)
                    {
                        if (_displayEnabled)
                            RenderScanline(nextLine);
                        else
                            ClearScanline(nextLine);
                    }
                    break;
                }
                case 2:
                    _spriteOverflow |= _lineSpriteOverflow;
                    break;
                case 3:
                    if (_scanline == activeScanlines)
                        _frameInterruptFlag = true;
                    break;
                case 4:
                    if (_scanline == activeScanlines)
                    {
                        _frameInterruptPending = true;
                        _frameInterruptFlag = true;
                    }
                    break;
                case 5:
                    DecrementLineCounter();
                    break;
                default:
                    return;
            }

            _eventIndex++;
        }
    }

    private void PerLineSpriteProcessing(ushort activeScanlines, ushort scanlinesPerFrame)
    {
        Array.Clear(_spritePixels);
        Array.Clear(_spriteCollisions);
        _lineSpriteOverflow = false;

        ushort spriteLine;
        if (_scanline == scanlinesPerFrame - 1)
            spriteLine = 255;
        else if (_scanline < activeScanlines - 1)
            spriteLine = _scanline;
        else
            return;

        _lineSpriteOverflow = RenderSpritesForScanline(
            spriteLine,
            _spritePixels,
            _spriteCollisions,
            _latchedBaseSpriteTableAddress,
            _latchedBaseSpritePatternAddress,
            _latchedDoubleSpriteHeight,
            _latchedDoubleSpriteSize,
            _latchedShiftSpritesLeft);
    }

    private void LatchSpriteRegisters()
    {
        _latchedBaseSpriteTableAddress = _baseSpriteTableAddress;
        _latchedBaseSpritePatternAddress = _baseSpritePatternAddress;
        _latchedDoubleSpriteHeight = _doubleSpriteHeight;
        _latchedDoubleSpriteSize = _doubleSpriteSize;
        _latchedShiftSpritesLeft = _shiftSpritesLeft;
    }

    private void RenderScanline(ushort sourceScanline)
    {
        int targetY = sourceScanline;
        uint backdropColor = BackdropColor();
        ClearVisibleScanline(targetY, backdropColor, ScreenWidth);

        int coarseXScroll;
        int fineXScroll;
        if (sourceScanline < 16 && _horizontalScrollLock)
        {
            coarseXScroll = 0;
            fineXScroll = 0;
        }
        else
        {
            coarseXScroll = (_xScroll >> 3) & 0x1F;
            fineXScroll = _xScroll & 0x07;
        }

        for (int dot = 0; dot < fineXScroll; dot++)
            SetPixel(dot, targetY, backdropColor);

        int nameTableRows = _mode == SmsGgVdpMode.Mode4_224 ? 32 : 28;
        for (int column = 0; column < 32; column++)
        {
            int coarseYScroll;
            int fineYScroll;
            if (column >= 24 && _verticalScrollLock)
            {
                coarseYScroll = 0;
                fineYScroll = 0;
            }
            else
            {
                coarseYScroll = _yScroll >> 3;
                fineYScroll = _yScroll & 0x07;
            }

            int nameTableRow = (((sourceScanline + fineYScroll) / 8) + coarseYScroll) % nameTableRows;
            int nameTableCol = (column + (32 - coarseXScroll)) % 32;
            BgTileData bgTileData = ReadNameTableWord(nameTableRow, nameTableCol);
            int bgTileRow = bgTileData.VerticalFlip
                ? 7 - ((sourceScanline + fineYScroll) % 8)
                : (sourceScanline + fineYScroll) % 8;
            byte bgBaseCramAddress = bgTileData.Palette1 ? (byte)0x10 : (byte)0x00;

            for (int bgTileCol = 0; bgTileCol < 8; bgTileCol++)
            {
                int dot = (8 * column) + fineXScroll + bgTileCol;
                if (dot >= ScreenWidth)
                    break;

                if (_hideLeftColumn && dot < 8)
                {
                    SetPixel(dot, targetY, backdropColor);
                    continue;
                }

                byte bgColorId = GetTileColor(
                    bgTileData.TileIndex,
                    bgTileRow,
                    bgTileData.HorizontalFlip ? bgTileCol : 7 - bgTileCol);
                byte spriteColorId = _spritePixels[dot];
                uint color = spriteColorId != 0 && (bgColorId == 0 || !bgTileData.Priority)
                    ? GetPaletteColorWord((byte)(0x10 | spriteColorId))
                    : GetPaletteColorWord((byte)(bgBaseCramAddress | bgColorId));
                SetPixel(dot, targetY, color);
            }
        }
    }

    private void ClearScanline(ushort sourceScanline)
    {
        int targetY = sourceScanline;
        ClearVisibleScanline(targetY, BackdropColor(), ScreenWidth);
    }

    private void ClearVisibleScanline(int targetY, uint color, int width)
    {
        for (int x = 0; x < width; x++)
            SetPixel(x, targetY, color);
    }

    private uint GetPaletteColorWord(byte address)
    {
        int logicalAddress = address & 0x1F;
        if (_version.IsMasterSystem())
        {
            byte value = _cram[logicalAddress];
            uint r = (uint)((value & 0x03) * 85);
            uint g = (uint)(((value >> 2) & 0x03) * 85);
            uint b = (uint)(((value >> 4) & 0x03) * 85);
            return 0xFF000000u | (r << 16) | (g << 8) | b;
        }

        int cramAddress = logicalAddress * 2;
        ushort value16 = (ushort)(_cram[cramAddress] | (_cram[cramAddress + 1] << 8));
        uint r12 = (uint)(value16 & 0x0F) * 17;
        uint g12 = (uint)((value16 >> 4) & 0x0F) * 17;
        uint b12 = (uint)((value16 >> 8) & 0x0F) * 17;
        return 0xFF000000u | (r12 << 16) | (g12 << 8) | b12;
    }

    private void SetPixel(int x, int y, uint color)
    {
        if ((uint)x >= ScreenWidth || (uint)y >= ScreenHeight)
            return;

        int offset = (y * ScreenWidth + x) * 4;
        _frameBuffer[offset] = (byte)(color & 0xFF);
        _frameBuffer[offset + 1] = (byte)((color >> 8) & 0xFF);
        _frameBuffer[offset + 2] = (byte)((color >> 16) & 0xFF);
        _frameBuffer[offset + 3] = 0xFF;
    }

    private void ClearFrameBuffer()
    {
        Array.Clear(_frameBuffer);
    }

    private static SmsGgViewportSize DefaultViewport(SmsGgVdpVersion version, bool ggUseSmsResolution, SmsGgVdpMode mode = SmsGgVdpMode.Mode4)
    {
        SmsGgViewportSize viewport = version switch
        {
            SmsGgVdpVersion.PalMasterSystem1 or SmsGgVdpVersion.PalMasterSystem2 => SmsGgViewportSize.PalSms,
            SmsGgVdpVersion.GameGear => ggUseSmsResolution ? SmsGgViewportSize.GameGearExpanded : SmsGgViewportSize.GameGear,
            _ => SmsGgViewportSize.NtscSms
        };

        if (mode == SmsGgVdpMode.Mode4_224)
        {
            if (version == SmsGgVdpVersion.GameGear && !ggUseSmsResolution)
                return viewport with { Top = (ushort)(viewport.Top + 16) };

            return viewport with
            {
                TopBorderHeight = (ushort)Math.Max(0, viewport.TopBorderHeight - 16),
                BottomBorderHeight = (ushort)Math.Max(0, viewport.BottomBorderHeight - 16)
            };
        }

        return viewport;
    }

    private static int ParseTraceInt(string name, int fallback)
    {
        string? raw = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(raw))
            return fallback;

        return int.TryParse(raw.Trim(), out int value) ? value : fallback;
    }

    private enum SmsGgVdpMode
    {
        Mode4 = 0,
        Mode4_224 = 1
    }

    private readonly record struct BgTileData(
        bool Priority,
        bool Palette1,
        bool VerticalFlip,
        bool HorizontalFlip,
        ushort TileIndex);
}
