using System;
using System.IO;

namespace EutherDrive.Core.MdTracerCore
{
    internal partial class md_z80
    {
        private static readonly bool TraceSmsBranch =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_SMS_TRACE_BRANCH"), "1", StringComparison.Ordinal);
        private const int SmsBranchLogLimit = 20000;
        private static int _smsBranchLogCount;
        private static string? _smsBranchLogPath;
        private static readonly bool TraceSmsCall77 =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_SMS_TRACE_CALL77"), "1", StringComparison.Ordinal);
        private const int SmsCall77LogLimit = 20000;
        private static int _smsCall77LogCount;
        private static string? _smsCall77LogPath;
        private static readonly bool TraceSmsCall96 =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_SMS_TRACE_CALL96"), "1", StringComparison.Ordinal);
        private const int SmsCall96LogLimit = 20000;
        private static int _smsCall96LogCount;
        private static string? _smsCall96LogPath;
        private static readonly bool ForceSmsCall77ab =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_SMS_FORCE_CALL_77AB"), "1", StringComparison.Ordinal);
        private static readonly bool TraceSmsStack77 =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_SMS_DEBUG_STACK77"), "1", StringComparison.Ordinal);
        private const int SmsStack77LogLimit = 2000;
        private static int _smsStack77LogCount;
        private static string? _smsStack77LogPath;

