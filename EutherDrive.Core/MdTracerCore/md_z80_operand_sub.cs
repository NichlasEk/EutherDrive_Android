namespace EutherDrive.Core.MdTracerCore
{
    internal partial class md_z80
    {
        public enum RP_TYPE { BC, DE, HL, SP };

        private byte g_status_flag
        {
            get
            {
                // F = SZ0H0PVNC (bit5/bit3 = 0)
                return (byte)(
                    (g_flag_S  << 7) |
                    (g_flag_Z  << 6) |
                    (0         << 5) |
                    (g_flag_H  << 4) |
                    (0         << 3) |
                    (g_flag_PV << 2) |
                    (g_flag_N  << 1) |
                    g_flag_C);
            }
            set
            {
                g_flag_S  = (value & 0x80) >> 7;
                g_flag_Z  = (value & 0x40) >> 6;
                g_flag_H  = (value & 0x10) >> 4;
                g_flag_PV = (value & 0x04) >> 2;
                g_flag_N  = (value & 0x02) >> 1;
                g_flag_C  = (value & 0x01);
            }
        }

        private byte read_byte(ushort in_addr) => read8(in_addr);

        private void write_byte(ushort in_addr, byte in_data) =>
        write8((uint)in_addr, in_data);

        private ushort read_word(ushort in_addr)
        {
            // Z80 little-endian: low @ addr, high @ addr+1
            return (ushort)((read8((uint)in_addr + 1) << 8) | read8(in_addr));
        }

        private void write_word(ushort in_addr, ushort in_data)
        {
            // little-endian
            write8((uint)in_addr,     (byte)(in_data & 0xFF));
            write8((uint)in_addr + 1, (byte)((in_data >> 8) & 0xFF));
        }

        private ushort read_rp(byte in_rp)
        {
            return in_rp switch
            {
                0 => g_reg_BC,
                1 => g_reg_DE,
                2 => g_reg_HL,
                3 => g_reg_SP,
                _ => (ushort)0
            };
        }

        private ushort read_rpix(byte in_rp)
        {
            return in_rp switch
            {
                0 => g_reg_BC,
                1 => g_reg_DE,
                2 => g_reg_IX,
                3 => g_reg_SP,
                _ => (ushort)0
            };
        }

        private ushort read_rpiy(byte in_rp)
        {
            return in_rp switch
            {
                0 => g_reg_BC,
                1 => g_reg_DE,
                2 => g_reg_IY,
                3 => g_reg_SP,
                _ => (ushort)0
            };
        }

        private void write_rp(byte in_rp, ushort in_data)
        {
            byte hi = (byte)((in_data >> 8) & 0xFF);
            byte lo = (byte)(in_data & 0xFF);

            switch (in_rp)
            {
                case 0: g_reg_B = hi; g_reg_C = lo; break; // BC
                case 1: g_reg_D = hi; g_reg_E = lo; break; // DE
                case 2: g_reg_H = hi; g_reg_L = lo; break; // HL
                case 3: g_reg_SP = in_data; break;         // SP
            }
        }

        private void write_reg(byte in_reg, byte in_data)
        {
            switch (in_reg)
            {
                case 0: g_reg_B = in_data; break;
                case 1: g_reg_C = in_data; break;
                case 2: g_reg_D = in_data; break;
                case 3: g_reg_E = in_data; break;
                case 4: g_reg_H = in_data; break;
                case 5: g_reg_L = in_data; break;
                // 6 = (HL) hanteras av separata op (LD (HL),r etc.)
                case 7: g_reg_A = in_data; break;
            }
        }

        private byte read_reg(byte in_reg)
        {
            return in_reg switch
            {
                0 => g_reg_B,
                1 => g_reg_C,
                2 => g_reg_D,
                3 => g_reg_E,
                4 => g_reg_H,
                5 => g_reg_L,
                6 => read_byte(g_reg_HL), // (HL) – viktig fix
                7 => g_reg_A,
                _ => (byte)0
            };
        }

        private bool chk_condion(byte in_cond)
        {
            return in_cond switch
            {
                0 => g_flag_Z == 0, // NZ
                1 => g_flag_Z == 1, // Z
                2 => g_flag_C == 0, // NC
                3 => g_flag_C == 1, // C
                4 => g_flag_PV == 0, // PO
                5 => g_flag_PV == 1, // PE
                6 => g_flag_S == 0, // P
                7 => g_flag_S == 1, // M
                _ => false
            };
        }

        private void set_flag_s(bool in_val)  => g_flag_S  = in_val ? 1 : 0;
        private void set_flag_z(bool in_val)  => g_flag_Z  = in_val ? 1 : 0;
        private void set_flag_pv(bool in_val) => g_flag_PV = in_val ? 1 : 0;
        private void set_flag_h(bool in_val)  => g_flag_H  = in_val ? 1 : 0;
        private void set_flag_c(bool in_val)  => g_flag_C  = in_val ? 1 : 0;

        private void set_flag_pv_logical(byte in_data)
        {
            // Even parity => PV = 1
            int ones = 0;
            for (int i = 0; i < 8; i++)
            {
                ones += (in_data & 1);
                in_data >>= 1;
            }
            g_flag_PV = ((ones & 1) == 0) ? 1 : 0;
        }

        private void stack_push(byte in_val)
        {
            g_reg_SP -= 1;
            write_byte(g_reg_SP, in_val);
        }

        private byte stack_pop()
        {
            byte w_val = read_byte(g_reg_SP);
            g_reg_SP += 1;
            return w_val;
        }
    }
}
