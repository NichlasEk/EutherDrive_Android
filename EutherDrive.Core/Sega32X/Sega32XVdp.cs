using EutherDrive.Core.Savestates;

namespace EutherDrive.Core.Sega32X;

internal enum Sega32XFrameBufferMode : ushort
{
    Blank = 0,
    PackedPixel = 1,
    DirectColor = 2,
    RunLength = 3,
}

internal sealed class Sega32XVdp
{
    private static readonly bool TraceState =
        string.Equals(
            Environment.GetEnvironmentVariable("EUTHERDRIVE_S32X_TRACE_VDP_STATE"),
            "1",
            StringComparison.Ordinal);
    private static readonly bool TraceFrameBufferWrites =
        string.Equals(
            Environment.GetEnvironmentVariable("EUTHERDRIVE_S32X_TRACE_FB_WRITES"),
            "1",
            StringComparison.Ordinal);
    private static readonly bool TraceRegisterWrites =
        string.Equals(
            Environment.GetEnvironmentVariable("EUTHERDRIVE_S32X_TRACE_VDP_REG_WRITES"),
            "1",
            StringComparison.Ordinal);
    public const int FrameWidth = 320;
    public const int FrameHeight = 224;
    private const int WordsPerBuffer = 0x20000 / 2;
    private const ulong MclkCyclesPerScanline = 3420;
    private const ulong HBlankStartMclkCycles = 343 * 8;
    private const ulong HBlankEndMclkCycles = HBlankStartMclkCycles - (320 * 8);
    private const ulong RenderLineMclkCycles = 26 * 8;
    private const ulong DramRefreshStartMclkCycles = HBlankStartMclkCycles;
    private const ulong DramRefreshEndMclkCycles = HBlankStartMclkCycles + ((40 * 7) / 3);
    private const int ScanlinesPerFrame = 262;
    private const int ActiveScanlinesPerFrame = 224;
    public const ulong FrameMclkCycles = MclkCyclesPerScanline * ScanlinesPerFrame;

    private readonly ushort[] _frameBuffer0 = new ushort[WordsPerBuffer];
    private readonly ushort[] _frameBuffer1 = new ushort[WordsPerBuffer];
    private readonly ushort[] _cram = new ushort[0x200 / 2];
    [NonSerialized] private readonly uint[] _renderedFrame = new uint[FrameWidth * FrameHeight];

    private bool _displayFrameBuffer;
    private bool _writeFrameBuffer = true;
    private ulong _scanlineMclk;
    private int _scanline;
    private ulong _frameAdvanceRemainder;
    private ulong _autoFillMclkRemaining;
    private ulong _cyclesTillNextRender = ulong.MaxValue;
    private readonly List<ulong> _frameBufferWriteTimingFifo = new(4);
    private ulong _lastFrameBufferWriteCycles;
    private bool _hInterruptThisLine = true;
    private ushort _hInterruptCounter;
    private ushort _latchedDisplayMode;
    private ushort _latchedScreenShift;
    private int _stateTraceCount;
    private int _frameBufferWriteTraceCount;
    private int _registerWriteTraceCount;

    public ushort DisplayMode { get; private set; }
    public ushort ScreenShift { get; private set; }
    public ushort AutoFillLength { get; private set; } = 1;
    public ushort AutoFillStartAddress { get; private set; }
    public ushort AutoFillData { get; private set; }
    public ushort FrameBufferControl { get; private set; }
    public ushort HInterruptInterval { get; private set; }
    public bool HInterruptInVBlank { get; private set; }
    public bool Priority => (DisplayMode & 0x0080) != 0;

    public void SaveState(BinaryWriter writer) => StateBinarySerializer.WriteInto(writer, this);

    public void LoadState(BinaryReader reader) => StateBinarySerializer.ReadInto(reader, this);

    public ushort ReadRegister(uint address)
    {
        return (address & 0xF) switch
        {
            0x0 => ReadDisplayMode(),
            0x2 => ScreenShift,
            0x4 => (ushort)((AutoFillLength - 1) & 0x00FF),
            0x6 => AutoFillStartAddress,
            0xA => ReadFrameBufferControl(),
            _ => 0,
        };
    }