        private void op_LD_r1_r2()
        {
            byte w_val = read_reg(g_opcode1_210);
            write_reg(g_opcode1_543, w_val);
            g_reg_PC += 1;
            g_clock = 4;
        }
        private void op_LD_r_n()
        {
            byte w_val = g_opcode2;
            write_reg(g_opcode1_543, w_val);
            g_reg_PC += 2;
            g_clock = 7;
        }
        private void op_LD_r_HL()
        {
            byte w_val = read_byte(g_reg_HL);
            write_reg(g_opcode1_543, w_val);
            g_reg_PC += 1;
            g_clock = 7;
        }
        private void op_LD_r_IXD()
        {
            byte w_val = read_byte((ushort)(g_reg_IX + (sbyte)g_opcode3));
            write_reg(g_opcode2_543, w_val);
            g_reg_PC += 3;
            g_clock = 19;
        }
        private void op_LD_r_IYD()
        {
            byte w_val = read_byte((ushort)(g_reg_IY + (sbyte)g_opcode3));
            write_reg(g_opcode2_543, w_val);
            g_reg_PC += 3;
            g_clock = 19;
        }
        private void op_LD_HL_r()
        {
            byte w_val = read_reg(g_opcode1_210);
            write_byte(g_reg_HL, w_val);
            g_reg_PC += 1;
            g_clock = 7;
        }
        private void op_LD_IXD_r()
        {
            byte w_val = read_reg(g_opcode2_210);
            ushort w_addr = (ushort)(g_reg_IX + (sbyte)g_opcode3);
            write_byte(w_addr, w_val);
            g_reg_PC += 3;
            g_clock = 19;
        }
        private void op_LD_IYD_r()
        {
            byte w_val = read_reg(g_opcode2_210);
            ushort w_addr = (ushort)(g_reg_IY + (sbyte)g_opcode3);
            write_byte(w_addr, w_val);
            g_reg_PC += 3;
            g_clock = 19;
        }
        private void op_LD_HL_n()
        {
            write_byte(g_reg_HL, g_opcode2);
            g_reg_PC += 2;
            g_clock = 10;
        }
        private void op_LD_IXD_n()
        {
            ushort w_addr = (ushort)(g_reg_IX + (sbyte)g_opcode3);
            write_byte(w_addr, g_opcode4);
            g_reg_PC += 4;
            g_clock = 19;
        }
        private void op_LD_IYD_n()
        {
            ushort w_addr = (ushort)(g_reg_IY + (sbyte)g_opcode3);
            write_byte(w_addr, g_opcode4);
            g_reg_PC += 4;
            g_clock = 19;
        }
        private void op_LD_a_BC()
        {
            g_reg_A = read_byte(g_reg_BC);
            g_reg_PC += 1;
            g_clock = 7;
        }
        private void op_LD_a_DE()
        {
            g_reg_A = read_byte(g_reg_DE);
            g_reg_PC += 1;
            g_clock = 7;
        }
        private void op_LD_a_NN()
        {
            g_reg_A = read_byte(g_opcode23);
            g_reg_PC += 3;
            g_clock = 13;
        }
        private void op_LD_BC_a()
        {
            write_byte(g_reg_BC, g_reg_A);
            g_reg_PC += 1;
            g_clock = 7;
        }
        private void op_LD_DE_a()
        {
            write_byte(g_reg_DE, g_reg_A);
            g_reg_PC += 1;
            g_clock = 7;
        }
        private void op_LD_NN_a()
        {
            write_byte(g_opcode23, g_reg_A);
            g_reg_PC += 3;
            g_clock = 13;
        }
        private void op_LD_a_i()
        {
            g_reg_A = g_reg_I;
            set_flag_s(g_reg_A >= 0x80);
            set_flag_z(g_reg_A == 0);
            set_flag_pv(g_IFF2 == true);
            g_flag_H = 0;
            g_flag_N = 0;
            g_reg_PC += 2;
            g_clock = 9;
        }
        private void op_LD_a_r()
        {
            g_reg_A = g_reg_R;
            set_flag_s(g_reg_A >= 0x80);
            set_flag_z(g_reg_A == 0);
            set_flag_pv(g_IFF1 == true);
            g_flag_H = 0;
            g_flag_N = 0;
            g_reg_PC += 2;
            g_clock = 9;
        }
        private void op_LD_i_a()
        {
            g_reg_I = g_reg_A;
            g_reg_PC += 2;
            g_clock = 9;
        }
        private void op_LD_r_a()
        {
            g_reg_R = g_reg_A;
            g_reg_PC += 2;
            g_clock = 9;
        }
        //--------------------------------------
        private void op_LD_rp_nn()
        {
            write_rp(g_opcode1_54, g_opcode23);
            g_reg_PC += 3;
            g_clock = 10;
        }
        private void op_LD_ix_nn()
        {
            g_reg_IX = g_opcode34;
            g_reg_PC += 4;
            g_clock = 14;
        }
        private void op_LD_iy_nn()
        {
            g_reg_IY = g_opcode34;
            g_reg_PC += 4;
            g_clock = 14;
        }
        private void op_LD_hl_NN()
        {
            ushort w_val = read_word(g_opcode23);
            write_rp((byte)RP_TYPE.HL, w_val);
            g_reg_PC += 3;
            g_clock = 16;
        }
        private void op_LD_rp_NN()
        {
            ushort w_val = read_word(g_opcode34);
            write_rp(g_opcode2_54, w_val);
            g_reg_PC += 4;
            g_clock = 20;
        }
        private void op_LD_ix_NN()
        {
            g_reg_IX = read_word(g_opcode34);
            g_reg_PC += 4;
            g_clock = 20;
        }
        private void op_LD_iy_NN()
        {
            g_reg_IY = read_word(g_opcode34);
            g_reg_PC += 4;
            g_clock = 20;
        }
        private void op_LD_NN_hl()
        {
            write_word(g_opcode23, g_reg_HL);
            g_reg_PC += 3;
            g_clock = 16;
        }
        private void op_LD_NN_rp()
        {
            ushort w_val = read_rp(g_opcode2_54);
            write_word(g_opcode34, w_val);
            g_reg_PC += 4;
            g_clock = 20;
        }
        private void op_LD_NN_ix()
        {
            write_word(g_opcode34, g_reg_IX);
            g_reg_PC += 4;
            g_clock = 20;
        }
        private void op_LD_NN_iy()
        {
            write_word(g_opcode34, g_reg_IY);
            g_reg_PC += 4;
            g_clock = 20;
        }
        private void op_LD_sp_hl()
        {
            g_reg_SP = g_reg_HL;
            g_reg_PC += 1;
            g_clock = 6;
        }
        private void op_LD_sp_ix()
        {
            g_reg_SP = g_reg_IX;
            g_reg_PC += 2;
            g_clock = 10;
        }
        private void op_LD_sp_iy()
        {
            g_reg_SP = g_reg_IY;
            g_reg_PC += 2;
            g_clock = 10;
        }
        //--------------------------------------
        private void M_LD_loop(bool in_count, bool in_loop)
        {
            ushort w_bc = g_reg_BC;
            ushort w_de = g_reg_DE;
            ushort w_hl = g_reg_HL;

            do
            {
                byte w_val = read_byte(w_hl);
                write_byte(w_de, w_val);
                w_bc -= 1;
                if (in_count == true)
                {
                    w_de += 1;
                    w_hl += 1;
                }
                else
                {
                    w_de -= 1;
                    w_hl -= 1;
                }
                g_clock += 21;
            } while ((in_loop == true) && (w_bc != 0));

            write_rp((byte)RP_TYPE.BC, w_bc);
            write_rp((byte)RP_TYPE.DE, w_de);
            write_rp((byte)RP_TYPE.HL, w_hl);
            g_flag_H = 0;
            g_flag_N = 0;
            // LDI/LDD: PV reflects whether BC became zero after the transfer.
            // Programs (e.g., Daffy) use RET PO after LDI to detect completion.
            set_flag_pv(w_bc != 0);
            g_clock += 16;
            g_reg_PC += 2;
        }
        private void op_LDI() { M_LD_loop(true, false); }
        private void op_LDIR() { M_LD_loop(true, true); }
        private void op_LDD() { M_LD_loop(false, false); }
        private void op_LDDR() { M_LD_loop(false, true); }
        //--------------------------------------
        private void op_EX_de_hl()
        {
            byte w_val = 0;
            w_val = g_reg_H; g_reg_H = g_reg_D; g_reg_D = w_val;
            w_val = g_reg_L; g_reg_L = g_reg_E; g_reg_E = w_val;
            g_reg_PC += 1;
            g_clock = 4;
        }
        private void op_EX_af_af2()
        {
            byte w_val = 0;
            int w_val2 = 0;
            w_val = g_reg_Au; g_reg_Au = g_reg_A; g_reg_A = w_val;
            w_val2 = g_flag_Su; g_flag_Su = g_flag_S; g_flag_S = w_val2;
            w_val2 = g_flag_Zu; g_flag_Zu = g_flag_Z; g_flag_Z = w_val2;
            w_val2 = g_flag_Hu; g_flag_Hu = g_flag_H; g_flag_H = w_val2;
            w_val2 = g_flag_PVu; g_flag_PVu = g_flag_PV; g_flag_PV = w_val2;
            w_val2 = g_flag_Nu; g_flag_Nu = g_flag_N; g_flag_N = w_val2;
            w_val2 = g_flag_Cu; g_flag_Cu = g_flag_C; g_flag_C = w_val2;
            g_reg_PC += 1;
            g_clock = 4;
        }
        private void op_EXX()
        {
            byte w_val = 0;
            w_val = g_reg_Bu; g_reg_Bu = g_reg_B; g_reg_B = w_val;
            w_val = g_reg_Cu; g_reg_Cu = g_reg_C; g_reg_C = w_val;
            w_val = g_reg_Du; g_reg_Du = g_reg_D; g_reg_D = w_val;
            w_val = g_reg_Eu; g_reg_Eu = g_reg_E; g_reg_E = w_val;
            w_val = g_reg_Hu; g_reg_Hu = g_reg_H; g_reg_H = w_val;
            w_val = g_reg_Lu; g_reg_Lu = g_reg_L; g_reg_L = w_val;
            g_reg_PC += 1;
            g_clock = 4;
        }
        private void op_EX_SP_hl()
        {
            byte w_val1 = read_byte(g_reg_SP);
            byte w_val2 = read_byte((ushort)(g_reg_SP + 1));
            write_byte(g_reg_SP, g_reg_L);
            write_byte((ushort)(g_reg_SP + 1), g_reg_H);
            g_reg_L = w_val1;
            g_reg_H = w_val2;
            g_reg_PC += 1;
            g_clock = 19;
        }
        private void op_EX_SP_ix()
        {
            byte w_val1 = read_byte(g_reg_SP);
            byte w_val2 = read_byte((ushort)(g_reg_SP + 1));
            write_byte(g_reg_SP, g_reg_IXL);
            write_byte((ushort)(g_reg_SP + 1), g_reg_IXH);
            g_write_IXL(w_val1);
            g_write_IXH(w_val2);
            g_reg_PC += 2;
            g_clock = 23;
        }
        private void op_EX_SP_iy()
        {
            byte w_val1 = read_byte(g_reg_SP);
            byte w_val2 = read_byte((ushort)(g_reg_SP + 1));
            write_byte(g_reg_SP, g_reg_IYL);
            write_byte((ushort)(g_reg_SP + 1), g_reg_IYH);
            g_write_IYL(w_val1);
            g_write_IYH(w_val2);
            g_reg_PC += 2;
            g_clock = 23;
        }
        //--------------------------------------
        private void op_PUSH_rp()
        {
            ushort w_val = read_rp(g_opcode1_54);
            stack_push((byte)((w_val >> 8) & 0xff));
            stack_push((byte)(w_val & 0xff));
            g_reg_PC += 1;
            g_clock = 11;
        }
        private void op_PUSH_af()
        {
            stack_push(g_reg_A);
            stack_push(g_status_flag);
            g_reg_PC += 1;
            g_clock = 11;
        }
        private void op_PUSH_ix()
        {
            stack_push(g_reg_IXH);
            stack_push(g_reg_IXL);
            g_reg_PC += 2;
            g_clock = 15;
        }
        private void op_PUSH_iy()
        {
            stack_push(g_reg_IYH);
            stack_push(g_reg_IYL);
            g_reg_PC += 2;
            g_clock = 15;
        }
        private void op_POP_rp()
        {
            byte lo = stack_pop();
            byte hi = stack_pop();
            write_rp(g_opcode1_54, (ushort)((hi << 8) | lo));
            g_reg_PC += 1;
            g_clock = 10;
        }
        private void op_POP_af()
        {
            g_status_flag = stack_pop();
            g_reg_A = stack_pop();
            g_reg_PC += 1;
            g_clock = 10;
        }
        private void op_POP_ix()
        {
            g_reg_IX = (ushort)(stack_pop() + (stack_pop() << 8));
            g_reg_PC += 2;
            g_clock = 14;
        }
        private void op_POP_iy()
        {
            g_reg_IY = (ushort)(stack_pop() + (stack_pop() << 8));
            g_reg_PC += 2;
            g_clock = 14;
        }
        //--------------------------------------
        private void set_flag_rotate(byte in_val, bool in_mode)
        {
            g_flag_H = 0;
            g_flag_N = 0;
            if (in_mode == true)
            {
                set_flag_s((in_val & 0x80) == 0x80);
                set_flag_z(in_val == 0);
                set_flag_pv_logical(in_val);
            }
        }
        private byte M_RRC(byte in_val, bool in_mode)
        {
            g_flag_C = in_val & 0x01;
            in_val >>= 1;
            in_val &= 0x7f;
            in_val |= (byte)((g_flag_C == 1) ? 0x80 : 0);
            set_flag_rotate(in_val, in_mode);
            return in_val;
        }
        private byte M_RLC(byte in_val, bool in_mode)
        {
            g_flag_C = (in_val & 0x80) >> 7;
            in_val <<= 1;
            in_val = (byte)(in_val | g_flag_C);
            set_flag_rotate(in_val, in_mode);
            return in_val;
        }
        private byte M_RL(byte in_val, bool in_mode)
        {
            int w_b7 = (in_val & 0x80) >> 7;
            in_val <<= 1;
            in_val = (byte)(in_val | g_flag_C);
            g_flag_C = w_b7;
            set_flag_rotate(in_val, in_mode);
            return in_val;
        }
        private byte M_RR(byte in_val, bool in_mode)
        {
            int w_b7 = in_val & 0x01;
            in_val >>= 1;
            in_val &= 0x7f;
            in_val |= (byte)((g_flag_C == 1) ? 0x80 : 0);
            g_flag_C = w_b7;
            set_flag_rotate(in_val, in_mode);
            return in_val;
        }
        private byte M_SLA(byte in_val)
        {
            g_flag_C = (in_val & 0x80) >> 7;
            in_val <<= 1;
            set_flag_rotate(in_val, true);
            return in_val;
        }
        private byte M_SRA(byte in_val)
        {
            byte w_b7 = (byte)(in_val & 0x80);
            g_flag_C = in_val & 1;
            in_val >>= 1;
            in_val &= 0x7f;
            in_val |= w_b7;
            set_flag_rotate(in_val, true);
            return in_val;
        }
        private byte M_SRL(byte in_val)
        {
            g_flag_C = in_val & 1;
            in_val >>= 1;
            in_val &= 0x7f;
            set_flag_rotate(in_val, true);
            return in_val;
        }
        private byte M_SLL(byte in_val)
        {
            g_flag_C = (in_val & 0x80) >> 7;
            in_val <<= 1;
            in_val |= 1;
            set_flag_rotate(in_val, true);
            return in_val;
        }
        //--------------------------------------
        private void op_RRCA()
        {
            g_reg_A = M_RRC(g_reg_A, false);
            g_reg_PC += 1;
            g_clock = 4;
        }
        private void op_RRC_r()
        {
            byte w_val = read_reg(g_opcode2_210);
            w_val = M_RRC(w_val, true);
            write_reg(g_opcode2_210, w_val);
            g_reg_PC += 2;
            g_clock = 4;
        }
        private void op_RRC_HL()
        {
            byte w_val = read_byte(g_reg_HL);
            w_val = M_RRC(w_val, true);
            write_byte(g_reg_HL, w_val);
            g_reg_PC += 2;
            g_clock = 15;
        }
        private void op_RRC_IXD()
        {
            ushort w_addr = (ushort)(g_reg_IX + (sbyte)g_opcode3);
            byte w_val = read_byte(w_addr);
            w_val = M_RRC(w_val, true);
            write_byte(w_addr, w_val);
            g_reg_PC += 4;
            g_clock = 23;
        }
        private void op_RRC_IYD()
        {
            ushort w_addr = (ushort)(g_reg_IY + (sbyte)g_opcode3);
            byte w_val = read_byte(w_addr);
            w_val = M_RRC(w_val, true);
            write_byte(w_addr, w_val);
            g_reg_PC += 4;
            g_clock = 23;
        }
        //--------------------------------------
        private void op_RLCA()
        {
            g_reg_A = M_RLC(g_reg_A, false);
            g_reg_PC += 1;
            g_clock = 4;
        }
        private void op_RLC_r()
        {
            byte w_val = read_reg(g_opcode2_210);
            w_val = M_RLC(w_val, true);
            write_reg(g_opcode2_210, w_val);
            g_reg_PC += 2;
            g_clock = 4;
        }
        private void op_RLC_HL()
        {
            byte w_val = read_byte(g_reg_HL);
            w_val = M_RLC(w_val, true);
            write_byte(g_reg_HL, w_val);
            g_reg_PC += 2;
            g_clock = 15;
        }
        private void op_RLC_IXD()
        {
            ushort w_addr = (ushort)(g_reg_IX + (sbyte)g_opcode3);
            byte w_val = read_byte(w_addr);
            w_val = M_RLC(w_val, true);
            write_byte(w_addr, w_val);
            g_reg_PC += 4;
            g_clock = 23;
        }
        private void op_RLC_IYD()
        {
            ushort w_addr = (ushort)(g_reg_IY + (sbyte)g_opcode3);
            byte w_val = read_byte(w_addr);
            w_val = M_RLC(w_val, true);
            write_byte(w_addr, w_val);
            g_reg_PC += 4;
            g_clock = 23;
        }
        //--------------------------------------
        private void op_RLA()
        {
            g_reg_A = M_RL(g_reg_A, false);
            g_reg_PC += 1;
            g_clock = 4;
        }
        private void op_RL_r()
        {
            byte w_val = read_reg(g_opcode2_210);
            w_val = M_RL(w_val, true);
            write_reg(g_opcode2_210, w_val);
            g_reg_PC += 2;
            g_clock = 8;
        }
        private void op_RL_HL()
        {
            byte w_val = read_byte(g_reg_HL);
            w_val = M_RL(w_val, true);
            write_byte(g_reg_HL, w_val);
            g_reg_PC += 2;
            g_clock = 15;
        }
        private void op_RL_IXD()
        {
            ushort w_addr = (ushort)(g_reg_IX + (sbyte)g_opcode3);
            byte w_val = read_byte(w_addr);
            w_val = M_RL(w_val, true);
            write_byte(w_addr, w_val);
            g_reg_PC += 4;
            g_clock = 23;
        }
        private void op_RL_IYD()
        {
            ushort w_addr = (ushort)(g_reg_IY + (sbyte)g_opcode3);
            byte w_val = read_byte(w_addr);
            w_val = M_RL(w_val, true);
            write_byte(w_addr, w_val);
            g_reg_PC += 4;
            g_clock = 23;
        }
        //--------------------------------------
        private void op_RRA()
        {
            g_reg_A = M_RR(g_reg_A, false);
            g_reg_PC += 1;
            g_clock = 4;
        }
        private void op_RR_r()
        {
            byte w_val = read_reg(g_opcode2_210);
            w_val = M_RR(w_val, true);
            write_reg(g_opcode2_210, w_val);
            g_reg_PC += 2;
            g_clock = 8;
        }
        private void op_RR_HL()
        {
            byte w_val = read_byte(g_reg_HL);
            w_val = M_RR(w_val, true);
            write_byte(g_reg_HL, w_val);
            g_reg_PC += 2;
            g_clock = 15;
        }
        private void op_RR_IXD()
        {
            ushort w_addr = (ushort)(g_reg_IX + (sbyte)g_opcode3);
            byte w_val = read_byte(w_addr);
            w_val = M_RR(w_val, true);
            write_byte(w_addr, w_val);
            g_reg_PC += 4;
            g_clock = 23;
        }
        private void op_RR_IYD()
        {
            ushort w_addr = (ushort)(g_reg_IY + (sbyte)g_opcode3);
            byte w_val = read_byte(w_addr);
            w_val = M_RR(w_val, true);
            write_byte(w_addr, w_val);
            g_reg_PC += 4;
            g_clock = 23;
        }
        //--------------------------------------
        private void op_SLA_r()
        {
            byte w_val = read_reg(g_opcode2_210);
            w_val = M_SLA(w_val);
            write_reg(g_opcode2_210, w_val);
            g_reg_PC += 2;
            g_clock = 8;
        }
        private void op_SLA_HL()
        {
            byte w_val = read_byte(g_reg_HL);
            w_val = M_SLA(w_val);
            write_byte(g_reg_HL, w_val);
            g_reg_PC += 2;
            g_clock = 15;
        }
        private void op_SLA_IXD()
        {
            ushort w_addr = (ushort)(g_reg_IX + (sbyte)g_opcode3);
            byte w_val = read_byte(w_addr);
            w_val = M_SLA(w_val);
            write_byte(w_addr, w_val);
            g_reg_PC += 4;
            g_clock = 23;
        }
        private void op_SLA_IYD()
        {
            ushort w_addr = (ushort)(g_reg_IY + (sbyte)g_opcode3);
            byte w_val = read_byte(w_addr);
            w_val = M_SLA(w_val);
            write_byte(w_addr, w_val);
            g_reg_PC += 4;
            g_clock = 23;
        }
        //--------------------------------------
        private void op_SRA_r()
        {
            byte w_val = read_reg(g_opcode2_210);
            w_val = M_SRA(w_val);
            write_reg(g_opcode2_210, w_val);
            g_reg_PC += 2;
            g_clock = 8;
        }
        private void op_SRA_HL()
        {
            byte w_val = read_byte(g_reg_HL);
            w_val = M_SRA(w_val);
            write_byte(g_reg_HL, w_val);
            g_reg_PC += 2;
            g_clock = 15;
        }
        private void op_SRA_IXD()
        {
            ushort w_addr = (ushort)(g_reg_IX + (sbyte)g_opcode3);
            byte w_val = read_byte(w_addr);
            w_val = M_SRA(w_val);
            write_byte(w_addr, w_val);
            g_reg_PC += 4;
            g_clock = 23;
        }
        private void op_SRA_IYD()
        {
            ushort w_addr = (ushort)(g_reg_IY + (sbyte)g_opcode3);
            byte w_val = read_byte(w_addr);
            w_val = M_SRA(w_val);
            write_byte(w_addr, w_val);
            g_reg_PC += 4;
            g_clock = 23;
        }
        //--------------------------------------
        private void op_SRL_r()
        {
            byte w_val = read_reg(g_opcode2_210);
            w_val = M_SRL(w_val);
            write_reg(g_opcode2_210, w_val);
            g_reg_PC += 2;
            g_clock = 8;
        }
        private void op_SRL_HL()
        {
            byte w_val = read_byte(g_reg_HL);
            w_val = M_SRL(w_val);
            write_byte(g_reg_HL, w_val);
            g_reg_PC += 2;
            g_clock = 15;
        }
        private void op_SRL_IXD()
        {
            ushort w_addr = (ushort)(g_reg_IX + (sbyte)g_opcode3);
            byte w_val = read_byte(w_addr);
            w_val = M_SRL(w_val);
            write_byte(w_addr, w_val);
            g_reg_PC += 4;
            g_clock = 23;
        }
        private void op_SRL_IYD()
        {
            ushort w_addr = (ushort)(g_reg_IY + (sbyte)g_opcode3);
            byte w_val = read_byte(w_addr);
            w_val = M_SRL(w_val);
            write_byte(w_addr, w_val);
            g_reg_PC += 4;
            g_clock = 23;
        }
        //--------------------------------------
        private byte M_ADD(byte in_val1, byte in_val2, byte in_val3)
        {
            int w_result = in_val1 + in_val2 + in_val3;
            byte w_result2 = (byte)(w_result & 0xff);
            set_flag_s((w_result2 & 0x80) > 0);
            set_flag_z(w_result2 == 0);
            set_flag_h(((in_val1 & 0xf) + (in_val2 & 0xf) + in_val3) > 0xf);
            set_flag_pv(((in_val1 ^ w_result) & 0x80) != 0 && ((in_val1 ^ in_val2) & 0x80) == 0);
            set_flag_c(w_result > 0xff);
            g_flag_N = 0;
            return w_result2;
        }
        private byte M_SUB(byte in_val1, byte in_val2, byte in_val3, bool in_c_up = true)
        {
            int w_result = in_val1 - in_val2 - in_val3;
            byte w_result2 = (byte)w_result;
            set_flag_s((w_result2 & 0x80) > 0);
            set_flag_z(w_result2 == 0);
            set_flag_h(((in_val1 & 0xf) < ((in_val2 & 0xf) + in_val3)));
            set_flag_pv(((in_val1 ^ in_val2) & 0x80) != 0 && ((in_val1 ^ w_result) & 0x80) != 0);
            if (in_c_up == true)
            {
                set_flag_c(w_result < 0);
            }
            g_flag_N = 1;
            return w_result2;
        }
        private byte M_INC(byte in_val1)
        {
            byte w_result = (byte)(in_val1 + 1);
            set_flag_s((w_result & 0x80) > 0);
            set_flag_z(w_result == 0);
            set_flag_h((w_result & 0xf) == 0);
            set_flag_pv(w_result == 0x80);
            g_flag_N = 0;
            return w_result;
        }
        private byte M_DEC(byte in_val1)
        {
            byte w_result = (byte)(in_val1 - 1);
            set_flag_s((w_result & 0x80) > 0);
            set_flag_z(w_result == 0);
            set_flag_h((w_result & 0xf) == 0xf);
            set_flag_pv(w_result == 0x7f);
            g_flag_N = 1;
            return w_result;
        }
        //--------------------------------------
        private void op_ADD_a_r()
        {
            byte w_val = read_reg(g_opcode1_210);
            g_reg_A = M_ADD(g_reg_A, w_val, 0);
            g_reg_PC += 1;
            g_clock = 4;
        }
        private void op_ADD_a_n()
        {
            byte w_val = g_opcode2;
            g_reg_A = M_ADD(g_reg_A, w_val, 0);
            g_reg_PC += 2;
            g_clock = 7;
        }
        private void op_ADD_a_HL()
        {
            byte w_val = read_byte(g_reg_HL);
            g_reg_A = M_ADD(g_reg_A, w_val, 0);
            g_reg_PC += 1;
            g_clock = 7;
        }
        private void op_ADD_a_IXD()
        {
            byte w_val = read_byte((ushort)(g_reg_IX + (sbyte)g_opcode3));
            g_reg_A = M_ADD(g_reg_A, w_val, 0);
            g_reg_PC += 3;
            g_clock = 19;
        }
        private void op_ADD_a_IYD()
        {
            byte w_val = read_byte((ushort)(g_reg_IY + (sbyte)g_opcode3));
            g_reg_A = M_ADD(g_reg_A, w_val, 0);
            g_reg_PC += 3;
            g_clock = 19;
        }

