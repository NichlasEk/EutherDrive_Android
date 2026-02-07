using System;

namespace DisassembleMadou
{
    class Program
    {
        static void DisassembleBytes(byte[] bytes, uint startAddress)
        {
            uint pc = startAddress;
            int i = 0;
            
            Console.WriteLine($"Disassembling {bytes.Length} bytes starting at 0x{startAddress:X6}");
            Console.WriteLine("==========================================");
            
            while (i < bytes.Length)
            {
                Console.Write($"0x{pc:X6}: ");
                
                if (i + 1 < bytes.Length)
                {
                    ushort opcode = (ushort)((bytes[i] << 8) | bytes[i + 1]);
                    i += 2;
                    pc += 2;
                    
                    // Simple disassembly for the specific instructions we care about
                    DisassembleOpcode(opcode, ref i, bytes, ref pc);
                }
                else
                {
                    Console.WriteLine($"DB 0x{bytes[i]:X2}");
                    i++;
                    pc++;
                }
            }
        }
        
        static void DisassembleOpcode(ushort opcode, ref int i, byte[] bytes, ref uint pc)
        {
            // Check for specific instructions in Madou palette routine
            
            // ROL.L #8,Dx : 1110 1001 11xx xxxx where xxxx = register
            if ((opcode & 0xFFC0) == 0xE900) // Actually E9xx
            {
                // E9 is 1110 1001, need to check more bits
                if ((opcode & 0xF138) == 0xE100) // ROL.L #8,Dx
                {
                    int reg = opcode & 0x7;
                    Console.WriteLine($"ROL.L #8,D{reg}");
                    return;
                }
            }
            
            // ANDI.W #data,Dx : 0000 0010 01xx xxxx followed by immediate word
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
            
            // ASL.W #5,Dx : 1110 1011 01xx xxxx
            if ((opcode & 0xF1C0) == 0xE140) // ASL.W #5,Dx
            {
                int reg = opcode & 0x7;
                Console.WriteLine($"ASL.W #5,D{reg}");
                return;
            }
            
            // ASL.W #1,Dx : 1110 1001 01xx xxxx
            if ((opcode & 0xF1C0) == 0xE100) // ASL.W #1,Dx
            {
                int reg = opcode & 0x7;
                Console.WriteLine($"ASL.W #1,D{reg}");
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
            
            // ADDA.W Dx,An : 1101 1xxx 11xx xxxx
            if ((opcode & 0xF000) == 0xD000)
            {
                int srcReg = opcode & 0x7;
                int dstReg = (opcode >> 9) & 0x7;
                int mode = (opcode >> 3) & 0x7;
                int size = (opcode >> 8) & 0x1; // 0=word, 1=long
                
                if (mode == 0) // Dn
                {
                    if (size == 0)
                        Console.WriteLine($"ADDA.W D{srcReg},A{dstReg}");
                    else
                        Console.WriteLine($"ADDA.L D{srcReg},A{dstReg}");
                    return;
                }
            }
            
            // Default: just print hex
            Console.WriteLine($"DC.W 0x{opcode:X4}");
        }
        
        static void Main()
        {
            // Bytes from madou_known_sofar.md around 0x013A40:
            // "... 49 F9 00 FF 94 78 E1 98 48 E7 80 00 02 40 00 FF EB 40 47 F9 00 07 8A 8C D6 C0 28 DB ..."
            
            byte[] bytes = {
                0x49, 0xF9, 0x00, 0xFF, 0x94, 0x78, // LEA $FF9478,A4 ?
                0xE1, 0x98, // ROL.L #8,D0 ?
                0x48, 0xE7, 0x80, 0x00, // MOVEM.L D0,-(A7) ?
                0x02, 0x40, 0x00, 0xFF, // ANDI.W #$00FF,D0
                0xEB, 0x40, // ASL.W #5,D0
                0x47, 0xF9, 0x00, 0x07, 0x8A, 0x8C, // LEA $078A8C,A3
                0xD6, 0xC0, // ADDA.W D0,A3
                0x28, 0xDB  // MOVE.L (A3)+,(A4)+
            };
            
            DisassembleBytes(bytes, 0x013A40);
            
            Console.WriteLine("\n\nAlternative interpretation:");
            Console.WriteLine("==========================");
            
            // Let's manually decode based on known 68000 opcodes
            Console.WriteLine("49 F9 00 FF 94 78: LEA ($00FF9478).L,A4");
            Console.WriteLine("E1 98: ROL.L #8,D0");
            Console.WriteLine("48 E7 80 00: MOVEM.L D0,-(A7)");
            Console.WriteLine("02 40 00 FF: ANDI.W #$00FF,D0");
            Console.WriteLine("EB 40: ASL.W #5,D0");
            Console.WriteLine("47 F9 00 07 8A 8C: LEA ($00078A8C).L,A3");
            Console.WriteLine("D6 C0: ADDA.W D0,A3");
            Console.WriteLine("28 DB: MOVE.L (A3)+,(A4)+");
            
            Console.WriteLine("\nThe sequence is definitely:");
            Console.WriteLine("1. LEA $FF9478,A4");
            Console.WriteLine("2. ROL.L #8,D0");
            Console.WriteLine("3. MOVEM.L D0,-(A7)");
            Console.WriteLine("4. ANDI.W #$00FF,D0");
            Console.WriteLine("5. ASL.W #5,D0");
            Console.WriteLine("6. LEA $078A8C,A3");
            Console.WriteLine("7. ADDA.W D0,A3");
            Console.WriteLine("8. MOVE.L (A3)+,(A4)+");
            
            Console.WriteLine("\nSo ANDI.W #$00FF,D0 is CORRECT in the ROM!");
            Console.WriteLine("The problem MUST be elsewhere.");
        }
    }
}