using System;
using System.Collections.Generic;
using System.Threading;

namespace Ryu64.MIPS
{
    public class OpcodeTable
    {
        public struct InstInfo
        {
            public uint Mask;
            public uint Value;
            public uint Cycles;
            public string FormattedASM;

            public InstInterp.InterpretOpcode Interpret;

            public InstInfo(uint Mask, uint Value, InstInterp.InterpretOpcode Interpret, string FormattedASM, uint Cycles)
            {
                this.Mask         = Mask;
                this.Value        = Value;
                this.Interpret    = Interpret;
                this.FormattedASM = FormattedASM;
                this.Cycles = Cycles;
            }
        }

        public struct OpcodeDesc
        {
            public uint Opcode;

            public byte   op1;
            public byte   op2;
            public byte   op3;
            public byte   op4;
            public ushort Imm;
            public uint   Target;

            public OpcodeDesc(uint Opcode)
            {
                this.Opcode = Opcode;

                op1                = (byte)  ((Opcode & 0b00000011111000000000000000000000) >> 21);
                op2                = (byte)  ((Opcode & 0b00000000000111110000000000000000) >> 16);
                op3                = (byte)  ((Opcode & 0b00000000000000001111100000000000) >> 11);
                op4                = (byte)  ((Opcode & 0b00000000000000000000011111000000) >> 6);
                Imm                = (ushort)((Opcode & 0b00000000000000001111111111111111));
                Target             =         ((Opcode & 0b00000011111111111111111111111111));
            }
        }

        private static List<InstInfo> AllInsts;
        private static int FastLookupSize = 0x1000;
        private static InstInfo[][] FastLookup;
        private static readonly object InitLock = new object();
        private static int _initialized;

