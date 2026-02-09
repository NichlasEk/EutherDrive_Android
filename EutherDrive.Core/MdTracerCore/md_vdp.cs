using System;
using System.Globalization;
using System.IO;
using System.Linq;

namespace EutherDrive.Core.MdTracerCore
{
    //----------------------------------------------------------------
    // VDP : chips:315-5313
    //----------------------------------------------------------------
    public partial class md_vdp
    {
        private static readonly bool TraceDmaDetail =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_DMA_DETAIL"), "1", StringComparison.Ordinal);
        internal const ushort VDP_STATUS_VBLANK_MASK = 0x0080;
         private static readonly bool TraceMdVdp =
             string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_MD_VDP"), "1", StringComparison.Ordinal);
         private static readonly bool DebugNtChk =
             string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_DEBUG_NTCHK"), "1", StringComparison.Ordinal);
        private static readonly bool DebugVdpFrame =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_DEBUG_VDPFRAME"), "1", StringComparison.Ordinal);
        private static readonly bool TraceVdpFrameSummary =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_VDP_FRAME"), "1", StringComparison.Ordinal);
        private static readonly int SmsVramDumpFrame =
            ParseTraceLimit("EUTHERDRIVE_SMS_VRAM_DUMP_FRAME", -1);
        private static readonly string SmsVramDumpPath =
            Environment.GetEnvironmentVariable("EUTHERDRIVE_SMS_VRAM_DUMP_PATH") ?? "/tmp/sms_vram_dump.txt";
        private static readonly int SmsNameTableDumpFrame =
            ParseTraceLimit("EUTHERDRIVE_SMS_NT_DUMP_FRAME", -1);
        private static readonly string SmsNameTableDumpPath =
            Environment.GetEnvironmentVariable("EUTHERDRIVE_SMS_NT_DUMP_PATH") ?? "/tmp/sms_nt_dump.txt";
        private static readonly bool TraceSmsFrameDetail =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_SMS_FRAME_DETAIL"), "1", StringComparison.Ordinal);
        private static readonly int SmsVramDumpOnHashChanges =
            ParseTraceLimit("EUTHERDRIVE_SMS_VRAM_DUMP_ON_HASH_CHANGES", 0);
        private static readonly string SmsVramDumpOnHashPrefix =
            Environment.GetEnvironmentVariable("EUTHERDRIVE_SMS_VRAM_DUMP_ON_HASH_PREFIX") ?? "/tmp/sms_vram_hash";
        private static readonly bool DebugDmaWin =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_DEBUG_DMAWIN"), "1", StringComparison.Ordinal);
        private static readonly bool SpriteLinkSequential =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_SPRITE_LINK_SEQUENTIAL"), "1", StringComparison.Ordinal);
        private static readonly int Z80VblankIrqPulseCycles = ParseZ80VblankIrqPulseCycles();
        public int g_scanline;
        private int g_hinterrupt_counter;
        private bool _smsCommandPending;
        private byte _smsCommandLow;
        private int _smsVdpCode;
        private int _smsVdpAddr;
        private byte _smsReadBuffer;
        private byte[] _smsVram = new byte[0x4000];
        private byte[] _smsRegs = new byte[16];
        private byte _smsLineCounter = 0xFF;
        private byte _smsLineCounterReload;
        private bool _smsLineInterruptPending;
        private byte _smsLatchedReg0;
        private byte _smsLatchedHScroll;
        private byte _smsLatchedVScroll;
        private byte _smsNextReg0;
        private byte _smsNextHScroll;
        private byte _smsNextVScroll;
        private ushort _smsHvLatch;
        private bool _smsHvLatchValid;
        private int _smsCommandLogCount;
        private const int SmsCommandLogLimit = 200;
        private bool _smsDisplayOnLogged;
        private bool _smsDataIgnoredLogged;
        private bool _smsCramWriteLogged;
        private bool _smsFirstLineRendered;
        private int _smsFrameHashCounter;
        private uint _smsLastFrameHash;
        private bool _smsVramDumped;
        private bool _smsNameTableDumped;
        private int _smsVramDumpOnHashChangesLeft = SmsVramDumpOnHashChanges;
        private long _smsVramWritesTotal;
        private long _smsCramWritesTotal;
        private long _smsVramWritesAtLastSummary;
        private long _smsCramWritesAtLastSummary;
        private int _smsBeWritesThisFrame;
        private int _smsBeWritesLastFrame;
        private int _smsBfWritesThisFrame;
        private int _smsBfWritesLastFrame;
        private byte[] _smsCram = new byte[0x20];
        private uint[] _smsPalette = new uint[0x20];
        private long _frameCounter;
        private int _mdCtrlWritesThisFrame;
        private int _mdDataWritesThisFrame;
        private int _mdVramWritesThisFrame;
        private int _mdCramWritesThisFrame;
        private int _mdVramWritesToNameTablesThisFrame;
        private int _mdVramWritesDroppedThisFrame;
        private int _mdDataWritesDroppedThisFrame;
        private int _mdDataPortWindowFrame = -1;
        private int _mdDataPortWindowLogCount;
        private int[] _mdDataWriteCodeCounts = new int[16];
        private long _mdVramWritesToNameTablesTotal;
        private long _mdVramWritesTotal;
        private bool _mdVramNameTableWriteLogged;
        private bool _mdVramClearLogged;
        private int _mdNoWriteFrames;
        private bool _mdDataPortLogged;
        private bool _mdCtrlPortLogged;
        [NonSerialized]
        private int _traceVramRangeLogCount;
        private bool _vblankActive;
        private bool _z80VblankIntActive;
        private bool _forceVBlankLogged;
        private long _lastForcedVBlankFrame = -1;
        private bool _forceMdVBlankLogged;
        private long _lastForcedMdVBlankFrame = -1;
        private long _lastTriggerVBlankLogFrame = -1;
        private long _lastStatusReadLogFrame = -1;
        private int _spriteOverflowMaxSprites;
        private int _spriteOverflowMaxCells;
        private bool _spriteOverflowAny;
        private long _spriteOverflowLastFrame = -1;
        private static readonly bool TraceVdpTiming =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_VDP_TIMING"), "1", StringComparison.Ordinal);
        private static readonly bool TraceVdpInterlace =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_VDP_INTERLACE"), "1", StringComparison.Ordinal);
        private static readonly bool TraceVdpState =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_VDP_STATE"), "1", StringComparison.Ordinal);
        private static readonly bool TraceVdpRender =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_VDP_RENDER"), "1", StringComparison.Ordinal);
        private static readonly bool TraceVint =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_VINT"), "1", StringComparison.Ordinal);
        private static readonly bool TraceInterlaceDebug =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_INTERLACE_DEBUG"), "1", StringComparison.Ordinal);
        private static readonly bool TraceNameTable =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_NAME_TABLE"), "1", StringComparison.Ordinal);
        private static readonly bool TraceVramPageStats =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_VRAM_PAGE_STATS"), "1", StringComparison.Ordinal);
        private static readonly bool TraceVramStats =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_VRAM_STATS"), "1", StringComparison.Ordinal);
        private static readonly bool TraceVramWriteCpu =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_VRAM_WRITE_CPU"), "1", StringComparison.Ordinal);
        private static readonly int TraceVramWriteCpuLimit =
            ParseTraceLimit("EUTHERDRIVE_TRACE_VRAM_WRITE_CPU_LIMIT", 256);
        private static readonly bool TraceVdpCtrlWrite =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_VDP_CTRL_WRITE"), "1", StringComparison.Ordinal);
        private static readonly int TraceVdpCtrlWriteLimit =
            ParseTraceLimit("EUTHERDRIVE_TRACE_VDP_CTRL_WRITE_LIMIT", 128);
        private static readonly bool TraceVdpAddrSet =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_VDP_ADDR_SET"), "1", StringComparison.Ordinal);
        private static readonly int TraceVdpAddrSetLimit =
            ParseTraceLimit("EUTHERDRIVE_TRACE_VDP_ADDR_SET_LIMIT", 128);
        private static readonly bool TraceRomStartLog =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_ROM_START"), "1", StringComparison.Ordinal)
            || string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_ALL"), "1", StringComparison.Ordinal);
        private static readonly bool TraceSpriteOverflowFrame =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_SPRITE_OVERFLOW_FRAME"), "1", StringComparison.Ordinal);
        private static readonly bool TraceNameTableRowDump =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_NAMETABLE_ROW_DUMP"), "1", StringComparison.Ordinal);
        private static readonly bool TracePatternTileDump =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_PATTERN_TILE_DUMP"), "1", StringComparison.Ordinal);
        private static readonly bool TraceDmaStatus =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_DMA_STATUS"), "1", StringComparison.Ordinal);
        private static readonly bool TraceSatWrites =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_SAT_WRITES"), "1", StringComparison.Ordinal);
        private static readonly string? TraceVramRangeEnv = Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_VRAM_RANGE");
        private static readonly bool TraceVramRangeEnabled =
            TryParseVramRange(TraceVramRangeEnv, out _traceVramRangeStart, out _traceVramRangeEnd);
        private static readonly int TraceVramRangeLimit =
            ParseTraceLimit("EUTHERDRIVE_TRACE_VRAM_RANGE_LIMIT", 200);
        private static readonly bool TraceVramRangeSkipFill =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_VRAM_RANGE_SKIP_FILL"), "1", StringComparison.Ordinal);
        private static readonly bool TraceVramName =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_VRAM_NAME"), "1", StringComparison.Ordinal);
        private static readonly bool TraceVdpFrameSize =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_VDP_FRAME_SIZE"), "1", StringComparison.Ordinal);
        private static readonly bool TraceVramWriteDetail =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_VRAM_WRITE_DETAIL"), "1", StringComparison.Ordinal);
        private static int _traceVramRangeStart;
        private static int _traceVramRangeEnd;

        private static int ParseZ80VblankIrqPulseCycles()
        {
            const int fallback = 171;
            string? raw = Environment.GetEnvironmentVariable("EUTHERDRIVE_Z80_VBLANK_IRQ_PULSE");
            if (string.IsNullOrWhiteSpace(raw))
                return fallback;
            if (int.TryParse(raw.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed) && parsed > 0)
                return parsed;
            return fallback;
        }
        private static bool ShowOverscan =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_SHOW_OVERSCAN"), "1", StringComparison.Ordinal);
        private static readonly System.Diagnostics.Stopwatch _timingStopwatch = System.Diagnostics.Stopwatch.StartNew();
        private long _lastTimingLogFrame = -1;
        private long _lastTimingLogMs;
        private long _lastStateLogFrame = -1;
        [NonSerialized]
        private long _lastNameTableDumpFrame = -1;
        [NonSerialized]
        private int _lastNameTableDumpScanline = -1;
        private bool _vblankRiseLogged;
        private bool _vblankFallLogged;
        private static readonly bool ForceSmsVBlank =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_FORCE_SMS_VBLANK"), "1", StringComparison.Ordinal);
        private static readonly bool ForceMdVBlank =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_FORCE_MD_VBLANK"), "1", StringComparison.Ordinal);
        private static readonly string? ForceHBlankEnv = Environment.GetEnvironmentVariable("EUTHERDRIVE_VDP_FORCE_HBLANK");
        private static readonly bool ForceHBlankEnvSet = ForceHBlankEnv != null;
        private static bool ForceHBlank = string.Equals(ForceHBlankEnv, "1", StringComparison.Ordinal);

        private enum InterlaceOutputPolicy
        {
            SingleField = 0,
            DoubleField = 1
        }