    public void WriteRegister(uint address, ushort value)
    {
        ushort oldValue = ReadRegister(address & ~1u);
        switch (address & 0xF)
        {
            case 0x0:
                DisplayMode = (ushort)(value & 0x00C3);
                break;
            case 0x2:
                ScreenShift = (ushort)(value & 0x0001);
                break;
            case 0x4:
                AutoFillLength = (ushort)((value & 0x00FF) + 1);
                break;
            case 0x6:
                AutoFillStartAddress = value;
                break;
            case 0x8:
                AutoFillData = value;
                DoAutoFill();
                break;
            case 0xA:
                FrameBufferControl = (ushort)(value & 0x0001);
                if (InVBlank() || GetFrameBufferMode() == Sega32XFrameBufferMode.Blank)
                {
                    _displayFrameBuffer = (FrameBufferControl & 0x0001) != 0;
                    _writeFrameBuffer = !_displayFrameBuffer;
                }
                break;
        }

        TraceRegisterWriteIfEnabled(address & 0xF, oldValue, ReadRegister(address & ~1u));
    }

    public void WriteHInterruptInterval(ushort value)
    {
        HInterruptInterval = (ushort)(value & 0x00FF);
    }

    public void WriteHenBit(bool enabled)
    {
        HInterruptInVBlank = enabled;
    }

    public void Reset()
    {
        Array.Clear(_frameBuffer0);
        Array.Clear(_frameBuffer1);
        Array.Clear(_cram);
        _displayFrameBuffer = false;
        _writeFrameBuffer = true;
        _scanlineMclk = 0;
        _scanline = 0;
        _frameAdvanceRemainder = 0;
        _autoFillMclkRemaining = 0;
        _cyclesTillNextRender = ulong.MaxValue;
        _frameBufferWriteTimingFifo.Clear();
        _lastFrameBufferWriteCycles = 0;
        _hInterruptThisLine = true;
        _hInterruptCounter = 0;
        _latchedDisplayMode = 0;
        _latchedScreenShift = 0;
        Array.Clear(_renderedFrame);
        DisplayMode = 0;
        ScreenShift = 0;
        AutoFillLength = 1;
        AutoFillStartAddress = 0;
        AutoFillData = 0;
        FrameBufferControl = 0;
        HInterruptInterval = 0;
        HInterruptInVBlank = false;
        _frameBufferWriteTraceCount = 0;
        _registerWriteTraceCount = 0;
    }

    public void AdvanceFrameTiming(ulong sh2Ticks, ulong sh2TicksPerFrame, Sega32XSystemRegisters registers)
    {
        if (sh2Ticks == 0 || sh2TicksPerFrame == 0)
            return;

        ulong scaled = _frameAdvanceRemainder + (FrameMclkCycles * sh2Ticks);
        ulong mclkAdvance = scaled / sh2TicksPerFrame;
        _frameAdvanceRemainder = scaled % sh2TicksPerFrame;
        AdvanceMclk(mclkAdvance, registers);
    }

