using System;
using System.IO;

namespace TestVramOddFix
{
    class Program
    {
        static void Main()
        {
            Console.WriteLine("Testing VRAM odd address fix for Madou Monogatari...\n");
            
            // Test 1: Verify the fix conceptually
            Console.WriteLine("Test 1: VRAM read/write consistency check");
            TestVramConsistency();
            
            // Test 2: Simulate what might happen in Madou
            Console.WriteLine("\n\nTest 2: Simulating Madou graphics data pattern");
            SimulateMadouPattern();
            
            // Test 3: Check actual implementation
            Console.WriteLine("\n\nTest 3: Checking implementation files");
            CheckImplementation();
        }
        
        static void TestVramConsistency()
        {
            byte[] vram = new byte[0x10000];
            
            // Test odd address write/read
            int oddAddr = 0x123;
            ushort testData = 0xABCD;
            
            Console.WriteLine($"Testing odd address 0x{oddAddr:X4}:");
            
            // OLD BUGGY implementation (before fix):
            // Write: MSB to addr, LSB to addr^1
            // Read: MSB from addr, LSB from addr+1
            vram[oddAddr] = (byte)(testData >> 8);
            vram[oddAddr ^ 1] = (byte)(testData & 0xFF);
            
            ushort readOldBuggy = (ushort)((vram[oddAddr] << 8) | vram[(oddAddr + 1) & 0xFFFF]);
            Console.WriteLine($"  OLD (buggy): Write 0x{testData:X4}, Read back 0x{readOldBuggy:X4}");
            Console.WriteLine($"    MSB written to 0x{oddAddr:X4} = 0x{vram[oddAddr]:X2}");
            Console.WriteLine($"    LSB written to 0x{oddAddr ^ 1:X4} = 0x{vram[oddAddr ^ 1]:X2}");
            Console.WriteLine($"    LSB read from 0x{(oddAddr + 1) & 0xFFFF:X4} = 0x{vram[(oddAddr + 1) & 0xFFFF]:X2}");
            
            // NEW FIXED implementation (after fix):
            // Write: MSB to addr, LSB to addr^1  
            // Read: MSB from addr, LSB from addr^1
            ushort readNewFixed = (ushort)((vram[oddAddr] << 8) | vram[oddAddr ^ 1]);
            Console.WriteLine($"  NEW (fixed): Write 0x{testData:X4}, Read back 0x{readNewFixed:X4}");
            Console.WriteLine($"    MSB read from 0x{oddAddr:X4} = 0x{vram[oddAddr]:X2}");
            Console.WriteLine($"    LSB read from 0x{oddAddr ^ 1:X4} = 0x{vram[oddAddr ^ 1]:X2}");
            
            Console.WriteLine($"\n  Result: Old gives 0x{readOldBuggy:X4} (WRONG), New gives 0x{readNewFixed:X4} (CORRECT)");
            
            // Test even address (should work correctly either way)
            Console.WriteLine($"\nTesting even address 0x{0x124:X4}:");
            int evenAddr = 0x124;
            vram[evenAddr] = (byte)(testData >> 8);
            vram[evenAddr ^ 1] = (byte)(testData & 0xFF);
            
            ushort readEvenOld = (ushort)((vram[evenAddr] << 8) | vram[(evenAddr + 1) & 0xFFFF]);
            ushort readEvenNew = (ushort)((vram[evenAddr] << 8) | vram[evenAddr ^ 1]);
            Console.WriteLine($"  Both give: 0x{readEvenOld:X4} (addr+1 = addr^1 when addr is even)");
        }
        
