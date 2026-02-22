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

        private static uint ReadRegisterWord(int reg)
        {
            return (uint)Registers.R4300.Reg[reg];
        }

        private static void WriteRegisterWordSignExtended(int reg, uint value)
        {
            Registers.R4300.Reg[reg] = unchecked((ulong)(long)(int)value);
        }

        private static void RequireAlignment(uint addr, uint alignment, bool isStore)
        {
            if ((addr & (alignment - 1u)) != 0)
                throw new Common.Exceptions.AddressErrorException(addr, isStore);
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
            RequireAlignment(addr, 8, isStore: false);
            Registers.R4300.Reg[Desc.op2] = (ulong)R4300.memory.ReadInt64(addr);
            Registers.R4300.PC += 4;
        }

        public static void LDL(OpcodeTable.OpcodeDesc Desc)
        {
            uint addr = EffectiveAddress(Desc);
            uint aligned = addr & 0xFFFFFFF8u;
            int n = (int)(addr & 0x7u);
            ulong mem = R4300.memory.ReadUInt64(aligned);
            ulong oldRt = Registers.R4300.Reg[Desc.op2];
            ulong result;
            switch (n)
            {
                case 0: result = (oldRt & 0x00FFFFFFFFFFFFFFUL) | (mem & 0xFF00000000000000UL); break;
                case 1: result = (oldRt & 0x0000FFFFFFFFFFFFUL) | (mem & 0xFFFF000000000000UL); break;
                case 2: result = (oldRt & 0x000000FFFFFFFFFFUL) | (mem & 0xFFFFFF0000000000UL); break;
                case 3: result = (oldRt & 0x00000000FFFFFFFFUL) | (mem & 0xFFFFFFFF00000000UL); break;
                case 4: result = (oldRt & 0x0000000000FFFFFFUL) | (mem & 0xFFFFFFFFFF000000UL); break;
                case 5: result = (oldRt & 0x000000000000FFFFUL) | (mem & 0xFFFFFFFFFFFF0000UL); break;
                case 6: result = (oldRt & 0x00000000000000FFUL) | (mem & 0xFFFFFFFFFFFFFF00UL); break;
                default: result = mem; break;
            }

            Registers.R4300.Reg[Desc.op2] = result;
            Registers.R4300.PC += 4;
        }

        public static void LDR(OpcodeTable.OpcodeDesc Desc)
        {
            uint addr = EffectiveAddress(Desc);
            uint aligned = addr & 0xFFFFFFF8u;
            int n = (int)(addr & 0x7u);
            ulong mem = R4300.memory.ReadUInt64(aligned);
            ulong oldRt = Registers.R4300.Reg[Desc.op2];
            ulong result;
            switch (n)
            {
                case 0: result = mem; break;
                case 1: result = (oldRt & 0xFF00000000000000UL) | (mem & 0x00FFFFFFFFFFFFFFUL); break;
                case 2: result = (oldRt & 0xFFFF000000000000UL) | (mem & 0x0000FFFFFFFFFFFFUL); break;
                case 3: result = (oldRt & 0xFFFFFF0000000000UL) | (mem & 0x000000FFFFFFFFFFUL); break;
                case 4: result = (oldRt & 0xFFFFFFFF00000000UL) | (mem & 0x00000000FFFFFFFFUL); break;
                case 5: result = (oldRt & 0xFFFFFFFFFF000000UL) | (mem & 0x0000000000FFFFFFUL); break;
                case 6: result = (oldRt & 0xFFFFFFFFFFFF0000UL) | (mem & 0x000000000000FFFFUL); break;
                default: result = (oldRt & 0xFFFFFFFFFFFFFF00UL) | (mem & 0x00000000000000FFUL); break;
            }

            Registers.R4300.Reg[Desc.op2] = result;
            Registers.R4300.PC += 4;
        }

        public static void LH(OpcodeTable.OpcodeDesc Desc)
        {
            uint addr = EffectiveAddress(Desc);
            RequireAlignment(addr, 2, isStore: false);
            Registers.R4300.Reg[Desc.op2] = (ulong)R4300.memory.ReadInt16(addr);
            Registers.R4300.PC += 4;
        }

        public static void LHU(OpcodeTable.OpcodeDesc Desc)
        {
            uint addr = EffectiveAddress(Desc);
            RequireAlignment(addr, 2, isStore: false);
            Registers.R4300.Reg[Desc.op2] = R4300.memory.ReadUInt16(addr);
            Registers.R4300.PC += 4;
        }

        public static void LW(OpcodeTable.OpcodeDesc Desc)
        {
            uint addr = EffectiveAddress(Desc);
            RequireAlignment(addr, 4, isStore: false);
            Registers.R4300.Reg[Desc.op2] = unchecked((ulong)(long)R4300.memory.ReadInt32(addr));
            Registers.R4300.PC += 4;
        }

        public static void LWL(OpcodeTable.OpcodeDesc Desc)
        {
            uint addr = EffectiveAddress(Desc);
            uint aligned = addr & 0xFFFFFFFCu;
            int n = (int)(addr & 0x3u);
            uint mem = R4300.memory.ReadUInt32(aligned);
            uint oldRt = ReadRegisterWord(Desc.op2);
            uint result;
            switch (n)
            {
                case 0: result = (oldRt & 0x00FFFFFFu) | (mem & 0xFF000000u); break;
                case 1: result = (oldRt & 0x0000FFFFu) | (mem & 0xFFFF0000u); break;
                case 2: result = (oldRt & 0x000000FFu) | (mem & 0xFFFFFF00u); break;
                default: result = mem; break;
            }

            WriteRegisterWordSignExtended(Desc.op2, result);
            Registers.R4300.PC += 4;
        }

        public static void LWR(OpcodeTable.OpcodeDesc Desc)
        {
            uint addr = EffectiveAddress(Desc);
            uint aligned = addr & 0xFFFFFFFCu;
            int n = (int)(addr & 0x3u);
            uint mem = R4300.memory.ReadUInt32(aligned);
            uint oldRt = ReadRegisterWord(Desc.op2);
            uint result;
            switch (n)
            {
                case 0: result = mem; break;
                case 1: result = (oldRt & 0xFF000000u) | (mem & 0x00FFFFFFu); break;
                case 2: result = (oldRt & 0xFFFF0000u) | (mem & 0x0000FFFFu); break;
                default: result = (oldRt & 0xFFFFFF00u) | (mem & 0x000000FFu); break;
            }

            WriteRegisterWordSignExtended(Desc.op2, result);
            Registers.R4300.PC += 4;
        }

        public static void LWU(OpcodeTable.OpcodeDesc Desc)
        {
            uint addr = EffectiveAddress(Desc);
            RequireAlignment(addr, 4, isStore: false);
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
            RequireAlignment(addr, 8, isStore: true);
            R4300.memory.WriteUInt64(addr, Registers.R4300.Reg[Desc.op2]);
            Registers.R4300.PC += 4;
        }

        public static void SDL(OpcodeTable.OpcodeDesc Desc)
        {
            uint addr = EffectiveAddress(Desc);
            uint aligned = addr & 0xFFFFFFF8u;
            int n = (int)(addr & 0x7u);
            ulong oldMem = R4300.memory.ReadUInt64(aligned);
            ulong value = Registers.R4300.Reg[Desc.op2];
            ulong merged;
            switch (n)
            {
                case 0: merged = (oldMem & 0x00FFFFFFFFFFFFFFUL) | (value & 0xFF00000000000000UL); break;
                case 1: merged = (oldMem & 0x0000FFFFFFFFFFFFUL) | (value & 0xFFFF000000000000UL); break;
                case 2: merged = (oldMem & 0x000000FFFFFFFFFFUL) | (value & 0xFFFFFF0000000000UL); break;
                case 3: merged = (oldMem & 0x00000000FFFFFFFFUL) | (value & 0xFFFFFFFF00000000UL); break;
                case 4: merged = (oldMem & 0x0000000000FFFFFFUL) | (value & 0xFFFFFFFFFF000000UL); break;
                case 5: merged = (oldMem & 0x000000000000FFFFUL) | (value & 0xFFFFFFFFFFFF0000UL); break;
                case 6: merged = (oldMem & 0x00000000000000FFUL) | (value & 0xFFFFFFFFFFFFFF00UL); break;
                default: merged = value; break;
            }
            R4300.memory.WriteUInt64(aligned, merged);

            Registers.R4300.PC += 4;
        }

        public static void SDR(OpcodeTable.OpcodeDesc Desc)
        {
            uint addr = EffectiveAddress(Desc);
            uint aligned = addr & 0xFFFFFFF8u;
            int n = (int)(addr & 0x7u);
            ulong oldMem = R4300.memory.ReadUInt64(aligned);
            ulong value = Registers.R4300.Reg[Desc.op2];
            ulong merged;
            switch (n)
            {
                case 0: merged = value; break;
                case 1: merged = (oldMem & 0xFF00000000000000UL) | (value & 0x00FFFFFFFFFFFFFFUL); break;
                case 2: merged = (oldMem & 0xFFFF000000000000UL) | (value & 0x0000FFFFFFFFFFFFUL); break;
                case 3: merged = (oldMem & 0xFFFFFF0000000000UL) | (value & 0x000000FFFFFFFFFFUL); break;
                case 4: merged = (oldMem & 0xFFFFFFFF00000000UL) | (value & 0x00000000FFFFFFFFUL); break;
                case 5: merged = (oldMem & 0xFFFFFFFFFF000000UL) | (value & 0x0000000000FFFFFFUL); break;
                case 6: merged = (oldMem & 0xFFFFFFFFFFFF0000UL) | (value & 0x000000000000FFFFUL); break;
                default: merged = (oldMem & 0xFFFFFFFFFFFFFF00UL) | (value & 0x00000000000000FFUL); break;
            }
            R4300.memory.WriteUInt64(aligned, merged);

            Registers.R4300.PC += 4;
        }

        public static void SH(OpcodeTable.OpcodeDesc Desc)
        {
            uint addr = EffectiveAddress(Desc);
            RequireAlignment(addr, 2, isStore: true);
            R4300.memory.WriteUInt16(addr, (ushort)Registers.R4300.Reg[Desc.op2]);
            Registers.R4300.PC += 4;
        }

        public static void SW(OpcodeTable.OpcodeDesc Desc)
        {
            uint addr = EffectiveAddress(Desc);
            RequireAlignment(addr, 4, isStore: true);
            R4300.memory.WriteUInt32(addr, (uint)Registers.R4300.Reg[Desc.op2]);
            Registers.R4300.PC += 4;
        }

        public static void SWL(OpcodeTable.OpcodeDesc Desc)
        {
            uint addr = EffectiveAddress(Desc);
            uint aligned = addr & 0xFFFFFFFCu;
            int n = (int)(addr & 0x3u);
            uint oldMem = R4300.memory.ReadUInt32(aligned);
            uint value = (uint)Registers.R4300.Reg[Desc.op2];
            uint merged;
            switch (n)
            {
                case 0: merged = (oldMem & 0x00FFFFFFu) | (value & 0xFF000000u); break;
                case 1: merged = (oldMem & 0x0000FFFFu) | (value & 0xFFFF0000u); break;
                case 2: merged = (oldMem & 0x000000FFu) | (value & 0xFFFFFF00u); break;
                default: merged = value; break;
            }
            R4300.memory.WriteUInt32(aligned, merged);

            Registers.R4300.PC += 4;
        }

        public static void SWR(OpcodeTable.OpcodeDesc Desc)
        {
            uint addr = EffectiveAddress(Desc);
            uint aligned = addr & 0xFFFFFFFCu;
            int n = (int)(addr & 0x3u);
            uint oldMem = R4300.memory.ReadUInt32(aligned);
            uint value = (uint)Registers.R4300.Reg[Desc.op2];
            uint merged;
            switch (n)
            {
                case 0: merged = value; break;
                case 1: merged = (oldMem & 0xFF000000u) | (value & 0x00FFFFFFu); break;
                case 2: merged = (oldMem & 0xFFFF0000u) | (value & 0x0000FFFFu); break;
                default: merged = (oldMem & 0xFFFFFF00u) | (value & 0x000000FFu); break;
            }
            R4300.memory.WriteUInt32(aligned, merged);

            Registers.R4300.PC += 4;
        }

        public static void SC(OpcodeTable.OpcodeDesc Desc)
        {
            // Treat SC as SW and report success in rt.
            uint addr = EffectiveAddress(Desc);
            RequireAlignment(addr, 4, isStore: true);
            R4300.memory.WriteUInt32(addr, (uint)Registers.R4300.Reg[Desc.op2]);
            Registers.R4300.Reg[Desc.op2] = 1;
            Registers.R4300.PC += 4;
        }

        public static void LWC1(OpcodeTable.OpcodeDesc Desc)
        {
            uint addr = EffectiveAddress(Desc);
            RequireAlignment(addr, 4, isStore: false);
            uint bits = R4300.memory.ReadUInt32(addr);
            Registers.COP1.Reg[Desc.op2] = Common.Util.UInt32ToFloat(bits);
            Registers.R4300.PC += 4;
        }

        public static void SWC1(OpcodeTable.OpcodeDesc Desc)
        {
            uint addr = EffectiveAddress(Desc);
            RequireAlignment(addr, 4, isStore: true);
            uint bits = Common.Util.FloatToUInt32((float)Registers.COP1.Reg[Desc.op2]);
            R4300.memory.WriteUInt32(addr, bits);
            Registers.R4300.PC += 4;
        }

        public static void LDC1(OpcodeTable.OpcodeDesc Desc)
        {
            uint addr = EffectiveAddress(Desc);
            RequireAlignment(addr, 8, isStore: false);
            ulong bits = R4300.memory.ReadUInt64(addr);
            Registers.COP1.Reg[Desc.op2] = Common.Util.UInt64ToDouble(bits);
            Registers.R4300.PC += 4;
        }

        public static void SDC1(OpcodeTable.OpcodeDesc Desc)
        {
            uint addr = EffectiveAddress(Desc);
            RequireAlignment(addr, 8, isStore: true);
            ulong bits = Common.Util.DoubleToUInt64(Registers.COP1.Reg[Desc.op2]);
            R4300.memory.WriteUInt64(addr, bits);
            Registers.R4300.PC += 4;
        }
    }
}
