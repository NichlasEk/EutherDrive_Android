using System;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace EutherDrive.Core.MdTracerCore
{
    //----------------------------------------------------------------
    // CPU : Zilog Z80
    //----------------------------------------------------------------
    internal partial class md_z80
    {
        public bool g_active;

        private ushort g_reg_PC;
        private byte   g_reg_A;
        private byte   g_reg_B;
        private byte   g_reg_C;
        private byte   g_reg_D;
        private byte   g_reg_E;
        private byte   g_reg_H;
        private byte   g_reg_L;

        private byte   g_reg_Au;
        private byte   g_reg_Bu;
        private byte   g_reg_Cu;
        private byte   g_reg_Du;
        private byte   g_reg_Eu;
        private byte   g_reg_Fu;
        private byte   g_reg_Hu;
        private byte   g_reg_Lu;

        private ushort g_reg_SP;
        private ushort g_reg_IX;
        private ushort g_reg_IY;

        private int g_flag_S;
        private int g_flag_Z;
        private int g_flag_H;
        private int g_flag_PV;
        private int g_flag_N;
        private int g_flag_C;

        private int g_flag_Su;
        private int g_flag_Zu;
        private int g_flag_Hu;
        private int g_flag_PVu;
        private int g_flag_Nu;
        private int g_flag_Cu;

        private byte g_reg_R;
        private byte g_reg_I;

        private bool g_IFF1;
        private bool g_IFF2;
        private int  g_interruptMode;
        private bool g_interrupt_irq;
        private bool g_interrupt_nmi;
        private bool g_halt;

        private ushort g_reg_BC => (ushort)((g_reg_B << 8) + g_reg_C);
        private ushort g_reg_DE => (ushort)((g_reg_D << 8) + g_reg_E);
        private ushort g_reg_HL => (ushort)((g_reg_H << 8) + g_reg_L);

        private byte g_reg_PCH => (byte)((g_reg_PC & 0xff00) >> 8);
        private byte g_reg_PCL => (byte)(g_reg_PC & 0x00ff);
        private void g_write_PCH(byte in_val) => g_reg_PC = (ushort)((in_val << 8) + g_reg_PCL);
        private void g_write_PCL(byte in_val) => g_reg_PC = (ushort)((g_reg_PCH << 8) + in_val);

        private byte g_reg_IXH => (byte)((g_reg_IX & 0xff00) >> 8);
        private byte g_reg_IXL => (byte)(g_reg_IX & 0x00ff);
        private void g_write_IXH(byte in_val) => g_reg_IX = (ushort)((in_val << 8) + g_reg_IXL);
        private void g_write_IXL(byte in_val) => g_reg_IX = (ushort)((g_reg_IXH << 8) + in_val);

        private byte g_reg_IYH => (byte)((g_reg_IY & 0xff00) >> 8);
        private byte g_reg_IYL => (byte)(g_reg_IY & 0x00ff);
        private void g_write_IYH(byte in_val) => g_reg_IY = (ushort)((in_val << 8) + g_reg_IYL);
        private void g_write_IYL(byte in_val) => g_reg_IY = (ushort)((g_reg_IYH << 8) + in_val);

        // VIKTIGT: använd alltid bus/minnes-API:t för fetch (respekterar bankning)
        private byte   g_opcode1  => read8(g_reg_PC);
        private byte   g_opcode2  => read8((uint)(g_reg_PC + 1));
        private byte   g_opcode3  => read8((uint)(g_reg_PC + 2));
        private byte   g_opcode4  => read8((uint)(g_reg_PC + 3));
        private ushort g_opcode23 => (ushort)((read8((uint)(g_reg_PC + 2)) << 8) + read8((uint)(g_reg_PC + 1)));
        private ushort g_opcode34 => (ushort)((read8((uint)(g_reg_PC + 3)) << 8) + read8((uint)(g_reg_PC + 2)));

        private byte g_opcode1_210 => (byte)(g_opcode1 & 0x07);
        private byte g_opcode2_210 => (byte)(g_opcode2 & 0x07);
        private byte g_opcode1_543 => (byte)((g_opcode1 >> 3) & 0x07);
        private byte g_opcode2_543 => (byte)((g_opcode2 >> 3) & 0x07);
        private byte g_opcode3_543 => (byte)((g_opcode3 >> 3) & 0x07);
        private byte g_opcode4_543 => (byte)((g_opcode4 >> 3) & 0x07);
        private byte g_opcode1_54  => (byte)((g_opcode1 >> 4) & 0x03);
        private byte g_opcode2_54  => (byte)((g_opcode2 >> 4) & 0x03);

        private int g_clock;
        private int g_clock_total;

        //----------------------------------------------------------------
        public md_z80()
        {
            initialize();
        }

        public void run(int in_clock)
        {
            #if DEBUG
            // enkel manuell dump om du vill toggla den ibland
            bool www = false;
            if (www) pgout();
            #endif

            if (g_active == false) return;

            g_clock_total += in_clock;
            while (g_clock_total >= 0)
            {
                // IRQ (NMI-block ej aktiverad i originalet)
                if (g_interrupt_irq)
                {
                    if (g_IFF1)
                    {
                        if (g_halt) g_reg_PC += 1;

                        g_interrupt_irq = false;
                        g_IFF1 = false;
                        g_IFF2 = false;
                        g_halt = false;

                        switch (g_interruptMode)
                        {
                            case 0:
                                // IM 0: devices place an opcode on bus – ej implementerat här
                                break;

                            case 1:
                                stack_push(g_reg_PCH);
                                stack_push(g_reg_PCL);
                                g_reg_PC = 0x0038;
                                g_halt = false;
                                break;

                            case 2:
                                // IM 2: vektor via I-register – ej implementerat här
                                break;
                        }
                    }
                }

                g_clock = 0;
                g_operand[g_opcode1]();   // exekvera en instruktion

                #if DEBUG
                //traceout();
                //logout2();
                #endif

                g_reg_R = (byte)((g_reg_R + 1) & 0x7f);
                g_clock_total -= g_clock;
            }
        }

        public void irq_request(bool in_val)
        {
            g_interrupt_irq = in_val;
        }

        public void reset()
        {
            g_reg_PC = 0;

            g_reg_A = g_reg_B = g_reg_C = g_reg_D = g_reg_E = g_reg_H = g_reg_L = 0;
            g_reg_R = g_reg_I = 0;

            g_reg_Au = g_reg_Bu = g_reg_Cu = g_reg_Du = g_reg_Eu = g_reg_Fu = g_reg_Hu = g_reg_Lu = 0;

            g_reg_SP = 0;
            g_reg_IX = 0xffff;
            g_reg_IY = 0xffff;

            g_flag_S = 0;
            g_flag_Z = 1;
            g_flag_H = 0;
            g_flag_PV = 0;
            g_flag_N = 0;
            g_flag_C = 0;

            g_interruptMode = 0;
            g_halt = false;
            g_IFF1 = false;
            g_IFF2 = false;

            g_bank_register = 0xff8000;   // bank default (definieras i annan partial)
        }

        // ---- Prefixgrenar -----------------------------------------------------

        private void op_dd()
        {
            if (g_operand_dd[g_opcode2] == op_NOP)
            {
                Debug.WriteLine("md_z80.op_dd: odefinierad opcode");
            }
            g_operand_dd[g_opcode2]();
            g_reg_R += 1;
        }

        private void op_ddcb()
        {
            if (g_operand_ddcb[g_opcode4] == op_NOP)
            {
                Debug.WriteLine("md_z80.op_ddcb: odefinierad opcode");
            }
            g_operand_ddcb[g_opcode4]();
            g_reg_R += 1;
        }

        private void op_fd()
        {
            if (g_operand_fd[g_opcode2] == op_NOP)
            {
                Debug.WriteLine("md_z80.op_fd: odefinierad opcode");
            }
            g_operand_fd[g_opcode2]();
            g_reg_R += 1;
        }

        private void op_fdcb()
        {
            if (g_operand_fdcb[g_opcode4] == op_NOP)
            {
                Debug.WriteLine("md_z80.op_fdcb: odefinierad opcode");
            }
            g_operand_fdcb[g_opcode4]();
            g_reg_R += 1;
        }

        private void op_ed()
        {
            if (g_operand_ed[g_opcode2] == op_NOP)
            {
                Debug.WriteLine("md_z80.op_ed: odefinierad opcode");
            }
            g_operand_ed[g_opcode2]();
            g_reg_R += 1;
        }

        private void op_cb()
        {
            if (g_operand_cb[g_opcode2] == op_NOP)
            {
                Debug.WriteLine("md_z80.op_cb: odefinierad opcode");
            }
            g_operand_cb[g_opcode2]();
            g_reg_R += 1;
        }

        // ---- Debughjälp (endast i DEBUG) -------------------------------------

        #if DEBUG
        private void pgout()
        {
            try
            {
                using (BinaryWriter writer = new BinaryWriter(File.OpenWrite(@"d:\t_1.bin")))
                {
                    // OBS: g_ram kan vara def i annan partial – anpassa/kommentera bort vid behov
                    writer.Write(g_ram, 0, Math.Min(g_ram.Length, 8192));
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"pgout() failed: {ex}");
            }
        }

        private readonly uint[] log_trace = new uint[100];

        private void traceout()
        {
            for (int i = 98; i >= 0; i--) log_trace[i + 1] = log_trace[i];
            log_trace[0] = g_reg_PC;
        }

        private void logout2()
        {
            try
            {
                // g_status_flag verkar inte definieras i den här partialen — kommentera/byt om nödvändigt
                byte statusMasked = (byte)(g_status_flag & 0xD7);

                using (FileStream fs = new FileStream("d:\\log2.txt", FileMode.Append, FileAccess.Write, FileShare.None, 4096, useAsync: false))
                {
                    var sb = new StringBuilder(80);
                    sb.Append(g_reg_PC.ToString("x4")).Append(',')
                    .Append(g_reg_A.ToString("x2")).Append(',')
                    .Append(statusMasked.ToString("x2")).Append(',')
                    .Append(g_reg_BC.ToString("x4")).Append(',')
                    .Append(g_reg_DE.ToString("x4")).Append(',')
                    .Append(g_reg_HL.ToString("x4")).Append(',')
                    .Append(g_reg_IX.ToString("x4")).Append(',')
                    .Append(g_reg_IY.ToString("x4")).Append(',')
                    .Append(g_reg_SP.ToString("x4")).Append(',')
                    .Append(g_bank_register.ToString("x1"))
                    .Append(Environment.NewLine);

                    byte[] data = Encoding.UTF8.GetBytes(sb.ToString());
                    fs.Write(data, 0, data.Length);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"logout2() failed: {ex}");
            }
        }
        #endif
    }
}
