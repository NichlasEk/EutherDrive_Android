using System;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Threading;

namespace KSNES.PictureProcessing;

public class PPU : IPPU
{
    private static readonly bool PerfStatsEnabled =
        string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_SNES_PERF"), "1", StringComparison.Ordinal);
    public const int MaxFrameWidth = 512;
    public const int MaxFrameHeight = 240;

    private static readonly bool TracePpu =
        string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_SNES_PPU"), "1", StringComparison.Ordinal);
    private static readonly int TracePpuLimit = GetTracePpuLimit();
    private static readonly bool DebugDisableBg1 =
        string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_SNES_DISABLE_BG1"), "1", StringComparison.Ordinal);
    private static readonly bool DebugDisableBg2 =
        string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_SNES_DISABLE_BG2"), "1", StringComparison.Ordinal);
    private static readonly bool DebugDisableBg3 =
        string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_SNES_DISABLE_BG3"), "1", StringComparison.Ordinal);
    private static readonly bool DebugDisableBg4 =
        string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_SNES_DISABLE_BG4"), "1", StringComparison.Ordinal);
    private static readonly bool DebugDisableObj =
        string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_SNES_DISABLE_OBJ"), "1", StringComparison.Ordinal);
    private static int _tracePpuCount;
    [NonSerialized]
    private ISNESSystem? _snes;
    private ushort[] _vram = [];
    private ushort[] _cgram = [];
    private ushort[] _cgramFrame = [];
    private ushort[] _oam = [];
    private ushort[] _highOam = [];
    private byte[] _spriteLineBuffer = [];
    private byte[] _spritePrioBuffer = [];
    private int[] _mode7Xcoords = [];
    private int[] _mode7Ycoords = [];
    private int[] _pixelOutput = [];
    [NonSerialized]
    private bool _frameTrueHiResOutput;

    [JsonIgnore]
    private readonly int[] _layersPerMode = [
        4, 0, 1, 4, 0, 1, 4, 2, 3, 4, 2, 3,
        4, 0, 1, 4, 0, 1, 4, 2, 4, 2, 5, 5,
        4, 0, 4, 1, 4, 0, 4, 1, 5, 5, 5, 5,
        4, 0, 4, 1, 4, 0, 4, 1, 5, 5, 5, 5,
        4, 0, 4, 1, 4, 0, 4, 1, 5, 5, 5, 5,
        4, 0, 4, 1, 4, 0, 4, 1, 5, 5, 5, 5,
        4, 0, 4, 4, 0, 4, 5, 5, 5, 5, 5, 5,
        4, 4, 4, 0, 4, 5, 5, 5, 5, 5, 5, 5,
        2, 4, 0, 1, 4, 0, 1, 4, 2, 4, 5, 5,
        4, 4, 1, 4, 0, 4, 1, 5, 5, 5, 5, 5
    ];

    [JsonIgnore]
    private readonly int[] _prioPerMode = [
        3, 1, 1, 2, 0, 0, 1, 1, 1, 0, 0, 0,
        3, 1, 1, 2, 0, 0, 1, 1, 0, 0, 5, 5,
        3, 1, 2, 1, 1, 0, 0, 0, 5, 5, 5, 5,
        3, 1, 2, 1, 1, 0, 0, 0, 5, 5, 5, 5,
        3, 1, 2, 1, 1, 0, 0, 0, 5, 5, 5, 5,
        3, 1, 2, 1, 1, 0, 0, 0, 5, 5, 5, 5,
        3, 1, 2, 1, 0, 0, 5, 5, 5, 5, 5, 5,
        3, 2, 1, 0, 0, 5, 5, 5, 5, 5, 5, 5,
        1, 3, 1, 1, 2, 0, 0, 1, 0, 0, 5, 5,
        3, 2, 1, 1, 0, 0, 0, 5, 5, 5, 5, 5
    ];

    [JsonIgnore]
    private readonly int[] _bitPerMode = [
        2, 2, 2, 2,
        4, 4, 2, 5,
        4, 4, 5, 5,
        8, 4, 5, 5,
        8, 2, 5, 5,
        4, 2, 5, 5,
        4, 5, 5, 5,
        8, 5, 5, 5,
        4, 4, 2, 5,
        8, 7, 5, 5
    ];

    [JsonIgnore]
    private readonly int[] _layercountPerMode = [12, 10, 8, 8, 8, 8, 6, 5, 10, 7];

    [JsonIgnore]
    private readonly double[] _brightnessMults = [0.1, 0.5, 1.1, 1.6, 2.2, 2.7, 3.3, 3.8, 4.4, 4.9, 5.5, 6, 6.6, 7.1, 7.6, 8.2];

    [JsonIgnore]
    private static readonly int[] BrightnessArgbTable = BuildBrightnessArgbTable();

    [JsonIgnore]
    private readonly int[] _spriteTileOffsets = [ 
        0, 1, 2, 3, 4, 5, 6, 7,
        16, 17, 18, 19, 20, 21, 22, 23,
        32, 33, 34, 35, 36, 37, 38, 39,
        48, 49, 50, 51, 52, 53, 54, 55,
        64, 65, 66, 67, 68, 69, 70, 71,
        80, 81, 82, 83, 84, 85, 86, 87,
        96, 97, 98, 99, 100, 101, 102, 103,
        112, 113, 114, 115, 116, 117, 118, 119
    ];

    [JsonIgnore]
    private readonly int[] _spriteWidths = [
        1, 1, 1, 2, 2, 4, 2, 2,
        2, 4, 8, 4, 8, 8, 4, 4
    ];

    [JsonIgnore]
    private readonly int[] _spriteHeights = [
        1, 1, 1, 2, 2, 4, 4, 4,
        2, 4, 8, 4, 8, 8, 8, 4
    ];

    private int _cgramAdr;
    private bool _cgramSecond;
    private int _cgramBuffer;

    private int _vramInc;
    private int _vramRemap;
    private bool _vramIncOnHigh;
    private int _vramAdr;
    private int _vramReadBuffer;
    private bool[] _tilemapWider = [];
    private bool[] _tilemapHigher = [];
    private int[] _tilemapAdr = [];
    private int[] _tileAdr = [];

    private int[] _bgHoff = [];
    private int[] _bgVoff = [];

    // Keep the original serialized field layout intact for savestate compatibility.
    // `_offPrev1` now serves as the BG scroll write buffer; `_offPrev2` is retained
    // so old savestates keep matching the binary field order.
    private int _offPrev1;
    private int _offPrev2;
    private int _mode;
    private bool _layer3Prio;
    private bool[] _bigTiles = [];
    private bool[] _mosaicEnabled = [];
    private int _mosaicSize;
    private int _mosaicStartLine;
    private bool[] _mainScreenEnabled = [];
    private bool[] _subScreenEnabled = [];
    private byte _tmRaw;
    private byte _tsRaw;
    private bool _forcedBlank;
    private int _brightness;

    private int _oamAdr;
    private int _oamRegAdr;
    private bool _oamInHigh;
    private bool _oamRegInHigh;
    private bool _objPriority;
    private bool _oamSecond;
    private int _oamBuffer;


    private int _sprAdr1;
    private int _sprAdr2;
    private int _objSize;

    private bool _rangeOver;
    private bool _timeOver;

    private bool _mode7ExBg;
    private bool _pseudoHires;
    private bool _overscan;
    private bool _objInterlace;
    private bool _interlace;

    public bool FrameOverscan { get; private set; }
    public bool ForcedBlank => _forcedBlank;
    public int Brightness => _brightness;
    public int Mode => _mode;
    public bool OverscanEnabled => _overscan;
    public bool Mode7ExBg => _mode7ExBg;
    public bool PseudoHires => _pseudoHires;
    public bool Interlace => _interlace;
    public bool ObjInterlace => _objInterlace;
    public int PresentWidth => _frameTrueHiResOutput ? 512 : 256;
    public int PresentHeight => FrameOverscan ? 240 : 224;
    public byte MainScreenMask => _tmRaw;
    public byte SubScreenMask => _tsRaw;
    private bool _evenFrame;

    public int LatchedHpos { get; set; }
    public int LatchedVpos { get; set; }
    private bool _latchHsecond;
    private bool _latchVsecond;
    public bool CountersLatched { get; set; }

    private int _mode7Hoff;
    private int _mode7Voff;
    private int _mode7A;
    private int _mode7B;
    private int _mode7C;
    private int _mode7D;
    private int _mode7X;
    private int _mode7Y;
    private int _mode7Prev;
    private int _multResult;

    private bool _mode7LargeField;
    private bool _mode7Char0fill;
    private bool _mode7FlipX;
    private bool _mode7FlipY;

    private bool[] _window1Inversed = [];
    private bool[] _window1Enabled = [];
    private bool[] _window2Inversed = [];
    private bool[] _window2Enabled = [];
    private int[] _windowMaskLogic = [];
    private int _window1Left;
    private int _window1Right;
    private int _window2Left;
    private int _window2Right;
    private bool[] _mainScreenWindow = [];
    private bool[] _subScreenWindow = [];

    private int _colorClip;
    private int _preventMath;
    private bool _addSub;
    private bool _directColor;

    private bool _subtractColors;
    private bool _halfColors;
    private bool[] _mathEnabled = [];
    private int _fixedColorB;
    private int _fixedColorG;
    private int _fixedColorR;

    private int[] _tilemapBuffer = [];
    private int[] _tileBufferP1 = [];
    private int[] _tileBufferP2 = [];
    private int[] _tileBufferP3 = [];
    private int[] _tileBufferP4 = [];
    private int[] _lastTileFetchedX = [];
    private int[] _lastTileFetchedY = [];
    private int[] _optHorBuffer = [];
    private int[] _optVerBuffer = [];
    private int[] _lastOrigTileX = [];
    [NonSerialized]
    private byte[] _windowStateCache = [];
    [NonSerialized]
    private byte[] _mainScreenVisibleCache = [];
    [NonSerialized]
    private byte[] _subScreenVisibleCache = [];
    [NonSerialized]
    private byte[] _clipToBlackCache = [];
    [NonSerialized]
    private byte[] _mathPreventCache = [];
    [NonSerialized]
    private bool _lineAnyColorMathEnabled;
    [NonSerialized]
    private int _lineModeIndex;
    [NonSerialized]
    private int _lineLayerCount;
    [NonSerialized]
    private bool _lineCachesDirty = true;
    [NonSerialized]
    private ushort[] _tilePixelBuffer = [];
    [NonSerialized]
    private byte[] _tilePriorityBuffer = [];
    [NonSerialized]
    private bool _runtimeBuffersReady;
    [NonSerialized]
    private int _spriteTouchedStart = 256;
    [NonSerialized]
    private int _spriteTouchedEnd = -1;
    [NonSerialized]
    private int[] _spriteXCache = [];
    [NonSerialized]
    private byte[] _spriteYCache = [];
    [NonSerialized]
    private byte[] _spriteTileCache = [];
    [NonSerialized]
    private byte[] _spriteAttrCache = [];
    [NonSerialized]
    private byte[] _spriteWidthCache = [];
    [NonSerialized]
    private byte[] _spriteHeightCache = [];
    [NonSerialized]
    private bool _spriteMetaDirty = true;
    [NonSerialized]
    internal ulong PerfRenderedLines;
    [NonSerialized]
    internal ulong PerfMode7Lines;
    [NonSerialized]
    internal ulong PerfHiResLines;
    [NonSerialized]
    internal ulong PerfTrueHiResLines;
    [NonSerialized]
    internal ulong PerfOutputPixels;

    private static int[] BuildBrightnessArgbTable()
    {
        var table = new int[16 * 0x8000];
        for (int brightness = 0; brightness < 16; brightness++)
        {
            int rowBase = brightness << 15;
            for (int color = 0; color < 0x8000; color++)
            {
                int r = color & 0x1f;
                int g = (color >> 5) & 0x1f;
                int b = (color >> 10) & 0x1f;
                int rr = (int)Math.Round((double)(brightness * r * 255) / (15 * 31), MidpointRounding.AwayFromZero);
                int gg = (int)Math.Round((double)(brightness * g * 255) / (15 * 31), MidpointRounding.AwayFromZero);
                int bb = (int)Math.Round((double)(brightness * b * 255) / (15 * 31), MidpointRounding.AwayFromZero);
                table[rowBase + color] = unchecked((int)0xFF000000) | bb | (gg << 8) | (rr << 16);
            }
        }

        return table;
    }

