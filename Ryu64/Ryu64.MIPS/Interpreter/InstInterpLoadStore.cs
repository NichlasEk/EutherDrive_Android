using System;

namespace Ryu64.MIPS
{
    public partial class InstInterp
    {
        private static readonly bool TraceLhuWindow =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_N64_LHU_WINDOW"), "1", StringComparison.Ordinal);

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
            int shift = 8 * n;
            ulong mask = shift == 0 ? 0UL : ((1UL << shift) - 1UL);
            ulong result = (oldRt & mask) | (mem << shift);

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
            int shift = 8 * (7 - n);
            ulong mask = n == 7 ? 0UL : ulong.MaxValue << (8 * (n + 1));
            ulong result = (oldRt & mask) | (mem >> shift);

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
            ushort value = R4300.memory.ReadUInt16(addr);
            if (TraceLhuWindow && Registers.R4300.PC >= 0x80322E10u && Registers.R4300.PC <= 0x80322E20u)
            {
                Console.WriteLine(
                    $"[N64LHU] pc=0x{Registers.R4300.PC:x8} addr=0x{addr:x8} value=0x{value:x4} rt={Desc.op2}");
            }
            Registers.R4300.Reg[Desc.op2] = value;
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
            int shift = 8 * n;
            uint mask = shift == 0 ? 0u : ((1u << shift) - 1u);
            uint result = (oldRt & mask) | (mem << shift);

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
            int shift = 8 * (3 - n);
            uint mask = n == 3 ? 0u : uint.MaxValue << (8 * (n + 1));
            uint result = (oldRt & mask) | (mem >> shift);

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
            uint addr = EffectiveAddress(Desc);
            RequireAlignment(addr, 4, isStore: false);
            uint value = R4300.memory.ReadUInt32(addr);
            WriteRegisterWordSignExtended(Desc.op2, value);
            R4300.SetLoadLinkedReservation(addr);
            Registers.R4300.PC += 4;
        }

        public static void SB(OpcodeTable.OpcodeDesc Desc)
        {
            uint addr = EffectiveAddress(Desc);
            uint pc = Registers.R4300.PC;
            if (pc >= 0x800A16C0u && pc <= 0x800A16E0u
                && string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_N64_PI_DMA"), "1", StringComparison.Ordinal))
            {
                ulong baseValue = Registers.R4300.Reg[Desc.op1];
                ulong storeValue = Registers.R4300.Reg[Desc.op2];
                Common.Logger.PrintWarningLine(
                    $"[N64SBTRACE] pc=0x{pc:x8} op=0x{Desc.Opcode:x8} rs={Desc.op1} rt={Desc.op2} imm=0x{Desc.Imm:x4} " +
                    $"base=0x{baseValue:x16} value=0x{storeValue:x16} eff=0x{addr:x8}");
            }
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
            int shift = 8 * n;
            ulong mask = n == 0 ? ulong.MaxValue : ((1UL << (8 * (8 - n))) - 1UL);
            ulong merged = (oldMem & ~mask) | ((value >> shift) & mask);
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
            int shift = 8 * (7 - n);
            ulong mask = ulong.MaxValue << shift;
            ulong merged = (oldMem & ~mask) | ((value << shift) & mask);
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
            int shift = 8 * n;
            uint mask = n == 0 ? uint.MaxValue : ((1u << (8 * (4 - n))) - 1u);
            uint merged = (oldMem & ~mask) | ((value >> shift) & mask);
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
            int shift = 8 * (3 - n);
            uint mask = uint.MaxValue << shift;
            uint merged = (oldMem & ~mask) | ((value << shift) & mask);
            R4300.memory.WriteUInt32(aligned, merged);

            Registers.R4300.PC += 4;
        }

        public static void SC(OpcodeTable.OpcodeDesc Desc)
        {
            uint addr = EffectiveAddress(Desc);
            RequireAlignment(addr, 4, isStore: true);
            uint value = (uint)Registers.R4300.Reg[Desc.op2];
            bool success = R4300.TryStoreConditional(addr);
            if (success)
                R4300.memory.WriteUInt32(addr, value);

            Registers.R4300.Reg[Desc.op2] = success ? 1UL : 0UL;
            Registers.R4300.PC += 4;
        }

        public static void LWC1(OpcodeTable.OpcodeDesc Desc)
        {
            uint addr = EffectiveAddress(Desc);
            RequireAlignment(addr, 4, isStore: false);
            uint bits = R4300.memory.ReadUInt32(addr);
            SetFprRaw32ForLoadStore(Desc.op2, bits);
            Registers.R4300.PC += 4;
        }

        public static void SWC1(OpcodeTable.OpcodeDesc Desc)
        {
            uint addr = EffectiveAddress(Desc);
            RequireAlignment(addr, 4, isStore: true);
            uint bits = GetFprRaw32ForLoadStore(Desc.op2);
            R4300.memory.WriteUInt32(addr, bits);
            Registers.R4300.PC += 4;
        }

        public static void LDC1(OpcodeTable.OpcodeDesc Desc)
        {
            uint addr = EffectiveAddress(Desc);
            RequireAlignment(addr, 8, isStore: false);
            ulong bits = R4300.memory.ReadUInt64(addr);
            SetFprRaw64ForLoadStore(Desc.op2, bits);
            Registers.R4300.PC += 4;
        }

        public static void SDC1(OpcodeTable.OpcodeDesc Desc)
        {
            uint addr = EffectiveAddress(Desc);
            RequireAlignment(addr, 8, isStore: true);
            ulong bits = GetFprRaw64ForLoadStore(Desc.op2);
            R4300.memory.WriteUInt64(addr, bits);
            Registers.R4300.PC += 4;
        }
    }
}
