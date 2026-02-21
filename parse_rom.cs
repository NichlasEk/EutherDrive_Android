using System;
using System.IO;
using System.Text;

class Program {
    const int SmdBlockSize = 0x4000;
    
    static void Main() {
        byte[] data = File.ReadAllBytes("/home/nichlas/roms/contra.md");
        // Remove header
        byte[] norm = new byte[data.Length - 512];
        Array.Copy(data, 512, norm, 0, norm.Length);
        
        // Deinterleave
        byte[] result = new byte[norm.Length];
        for (int blockStart = 0; blockStart < norm.Length; blockStart += SmdBlockSize) {
            for (int i = 0; i < 0x2000; i++) {
                result[blockStart + 2 * i] = norm[blockStart + 0x2000 + i];
                result[blockStart + 2 * i + 1] = norm[blockStart + i];
            }
        }
        
        // Let's dump the code around 0x005D60
        int target = 0x5D60;
        Console.Write("Opcodes at 5D60: ");
        for(int i=0; i<8; i++) {
            Console.Write($"{result[target + i*2]:X2}{result[target + i*2 + 1]:X2} ");
        }
        Console.WriteLine();
    }
}
