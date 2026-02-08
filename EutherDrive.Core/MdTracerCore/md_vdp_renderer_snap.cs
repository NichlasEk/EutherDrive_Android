using System;
using System.Globalization;

namespace EutherDrive.Core.MdTracerCore
{
    public partial class md_vdp
    {
        private static readonly bool HScrollUnsigned =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_HSCROLL_UNSIGNED"), "1", StringComparison.Ordinal);
        private static readonly bool HScrollDirect =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_HSCROLL_DIRECT"), "1", StringComparison.Ordinal);
        private static readonly bool VScrollSubtract =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_VSCROLL_SUBTRACT"), "1", StringComparison.Ordinal);
        private static readonly int TraceSpriteLine =
            int.TryParse(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_SPRITES_LINE"), out int line)
                ? line
                : -1;
        private static readonly int TraceSpriteId =
            int.TryParse(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_SPRITE_ID"), out int spriteId)
                ? spriteId
                : -1;
        private static readonly bool TraceSat =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_SAT"), "1", StringComparison.Ordinal);
        private static readonly int TraceSatCount =
            ParseTraceIntLocal("EUTHERDRIVE_TRACE_SAT_COUNT", 4);
        private static readonly bool TraceScrollLine =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_SCROLL_LINE"), "1", StringComparison.Ordinal);
        private static readonly int TraceScrollLineScanline =
            ParseTraceIntLocal("EUTHERDRIVE_TRACE_SCROLL_LINE_SCANLINE", 112);
        private static readonly int TraceScrollLineLimit =
            ParseTraceIntLocal("EUTHERDRIVE_TRACE_SCROLL_LINE_LIMIT", 32);
        private long _lastSatLogFrame = -1;
        private byte _lastSatLogField = 0xFF;
        private long _traceScrollLineFrame = -1;
        private int _traceScrollLineRemaining = TraceScrollLineLimit;

        private static int ParseTraceIntLocal(string name, int fallback)
        {
            string? raw = Environment.GetEnvironmentVariable(name);
            if (string.IsNullOrWhiteSpace(raw))
                return fallback;
            if (!int.TryParse(raw.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int value))
                return fallback;
            return value < 0 ? fallback : value;
        }

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
            if (TraceScrollLine && g_scanline == TraceScrollLineScanline && _traceScrollLineFrame != _frameCounter)
            {
                _traceScrollLineFrame = _frameCounter;
                _traceScrollLineRemaining = TraceScrollLineLimit;
            }

            // HScroll A/B
            {
                int hscrollLine = GetHScrollLine(g_scanline);
                int w_addr = g_vdp_reg_13_hscroll;
                int hscrollMask = g_vdp_reg_11_1_hscroll switch
                {
                    1 => 0x0007, // per 8-line block (tile row)
                    2 => 0x00F8, // per 8-line groups
                    3 => 0x00FF, // per line
                    _ => 0x0000  // full screen
                };
                w_addr += (hscrollLine & hscrollMask) << 2;
                w_addr >>= 1;

                int raw_hscrollA = (int)(vram_read_render(w_addr << 1) & 0x3ff);
                int raw_hscrollB = (int)(vram_read_render((w_addr + 1) << 1) & 0x3ff);
                int w_hscrollA;
                int w_hscrollB;
                if (HScrollUnsigned)
                {
                    w_hscrollA = raw_hscrollA;
                    w_hscrollB = raw_hscrollB;
                }
                else
                {
                    w_hscrollA = (raw_hscrollA & 0x200) != 0 ? (raw_hscrollA | ~0x3ff) : raw_hscrollA;
                    w_hscrollB = (raw_hscrollB & 0x200) != 0 ? (raw_hscrollB | ~0x3ff) : raw_hscrollB;
                }

                int modA = w_hscrollA % g_scroll_xsize;
                if (modA < 0) modA += g_scroll_xsize;
                int modB = w_hscrollB % g_scroll_xsize;
                if (modB < 0) modB += g_scroll_xsize;
                int w_view_xA = HScrollDirect ? modA : (g_scroll_xsize - modA) % g_scroll_xsize;
                int w_view_xB = HScrollDirect ? modB : (g_scroll_xsize - modB) % g_scroll_xsize;

                g_line_snap[g_scanline].hscrollA = w_view_xA;
                g_line_snap[g_scanline].hscrollB = w_view_xB;
                if (TraceScrollLine && g_scanline == TraceScrollLineScanline && _traceScrollLineRemaining > 0)
                {
                    if (_traceScrollLineRemaining != int.MaxValue)
                        _traceScrollLineRemaining--;
                    Console.WriteLine(
                        $"[SCROLLLINE] frame={_frameCounter} scanline={g_scanline} hmode={g_vdp_reg_11_1_hscroll} " +
                        $"hbase=0x{g_vdp_reg_13_hscroll:X4} hline={hscrollLine} addr=0x{(w_addr << 1):X4} " +
                        $"hsA=0x{w_hscrollA:X3} hsB=0x{w_hscrollB:X3} viewA={w_view_xA} viewB={w_view_xB} " +
                        $"xsize={g_scroll_xsize}");
                }
            }

            if (TraceSat && g_scanline == 0 && (_lastSatLogFrame != _frameCounter || _lastSatLogField != g_vdp_interlace_field))
            {
                _lastSatLogFrame = _frameCounter;
                _lastSatLogField = g_vdp_interlace_field;
                EnsureSpriteTableCache();
                int baseAddr = g_sprite_cache_base;
                System.Text.StringBuilder sb = new System.Text.StringBuilder();
                sb.AppendFormat("[SAT] frame={0} field={1} base=0x{2:X4}", _frameCounter, g_vdp_interlace_field, baseAddr);
                int count = TraceSatCount;
                if (count < 1) count = 1;
                if (count > 32) count = 32;
                for (int i = 0; i < count; i++)
                {
                    int addr = baseAddr + (i << 3);
                    ushort v1 = SpriteCacheReadWord(addr - g_sprite_cache_base);
                    ushort v2 = SpriteCacheReadWord(addr - g_sprite_cache_base + 2);
                    ushort v3 = SpriteCacheReadWord(addr - g_sprite_cache_base + 4);
                    ushort v4 = SpriteCacheReadWord(addr - g_sprite_cache_base + 6);
                    int y = v1 & g_sprite_vmask;
                    int link = v2 & 0x007f;
                    int x = v4 & 0x01ff;
                    int sizeX = ((v2 >> 10) & 0x03) + 1;
                    int sizeY = ((v2 >> 8) & 0x03) + 1;
                    sb.AppendFormat(
                        " | i={0} y={1} x={2} size={3}x{4} link={5} tile=0x{6:X3} v2=0x{7:X4}",
                        i, y, x, sizeX, sizeY, link, v3 & 0x07ff, v2);
                }
                Console.WriteLine(sb.ToString());
            }

            // VScroll A/B
            {
                int lineY = (g_vdp_interlace_mode == 0) ? g_scanline : GetInterlaceLine(g_scanline);
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
                        int viewA = VScrollSubtract ? (lineY - w_vscrollA) : (w_vscrollA + lineY);
                        int viewB = VScrollSubtract ? (lineY - w_vscrollB) : (w_vscrollB + lineY);
                        viewA %= g_scroll_ysize;
                        viewB %= g_scroll_ysize;
                        if (viewA < 0) viewA += g_scroll_ysize;
                        if (viewB < 0) viewB += g_scroll_ysize;
                        g_line_snap[g_scanline].vscrollA[i] = viewA;
                        g_line_snap[g_scanline].vscrollB[i] = viewB;
                    }
                    if (TraceScrollLine && g_scanline == TraceScrollLineScanline && _traceScrollLineRemaining > 0)
                    {
                        if (_traceScrollLineRemaining != int.MaxValue)
                            _traceScrollLineRemaining--;
                        Console.WriteLine(
                            $"[SCROLLLINE] frame={_frameCounter} scanline={g_scanline} vmode=full lineY={lineY} " +
                            $"vsA=0x{w_vscrollA:X3} vsB=0x{w_vscrollB:X3} viewA={g_line_snap[g_scanline].vscrollA[0]} " +
                            $"viewB={g_line_snap[g_scanline].vscrollB[0]} ysize={g_scroll_ysize}");
                    }
                }
                else
                {
                    int sampleCount = 0;
                    ushort[] sampleVsA = new ushort[4];
                    ushort[] sampleVsB = new ushort[4];
                    int[] sampleViewA = new int[4];
                    int[] sampleViewB = new int[4];
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

                        int viewA = VScrollSubtract ? (lineY - w_vscrollA) : (w_vscrollA + lineY);
                        int viewB = VScrollSubtract ? (lineY - w_vscrollB) : (w_vscrollB + lineY);
                        viewA %= g_scroll_ysize;
                        viewB %= g_scroll_ysize;
                        if (viewA < 0) viewA += g_scroll_ysize;
                        if (viewB < 0) viewB += g_scroll_ysize;
                        g_line_snap[g_scanline].vscrollA[i] = viewA;
                        g_line_snap[g_scanline].vscrollB[i] = viewB;
                        if (TraceScrollLine && g_scanline == TraceScrollLineScanline && sampleCount < 4)
                        {
                            sampleVsA[sampleCount] = w_vscrollA;
                            sampleVsB[sampleCount] = w_vscrollB;
                            sampleViewA[sampleCount] = g_line_snap[g_scanline].vscrollA[i];
                            sampleViewB[sampleCount] = g_line_snap[g_scanline].vscrollB[i];
                            sampleCount++;
                        }
                    }
                    if (TraceScrollLine && g_scanline == TraceScrollLineScanline && _traceScrollLineRemaining > 0)
                    {
                        if (_traceScrollLineRemaining != int.MaxValue)
                            _traceScrollLineRemaining--;
                        var sb = new System.Text.StringBuilder();
                        sb.Append($"[SCROLLLINE] frame={_frameCounter} scanline={g_scanline} vmode=percell lineY={lineY} ysize={g_scroll_ysize}");
                        for (int i = 0; i < sampleCount; i++)
                        {
                            sb.Append($" i={i} vsA=0x{sampleVsA[i]:X3} vsB=0x{sampleVsB[i]:X3} viewA={sampleViewA[i]} viewB={sampleViewB[i]}");
                        }
                        Console.WriteLine(sb.ToString());
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
                bool traceSprites = TraceSpriteLine >= 0 && g_scanline == TraceSpriteLine;
                bool traceSpriteId = TraceSpriteId >= 0;
                System.Text.StringBuilder? spriteLog = traceSprites
                    ? new System.Text.StringBuilder()
                    : null;
                int w_line_sprite_cnt = 0;
                int w_line_cell_cnt = 0;
                int w_sprite_cnt = 0;
                bool overflowLine = false;
                g_line_snap[g_scanline].sprite_rendrere_num = 0;

                UpdateSpriteRowCacheIfNeeded();
                int lineIndex = (g_vdp_interlace_mode == 2)
                    ? ((g_scanline << 1) | g_vdp_interlace_field)
                    : g_scanline;
                if ((uint)lineIndex >= (uint)g_sprite_row_cache.Length)
                    return;
                ref SpriteRowCacheRow row = ref g_sprite_row_cache[lineIndex];

                for (int i = 0; i < row.Count; i++)
                {
                    int w_now_link = row.SpriteIndices[i];
                    int w_addr = w_now_link << 3;
                    ushort w_val1 = SpriteCacheReadWord(w_addr);
                    int baseAddr = GetSpriteTableBase();
                    ushort w_val3 = ReadVramWordAligned(baseAddr + w_addr + 4);
                    ushort w_val4 = ReadVramWordAligned(baseAddr + w_addr + 6);

                    int w_top_x      = w_val4 & 0x01ff;
                    int w_top_y      = w_val1 & g_sprite_vmask;
                    int w_xcell_size = row.Width[i];
                    int w_ycell_size = row.Height[i];

                    int spriteBase = (g_vdp_interlace_mode == 2) ? 256 : 128;
                    int w_left   = w_top_x - 128;
                    int w_top    = w_top_y - spriteBase;
                    int w_right  = w_left + (w_xcell_size << 3) - 1;
                    int w_bottom = w_top  + (w_ycell_size * cellHeight) - 1;

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

                    if (traceSprites && w_sprite_cnt < 12)
                    {
                        spriteLog!.AppendFormat(
                            " id={0} topY={1} leftX={2} yRange=[{3},{4}] char=0x{5:X4} size={6}x{7} pri={8} pal={9}",
                            w_now_link, w_top_y, w_top_x, w_top, w_bottom, w_val3 & 0x07ff, w_xcell_size, w_ycell_size,
                            (w_val3 >> 15) & 0x0001, (w_val3 >> 13) & 0x0003);
                    }
                    if (traceSpriteId && w_now_link == TraceSpriteId)
                    {
                        int outY = GetOutputLineForScanline(g_scanline);
                        int spriteHeight = w_ycell_size * cellHeight;
                        bool visibleThisLine = true;
                        Console.WriteLine(
                            $"[SPRITE-PROBE] frame={_frameCounter} field={g_vdp_interlace_field} scanline={g_scanline} outY={outY} " +
                            $"spriteId={w_now_link} spriteY={w_top_y} spriteHeight={spriteHeight} visibleThisLine={visibleThisLine}");
                    }

                    w_sprite_cnt++;
                    g_line_snap[g_scanline].sprite_rendrere_num = w_sprite_cnt;

                    w_line_cell_cnt += w_xcell_size;
                    if (g_max_sprite_cell <= w_line_cell_cnt)
                    {
                        overflowLine = true;
                        break;
                    }

                    w_line_sprite_cnt += 1;
                    if (g_max_sprite_line <= w_line_sprite_cnt)
                    {
                        overflowLine = true;
                        break;
                    }
                }

                if (traceSprites)
                {
                    Console.WriteLine(
                        $"[SPRITE-LINE] frame={_frameCounter} scanline={g_scanline} interlace={g_vdp_interlace_mode} field={g_vdp_interlace_field} " +
                        $"sprites={w_sprite_cnt} lineSprites={w_line_sprite_cnt} cells={w_line_cell_cnt} overflow={overflowLine}" +
                        $"{(spriteLog!.Length > 0 ? spriteLog.ToString() : string.Empty)}");
                }
            }
        }
    }
}