        private void op_INC_r()
        {
            byte w_val = read_reg(g_opcode1_543);
            byte w_result = M_INC(w_val);
            write_reg(g_opcode1_543, w_result);
            g_reg_PC += 1;
            g_clock = 4;
        }
        private void op_INC_HL()
        {
            byte w_val = read_byte(g_reg_HL);
            byte w_result = M_INC(w_val);
            write_byte(g_reg_HL, w_result);
            g_reg_PC += 1;
            g_clock = 11;
        }
        private void op_INC_IXD()
        {
            ushort w_addr = (ushort)(g_reg_IX + (sbyte)g_opcode3);
            byte w_val = read_byte(w_addr);
            byte w_result = M_INC(w_val);
            write_byte(w_addr, w_result);
            g_reg_PC += 3;
            g_clock = 23;
        }
        private void op_INC_IYD()
        {
            ushort w_addr = (ushort)(g_reg_IY + (sbyte)g_opcode3);
            byte w_val = read_byte(w_addr);
            byte w_result = M_INC(w_val);
            write_byte(w_addr, w_result);
            g_reg_PC += 3;
            g_clock = 23;
        }

        //--------------------------------------
        private void op_ADC_a_r()
        {
            byte w_val = read_reg(g_opcode1_210);
            g_reg_A = M_ADD(g_reg_A, w_val, (byte)g_flag_C);
            g_reg_PC += 1;
            g_clock = 4;
        }
        private void op_ADC_a_n()
        {
            byte w_val = g_opcode2;
            g_reg_A = M_ADD(g_reg_A, w_val, (byte)g_flag_C);
            g_reg_PC += 2;
            g_clock = 7;
        }
        private void op_ADC_a_HL()
        {
            byte w_val = read_byte(g_reg_HL);
            g_reg_A = M_ADD(g_reg_A, w_val, (byte)g_flag_C);
            g_reg_PC += 1;
            g_clock = 7;
        }
        private void op_ADC_a_IXD()
        {
            byte w_val = read_byte((ushort)(g_reg_IX + (sbyte)g_opcode3));
            g_reg_A = M_ADD(g_reg_A, w_val, (byte)g_flag_C);
            g_reg_PC += 3;
            g_clock = 19;
        }
        private void op_ADC_a_IYD()
        {
            byte w_val = read_byte((ushort)(g_reg_IY + (sbyte)g_opcode3));
            g_reg_A = M_ADD(g_reg_A, w_val, (byte)g_flag_C);
            g_reg_PC += 3;
            g_clock = 19;
        }
        //--------------------------------------
        private void op_SUB_a_r()
        {
            byte w_val = read_reg(g_opcode1_210);
            g_reg_A = M_SUB(g_reg_A, w_val, 0);
            g_reg_PC += 1;
            g_clock = 4;
        }
        private void op_SUB_a_n()
        {
            byte w_val = g_opcode2;
            g_reg_A = M_SUB(g_reg_A, w_val, 0);
            g_reg_PC += 2;
            g_clock = 7;
        }
        private void op_SUB_a_HL()
        {
            byte w_val = read_byte(g_reg_HL);
            g_reg_A = M_SUB(g_reg_A, w_val, 0);
            g_reg_PC += 1;
            g_clock = 7;
        }
        private void op_SUB_a_IXD()
        {
            byte w_val = read_byte((ushort)(g_reg_IX + (sbyte)g_opcode3));
            g_reg_A = M_SUB(g_reg_A, w_val, 0);
            g_reg_PC += 3;
            g_clock = 19;
        }
        private void op_SUB_a_IYD()
        {
            byte w_val = read_byte((ushort)(g_reg_IY + (sbyte)g_opcode3));
            g_reg_A = M_SUB(g_reg_A, w_val, 0);
            g_reg_PC += 3;
            g_clock = 19;
        }
        private void op_DEC_r()
        {
            byte w_val = read_reg(g_opcode1_543);
            byte w_result = M_DEC(w_val);
            write_reg(g_opcode1_543, w_result);
            g_reg_PC += 1;
            g_clock = 4;
        }
        private void op_DEC_HL()
        {
            byte w_val = read_byte(g_reg_HL);
            byte w_result = M_DEC(w_val);
            write_byte(g_reg_HL, w_result);
            g_reg_PC += 1;
            g_clock = 11;
        }
        private void op_DEC_IXD()
        {
            ushort w_addr = (ushort)(g_reg_IX + (sbyte)g_opcode3);
            byte w_val = read_byte(w_addr);
            byte w_result = M_DEC(w_val);
            write_byte(w_addr, w_result);
            g_reg_PC += 3;
            g_clock = 23;
        }
        private void op_DEC_IYD()
        {
            ushort w_addr = (ushort)(g_reg_IY + (sbyte)g_opcode3);
            byte w_val = read_byte(w_addr);
            byte w_result = M_DEC(w_val);
            write_byte(w_addr, w_result);
            g_reg_PC += 3;
            g_clock = 23;
        }
        //--------------------------------------
        private void op_SBC_a_r()
        {
            byte w_val = read_reg(g_opcode1_210);
            g_reg_A = M_SUB(g_reg_A, w_val, (byte)g_flag_C);
            g_reg_PC += 1;
            g_clock = 4;
        }
        private void op_SBC_a_n()
        {
            byte w_val = g_opcode2;
            g_reg_A = M_SUB(g_reg_A, w_val, (byte)g_flag_C);
            g_reg_PC += 2;
            g_clock = 7;
        }
        private void op_SBC_a_HL()
        {
            byte w_val = read_byte(g_reg_HL);
            g_reg_A = M_SUB(g_reg_A, w_val, (byte)g_flag_C);
            g_reg_PC += 1;
            g_clock = 7;
        }
        private void op_SBC_a_IXD()
        {
            byte w_val = read_byte((ushort)(g_reg_IX + (sbyte)g_opcode3));
            g_reg_A = M_SUB(g_reg_A, w_val, (byte)g_flag_C);
            g_reg_PC += 3;
            g_clock = 19;
        }
        private void op_SBC_a_IYD()
        {
            byte w_val = read_byte((ushort)(g_reg_IY + (sbyte)g_opcode3));
            g_reg_A = M_SUB(g_reg_A, w_val, (byte)g_flag_C);
            g_reg_PC += 3;
            g_clock = 19;
        }
        //--------------------------------------
        private ushort M_ADD_W(ushort in_val1, ushort in_val2)
        {
            int w_result = in_val1 + in_val2;
            ushort w_result2 = (ushort)w_result;
            set_flag_h(((in_val1 & 0x0FFF) + (in_val2 & 0x0FFF)) > 0x0FFF);
            set_flag_c(w_result > 0xFFFF);
            g_flag_N = 0;
            return w_result2;
        }
        private ushort M_ADC_W(ushort in_val1, ushort in_val2)
        {
            in_val2 = (ushort)(in_val2 + g_flag_C);
            int w_result = in_val1 + in_val2;
            ushort w_result2 = (ushort)(w_result);
            set_flag_s((w_result2 & 0x8000) > 0);
            set_flag_z(0 == w_result2);
            set_flag_h(((in_val1 & 0x0FFF) + (in_val2 & 0x0FFF)) > 0x0FFF);
            set_flag_pv(((in_val1 ^ w_result) & 0x8000) != 0 && ((in_val1 ^ in_val2) & 0x8000) == 0);
            set_flag_c(w_result > 0xFFFF);
            g_flag_N = 0;
            return w_result2;
        }

