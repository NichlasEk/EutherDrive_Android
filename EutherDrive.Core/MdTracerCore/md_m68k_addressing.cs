using System;

namespace EutherDrive.Core.MdTracerCore
{
    internal partial class md_m68k
    {
        private static readonly bool TracePcRelAddr =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_PC_REL_ADDR"), "1", StringComparison.Ordinal);
        private static readonly int TracePcRelAddrLimit =
            ParseTraceLimit("EUTHERDRIVE_TRACE_PC_REL_ADDR_LIMIT", 128);
        private static int _tracePcRelAddrRemaining = TracePcRelAddr ? TracePcRelAddrLimit : 0;

        private uint g_analyze_address;
        private UNION_UINT g_work_data;
        private UNION_UINT g_work_data2;
        private UNION_UINT g_work_val1;
        private UNION_UINT g_work_val2;

        private void adressing_func_address(int in_mode, int in_reg, int in_size)
        {
            int w_mode = ((in_mode == 7) ? in_reg + 7 : in_mode);
            switch (w_mode)
            {
                case 2:
                    g_analyze_address = g_reg_addr[in_reg].l;
                    if (in_size == 2) g_clock += 8; else g_clock += 4;
                    break;
                case 3:
                    g_analyze_address = g_reg_addr[in_reg].l;
                    switch (in_size)
                    {
                        case 0: g_reg_addr[in_reg].l += (uint)((in_reg == 7) ? 2 : 1); break;
                        case 1: g_reg_addr[in_reg].l += 2; break;
                        default: g_reg_addr[in_reg].l += 4; break;
                    }
                    if (in_size == 2) g_clock += 8; else g_clock += 4;
                    break;
                case 4:
                    switch (in_size)
                    {
                        case 0: g_reg_addr[in_reg].l -= (uint)((in_reg == 7) ? 2 : 1); break;
                        case 1: g_reg_addr[in_reg].l -= 2; break;
                        default: g_reg_addr[in_reg].l -= 4; break;
                    }
                    g_analyze_address = g_reg_addr[in_reg].l;
                    if (in_size == 2) g_clock += 10; else g_clock += 6;
                    break;
                case 5:
                    {
                        ushort w_ext = read16(g_reg_PC);
                        g_analyze_address = (uint)(g_reg_addr[in_reg].l + (short)w_ext);
                        g_reg_PC += 2;
                        if (in_size == 2) g_clock += 12; else g_clock += 8;
                    }
                    break;
                case 6:
                    {
                        ushort w_ext = read16(g_reg_PC);
                        int w_ext_reg = (w_ext >> 12) & 0x07;
                        uint w_ind = 0;
                        if ((w_ext & 0x8000) == 0)
                        {
                            w_ind = g_reg_data[w_ext_reg].l;
                        }
                        else
                        {
                            w_ind = g_reg_addr[w_ext_reg].l;
                        }
                        if ((w_ext & 0x0800) == 0) w_ind = get_int_cast(w_ind, 1);
                        sbyte w_ext_disp = (sbyte)(w_ext & 0x00ff);
                        g_analyze_address = (uint)(g_reg_addr[in_reg].l + (int)w_ind + w_ext_disp);
                        g_reg_PC += 2;
                        if (in_size == 2) g_clock += 14; else g_clock += 10;
                    }
                    break;
                case 7:
                    // Absolute short is sign-extended to 32 bits. Masking to 24 bits is handled by the bus.
                    g_analyze_address = (uint)(int)(short)read16(g_reg_PC);
                    g_reg_PC += 2;
                    if (in_size == 2) g_clock += 12; else g_clock += 8;
                    break;
                case 8:
                    g_analyze_address = read32(g_reg_PC);
                    g_reg_PC += 4;
                    if (in_size == 2) g_clock += 16; else g_clock += 12;
                    break;
                case 9:
                    {
                        ushort w_ext = read16(g_reg_PC);
                        g_analyze_address = (uint)(g_reg_PC + (short)w_ext);
                        if (TracePcRelAddr && _tracePcRelAddrRemaining > 0)
                        {
                            _tracePcRelAddrRemaining--;
                            Console.WriteLine($"[PCREL] pc=0x{g_reg_PC:X6} ext=0x{w_ext:X4} addr=0x{g_analyze_address:X6}");
                        }
                        g_reg_PC += 2;
                        if (in_size == 2) g_clock += 12; else g_clock += 8;
                    }
                    break;
                case 10:
                    {
                        ushort w_ext = read16(g_reg_PC);
                        int w_ext_reg = (w_ext >> 12) & 0x07;
                        uint w_ind = 0;
                        if ((w_ext & 0x8000) == 0)
                        {
                            w_ind = g_reg_data[w_ext_reg].l;
                        }
                        else
                        {
                            w_ind = g_reg_addr[w_ext_reg].l;
                        }
                        if ((w_ext & 0x0800) == 0) w_ind = get_int_cast(w_ind, 1);
                        sbyte w_ext_disp = (sbyte)(w_ext & 0x00ff);
                        g_analyze_address = (uint)(g_reg_PC + (int)w_ind + w_ext_disp);
                        if (TracePcRelAddr && _tracePcRelAddrRemaining > 0)
                        {
                            _tracePcRelAddrRemaining--;
                            string idxKind = ((w_ext & 0x8000) == 0) ? "D" : "A";
                            Console.WriteLine(
                                $"[PCRELX] pc=0x{g_reg_PC:X6} base=0x{g_reg_PC:X6} ext=0x{w_ext:X4} idx={idxKind}{w_ext_reg} " +
                                $"index=0x{w_ind:X8} disp=0x{(byte)w_ext_disp:X2} addr=0x{g_analyze_address:X6}");
                        }
                        g_reg_PC += 2;
                        if (in_size == 2) g_clock += 14; else g_clock += 10;
                    }
                    break;
                case 11:
                    g_analyze_address = g_reg_PC;
                    if (in_size == 2)
                    {
                        g_reg_PC += 4;
                        g_clock += 8;
                    }
                    else
                    {
                        g_reg_PC += 2;
                        g_clock += 4;
                    }
                    break;
            }
        }
        //----------------------------------------------------------------------
        private uint adressing_func_read(int in_mode, int in_reg, int in_size)
        {
            uint w_out = 0;
            int w_mode = ((in_mode == 7) ? in_reg + 7 : in_mode);
            switch (w_mode)
            {
                case 0:
                    switch (in_size)
                    {
                        case 0: w_out = g_reg_data[in_reg].b0; break;
                        case 1: w_out = g_reg_data[in_reg].w; break;
                        default: w_out = g_reg_data[in_reg].l; break;
                    }
                    break;
                case 1:
                    switch (in_size)
                    {
                        case 0: w_out = g_reg_addr[in_reg].b0; break;
                        case 1: w_out = g_reg_addr[in_reg].w; break;
                        default: w_out = g_reg_addr[in_reg].l; break;
                    }
                    break;
                case 11:
                    switch (in_size)
                    {
                        case 0: w_out = (uint)(md_main.g_md_bus.read16(g_analyze_address) & 0x00ff); break;
                        case 1: w_out = md_main.g_md_bus.read16(g_analyze_address); break;
                        default: w_out = md_main.g_md_bus.read32(g_analyze_address); break;
                    }
                    break;
                default:
                    switch (in_size)
                    {
                        case 0: w_out = md_main.g_md_bus.read8(g_analyze_address); break;
                        case 1: w_out = md_main.g_md_bus.read16(g_analyze_address); break;
                        default: w_out = md_main.g_md_bus.read32(g_analyze_address); break;
                    }
                    break;
            }
            return w_out;
        }
        //----------------------------------------------------------------------
        private void adressing_func_write(int in_mode, int in_reg, int in_size, uint in_val)
        {
            int w_mode = ((in_mode == 7) ? in_reg + 7 : in_mode);
            switch (w_mode)
            {
                case 0:
                    switch (in_size)
                    {
                        case 0: g_reg_data[in_reg].b0 = (byte)in_val; break;
                        case 1: g_reg_data[in_reg].w = (ushort)in_val; break;
                        default: g_reg_data[in_reg].l = in_val; break;
                    }
                    break;
                case 1:
                    switch (in_size)
                    {
                        case 0: g_reg_addr[in_reg].b0 = (byte)in_val; break;
                        case 1: g_reg_addr[in_reg].w = (ushort)in_val; break;
                        default: g_reg_addr[in_reg].l = in_val; break;
                    }
                    break;
                case 11:
                    switch (in_size)
                    {
                        case 0: md_main.g_md_bus.write16(g_analyze_address, (byte)in_val); break;
                        case 1: md_main.g_md_bus.write16(g_analyze_address, (ushort)in_val); break;
                        default: md_main.g_md_bus.write32(g_analyze_address, in_val); break;
                    }
                    break;
                default:
                    switch (in_size)
                    {
                        case 0: md_main.g_md_bus.write8(g_analyze_address, (byte)in_val); break;
                        case 1: md_main.g_md_bus.write16(g_analyze_address, (ushort)in_val); break;
                        default: md_main.g_md_bus.write32(g_analyze_address, in_val); break;
                    }
                    break;
            }
        }
    }
}
