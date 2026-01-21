using System;
using System.Diagnostics;

namespace EutherDrive.Core.MdTracerCore
{
    public partial class md_vdp
    {
        // Småhjälp för "headless" varningar
        private static void Warn(string msg) => Debug.WriteLine($"[VDP] {msg}");

        // Helper to find where scroll data is actually stored in VRAM
        // Scans through candidate bases and returns the first one with non-zero data
        private int FindScrollDataBase(params int[] candidates)
        {
            foreach (int baseAddr in candidates)
            {
                if (baseAddr < 0 || baseAddr >= 0x10000) continue;

                // Check first few entries for non-zero data
                int nonZeroCount = 0;
                ushort firstVal = 0;
                for (int i = 0; i < 32; i += 2)
                {
                    int src = baseAddr + i;
                    if (src < 0x10000)
                    {
                        ushort val = (ushort)((g_vram[src] << 8) | g_vram[(src + 1) & 0xffff]);
                        if (val != 0)
                        {
                            nonZeroCount++;
                            if (firstVal == 0) firstVal = val;
                        }
                    }
                }
                Console.WriteLine($"[CACHE-COPY] Scan 0x{baseAddr:X4}: nonZero={nonZeroCount} firstVal=0x{firstVal:X4}");
                if (nonZeroCount > 0)
                {
                    return baseAddr;
                }
            }
            // Fall back to first candidate if no data found
            Console.WriteLine($"[CACHE-COPY] No data found in any candidate, using fallback 0x{candidates[0]:X4}");
            return candidates.Length > 0 ? candidates[0] : -1;
        }

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
        
        // Latched version of register 12 (takes effect on V-Int)
        private byte _reg12_latched_7_cellmode1;
        private byte _reg12_latched_3_shadow;
        private byte _reg12_latched_2_interlacemode;
        private byte _reg12_latched_0_cellmode2;
        private bool _reg12_latch_pending;
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
            g_sprite_cache_base = -1;
            InvalidateSpriteRowCache();

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
                    // Interlace mode 2 v-counter mapping: emulate the hardware's "double resolution" pattern.
                    int vCounter = ((g_scanline & 0x7F) << 1) | ((g_scanline & 0x80) >> 7);
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
                     
                      // REMOVED: SPECIAL FIX FOR SONIC 2 SPECIAL STAGE
                      // Game should handle its own display and mode switching
                    if (MdTracerCore.MdLog.Enabled && prevDisplay != g_vdp_reg_1_6_display)
                    {
                        MdTracerCore.MdLog.WriteLine($"[VDP] reg1 display {prevDisplay} -> {g_vdp_reg_1_6_display} data=0x{in_data:X2}");
                    }
                      // Log reg1 writes during debugging
                      // if (_frameCounter >= 4900 && _frameCounter <= 4950)
                      // {
                      //     Console.WriteLine($"[VDP-REG1-DEBUG] frame={_frameCounter} prev={prevDisplay} new={g_vdp_reg_1_6_display} data=0x{in_data:X2} raw=0x{in_data:X2}");
                      // }
                     // Also log ANY reg1 write that turns display ON (bit 6 = 1)
                     if (g_vdp_reg_1_6_display == 1)
                     {
                         Console.WriteLine($"[DISPLAY-ON] frame={_frameCounter} reg1=0x{in_data:X2} (display enabled)");
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
                {
                    int oldVal = g_vdp_reg_2_scrolla;
                    g_vdp_reg_2_scrolla = (ushort)((in_data & 0x78) << 10);
                    if (oldVal != g_vdp_reg_2_scrolla && _frameCounter > 100)
                        Console.WriteLine($"[VDP-REG2] frame={_frameCounter} old=0x{oldVal:X4} new=0x{g_vdp_reg_2_scrolla:X4}");
                    break;
                }

                case 3:
                    g_vdp_reg_3_windows = (ushort)((in_data & 0x7e) << 10);
                    break;

                case 4:
                {
                    int oldVal = g_vdp_reg_4_scrollb;
                    g_vdp_reg_4_scrollb = (ushort)((in_data & 0x0f) << 13);
                    if (oldVal != g_vdp_reg_4_scrollb && _frameCounter > 100)
                        Console.WriteLine($"[VDP-REG4] frame={_frameCounter} old=0x{oldVal:X4} new=0x{g_vdp_reg_4_scrollb:X4}");
                    break;
                }

                case 5:
                {
                    int oldVal = g_vdp_reg_5_sprite;
                    g_vdp_reg_5_sprite = (ushort)(((IsH40Mode() ? (in_data & 0x7e) : (in_data & 0x7f)) << 9));
                    if (oldVal != g_vdp_reg_5_sprite)
                    {
                        g_sprite_cache_base = -1;
                        InvalidateSpriteRowCache();
                    }
                    if (TraceSatWrites && oldVal != g_vdp_reg_5_sprite)
                    {
                        Console.WriteLine($"[SAT-REG5] frame={_frameCounter} scanline={g_scanline} old=0x{oldVal:X4} new=0x{g_vdp_reg_5_sprite:X4}");
                    }
                    break;
                }

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
                       // Register 12 is latched on V-Int (takes effect at next VBlank)
                       // Critical for H40->H32 transition in some games
                      _reg12_latched_7_cellmode1     = (byte)((in_data >> 7) & 0x01);
                      _reg12_latched_3_shadow        = (byte)((in_data >> 3) & 0x01);
                      _reg12_latched_2_interlacemode = (byte)((in_data >> 1) & 0x03);
                      _reg12_latched_0_cellmode2     = (byte)(in_data & 0x01);
                      _reg12_latch_pending = true;
                      
                      // [REG12-W] on reg12 write: frame, data byte (0x??), rs1(bit7), rs0(bit0), shadow(bit3), interlace(bits1-2)
                      byte rs1 = (byte)((in_data >> 7) & 0x01);
                      byte rs0 = (byte)(in_data & 0x01);
                      byte shadow = (byte)((in_data >> 3) & 0x01);
                      byte interlace = (byte)((in_data >> 1) & 0x03);
                      Console.WriteLine($"[REG12-W] frame={_frameCounter} data=0x{in_data:X2} rs1={rs1} rs0={rs0} shadow={shadow} interlace={interlace}");
                      
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
                    if ((in_data & 0x80) != 0)
                    {
                        g_vdp_reg_23_5_dma_high = (byte)(in_data & 0x3f);
                        g_vdp_reg_23_dma_mode = (byte)((in_data & 0x40) != 0 ? 3 : 2); // copy : fill
                    }
                    else
                    {
                        g_vdp_reg_23_5_dma_high = (byte)(in_data & 0x7f);
                        g_vdp_reg_23_dma_mode = 1; // memory-to-vram
                    }
                    break;
            }
        }
    }
}
