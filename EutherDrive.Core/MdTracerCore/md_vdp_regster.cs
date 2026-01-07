using System;
using System.Diagnostics;

namespace EutherDrive.Core.MdTracerCore
{
    internal partial class md_vdp
    {
        // Småhjälp för “headless” varningar
        private static void Warn(string msg) => Debug.WriteLine($"[VDP] {msg}");

        //VDP status register
        public byte g_vdp_status_9_empl;
        public byte g_vdp_status_8_full;
        public byte g_vdp_status_7_vinterrupt;
        public byte g_vdp_status_6_sprite;
        public byte g_vdp_status_5_collision;
        public byte g_vdp_status_4_frame;
        public byte g_vdp_status_3_vbrank;
        public byte g_vdp_status_2_hbrank;
        public byte g_vdp_status_1_dma;
        public byte g_vdp_status_0_tvmode;

        //HV Counter
        public ushort g_vdp_c00008_hvcounter;
        public bool g_vdp_c00008_hvcounter_latched;

        //VDP register
        private byte[] g_vdp_reg = Array.Empty<byte>();
        public byte g_vdp_reg_0_4_hinterrupt;
        public byte g_vdp_reg_0_1_hvcounter;
        public byte g_vdp_reg_1_6_display;
        public byte g_vdp_reg_1_5_vinterrupt;
        public byte g_vdp_reg_1_4_dma;
        public byte g_vdp_reg_1_3_cellmode;
        public int  g_vdp_reg_2_scrolla;
        public int  g_vdp_reg_3_windows;
        public int  g_vdp_reg_4_scrollb;
        public int  g_vdp_reg_5_sprite;
        public byte g_vdp_reg_7_backcolor;
        public byte g_vdp_reg_10_hint;
        public byte g_vdp_reg_11_3_ext;
        public byte g_vdp_reg_11_2_vscroll;
        public byte g_vdp_reg_11_1_hscroll;
        public byte g_vdp_reg_12_7_cellmode1;
        public byte g_vdp_reg_12_3_shadow;
        public byte g_vdp_reg_12_2_interlacemode;
        public byte g_vdp_interlace_mode;
        public byte g_vdp_reg_12_0_cellmode2;
        public int  g_vdp_reg_13_hscroll;
        public byte g_vdp_reg_15_autoinc;
        public int  g_vdp_reg_16_5_scrollV;
        public int  g_vdp_reg_16_1_scrollH;
        public byte g_vdp_reg_17_7_windows;
        public byte g_vdp_reg_17_4_basspointer;
        public byte g_vdp_reg_18_7_windows;
        public byte g_vdp_reg_18_4_basspointer;
        public byte g_vdp_reg_19_dma_counter_low;
        public byte g_vdp_reg_20_dma_counter_high;
        public byte g_vdp_reg_21_dma_source_low;
        public byte g_vdp_reg_22_dma_source_mid;
        public byte g_vdp_reg_23_dma_mode;
        public byte g_vdp_reg_23_5_dma_high;

        private bool g_hmodeLogged;

