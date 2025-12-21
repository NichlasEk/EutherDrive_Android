namespace EutherDrive.Core.MdTracerCore
{
    internal partial class md_vdp
    {
        // --- Bas-konstanter (rimliga defaults) ---
        public const int DISPLAY_XSIZE = 320;
        public const int DISPLAY_YSIZE = 240;
        public const int DISPLAY_BUFSIZE = DISPLAY_XSIZE * DISPLAY_YSIZE;

        public const int VRAM_DATASIZE = 0x10000; // 64 KiB
        public const int VSRAM_DATASIZE = 80;     // bytes (40 words)

        public const int COLOR_MAX = 64;
        public const int MAX_SPRITE = 80;
        public const int PATTERN_MAX = 0x2000;

        // --- Display / scroll dimensioner (register-koden refererar dessa) ---
        public static int g_display_xsize = DISPLAY_XSIZE;
        public static int g_display_ysize = DISPLAY_YSIZE;

        public static int g_display_xcell = DISPLAY_XSIZE / 8; // 40
        public static int g_display_ycell = DISPLAY_YSIZE / 8; // 30

        public static int g_scroll_xcell = g_display_xcell;
        public static int g_scroll_ycell = g_display_ycell;

        public static int g_scroll_xsize = g_display_xsize;
        public static int g_scroll_ysize = g_display_ysize;

        public static int g_scroll_xsize_mask = g_scroll_xsize - 1;
        public static int g_scroll_ysize_mask = g_scroll_ysize - 1;

        // NTSC default
        public static int g_vertical_line_max = 262;

        // --- Buffertar / maps ---
        // md_vdp_initialize.cs verkar skapa bool[] här
        public static bool[] g_pattern_chk = new bool[PATTERN_MAX];

        public static uint[] g_game_cmap      = new uint[DISPLAY_BUFSIZE];
        public static uint[] g_game_primap    = new uint[DISPLAY_BUFSIZE];
        public static uint[] g_game_shadowmap = new uint[DISPLAY_BUFSIZE];
        public static uint[] g_game_screen    = new uint[DISPLAY_BUFSIZE];

        // md_vdp_initialize.cs verkar skapa uint[] här
        public static uint[] g_renderer_vram = new uint[VRAM_DATASIZE];

        // Snapshots (debug)
        public static uint[] g_snap_renderer_vram = new uint[VRAM_DATASIZE];

        public static uint[] g_snap_color           = new uint[COLOR_MAX];
        public static uint[] g_snap_color_shadow    = new uint[COLOR_MAX];
        public static uint[] g_snap_color_highlight = new uint[COLOR_MAX];

        // Pixel-buffers per layer (init-koden vill ha dessa)
        public static uint[] g_scrollA_pixels  = new uint[DISPLAY_BUFSIZE];
        public static uint[] g_scrollB_pixels  = new uint[DISPLAY_BUFSIZE];
        public static uint[] g_scrollW_pixels  = new uint[DISPLAY_BUFSIZE];
        public static uint[] g_scrollS_pixels  = new uint[DISPLAY_BUFSIZE];
        public static uint[] g_pattern_pixels  = new uint[DISPLAY_BUFSIZE];

        public static bool[] g_sprite_enable = new bool[MAX_SPRITE];

        // Vissa tabeller (monokrom debug). Vi gör en enkel placeholder.
        public static uint[] MONOCOLOR_TABLE = new uint[16];

        // Sprite-begränsningar (register-koden vill räkna mot dessa)
        public static int g_max_sprite_num  = MAX_SPRITE;
        public static int g_max_sprite_line = 20;
        public static int g_max_sprite_cell = 32;

        // Masker för sprite-beräkningar
        public static int g_sprite_vmask = 0x1FF; // 9-bit y mask (placeholder)

        // Window/gränser (regster.cs refererar dessa)
        public static int g_screenA_left_x = 0;
        public static int g_screenA_right_x = DISPLAY_XSIZE - 1;
        public int g_screenA_top_y = 0;
        public int g_screenA_bottom_y = 223;


        // --- Typer som init-koden vill ha ---
        internal sealed class VDP_REGISTER
        {
            private readonly ushort[] _r = new ushort[0x20];
            public ushort this[int i] { get => _r[i]; set => _r[i] = value; }
        }

        // md_vdp_initialize.cs vill initiera ARRAYER här (int[]/uint[])
        internal struct VDP_LINE_SNAP
        {
            public int[] vscrollA;
            public int[] vscrollB;

            public int[] sprite_left;
            public int[] sprite_right;
            public int[] sprite_top;
            public int[] sprite_bottom;

            public int[] sprite_xcell_size;
            public int[] sprite_ycell_size;

            public uint[] sprite_priority;
            public uint[] sprite_palette;
            public uint[] sprite_reverse;

            public uint[] sprite_char;
        }

        // Snap-arrays som init-koden vill skapa
        public static VDP_REGISTER g_snap_register = new VDP_REGISTER();

        public static VDP_LINE_SNAP[] g_line_snap = new VDP_LINE_SNAP[DISPLAY_YSIZE];
        public static VDP_LINE_SNAP[] g_snap_line_snap = new VDP_LINE_SNAP[DISPLAY_YSIZE];
    }
}