    public void AdvanceMclk(ulong mclkAdvance, Sega32XSystemRegisters registers)
    {
        if (mclkAdvance == 0)
            return;

        if (_autoFillMclkRemaining > mclkAdvance)
            _autoFillMclkRemaining -= mclkAdvance;
        else
            _autoFillMclkRemaining = 0;

        ulong remaining = mclkAdvance;
        while (remaining > 0)
        {
            ulong chunk = remaining;
            if ((_hInterruptThisLine || HInterruptInVBlank) && _scanlineMclk < HBlankStartMclkCycles)
            {
                ulong tillHBlank = HBlankStartMclkCycles - _scanlineMclk;
                if (tillHBlank < chunk)
                    chunk = tillHBlank;
            }
            else
            {
                _hInterruptCounter = HInterruptInterval;
            }

            ulong tillScanlineEnd = MclkCyclesPerScanline - _scanlineMclk;
            if (tillScanlineEnd < chunk)
                chunk = tillScanlineEnd;

            ulong prevScanlineMclk = _scanlineMclk;
            _scanlineMclk += chunk;
            remaining -= chunk;

            if ((_hInterruptThisLine || HInterruptInVBlank)
                && prevScanlineMclk < HBlankStartMclkCycles
                && _scanlineMclk >= HBlankStartMclkCycles)
            {
                HandleHBlankStart(registers);
            }

            if (_scanlineMclk >= MclkCyclesPerScanline)
            {
                _scanlineMclk -= MclkCyclesPerScanline;
                _scanline++;

                if (_scanline == ActiveScanlinesPerFrame)
                {
                    _displayFrameBuffer = (FrameBufferControl & 0x0001) != 0;
                    _writeFrameBuffer = !_displayFrameBuffer;
                    registers.NotifyVBlankStart();
                    _hInterruptThisLine = false;
                }
                else if (_scanline >= ScanlinesPerFrame)
                {
                    _scanline = 0;
                    registers.NotifyVBlankEnd();
                }
                else if (_scanline == ScanlinesPerFrame - 1)
                {
                    _hInterruptThisLine = true;
                }

                if (_scanline < ActiveScanlinesPerFrame && _scanlineMclk >= RenderLineMclkCycles)
                {
                    RenderScanline(_scanline);
                    _cyclesTillNextRender = MclkCyclesPerScanline + RenderLineMclkCycles - _scanlineMclk;
                }
            }
            else if (prevScanlineMclk < RenderLineMclkCycles && _scanlineMclk >= RenderLineMclkCycles)
            {
                if (_scanline < ActiveScanlinesPerFrame)
                {
                    RenderScanline(_scanline);
                    _cyclesTillNextRender = MclkCyclesPerScanline + RenderLineMclkCycles - _scanlineMclk;
                }
            }
        }
    }

    public ulong MclkCyclesUntilNextEvent(bool hInterruptEnabled)
    {
        ulong cyclesTillNext = _cyclesTillNextRender;

        if (hInterruptEnabled && _hInterruptCounter == 0 && _scanlineMclk < HBlankStartMclkCycles)
        {
            cyclesTillNext = Math.Min(cyclesTillNext, HBlankStartMclkCycles - _scanlineMclk);
        }

        if (_autoFillMclkRemaining != 0)
        {
            cyclesTillNext = Math.Min(cyclesTillNext, _autoFillMclkRemaining);
        }

        if (cyclesTillNext == ulong.MaxValue)
        {
            ulong toRender = _scanlineMclk < RenderLineMclkCycles
                ? RenderLineMclkCycles - _scanlineMclk
                : (MclkCyclesPerScanline - _scanlineMclk) + RenderLineMclkCycles;
            cyclesTillNext = toRender == 0 ? 1UL : toRender;
        }

        return cyclesTillNext == 0 ? 1UL : cyclesTillNext;
    }

    public ushort ReadFrameBufferWord(uint address)
    {
        ushort[] frameBuffer = GetWriteBuffer();
        return frameBuffer[((address & 0x1FFFF) >> 1) % frameBuffer.Length];
    }

    public ulong FrameBufferWriteLatency(ulong cycles)
    {
        if (cycles <= _lastFrameBufferWriteCycles)
            return 1;

        ulong cycleDiff = cycles - _lastFrameBufferWriteCycles;
        while (cycleDiff != 0 && _frameBufferWriteTimingFifo.Count != 0)
        {
            ulong remaining = _frameBufferWriteTimingFifo[0];
            if (cycleDiff < remaining)
            {
                _frameBufferWriteTimingFifo[0] = remaining - cycleDiff;
                cycleDiff = 0;
                break;
            }

            cycleDiff -= remaining;
            _frameBufferWriteTimingFifo.RemoveAt(0);
        }

        ulong initialWriteTime = cycles - _lastFrameBufferWriteCycles switch
        {
            1 => 3,
            2 => 2,
            _ => 1,
        };

        ulong fifoWaitTime = 0;
        if (_frameBufferWriteTimingFifo.Count == 4)
        {
            fifoWaitTime = 1 + _frameBufferWriteTimingFifo[0];
            _frameBufferWriteTimingFifo.RemoveAt(0);
        }

        ulong waitCycles = Math.Max(initialWriteTime, fifoWaitTime);
        _frameBufferWriteTimingFifo.Add(5);
        _lastFrameBufferWriteCycles = cycles + waitCycles - 1;
        return waitCycles;
    }

