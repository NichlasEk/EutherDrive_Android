using System;

namespace EutherDrive.Core.MdTracerCore
{
    //----------------------------------------------------------------
    // VDP : chips:315-5313
    //----------------------------------------------------------------
    internal partial class md_vdp
    {
        internal const ushort VDP_STATUS_VBLANK_MASK = 0x0080;
        private static readonly bool TraceMdVdp =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_MD_VDP"), "1", StringComparison.Ordinal);
        public int g_scanline;
        private int g_hinterrupt_counter;
        private bool _smsCommandPending;
        private byte _smsCommandLow;
        private int _smsVdpCode;
        private int _smsVdpAddr;
        private byte[] _smsVram = new byte[0x4000];
        private byte[] _smsRegs = new byte[16];
        private int _smsCommandLogCount;
        private const int SmsCommandLogLimit = 200;
        private bool _smsDisplayOnLogged;
        private bool _smsDataIgnoredLogged;
        private bool _smsCramWriteLogged;
        private bool _smsFirstLineRendered;
        private int _smsFrameHashCounter;
        private uint _smsLastFrameHash;
        private long _smsVramWritesTotal;
        private long _smsCramWritesTotal;
        private long _smsVramWritesAtLastSummary;
        private long _smsCramWritesAtLastSummary;
        private int _smsBeWritesThisFrame;
        private int _smsBeWritesLastFrame;
        private int _smsBfWritesThisFrame;
        private int _smsBfWritesLastFrame;
        private long _frameCounter;
        private int _mdCtrlWritesThisFrame;
        private int _mdDataWritesThisFrame;
        private int _mdVramWritesThisFrame;
        private int _mdCramWritesThisFrame;
        private int _mdNoWriteFrames;
        private bool _mdDataPortLogged;
        private bool _mdCtrlPortLogged;
        private bool _vblankActive;
        private bool _forceVBlankLogged;
        private long _lastForcedVBlankFrame = -1;
        private bool _forceMdVBlankLogged;
        private long _lastForcedMdVBlankFrame = -1;
        private long _lastTriggerVBlankLogFrame = -1;
        private long _lastStatusReadLogFrame = -1;
        private static readonly bool TraceVdpTiming =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_VDP_TIMING"), "1", StringComparison.Ordinal);
        private static readonly bool TraceVdpInterlace =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_VDP_INTERLACE"), "1", StringComparison.Ordinal);
        private static readonly bool TraceVdpState =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_VDP_STATE"), "1", StringComparison.Ordinal);
        private static readonly bool ShowOverscan =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_SHOW_OVERSCAN"), "1", StringComparison.Ordinal);
        private static readonly System.Diagnostics.Stopwatch _timingStopwatch = System.Diagnostics.Stopwatch.StartNew();
        private long _lastTimingLogFrame = -1;
        private long _lastTimingLogMs;
        private long _lastStateLogFrame = -1;
        private bool _vblankRiseLogged;
        private bool _vblankFallLogged;
        private static readonly bool ForceSmsVBlank =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_FORCE_SMS_VBLANK"), "1", StringComparison.Ordinal);
        private static readonly bool ForceMdVBlank =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_FORCE_MD_VBLANK"), "1", StringComparison.Ordinal);

        private enum InterlaceOutputPolicy
        {
            SingleField = 0,
            DoubleField = 1
        }

        private static InterlaceOutputPolicy InterlaceOutput =
            ParseInterlaceOutputPolicy(Environment.GetEnvironmentVariable("EUTHERDRIVE_VDP_INTERLACE_OUTPUT"));

        private byte g_vdp_interlace_field;
        private bool _interlaceFieldAdvanced; // Guard för att förhindra dubbel AdvanceInterlaceField() per frame

        // --- UI-agnostiska inparametrar (matas från Avalonia) ---
        public bool MouseClickInterrupt { get; set; }
        public int MouseClickPosX { get; set; }
        public int MouseClickPosY { get; set; }

