using System;
using System.Diagnostics;

namespace EutherDrive.Core.MdTracerCore
{
    internal partial class md_ym2612
    {
        private static readonly bool TraceDac =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_DAC"), "1", StringComparison.Ordinal);
        private static readonly bool TraceYmReg =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_YMREG"), "1", StringComparison.Ordinal);
        private static readonly bool TraceYmIrq =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_YMIRQ"), "1", StringComparison.Ordinal);

        private long _dacWriteCount;
        private long _dacEnableCount;
        private long _dacDisableCount;
        private byte _dacLastValue;
        private bool _dacEnabled;
        private long _dacLastLogTicks;
        private int _ymRegLogRemaining = 256;
        private int _key28LogRemaining = 256;
        private bool _key28KeyOffLogged;
        private bool _ymIrqAsserted;

        private bool g_reg_22_lfo_enable;
        private int g_reg_22_lfo_inc;
        private int g_reg_24_timerA;
        private int g_reg_26_timerB;
        private byte g_reg_27_mode;
        private bool g_reg_27_enable_A;
        private bool g_reg_27_enable_B;
        private bool g_reg_27_load_B;
        private bool g_reg_27_load_A;
        private int _timerAReload = 1024;
        private int _timerBReload = 256 << 4;
        private int _timerACount = 1024;
        private int _timerBCount = 256 << 4;
        private double _timerTickFrac;
        private bool _timersDrivenByZ80;
        private int g_reg_2a_dac_data;
        private int g_reg_2b_dac;

        private double[,] g_reg_30_multi = new double[0, 0];
        private int[,] g_reg_30_dt = new int[0, 0];
        private int[,] g_reg_40_tl = new int[0, 0];
        private int[,] g_reg_50_key_scale = new int[0, 0];
        private int[,] g_reg_60_ams_enable = new int[0, 0];
        private int[,] g_reg_80_sl = new int[0, 0];
        private int[,] g_reg_90_ssg = new int[0, 0];
        private int[,] g_reg_a0_fnum = new int[0, 0];
        private int[,] g_reg_a4_fnum = new int[0, 0];
        private int[,] g_reg_a4_block = new int[0, 0];

        private byte[] g_reg_b0_fb = Array.Empty<byte>();
        private int[] g_reg_b0_algo = Array.Empty<int>();
        private bool[] g_reg_b4_l = Array.Empty<bool>();
        private bool[] g_reg_b4_r = Array.Empty<bool>();
        private int[] g_reg_b4_ams = Array.Empty<int>();
        public int[] g_reg_b4_pms = Array.Empty<int>();

        public byte read8(uint in_address)
        {
            return g_com_status;
        }

        public byte ReadStatus(bool clearOnRead)
        {
            byte status = g_com_status;
            if (clearOnRead && (status & 0x03) != 0)
            {
                g_com_status &= 0xFC;
                UpdateYmIrq("statusRead");
            }
            return status;
        }

