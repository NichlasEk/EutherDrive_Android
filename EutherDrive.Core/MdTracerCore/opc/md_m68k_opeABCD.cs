using System;

namespace EutherDrive.Core.MdTracerCore
{
    internal partial class md_m68k
    {
        private void analyse_ABCD_mode_0()
        {
            g_reg_PC += 2;

            g_work_val1.b0 = g_reg_data[g_op1].b0;
            g_work_val2.b0 = g_reg_data[g_op4].b0;

            g_clock += 6;
            g_work_data.b0 = BcdAdd(g_work_val1.b0, g_work_val2.b0);
            g_reg_data[g_op1].b0 = g_work_data.b0;
        }

        private void analyse_ABCD_mode_1()
        {
            g_reg_PC += 2;

            // pre-decrement source/dest
            g_reg_addr[g_op1].l -= (uint)(g_op1 == 7 ? 2 : 1);
            g_work_val1.b0 = md_main.g_md_bus.read8((uint)g_reg_addr[g_op1].l);

            g_reg_addr[g_op4].l -= (uint)(g_op4 == 7 ? 2 : 1);
            g_work_val2.b0 = md_main.g_md_bus.read8((uint)g_reg_addr[g_op4].l);

            g_clock += 19;
            g_work_data.b0 = BcdAdd(g_work_val1.b0, g_work_val2.b0);
            md_main.g_md_bus.write8((uint)g_reg_addr[g_op1].l, g_work_data.b0);
        }
    }
}