        private void RecomputeWindowBounds()
        {
            byte reg17 = g_vdp_reg[17];
            int w_pos = (reg17 & 0x1f) << 4;
            int windowScale = g_vdp_interlace_mode == 2 ? 2 : 1;
            int displayY = g_display_ysize * windowScale;
            if ((reg17 & 0x80) == 0)
            {
                if (w_pos < g_display_xsize)
                {
                    g_screenA_left_x  = w_pos;
                    g_screenA_right_x = g_display_xsize - 1;
                }
                else
                {
                    g_screenA_left_x = 0;
                    g_screenA_right_x = 0;
                }
            }
            else
            {
                if (w_pos == 0)
                {
                    g_screenA_left_x = 0;
                    g_screenA_right_x = 0;
                }
                else if (w_pos < g_display_xsize)
                {
                    g_screenA_left_x  = 0;
                    g_screenA_right_x = w_pos - 1;
                }
                else
                {
                    g_screenA_left_x  = 0;
                    g_screenA_right_x = g_display_xsize - 1;
                }
            }

            byte reg18 = g_vdp_reg[18];
            int w_pos_y = (reg18 & 0x1f) << 3;
            if (windowScale != 1)
                w_pos_y <<= 1;
            if ((reg18 & 0x80) == 0)
            {
                if (w_pos_y < displayY)
                {
                    g_screenA_top_y    = w_pos_y;
                    g_screenA_bottom_y = displayY - 1;
                }
                else
                {
                    g_screenA_top_y = 0;
                    g_screenA_bottom_y = 0;
                }
            }
            else
            {
                if (w_pos_y == 0)
                {
                    g_screenA_top_y = 0;
                    g_screenA_bottom_y = 0;
                }
                else if (w_pos_y < displayY)
                {
                    g_screenA_top_y    = 0;
                    g_screenA_bottom_y = w_pos_y - 1;
                }
                else
                {
                    g_screenA_top_y    = 0;
                    g_screenA_bottom_y = displayY - 1;
                }
            }
        }

        private bool IsH40Mode() => g_vdp_reg_12_0_cellmode2 != 0 || g_vdp_reg_12_7_cellmode1 != 0;

        private void ApplyHorizontalMode(bool h40Mode)
        {
            int prevDisplayX = g_display_xsize;

            if (!h40Mode)
            {
                g_display_xsize = 256;
                g_display_xcell = 32;
                g_max_sprite_num  = 64;
                g_max_sprite_line = 16;
                g_max_sprite_cell = 32;
            }
            else
            {
                g_display_xsize = 320;
                g_display_xcell = 40;
                g_max_sprite_num  = 80;
                g_max_sprite_line = 20;
                g_max_sprite_cell = 40;
            }

            UpdateOutputWidth();
            RecomputeWindowBounds();

            g_vdp_reg_3_windows = (ushort)((g_vdp_reg[3] & 0x7e) << 10);
            g_vdp_reg_5_sprite  = (ushort)(((h40Mode ? (g_vdp_reg[5] & 0x7e) : (g_vdp_reg[5] & 0x7f)) << 9));

            if (MdTracerCore.MdLog.Enabled && (!g_hmodeLogged || g_display_xsize != prevDisplayX))
            {
                string mode = h40Mode ? "H40" : "H32";
                MdTracerCore.MdLog.WriteLine($"[VDP] HMODE={mode} width={g_display_xsize}");
                g_hmodeLogged = true;
            }
        }

        private void RecomputeScrollSizes()
        {
            g_scroll_ycell = 32 * (g_vdp_reg_16_5_scrollV + 1);
            int yShift = g_vdp_interlace_mode == 2 ? 4 : 3;
            g_scroll_ysize = g_scroll_ycell << yShift;
            g_scroll_ysize_mask = g_scroll_ysize - 1;

            g_scroll_xcell = 32 * (g_vdp_reg_16_1_scrollH + 1);
            g_scroll_xsize = g_scroll_xcell << 3;
            g_scroll_xsize_mask = g_scroll_xsize - 1;
        }

        private static byte DecodeInterlaceMode(byte raw)
        {
            return raw switch
            {
                0 => 0,
                1 => 1,
                3 => 2,
                _ => 0
            };
        }

        private ushort build_vdp_status_word()
        {
            ushort w_out = 0;
            w_out = g_vdp_status_9_empl;
            w_out = (ushort)((w_out << 1) | g_vdp_status_8_full);
            w_out = (ushort)((w_out << 1) | g_vdp_status_7_vinterrupt);
            w_out = (ushort)((w_out << 1) | g_vdp_status_6_sprite);
            w_out = (ushort)((w_out << 1) | g_vdp_status_5_collision);
            w_out = (ushort)((w_out << 1) | g_vdp_status_4_frame);
            w_out = (ushort)((w_out << 1) | g_vdp_status_3_vbrank);
            w_out = (ushort)((w_out << 1) | g_vdp_status_2_hbrank);
            w_out = (ushort)((w_out << 1) | g_vdp_status_1_dma);
            w_out = (ushort)((w_out << 1) | g_vdp_status_0_tvmode);
            return w_out;
        }

