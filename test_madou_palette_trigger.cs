using System;
using System.IO;

class TestMadouPaletteTrigger
{
    static void Main()
    {
        Console.WriteLine("Madou Palette Routine Test");
        Console.WriteLine("==========================");
        Console.WriteLine();
        Console.WriteLine("The palette routine at 0x013A46 should be called with D0=0x00070000");
        Console.WriteLine("Then: ROL.L #8,D0 -> 0x00000007");
        Console.WriteLine("     ANDI.W #$00FF,D0 -> 0x00000007");
        Console.WriteLine("     ASL.W #5,D0 -> 0x000000E0 (224 decimal)");
        Console.WriteLine();
        Console.WriteLine("This 0xE0 is used as an index/offset for palette data.");
        Console.WriteLine();
        Console.WriteLine("If our ROL.L #8 fix works, we should see:");
        Console.WriteLine("1. [DEBUG-FIX-CRITICAL] PC=0x013A50 ...");
        Console.WriteLine("2. Correct D0 value (0x07000000) before ROL");
        Console.WriteLine("3. Correct result (0x000000E0) after ASL.W #5");
        Console.WriteLine();
        Console.WriteLine("If graphics are still corrupt, the bug is elsewhere:");
        Console.WriteLine("- DMA source address wrong (0xFF94F8 vs 0xFF9498)");
        Console.WriteLine("- Palette conversion routine bug");
        Console.WriteLine("- VDP/CRAM write bug");
        Console.WriteLine();
        Console.WriteLine("Next steps:");
        Console.WriteLine("1. Run emulator with EUTHERDRIVE_FRAME_LIMIT=1000 to reach palette code");
        Console.WriteLine("2. Check logs for [DEBUG-FIX] messages");
        Console.WriteLine("3. If no messages, game isn't reaching 0x013A50 yet");
        Console.WriteLine("4. Need to trace why palette routine isn't called");
        Console.WriteLine();
        Console.WriteLine("Command to run:");
        Console.WriteLine("cd /home/nichlas/EutherDrive/bin/headless");
        Console.WriteLine("export EUTHERDRIVE_FRAME_LIMIT=1000");
        Console.WriteLine("./EutherDrive.Headless \"/home/nichlas/roms/madou.md\" 2>&1 | grep -i 'debug\\|013a50\\|013a58\\|fix\\|rol\\|andi\\|madou'");
    }
}