        // --- Minimal framebuffer (RGBA32) för Avalonia ---
        private const int MaxFrameWidth = 320;
        private const int MaxFrameHeight = 480;
        public int FrameWidth  { get; private set; } = 320;  // MD standard 320x224 (NTSC)
        public int FrameHeight { get; private set; } = 224;
        public int Pitch => FrameWidth * 4;                  // bytes per rad
        public byte[] RgbaFrame { get; private set; } = Array.Empty<byte>();

        public md_vdp()
        {
            initialize();
            SyncFrameSizeFromVdp();
            dx_rendering_initialize(); // no-op stub just nu
        }

        private void SyncFrameSizeFromVdp()
        {
            UpdateOutputWidth();
            FrameWidth = g_output_xsize;
            FrameHeight = g_output_ysize;
            EnsureFrameBuffer();
        }

        private void UpdateOutputWidth()
        {
            g_output_xsize = ShowOverscan ? MaxFrameWidth : g_display_xsize;
            g_output_ysize = GetOutputHeight();
        }

        private void EnsureFrameBuffer()
        {
            if (RgbaFrame.Length != 0)
                return;

            int allocW = Math.Max(FrameWidth, MaxFrameWidth);
            int allocH = Math.Max(FrameHeight, MaxFrameHeight);
            RgbaFrame = new byte[allocW * allocH * 4];
        }

        internal long FrameCounter => _frameCounter;
        internal byte InterlaceField => g_vdp_interlace_field;

        /// <summary>Byt upplösning (valfritt att kalla om du vill synka till VDP-registret senare).</summary>
        public void SetFrameSize(int width, int height)
        {
            if (width <= 0 || height <= 0) return;
            FrameWidth = width;
            FrameHeight = height;
            EnsureFrameBuffer();
        }

        public void run(int in_vline)
        {

            g_scanline = in_vline;

            if (g_scanline == 0)
            {
                _smsBeWritesThisFrame = 0;
                _smsBfWritesThisFrame = 0;
                ClearVBlank();
                rendering_line();
                set_hinterrupt();
                interrupt_check();
            }
            else if (g_scanline < g_display_ysize)   // g_display_ysize finns i dina VDP-filer
            {
                rendering_line();
                interrupt_check();
            }
            else if (g_scanline == g_display_ysize)
            {
                _smsBeWritesLastFrame = _smsBeWritesThisFrame;
                _smsBfWritesLastFrame = _smsBfWritesThisFrame;
                rendering_frame();
                interrupt_check();

                TriggerVBlank();
            }
            else if (g_scanline == g_vertical_line_max - 1) // också definierad i VDP
            {
            // Keep VBlank flag until the next frame even when clearing per-line stats.
            g_vdp_status_4_frame = (byte)((g_vdp_status_4_frame == 0) ? 1 : 0);
                g_vdp_status_5_collision = 0;
                g_vdp_status_6_sprite = 0;
            }

            // Nollställ guard för fält-växling först EFTER att alla nested run() anrop är klara.
            // Detta säkerställer att varje frame-växling kan toggla fältet exakt en gång,
            // även när rendering_frame() anropas två gånger (vid scanline 224 och via run(0)).
            _interlaceFieldAdvanced = false;
        }

        private void set_hvcounter()
        {
            if (g_vdp_interlace_mode == 0)
            {
                g_vdp_c00008_hvcounter = (ushort)(((MouseClickPosX >> 1) & 0x00ff)
                + (MouseClickPosY << 8));
            }
            else
            {
                int interlaceY = (MouseClickPosY << 1) | g_vdp_interlace_field;
                g_vdp_c00008_hvcounter = (ushort)(((MouseClickPosX >> 1) & 0x00ff)
                + ((interlaceY << 8) & 0xfe00)
                + (interlaceY & 0x0100));
            }
        }

        private static void DrawCrosshairArgb(int cx, int cy, byte r, byte g, byte b)
        {
            uint c = 0xFF000000u | ((uint)r << 16) | ((uint)g << 8) | b;
            DrawRectArgb(cx - 4, cy - 1, 9, 3, c);
            DrawRectArgb(cx - 1, cy - 4, 3, 9, c);
        }