        public void write8(uint in_address, byte in_val)
        {
            int w_mode = -1;
            byte w_addr = 0;

            in_address &= 0x0003;

            switch (in_address & 0x00000f)
            {
                case 0:
                {
                    g_reg_addr1 = in_val;
                    if (TraceYmReg && in_val == 0x28 && _key28LogRemaining > 0)
                    {
                        _key28LogRemaining--;
                        ushort pc = md_main.g_md_z80 != null ? md_main.g_md_z80.DebugPc : (ushort)0xFFFF;
                        Console.WriteLine($"[KEY28] pc=0x{pc:X4} sel=1 val=0x{in_val:X2}");
                    }
                    break;
                }

                case 1:
                {
                    w_mode = 0;
                    w_addr = g_reg_addr1;
                    break;
                }

                case 2:
                {
                    g_reg_addr2 = in_val;
                    if (TraceYmReg && in_val == 0x28 && _key28LogRemaining > 0)
                    {
                        _key28LogRemaining--;
                        ushort pc = md_main.g_md_z80 != null ? md_main.g_md_z80.DebugPc : (ushort)0xFFFF;
                        Console.WriteLine($"[KEY28] pc=0x{pc:X4} sel=2 val=0x{in_val:X2}");
                    }
                    break;
                }

                case 3:
                {
                    w_mode = 1;
                    w_addr = g_reg_addr2;
                    break;
                }
            }

            if (w_mode == -1)
                return;

            // skriv alltid till reg-matrisen
            g_reg[w_mode, w_addr] = in_val;
            MaybeLogYmReg(w_mode, w_addr, in_val);

            // ------------------------------------------------------------
            // 0x20..0x2B (mode 0 only)
            // ------------------------------------------------------------
            if ((0x20 <= w_addr) && (w_addr <= 0x2b))
            {
                if (w_mode == 0)
                {
                    switch (w_addr)
                    {
                        case 0x22:
                        {
                            if ((in_val & 0x08) == 0x08)
                            {
                                g_reg_22_lfo_enable = true;
                                g_reg_22_lfo_inc = LFO_INC_MAP[in_val & 0x07];
                            }
                            else
                            {
                                g_reg_22_lfo_enable = false;
                                g_reg_22_lfo_inc = 0;
                            }
                            break;
                        }

                        case 0x24:
                        {
                            g_reg_24_timerA = (g_reg_24_timerA & 0x003) | (((int)in_val) << 2);

                            int newTimerA = (1024 - g_reg_24_timerA) << 12;
                            if (g_com_timerA != newTimerA)
                            {
                                g_com_timerA_cnt = g_com_timerA = newTimerA;
                            }
                            UpdateTimerA();
                            break;
                        }

                        case 0x25:
                        {
                            g_reg_24_timerA = (g_reg_24_timerA & 0x3fc) | (in_val & 3);

                            int newTimerA = (1024 - g_reg_24_timerA) << 12;
                            if (g_com_timerA != newTimerA)
                            {
                                g_com_timerA_cnt = g_com_timerA = newTimerA;
                            }
                            UpdateTimerA();
                            break;
                        }

                        case 0x26:
                        {
                            g_reg_26_timerB = in_val;

                            int newTimerB = (256 - g_reg_26_timerB) << (4 + 12);
                            if (g_com_timerB != newTimerB)
                            {
                                g_com_timerB = newTimerB;
                                g_com_timerB_cnt = g_com_timerB;
                            }
                            UpdateTimerB();
                            break;
                        }

                        case 0x27:
                        {
                            if (((in_val ^ g_reg_27_mode) & 0x40) != 0)
                            {
                                g_ch_reg_reflesh[2] = true;
                            }

                            if ((in_val & 0x10) != 0)
                                g_com_status &= 0xFE;
                            if ((in_val & 0x20) != 0)
                                g_com_status &= 0xFD;

                            g_reg_27_mode = in_val;
                            g_reg_27_enable_B = (in_val & 0x08) != 0;
                            g_reg_27_enable_A = (in_val & 0x04) != 0;
                            g_reg_27_load_B = (in_val & 0x02) != 0;
                            g_reg_27_load_A = (in_val & 0x01) != 0;
                            if (g_reg_27_load_A)
                                _timerACount = _timerAReload;
                            if (g_reg_27_load_B)
                                _timerBCount = _timerBReload;
                            UpdateYmIrq("reg27");
                            break;
                        }

                        case 0x28:
                        {
                            if ((in_val & 0x03) != 0x03)
                            {
                                if (TraceYmReg && _key28LogRemaining > 0)
                                {
                                    int slotMask = (in_val >> 4) & 0x0F;
                                    if (slotMask != 0)
                                    {
                                        _key28LogRemaining--;
                                        ushort pc = md_main.g_md_z80 != null ? md_main.g_md_z80.DebugPc : (ushort)0xFFFF;
                                        Console.WriteLine($"[KEY28] pc=0x{pc:X4} val=0x{in_val:X2} ch={(in_val & 0x07)} slotmask=0x{slotMask:X1}");
                                    }
                                    else if (!_key28KeyOffLogged)
                                    {
                                        _key28KeyOffLogged = true;
                                        _key28LogRemaining--;
                                        ushort pc = md_main.g_md_z80 != null ? md_main.g_md_z80.DebugPc : (ushort)0xFFFF;
                                        Console.WriteLine($"[KEY28] pc=0x{pc:X4} val=0x{in_val:X2} ch={(in_val & 0x07)} slotmask=0x0");
                                    }
                                }
                                int w_ch = KEYON_MAP[in_val & 0x07];

                                if ((in_val & 0x10) != 0) Slot_Key_on(w_ch, 0); else Slot_Key_off(w_ch, 0);
                                if ((in_val & 0x20) != 0) Slot_Key_on(w_ch, 1); else Slot_Key_off(w_ch, 1);
                                if ((in_val & 0x40) != 0) Slot_Key_on(w_ch, 2); else Slot_Key_off(w_ch, 2);
                                if ((in_val & 0x80) != 0) Slot_Key_on(w_ch, 3); else Slot_Key_off(w_ch, 3);
                            }
                            break;
                        }

                        // case 0x29: Not implemented as processing is not required

                        case 0x2A:
                        {
                            if (TraceDac)
                            {
                                _dacWriteCount++;
                                _dacLastValue = in_val;
                                MaybeLogDac();
                            }
                            g_reg_2a_dac_data = ((int)(uint)in_val - 0x80) << DAC_SHIFT;
                            break;
                        }

                        case 0x2B:
                        {
                            if (TraceDac)
                            {
                                bool enabled = (in_val & 0x80) != 0;
                                if (enabled)
                                    _dacEnableCount++;
                                else
                                    _dacDisableCount++;
                                _dacEnabled = enabled;
                                MaybeLogDac();
                            }
                            g_reg_2b_dac = in_val & 0x80;
                            break;
                        }
                    }
                }

                return;
            }

            // ------------------------------------------------------------
            // 0x30..0x9E (slot regs) - NOTE: C# switch case scope fix here
            // ------------------------------------------------------------
            if ((0x30 <= w_addr) && (w_addr <= 0x9e) && ((w_addr & 0x03) != 3))
            {
                int w_ch = 0;
                int w_slot = 0;

                switch (w_addr & 0xf0)
                {
                    case 0x30:
                    {
                        w_ch = ((w_addr - 0x30) & 0x03) + (w_mode * 3);
                        w_slot = (w_addr - 0x30) >> 2;
                        w_slot = SLOT_MAP[w_slot];

                        g_reg_30_multi[w_ch, w_slot] = MULTIPLE_TABLE[in_val & 0x0F];
                        g_reg_30_dt[w_ch, w_slot] = (in_val >> 4) & 7;
                        g_ch_reg_reflesh[w_ch] = true;
                        break;
                    }

                    case 0x40:
                    {
                        w_ch = ((w_addr - 0x40) & 0x03) + (w_mode * 3);
                        w_slot = (w_addr - 0x40) >> 2;
                        w_slot = SLOT_MAP[w_slot];

                        g_reg_40_tl[w_ch, w_slot] = (int)((in_val & 0x7f) << (CNT_HIGH_BIT - 7));
                        break;
                    }

                    case 0x50:
                    {
                        w_ch = ((w_addr - 0x50) & 0x03) + (w_mode * 3);
                        w_slot = (w_addr - 0x50) >> 2;
                        w_slot = SLOT_MAP[w_slot];

                        g_reg_50_key_scale[w_ch, w_slot] = 3 - (in_val >> 6);

                        int low5 = in_val & 0x1f;
                        g_slot_env_indexA[w_ch, w_slot] = (low5 != 0) ? (low5 << 1) : 0;
                        g_slot_env_incA[w_ch, w_slot] =
                        (int)ENV_RATE_A_TABLE[g_slot_env_indexA[w_ch, w_slot] + g_slot_key_scale[w_ch, w_slot]];

                        g_ch_reg_reflesh[w_ch] = true;
                        break;
                    }

                    case 0x60:
                    {
                        w_ch = ((w_addr - 0x60) & 0x03) + (w_mode * 3);
                        w_slot = (w_addr - 0x60) >> 2;
                        w_slot = SLOT_MAP[w_slot];

                        if ((g_reg_60_ams_enable[w_ch, w_slot] = (in_val & 0x80)) != 0)
                            g_slot_ams[w_ch, w_slot] = g_reg_b4_ams[w_ch];
                        else
                            g_slot_ams[w_ch, w_slot] = 31;

                        int low5 = in_val & 0x1f;
                        g_slot_env_indexD[w_ch, w_slot] = (low5 != 0) ? (low5 << 1) : 0;
                        g_slot_env_incD[w_ch, w_slot] =
                        (int)ENV_RATE_D_TABLE[g_slot_env_indexD[w_ch, w_slot] + g_slot_key_scale[w_ch, w_slot]];

                        g_ch_reg_reflesh[w_ch] = true;
                        break;
                    }

                    case 0x70:
                    {
                        w_ch = ((w_addr - 0x70) & 0x03) + (w_mode * 3);
                        w_slot = (w_addr - 0x70) >> 2;
                        w_slot = SLOT_MAP[w_slot];

                        int low5 = in_val & 0x1f;
                        g_slot_env_indexS[w_ch, w_slot] = (low5 != 0) ? (low5 << 1) : 0;
                        g_slot_env_incS[w_ch, w_slot] =
                        (int)ENV_RATE_D_TABLE[g_slot_env_indexS[w_ch, w_slot] + g_slot_key_scale[w_ch, w_slot]];

                        g_ch_reg_reflesh[w_ch] = true;
                        break;
                    }

                    case 0x80:
                    {
                        w_ch = ((w_addr - 0x80) & 0x03) + (w_mode * 3);
                        w_slot = (w_addr - 0x80) >> 2;
                        w_slot = SLOT_MAP[w_slot];

                        g_reg_80_sl[w_ch, w_slot] = (int)SL_TABLE[in_val >> 4];
                        g_slot_env_indexR[w_ch, w_slot] = ((in_val & 0xF) << 2) + 2;
                        g_slot_env_incR[w_ch, w_slot] =
                        (int)ENV_RATE_D_TABLE[g_slot_env_indexR[w_ch, w_slot] + g_slot_key_scale[w_ch, w_slot]];

                        g_ch_reg_reflesh[w_ch] = true;
                        break;
                    }

                    case 0x90:
                    {
                        // ssg_eg no support
                        w_ch = ((w_addr - 0x90) & 0x03) + (w_mode * 3);
                        w_slot = (w_addr - 0x90) >> 2;
                        w_slot = SLOT_MAP[w_slot];

                        if ((in_val & 0x08) != 0)
                            g_reg_90_ssg[w_ch, w_slot] = in_val & 0x0F;
                        else
                            g_reg_90_ssg[w_ch, w_slot] = 0;

                        break;
                    }
                }

                return;
            }

            // ------------------------------------------------------------
            // 0xA0..0xB6 (fnum/block/algo/pan)
            // ------------------------------------------------------------
            if ((0xa0 <= w_addr) && (w_addr <= 0xb6) && ((w_addr & 0x03) != 3))
            {
                int w_ch = 0;
                int w_slot = 0;
                int wfnum = 0;

                switch (w_addr & 0xfc)
                {
                    case 0xa0:
                    {
                        w_ch = (w_addr - 0xa0) + (w_mode * 3);
                        wfnum = (g_slot_fnum[w_ch, 0] & 0x700) + in_val;
                        g_slot_fnum[w_ch, 0] = wfnum;
                        g_slot_keycode[w_ch, 0] = (int)(((uint)g_reg_a4_block[w_ch, 0] << 2) | KEYCODE_TABLE[g_slot_fnum[w_ch, 0] >> 7]);
                        g_ch_reg_reflesh[w_ch] = true;
                        md_main.g_md_music.g_freq_out[w_ch] = (int)((wfnum << (g_reg_a4_block[w_ch, 0] - 1)) * 0.0529819f);
                        break;
                    }

                    case 0xa4:
                    {
                        w_ch = (w_addr - 0xa4) + (w_mode * 3);
                        wfnum = (g_slot_fnum[w_ch, 0] & 0x0FF) + ((int)(in_val & 0x07) << 8);
                        g_slot_fnum[w_ch, 0] = wfnum;
                        g_reg_a4_block[w_ch, 0] = (in_val & 0x38) >> 3;
                        g_slot_keycode[w_ch, 0] = (int)(((uint)g_reg_a4_block[w_ch, 0] << 2) | KEYCODE_TABLE[g_slot_fnum[w_ch, 0] >> 7]);
                        g_ch_reg_reflesh[w_ch] = true;
                        md_main.g_md_music.g_freq_out[w_ch] = (int)((wfnum << (g_reg_a4_block[w_ch, 0] - 1)) * 0.0529819f);
                        break;
                    }

                    case 0xa8:
                    {
                        w_slot = ((w_addr - 0xa8) & 0x03) + 1;
                        g_slot_fnum[2, w_slot] = (g_slot_fnum[2, w_slot] & 0x700) + in_val;
                        g_slot_keycode[2, w_slot] = (int)(((uint)g_reg_a4_block[2, w_slot] << 2) |
                        KEYCODE_TABLE[g_slot_fnum[2, w_slot] >> 7]);
                        g_ch_reg_reflesh[w_ch] = true;
                        break;
                    }

                    case 0xac:
                    {
                        w_slot = ((w_addr - 0xac) & 0x03) + 1;
                        g_slot_fnum[2, w_slot] = (g_slot_fnum[2, w_slot] & 0x0FF) +
                        ((int)(in_val & 0x07) << 8);
                        g_reg_a4_block[2, w_slot] = (in_val & 0x38) >> 3;
                        g_slot_keycode[2, w_slot] = (int)(((uint)g_reg_a4_block[2, w_slot] << 2) |
                        KEYCODE_TABLE[g_slot_fnum[2, w_slot] >> 7]);
                        g_ch_reg_reflesh[w_ch] = true;
                        break;
                    }

                    case 0xb0:
                    {
                        w_ch = (w_addr - 0xb0) + (w_mode * 3);

                        g_reg_b0_fb[w_ch] = (byte)(9 - ((in_val >> 3) & 0x07));
                        if (g_reg_b0_algo[w_ch] != (in_val & 0x07))
                        {
                            g_reg_b0_algo[w_ch] = in_val & 0x07;
                            g_slot_CNT_MASK[w_ch, 0] = false;
                            g_slot_CNT_MASK[w_ch, 1] = false;
                            g_slot_CNT_MASK[w_ch, 2] = false;
                            g_slot_CNT_MASK[w_ch, 3] = false;
                        }
                        break;
                    }

                    case 0xb4:
                    {
                        w_ch = (w_addr - 0xb4) + (w_mode * 3);

                        g_reg_b4_l[w_ch] = (in_val & 0x80) != 0;
                        g_reg_b4_r[w_ch] = (in_val & 0x40) != 0;
                        g_reg_b4_ams[w_ch] = (int)LFO_AMS_MAP[(in_val >> 4) & 3];
                        g_reg_b4_pms[w_ch] = (int)LFO_PMS_MAP[in_val & 7];

                        if (g_reg_60_ams_enable[w_ch, 0] != 0) g_slot_ams[w_ch, 0] = g_reg_b4_ams[w_ch]; else g_slot_ams[w_ch, 0] = 31;
                        if (g_reg_60_ams_enable[w_ch, 1] != 0) g_slot_ams[w_ch, 1] = g_reg_b4_ams[w_ch]; else g_slot_ams[w_ch, 1] = 31;
                        if (g_reg_60_ams_enable[w_ch, 2] != 0) g_slot_ams[w_ch, 2] = g_reg_b4_ams[w_ch]; else g_slot_ams[w_ch, 2] = 31;
                        if (g_reg_60_ams_enable[w_ch, 3] != 0) g_slot_ams[w_ch, 3] = g_reg_b4_ams[w_ch]; else g_slot_ams[w_ch, 3] = 31;

                        break;
                    }
                }

                return;
            }
        }

