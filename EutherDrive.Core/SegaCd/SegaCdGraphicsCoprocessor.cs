using System;

namespace EutherDrive.Core.SegaCd;

public sealed class SegaCdGraphicsCoprocessor
{
    private const uint SubCpuDivider = 12;

    private StampSizeDots _stampSize = StampSizeDots.Sixteen;
    private StampMapSizeScreens _stampMapSize = StampMapSizeScreens.One;
    private bool _stampMapRepeats;
    private uint _stampMapBaseAddress;
    private uint _imageBufferVCellSize = 1;
    private uint _imageBufferStartAddress;
    private uint _imageBufferVOffset;
    private uint _imageBufferHOffset;
    private uint _imageBufferVDotSize;
    private uint _imageBufferHDotSize;
    private uint _traceVectorBaseAddress;
    private State _state = State.Idle;
    private bool _interruptPending;

    public byte ReadRegisterByte(uint address)
    {
        switch (address & 0x1FF)
        {
            case 0x0058:
                {
                    bool inProgress = _state.Kind == StateKind.Processing;
                    return (byte)((inProgress ? 1 : 0) << 7);
                }
            case 0x0059:
                return (byte)(((_stampMapSize == StampMapSizeScreens.Sixteen ? 1 : 0) << 2)
                    | ((_stampSize == StampSizeDots.ThirtyTwo ? 1 : 0) << 1)
                    | (_stampMapRepeats ? 1 : 0));
            case 0x005A:
                return (byte)(_stampMapBaseAddress >> 10);
            case 0x005B:
                return (byte)(_stampMapBaseAddress >> 2);
            case 0x005D:
                return (byte)(_imageBufferVCellSize - 1);
            case 0x005E:
                return (byte)(_imageBufferStartAddress >> 10);
            case 0x005F:
                return (byte)(_imageBufferStartAddress >> 2);
            case 0x0061:
                return (byte)((_imageBufferVOffset << 3) | _imageBufferHOffset);
            case 0x0062:
                return (byte)((_imageBufferHDotSize >> 8) & 0xFF);
            case 0x0063:
                return (byte)(_imageBufferHDotSize & 0xFF);
            case 0x0065:
                return (byte)(_imageBufferVDotSize & 0xFF);
            default:
                return 0x00;
        }
    }

    public ushort ReadRegisterWord(uint address)
    {
        switch (address & 0x1FF)
        {
            case 0x0058:
                return (ushort)((ReadRegisterByte(address) << 8) | ReadRegisterByte(address | 1));
            case 0x005A:
                return (ushort)(_stampMapBaseAddress >> 2);
            case 0x005C:
                return ReadRegisterByte(address | 1);
            case 0x005E:
                return (ushort)(_imageBufferStartAddress >> 2);
            case 0x0060:
                return ReadRegisterByte(address | 1);
            case 0x0062:
                return (ushort)(_imageBufferHDotSize & 0x01FF);
            case 0x0064:
                return ReadRegisterByte(address | 1);
            default:
                return 0x0000;
        }
    }

    public void WriteRegisterByte(uint address, byte value)
    {
        switch (address & 0x1FF)
        {
            case 0x0059:
                _stampMapSize = (value & 0x04) != 0 ? StampMapSizeScreens.Sixteen : StampMapSizeScreens.One;
                _stampSize = (value & 0x02) != 0 ? StampSizeDots.ThirtyTwo : StampSizeDots.Sixteen;
                _stampMapRepeats = (value & 0x01) != 0;
                break;
            case 0x005A:
            case 0x005B:
                WriteRegisterWord(address & ~1u, (ushort)((value << 8) | value));
                break;
            case 0x005D:
                _imageBufferVCellSize = (uint)((value & 0x1F) + 1);
                break;
            case 0x005E:
            case 0x005F:
                WriteRegisterWord(address & ~1u, (ushort)((value << 8) | value));
                break;
            case 0x0061:
                _imageBufferVOffset = (uint)((value >> 3) & 0x07);
                _imageBufferHOffset = (uint)(value & 0x07);
                break;
            case 0x0062:
            case 0x0063:
                WriteRegisterWord(address & ~1u, (ushort)((value << 8) | value));
                break;
            case 0x0064:
            case 0x0065:
                WriteRegisterWord(address & ~1u, (ushort)((value << 8) | value));
                break;
            case 0x0066:
            case 0x0067:
                WriteRegisterWord(address & ~1u, (ushort)((value << 8) | value));
                break;
        }
    }