        private static void DrawRectArgb(int x0, int y0, int w, int h, uint argb)
        {
            int x1 = Math.Min(Width, x0 + w);
            int y1 = Math.Min(Height, y0 + h);
            x0 = Math.Max(0, x0);
            y0 = Math.Max(0, y0);

            for (int y = y0; y < y1; y++)
            {
                int row = y * Width;
                for (int x = x0; x < x1; x++)
                    _frame[row + x] = argb;
            }
        }


        private void set_hinterrupt()
        {
            g_hinterrupt_counter = g_vdp_reg_10_hint;
        }

        private void interrupt_check()
        {
            // H-INT
            g_hinterrupt_counter -= 1;
            if (g_hinterrupt_counter < 0)
            {
                md_m68k.g_interrupt_H_req = true;
                set_hinterrupt();
            }

            // EXT (mus-klick)
            if (MouseClickInterrupt)
            {
                if (g_vdp_reg_11_3_ext == 1)
                {
                    md_m68k.g_interrupt_EXT_req = true;

                    if ((g_vdp_reg_0_1_hvcounter == 1) && (g_vdp_c00008_hvcounter_latched == false))
                    {
                        set_hvcounter();
                        g_vdp_c00008_hvcounter_latched = true;
                    }
                    else
                    {
                        g_vdp_c00008_hvcounter_latched = false;
                    }
                }
                else
                {
                    MouseClickInterrupt = false;
                }
            }
        }

        // --------- Render init ----------
        private void dx_rendering_initialize() { /* no-op i headless */ }

        private void UpdateRgbaFrameFromGameScreen()
        {
            SyncFrameSizeFromVdp();
            int pixels = FrameWidth * FrameHeight;
            if (g_game_screen == null || g_game_screen.Length < pixels)
                return;

            int di = 0;
            for (int i = 0; i < pixels; i++)
            {
                uint argb = g_game_screen[i];
                RgbaFrame[di + 0] = (byte)(argb >> 16); // R
                RgbaFrame[di + 1] = (byte)(argb >> 8);  // G
                RgbaFrame[di + 2] = (byte)argb;         // B
                RgbaFrame[di + 3] = (byte)(argb >> 24); // A
                di += 4;
            }
        }

        private int GetInterlaceLine(int scanline)
        {
            if (g_vdp_interlace_mode == 0)
                return scanline;
            return (scanline << 1) | g_vdp_interlace_field;
        }

        private int GetRenderLine(int scanline)
        {
            if (g_vdp_interlace_mode == 0)
                return scanline;
            if (InterlaceOutput == InterlaceOutputPolicy.SingleField)
                return scanline << 1;
            return (scanline << 1) | g_vdp_interlace_field;
        }

        private int GetHScrollLine(int scanline)
        {
            if (g_vdp_interlace_mode == 2)
                return scanline >> 1;
            return scanline;
        }

        private int GetCellHeightShift() => g_vdp_interlace_mode == 2 ? 4 : 3;

        private int GetCellHeightPixels() => g_vdp_interlace_mode == 2 ? 16 : 8;

        private int GetRowInCell(int lineInCell) => g_vdp_interlace_mode == 2 ? (lineInCell & 0x0f) : (lineInCell & 0x07);

        private int GetTileWordBase(int tileIndex)
        {
            if (g_vdp_interlace_mode == 2)
            {
                int masked = tileIndex & 0x03ff;
                return masked << 5;
            }
            return (tileIndex & 0x07ff) << 4;
        }

        private int GetReversePage(uint reverse)
        {
            if (g_vdp_interlace_mode == 2)
                return (reverse & 0x01) != 0 ? VRAM_DATASIZE : 0;
            return (int)(reverse * VRAM_DATASIZE);
        }

        private int GetRowWordOffset(int rowInCell, uint reverse)
        {
            int row = rowInCell;
            if (g_vdp_interlace_mode == 2 && (reverse & 0x02) != 0)
                row = 15 - row;
            return row << 1;
        }

