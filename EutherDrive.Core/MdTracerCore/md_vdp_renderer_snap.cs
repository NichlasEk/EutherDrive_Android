using System;

namespace EutherDrive.Core.MdTracerCore
{
    internal partial class md_vdp
    {
        private void rendering_frame_snap()
        {
            int w_vscroll_mask = (g_vdp_reg_11_2_vscroll == 1) ? 0x000f : 0xffff;

            g_snap_register.display_xsize   = g_display_xsize;
            g_snap_register.display_ysize   = g_display_ysize;
            g_snap_register.scroll_xsize    = g_scroll_xsize;
            g_snap_register.scroll_xcell    = g_scroll_xcell;
            g_snap_register.scroll_mask     = w_vscroll_mask;
            g_snap_register.scrollw_xcell   = g_scroll_xcell;
            g_snap_register.vdp_reg_1_6_display = g_vdp_reg_1_6_display;
            g_snap_register.vdp_reg_2_scrolla   = ((int)g_vdp_reg_2_scrolla) >> 1;
            g_snap_register.vdp_reg_4_scrollb   = ((int)g_vdp_reg_4_scrollb) >> 1;
            g_snap_register.vdp_reg_3_windows   = ((int)g_vdp_reg_3_windows) >> 1;
            g_snap_register.vdp_reg_7_backcolor = (uint)g_vdp_reg_7_backcolor;
            g_snap_register.vdp_reg_12_3_shadow = (uint)g_vdp_reg_12_3_shadow;
            g_snap_register.screenA_left   = (uint)g_screenA_left_x;
            g_snap_register.screenA_right  = (uint)g_screenA_right_x;
            g_snap_register.screenA_top    = (uint)g_screenA_top_y;
            g_snap_register.screenA_bottom = (uint)g_screenA_bottom_y;

            // Statiska tabeller: ok med vanlig kopia
            Array.Copy(g_renderer_vram,     g_snap_renderer_vram,   g_renderer_vram.Length);
            Array.Copy(g_color,             g_snap_color,           g_color.Length);
            Array.Copy(g_color_shadow,      g_snap_color_shadow,    g_color_shadow.Length);
            Array.Copy(g_color_highlight,   g_snap_color_highlight, g_color_highlight.Length);

            // DJUP kopia av per-scanline-state (behåll egna arrayer i snapshot)
            for (int y = 0; y < g_line_snap.Length; y++)
            {
                // skalar
                g_snap_line_snap[y].hscrollA             = g_line_snap[y].hscrollA;
                g_snap_line_snap[y].hscrollB             = g_line_snap[y].hscrollB;
                g_snap_line_snap[y].window_x_st          = g_line_snap[y].window_x_st;
                g_snap_line_snap[y].window_x_ed          = g_line_snap[y].window_x_ed;
                g_snap_line_snap[y].sprite_rendrere_num  = g_line_snap[y].sprite_rendrere_num;

                // vscroll (storlek = VSRAM_DATASIZE)
                Array.Copy(g_line_snap[y].vscrollA, g_snap_line_snap[y].vscrollA, g_line_snap[y].vscrollA.Length);
                Array.Copy(g_line_snap[y].vscrollB, g_snap_line_snap[y].vscrollB, g_line_snap[y].vscrollB.Length);

                // spritefält (storlek = MAX_SPRITE)
                int count = g_line_snap[y].sprite_rendrere_num;
                if (count < 0 || count > g_line_snap[y].sprite_left.Length)
                    count = g_line_snap[y].sprite_left.Length;

                Array.Copy(g_line_snap[y].sprite_left,      g_snap_line_snap[y].sprite_left,      count);
                Array.Copy(g_line_snap[y].sprite_right,     g_snap_line_snap[y].sprite_right,     count);
                Array.Copy(g_line_snap[y].sprite_top,       g_snap_line_snap[y].sprite_top,       count);
                Array.Copy(g_line_snap[y].sprite_bottom,    g_snap_line_snap[y].sprite_bottom,    count);
                Array.Copy(g_line_snap[y].sprite_xcell_size,g_snap_line_snap[y].sprite_xcell_size,count);
                Array.Copy(g_line_snap[y].sprite_ycell_size,g_snap_line_snap[y].sprite_ycell_size,count);
                Array.Copy(g_line_snap[y].sprite_priority,  g_snap_line_snap[y].sprite_priority,  count);
                Array.Copy(g_line_snap[y].sprite_palette,   g_snap_line_snap[y].sprite_palette,   count);
                Array.Copy(g_line_snap[y].sprite_reverse,   g_snap_line_snap[y].sprite_reverse,   count);
                Array.Copy(g_line_snap[y].sprite_char,      g_snap_line_snap[y].sprite_char,      count);

                // om arrays är längre än count – nolla svansen i snapshot för determinism
                int tail = g_snap_line_snap[y].sprite_left.Length - count;
                if (tail > 0)
                {
                    Array.Clear(g_snap_line_snap[y].sprite_left,       count, tail);
                    Array.Clear(g_snap_line_snap[y].sprite_right,      count, tail);
                    Array.Clear(g_snap_line_snap[y].sprite_top,        count, tail);
                    Array.Clear(g_snap_line_snap[y].sprite_bottom,     count, tail);
                    Array.Clear(g_snap_line_snap[y].sprite_xcell_size, count, tail);
                    Array.Clear(g_snap_line_snap[y].sprite_ycell_size, count, tail);
                    Array.Clear(g_snap_line_snap[y].sprite_priority,   count, tail);
                    Array.Clear(g_snap_line_snap[y].sprite_palette,    count, tail);
                    Array.Clear(g_snap_line_snap[y].sprite_reverse,    count, tail);
                    Array.Clear(g_snap_line_snap[y].sprite_char,       count, tail);
                }
            }
        }

