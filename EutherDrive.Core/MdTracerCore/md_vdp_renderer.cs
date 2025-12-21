using System.Diagnostics;

namespace EutherDrive.Core.MdTracerCore
{
    internal partial class md_vdp
    {
        private const int PATTERN_MAX      = 2048;
        private const int DISPLAY_XSIZE    = 320;
        private const int DISPLAY_YSIZE    = 240;
        private const int DISPLAY_BUFSIZE  = DISPLAY_XSIZE * DISPLAY_YSIZE;
        public  int       SPRITE_XSIZE     = 512;
        public  int       SPRITE_YSIZE     = 512;
        private const int VRAM_DATASIZE    = 65536 / 2;
        private const int VSRAM_DATASIZE   = 20;
        private const int COLOR_MAX        = 64;
        private const int MAX_SPRITE       = 20;

        // Render-data
        private bool[]         g_pattern_chk;
        private uint[]         g_renderer_vram;
        private VDP_LINE_SNAP[] g_line_snap;
        private uint[]         g_game_cmap;
        private uint[]         g_game_primap;
        private uint[]         g_game_shadowmap;

        // Snapshots (för “double buffering”/analys)
        private VDP_REGISTER    g_snap_register;
        private uint[]          g_snap_renderer_vram;
        private VDP_LINE_SNAP[] g_snap_line_snap;
        private uint[]          g_snap_color;
        private uint[]          g_snap_color_shadow;
        private uint[]          g_snap_color_highlight;

        // Framebuffer (ARGB32 per pixel)
        public uint[] g_game_screen;

        // Geometri / storlek
        public  int g_display_xsize;
        public  int g_display_ysize;
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

        // GPU-path stöds inte i headless-läget
        public bool rendering_gpu;

        // Kör en scanline
        private void rendering_line()
        {
            if (g_vdp_reg_1_6_display == 1)
            {
                // Ta snapshot av VDP-tillstånd för den här linjen
                rendering_line_snap();

                // Alltid CPU-rendering i headless
                rendering_line_cpu();
            }
            else
            {
                // Display off: nolla raden i framebuffer
                int pos = g_scanline * g_display_xsize;
                for (int x = 0; x < g_display_xsize; x++)
                {
                    g_game_screen[pos++] = 0;
                }
            }
        }

        // Avsluta en frame (ingen separat render-tråd eller DX)
        private void rendering_frame()
        {
            // “Lås in” det som behövs för postprocess/analys
            rendering_frame_snap();
        }

        // --- Hjälp (valfritt) ---
        // Exponera en enkel pekare till framebuffer om du vill hämta bilden från UI-lagret:
        public uint[] GetFrameBuffer() => g_game_screen;
    }
}