    public void WriteRegisterWord(uint address, ushort value)
    {
        switch (address & 0x1FF)
        {
            case 0x0058:
                WriteRegisterByte(address | 1, (byte)value);
                break;
            case 0x005A:
                _stampMapBaseAddress = (uint)(value & 0xFFE0) << 2;
                break;
            case 0x005C:
                WriteRegisterByte(address | 1, (byte)value);
                break;
            case 0x005E:
                _imageBufferStartAddress = (uint)(value & 0xFFF8) << 2;
                break;
            case 0x0060:
                WriteRegisterByte(address | 1, (byte)value);
                break;
            case 0x0062:
                _imageBufferHDotSize = (uint)(value & 0x01FF);
                break;
            case 0x0064:
                _imageBufferVDotSize = (uint)(value & 0x00FF);
                break;
            case 0x0066:
                _traceVectorBaseAddress = (uint)(value & 0xFFFE) << 2;
                uint hDot = _imageBufferHDotSize;
                uint vDot = _imageBufferVDotSize;
                uint estimatedPerLine = 4 + 2 * hDot + hDot / 4;
                uint estimated = SubCpuDivider * 3 * vDot * estimatedPerLine;
                _state = State.Processing(estimated);
                break;
        }
    }

    public void Tick(ulong mclkCycles, WordRam wordRam, bool graphicsInterruptEnabled)
    {
        if (_state.Kind != StateKind.Processing)
            return;

        if (!_state.OperationPerformed)
            PerformGraphicsOperation(wordRam);

        if (mclkCycles >= _state.CyclesRemaining)
        {
            _state = State.Idle;
            _imageBufferVDotSize = 0;
            if (graphicsInterruptEnabled)
                _interruptPending = true;
        }
        else
        {
            _state = State.Processing(_state.CyclesRemaining - (uint)mclkCycles, true);
        }
    }

    public bool InterruptPending => _interruptPending;

    public void AcknowledgeInterrupt()
    {
        _interruptPending = false;
    }

    private void PerformGraphicsOperation(WordRam wordRam)
    {
        uint stampMapDimensionPixels = OneDimensionInPixels(_stampMapSize);
        bool repeats = _stampMapRepeats;
        uint stampMapBase = StampMapBaseMasked();
        uint traceBase = _traceVectorBaseAddress;
        uint imageBufferLineSize = 8 * _imageBufferVCellSize;
        uint imageBufferStart = _imageBufferStartAddress;
        uint imageBufferLine = _imageBufferVOffset;

        for (uint line = 0; line < _imageBufferVDotSize; line++)
        {
            uint traceAddr = (traceBase + 8 * line) & WordRam.AddressMask;
            var trace = TraceVectorData.FromBytes(new[]
            {
                ReadWordRam(wordRam, traceAddr + 0),
                ReadWordRam(wordRam, traceAddr + 1),
                ReadWordRam(wordRam, traceAddr + 2),
                ReadWordRam(wordRam, traceAddr + 3),
                ReadWordRam(wordRam, traceAddr + 4),
                ReadWordRam(wordRam, traceAddr + 5),
                ReadWordRam(wordRam, traceAddr + 6),
                ReadWordRam(wordRam, traceAddr + 7),
            });

            var traceX = trace.StartX;
            var traceY = trace.StartY;

            for (uint dot = 0; dot < _imageBufferHDotSize; dot++)
            {
                uint x = traceX.IntegerPart;
                uint y = traceY.IntegerPart;
                bool outOfBounds = x >= stampMapDimensionPixels || y >= stampMapDimensionPixels;

                byte sample;
                if (!repeats && outOfBounds)
                {
                    sample = 0;
                }
                else
                {
        uint stampMapAddr = ComputeStampMapAddress(stampMapBase, _stampSize, _stampMapSize, x, y);
                    ushort stampWord = (ushort)((ReadWordRam(wordRam, stampMapAddr) << 8)
                        | ReadWordRam(wordRam, stampMapAddr + 1));
                    var stamp = StampData.FromWord(stampWord);
                    sample = SampleStamp(wordRam, stamp, _stampSize, x, y);
                }

                uint imageBufferDot = _imageBufferHOffset + dot;
                uint imageBufferAddr = imageBufferStart
                    + ComputeRelativeAddrVThenH(imageBufferLineSize, imageBufferDot, imageBufferLine);

                var nibble = (imageBufferDot & 1) != 0 ? Nibble.Low : Nibble.High;
                wordRam.GraphicsWriteRam(WordRam.SubBaseAddress | imageBufferAddr, nibble, sample);

                traceX += trace.DeltaX;
                traceY += trace.DeltaY;
            }

            imageBufferLine++;
            if (imageBufferLine == imageBufferLineSize)
            {
                imageBufferLine = 0;
                uint imageBufferSizePixels = imageBufferLineSize * 8;
                imageBufferStart = (imageBufferStart + imageBufferSizePixels / 2) & WordRam.AddressMask;
            }
        }
    }

