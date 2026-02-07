using System;

class TestBigEndianFix
{
    static void Main()
    {
        Console.WriteLine("Test big-endian word access på 32-bit register:");
        
        uint d0 = 0x07000000;
        Console.WriteLine($"D0 = 0x{d0:X8}");
        
        // Simulera read_g_reg_data för word (size=1)
        uint readWord = (d0 >> 16) & 0xFFFF;
        Console.WriteLine($"read_g_reg_data(0, 1) = 0x{readWord:X4} (höga 16 bitar)");
        
        // Simulera ANDI.W #$00FF
        uint andResult = readWord & 0x00FF;
        Console.WriteLine($"ANDI.W #$00FF = 0x{andResult:X4}");
        
        // Simulera write_g_reg_data för word
        uint newD0 = (d0 & 0x0000FFFF) | (andResult << 16);
        Console.WriteLine($"write_g_reg_data(0, 1, 0x{andResult:X4}) => D0 = 0x{newD0:X8}");
        
        // Simulera ASL.W #5
        uint aslResult = andResult << 5;
        Console.WriteLine($"ASL.W #5 på 0x{andResult:X4} = 0x{aslResult:X4}");
        
        uint finalD0 = (newD0 & 0x0000FFFF) | (aslResult << 16);
        Console.WriteLine($"Final D0 = 0x{finalD0:X8}");
        
        Console.WriteLine("\nTest med start D0 = 0x00070000:");
        d0 = 0x00070000;
        Console.WriteLine($"D0 = 0x{d0:X8}");
        
        readWord = (d0 >> 16) & 0xFFFF;
        Console.WriteLine($"read_g_reg_data(0, 1) = 0x{readWord:X4} (höga 16 bitar = 0x0007)");
        
        andResult = readWord & 0x00FF;
        Console.WriteLine($"ANDI.W #$00FF = 0x{andResult:X4}");
        
        newD0 = (d0 & 0x0000FFFF) | (andResult << 16);
        Console.WriteLine($"Efter ANDI.W: D0 = 0x{newD0:X8}");
        
        // ROL.L #8 på 0x00070000
        uint rolResult = 0;
        uint value = 0x00070000;
        for (int i = 0; i < 8; i++)
        {
            bool msb = (value & 0x80000000) != 0;
            value = (value << 1);
            if (msb) value |= 0x01;
        }
        Console.WriteLine($"\nROL.L #8 på 0x00070000 = 0x{value:X8}");
        
        // Testa vad som händer med vår fix
        Console.WriteLine("\n--- Testa hela sekvensen med vår fix ---");
        Console.WriteLine("Start: D0 = 0x00070000");
        Console.WriteLine("1. ROL.L #8, D0 -> D0 = 0x07000000");
        d0 = 0x07000000;
        
        Console.WriteLine("2. ANDI.W #$00FF, D0");
        readWord = (d0 >> 16) & 0xFFFF;  // Höga 16 bitar: 0x0700
        Console.WriteLine($"   Läser höga 16 bitar: 0x{readWord:X4}");
        andResult = readWord & 0x00FF;  // 0x0700 & 0x00FF = 0x0000
        Console.WriteLine($"   AND med 0x00FF: 0x{andResult:X4}");
        d0 = (d0 & 0x0000FFFF) | (andResult << 16);  // Skriver till höga 16 bitar
        Console.WriteLine($"   Resultat: D0 = 0x{d0:X8} (FORTFARANDE FEL!)");
        
        Console.WriteLine("\nProblemet kvarstår! 0x07000000 ANDI.W #$00FF ger 0x07000000");
        Console.WriteLine("Spelet behöver 0x00000007!");
        
        Console.WriteLine("\n--- Testa om det är ROR.L #8 istället ---");
        d0 = 0x00070000;
        value = d0;
        for (int i = 0; i < 8; i++)
        {
            bool lsb = (value & 0x01) != 0;
            value = value >> 1;
            if (lsb) value |= 0x80000000;
        }
        Console.WriteLine($"ROR.L #8 på 0x00070000 = 0x{value:X8} (0x00000700)");
        Console.WriteLine("Inte heller 0x00000007!");
        
        Console.WriteLine("\n--- Testa ROL.L #8 på 0x07000000 ---");
        d0 = 0x07000000;
        value = d0;
        for (int i = 0; i < 8; i++)
        {
            bool msb = (value & 0x80000000) != 0;
            value = (value << 1);
            if (msb) value |= 0x01;
        }
        Console.WriteLine($"ROL.L #8 på 0x07000000 = 0x{value:X8} (0x00000007!)");
        Console.WriteLine("DETTA är vad spelet behöver!");
    }
}