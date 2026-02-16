using System;
using System.Diagnostics;

namespace EutherDrive.Core.MdTracerCore
{
    public partial class md_vdp
    {
        // Småhjälp för "headless" varningar
        private static void Warn(string msg)
        {
            if (string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_VDP"), "1", StringComparison.Ordinal))
                Console.WriteLine($"[VDP] {msg}");
        }
        private static readonly bool TraceDmaRegs =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_DMA_REGS"), "1", StringComparison.Ordinal);
        private static readonly bool DmaModeAltDecode =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_VDP_DMA_MODE_ALT"), "1", StringComparison.Ordinal);
        private static readonly bool TraceDisplayOn =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_DISPLAY_ON"), "1", StringComparison.Ordinal);
        private static readonly bool TraceReg12 =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_REG12"), "1", StringComparison.Ordinal);

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
        public byte g_vdp_reg_1_7_vram128;
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
        public byte g_vdp_reg_14_plane_a_rebase;
        public byte g_vdp_reg_14_plane_b_rebase;
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

        private static int DecodeScrollSize(int raw)
        {
            switch (raw & 0x03)
            {
                case 0: return 32;
                case 1: return 64;
                default: return 128;
            }
        }

        private void RecomputeScrollSizes()
        {
            g_scroll_ycell = DecodeScrollSize(g_vdp_reg_16_5_scrollV);
            int yShift = g_vdp_interlace_mode == 2 ? 4 : 3;
            g_scroll_ysize = g_scroll_ycell << yShift;
            g_scroll_ysize_mask = g_scroll_ysize - 1;

            g_scroll_xcell = DecodeScrollSize(g_vdp_reg_16_1_scrollH);
            g_scroll_xsize = g_scroll_xcell << 3;
            g_scroll_xsize_mask = g_scroll_xsize - 1;
        }

        private static byte DecodeInterlaceMode(byte raw)
        {
            byte result = raw switch
            {
                0 => 0,
                1 => 1,
                3 => 2,
                _ => 0
            };
            if (raw != result)
            {
                Console.WriteLine($"[VDP-INTERLACE-DECODE] raw={raw} => result={result}");
            }
            return result;
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
                if (md_main.g_masterSystemMode)
                {
                    int hCounterSms = 0;
                    int smsCyclesPerLine = md_main.GetSmsCyclesPerLine();
                    int lineCycles = md_main.g_md_z80?.LineCycles ?? 0;
                    if (smsCyclesPerLine > 0)
                        hCounterSms = (lineCycles * 256) / smsCyclesPerLine;
                    if (hCounterSms < 0) hCounterSms = 0;
                    if (hCounterSms > 0xFF) hCounterSms = 0xFF;

                    bool mode224 = g_display_ysize > 192;
                    bool pal = g_vertical_line_max >= 312;
                    int line = g_scanline;
                    // Approximate hardware behavior: V counter advances slightly before line end.
                    if (hCounterSms >= 230)
                        line = (line + 1) % g_vertical_line_max;
                    int vCounter;
                    if (!mode224)
                    {
                        if (!pal)
                        {
                            vCounter = (line <= 0xDA) ? line : (line - 6);
                        }
                        else
                        {
                            vCounter = (line <= 0xF2) ? line : (line - 57);
                        }
                    }
                    else
                    {
                        if (!pal)
                        {
                            vCounter = (line <= 0xEA) ? line : (line - 6);
                        }
                        else
                        {
                            if (line <= 0xFF)
                                vCounter = line;
                            else if (line <= 0x102)
                                vCounter = line - 0x100;
                            else
                                vCounter = line - 57;
                        }
                    }

                    w_out = (ushort)(((vCounter & 0xFF) << 8) | (hCounterSms & 0xFF));
                    g_vdp_c00008_hvcounter = w_out;
                    return w_out;
                }

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
                     byte prevVram128 = g_vdp_reg_1_7_vram128;
                     g_vdp_reg_1_7_vram128  = (byte)((in_data >> 7) & 0x01);
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
                     if (TraceDisplayOn && g_vdp_reg_1_6_display == 1)
                     {
                         Console.WriteLine($"[DISPLAY-ON] frame={_frameCounter} reg1=0x{in_data:X2} (display enabled)");
                     }
                     if (prevVram128 != g_vdp_reg_1_7_vram128)
                     {
                         Console.WriteLine($"[VDP-REG1] frame={_frameCounter} vram128 {prevVram128} -> {g_vdp_reg_1_7_vram128} (bit7)");
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
                    g_vdp_reg_2_scrolla = (ushort)((in_data & 0x38) << 10);
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
                    g_vdp_reg_4_scrollb = (ushort)((in_data & 0x07) << 13);
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
                       bool newH40 = rs1 != 0 || rs0 != 0;
                       if (TraceReg12)
                           Console.WriteLine($"[REG12-W] frame={_frameCounter} data=0x{in_data:X2} rs1={rs1} rs0={rs0} shadow={shadow} interlace={interlace} H40={newH40} PENDING");
                       
                       break;

                case 13:
                    g_vdp_reg_13_hscroll = (ushort)((in_data & 0x3f) << 10);
                    break;

                case 14:
                {
                    byte prevA = g_vdp_reg_14_plane_a_rebase;
                    byte prevB = g_vdp_reg_14_plane_b_rebase;
                    g_vdp_reg_14_plane_a_rebase = (byte)((in_data & 0x01) != 0 ? 1 : 0);
                    g_vdp_reg_14_plane_b_rebase = (byte)(((in_data & 0x10) != 0 && g_vdp_reg_14_plane_a_rebase != 0) ? 1 : 0);
                    if (prevA != g_vdp_reg_14_plane_a_rebase || prevB != g_vdp_reg_14_plane_b_rebase)
                    {
                        Console.WriteLine($"[VDP-REG14] frame={_frameCounter} data=0x{in_data:X2} rebaseA={g_vdp_reg_14_plane_a_rebase} rebaseB={g_vdp_reg_14_plane_b_rebase}");
                    }
                    break;
                }

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
                    if (TraceDmaRegs)
                        Console.WriteLine($"[VDP-REG-DMA-LEN] frame={_frameCounter} reg19=0x{in_data:X2} (DMA counter low)");
                    if (TraceDmaRegs)
                        Console.WriteLine($"[VDP-DMA-REG] frame={_frameCounter} pc=0x{md_m68k.g_reg_PC:X6} A0=0x{md_m68k.g_reg_addr[0].l:X8} D0=0x{md_m68k.g_reg_data[0].l:X8} D1=0x{md_m68k.g_reg_data[1].l:X8} r19=0x{g_vdp_reg_19_dma_counter_low:X2} r20=0x{g_vdp_reg_20_dma_counter_high:X2} r21=0x{g_vdp_reg_21_dma_source_low:X2} r22=0x{g_vdp_reg_22_dma_source_mid:X2} r23=0x{g_vdp_reg_23_5_dma_high:X2}");
                    break;

                case 20:
                    g_vdp_reg_20_dma_counter_high = in_data;
                    if (TraceDmaRegs)
                        Console.WriteLine($"[VDP-REG-DMA-LEN] frame={_frameCounter} reg20=0x{in_data:X2} (DMA counter high)");
                    if (TraceDmaRegs)
                        Console.WriteLine($"[VDP-DMA-REG] frame={_frameCounter} pc=0x{md_m68k.g_reg_PC:X6} A0=0x{md_m68k.g_reg_addr[0].l:X8} D0=0x{md_m68k.g_reg_data[0].l:X8} D1=0x{md_m68k.g_reg_data[1].l:X8} r19=0x{g_vdp_reg_19_dma_counter_low:X2} r20=0x{g_vdp_reg_20_dma_counter_high:X2} r21=0x{g_vdp_reg_21_dma_source_low:X2} r22=0x{g_vdp_reg_22_dma_source_mid:X2} r23=0x{g_vdp_reg_23_5_dma_high:X2}");
                    break;

                case 21:
                    g_vdp_reg_21_dma_source_low = in_data;
                    if (TraceDmaRegs)
                        Console.WriteLine($"[VDP-REG-DMA-SRC] frame={_frameCounter} reg21=0x{in_data:X2} (DMA source low) PC=0x{md_m68k.g_reg_PC:X6}");
                    if (TraceDmaRegs)
                        Console.WriteLine($"[VDP-DMA-REG] frame={_frameCounter} pc=0x{md_m68k.g_reg_PC:X6} A0=0x{md_m68k.g_reg_addr[0].l:X8} D0=0x{md_m68k.g_reg_data[0].l:X8} D1=0x{md_m68k.g_reg_data[1].l:X8} r19=0x{g_vdp_reg_19_dma_counter_low:X2} r20=0x{g_vdp_reg_20_dma_counter_high:X2} r21=0x{g_vdp_reg_21_dma_source_low:X2} r22=0x{g_vdp_reg_22_dma_source_mid:X2} r23=0x{g_vdp_reg_23_5_dma_high:X2}");
                    break;

                case 22:
                    g_vdp_reg_22_dma_source_mid = in_data;
                    if (TraceDmaRegs)
                        Console.WriteLine($"[VDP-REG-DMA-SRC] frame={_frameCounter} reg22=0x{in_data:X2} (DMA source mid) PC=0x{md_m68k.g_reg_PC:X6}");
                    if (TraceDmaRegs)
                        Console.WriteLine($"[VDP-DMA-REG] frame={_frameCounter} pc=0x{md_m68k.g_reg_PC:X6} A0=0x{md_m68k.g_reg_addr[0].l:X8} D0=0x{md_m68k.g_reg_data[0].l:X8} D1=0x{md_m68k.g_reg_data[1].l:X8} r19=0x{g_vdp_reg_19_dma_counter_low:X2} r20=0x{g_vdp_reg_20_dma_counter_high:X2} r21=0x{g_vdp_reg_21_dma_source_low:X2} r22=0x{g_vdp_reg_22_dma_source_mid:X2} r23=0x{g_vdp_reg_23_5_dma_high:X2}");
                    break;

                case 23:
                    if (DmaModeAltDecode)
                    {
                        g_vdp_reg_23_5_dma_high = (byte)(in_data & 0x7f);
                        switch ((in_data >> 6) & 0x03)
                        {
                            case 0x00:
                                g_vdp_reg_23_dma_mode = 1; // memory-to-vram
                                break;
                            case 0x01:
                                g_vdp_reg_23_dma_mode = 2; // fill
                                break;
                            case 0x02:
                                g_vdp_reg_23_dma_mode = 3; // copy
                                break;
                            default:
                                g_vdp_reg_23_dma_mode = 1;
                                break;
                        }
                        if (TraceDmaRegs)
                            Console.WriteLine($"[VDP-REG-DMA-SRC] frame={_frameCounter} reg23=0x{in_data:X2} mode={g_vdp_reg_23_dma_mode} alt=1 (DMA source high, mode set)");
                    }
                    else if ((in_data & 0x80) != 0)
                    {
                        g_vdp_reg_23_5_dma_high = (byte)(in_data & 0x3f);
                        g_vdp_reg_23_dma_mode = (byte)((in_data & 0x40) != 0 ? 3 : 2); // copy : fill
                        if (TraceDmaRegs)
                            Console.WriteLine($"[VDP-REG-DMA-SRC] frame={_frameCounter} reg23=0x{in_data:X2} mode={g_vdp_reg_23_dma_mode} (DMA source high, mode set)");
                    }
                    else
                    {
                        g_vdp_reg_23_5_dma_high = (byte)(in_data & 0x7f);
                        g_vdp_reg_23_dma_mode = 1; // memory-to-vram
                        if (TraceDmaRegs)
                            Console.WriteLine($"[VDP-REG-DMA-SRC] frame={_frameCounter} reg23=0x{in_data:X2} mode=1 (DMA source high, memory-to-vram)");
                    }
                    if (TraceDmaRegs)
                        Console.WriteLine($"[VDP-DMA-REG] frame={_frameCounter} pc=0x{md_m68k.g_reg_PC:X6} A0=0x{md_m68k.g_reg_addr[0].l:X8} D0=0x{md_m68k.g_reg_data[0].l:X8} D1=0x{md_m68k.g_reg_data[1].l:X8} r19=0x{g_vdp_reg_19_dma_counter_low:X2} r20=0x{g_vdp_reg_20_dma_counter_high:X2} r21=0x{g_vdp_reg_21_dma_source_low:X2} r22=0x{g_vdp_reg_22_dma_source_mid:X2} r23=0x{g_vdp_reg_23_5_dma_high:X2}");
                    break;
            }
        }
    }
}
