namespace EutherDrive.Core.MdTracerCore
{
    internal partial class md_m68k
    {
        private static byte BcdAdd(byte src, byte dst)
        {
            int extend = g_status_X ? 1 : 0;

            int sum1 = src + dst;
            bool carry1 = sum1 > 0xFF;
            int sum2 = sum1 + extend;
            bool carry2 = sum2 > 0xFF;
            int sum = sum2 & 0xFF;

            int adjust = 0;
            if (((src & 0x0F) + (dst & 0x0F) + extend >= 0x10) || ((sum & 0x0F) > 0x09))
                adjust += 0x06;
            if (sum > 0x99 || carry1 || carry2)
                adjust += 0x60;

            int corrected = (sum + adjust) & 0xFF;
            bool correctedCarry = (sum + adjust) > 0xFF;

            bool bit6Carry = ((sum & 0x7F) + (adjust & 0x7F)) >= 0x80;
            bool overflow = bit6Carry != correctedCarry;

            bool carry = carry1 || carry2 || correctedCarry;

            g_status_C = carry;
            g_status_X = carry;
            g_status_V = overflow;
            g_status_N = (corrected & 0x80) != 0;
            if (corrected != 0)
                g_status_Z = false;

            return (byte)corrected;
        }

        private static byte BcdSub(byte dst, byte src)
        {
            int extend = g_status_X ? 1 : 0;

            int diff1 = dst - src;
            bool borrow1 = dst < src;
            int diff2 = diff1 - extend;
            bool borrow2 = diff1 < extend;
            int diff = diff2 & 0xFF;
            bool borrow = borrow1 || borrow2;

            int adjust = 0;
            if ((dst & 0x0F) < ((src & 0x0F) + extend))
                adjust += 0x06;
            if (borrow)
                adjust += 0x60;

            int corrected = (diff - adjust) & 0xFF;
            bool correctedBorrow = diff < adjust;

            bool bit6Borrow = (diff & 0x7F) < (adjust & 0x7F);
            bool overflow = bit6Borrow != correctedBorrow;

            bool finalBorrow = borrow || correctedBorrow;

            g_status_C = finalBorrow;
            g_status_X = finalBorrow;
            g_status_V = overflow;
            g_status_N = (corrected & 0x80) != 0;
            if (corrected != 0)
                g_status_Z = false;

            return (byte)corrected;
        }
    }
}
