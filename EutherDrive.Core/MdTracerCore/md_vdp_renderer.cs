using System;
using System.Diagnostics;

namespace EutherDrive.Core.MdTracerCore
{
    internal partial class md_vdp
    {
        private const int PATTERN_MAX      = 2048;
        private const int DISPLAY_XSIZE    = 320;
        private const int DISPLAY_YSIZE    = 480;
        private const int DISPLAY_BUFSIZE  = DISPLAY_XSIZE * DISPLAY_YSIZE;
        public  int       SPRITE_XSIZE     = 512;
        public  int       SPRITE_YSIZE     = 512;
        private const int VRAM_DATASIZE    = 65536 / 2;
        private const int VSRAM_DATASIZE   = 20;
        private const int COLOR_MAX        = 64;
        private const int MAX_SPRITE       = 20;

        // Render-data
        private bool[]         g_pattern_chk = Array.Empty<bool>();
        internal uint[]        g_renderer_vram = Array.Empty<uint>();
        private VDP_LINE_SNAP[] g_line_snap = Array.Empty<VDP_LINE_SNAP>();
        private uint[]         g_game_cmap = Array.Empty<uint>();
        private uint[]         g_game_primap = Array.Empty<uint>();
        private uint[]         g_game_shadowmap = Array.Empty<uint>();

        // Snapshots (för “double buffering”/analys)
        private VDP_REGISTER    g_snap_register = new VDP_REGISTER();
        private uint[]          g_snap_renderer_vram = Array.Empty<uint>();
        private VDP_LINE_SNAP[] g_snap_line_snap = Array.Empty<VDP_LINE_SNAP>();
        private uint[]          g_snap_color = Array.Empty<uint>();
        private uint[]          g_snap_color_shadow = Array.Empty<uint>();
        private uint[]          g_snap_color_highlight = Array.Empty<uint>();

        // Framebuffer (ARGB32 per pixel)
        public uint[] g_game_screen = Array.Empty<uint>();

        // Geometri / storlek
        public  int g_display_xsize;
        private int g_output_xsize;
        public  int g_display_ysize;
        private int g_output_ysize;
        private int g_display_xcell;
        private int g_display_ycell;
        private int g_scroll_xcell;
        private int g_scroll_ycell;
        public  int g_scroll_xsize;
        public  int g_scroll_ysize;
        private int g_scroll_xsize_mask;
        private int g_scroll_ysize_mask;
        public  int g_vertical_line_max;

        // Window A clip
        private int g_screenA_left_x;
        private int g_screenA_right_x;
        private int g_screenA_top_y;
        private int g_screenA_bottom_y;

        // Spritebegränsningar
        private int g_max_sprite_num;
        private int g_max_sprite_line;
        private int g_max_sprite_cell;
        private int g_sprite_vmask;
        private bool[] g_sprite_line_mask = Array.Empty<bool>();
        private uint[] g_game_field_even = Array.Empty<uint>();
        private uint[] g_game_field_odd = Array.Empty<uint>();

        // GPU-path stöds inte i headless-läget
        public bool rendering_gpu;

        // Interlace snapshot buffer (för att spara sprite-data vid double-field rendering)
        private VDP_LINE_SNAP[] _interlaceSnapshotBuffer = Array.Empty<VDP_LINE_SNAP>();

        // Kör en scanline
        private void rendering_line()
        {
            if (g_vdp_interlace_mode == 2 && InterlaceOutput == InterlaceOutputPolicy.DoubleField)
            {
                byte currentField = (byte)(g_vdp_interlace_field & 0x01);
                uint[] dest = (currentField == 0) ? g_game_field_even : g_game_field_odd;
                RenderLineWithField(currentField, g_scanline, dest);
                return;
            }

            RenderLineWithField(g_vdp_interlace_field, GetOutputLineForScanline(g_scanline));
        }

        private void RenderLineWithField(byte field, int outputLineOverride = -1, uint[]? targetBuffer = null)
        {
            g_vdp_interlace_field = (byte)(field & 0x01);
            if (MdTracerCore.MdLog.Enabled && g_scanline == 0)
                MdTracerCore.MdLog.WriteLine($"[VDP] frame={_frameCounter} display={g_vdp_reg_1_6_display}");

            if (g_vdp_reg_1_6_display == 1)
            {
                if (md_main.g_masterSystemMode && !_smsFirstLineRendered && g_scanline == 0)
                {
                    _smsFirstLineRendered = true;
                    if (MdTracerCore.MdLog.Enabled)
                        MdTracerCore.MdLog.WriteLine("[SMS VDP] first scanline rendered");
                }
                // Ta snapshot av VDP-tillstånd för den här linjen
                rendering_line_snap();

                // Beräkna output line
                int outputLine = (outputLineOverride >= 0) ? outputLineOverride : GetOutputLineForScanline(g_scanline);

                // Alltid CPU-rendering i headless
                rendering_line_cpu(outputLine, targetBuffer);
            }
            else
            {
                // Display off: nolla raden i framebuffer
                int outputLine = (outputLineOverride >= 0) ? outputLineOverride : GetOutputLineForScanline(g_scanline);
                if ((uint)outputLine < (uint)g_output_ysize)
                {
                    int pos = outputLine * g_output_xsize;
                    for (int x = 0; x < g_output_xsize; x++)
                    {
                        if (targetBuffer != null)
                            targetBuffer[pos++] = 0xFF000000u;
                        else
                            g_game_screen[pos++] = 0xFF000000u;
                    }
                }
            }
        }

        // Avsluta en frame (ingen separat render-tråd eller DX)
        private void rendering_frame()
        {
            // VIKTIGT: I interlace mode flippar fältet varje ram.
            // Vi ska INTE sätta fältet till 0 här - det förstör fält-växlingen!
            // AdvanceInterlaceField() hanterar fält-växlingen korrekt.

            ForceVBlankForTest();
            ForceMdVBlankForTest();
            // "Lås in" det som behövs för postprocess/analys
            rendering_frame_snap();
            WeaveInterlaceFields();
            UpdateRgbaFrameFromGameScreen();
            SmsLogFrameHash();
            AdvanceInterlaceField();
            _frameCounter++;
            LogMdWriteSummary();
            MaybeLogVdpTiming();
            MaybeLogVdpState();
            LogInterlaceDebug();
        }

        private void WeaveInterlaceFields()
        {
            if (g_vdp_interlace_mode != 2 || InterlaceOutput != InterlaceOutputPolicy.DoubleField)
                return;

            int fieldLineCount = g_display_ysize;
            int outputLines = g_output_ysize;
            int width = g_output_xsize;
            int maxLines = Math.Min(fieldLineCount * 2, outputLines);

            for (int y = 0; y < fieldLineCount && (y << 1) < maxLines; y++)
            {
                int evenBase = y * width;
                int oddBase = y * width;
                int outEven = (y << 1) * width;
                int outOdd = ((y << 1) + 1) * width;

                Array.Copy(g_game_field_even, evenBase, g_game_screen, outEven, width);
                if ((y << 1) + 1 < maxLines)
                    Array.Copy(g_game_field_odd, oddBase, g_game_screen, outOdd, width);
            }
        }

        // --- Hjälp (valfritt) ---
        // Exponera en enkel pekare till framebuffer om du vill hämta bilden från UI-lagret:
        public uint[] GetFrameBuffer() => g_game_screen;
    }
}
