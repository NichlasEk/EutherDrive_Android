using System;

class DecodeOpcode
{
    static void Main()
    {
        ushort opcode = 0xE198;
        Console.WriteLine($"Opcode: 0x{opcode:X4}");
        Console.WriteLine($"Binary: {Convert.ToString(opcode, 2).PadLeft(16, '0')}");
        
        // 68000 ROL instruction format:
        // 1110 ddd 1 ss ttt
        // where:
        // ddd = direction/size (000=ROR.W, 001=ROL.W, 010=ROR.L, 011=ROL.L)
        // ss = shift count (00=1, 01=2, 10=8, 11=register)
        // ttt = register number
        
        int ddd = (opcode >> 9) & 0x7;
        int ss = (opcode >> 6) & 0x3;
        int ttt = opcode & 0x7;
        
        Console.WriteLine($"ddd (bits 11-9): {ddd} ({Convert.ToString(ddd, 2).PadLeft(3, '0')})");
        Console.WriteLine($"ss (bits 7-6): {ss} ({Convert.ToString(ss, 2).PadLeft(2, '0')})");
        Console.WriteLine($"ttt (bits 2-0): {ttt} ({Convert.ToString(ttt, 2).PadLeft(3, '0')})");
        
        string[] dirSize = { "ROR.W", "ROL.W", "ROR.L", "ROL.L", "???", "???", "???", "???" };
        string[] shiftCount = { "1", "2", "8", "register" };
        
        Console.WriteLine($"\nDecoded: {dirSize[ddd]} #{shiftCount[ss]}, D{ttt}");
        
        // Check what our emulator would decode
        int g_op = (opcode >> 12) & 0xF;
        int g_op1 = (opcode >> 9) & 0x7;
        int g_op2 = (opcode >> 6) & 0x7;
        int g_op3 = (opcode >> 3) & 0x7;
        int g_op4 = opcode & 0x7;
        
        Console.WriteLine($"\nOur decoder:");
        Console.WriteLine($"g_op (bits 15-12): 0x{g_op:X} ({g_op})");
        Console.WriteLine($"g_op1 (bits 11-9): 0x{g_op1:X} ({g_op1})");
        Console.WriteLine($"g_op2 (bits 8-6): 0x{g_op2:X} ({g_op2})");
        Console.WriteLine($"g_op3 (bits 5-3): 0x{g_op3:X} ({g_op3})");
        Console.WriteLine($"g_op4 (bits 2-0): 0x{g_op4:X} ({g_op4})");
        
        // In analyse_RO_reg():
        // w_size = g_op2 & 0x03
        // w_ir = g_op3 & 0x04
        // w_dr = (g_opcode & 0x0008) != 0 ? 1 : 0  // bit 3 of opcode
        
        int w_size = g_op2 & 0x03;
        int w_ir = g_op3 & 0x04;
        int w_dr = (opcode & 0x0008) != 0 ? 1 : 0;
        
        Console.WriteLine($"\nIn analyse_RO_reg():");
        Console.WriteLine($"w_size (g_op2 & 0x03): {w_size} (0=byte,1=word,2=long)");
        Console.WriteLine($"w_ir (g_op3 & 0x04): {w_ir} (0=immediate,4=register)");
        Console.WriteLine($"w_dr (bit 3 of opcode): {w_dr} (0=ROR,1=ROL)");
        Console.WriteLine($"Bit 3 of 0xE198: {(opcode & 0x0008) >> 3}");
        
        // Also check other possible opcodes
        Console.WriteLine("\n--- Other possible opcodes ---");
        ushort[] testOpcodes = { 0xE198, 0xE118, 0xE158, 0xE1D8, 0xE1B8, 0xE138 };
        foreach (ushort test in testOpcodes)
        {
            Console.WriteLine($"\nOpcode 0x{test:X4}:");
            int test_ddd = (test >> 9) & 0x7;
            int test_ss = (test >> 6) & 0x3;
            int test_ttt = test & 0x7;
            Console.WriteLine($"  Decoded: {dirSize[test_ddd]} #{shiftCount[test_ss]}, D{test_ttt}");
            Console.WriteLine($"  Bit 3: {(test & 0x0008) >> 3} (0=ROR,1=ROL)");
            
            // What would our emulator decode?
            int test_g_op2 = (test >> 6) & 0x7;
            int test_w_size = test_g_op2 & 0x03;
            int test_w_dr = (test & 0x0008) != 0 ? 1 : 0;
            Console.WriteLine($"  Our w_size: {test_w_size} (0=byte,1=word,2=long)");
            Console.WriteLine($"  Our w_dr: {test_w_dr} (0=ROR,1=ROL)");
        }
        
        // What opcode would be ROL.L #8, D0?
        Console.WriteLine("\n--- What opcode for ROL.L #8, D0? ---");
        // ddd should be 011 for ROL.L
        // ss should be 10 for #8
        // ttt should be 000 for D0
        // So: 1110 011 1 10 000 = 1110 0111 1000 0000 = 0xE780
        ushort rol_long_8 = 0xE780;
        Console.WriteLine($"ROL.L #8, D0 should be: 0x{rol_long_8:X4}");
        Console.WriteLine($"Binary: {Convert.ToString(rol_long_8, 2).PadLeft(16, '0')}");
        
        // What about ROR.L #8, D0?
        // ddd should be 010 for ROR.L
        // ss should be 10 for #8
        // ttt should be 000 for D0
        // So: 1110 010 1 10 000 = 1110 0101 1000 0000 = 0xE580
        ushort ror_long_8 = 0xE580;
        Console.WriteLine($"ROR.L #8, D0 should be: 0x{ror_long_8:X4}");
        Console.WriteLine($"Binary: {Convert.ToString(ror_long_8, 2).PadLeft(16, '0')}");
    }
}