        private int GetTileWordAddress(int tileIndex, int rowInCell, uint reverse)
        {
            return GetReversePage(reverse) + GetTileWordBase(tileIndex) + GetRowWordOffset(rowInCell, reverse);
        }

        private int GetOutputHeight()
        {
            if (g_vdp_interlace_mode != 0 && InterlaceOutput == InterlaceOutputPolicy.DoubleField)
                return g_display_ysize * 2;
            return g_display_ysize;
        }

        private int GetOutputLineForScanline(int scanline)
        {
            if (g_vdp_interlace_mode != 0 && InterlaceOutput == InterlaceOutputPolicy.DoubleField)
                return (scanline << 1) | g_vdp_interlace_field;
            return scanline;
        }

        private void AdvanceInterlaceField()
        {
            // Guard mot dubbel anrop (kan hända när rendering_frame() anropas två gånger)
            if (_interlaceFieldAdvanced)
                return;

            if (g_vdp_interlace_mode == 0)
            {
                g_vdp_interlace_field = 0;
                return;
            }

            g_vdp_interlace_field ^= 0x01;
            _interlaceFieldAdvanced = true;
            if (TraceVdpInterlace)
            {
                string fieldLabel = g_vdp_interlace_field == 0 ? "even" : "odd";
                Console.WriteLine($"[VDP] interlace field={fieldLabel} frame={_frameCounter}");
            }
        }