    public void WriteFrameBufferByte(uint address, byte value, bool overwrite = false)
    {
        // Match jgenesis/hardware semantics: zero byte writes are ignored for both
        // normal frame buffer writes and overwrite-image writes.
        if (value == 0)
            return;

        ushort[] frameBuffer = GetWriteBuffer();
        int index = (int)(((address & 0x1FFFF) >> 1) % frameBuffer.Length);
        ushort current = frameBuffer[index];
        ushort merged = (address & 1) == 0
            ? (ushort)((current & 0x00FF) | (value << 8))
            : (ushort)((current & 0xFF00) | value);
        frameBuffer[index] = merged;
        TraceFrameBufferWriteIfEnabled("byte", address, merged, frameBuffer);
    }

    public void WriteFrameBufferWord(uint address, ushort value)
    {
        ushort[] frameBuffer = GetWriteBuffer();
        int index = (int)(((address & 0x1FFFF) >> 1) % frameBuffer.Length);
        frameBuffer[index] = value;
        TraceFrameBufferWriteIfEnabled("word", address, value, frameBuffer);
    }

    public void OverwriteFrameBufferWord(uint address, ushort value)
    {
        ushort[] frameBuffer = GetWriteBuffer();
        int index = (int)(((address & 0x1FFFF) >> 1) % frameBuffer.Length);
        ushort current = frameBuffer[index];
        byte msb = (byte)(value >> 8);
        byte lsb = (byte)value;
        if (msb != 0)
            current = (ushort)((current & 0x00FF) | (msb << 8));
        if (lsb != 0)
            current = (ushort)((current & 0xFF00) | lsb);
        frameBuffer[index] = current;
        TraceFrameBufferWriteIfEnabled("ovr", address, current, frameBuffer);
    }

    public ushort ReadCramWord(uint address)
    {
        return _cram[((address & 0x1FF) >> 1) % _cram.Length];
    }

    public void WriteCramWord(uint address, ushort value)
    {
        _cram[((address & 0x1FF) >> 1) % _cram.Length] = value;
    }

    public void RenderBgra(byte[] output, int stride)
    {
        Array.Clear(output);

        Sega32XFrameBufferMode mode = GetLatchedFrameBufferMode();
        if (mode == Sega32XFrameBufferMode.Blank)
            return;

        TraceVdpStateIfEnabled(mode, GetDisplayBuffer(), "render");
        RenderRenderedFrameBgra(output, stride);
    }

    public void DebugRenderOtherBufferBgra(byte[] output, int stride)
    {
        ushort[] displayBuffer = GetDisplayBuffer();
        ushort[] otherBuffer = ReferenceEquals(displayBuffer, _frameBuffer0) ? _frameBuffer1 : _frameBuffer0;
        RenderBgraBuffer(output, stride, otherBuffer, tracePhase: null);
    }

    private void RenderBgraBuffer(byte[] output, int stride, ushort[] frameBuffer, string? tracePhase)
    {
        Array.Clear(output);

        Sega32XFrameBufferMode mode = GetLatchedFrameBufferMode();
        if (mode == Sega32XFrameBufferMode.Blank)
            return;

        if (!string.IsNullOrEmpty(tracePhase))
            TraceVdpStateIfEnabled(mode, frameBuffer, tracePhase);
        for (int y = 0; y < FrameHeight; y++)
        {
            int row = y * stride;
            switch (mode)
            {
                case Sega32XFrameBufferMode.PackedPixel:
                    RenderPackedLine(output, row, frameBuffer, y);
                    break;
                case Sega32XFrameBufferMode.DirectColor:
                    RenderDirectColorLine(output, row, frameBuffer, y);
                    break;
                case Sega32XFrameBufferMode.RunLength:
                    RenderRunLengthLine(output, row, frameBuffer, y);
                    break;
            }
        }
    }

    private void RenderRenderedFrameBgra(byte[] output, int stride)
    {
        for (int y = 0; y < FrameHeight; y++)
        {
            int row = y * stride;
            int sourceRow = y * FrameWidth;
            for (int x = 0; x < FrameWidth; x++)
                WritePixel(output, row + (x * 4), 0xFF00_0000u | (_renderedFrame[sourceRow + x] & 0x00FF_FFFFu));
        }
    }