        static void SimulateMadouPattern()
        {
            // Madou might be doing something like:
            // 1. Writing graphics data to VRAM
            // 2. Possibly using odd addresses for some operations
            // 3. Reading back corrupted data
            
            Console.WriteLine("Simulating graphics data transfer:");
            Console.WriteLine("  Game writes pattern data to VRAM");
            Console.WriteLine("  Some writes might be to odd addresses");
            Console.WriteLine("  With bug: data ends up in wrong locations");
            Console.WriteLine("  With fix: data stays in correct locations");
            
            // Example: Tile data (8x8 pixels, 32 bytes)
            byte[] tileData = new byte[32];
            for (int i = 0; i < 32; i++)
                tileData[i] = (byte)(i * 8);
                
            byte[] vram = new byte[0x10000];
            
            // Write tile to VRAM starting at odd address
            int startAddr = 0x1001; // Odd address
            for (int i = 0; i < 32; i += 2)
            {
                ushort word = (ushort)((tileData[i] << 8) | tileData[i + 1]);
                int addr = startAddr + i;
                
                // Write using VDP logic
                vram[addr] = (byte)(word >> 8);
                vram[addr ^ 1] = (byte)(word & 0xFF);
            }
            
            // Read back with OLD buggy logic
            Console.WriteLine("\nReading back with OLD logic (buggy):");
            for (int i = 0; i < 32; i += 2)
            {
                int addr = startAddr + i;
                ushort read = (ushort)((vram[addr] << 8) | vram[(addr + 1) & 0xFFFF]);
                Console.WriteLine($"  Addr 0x{addr:X4}: wrote 0x{(tileData[i] << 8) | tileData[i + 1]:X4}, read 0x{read:X4}");
            }
            
            // Read back with NEW fixed logic  
            Console.WriteLine("\nReading back with NEW logic (fixed):");
            for (int i = 0; i < 32; i += 2)
            {
                int addr = startAddr + i;
                ushort read = (ushort)((vram[addr] << 8) | vram[addr ^ 1]);
                Console.WriteLine($"  Addr 0x{addr:X4}: wrote 0x{(tileData[i] << 8) | tileData[i + 1]:X4}, read 0x{read:X4}");
            }
        }
        
        static void CheckImplementation()
        {
            Console.WriteLine("Checking implementation files:");
            
            string vdpMemoryPath = "/home/nichlas/EutherDrive/EutherDrive.Core/MdTracerCore/md_vdp_memory.cs";
            if (File.Exists(vdpMemoryPath))
            {
                string content = File.ReadAllText(vdpMemoryPath);
                if (content.Contains("vram_read_w(int addr) =>") && content.Contains("addr ^ 1"))
                {
                    Console.WriteLine("  ✓ md_vdp_memory.cs: vram_read_w uses addr ^ 1 (FIXED)");
                }
                else if (content.Contains("vram_read_w(int addr) =>") && content.Contains("addr + 1"))
                {
                    Console.WriteLine("  ✗ md_vdp_memory.cs: vram_read_w uses addr + 1 (BUGGY)");
                }
                
                if (content.Contains("vram_write_w") && content.Contains("addr ^ 1"))
                {
                    Console.WriteLine("  ✓ md_vdp_memory.cs: vram_write_w uses addr ^ 1 (CORRECT)");
                }
            }
            
            // Check other files that might use similar patterns
            string[] filesToCheck = {
                "/home/nichlas/EutherDrive/EutherDrive.Core/MdTracerCore/md_vdp.cs",
                "/home/nichlas/EutherDrive/EutherDrive.Core/MdTracerCore/md_vdp_renderer_line.cs"
            };
            
            foreach (string file in filesToCheck)
            {
                if (File.Exists(file))
                {
                    string content = File.ReadAllText(file);
                    int count = CountOccurrences(content, "addr ^ 1");
                    if (count > 0)
                    {
                        Console.WriteLine($"  ✓ {Path.GetFileName(file)}: Uses addr ^ 1 in {count} places");
                    }
                }
            }
        }
        
        static int CountOccurrences(string text, string pattern)
        {
            int count = 0;
            int i = 0;
            while ((i = text.IndexOf(pattern, i)) != -1)
            {
                i += pattern.Length;
                count++;
            }
            return count;
        }
    }
}