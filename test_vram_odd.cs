using System;

namespace EutherDrive.Test
{
    class TestVramOdd
    {
        static void Main()
        {
            Console.WriteLine("Testing VRAM odd address read/write consistency...");
            
            // Simulate the current implementation
            byte[] g_vram = new byte[0x10000];
            
            // Test write to odd address
            int addr = 0x0001; // Odd address
            ushort data = 0x1234;
            
            // Current write implementation
            g_vram[addr] = (byte)(data >> 8); // MSB to addr
            g_vram[(addr ^ 1) & 0xffff] = (byte)(data & 0xff); // LSB to addr^1
            
            Console.WriteLine($"Write: addr=0x{addr:X4}, data=0x{data:X4}");
            Console.WriteLine($"  MSB 0x{(byte)(data >> 8):X2} -> g_vram[0x{addr:X4}] = 0x{g_vram[addr]:X2}");
            Console.WriteLine($"  LSB 0x{(byte)(data & 0xff):X2} -> g_vram[0x{(addr ^ 1) & 0xffff:X4}] = 0x{g_vram[(addr ^ 1) & 0xffff]:X2}");
            
            // Current read implementation
            ushort readBack = (ushort)((g_vram[addr] << 8) | g_vram[(addr + 1) & 0xffff]);
            Console.WriteLine($"Read:  addr=0x{addr:X4}");
            Console.WriteLine($"  MSB from g_vram[0x{addr:X4}] = 0x{g_vram[addr]:X2}");
            Console.WriteLine($"  LSB from g_vram[0x{(addr + 1) & 0xffff:X4}] = 0x{g_vram[(addr + 1) & 0xffff]:X2}");
            Console.WriteLine($"  Result: 0x{readBack:X4}");
            
            Console.WriteLine($"\nProblem: Write LSB to addr^1 (0x{(addr ^ 1) & 0xffff:X4}) but read LSB from addr+1 (0x{(addr + 1) & 0xffff:X4})");
            Console.WriteLine($"  These are different when addr is odd!");
            
            // Test with even address
            Console.WriteLine("\n--- Testing even address ---");
            addr = 0x0002; // Even address
            data = 0xABCD;
            
            g_vram = new byte[0x10000]; // Reset
            g_vram[addr] = (byte)(data >> 8);
            g_vram[(addr ^ 1) & 0xffff] = (byte)(data & 0xff);
            
            readBack = (ushort)((g_vram[addr] << 8) | g_vram[(addr + 1) & 0xffff]);
            
            Console.WriteLine($"Write even addr=0x{addr:X4}, data=0x{data:X4}");
            Console.WriteLine($"  addr^1 = 0x{(addr ^ 1) & 0xffff:X4}, addr+1 = 0x{(addr + 1) & 0xffff:X4}");
            Console.WriteLine($"  Read back: 0x{readBack:X4} ({(readBack == data ? "CORRECT" : "WRONG")})");
            
            // The fix should be:
            Console.WriteLine("\n--- The fix ---");
            Console.WriteLine("Option 1: Change read to use addr^1 for LSB:");
            Console.WriteLine("  read: (g_vram[addr] << 8) | g_vram[(addr ^ 1) & 0xffff]");
            Console.WriteLine("\nOption 2: Change write to use addr+1 for LSB:");
            Console.WriteLine("  write: g_vram[addr] = MSB; g_vram[(addr + 1) & 0xffff] = LSB");
            Console.WriteLine("\nOption 3: Always use even addresses (mask addr & ~1):");
            Console.WriteLine("  This is what real VDP does - word accesses are always to even addresses");
        }
    }
}