    private int GetCurrentOamAddress()
    {
        return _oamAdr | (_oamInHigh ? 0x100 : 0);
    }

    private void WriteBgHScroll(int layer, int value)
    {
        int current = _bgHoff[layer];
        _bgHoff[layer] = (value << 8) | (_offPrev1 & ~0x07) | ((current >> 8) & 0x07);
        _offPrev1 = value;
    }

    private void WriteBgVScroll(int layer, int value)
    {
        _bgVoff[layer] = (value << 8) | _offPrev1;
        _offPrev1 = value;
    }

    private void MarkLineCachesDirty()
    {
        _lineCachesDirty = true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private byte GetBgModeRaw()
    {
        int value = _mode & 0x7;
        if (_layer3Prio)
            value |= 0x08;
        if (_bigTiles[0])
            value |= 0x10;
        if (_bigTiles[1])
            value |= 0x20;
        if (_bigTiles[2])
            value |= 0x40;
        if (_bigTiles[3])
            value |= 0x80;
        return (byte)value;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private byte GetWindowSelectRaw(int baseLayer)
    {
        int value = 0;
        if (_window1Inversed[baseLayer])
            value |= 0x01;
        if (_window1Enabled[baseLayer])
            value |= 0x02;
        if (_window2Inversed[baseLayer])
            value |= 0x04;
        if (_window2Enabled[baseLayer])
            value |= 0x08;
        if (_window1Inversed[baseLayer + 1])
            value |= 0x10;
        if (_window1Enabled[baseLayer + 1])
            value |= 0x20;
        if (_window2Inversed[baseLayer + 1])
            value |= 0x40;
        if (_window2Enabled[baseLayer + 1])
            value |= 0x80;
        return (byte)value;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private byte GetWindowMaskLogicRaw(int baseLayer)
    {
        return (byte)(
            _windowMaskLogic[baseLayer]
            | (_windowMaskLogic[baseLayer + 1] << 2)
            | (_windowMaskLogic[baseLayer + 2] << 4)
            | (_windowMaskLogic[baseLayer + 3] << 6));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private byte GetScreenWindowRaw(bool[] windowMask)
    {
        int value = 0;
        if (windowMask[0])
            value |= 0x01;
        if (windowMask[1])
            value |= 0x02;
        if (windowMask[2])
            value |= 0x04;
        if (windowMask[3])
            value |= 0x08;
        if (windowMask[4])
            value |= 0x10;
        return (byte)value;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private byte GetColorWindowRaw()
    {
        int value = (_colorClip << 6) | (_preventMath << 4);
        if (_addSub)
            value |= 0x02;
        if (_directColor)
            value |= 0x01;
        return (byte)value;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private byte GetColorMathRaw()
    {
        int value = 0;
        if (_subtractColors)
            value |= 0x80;
        if (_halfColors)
            value |= 0x40;
        if (_mathEnabled[0])
            value |= 0x01;
        if (_mathEnabled[1])
            value |= 0x02;
        if (_mathEnabled[2])
            value |= 0x04;
        if (_mathEnabled[3])
            value |= 0x08;
        if (_mathEnabled[4])
            value |= 0x10;
        if (_mathEnabled[5])
            value |= 0x20;
        return (byte)value;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private byte GetSetIniRaw()
    {
        int value = 0;
        if (_mode7ExBg)
            value |= 0x40;
        if (_pseudoHires)
            value |= 0x08;
        if (_overscan)
            value |= 0x04;
        if (_objInterlace)
            value |= 0x02;
        if (_interlace)
            value |= 0x01;
        return (byte)value;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void FillCacheRange(byte[] cache, int start, byte value)
    {
        Array.Fill(cache, value, start, 256);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool AnyColorMathEnabled()
    {
        return _mathEnabled[0]
            || _mathEnabled[1]
            || _mathEnabled[2]
            || _mathEnabled[3]
            || _mathEnabled[4]
            || _mathEnabled[5];
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool HasActiveMainWindow(int layer)
    {
        return _mainScreenWindow[layer] && (_window1Enabled[layer] || _window2Enabled[layer]);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void ClearPreviousSpriteRange()
    {
        if (_spriteTouchedEnd < _spriteTouchedStart)
            return;

        int count = _spriteTouchedEnd - _spriteTouchedStart + 1;
        Array.Clear(_spriteLineBuffer, _spriteTouchedStart, count);
        Array.Clear(_spritePrioBuffer, _spriteTouchedStart, count);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void MarkSpriteMetaDirty()
    {
        _spriteMetaDirty = true;
    }

    private void EnsureSpriteMetaCache()
    {
        if (!_spriteMetaDirty)
            return;

        if (_spriteXCache.Length != 128)
            _spriteXCache = new int[128];
        if (_spriteYCache.Length != 128)
            _spriteYCache = new byte[128];
        if (_spriteTileCache.Length != 128)
            _spriteTileCache = new byte[128];
        if (_spriteAttrCache.Length != 128)
            _spriteAttrCache = new byte[128];
        if (_spriteWidthCache.Length != 128)
            _spriteWidthCache = new byte[128];
        if (_spriteHeightCache.Length != 128)
            _spriteHeightCache = new byte[128];

        int interlaceMul = _objInterlace ? 4 : 8;
        for (int spriteIndex = 0; spriteIndex < 128; spriteIndex++)
        {
            int index = spriteIndex << 1;
            int x = _oam[index] & 0xff;
            int highBits = (_highOam[index >> 4] >> (index & 0xf)) & 0x3;
            x |= (highBits & 0x1) << 8;
            if (x > 255)
                x = -(512 - x);

            bool big = (highBits & 0x2) != 0;
            int spriteSizeIndex = _objSize + (big ? 8 : 0);
            _spriteXCache[spriteIndex] = x;
            _spriteYCache[spriteIndex] = (byte)(_oam[index] >> 8);
            _spriteTileCache[spriteIndex] = (byte)(_oam[index + 1] & 0xff);
            _spriteAttrCache[spriteIndex] = (byte)(_oam[index + 1] >> 8);
            _spriteWidthCache[spriteIndex] = (byte)_spriteWidths[spriteSizeIndex];
            _spriteHeightCache[spriteIndex] = (byte)(_spriteHeights[spriteSizeIndex] * interlaceMul);
        }

        _spriteMetaDirty = false;
    }

    private void SetCurrentOamAddress(int address)
    {
        address &= 0x1ff;
        _oamAdr = address & 0xff;
        _oamInHigh = (address & 0x100) != 0;
    }

    private bool InActiveDisplay()
    {
        if (_snes is null)
        {
            return false;
        }

        int visibleLines = _overscan ? 240 : 225;
        return _snes.YPos < visibleLines;
    }

    public void Reset()
    {
        _vram = new ushort[0x8000];
        _cgram = new ushort[0x100];
        _cgramFrame = new ushort[0x100];
        _oam = new ushort[0x100];
        _highOam = new ushort[0x10];
        EnsureRuntimeBuffers();
        _cgramAdr= 0;
        _cgramSecond = false;
        _cgramBuffer = 0;
        _vramInc = 0;
        _vramRemap = 0;
        _vramIncOnHigh = false;
        _vramAdr = 0;
        _vramReadBuffer = 0;
        _tilemapWider = new bool[4];
        _tilemapHigher = new bool[4];
        _tilemapAdr = new int[4];
        _tileAdr = new int[4];
        _bgHoff = new int[5];
        _bgVoff = new int[5];
        _offPrev1 = 0;
        _offPrev2 = 0;
        _mode = 0;
        _layer3Prio = false;
        _bigTiles = new bool[4];
        _mosaicEnabled = new bool[5];
        _mosaicSize = 1;
        _mosaicStartLine = 1;
        _mainScreenEnabled = new bool[5];
        _subScreenEnabled = new bool[5];
        _forcedBlank = true;
        _brightness = 0;
        _oamAdr = 0;
        _oamRegAdr = 0;
        _oamInHigh = false;
        _oamRegInHigh = false;
        _objPriority = false;
        _oamSecond = false;
        _oamBuffer = 0;
        _sprAdr1 = 0;
        _sprAdr2 = 0;
        _objSize = 0;
        _rangeOver = false;
        _timeOver = false;
        _mode7ExBg = false;
        _pseudoHires = false;
        _overscan = false;
        _objInterlace = false;
        _interlace = false;
        FrameOverscan = false;
        _evenFrame = false;
        LatchedHpos = 0;
        LatchedVpos = 0;
        _latchHsecond = false;
        _latchVsecond = false;
        CountersLatched = false;
        _mode7Hoff = 0;
        _mode7Voff = 0;
        _mode7A = 0;
        _mode7B = 0;
        _mode7C = 0;
        _mode7D = 0;
        _mode7X = 0;
        _mode7Y = 0;
        _mode7Prev = 0;
        _multResult = 0;
        _mode7LargeField = false;
        _mode7Char0fill = false;
        _mode7FlipX = false;
        _mode7FlipY = false;
        _window1Inversed = new bool[6];
        _window1Enabled = new bool[6];
        _window2Inversed = new bool[6];
        _window2Enabled = new bool[6];
        _windowMaskLogic = new int[6];
        _window1Left = 0;
        _window1Right = 0;
        _window2Left = 0;
        _window2Right = 0;
        _mainScreenWindow = new bool[5];
        _subScreenWindow = new bool[5];
        _colorClip = 0;
        _preventMath = 0;
        _addSub = false;
        _directColor = false;
        _subtractColors = false;
        _halfColors = false;
        _mathEnabled = new bool[6];
        _fixedColorB = 0;
        _fixedColorG = 0;
        _fixedColorR = 0;
        _frameTrueHiResOutput = false;
        _tilemapBuffer = new int[4];
        _tileBufferP1 = new int[4];
        _tileBufferP2 = new int[4];
        _tileBufferP3 = new int[4];
        _tileBufferP4 = new int[4];
        _optHorBuffer = new int[2];
        _optVerBuffer = new int[2];
        _lastTileFetchedX = new int[4];
        _lastTileFetchedY = new int[4];
        _lastOrigTileX = new int[2];
        _lineCachesDirty = true;
        _spriteTouchedStart = 256;
        _spriteTouchedEnd = -1;
        _spriteMetaDirty = true;
        ResetLineCaches();
    }

    public void SetSystem(ISNESSystem snes)
    {
        _snes = snes;
        EnsureRuntimeBuffers();
    }

    public int Read(int adr)
    {
        switch (adr)
        {
            case 0x34:
                return _multResult & 0xff;
            case 0x35:
                return (_multResult & 0xff00) >> 8;
            case 0x36:
                return (_multResult & 0xff0000) >> 16;
            case 0x37:
                if (_snes!.PPULatch)
                {
                    LatchedHpos = _snes.XPos >> 2;
                    LatchedVpos = _snes.YPos;
                    CountersLatched = true;
                }
                return _snes.OpenBus;
            case 0x38:
                int val;
                int oamAddress = GetCurrentOamAddress();
                if (!_oamSecond)
                {
                    if (oamAddress >= 0x100)
                    {
                        int highAddress = (oamAddress << 1) & 0x1f;
                        val = _highOam[highAddress >> 1] & 0xff;
                    }
                    else
                    {
                        val = _oam[_oamAdr] & 0xff;
                    }
                    _oamSecond = true;
                }
                else
                {
                    if (oamAddress >= 0x100)
                    {
                        int highAddress = ((oamAddress << 1) | 1) & 0x1f;
                        val = _highOam[highAddress >> 1] >> 8;
                    }
                    else
                    {
                        val = _oam[_oamAdr] >> 8;
                    }
                    SetCurrentOamAddress(oamAddress + 1);
                    _oamSecond = false;
                }
                return val;
            case 0x39:
                int val2 = _vramReadBuffer;
                _vramReadBuffer = _vram[GetVramRemap()];
                if (!_vramIncOnHigh)
                {
                    _vramAdr += _vramInc;
                    _vramAdr &= 0xffff;
                }
                return val2 & 0xff;
            case 0x3a:
                int val3 = _vramReadBuffer;
                _vramReadBuffer = _vram[GetVramRemap()];
                if (_vramIncOnHigh)
                {
                    _vramAdr += _vramInc;
                    _vramAdr &= 0xffff;
                }
                return (val3 & 0xff00) >> 8;
            case 0x3b:
                int val4;
                if (!_cgramSecond)
                {
                    val4 = _cgram[_cgramAdr] & 0xff;
                    _cgramSecond = true;
                }
                else
                {
                    val4 = _cgram[_cgramAdr++] >> 8;
                    _cgramAdr &= 0xff;
                    _cgramSecond = false;
                }
                return val4;
            case 0x3c:
                int val5;
                if (!_latchHsecond)
                {
                    val5 = LatchedHpos & 0xff;
                    _latchHsecond = true;
                }
                else
                {
                    val5 = (LatchedHpos & 0xff00) >> 8;
                    _latchHsecond = false;
                }
                return val5;
            case 0x3d:
                int val6;
                if (!_latchVsecond)
                {
                    val6 = LatchedVpos & 0xff;
                    _latchVsecond = true;
                }
                else
                {
                    val6 = (LatchedVpos & 0xff00) >> 8;
                    _latchVsecond = false;
                }
                return val6;
            case 0x3e:
                int val7 = _timeOver ? 0x80 : 0;
                val7 |= _rangeOver ? 0x40 : 0;
                return val7 | 0x1;
            case 0x3f:
                int val8 = _evenFrame ? 0x80 : 0;
                val8 |= CountersLatched ? 0x40 : 0;
                if (_snes!.IsPal)
                    val8 |= 0x10;
                if (_snes!.PPULatch)
                {
                    CountersLatched = false;
                }
                _latchHsecond = false;
                _latchVsecond = false;
                return val8 | 0x2;
        }
        return _snes!.OpenBus;
    }

    public void Write(int adr, int value, bool dma = false)
    {
        switch (adr)
        {
            case 0x00:
                _forcedBlank = (value & 0x80) > 0;
                _brightness = value & 0xf;
                TracePpuWrite($"[PPU] INIDISP=0x{value:X2} forcedBlank={_forcedBlank} bright={_brightness}");
                return;
            case 0x01:
                _sprAdr1 = (value & 0x7) << 13;
                _sprAdr2 = (value & 0x18) << 9;
                _objSize = (value & 0xe0) >> 5;
                MarkSpriteMetaDirty();
                return;
            case 0x02:
                _oamRegAdr = value;
                if (!InActiveDisplay() || _forcedBlank)
                {
                    _oamAdr = _oamRegAdr;
                    _oamInHigh = _oamRegInHigh;
                }
                _oamSecond = false;
                return;
            case 0x03:
                _oamRegInHigh = (value & 0x1) > 0;
                _objPriority = (value & 0x80) > 0;
                if (!InActiveDisplay() || _forcedBlank)
                {
                    _oamAdr = _oamRegAdr;
                    _oamInHigh = _oamRegInHigh;
                }
                _oamSecond = false;
                return;
            case 0x04:
                int oamAddress = GetCurrentOamAddress();
                if (!_oamSecond)
                {
                    if (oamAddress >= 0x100)
                    {
                        int highAddress = (oamAddress << 1) & 0x1f;
                        int address = highAddress >> 1;
                        _highOam[address] = (ushort) ((_highOam[address] & 0xff00) | value);
                    }
                    else
                    {
                        _oamBuffer = (_oamBuffer & 0xff00) | value;
                    }
                    _oamSecond = true;
                }
                else
                {
                    if (oamAddress >= 0x100)
                    {
                        int highAddress = ((oamAddress << 1) | 1) & 0x1f;
                        int address = highAddress >> 1;
                        _highOam[address] = (ushort) ((_highOam[address] & 0xff) | (value << 8));
                    }
                    else
                    {
                        _oamBuffer = (_oamBuffer & 0xff) | (value << 8);
                        _oam[_oamAdr] = (ushort) _oamBuffer;
                    }
                    SetCurrentOamAddress(oamAddress + 1);
                    _oamSecond = false;
                }
                MarkSpriteMetaDirty();
                return;
            case 0x05:
                if (GetBgModeRaw() == (byte)value)
                    return;
                _mode = value & 0x7;
                _layer3Prio = (value & 0x08) > 0;
                _bigTiles[0] = (value & 0x10) > 0;
                _bigTiles[1] = (value & 0x20) > 0;
                _bigTiles[2] = (value & 0x40) > 0;
                _bigTiles[3] = (value & 0x80) > 0;
                MarkLineCachesDirty();
                TracePpuWrite($"[PPU] BGMODE=0x{value:X2} mode={_mode} l3prio={_layer3Prio}");
                return;
            case 0x06:
                _mosaicEnabled[0] = (value & 0x1) > 0;
                _mosaicEnabled[1] = (value & 0x2) > 0;
                _mosaicEnabled[2] = (value & 0x4) > 0;
                _mosaicEnabled[3] = (value & 0x8) > 0;
                _mosaicSize = ((value & 0xf0) >> 4) + 1;
                _mosaicStartLine = Math.Max(0, _snes!.YPos - 1);
                return;
            case 0x07:
            case 0x08:
            case 0x09:
            case 0x0a:
                _tilemapWider[adr - 7] = (value & 0x1) > 0;
                _tilemapHigher[adr - 7] = (value & 0x2) > 0;
                _tilemapAdr[adr - 7] = (value & 0xfc) << 8;
                TracePpuWrite($"[PPU] BG{adr - 6}SC=0x{value:X2} map=0x{_tilemapAdr[adr - 7]:X4} wide={_tilemapWider[adr - 7]} high={_tilemapHigher[adr - 7]}");
                return;
            case 0x0b:
                // BGnNBA encodes tile data base in 0x1000-byte steps; _vram is word-addressed.
                _tileAdr[0] = (value & 0xf) << 12;
                _tileAdr[1] = (value & 0xf0) << 8;
                TracePpuWrite($"[PPU] BG12NBA=0x{value:X2} bg1Tile=0x{_tileAdr[0]:X4} bg2Tile=0x{_tileAdr[1]:X4}");
                return;
            case 0x0c:
                _tileAdr[2] = (value & 0xf) << 12;
                _tileAdr[3] = (value & 0xf0) << 8;
                TracePpuWrite($"[PPU] BG34NBA=0x{value:X2} bg3Tile=0x{_tileAdr[2]:X4} bg4Tile=0x{_tileAdr[3]:X4}");
                return;
            case 0x0d:
                _mode7Hoff = Get13Signed((value << 8) | _mode7Prev);
                _mode7Prev = value;
                WriteBgHScroll((adr - 0xd) >> 1, value);
                return;
            case 0x0f:
            case 0x11:
            case 0x13:
                WriteBgHScroll((adr - 0xd) >> 1, value);
                return;
            case 0x0e:
                _mode7Voff = Get13Signed((value << 8) | _mode7Prev);
                _mode7Prev = value;
                WriteBgVScroll((adr - 0xe) >> 1, value);
                return;
            case 0x10:
            case 0x12:
            case 0x14:
                WriteBgVScroll((adr - 0xe) >> 1, value);
                return;
            case 0x15:
                var incVal = value & 0x3;
                if (incVal == 0)
                {
                    _vramInc = 1;
                }
                else if (incVal == 1)
                {
                    _vramInc = 32;
                }
                else
                {
                    _vramInc = 128;
                }
                _vramRemap = (value & 0x0c) >> 2;
                _vramIncOnHigh = (value & 0x80) > 0;
                TracePpuWrite($"[PPU] VMAIN=0x{value:X2} inc={_vramInc} remap={_vramRemap} incOnHigh={_vramIncOnHigh}");
                return;
            case 0x16:
                _vramAdr = (_vramAdr & 0xff00) | value;
                _vramReadBuffer = _vram[GetVramRemap()];
                TracePpuWrite($"[PPU] VMADDL=0x{value:X2} vramAdr=0x{_vramAdr:X4} remapAdr=0x{GetVramRemap():X4}");
                return;
            case 0x17:
                _vramAdr = (_vramAdr & 0xff) | (value << 8);
                _vramReadBuffer = _vram[GetVramRemap()];
                TracePpuWrite($"[PPU] VMADDH=0x{value:X2} vramAdr=0x{_vramAdr:X4} remapAdr=0x{GetVramRemap():X4}");
                return;
            case 0x18:
                int adr2 = GetVramRemap();
                if (_forcedBlank || GetCurrentVblank())
                {
                    _vram[adr2] = (ushort) ((_vram[adr2] & 0xff00) | value);
                    TracePpuWrite($"[PPU] VMDATAL adr=0x{adr2:X4} val=0x{value:X2} word=0x{_vram[adr2]:X4}");
                }
                else
                {
                    TracePpuWrite($"[PPU] VMDATAL-REJECT adr=0x{adr2:X4} val=0x{value:X2} forcedBlank={_forcedBlank} vblank={GetCurrentVblank()} hblank={GetCurrentHblank()} xy=({_snes?.XPos ?? -1},{_snes?.YPos ?? -1})");
                }
                if (!_vramIncOnHigh)
                {
                    _vramAdr += _vramInc;
                    _vramAdr &= 0xffff;
                }
                return;
            case 0x19:
                int adr3 = GetVramRemap();
                if (_forcedBlank || GetCurrentVblank())
                {
                    _vram[adr3] = (ushort) ((_vram[adr3] & 0xff) | (value << 8));
                    TracePpuWrite($"[PPU] VMDATAH adr=0x{adr3:X4} val=0x{value:X2} word=0x{_vram[adr3]:X4}");
                }
                else
                {
                    TracePpuWrite($"[PPU] VMDATAH-REJECT adr=0x{adr3:X4} val=0x{value:X2} forcedBlank={_forcedBlank} vblank={GetCurrentVblank()} hblank={GetCurrentHblank()} xy=({_snes?.XPos ?? -1},{_snes?.YPos ?? -1})");
                }
                if (_vramIncOnHigh)
                {
                    _vramAdr += _vramInc;
                    _vramAdr &= 0xffff;
                }
                return;
            case 0x1a:
                _mode7LargeField = (value & 0x80) > 0;
                _mode7Char0fill = (value & 0x40) > 0;
                _mode7FlipY = (value & 0x2) > 0;
                _mode7FlipX = (value & 0x1) > 0;
                return;
            case 0x1b:
                _mode7A = Get16Signed((value << 8) | _mode7Prev);
                _mode7Prev = value;
                _multResult = GetMultResult(_mode7A, _mode7B);
                return;
            case 0x1c:
                _mode7B = Get16Signed((value << 8) | _mode7Prev);
                _mode7Prev = value;
                _multResult = GetMultResult(_mode7A, _mode7B);
                return;
            case 0x1d:
                _mode7C = Get16Signed((value << 8) | _mode7Prev);
                _mode7Prev = value;
                return;
            case 0x1e:
                _mode7D = Get16Signed((value << 8) | _mode7Prev);
                _mode7Prev = value;
                return;
            case 0x1f:
                _mode7X = Get13Signed((value << 8) | _mode7Prev);
                _mode7Prev = value;
                return;
            case 0x20:
                _mode7Y = Get13Signed((value << 8) | _mode7Prev);
                _mode7Prev = value;
                return;
            case 0x21:
                _cgramAdr = value;
                _cgramSecond = false;
                TracePpuWrite($"[PPU] CGRAM_ADDR=0x{_cgramAdr:X2}");
                return;
            case 0x22:
                if (!_cgramSecond)
                {
                    _cgramBuffer = (_cgramBuffer & 0xff00) | value;
                    _cgramSecond = true;
                }
                else
                {
                    _cgramBuffer = (_cgramBuffer & 0xff) | (value << 8);
                    _cgram[_cgramAdr++] = (ushort) _cgramBuffer;
                    _cgramAdr &= 0xff;
                    _cgramSecond = false;
                    TracePpuWrite($"[PPU] CGRAM_WRITE adr=0x{_cgramAdr:X2} val=0x{_cgramBuffer:X4}");
                }
                return;
            case 0x23:
                if (GetWindowSelectRaw(0) == (byte)value)
                    return;
                _window1Inversed[0] = (value & 0x01) > 0;
                _window1Enabled[0] = (value & 0x02) > 0;
                _window2Inversed[0] = (value & 0x04) > 0;
                _window2Enabled[0] = (value & 0x08) > 0;
                _window1Inversed[1] = (value & 0x10) > 0;
                _window1Enabled[1] = (value & 0x20) > 0;
                _window2Inversed[1] = (value & 0x40) > 0;
                _window2Enabled[1] = (value & 0x80) > 0;
                MarkLineCachesDirty();
                return;
            case 0x24:
                if (GetWindowSelectRaw(2) == (byte)value)
                    return;
                _window1Inversed[2] = (value & 0x01) > 0;
                _window1Enabled[2] = (value & 0x02) > 0;
                _window2Inversed[2] = (value & 0x04) > 0;
                _window2Enabled[2] = (value & 0x08) > 0;
                _window1Inversed[3] = (value & 0x10) > 0;
                _window1Enabled[3] = (value & 0x20) > 0;
                _window2Inversed[3] = (value & 0x40) > 0;
                _window2Enabled[3] = (value & 0x80) > 0;
                MarkLineCachesDirty();
                return;
            case 0x25:
                if (GetWindowSelectRaw(4) == (byte)value)
                    return;
                _window1Inversed[4] = (value & 0x01) > 0;
                _window1Enabled[4] = (value & 0x02) > 0;
                _window2Inversed[4] = (value & 0x04) > 0;
                _window2Enabled[4] = (value & 0x08) > 0;
                _window1Inversed[5] = (value & 0x10) > 0;
                _window1Enabled[5] = (value & 0x20) > 0;
                _window2Inversed[5] = (value & 0x40) > 0;
                _window2Enabled[5] = (value & 0x80) > 0;
                MarkLineCachesDirty();
                return;
            case 0x26:
                if (_window1Left == value)
                    return;
                _window1Left = value;
                MarkLineCachesDirty();
                return;
            case 0x27:
                if (_window1Right == value)
                    return;
                _window1Right = value;
                MarkLineCachesDirty();
                return;
            case 0x28:
                if (_window2Left == value)
                    return;
                _window2Left = value;
                MarkLineCachesDirty();
                return;
            case 0x29:
                if (_window2Right == value)
                    return;
                _window2Right = value;
                MarkLineCachesDirty();
                return;
            case 0x2a:
                if (GetWindowMaskLogicRaw(0) == (byte)value)
                    return;
                _windowMaskLogic[0] = value & 0x3;
                _windowMaskLogic[1] = (value & 0xc) >> 2;
                _windowMaskLogic[2] = (value & 0x30) >> 4;
                _windowMaskLogic[3] = (value & 0xc0) >> 6;
                MarkLineCachesDirty();
                return;
            case 0x2b:
                if ((_windowMaskLogic[4] | (_windowMaskLogic[5] << 2)) == (value & 0x0f))
                    return;
                _windowMaskLogic[4] = value & 0x3;
                _windowMaskLogic[5] = (value & 0xc) >> 2;
                MarkLineCachesDirty();
                return;
            case 0x2c:
                if (_tmRaw == (byte)value)
                    return;
                _tmRaw = (byte)value;
                _mainScreenEnabled[0] = (value & 0x1) > 0;
                _mainScreenEnabled[1] = (value & 0x2) > 0;
                _mainScreenEnabled[2] = (value & 0x4) > 0;
                _mainScreenEnabled[3] = (value & 0x8) > 0;
                _mainScreenEnabled[4] = (value & 0x10) > 0;
                MarkLineCachesDirty();
                TracePpuWrite($"[PPU] TM=0x{value:X2}");
                return;
            case 0x2d:
                if (_tsRaw == (byte)value)
                    return;
                _tsRaw = (byte)value;
                _subScreenEnabled[0] = (value & 0x1) > 0;
                _subScreenEnabled[1] = (value & 0x2) > 0;
                _subScreenEnabled[2] = (value & 0x4) > 0;
                _subScreenEnabled[3] = (value & 0x8) > 0;
                _subScreenEnabled[4] = (value & 0x10) > 0;
                MarkLineCachesDirty();
                TracePpuWrite($"[PPU] TS=0x{value:X2}");
                return;
            case 0x2e:
                if (GetScreenWindowRaw(_mainScreenWindow) == (byte)value)
                    return;
                _mainScreenWindow[0] = (value & 0x1) > 0;
                _mainScreenWindow[1] = (value & 0x2) > 0;
                _mainScreenWindow[2] = (value & 0x4) > 0;
                _mainScreenWindow[3] = (value & 0x8) > 0;
                _mainScreenWindow[4] = (value & 0x10) > 0;
                MarkLineCachesDirty();
                return;
            case 0x2f:
                if (GetScreenWindowRaw(_subScreenWindow) == (byte)value)
                    return;
                _subScreenWindow[0] = (value & 0x1) > 0;
                _subScreenWindow[1] = (value & 0x2) > 0;
                _subScreenWindow[2] = (value & 0x4) > 0;
                _subScreenWindow[3] = (value & 0x8) > 0;
                _subScreenWindow[4] = (value & 0x10) > 0;
                MarkLineCachesDirty();
                return;
            case 0x30:
                if (GetColorWindowRaw() == (byte)value)
                    return;
                _colorClip = (value & 0xc0) >> 6;
                _preventMath = (value & 0x30) >> 4;
                _addSub = (value & 0x2) > 0;
                _directColor = (value & 0x1) > 0;
                MarkLineCachesDirty();
                TracePpuWrite($"[PPU] CGWSEL=0x{value:X2} clip={_colorClip} prevent={_preventMath} addSub={_addSub} directColor={_directColor}");
                return;
            case 0x31:
                if (GetColorMathRaw() == (byte)value)
                    return;
                _subtractColors = (value & 0x80) > 0;
                _halfColors = (value & 0x40) > 0;
                _mathEnabled[0] = (value & 0x1) > 0;
                _mathEnabled[1] = (value & 0x2) > 0;
                _mathEnabled[2] = (value & 0x4) > 0;
                _mathEnabled[3] = (value & 0x8) > 0;
                _mathEnabled[4] = (value & 0x10) > 0;
                _mathEnabled[5] = (value & 0x20) > 0;
                MarkLineCachesDirty();
                TracePpuWrite($"[PPU] CGADSUB=0x{value:X2} sub={_subtractColors} half={_halfColors} math=[{MathMask()}]");
                return;
            case 0x32:
                if ((value & 0x80) > 0)
                {
                    _fixedColorB = value & 0x1f;
                }
                if ((value & 0x40) > 0)
                {
                    _fixedColorG = value & 0x1f;
                }
                if ((value & 0x20) > 0)
                {
                    _fixedColorR = value & 0x1f;
                }
                TracePpuWrite($"[PPU] COLDATA=0x{value:X2} fixedR={_fixedColorR} fixedG={_fixedColorG} fixedB={_fixedColorB}");
                return;
            case 0x33:
                if (GetSetIniRaw() == (byte)value)
                    return;
                _mode7ExBg = (value & 0x40) > 0;
                _pseudoHires = (value & 0x08) > 0;
                _overscan = (value & 0x04) > 0;
                _objInterlace = (value & 0x02) > 0;
                _interlace = (value & 0x01) > 0;
                MarkSpriteMetaDirty();
                MarkLineCachesDirty();
                return;
        }
    }

    public int[] GetPixels()
    {
        EnsureRuntimeBuffers();
        return _pixelOutput;
    }

    public void EnsureRuntimeBuffers()
    {
        if (_runtimeBuffersReady)
            return;

        if (_spriteLineBuffer.Length != 256)
            _spriteLineBuffer = new byte[256];
        if (_spritePrioBuffer.Length != 256)
            _spritePrioBuffer = new byte[256];
        if (_mode7Xcoords.Length != 256)
            _mode7Xcoords = new int[256];
        if (_mode7Ycoords.Length != 256)
            _mode7Ycoords = new int[256];
        if (_pixelOutput.Length != MaxFrameWidth * MaxFrameHeight)
            _pixelOutput = new int[MaxFrameWidth * MaxFrameHeight];
        if (_windowStateCache.Length != 6 * 256)
            _windowStateCache = new byte[6 * 256];
        if (_mainScreenVisibleCache.Length != 5 * 256)
            _mainScreenVisibleCache = new byte[5 * 256];
        if (_subScreenVisibleCache.Length != 5 * 256)
            _subScreenVisibleCache = new byte[5 * 256];
        if (_clipToBlackCache.Length != 256)
            _clipToBlackCache = new byte[256];
        if (_mathPreventCache.Length != 256)
            _mathPreventCache = new byte[256];
        if (_tilePixelBuffer.Length != 4 * 8)
            _tilePixelBuffer = new ushort[4 * 8];
        if (_tilePriorityBuffer.Length != 4)
            _tilePriorityBuffer = new byte[4];
        if (_spriteXCache.Length != 128)
            _spriteMetaDirty = true;

        _runtimeBuffersReady = true;
    }

    private bool IsHiResOutput()
    {
        return IsTrueHiResOutput() || _pseudoHires;
    }

    private bool IsTrueHiResOutput()
    {
        return _mode == 5 || _mode == 6;
    }

    private void ExpandBufferedLinesToHiRes(int toLineInclusive)
    {
        for (int y = 0; y < toLineInclusive; y++)
        {
            int rowBase = y * MaxFrameWidth;
            for (int x = 255; x >= 0; x--)
            {
                int color = _pixelOutput[rowBase + x];
                _pixelOutput[rowBase + x * 2] = color;
                _pixelOutput[rowBase + x * 2 + 1] = color;
            }
        }
    }

    public ushort[] GetVramDebugCopy()
    {
        return (ushort[])_vram.Clone();
    }

    public ushort[] GetCgramDebugCopy()
    {
        return (ushort[])_cgram.Clone();
    }

    public ushort[] GetOamDebugCopy()
    {
        return (ushort[])_oam.Clone();
    }

    public ulong ComputeVramHash()
    {
        return ComputeHash(_vram);
    }

    public ulong ComputeCgramHash()
    {
        return ComputeHash(_cgram);
    }

    public ulong ComputeOamHash()
    {
        ulong hash = ComputeHash(_oam);
        hash = MixHash(hash, ComputeHash(_highOam));
        return hash;
    }

    public string GetDivergenceSummary()
    {
        return string.Create(
            CultureInfo.InvariantCulture,
            $"ppu(blank={(_forcedBlank ? 1 : 0)} bright={_brightness} mode={_mode} tm=0x{_tmRaw:X2} ts=0x{_tsRaw:X2} " +
            $"pseudo={(_pseudoHires ? 1 : 0)} interlace={(_interlace ? 1 : 0)} objInterlace={(_objInterlace ? 1 : 0)} " +
            $"{GetBgDivergenceSummary(0)} {GetBgDivergenceSummary(1)} {GetBgDivergenceSummary(2)})");
    }

    public string GetDebugSnapshot()
    {
        return string.Join(
            Environment.NewLine,
            new[]
            {
                $"ppu mode={_mode} tm=0x{_tmRaw:X2} ts=0x{_tsRaw:X2} forcedBlank={_forcedBlank} bright={_brightness}",
                $"window wh0={_window1Left} wh1={_window1Right} wh2={_window2Left} wh3={_window2Right} mainW=[{BoolArrayString(_mainScreenWindow, 5)}] subW=[{BoolArrayString(_subScreenWindow, 5)}] w1E=[{BoolArrayString(_window1Enabled, 6)}] w2E=[{BoolArrayString(_window2Enabled, 6)}] w1Inv=[{BoolArrayString(_window1Inversed, 6)}] w2Inv=[{BoolArrayString(_window2Inversed, 6)}] logic=[{IntArrayString(_windowMaskLogic, 6)}]",
                $"cgram[0]=0x{_cgram[0]:X4} cgramFrame[0]=0x{_cgramFrame[0]:X4} cgram[1]=0x{_cgram[1]:X4} cgramFrame[1]=0x{_cgramFrame[1]:X4}",
                GetBgDebugSnapshot(0),
                GetBgDebugSnapshot(1),
                GetBgDebugSnapshot(2),
                $"obj sprAdr1=0x{_sprAdr1:X4} sprAdr2=0x{_sprAdr2:X4} objSize={_objSize} objPriority={_objPriority} oamAdr=0x{_oamAdr:X2} oamRegAdr=0x{_oamRegAdr:X2} oamInHigh={_oamInHigh} oamRegInHigh={_oamRegInHigh} rangeOver={_rangeOver} timeOver={_timeOver}",
                $"oam[0..7]=[{GetOamWindow(0, 8)}]",
                $"oam[8..31]=[{GetOamWindow(8, 24)}]",
                $"obj tiles 70=[{GetVramWindow((_sprAdr1 + 0x70 * 16) & 0x7fff, 8)}] 72=[{GetVramWindow((_sprAdr1 + 0x72 * 16) & 0x7fff, 8)}] 74=[{GetVramWindow((_sprAdr1 + 0x74 * 16) & 0x7fff, 8)}]",
                $"obj tiles 76=[{GetVramWindow((_sprAdr1 + 0x76 * 16) & 0x7fff, 8)}] 78=[{GetVramWindow((_sprAdr1 + 0x78 * 16) & 0x7fff, 8)}] 79=[{GetVramWindow((_sprAdr1 + 0x79 * 16) & 0x7fff, 8)}] 7A=[{GetVramWindow((_sprAdr1 + 0x7A * 16) & 0x7fff, 8)}]",
                $"highOam[0..7]=[{GetHighOamWindow(0, 8)}]"
            });
    }

    private string GetBgDebugSnapshot(int layer)
    {
        if (layer < 0 || layer >= 4)
        {
            return string.Empty;
        }

        int mapAddr = _tilemapAdr.Length > layer ? _tilemapAdr[layer] & 0x7fff : 0;
        int tileAddr = _tileAdr.Length > layer ? _tileAdr[layer] & 0x7fff : 0;
        int hoff = _bgHoff.Length > layer ? _bgHoff[layer] : 0;
        int voff = _bgVoff.Length > layer ? _bgVoff[layer] : 0;
        int bits = _bitPerMode[_mode * 4 + layer];
        ushort tilemapWord = _vram.Length > 0 ? _vram[mapAddr & 0x7fff] : (ushort)0;
        int tileNum = tilemapWord & 0x03ff;
        int tileWordBase = (tileAddr + tileNum * 4 * bits) & 0x7fff;

        return string.Join(
            Environment.NewLine,
            new[]
            {
                $"bg{layer + 1} map=0x{mapAddr:X4} tile=0x{tileAddr:X4} hoff=0x{hoff:X4} voff=0x{voff:X4} bits={bits} wide={(_tilemapWider.Length > layer && _tilemapWider[layer] ? 1 : 0)} high={(_tilemapHigher.Length > layer && _tilemapHigher[layer] ? 1 : 0)} big={(_bigTiles.Length > layer && _bigTiles[layer] ? 1 : 0)}",
                $"bg{layer + 1} tilemap[0..7]=[{GetVramWindow(mapAddr, 8)}]",
                $"bg{layer + 1} tile0 word=0x{tilemapWord:X4} num=0x{tileNum:X3} pal={(tilemapWord >> 10) & 0x7} prio={((tilemapWord >> 13) & 0x1)} xflip={((tilemapWord >> 14) & 0x1)} yflip={((tilemapWord >> 15) & 0x1)}",
                $"bg{layer + 1} tiledata[{tileNum:X3}]=[{GetVramWindow(tileWordBase, Math.Min(bits << 2, 8))}]"
            });
    }

    private string GetBgDivergenceSummary(int layer)
    {
        if (layer < 0 || layer >= 4)
            return $"bg{layer + 1}=invalid";

        int mapAddr = _tilemapAdr.Length > layer ? _tilemapAdr[layer] & 0x7fff : 0;
        int tileAddr = _tileAdr.Length > layer ? _tileAdr[layer] & 0x7fff : 0;
        int hoff = _bgHoff.Length > layer ? _bgHoff[layer] : 0;
        int voff = _bgVoff.Length > layer ? _bgVoff[layer] : 0;
        int bits = _bitPerMode[_mode * 4 + layer];
        int wide = _tilemapWider.Length > layer && _tilemapWider[layer] ? 1 : 0;
        int high = _tilemapHigher.Length > layer && _tilemapHigher[layer] ? 1 : 0;
        int big = _bigTiles.Length > layer && _bigTiles[layer] ? 1 : 0;
        return string.Create(
            CultureInfo.InvariantCulture,
            $"bg{layer + 1}(map=0x{mapAddr:X4} tile=0x{tileAddr:X4} hoff=0x{hoff:X4} voff=0x{voff:X4} bits={bits} wide={wide} high={high} big={big})");
    }

    private static ulong ComputeHash(ushort[] values)
    {
        const ulong offset = 14695981039346656037UL;
        const ulong prime = 1099511628211UL;

        ulong hash = offset;
        foreach (ushort value in values)
        {
            hash ^= (byte)value;
            hash *= prime;
            hash ^= (byte)(value >> 8);
            hash *= prime;
        }

        return hash;
    }

    private static ulong MixHash(ulong left, ulong right)
    {
        return left ^ (right + 0x9E3779B97F4A7C15UL + (left << 6) + (left >> 2));
    }

    private static string BoolArrayString(bool[] values, int count)
    {
        if (values.Length < count)
            count = values.Length;

        string[] parts = new string[count];
        for (int i = 0; i < count; i++)
            parts[i] = values[i] ? "1" : "0";

        return string.Join("", parts);
    }

    private static string IntArrayString(int[] values, int count)
    {
        if (values.Length < count)
            count = values.Length;

        string[] parts = new string[count];
        for (int i = 0; i < count; i++)
            parts[i] = values[i].ToString(CultureInfo.InvariantCulture);

        return string.Join(",", parts);
    }

    private string GetVramWindow(int start, int count)
    {
        if (_vram.Length == 0 || count <= 0)
            return string.Empty;

        string[] values = new string[Math.Min(count, 8)];
        int limit = values.Length;
        for (int i = 0; i < limit; i++)
            values[i] = _vram[(start + i) & 0x7fff].ToString("X4");
        return string.Join(' ', values);
    }

    private string GetOamWindow(int start, int count)
    {
        if (_oam.Length == 0 || count <= 0)
            return string.Empty;

        string[] values = new string[Math.Min(count, 8)];
        for (int i = 0; i < values.Length; i++)
            values[i] = _oam[(start + i) & 0xff].ToString("X4");
        return string.Join(' ', values);
    }

    private string GetHighOamWindow(int start, int count)
    {
        if (_highOam.Length == 0 || count <= 0)
            return string.Empty;

        string[] values = new string[Math.Min(count, 8)];
        for (int i = 0; i < values.Length; i++)
            values[i] = _highOam[(start + i) & 0x0f].ToString("X4");
        return string.Join(' ', values);
    }

    private static int GetTracePpuLimit()
    {
        if (int.TryParse(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_SNES_PPU_LIMIT"), out int limit) && limit > 0)
            return limit;
        return 2000;
    }

    private static void TracePpuWrite(string message)
    {
        if (!TracePpu)
            return;
        int count = Interlocked.Increment(ref _tracePpuCount);
        if (count > TracePpuLimit)
            return;
        Console.WriteLine(message);
    }

    private string MathMask()
    {
        Span<char> mask = stackalloc char[6];
        for (int i = 0; i < 6; i++)
            mask[i] = _mathEnabled[i] ? '1' : '0';
        return new string(mask);
    }

    public void CheckOverscan(int line) 
    {
        if (line == 225 && _overscan)
        {
            FrameOverscan = true;
        }
    }

    public void PrepareSpriteLine(int line)
    {
        EnsureRuntimeBuffers();
        EnsureSpriteMetaCache();
        ClearPreviousSpriteRange();
        _spriteTouchedStart = 256;
        _spriteTouchedEnd = -1;
        _rangeOver = false;
        _timeOver = false;

        if (line < 0
            || line >= (FrameOverscan ? 240 : 225)
            || _forcedBlank
            || (!_mainScreenEnabled[4] && !_subScreenEnabled[4]))
        {
            return;
        }

        EvaluateSprites(line);
    }

    public unsafe void RenderLine(int line) 
    {
        int screenY = line - 1;
        EnsureRuntimeBuffers();
        bool hiResOutput = IsHiResOutput();
        bool trueHiResOutput = IsTrueHiResOutput();
        if (line == 0)
        {
            FrameOverscan = false;
            _frameTrueHiResOutput = false;
        }
        else if (line == (FrameOverscan ? 240 : 225))
        {
            if (!_forcedBlank)
            {
                _oamAdr = _oamRegAdr;
                _oamInHigh = _oamRegInHigh;
            }
            _evenFrame = !_evenFrame;
        }
        else if (line > 0 && line < (FrameOverscan ? 240 : 225))
        {
            if (PerfStatsEnabled)
                PerfRenderedLines++;
            if (line == 1)
            {
                _mosaicStartLine = 0;
                Buffer.BlockCopy(_cgram, 0, _cgramFrame, 0, _cgram.Length * sizeof(ushort));
            }
            if (_mode == 7)
            {
                if (PerfStatsEnabled)
                    PerfMode7Lines++;
                GenerateMode7Coords(screenY);
            }
            if (PerfStatsEnabled)
            {
                if (hiResOutput)
                    PerfHiResLines++;
                if (trueHiResOutput)
                    PerfTrueHiResLines++;
            }
            int outputRow = screenY * MaxFrameWidth;
            int brightnessOffset = _brightness << 15;
            int[] brightnessTable = BrightnessArgbTable;
            int[] pixelOutput = _pixelOutput;
            bool useSimpleMainPath = CanUseSimpleMainScreenPath(hiResOutput, trueHiResOutput);
            if (useSimpleMainPath)
            {
                RenderLineSimpleMainOnly(screenY, outputRow, brightnessOffset, brightnessTable, pixelOutput);
                if (PerfStatsEnabled)
                    PerfOutputPixels += 256;
                return;
            }

            ResetLineCaches();
            if (_lineCachesDirty)
            {
                BuildLineCaches();
                _lineCachesDirty = false;
            }
            if (trueHiResOutput && !_frameTrueHiResOutput)
            {
                ExpandBufferedLinesToHiRes(screenY);
                _frameTrueHiResOutput = true;
            }

            fixed (int* argbTab = brightnessTable)
            fixed (int* pOut = pixelOutput)
            fixed (byte* clipCache = _clipToBlackCache)
            {
                int* argbTabOffset = argbTab + brightnessOffset;
                int* pRow = pOut + outputRow;

                for (int i = 0; i < 256; i++)
                {
                    int r1 = 0, g1 = 0, b1 = 0;
                    int r2 = 0, g2 = 0, b2 = 0;
                    bool mainVisible = false, subVisible = false;

                    if (!_forcedBlank)
                    {
                        GetColor(false, i, screenY, out ushort color, out int item2, out int item3);
                        bool mathEnabled = GetMathEnabled(i, item2, item3);
                        mainVisible = item2 < 5;
                        r2 = color & 0x1f;
                        g2 = (color >> 5) & 0x1f;
                        b2 = (color >> 10) & 0x1f;

                        if (clipCache[i] != 0)
                        {
                            r2 = 0; g2 = 0; b2 = 0;
                        }

                        ushort secondColor = 0;
                        int secondLayer = 5;
                        int secondPixel = 0;
                        if (_mode == 5 || _mode == 6 || _pseudoHires || (mathEnabled && _addSub))
                        {
                            GetColor(true, i, screenY, out secondColor, out secondLayer, out secondPixel);
                            subVisible = secondLayer < 5;
                            r1 = secondColor & 0x1f;
                            g1 = (secondColor >> 5) & 0x1f;
                            b1 = (secondColor >> 10) & 0x1f;
                        }

                        if (mathEnabled)
                        {
                            if (_subtractColors)
                            {
                                r2 -= _addSub && secondLayer < 5 ? r1 : _fixedColorR;
                                g2 -= _addSub && secondLayer < 5 ? g1 : _fixedColorG;
                                b2 -= _addSub && secondLayer < 5 ? b1 : _fixedColorB;
                            }
                            else
                            {
                                r2 += _addSub && secondLayer < 5 ? r1 : _fixedColorR;
                                g2 += _addSub && secondLayer < 5 ? g1 : _fixedColorG;
                                b2 += _addSub && secondLayer < 5 ? b1 : _fixedColorB;
                            }

                            if (_halfColors && (secondLayer < 5 || !_addSub))
                            {
                                r2 >>= 1; g2 >>= 1; b2 >>= 1;
                            }

                            if ((uint)r2 > 31) r2 = r2 < 0 ? 0 : 31;
                            if ((uint)g2 > 31) g2 = g2 < 0 ? 0 : 31;
                            if ((uint)b2 > 31) b2 = b2 < 0 ? 0 : 31;
                        }

                        if (!trueHiResOutput && hiResOutput)
                        {
                            if (!mainVisible && subVisible)
                            {
                                r2 = r1; g2 = g1; b2 = b1;
                            }
                            else if (mainVisible && subVisible)
                            {
                                r2 = (r2 + r1) >> 1;
                                g2 = (g2 + g1) >> 1;
                                b2 = (b2 + b1) >> 1;
                            }
                        }
                    }

                    int mainColor = argbTabOffset[(b2 << 10) | (g2 << 5) | r2];
                    if (trueHiResOutput)
                    {
                        int subColor = argbTabOffset[(b1 << 10) | (g1 << 5) | r1];
                        pRow[i * 2] = subColor;
                        pRow[i * 2 + 1] = mainColor;
                    }
                    else if (_frameTrueHiResOutput)
                    {
                        pRow[i * 2] = mainColor;
                        pRow[i * 2 + 1] = mainColor;
                    }
                    else
                    {
                        pRow[i] = mainColor;
                    }
                }
            }
            if (PerfStatsEnabled)
                PerfOutputPixels += (ulong)(trueHiResOutput || _frameTrueHiResOutput ? 512 : 256);
        }
    }

    private bool CanUseSimpleMainScreenPath(bool hiResOutput, bool trueHiResOutput)
    {
        return !hiResOutput
            && !trueHiResOutput
            && !_frameTrueHiResOutput
            && !_forcedBlank
            && _mode != 7
            && !_directColor
            && !AnyColorMathEnabled()
            && _colorClip == 0
            && !HasActiveMainWindow(0)
            && !HasActiveMainWindow(1)
            && !HasActiveMainWindow(2)
            && !HasActiveMainWindow(3)
            && !HasActiveMainWindow(4);
    }

    private bool CanUseChunkedSimpleMainScreenPath()
    {
        if (_mode == 2 || _mode == 4 || _mode == 6)
            return false;

        return !_mosaicEnabled[0]
            && !_mosaicEnabled[1]
            && !_mosaicEnabled[2]
            && !_mosaicEnabled[3];
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private unsafe void RenderLineSimpleMainOnly(
        int screenY,
        int outputRow,
        int brightnessOffset,
        int[] brightnessTable,
        int[] pixelOutput)
    {
        fixed (int* argbTab = brightnessTable)
        fixed (int* pOut = pixelOutput)
        fixed (ushort* cgramPtr = _cgram)
        {
            int* pRow = pOut + outputRow;
            int* argbTabOffset = argbTab + brightnessOffset;
            Span<int> activeLayers = stackalloc int[12];
            Span<int> activePriorities = stackalloc int[12];
            int activeCount = 0;
            int modeIndex = _layer3Prio && _mode == 1 ? 96 : 12 * _mode;
            int layerCount = _layercountPerMode[_mode];
            for (int j = 0; j < layerCount; j++)
            {
                int layer = _layersPerMode[modeIndex + j];
                if (layer >= 5 || !_mainScreenEnabled[layer])
                    continue;

                activeLayers[activeCount] = layer;
                activePriorities[activeCount] = _prioPerMode[modeIndex + j];
                activeCount++;
            }

            int baseY = _interlace && (_mode == 5 || _mode == 6)
                ? screenY * 2 + (_evenFrame ? 1 : 0)
                : screenY;
            if (CanUseChunkedSimpleMainScreenPath())
            {
                RenderLineSimpleMainOnlyChunked(
                    outputRow,
                    brightnessOffset,
                    brightnessTable,
                    pixelOutput,
                    activeLayers,
                    activePriorities,
                    activeCount,
                    baseY);
                return;
            }

            for (int x = 0; x < 256; x++)
            {
                ushort color = cgramPtr[0];
                for (int j = 0; j < activeCount; j++)
                {
                    int layer = activeLayers[j];
                    int lx = x;
                    int ly = baseY;

                    if (layer < 4)
                    {
                        if (_mosaicEnabled[layer])
                        {
                            lx -= lx % _mosaicSize;
                            ly -= (ly - _mosaicStartLine) % _mosaicSize;
                        }

                        lx += _bgHoff[layer];
                        ly += _bgVoff[layer];

                        if ((_mode == 2 || _mode == 4 || _mode == 6) && layer < 2)
                        {
                            int andVal = layer == 0 ? 0x2000 : 0x4000;
                            if (x == 0)
                                _lastOrigTileX[layer] = lx >> 3;
                            int tileStartX = (lx - _bgHoff[layer]) - (lx - (lx & 0xfff8));
                            if (lx >> 3 != _lastOrigTileX[layer] && x > 0)
                            {
                                FetchTileInBuffer(_bgHoff[2] + ((tileStartX - 1) & 0x1f8), _bgVoff[2], 2, true);
                                _optHorBuffer[layer] = _tilemapBuffer[2];
                                if (_mode == 4)
                                {
                                    if ((_optHorBuffer[layer] & 0x8000) != 0)
                                    {
                                        _optVerBuffer[layer] = _optHorBuffer[layer];
                                        _optHorBuffer[layer] = 0;
                                    }
                                    else
                                    {
                                        _optVerBuffer[layer] = 0;
                                    }
                                }
                                else
                                {
                                    FetchTileInBuffer(_bgHoff[2] + ((tileStartX - 1) & 0x1f8), _bgVoff[2] + 8, 2, true);
                                    _optVerBuffer[layer] = _tilemapBuffer[2];
                                }
                                _lastOrigTileX[layer] = lx >> 3;
                            }

                            if ((_optHorBuffer[layer] & andVal) != 0)
                                lx = (lx & 0x7) + ((_optHorBuffer[layer] + ((tileStartX + 7) & 0x1f8)) & 0x1ff8);
                            if ((_optVerBuffer[layer] & andVal) != 0)
                                ly = (_optVerBuffer[layer] & 0x1fff) + (ly - _bgVoff[layer]);
                        }
                    }

                    int pixel = GetPixelForLayer(lx, ly, layer, activePriorities[j]);
                    if ((pixel & 0xFF) == 0)
                        continue;

                    color = cgramPtr[pixel & 0xFF];
                    break;
                }

                pRow[x] = argbTabOffset[color & 0x7fff];
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private unsafe void RenderLineSimpleMainOnlyChunked(
        int outputRow,
        int brightnessOffset,
        int[] brightnessTable,
        int[] pixelOutput,
        ReadOnlySpan<int> activeLayers,
        ReadOnlySpan<int> activePriorities,
        int activeCount,
        int baseY)
    {
        fixed (int* argbTab = brightnessTable)
        fixed (int* pOut = pixelOutput)
        fixed (ushort* cgramPtr = _cgram)
        fixed (byte* spritePrio = _spritePrioBuffer)
        fixed (byte* spriteLine = _spriteLineBuffer)
        {
            int* pRow = pOut + outputRow;
            int* argbTabOffset = argbTab + brightnessOffset;
            Span<ushort> chunkPixels = stackalloc ushort[12 * 8];
            Span<byte> chunkOpaque = stackalloc byte[12 * 8];

            for (int startX = 0; startX < 256; startX += 8)
            {
                for (int j = 0; j < activeCount; j++)
                {
                    Span<ushort> entryPixels = chunkPixels.Slice(j << 3, 8);
                    Span<byte> entryOpaque = chunkOpaque.Slice(j << 3, 8);
                    entryPixels.Clear();
                    entryOpaque.Clear();

                    int layer = activeLayers[j];
                    if (layer < 4)
                        FillChunkPixelsForLayer(startX, baseY, layer, activePriorities[j], entryPixels, entryOpaque);
                }

                for (int pixelX = 0; pixelX < 8; pixelX++)
                {
                    int screenX = startX + pixelX;
                    ushort color = cgramPtr[0];
                    for (int j = 0; j < activeCount; j++)
                    {
                        int layer = activeLayers[j];
                        if (layer == 4)
                        {
                            if (spritePrio[screenX] != activePriorities[j])
                                continue;

                            byte spritePixel = spriteLine[screenX];
                            if (spritePixel == 0)
                                continue;

                            color = cgramPtr[spritePixel];
                            break;
                        }

                        if (chunkOpaque[(j << 3) | pixelX] == 0)
                            continue;

                        color = cgramPtr[chunkPixels[(j << 3) | pixelX]];
                        break;
                    }

                    pRow[screenX] = argbTabOffset[color & 0x7fff];
                }
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void FillChunkPixelsForLayer(int startX, int y, int layer, int priority, Span<ushort> destPixels, Span<byte> destOpaque)
    {
        Span<ushort> tileRow = stackalloc ushort[8];
        int tilePriority = -1;
        int loadedTileX = int.MinValue;
        int loadedY = int.MinValue;
        int sampleX = startX + _bgHoff[layer];
        int sampleY = y + _bgVoff[layer];

        for (int i = 0; i < 8; i++)
        {
            int tileX = sampleX >> 3;
            if (tileX != loadedTileX || sampleY != loadedY)
            {
                DecodeTileRow(sampleX, sampleY, layer, tileRow, out tilePriority);
                loadedTileX = tileX;
                loadedY = sampleY;
            }

            ushort pixel = tileRow[sampleX & 0x7];
            if (tilePriority == priority && pixel != 0)
            {
                destPixels[i] = pixel;
                destOpaque[i] = 1;
            }

            sampleX++;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void DecodeTileRow(int x, int y, int layer, Span<ushort> dest, out int tilePriority)
    {
        bool wideTiles = _bigTiles[layer] || _mode == 5 || _mode == 6;
        int tileWidthPixels = wideTiles ? 16 : 8;
        int tileHeightPixels = _bigTiles[layer] ? 16 : 8;
        int screenWidthPixels = (_tilemapWider[layer] ? 64 : 32) * tileWidthPixels;
        int screenHeightPixels = (_tilemapHigher[layer] ? 64 : 32) * tileHeightPixels;

        int wrappedX = x & (screenWidthPixels - 1);
        int wrappedY = y & (screenHeightPixels - 1);
        int tilemapBase = _tilemapAdr[layer];

        int singleScreenWidthPixels = 32 * tileWidthPixels;
        int singleScreenHeightPixels = 32 * tileHeightPixels;
        if (wrappedX >= singleScreenWidthPixels)
        {
            tilemapBase += 1024;
            wrappedX &= singleScreenWidthPixels - 1;
        }

        if (wrappedY >= singleScreenHeightPixels)
        {
            tilemapBase += _tilemapWider[layer] ? 2048 : 1024;
            wrappedY &= singleScreenHeightPixels - 1;
        }

        int tileColumn = wrappedX / tileWidthPixels;
        int tileRow = wrappedY / tileHeightPixels;
        ushort tilemapWord = _vram[(tilemapBase + (tileRow << 5) + tileColumn) & 0x7fff];

        bool yFlip = (tilemapWord & 0x8000) != 0;
        bool xFlip = (tilemapWord & 0x4000) != 0;
        int yRow = yFlip ? 7 - (wrappedY & 0x7) : wrappedY & 0x7;
        int tileNum = tilemapWord & 0x3ff;

        bool shiftRight = wideTiles && (xFlip ? (wrappedX & 15) < 8 : (wrappedX & 15) >= 8);
        bool shiftDown = tileHeightPixels == 16 && (yFlip ? (wrappedY & 15) < 8 : (wrappedY & 15) >= 8);

        if (shiftRight)
            tileNum += 1;
        if (shiftDown)
            tileNum += 0x10;

        int bits = _bitPerMode[_mode * 4 + layer];
        int tileBase = (_tileAdr[layer] + tileNum * 4 * bits + yRow) & 0x7fff;

        int plane1 = _vram[tileBase];
        int plane2 = bits > 2 ? _vram[(tileBase + 8) & 0x7fff] : 0;
        int plane3 = bits > 4 ? _vram[(tileBase + 16) & 0x7fff] : 0;
        int plane4 = bits > 4 ? _vram[(tileBase + 24) & 0x7fff] : 0;

        int paletteNum = (tilemapWord & 0x1c00) >> 10;
        paletteNum += _mode == 0 ? layer * 8 : 0;
        int mul = bits > 4 ? 256 : bits > 2 ? 16 : 4;
        int pixelBase = paletteNum * mul;
        tilePriority = (tilemapWord >> 13) & 0x1;

        for (int i = 0; i < 8; i++)
        {
            int shift = xFlip ? i : 7 - i;
            int tileData = (plane1 >> shift) & 0x1;
            tileData |= ((plane1 >> (8 + shift)) & 0x1) << 1;
            if (bits > 2)
            {
                tileData |= ((plane2 >> shift) & 0x1) << 2;
                tileData |= ((plane2 >> (8 + shift)) & 0x1) << 3;
            }

            if (bits > 4)
            {
                tileData |= ((plane3 >> shift) & 0x1) << 4;
                tileData |= ((plane3 >> (8 + shift)) & 0x1) << 5;
                tileData |= ((plane4 >> shift) & 0x1) << 6;
                tileData |= ((plane4 >> (8 + shift)) & 0x1) << 7;
            }

            dest[i] = tileData > 0 ? (ushort)(pixelBase + tileData) : (ushort)0;
        }
    }

    internal void ResetPerfCounters()
    {
        if (!PerfStatsEnabled)
            return;
        PerfRenderedLines = 0;
        PerfMode7Lines = 0;
        PerfHiResLines = 0;
        PerfTrueHiResLines = 0;
        PerfOutputPixels = 0;
    }

    private void ResetLineCaches()
    {
        _lastTileFetchedX[0] = -1;
        _lastTileFetchedX[1] = -1;
        _lastTileFetchedX[2] = -1;
        _lastTileFetchedX[3] = -1;
        _lastTileFetchedY[0] = -1;
        _lastTileFetchedY[1] = -1;
        _lastTileFetchedY[2] = -1;
        _lastTileFetchedY[3] = -1;
        _optHorBuffer[0] = 0;
        _optHorBuffer[1] = 0;
        _optVerBuffer[0] = 0;
        _optVerBuffer[1] = 0;
        _lastOrigTileX[0] = -1;
        _lastOrigTileX[1] = -1;
    }

    private void BuildLineCaches()
    {
        _lineModeIndex = _layer3Prio && _mode == 1 ? 96 : 12 * _mode;
        if (_mode7ExBg && _mode == 7)
            _lineModeIndex = 108;
        _lineLayerCount = _layercountPerMode[_mode];
        _lineAnyColorMathEnabled = AnyColorMathEnabled();

        for (int layer = 0; layer < 6; layer++)
        {
            int baseIndex = layer << 8;
            bool hasWindow = _window1Enabled[layer] || _window2Enabled[layer];
            if (layer >= 5)
                continue;

            bool mainEnabled = _mainScreenEnabled[layer];
            bool subEnabled = _subScreenEnabled[layer];
            bool mainUsesWindow = _mainScreenWindow[layer];
            bool subUsesWindow = _subScreenWindow[layer];
            int visibleBaseIndex = layer << 8;
            if (!hasWindow || (!mainUsesWindow && !subUsesWindow))
            {
                FillCacheRange(_mainScreenVisibleCache, visibleBaseIndex, mainEnabled ? (byte)1 : (byte)0);
                FillCacheRange(_subScreenVisibleCache, visibleBaseIndex, subEnabled ? (byte)1 : (byte)0);
                continue;
            }

            if (!mainEnabled && !subEnabled)
            {
                FillCacheRange(_mainScreenVisibleCache, visibleBaseIndex, 0);
                FillCacheRange(_subScreenVisibleCache, visibleBaseIndex, 0);
                continue;
            }

            for (int x = 0; x < 256; x++)
            {
                bool windowState = ComputeWindowState(x, layer);
                _windowStateCache[baseIndex + x] = windowState ? (byte)1 : (byte)0;
                _mainScreenVisibleCache[visibleBaseIndex + x] =
                    mainEnabled && (!mainUsesWindow || !windowState) ? (byte)1 : (byte)0;
                _subScreenVisibleCache[visibleBaseIndex + x] =
                    subEnabled && (!subUsesWindow || !windowState) ? (byte)1 : (byte)0;
            }
        }

        bool usesColorWindow = (_colorClip != 0 || _preventMath != 0) && (_window1Enabled[5] || _window2Enabled[5]);
        if (!usesColorWindow)
        {
            FillCacheRange(_clipToBlackCache, 0, ShouldClipToBlack(false) ? (byte)1 : (byte)0);
            FillCacheRange(_mathPreventCache, 0, ShouldPreventMath(false) ? (byte)1 : (byte)0);
            return;
        }

        int colorWindowBase = 5 << 8;
        for (int x = 0; x < 256; x++)
        {
            bool colorWindow = ComputeWindowState(x, 5);
            _windowStateCache[colorWindowBase + x] = colorWindow ? (byte)1 : (byte)0;
            _clipToBlackCache[x] = ShouldClipToBlack(colorWindow) ? (byte)1 : (byte)0;
            _mathPreventCache[x] = ShouldPreventMath(colorWindow) ? (byte)1 : (byte)0;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private unsafe void GetColor(bool sub, int x, int y, out ushort color, out int layer, out int pixel) 
    {
        int modeIndex = _lineModeIndex;
        int count = _lineLayerCount;
        pixel = 0;
        layer = 5;
        if (_interlace && (_mode == 5 || _mode == 6))
        {
            y = y * 2 + (_evenFrame ? 1 : 0);
        }

        fixed (byte* mainVis = _mainScreenVisibleCache)
        fixed (byte* subVis = _subScreenVisibleCache)
        fixed (ushort* cgramPtr = _cgram)
        {
            byte* visCache = sub ? subVis : mainVis;
            int j;
            for (j = 0; j < count; j++)
            {
                layer = _layersPerMode[modeIndex + j];
                if (visCache[(layer << 8) | x] != 0)
                {
                    int lx = x;
                    int ly = y;
                    if (_mosaicEnabled[layer])
                    {
                        lx -= lx % _mosaicSize;
                        ly -= (ly - _mosaicStartLine) % _mosaicSize;
                    }
                    lx += _mode == 7 ? 0 : _bgHoff[layer];
                    ly += _mode == 7 ? 0 : _bgVoff[layer];

                    if ((_mode == 2 || _mode == 4 || _mode == 6) && layer < 2)
                    {
                        int andVal = layer == 0 ? 0x2000 : 0x4000;
                        if (x == 0) _lastOrigTileX[layer] = lx >> 3;
                        int tileStartX = (lx - _bgHoff[layer]) - (lx - (lx & 0xfff8));
                        if (lx >> 3 != _lastOrigTileX[layer] && x > 0)
                        {
                            FetchTileInBuffer(_bgHoff[2] + ((tileStartX - 1) & 0x1f8), _bgVoff[2], 2, true);
                            _optHorBuffer[layer] = _tilemapBuffer[2];
                            if (_mode == 4)
                            {
                                if ((_optHorBuffer[layer] & 0x8000) > 0) { _optVerBuffer[layer] = _optHorBuffer[layer]; _optHorBuffer[layer] = 0; }
                                else _optVerBuffer[layer] = 0;
                            }
                            else
                            {
                                FetchTileInBuffer(_bgHoff[2] + ((tileStartX - 1) & 0x1f8), _bgVoff[2] + 8, 2, true);
                                _optVerBuffer[layer] = _tilemapBuffer[2];
                            }
                            _lastOrigTileX[layer] = lx >> 3;
                        }
                        if ((_optHorBuffer[layer] & andVal) > 0) lx = (lx & 0x7) + ((_optHorBuffer[layer] + ((tileStartX + 7) & 0x1f8)) & 0x1ff8);
                        if ((_optVerBuffer[layer] & andVal) > 0) ly = (_optVerBuffer[layer] & 0x1fff) + (ly - _bgVoff[layer]);
                    }
                    pixel = GetPixelForLayer(lx, ly, layer, _prioPerMode[modeIndex + j]);
                    if ((pixel & 0xFF) != 0) break;
                }
            }

            if (j == count)
            {
                color = (sub) ? (ushort)((_fixedColorB << 10) | (_fixedColorG << 5) | _fixedColorR) : cgramPtr[0];
                layer = 5;
                return;
            }

            color = cgramPtr[pixel & 0xFF];
            if (_directColor && layer < 4 && _bitPerMode[_mode * 4 + layer] == 8)
            {
                int r = ((pixel & 0x7) << 2) | ((pixel & 0x100) >> 7);
                int g = ((pixel & 0x38) >> 1) | ((pixel & 0x200) >> 8);
                int b = ((pixel & 0xc0) >> 3) | ((pixel & 0x400) >> 8);
                color = (ushort)((b << 10) | (g << 5) | r);
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool GetMathEnabled(int x, int l, int pal) 
    {
        if (_mathPreventCache[x] != 0)
        {
            return false;
        }
        if (_mathEnabled[l] && (l != 4 || pal >= 0xc0))
        {
            return true;
        }
        return false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool ShouldClipToBlack(bool colorWindow)
    {
        return _colorClip == 3 || (_colorClip == 2 && colorWindow) || (_colorClip == 1 && !colorWindow);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool ShouldPreventMath(bool colorWindow)
    {
        return _preventMath == 3 || (_preventMath == 2 && colorWindow) || (_preventMath == 1 && !colorWindow);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool ComputeWindowState(int x, int l) 
    {
        if (!_window1Enabled[l] && !_window2Enabled[l])
        {
            return false;
        }
        if (_window1Enabled[l] && !_window2Enabled[l])
        {
            bool test = x >= _window1Left && x <= _window1Right;
            return _window1Inversed[l] ? !test : test;
        }
        if (!_window1Enabled[l] && _window2Enabled[l])
        {
            bool test = x >= _window2Left && x <= _window2Right;
            return _window2Inversed[l] ? !test : test;
        }
        bool w1test = x >= _window1Left && x <= _window1Right;
        w1test = _window1Inversed[l] ? !w1test : w1test;
        bool w2test = x >= _window2Left && x <= _window2Right;
        w2test = _window2Inversed[l] ? !w2test : w2test;
        return _windowMaskLogic[l] switch
        {
            0 => w1test || w2test,
            1 => w1test && w2test,
            2 => w1test != w2test,
            3 => w1test == w2test,
            _ => false,
        };
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private unsafe int GetPixelForLayer(int x, int y, int l, int p) 
    {
        if (l > 3)
        {
            fixed (byte* sPrio = _spritePrioBuffer)
            fixed (byte* sLine = _spriteLineBuffer)
            {
                if (sPrio[x] != p) return 0;
                return sLine[x];
            }
        }
        if (_mode == 7)
        {
            return GetMode7Pixel(x, y, l, p);
        }
        
        fixed (int* lastX = _lastTileFetchedX)
        fixed (int* lastY = _lastTileFetchedY)
        fixed (byte* tPrio = _tilePriorityBuffer)
        fixed (ushort* tPix = _tilePixelBuffer)
        {
            if (x >> 3 != lastX[l] || y != lastY[l])
            {
                FetchTileInBuffer(x, y, l, false);
                lastX[l] = x >> 3;
                lastY[l] = y;
            }
            if (tPrio[l] != p) return 0;
            return tPix[(l << 3) | (x & 0x7)];
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private unsafe void FetchTileInBuffer(int x, int y, int l, bool offset) 
    {
        bool wideTiles = _bigTiles[l] || _mode == 5 || _mode == 6;
        int tileWidthPixels = wideTiles ? 16 : 8;
        int tileHeightPixels = _bigTiles[l] ? 16 : 8;
        int screenWidthPixels = (_tilemapWider[l] ? 64 : 32) * tileWidthPixels;
        int screenHeightPixels = (_tilemapHigher[l] ? 64 : 32) * tileHeightPixels;

        int wrappedX = x & (screenWidthPixels - 1);
        int wrappedY = y & (screenHeightPixels - 1);
        int tilemapBase = _tilemapAdr[l];

        int singleScreenWidthPixels = 32 * tileWidthPixels;
        int singleScreenHeightPixels = 32 * tileHeightPixels;
        if (wrappedX >= singleScreenWidthPixels)
        {
            tilemapBase += 1024;
            wrappedX &= singleScreenWidthPixels - 1;
        }

        if (wrappedY >= singleScreenHeightPixels)
        {
            tilemapBase += _tilemapWider[l] ? 2048 : 1024;
            wrappedY &= singleScreenHeightPixels - 1;
        }

        int tileColumn = wrappedX / tileWidthPixels;
        int tileRow = wrappedY / tileHeightPixels;
        
        fixed (ushort* vramPtr = _vram)
        fixed (ushort* tPix = _tilePixelBuffer)
        {
            ushort tilemapWord = vramPtr[(tilemapBase + (tileRow << 5) + tileColumn) & 0x7fff];
            _tilemapBuffer[l] = tilemapWord;
            if (offset)
            {
                return;
            }
            
            bool yFlip = (tilemapWord & 0x8000) != 0;
            bool xFlip = (tilemapWord & 0x4000) != 0;
            int yRow = yFlip ? 7 - (wrappedY & 0x7) : wrappedY & 0x7;
            int tileNum = tilemapWord & 0x3ff;
            
            bool shiftRight = wideTiles && (xFlip ? (wrappedX & 15) < 8 : (wrappedX & 15) >= 8);
            bool shiftDown = tileHeightPixels == 16 && (yFlip ? (wrappedY & 15) < 8 : (wrappedY & 15) >= 8);
            
            if (shiftRight) tileNum += 1;
            if (shiftDown) tileNum += 0x10;
            
            int bits = _bitPerMode[_mode * 4 + l];
            int tileBase = (_tileAdr[l] + tileNum * 4 * bits + yRow) & 0x7fff;
            
            int plane1 = vramPtr[tileBase];
            int plane2 = bits > 2 ? vramPtr[(tileBase + 8) & 0x7fff] : 0;
            int plane3 = bits > 4 ? vramPtr[(tileBase + 16) & 0x7fff] : 0;
            int plane4 = bits > 4 ? vramPtr[(tileBase + 24) & 0x7fff] : 0;
            
            int paletteNum = (tilemapWord & 0x1c00) >> 10;
            paletteNum += _mode == 0 ? l * 8 : 0;
            int mul = bits > 4 ? 256 : bits > 2 ? 16 : 4;
            int pixelBase = paletteNum * mul;
            int pixelOffset = l << 3;
            _tilePriorityBuffer[l] = (byte)((tilemapWord >> 13) & 0x1);
            
            ushort* dest = tPix + pixelOffset;
            for (int i = 0; i < 8; i++)
            {
                int shift = xFlip ? i : 7 - i;
                int tileData = (plane1 >> shift) & 0x1;
                tileData |= ((plane1 >> (8 + shift)) & 0x1) << 1;
                if (bits > 2)
                {
                    tileData |= ((plane2 >> shift) & 0x1) << 2;
                    tileData |= ((plane2 >> (8 + shift)) & 0x1) << 3;
                }

                if (bits > 4)
                {
                    tileData |= ((plane3 >> shift) & 0x1) << 4;
                    tileData |= ((plane3 >> (8 + shift)) & 0x1) << 5;
                    tileData |= ((plane4 >> shift) & 0x1) << 6;
                    tileData |= ((plane4 >> (8 + shift)) & 0x1) << 7;
                }

                dest[i] = tileData > 0 ? (ushort)(pixelBase + tileData) : (ushort)0;
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void EvaluateSprites(int line) 
    {
        Span<int> scannedSprites = stackalloc int[32];
        int spriteCount = 0;
        int startSprite = _objPriority ? ((_oamAdr >> 1) & 0x7f) : 0;

        // Scan OAM in hardware order first. The first 32 in-range sprites win;
        // only after selection are tiles rendered in reverse for priority.
        for (int i = 0; i < 128; i++)
        {
            int spriteIndex = (startSprite + i) & 0x7f;
            int x = _spriteXCache[spriteIndex];
            int y = _spriteYCache[spriteIndex];
            int spriteWidth = _spriteWidthCache[spriteIndex];
            int spriteHeight = _spriteHeightCache[spriteIndex];
            int sprRow = line - y;
            if (sprRow < 0 || sprRow >= spriteHeight)
            {
                sprRow = line + (256 - y);
            }

            if (sprRow < 0 || sprRow >= spriteHeight || x <= -(spriteWidth * 8))
            {
                continue;
            }

            if (spriteCount == 32)
            {
                _rangeOver = true;
                break;
            }

            scannedSprites[spriteCount++] = spriteIndex;
        }

        int sliverCount = 0;
        for (int spriteBufferIndex = spriteCount - 1; spriteBufferIndex >= 0; spriteBufferIndex--)
        {
            int spriteIndex = scannedSprites[spriteBufferIndex];
            int x = _spriteXCache[spriteIndex];
            int y = _spriteYCache[spriteIndex];
            int tile = _spriteTileCache[spriteIndex];
            int ex = _spriteAttrCache[spriteIndex];
            int spriteWidth = _spriteWidthCache[spriteIndex];
            int spriteHeight = _spriteHeightCache[spriteIndex];
            int sprRow = line - y;
            if (sprRow < 0 || sprRow >= spriteHeight)
            {
                sprRow = line + (256 - y);
            }

            sprRow = _objInterlace ? sprRow * 2 + (_evenFrame ? 1 : 0) : sprRow;
            // OBJSEL selects a base plus an optional second 256-tile block with a programmable gap.
            // Attribute bit 0 selects that second block, which starts after the first 0x1000-byte OBJ area.
            int adr = _sprAdr1 + ((ex & 0x1) > 0 ? 0x1000 + _sprAdr2 : 0);
            int spriteFullHeight = _objInterlace ? spriteHeight << 1 : spriteHeight;
            sprRow = (ex & 0x80) > 0 ? spriteFullHeight - 1 - sprRow : sprRow;
            int tileRow = sprRow >> 3;
            sprRow &= 0x7;
            for (int k = 0; k < spriteWidth; k++)
            {
                int sliverX = x + k * 8;
                if (sliverX <= -7 || sliverX >= 256)
                {
                    continue;
                }

                if (sliverCount == 34)
                {
                    _timeOver = true;
                    return;
                }

                int sliverStart = Math.Max(0, sliverX);
                int sliverEnd = Math.Min(255, sliverX + 7);
                if (sliverStart < _spriteTouchedStart)
                    _spriteTouchedStart = sliverStart;
                if (sliverEnd > _spriteTouchedEnd)
                    _spriteTouchedEnd = sliverEnd;

                int tileColumn = (ex & 0x40) > 0 ? spriteWidth - 1 - k : k;
                int tileNum = tile;
                tileNum = (tileNum & ~0x0f) | ((tileNum + tileColumn) & 0x0f);
                tileNum = (tileNum & ~0xf0) | ((tileNum + (tileRow << 4)) & 0xf0);
                tileNum &= 0xff;
                int tileAddr = (adr + tileNum * 16 + sprRow) & 0x7fff;
                int tileP1 = _vram[tileAddr];
                int tileP2 = _vram[(tileAddr + 8) & 0x7fff];
                for (int j = 0; j < 8; j++)
                {
                    int shift = (ex & 0x40) > 0 ? j : 7 - j;
                    int tileData = (tileP1 >> shift) & 0x1;
                    tileData |= ((tileP1 >> (8 + shift)) & 0x1) << 1;
                    tileData |= ((tileP2 >> shift) & 0x1) << 2;
                    tileData |= ((tileP2 >> (8 + shift)) & 0x1) << 3;
                    int color = tileData + 16 * ((ex & 0xe) >> 1);
                    int xInd = x + k * 8 + j;
                    if (tileData > 0 && xInd < 256 && xInd >= 0)
                    {
                        _spriteLineBuffer[xInd] = (byte)(0x80 + color);
                        _spritePrioBuffer[xInd] = (byte)((ex & 0x30) >> 4);
                    }
                }

                sliverCount++;
            }
        }
    }

     private void GenerateMode7Coords(int y)
     {
        int rY = _mode7FlipY ? 255 - y : y;
        int clippedH = _mode7Hoff - _mode7X;
        clippedH = (clippedH & 0x2000) > 0 ? clippedH | ~0x3ff : clippedH & 0x3ff;
        int clippedV = _mode7Voff - _mode7Y;
        clippedV = (clippedV & 0x2000) > 0 ? clippedV | ~0x3ff : clippedV & 0x3ff;
        int lineStartX = ((_mode7A * clippedH) & ~63) + ((_mode7B * rY) & ~63) + ((_mode7B * clippedV) & ~63) + (_mode7X << 8);
        int lineStartY = ((_mode7C * clippedH) & ~63) + ((_mode7D * rY) & ~63) + ((_mode7D * clippedV) & ~63) + (_mode7Y << 8);
        _mode7Xcoords[0] = lineStartX;
        _mode7Ycoords[0] = lineStartY;
        for (var i = 1; i < 256; i++)
        {
            _mode7Xcoords[i] = _mode7Xcoords[i - 1] + _mode7A;
            _mode7Ycoords[i] = _mode7Ycoords[i - 1] + _mode7C;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int GetMode7Pixel(int x, int y, int l, int p) 
    {
        int pixelData = _tilemapBuffer[0];
        if (x != _lastTileFetchedX[0] || y != _lastTileFetchedY[0])
        {
            int rX = _mode7FlipX ? 255 - x : x;
            int px = _mode7Xcoords[rX] >> 8;
            int py = _mode7Ycoords[rX] >> 8;
            var pixelIsTransparent = false;
            if (_mode7LargeField && (px < 0 || px >= 1024 || py < 0 || py >= 1024))
            {
                if (_mode7Char0fill)
                {
                    px &= 0x7;
                    py &= 0x7;
                }
                else
                {
                    pixelIsTransparent = true;
                }
            }
            int tileX = (px & 0x3f8) >> 3;
            int tileY = (py & 0x3f8) >> 3;
            int tileByte = _vram[tileY * 128 + tileX] & 0xff;
            pixelData = _vram[tileByte * 64 + (py & 0x7) * 8 + (px & 0x7)];
            pixelData >>= 8;
            pixelData = pixelIsTransparent ? 0 : pixelData;
            _tilemapBuffer[0] = pixelData;
            _lastTileFetchedX[0] = x;
            _lastTileFetchedY[0] = y;
        }
        if (l == 1 && pixelData >> 7 != p)
        {
            return 0;
        }
        if (l == 1)
        {
            return pixelData & 0x7f;
        }
        return pixelData;
    }

    private int GetVramRemap() 
    {
        int adr = _vramAdr & 0x7fff;
        if (_vramRemap == 1)
        {
            adr = (adr & 0xff00) | ((adr & 0xe0) >> 5) | ((adr & 0x1f) << 3);
        }
        else if (_vramRemap == 2)
        {
            adr = (adr & 0xfe00) | ((adr & 0x1c0) >> 6) | ((adr & 0x3f) << 3);
        }
        else if (_vramRemap == 3)
        {
            adr = (adr & 0xfc00) | ((adr & 0x380) >> 7) | ((adr & 0x7f) << 3);
        }
        return adr;
    }

    private bool GetCurrentVblank()
    {
        if (_snes is null)
        {
            return false;
        }

        int vBlankStart = _snes.IsPal ? 240 : (_overscan ? 240 : 225);
        int maxV = _snes.IsPal ? 312 : 262;
        return _snes.YPos >= vBlankStart && _snes.YPos < maxV;
    }

    private bool GetCurrentHblank()
    {
        if (_snes is null)
        {
            return false;
        }

        return _snes.XPos < 4 || _snes.XPos >= 1096;
    }

    private static int Get13Signed(int val)
    {
        if ((val & 0x1000) != 0)
        {
            return -(8192 - (val & 0xfff));
        }
        return val;
    }

   private static int Get16Signed(int val) 
   {
       if ((val & 0x8000) != 0)
       {
           return -(65536 - val);
       }
       return val;
    }

    private static int GetMultResult(int a, int b) {
        b = b < 0 ? 65536 + b : b;
        b >>= 8;
        b = (b & 0x80) > 0 ? -(256 - b) : b;
        int ans = a * b;
        if (ans < 0)
        {
            return 16777216 + ans;
        }
        return ans;
    }
}
