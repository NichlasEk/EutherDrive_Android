using System;

namespace TestVramLogic
{
    class Program
    {
        static void Main()
        {
            Console.WriteLine("Testing VRAM byte/word write logic...\n");
            
            // Test current implementation
            TestCurrentImplementation();
            
            Console.WriteLine("\n\nAlternative interpretation:");
            TestAlternativeInterpretation();
        }
        
        static void TestCurrentImplementation()
        {
            Console.WriteLine("CURRENT IMPLEMENTATION:");
            Console.WriteLine("=======================");
            
            // Word write: vram_write_w(addr, data)
            // - MSB to addr
            // - LSB to addr ^ 1
            
            // Byte write (line 223):
            // - Even addr: byte to addr
            // - Odd addr: byte to addr ^ 1
            
            Console.WriteLine("Word write to addr=0x1000, data=0x1234:");
            Console.WriteLine("  MSB 0x12 -> g_vram[0x1000]");
            Console.WriteLine("  LSB 0x34 -> g_vram[0x1000 ^ 1] = g_vram[0x1001]");
            
            Console.WriteLine("\nTwo byte writes:");
            Console.WriteLine("  Byte write to even addr=0x1000, data=0x12:");
            Console.WriteLine("    -> g_vram[0x1000] (even: use addr)");
            Console.WriteLine("  Byte write to odd addr=0x1001, data=0x34:");
            Console.WriteLine("    -> g_vram[0x1001 ^ 1] = g_vram[0x1000] (odd: use addr ^ 1)");
            Console.WriteLine("  Result: Both bytes go to g_vram[0x1000]! WRONG!");
            
            Console.WriteLine("\nvram_read_w(0x1000):");
            Console.WriteLine("  MSB from g_vram[0x1000]");
            Console.WriteLine("  LSB from g_vram[0x1000 ^ 1] = g_vram[0x1001]");
            Console.WriteLine("  But LSB was written to g_vram[0x1000]!");
            Console.WriteLine("  Read gets wrong value!");
        }
        
        static void TestAlternativeInterpretation()
        {
            Console.WriteLine("\nALTERNATIVE: Maybe byte writes are already byte-swapped?");
            Console.WriteLine("=========================================================");
            
            Console.WriteLine("What if 'addr' in vram_write_w is already byte-swapped?");
            Console.WriteLine("i.e., vram_write_w receives address from VDP port, not 68000 address");
            
            Console.WriteLine("\n68000 writes word to VDP data port at address 0xC00000:");
            Console.WriteLine("  On bus: MSB on D15-D8, LSB on D7-D0");
            Console.WriteLine("  VDP receives: address (maybe 0x0000), data 0x1234");
            
            Console.WriteLine("\nVDP stores:");
            Console.WriteLine("  MSB 0x12 -> VRAM[address]");
            Console.WriteLine("  LSB 0x34 -> VRAM[address ^ 1]");
            
            Console.WriteLine("\n68000 writes byte to VDP data port at address 0xC00001 (odd):");
            Console.WriteLine("  On bus: byte on D7-D0 (lower byte lane)");
            Console.WriteLine("  VDP receives: address (maybe 0x0001), data 0xXX");
            
            Console.WriteLine("\nVDP needs to store byte at correct position:");
            Console.WriteLine("  If address LSB=1 (odd): byte goes to VRAM[address ^ 1]");
            Console.WriteLine("  This matches line 223!");
        }
        
        static void TestSonic2Scenario()
        {
            Console.WriteLine("\n\nSONIC 2 CORRUPTION SCENARIO:");
            Console.WriteLine("============================");
            Console.WriteLine("If Sonic 2 worked before but is corrupt now,");
            Console.WriteLine("then my vram_read_w fix (addr ^ 1) is likely wrong.");
            
            Console.WriteLine("\nOriginal vram_read_w used addr + 1:");
            Console.WriteLine("  (g_vram[addr] << 8) | g_vram[addr + 1]");
            
            Console.WriteLine("\nMy fix uses addr ^ 1:");
            Console.WriteLine("  (g_vram[addr] << 8) | g_vram[addr ^ 1]");
            
            Console.WriteLine("\nFor even addresses: addr ^ 1 = addr + 1 (same)");
            Console.WriteLine("For odd addresses: addr ^ 1 = addr - 1 (different)");
            
            Console.WriteLine("\nSo the fix only affects odd address reads.");
            Console.WriteLine("If Sonic 2 reads VRAM at odd addresses, it gets wrong data.");
        }
    }
}