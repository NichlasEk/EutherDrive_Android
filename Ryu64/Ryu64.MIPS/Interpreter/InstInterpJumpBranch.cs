using System;

namespace Ryu64.MIPS
{
    public partial class InstInterp
    {
        private static readonly bool TraceBranchWindow =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_N64_BRANCH_WINDOW"), "1", StringComparison.Ordinal);

        private static ulong SignExtendPcToReg(uint pc)
        {
            return unchecked((ulong)(long)(int)pc);
        }

        private static uint BranchAdjustJ(ushort imm)
        {
            return unchecked((uint)((((int)(short)imm) << 2) - 4));
        }

        public static void BEQ(OpcodeTable.OpcodeDesc Desc)
        {
            bool take = (long)Registers.R4300.Reg[Desc.op1] == (long)Registers.R4300.Reg[Desc.op2];
            if (TraceBranchWindow && Registers.R4300.PC >= 0x80322E18u && Registers.R4300.PC <= 0x80322E20u)
            {
                Console.WriteLine(
                    $"[N64BEQ] pc=0x{Registers.R4300.PC:x8} rs={Desc.op1} rt={Desc.op2} " +
                    $"rsv=0x{Registers.R4300.Reg[Desc.op1]:x16} rtv=0x{Registers.R4300.Reg[Desc.op2]:x16} imm=0x{Desc.Imm:x4}");
            }
            Registers.R4300.PC += 4;
            R4300.ExecuteDelaySlot();
            if (take)
                Registers.R4300.PC += BranchAdjustJ(Desc.Imm);
        }

        public static void BEQL(OpcodeTable.OpcodeDesc Desc)
        {
            bool take = (long)Registers.R4300.Reg[Desc.op1] == (long)Registers.R4300.Reg[Desc.op2];
            Registers.R4300.PC += 4;
            if (take)
            {
                R4300.ExecuteDelaySlot();
                Registers.R4300.PC += BranchAdjustJ(Desc.Imm);
            }
            else Registers.R4300.PC += 4;
        }

        public static void BGEZ(OpcodeTable.OpcodeDesc Desc)
        {
            bool take = (long)Registers.R4300.Reg[Desc.op1] >= 0;
            Registers.R4300.PC += 4;
            R4300.ExecuteDelaySlot();
            if (take)
                Registers.R4300.PC += BranchAdjustJ(Desc.Imm);
        }

        public static void BGEZAL(OpcodeTable.OpcodeDesc Desc)
        {
            bool take = (long)Registers.R4300.Reg[Desc.op1] >= 0;
            uint link = Registers.R4300.PC + 8;
            Registers.R4300.PC += 4;
            Registers.R4300.Reg[31] = SignExtendPcToReg(link);
            R4300.ExecuteDelaySlot();
            if (take)
                Registers.R4300.PC += BranchAdjustJ(Desc.Imm);
        }

        public static void BGEZL(OpcodeTable.OpcodeDesc Desc)
        {
            bool take = (long)Registers.R4300.Reg[Desc.op1] >= 0;
            Registers.R4300.PC += 4;
            if (take)
            {
                R4300.ExecuteDelaySlot();
                Registers.R4300.PC += BranchAdjustJ(Desc.Imm);
            }
            else
            {
                Registers.R4300.PC += 4;
            }
        }

        public static void BGEZALL(OpcodeTable.OpcodeDesc Desc)
        {
            bool take = (long)Registers.R4300.Reg[Desc.op1] >= 0;
            uint link = Registers.R4300.PC + 8;
            Registers.R4300.PC += 4;
            Registers.R4300.Reg[31] = SignExtendPcToReg(link);
            if (take)
            {
                R4300.ExecuteDelaySlot();
                Registers.R4300.PC += BranchAdjustJ(Desc.Imm);
            }
            else
            {
                Registers.R4300.PC += 4;
            }
        }

        public static void BGTZ(OpcodeTable.OpcodeDesc Desc)
        {
            bool take = (long)Registers.R4300.Reg[Desc.op1] > 0;
            Registers.R4300.PC += 4;
            R4300.ExecuteDelaySlot();
            if (take)
                Registers.R4300.PC += BranchAdjustJ(Desc.Imm);
        }

        public static void BLEZ(OpcodeTable.OpcodeDesc Desc)
        {
            bool take = (long)Registers.R4300.Reg[Desc.op1] <= 0;
            Registers.R4300.PC += 4;
            R4300.ExecuteDelaySlot();
            if (take)
                Registers.R4300.PC += BranchAdjustJ(Desc.Imm);
        }

