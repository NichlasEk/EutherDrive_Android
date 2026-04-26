using System;
using System.Diagnostics;

namespace EutherDrive.Core.MdTracerCore;

public static class MdVdpInterlaceMode2PatternTest
{
    public static void Run()
    {
        Console.WriteLine("[TEST] Interlace Mode 2 Pattern Address Test");
        var vdp = new md_vdp();

        // Display on, H40.
        vdp.write16(0xC00004, 0x8174);
        vdp.write16(0xC00004, 0x8C81);
        // Auto-increment by one word.
        vdp.write16(0xC00004, 0x8F02);

        // Set interlace mode 2
        ushort reg12Mode2 = (ushort)(0x8000 | (0x0C << 8) | 0x87);
        vdp.write16(0xC00004, reg12Mode2);

        if (vdp.g_vdp_interlace_mode != 2)
        {
            Console.WriteLine("[TEST-FAIL] Expected interlace mode 2");
            return;
        }
        Console.WriteLine("[TEST] Interlace mode 2 activated");

        // Set scroll A base to 0xC000
        // Formula: (data & 0x3E) << 10
        // For scrollA = 0xC000: data & 0x3E = 0x30, so data = 0x30
        ushort reg2ScrollA = (ushort)(0x8000 | (0x02 << 8) | 0x30);
        vdp.write16(0xC00004, reg2ScrollA);
        Console.WriteLine($"[TEST] Scroll A base = 0x{vdp.g_vdp_reg_2_scrolla:X4}");

        // Set scroll B base to 0xE000
        // Formula: data << 13, so for 0xE000 we need data = 0x07
        // 0x07 << 13 = 0xE000 (in 16-bit)
        ushort reg4ScrollB = (ushort)(0x8000 | (0x04 << 8) | 0x07);
        vdp.write16(0xC00004, reg4ScrollB);
        Console.WriteLine($"[TEST] Scroll B base = 0x{vdp.g_vdp_reg_4_scrollb:X4}");

        // Write a scroll plane entry at 0xC000 (tile index 0, no reverse)
        // This should be read from 0x6000 in renderer VRAM
        ushort entry0 = 0x0001; // Tile index 1, priority 0, palette 0
        SetVramWriteAddress(vdp, 0xC000);
        vdp.write16(0xC00000, entry0);
        FlushVdpFifo(vdp);
        Console.WriteLine($"[TEST] Wrote scroll entry 0x{entry0:X4} to VRAM 0xC000");

        // Interlace mode 2 patterns are 8x16: tile 1 starts at byte address 0x0040.
        ushort patternData = 0x1111; // All pixels use palette entry 1.
        int patternAddr = 1 << 6;
        Console.WriteLine($"[TEST] Pattern address for tile 1 = 0x{patternAddr:X4}");

        // Write pattern data (2 bytes at a time)
        SetVramWriteAddress(vdp, patternAddr);
        for (int i = 0; i < 64; i += 2)
        {
            vdp.write16(0xC00000, patternData);
        }
        FlushVdpFifo(vdp);
        Console.WriteLine($"[TEST] Wrote pattern data to 0x{patternAddr:X4}");

        SetCramWriteAddress(vdp, 0x02);
        vdp.write16(0xC00000, 0x0EEE);
        FlushVdpFifo(vdp);

        // Now verify GetTileWordAddress returns correct address
        int tileAddr = vdp.GetTileWordAddress(1, 0, 0);
        Console.WriteLine($"[TEST] GetTileWordAddress(1, 0, 0) = 0x{tileAddr:X4}");
        Console.WriteLine($"[TEST] Expected: 0x{(patternAddr >> 1):X4}");

        // Check what renderer_vram contains at the scroll entry location
        Console.WriteLine($"[TEST] renderer_vram[0x6000] = 0x{vdp.g_renderer_vram[0x6000]:X4}");

        // Render a frame and check the output
        StepFrame(vdp);

        // Check the framebuffer
        uint firstPixel = vdp.g_game_screen[0];
        Console.WriteLine($"[TEST] First pixel = 0x{firstPixel:X8}");
        if (firstPixel == 0xFF000000)
        {
            Console.WriteLine("[TEST-FAIL] First pixel is black - rendering not working");
        }
        else
        {
            Console.WriteLine("[TEST-PASS] First pixel is not black");
        }

        Console.WriteLine("[TEST] Done");
    }

    private static void SetVramWriteAddress(md_vdp vdp, int address)
    {
        vdp.write16(0xC00004, (ushort)(0x4000 | (address & 0x3FFF)));
        vdp.write16(0xC00004, (ushort)((address >> 14) & 0x0003));
    }

    private static void SetCramWriteAddress(md_vdp vdp, int address)
    {
        vdp.write16(0xC00004, (ushort)(0xC000 | (address & 0x3FFF)));
        vdp.write16(0xC00004, (ushort)((address >> 14) & 0x0003));
    }

    private static void FlushVdpFifo(md_vdp vdp)
    {
        vdp.ProcessVdpFifoForM68kCycles(4096);
    }

    private static void StepFrame(md_vdp vdp)
    {
        int lines = vdp.g_display_ysize;
        for (int line = 0; line <= lines; line++)
            vdp.run(line);
    }
}