    private uint StampMapBaseMasked()
    {
        return (_stampMapSize, _stampSize) switch
        {
            (StampMapSizeScreens.One, StampSizeDots.Sixteen) => _stampMapBaseAddress & 0x03FE00,
            (StampMapSizeScreens.One, StampSizeDots.ThirtyTwo) => _stampMapBaseAddress & 0x03FF80,
            (StampMapSizeScreens.Sixteen, StampSizeDots.Sixteen) => _stampMapBaseAddress & 0x020000,
            _ => _stampMapBaseAddress & 0x038000
        };
    }

    private static byte ReadWordRam(WordRam wordRam, uint address)
    {
        return wordRam.SubCpuReadRam(WordRam.SubBaseAddress | address);
    }

    private static uint ComputeStampMapAddress(uint baseAddr, StampSizeDots stampSize, StampMapSizeScreens mapSize, uint x, uint y)
    {
        uint stampDimensionPixels = OneDimensionInPixels(stampSize);
        uint mapDimensionPixels = OneDimensionInPixels(mapSize);
        uint stampMapX = (x & (mapDimensionPixels - 1)) / stampDimensionPixels;
        uint stampMapY = (y & (mapDimensionPixels - 1)) / stampDimensionPixels;
        uint relativeAddr = 2 * (stampMapY * mapDimensionPixels / stampDimensionPixels + stampMapX);
        return baseAddr + relativeAddr;
    }

    private static byte SampleStamp(WordRam wordRam, StampData stamp, StampSizeDots stampSize, uint x, uint y)
    {
        uint stampNumber = stampSize == StampSizeDots.Sixteen ? stamp.StampNumber : (uint)(stamp.StampNumber >> 2);
        if (stampNumber == 0)
            return 0;

        uint stampSizePixels = OneDimensionInPixels(stampSize);
        uint stampAddr = stampNumber * (stampSizePixels * stampSizePixels / 2);

        x &= stampSizePixels - 1;
        y &= stampSizePixels - 1;

        if (stamp.HorizontalFlip)
            x = FlipStampCoordinate(x, stampSizePixels);

        (x, y) = stamp.Rotation switch
        {
            StampRotation.Zero => (x, y),
            StampRotation.Ninety => (y, FlipStampCoordinate(x, stampSizePixels)),
            StampRotation.OneEighty => (FlipStampCoordinate(x, stampSizePixels), FlipStampCoordinate(y, stampSizePixels)),
            _ => (FlipStampCoordinate(y, stampSizePixels), x)
        };

        uint sampleAddr = stampAddr + ComputeRelativeAddrVThenH(stampSizePixels, x, y);
        byte b = ReadWordRam(wordRam, sampleAddr);
        return (x & 1) != 0 ? (byte)(b & 0x0F) : (byte)(b >> 4);
    }

