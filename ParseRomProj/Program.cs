using System;
using System.IO;

class Program {
    const int SmdBlockSize = 0x4000;
    
    static void Main() {
        byte[] data = File.ReadAllBytes("/home/nichlas/roms/contra.md");
        byte[] norm = new byte[data.Length - 512];
        Array.Copy(data, 512, norm, 0, norm.Length);
        
        byte[] result = new byte[norm.Length];
        for (int blockStart = 0; blockStart < norm.Length; blockStart += SmdBlockSize) {
            for (int i = 0; i < 0x2000; i++) {
                result[blockStart + 2 * i] = norm[blockStart + 0x2000 + i];
                result[blockStart + 2 * i + 1] = norm[blockStart + i];
            }
        }
        
        int target = 0x5D8A;
        Console.WriteLine("Jump table words:");
        for(int i=0; i<8; i++) {
            ushort val = (ushort)((result[target + i*2] << 8) | result[target + i*2 + 1]);
            Console.WriteLine($"{target + i*2:X4}: {val:X4}");
        }
    }
}
