using System;
using System.Diagnostics;

namespace EutherDrive.Core.MdTracerCore;

public static class MdVdpInterlaceMode2PatternTest
{
    public static void Run()
    {
        Console.WriteLine("[TEST] Interlace Mode 2 Pattern Address Test");
        var vdp = new md_vdp();

        // Set interlace mode 2
        ushort reg12Mode2 = (ushort)(0x8000 | (0x0C << 8) | 0x06); // 0x8000 | 0x0C00 | 0x06 = 0x8C06
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
        vdp.write16(0xC00000, entry0);
        Console.WriteLine($"[TEST] Wrote scroll entry 0x{entry0:X4} to VRAM 0xC000");

        // Write pattern data for tile 1 at address 0xE000 (scrollA + 0x2000)
        // Pattern data is 8 rows of 8 pixels = 64 bytes
        ushort patternData = 0xFFFF; // All pixels set
        int patternAddr = vdp.g_vdp_reg_2_scrolla + 0x2000 + (1 << 6); // 0xC000 + 0x2000 + 0x40 = 0xE040
        Console.WriteLine($"[TEST] Pattern address for tile 1 = 0x{patternAddr:X4}");

        // Write pattern data (2 bytes at a time)
        for (int i = 0; i < 64; i += 2)
        {
            vdp.write16((uint)(patternAddr + i), patternData);
        }
        Console.WriteLine($"[TEST] Wrote pattern data to 0x{patternAddr:X4}");

        // Now verify GetTileWordAddress returns correct address
        int tileAddr = vdp.GetTileWordAddress(1, 0, 0, true);
        Console.WriteLine($"[TEST] GetTileWordAddress(1, 0, 0, true) = 0x{tileAddr:X4}");
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

    private static void StepFrame(md_vdp vdp)
    {
        int lines = vdp.g_display_ysize * 2; // Interlace mode 2 doubles lines
        for (int line = 0; line <= lines; line++)
            vdp.run(line);
    }
}
