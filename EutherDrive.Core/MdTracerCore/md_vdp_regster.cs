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
                    g_vdp_reg_12_7_cellmode1     = (byte)((in_data >> 7) & 0x01);
                    g_vdp_reg_12_3_shadow        = (byte)((in_data >> 3) & 0x01);
                    g_vdp_reg_12_2_interlacemode = (byte)((in_data >> 1) & 0x03);
                    byte prevInterlace = g_vdp_interlace_mode;
                    g_vdp_interlace_mode = DecodeInterlaceMode(g_vdp_reg_12_2_interlacemode);
                    ApplyInterlaceOverrides();

                    // Log reg12 writes when interlace mode might change
                    if (prevInterlace != g_vdp_interlace_mode || _frameCounter < 700)
                    {
                        Console.WriteLine($"[VDP-REG12] frame={_frameCounter} data=0x{in_data:X2} prevMode={prevInterlace} newMode={g_vdp_interlace_mode}");
                    }

                    // Track when interlace mode 2 first activates
                    if (g_vdp_interlace_mode == 2 && prevInterlace != 2)
                    {
                        _firstInterlace2Frame = (int)_frameCounter;
                        Console.WriteLine($"[INTERLACE2-ACTIVATED] frame={_frameCounter}");
                    }

                    if (prevInterlace != g_vdp_interlace_mode)
                    {
                        g_vdp_interlace_field = 0;
                        InvalidateSpriteRowCache();
                        RecomputeScrollSizes();
                        RecomputeWindowBounds();
                        UpdateOutputWidth();
                        if (g_game_screen != null && g_game_screen.Length > 0)
                            Array.Fill(g_game_screen, 0xFF000000u);

                        // When switching to interlace mode 2, copy scroll plane entries to cache
                        // IMPORTANT: Scan VRAM to find where data actually is, not just use current scroll base registers
                        // Sonic 2 changes scroll base registers after writing scroll data!
                        if (g_vdp_interlace_mode == 2 && prevInterlace != 2)
                        {
                            // Get current scroll bases from registers
                            int scrollA_base = g_vdp_reg_2_scrolla & 0xFFFE;
                            int scrollB_base = g_vdp_reg_4_scrollb & 0xFFFE;

                            Console.WriteLine($"[CACHE-COPY] Activating interlace mode 2: prev={prevInterlace} scrollA=0x{scrollA_base:X4} scrollB=0x{scrollB_base:X4}");

                            // Direct dump of suspected scroll areas
                            Console.WriteLine($"[CACHE-COPY] Direct VRAM dump:");
                            Console.WriteLine($"[CACHE-COPY] g_vram[0xA000]=0x{g_vram[0xA000]:X2}{g_vram[0xA001]:X2} g_vram[0xA002]=0x{g_vram[0xA002]:X2}{g_vram[0xA003]:X2}");
                            Console.WriteLine($"[CACHE-COPY] g_vram[0xC000]=0x{g_vram[0xC000]:X2}{g_vram[0xC001]:X2} g_vram[0xC002]=0x{g_vram[0xC002]:X2}{g_vram[0xC003]:X2}");
                            Console.WriteLine($"[CACHE-COPY] g_vram[0xE000]=0x{g_vram[0xE000]:X2}{g_vram[0xE001]:X2} g_vram[0xE002]=0x{g_vram[0xE002]:X2}{g_vram[0xE003]:X2}");

                            // Scan common VRAM regions to find where scroll data actually is
                            // Common scroll A locations: 0xC000, 0xE000, 0xA000
                            // Common scroll B locations: 0xE000, 0xA000, 0x8000
                            int scrollA_actual = FindScrollDataBase(0xC000, 0xE000, 0xA000);
                            int scrollB_actual = FindScrollDataBase(0xE000, 0xA000, 0x8000);

                            Console.WriteLine($"[CACHE-COPY] Detected scroll bases: ScrollA=0x{scrollA_actual:X4} ScrollB=0x{scrollB_actual:X4}");

                            // Copy Scroll A entries to cache at 0x6000
                            if (scrollA_actual >= 0)
                            {
                                for (int i = 0; i < 0x2000; i += 2)
                                {
                                    int src = scrollA_actual + i;
                                    if (src < 0x10000)
                                    {
                                        ushort val = (ushort)((g_vram[src] << 8) | g_vram[(src + 1) & 0xffff]);
                                        g_renderer_vram[0x6000 + (i >> 1)] = val;
                                    }
                                }
                            }

                            // Copy Scroll B entries to cache at 0x6800
                            if (scrollB_actual >= 0)
                            {
                                for (int i = 0; i < 0x1000; i += 2)
                                {
                                    int src = scrollB_actual + i;
                                    if (src < 0x10000)
                                    {
                                        ushort val = (ushort)((g_vram[src] << 8) | g_vram[(src + 1) & 0xffff]);
                                        g_renderer_vram[0x6800 + (i >> 1)] = val;
                                    }
                                }
                            }

                            Console.WriteLine($"[CACHE-COPY] After copy: cacheA[0]=0x{g_renderer_vram[0x6000]:X4} cacheB[0]=0x{g_renderer_vram[0x6800]:X4}");
                        }

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

                    g_sprite_vmask = g_vdp_interlace_mode switch
                    {
                        0 => 0x1ff,
                        2 => 0x3fe, // Interlace mode 2: sprite Y LSB is ignored (even lines only).
                        _ => 0x3ff
                    };

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