        public static void BLEZL(OpcodeTable.OpcodeDesc Desc)
        {
            bool take = (long)Registers.R4300.Reg[Desc.op1] <= 0;
            Registers.R4300.PC += 4;
            if (take)
            {
                R4300.ExecuteDelaySlot();
                Registers.R4300.PC += BranchAdjustJ(Desc.Imm);
            }
            else Registers.R4300.PC += 4;
        }

        public static void BLTZ(OpcodeTable.OpcodeDesc Desc)
        {
            bool take = (long)Registers.R4300.Reg[Desc.op1] < 0;
            Registers.R4300.PC += 4;
            R4300.ExecuteDelaySlot();
            if (take)
                Registers.R4300.PC += BranchAdjustJ(Desc.Imm);
        }

        public static void BLTZAL(OpcodeTable.OpcodeDesc Desc)
        {
            bool take = (long)Registers.R4300.Reg[Desc.op1] < 0;
            uint link = Registers.R4300.PC + 8;
            Registers.R4300.PC += 4;
            Registers.R4300.Reg[31] = SignExtendPcToReg(link);
            R4300.ExecuteDelaySlot();
            if (take)
                Registers.R4300.PC += BranchAdjustJ(Desc.Imm);
        }

        public static void BLTZL(OpcodeTable.OpcodeDesc Desc)
        {
            bool take = (long)Registers.R4300.Reg[Desc.op1] < 0;
            Registers.R4300.PC += 4;
            if (take)
            {
                R4300.ExecuteDelaySlot();
                Registers.R4300.PC += BranchAdjustJ(Desc.Imm);
            }
            else
            {
                Registers.R4300.PC += 4;
            }
        }

        public static void BLTZALL(OpcodeTable.OpcodeDesc Desc)
        {
            bool take = (long)Registers.R4300.Reg[Desc.op1] < 0;
            uint link = Registers.R4300.PC + 8;
            Registers.R4300.PC += 4;
            Registers.R4300.Reg[31] = SignExtendPcToReg(link);
            if (take)
            {
                R4300.ExecuteDelaySlot();
                Registers.R4300.PC += BranchAdjustJ(Desc.Imm);
            }
            else
            {
                Registers.R4300.PC += 4;
            }
        }

        public static void BNE(OpcodeTable.OpcodeDesc Desc)
        {
            bool take = (long)Registers.R4300.Reg[Desc.op1] != (long)Registers.R4300.Reg[Desc.op2];
            Registers.R4300.PC += 4;
            R4300.ExecuteDelaySlot();
            if (take)
                Registers.R4300.PC += BranchAdjustJ(Desc.Imm);
        }

        public static void BNEL(OpcodeTable.OpcodeDesc Desc)
        {
            bool take = (long)Registers.R4300.Reg[Desc.op1] != (long)Registers.R4300.Reg[Desc.op2];
            Registers.R4300.PC += 4;
            if (take)
            {
                R4300.ExecuteDelaySlot();
                Registers.R4300.PC += BranchAdjustJ(Desc.Imm);
            }
            else Registers.R4300.PC += 4;
        }

        public static void J(OpcodeTable.OpcodeDesc Desc)
        {
            uint branchPc = Registers.R4300.PC;
            Registers.R4300.PC += 4;
            R4300.ExecuteDelaySlot();
            Registers.R4300.PC = (branchPc & 0xF0000000) | (Desc.Target << 2);
        }

        public static void JAL(OpcodeTable.OpcodeDesc Desc)
        {
            uint branchPc = Registers.R4300.PC;
            uint link = branchPc + 8;
            Registers.R4300.PC += 4;
            Registers.R4300.Reg[31] = SignExtendPcToReg(link);
            R4300.ExecuteDelaySlot();
            Registers.R4300.PC = (branchPc & 0xF0000000) | (Desc.Target << 2);
        }

        public static void JR(OpcodeTable.OpcodeDesc Desc)
        {
            uint target = (uint)Registers.R4300.Reg[Desc.op1];
            Registers.R4300.PC += 4;
            R4300.ExecuteDelaySlot();
            Registers.R4300.PC = target;
        }

        public static void JALR(OpcodeTable.OpcodeDesc Desc)
        {
            uint link = Registers.R4300.PC + 8;
            uint target = (uint)Registers.R4300.Reg[Desc.op1];
            Registers.R4300.PC += 4;
            Registers.R4300.Reg[Desc.op3] = SignExtendPcToReg(link);
            R4300.ExecuteDelaySlot();
            Registers.R4300.PC = target;
        }
    }
}
