using System;

namespace EutherDrive.Core.MdTracerCore
{
    internal partial class md_m68k
    {
        private void analyse_ADDA()
        {
            // ADDA <ea>, An
            g_reg_PC += 2;

            // Size: 0=byte (ogiltigt här), 1=word, 2=long
            // I ADDA kodas storleken som: 01 = word, 11 = long
            int w_size = (g_op2 >> 2) + 1;   // -> 1 eller 2

            // Timing: word 8 cykler, long 6 (matchar ditt original)
            g_clock = (w_size == 1) ? 8 : 6;

            // Src = <ea>
            adressing_func_address(g_op3, g_op4, w_size);
            g_work_val2.l = adressing_func_read(g_op3, g_op4, w_size);

            // ADDA.W är sign-extendad till 32 bit före addition; ADDA.L är 32-bit
            g_work_val2.l = get_int_cast(g_work_val2.l, w_size);

            // Dst = An (adressregister).
            // Read it after EA side effects so cases like ADDA.W (An)+,An
            // use the architecturally updated An value.
            g_work_val1.l = g_reg_addr[g_op1].l;

            // An = An + src
            g_work_data.l = g_work_val1.l + g_work_val2.l;
            g_reg_addr[g_op1].l = g_work_data.l;

            // OBS: ADDA påverkar inte N,Z,V,C,X (inga flaggor ändras).
        }
    }
}