    public bool CompositeBgraOver(byte[] output, int stride)
    {
        if (output.Length == 0 || stride <= 0)
            return false;

        Sega32XFrameBufferMode mode = GetLatchedFrameBufferMode();
        if (mode == Sega32XFrameBufferMode.Blank)
            return false;

        TraceVdpStateIfEnabled(mode, GetDisplayBuffer(), "composite");
        bool wroteAnyPixel = false;
        int rowBytes = FrameWidth * 4;
        bool outputFullyCoversFrame = output.Length >= (((FrameHeight - 1) * stride) + rowBytes);

        for (int y = 0; y < FrameHeight; y++)
        {
            int row = y * stride;
            int sourceRow = y * FrameWidth;
            for (int x = 0; x < FrameWidth; x++)
            {
                uint pixel = _renderedFrame[sourceRow + x];
                bool use32xPixel = (pixel & 0x8000_0000u) != 0;
                int offset = row + (x * 4);
                if (!outputFullyCoversFrame && (uint)(offset + 3) >= output.Length)
                    continue;

                bool mdHasVisiblePixel = output[offset + 3] != 0;
                bool mdPixelIsBlack = output[offset] == 0
                    && output[offset + 1] == 0
                    && output[offset + 2] == 0;

                // The MD presentation path currently emits opaque black for "empty" pixels.
                // Until Genesis transparency is preserved through compositing, treat opaque black
                // as transparent for low-priority 32X pixels so pure-32X scenes can appear.
                if (!use32xPixel && mdHasVisiblePixel && !mdPixelIsBlack)
                    continue;

                WritePixel(output, offset, 0xFF00_0000u | (pixel & 0x00FF_FFFFu));
                wroteAnyPixel = true;
            }
        }

        return wroteAnyPixel;
    }

    private void RenderScanline(int line)
    {
        int row = line * FrameWidth;
        if ((uint)line >= ActiveScanlinesPerFrame)
            return;

        Sega32XFrameBufferMode mode = GetLatchedFrameBufferMode();
        if (mode == Sega32XFrameBufferMode.Blank)
        {
            Array.Clear(_renderedFrame, row, FrameWidth);
            return;
        }

        ushort[] frameBuffer = GetDisplayBuffer();
        for (int x = 0; x < FrameWidth; x++)
            _renderedFrame[row + x] = GetRenderedPixel(mode, frameBuffer, line, x);
    }

    private void RenderPackedLine(byte[] output, int row, ushort[] frameBuffer, int line)
    {
        ushort lineAddress = frameBuffer[line % frameBuffer.Length];
        if ((_latchedScreenShift & 0x0001) != 0)
        {
            for (int x = 0; x < FrameWidth; x++)
            {
                int sourcePixel = x + 1;
                ushort word = frameBuffer[(lineAddress + (sourcePixel >> 1)) % frameBuffer.Length];
                int paletteIndex = ((sourcePixel & 1) == 0) ? ((word >> 8) & 0xFF) : (word & 0xFF);
                WritePixel(output, row + (x * 4), ToBgra(_cram[paletteIndex]));
            }
            return;
        }

        for (int x = 0; x < FrameWidth; x += 2)
        {
            ushort word = frameBuffer[(lineAddress + (x >> 1)) % frameBuffer.Length];
            WritePixel(output, row + (x * 4), ToBgra(_cram[(word >> 8) & 0xFF]));
            WritePixel(output, row + ((x + 1) * 4), ToBgra(_cram[word & 0xFF]));
        }
    }

    private uint GetRenderedPixel(Sega32XFrameBufferMode mode, ushort[] frameBuffer, int line, int x)
    {
        return mode switch
        {
            Sega32XFrameBufferMode.PackedPixel => GetPackedPixel(frameBuffer, line, x),
            Sega32XFrameBufferMode.DirectColor => GetDirectColorPixel(frameBuffer, line, x),
            Sega32XFrameBufferMode.RunLength => GetRunLengthPixel(frameBuffer, line, x),
            _ => 0,
        };
    }

