using System;
using System.IO;

var bytes = File.ReadAllBytes("/home/nichlas/roms/contra.md");
Console.WriteLine($"Size: {bytes.Length}");
// Try to find the sequence 30 3B ... 4E BB
for(int i=0; i<bytes.Length-4; i++) {
    if(bytes[i] == 0x30 && bytes[i+1] == 0x3B) {
        Console.WriteLine($"Found 30 3B at {i:X}");
    }
}
