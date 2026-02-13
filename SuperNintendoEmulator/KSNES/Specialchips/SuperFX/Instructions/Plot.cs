using System;

namespace KSNES.Specialchips.SuperFX;

internal static class Plot
{
    public static byte Cmode(MemoryType memoryType, GraphicsSupportUnit gsu)
    {
        ushort source = Instructions.ReadRegister(gsu, gsu.SReg);

        gsu.PlotTransparentPixels = source.Bit(0);
        gsu.DitherOn = source.Bit(1);
        gsu.PorHighNibbleFlag = source.Bit(2);
        gsu.PorFreezeHighNibble = source.Bit(3);
        gsu.ForceObjMode = source.Bit(4);

        Instructions.ClearPrefixFlags(gsu);
        return memoryType.AccessCycles(gsu.ClockSpeed);
    }

    public static byte Color(MemoryType memoryType, GraphicsSupportUnit gsu)
    {
        ushort source = Instructions.ReadRegister(gsu, gsu.SReg);
        gsu.Color = MaskColor((byte)source, gsu);

        Instructions.ClearPrefixFlags(gsu);
        return memoryType.AccessCycles(gsu.ClockSpeed);
    }

    public static byte Getc(MemoryType memoryType, GraphicsSupportUnit gsu)
    {
        byte value = gsu.State.RomBuffer;
        gsu.Color = MaskColor(value, gsu);

        byte cycles = gsu.State.RomBufferWaitCycles;
        gsu.State.RomBufferWaitCycles = 0;

        Instructions.ClearPrefixFlags(gsu);
        return (byte)(cycles + memoryType.AccessCycles(gsu.ClockSpeed));
    }

    public static byte PlotPixel(MemoryType memoryType, GraphicsSupportUnit gsu, byte[] ram)
    {
        byte x = (byte)gsu.R[1];
        byte y = (byte)gsu.R[2];

        int cycles = 0;

        byte coarseX = (byte)(x & ~0x07);
        if ((coarseX != gsu.PlotState.LastCoarseX || y != gsu.PlotState.LastY)
            && gsu.PlotState.PixelBuffer.AnyValid())
        {
            cycles += FlushPixelBuffer(gsu, ram);
        }

        gsu.PlotState.LastCoarseX = coarseX;
        gsu.PlotState.LastY = y;

        byte color = gsu.DitherOn && ((x.Bit(0) ^ y.Bit(0)))
            ? (byte)(gsu.Color >> 4)
            : gsu.Color;

        bool isTransparent = gsu.PorFreezeHighNibble
            ? (color & 0x0F & gsu.ColorGradient.ColorMask()) == 0
            : (color & gsu.ColorGradient.ColorMask()) == 0;

        if (gsu.PlotTransparentPixels || !isTransparent)
        {
            byte i = (byte)(x & 0x07);
            gsu.PlotState.PixelBuffer.WritePixel(i, color);

            if (gsu.PlotState.PixelBuffer.AllValid())
            {
                cycles += FlushPixelBuffer(gsu, ram);
            }
        }

        gsu.R[1] = unchecked((ushort)(gsu.R[1] + 1));

        Instructions.ClearPrefixFlags(gsu);
        int access = memoryType.AccessCycles(gsu.ClockSpeed);
        return (byte)Math.Max(access, cycles);
    }

    public static byte Rpix(MemoryType memoryType, GraphicsSupportUnit gsu, byte[] rom, byte[] ram)
    {
        uint bitplanes = gsu.ColorGradient.Bitplanes();
        int cycles = (int)bitplanes * gsu.ClockSpeed.MemoryAccessCycles();
        if (memoryType != MemoryType.CodeCache)
        {
            cycles += 4;
        }

        if (!gsu.PlotState.PixelBuffer.AnyValid() || gsu.PlotState.PixelBuffer.AllValid())
        {
            cycles += gsu.ClockSpeed == ClockSpeed.Slow
                ? 7 * (int)bitplanes - (int)bitplanes / 2
                : 10 * (int)bitplanes;
        }

        if (gsu.PlotState.PixelBuffer.AnyValid())
        {
            cycles += FlushPixelBuffer(gsu, ram);
        }

        cycles += gsu.PlotState.FlushCyclesRemaining;
        gsu.PlotState.FlushCyclesRemaining = 0;

        byte x = (byte)gsu.R[1];
        byte y = (byte)gsu.R[2];

        int tileAddr = ComputeTileAddr(gsu, x, y, ram.Length);
        uint tileSize = gsu.ColorGradient.TileSize();
        byte[] tileData = ram;

        byte row = (byte)(y & 0x07);
        uint lineBaseAddr = (uint)(row * 0x02);

        byte pixelIdx = (byte)(x & 0x07);
        byte bitplaneIdx = (byte)(7 - pixelIdx);

        byte color = 0;
        for (uint plane = 0; plane < bitplanes; plane += 2)
        {
            int planeAddr = tileAddr + (int)(lineBaseAddr + 8 * plane);
            color |= (byte)((tileData[planeAddr].Bit(bitplaneIdx) ? 1 : 0) << (int)plane);
            color |= (byte)((tileData[planeAddr + 1].Bit(bitplaneIdx) ? 1 : 0) << (int)(plane + 1));
        }

        cycles += Instructions.WriteRegister(gsu, gsu.DReg, color, rom, ram);

        gsu.ZeroFlag = color == 0;
        gsu.SignFlag = color.SignBit();

        Instructions.ClearPrefixFlags(gsu);
        return (byte)cycles;
    }