    private uint GetPackedPixel(ushort[] frameBuffer, int line, int x)
    {
        ushort lineAddress = frameBuffer[line % frameBuffer.Length];
        int paletteIndex;
        if ((_latchedScreenShift & 0x0001) != 0)
        {
            int sourcePixel = x + 1;
            ushort word = frameBuffer[(lineAddress + (sourcePixel >> 1)) % frameBuffer.Length];
            paletteIndex = ((sourcePixel & 1) == 0) ? ((word >> 8) & 0xFF) : (word & 0xFF);
        }
        else
        {
            ushort packedWord = frameBuffer[(lineAddress + (x >> 1)) % frameBuffer.Length];
            paletteIndex = (x & 1) == 0 ? ((packedWord >> 8) & 0xFF) : (packedWord & 0xFF);
        }

        ushort color = _cram[paletteIndex];
        return BuildRenderedPixel(color);
    }

    private uint GetDirectColorPixel(ushort[] frameBuffer, int line, int x)
    {
        ushort lineAddress = frameBuffer[line % frameBuffer.Length];
        ushort color = frameBuffer[(lineAddress + x) % frameBuffer.Length];
        
        return BuildRenderedPixel(color);
    }

    private uint GetRunLengthPixel(ushort[] frameBuffer, int line, int x)
    {
        int pixel = 0;
        int readIndex = frameBuffer[line % frameBuffer.Length];
        while (pixel < FrameWidth)
        {
            ushort word = frameBuffer[readIndex % frameBuffer.Length];
            readIndex++;
            int runLength = ((word >> 8) & 0xFF) + 1;
            int paletteIndex = word & 0xFF;
            if (x < pixel + runLength)
                return BuildRenderedPixel(_cram[paletteIndex]);
            pixel += runLength;
        }

        return 0;
    }

    private uint BuildRenderedPixel(ushort color)
    {
        ushort priorityColor = ApplyPriorityMask(color);
        uint rgb = ToBgra(priorityColor) & 0x00FF_FFFFu;
        uint flags = ((priorityColor & 0x8000u) != 0) ? 0x8000_0000u : 0u;
        return rgb | flags;
    }

    private ushort ApplyPriorityMask(ushort pixel)
    {
        if ((_latchedDisplayMode & 0x0080) != 0)
            return (ushort)(pixel ^ 0x8000);
        return pixel;
    }

    private void RenderDirectColorLine(byte[] output, int row, ushort[] frameBuffer, int line)
    {
        // Line addresses are stored at the beginning of the frame buffer (first 256 words)
        ushort lineAddress = frameBuffer[line & 0xFF];
        for (int x = 0; x < FrameWidth; x++)
        {
            ushort color = frameBuffer[(lineAddress + x) & 0xFFFF];
            WritePixel(output, row + (x * 4), ToBgra(color));
        }
    }

    private void RenderRunLengthLine(byte[] output, int row, ushort[] frameBuffer, int line)
    {
        int x = 0;
        // Line addresses are stored at the beginning of the frame buffer (first 256 words)
        int readIndex = frameBuffer[line & 0xFF];
        while (x < FrameWidth)
        {
            ushort word = frameBuffer[readIndex & 0xFFFF];
            readIndex++;
            int runLength = ((word >> 8) & 0xFF) + 1;
            uint bgra = ToBgra(_cram[word & 0xFF]);
            while (x < FrameWidth && runLength-- > 0)
            {
                WritePixel(output, row + (x * 4), bgra);
                x++;
            }
        }
    }

    private void DoAutoFill()
    {
        ushort[] frameBuffer = GetWriteBuffer();
        int index = AutoFillStartAddress % frameBuffer.Length;
        for (int i = 0; i < AutoFillLength; i++)
        {
            frameBuffer[index] = AutoFillData;
            index = (index & 0xFF00) | ((index + 1) & 0x00FF);
        }

        AutoFillStartAddress = (ushort)index;

        ulong autoFillSclk = 7 + (3 * (ulong)AutoFillLength);
        _autoFillMclkRemaining = (autoFillSclk * 7) / 3;
    }

    private Sega32XFrameBufferMode GetFrameBufferMode() => (Sega32XFrameBufferMode)(DisplayMode & 0x0003);