        private void rendering_line_snap()
        {
            int cellHeight = GetCellHeightPixels();

            // HScroll A/B
            {
                int hscrollLine = GetHScrollLine(g_scanline);
                int w_addr = g_vdp_reg_13_hscroll;
                switch (g_vdp_reg_11_1_hscroll)
                {
                    case 2: w_addr += (hscrollLine & 0xfff8) << 2; break;
                    case 3: w_addr +=  hscrollLine             << 2; break;
                }
                w_addr >>= 1;

                int w_hscrollA = (int)(g_renderer_vram[w_addr]     & 0x3ff);
                int w_hscrollB = (int)(g_renderer_vram[w_addr + 1] & 0x3ff);

                int w_view_xA = ((g_scroll_xsize << 2) - w_hscrollA) % g_scroll_xsize;
                int w_view_xB = ((g_scroll_xsize << 2) - w_hscrollB) % g_scroll_xsize;

                g_line_snap[g_scanline].hscrollA = w_view_xA;
                g_line_snap[g_scanline].hscrollB = w_view_xB;
            }

            // VScroll A/B
            {
                if (g_vdp_reg_11_2_vscroll == 0)
                {
                    ushort w_vscrollA = g_vsram[0];
                    ushort w_vscrollB = g_vsram[1];

                    if (g_vdp_interlace_mode == 0)
                    {
                        w_vscrollA &= 0x3ff;
                        w_vscrollB &= 0x3ff;
                    }
                    else
                    {
                        w_vscrollA &= 0x7ff;
                        w_vscrollB &= 0x7ff;
                    }
                    if (g_vdp_interlace_mode == 2)
                    {
                        w_vscrollA &= 0x7fe;
                        w_vscrollB &= 0x7fe;
                    }

                    for (int i = 0; i < VSRAM_DATASIZE; i++)
                    {
                        g_line_snap[g_scanline].vscrollA[i] = (w_vscrollA + g_scanline) % g_scroll_ysize;
                        g_line_snap[g_scanline].vscrollB[i] = (w_vscrollB + g_scanline) % g_scroll_ysize;
                    }
                }
                else
                {
                    for (int i = 0; i < VSRAM_DATASIZE; i++)
                    {
                        ushort w_vscrollA = g_vsram[i << 1];
                        ushort w_vscrollB = g_vsram[(i << 1) + 1];

                        if (g_vdp_interlace_mode == 0)
                        {
                            w_vscrollA &= 0x3ff;
                            w_vscrollB &= 0x3ff;
                        }
                        else
                        {
                            w_vscrollA &= 0x7ff;
                            w_vscrollB &= 0x7ff;
                        }
                        if (g_vdp_interlace_mode == 2)
                        {
                            w_vscrollA &= 0x7fe;
                            w_vscrollB &= 0x7fe;
                        }

                        g_line_snap[g_scanline].vscrollA[i] = (w_vscrollA + g_scanline) % g_scroll_ysize;
                        g_line_snap[g_scanline].vscrollB[i] = (w_vscrollB + g_scanline) % g_scroll_ysize;
                    }
                }
            }

            // Window X-intervall
            {
                int w_xcell_st = 0;
                int w_xcell_ed = 0;

                // Använd g_scanline (scanline) istället för renderLine (output-linje)
                // för korrekt window-positionering i interlace mode
                if ((g_screenA_bottom_y == 0) || (g_scanline < g_screenA_top_y) || (g_screenA_bottom_y < g_scanline))
                {
                    w_xcell_ed = g_display_xcell - 1;
                }
                else
                {
                    if (g_screenA_left_x == 0)
                    {
                        w_xcell_st = g_screenA_right_x >> 3;
                        w_xcell_ed = g_display_xcell - 1;
                    }
                    else
                    {
                        w_xcell_ed = g_screenA_left_x >> 3;
                    }
                }

                g_line_snap[g_scanline].window_x_st = w_xcell_st;
                g_line_snap[g_scanline].window_x_ed = w_xcell_ed;
            }

            // Sprites för aktuell rad
            {
                int w_link = 0;
                int w_line_sprite_cnt = 0;
                int w_line_cell_cnt = 0;
                int w_sprite_cnt = 0;
                int w_sprite_mask1 = MAX_SPRITE;

                g_line_snap[g_scanline].sprite_rendrere_num = 0;

                for (int i = 0; i < g_max_sprite_num; i++)
                {
                    int    w_addr = g_vdp_reg_5_sprite + (w_link << 3);
                    ushort w_val1 = vram_read_w(w_addr);
                    ushort w_val2 = vram_read_w(w_addr + 2);
                    ushort w_val3 = vram_read_w(w_addr + 4);
                    ushort w_val4 = vram_read_w(w_addr + 6);

                    int w_now_link = w_link;
                    w_link = w_val2 & 0x007f;

                    int w_top_x      = w_val4 & 0x01ff;
                    int w_top_y      = w_val1 & g_sprite_vmask;
                    if (g_vdp_interlace_mode == 2)
                        w_top_y &= ~1;
                    int w_xcell_size = ((w_val2 >> 10) & 0x0003) + 1;
                    int w_ycell_size = ((w_val2 >>  8) & 0x0003) + 1;

                    int w_left   = w_top_x - 128;
                    int w_top    = w_top_y - 128;
                    int w_right  = w_left + (w_xcell_size << 3) - 1;
                    int w_bottom = w_top  + (w_ycell_size * cellHeight) - 1;

                    // I interlace mode 2, använd g_scanline direkt för sprite-hittande
                    // Sprite Y är redan maskerad till jämna värden (& ~1), så sprites
                    // triggas på samma scanlines oavsett fält
                    int lineY = g_scanline;
                    if ((lineY < w_top) || (w_bottom < lineY))
                        continue;

                    if (w_top_x == 1) w_sprite_mask1 = w_now_link;
                    if ((w_top_x == 0) && (w_sprite_mask1 != MAX_SPRITE))
                        break;

                    g_line_snap[g_scanline].sprite_left[w_sprite_cnt]       = w_left;
                    g_line_snap[g_scanline].sprite_right[w_sprite_cnt]      = w_right;
                    g_line_snap[g_scanline].sprite_top[w_sprite_cnt]        = w_top;
                    g_line_snap[g_scanline].sprite_bottom[w_sprite_cnt]     = w_bottom;
                    g_line_snap[g_scanline].sprite_xcell_size[w_sprite_cnt] = w_xcell_size;
                    g_line_snap[g_scanline].sprite_ycell_size[w_sprite_cnt] = w_ycell_size;
                    g_line_snap[g_scanline].sprite_priority[w_sprite_cnt]   = (uint)((w_val3 >> 15) & 0x0001);
                    g_line_snap[g_scanline].sprite_palette[w_sprite_cnt]    = (uint)(((w_val3 >> 13) & 0x0003) << 4);
                    g_line_snap[g_scanline].sprite_reverse[w_sprite_cnt]    = (uint)((w_val3 >> 11) & 0x0003);
                    g_line_snap[g_scanline].sprite_char[w_sprite_cnt]       = (uint)(w_val3 & 0x07ff);

                    w_sprite_cnt++;
                    g_line_snap[g_scanline].sprite_rendrere_num = w_sprite_cnt;

                    if (w_link == 0) break;

                    w_line_cell_cnt += w_xcell_size;
                    if (g_max_sprite_cell <= w_line_cell_cnt)
                    {
                        g_vdp_status_6_sprite = 1;
                        break;
                    }

                    w_line_sprite_cnt += 1;
                    if (g_max_sprite_line <= w_line_sprite_cnt)
                    {
                        g_vdp_status_6_sprite = 1;
                        break;
                    }
                }
            }
        }
    }
}
