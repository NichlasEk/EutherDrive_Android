using System;

namespace DecodeMadouBytes
{
    class Program
    {
        static void Main()
        {
            // Bytes from Madou ROM at 0x013A46-0x013A60
            byte[] bytes = {
                0x72, 0x03,             // 013A46
                0x49, 0xF9, 0x00, 0xFF, 0x94, 0x78, // 013A48
                0xE1, 0x98,             // 013A4E
                0x48, 0xE7, 0x80, 0x00, // 013A50
                0x02, 0x40, 0x00, 0xFF, // 013A54
                0xEB, 0x40,             // 013A58
                0x47, 0xF9, 0x00, 0x07, 0x8A, 0x8C, // 013A5A
                0xD6, 0xC0              // 013A60
            };
            
            Console.WriteLine("Decoding Madou ROM bytes at 0x013A46-0x013A60:");
            Console.WriteLine("==============================================");
            
            uint pc = 0x013A46;
            int i = 0;
            
            while (i < bytes.Length)
            {
                Console.Write($"0x{pc:X6}: ");
                
                if (i + 1 < bytes.Length)
                {
                    ushort opcode = (ushort)((bytes[i] << 8) | bytes[i + 1]);
                    i += 2;
                    pc += 2;
                    
                    DecodeInstruction(opcode, ref i, bytes, ref pc);
                }
            }
        }
        
        static void DecodeInstruction(ushort opcode, ref int i, byte[] bytes, ref uint pc)
        {
            // Check for specific instructions
            
            // MOVEQ #data,Dn : 0111 0rrr dddd dddd
            if ((opcode & 0xF100) == 0x7000)
            {
                int reg = (opcode >> 9) & 0x7;
                sbyte data = (sbyte)(opcode & 0xFF);
                Console.WriteLine($"MOVEQ #{data},D{reg}");
                return;
            }
            
            // LEA (abs).L,An : 0100 1111 11xx xxxx followed by long address
            if ((opcode & 0xFF00) == 0x4F00)
            {
                int reg = opcode & 0x7;
                int mode = (opcode >> 3) & 0x7;
                if (mode == 7 && (opcode & 0x38) == 0x38) // Absolute long
                {
                    if (i + 3 < bytes.Length)
                    {
                        uint addr = (uint)((bytes[i] << 24) | (bytes[i + 1] << 16) | (bytes[i + 2] << 8) | bytes[i + 3]);
                        i += 4;
                        pc += 4;
                        Console.WriteLine($"LEA 0x{addr:X6},A{reg}");
                        return;
                    }
                }
            }
            
            // ROL.L #8,Dn : 1110 1001 11rr r000
            if ((opcode & 0xF138) == 0xE100)
            {
                int reg = opcode & 0x7;
                Console.WriteLine($"ROL.L #8,D{reg}");
                return;
            }
            
            // ROR.L #8,Dn : 1110 1000 11rr r000
            if ((opcode & 0xF138) == 0xE000)
            {
                int reg = opcode & 0x7;
                Console.WriteLine($"ROR.L #8,D{reg}");
                return;
            }
            
            // MOVEM.L reglist,-(An) : 0100 1000 10aa aaa1 followed by mask
            if ((opcode & 0xFF80) == 0x4880)
            {
                int reg = opcode & 0x7;
                if (i < bytes.Length)
                {
                    ushort mask = bytes[i];
                    i++;
                    pc++;
                    Console.WriteLine($"MOVEM.L regmask=0x{mask:X2},-(A{reg})");
                    return;
                }
            }
            
            // MOVEM.L Dn,-(An) : Actually 48E7 is MOVEM.L reglist,-(A7)
            if (opcode == 0x48E7)
            {
                if (i + 1 < bytes.Length)
                {
                    ushort mask = (ushort)((bytes[i] << 8) | bytes[i + 1]);
                    i += 2;
                    pc += 2;
                    Console.WriteLine($"MOVEM.L regmask=0x{mask:X4},-(A7)");
                    return;
                }
            }
            
            // ANDI.W #data,Dn : 0000 0010 01rr r000 followed by immediate word
            if ((opcode & 0xFF00) == 0x0200)
            {
                int reg = opcode & 0x7;
                int mode = (opcode >> 3) & 0x7;
                if (mode == 0) // Data register direct
                {
                    if (i + 1 < bytes.Length)
                    {
                        ushort imm = (ushort)((bytes[i] << 8) | bytes[i + 1]);
                        i += 2;
                        pc += 2;
                        Console.WriteLine($"ANDI.W #${imm:X4},D{reg}");
                        return;
                    }
                }
            }
            
            // ASL.W #5,Dn : 1110 1001 01rr r000
            if ((opcode & 0xF1C0) == 0xE100)
            {
                int reg = opcode & 0x7;
                int count = (opcode >> 9) & 0x7;
                Console.WriteLine($"ASL.W #{count},D{reg}");
                return;
            }
            
            // LSL.W #5,Dn : 1110 1011 01rr r000
            if ((opcode & 0xF1C0) == 0xE900)
            {
                int reg = opcode & 0x7;
                int count = (opcode >> 9) & 0x7;
                Console.WriteLine($"LSL.W #{count},D{reg}");
                return;
            }
            
            // LEA (abs).L,An (alternative encoding)
            if (opcode == 0x47F9)
            {
                if (i + 3 < bytes.Length)
                {
                    uint addr = (uint)((bytes[i] << 24) | (bytes[i + 1] << 16) | (bytes[i + 2] << 8) | bytes[i + 3]);
                    i += 4;
                    pc += 4;
                    Console.WriteLine($"LEA 0x{addr:X6},A0"); // Actually A? Need to check
                    return;
                }
            }
            
            // ADDA.W Dn,An : 1101 1aaa 0rrr r000
            if ((opcode & 0xF000) == 0xD000)
            {
                int aReg = (opcode >> 9) & 0x7;
                int dReg = opcode & 0x7;
                int mode = (opcode >> 3) & 0x7;
                if (mode == 0) // Dn
                {
                    Console.WriteLine($"ADDA.W D{dReg},A{aReg}");
                    return;
                }
            }
            
            // Default: show raw bytes
            Console.WriteLine($"${opcode:X4} (unknown)");
        }
    }
}