        private ushort get_vdp_status() => build_vdp_status_word();

        internal ushort PeekVdpStatus() => build_vdp_status_word();

        internal ushort ReadStatusWord() => get_vdp_status();

        private ushort get_vdp_hvcounter()
        {
            ushort w_out = g_vdp_c00008_hvcounter;
            if (!g_vdp_c00008_hvcounter_latched)
            {
                int hCounter = (g_display_xsize
                    * (md_m68k.g_clock_total - md_m68k.g_clock_now)
                    / md_main.VDL_LINE_RENDER_MC68_CLOCK) & 0xff;
                if (g_vdp_interlace_mode == 0)
                {
                    w_out = (ushort)((GetInterlaceLine(g_scanline) << 8) + hCounter);
                }
                else if (g_vdp_interlace_mode == 2)
                {
                    int vCounter = (g_scanline & 0xFE) | g_vdp_interlace_field;
                    w_out = (ushort)((vCounter << 8) | hCounter);
                }
                else
                {
                    w_out = (ushort)
                    (((GetInterlaceLine(g_scanline) << 7) & 0xff00)
                    + hCounter);
                }
                g_vdp_c00008_hvcounter = w_out;
            }
            return w_out;
        }

        private void set_vdp_register(uint in_num, byte in_data)
        {
            g_vdp_reg[in_num] = in_data;
            switch (in_num)
            {
                case 0:
                    g_vdp_reg_0_4_hinterrupt = (byte)((in_data >> 4) & 0x01);
                    g_vdp_reg_0_1_hvcounter  = (byte)((in_data >> 1) & 0x01);
                    break;

                case 1:
                    byte prevDisplay = g_vdp_reg_1_6_display;
                    g_vdp_reg_1_6_display  = (byte)((in_data >> 6) & 0x01);
                    g_vdp_reg_1_5_vinterrupt = (byte)((in_data >> 5) & 0x01);
                    g_vdp_reg_1_4_dma      = (byte)((in_data >> 4) & 0x01);
                    g_vdp_reg_1_3_cellmode = (byte)((in_data >> 3) & 0x01);
                    if (MdTracerCore.MdLog.Enabled && prevDisplay != g_vdp_reg_1_6_display)
                    {
                        MdTracerCore.MdLog.WriteLine($"[VDP] reg1 display {prevDisplay} -> {g_vdp_reg_1_6_display} data=0x{in_data:X2}");
                    }
                    if (MdTracerCore.MdLog.Enabled && prevDisplay == 0 && g_vdp_reg_1_6_display == 1)
                    {
                        MdTracerCore.MdLog.WriteLine("[VDP] display enabled (reg1 bit6)");
                    }
                    if (g_vdp_reg_1_3_cellmode == 0)
                    {
                        g_display_ysize    = 224;
                        g_display_ycell    = 28;
                        g_vertical_line_max = 262;
                    }
                    else
                    {
                        g_display_ysize    = 240;
                        g_display_ycell    = 30;
                        g_vertical_line_max = 312;
                    }
                    RecomputeWindowBounds();
                    UpdateOutputWidth();
                    break;

                case 2:
                    g_vdp_reg_2_scrolla = (ushort)((in_data & 0x78) << 10);
                    break;

                case 3:
                    g_vdp_reg_3_windows = (ushort)((in_data & 0x7e) << 10);
                    break;

                case 4:
                    g_vdp_reg_4_scrollb = (ushort)((in_data & 0x0f) << 13);
                    break;

                case 5:
                    g_vdp_reg_5_sprite = (ushort)(((IsH40Mode() ? (in_data & 0x7e) : (in_data & 0x7f)) << 9));
                    break;

                case 7:
                    g_vdp_reg_7_backcolor = (byte)(in_data & 0x3f);
                    break;

                case 10:
                    g_vdp_reg_10_hint = in_data;
                    break;

                case 11:
                    g_vdp_reg_11_3_ext     = (byte)((in_data >> 3) & 0x01);
                    g_vdp_reg_11_2_vscroll = (byte)((in_data >> 2) & 0x01);
                    g_vdp_reg_11_1_hscroll = (byte)(in_data & 0x03);
                    break;

                case 12:
                    g_vdp_reg_12_7_cellmode1     = (byte)((in_data >> 7) & 0x01);
                    g_vdp_reg_12_3_shadow        = (byte)((in_data >> 3) & 0x01);
                    g_vdp_reg_12_2_interlacemode = (byte)((in_data >> 1) & 0x03);
                    byte prevInterlace = g_vdp_interlace_mode;
                    g_vdp_interlace_mode = DecodeInterlaceMode(g_vdp_reg_12_2_interlacemode);

                    if (prevInterlace != g_vdp_interlace_mode)
                    {
                        g_vdp_interlace_field = 0;
                        RecomputeScrollSizes();
                        RecomputeWindowBounds();
                        UpdateOutputWidth();
                        if (g_game_screen != null && g_game_screen.Length > 0)
                            Array.Fill(g_game_screen, 0xFF000000u);

                        if (MdTracerCore.MdLog.Enabled || TraceVdpInterlace)
                        {
                            string modeLabel = g_vdp_interlace_mode switch { 0 => "none", 1 => "interlace", _ => "mode2" };
                            string fieldLabel = g_vdp_interlace_field == 0 ? "even" : "odd";
                            if (TraceVdpInterlace)
                                Console.WriteLine($"[VDP] interlace={modeLabel} field={fieldLabel} reg=0x{g_vdp_reg_12_2_interlacemode:X2}");
                            else
                                MdTracerCore.MdLog.WriteLine($"[VDP] interlace={modeLabel} field={fieldLabel} reg=0x{g_vdp_reg_12_2_interlacemode:X2}");
                        }
                    }

                    if (g_vdp_interlace_mode == 1)
                        Warn("Interlace mode 1 not fully implemented.");

                    g_sprite_vmask = (g_vdp_interlace_mode == 0) ? 0x1ff : 0x3ff;

                    g_vdp_reg_12_0_cellmode2 = (byte)(in_data & 0x01);
                    ApplyHorizontalMode(IsH40Mode());
                    break;

                case 13:
                    g_vdp_reg_13_hscroll = (ushort)((in_data & 0x7f) << 10);
                    break;

                case 15:
                    g_vdp_reg_15_autoinc = in_data;
                    break;

                case 16:
                    g_vdp_reg_16_5_scrollV = (in_data >> 4) & 0x03;
                    g_vdp_reg_16_1_scrollH = in_data & 0x03;
                    RecomputeScrollSizes();
                    break;

                case 17:
                {
                    RecomputeWindowBounds();
                    break;
                }

                case 18:
                {
                    RecomputeWindowBounds();
                    break;
                }

                case 19:
                    g_vdp_reg_19_dma_counter_low = in_data;
                    break;

                case 20:
                    g_vdp_reg_20_dma_counter_high = in_data;
                    break;

                case 21:
                    g_vdp_reg_21_dma_source_low = in_data;
                    break;

                case 22:
                    g_vdp_reg_22_dma_source_mid = in_data;
                    break;

                case 23:
                    g_vdp_reg_23_dma_mode = (byte)((in_data >> 6) & 0x03);
                    g_vdp_reg_23_5_dma_high = (byte)((in_data & 0x80) == 0 ? (in_data & 0x7f) : (in_data & 0x3f));
                    break;
            }
        }
    }
}
