using System;
using static EutherDrive.Core.MdTracerCore.md_m68k;
namespace EutherDrive.Core.MdTracerCore
{
    internal partial class md_m68k
    {
        private static readonly bool TraceMovep =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_MOVEP"), "1", StringComparison.Ordinal);

        private void analyse_MOVEP_4()
        {
            g_reg_PC += 2;
            ushort w_ext = md_main.g_md_bus.read16(g_reg_PC);
            g_reg_PC += 2;
            uint pc = g_reg_PC - 4;
            uint baseAddr = g_reg_addr[g_op4].l;
            int disp = (short)w_ext;
            uint addr0 = baseAddr + (uint)disp;
            uint addr1 = addr0 + 2;
            byte b1 = md_main.g_md_bus.read8(addr0);
            byte b0 = md_main.g_md_bus.read8(addr1);
            g_reg_data[g_op1].b1 = b1;
            g_reg_data[g_op1].b0 = b0;
            if (TraceMovep)
            {
                uint addrUnsigned = baseAddr + w_ext;
                Console.WriteLine($"[MOVEP] pc=0x{pc:X6} op=mem->Dn.w Dn={g_op1} An={g_op4} base=0x{baseAddr:X6} disp=0x{w_ext:X4} dispS=0x{(ushort)disp:X4} addr=0x{addr0:X6}/0x{addr1:X6} addrU=0x{addrUnsigned:X6} val=0x{b1:X2}{b0:X2}");
            }
            g_clock = 16;
        }
        private void analyse_MOVEP_5()
        {
            g_reg_PC += 2;
            ushort w_ext = md_main.g_md_bus.read16(g_reg_PC);
            g_reg_PC += 2;
            uint pc = g_reg_PC - 4;
            uint baseAddr = g_reg_addr[g_op4].l;
            int disp = (short)w_ext;
            uint addr0 = baseAddr + (uint)disp;
            byte b3 = md_main.g_md_bus.read8(addr0);
            byte b2 = md_main.g_md_bus.read8(addr0 + 2);
            byte b1 = md_main.g_md_bus.read8(addr0 + 4);
            byte b0 = md_main.g_md_bus.read8(addr0 + 6);
            g_reg_data[g_op1].b3 = b3;
            g_reg_data[g_op1].b2 = b2;
            g_reg_data[g_op1].b1 = b1;
            g_reg_data[g_op1].b0 = b0;
            if (TraceMovep)
            {
                uint addrUnsigned = baseAddr + w_ext;
                Console.WriteLine($"[MOVEP] pc=0x{pc:X6} op=mem->Dn.l Dn={g_op1} An={g_op4} base=0x{baseAddr:X6} disp=0x{w_ext:X4} dispS=0x{(ushort)disp:X4} addr=0x{addr0:X6} addrU=0x{addrUnsigned:X6} val=0x{b3:X2}{b2:X2}{b1:X2}{b0:X2}");
            }
            g_clock = 24;
        }
        private void analyse_MOVEP_6()
        {
            g_reg_PC += 2;
            ushort w_ext = md_main.g_md_bus.read16(g_reg_PC);
            g_reg_PC += 2;
            uint pc = g_reg_PC - 4;
            uint baseAddr = g_reg_addr[g_op4].l;
            int disp = (short)w_ext;
            uint addr0 = baseAddr + (uint)disp;
            byte b1 = g_reg_data[g_op1].b1;
            byte b0 = g_reg_data[g_op1].b0;
            md_main.g_md_bus.write8(addr0, b1);
            md_main.g_md_bus.write8(addr0 + 2, b0);
            if (TraceMovep)
            {
                uint addrUnsigned = baseAddr + w_ext;
                Console.WriteLine($"[MOVEP] pc=0x{pc:X6} op=Dn->mem.w Dn={g_op1} An={g_op4} base=0x{baseAddr:X6} disp=0x{w_ext:X4} dispS=0x{(ushort)disp:X4} addr=0x{addr0:X6} addrU=0x{addrUnsigned:X6} val=0x{b1:X2}{b0:X2}");
            }
            g_clock = 18;
        }
        private void analyse_MOVEP_7()
        {
            g_reg_PC += 2;
            ushort w_ext = md_main.g_md_bus.read16(g_reg_PC);
            g_reg_PC += 2;
            uint pc = g_reg_PC - 4;
            uint baseAddr = g_reg_addr[g_op4].l;
            int disp = (short)w_ext;
            uint addr0 = baseAddr + (uint)disp;
            byte b3 = g_reg_data[g_op1].b3;
            byte b2 = g_reg_data[g_op1].b2;
            byte b1 = g_reg_data[g_op1].b1;
            byte b0 = g_reg_data[g_op1].b0;
            md_main.g_md_bus.write8(addr0, b3);
            md_main.g_md_bus.write8(addr0 + 2, b2);
            md_main.g_md_bus.write8(addr0 + 4, b1);
            md_main.g_md_bus.write8(addr0 + 6, b0);
            if (TraceMovep)
            {
                uint addrUnsigned = baseAddr + w_ext;
                Console.WriteLine($"[MOVEP] pc=0x{pc:X6} op=Dn->mem.l Dn={g_op1} An={g_op4} base=0x{baseAddr:X6} disp=0x{w_ext:X4} dispS=0x{(ushort)disp:X4} addr=0x{addr0:X6} addrU=0x{addrUnsigned:X6} val=0x{b3:X2}{b2:X2}{b1:X2}{b0:X2}");
            }
            g_clock = 28;
        }
   }
}
