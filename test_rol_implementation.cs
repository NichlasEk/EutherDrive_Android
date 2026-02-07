using System;

class TestROLImplementation
{
    // Vår ROL implementation från md_m68k_opeRO.cs
    static uint ROL_OurImplementation(uint value, int count, int size)
    {
        uint mask = size == 0 ? 0xFFu : size == 1 ? 0xFFFFu : 0xFFFFFFFFu;
        uint mostbit = size == 0 ? 0x80u : size == 1 ? 0x8000u : 0x80000000u;
        
        for (int i = 0; i < count; i++)
        {
            bool msb = (value & mostbit) != 0;
            value = (value << 1) & mask;
            if (msb) value |= 0x01;
        }
        return value & mask;
    }
    
    // Testa med olika värden
    static void Main()
    {
        Console.WriteLine("Test ROL.L #8 implementation:");
        
        uint[] testValues = { 0x00070000, 0x07000000, 0x00000007, 0x00000700 };
        
        foreach (uint val in testValues)
        {
            uint result = ROL_OurImplementation(val, 8, 2); // size=2 (long)
            Console.WriteLine($"0x{val:X8} ROL.L #8 = 0x{result:X8}");
        }
        
        Console.WriteLine("\nTest cykel:");
        uint d0 = 0x00070000;
        for (int i = 0; i < 4; i++)
        {
            d0 = ROL_OurImplementation(d0, 8, 2);
            Console.WriteLine($"Cykel {i+1}: D0 = 0x{d0:X8}");
        }
        
        // Testa vad 68000 hardware faktiskt gör
        Console.WriteLine("\n--- Testa med riktig 68000 beteende ---");
        Console.WriteLine("Enligt 68000 manual:");
        Console.WriteLine("ROL.L #8: Rotera 32-bit värde 8 bits åt vänster");
        Console.WriteLine("Bits flyttas: bit 31→C→bit 0, bit 30→bit 31, etc");
        
        // Manual calculation
        uint manualROL8(uint x)
        {
            // Rotera 8 bits åt vänster
            // Flytta höga 24 bits 8 positions vänster
            // Flytta låga 8 bits till höga 8 bits
            return ((x << 8) & 0xFFFFFFFF) | ((x >> 24) & 0xFF);
        }
        
        Console.WriteLine("\nManual ROL.L #8:");
        foreach (uint val in testValues)
        {
            uint result = manualROL8(val);
            Console.WriteLine($"0x{val:X8} ROL.L #8 = 0x{result:X8}");
        }
        
        // Testa vår implementation vs manual
        Console.WriteLine("\nJämförelse:");
        uint test = 0x00070000;
        uint ourResult = ROL_OurImplementation(test, 8, 2);
        uint manualResult = manualROL8(test);
        Console.WriteLine($"0x{test:X8}: Vår=0x{ourResult:X8}, Manual=0x{manualResult:X8}, Match={ourResult == manualResult}");
        
        // Testa 0x07000000
        test = 0x07000000;
        ourResult = ROL_OurImplementation(test, 8, 2);
        manualResult = manualROL8(test);
        Console.WriteLine($"0x{test:X8}: Vår=0x{ourResult:X8}, Manual=0x{manualResult:X8}, Match={ourResult == manualResult}");
        
        // Vad är korrekt?
        Console.WriteLine("\n--- Vad är korrekt ROL.L #8? ---");
        Console.WriteLine("0x00070000 i binärt: 0000 0000 0000 0111 0000 0000 0000 0000");
        Console.WriteLine("ROL.L #8: rotera alla bits 8 positions vänster");
        Console.WriteLine("Resultat: 0000 0111 0000 0000 0000 0000 0000 0000 = 0x07000000 ✓");
        
        Console.WriteLine("\n0x07000000 i binärt: 0000 0111 0000 0000 0000 0000 0000 0000");
        Console.WriteLine("ROL.L #8: rotera alla bits 8 positions vänster");
        Console.WriteLine("Resultat: 0000 0000 0000 0000 0000 0000 0000 0111 = 0x00000007 ✓");
        
        Console.WriteLine("\nSå vår ROL implementation är korrekt!");
        Console.WriteLine("Problemet är att spelet börjar med fel D0 värde.");
        
        // Testa om det är byte order problem
        Console.WriteLine("\n--- Byte order test ---");
        uint d0BE = 0x00070000; // Big-endian: bytes [00, 07, 00, 00]
        uint d0LE = 0x00000700; // Little-endian: bytes [00, 07, 00, 00] om byteswappat
        
        Console.WriteLine($"Big-endian: 0x{d0BE:X8}");
        Console.WriteLine($"Little-endian byteswapped: 0x{d0LE:X8}");
        
        uint rolBE = ROL_OurImplementation(d0BE, 8, 2);
        uint rolLE = ROL_OurImplementation(d0LE, 8, 2);
        
        Console.WriteLine($"ROL.L #8 på BE: 0x{rolBE:X8}");
        Console.WriteLine($"ROL.L #8 på LE: 0x{rolLE:X8}");
        
        // Vad händer om vi byteswappar före och efter?
        uint SwapBytes(uint x)
        {
            return ((x & 0xFF000000) >> 24) |
                   ((x & 0x00FF0000) >> 8) |
                   ((x & 0x0000FF00) << 8) |
                   ((x & 0x000000FF) << 24);
        }
        
        Console.WriteLine("\nTesta med byteswap:");
        uint d0Swapped = SwapBytes(d0BE); // 0x00000700
        Console.WriteLine($"0x{d0BE:X8} byteswapped = 0x{d0Swapped:X8}");
        uint rolSwapped = ROL_OurImplementation(d0Swapped, 8, 2);
        Console.WriteLine($"ROL.L #8 = 0x{rolSwapped:X8}");
        uint backSwapped = SwapBytes(rolSwapped);
        Console.WriteLine($"Byteswapped back = 0x{backSwapped:X8}");
    }
}