        private ushort M_SBC_W(ushort in_val1, ushort in_val2)
        {
            in_val2 = (ushort)(in_val2 + g_flag_C);
            int w_result = in_val1 - in_val2;
            ushort w_result2 = (ushort)(w_result & 0xFFFF);
            set_flag_s((w_result2 & 0x8000) > 0);
            set_flag_z(w_result2 == 0);
            set_flag_h(((in_val1 & 0x0FFF) < ((in_val2 & 0x0FFF))));
            set_flag_pv(((in_val1 ^ in_val2) & 0x8000) != 0 && ((in_val1 ^ w_result) & 0x8000) != 0);
            set_flag_c(w_result < 0);
            g_flag_N = 1;
            return w_result2;
        }
        //--------------------------------------
        private void op_ADD_hl_rp()
        {
            ushort w_val1 = g_reg_HL;
            ushort w_val2 = read_rp(g_opcode1_54);
            ushort w_result = M_ADD_W(w_val1, w_val2);
            write_rp((byte)RP_TYPE.HL, w_result);
            g_reg_PC += 1;
            g_clock = 11;
        }
        private void op_ADC_hl_rp()
        {
            ushort w_val1 = g_reg_HL;
            ushort w_val2 = read_rp(g_opcode2_54);
            ushort w_result = M_ADC_W(w_val1, w_val2);
            write_rp((byte)RP_TYPE.HL, w_result);
            g_reg_PC += 2;
            g_clock = 15;
        }
        private void op_ADD_ix_rp()
        {
            ushort w_val1 = g_reg_IX;
            ushort w_val2 = read_rpix(g_opcode2_54);
            g_reg_IX = M_ADD_W(w_val1, w_val2);
            g_reg_PC += 2;
            g_clock = 15;
        }
        private void op_ADD_iy_rp()
        {
            ushort w_val1 = g_reg_IY;
            ushort w_val2 = read_rpiy(g_opcode2_54);
            g_reg_IY = M_ADD_W(w_val1, w_val2);
            g_reg_PC += 2;
            g_clock = 15;
        }
        private void op_INC_rp()
        {
            byte w_reg = g_opcode1_54;
            write_rp(w_reg, (ushort)(read_rp(w_reg) + 1));
            g_reg_PC += 1;
            g_clock = 6;
        }
        private void op_INC_ix()
        {
            g_reg_IX += 1;
            g_reg_PC += 2;
            g_clock = 10;
        }
        private void op_INC_iy()
        {
            g_reg_IY += 1;
            g_reg_PC += 2;
            g_clock = 13;
        }
        //--------------------------------------
        private void op_SBC_hl_rp()
        {
            ushort w_val1 = g_reg_HL;
            ushort w_val2 = read_rp(g_opcode2_54);
            ushort w_result = M_SBC_W(w_val1, w_val2);
            write_rp((byte)RP_TYPE.HL, w_result);
            g_reg_PC += 2;
            g_clock = 15;
        }
        private void op_DEC_rp()
        {
            ushort w_val1 = read_rp(g_opcode1_54);
            write_rp(g_opcode1_54, (ushort)(w_val1 - 1));
            g_reg_PC += 1;
            g_clock = 6;
        }
        private void op_DEC_ix()
        {
            g_reg_IX -= 1;
            g_reg_PC += 2;
            g_clock = 10;
        }
        private void op_DEC_iy()
        {
            g_reg_IY -= 1;
            g_reg_PC += 2;
            g_clock = 10;
        }
        //--------------------------------------
        private void M_AND(byte in_val)
        {
            g_reg_A = (byte)(g_reg_A & in_val);
            set_flag_s((g_reg_A & 0x80) > 0);
            set_flag_z(g_reg_A == 0);
            set_flag_pv_logical(g_reg_A);
            g_flag_N = 0;
            g_flag_C = 0;
            g_flag_H = 1;
        }
        private void M_OR(byte in_val)
        {
            g_reg_A = (byte)(g_reg_A | in_val);
            set_flag_s((g_reg_A & 0x80) > 0);
            set_flag_z(g_reg_A == 0);
            set_flag_pv_logical(g_reg_A);
            g_flag_N = 0;
            g_flag_C = 0;
            g_flag_H = 0;
        }
        private void M_XOR(byte in_val)
        {
            g_reg_A = (byte)(g_reg_A ^ in_val);
            set_flag_s((g_reg_A & 0x80) > 0);
            set_flag_z(g_reg_A == 0);
            set_flag_pv_logical(g_reg_A);
            g_flag_N = 0;
            g_flag_C = 0;
            g_flag_H = 0;
        }