        private static InterlaceOutputPolicy InterlaceOutput =
            ParseInterlaceOutputPolicy(Environment.GetEnvironmentVariable("EUTHERDRIVE_VDP_INTERLACE_OUTPUT"));
        private static readonly bool ForceInterlaceBob =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_VDP_FORCE_BOB"), "1", StringComparison.Ordinal);

        private byte g_vdp_interlace_field;
        private bool _interlaceFieldAdvanced; // Guard för att förhindra dubbel AdvanceInterlaceField() per frame

        private byte[] g_sprite_table_cache = Array.Empty<byte>();
        private int g_sprite_cache_base = -1;
        private int g_sprite_cache_size;
        private SpriteRowCacheRow[] g_sprite_row_cache = Array.Empty<SpriteRowCacheRow>();
        private bool g_sprite_row_cache_dirty = true;
        private int g_sprite_row_cache_field = -1;

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
            int oldWidth = FrameWidth;
            int oldHeight = FrameHeight;
            UpdateOutputWidth();
            FrameWidth = g_output_xsize;
            FrameHeight = g_output_ysize;
            if (oldWidth != FrameWidth || oldHeight != FrameHeight)
            {
                if (TraceVdpFrameSize)
                    Console.WriteLine($"[VDP-FRAME-SIZE] Changed: {oldWidth}x{oldHeight} -> {FrameWidth}x{FrameHeight} interlace={g_vdp_interlace_mode} display_ysize={g_display_ysize}");
            }
            EnsureFrameBuffer();
        }

        private void UpdateOutputWidth()
        {
            if (md_main.g_masterSystemMode)
                g_output_xsize = ShowOverscan ? MaxFrameWidth : g_display_xsize;
            else
                g_output_xsize = g_display_xsize;
            g_output_ysize = GetOutputHeight();
        }

        public void SetShowOverscan(bool enabled)
        {
            ShowOverscan = enabled;
            SyncFrameSizeFromVdp();
        }

        public bool GetShowOverscan() => ShowOverscan;

        internal void SetSmsLineCounterReload(byte value)
        {
            _smsLineCounterReload = value;
        }

        internal void BeginSmsLine()
        {
            _smsLatchedReg0 = _smsNextReg0;
            _smsLatchedHScroll = _smsNextHScroll;
            _smsLatchedVScroll = _smsNextVScroll;
        }

        internal void EndSmsLine()
        {
            _smsNextReg0 = _smsRegs[0];
            _smsNextHScroll = _smsRegs[8];
            _smsNextVScroll = _smsRegs[9];
        }

        internal void OnSmsStatusRead()
        {
            _smsLineInterruptPending = false;
            UpdateSmsIrqLine();
        }

        internal void UpdateSmsIrqLine()
        {
            if (!md_main.g_masterSystemMode)
                return;

            bool vblankPending = g_vdp_status_3_vbrank != 0;
            bool vblankEnabled = (_smsRegs[1] & 0x20) != 0;
            bool lineEnabled = (_smsRegs[0] & 0x10) != 0;
            bool irq = (vblankPending && vblankEnabled) || (_smsLineInterruptPending && lineEnabled);
            md_main.g_md_z80?.irq_request(irq, "VDP", 0);
        }

        private void SmsLineCounterTick()
        {
            if (!md_main.g_masterSystemMode)
                return;

            int smsActiveScanlines = g_display_ysize;
            if (g_scanline < smsActiveScanlines || g_scanline == g_vertical_line_max - 1)
            {
                if (_smsLineCounter == 0)
                {
                    _smsLineCounter = _smsLineCounterReload;
                    _smsLineInterruptPending = true;
                    UpdateSmsIrqLine();
                }
                else
                {
                    _smsLineCounter--;
                }
            }
            else
            {
                _smsLineCounter = _smsLineCounterReload;
            }

            // Keep IRQ line level in sync with current VBlank/line status.
            UpdateSmsIrqLine();
        }

        private void ApplyInterlaceOverrides()
        {
            if (!ForceHBlankEnvSet)
                ForceHBlank = g_vdp_interlace_mode == 2;
            if (!ForceDirectVramReadSpritesEnvSet)
                ForceDirectVramReadSprites = g_vdp_interlace_mode == 2;
        }

         private void EnsureFrameBuffer()
         {
             if (RgbaFrame.Length != 0)
                 return;

             int allocW = Math.Max(FrameWidth, MaxFrameWidth);
             int allocH = Math.Max(FrameHeight, MaxFrameHeight);
             RgbaFrame = new byte[allocW * allocH * 4];
         }
         
         private void ApplyLatchedRegister12()
         {
             if (!_reg12_latch_pending) return;
             
             byte prevInterlace = g_vdp_interlace_mode;
             bool prevH40 = IsH40Mode();
             
             // Apply latched values
             g_vdp_reg_12_7_cellmode1     = _reg12_latched_7_cellmode1;
             g_vdp_reg_12_3_shadow        = _reg12_latched_3_shadow;
             g_vdp_reg_12_2_interlacemode = _reg12_latched_2_interlacemode;
             g_vdp_reg_12_0_cellmode2     = _reg12_latched_0_cellmode2;
             
              g_vdp_interlace_mode = DecodeInterlaceMode(g_vdp_reg_12_2_interlacemode);
              ApplyInterlaceOverrides();
             
              bool newH40 = IsH40Mode();
              
              // [REG12-APPLY] when applying latched reg12 at VBlank: frame, applied data, computed width(256/320)
              byte appliedData = (byte)((_reg12_latched_7_cellmode1 << 7) | (_reg12_latched_3_shadow << 3) | (_reg12_latched_2_interlacemode << 1) | _reg12_latched_0_cellmode2);
              int width = newH40 ? 320 : 256;
              if (TraceReg12)
                  Console.WriteLine($"[REG12-APPLY] frame={_frameCounter} applied=0x{appliedData:X2} width={width}");
             
                   if (prevInterlace != g_vdp_interlace_mode)
                   {
                       Console.WriteLine($"[VDP-INTERLACE-CHANGE] frame={_frameCounter} prev={prevInterlace} new={g_vdp_interlace_mode} reg12_interlacemode={g_vdp_reg_12_2_interlacemode}");
                       g_vdp_interlace_field = 0;
                       InvalidateSpriteRowCache();
                       RecomputeScrollSizes();
                       RecomputeWindowBounds();
                       UpdateOutputWidth();
                       if (g_game_screen != null && g_game_screen.Length > 0)
                       {
                           Console.WriteLine($"[VDP-DEBUG] Filling g_game_screen with 0xFF000000 at frame {_frameCounter}, length={g_game_screen.Length}");
                           Array.Fill(g_game_screen, 0xFF000000u);
                       }
                   }
             
             if (prevH40 != newH40)
             {
                 ApplyHorizontalMode(newH40);
             }
             
             _reg12_latch_pending = false;
         }

        internal long FrameCounter => _frameCounter;
        internal byte InterlaceField => g_vdp_interlace_field;
        internal ushort GetSmsHvCounter() => get_vdp_hvcounter();

        internal byte LatchSmsVCounter()
        {
            _smsHvLatch = get_vdp_hvcounter();
            _smsHvLatchValid = true;
            return (byte)(_smsHvLatch >> 8);
        }

        internal byte ReadSmsHCounter()
        {
            if (_smsHvLatchValid)
            {
                _smsHvLatchValid = false;
                return (byte)(_smsHvLatch & 0xFF);
            }
            return (byte)(get_vdp_hvcounter() & 0xFF);
        }

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
            if (ForceHBlank)
                g_vdp_status_2_hbrank = 1;