    private Sega32XFrameBufferMode GetLatchedFrameBufferMode() => (Sega32XFrameBufferMode)(_latchedDisplayMode & 0x0003);

    private ushort ReadDisplayMode()
    {
        return (ushort)(0x8000 | (Priority ? 0x0080 : 0) | (DisplayMode & 0x0043));
    }

    private ushort ReadFrameBufferControl()
    {
        bool inVBlank = InVBlank();
        bool inHBlank = InHBlank();
        bool cramAccessible = inVBlank || inHBlank;
        bool frameBufferBusy = _autoFillMclkRemaining != 0
            || (_scanlineMclk >= DramRefreshStartMclkCycles && _scanlineMclk < DramRefreshEndMclkCycles);

        return (ushort)((inVBlank ? 1 << 15 : 0)
            | (inHBlank ? 1 << 14 : 0)
            | (cramAccessible ? 1 << 13 : 0)
            | (frameBufferBusy ? 1 << 1 : 0)
            | (_displayFrameBuffer ? 1 : 0));
    }

    private bool InVBlank() => _scanline >= ActiveScanlinesPerFrame;

    private bool InHBlank() => !(_scanlineMclk >= HBlankEndMclkCycles && _scanlineMclk < HBlankStartMclkCycles);

    private ushort[] GetDisplayBuffer() => _displayFrameBuffer ? _frameBuffer1 : _frameBuffer0;

    private ushort[] GetWriteBuffer() => _writeFrameBuffer ? _frameBuffer1 : _frameBuffer0;

    private void HandleHBlankStart(Sega32XSystemRegisters registers)
    {
        _latchedDisplayMode = DisplayMode;
        _latchedScreenShift = ScreenShift;

        if (GetLatchedFrameBufferMode() == Sega32XFrameBufferMode.Blank)
            _displayFrameBuffer = (FrameBufferControl & 0x0001) != 0;

        if (_hInterruptCounter == 0)
        {
            _hInterruptCounter = HInterruptInterval;
            registers.NotifyHInterrupt();
        }
        else
        {
            _hInterruptCounter--;
        }
    }

    private static readonly uint[] ColorLookupTable = BuildColorLookupTable();

    private static uint[] BuildColorLookupTable()
    {
        uint[] table = new uint[32768];
        for (int i = 0; i < 32768; i++)
        {
            int r = (i >> 0) & 0x1F;
            int g = (i >> 5) & 0x1F;
            int b = (i >> 10) & 0x1F;
            byte rb = (byte)((r << 3) | (r >> 2));
            byte gb = (byte)((g << 3) | (g >> 2));
            byte bb = (byte)((b << 3) | (b >> 2));
            table[i] = 0xFF000000u | ((uint)rb << 16) | ((uint)gb << 8) | bb;
        }
        return table;
    }

    private static uint ToBgra(ushort color) => ColorLookupTable[color & 0x7FFF];

    private static void WritePixel(byte[] output, int offset, uint bgra)
    {
        output[offset] = (byte)bgra;
        output[offset + 1] = (byte)(bgra >> 8);
        output[offset + 2] = (byte)(bgra >> 16);
        output[offset + 3] = (byte)(bgra >> 24);
    }