        public static void Init()
        {
            lock (InitLock)
            {
                if (Volatile.Read(ref _initialized) != 0)
                    return;

                AllInsts = new List<InstInfo>();
                InstInfo[][] nextFastLookup = new InstInfo[FastLookupSize][];

            /*
            Note:
            The Formatting for the Assembly string goes as follows:
                {0} is the op1 part of a OpcodeDesc
                {1} is the op2 part of a OpcodeDesc
                {2} is the op3 part of a OpcodeDesc
                {3} is the op4 part of a OpcodeDesc
                {4} is the Imm part of a OpcodeDesc
                {5} is the Target part of a OpcodeDesc
            */

            // Other Instructions
            SetOpcode("00000000000000000000000000000000", InstInterp.NOP, "NOP");

            // Load / Store Instructions
            SetOpcode("100000XXXXXXXXXXXXXXXXXXXXXXXXXX", InstInterp.LB,  "LB R[{1}], 0x{4:x4}(R[{0}])");
            SetOpcode("100100XXXXXXXXXXXXXXXXXXXXXXXXXX", InstInterp.LBU, "LBU R[{1}], 0x{4:x4}(R[{0}])");
            SetOpcode("110111XXXXXXXXXXXXXXXXXXXXXXXXXX", InstInterp.LD,  "LD R[{1}], 0x{4:x4}(R[{0}])");
            SetOpcode("011010XXXXXXXXXXXXXXXXXXXXXXXXXX", InstInterp.LDL, "LDL R[{1}], 0x{4:x4}(R[{0}])");
            SetOpcode("011011XXXXXXXXXXXXXXXXXXXXXXXXXX", InstInterp.LDR, "LDR R[{1}], 0x{4:x4}(R[{0}])");
            SetOpcode("100001XXXXXXXXXXXXXXXXXXXXXXXXXX", InstInterp.LH,  "LH R[{1}], 0x{4:x4}(R[{0}])");
            SetOpcode("100101XXXXXXXXXXXXXXXXXXXXXXXXXX", InstInterp.LHU, "LHU R[{1}], 0x{4:x4}(R[{0}])");
            SetOpcode("100011XXXXXXXXXXXXXXXXXXXXXXXXXX", InstInterp.LW,  "LW R[{1}], 0x{4:x4}(R[{0}])");
            SetOpcode("100010XXXXXXXXXXXXXXXXXXXXXXXXXX", InstInterp.LWL, "LWL R[{1}], 0x{4:x4}(R[{0}])");
            SetOpcode("100110XXXXXXXXXXXXXXXXXXXXXXXXXX", InstInterp.LWR, "LWR R[{1}], 0x{4:x4}(R[{0}])");
            SetOpcode("100111XXXXXXXXXXXXXXXXXXXXXXXXXX", InstInterp.LWU, "LWU R[{1}], 0x{4:x4}(R[{0}])");
            SetOpcode("110000XXXXXXXXXXXXXXXXXXXXXXXXXX", InstInterp.LL,  "LL R[{1}], 0x{4:x4}(R[{0}])");
            SetOpcode("101000XXXXXXXXXXXXXXXXXXXXXXXXXX", InstInterp.SB,  "SB R[{1}], 0x{4:x4}(R[{0}])");
            SetOpcode("111111XXXXXXXXXXXXXXXXXXXXXXXXXX", InstInterp.SD,  "SD R[{1}], 0x{4:x4}(R[{0}])");
            SetOpcode("101100XXXXXXXXXXXXXXXXXXXXXXXXXX", InstInterp.SDL, "SDL R[{1}], 0x{4:x4}(R[{0}])");
            SetOpcode("101101XXXXXXXXXXXXXXXXXXXXXXXXXX", InstInterp.SDR, "SDR R[{1}], 0x{4:x4}(R[{0}])");
            SetOpcode("101001XXXXXXXXXXXXXXXXXXXXXXXXXX", InstInterp.SH,  "SH R[{1}], 0x{4:x4}(R[{0}])");
            SetOpcode("101011XXXXXXXXXXXXXXXXXXXXXXXXXX", InstInterp.SW,  "SW R[{1}], 0x{4:x4}(R[{0}])");
            SetOpcode("101010XXXXXXXXXXXXXXXXXXXXXXXXXX", InstInterp.SWL, "SWL R[{1}], 0x{4:x4}(R[{0}])");
            SetOpcode("101110XXXXXXXXXXXXXXXXXXXXXXXXXX", InstInterp.SWR, "SWR R[{1}], 0x{4:x4}(R[{0}])");
            SetOpcode("111000XXXXXXXXXXXXXXXXXXXXXXXXXX", InstInterp.SC,  "SC R[{1}], 0x{4:x4}(R[{0}])");
            SetOpcode("110001XXXXXXXXXXXXXXXXXXXXXXXXXX", InstInterp.LWC1, "LWC1 F[{1}], 0x{4:x4}(R[{0}])");
            SetOpcode("111001XXXXXXXXXXXXXXXXXXXXXXXXXX", InstInterp.SWC1, "SWC1 F[{1}], 0x{4:x4}(R[{0}])");
            SetOpcode("110101XXXXXXXXXXXXXXXXXXXXXXXXXX", InstInterp.LDC1, "LDC1 F[{1}], 0x{4:x4}(R[{0}])");
            SetOpcode("111101XXXXXXXXXXXXXXXXXXXXXXXXXX", InstInterp.SDC1, "SDC1 F[{1}], 0x{4:x4}(R[{0}])");

            // Arithmetic Instructions
            SetOpcode("000000XXXXXXXXXXXXXXX00000100000", InstInterp.ADD,    "ADD R[{2}], R[{0}], R[{1}]");
            SetOpcode("001000XXXXXXXXXXXXXXXXXXXXXXXXXX", InstInterp.ADDI,   "ADDI R[{1}], R[{0}], 0x{4:x4}");
            SetOpcode("001001XXXXXXXXXXXXXXXXXXXXXXXXXX", InstInterp.ADDIU,  "ADDIU R[{1}], R[{0}], 0x{4:x4}");
            SetOpcode("011000XXXXXXXXXXXXXXXXXXXXXXXXXX", InstInterp.DADDI,  "DADDI R[{1}], R[{0}], 0x{4:x4}");
            SetOpcode("011001XXXXXXXXXXXXXXXXXXXXXXXXXX", InstInterp.DADDIU, "DADDIU R[{1}], R[{0}], 0x{4:x4}");
            SetOpcode("000000XXXXXXXXXXXXXXX00000100001", InstInterp.ADDU,   "ADDU R[{2}], R[{0}], R[{1}]");
            SetOpcode("000000XXXXXXXXXXXXXXX00000101100", InstInterp.DADD,   "DADD R[{2}], R[{0}], R[{1}]");
            SetOpcode("000000XXXXXXXXXXXXXXX00000101101", InstInterp.DADDU,  "DADDU R[{2}], R[{0}], R[{1}]");
            SetOpcode("000000XXXXXXXXXXXXXXX00000100100", InstInterp.AND,    "AND R[{2}], R[{0}], R[{1}]");
            SetOpcode("000000XXXXXXXXXXXXXXX00000100111", InstInterp.NOR,    "NOR R[{2}], R[{0}], R[{1}]");
            SetOpcode("001100XXXXXXXXXXXXXXXXXXXXXXXXXX", InstInterp.ANDI,   "ANDI R[{1}], R[{0}], 0x{4:x4}");
            SetOpcode("000000XXXXXXXXXXXXXXX00000100010", InstInterp.SUB,    "SUB R[{2}], R[{0}], R[{1}]");
            SetOpcode("000000XXXXXXXXXXXXXXX00000100011", InstInterp.SUBU,   "SUBU R[{2}], R[{0}], R[{1}]");
            SetOpcode("000000XXXXXXXXXXXXXXX00000101110", InstInterp.DSUB,   "DSUB R[{2}], R[{0}], R[{1}]");
            SetOpcode("000000XXXXXXXXXXXXXXX00000101111", InstInterp.DSUBU,  "DSUBU R[{2}], R[{0}], R[{1}]");
            SetOpcode("000000XXXXXXXXXXXXXXX00000100110", InstInterp.XOR,    "XOR R[{2}], R[{0}], R[{1}]");
            SetOpcode("001110XXXXXXXXXXXXXXXXXXXXXXXXXX", InstInterp.XORI,   "XORI R[{1}], R[{0}], 0x{4:x4}");
            SetOpcode("00111100000XXXXXXXXXXXXXXXXXXXXX", InstInterp.LUI,    "LUI R[{1}], 0x{4:x4}");
            SetOpcode("0000000000000000XXXXX00000010000", InstInterp.MFHI,   "MFHI R[{2}]");
            SetOpcode("0000000000000000XXXXX00000010010", InstInterp.MFLO,   "MFLO R[{2}]");
            SetOpcode("000000XXXXXXXXXXXXXXX00000100101", InstInterp.OR,     "OR R[{2}], R[{0}], R[{1}]");
            SetOpcode("001101XXXXXXXXXXXXXXXXXXXXXXXXXX", InstInterp.ORI,    "ORI R[{1}], R[{0}], 0x{4:x4}");
            SetOpcode("000000XXXXX000000000000000010011", InstInterp.MTLO,   "MTLO R[{0}]");
            SetOpcode("000000XXXXX000000000000000010001", InstInterp.MTHI,   "MTHI R[{0}]");
            SetOpcode("000000XXXXXXXXXX0000000000011000", InstInterp.MULT,   "MULT R[{0}], R[{1}]",   5);
            SetOpcode("000000XXXXXXXXXX0000000000011001", InstInterp.MULTU,  "MULTU R[{0}], R[{1}]",  5);
            SetOpcode("000000XXXXXXXXXX0000000000011101", InstInterp.DMULTU, "DMULTU R[{0}], R[{1}]", 8);
            SetOpcode("000000XXXXXXXXXX0000000000011010", InstInterp.DIV,    "DIV R[{0}], R[{1}]",    8);
            SetOpcode("000000XXXXXXXXXX0000000000011011", InstInterp.DIVU,   "DIVU R[{0}], R[{1}]",   8);
            SetOpcode("000000XXXXXXXXXX0000000000011110", InstInterp.DDIV,   "DDIV R[{0}], R[{1}]",   8);
            SetOpcode("000000XXXXXXXXXX0000000000011111", InstInterp.DDIVU,  "DDIVU R[{0}], R[{1}]",  8);
            SetOpcode("00000000000XXXXXXXXXXXXXXX000000", InstInterp.SLL,    "SLL R[{2}], R[{1}], 0x{3:x2}");
            SetOpcode("000000XXXXXXXXXXXXXXX00000000100", InstInterp.SLLV,   "SLLV R[{2}], R[{1}], R[{0}]");
            SetOpcode("000000XXXXXXXXXXXXXXX00000010100", InstInterp.DSLLV,  "DSLLV R[{2}], R[{1}], R[{0}]");
            SetOpcode("00000000000XXXXXXXXXXXXXXX000011", InstInterp.SRA,    "SRA R[{2}], R[{1}], 0x{3:x2}");
            SetOpcode("00000000000XXXXXXXXXXXXXXX000010", InstInterp.SRL,    "SRL R[{2}], R[{1}], 0x{3:x2}");
            SetOpcode("000000XXXXXXXXXXXXXXX00000000110", InstInterp.SRLV,   "SRLV R[{2}], R[{1}], R[{0}]");
            SetOpcode("00000000000XXXXXXXXXXXXXXX111000", InstInterp.DSLL,   "DSLL R[{2}], R[{1}], 0x{3:x2}");
            SetOpcode("00000000000XXXXXXXXXXXXXXX111010", InstInterp.DSRL,   "DSRL R[{2}], R[{1}], 0x{3:x2}");
            SetOpcode("00000000000XXXXXXXXXXXXXXX111011", InstInterp.DSRA,   "DSRA R[{2}], R[{1}], 0x{3:x2}");
            SetOpcode("00000000000XXXXXXXXXXXXXXX111100", InstInterp.DSLL32, "DSLL32 R[{2}], R[{1}], 0x{3:x2}");
            SetOpcode("00000000000XXXXXXXXXXXXXXX111110", InstInterp.DSRL32, "DSRL32 R[{2}], R[{1}], 0x{3:x2}");
            SetOpcode("00000000000XXXXXXXXXXXXXXX111111", InstInterp.DSRA32, "DSRA32 R[{2}], R[{1}], 0x{3:x2}");
            SetOpcode("000000XXXXXXXXXXXXXXX00000010110", InstInterp.DSRLV,  "DSRLV R[{2}], R[{1}], R[{0}]");
            SetOpcode("000000XXXXXXXXXXXXXXX00000010111", InstInterp.DSRAV,  "DSRAV R[{2}], R[{1}], R[{0}]");
            SetOpcode("001010XXXXXXXXXXXXXXXXXXXXXXXXXX", InstInterp.SLTI,   "SLTI R[{1}], R[{0}], 0x{4:x4}");
            SetOpcode("001011XXXXXXXXXXXXXXXXXXXXXXXXXX", InstInterp.SLTIU,  "SLTIU R[{1}], R[{0}], 0x{4:x4}");
            SetOpcode("000000XXXXXXXXXXXXXXX00000101010", InstInterp.SLT,    "SLT R[{2}], R[{0}], R[{1}]");
            SetOpcode("000000XXXXXXXXXXXXXXX00000101011", InstInterp.SLTU,   "SLTU R[{2}], R[{0}], R[{1}]");

            // Branch / Jump Instructions
            SetOpcode("000100XXXXXXXXXXXXXXXXXXXXXXXXXX", InstInterp.BEQ,    "BEQ R[{0}], R[{1}], 0x{4:x4}");
            SetOpcode("010100XXXXXXXXXXXXXXXXXXXXXXXXXX", InstInterp.BEQL,   "BEQL R[{0}], R[{1}], 0x{4:x4}");
            SetOpcode("000001XXXXX00001XXXXXXXXXXXXXXXX", InstInterp.BGEZ,   "BGEZ R[{0}], 0x{4:x4}");
            SetOpcode("000001XXXXX10001XXXXXXXXXXXXXXXX", InstInterp.BGEZAL, "BGEZAL R[{0}], 0x{4:x4}");
            SetOpcode("000001XXXXX00011XXXXXXXXXXXXXXXX", InstInterp.BGEZL,  "BGEZL R[{0}], 0x{4:x4}");
            SetOpcode("000001XXXXX10011XXXXXXXXXXXXXXXX", InstInterp.BGEZALL,"BGEZALL R[{0}], 0x{4:x4}");
            SetOpcode("000111XXXXX00000XXXXXXXXXXXXXXXX", InstInterp.BGTZ,   "BGTZ R[{0}], 0x{4:x4}");
            SetOpcode("000110XXXXX00000XXXXXXXXXXXXXXXX", InstInterp.BLEZ,   "BLEZ R[{0}], 0x{4:x4}");
            SetOpcode("010110XXXXX00000XXXXXXXXXXXXXXXX", InstInterp.BLEZL,  "BLEZL R[{0}], 0x{4:x4}");
            SetOpcode("000001XXXXX00000XXXXXXXXXXXXXXXX", InstInterp.BLTZ,   "BLTZ R[{0}], 0x{4:x4}");
            SetOpcode("000001XXXXX10000XXXXXXXXXXXXXXXX", InstInterp.BLTZAL, "BLTZAL R[{0}], 0x{4:x4}");
            SetOpcode("000001XXXXX00010XXXXXXXXXXXXXXXX", InstInterp.BLTZL,  "BLTZL R[{0}], 0x{4:x4}");
            SetOpcode("000001XXXXX10010XXXXXXXXXXXXXXXX", InstInterp.BLTZALL,"BLTZALL R[{0}], 0x{4:x4}");
            SetOpcode("000101XXXXXXXXXXXXXXXXXXXXXXXXXX", InstInterp.BNE,    "BNE R[{0}], R[{1}], 0x{4:x4}");
            SetOpcode("010101XXXXXXXXXXXXXXXXXXXXXXXXXX", InstInterp.BNEL,   "BNEL R[{0}], R[{1}], 0x{4:x4}");
            SetOpcode("000010XXXXXXXXXXXXXXXXXXXXXXXXXX", InstInterp.J,      "J 0x{5:x8}");
            SetOpcode("000011XXXXXXXXXXXXXXXXXXXXXXXXXX", InstInterp.JAL,    "JAL 0x{5:x8}");
            SetOpcode("000000XXXXX000000000000000001000", InstInterp.JR,     "JR R[{0}]");
            SetOpcode("000000XXXXX00000XXXXX00000001001", InstInterp.JALR,   "JALR R[{2}], R[{0}]");

            // COP0 Instructions
            SetOpcode("01000000000XXXXXXXXXXXXXXXXXXXXX", InstInterp.MFC0,  "MFC0 R[{1}], CP0R[{2}]");
            SetOpcode("01000000001XXXXXXXXXXXXXXXXXXXXX", InstInterp.DMFC0, "DMFC0 R[{1}], CP0R[{2}]");
            SetOpcode("01000000010XXXXXXXXXXXXXXXXXXXXX", InstInterp.CFC0,  "CFC0 R[{1}], CP0R[{2}]");
            SetOpcode("01000000100XXXXXXXXXXXXXXXXXXXXX", InstInterp.MTC0,  "MTC0 R[{1}], CP0R[{2}]");
            SetOpcode("01000000101XXXXXXXXXXXXXXXXXXXXX", InstInterp.DMTC0, "DMTC0 R[{1}], CP0R[{2}]");
            SetOpcode("01000000110XXXXXXXXXXXXXXXXXXXXX", InstInterp.CTC0,  "CTC0 R[{1}], CP0R[{2}]");
            SetOpcode("101111XXXXXXXXXXXXXXXXXXXXXXXXXX", InstInterp.CACHE, "CACHE 0x{1:x2}, 0x{4:x4}(R[{0}])");

            // COP1 Instructions
            SetOpcode("01000100000XXXXXXXXXXXXXXXXXXXXX", InstInterp.MFC1, "MFC1 R[{1}], CP1R[{2}]");
            SetOpcode("01000100001XXXXXXXXXXXXXXXXXXXXX", InstInterp.DMFC1, "DMFC1 R[{1}], CP1R[{2}]");
            SetOpcode("01000100010XXXXXXXXXX00000000000", InstInterp.CFC1, "CFC1 R[{1}], CP1R[{2}]");
            SetOpcode("01000100100XXXXXXXXXXXXXXXXXXXXX", InstInterp.MTC1, "MTC1 R[{1}], CP1R[{2}]");
            SetOpcode("01000100101XXXXXXXXXXXXXXXXXXXXX", InstInterp.DMTC1, "DMTC1 R[{1}], CP1R[{2}]");
            SetOpcode("01000100110XXXXXXXXXX00000000000", InstInterp.CTC1, "CTC1 R[{1}], CP1R[{2}]");
            SetOpcode("0100010100000000XXXXXXXXXXXXXXXX", InstInterp.BC1F, "BC1F 0x{4:x4}");
            SetOpcode("0100010100000001XXXXXXXXXXXXXXXX", InstInterp.BC1T, "BC1T 0x{4:x4}");
            SetOpcode("0100010100000010XXXXXXXXXXXXXXXX", InstInterp.BC1FL, "BC1FL 0x{4:x4}");
            SetOpcode("0100010100000011XXXXXXXXXXXXXXXX", InstInterp.BC1TL, "BC1TL 0x{4:x4}");
            SetOpcode("01000110000XXXXXXXXXXXXXXX000000", InstInterp.ADD_S, "ADD.S F[{3}], F[{2}], F[{1}]");
            SetOpcode("01000110000XXXXXXXXXXXXXXX000001", InstInterp.SUB_S, "SUB.S F[{3}], F[{2}], F[{1}]");
            SetOpcode("01000110000XXXXXXXXXXXXXXX000010", InstInterp.MUL_S, "MUL.S F[{3}], F[{2}], F[{1}]");
            SetOpcode("01000110000XXXXXXXXXXXXXXX000011", InstInterp.DIV_S, "DIV.S F[{3}], F[{2}], F[{1}]");
            SetOpcode("01000110000XXXXXXXXXXXXXXX000110", InstInterp.MOV_S, "MOV.S F[{3}], F[{2}]");
            SetOpcode("01000110000XXXXXXXXXXXXXXX000111", InstInterp.NEG_S, "NEG.S F[{3}], F[{2}]");
            SetOpcode("01000110000XXXXXXXXXXXXXXX100001", InstInterp.CVT_D_S, "CVT.D.S F[{3}], F[{2}]");
            SetOpcode("01000110000XXXXXXXXXXXXXXX100100", InstInterp.CVT_W_S, "CVT.W.S F[{3}], F[{2}]");
            SetOpcode("01000110000XXXXXXXXXXXXXXX100101", InstInterp.CVT_L_S, "CVT.L.S F[{3}], F[{2}]");
            SetOpcode("01000110001XXXXXXXXXXXXXXX000000", InstInterp.ADD_D, "ADD.D F[{3}], F[{2}], F[{1}]");
            SetOpcode("01000110001XXXXXXXXXXXXXXX000001", InstInterp.SUB_D, "SUB.D F[{3}], F[{2}], F[{1}]");
            SetOpcode("01000110001XXXXXXXXXXXXXXX000010", InstInterp.MUL_D, "MUL.D F[{3}], F[{2}], F[{1}]");
            SetOpcode("01000110001XXXXXXXXXXXXXXX000011", InstInterp.DIV_D, "DIV.D F[{3}], F[{2}], F[{1}]");
            SetOpcode("01000110001XXXXXXXXXXXXXXX000110", InstInterp.MOV_D, "MOV.D F[{3}], F[{2}]");
            SetOpcode("01000110001XXXXXXXXXXXXXXX000111", InstInterp.NEG_D, "NEG.D F[{3}], F[{2}]");
            SetOpcode("01000110001XXXXXXXXXXXXXXX100000", InstInterp.CVT_S_D, "CVT.S.D F[{3}], F[{2}]");
            SetOpcode("01000110001XXXXXXXXXXXXXXX100100", InstInterp.CVT_W_D, "CVT.W.D F[{3}], F[{2}]");
            SetOpcode("01000110001XXXXXXXXXXXXXXX100101", InstInterp.CVT_L_D, "CVT.L.D F[{3}], F[{2}]");
            SetOpcode("01000110100XXXXXXXXXXXXXXX100000", InstInterp.CVT_S_W, "CVT.S.W F[{3}], F[{2}]");
            SetOpcode("01000110100XXXXXXXXXXXXXXX100001", InstInterp.CVT_D_W, "CVT.D.W F[{3}], F[{2}]");
            SetOpcode("01000110101XXXXXXXXXXXXXXX100000", InstInterp.CVT_S_L, "CVT.S.L F[{3}], F[{2}]");
            SetOpcode("01000110101XXXXXXXXXXXXXXX100001", InstInterp.CVT_D_L, "CVT.D.L F[{3}], F[{2}]");
            SetOpcode("01000110000XXXXXXXXXXXXXXX110010", InstInterp.C_EQ_S, "C.EQ.S F[{2}], F[{1}]");
            SetOpcode("01000110000XXXXXXXXXXXXXXX111100", InstInterp.C_LT_S, "C.LT.S F[{2}], F[{1}]");
            SetOpcode("01000110000XXXXXXXXXXXXXXX111110", InstInterp.C_LE_S, "C.LE.S F[{2}], F[{1}]");
            SetOpcode("01000110001XXXXXXXXXXXXXXX110010", InstInterp.C_EQ_D, "C.EQ.D F[{2}], F[{1}]");
            SetOpcode("01000110001XXXXXXXXXXXXXXX111100", InstInterp.C_LT_D, "C.LT.D F[{2}], F[{1}]");
            SetOpcode("01000110001XXXXXXXXXXXXXXX111110", InstInterp.C_LE_D, "C.LE.D F[{2}], F[{1}]");

            // SPECIAL Instructions
            SetOpcode("000000XXXXXXXXXXXXXXXXXXXX001100", InstInterp.SYSCALL, "SYSCALL");
            SetOpcode("000000XXXXXXXXXXXXXXXXXXXX001101", InstInterp.BREAK,   "BREAK");
            // According to the manual, the SYNC instruction is executed as a NOP on the VR4300.
            SetOpcode("00000000000000000000000000001111", InstInterp.NOP,     "SYNC");

            // TLB Instructions
            SetOpcode("01000010000000000000000000000001", InstInterp.TLBR,  "TLBR");
            SetOpcode("01000010000000000000000000000010", InstInterp.TLBWI, "TLBWI");
            SetOpcode("01000010000000000000000000000110", InstInterp.TLBWR, "TLBWR");
            SetOpcode("01000010000000000000000000001000", InstInterp.TLBP,  "TLBP");
            SetOpcode("01000010000000000000000000011000", InstInterp.ERET,  "ERET");

            // Credit to Ryujinx for the FastLookup code!
            // https://github.com/Ryujinx/Ryujinx/blob/master/ChocolArm64/AOpCodeTable.cs

            List<InstInfo>[] Tmp = new List<InstInfo>[FastLookupSize];

            for (int i = 0; i < FastLookupSize; ++i)
                Tmp[i] = new List<InstInfo>();

            foreach (InstInfo Inst in AllInsts)
            {
                int Mask  = ToFastLookupIndex((int)Inst.Mask);
                int Value = ToFastLookupIndex((int)Inst.Value);

                for (int i = 0; i < FastLookupSize; ++i)
                    if ((i & Mask) == Value) Tmp[i].Add(Inst);
            }

                for (int i = 0; i < FastLookupSize; ++i)
                    nextFastLookup[i] = Tmp[i].ToArray();

                FastLookup = nextFastLookup;
                Volatile.Write(ref _initialized, 1);
            }
        }