            if (g_scanline == 0)
            {
                _smsBeWritesThisFrame = 0;
                _smsBfWritesThisFrame = 0;
                ClearVBlank();
                if (md_main.g_masterSystemMode)
                {
                    _smsLineCounter = _smsLineCounterReload;
                    EndSmsLine();   // seed next regs from current
                    BeginSmsLine(); // use current regs for line 0
                }
                rendering_line();
                if (md_main.g_masterSystemMode)
                    EndSmsLine();
                if (md_main.g_masterSystemMode)
                    SmsLineCounterTick();
                set_hinterrupt();
                interrupt_check();
            }
            else if (g_scanline < g_display_ysize)   // g_display_ysize finns i dina VDP-filer
            {
                if (md_main.g_masterSystemMode)
                    BeginSmsLine();
                rendering_line();
                if (md_main.g_masterSystemMode)
                    EndSmsLine();
                if (md_main.g_masterSystemMode)
                    SmsLineCounterTick();
                interrupt_check();
            }
            else if (g_scanline == g_display_ysize)
            {
                _smsBeWritesLastFrame = _smsBeWritesThisFrame;
                _smsBfWritesLastFrame = _smsBfWritesThisFrame;

                rendering_frame();
                interrupt_check();

                // Trigger VBlank once per frame at scanline g_display_ysize for all modes.
                TriggerVBlank();
            }
            else if (g_scanline == g_display_ysize + 1)
            {
                if (!md_main.g_masterSystemMode)
                    ClearZ80VBlankInterrupt();
            }
            else if (g_scanline == g_vertical_line_max - 1) // också definierad i VDP
            {
            // Keep VBlank flag until the next frame even when clearing per-line stats.
            if (g_vdp_interlace_mode == 0)
                g_vdp_status_4_frame = (byte)((g_vdp_status_4_frame == 0) ? 1 : 0);
            else
                g_vdp_status_4_frame = g_vdp_interlace_field;
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
            else if (g_vdp_interlace_mode == 2)
            {
                // Mode 2 (double resolution): double Y and add field bit
                int interlaceY = (MouseClickPosY << 1) | g_vdp_interlace_field;
                g_vdp_c00008_hvcounter = (ushort)(((MouseClickPosX >> 1) & 0x00ff)
                + ((interlaceY << 8) & 0xfe00)
                + (interlaceY & 0x0100));
            }
            else
            {
                // Mode 1: don't double Y, but still need field handling?
                // Actually for HV counter in mode 1, Y is not doubled
                g_vdp_c00008_hvcounter = (ushort)(((MouseClickPosX >> 1) & 0x00ff)
                + (MouseClickPosY << 8));
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
            if (g_vdp_interlace_mode == 2)
                return (scanline << 1) | g_vdp_interlace_field;
            // Mode 1: don't double scanlines, just return as-is
            // Field handling is done elsewhere (HV counter, etc.)
            return scanline;
        }

        private int GetRenderLine(int scanline)
        {
            if (g_vdp_interlace_mode == 0)
                return scanline;
            if (g_vdp_interlace_mode == 2)
            {
                if (InterlaceOutput == InterlaceOutputPolicy.SingleField)
                    return scanline << 1;
                return (scanline << 1) | g_vdp_interlace_field;
            }
            // Mode 1: don't double scanlines for rendering
            return scanline;
        }

        private int GetHScrollLine(int scanline)
        {
            if (g_vdp_interlace_mode == 2)
                return GetInterlaceLine(scanline) >> 1;
            return scanline;
        }

        private int GetCellHeightShift() => g_vdp_interlace_mode == 2 ? 4 : 3;

        private int GetCellHeightPixels() => g_vdp_interlace_mode == 2 ? 16 : 8;

        private int GetRowInCell(int lineInCell) => g_vdp_interlace_mode == 2 ? (lineInCell & 0x0f) : (lineInCell & 0x07);

        private ushort ReadVramWordRaw(int byteAddr)
        {
            int addr = byteAddr & 0xFFFE;
            return (ushort)((g_vram[addr] << 8) | g_vram[(addr + 1) & 0xFFFF]);
        }

        private void TraceNameTableRowDumpIfNeeded()
        {
            if (!TraceNameTableRowDump)
                return;

            if (g_scanline != 0 && g_scanline != 112)
                return;

            if (_lastNameTableDumpFrame == _frameCounter && _lastNameTableDumpScanline == g_scanline)
                return;

            _lastNameTableDumpFrame = _frameCounter;
            _lastNameTableDumpScanline = g_scanline;

            bool h40 = IsH40Mode();
            int mask = h40 ? 0xFE00 : 0xFC00;
            int planeWidthTiles = g_scroll_xcell;
            int rowStrideWords = planeWidthTiles;
            int rowIndex = (g_scanline >> GetCellHeightShift());

            int baseA = g_vdp_reg_2_scrolla & mask;
            int baseB = g_vdp_reg_4_scrollb & mask;
            int baseW = g_vdp_reg_3_windows & mask;
            int baseS = g_vdp_reg_5_sprite & (h40 ? ~0x3FF : ~0x1FF);
            int hscrollBase = g_vdp_reg_13_hscroll & 0xFC00;

            Console.WriteLine(
                $"[NT-REG] frame={_frameCounter} scanline={g_scanline} " +
                $"reg02=0x{g_vdp_reg[2]:X2} reg04=0x{g_vdp_reg[4]:X2} reg03=0x{g_vdp_reg[3]:X2} reg05=0x{g_vdp_reg[5]:X2} " +
                $"reg0B=0x{g_vdp_reg[11]:X2} reg0C=0x{g_vdp_reg[12]:X2} reg0D=0x{g_vdp_reg[13]:X2} " +
                $"reg0F=0x{g_vdp_reg[15]:X2} reg10=0x{g_vdp_reg[16]:X2} " +
                $"H40={(h40 ? 1 : 0)} widthTiles={planeWidthTiles} row={rowIndex}");

            Console.WriteLine(
                $"[NT-BASE] frame={_frameCounter} scanline={g_scanline} " +
                $"A=0x{baseA:X4} B=0x{baseB:X4} W=0x{baseW:X4} S=0x{baseS:X4} HS=0x{hscrollBase:X4}");

            Console.WriteLine(
                $"[VSRAM] frame={_frameCounter} scanline={g_scanline} mode={(g_vdp_reg_11_2_vscroll != 0 ? 1 : 0)} " +
                $"A0=0x{g_vsram[0]:X4} B0=0x{g_vsram[1]:X4} A1=0x{g_vsram[2]:X4} B1=0x{g_vsram[3]:X4}");

            DumpNameTableRow("A", baseA, rowIndex, rowStrideWords, planeWidthTiles);
            DumpNameTableRow("B", baseB, rowIndex, rowStrideWords, planeWidthTiles);

            DumpHScrollSample(hscrollBase);
        }

        private void DumpNameTableRow(string label, int baseAddr, int rowIndex, int rowStrideWords, int widthTiles)
        {
            int rowWordBase = (baseAddr >> 1) + (rowIndex * rowStrideWords);
            int maxWords = Math.Min(64, widthTiles);

            Console.Write($"[NT-{label}] base=0x{baseAddr:X4} row={rowIndex} stride={rowStrideWords} addr=0x{rowWordBase:X4}:");
            for (int i = 0; i < maxWords; i++)
            {
                int byteAddr = ((rowWordBase + i) << 1) & 0xFFFF;
                ushort raw = ReadVramWordRaw(byteAddr);
                Console.Write($" {raw:X4}");
            }
            Console.WriteLine();

            for (int i = 0; i < Math.Min(8, maxWords); i++)
            {
                int byteAddr = ((rowWordBase + i) << 1) & 0xFFFF;
                ushort raw = ReadVramWordRaw(byteAddr);
                uint cache = g_renderer_vram[rowWordBase + i];
                int tile = raw & 0x07FF;
                int pal = (raw >> 13) & 0x03;
                int prio = (raw >> 15) & 0x01;
                int hflip = (raw >> 11) & 0x01;
                int vflip = (raw >> 12) & 0x01;
                Console.WriteLine(
                    $"[NT-{label}-DEC] i={i} raw=0x{raw:X4} cache=0x{cache:X4} tile=0x{tile:X3} pal={pal} prio={prio} hf={hflip} vf={vflip}");
            }

            if (TracePatternTileDump)
            {
                int maxDump = Math.Min(4, maxWords);
                int[] nonZero = new int[maxWords];
                int nonZeroCount = 0;
                for (int i = 0; i < maxWords; i++)
                {
                    int byteAddr = ((rowWordBase + i) << 1) & 0xFFFF;
                    ushort raw = ReadVramWordRaw(byteAddr);
                    int tile = raw & 0x07FF;
                    if (tile != 0 && nonZeroCount < nonZero.Length)
                        nonZero[nonZeroCount++] = i;
                }
                if (nonZeroCount > 0)
                {
                    for (int idx = 0; idx < Math.Min(maxDump, nonZeroCount); idx++)
                    {
                        int i = nonZero[idx];
                        int byteAddr = ((rowWordBase + i) << 1) & 0xFFFF;
                        ushort raw = ReadVramWordRaw(byteAddr);
                        int tile = raw & 0x07FF;
                        DumpTilePattern(label, i, tile);
                    }
                }
                else
                {
                    for (int i = 0; i < maxDump; i++)
                    {
                        int byteAddr = ((rowWordBase + i) << 1) & 0xFFFF;
                        ushort raw = ReadVramWordRaw(byteAddr);
                        int tile = raw & 0x07FF;
                        DumpTilePattern(label, i, tile);
                    }
                }
            }
        }

        private void DumpHScrollSample(int hscrollBase)
        {
            int hscrollMode = g_vdp_reg_11_1_hscroll;
            int line = GetHScrollLine(g_scanline);
            int addr = hscrollBase;
            switch (hscrollMode)
            {
                case 2: addr += (line & 0xfff8) << 2; break;
                case 3: addr += line << 2; break;
            }
            int wordAddr = addr >> 1;
            ushort a0 = ReadVramWordRaw((wordAddr << 1) & 0xFFFF);
            ushort b0 = ReadVramWordRaw(((wordAddr + 1) << 1) & 0xFFFF);
            Console.WriteLine(
                $"[HSCROLL-SAMPLE] mode={hscrollMode} line={line} base=0x{hscrollBase:X4} addr=0x{addr:X4} " +
                $"A=0x{a0:X4} B=0x{b0:X4}");
        }

        private void DumpTilePattern(string label, int index, int tile)
        {
            int baseAddr = (tile & 0x07FF) << 5;
            int baseWord = (tile & 0x07FF) << 4;
            Console.WriteLine($"[TILE-{label}] i={index} tile=0x{tile:X3} base=0x{baseAddr:X4}");

            for (int y = 0; y < 8; y++)
            {
                char[] rowVram = new char[8];
                char[] rowCache = new char[8];
                for (int x = 0; x < 8; x++)
                {
                    int byteAddr = baseAddr + (y << 2) + ((x >> 2) << 1);
                    ushort word = ReadVramWordRaw(byteAddr);
                    int nib = (word >> ((3 - (x & 3)) << 2)) & 0x0F;
                    rowVram[x] = "0123456789ABCDEF"[nib];

                    int wordIndex = baseWord + (y << 1) + (x >> 2);
                    uint cacheWord = g_renderer_vram[wordIndex];
                    int cacheNib = (int)((cacheWord >> ((3 - (x & 3)) << 2)) & 0x0F);
                    rowCache[x] = "0123456789ABCDEF"[cacheNib];
                }
                Console.WriteLine(
                    $"[TILE-{label}-ROW] i={index} y={y} vram={new string(rowVram)} cache={new string(rowCache)}");
            }
        }

        private int GetTileWordBase(int tileIndex)
        {
            // Normal mode: 32 bytes per pattern (8x8 tiles)
            return (tileIndex & 0x07ff) << 4;
        }

        private int GetReversePage(uint reverse)
        {
            // Normal mode: reverse flag selects which page of pattern data
            return (int)(reverse * VRAM_DATASIZE);
        }

        private int GetRowWordOffset(int rowInCell, uint reverse)
        {
            int row = rowInCell;
            if (g_vdp_interlace_mode == 2 && (reverse & 0x02) != 0)
                row = 15 - row;
            return row << 1;
        }

        internal enum TileRebaseKind
        {
            None = 0,
            PlaneA = 1,
            PlaneB = 2,
            Window = 3
        }

        private int GetTileRebaseOffsetWords(TileRebaseKind rebaseKind)
        {
            if (g_vdp_reg_1_7_vram128 == 0)
                return 0;

            bool useRebase = rebaseKind switch
            {
                TileRebaseKind.PlaneA => g_vdp_reg_14_plane_a_rebase != 0,
                TileRebaseKind.PlaneB => g_vdp_reg_14_plane_b_rebase != 0,
                TileRebaseKind.Window => g_vdp_reg_14_plane_a_rebase != 0,
                _ => false
            };

            return useRebase ? VRAM_DATASIZE : 0; // 0x10000 bytes -> 0x8000 words
        }

        internal int GetTileWordAddress(int tileIndex, int rowInCell, uint reverse, TileRebaseKind rebaseKind = TileRebaseKind.None)
        {
            int rebaseOffset = GetTileRebaseOffsetWords(rebaseKind);
            if (g_vdp_interlace_mode == 2)
            {
                // In interlace mode 2, tile patterns are 8x16 (16 rows, 64 bytes each)
                //
                // pattern_chk stores at: w_address >> 1 = (tileIndex*64 + row*4) / 2 = tileIndex*32 + row*2
                //
                // So GetTileWordAddress must return: tileIndex*32 + row*2

                // 32 words per pattern (64 bytes / 2)
                int patternBase = (tileIndex & 0x3FF) << 5;

                // Row offset: 2 words per row (4 bytes / 2), with vertical flip support.
                int rowOffset = GetRowWordOffset(rowInCell, reverse);

                // Use the same reverse pages as normal mode for horizontal flip support.
                return GetReversePage(reverse) + rebaseOffset + patternBase + rowOffset;
            }

            // Normal mode: use the standard formula
            return GetReversePage(reverse) + rebaseOffset + GetTileWordBase(tileIndex) + GetRowWordOffset(rowInCell, reverse);
        }

        private int GetSpriteTableBase() => g_vdp_reg_5_sprite & (IsH40Mode() ? ~0x3FF : ~0x1FF);

        private int GetSpriteTableSize() => IsH40Mode() ? 0x400 : 0x200;

        private void EnsureSpriteTableCache()
        {
            int baseAddr = GetSpriteTableBase();
            int size = GetSpriteTableSize();
            if (g_sprite_cache_base == baseAddr && g_sprite_cache_size == size && g_sprite_table_cache.Length == size)
                return;

            g_sprite_cache_base = baseAddr;
            g_sprite_cache_size = size;
            g_sprite_table_cache = new byte[size];
            for (int i = 0; i < size; i++)
                g_sprite_table_cache[i] = g_vram[(baseAddr + i) & 0xffff];

            g_sprite_row_cache_dirty = true;
            g_sprite_row_cache_field = -1;
        }

        private void RefreshSpriteTableCache()
        {
            if (g_sprite_cache_base < 0 || g_sprite_cache_size <= 0 || g_sprite_table_cache.Length == 0)
                return;

            for (int i = 0; i < g_sprite_cache_size; i++)
                g_sprite_table_cache[i] = g_vram[(g_sprite_cache_base + i) & 0xffff];
        }

        private void InvalidateSpriteRowCache()
        {
            g_sprite_row_cache_dirty = true;
            g_sprite_row_cache_field = -1;
        }

        private ushort SpriteCacheReadWord(int offset)
        {
            if ((uint)offset >= (uint)g_sprite_table_cache.Length || (uint)(offset + 1) >= (uint)g_sprite_table_cache.Length)
                return 0;
            return (ushort)((g_sprite_table_cache[offset] << 8) | g_sprite_table_cache[offset + 1]);
        }

        private static int DivCeil(int numerator, int denominator)
        {
            if (denominator <= 0)
                return 0;
            if (numerator <= 0)
                return 0;
            return (numerator + denominator - 1) / denominator;
        }

        private ushort ReadVramWordAligned(int addr)
        {
            addr &= 0xFFFE;
            return (ushort)((g_vram[addr] << 8) | g_vram[addr ^ 1]);
        }

        private void UpdateSpriteRowCacheIfNeeded()
        {
            EnsureSpriteTableCache();

            if (!g_sprite_row_cache_dirty)
                return;

            RefreshSpriteTableCache();
            g_sprite_row_cache_dirty = false;
            g_sprite_row_cache_field = -1;

            int lineCount = g_sprite_row_cache.Length;
            for (int i = 0; i < lineCount; i++)
            {
                g_sprite_row_cache[i].Count = 0;
                g_sprite_row_cache[i].TotalSprites = 0;
                g_sprite_row_cache[i].TotalCells = 0;
                g_sprite_row_cache[i].Overflow = false;
            }

            int tileHeightShift = GetCellHeightShift();
            int blankLines = 128 << (g_vdp_interlace_mode == 2 ? 1 : 0);
            int screenLineLimit = blankLines + (g_display_ycell << tileHeightShift);
            int spritesRemaining = g_max_sprite_num;
            int spriteIndex = 0;

            do
            {
                int addr = spriteIndex << 3;
                ushort w_val1 = SpriteCacheReadWord(addr);
                ushort w_val2 = SpriteCacheReadWord(addr + 2);

                int spriteY = w_val1 & g_sprite_vmask;
                int heightTiles = ((w_val2 >> 8) & 0x0003) + 1;
                int widthTiles = ((w_val2 >> 10) & 0x0003) + 1;

                int startLine = Math.Max(blankLines, spriteY);
                int endLineExclusive = Math.Min(screenLineLimit, spriteY + (heightTiles << tileHeightShift));

                for (int line = startLine; line < endLineExclusive; line++)
                {
                    int rowIndex = line - blankLines;
                    if ((uint)rowIndex >= (uint)g_sprite_row_cache.Length)
                        continue;
                    ref SpriteRowCacheRow row = ref g_sprite_row_cache[rowIndex];
                    row.TotalSprites++;
                    row.TotalCells += widthTiles;
                    if (row.TotalSprites > g_max_sprite_line || row.TotalCells > g_max_sprite_cell)
                        row.Overflow = true;
                    if (row.Count >= g_max_sprite_line)
                        continue;

                    int slot = row.Count++;
                    row.SpriteIndices[slot] = (byte)spriteIndex;
                    row.YInSprite[slot] = (byte)(line - spriteY);
                    row.Width[slot] = (byte)widthTiles;
                    row.Height[slot] = (byte)heightTiles;
                }

                if (SpriteLinkSequential)
                {
                    spriteIndex++;
                    if (spriteIndex >= g_max_sprite_num)
                        break;
                }
                else
                {
                    int link = w_val2 & 0x007f;
                    if (link >= g_max_sprite_num)
                        break;
                    spriteIndex = link;
                }
            }
            while (spriteIndex != 0 && --spritesRemaining != 0);
        }

        private void EvaluateSpriteOverflowForLine(int scanline)
        {
            UpdateSpriteRowCacheIfNeeded();

            int lineIndex = (g_vdp_interlace_mode == 2)
                ? ((scanline << 1) | g_vdp_interlace_field)
                : scanline;
            if ((uint)lineIndex >= (uint)g_sprite_row_cache.Length)
                return;

            ref SpriteRowCacheRow row = ref g_sprite_row_cache[lineIndex];
            if (row.Overflow)
                g_vdp_status_6_sprite = 1;

            if (!TraceSpriteOverflowFrame)
                return;

            if (_spriteOverflowLastFrame != _frameCounter || scanline == 0)
            {
                _spriteOverflowLastFrame = _frameCounter;
                _spriteOverflowMaxSprites = 0;
                _spriteOverflowMaxCells = 0;
                _spriteOverflowAny = false;
            }

            if (row.TotalSprites > _spriteOverflowMaxSprites)
                _spriteOverflowMaxSprites = row.TotalSprites;
            if (row.TotalCells > _spriteOverflowMaxCells)
                _spriteOverflowMaxCells = row.TotalCells;
            if (row.Overflow)
                _spriteOverflowAny = true;

            if (scanline == g_display_ysize - 1)
            {
                Console.WriteLine(
                    $"[SPRITE-OVERFLOW] frame={_frameCounter} maxSprites={_spriteOverflowMaxSprites} maxCells={_spriteOverflowMaxCells} overflow={(_spriteOverflowAny ? 1 : 0)}");
            }
        }

        private int GetOutputHeight()
        {
            if (md_main.g_masterSystemMode && !ShowOverscan)
                return g_display_ysize;

            int height = g_display_ysize;
            // Only double height for interlace mode 2 (double resolution)
            // Mode 1 (standard interlace) should NOT double the height
            bool shouldDouble = g_vdp_interlace_mode == 2 && InterlaceOutput == InterlaceOutputPolicy.DoubleField;
            if (shouldDouble)
                height = g_display_ysize * 2;
            
            if (TraceVdpInterlace || shouldDouble)
            {
                Console.WriteLine($"[VDP-OUTPUT-HEIGHT-DETAIL] g_display_ysize={g_display_ysize} g_vdp_interlace_mode={g_vdp_interlace_mode} InterlaceOutput={InterlaceOutput} shouldDouble={shouldDouble} => {height}");
            }
            
            return height;
        }

        private int GetOutputLineForScanline(int scanline)
        {
            // Only apply field interlace for mode 2 (double resolution) when DoubleField policy is used
            // Mode 1 should not double scanlines
            if (g_vdp_interlace_mode == 2 && InterlaceOutput == InterlaceOutputPolicy.DoubleField)
            {
                if (ForceInterlaceBob && g_vdp_interlace_mode == 2)
                    return scanline << 1;
                return (scanline << 1) | g_vdp_interlace_field;
            }
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
            g_vdp_status_4_frame = g_vdp_interlace_field;
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
                Console.WriteLine($"[VDP-INTERLACE] Parsed InterlaceOutputPolicy: SingleField (raw='{raw}')");
                return InterlaceOutputPolicy.SingleField;
            }
            Console.WriteLine($"[VDP-INTERLACE] Parsed InterlaceOutputPolicy: DoubleField (raw='{raw}')");
            return InterlaceOutputPolicy.DoubleField;
        }