        //--------------------------------------
        private void op_AND_r()
        {
            byte w_val = read_reg(g_opcode1_210);
            M_AND(w_val);
            g_reg_PC += 1;
            g_clock = 4;
        }
        private void op_AND_n()
        {
            byte w_val = g_opcode2;
            M_AND(w_val);
            g_reg_PC += 2;
            g_clock = 7;
        }
        private void op_AND_HL()
        {
            byte w_val = read_byte(g_reg_HL);
            M_AND(w_val);
            g_reg_PC += 1;
            g_clock = 7;
        }
        private void op_AND_IXD()
        {
            byte w_val = read_byte((ushort)(g_reg_IX + (sbyte)g_opcode3));
            M_AND(w_val);
            g_reg_PC += 3;
            g_clock = 19;
        }
        private void op_AND_IYD()
        {
            byte w_val = read_byte((ushort)(g_reg_IY + (sbyte)g_opcode3));
            M_AND(w_val);
            g_reg_PC += 3;
            g_clock = 19;
        }
        //--------------------------------------
        private void op_OR_r()
        {
            byte w_val = read_reg(g_opcode1_210);
            M_OR(w_val);
            g_reg_PC += 1;
            g_clock = 4;
        }
        private void op_OR_n()
        {
            byte w_val = g_opcode2;
            M_OR(w_val);
            g_reg_PC += 2;
            g_clock = 7;
        }
        private void op_OR_HL()
        {
            byte w_val = read_byte(g_reg_HL);
            M_OR(w_val);
            g_reg_PC += 1;
            g_clock = 7;
        }
        private void op_OR_IXD()
        {
            byte w_val = read_byte((ushort)(g_reg_IX + (sbyte)g_opcode3));
            M_OR(w_val);
            g_reg_PC += 3;
            g_clock = 19;
        }
        private void op_OR_IYD()
        {
            byte w_val = read_byte((ushort)(g_reg_IY + (sbyte)g_opcode3));
            M_OR(w_val);
            g_reg_PC += 3;
            g_clock = 19;
        }
        //--------------------------------------
        private void op_XOR_r()
        {
            byte w_val = read_reg(g_opcode1_210);
            M_XOR(w_val);
            g_reg_PC += 1;
            g_clock = 4;
        }
        private void op_XOR_n()
        {
            byte w_val = g_opcode2;
            M_XOR(w_val);
            g_reg_PC += 2;
            g_clock = 7;
        }
        private void op_XOR_HL()
        {
            byte w_val = read_byte(g_reg_HL);
            M_XOR(w_val);
            g_reg_PC += 1;
            g_clock = 7;
        }
        private void op_XOR_IXD()
        {
            byte w_val = read_byte((ushort)(g_reg_IX + (sbyte)g_opcode3));
            M_XOR(w_val);
            g_reg_PC += 3;
            g_clock = 19;
        }
        private void op_XOR_IYD()
        {
            byte w_val = read_byte((ushort)(g_reg_IY + (sbyte)g_opcode3));
            M_XOR(w_val);
            g_reg_PC += 3;
            g_clock = 19;
        }
        //--------------------------------------
        private void op_CPL()
        {
            g_reg_A = (byte)~g_reg_A;
            g_flag_H = 1;
            g_flag_N = 1;
            g_reg_PC += 1;
            g_clock = 4;
        }
        private void op_NEG()
        {
            byte w_val = g_reg_A;
            g_reg_A = (byte)-w_val;
            set_flag_s((g_reg_A & 0x80) > 0);
            set_flag_z(g_reg_A == 0);
            set_flag_pv(w_val == 0x80);
            set_flag_h((w_val & 0x0F) != 0);
            set_flag_c(g_reg_A != 0);
            g_flag_N = 1;
            g_reg_PC += 2;
            g_clock = 8;
        }
        private void op_CCF()
        {
            g_flag_H = g_flag_C;
            g_flag_C = (g_flag_C == 1) ? 0 : 1;
            g_flag_N = 0;
            g_reg_PC += 1;
            g_clock = 4;
        }
        private void op_SCF()
        {
            g_flag_C = 1;
            g_flag_H = 0;
            g_flag_N = 0;
            g_reg_PC += 1;
            g_clock = 4;
        }
        //--------------------------------------
        private void M_BIT(byte in_val1, byte in_bit)
        {
            byte mask = (byte)(1 << in_bit);
            g_flag_Z = ((in_val1 & mask) == 0) ? 1 : 0;
            // Z80 BIT: PV mirrors Z; S set only when testing bit 7 and it is set.
            g_flag_PV = g_flag_Z;
            g_flag_S = (in_bit == 7 && (in_val1 & 0x80) != 0) ? 1 : 0;
            g_flag_H = 1;
            g_flag_N = 0;
        }
        private void op_BIT_b_r()
        {
            byte w_val1 = read_reg(g_opcode2_210);
            M_BIT(w_val1, g_opcode2_543);
            g_reg_PC += 2;
            g_clock = 8;
        }
        private void op_BIT_b_HL()
        {
            byte w_val1 = read_byte(g_reg_HL);
            M_BIT(w_val1, g_opcode2_543);
            g_reg_PC += 2;
            g_clock = 12;
        }
        private void op_BIT_b_IXD()
        {
            byte w_val1 = read_byte((ushort)(g_reg_IX + (sbyte)g_opcode3));
            M_BIT(w_val1, g_opcode4_543);
            g_reg_PC += 4;
            g_clock = 20;
        }
        private void op_BIT_b_IYD()
        {
            byte w_val1 = read_byte((ushort)(g_reg_IY + (sbyte)g_opcode3));
            M_BIT(w_val1, g_opcode4_543);
            g_reg_PC += 4;
            g_clock = 20;
        }
        //--------------------------------------
        private void op_SET_b_r()
        {
            byte w_val1 = read_reg(g_opcode2_210);
            byte w_val2 = (byte)(1 << g_opcode2_543);
            write_reg(g_opcode2_210, (byte)(w_val1 | w_val2));
            g_reg_PC += 2;
            g_clock = 8;
        }
        private void op_SET_b_HL()
        {
            byte w_val1 = read_byte(g_reg_HL);
            byte w_val2 = (byte)(1 << g_opcode2_543);
            write_byte(g_reg_HL, (byte)(w_val1 | w_val2));
            g_reg_PC += 2;
            g_clock = 15;
        }
        private void op_SET_b_IXD()
        {
            ushort w_addr = (ushort)(g_reg_IX + (sbyte)g_opcode3);
            byte w_val1 = read_byte(w_addr);
            byte w_val2 = (byte)(1 << g_opcode4_543);
            write_byte(w_addr, (byte)(w_val1 | w_val2));
            g_reg_PC += 4;
            g_clock = 23;
        }
        private void op_SET_b_IYD()
        {
            ushort w_addr = (ushort)(g_reg_IY + (sbyte)g_opcode3);
            byte w_val1 = read_byte(w_addr);
            byte w_val2 = (byte)(1 << g_opcode4_543);
            write_byte(w_addr, (byte)(w_val1 | w_val2));
            g_reg_PC += 4;
            g_clock = 23;
        }
        private void op_RES_b_r()
        {
            byte w_reg = g_opcode2_210;
            byte w_val1 = read_reg(w_reg);
            byte w_val2 = (byte)~(1 << g_opcode2_543);
            write_reg(w_reg, (byte)(w_val1 & w_val2));
            g_reg_PC += 2;
            g_clock = 8;
        }
        private void op_RES_b_HL()
        {
            byte w_val1 = read_byte(g_reg_HL);
            byte w_val2 = (byte)~(1 << g_opcode2_543);
            write_byte(g_reg_HL, (byte)(w_val1 & w_val2));
            g_reg_PC += 2;
            g_clock = 15;
        }
        private void op_RES_b_IXD()
        {
            ushort w_addr = (ushort)(g_reg_IX + (sbyte)g_opcode3);
            byte w_val1 = read_byte(w_addr);
            byte w_val2 = (byte)~(1 << g_opcode4_543);
            write_byte(w_addr, (byte)(w_val1 & w_val2));
            g_reg_PC += 4;
            g_clock = 23;
        }
        private void op_RES_b_IYD()
        {
            ushort w_addr = (ushort)(g_reg_IY + (sbyte)g_opcode3);
            byte w_val1 = read_byte(w_addr);
            byte w_val2 = (byte)~(1 << g_opcode4_543);
            write_byte(w_addr, (byte)(w_val1 & w_val2));
            g_reg_PC += 4;
            g_clock = 23;
        }
        //--------------------------------------
        private void M_CP_loop(bool in_count, bool in_loop)
        {
            ushort w_bc = g_reg_BC;
            ushort w_hl = g_reg_HL;
            byte w_val = read_byte(w_hl);
            M_SUB(g_reg_A, w_val, 0, false);
            w_bc -= 1;
            if (in_count) w_hl += 1; else w_hl -= 1;

            write_rp((byte)RP_TYPE.BC, w_bc);
            write_rp((byte)RP_TYPE.HL, w_hl);
            set_flag_pv(w_bc != 0);

            if ((in_loop == true) && (w_bc != 0) && (g_flag_Z == 0))
            {
                g_clock += 21;
            }
            else
            {
                g_clock += 16;
                g_reg_PC += 2;
            }
        }
        private void op_CPI()  { M_CP_loop(true,  false); }
        private void op_CPIR() { M_CP_loop(true,  true ); }
        private void op_CPD()  { M_CP_loop(false, false); }
        private void op_CPDR() { M_CP_loop(false, true ); }
        //--------------------------------------
        private void op_CP_r()
        {
            byte w_reg = g_opcode1_210;
            byte w_val1 = read_reg(w_reg);
            M_SUB(g_reg_A, w_val1, 0);
            g_reg_PC += 1;
            g_clock = 4;
        }
        private void op_CP_n()
        {
            byte w_val1 = g_opcode2;
            M_SUB(g_reg_A, w_val1, 0);
            g_reg_PC += 2;
            g_clock = 7;
        }
        private void op_CP_HL()
        {
            byte w_val1 = read_byte(g_reg_HL);
            M_SUB(g_reg_A, w_val1, 0);
            g_reg_PC += 1;
            g_clock = 7;
        }
        private void op_CP_IXD()
        {
            ushort w_addr = (ushort)(g_reg_IX + (sbyte)g_opcode3);
            byte w_val1 = read_byte(w_addr);
            M_SUB(g_reg_A, w_val1, 0);
            g_reg_PC += 3;
            g_clock = 7;
        }
        private void op_CP_IYD()
        {
            ushort w_addr = (ushort)(g_reg_IY + (sbyte)g_opcode3);
            byte w_val1 = read_byte(w_addr);
            M_SUB(g_reg_A, w_val1, 0);
            g_reg_PC += 3;
            g_clock = 7;
        }
        //--------------------------------------
        private void op_JP_nn()
        {
            g_reg_PC = g_opcode23;
            g_clock = 10;
        }
        private void op_JP_cc_nn()
        {
            ushort w_addr = g_opcode23;
            bool taken = chk_condion(g_opcode1_543);
            LogSmsBranch("JPcc", g_reg_PC, w_addr, taken);
            if (taken)
            {
                g_reg_PC = w_addr;
            }
            else
            {
                g_reg_PC += 3;
            }
            g_clock = 10;
        }
        private void op_JR_e()
        {
            ushort pcBefore = g_reg_PC;
            ushort pcAfterDisp = (ushort)(pcBefore + 2);
            sbyte disp = (sbyte)g_opcode2;
            ushort target = unchecked((ushort)(pcAfterDisp + disp));
            if (TraceBoot && pcBefore == 0x0006)
            {
                MdTracerCore.MdLog.WriteLine($"[JR] pc0=0006 disp=0x{g_opcode2:X2} pc_after_disp=0x{pcAfterDisp:X4} target=0x{target:X4}");
            }
            g_reg_PC = target;
            g_clock = 12;
        }
        private void op_JR_c_e()
        {
            ushort pcBefore = g_reg_PC;
            ushort pcAfterDisp = (ushort)(pcBefore + 2);
            sbyte disp = (sbyte)g_opcode2;
            ushort target = unchecked((ushort)(pcAfterDisp + disp));
            bool taken = g_flag_C == 1;
            LogSmsBranch("JRc", pcBefore, target, taken);
            if (taken)
            {
                g_reg_PC = (ushort)((int)g_reg_PC + (sbyte)g_opcode2);
            }
            g_reg_PC += 2;
            g_clock = 12;
        }
        private void op_JR_nc_e()
        {
            ushort pcBefore = g_reg_PC;
            ushort pcAfterDisp = (ushort)(pcBefore + 2);
            sbyte disp = (sbyte)g_opcode2;
            ushort target = unchecked((ushort)(pcAfterDisp + disp));
            bool taken = g_flag_C == 0;
            LogSmsBranch("JRnc", pcBefore, target, taken);
            if (taken)
            {
                g_reg_PC = (ushort)((int)g_reg_PC + (sbyte)g_opcode2);
            }
            g_reg_PC += 2;
            g_clock = 12;
        }
        private void op_JR_z_e()
        {
            ushort pcBefore = g_reg_PC;
            ushort pcAfterDisp = (ushort)(pcBefore + 2);
            sbyte disp = (sbyte)g_opcode2;
            ushort target = unchecked((ushort)(pcAfterDisp + disp));
            bool taken = g_flag_Z == 1;
            LogSmsBranch("JRz", pcBefore, target, taken);
            if (taken)
            {
                g_reg_PC = (ushort)((int)g_reg_PC + (sbyte)g_opcode2);
            }
            g_reg_PC += 2;
            g_clock = 12;
        }
        private void op_JR_nz_e()
        {
            ushort pcBefore = g_reg_PC;
            ushort pcAfterDisp = (ushort)(pcBefore + 2);
            sbyte disp = (sbyte)g_opcode2;
            ushort target = unchecked((ushort)(pcAfterDisp + disp));
            bool taken = g_flag_Z == 0;
            LogSmsBranch("JRnz", pcBefore, target, taken);
            if (taken)
            {
                g_reg_PC = (ushort)((int)g_reg_PC + (sbyte)g_opcode2);
            }
            g_reg_PC += 2;
            g_clock = 12;
        }
        private void op_JP_HL()
        {
            g_reg_PC = g_reg_HL;
            g_clock = 4;
        }
        private void op_JP_IX()
        {
            g_reg_PC = g_reg_IX;
            g_clock = 8;
        }
        private void op_JP_IY()
        {
            g_reg_PC = g_reg_IY;
            g_clock = 8;
        }
        private void op_DJNZ()
        {
            g_reg_B = (byte)(g_reg_B - 1);
            ushort pcAfterDisp = (ushort)(g_reg_PC + 2);
            if (g_reg_B != 0)
            {
                g_reg_PC = (ushort)(pcAfterDisp + (sbyte)g_opcode2);
            }
            else
            {
                g_reg_PC = pcAfterDisp;
            }
            g_clock = 13;
        }
        //--------------------------------------
        private void op_CALL_nn()
        {
            if (md_main.g_masterSystemMode && string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_SMS_CALL"), "1", StringComparison.Ordinal))
            {
                Console.WriteLine($"[SMS CALL] PC=0x{g_reg_PC:X4} op2=0x{g_opcode2:X2} op3=0x{g_opcode3:X2} target=0x{g_opcode23:X4}");
            }
            ushort target = g_opcode23;
            if (md_main.g_masterSystemMode && ForceSmsCall77ab && g_reg_PC == 0x9695 && target == 0x7728)
                target = 0x77AB;
            if (md_main.g_masterSystemMode && TraceSmsStack77 && _smsStack77LogCount < SmsStack77LogLimit && target == 0x77EE)
            {
                if (_smsStack77LogPath == null)
                {
                    string dir = Environment.GetEnvironmentVariable("EUTHERDRIVE_SMS_DUMP_DIR");
                    if (string.IsNullOrWhiteSpace(dir))
                        dir = "/home/nichlas/EutherDrive/logs";
                    Directory.CreateDirectory(dir);
                    _smsStack77LogPath = Path.Combine(dir, "sms_stack77.log");
                    File.WriteAllText(_smsStack77LogPath, "SMS stack77 log\n");
                }
                long frame = md_main.g_md_vdp?.FrameCounter ?? -1;
                ushort spBefore = g_reg_SP;
                ushort retAddr = (ushort)(g_reg_PC + 3);
                File.AppendAllText(_smsStack77LogPath,
                    $"frame={frame} CALL pc=0x{g_reg_PC:X4} target=0x{target:X4} SP=0x{spBefore:X4} ret=0x{retAddr:X4}\n");
                _smsStack77LogCount++;
            }
            LogSmsCall77(g_reg_PC, target, true);
            LogSmsCall96(g_reg_PC, target, true);
            ushort w_pc = (ushort)(g_reg_PC + 3);
            stack_push((byte)((w_pc >> 8) & 0xff));
            stack_push((byte)(w_pc & 0xff));
            g_reg_PC = target;
            g_clock = 17;
        }
        private void op_CALL_cc_nn()
        {
            bool taken = chk_condion(g_opcode1_543);
            LogSmsCall77(g_reg_PC, g_opcode23, taken);
            LogSmsBranch("CALLcc", g_reg_PC, g_opcode23, taken);
            LogSmsCall96(g_reg_PC, g_opcode23, taken);
            if (taken)
            {
                ushort w_pc = (ushort)(g_reg_PC + 3);
                stack_push((byte)((w_pc >> 8) & 0xff));
                stack_push((byte)(w_pc & 0xff));
                g_reg_PC = g_opcode23;
                g_clock = 17;
            }
            else
            {
                g_reg_PC += 3;
                g_clock = 10;
            }
        }
        private void op_RET()
        {
            if (md_main.g_masterSystemMode && TraceSmsStack77 && _smsStack77LogCount < SmsStack77LogLimit && g_reg_PC == 0x77FA)
            {
                if (_smsStack77LogPath == null)
                {
                    string dir = Environment.GetEnvironmentVariable("EUTHERDRIVE_SMS_DUMP_DIR");
                    if (string.IsNullOrWhiteSpace(dir))
                        dir = "/home/nichlas/EutherDrive/logs";
                    Directory.CreateDirectory(dir);
                    _smsStack77LogPath = Path.Combine(dir, "sms_stack77.log");
                    File.WriteAllText(_smsStack77LogPath, "SMS stack77 log\n");
                }
                long frame = md_main.g_md_vdp?.FrameCounter ?? -1;
                ushort spBefore = g_reg_SP;
                byte lo = read_byte(spBefore);
                byte hi = read_byte((ushort)(spBefore + 1));
                ushort retAddr = (ushort)((hi << 8) | lo);
                File.AppendAllText(_smsStack77LogPath,
                    $"frame={frame} RET pc=0x{g_reg_PC:X4} SP=0x{spBefore:X4} ret=0x{retAddr:X4}\n");
                _smsStack77LogCount++;
            }
            g_write_PCL(stack_pop());
            g_write_PCH(stack_pop());
            g_clock = 10;
        }
        private void op_RET_cc()
        {
            bool taken = chk_condion(g_opcode1_543);
            LogSmsBranch("RETcc", g_reg_PC, g_reg_PC, taken);
            if (taken)
            {
                g_write_PCL(stack_pop());
                g_write_PCH(stack_pop());
                g_clock += 11;
            }
            else
            {
                g_reg_PC += 1;
                g_clock += 5;
            }
        }
        private static readonly bool TraceZ80Reti =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_Z80_RETI"), "1", StringComparison.Ordinal)
            && !string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_CONSOLE"), "0", StringComparison.Ordinal)
            && !string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_AUDIO_RAW_TIMING"), "1", StringComparison.Ordinal);
        private void op_RETI()
        {
            // [INT-INSTRUMENTATION] Log pop before RETI
            ushort spBefore = g_reg_SP;
            byte pcl = stack_pop();
            byte pch = stack_pop();
            ushort pcPopped = (ushort)((pch << 8) | pcl);
            if (TraceZ80Reti)
            {
                bool spInRam = spBefore >= 0x0000 && spBefore <= 0x1FFF;
                bool spInRom = spBefore >= 0x2000 && spBefore <= 0x3FFF;
                long frame = md_main.g_md_vdp?.FrameCounter ?? -1;
                Console.WriteLine($"[Z80-RETI] frame={frame} SP=0x{spBefore:X4} popPC=0x{pcPopped:X4} spRegion={(spInRam ? "RAM" : spInRom ? "ROM" : "INVALID")}");
            }
            g_write_PCL(pcl);
            g_write_PCH(pch);
            g_IFF1 = g_IFF2;
            g_clock = 15;
        }
        private void op_RETN()
        {
            // [INT-INSTRUMENTATION] Log pop before RETN
            ushort spBefore = g_reg_SP;
            byte pcl = stack_pop();
            byte pch = stack_pop();
            ushort pcPopped = (ushort)((pch << 8) | pcl);
            if (TraceZ80Reti)
            {
                bool spInRam = spBefore >= 0x0000 && spBefore <= 0x1FFF;
                bool spInRom = spBefore >= 0x2000 && spBefore <= 0x3FFF;
                long frame = md_main.g_md_vdp?.FrameCounter ?? -1;
                Console.WriteLine($"[Z80-RETN] frame={frame} SP=0x{spBefore:X4} popPC=0x{pcPopped:X4} spRegion={(spInRam ? "RAM" : spInRom ? "ROM" : "INVALID")}");
            }
            g_write_PCL(pcl);
            g_write_PCH(pch);
            g_IFF1 = g_IFF2;
            g_clock = 14;
        }
        private void op_RST()
        {
            g_reg_SP -= 2;
            ushort w_pc = (ushort)(g_reg_PC + 1);
            write_word(g_reg_SP, w_pc);
            g_reg_PC = (ushort)(g_opcode1_543 << 3);
            g_clock = 12;
        }
        //--------------------------------------
        private void op_NOP()
        {
            g_reg_PC += 1;
            g_clock = 4;
        }
        private void op_HALT()
        {
            // Fix: lås CPU tills IRQ/NMI – flytta inte PC.
            g_halt = true;
            g_clock = 4;
        }
        private void op_DI()
        {
            g_IFF1 = false;
            g_IFF2 = false;
            _iff1Delay = 0;
            SmsControlLog($"[md_z80 SMS irq_ctrl] DI at PC=0x{g_reg_PC:X4}");
            g_reg_PC += 1;
            g_clock = 4;
        }
        private void op_EI()
        {
            _iff1Delay = 2;
            SmsControlLog($"[md_z80 SMS irq_ctrl] EI at PC=0x{g_reg_PC:X4}");
            if (md_main.g_masterSystemMode && string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_SMS_IRQ"), "1", StringComparison.Ordinal))
                Console.WriteLine($"[SMS IRQ] EI PC=0x{g_reg_PC:X4}");
            g_reg_PC += 1;
            g_clock = 4;
        }
        private void op_IM0()
        {
            g_interruptMode = 0;
            SmsControlLog($"[md_z80 SMS irq_ctrl] IM0 at PC=0x{g_reg_PC:X4}");
            g_reg_PC += 2;
            g_clock = 8;
        }
        private void op_IM1()
        {
            g_interruptMode = 1;
            SmsControlLog($"[md_z80 SMS irq_ctrl] IM1 at PC=0x{g_reg_PC:X4}");
            g_reg_PC += 2;
            g_clock = 8;
        }
        private void op_IM2()
        {
            g_interruptMode = 2;
            g_reg_PC += 2;
            g_clock = 8;
        }
        //--------------------------------------
        private void op_IN_a_N()
        {
            uint port = NormalizeIoPort((ushort)((g_reg_A << 8) | g_opcode2));
            TraceSmsIo("IN n", port, 0);
            g_reg_A = read8(port);
            g_reg_PC += 2;
            g_clock = 11;
        }
        private void op_IN_r_C()
        {
            uint port = NormalizeIoPort(g_reg_BC);
            TraceSmsIo("IN c", port, 0);
            byte value = read8(port);
            write_reg(g_opcode2_543, value);
            g_reg_PC += 2;
            g_clock = 12;
        }
        private void op_INI()
        {
            uint port = NormalizeIoPort(g_reg_BC);
            byte value = read8(port);
            write_byte(g_reg_HL, value);
            ushort newHL = (ushort)(g_reg_HL + 1);
            g_reg_H = (byte)((newHL >> 8) & 0xFF);
            g_reg_L = (byte)(newHL & 0xFF);
            g_reg_B = (byte)(g_reg_B - 1);
            g_reg_PC += 2;
            set_flag_z(g_reg_B == 0);
            g_flag_N = 1;
            g_clock = 16;
        }
        private void op_INIR()
        {
            do
            {
                uint port = NormalizeIoPort(g_reg_BC);
                byte value = read8(port);
                write_byte(g_reg_HL, value);
                ushort newHL = (ushort)(g_reg_HL + 1);
                g_reg_H = (byte)((newHL >> 8) & 0xFF);
                g_reg_L = (byte)(newHL & 0xFF);
                g_reg_B = (byte)(g_reg_B - 1);
                g_clock += 21;
            } while (g_reg_B != 0);
            g_reg_PC += 2;
            set_flag_z(g_reg_B == 0);
            g_flag_N = 1;
            g_clock += 16;
        }
        private void op_IND()
        {
            uint port = NormalizeIoPort(g_reg_BC);
            byte value = read8(port);
            write_byte(g_reg_HL, value);
            ushort newHL = (ushort)(g_reg_HL - 1);
            g_reg_H = (byte)((newHL >> 8) & 0xFF);
            g_reg_L = (byte)(newHL & 0xFF);
            g_reg_B = (byte)(g_reg_B - 1);
            g_reg_PC += 2;
            set_flag_z(g_reg_B == 0);
            g_flag_N = 1;
            g_clock = 16;
        }
        private void op_INDR()
        {
            do
            {
                uint port = NormalizeIoPort(g_reg_BC);
                byte value = read8(port);
                write_byte(g_reg_HL, value);
                ushort newHL = (ushort)(g_reg_HL - 1);
                g_reg_H = (byte)((newHL >> 8) & 0xFF);
                g_reg_L = (byte)(newHL & 0xFF);
                g_reg_B = (byte)(g_reg_B - 1);
                g_clock += 21;
            } while (g_reg_B != 0);
            g_reg_PC += 2;
            set_flag_z(g_reg_B == 0);
            g_flag_N = 1;
            g_clock += 16;
        }
        //--------------------------------------
        private void op_OUT_N_a()
        {
            uint port = NormalizeIoPort((ushort)((g_reg_A << 8) | g_opcode2));
            if (md_main.g_masterSystemMode && TraceSmsOut && ((port & 0xFF) == 0xBE || (port & 0xFF) == 0xBF))
            {
                ushort pc = g_reg_PC;
                if ((port & 0xFF) == 0xBE)
                {
                    ushort hl = g_reg_HL;
                    byte hlVal = PeekZ80ByteNoSideEffect(hl);
                    string src = hl < 0x4000 ? "RAM" : (hl >= 0x8000 ? "ROM" : "OPEN");
                    if (TraceSmsOutDetail)
                    {
                        string bytes = DumpZ80PcBytes(pc, 0, 7);
                        long frame = md_main.g_md_vdp?.FrameCounter ?? -1;
                        Console.WriteLine($"[SMS OUT] frame={frame} pc=0x{pc:X4} op=OUT n,A port=0x{(port & 0xFF):X2} A=0x{g_reg_A:X2} F=0x{g_status_flag:X2} BC=0x{g_reg_BC:X4} DE=0x{g_reg_DE:X4} HL=0x{hl:X4} src={src} mem=0x{hlVal:X2} bytes={bytes}");
                    }
                    else
                    {
                        long frame = md_main.g_md_vdp?.FrameCounter ?? -1;
                        Console.WriteLine($"[SMS OUT] frame={frame} pc=0x{pc:X4} op=OUT n,A port=0x{(port & 0xFF):X2} A=0x{g_reg_A:X2} HL=0x{hl:X4} src={src} mem=0x{hlVal:X2}");
                    }
                }
                else
                {
                    Console.WriteLine($"[SMS OUT] pc=0x{pc:X4} op=OUT n,A port=0x{(port & 0xFF):X2} A=0x{g_reg_A:X2}");
                }
            }
            TraceSmsIo("OUT n", port, g_reg_A);
            write8(port, g_reg_A);
            g_reg_PC += 2;
            g_clock = 11;
        }
        private void op_OUT_C_r()
        {
            uint port = NormalizeIoPort(g_reg_BC);
            byte value = read_reg(g_opcode2_543);
            if (md_main.g_masterSystemMode && TraceSmsOut && ((port & 0xFF) == 0xBE || (port & 0xFF) == 0xBF))
            {
                ushort pc = g_reg_PC;
                if ((port & 0xFF) == 0xBE)
                {
                    ushort hl = g_reg_HL;
                    byte hlVal = PeekZ80ByteNoSideEffect(hl);
                    string src = hl < 0x4000 ? "RAM" : (hl >= 0x8000 ? "ROM" : "OPEN");
                    if (TraceSmsOutDetail)
                    {
                        string bytes = DumpZ80PcBytes(pc, 0, 7);
                        long frame = md_main.g_md_vdp?.FrameCounter ?? -1;
                        Console.WriteLine($"[SMS OUT] frame={frame} pc=0x{pc:X4} op=OUT (C),r port=0x{(port & 0xFF):X2} val=0x{value:X2} A=0x{g_reg_A:X2} F=0x{g_status_flag:X2} BC=0x{g_reg_BC:X4} DE=0x{g_reg_DE:X4} HL=0x{hl:X4} src={src} mem=0x{hlVal:X2} bytes={bytes}");
                    }
                    else
                    {
                        long frame = md_main.g_md_vdp?.FrameCounter ?? -1;
                        Console.WriteLine($"[SMS OUT] frame={frame} pc=0x{pc:X4} op=OUT (C),r port=0x{(port & 0xFF):X2} val=0x{value:X2} A=0x{g_reg_A:X2} B=0x{g_reg_B:X2} C=0x{g_reg_C:X2} HL=0x{hl:X4} src={src} mem=0x{hlVal:X2}");
                    }
                }
                else
                {
                    Console.WriteLine($"[SMS OUT] pc=0x{pc:X4} op=OUT (C),r port=0x{(port & 0xFF):X2} val=0x{value:X2} A=0x{g_reg_A:X2} B=0x{g_reg_B:X2} C=0x{g_reg_C:X2}");
                }
            }
            TraceSmsIo("OUT c", port, value);
            write8(port, value);
            g_reg_PC += 2;
            g_clock = 12;
        }
        private void op_OUTI()
        {
            uint port = NormalizeIoPort(g_reg_BC);
            byte value = read_byte(g_reg_HL);
            if (md_main.g_masterSystemMode && TraceSmsOuti)
            {
                ushort pc = g_reg_PC;
                ushort hl = g_reg_HL;
                byte b = g_reg_B;
                byte c = g_reg_C;
                string src = hl >= 0xC000 ? "RAM" : "ROM";
                Console.WriteLine($"[SMS OUTI] pc=0x{pc:X4} port=0x{(port & 0xFF):X2} HL=0x{hl:X4} src={src} val=0x{value:X2} B=0x{b:X2} C=0x{c:X2}");
            }
            write8(port, value);
            ushort newHL = (ushort)(g_reg_HL + 1);
            g_reg_H = (byte)((newHL >> 8) & 0xFF);
            g_reg_L = (byte)(newHL & 0xFF);
            g_reg_B = (byte)(g_reg_B - 1);
            g_reg_PC += 2;
            set_flag_z(g_reg_B == 0);
            g_flag_N = 1;
            g_clock = 16;
        }
        private void op_OUTIR()
        {
            do
            {
                uint port = NormalizeIoPort(g_reg_BC);
                byte value = read_byte(g_reg_HL);
                if (md_main.g_masterSystemMode && TraceSmsOuti)
                {
                    ushort pc = g_reg_PC;
                    ushort hl = g_reg_HL;
                    byte b = g_reg_B;
                    byte c = g_reg_C;
                    string src = hl >= 0xC000 ? "RAM" : "ROM";
                    Console.WriteLine($"[SMS OUTIR] pc=0x{pc:X4} port=0x{(port & 0xFF):X2} HL=0x{hl:X4} src={src} val=0x{value:X2} B=0x{b:X2} C=0x{c:X2}");
                }
                write8(port, value);
                ushort newHL = (ushort)(g_reg_HL + 1);
                g_reg_H = (byte)((newHL >> 8) & 0xFF);
                g_reg_L = (byte)(newHL & 0xFF);
                g_reg_B = (byte)(g_reg_B - 1);
                g_clock += 21;
            } while (g_reg_B != 0);
            g_reg_PC += 2;
            set_flag_z(g_reg_B == 0);
            g_flag_N = 1;
            g_clock += 16;
        }
        private void op_OUTD()
        {
            uint port = NormalizeIoPort(g_reg_BC);
            byte value = read_byte(g_reg_HL);
            if (md_main.g_masterSystemMode && TraceSmsOuti)
            {
                ushort pc = g_reg_PC;
                ushort hl = g_reg_HL;
                byte b = g_reg_B;
                byte c = g_reg_C;
                string src = hl >= 0xC000 ? "RAM" : "ROM";
                Console.WriteLine($"[SMS OUTD] pc=0x{pc:X4} port=0x{(port & 0xFF):X2} HL=0x{hl:X4} src={src} val=0x{value:X2} B=0x{b:X2} C=0x{c:X2}");
            }
            write8(port, value);
            ushort newHL = (ushort)(g_reg_HL - 1);
            g_reg_H = (byte)((newHL >> 8) & 0xFF);
            g_reg_L = (byte)(newHL & 0xFF);
            g_reg_B = (byte)(g_reg_B - 1);
            g_reg_PC += 2;
            set_flag_z(g_reg_B == 0);
            g_flag_N = 1;
            g_clock = 16;
        }
        private void op_OUTDR()
        {
            do
            {
                uint port = NormalizeIoPort(g_reg_BC);
                byte value = read_byte(g_reg_HL);
                if (md_main.g_masterSystemMode && TraceSmsOuti)
                {
                    ushort pc = g_reg_PC;
                    ushort hl = g_reg_HL;
                    byte b = g_reg_B;
                    byte c = g_reg_C;
                    string src = hl >= 0xC000 ? "RAM" : "ROM";
                    Console.WriteLine($"[SMS OUTDR] pc=0x{pc:X4} port=0x{(port & 0xFF):X2} HL=0x{hl:X4} src={src} val=0x{value:X2} B=0x{b:X2} C=0x{c:X2}");
                }
                write8(port, value);
                ushort newHL = (ushort)(g_reg_HL - 1);
                g_reg_H = (byte)((newHL >> 8) & 0xFF);
                g_reg_L = (byte)(newHL & 0xFF);
                g_reg_B = (byte)(g_reg_B - 1);
                g_clock += 21;
            } while (g_reg_B != 0);
            g_reg_PC += 2;
            set_flag_z(g_reg_B == 0);
            g_flag_N = 1;
            g_clock += 16;
        }
        //--------------------------------------
        private void LogSmsBranch(string kind, ushort pc, ushort target, bool taken)
        {
            if (!md_main.g_masterSystemMode || !TraceSmsBranch)
                return;

            if (_smsBranchLogCount >= SmsBranchLogLimit)
                return;

            // Only care about the upload control window.
            if (pc < 0x77AB || pc > 0x7805)
                return;

            if (_smsBranchLogPath == null)
            {
                string dir = Environment.GetEnvironmentVariable("EUTHERDRIVE_SMS_DUMP_DIR");
                if (string.IsNullOrWhiteSpace(dir))
                    dir = "/home/nichlas/EutherDrive/logs";
                Directory.CreateDirectory(dir);
                _smsBranchLogPath = Path.Combine(dir, "sms_branch_77.log");
                File.WriteAllText(_smsBranchLogPath, "SMS branch log (0x77AB-0x7805)\n");
            }

            long frame = md_main.g_md_vdp?.FrameCounter ?? -1;
            string flags = $"S={g_flag_S} Z={g_flag_Z} H={g_flag_H} P/V={g_flag_PV} N={g_flag_N} C={g_flag_C}";
            string line = $"frame={frame} pc=0x{pc:X4} {kind} target=0x{target:X4} taken={(taken ? 1 : 0)} {flags}\n";
            File.AppendAllText(_smsBranchLogPath, line);
            _smsBranchLogCount++;
        }

        private void LogSmsCall77(ushort pc, ushort target, bool taken)
        {
            if (!md_main.g_masterSystemMode || !TraceSmsCall77)
                return;

            if (_smsCall77LogCount >= SmsCall77LogLimit)
                return;

            if (target != 0x77AB && target != 0x7728 && target != 0x77EE)
                return;

            if (_smsCall77LogPath == null)
            {
                string dir = Environment.GetEnvironmentVariable("EUTHERDRIVE_SMS_DUMP_DIR");
                if (string.IsNullOrWhiteSpace(dir))
                    dir = "/home/nichlas/EutherDrive/logs";
                Directory.CreateDirectory(dir);
                _smsCall77LogPath = Path.Combine(dir, "sms_call77.log");
                File.WriteAllText(_smsCall77LogPath, "SMS call77 log\n");
            }

            long frame = md_main.g_md_vdp?.FrameCounter ?? -1;
            string line = $"frame={frame} pc=0x{pc:X4} target=0x{target:X4} taken={(taken ? 1 : 0)}\n";
            File.AppendAllText(_smsCall77LogPath, line);
            _smsCall77LogCount++;
        }

        private void LogSmsCall96(ushort pc, ushort target, bool taken)
        {
            if (!md_main.g_masterSystemMode || !TraceSmsCall96)
                return;

            if (_smsCall96LogCount >= SmsCall96LogLimit)
                return;

            if (target < 0x9680 || target > 0x96B0)
                return;

            if (_smsCall96LogPath == null)
            {
                string dir = Environment.GetEnvironmentVariable("EUTHERDRIVE_SMS_DUMP_DIR");
                if (string.IsNullOrWhiteSpace(dir))
                    dir = "/home/nichlas/EutherDrive/logs";
                Directory.CreateDirectory(dir);
                _smsCall96LogPath = Path.Combine(dir, "sms_call96.log");
                File.WriteAllText(_smsCall96LogPath, "SMS call96 log\n");
            }

            long frame = md_main.g_md_vdp?.FrameCounter ?? -1;
            string line = $"frame={frame} pc=0x{pc:X4} target=0x{target:X4} taken={(taken ? 1 : 0)}\n";
            File.AppendAllText(_smsCall96LogPath, line);
            _smsCall96LogCount++;
        }

        private uint NormalizeIoPort(ushort port)
        {
            ushort low = (ushort)(port & 0x00FF);
            if (md_main.g_masterSystemMode)
                return 0x10000u | low;
            // Map YM2612 ports to memory-mapped addresses:
            // Port 0x40 -> 0xA04000 (YM2612 Port 0 Address, A0=0,A1=0)
            // Port 0x41 -> 0xA04001 (YM2612 Port 0 Data, A0=1,A1=0)
            // Port 0x42 -> 0xA04002 (YM2612 Port 1 Address, A0=0,A1=1)
            // Port 0x43 -> 0xA04003 (YM2612 Port 1 Data, A0=1,A1=1)
            if (low >= 0x40 && low <= 0x43)
            {
                uint result = 0xA00000u | (uint)(0x4000 | low);
                if (MdTracerCore.MdLog.Enabled)
                    Console.WriteLine($"[Z80IO] port=0x{port:X4} -> addr=0x{result:X6} pc=0x{DebugPc:X4}");
                return result;
            }
            // Ports 0x00-0x03 also map to YM2612 (legacy/alternative mapping)
            if (low <= 0x03)
            {
                uint result = 0xA00000u | (uint)(0x4000 | low);
                if (MdTracerCore.MdLog.Enabled)
                    Console.WriteLine($"[Z80IO] port=0x{port:X4} -> addr=0x{result:X6} pc=0x{DebugPc:X4}");
                return result;
            }
            return low;
        }

        private static readonly bool TraceSmsOuti =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_SMS_OUTI"), "1", StringComparison.Ordinal);
        private static readonly bool TraceSmsOut =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_SMS_OUT"), "1", StringComparison.Ordinal);
        private static readonly bool TraceSmsOutDetail =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_SMS_OUT_DETAIL"), "1", StringComparison.Ordinal);

        private static readonly bool TraceSmsIoEnabled =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_SMS_IO"), "1", StringComparison.Ordinal);
        private const int TraceSmsIoLimit = 64;
        private static int _traceSmsIoCount;
        private void TraceSmsIo(string op, uint port, byte value)
        {
            if (!md_main.g_masterSystemMode || !TraceSmsIoEnabled)
                return;
            if (_traceSmsIoCount >= TraceSmsIoLimit)
                return;
            _traceSmsIoCount++;
            ushort pc = g_reg_PC;
            ushort p = (ushort)(port & 0xFFFF);
            Console.WriteLine($"[SMS IO] {op} port=0x{p:X2} val=0x{value:X2} PC=0x{pc:X4}");
        }

        private void op_DAA()
        {
            byte w_add = 0;
            if ((g_flag_C == 1) || (g_reg_A > 0x99)) w_add = 0x60;
            if ((g_flag_H == 1) || ((g_reg_A & 0x0f) > 0x09)) w_add += 0x06;

            byte w_val_a = g_reg_A;
            w_val_a += (byte)((g_flag_N == 0) ? w_add : -w_add);

            set_flag_s((w_val_a & 0x80) > 0);
            set_flag_z(w_val_a == 0);
            set_flag_h((((g_reg_A ^ w_val_a) & 0x10) >> 4) == 1);
            set_flag_pv_logical(w_val_a);
            if (g_reg_A > 0x99) g_flag_C = 1;

            g_reg_A = w_val_a;
            g_reg_PC += 1;
            g_clock = 4;
        }
        private void op_RLD()
        {
            byte w_reg_a = g_reg_A;
            byte w_val = read_byte(g_reg_HL);
            g_reg_A = (byte)(((byte)(w_reg_a & 0xF0)) | (((byte)(w_val & 0xF0)) >> 4));
            write_byte(g_reg_HL, (byte)((w_reg_a & 0x0F) | ((w_val & 0x0F) << 4)));

            set_flag_s((g_reg_A & 0x80) > 0);
            set_flag_z(g_reg_A == 0);
            g_flag_H = 0;
            g_flag_N = 0;
            set_flag_pv_logical(g_reg_A);

            g_reg_PC += 2;
            g_clock = 18;
        }
        private void op_RRD()
        {
            byte w_reg_a = g_reg_A;
            byte w_val = read_byte(g_reg_HL);
            g_reg_A = (byte)((w_reg_a & 0xF0) | (w_val & 0x0F));
            write_byte(g_reg_HL, (byte)(((w_reg_a & 0x0F) << 4) | ((w_val & 0xF0) >> 4)));

            set_flag_s((g_reg_A & 0x80) > 0);
            set_flag_z(g_reg_A == 0);
            g_flag_H = 0;
            g_flag_N = 0;
            set_flag_pv_logical(g_reg_A);

            g_reg_PC += 2;
            g_clock = 18;
        }
    }
}
