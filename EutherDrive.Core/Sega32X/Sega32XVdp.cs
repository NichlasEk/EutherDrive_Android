namespace EutherDrive.Core.Sega32X;

internal enum Sega32XFrameBufferMode : ushort
{
    PackedPixel = 0,
    DirectColor = 1,
    RunLength = 2,
    Blank = 3,
}

internal sealed class Sega32XVdp
{
    public const int FrameWidth = 320;
    public const int FrameHeight = 224;
    private const int WordsPerBuffer = 0x10000 / 2;

    private readonly ushort[] _frameBuffer0 = new ushort[WordsPerBuffer];
    private readonly ushort[] _frameBuffer1 = new ushort[WordsPerBuffer];
    private readonly ushort[] _cram = new ushort[0x200 / 2];

    private bool _displayFrameBuffer;
    private bool _writeFrameBuffer;

    public ushort DisplayMode { get; private set; }
    public ushort ScreenShift { get; private set; }
    public ushort AutoFillLength { get; private set; } = 1;
    public ushort AutoFillStartAddress { get; private set; }
    public ushort AutoFillData { get; private set; }
    public ushort FrameBufferControl { get; private set; }

    public ushort ReadRegister(uint address)
    {
        return (address & 0xF) switch
        {
            0x0 => DisplayMode,
            0x2 => ScreenShift,
            0x4 => (ushort)((AutoFillLength - 1) & 0x00FF),
            0x6 => AutoFillStartAddress,
            0xA => FrameBufferControl,
            _ => 0,
        };
    }

    public void WriteRegister(uint address, ushort value)
    {
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
                _displayFrameBuffer = (FrameBufferControl & 0x0001) != 0;
                _writeFrameBuffer = !_displayFrameBuffer;
                break;
        }
    }

    public ushort ReadFrameBufferWord(uint address)
    {
        ushort[] frameBuffer = GetWriteBuffer();
        return frameBuffer[((address & 0x1FFFF) >> 1) % frameBuffer.Length];
    }

    public void WriteFrameBufferWord(uint address, ushort value)
    {
        ushort[] frameBuffer = GetWriteBuffer();
        frameBuffer[((address & 0x1FFFF) >> 1) % frameBuffer.Length] = value;
    }

    public void OverwriteFrameBufferWord(uint address, ushort value)
    {
        if (value == 0)
            return;

        ushort[] frameBuffer = GetWriteBuffer();
        int index = (int)(((address & 0x1FFFF) >> 1) % frameBuffer.Length);
        ushort current = frameBuffer[index];
        byte high = (byte)(value >> 8);
        byte low = (byte)value;
        if (high != 0)
            current = (ushort)((current & 0x00FF) | (high << 8));
        if (low != 0)
            current = (ushort)((current & 0xFF00) | low);
        frameBuffer[index] = current;
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

        Sega32XFrameBufferMode mode = GetFrameBufferMode();
        if (mode == Sega32XFrameBufferMode.Blank)
            return;

        ushort[] frameBuffer = GetDisplayBuffer();
        for (int y = 0; y < FrameHeight; y++)
        {
            int row = y * stride;
            int lineBase = y * (FrameWidth / 2);
            switch (mode)
            {
                case Sega32XFrameBufferMode.PackedPixel:
                    RenderPackedLine(output, row, frameBuffer, lineBase);
                    break;
                case Sega32XFrameBufferMode.DirectColor:
                    RenderDirectColorLine(output, row, frameBuffer, y * FrameWidth);
                    break;
                case Sega32XFrameBufferMode.RunLength:
                    RenderRunLengthLine(output, row, frameBuffer, lineBase);
                    break;
            }
        }
    }

    private void RenderPackedLine(byte[] output, int row, ushort[] frameBuffer, int lineBase)
    {
        for (int x = 0; x < FrameWidth; x += 2)
        {
            ushort word = frameBuffer[(lineBase + (x >> 1)) % frameBuffer.Length];
            WritePixel(output, row + (x * 4), ToBgra(_cram[(word >> 8) & 0xFF]));
            WritePixel(output, row + ((x + 1) * 4), ToBgra(_cram[word & 0xFF]));
        }
    }

    private void RenderDirectColorLine(byte[] output, int row, ushort[] frameBuffer, int lineBase)
    {
        for (int x = 0; x < FrameWidth; x++)
        {
            ushort color = frameBuffer[(lineBase + x) % frameBuffer.Length];
            WritePixel(output, row + (x * 4), ToBgra(color));
        }
    }

    private void RenderRunLengthLine(byte[] output, int row, ushort[] frameBuffer, int lineBase)
    {
        int x = 0;
        int readIndex = lineBase;
        while (x < FrameWidth)
        {
            ushort word = frameBuffer[readIndex % frameBuffer.Length];
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
            index = (index + 1) % frameBuffer.Length;
        }
    }

    private Sega32XFrameBufferMode GetFrameBufferMode() => (Sega32XFrameBufferMode)(DisplayMode & 0x0003);

    private ushort[] GetDisplayBuffer() => _displayFrameBuffer ? _frameBuffer1 : _frameBuffer0;

    private ushort[] GetWriteBuffer() => _writeFrameBuffer ? _frameBuffer1 : _frameBuffer0;

    private static uint ToBgra(ushort color)
    {
        int r = (color >> 0) & 0x1F;
        int g = (color >> 5) & 0x1F;
        int b = (color >> 10) & 0x1F;
        byte rb = (byte)((r << 3) | (r >> 2));
        byte gb = (byte)((g << 3) | (g >> 2));
        byte bb = (byte)((b << 3) | (b >> 2));
        return 0xFF000000u | ((uint)rb << 16) | ((uint)gb << 8) | bb;
    }

    private static void WritePixel(byte[] output, int offset, uint bgra)
    {
        if ((uint)(offset + 3) >= output.Length)
            return;

        output[offset] = (byte)(bgra & 0xFF);
        output[offset + 1] = (byte)((bgra >> 8) & 0xFF);
        output[offset + 2] = (byte)((bgra >> 16) & 0xFF);
        output[offset + 3] = 0xFF;
    }
}