        private static InterlaceOutputPolicy ParseInterlaceOutputPolicy(string? raw)
        {
            if (string.Equals(raw, "single_field", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(raw, "single", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(raw, "bob", StringComparison.OrdinalIgnoreCase))
            {
                return InterlaceOutputPolicy.SingleField;
            }
            return InterlaceOutputPolicy.DoubleField;
        }

        private void MaybeLogVdpState()
        {
            if (!TraceVdpState)
                return;
            if (_frameCounter - _lastStateLogFrame < 60)
                return;
            _lastStateLogFrame = _frameCounter;

            int planeA = g_vdp_reg_2_scrolla >> 1;
            int planeB = g_vdp_reg_4_scrollb >> 1;
            int window = g_vdp_reg_3_windows >> 1;
            int sprite = g_vdp_reg_5_sprite >> 1;
            int hscroll = g_vdp_reg_13_hscroll >> 1;

            uint sampleA = (uint)((uint)planeA < (uint)g_renderer_vram.Length ? g_renderer_vram[planeA] : 0);
            uint sampleB = (uint)((uint)planeB < (uint)g_renderer_vram.Length ? g_renderer_vram[planeB] : 0);
            uint sampleW = (uint)((uint)window < (uint)g_renderer_vram.Length ? g_renderer_vram[window] : 0);
            uint sampleS = (uint)((uint)sprite < (uint)g_renderer_vram.Length ? g_renderer_vram[sprite] : 0);

            Console.WriteLine(
                $"[VDPSTATE] frame={_frameCounter} display={g_vdp_reg_1_6_display} interlace={g_vdp_interlace_mode} field={g_vdp_interlace_field} " +
                $"disp={g_display_xsize}x{g_display_ysize} out={g_output_xsize}x{g_output_ysize} " +
                $"A=0x{planeA:X4} B=0x{planeB:X4} W=0x{window:X4} S=0x{sprite:X4} HS=0x{hscroll:X4} " +
                $"A0=0x{sampleA:X4} B0=0x{sampleB:X4} W0=0x{sampleW:X4} S0=0x{sampleS:X4} bg=0x{g_vdp_reg_7_backcolor:X2}");
        }

        private void SmsLogFrameHash()
        {
            if (!md_main.g_masterSystemMode || !MdTracerCore.MdLog.Enabled)
                return;

            _smsFrameHashCounter++;
            if ((_smsFrameHashCounter % 60) != 0)
                return;

            if (g_game_screen == null || g_game_screen.Length == 0)
                return;

            int step = Math.Max(1, g_game_screen.Length / 64);
            uint hash = 0;
            for (int i = 0; i < g_game_screen.Length; i += step)
            {
                hash ^= g_game_screen[i];
                hash = (hash << 3) | (hash >> 29);
            }

            if (hash != _smsLastFrameHash)
            {
                _smsLastFrameHash = hash;
                MdTracerCore.MdLog.WriteLine($"[SMS VDP] framebuffer hash=0x{hash:X8}");
            }

            SmsLogFrameSummary(hash);
        }

        private void SmsLogFrameSummary(uint hash)
        {
            ushort pc = md_main.g_md_z80?.DebugPc ?? 0;
            ushort bc = md_main.g_md_z80?.DebugBc ?? 0;
            long vramDelta = _smsVramWritesTotal - _smsVramWritesAtLastSummary;
            long cramDelta = _smsCramWritesTotal - _smsCramWritesAtLastSummary;
            _smsVramWritesAtLastSummary = _smsVramWritesTotal;
            _smsCramWritesAtLastSummary = _smsCramWritesTotal;
            bool displayEnabled = g_vdp_reg_1_6_display != 0;
            bool pcLeftBootRangeSinceLastSummary = md_main.g_md_z80?.ConsumeBootRangeExitFlag() ?? false;

            MdTracerCore.MdLog.WriteLine(
                $"[SMS VDP] summary PC=0x{pc:X4} pcLeftBootRangeSinceLastSummary={(pcLeftBootRangeSinceLastSummary ? 1 : 0)} BC=0x{bc:X4} smsVdpCode={_smsVdpCode} vramWritesDelta={vramDelta} cramWritesDelta={cramDelta} fbHash=0x{hash:X8} reg1.display={(displayEnabled ? 1 : 0)} BEWritesPerFrame={_smsBeWritesLastFrame} BFWritesPerFrame={_smsBfWritesLastFrame}");
        }

        internal void RecordSmsBeWrite()
        {
            _smsBeWritesThisFrame++;
        }

        internal void RecordSmsBfWrite()
        {
            _smsBfWritesThisFrame++;
        }

        private void TriggerVBlank()
        {
            if (_vblankActive)
                return;

            _vblankActive = true;
            g_vdp_status_3_vbrank = 1;
            if (g_vdp_status_7_vinterrupt == 0)
            {
                g_vdp_status_7_vinterrupt = 1;
                md_m68k.g_interrupt_V_req = true;
                // Z80 needs VBlank interrupt for sound drivers in both MD and SMS mode
                md_main.g_md_z80?.irq_request(true, "VDP", 0);
            }
            LogTriggerVBlank();
            LogVBlankEdge(1);
        }

        private void ClearVBlank()
        {
            if (!_vblankActive)
                return;

            _vblankActive = false;
            g_vdp_status_3_vbrank = 0;
            g_vdp_status_7_vinterrupt = 0;
            md_m68k.g_interrupt_V_req = false;
            // Clear Z80 INT for sound drivers in both MD and SMS mode
            md_main.g_md_z80?.irq_request(false, "VDP", 0);
            LogVBlankEdge(0);
        }

        private void ForceVBlankForTest()
        {
            if (!ForceSmsVBlank)
                return;

            if (_lastForcedVBlankFrame == _frameCounter)
                return;

            g_vdp_status_7_vinterrupt = 1;

            if (!_forceVBlankLogged)
            {
                _forceVBlankLogged = true;
                MdTracerCore.MdLog.WriteLine("[SMS VDP] forced VBlank/status7 for test");
            }

            _lastForcedVBlankFrame = _frameCounter;
        }

        private void ForceMdVBlankForTest()
        {
            if (!ForceMdVBlank || md_main.g_masterSystemMode)
                return;

            if (_lastForcedMdVBlankFrame == _frameCounter)
                return;

            TriggerVBlank();

            if (!_forceMdVBlankLogged)
            {
                _forceMdVBlankLogged = true;
                MdTracerCore.MdLog.WriteLine("[MD VDP] forced VBlank/IRQ6 for test");
            }

            _lastForcedMdVBlankFrame = _frameCounter;
            LogForcedVBlank();
        }

        private void LogTriggerVBlank()
        {
            if (!TraceMdVdp)
                return;
            if (_lastTriggerVBlankLogFrame == _frameCounter)
                return;
            _lastTriggerVBlankLogFrame = _frameCounter;
            bool irq = md_m68k.g_interrupt_V_req;
            Console.WriteLine($"[VDP] TriggerVBlank frame={_frameCounter} line={g_scanline} setVBlankFlag=1 irq6Requested={(irq ? 1 : 0)}");
        }

        private void LogVBlankEdge(int state)
        {
            if (!TraceVdpTiming)
                return;
            if (state == 1)
            {
                if (_vblankRiseLogged)
                    return;
                _vblankRiseLogged = true;
                Console.WriteLine($"[VDP] VBlank edge 0->1 frame={_frameCounter} line={g_scanline}");
            }
            else
            {
                if (_vblankFallLogged)
                    return;
                _vblankFallLogged = true;
                Console.WriteLine($"[VDP] VBlank edge 1->0 frame={_frameCounter} line={g_scanline}");
            }
        }

        private void MaybeLogVdpTiming()
        {
            if (!TraceVdpTiming)
                return;
            long nowMs = _timingStopwatch.ElapsedMilliseconds;
            if (_frameCounter - _lastTimingLogFrame < 60 && nowMs - _lastTimingLogMs < 1000)
                return;
            _lastTimingLogFrame = _frameCounter;
            _lastTimingLogMs = nowMs;

            ushort hv = get_vdp_hvcounter();
            ushort status = PeekVdpStatus();
            int vblank = ((status & VDP_STATUS_VBLANK_MASK) != 0) ? 1 : 0;
            int vintPending = md_m68k.g_interrupt_V_req ? 1 : 0;
            int vintEnabled = g_vdp_reg_1_5_vinterrupt;

            Console.WriteLine(
                $"[VDP] timing frame={_frameCounter} line={g_scanline} hv=0x{hv:X4} status=0x{status:X4} vblank={vblank} vintPending={vintPending} reg1.vint={vintEnabled}");
        }

        private void LogStatusRead(ushort preStatus, ushort postStatus)
        {
            if (!TraceMdVdp)
                return;
            if (_lastStatusReadLogFrame == _frameCounter)
                return;
            _lastStatusReadLogFrame = _frameCounter;
            int vblankPre = ((preStatus & VDP_STATUS_VBLANK_MASK) != 0) ? 1 : 0;
            Console.WriteLine($"[VDP] StatusRead frame={_frameCounter} pre=0x{preStatus:X4} post=0x{postStatus:X4} vblankPre={vblankPre}");
        }

        private void LogForcedVBlank()
        {
            if (!TraceMdVdp)
                return;
            Console.WriteLine($"[VDP] Forced VBlank applied frame={_frameCounter} line={g_scanline}");
        }

        private void LogMdWriteSummary()
        {
            if (!MdTracerCore.MdLog.Enabled)
                return;

            if (_mdCtrlWritesThisFrame == 0 && _mdDataWritesThisFrame == 0 && _mdVramWritesThisFrame == 0 && _mdCramWritesThisFrame == 0)
                _mdNoWriteFrames++;
            else
                _mdNoWriteFrames = 0;

            bool log = (_frameCounter % 60) == 0 || _mdNoWriteFrames == 120;
            if (log)
            {
                MdTracerCore.MdLog.WriteLine($"[VDP] md writes frame={_frameCounter} ctrl={_mdCtrlWritesThisFrame} data={_mdDataWritesThisFrame} vram={_mdVramWritesThisFrame} cram={_mdCramWritesThisFrame} zeroFrames={_mdNoWriteFrames}");
            }

            _mdCtrlWritesThisFrame = 0;
            _mdDataWritesThisFrame = 0;
            _mdVramWritesThisFrame = 0;
            _mdCramWritesThisFrame = 0;
        }

    }
}
