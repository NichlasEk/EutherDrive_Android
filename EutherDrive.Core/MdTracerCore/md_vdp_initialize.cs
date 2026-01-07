namespace EutherDrive.Core.MdTracerCore
{
    internal partial class md_vdp
    {
        public void initialize()
        {
            // Core memories
            g_vram = new byte[65536];
            g_cram = new ushort[64];
            g_vsram = new ushort[40];

            // Color lookup tables
            g_color = new uint[COLOR_MAX];
            g_color_shadow = new uint[COLOR_MAX];
            g_color_highlight = new uint[COLOR_MAX];
            for (int i = 0; i < COLOR_MAX; i++)
            {
                g_color[i] = 0xFF000000u;
                g_color_shadow[i] = 0xFF000000u;
                g_color_highlight[i] = 0xFF000000u;
            }

            // Pattern cache & frame buffers
            g_pattern_chk = new bool[PATTERN_MAX];
            g_game_cmap = new uint[DISPLAY_BUFSIZE];
            g_game_primap = new uint[DISPLAY_BUFSIZE];
            g_game_shadowmap = new uint[DISPLAY_BUFSIZE];

            g_game_screen = new uint[DISPLAY_BUFSIZE];
            Array.Fill(g_game_screen, 0xFF000000u);
            g_renderer_vram = new uint[VRAM_DATASIZE * 4];

            // Per-line snapshot buffers
            g_snap_register = new VDP_REGISTER();

            g_line_snap = new VDP_LINE_SNAP[DISPLAY_YSIZE];
            for (int i = 0; i < DISPLAY_YSIZE; i++)
            {
                g_line_snap[i] = new VDP_LINE_SNAP();
                g_line_snap[i].vscrollA = new int[VSRAM_DATASIZE];
                g_line_snap[i].vscrollB = new int[VSRAM_DATASIZE];
                g_line_snap[i].sprite_left = new int[MAX_SPRITE];
                g_line_snap[i].sprite_right = new int[MAX_SPRITE];
                g_line_snap[i].sprite_top = new int[MAX_SPRITE];
                g_line_snap[i].sprite_bottom = new int[MAX_SPRITE];
                g_line_snap[i].sprite_xcell_size = new int[MAX_SPRITE];
                g_line_snap[i].sprite_ycell_size = new int[MAX_SPRITE];
                g_line_snap[i].sprite_priority = new uint[MAX_SPRITE];
                g_line_snap[i].sprite_palette = new uint[MAX_SPRITE];
                g_line_snap[i].sprite_reverse = new uint[MAX_SPRITE];
                g_line_snap[i].sprite_char = new uint[MAX_SPRITE];
            }

            g_snap_line_snap = new VDP_LINE_SNAP[DISPLAY_YSIZE];
            for (int i = 0; i < DISPLAY_YSIZE; i++)
            {
                g_snap_line_snap[i] = new VDP_LINE_SNAP();
                g_snap_line_snap[i].vscrollA = new int[VSRAM_DATASIZE];
                g_snap_line_snap[i].vscrollB = new int[VSRAM_DATASIZE];
                g_snap_line_snap[i].sprite_left = new int[MAX_SPRITE];
                g_snap_line_snap[i].sprite_right = new int[MAX_SPRITE];
                g_snap_line_snap[i].sprite_top = new int[MAX_SPRITE];
                g_snap_line_snap[i].sprite_bottom = new int[MAX_SPRITE];
                g_snap_line_snap[i].sprite_xcell_size = new int[MAX_SPRITE];
                g_snap_line_snap[i].sprite_ycell_size = new int[MAX_SPRITE];
                g_snap_line_snap[i].sprite_priority = new uint[MAX_SPRITE];
                g_snap_line_snap[i].sprite_palette = new uint[MAX_SPRITE];
                g_snap_line_snap[i].sprite_reverse = new uint[MAX_SPRITE];
                g_snap_line_snap[i].sprite_char = new uint[MAX_SPRITE];
            }

            g_snap_renderer_vram = new uint[VRAM_DATASIZE * 4];
            g_snap_color = new uint[64];
            g_snap_color_shadow = new uint[64];
            g_snap_color_highlight = new uint[64];

            // Replace GDI+ bitmaps with raw pixel buffers (ARGB32)
            // These should be sized width*height each.
            g_scrollA_pixels = new uint[1024 * 1024];
            g_scrollB_pixels = new uint[1024 * 1024];
            g_scrollW_pixels = new uint[1024 * 1024];
            g_scrollS_pixels = new uint[512 * 512];
            g_pattern_pixels = new uint[128 * 1024];

            g_sprite_enable = new bool[80];

            MONOCOLOR_TABLE = new uint[16];
            for (uint i = 0; i <= 15; i++)
            {
                uint w = i << 4;
                MONOCOLOR_TABLE[i] = 0xff000000 | (w << 16) | (w << 8) | w;
            }


            // Default geometry/timing
            g_display_xsize = 256;
            g_display_ysize = 224;
            g_scroll_xcell = 32;
            g_scroll_ycell = 32;
            g_scroll_xsize = 256;
            g_scroll_ysize = 256;
            g_scroll_xsize_mask = 0x00ff;
            g_scroll_ysize_mask = 0x00ff;
            g_vertical_line_max = 262;
            UpdateOutputWidth();

            // VDP status / regs
            g_vdp_status_9_empl = 1;  // const
            g_vdp_status_8_full = 0;  // const
            g_vdp_status_7_vinterrupt = 0;
            g_vdp_status_6_sprite = 0;
            g_vdp_status_5_collision = 0;
            g_vdp_status_4_frame = 0;
            g_vdp_status_3_vbrank = 0;
            g_vdp_status_2_hbrank = 0; // const
            g_vdp_status_1_dma = 0;    // const
            g_vdp_status_0_tvmode = 0;

            g_vdp_reg = new byte[24];
            g_vdp_reg_2_scrolla = 0xffff;
            g_vdp_reg_3_windows = 0xffff;
            g_vdp_reg_4_scrollb = 0xffff;
            g_vdp_interlace_mode = 0;
            g_vdp_interlace_field = 0;
            _interlaceFieldAdvanced = false;

            g_scanline = 0;
            g_hinterrupt_counter = -1;

            ApplyHorizontalMode(IsH40Mode());
        }
    }
}
