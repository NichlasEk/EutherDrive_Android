using System.Diagnostics;

namespace EutherDrive.Core.MdTracerCore
{
    internal partial class md_vdp
    {
        // TEMPORARY: Force direct VRAM reads to eliminate cache mismatch
        // Set to true to read patterns directly from vram[] instead of g_renderer_vram
        private static readonly string? ForceDirectVramReadSpritesEnv = Environment.GetEnvironmentVariable("EUTHERDRIVE_FORCE_DIRECT_VRAM");
        private static readonly bool ForceDirectVramReadSpritesEnvSet = ForceDirectVramReadSpritesEnv != null;
        private static bool ForceDirectVramReadSprites =
            string.Equals(ForceDirectVramReadSpritesEnv, "1", StringComparison.Ordinal);
        private static readonly bool ForceDirectVramReadPlanes =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_FORCE_DIRECT_VRAM_PLANES"), "1", StringComparison.Ordinal);
        private static readonly bool DisableWindow =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_VDP_DISABLE_WINDOW"), "1", StringComparison.Ordinal);
        private static readonly bool DisableSprites =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_VDP_DISABLE_SPRITES"), "1", StringComparison.Ordinal);

        // Helper to read a word from vram[] (handles the MD byte-swap)
        private ushort vram_read_render(int addr)
        {
            addr &= 0xFFFE; // Word-align, wrap at 64KB
            return (ushort)((g_vram[addr] << 8) | g_vram[addr ^ 1]);
        }

        private uint ReadPatternPixelMode2Direct(int tileIndex, int xInTile, int yInTile, uint reverse)
        {
            int x = ((reverse & 0x01) != 0) ? (7 - xInTile) : xInTile;
            int cellHeight = GetCellHeightPixels();
            int y = ((reverse & 0x02) != 0) ? (cellHeight - 1 - yInTile) : yInTile;
            int baseAddr = (tileIndex & 0x3FF) << 6;
            int byteAddr = baseAddr + (y << 2) + ((x >> 2) << 1);
            ushort word = vram_read_render(byteAddr);
            return (uint)((word >> ((3 - (x & 3)) << 2)) & 0x0f);
        }

