using System;
using System.Diagnostics;
using System.Globalization;

namespace EutherDrive.Core.MdTracerCore
{
    public partial class md_vdp
    {
        private static readonly bool TraceScrollRegs =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_SCROLL_REGS"), "1", StringComparison.Ordinal);
        private static readonly int TraceScrollRegsLimit =
            ParseTraceIntLocalReg("EUTHERDRIVE_TRACE_SCROLL_REGS_LIMIT", 128);
        private int _traceScrollRegsRemaining = TraceScrollRegsLimit;

        private static int ParseTraceIntLocalReg(string name, int fallback)
        {
            string? raw = Environment.GetEnvironmentVariable(name);
            if (string.IsNullOrWhiteSpace(raw))
                return fallback;
            if (!int.TryParse(raw.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int value))
                return fallback;
            return value < 0 ? fallback : value;
        }

        public byte[] GetRegisterSnapshot()
        {
            if (g_vdp_reg == null || g_vdp_reg.Length == 0)
                return Array.Empty<byte>();
            byte[] copy = new byte[g_vdp_reg.Length];
            Buffer.BlockCopy(g_vdp_reg, 0, copy, 0, copy.Length);
            return copy;
        }
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

            // Reg #3 uses bits 5..1 for window base.
            g_vdp_reg_3_windows = (ushort)((g_vdp_reg[3] & 0x3e) << 10);
            // Reg #5 stores full SAT base bits 15..9; H32/H40 masking is applied on use.
            g_vdp_reg_5_sprite  = (ushort)((g_vdp_reg[5] & 0x7f) << 9);
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
            // Align DMA active with actual DMA state (jgenesis-style control_port.dma_active).
            // DMA active should reflect an active DMA transfer, not just a nonzero length register.
            byte dmaActive = (byte)(g_dma_mode != 0 ? 1 : 0);
            g_vdp_status_1_dma = dmaActive;

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

        private const int MclkCyclesPerScanline = 3420;
        private const int M68kToMclkDivider = 7;
        private const int AccessSlotsTableSize = 256;

        private static readonly bool[] H32AccessSlots = BuildSlotTable(new[]
        {
            5, 13, 21, 37, 45, 53, 69, 77, 85, 101, 109, 117, 132, 133, 147, 161
        });

        private static readonly bool[] H40AccessSlots = BuildSlotTable(new[]
        {
            6, 14, 22, 38, 46, 54, 70, 78, 86, 102, 110, 118, 134, 142, 150, 165, 166, 190
        });

        private static readonly bool[] H32BlankRefreshSlots = BuildSlotTable(new[]
        {
            1, 33, 65, 97, 129
        });

        private static readonly bool[] H40BlankRefreshSlots = BuildSlotTable(new[]
        {
            26, 58, 90, 122, 154, 204
        });

        private ushort ReadStatusWordTimed(ushort m68kOpcode)
        {
            var timing = GetTimedHv(m68kOpcode);
            bool hblankFlag = !IsHblankFlagClearRange(timing.InternalH, timing.IsH40);
            bool displayEnabled = g_vdp_reg_1_6_display != 0;
            bool passedVint = timing.PassedVint;
            bool vintFlag = md_m68k.g_interrupt_V_req || passedVint;

            ushort w_out = 0;
            // DMA active should reflect an active DMA transfer, not just a nonzero length register.
            byte dmaActive = (byte)(g_dma_mode != 0 ? 1 : 0);
            g_vdp_status_1_dma = dmaActive;

            w_out = g_vdp_status_9_empl;
            w_out = (ushort)((w_out << 1) | g_vdp_status_8_full);
            w_out = (ushort)((w_out << 1) | (vintFlag ? 1 : 0));
            w_out = (ushort)((w_out << 1) | g_vdp_status_6_sprite);
            w_out = (ushort)((w_out << 1) | g_vdp_status_5_collision);
            w_out = (ushort)((w_out << 1) | g_vdp_status_4_frame);
            // Keep VBlank status stable across the whole blanking interval from VDP line state.
            // Timing-derived flag can transiently disagree around IRQ edges and stall polling loops.
            bool vblankFlag = g_vdp_status_3_vbrank != 0 || timing.VBlankFlag;
            w_out = (ushort)((w_out << 1) | ((vblankFlag || !displayEnabled) ? 1 : 0));
            w_out = (ushort)((w_out << 1) | (hblankFlag ? 1 : 0));
            w_out = (ushort)((w_out << 1) | g_vdp_status_1_dma);
            w_out = (ushort)((w_out << 1) | g_vdp_status_0_tvmode);
            return w_out;
        }

