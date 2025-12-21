namespace EutherDrive.Core.MdTracerCore;

internal partial class md_m68k
{
    // SR pack/unpack (16-bit)
    internal static ushort g_reg_SR
    {
        get
        {
            ushort value = 0;
            if (g_status_T) value |= 0x8000;
            if (g_status_B1) value |= 0x4000;
            if (g_status_S) value |= 0x2000;
            if (g_status_B2) value |= 0x1000;
            if (g_status_B3) value |= 0x0800;

            value |= (ushort)((g_status_interrupt_mask & 0x07) << 8);

            if (g_status_B4) value |= 0x0080;
            if (g_status_B5) value |= 0x0040;
            if (g_status_B6) value |= 0x0020;
            if (g_status_X)  value |= 0x0010;
            if (g_status_N)  value |= 0x0008;
            if (g_status_Z)  value |= 0x0004;
            if (g_status_V)  value |= 0x0002;
            if (g_status_C)  value |= 0x0001;

            return value;
        }
        set
        {
            g_status_T  = (value & 0x8000) != 0;
            g_status_B1 = (value & 0x4000) != 0;
            g_status_S  = (value & 0x2000) != 0;
            g_status_B2 = (value & 0x1000) != 0;
            g_status_B3 = (value & 0x0800) != 0;

            g_status_interrupt_mask = (byte)((value >> 8) & 0x07);

            g_status_B4 = (value & 0x0080) != 0;
            g_status_B5 = (value & 0x0040) != 0;
            g_status_B6 = (value & 0x0020) != 0;
            g_status_X  = (value & 0x0010) != 0;
            g_status_N  = (value & 0x0008) != 0;
            g_status_Z  = (value & 0x0004) != 0;
            g_status_V  = (value & 0x0002) != 0;
            g_status_C  = (value & 0x0001) != 0;
        }
    }

    // CCR pack/unpack (8-bit)
    internal static byte g_status_CCR
    {
        get
        {
            byte value = 0;
            if (g_status_B4) value |= 0x80;
            if (g_status_B5) value |= 0x40;
            if (g_status_B6) value |= 0x20;
            if (g_status_X)  value |= 0x10;
            if (g_status_N)  value |= 0x08;
            if (g_status_Z)  value |= 0x04;
            if (g_status_V)  value |= 0x02;
            if (g_status_C)  value |= 0x01;
            return value;
        }
        set
        {
            g_status_B4 = (value & 0x80) != 0;
            g_status_B5 = (value & 0x40) != 0;
            g_status_B6 = (value & 0x20) != 0;
            g_status_X  = (value & 0x10) != 0;
            g_status_N  = (value & 0x08) != 0;
            g_status_Z  = (value & 0x04) != 0;
            g_status_V  = (value & 0x02) != 0;
            g_status_C  = (value & 0x01) != 0;
        }
    }
}