    private static byte FlushPixelBuffer(GraphicsSupportUnit gsu, byte[] ram)
    {
        byte x = gsu.PlotState.LastCoarseX;
        byte y = gsu.PlotState.LastY;

        int tileAddr = ComputeTileAddr(gsu, x, y, ram.Length);
        uint tileSize = gsu.ColorGradient.TileSize();

        byte row = (byte)(y & 0x07);
        uint lineBaseAddr = (uint)(row * 0x02);

        uint bitplanes = gsu.ColorGradient.Bitplanes();
        for (byte pixelIdx = 0; pixelIdx < 8; pixelIdx++)
        {
            if (!gsu.PlotState.PixelBuffer.IsValid(pixelIdx))
            {
                continue;
            }

            int shift = 7 - pixelIdx;
            byte color = gsu.PlotState.PixelBuffer.GetPixel(pixelIdx);

            for (uint plane = 0; plane < bitplanes; plane += 2)
            {
                int planeAddr = tileAddr + (int)(lineBaseAddr + 8 * plane);

                ram[planeAddr] = (byte)((ram[planeAddr] & ~(1 << shift))
                    | (((color >> (int)plane) & 1) << shift));
                ram[planeAddr + 1] = (byte)((ram[planeAddr + 1] & ~(1 << shift))
                    | (((color >> (int)plane + 1) & 1) << shift));
            }
        }

        byte cycles = gsu.PlotState.FlushCyclesRemaining;

        byte flushCyclesRequired = (byte)(gsu.ClockSpeed.MemoryAccessCycles() * bitplanes);
        if (!gsu.PlotState.PixelBuffer.AllValid())
        {
            flushCyclesRequired = (byte)(flushCyclesRequired * 2);
        }

        gsu.PlotState.PixelBuffer.ClearValid();
        gsu.PlotState.FlushCyclesRemaining = flushCyclesRequired;
        gsu.PlotState.JustFlushed = true;

        return cycles;
    }

    private static int ComputeTileAddr(GraphicsSupportUnit gsu, byte x, byte y, int ramLen)
    {
        ushort tileX = (ushort)(x / 8);
        ushort tileY = (ushort)(y / 8);

        ScreenHeight screenHeight = gsu.ForceObjMode ? ScreenHeight.ObjMode : gsu.ScreenHeight;

        ushort tileNumber = screenHeight switch
        {
            ScreenHeight.Bg128Pixel => (ushort)(tileX * 0x10 + tileY),
            ScreenHeight.Bg160Pixel => (ushort)(tileX * 0x14 + tileY),
            ScreenHeight.Bg192Pixel => (ushort)(tileX * 0x18 + tileY),
            ScreenHeight.ObjMode =>
                (ushort)(((y.Bit(7) ? 1 : 0) << 9)
                    | ((x.Bit(7) ? 1 : 0) << 8)
                    | ((tileY & 0x0F) * 0x10)
                    | (tileX & 0x0F)),
            _ => 0
        };

        uint tileSize = gsu.ColorGradient.TileSize();
        uint tileAddr = gsu.ScreenBase + tileNumber * tileSize;
        return (int)(tileAddr & (uint)(ramLen - 1));
    }

    private static byte MaskColor(byte newColor, GraphicsSupportUnit gsu)
    {
        if (gsu.PorHighNibbleFlag)
        {
            newColor = (byte)((newColor & 0xF0) | (newColor >> 4));
        }

        if (gsu.PorFreezeHighNibble)
        {
            newColor = (byte)((newColor & 0x0F) | (gsu.Color & 0xF0));
        }

        return newColor;
    }
}