        private static int ToFastLookupIndex(int Value)
        {
            return ((Value >> 10) & 0x00F) | ((Value >> 18) & 0xFF0);
        }

        public static InstInfo GetOpcodeInfo(uint Opcode)
        {
            if (Volatile.Read(ref _initialized) == 0)
                Init();

            InstInfo[][] lookup = FastLookup;
            int idx = ToFastLookupIndex((int)Opcode);
            IEnumerable<InstInfo> list = lookup[idx] ?? Array.Empty<InstInfo>();
            return GetOpcodeInfoFromList(list, Opcode);
        }

        public static InstInfo GetOpcodeInfoFromList(IEnumerable<InstInfo> InstList, uint Opcode)
        {
            foreach (InstInfo info in InstList)
                if ((Opcode & info.Mask) == info.Value)
                    return info;

            throw new NotImplementedException($"Instruction \"{Convert.ToString(Opcode, 2).PadLeft(32, '0')}\" isn't a implemented MIPS instruction.  PC: 0x{Registers.R4300.PC:x8}");
        }

        private static void SetOpcode(string Encoding, InstInterp.InterpretOpcode Interpret, string FormattedASM = "", uint Cycles = 1)
        {
            uint Bit   = (uint)Encoding.Length - 1;
            uint Value = 0;
            uint XMask = 0;

            for (int Index = 0; Index < Encoding.Length; ++Index, --Bit)
            {
                char Chr = Encoding.ToUpper()[Index];

                if (Chr == '1')
                    Value |= (uint)(1 << (int)Bit);
                else if (Chr == 'X')
                    XMask |= (uint)(1 << (int)Bit);
                else if (Chr != '0')
                    throw new ArgumentException(nameof(Encoding));
            }

            XMask = ~XMask;

            AllInsts.Add(new InstInfo(XMask, Value, Interpret, FormattedASM, Cycles));
        }
    }
}