        private void UpdateTimerA()
        {
            int reload = 1024 - g_reg_24_timerA;
            if (reload <= 0)
                reload = 1024;
            _timerAReload = reload;
            if (g_reg_27_load_A)
                _timerACount = _timerAReload;
        }

        private void UpdateTimerB()
        {
            int reload = (256 - g_reg_26_timerB) << 4;
            if (reload <= 0)
                reload = 256 << 4;
            _timerBReload = reload;
            if (g_reg_27_load_B)
                _timerBCount = _timerBReload;
        }

        private void MaybeLogDac()
        {
            long now = Stopwatch.GetTimestamp();
            if (_dacLastLogTicks == 0)
            {
                _dacLastLogTicks = now;
                return;
            }

            if (now - _dacLastLogTicks < Stopwatch.Frequency)
                return;

            _dacLastLogTicks = now;
            long writes = _dacWriteCount;
            long enables = _dacEnableCount;
            long disables = _dacDisableCount;
            _dacWriteCount = 0;
            _dacEnableCount = 0;
            _dacDisableCount = 0;

            Console.WriteLine(
                "[YM-DAC] writes={0} enable={1} disable={2} enabled={3} last=0x{4:X2}",
                writes, enables, disables, _dacEnabled ? 1 : 0, _dacLastValue);
        }

        private void MaybeLogYmReg(int port, byte addr, byte val)
        {
            if (!TraceYmReg || _ymRegLogRemaining <= 0)
                return;
            _ymRegLogRemaining--;
            Console.WriteLine($"[YMREG] port={port} addr=0x{addr:X2} val=0x{val:X2}");
        }

        private void UpdateYmIrq(string reason)
        {
            bool shouldAssert = ((g_com_status & 0x01) != 0 && g_reg_27_enable_A)
                || ((g_com_status & 0x02) != 0 && g_reg_27_enable_B);
            if (shouldAssert != _ymIrqAsserted)
            {
                _ymIrqAsserted = shouldAssert;
                if (TraceYmIrq)
                {
                    string state = shouldAssert ? "assert" : "clear";
                    Console.WriteLine($"[YMIRQ] {state} reason={reason} status=0x{g_com_status:X2}");
                }
            }
            md_main.g_md_z80?.irq_request(shouldAssert, "YM", g_com_status);
        }
    }
}