        private static bool TryParseVramRange(string? raw, out int start, out int end)
        {
            start = 0;
            end = 0;
            if (string.IsNullOrWhiteSpace(raw))
                return false;

            string trimmed = raw.Trim();
            int sep = trimmed.IndexOf(':');
            if (sep < 0)
                sep = trimmed.IndexOf('-');
            if (sep <= 0 || sep >= trimmed.Length - 1)
                return false;

            string left = trimmed.Substring(0, sep);
            string right = trimmed.Substring(sep + 1);
            if (!TryParseHexU16(left, out start) || !TryParseHexU16(right, out end))
                return false;

            if (end < start)
            {
                int tmp = start;
                start = end;
                end = tmp;
            }
            return true;
        }

        private static int ParseTraceLimit(string name, int fallback)
        {
            string? raw = Environment.GetEnvironmentVariable(name);
            if (string.IsNullOrWhiteSpace(raw))
                return fallback;
            if (!int.TryParse(raw.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int value))
                return fallback;
            if (value <= 0)
                return int.MaxValue;
            return value;
        }

        private static bool TryParseHexU16(string token, out int value)
        {
            value = 0;
            if (string.IsNullOrWhiteSpace(token))
                return false;

            string trimmed = token.Trim();
            if (trimmed.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                trimmed = trimmed.Substring(2);

            if (!int.TryParse(trimmed, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value))
                return false;

            value &= 0xFFFF;
            return true;
        }

        private void MaybeLogVdpState()
        {
            if (!TraceVdpState)
                return;
            if (_frameCounter - _lastStateLogFrame < 60)
                return;
            _lastStateLogFrame = _frameCounter;

            int planeA = (g_vdp_reg_2_scrolla & (IsH40Mode() ? 0xFE00 : 0xFC00)) >> 1;
            int planeB = (g_vdp_reg_4_scrollb & (IsH40Mode() ? 0xFE00 : 0xFC00)) >> 1;
            int window = (g_vdp_reg_3_windows & (IsH40Mode() ? 0xFE00 : 0xFC00)) >> 1;
            int sprite = (g_vdp_reg_5_sprite & (IsH40Mode() ? ~0x3FF : ~0x1FF)) >> 1;
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
            if (!md_main.g_masterSystemMode)
                return;

            bool dumpRequested = SmsVramDumpFrame >= 0 || SmsNameTableDumpFrame >= 0 || _smsVramDumpOnHashChangesLeft > 0 || TraceSmsFrameDetail;
            if (!MdTracerCore.MdLog.Enabled && !dumpRequested)
                return;

            _smsFrameHashCounter++;
            bool isHashSampleFrame = (_smsFrameHashCounter % 60) == 0;

            if (g_game_screen == null || g_game_screen.Length == 0)
                return;

            int step = Math.Max(1, g_game_screen.Length / 64);
            uint hash = 0;
            for (int i = 0; i < g_game_screen.Length; i += step)
            {
                hash ^= g_game_screen[i];
                hash = (hash << 3) | (hash >> 29);
            }

            if (isHashSampleFrame && hash != _smsLastFrameHash)
            {
                _smsLastFrameHash = hash;
                MdTracerCore.MdLog.WriteLine($"[SMS VDP] framebuffer hash=0x{hash:X8}");
                if (TraceSmsFrameDetail)
                    LogSmsFrameDetail(hash);
                if (_smsVramDumpOnHashChangesLeft > 0)
                {
                    string path = $"{SmsVramDumpOnHashPrefix}_{_frameCounter}.txt";
                    DumpSmsVram(path);
                    _smsVramDumpOnHashChangesLeft--;
                }
            }

            if (!_smsVramDumped && SmsVramDumpFrame >= 0 && _frameCounter >= SmsVramDumpFrame)
            {
                _smsVramDumped = true;
                DumpSmsVram(SmsVramDumpPath);
            }

            if (!_smsNameTableDumped && SmsNameTableDumpFrame >= 0 && _frameCounter >= SmsNameTableDumpFrame)
            {
                _smsNameTableDumped = true;
                DumpSmsNameTable(SmsNameTableDumpPath);
            }

            if (isHashSampleFrame)
                SmsLogFrameSummary(hash);
        }

        private uint ComputeSmsVramRegionHash(int start, int length)
        {
            if (start < 0 || length <= 0)
                return 0;
            int end = Math.Min(_smsVram.Length, start + length);
            if (end <= start)
                return 0;
            uint h = 0;
            for (int i = start; i < end; i++)
            {
                h ^= _smsVram[i];
                h = (h << 5) | (h >> 27);
            }
            return h;
        }

        private void LogSmsFrameDetail(uint hash)
        {
            int nameBase = (_smsRegs[2] & 0x0F) << 10;
            int nameLength = (g_display_ysize > 192) ? (32 * 32 * 2) : (32 * 28 * 2); // 0x800 or 0x700
            if (g_display_ysize > 192)
                nameBase = (nameBase & 0xF000) | 0x0700;
            else
                nameBase &= 0xF800;
            int satBase = (_smsRegs[5] & 0x7E) << 7;
            int spritePatternBase = ((_smsRegs[6] & 0x04) != 0) ? 0x2000 : 0x0000;
            int hscroll = _smsRegs[8];
            int vscroll = _smsRegs[9];
            bool hscrollLock = (_smsRegs[0] & 0x40) != 0;
            bool vscrollLock = (_smsRegs[0] & 0x80) != 0;
            bool hideLeftColumn = (_smsRegs[0] & 0x20) != 0;
            bool sprites8x16 = (_smsRegs[1] & 0x02) != 0;

            uint hashPattern = ComputeSmsVramRegionHash(0x0000, 0x2000);
            uint hashUpper = ComputeSmsVramRegionHash(0x2000, 0x2000);
            uint hashName = ComputeSmsVramRegionHash(nameBase, nameLength);
            uint hashSat = ComputeSmsVramRegionHash(satBase & 0x3FFF, 0x100);
            uint hash3c = ComputeSmsVramRegionHash(0x3C00, 0x200);
            uint hash3e = ComputeSmsVramRegionHash(0x3E00, 0x100);

            MdTracerCore.MdLog.WriteLine(
                $"[SMS VDP] detail frame={_frameCounter} fbHash=0x{hash:X8} " +
                $"r0=0x{_smsRegs[0]:X2} r1=0x{_smsRegs[1]:X2} r2=0x{_smsRegs[2]:X2} r5=0x{_smsRegs[5]:X2} r6=0x{_smsRegs[6]:X2} r7=0x{_smsRegs[7]:X2} " +
                $"r8=0x{_smsRegs[8]:X2} r9=0x{_smsRegs[9]:X2} " +
                $"nameBase=0x{nameBase:X4} satBase=0x{satBase:X4} spritePatternBase=0x{spritePatternBase:X4} " +
                $"hscroll={hscroll} vscroll={vscroll} hLock={(hscrollLock ? 1 : 0)} vLock={(vscrollLock ? 1 : 0)} hideLeft={(hideLeftColumn ? 1 : 0)} spr16={(sprites8x16 ? 1 : 0)} " +
                $"vramHash[0-1FFF]=0x{hashPattern:X8} vramHash[2000-3FFF]=0x{hashUpper:X8} " +
                $"nameHash=0x{hashName:X8} satHash=0x{hashSat:X8} hash3C=0x{hash3c:X8} hash3E=0x{hash3e:X8}");
        }

        private void DumpSmsVram(string path)
        {
            try
            {
                using var writer = new StreamWriter(path, false);
                writer.WriteLine($"SMS VRAM dump frame={_frameCounter}");
                writer.WriteLine($"reg2=0x{_smsRegs[2]:X2} reg4=0x{_smsRegs[4]:X2} reg1=0x{_smsRegs[1]:X2}");
                for (int addr = 0; addr < _smsVram.Length; addr += 16)
                {
                    writer.Write($"{addr:X4}:");
                    for (int i = 0; i < 16 && addr + i < _smsVram.Length; i++)
                        writer.Write($" {_smsVram[addr + i]:X2}");
                    writer.WriteLine();
                }
                writer.Flush();
                MdTracerCore.MdLog.WriteLine($"[SMS VRAM-DUMP] wrote {path}");
            }
            catch (Exception ex)
            {
                MdTracerCore.MdLog.WriteLine($"[SMS VRAM-DUMP] failed: {ex.Message}");
            }
        }

        private void DumpSmsNameTable(string path)
        {
            try
            {
            int nameBase = (_smsRegs[2] & 0x0F) << 10;
            int nameLength = (g_display_ysize > 192) ? (32 * 32 * 2) : (32 * 28 * 2); // 0x800 or 0x700
            if (g_display_ysize > 192)
                nameBase = (nameBase & 0xF000) | 0x0700;
            else
                nameBase &= 0xF800;
                int nameEnd = Math.Min(_smsVram.Length, nameBase + nameLength);

                using var writer = new StreamWriter(path, false);
                writer.WriteLine($"SMS NT dump frame={_frameCounter}");
                writer.WriteLine($"targetFrame={SmsNameTableDumpFrame}");
                writer.WriteLine($"reg2=0x{_smsRegs[2]:X2} reg4=0x{_smsRegs[4]:X2} reg1=0x{_smsRegs[1]:X2}");
                writer.WriteLine($"nameBase=0x{nameBase:X4} length=0x{nameLength:X3}");
                for (int addr = nameBase; addr + 1 < nameEnd; addr += 2)
                {
                    int wordIndex = (addr - nameBase) >> 1;
                    ushort entry = (ushort)(_smsVram[addr] | (_smsVram[addr + 1] << 8));
                    if ((wordIndex & 0x1F) == 0)
                        writer.Write($"{wordIndex:X3}:");
                    writer.Write($" {entry:X4}");
                    if ((wordIndex & 0x1F) == 0x1F)
                        writer.WriteLine();
                }
                writer.WriteLine();
                writer.Flush();
                MdTracerCore.MdLog.WriteLine($"[SMS NT-DUMP] wrote {path}");
            }
            catch (Exception ex)
            {
                MdTracerCore.MdLog.WriteLine($"[SMS NT-DUMP] failed: {ex.Message}");
            }
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

        // Public method to track VRAM writes from memory layer
        public void TrackVramWrite(int address, ushort value)
        {
            _mdVramWritesTotal++;
            _mdVramWritesThisFrame++;

            // Check if write is to name table area (scroll plane A or B)
            int scrollA_base = g_vdp_reg_2_scrolla & 0xFFFE;
            int scrollB_base = g_vdp_reg_4_scrollb & 0xFFFE;

            bool isNameTableWrite = (address >= scrollA_base && address < scrollA_base + 0x2000) ||
                                    (address >= scrollB_base && address < scrollB_base + 0x1000) ||
                                    (address >= 0xC000 && address < 0xE000) ||  // Common Scroll A
                                    (address >= 0xE000 && address < 0xF000);   // Common Scroll B

            if (isNameTableWrite)
            {
                _mdVramWritesToNameTablesTotal++;
                _mdVramWritesToNameTablesThisFrame++;

                // Log first few name table writes
                if (TraceVramName && !_mdVramNameTableWriteLogged && _mdVramWritesToNameTablesTotal <= 32)
                {
                    Console.WriteLine($"[VRAM-NAME] frame={_frameCounter} addr=0x{address:X4} val=0x{value:X4} " +
                        $"scrollA=0x{scrollA_base:X4} scrollB=0x{scrollB_base:X4} interlace={g_vdp_interlace_mode}");
                }
            }
        }

        // Wrapper for calling from memory layer (avoids circular reference issues)
        internal void RecordVramWriteForTracking(int address, ushort value)
        {
            TrackVramWrite(address, value);
        }

        // Track writes to specific scroll regions (for debugging DMA-FILL issues)
        private int _writesToScrollAThisFrame;
        private int _writesToScrollBThisFrame;
        private bool _loggedPostDmaWrites;
        private int _vramWriteLogCount;
        private bool _dmaFillLogged;

        // Detailed tracking for all VRAM writes to scroll regions
        public void LogVramWrite(string source, int address, ushort value, int autoInc, int vdpCode)
        {
            if (TraceVramRangeEnabled && address >= _traceVramRangeStart && address <= _traceVramRangeEnd)
            {
                if (TraceVramRangeSkipFill && source == "DMA-FILL")
                    return;
                if (_traceVramRangeLogCount < TraceVramRangeLimit)
                {
                    uint pc = md_main.g_md_m68k != null ? md_m68k.g_reg_PC : 0u;
                    uint dmaSrc = g_dma_src_addr;
                    Console.WriteLine($"[VRAM-RANGE] frame={_frameCounter} source={source} pc=0x{pc:X6} dmaSrc=0x{dmaSrc:X6} " +
                        $"addr=0x{address:X4} val=0x{value:X4} inc=0x{autoInc:X2} code=0x{vdpCode:X2}");
                    _traceVramRangeLogCount++;
                }
            }

            // Check if address is in scroll regions
            bool inScrollA = address >= 0xC000 && address < 0xE000;
            bool inScrollB = address >= 0xE000 && address < 0x10000;
            bool inAltScrollB = address >= 0xA000 && address < 0xC000;

            if (TraceVramWriteDetail && (inScrollA || inScrollB || inAltScrollB))
            {
                if (_vramWriteLogCount < 100)
                {
                    string region = inScrollA ? "ScrollA" : (inScrollB ? "ScrollB" : "AltScrollB");
                    Console.WriteLine($"[VRAM-WRITE-DETAIL] frame={_frameCounter} source={source} {region} addr=0x{address:X4} val=0x{value:X4} inc={autoInc} code=0x{vdpCode:X2}");
                    _vramWriteLogCount++;
                }

                // After DMA-FILL, watch for changes (debug)
                // if (_frameCounter >= 1148 && _frameCounter < 1200)
                // {
                //     Console.WriteLine($"[VRAM-WATCH] frame={_frameCounter} {source} write to 0x{address:X4} value=0x{value:X4}");
                // }
            }
        }

        // Track writes to specific scroll regions
        public void TrackScrollRegionWrite(int address)
        {
            // Scroll A: 0xC000-0xDFFF (typical)
            // Scroll B: 0xE000-0xFFFF (typical)
            if (address >= 0xC000 && address < 0xE000)
            {
                _writesToScrollAThisFrame++;
            }
            else if (address >= 0xE000 && address < 0x10000)
            {
                _writesToScrollBThisFrame++;
            }
        }

        // Public method to track VRAM clears
        public void TrackVramClear(string reason)
        {
            if (!_mdVramClearLogged)
            {
                Console.WriteLine($"[VRAM-CLEAR] frame={_frameCounter} reason={reason}");
                _mdVramClearLogged = true;
            }
        }

        private void TriggerVBlank()
        {
            if (_vblankActive)
                return;

            _vblankActive = true;
            g_vdp_status_3_vbrank = 1;
            if (md_main.g_masterSystemMode)
                UpdateSmsIrqLine();

            if (md_main.g_masterSystemMode &&
                string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_SMS_IRQSTATE"), "1", StringComparison.Ordinal))
            {
                bool vblankEnabled = (_smsRegs[1] & 0x20) != 0;
                bool lineEnabled = (_smsRegs[0] & 0x10) != 0;
                Console.WriteLine(
                    $"[SMS IRQSTATE] frame={_frameCounter} line={g_scanline} reg0=0x{_smsRegs[0]:X2} reg1=0x{_smsRegs[1]:X2} vblankEn={(vblankEnabled ? 1 : 0)} lineEn={(lineEnabled ? 1 : 0)}");
            }
             
              // Apply latched register 12 values (takes effect at V-Int)
              if (_reg12_latch_pending)
              {
                  ApplyLatchedRegister12();
              }

               // [VDP-FRAME] per-frame summary (gated)
               if (TraceVdpFrameSummary)
               {
                   byte reg12Data = (byte)((g_vdp_reg_12_7_cellmode1 << 7) | (g_vdp_reg_12_3_shadow << 3) | (g_vdp_reg_12_2_interlacemode << 1) | g_vdp_reg_12_0_cellmode2);
                   int width = IsH40Mode() ? 320 : 256;
                   byte reg1Display = (byte)((g_vdp_reg[1] >> 6) & 0x01); // bit 6 = display enable
                    int reg2Base = g_vdp_reg_2_scrolla;
                   int reg4Base = g_vdp_reg_4_scrollb;
                   byte reg16PlaneSize = g_vdp_reg[16];
                   
                   Console.WriteLine($"[VDP-FRAME] frame={_frameCounter} scanline={g_scanline} reg12=0x{reg12Data:X2} width={width} reg1.display={reg1Display} reg2.base=0x{reg2Base:X4} reg4.base=0x{reg4Base:X4} reg16=0x{reg16PlaneSize:X2}");
               }
              
               // REMOVED: Sonic 2 special stage hack that was causing H32 mode in all games at frame 4910
               // This hack forced register 12 to 0x08 (H32 mode, shadow ON) and display ON
               // It was triggering in ALL games, not just Sonic 2, causing resolution bugs

              // [BD] backdrop logging gated by EUTHERDRIVE_DEBUG_BD=1
              if (Environment.GetEnvironmentVariable("EUTHERDRIVE_DEBUG_BD") == "1")
              {
                  byte backdropIndex = g_vdp_reg[7]; // reg7 backdrop index (0-63)
                  uint cramValue = g_color[backdropIndex]; // CRAM value (ARGB format)
                  Console.WriteLine($"[BD] frame={_frameCounter} backdropIndex={backdropIndex} cram=0x{cramValue:X8}");
                  
                  // Also check palette entry 0 directly
                  uint palette0 = g_color[0];
                  uint shadowPalette0 = g_color_shadow[0];
                  uint highlightPalette0 = g_color_highlight[0];
                  Console.WriteLine($"[PALETTE0] frame={_frameCounter} g_color[0]=0x{palette0:X8} shadow[0]=0x{shadowPalette0:X8} highlight[0]=0x{highlightPalette0:X8}");
              }

              // [HSCROLL-REG] logging gated by EUTHERDRIVE_DEBUG_HSCROLL=1
              if (Environment.GetEnvironmentVariable("EUTHERDRIVE_DEBUG_HSCROLL") == "1")
              {
                  byte reg11 = g_vdp_reg[11]; // HScroll mode
                  byte hscrollMode = g_vdp_reg_11_1_hscroll; // HScroll mode bits 0-1
                  byte vscrollMode = g_vdp_reg_11_2_vscroll; // VScroll mode bit 2
                  byte extMode = g_vdp_reg_11_3_ext; // EXT mode bit 3
                  int reg13 = g_vdp_reg_13_hscroll; // HScroll base
                  int hscrollBase = reg13; // computed base address
                  Console.WriteLine($"[HSCROLL-REG] frame={_frameCounter} reg11=0x{reg11:X2} hscrollMode={hscrollMode} vscrollMode={vscrollMode} extMode={extMode} reg13=0x{reg13:X4} base=0x{hscrollBase:X4}");
              }

              // [HSCROLL] dump for debugging
              if (Environment.GetEnvironmentVariable("EUTHERDRIVE_DEBUG_HSCROLL") == "1") // && _frameCounter >= 4904 && _frameCounter <= 4910
              {
                  // Dump HScroll values for y=0..31
                  for (int y = 0; y < 32; y++)
                  {
                      // Get HScroll mode from register 11 bits 0-1
                      byte hscrollMode = g_vdp_reg_11_1_hscroll;
                      int hscrollBase = g_vdp_reg_13_hscroll;
                      
                      // Calculate address based on mode (matching renderer logic)
                      int w_addr = hscrollBase;
                      switch (hscrollMode)
                      {
                          case 2: w_addr += (y & 0xfff8) << 2; break; // per-8-line
                          case 3: w_addr += y << 2; break; // per-line
                          // Mode 0 and 1: full-screen or other, use base address directly
                      }
                      w_addr >>= 1; // Convert to word address
                      
                      // Read HScroll A and B values from VRAM
                      int hscrollA = 0;
                      int hscrollB = 0;
                      if (w_addr >= 0 && w_addr < 0x8000) // VRAM size in words
                      {
                          int byteAddr = w_addr * 2;
                          if (byteAddr < 0x10000)
                          {
                              hscrollA = (g_vram[byteAddr] << 8) | g_vram[(byteAddr + 1) & 0xFFFF];
                              hscrollB = (g_vram[byteAddr + 2] << 8) | g_vram[(byteAddr + 3) & 0xFFFF];
                          }
                      }
                      
                      Console.WriteLine($"[HSCROLL] frame={_frameCounter} y={y} mode={hscrollMode} base=0x{hscrollBase:X4} addr=0x{w_addr:X4} A=0x{hscrollA:X4} B=0x{hscrollB:X4}");
                  }
              }

            // Log name table non-zero counts at VBlank (once per second approx)
            if (TraceNameTable)
                LogNameTableStats();

            // Log VRAM page stats (brute force - top 3 pages with most non-zero tiles)
            if (TraceVramPageStats)
                LogVramPageStats();

            // Log register snapshot in interlace mode 2
            LogInterlaceRegisterSnapshot();

            // Per-frame write summary + VRAM page histogram after interlace activation
            LogInterlaceWriteSummary();

            // DMA status + CPU data-port write stats around interlace frame window
            LogDmaStatusWindow();

            // Log VRAM write summary at VBlank (once per second approx)
            if (TraceVramStats && _frameCounter % 60 == 0 && (_mdVramWritesThisFrame > 0 || _mdVramWritesToNameTablesThisFrame > 0))
            {
                Console.WriteLine($"[VRAM-STATS] frame={_frameCounter} interlace={g_vdp_interlace_mode} vblank=1 display={g_vdp_reg_1_6_display} " +
                    $"vramWrites={_mdVramWritesThisFrame} nameTableWrites={_mdVramWritesToNameTablesThisFrame} dropped={_mdVramWritesDroppedThisFrame} " +
                    $"totalNameTable={_mdVramWritesToNameTablesTotal} totalVram={_mdVramWritesTotal}");
            }

            // Reset per-frame counters
            _mdCtrlWritesThisFrame = 0;
            _mdDataWritesThisFrame = 0;
            _mdCramWritesThisFrame = 0;
            _mdVramWritesThisFrame = 0;
            _mdVramWritesToNameTablesThisFrame = 0;
            _mdVramWritesDroppedThisFrame = 0;
            _mdDataWritesDroppedThisFrame = 0;
            ResetVramWriteCounters();
            Array.Clear(_mdDataWriteCodeCounts, 0, _mdDataWriteCodeCounts.Length);

            if (g_vdp_status_7_vinterrupt == 0)
            {
                g_vdp_status_7_vinterrupt = 1;
                md_m68k.g_interrupt_V_req = true;
                // Z80 needs VBlank interrupt for sound drivers in both MD and SMS mode
                if (md_main.g_masterSystemMode)
                {
                    UpdateSmsIrqLine();
                }
                else
                {
                    md_main.g_md_z80?.irq_request(true, "VDP", 0);
                    if (Z80VblankIrqPulseCycles > 0)
                        md_main.g_md_z80?.ArmIrqAutoClear("VDP", Z80VblankIrqPulseCycles);
                }
                _z80VblankIntActive = true;
                if (TraceVint)
                    Console.WriteLine($"[VINT] TriggerVBlank frame={_frameCounter} scanline={g_scanline} interlace={g_vdp_interlace_mode} field={g_vdp_interlace_field}");
            }
            LogTriggerVBlank();
            LogVBlankEdge(1);
        }

        // Count non-zero words and non-zero tile indices in scroll name tables
        private void LogNameTableStats()
        {
            if (_frameCounter % 60 != 0)
                return;

            // Read from actual VRAM, not renderer cache
            int scrollA_base = g_vdp_reg_2_scrolla & 0xFFFE;
            int scrollB_base = g_vdp_reg_4_scrollb & 0xFFFE;

            int countNonZeroA = 0;
            int countNonZeroTileA = 0;
            int countNonZeroB = 0;
            int countNonZeroTileB = 0;

            int entries = g_scroll_xcell * g_scroll_ycell;
            if (entries <= 0)
                entries = 64;
            if (entries > 0x2000 / 2)
                entries = 0x2000 / 2;

            // Count words from each scroll table
            for (int i = 0; i < entries; i++)
            {
                // Read word from VRAM (handle byte-swap)
                int addrA = (scrollA_base + (i << 1)) & 0xFFFE;
                int addrB = (scrollB_base + (i << 1)) & 0xFFFE;
                ushort wordA = (ushort)((g_vram[addrA] << 8) | g_vram[addrA ^ 1]);
                ushort wordB = (ushort)((g_vram[addrB] << 8) | g_vram[addrB ^ 1]);

                if (wordA != 0)
                {
                    countNonZeroA++;
                    if ((wordA & 0x07FF) != 0)
                        countNonZeroTileA++;
                }
                if (wordB != 0)
                {
                    countNonZeroB++;
                    if ((wordB & 0x07FF) != 0)
                        countNonZeroTileB++;
                }
            }

            // Probe: show first non-zero tile if any
            ushort firstNonZeroA = 0;
            int firstNonZeroAddrA = 0;
            int firstNonZeroTileA = 0;
            for (int i = 0; i < entries; i++)
            {
                int addr = (scrollA_base + (i << 1)) & 0xFFFE;
                ushort word = (ushort)((g_vram[addr] << 8) | g_vram[addr ^ 1]);
                if (word != 0)
                {
                    firstNonZeroA = word;
                    firstNonZeroAddrA = addr;
                    firstNonZeroTileA = word & 0x07FF;
                    break;
                }
            }

            ushort firstNonZeroB = 0;
            int firstNonZeroAddrB = 0;
            int firstNonZeroTileB = 0;
            for (int i = 0; i < entries; i++)
            {
                int addr = (scrollB_base + (i << 1)) & 0xFFFE;
                ushort word = (ushort)((g_vram[addr] << 8) | g_vram[addr ^ 1]);
                if (word != 0)
                {
                    firstNonZeroB = word;
                    firstNonZeroAddrB = addr;
                    firstNonZeroTileB = word & 0x07FF;
                    break;
                }
            }

            Console.WriteLine($"[NAME-TABLE] frame={_frameCounter} interlace={g_vdp_interlace_mode} " +
                $"scrollA_base=0x{scrollA_base:X4} scrollB_base=0x{scrollB_base:X4} " +
                $"nonZeroA={countNonZeroA}/{entries} tileA={countNonZeroTileA} " +
                $"nonZeroB={countNonZeroB}/{entries} tileB={countNonZeroTileB} " +
                $"writes={_mdVramWritesThisFrame} " +
                (firstNonZeroA != 0 ? $"probeA=addr0x{firstNonZeroAddrA:X4} word=0x{firstNonZeroA:X4} tile={firstNonZeroTileA}" : "probeA=none") +
                " " +
                (firstNonZeroB != 0 ? $"probeB=addr0x{firstNonZeroAddrB:X4} word=0x{firstNonZeroB:X4} tile={firstNonZeroTileB}" : "probeB=none"));
        }

        // VRAM truth test for Sonic 2 debugging
        private void LogVramTruthTest()
        {
            if (_frameCounter < 4910 || _frameCounter > 4920)
                return;
            
            // Check pattern region (tile patterns)
            int patternNonZero = 0;
            int patternTotal = 512; // Check first 1KB of pattern data
            for (int addr = 0; addr < patternTotal * 2; addr += 2)
            {
                ushort word = (ushort)(g_vram[addr] | (g_vram[addr + 1] << 8));
                if (word != 0) patternNonZero++;
            }
            
            // Check sprite attribute table
            int satNonZero = 0;
            int satTotal = 80; // 80 sprites * 8 bytes / 2 bytes per word
            int satBase = g_vdp_reg_5_sprite;
            for (int i = 0; i < satTotal; i++)
            {
                int addr = (satBase + i * 2) & 0xFFFF;
                ushort word = (ushort)(g_vram[addr] | (g_vram[addr + 1] << 8));
                if (word != 0) satNonZero++;
            }
            
            Console.WriteLine($"[VRAM-CHK] frame={_frameCounter} pat_nonZero={patternNonZero}/{patternTotal} sat_nonZero={satNonZero}/{satTotal}");
        }
        
        // Trace VDP data-port writes (CPU -> VDP)
        private void TraceVdpDataWrite(uint in_address, ushort in_data, int code, int destAddr, int autoInc)
        {
            // Always count writes to VRAM for stats
            if ((code & 0x0F) == 1)
            {
                int maskedAddr = destAddr & 0xFFFF;
                int page = maskedAddr >> 12;
                _vramWritePageCounts[page]++;

                // Track when we first see writes to scroll regions
                if (maskedAddr >= 0xC000 && maskedAddr < 0xE100 && in_data != 0)
                {
                    if (_firstScrollWriteFrame == 0)
                    {
                        _firstScrollWriteFrame = (int)_frameCounter;
                    }
                    _lastScrollWriteFrame = (int)_frameCounter;
                }
            }

            // Log ALL writes to VRAM 0xA000-0xFFFF (gated)
            if ((code & 0x0F) == 1)
            {
                int maskedAddr = destAddr & 0xFFFF;

                // Log writes around interlace mode 2 activation
                bool nearInterlace2 = (_frameCounter >= _firstInterlace2Frame - 120) && (_frameCounter <= _firstInterlace2Frame + 300);

                if (TraceVramWriteCpu && _vramWriteCpuLogRemaining > 0 && (maskedAddr >= 0xA000 || nearInterlace2))
                {
                    string target = "VRAM";
                    Console.WriteLine($"[VRAM-WRITE-CPU] frame={_frameCounter} addr=0x{maskedAddr:X4} target={target} value=0x{in_data:X4} autoInc={autoInc}");
                    if (_vramWriteCpuLogRemaining != int.MaxValue)
                        _vramWriteCpuLogRemaining--;
                }

                // Watchpoint: first non-zero in scroll regions
                if (maskedAddr >= 0xC000 && maskedAddr < 0xC100)
                {
                    if (_firstNonZeroScrollA == 0 && in_data != 0)
                    {
                        _firstNonZeroScrollA = (ushort)in_data;
                        Console.WriteLine($"[WATCHPOINT-ScrollA] frame={_frameCounter} addr=0x{maskedAddr:X4} value=0x{in_data:X4} FIRST_NON_ZERO");
                    }
                }
                if (maskedAddr >= 0xE000 && maskedAddr < 0xE100)
                {
                    if (_firstNonZeroScrollB == 0 && in_data != 0)
                    {
                        _firstNonZeroScrollB = (ushort)in_data;
                        Console.WriteLine($"[WATCHPOINT-ScrollB] frame={_frameCounter} addr=0x{maskedAddr:X4} value=0x{in_data:X4} FIRST_NON_ZERO");
                    }
                }
            }
        }

        private int _firstScrollWriteFrame = 0;
        private int _lastScrollWriteFrame = 0;
        private int _firstInterlace2Frame = 0;
        private ushort _firstNonZeroScrollA = 0;
        private ushort _firstNonZeroScrollB = 0;
        private int[] _vramWritePageCounts = new int[16];
        private int _vramWriteCpuLogRemaining = TraceVramWriteCpuLimit;
        private int _vdpCtrlWriteLogRemaining = TraceVdpCtrlWriteLimit;
        private int _vdpAddrSetLogRemaining = TraceVdpAddrSetLimit;

        // Trace VDP control port writes (CPU -> VDP control)
        private void TraceVdpControlWrite(uint in_address, ushort in_data, bool commandSelect, ushort commandWord)
        {
            // Always trace register writes (not just interlace mode 2)
            // Log only changes to key registers
            if ((in_data & 0xC000) == 0x8000)
            {
                // Register write
                byte rs = (byte)((in_data >> 8) & 0x1F);
                byte data = (byte)(in_data & 0xFF);

                // Key registers for scroll/name tables
                if (TraceVdpCtrlWrite && _vdpCtrlWriteLogRemaining > 0 &&
                    (rs == 1 || rs == 2 || rs == 3 || rs == 4 || rs == 5 || rs == 0x0C || rs == 0x0F || rs == 0x10))
                {
                    Console.WriteLine($"[VDP-CTRL-WRITE] frame={_frameCounter} reg{rs}=0x{data:X2}");
                    if (_vdpCtrlWriteLogRemaining != int.MaxValue)
                        _vdpCtrlWriteLogRemaining--;
                }
            }
            else if (!commandSelect)
            {
                // Address set command (first word)
                int code = (in_data >> 14) & 0x3;
                int addr = in_data & 0x3FFF;
                string target = code switch
                {
                    1 => "VRAM",
                    3 => "CRAM",
                    _ => $"UNK({code})"
                };
                if (TraceVdpAddrSet && _vdpAddrSetLogRemaining > 0)
                {
                    Console.WriteLine($"[VDP-ADDR-SET] frame={_frameCounter} target={target} addr=0x{addr:X4}");
                    if (_vdpAddrSetLogRemaining != int.MaxValue)
                        _vdpAddrSetLogRemaining--;
                }
            }
        }

        // Reset write counters at VBlank
        private void ResetVramWriteCounters()
        {
            _vramWriteLogCount = 0;
            for (int i = 0; i < 16; i++) _vramWritePageCounts[i] = 0;
            _writesToScrollAThisFrame = 0;
            _writesToScrollBThisFrame = 0;
        }

        internal void RecordDataPortWriteCode(int code)
        {
            int idx = code & 0x0f;
            if ((uint)idx < (uint)_mdDataWriteCodeCounts.Length)
                _mdDataWriteCodeCounts[idx]++;
        }

        // Count non-zero words per 0x1000 page and report top 3
        private void LogVramPageStats()
        {
            if (_frameCounter % 60 != 0)
                return;

            int[] pageCounts = new int[16];
            int[] pageNonZeroTiles = new int[16];

            for (int page = 0; page < 16; page++)
            {
                int pageStart = page << 12;
                int nonZero = 0;
                int nonZeroTiles = 0;

                // Sample 64 words per page (every 8th word)
                for (int i = 0; i < 64; i++)
                {
                    int addr = (pageStart + (i << 4)) & 0xFFFE; // sample every 16 bytes
                    ushort word = (ushort)((g_vram[addr] << 8) | g_vram[addr ^ 1]);
                    if (word != 0)
                    {
                        nonZero++;
                        if ((word & 0x07FF) != 0)
                            nonZeroTiles++;
                    }
                }
                pageCounts[page] = nonZero;
                pageNonZeroTiles[page] = nonZeroTiles;
            }

            // Find top 3 pages by nonZeroTiles
            var topPages = pageNonZeroTiles
                .Select((count, page) => (count, page))
                .OrderByDescending(x => x.count)
                .Take(3)
                .Where(x => x.count > 0)
                .ToList();

            string topStr = topPages.Any()
                ? string.Join(" ", topPages.Select(x => $"0x{x.page << 12:X4}({x.count})"))
                : "none";

            // In interlace mode 2, also show which pages are being written to
            if (g_vdp_interlace_mode == 2)
            {
                var writePages = _vramWritePageCounts
                    .Select((count, page) => (count, page))
                    .Where(x => x.count > 0)
                    .OrderByDescending(x => x.count)
                    .Take(3)
                    .ToList();

                string writeStr = writePages.Any()
                    ? string.Join(" ", writePages.Select(x => $"0x{x.page << 12:X4}(w={x.count})"))
                    : "none";

                Console.WriteLine($"[VRAM-PAGE-STATS] frame={_frameCounter} interlace={g_vdp_interlace_mode} topPages={topStr} writePages={writeStr}");
            }
            else
            {
                Console.WriteLine($"[VRAM-PAGE-STATS] frame={_frameCounter} interlace={g_vdp_interlace_mode} topPages={topStr}");
            }
        }

        // Log register snapshot in interlace mode 2
        private void LogInterlaceRegisterSnapshot()
        {
            if (g_vdp_interlace_mode != 2 || _frameCounter % 60 != 0)
                return;

            // Compute bases - H40 mode is bit 1 of register 12 (stored in bits 6-7 as cellmode)
            // H32: mask = 0xFC00, H40: mask = 0xFE00
            // H40 is set when reg12 bit 1 is set, which appears as g_vdp_reg_12_7_cellmode1=1 (bit 7 in raw reg)
            bool isH40 = (g_vdp_reg_12_7_cellmode1 != 0);
            int mask = isH40 ? 0xFE00 : 0xFC00;

            int scrollA_base = g_vdp_reg_2_scrolla & mask;
            int scrollB_base = g_vdp_reg_4_scrollb & mask;
            int window_base = g_vdp_reg_3_windows & mask;
            int sprite_base = g_vdp_reg_5_sprite & mask;
            int hscroll_base = g_vdp_reg_13_hscroll & 0xFC00;

            // Get raw register 12 value (combining bits)
            byte reg12_raw = (byte)((g_vdp_reg_12_7_cellmode1 << 7) | (g_vdp_reg_12_3_shadow << 3) |
                                     (g_vdp_reg_12_2_interlacemode << 1) | g_vdp_reg_12_0_cellmode2);

            Console.WriteLine($"[VDP-REGS-INT2] frame={_frameCounter} " +
                $"reg0C=0x{reg12_raw:X2}(H40={isH40}) " +
                $"reg02=0x{g_vdp_reg_2_scrolla:X2} reg04=0x{g_vdp_reg_4_scrollb:X2} reg05=0x{g_vdp_reg_5_sprite:X2} " +
                $"reg03=0x{g_vdp_reg_3_windows:X2} reg0D=0x{g_vdp_reg_13_hscroll:X2} reg0F=0x{g_vdp_reg_15_autoinc:X2} " +
                $"bases: A=0x{scrollA_base:X4} B=0x{scrollB_base:X4} W=0x{window_base:X4} S=0x{sprite_base:X4} HS=0x{hscroll_base:X4}");
        }

        private void LogInterlaceWriteSummary()
        {
            if (g_vdp_interlace_mode != 2 || _firstInterlace2Frame == 0 || _frameCounter < _firstInterlace2Frame)
                return;

            string pageHistogram = string.Join(" ",
                _vramWritePageCounts.Select((count, page) => $"0x{page:X1}={count}"));

            Console.WriteLine(
                $"[VRAM-WRITE-SUM] frame={_frameCounter} vram={_mdVramWritesThisFrame} cram={_mdCramWritesThisFrame} " +
                $"ctrl={_mdCtrlWritesThisFrame} data={_mdDataWritesThisFrame} nameTbl={_mdVramWritesToNameTablesThisFrame} " +
                $"scrollA={_writesToScrollAThisFrame} scrollB={_writesToScrollBThisFrame} dropped={_mdVramWritesDroppedThisFrame}");
            Console.WriteLine($"[VRAM-PAGE-HIST] frame={_frameCounter} {pageHistogram}");
            string codeHist = string.Join(" ",
                _mdDataWriteCodeCounts
                    .Select((count, code) => count > 0 ? $"0x{code:X1}={count}" : null)
                    .Where(x => x != null));
            if (!string.IsNullOrEmpty(codeHist))
                Console.WriteLine($"[VDP-DATA-CODE] frame={_frameCounter} {codeHist}");
        }

        private void ClearVBlank()
        {
            if (!_vblankActive)
                return;

            _vblankActive = false;
            if (!md_main.g_masterSystemMode)
            {
                g_vdp_status_3_vbrank = 0;
                g_vdp_status_7_vinterrupt = 0;
                md_m68k.g_interrupt_V_req = false;
                // Clear Z80 INT for sound drivers in MD mode
                md_main.g_md_z80?.irq_request(false, "VDP", 0);
                _z80VblankIntActive = false;
            }
            LogVBlankEdge(0);
        }

        private void ClearZ80VBlankInterrupt()
        {
            if (!_z80VblankIntActive)
                return;
            _z80VblankIntActive = false;
            md_main.g_md_z80?.irq_request(false, "VDP", 0);
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

        private void LogDmaStatusWindow()
        {
            if (!TraceDmaStatus)
                return;
            if (_frameCounter < 640 || _frameCounter > 700)
                return;

            int vramWrites = _mdVramWritesThisFrame;
            int cramWrites = _mdCramWritesThisFrame;
            int codeVram = _mdDataWriteCodeCounts[1];
            int codeCram = _mdDataWriteCodeCounts[3];
            int codeVsram = _mdDataWriteCodeCounts[5];
            int codeOther = _mdDataWritesThisFrame - (codeVram + codeCram + codeVsram);
            int scrollA = g_vdp_reg_2_scrolla & 0xFFFE;
            int scrollB = g_vdp_reg_4_scrollb & 0xFFFE;
            bool dmaActive = g_vdp_status_1_dma != 0 || g_dma_mode != 0 || g_dma_leng > 0;
            LogDmaStatusLine(
                $"[DMA-STATUS] frame={_frameCounter} dmaActive={(dmaActive ? 1 : 0)} dmaMode={g_dma_mode} dmaLen={g_dma_leng} " +
                $"dmaDest=0x{g_vdp_reg_dest_address:X4} cpuDataWrites={_mdDataWritesThisFrame} cpuDropped={_mdDataWritesDroppedThisFrame} " +
                $"vramWrites={vramWrites} cramWrites={cramWrites} codeVram={codeVram} codeCram={codeCram} codeVsram={codeVsram} codeOther={codeOther} " +
                $"scrollA=0x{scrollA:X4} scrollB=0x{scrollB:X4} vramDropped={_mdVramWritesDroppedThisFrame} " +
                $"gate={(GateCpuWritesDuringDma ? 1 : 0)} gateBypass={(DisableDmaWriteGate ? 1 : 0)}");
        }

        private void TraceDataPortWindowWrite(uint in_address, ushort in_data)
        {
            if (!TraceDmaStatus)
                return;
            if (_frameCounter < 640 || _frameCounter > 700)
                return;

            if (_mdDataPortWindowFrame != (int)_frameCounter)
            {
                _mdDataPortWindowFrame = (int)_frameCounter;
                _mdDataPortWindowLogCount = 0;
            }

            if (_mdDataPortWindowLogCount >= 16)
                return;
            _mdDataPortWindowLogCount++;

            int codeLow = g_vdp_reg_code & 0x0f;
            string target = codeLow switch
            {
                0x01 => "VRAM",
                0x03 => "CRAM",
                0x05 => "VSRAM",
                _ => $"UNK({codeLow})"
            };
            LogDmaStatusLine(
                $"[DATA-WRITE] frame={_frameCounter} addr=0x{g_vdp_reg_dest_address:X4} code=0x{g_vdp_reg_code:X2} " +
                $"target={target} autoinc=0x{g_vdp_reg_15_autoinc:X2} val=0x{in_data:X4}");
        }

        private static void LogDmaStatusLine(string line)
        {
            if (TraceDmaDetail)
                Console.WriteLine(line);
            if (TraceRomStartLog)
            {
                try
                {
                    System.IO.File.AppendAllText("rom_start.log", line + Environment.NewLine);
                }
                catch
                {
                    // Ignore logging failures.
                }
            }
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

        private void LogInterlaceDebug()
        {
            // Log interlace timing debug info once per second (every 60 frames)
            if (_frameCounter % 60 != 0)
                return;

            // Get current HV counter value
            ushort hvCounter = get_vdp_hvcounter();
            int hvV = hvCounter >> 8;
            int hvH = hvCounter & 0xFF;

            // Get VBlank and status bits
            int vblankBit = g_vdp_status_3_vbrank;

            // Get backdrop color index
            int backdropIdx = g_vdp_reg_7_backcolor;
            uint backdropColor = (g_color != null && backdropIdx < g_color.Length) ? g_color[backdropIdx] : 0;

            // Get display enable status
            int displayEnabled = g_vdp_reg_1_6_display;

            // Get Z80 active state and cycles
            bool z80Active = md_main.g_md_z80?.g_active ?? false;

            // Get audio write counters per second
            int ymKeyOn = 0, ymFnum = 0, ymParam = 0, ymDacCmd = 0, ymDacDat = 0;
            int psgWrites = 0;
            if (md_ym2612.AudStatEnabled && md_main.g_md_music != null)
            {
                md_main.g_md_music.g_md_ym2612.ConsumeAudStatCounters(
                    out ymKeyOn, out ymFnum, out ymParam, out ymDacCmd, out ymDacDat);
                psgWrites = md_main.g_md_music.g_md_sn76489.ConsumeAudStatWrites();
            }

            // Check CRAM colors - count non-black colors
            int cramNonBlack = 0;
            if (g_color != null)
            {
                for (int i = 0; i < Math.Min(64, g_color.Length); i++)
                {
                    uint rgb = g_color[i] & 0x00FFFFFF;
                    if (rgb != 0) cramNonBlack++;
                }
            }

            // Check framebuffer content - count non-black pixels and stats
            int nonBlackCount = 0;
            int totalPixels = 0;
            var uniqueColors = new System.Collections.Generic.HashSet<uint>();

            if (g_game_screen != null)
            {
                // Check the full output size (320x448 in interlace mode 2)
                int outputHeight = (g_vdp_interlace_mode == 2) ? g_display_ysize * 2 : g_display_ysize;
                int checkCount = Math.Min(g_output_xsize * outputHeight, g_game_screen.Length);
                totalPixels = checkCount;

                // Sample first 10000 pixels for unique colors (performance)
                int sampleLimit = Math.Min(10000, checkCount);

                for (int i = 0; i < checkCount; i++)
                {
                    uint pixel = g_game_screen[i];
                    uint rgb = pixel & 0x00FFFFFF; // Ignore alpha

                    if (rgb != 0) // Non-black (RGB != 0)
                        nonBlackCount++;

                    if (i < sampleLimit)
                    {
                        if (rgb != 0) // Only track non-black colors
                            uniqueColors.Add(rgb);
                    }
                }
            }

            // Get some color palette info
            uint sampleCram1 = g_color != null && g_color.Length > 0 ? g_color[0] : 0;
            uint sampleCram16 = g_color != null && g_color.Length > 16 ? g_color[16] : 0;
            uint sampleCram32 = g_color != null && g_color.Length > 32 ? g_color[32] : 0;

            if (TraceInterlaceDebug)
            {
                Console.WriteLine(
                    $"[INTERLACE-DEBUG] frame={_frameCounter} interlace={g_vdp_interlace_mode} field={g_vdp_interlace_field} " +
                    $"hvV={hvV} hvH={hvH} vblank={vblankBit} display={displayEnabled} " +
                    $"z80Active={(z80Active ? 1 : 0)} ymKeyOn={ymKeyOn} ymParam={ymParam} psgWrites={psgWrites} " +
                    $"fbPixels={nonBlackCount}/{totalPixels} uniqueColors={uniqueColors.Count} " +
                    $"backdrop={backdropIdx} backdropColor=0x{backdropColor:X8} " +
                    $"cramNonBlack={cramNonBlack} cram0=0x{sampleCram1:X8} cram16=0x{sampleCram16:X8}");
            }
         }

          // [NT-CHK] Name Table Check - gated by EUTHERDRIVE_DEBUG_NTCHK=1
          private void DebugNtCheck()
          {
              if (!DebugNtChk) return;
              
               int baseA = g_vdp_reg_2_scrolla;
               int baseB = g_vdp_reg_4_scrollb;
              const int nameTableSize = 0x800; // 2KB per name table
              const int totalWords = nameTableSize / 2;
              
              int nonZeroA = 0;
              int nonZeroB = 0;
              
              for (int i = 0; i < nameTableSize; i += 2)
              {
                  ushort valA = (ushort)((g_vram[(baseA + i) & 0xFFFF] << 8) | g_vram[(baseA + i + 1) & 0xFFFF]);
                  ushort valB = (ushort)((g_vram[(baseB + i) & 0xFFFF] << 8) | g_vram[(baseB + i + 1) & 0xFFFF]);
                  
                  if (valA != 0) nonZeroA++;
                  if (valB != 0) nonZeroB++;
              }
              
              MdLog.WriteLineVdp($"[NT-CHK] frame={_frameCounter} baseA=0x{baseA:X4} nonZeroA={nonZeroA}/{totalWords} baseB=0x{baseB:X4} nonZeroB={nonZeroB}/{totalWords}");
           }
         
          // [VDP-FRAME] VDP Frame Status - gated by EUTHERDRIVE_DEBUG_VDPFRAME=1
          private void DebugVdpFrameStatus()
          {
              if (!DebugVdpFrame) return;
              
              // Reconstruct reg12 from its components
              byte reg12 = (byte)((g_vdp_reg_12_7_cellmode1 << 7) | (g_vdp_reg_12_3_shadow << 3) |
                                 (g_vdp_reg_12_2_interlacemode << 1) | g_vdp_reg_12_0_cellmode2);
              bool h40 = (reg12 & 0x81) == 0x81;
              int renderWidth = h40 ? 320 : 256;
              bool displayOn = g_vdp_reg_1_6_display != 0;
              int planeSize = (g_vdp_reg_16_5_scrollV >> 4) & 3;
               int satBase = g_vdp_reg_5_sprite;
               int baseA = g_vdp_reg_2_scrolla;
               int baseB = g_vdp_reg_4_scrollb;
              
              MdLog.WriteLineVdp($"[VDP-FRAME] frame={_frameCounter} reg12=0x{reg12:X2} h40={(h40 ? 1 : 0)} width={renderWidth} display={(displayOn ? 1 : 0)} planeSize={planeSize} satBase=0x{satBase:X4} baseA=0x{baseA:X4} baseB=0x{baseB:X4} interlace={g_vdp_interlace_mode} field={g_vdp_interlace_field}");
          }
         
          // [DMAWIN] DMA Window Check - gated by EUTHERDRIVE_DEBUG_DMAWIN=1
          // Call this from DMA operations (DMA-FILL and DMA-COPY)
          private void DebugDmaWindow(uint destAddr, uint length, byte regCode, string dmaType)
          {
              if (!DebugDmaWin) return;
              
               // Frame range check for debugging
               // if (_frameCounter < 4904 || _frameCounter > 4915) return;
              
               int baseA = g_vdp_reg_2_scrolla;
               int baseB = g_vdp_reg_4_scrollb;
              const int nameTableSize = 0x800; // 2KB per name table
              
              bool overlapsA = OverlapsRegion(destAddr, length, (uint)baseA, nameTableSize);
              bool overlapsB = OverlapsRegion(destAddr, length, (uint)baseB, nameTableSize);
              
              MdLog.WriteLineVdp($"[DMAWIN] frame={_frameCounter} {dmaType} dest=0x{destAddr:X4} len=0x{length:X4} code=0x{regCode:X2} overlapsA={(overlapsA ? 1 : 0)} overlapsB={(overlapsB ? 1 : 0)}");
          }
         
         private static bool OverlapsRegion(uint addr1, uint len1, uint addr2, uint len2)
         {
             uint end1 = addr1 + len1;
             uint end2 = addr2 + len2;
             return addr1 < end2 && addr2 < end1;
         }

     }
}
