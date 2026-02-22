namespace Ryu64.MIPS
{
    public partial class InstInterp
    {
        private static uint EffectiveAddress(OpcodeTable.OpcodeDesc desc)
        {
            long baseAddr = (long)Registers.R4300.Reg[desc.op1];
            long imm = (short)desc.Imm;
            return (uint)(baseAddr + imm);
        }

        public static void LB(OpcodeTable.OpcodeDesc Desc)
        {
            uint addr = EffectiveAddress(Desc);
            Registers.R4300.Reg[Desc.op2] = (ulong)R4300.memory.ReadInt8(addr);
            Registers.R4300.PC += 4;
        }

        public static void LBU(OpcodeTable.OpcodeDesc Desc)
        {
            uint addr = EffectiveAddress(Desc);
            Registers.R4300.Reg[Desc.op2] = R4300.memory.ReadUInt8(addr);
            Registers.R4300.PC += 4;
        }

        public static void LD(OpcodeTable.OpcodeDesc Desc)
        {
            uint addr = EffectiveAddress(Desc);
            Registers.R4300.Reg[Desc.op2] = (ulong)R4300.memory.ReadInt64(addr);
            Registers.R4300.PC += 4;
        }

        public static void LDL(OpcodeTable.OpcodeDesc Desc)
        {
            uint addr = EffectiveAddress(Desc);
            Registers.R4300.Reg[Desc.op2] &= 0x00000000FFFFFFFF;
            Registers.R4300.Reg[Desc.op2] |= (ulong)R4300.memory.ReadInt64(addr) << 32;
            Registers.R4300.PC += 4;
        }

        public static void LDR(OpcodeTable.OpcodeDesc Desc)
        {
            uint addr = EffectiveAddress(Desc);
            Registers.R4300.Reg[Desc.op2] >>= 32;
            Registers.R4300.Reg[Desc.op2] |= (ulong)R4300.memory.ReadInt64(addr) >> 32;
            Registers.R4300.PC += 4;
        }

        public static void LH(OpcodeTable.OpcodeDesc Desc)
        {
            uint addr = EffectiveAddress(Desc);
            Registers.R4300.Reg[Desc.op2] = (ulong)R4300.memory.ReadInt16(addr);
            Registers.R4300.PC += 4;
        }

        public static void LHU(OpcodeTable.OpcodeDesc Desc)
        {
            uint addr = EffectiveAddress(Desc);
            Registers.R4300.Reg[Desc.op2] = R4300.memory.ReadUInt16(addr);
            Registers.R4300.PC += 4;
        }

        public static void LW(OpcodeTable.OpcodeDesc Desc)
        {
            uint addr = EffectiveAddress(Desc);
            Registers.R4300.Reg[Desc.op2] = unchecked((ulong)(long)R4300.memory.ReadInt32(addr));
            Registers.R4300.PC += 4;
        }

        public static void LWL(OpcodeTable.OpcodeDesc Desc)
        {
            uint addr = EffectiveAddress(Desc);
            Registers.R4300.Reg[Desc.op2] &= 0xFFFFFFFF0000FFFF;
            Registers.R4300.Reg[Desc.op2] |= (ulong)((uint)R4300.memory.ReadInt32(addr) << 16);
            Registers.R4300.PC += 4;
        }

        public static void LWR(OpcodeTable.OpcodeDesc Desc)
        {
            uint addr = EffectiveAddress(Desc);
            Registers.R4300.Reg[Desc.op2] &= 0xFFFFFFFFFFFF0000;
            Registers.R4300.Reg[Desc.op2] |= (uint)R4300.memory.ReadInt32(addr) >> 16;
            Registers.R4300.PC += 4;
        }

        public static void LWU(OpcodeTable.OpcodeDesc Desc)
        {
            uint addr = EffectiveAddress(Desc);
            Registers.R4300.Reg[Desc.op2] = (uint)R4300.memory.ReadUInt32(addr);
            Registers.R4300.PC += 4;
        }

        public static void LL(OpcodeTable.OpcodeDesc Desc)
        {
            // Treat LL as LW for now.
            LW(Desc);
        }

        public static void SB(OpcodeTable.OpcodeDesc Desc)
        {
            uint addr = EffectiveAddress(Desc);
            R4300.memory.WriteUInt8(addr, (byte)Registers.R4300.Reg[Desc.op2]);
            Registers.R4300.PC += 4;
        }

        public static void SD(OpcodeTable.OpcodeDesc Desc)
        {
            uint addr = EffectiveAddress(Desc);
            R4300.memory.WriteUInt64(addr, Registers.R4300.Reg[Desc.op2]);
            Registers.R4300.PC += 4;
        }

        public static void SDL(OpcodeTable.OpcodeDesc Desc)
        {
            uint addr = EffectiveAddress(Desc);
            R4300.memory.WriteUInt32(addr, (uint)(Registers.R4300.Reg[Desc.op2] >> 32));
            Registers.R4300.PC += 4;
        }

        public static void SDR(OpcodeTable.OpcodeDesc Desc)
        {
            uint addr = EffectiveAddress(Desc);
            R4300.memory.WriteUInt32(addr + 4, (uint)Registers.R4300.Reg[Desc.op2]);
            Registers.R4300.PC += 4;
        }

        public static void SH(OpcodeTable.OpcodeDesc Desc)
        {
            uint addr = EffectiveAddress(Desc);
            R4300.memory.WriteUInt16(addr, (ushort)Registers.R4300.Reg[Desc.op2]);
            Registers.R4300.PC += 4;
        }

        public static void SW(OpcodeTable.OpcodeDesc Desc)
        {
            uint addr = EffectiveAddress(Desc);
            R4300.memory.WriteUInt32(addr, (uint)Registers.R4300.Reg[Desc.op2]);
            Registers.R4300.PC += 4;
        }

        public static void SWL(OpcodeTable.OpcodeDesc Desc)
        {
            uint addr = EffectiveAddress(Desc);
            R4300.memory.WriteInt16(addr, (short)(Registers.R4300.Reg[Desc.op2] >> 16));
            Registers.R4300.PC += 4;
        }

        public static void SWR(OpcodeTable.OpcodeDesc Desc)
        {
            uint addr = EffectiveAddress(Desc);
            R4300.memory.WriteUInt16(addr + 2, (ushort)Registers.R4300.Reg[Desc.op2]);
            Registers.R4300.PC += 4;
        }

        public static void SC(OpcodeTable.OpcodeDesc Desc)
        {
            // Treat SC as SW and report success in rt.
            uint addr = EffectiveAddress(Desc);
            R4300.memory.WriteUInt32(addr, (uint)Registers.R4300.Reg[Desc.op2]);
            Registers.R4300.Reg[Desc.op2] = 1;
            Registers.R4300.PC += 4;
        }

        public static void LWC1(OpcodeTable.OpcodeDesc Desc)
        {
            uint addr = EffectiveAddress(Desc);
            uint bits = R4300.memory.ReadUInt32(addr);
            Registers.COP1.Reg[Desc.op2] = Common.Util.UInt32ToFloat(bits);
            Registers.R4300.PC += 4;
        }

        public static void SWC1(OpcodeTable.OpcodeDesc Desc)
        {
            uint addr = EffectiveAddress(Desc);
            uint bits = Common.Util.FloatToUInt32((float)Registers.COP1.Reg[Desc.op2]);
            R4300.memory.WriteUInt32(addr, bits);
            Registers.R4300.PC += 4;
        }

        public static void LDC1(OpcodeTable.OpcodeDesc Desc)
        {
            uint addr = EffectiveAddress(Desc);
            ulong bits = R4300.memory.ReadUInt64(addr);
            Registers.COP1.Reg[Desc.op2] = Common.Util.UInt64ToDouble(bits);
            Registers.R4300.PC += 4;
        }

        public static void SDC1(OpcodeTable.OpcodeDesc Desc)
        {
            uint addr = EffectiveAddress(Desc);
            ulong bits = Common.Util.DoubleToUInt64(Registers.COP1.Reg[Desc.op2]);
            R4300.memory.WriteUInt64(addr, bits);
            Registers.R4300.PC += 4;
        }
    }
}