        internal ushort PeekVdpStatus() => build_vdp_status_word();

        internal ushort ReadStatusWord(ushort m68kOpcode) => ReadStatusWordTimed(m68kOpcode);

        private ushort get_vdp_hvcounter()
        {
            return get_vdp_hvcounter(md_m68k.g_opcode);
        }

        private ushort get_vdp_hvcounter(ushort m68kOpcode)
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
                    int vCounterSms;
                    if (!mode224)
                    {
                        if (!pal)
                        {
                            vCounterSms = (line <= 0xDA) ? line : (line - 6);
                        }
                        else
                        {
                            vCounterSms = (line <= 0xF2) ? line : (line - 57);
                        }
                    }
                    else
                    {
                        if (!pal)
                        {
                            vCounterSms = (line <= 0xEA) ? line : (line - 6);
                        }
                        else
                        {
                            if (line <= 0xFF)
                                vCounterSms = line;
                            else if (line <= 0x102)
                                vCounterSms = line - 0x100;
                            else
                                vCounterSms = line - 57;
                        }
                    }

                    w_out = (ushort)(((vCounterSms & 0xFF) << 8) | (hCounterSms & 0xFF));
                    g_vdp_c00008_hvcounter = w_out;
                    return w_out;
                }

                var timing = GetTimedHv(m68kOpcode);
                byte hCounter = (byte)((timing.HvCounter) & 0xFF);
                byte vCounter = (byte)((timing.HvCounter >> 8) & 0xFF);
                w_out = (ushort)((vCounter << 8) | hCounter);
                g_vdp_c00008_hvcounter = w_out;
            }
            return w_out;
        }

        private static bool[] BuildSlotTable(int[] indices)
        {
            var table = new bool[AccessSlotsTableSize];
            foreach (int idx in indices)
            {
                if (idx >= 0 && idx < table.Length)
                    table[idx] = true;
            }
            return table;
        }

        private bool ApplyDataPortAccessSlotDelay(ushort opcode)
        {
            if (md_main.g_masterSystemMode)
                return true;

            bool isH40 = IsH40Mode();
            long scanlineMclkRaw = GetScanlineMclkRaw();
            if (scanlineMclkRaw >= MclkCyclesPerScanline)
                scanlineMclkRaw %= MclkCyclesPerScanline;

            ushort pixel = isH40
                ? ScanlineMclkToPixelH40(scanlineMclkRaw)
                : ScanlineMclkToPixelH32(scanlineMclkRaw);
            int slotIdx = (pixel >> 1) & 0xFF;

            bool blank = _vblankActive || g_vdp_reg_1_6_display == 0;
            bool allowed = IsSlotAllowed(slotIdx, blank, isH40);
            if (allowed)
                return true;

            int nextSlot = FindNextAllowedSlot(slotIdx, blank, isH40);
            if (nextSlot < 0)
                return true;

            int deltaSlots = nextSlot >= slotIdx
                ? (nextSlot - slotIdx)
                : (AccessSlotsTableSize - slotIdx + nextSlot);
            if (deltaSlots == 0)
                return true;

            int mclkPerPixel = isH40 ? 8 : 10;
            long waitMclk = deltaSlots * 2L * mclkPerPixel;
            int waitCycles = (int)Math.Ceiling(waitMclk / (double)M68kToMclkDivider);
            if (waitCycles <= 0)
                waitCycles = 1;

            md_main.AddM68kWaitCycles(waitCycles);
            return false;
        }

        private static bool IsSlotAllowed(int slotIdx, bool blank, bool isH40)
        {
            var accessSlots = isH40 ? H40AccessSlots : H32AccessSlots;
            var blankRefresh = isH40 ? H40BlankRefreshSlots : H32BlankRefreshSlots;
            if (slotIdx < 0 || slotIdx >= accessSlots.Length)
                return true;
            if (blank)
                return !blankRefresh[slotIdx];
            return accessSlots[slotIdx];
        }

        private static int FindNextAllowedSlot(int startIdx, bool blank, bool isH40)
        {
            for (int i = 1; i < AccessSlotsTableSize; i++)
            {
                int idx = (startIdx + i) & 0xFF;
                if (IsSlotAllowed(idx, blank, isH40))
                    return idx;
            }
            return -1;
        }

        private struct TimedHv
        {
            public ushort HvCounter;
            public ushort InternalH;
            public byte VCounter;
            public bool VBlankFlag;
            public bool PassedVint;
            public bool IsH40;
        }

        private TimedHv GetTimedHv(ushort m68kOpcode)
        {
            bool isH40 = IsH40Mode();
            long scanlineMclkRaw = GetScanlineMclkRaw();
            long readAdjustment = StatusReadMclkAdjustment(m68kOpcode);
            long scanlineMclkAdj = scanlineMclkRaw + readAdjustment;
            if (scanlineMclkRaw >= MclkCyclesPerScanline)
                scanlineMclkRaw %= MclkCyclesPerScanline;
            if (scanlineMclkAdj >= MclkCyclesPerScanline)
                scanlineMclkAdj %= MclkCyclesPerScanline;

            ComputeHv(scanlineMclkAdj, isH40, out ushort hvCounter, out ushort internalH, out byte vCounter, out bool vblankFlag);

            bool passedVint = PassedVint(scanlineMclkRaw, scanlineMclkAdj, isH40, vCounter);

            return new TimedHv
            {
                HvCounter = hvCounter,
                InternalH = internalH,
                VCounter = vCounter,
                VBlankFlag = vblankFlag,
                PassedVint = passedVint,
                IsH40 = isH40
            };
        }

        private long GetScanlineMclkRaw()
        {
            int sliceLen = md_m68k.g_slice_clock_len;
            if (sliceLen <= 0)
                sliceLen = md_main.VDL_LINE_RENDER_MC68_CLOCK;
            int cyclesIntoSlice = md_m68k.g_clock_now - md_m68k.g_slice_start_clock_total;
            if (cyclesIntoSlice < 0) cyclesIntoSlice = 0;
            if (cyclesIntoSlice > sliceLen) cyclesIntoSlice = sliceLen;
            return (long)cyclesIntoSlice * M68kToMclkDivider;
        }

        private static long StatusReadMclkAdjustment(ushort opcode)
        {
            int? moveCycles = md_m68k.TryEstimateMoveCycles();
            if (moveCycles.HasValue)
            {
                int cycles = Math.Max(4, moveCycles.Value);
                return (long)(cycles - 4) * M68kToMclkDivider;
            }

            var info = md_m68k.g_opcode_info != null ? md_m68k.g_opcode_info[opcode] : null;
            if (info?.opname_org != null)
            {
                if (info.opname_org.StartsWith("BTST", StringComparison.OrdinalIgnoreCase) ||
                    info.opname_org.StartsWith("CMP", StringComparison.OrdinalIgnoreCase))
                {
                    return 8L * M68kToMclkDivider;
                }
            }

            return 8L * M68kToMclkDivider;
        }

        private void ComputeHv(long scanlineMclk, bool isH40, out ushort hvCounter, out ushort internalH, out byte vCounter, out bool vblankFlag)
        {
            int scanline = g_scanline;
            long hInterruptMclk = isH40 ? (0x14A * 8L) : (0x10A * 10L);
            bool inHblank = scanlineMclk >= hInterruptMclk;
            int scanlinesPerFrame = GetScanlinesPerFrame();
            int scanlineForCounter = inHblank
                ? (scanline == scanlinesPerFrame - 1 ? 0 : scanline + 1)
                : scanline;

            vCounter = ComputeVCounter(scanlineForCounter, out vblankFlag);

            ushort pixel = isH40
                ? ScanlineMclkToPixelH40(scanlineMclk)
                : ScanlineMclkToPixelH32(scanlineMclk);
            internalH = isH40 ? PixelToInternalHH40(pixel) : PixelToInternalHH32(pixel);

            byte hCounter = (byte)((internalH >> 1) & 0xFF);
            hvCounter = (ushort)((vCounter << 8) | hCounter);
        }

        private bool PassedVint(long scanlineMclkRaw, long scanlineMclkAdj, bool isH40, byte vCounter)
        {
            int activeScanlines = GetActiveScanlines();
            if (vCounter != (byte)activeScanlines)
                return false;
            long vintMclk = isH40 ? (0x002 * 8L) : (0x001 * 10L);
            return scanlineMclkAdj >= vintMclk && (scanlineMclkRaw < vintMclk || scanlineMclkRaw > scanlineMclkAdj);
        }

        private int GetActiveScanlines()
        {
            bool pal = IsPalTiming();
            if (!pal)
                return 224;
            return g_display_ysize > 0 ? g_display_ysize : 224;
        }

        private int GetScanlinesPerFrame()
        {
            bool pal = IsPalTiming();
            bool interlaced = g_vdp_interlace_mode != 0;
            bool interlacedOdd = g_vdp_interlace_field != 0;
            if (!pal)
                return 262 + (interlacedOdd ? 1 : 0);
            return 312 + (!interlaced || interlacedOdd ? 1 : 0);
        }

        private byte ComputeVCounter(int scanline, out bool vblankFlag)
        {
            bool pal = IsPalTiming();
            bool interlaced = g_vdp_interlace_mode != 0;
            bool interlacedDouble = g_vdp_interlace_mode == 2;
            int activeScanlines = GetActiveScanlines();

            if (!interlaced)
            {
                int threshold = pal
                    ? (g_display_ysize <= 224 ? 0x102 : 0x10A)
                    : 0xEA;
                int scanlinesPerFrame = pal ? 313 : 262;
                int counter = scanline <= threshold
                    ? scanline
                    : (scanline - scanlinesPerFrame) & 0x1FF;
                vblankFlag = counter >= activeScanlines && counter != 0x1FF;
                return (byte)counter;
            }
            else
            {
                int threshold = pal
                    ? (g_display_ysize <= 224 ? 0x101 : 0x109)
                    : 0xEA;
                int scanlinesPerFrame = GetScanlinesPerFrame();
                int internalCounter = scanline <= threshold
                    ? scanline
                    : (scanline - scanlinesPerFrame) & 0x1FF;
                vblankFlag = internalCounter >= activeScanlines && internalCounter != 0x1FF;
                int externalCounter = interlacedDouble
                    ? ((internalCounter << 1) & 0xFE) | ((internalCounter >> 7) & 1)
                    : (internalCounter & 0xFE) | ((internalCounter >> 8) & 1);
                return (byte)externalCounter;
            }
        }

        private bool IsPalTiming() => g_vertical_line_max >= 312;

        private static ushort ScanlineMclkToPixelH32(long scanlineMclk) => (ushort)(scanlineMclk / 10);

        private static ushort PixelToInternalHH32(ushort pixel) => pixel <= 0x127 ? pixel : (ushort)(pixel + (0x1D2 - 0x128));

        private static ushort ScanlineMclkToPixelH40(long scanlineMclk)
        {
            const long jumpDiff = 0x1C9 - 0x16D;
            long hsyncStartMclk = (0x1CC - jumpDiff) * 8;
            if (scanlineMclk < hsyncStartMclk)
                return (ushort)(scanlineMclk / 8);

            long hsyncEndMclk = hsyncStartMclk + 2 * (8 + 7 * 10 + 2 * 9 + 7 * 10);
            if (scanlineMclk >= hsyncStartMclk && scanlineMclk < hsyncEndMclk)
            {
                long hsyncMclk = scanlineMclk - hsyncStartMclk;
                long pattern = hsyncMclk % (8 + 7 * 10 + 2 * 9 + 7 * 10);
                int patternPixel = pattern switch
                {
                    >= 0 and <= 7 => 0,
                    >= 8 and <= 77 => 1 + (int)((pattern - 8) / 10),
                    >= 78 and <= 95 => 8 + (int)((pattern - 78) / 9),
                    >= 96 and <= 165 => 10 + (int)((pattern - 96) / 10),
                    _ => 0
                };
                return hsyncMclk < 166
                    ? (ushort)(0x1CC - jumpDiff + patternPixel)
                    : (ushort)(0x1CC - jumpDiff + 17 + patternPixel);
            }

            long postHsyncMclk = scanlineMclk - hsyncEndMclk;
            return (ushort)(0x1CC - jumpDiff + 34 + postHsyncMclk / 8);
        }

        private static ushort PixelToInternalHH40(ushort pixel) => pixel <= 0x16C ? pixel : (ushort)(pixel + (0x1C9 - 0x16D));

        private static bool IsHblankFlagClearRange(ushort internalH, bool isH40)
        {
            return isH40
                ? (internalH >= 0x00B && internalH < 0x166)
                : (internalH >= 0x00A && internalH < 0x126);
        }

        private void set_vdp_register(uint in_num, byte in_data)
        {
            byte oldRaw = g_vdp_reg[in_num];
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
                    // Register #3: window nametable base uses bits 5..1.
                    // Bit 6 is not part of the base address.
                    g_vdp_reg_3_windows = (ushort)((in_data & 0x3e) << 10);
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
                    // Register #5 stores SAT base bits 15..9 independent of H32/H40.
                    // H mode masking (A9 ignored in H40) is applied when SAT is read.
                    g_vdp_reg_5_sprite = (ushort)((in_data & 0x7f) << 9);
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
                {
                    byte oldVal = g_vdp_reg_10_hint;
                    g_vdp_reg_10_hint = in_data;
                    if (TraceScrollRegs && _traceScrollRegsRemaining > 0 && oldVal != g_vdp_reg_10_hint)
                    {
                        if (_traceScrollRegsRemaining != int.MaxValue)
                            _traceScrollRegsRemaining--;
                        Console.WriteLine($"[VDP-REG10] frame={_frameCounter} old=0x{oldVal:X2} new=0x{g_vdp_reg_10_hint:X2}");
                    }
                    break;
                }

                case 11:
                {
                    byte oldVal = oldRaw;
                    g_vdp_reg_11_3_ext     = (byte)((in_data >> 3) & 0x01);
                    g_vdp_reg_11_2_vscroll = (byte)((in_data >> 2) & 0x01);
                    g_vdp_reg_11_1_hscroll = (byte)(in_data & 0x03);
                    if (TraceScrollRegs && _traceScrollRegsRemaining > 0 && oldVal != in_data)
                    {
                        if (_traceScrollRegsRemaining != int.MaxValue)
                            _traceScrollRegsRemaining--;
                        Console.WriteLine($"[VDP-REG11] frame={_frameCounter} old=0x{oldVal:X2} new=0x{in_data:X2} hscroll={g_vdp_reg_11_1_hscroll} vscroll={g_vdp_reg_11_2_vscroll} ext={g_vdp_reg_11_3_ext}");
                    }
                    break;
                }

                  case 12:
                  {
                       // Apply reg12 immediately on write.
                       // Per-line rendering timing is handled by line snapshots in the renderer.
                       byte prevInterlace = g_vdp_interlace_mode;
                       bool prevH40 = IsH40Mode();

                       g_vdp_reg_12_7_cellmode1     = (byte)((in_data >> 7) & 0x01);
                       g_vdp_reg_12_3_shadow        = (byte)((in_data >> 3) & 0x01);
                       g_vdp_reg_12_2_interlacemode = (byte)((in_data >> 1) & 0x03);
                       g_vdp_reg_12_0_cellmode2     = (byte)(in_data & 0x01);
                       g_vdp_interlace_mode = DecodeInterlaceMode(g_vdp_reg_12_2_interlacemode);
                       ApplyInterlaceOverrides();
                       _reg12_latch_pending = false;

                       bool newH40 = IsH40Mode();
                       if (prevH40 != newH40)
                       {
                           ApplyHorizontalMode(newH40);
                       }

                       if (prevInterlace != g_vdp_interlace_mode)
                       {
                           Console.WriteLine($"[VDP-INTERLACE-CHANGE] frame={_frameCounter} prev={prevInterlace} new={g_vdp_interlace_mode} reg12_interlacemode={g_vdp_reg_12_2_interlacemode}");
                           g_vdp_interlace_field = 0;
                           InvalidateSpriteRowCache();
                           RecomputeScrollSizes();
                           RecomputeWindowBounds();
                           UpdateOutputWidth();
                       }

                       if (TraceReg12)
                       {
                           byte rs1 = (byte)((in_data >> 7) & 0x01);
                           byte rs0 = (byte)(in_data & 0x01);
                           byte shadow = (byte)((in_data >> 3) & 0x01);
                           byte interlace = (byte)((in_data >> 1) & 0x03);
                           int width = newH40 ? 320 : 256;
                           Console.WriteLine($"[REG12-W] frame={_frameCounter} data=0x{in_data:X2} rs1={rs1} rs0={rs0} shadow={shadow} interlace={interlace} H40={newH40} APPLY width={width}");
                       }

                       break;
                  }

                case 13:
                {
                    int oldVal = g_vdp_reg_13_hscroll;
                    g_vdp_reg_13_hscroll = (ushort)((in_data & 0x3f) << 10);
                    if (TraceScrollRegs && _traceScrollRegsRemaining > 0 && oldVal != g_vdp_reg_13_hscroll)
                    {
                        if (_traceScrollRegsRemaining != int.MaxValue)
                            _traceScrollRegsRemaining--;
                        Console.WriteLine($"[VDP-REG13] frame={_frameCounter} old=0x{oldVal:X4} new=0x{g_vdp_reg_13_hscroll:X4} raw=0x{in_data:X2}");
                    }
                    break;
                }

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
                {
                    byte oldVal = g_vdp_reg_15_autoinc;
                    g_vdp_reg_15_autoinc = in_data;
                    if (TraceScrollRegs && _traceScrollRegsRemaining > 0 && oldVal != g_vdp_reg_15_autoinc)
                    {
                        if (_traceScrollRegsRemaining != int.MaxValue)
                            _traceScrollRegsRemaining--;
                        Console.WriteLine($"[VDP-REG15] frame={_frameCounter} old=0x{oldVal:X2} new=0x{g_vdp_reg_15_autoinc:X2}");
                    }
                    break;
                }

                case 16:
                {
                    int oldV = g_vdp_reg_16_5_scrollV;
                    int oldH = g_vdp_reg_16_1_scrollH;
                    g_vdp_reg_16_5_scrollV = (in_data >> 4) & 0x03;
                    g_vdp_reg_16_1_scrollH = in_data & 0x03;
                    RecomputeScrollSizes();
                    if (TraceScrollRegs && _traceScrollRegsRemaining > 0
                        && (oldV != g_vdp_reg_16_5_scrollV || oldH != g_vdp_reg_16_1_scrollH))
                    {
                        if (_traceScrollRegsRemaining != int.MaxValue)
                            _traceScrollRegsRemaining--;
                        Console.WriteLine($"[VDP-REG16] frame={_frameCounter} oldV={oldV} oldH={oldH} newV={g_vdp_reg_16_5_scrollV} newH={g_vdp_reg_16_1_scrollH} raw=0x{in_data:X2}");
                    }
                    break;
                }

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