    private void TraceVdpStateIfEnabled(Sega32XFrameBufferMode mode, ushort[] frameBuffer, string phase)
    {
        if (!TraceState || _stateTraceCount >= 4)
            return;

        _stateTraceCount++;
        int lineCount = Math.Min(8, FrameHeight);
        string[] lines = new string[lineCount];
        for (int i = 0; i < lineCount; i++)
            lines[i] = $"L{i}=0x{frameBuffer[i]:X4}";

        int sampleCount = Math.Min(16, frameBuffer.Length);
        string[] words = new string[sampleCount];
        for (int i = 0; i < sampleCount; i++)
            words[i] = $"{frameBuffer[i]:X4}";

        int line0Addr = frameBuffer[0] % frameBuffer.Length;
        int line0SampleCount = Math.Min(16, frameBuffer.Length - line0Addr);
        string[] line0Words = new string[line0SampleCount];
        for (int i = 0; i < line0SampleCount; i++)
            line0Words[i] = $"{frameBuffer[line0Addr + i]:X4}";

        int cramSampleCount = Math.Min(16, _cram.Length);
        string[] cramWords = new string[cramSampleCount];
        for (int i = 0; i < cramSampleCount; i++)
            cramWords[i] = $"{_cram[i]:X4}";

        ushort[] otherBuffer = ReferenceEquals(frameBuffer, _frameBuffer0) ? _frameBuffer1 : _frameBuffer0;
        int otherLineCount = Math.Min(8, FrameHeight);
        string[] otherLines = new string[otherLineCount];
        for (int i = 0; i < otherLineCount; i++)
            otherLines[i] = $"W{i}=0x{otherBuffer[i]:X4}";

        int otherLine0Addr = otherBuffer[0] % otherBuffer.Length;
        int otherLine0SampleCount = Math.Min(16, otherBuffer.Length - otherLine0Addr);
        string[] otherLine0Words = new string[otherLine0SampleCount];
        for (int i = 0; i < otherLine0SampleCount; i++)
            otherLine0Words[i] = $"{otherBuffer[otherLine0Addr + i]:X4}";

        EmitTraceLine(
            $"[S32X-VDP-{phase}] mode={mode} disp=0x{DisplayMode:X4} latched=0x{_latchedDisplayMode:X4} " +
            $"shift=0x{ScreenShift:X4} latchedShift=0x{_latchedScreenShift:X4} " +
            $"fbctl=0x{FrameBufferControl:X4} front={(_displayFrameBuffer ? 1 : 0)} write={(_writeFrameBuffer ? 1 : 0)} " +
            $"scanline={_scanline} mclk={_scanlineMclk} priority={(Priority ? 1 : 0)} " +
            $"{string.Join(' ', lines)} words={string.Join(' ', words)} " +
            $"line0@0x{line0Addr:X4}={string.Join(' ', line0Words)} cram={string.Join(' ', cramWords)} " +
            $"{string.Join(' ', otherLines)} other0@0x{otherLine0Addr:X4}={string.Join(' ', otherLine0Words)}");
    }

    private static void EmitTraceLine(string line)
    {
        Console.WriteLine(line);
        string? traceFilePath = Environment.GetEnvironmentVariable("EUTHERDRIVE_S32X_TRACE_FILE");
        if (!string.IsNullOrWhiteSpace(traceFilePath))
            System.IO.File.AppendAllText(traceFilePath, line + Environment.NewLine);
    }

    private void TraceFrameBufferWriteIfEnabled(string kind, uint address, ushort value, ushort[] targetBuffer)
    {
        if (!TraceFrameBufferWrites || _frameBufferWriteTraceCount >= 192)
            return;

        uint masked = address & 0x1FFFF;
        if (masked >= 0x0400 && (masked < 0x1000 || masked >= 0x2000))
            return;

        _frameBufferWriteTraceCount++;
        string target = ReferenceEquals(targetBuffer, _frameBuffer0) ? "fb0" : "fb1";
        EmitTraceLine(
            $"[S32X-FBWRITE] kind={kind} addr=0x{masked:X5} word=0x{((masked >> 1) & 0xFFFF):X4} value=0x{value:X4} target={target} disp={( _displayFrameBuffer ? 1 : 0)} write={( _writeFrameBuffer ? 1 : 0)}");
    }

    private void TraceRegisterWriteIfEnabled(uint registerOffset, ushort oldValue, ushort newValue)
    {
        if (!TraceRegisterWrites || _registerWriteTraceCount >= 128 || oldValue == newValue)
            return;

        // Focus on mode/swap state changes; autofill traffic is too noisy to be useful.
        if (registerOffset != 0x0 && registerOffset != 0x2 && registerOffset != 0xA)
            return;

        _registerWriteTraceCount++;
        EmitTraceLine(
            $"[S32X-VDPREG] reg=0x{registerOffset:X1} old=0x{oldValue:X4} new=0x{newValue:X4} " +
            $"disp=0x{DisplayMode:X4} fbctl=0x{FrameBufferControl:X4} front={(_displayFrameBuffer ? 1 : 0)} write={(_writeFrameBuffer ? 1 : 0)} " +
            $"scanline={_scanline} mclk={_scanlineMclk}");
    }
}