        private void rendering_line_cpu(int outputLine, uint[]? targetBuffer = null)
        {
            uint[] destBuffer = targetBuffer ?? g_game_screen;
            int w_vscroll_mask = 0xffff;
            if (g_vdp_reg_11_2_vscroll == 1)
            {
                w_vscroll_mask = 0x000f;
            }
            int renderLine = GetRenderLine(g_scanline);
            int cellShift = GetCellHeightShift();

            // DIAGNOSTIC: Dump name tables from both sources when interlace=2 (once per 60 frames)
            if (g_vdp_interlace_mode == 2 && g_scanline == 0 && _frameCounter % 60 == 0)
            {
                bool isH40 = (g_vdp_reg_12_7_cellmode1 != 0);
                int scrollMask = isH40 ? 0xFE00 : 0xFC00;
                int scrollA_base = g_vdp_reg_2_scrolla & scrollMask;
                int scrollB_base = g_vdp_reg_4_scrollb & scrollMask;

                // Count non-zero in first 64 entries from both sources
                int directNonZeroA = 0, cacheNonZeroA = 0;
                int directNonZeroB = 0, cacheNonZeroB = 0;

                for (int i = 0; i < 64; i++)
                {
                    int addrA = (scrollA_base + (i << 1)) & 0xFFFE;
                    int addrB = (scrollB_base + (i << 1)) & 0xFFFE;

                    ushort directA = vram_read_render(addrA);
                    ushort directB = vram_read_render(addrB);

                    int cacheIdxA = 0x6000 + i;
                    int cacheIdxB = 0x6800 + i;
                    uint cacheA = cacheIdxA < g_renderer_vram.Length ? g_renderer_vram[cacheIdxA] : 0;
                    uint cacheB = cacheIdxB < g_renderer_vram.Length ? g_renderer_vram[cacheIdxB] : 0;

                    if (directA != 0) directNonZeroA++;
                    if (cacheA != 0) cacheNonZeroA++;
                    if (directB != 0) directNonZeroB++;
                    if (cacheB != 0) cacheNonZeroB++;
                }

                string sourceUsed = ForceDirectVramReadPlanes ? "VRAM" : "CACHE";

                Console.WriteLine($"[VRAM-VS-CACHE] frame={_frameCounter} interlace=2 source={sourceUsed} " +
                    $"scrollA_base=0x{scrollA_base:X4} scrollB_base=0x{scrollB_base:X4} " +
                    $"directA={directNonZeroA}/64 cacheA={cacheNonZeroA}/64 " +
                    $"directB={directNonZeroB}/64 cacheB={cacheNonZeroB}/64");

                // Show first few entries for comparison if they differ
                if (directNonZeroA != cacheNonZeroA || directNonZeroB != cacheNonZeroB)
                {
                    Console.WriteLine($"[VRAM-VS-CACHE-DETAIL] first 8 entries:");
                    for (int i = 0; i < 8; i++)
                    {
                        int addrA = (scrollA_base + (i << 1)) & 0xFFFE;
                        int addrB = (scrollB_base + (i << 1)) & 0xFFFE;
                        ushort directA = vram_read_render(addrA);
                        ushort directB = vram_read_render(addrB);
                        uint cacheA = g_renderer_vram[0x6000 + i];
                        uint cacheB = g_renderer_vram[0x6800 + i];
                        Console.WriteLine($"  i={i}: addrA=0x{addrA:X4} direct=0x{directA:X4} cache=0x{cacheA:X4} | addrB=0x{addrB:X4} direct=0x{directB:X4} cache=0x{cacheB:X4}");
                    }
                }
            }

            // Nollställ rad-buffertar för aktuell scanline
            for (int dx = 0; dx < g_display_xsize; dx++)
            {
                g_game_cmap[dx] = 0;
                g_game_primap[dx] = 0;
                g_game_shadowmap[dx] = 0;
                g_sprite_line_mask[dx] = false;
            }

            // --- Scroll B ---
            {
                int w_view_x = g_line_snap[g_scanline].hscrollB;
                uint w_priority = 0;
                uint w_palette = 0;
                uint w_reverse = 0;
                uint w_char = 0;
                int w_view_addr = 0;
                int w_view_dx = 8;
                int w_view_dy = 0;
                int w_screen_adrdr = g_vdp_reg_4_scrollb >> 1;
                int w_pic_addr = 0;

                // Debug log first entry in interlace mode 2
                if (g_vdp_interlace_mode == 2 && g_scanline == 0)
                {
                    var logLine = $"[RENDER] field={g_vdp_interlace_field} scrollB_addr=0x{w_screen_adrdr:X4} cacheB=0x{g_renderer_vram[0x6800]:X4} cacheA=0x{g_renderer_vram[0x6000]:X4}\n";
                    System.IO.File.AppendAllText("/tmp/eutherdrive_render.log", logLine);
                }

                for (int wx = 0; wx < g_display_xsize; wx++)
                {
                    if ((wx & w_vscroll_mask) == 0)
                    {
                        int w_view_y = g_line_snap[g_scanline].vscrollB[wx >> 4];
                        w_view_dy = GetRowInCell(w_view_y);
                        w_view_addr = w_screen_adrdr + ((w_view_y >> cellShift) * g_scroll_xcell);
                        w_view_dx = 8;
                    }
                    if (w_view_dx == 8)
                    {
                        w_view_x %= g_scroll_xsize;
                        w_view_dx = w_view_x & 7;
                        uint w_val = g_renderer_vram[w_view_addr + (w_view_x >> 3)];
                        w_priority = ((w_val >> 15) & 0x0001);
                        w_palette  = (((w_val >> 13) & 0x0003) << 4);
                        w_reverse  = ((w_val >> 11) & 0x0003);
                        w_char     =  (w_val & 0x07ff);

                        // DIRECT VRAM MODE: Read pattern directly from vram[]
                        if (ForceDirectVramReadPlanes && g_vdp_interlace_mode == 2)
                        {
                            uint picValueDirect = ReadPatternPixelMode2Direct((int)w_char, w_view_dx, w_view_dy, w_reverse);

                            // Debug: log first tile read
                            if (wx == 0 && _frameCounter < 5)
                            {
                                Console.WriteLine($"[DIRECT-VRAM] frame={_frameCounter} scanline={g_scanline} wx={wx} tile={w_char} row={w_view_dy} pic={picValueDirect}");
                            }

                            if (picValueDirect != 0)
                            {
                                g_game_cmap[wx]   = w_palette + picValueDirect;
                                g_game_primap[wx] = w_priority;
                            }
                            g_game_shadowmap[wx] = w_priority;
                            w_view_x += 1;
                            w_view_dx += 1;
                            continue; // Skip normal pattern read path
                        }

                        w_pic_addr = GetTileWordAddress((int)w_char, w_view_dy, w_reverse);

                        // Debug: log first pattern read
                        if (g_scanline == 100 && wx == 0 && _frameCounter == 100)
                        {
                            Console.WriteLine($"[RENDER-READ] frame={_frameCounter} scanline={g_scanline} tile={w_char} row={w_view_dy} pic_addr={w_pic_addr} rvram[{w_pic_addr}]=0x{g_renderer_vram[w_pic_addr]:X4}");
                        }
                    }
                    uint w_pic_w = g_renderer_vram[w_pic_addr + (w_view_dx >> 2)];
                    uint picValue = (uint)((w_pic_w >> ((3 - (w_view_dx & 3)) << 2)) & 0x0f);
                    if (picValue != 0)
                    {
                        g_game_cmap[wx]   = w_palette + picValue;
                        g_game_primap[wx] = w_priority;
                    }
                    g_game_shadowmap[wx] = w_priority;
                    w_view_x += 1;
                    w_view_dx += 1;
                }
            }

            // --- Scroll A ---
            {
                if (!((g_screenA_bottom_y == 0)
                    || (renderLine < g_screenA_top_y)
                    || (g_screenA_bottom_y < renderLine)))
                {
                    int w_view_x = g_line_snap[g_scanline].hscrollA;
                    uint w_priority = 0;
                    uint w_palette = 0;
                    uint w_reverse = 0;
                    uint w_char = 0;
                    int w_view_addr = 0;
                    int w_view_dx = 8;
                    int w_view_dy = 0;
                    int w_screen_adrdr = g_vdp_reg_2_scrolla >> 1;
                    int w_pic_addr = 0;

                    for (int wx = 0; wx < g_display_xsize; wx++)
                    {
                        if ((wx & w_vscroll_mask) == 0)
                        {
                            int w_view_y = g_line_snap[g_scanline].vscrollA[wx >> 4];
                            w_view_dy = GetRowInCell(w_view_y);
                            w_view_addr = w_screen_adrdr + ((w_view_y >> cellShift) * g_scroll_xcell);
                            w_view_dx = 8;
                        }
                        if (w_view_dx == 8)
                        {
                            w_view_x %= g_scroll_xsize;
                            w_view_dx = w_view_x & 7;
                            uint w_val = g_renderer_vram[w_view_addr + (w_view_x >> 3)];
                            w_priority = ((w_val >> 15) & 0x0001);
                            w_palette  = (((w_val >> 13) & 0x0003) << 4);
                            w_reverse  = ((w_val >> 11) & 0x0003);
                            w_char     =  (w_val & 0x07ff);

                            // DIRECT VRAM MODE: Read pattern directly from vram[]
                            if (ForceDirectVramReadPlanes && g_vdp_interlace_mode == 2)
                            {
                                uint picValueDirect = ReadPatternPixelMode2Direct((int)w_char, w_view_dx, w_view_dy, w_reverse);

                                if (((g_screenA_right_x != 0)
                                    && (g_screenA_left_x <= wx) && (wx <= g_screenA_right_x)
                                    && (g_game_primap[wx] <= w_priority)))
                                {
                                    if (picValueDirect != 0)
                                    {
                                        g_game_cmap[wx]   = w_palette + picValueDirect;
                                        g_game_primap[wx] = w_priority;
                                    }
                                    g_game_shadowmap[wx] |= w_priority;
                                }
                                w_view_x += 1;
                                w_view_dx += 1;
                                continue;
                            }

                            w_pic_addr = GetTileWordAddress((int)w_char, w_view_dy, w_reverse);
                        }
                        if (((g_screenA_right_x != 0)
                            && (g_screenA_left_x <= wx) && (wx <= g_screenA_right_x)
                            && (g_game_primap[wx] <= w_priority)))
                        {
                            uint w_pic_w = g_renderer_vram[w_pic_addr + (w_view_dx >> 2)];
                            uint picValue = (uint)((w_pic_w >> ((3 - (w_view_dx & 3)) << 2)) & 0x0f);
                            if (picValue != 0)
                            {
                                g_game_cmap[wx]   = w_palette + picValue;
                                g_game_primap[wx] = w_priority;
                            }
                            g_game_shadowmap[wx] |= w_priority;
                        }
                        w_view_x += 1;
                        w_view_dx += 1;
                    }
                }
            }

            // --- Sprites ---
            if (!DisableSprites)
            {
                bool allowSpriteMasking = false;
                for (int w_sp = 0; w_sp < g_line_snap[g_scanline].sprite_rendrere_num; w_sp++)
                {
                    int  w_left       = g_line_snap[g_scanline].sprite_left[w_sp];
                    int  w_top        = g_line_snap[g_scanline].sprite_top[w_sp];
                    int  w_xcell_size = g_line_snap[g_scanline].sprite_xcell_size[w_sp];
                    int  w_ycell_size = g_line_snap[g_scanline].sprite_ycell_size[w_sp];
                    uint w_priority   = g_line_snap[g_scanline].sprite_priority[w_sp];
                    uint w_palette    = g_line_snap[g_scanline].sprite_palette[w_sp];
                    uint w_reverse    = g_line_snap[g_scanline].sprite_reverse[w_sp];
                    int  w_char       = (int)g_line_snap[g_scanline].sprite_char[w_sp];

                    int rawX = w_left + 128;
                    if (rawX == 0)
                    {
                        if (allowSpriteMasking)
                            break;
                    }
                    else
                    {
                        allowSpriteMasking = true;
                    }

                    int cellHeight = GetCellHeightPixels();
                    // I interlace mode 2, använd full linje (scanline*2 + field) för korrekt sprite-rad.
                    int lineY = (g_vdp_interlace_mode == 2)
                        ? ((g_scanline << 1) | g_vdp_interlace_field)
                        : g_scanline;
                    int w_y     = lineY - w_top;
                    int w_ycell = w_y >> cellShift;
                    int w_cy    = w_y & (cellHeight - 1);
                    int w_posx  = w_left;
                    int cellHeightTiles = g_vdp_interlace_mode == 2 ? 1 : (cellHeight >> 3);
                    int cellStride = w_ycell_size * cellHeightTiles;
                    int yCellIndex = w_ycell * cellHeightTiles;
                    int yCellIndexFlipped = (w_ycell_size - w_ycell - 1) * cellHeightTiles;

                    for (int w_cur_xcell = 0; w_cur_xcell < w_xcell_size; w_cur_xcell++)
                    {
                        int w_char_cur = 0;
                        switch (w_reverse)
                        {
                            case 0: w_char_cur = w_char + (cellStride * w_cur_xcell) + yCellIndex; break;
                            case 1: w_char_cur = w_char + (cellStride * (w_xcell_size - w_cur_xcell - 1)) + yCellIndex; break;
                            case 2: w_char_cur = w_char + (cellStride * w_cur_xcell) + yCellIndexFlipped; break;
                            default:w_char_cur = w_char + (cellStride * (w_xcell_size - w_cur_xcell - 1)) + yCellIndexFlipped; break;
                        }
                        for (int w_cx = 0; w_cx < 8; w_cx++)
                        {
                            if ((0 <= w_posx) && (w_posx < g_display_xsize))
                            {
                                if (g_game_primap[w_posx] <= w_priority && !g_sprite_line_mask[w_posx])
                                {
                                    uint w_pic_w;
                                    int x_in_tile = w_cx;
                                    int y_in_tile = w_cy;

                                    if (ForceDirectVramReadSprites && g_vdp_interlace_mode == 2)
                                    {
                                        x_in_tile = ((w_reverse & 0x01) != 0) ? (7 - w_cx) : w_cx;
                                        if ((w_reverse & 0x02) != 0)
                                            y_in_tile = (cellHeight - 1) - w_cy;
                                        int tileIndex = w_char_cur & 0x3FF;
                                        int baseAddr = tileIndex << 6;
                                        int byteAddr = baseAddr + (y_in_tile << 2) + ((x_in_tile >> 2) << 1);
                                        w_pic_w = vram_read_render(byteAddr);
                                    }
                                    else
                                    {
                                        int w_num = GetTileWordAddress(w_char_cur, y_in_tile, w_reverse) + (x_in_tile >> 2);
                                        w_pic_w = g_renderer_vram[w_num];
                                    }

                                    uint w_pic = (w_pic_w >> ((3 - (x_in_tile & 3)) << 2)) & 0x0f;

                                    if (w_pic != 0)
                                    {
                                        uint w_color = (uint)(w_palette + w_pic);
                                        if (g_vdp_reg_12_3_shadow == 0)
                                        {
                                            g_game_cmap[w_posx]   = w_color;
                                            g_game_primap[w_posx] = w_priority;
                                        }
                                        else if (w_color == 0x3e)
                                        {
                                            uint w_map = g_game_shadowmap[w_posx];
                                            if (w_map < 2) g_game_shadowmap[w_posx] = (uint)(w_map + 1);
                                        }
                                        else if (w_color == 0x3f)
                                        {
                                            uint w_map = g_game_shadowmap[w_posx];
                                            if (w_map > 0) g_game_shadowmap[w_posx] = (uint)(w_map - 1);
                                        }
                                        else if ((w_color & 0x0f) == 0x0e)
                                        {
                                            g_game_cmap[w_posx]     = w_color;
                                            g_game_primap[w_posx]   = w_priority;
                                            g_game_shadowmap[w_posx]= 0x1000;
                                        }
                                        else
                                        {
                                            g_game_cmap[w_posx]   = w_color;
                                            g_game_primap[w_posx] = w_priority;
                                            g_game_shadowmap[w_posx] |= w_priority;
                                        }
                                        g_sprite_line_mask[w_posx] = true;
                                    }
                                }
                            }
                            w_posx += 1;
                        }
                    }
                }
            }

            // --- Window ---
            {
                if (DisableWindow)
                    goto SkipWindow;
                int w_xcell_st = g_line_snap[g_scanline].window_x_st;
                int w_xcell_ed = g_line_snap[g_scanline].window_x_ed;
                if (w_xcell_st != w_xcell_ed)
                {
                    // Window-rader behöver full interlace-linje i mode 2 för korrekt tile-rad.
                    int lineY = (g_vdp_interlace_mode == 0) ? g_scanline : GetInterlaceLine(g_scanline);
                    int w_view_dy = GetRowInCell(lineY);
                    int w_addr = (g_vdp_reg_3_windows >> 1) + ((lineY >> cellShift) * g_scroll_xcell) + w_xcell_st;
                    int w_posx = w_xcell_st << 3;
                    for (int w_cx = w_xcell_st; w_cx <= w_xcell_ed; w_cx++)
                    {
                        uint w_val = g_renderer_vram[w_addr++];
                        uint w_priority = ((w_val >> 15) & 0x0001);
                        uint w_palette  = (((w_val >> 13) & 0x0003) << 4);
                        uint w_reverse_bits = (w_val >> 11) & 0x0003;
                        uint w_char     = (w_val & 0x07ff);

                        for (int w_dx = 0; w_dx < 8; w_dx++)
                        {
                            if ((g_game_cmap[w_posx] == 0) || (g_game_primap[w_posx] <= w_priority))
                            {
                                // DIRECT VRAM MODE: Read pattern directly from vram[]
                                if (ForceDirectVramReadPlanes && g_vdp_interlace_mode == 2)
                                {
                                    uint picValueDirect = ReadPatternPixelMode2Direct((int)w_char, w_dx, w_view_dy, w_reverse_bits);

                                    if (picValueDirect != 0)
                                    {
                                        g_game_cmap[w_posx]   = w_palette + picValueDirect;
                                        g_game_primap[w_posx] = w_priority;
                                    }
                                }
                                else
                                {
                                    int  w_pic_addr = GetTileWordAddress((int)w_char, w_view_dy, w_reverse_bits) + (w_dx >> 2);
                                    uint w_pic_w    = g_renderer_vram[w_pic_addr];
                                    uint picValue   = (uint)((w_pic_w >> ((3 - (w_dx & 3)) << 2)) & 0x0f);
                                    if (picValue != 0)
                                    {
                                        g_game_cmap[w_posx]   = w_palette + picValue;
                                        g_game_primap[w_posx] = w_priority;
                                    }
                                }
                                g_game_shadowmap[w_posx] |= w_priority;
                            }
                            w_posx += 1;
                        }
                    }
                }
            SkipWindow: ;
            }

            // --- Skriv ut till framebuffern ---
            {
                if ((uint)outputLine >= (uint)g_output_ysize)
                    return;

                int w_base = outputLine * g_output_xsize;
                int visibleWidth = g_display_xsize;
                int outputWidth = g_output_xsize;
                for (int wx = 0; wx < visibleWidth; wx++)
                {
                    uint w_colnum = g_game_cmap[wx];
                    if (w_colnum == 0) w_colnum = g_vdp_reg_7_backcolor;

                    uint color;
                    if (g_vdp_reg_12_3_shadow == 0)
                    {
                        color = g_color[w_colnum];
                    }
                    else
                    {
                        uint w_shadow = g_game_shadowmap[wx];
                        color = (w_shadow == 0) ? g_color_shadow[w_colnum]
                        : (w_shadow == 2) ? g_color_highlight[w_colnum]
                        : g_color[w_colnum];
                    }
                    destBuffer[w_base + wx] = color;
                }

                if (outputWidth > visibleWidth)
                {
                    uint borderColor = g_color[g_vdp_reg_7_backcolor];
                    for (int wx = visibleWidth; wx < outputWidth; wx++)
                    {
                        destBuffer[w_base + wx] = borderColor;
                    }
                }
            }
        }
    }
}
