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

            int low = (g_work_val1.b0 & 0x0F) + (g_work_val2.b0 & 0x0F);
            if (g_status_X) low += 1;

            if (low > 9) { low -= 10; g_status_C = true; }
            else         { g_status_C = false; }

            int high = ((g_work_val1.b0 >> 4) & 0x0F) + ((g_work_val2.b0 >> 4) & 0x0F);
            if (g_status_C) high += 1;

            if (high > 9) { high -= 10; g_status_C = true; }
            else          { g_status_C = false; }

            g_work_data.b0 = (byte)((high << 4) | low);
            g_reg_data[g_op1].b0 = g_work_data.b0;

            if (g_work_data.b0 != 0)
                g_status_Z = false;     // Z är kumulativ för ABCD

                g_status_X = g_status_C;
        }

        private void analyse_ABCD_mode_1()
        {
            g_reg_PC += 2;

            // pre-decrement source/dest
            g_reg_addr[g_op1].l -= 1;
            g_work_val1.b0 = md_main.g_md_bus.read8((uint)g_reg_addr[g_op1].l);

            g_reg_addr[g_op4].l -= 1;
            g_work_val2.b0 = md_main.g_md_bus.read8((uint)g_reg_addr[g_op4].l);

            g_clock += 19;

            int low = (g_work_val1.b0 & 0x0F) + (g_work_val2.b0 & 0x0F);
            if (g_status_X) low += 1;

            if (low > 9) { low -= 10; g_status_C = true; }
            else         { g_status_C = false; }

            int high = ((g_work_val1.b0 >> 4) & 0x0F) + ((g_work_val2.b0 >> 4) & 0x0F);
            if (g_status_C) high += 1;

            if (high > 9) { high -= 10; g_status_C = true; }
            else          { g_status_C = false; }

            g_work_data.b0 = (byte)((high << 4) | low);

            md_main.g_md_bus.write8((uint)g_reg_addr[g_op1].l, g_work_data.b0);

            if (g_work_data.b0 != 0)
                g_status_Z = false;     // Z kumulativ

                g_status_X = g_status_C;
        }
    }
}