    private static uint FlipStampCoordinate(uint coordinate, uint sizePixels)
    {
        return sizePixels - 1 - (coordinate & (sizePixels - 1));
    }

    private static uint ComputeRelativeAddrVThenH(uint vSizePixels, uint x, uint y)
    {
        uint vSizeCells = vSizePixels / 8;
        uint cellX = x / 8;
        uint cellY = y / 8;
        uint cellNumber = cellX * vSizeCells + cellY;
        uint cellAddr = 32 * cellNumber;
        uint addrInCell = 4 * (y & 0x07) + ((x & 0x07) >> 1);
        return cellAddr + addrInCell;
    }

    private enum StampSizeDots
    {
        Sixteen,
        ThirtyTwo
    }

    private enum StampMapSizeScreens
    {
        One,
        Sixteen
    }

    private enum StampRotation
    {
        Zero,
        Ninety,
        OneEighty,
        TwoSeventy
    }

    private readonly struct StampData
    {
        public ushort StampNumber { get; }
        public StampRotation Rotation { get; }
        public bool HorizontalFlip { get; }

        private StampData(ushort stampNumber, StampRotation rotation, bool horizontalFlip)
        {
            StampNumber = stampNumber;
            Rotation = rotation;
            HorizontalFlip = horizontalFlip;
        }

        public static StampData FromWord(ushort word)
        {
            bool horizontalFlip = (word & 0x8000) != 0;
            StampRotation rotation = (word & 0x6000) switch
            {
                0x0000 => StampRotation.Zero,
                0x2000 => StampRotation.Ninety,
                0x4000 => StampRotation.OneEighty,
                _ => StampRotation.TwoSeventy
            };
            ushort stampNumber = (ushort)(word & 0x07FF);
            return new StampData(stampNumber, rotation, horizontalFlip);
        }
    }

    private readonly struct TraceVectorData
    {
        public SegaCdFixedPointDecimal StartX { get; }
        public SegaCdFixedPointDecimal StartY { get; }
        public SegaCdFixedPointDecimal DeltaX { get; }
        public SegaCdFixedPointDecimal DeltaY { get; }

        private TraceVectorData(
            SegaCdFixedPointDecimal startX,
            SegaCdFixedPointDecimal startY,
            SegaCdFixedPointDecimal deltaX,
            SegaCdFixedPointDecimal deltaY)
        {
            StartX = startX;
            StartY = startY;
            DeltaX = deltaX;
            DeltaY = deltaY;
        }

        public static TraceVectorData FromBytes(byte[] bytes)
        {
            ushort startX = (ushort)((bytes[0] << 8) | bytes[1]);
            ushort startY = (ushort)((bytes[2] << 8) | bytes[3]);
            ushort deltaX = (ushort)((bytes[4] << 8) | bytes[5]);
            ushort deltaY = (ushort)((bytes[6] << 8) | bytes[7]);

            return new TraceVectorData(
                SegaCdFixedPointDecimal.FromPosition(startX),
                SegaCdFixedPointDecimal.FromPosition(startY),
                SegaCdFixedPointDecimal.FromDelta(deltaX),
                SegaCdFixedPointDecimal.FromDelta(deltaY));
        }
    }

    private enum StateKind
    {
        Idle,
        Processing
    }

    private readonly struct State
    {
        public StateKind Kind { get; }
        public uint CyclesRemaining { get; }
        public bool OperationPerformed { get; }

        private State(StateKind kind, uint cyclesRemaining, bool performed)
        {
            Kind = kind;
            CyclesRemaining = cyclesRemaining;
            OperationPerformed = performed;
        }

        public static State Idle => new(StateKind.Idle, 0, false);

        public static State Processing(uint cyclesRemaining, bool performed = false)
            => new(StateKind.Processing, cyclesRemaining, performed);
    }

    private static uint OneDimensionInPixels(StampSizeDots size)
    {
        return size == StampSizeDots.ThirtyTwo ? 32u : 16u;
    }

    private static uint OneDimensionInPixels(StampMapSizeScreens size)
    {
        return size == StampMapSizeScreens.Sixteen ? 4096u : 256u;
    }
}
